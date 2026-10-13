// -----------------------------------------------------------------------------
// Horizun Core - original Horizun code. Revit-free.
//
// ARCS IN A BAR'S CENTRELINE (acero 3.0, L2). A requirement set declares an arc as
// a three-point arc: the two points it joins and one point it passes through,
// written { "through": [x, y, z] } between them. A circular hoop is two arcs.
//
// The declared VERTICES stay what they are - the points the arcs and lines join -
// so the bar Revit is asked for is made of true arcs. Everything that MEASURES a
// bar (containment, anchorage, length, the audit) uses the path below: the arcs
// sampled finely enough that no chord is further than a stated sagitta from the
// arc, so a measurement is never made against a polygon pretending to be a circle.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;

namespace Horizun.Revit.Core
{
    public static class RebarArcs
    {
        /// <summary>The sagitta the sampled path keeps to, in millimetres, unless told otherwise.</summary>
        public const double DefaultChordMm = 0.05;

        /// <summary>
        /// Whether a declared centreline with arcs is refused before Revit is asked, and why (null: not refused).
        /// MEASURED on Revit 2024 (L2): Rebar.CreateFromCurves fails with an internal error on a CLOSED chain with an
        /// arc in it - a circle of two or four arcs, either style. An OPEN chain is not refused: the anonymized regression case's hoops,
        /// three arcs that run once round and on over a 250 mm lap with a 135 degree hook at each end, were built
        /// that way on Revit 2024 (etapa 1b). Whether Revit builds a given open chain is then Revit's answer, read
        /// back after the commit like any other bar - an apply that Revit refuses rolls back and names the rule.
        /// </summary>
        public static string ChainRefusal(bool closed, int vertexCount, int arcCount)
        {
            if (arcCount <= 0) return null;
            if (!closed) return null;
            return "a closed chain of " + vertexCount + " segment(s) with " + arcCount + " arc(s). Measured on Revit 2024: " +
                   "Rebar.CreateFromCurves fails with an internal error on every closed chain containing an arc. Declare the " +
                   "hoop OPEN, running on past its start over its lap (three arcs: 0 to 180, 180 to 360, and the lap), or " +
                   "place it from a rebar shape (shape_placement on a circular section rule's stirrups).";
        }

        /// <summary>
        /// The centreline as a polyline: every declared vertex, and every arc sampled. Segment i
        /// runs from vertex i to vertex i+1; on a closed bar segment n-1 runs from the last back
        /// to the first. A closed bar's path ends just before it returns to its first point, so
        /// a caller that closes it adds only the last short chord.
        /// </summary>
        public static List<double[]> Path(IList<double[]> vertices, bool closed, IDictionary<int, double[]> arcs, double chordMm)
        {
            var outp = new List<double[]>();
            if (vertices == null || vertices.Count == 0) return outp;
            int n = vertices.Count, segs = closed ? n : n - 1;
            outp.Add(Copy(vertices[0]));
            for (int i = 0; i < segs; i++)
            {
                double[] a = vertices[i], b = vertices[(i + 1) % n];
                double[] t;
                if (arcs != null && arcs.TryGetValue(i, out t) && t != null)
                {
                    List<double[]> pts = ArcPoints(a, t, b, chordMm);
                    for (int k = 1; k < pts.Count; k++)
                        if (!(closed && i == segs - 1 && k == pts.Count - 1)) outp.Add(pts[k]);
                }
                else if (!(closed && i == segs - 1)) outp.Add(Copy(b));
            }
            return outp;
        }

        /// <summary>
        /// The arc from a through t to b, sampled so no chord strays more than chordMm from it.
        /// Null when the three points are collinear (no arc passes through them).
        /// </summary>
        public static List<double[]> ArcPoints(double[] a, double[] t, double[] b, double chordMm)
        {
            double[] centre; double radius; double[] n;
            if (!Circle(a, t, b, out centre, out radius, out n)) return null;
            double[] u = Unit(Sub(a, centre)), v = Cross(n, u);
            double angT = Angle(Sub(t, centre), u, v), angB = Angle(Sub(b, centre), u, v);
            // The normal is (t - a) x (b - a), so a, t, b run counter-clockwise about it: turning
            // positively from a the arc meets t, then b.
            double sweep = angB;
            if (angT > angB) return null;
            double step = chordMm > 0 && radius > chordMm ? 2 * Math.Acos(1 - chordMm / radius) : Math.PI / 32;
            int count = Math.Max(2, (int)Math.Ceiling(Math.Abs(sweep) / step));
            var pts = new List<double[]>(count + 1);
            for (int k = 0; k <= count; k++)
            {
                double ang = sweep * k / count;
                double c = Math.Cos(ang), s = Math.Sin(ang);
                pts.Add(new[] { centre[0] + radius * (c * u[0] + s * v[0]),
                                centre[1] + radius * (c * u[1] + s * v[1]),
                                centre[2] + radius * (c * u[2] + s * v[2]) });
            }
            pts[pts.Count - 1] = Copy(b);
            return pts;
        }

        /// <summary>The circle through three points: false when they are collinear.</summary>
        public static bool Circle(double[] a, double[] b, double[] c, out double[] centre, out double radius, out double[] normal)
        {
            centre = null; radius = 0; normal = null;
            double[] ab = Sub(b, a), ac = Sub(c, a);
            double[] n = Cross(ab, ac);
            double nn = Dot(n, n);
            if (nn < 1e-9 * Math.Max(1, Dot(ab, ab) * Dot(ac, ac))) return false;
            // centre = a + ((|ac|^2 (n x ab) + |ab|^2 (ac x n)) / (2 |n|^2))
            double[] t1 = Cross(n, ab), t2 = Cross(ac, n);
            double sac = Dot(ac, ac), sab = Dot(ab, ab);
            centre = new[]
            {
                a[0] + (sac * t1[0] + sab * t2[0]) / (2 * nn),
                a[1] + (sac * t1[1] + sab * t2[1]) / (2 * nn),
                a[2] + (sac * t1[2] + sab * t2[2]) / (2 * nn)
            };
            radius = Math.Sqrt(Dot(Sub(a, centre), Sub(a, centre)));
            normal = Unit(n);
            return true;
        }

        /// <summary>How far the through point stands off the chord a-b, in millimetres.</summary>
        public static double Sagitta(double[] a, double[] t, double[] b)
        {
            double[] ab = Sub(b, a);
            double l = Math.Sqrt(Dot(ab, ab));
            if (l < 1e-12) return 0;
            double[] cr = Cross(ab, Sub(t, a));
            return Math.Sqrt(Dot(cr, cr)) / l;
        }

        private static double Angle(double[] p, double[] u, double[] v)
        {
            double ang = Math.Atan2(Dot(p, v), Dot(p, u));
            return ang < 0 ? ang + 2 * Math.PI : ang;
        }

        private static double[] Copy(double[] p) => new[] { p[0], p[1], p[2] };
        private static double[] Sub(double[] a, double[] b) => new[] { a[0] - b[0], a[1] - b[1], a[2] - b[2] };
        private static double Dot(double[] a, double[] b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];
        private static double[] Cross(double[] a, double[] b) =>
            new[] { a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0] };
        private static double[] Unit(double[] a)
        {
            double l = Math.Sqrt(Dot(a, a));
            return l < 1e-12 ? new[] { 0.0, 0, 0 } : new[] { a[0] / l, a[1] / l, a[2] / l };
        }
    }
}
