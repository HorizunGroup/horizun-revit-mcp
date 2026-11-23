// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// A FLOOR PROFILE THE REHEARSAL ACCEPTED AND THE APPLY REFUSED (field cycle, 2026-10-21).
//
// MEASURED (twice, cycles 9 and 11): horizun_create_elements kind=floor with a profile that a
// geometry library calls valid - a neck of zero width, a vertex that touches another edge - passed
// the default dry run and failed at apply with "curve loops cannot compose a valid boundary". The
// cause is Revit's own tolerance: two boundary curves that are NOT adjacent must stay further apart
// than ShortCurveTolerance (about 0.8 mm), and an edge that doubles back on its neighbour is a
// spike of width zero. Exactly-touching and self-crossing were already refused; this closes the
// narrow band between "touching" and "far enough apart".
//
// The default dry run validates arguments only. These checks are arguments too, so they run there:
// the refusal comes before anything is written, names the two edges and the distance, and says
// what Revit needs.
//
// Revit-free: plain 2D geometry on the contour.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;

namespace Horizun.Revit.Core
{
    public static class LoopHealthRules
    {
        /// <summary>Shortest distance between two 2D segments.</summary>
        public static double SegmentDistance(double[] a, double[] b, double[] c, double[] d)
        {
            if (Crosses(a, b, c, d)) return 0;
            return Math.Min(Math.Min(PointToSegment(a, c, d), PointToSegment(b, c, d)),
                            Math.Min(PointToSegment(c, a, b), PointToSegment(d, a, b)));
        }

        private static double Cross(double[] o, double[] p, double[] q) => (p[0] - o[0]) * (q[1] - o[1]) - (p[1] - o[1]) * (q[0] - o[0]);

        private static bool Crosses(double[] a, double[] b, double[] c, double[] d)
        {
            double d1 = Cross(a, b, c), d2 = Cross(a, b, d), d3 = Cross(c, d, a), d4 = Cross(c, d, b);
            return ((d1 > 0) != (d2 > 0)) && ((d3 > 0) != (d4 > 0)) && d1 != 0 && d2 != 0 && d3 != 0 && d4 != 0;
        }

        private static double PointToSegment(double[] p, double[] a, double[] b)
        {
            double dx = b[0] - a[0], dy = b[1] - a[1];
            double len2 = dx * dx + dy * dy;
            double t = len2 < 1e-30 ? 0 : Math.Max(0, Math.Min(1, ((p[0] - a[0]) * dx + (p[1] - a[1]) * dy) / len2));
            double x = a[0] + t * dx - p[0], y = a[1] + t * dy - p[1];
            return Math.Sqrt(x * x + y * y);
        }

        /// <summary>
        /// Problems Revit would refuse at apply, as sentences: a neck or touching vertex (non-adjacent edges
        /// closer than `minSeparationFeet`) and a spike (an edge that doubles back on its neighbour). Empty
        /// when the contour is healthy. Distances are reported in millimetres.
        /// </summary>
        public static List<string> Problems(IList<double[]> contour, double minSeparationFeet)
        {
            var problems = new List<string>();
            if (contour == null || contour.Count < 3) return problems;
            int n = contour.Count;
            for (int i = 0; i < n; i++)
            {
                double[] a = contour[i], b = contour[(i + 1) % n];
                for (int j = i + 2; j < n; j++)
                {
                    if (i == 0 && j == n - 1) continue; // adjacent through the closing edge
                    double[] c = contour[j], d = contour[(j + 1) % n];
                    double gap = SegmentDistance(a, b, c, d);
                    if (gap > 0 && gap < minSeparationFeet)
                        problems.Add("edges " + i + " and " + j + " come within " + Mm(gap) + " mm of each other (a neck or a vertex that nearly touches an edge); " +
                                     "Revit needs non-adjacent boundary curves at least " + Mm(minSeparationFeet) + " mm apart");
                }
                // Spike: the next edge folds back over this one.
                double[] e = contour[(i + 2) % n];
                double ux = b[0] - a[0], uy = b[1] - a[1], vx = e[0] - b[0], vy = e[1] - b[1];
                double lu = Math.Sqrt(ux * ux + uy * uy), lv = Math.Sqrt(vx * vx + vy * vy);
                if (lu > 1e-12 && lv > 1e-12)
                {
                    double cos = (ux * vx + uy * vy) / (lu * lv);
                    double sin = Math.Abs(ux * vy - uy * vx) / (lu * lv);
                    if (cos < 0 && sin < 1e-4)
                        problems.Add("edges " + i + " and " + ((i + 1) % n) + " double back on each other (a spike of width zero)");
                }
            }
            return problems;
        }

        private static string Mm(double feet) => (feet * 304.8).ToString("0.###", CultureInfo.InvariantCulture);
    }
}
