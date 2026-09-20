// -----------------------------------------------------------------------------
// Horizun MCP — original Horizun code.
//
// IS THE WALL A SYMBOL WAS HOSTED ON THE WALL IT IS DRAWN AGAINST?
//
// MEASURED: a receptacle drawn 60 mm from a wall that the wall reading did not
// convert - its face line had been paired with the chase next to it - was hosted
// on the nearest wall that DID exist, 220 mm away, inside the host search. The
// row was built, verified and matched by revision: every check this bridge had
// passed, on the wrong wall.
//
// The model alone cannot see it: the right wall is not in the model. The drawing
// can. When a rule declares which layers its hosts are drawn on, the symbol's
// nearest line on those layers must be one of its host's own faces - a line
// parallel to the host, within its band, alongside it. If some OTHER drawn wall
// is clearly nearer, the row is withdrawn and says which line and how far.
//
// "Clearly" is the tolerance: a device beside a corner is a few millimetres from
// the other wall's face too, and that is not a different host.
//
// A HOST'S OWN FACE STANDS AT ITS HALF WIDTH. MEASURED (unit 915F): a receptacle
// drawn against a 119.1 mm wall that was withdrawn for want of a type was hosted
// on the BACK face of the 101.6 mm wall behind it, 170 mm from where it is drawn.
// The check had counted every parallel line within half width + host search as the
// host's own face, so the drawn face of the missing wall, 220 mm off the host's
// centreline, passed as the host's. A face now stands within the half width plus
// the point tolerance plus the finish a reading may leave outside the pair.
//
// AND A SYMBOL IS AGAINST A LINE ONLY WHERE IT FACES IT. MEASURED (unit 914, 15969):
// with the narrower face band, a switch 168.6 mm off its host face was withdrawn
// because the corner of a neighbouring wall's step, which starts 91 mm beyond the
// symbol along the wall, was 133 mm away. A line whose extent the symbol does not
// project onto is a corner, not a wall the symbol is drawn against.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;

namespace Horizun.Revit.Core
{
    public sealed class CadHostPlausibilityResult
    {
        /// <summary>True when another drawn wall is clearly nearer than any face of the host.</summary>
        public bool NearerWallDrawn;
        /// <summary>The distance to the nearest host face line, or null when the host's faces are not drawn.</summary>
        public double? HostFaceMm;
        /// <summary>The distance to the nearest line on the host layers that is NOT one of the host's faces.</summary>
        public double? OtherWallMm;
        public CadSegment OtherWallLine;
        /// <summary>For an END-face host: the same wall resumes beyond the symbol - the end is a jamb.</summary>
        public bool Jamb;
    }

    public static class CadHostPlausibility
    {
        /// <summary>
        /// How far a drawn face may stand outside the model wall's own face: the finish
        /// lines a reading leaves outside the pair it built (measured 8 to 17.5 mm).
        /// </summary>
        public const double FaceStandOffMm = 20.0;

        public static CadHostPlausibilityResult Check(CadPoint symbol, CadPoint hostA, CadPoint hostB,
                                                      double hostHalfWidthMm, IEnumerable<CadSegment> hostLayerLines,
                                                      double angleToleranceDegrees, double toleranceMm,
                                                      double hostSearchMm)
        {
            var result = new CadHostPlausibilityResult();
            double dx = hostB.X - hostA.X, dy = hostB.Y - hostA.Y;
            double length = Math.Sqrt(dx * dx + dy * dy);
            if (length <= 1e-9 || hostLayerLines == null) return result;
            var u = new CadVector(dx / length, dy / length);

            double host = double.MaxValue, other = double.MaxValue;
            CadSegment otherLine = null;
            foreach (CadSegment s in hostLayerLines)
            {
                if (s == null) continue;
                double d = Distance(symbol, s.A, s.B);
                CadVector? dir = s.PlanDirection;
                bool isHostFace = false;
                if (dir != null && dir.Value.UndirectedAngleDegrees(u) <= angleToleranceDegrees)
                {
                    CadPoint mid = s.Midpoint;
                    double across = Math.Abs((mid.X - hostA.X) * -u.Y + (mid.Y - hostA.Y) * u.X);
                    double t0 = (s.A.X - hostA.X) * u.X + (s.A.Y - hostA.Y) * u.Y;
                    double t1 = (s.B.X - hostA.X) * u.X + (s.B.Y - hostA.Y) * u.Y;
                    double alongside = Math.Min(Math.Max(t0, t1), length) - Math.Max(Math.Min(t0, t1), 0);
                    isHostFace = across <= hostHalfWidthMm + toleranceMm + FaceStandOffMm &&
                                 alongside > -toleranceMm;
                }
                // THE HOST'S OWN END. MEASURED (unit 912, 15BB4): a switch drawn inside
                // its wall, 8 mm from the line that closes the wall's end, was withdrawn
                // because that cap counted as a nearer wall. A line across the host's band
                // at one of its ends is the host.
                if (!isHostFace && IsCap(s, hostA, u, length, hostHalfWidthMm, toleranceMm)) continue;
                if (isHostFace) host = Math.Min(host, d);
                else if (d < other && Faces(symbol, s.A, s.B, toleranceMm)) { other = d; otherLine = s; }
            }

            if (host < double.MaxValue) result.HostFaceMm = host;
            if (other < double.MaxValue) { result.OtherWallMm = other; result.OtherWallLine = otherLine; }
            // No drawn face of the host is not evidence of a different host.
            result.NearerWallDrawn = result.HostFaceMm.HasValue && result.OtherWallMm.HasValue &&
                                     other + toleranceMm < host && other <= hostSearchMm;
            return result;
        }

