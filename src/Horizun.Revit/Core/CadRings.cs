using System;
using System.Collections.Generic;
using System.Linq;

namespace Horizun.Revit.Core
{
    /// <summary>
    /// CLOSED OUTLINES INSIDE LINE WORK - a box drawn on a layer whose lines are runs.
    ///
    /// MEASURED (campaign 7, real mechanical plan M102): a 24 x 24 in square on the duct layer, drawn as a
    /// CLOSED polyline (flag 70 = 1, five vertices, the last one repeating the first) with both diagonals
    /// as separate LINEs. Revit handed it over as ONE PolyLine - TL, TR, BR, BL, TL, BR: the square with
    /// one diagonal appended - and the other diagonal as a Line. The first fix (7a746f9) named a PolyLine a
    /// ring only when its first and last coordinates coincided, which never happened here, so the square
    /// stayed four duct runs and its diagonals two more; its unit test fed segments ALREADY named ring: and
    /// passed. The harvest now builds a polyline's segments through <see cref="PolylineSegments"/>, which
    /// the tests call too.
    /// </summary>
    public static class CadRings
    {
        /// <summary>Coincidence for vertices of ONE polyline, in mm: they are copies of the same number.</summary>
        public const double VertexToleranceMm = 0.001;

        /// <summary>
        /// For each segment of a polyline (points[i] -> points[i+1]) the loop it belongs to, or -1.
        ///
        /// A loop is a stretch of at least three segments that returns to a vertex of the SAME open stretch;
        /// the stretch restarts after each loop, so loops never overlap and a tail after (or before) a loop
        /// stays a tail. TL,TR,BR,BL,TL,BR gives 0,0,0,0,-1.
        /// </summary>
        public static int[] LoopOfSegment(IList<CadPoint> points, double tolerance)
        {
            int n = points?.Count ?? 0;
            var loopOf = Enumerable.Repeat(-1, Math.Max(0, n - 1)).ToArray();
            int start = 0, loops = 0;
            for (int j = 3; j < n; j++)
                for (int i = start; i <= j - 3; i++)
                {
                    if (points[i].DistanceTo(points[j]) > tolerance) continue;
                    for (int s = i; s < j; s++) loopOf[s] = loops;
                    loops++;
                    start = j;
                    break;
                }
            return loopOf;
        }

        /// <summary>
        /// A polyline's segments as the harvest records them: the edges of each closed loop carry
        /// ring:&lt;index of the loop's first segment in the harvest&gt; and ClosedRing; everything else is an
        /// ordinary polyline segment. <paramref name="firstIndex"/> is the harvest's segment count before
        /// these, which makes each ring id unique in one harvest.
        /// </summary>
        public static List<CadSegment> PolylineSegments(IList<CadPoint> points, string layer, int firstIndex)
        {
            var segs = new List<CadSegment>();
            if (points == null || points.Count < 2) return segs;
            int[] loopOf = LoopOfSegment(points, VertexToleranceMm);
            var ringId = new Dictionary<int, string>();
            for (int i = 0; i < loopOf.Length; i++)
            {
                string id = null;
                if (loopOf[i] >= 0 && !ringId.TryGetValue(loopOf[i], out id))
                    ringId[loopOf[i]] = id = "ring:" + (firstIndex + i).ToString(System.Globalization.CultureInfo.InvariantCulture);
                segs.Add(new CadSegment(points[i], points[i + 1], layer, CadCurveKind.Polyline, i, id, id != null));
            }
            return segs;
        }

