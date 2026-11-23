// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// THE REVIT-FREE HALF of horizun_join_geometry operation=corners / fix_corners: which
// wall ends are OPEN corners - an end joined to no wall, within `tolerance` of another
// wall that is not parallel - and where the end must go to close it.
//
// field cycle video review, 2026-10-24, A3/A6: "un muro llega a un punto y el otro sigue
// sin encajar"; "falla mucho en las uniones" - in ARQ, across every level. Nothing typed
// listed them: the agents found them in a 3D view.
//
//   L corner  the end is near one END of the other wall; both ends go to the point
//             where the two location lines cross.
//   T corner  the end is near the INSIDE of the other wall; the end goes to the other
//             wall's location line.
//
// The fix for one open end is the smallest that closes it: allow the join at that end
// when Revit has it disallowed, and move the end to the crossing point when it is not
// already there. An end whose crossing point is farther than the tolerance is reported,
// never moved: that is a design gap, not a sloppy corner. Plan coordinates only (x, y);
// the caller decides which walls share a storey.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;

namespace Horizun.Revit.Core
{
    public sealed class WallSegment
    {
        public long Id;
        public double X0, Y0, X1, Y1;
        /// <summary>Walls joined at each end (index 0 = start, 1 = end), read from Revit.</summary>
        public HashSet<long> JoinedAtStart = new HashSet<long>(), JoinedAtEnd = new HashSet<long>();
        public bool JoinAllowedAtStart = true, JoinAllowedAtEnd = true;
        /// <summary>Only set after measuring the finish end against a bounded face of a loaded linked core.</summary>
        public bool LinkedCoreAtStart, LinkedCoreAtEnd;
        public bool LinkedCoreAt(int end) => end == 0 ? LinkedCoreAtStart : LinkedCoreAtEnd;
        /// <summary>Vertical extent (any unit, same for all walls): walls that do not overlap in z never form a corner.</summary>
        public double ZMin, ZMax;
    }

    public sealed class OpenCorner
    {
        public long WallId;
        public int End;                 // 0 start, 1 end
        public long OtherId;
        public string Kind;             // L | T
        public double GapFeet;          // distance from the end to the other wall's segment
        public double? ToX, ToY;        // where the end must go (null: reported only)
        public double MoveFeet;         // how far the end moves
        public bool NeedsAllowJoin;
        public string Action;           // allow_join | extend | allow_join_and_extend | report_only
        public string Reason;
    }

    public static class WallCornerRules
    {
        public const string ClosedByOther = "closed_by_other";
        public const double DefaultToleranceMm = 50;
        public const double MaxToleranceMm = 500;
        /// <summary>Lines closer than this to parallel (degrees) never make a corner.</summary>
        public const double MinAngleDegrees = 5;
        /// <summary>An end already at the crossing within this (ft, 0.5 mm) needs no move.</summary>
        public const double SamePointFeet = 0.5 / 304.8;

        /// <summary>A single thin layer may still be labelled Structure by its template.
        /// This only admits measurement; it does not exempt any end without bounded-face contact.</summary>
        public static bool ThinFinishCandidate(double widthFeet, int positiveLayerCount, bool allLayersFinish) =>
            !double.IsNaN(widthFeet) && !double.IsInfinity(widthFeet) && widthFeet > 0 && widthFeet <= 100.0 / 304.8 &&
            positiveLayerCount > 0 && (positiveLayerCount == 1 || allLayersFinish);

        public static List<OpenCorner> Find(IList<WallSegment> walls, double toleranceFeet)
        {
            var found = new List<OpenCorner>();
            if (walls == null) return found;
            foreach (WallSegment w in walls)
                for (int end = 0; end < 2; end++)
                {
                    if (w.LinkedCoreAt(end)) continue;
                    HashSet<long> joined = end == 0 ? w.JoinedAtStart : w.JoinedAtEnd;
                    if (joined != null && joined.Any(id => id != w.Id)) continue;
                    double ex = end == 0 ? w.X0 : w.X1, ey = end == 0 ? w.Y0 : w.Y1;
                    OpenCorner best = null;
                    foreach (WallSegment o in walls)
                    {
                        if (o.Id == w.Id) continue;
                        if (w.ZMax < o.ZMin || o.ZMax < w.ZMin) continue;
                        if (AngleDegrees(w, o) < MinAngleDegrees) continue;
                        double gap = DistanceToSegment(ex, ey, o, out double t);
                        if (gap > toleranceFeet) continue;
                        if (best != null && gap >= best.GapFeet) continue;
                        best = Corner(w, end, ex, ey, o, gap, t, toleranceFeet);
                    }
                    if (best != null) found.Add(best);
                }
            // an L where this end already sits on the crossing and the OTHER end moves to it closes by that move
            foreach (OpenCorner c in found.Where(x => x.Action == "report_only" && !x.ToX.HasValue && x.Reason != null && x.Reason.StartsWith("the end is already")))
                if (found.Any(o => o.WallId == c.OtherId && o.OtherId == c.WallId && o.ToX.HasValue))
                {
                    c.Action = ClosedByOther;
                    c.Reason = "closes when wall " + c.OtherId + " moves its end to this point";
                }
            return found;
        }