        /// <summary>How far past a wall's end the same wall may resume and the end still be a jamb.</summary>
        public const double JambGapMm = 1500.0;

        /// <summary>
        /// A symbol hosted on the END face of a model wall: is that the end the drawing shows it on?
        /// <paramref name="endPoint"/> is the wall's end on its location line and <paramref name="outward"/>
        /// the unit direction the end face looks along. Two refusals: a drawn end cap nearer the symbol
        /// than the model's end (the drawing's wall runs further than the model's - case 159C4), and the
        /// same wall resuming within <see cref="JambGapMm"/> beyond the symbol (a jamb: the device belongs
        /// to the wall around the opening, not to its end).
        /// </summary>
        public static CadHostPlausibilityResult CheckEnd(CadPoint symbol, CadPoint endPoint, CadVector outward,
                                                         double hostHalfWidthMm, IEnumerable<CadSegment> hostLayerLines,
                                                         double angleToleranceDegrees, double toleranceMm)
        {
            var result = new CadHostPlausibilityResult();
            if (hostLayerLines == null) return result;
            var u = outward;
            var n = new CadVector(-u.Y, u.X);
            double s = (symbol.X - endPoint.X) * u.X + (symbol.Y - endPoint.Y) * u.Y;       // in front of the model end
            double lateral = (symbol.X - endPoint.X) * n.X + (symbol.Y - endPoint.Y) * n.Y;
            result.HostFaceMm = s;
            double nearest = double.MaxValue;
            foreach (CadSegment line in hostLayerLines)
            {
                if (line == null) continue;
                CadVector? dir = line.PlanDirection;
                if (dir == null) continue;
                double a0 = (line.A.X - endPoint.X) * u.X + (line.A.Y - endPoint.Y) * u.Y;
                double a1 = (line.B.X - endPoint.X) * u.X + (line.B.Y - endPoint.Y) * u.Y;
                double c0 = (line.A.X - endPoint.X) * n.X + (line.A.Y - endPoint.Y) * n.Y;
                double c1 = (line.B.X - endPoint.X) * n.X + (line.B.Y - endPoint.Y) * n.Y;
                if (dir.Value.UndirectedAngleDegrees(n) <= angleToleranceDegrees)
                {
                    // a cap ACROSS the band, facing the symbol laterally, between the model end and the symbol
                    double along = (a0 + a1) / 2;
                    bool spans = Math.Min(c0, c1) <= lateral + toleranceMm && Math.Max(c0, c1) >= lateral - toleranceMm;
                    if (spans && along > toleranceMm + FaceStandOffMm && along < s - toleranceMm && s - along < nearest)
                    {
                        nearest = s - along;
                        result.OtherWallLine = line;
                    }
                }
                else if (dir.Value.UndirectedAngleDegrees(u) <= angleToleranceDegrees)
                {
                    // the SAME wall resuming beyond the symbol: a face line inside the band that starts past it
                    double c = (c0 + c1) / 2;
                    double start = Math.Min(a0, a1);
                    if (Math.Abs(c) <= hostHalfWidthMm + toleranceMm + FaceStandOffMm &&
                        start > s + toleranceMm && start - s <= JambGapMm)
                        result.Jamb = true;
                }
            }
            if (nearest < double.MaxValue)
            {
                result.OtherWallMm = nearest;
                result.NearerWallDrawn = true;
            }
            return result;
        }

        private static bool IsCap(CadSegment s, CadPoint hostA, CadVector u, double length, double halfWidth, double tol)
        {
            double a0 = (s.A.X - hostA.X) * u.X + (s.A.Y - hostA.Y) * u.Y;
            double a1 = (s.B.X - hostA.X) * u.X + (s.B.Y - hostA.Y) * u.Y;
            double c0 = (s.A.X - hostA.X) * -u.Y + (s.A.Y - hostA.Y) * u.X;
            double c1 = (s.B.X - hostA.X) * -u.Y + (s.B.Y - hostA.Y) * u.X;
            if (Math.Abs(a0 - a1) > tol) return false;                         // not across the wall
            double along = (a0 + a1) / 2;
            if (Math.Min(Math.Abs(along), Math.Abs(along - length)) > tol) return false;   // not at an end
            double lo = Math.Min(c0, c1), hi = Math.Max(c0, c1);
            // it spans the band (a board or two of finish may stick out past it)
            return lo <= -halfWidth + tol + FaceStandOffMm && hi >= halfWidth - tol - FaceStandOffMm &&
                   lo >= -halfWidth - tol - FaceStandOffMm && hi <= halfWidth + tol + FaceStandOffMm;
        }

        /// <summary>True when the symbol's foot on the line falls within the line, give or take the tolerance.</summary>
        private static bool Faces(CadPoint p, CadPoint a, CadPoint b, double toleranceMm)
        {
            double dx = b.X - a.X, dy = b.Y - a.Y;
            double length = Math.Sqrt(dx * dx + dy * dy);
            if (length <= 1e-9) return false;
            double along = ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / length;
            return along >= -toleranceMm && along <= length + toleranceMm;
        }

        private static double Distance(CadPoint p, CadPoint a, CadPoint b)
        {
            double dx = b.X - a.X, dy = b.Y - a.Y;
            double l2 = dx * dx + dy * dy;
            double t = l2 <= 0 ? 0 : Math.Max(0, Math.Min(1, ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / l2));
            double x = a.X + t * dx, y = a.Y + t * dy;
            return Math.Sqrt((p.X - x) * (p.X - x) + (p.Y - y) * (p.Y - y));
        }
    }
}