        /// <summary>
        /// The segments of these (one layer's) that are a closed outline's FIGURE: the ring edges, and every
        /// other segment joining two different corners of one ring through its inside - a chord. MEASURED
        /// (M102): the square's diagonals, one appended to the ring's PolyLine and one a separate Line. A
        /// segment that only touches a corner, or runs along an edge, is not a chord and is left alone.
        /// </summary>
        public static HashSet<CadSegment> Figure(IList<CadSegment> segments, double toleranceMm,
                                                 out int rings, out int chords)
        {
            var figure = new HashSet<CadSegment>();
            rings = 0;
            chords = 0;
            if (segments == null) return figure;
            var ringGroups = segments.Where(s => s != null && s.ClosedRing && s.SourceCurveId != null)
                                     .GroupBy(s => s.SourceCurveId, StringComparer.Ordinal)
                                     .Select(g => g.OrderBy(s => s.SourceIndex).ToList())
                                     .Where(g => g.Count >= 3).ToList();
            rings = ringGroups.Count;
            foreach (List<CadSegment> ring in ringGroups)
                foreach (CadSegment s in ring) figure.Add(s);
            if (rings == 0) return figure;
            double tol = Math.Max(toleranceMm, VertexToleranceMm);
            foreach (CadSegment s in segments)
            {
                if (s == null || figure.Contains(s)) continue;
                foreach (List<CadSegment> ring in ringGroups)
                {
                    List<CadPoint> corners = ring.Select(e => e.A).ToList();
                    int a = CornerAt(corners, s.A, tol), b = CornerAt(corners, s.B, tol);
                    if (a < 0 || b < 0 || a == b) continue;
                    if (!Inside(corners, s.Midpoint)) continue;
                    figure.Add(s);
                    chords++;
                    break;
                }
            }
            return figure;
        }

        /// <summary>
        /// These segments without the closed outlines that the requirement set's rules would leave unclaimed:
        /// on a layer whose winning rule reads single lines and does not declare include_closed_polylines. A
        /// network read from the same layers as a conversion must not join what the conversion never builds.
        /// </summary>
        public static List<CadSegment> WithoutOutlines(List<CadSegment> segments, CadRequirementSet set,
                                                       out int leftOut, out int rings, out int chords)
        {
            leftOut = 0;
            rings = 0;
            chords = 0;
            if (segments == null || set == null) return segments;
            var drop = new HashSet<CadSegment>();
            foreach (var layer in segments.GroupBy(s => s.Layer ?? "(no layer)", StringComparer.OrdinalIgnoreCase))
            {
                CadRule rule = set.RulesFor(layer.Key).FirstOrDefault();
                if (rule?.Geometry == null || rule.Geometry.Source != CadGeometrySource.SingleLines ||
                    rule.Geometry.IncludeClosedPolylines) continue;
                int r, c;
                foreach (CadSegment s in Figure(layer.ToList(), set.PointToleranceMm, out r, out c)) drop.Add(s);
                rings += r;
                chords += c;
            }
            leftOut = drop.Count;
            return drop.Count == 0 ? segments : segments.Where(s => !drop.Contains(s)).ToList();
        }

        private static int CornerAt(List<CadPoint> corners, CadPoint p, double tol)
        {
            for (int i = 0; i < corners.Count; i++)
                if (corners[i].PlanDistanceTo(p) <= tol) return i;
            return -1;
        }

        /// <summary>Strictly inside the ring in plan (even-odd rule); a point on an edge is not inside.</summary>
        private static bool Inside(List<CadPoint> ring, CadPoint p)
        {
            bool inside = false;
            for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
            {
                CadPoint a = ring[i], b = ring[j];
                double cross = (b.X - a.X) * (p.Y - a.Y) - (b.Y - a.Y) * (p.X - a.X);
                bool within = p.X >= Math.Min(a.X, b.X) - 1e-9 && p.X <= Math.Max(a.X, b.X) + 1e-9 &&
                              p.Y >= Math.Min(a.Y, b.Y) - 1e-9 && p.Y <= Math.Max(a.Y, b.Y) + 1e-9;
                if (Math.Abs(cross) <= 1e-6 * Math.Max(1.0, a.PlanDistanceTo(b)) && within) return false;
                if ((a.Y > p.Y) != (b.Y > p.Y) && p.X < (b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y) + a.X)
                    inside = !inside;
            }
            return inside;
        }
    }
}