        private static OpenCorner Corner(WallSegment w, int end, double ex, double ey, WallSegment o, double gap, double t, double tol)
        {
            double oLen = Length(o);
            double nearEndFeet = Math.Min(t, 1 - t) * oLen;
            string kind = nearEndFeet <= tol ? "L" : "T";
            bool allow = end == 0 ? !w.JoinAllowedAtStart : !w.JoinAllowedAtEnd;
            var c = new OpenCorner { WallId = w.Id, End = end, OtherId = o.Id, Kind = kind, GapFeet = gap, NeedsAllowJoin = allow };
            if (kind == "L" && o.LinkedCoreAt(NearestEnd(o, ex, ey)))
            {
                c.Action = "report_only";
                c.Reason = "the partner finish end terminates on a measured linked core face; extending its L corner would move that supported end";
                return c;
            }
            if (!Cross(w, o, out double px, out double py))
            {
                c.Action = "report_only"; c.Reason = "the two location lines do not cross (parallel)";
                return c;
            }
            double move = Math.Sqrt((px - ex) * (px - ex) + (py - ey) * (py - ey));
            // the crossing must be near this end, and for an L near the other wall's end too
            if (move > tol)
            {
                c.Action = "report_only"; c.Reason = "the crossing point is " + Math.Round(move * 304.8) + " mm from the end, beyond the tolerance: a design gap, not a sloppy corner";
                return c;
            }
            // the moved end must not reverse or collapse the wall
            double fx = end == 0 ? w.X1 : w.X0, fy = end == 0 ? w.Y1 : w.Y0;
            double newLen = Math.Sqrt((px - fx) * (px - fx) + (py - fy) * (py - fy));
            if (newLen < 2 * tol || newLen < 0.1)
            {
                c.Action = "report_only"; c.Reason = "moving the end would leave the wall " + Math.Round(newLen * 304.8) + " mm long";
                return c;
            }
            c.MoveFeet = move;
            if (move > SamePointFeet) { c.ToX = px; c.ToY = py; }
            c.Action = c.ToX.HasValue ? (allow ? "allow_join_and_extend" : "extend") : (allow ? "allow_join" : "report_only");
            if (c.Action == "report_only")
                c.Reason = "the end is already on the other wall's line and joins are allowed, yet Revit did not join them (read the wall types and the warnings)";
            return c;
        }

        public static double Length(WallSegment s) => Math.Sqrt((s.X1 - s.X0) * (s.X1 - s.X0) + (s.Y1 - s.Y0) * (s.Y1 - s.Y0));

        public static int NearestEnd(WallSegment wall, double x, double y) =>
            Math.Pow(wall.X0 - x, 2) + Math.Pow(wall.Y0 - y, 2) <= Math.Pow(wall.X1 - x, 2) + Math.Pow(wall.Y1 - y, 2) ? 0 : 1;

        /// <summary>An L requires the reciprocal endpoint join; a T meets the partner's interior.</summary>
        public static bool MeetingClosed(bool movingEndJoined, bool partnerEndJoined, bool isL) =>
            movingEndJoined && (!isL || partnerEndJoined);

        public static bool EndInMeeting(WallSegment original, WallSegment current, double x, double y, double meetingX, double meetingY, double radius) =>
            original.ZMax >= current.ZMin && current.ZMax >= original.ZMin &&
            Math.Sqrt(Math.Pow(x - meetingX, 2) + Math.Pow(y - meetingY, 2)) <= radius;

        public static double AngleDegrees(WallSegment a, WallSegment b)
        {
            double ax = a.X1 - a.X0, ay = a.Y1 - a.Y0, bx = b.X1 - b.X0, by = b.Y1 - b.Y0;
            double la = Math.Sqrt(ax * ax + ay * ay), lb = Math.Sqrt(bx * bx + by * by);
            if (la < 1e-9 || lb < 1e-9) return 0;
            double cos = Math.Abs((ax * bx + ay * by) / (la * lb));
            return Math.Acos(Math.Min(1, cos)) * 180 / Math.PI;
        }

        /// <summary>Distance from (x, y) to segment s; t is the parameter of the nearest point (0..1).</summary>
        public static double DistanceToSegment(double x, double y, WallSegment s, out double t)
        {
            double dx = s.X1 - s.X0, dy = s.Y1 - s.Y0, l2 = dx * dx + dy * dy;
            t = l2 < 1e-18 ? 0 : Math.Max(0, Math.Min(1, ((x - s.X0) * dx + (y - s.Y0) * dy) / l2));
            double nx = s.X0 + t * dx - x, ny = s.Y0 + t * dy - y;
            return Math.Sqrt(nx * nx + ny * ny);
        }

        /// <summary>Where the two unbounded location lines cross.</summary>
        public static bool Cross(WallSegment a, WallSegment b, out double x, out double y)
        {
            double ax = a.X1 - a.X0, ay = a.Y1 - a.Y0, bx = b.X1 - b.X0, by = b.Y1 - b.Y0;
            double den = ax * by - ay * bx;
            x = y = 0;
            if (Math.Abs(den) < 1e-12) return false;
            double u = ((b.X0 - a.X0) * by - (b.Y0 - a.Y0) * bx) / den;
            x = a.X0 + u * ax; y = a.Y0 + u * ay;
            return true;
        }
    }
}
