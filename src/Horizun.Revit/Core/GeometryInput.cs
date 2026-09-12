using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    /// <summary>Input geometry in internal feet; no Revit calls and no writes.</summary>
    public static class GeometryInput
    {
        public const double Tolerance = 1e-6; // 0.0003048 mm, absolute positional tolerance.
        // Segments are [x0,y0,x1,y1]. Revit may split one sketch edge at a
        // connection. Compare coverage in both directions, not segment counts.
        public static bool SameBoundaryXY(IReadOnlyList<double[]> a, IReadOnlyList<double[]> b)
        {
            if (a.Count == 0 || b.Count == 0) return false;
            if (a.Concat(b).Any(s => s == null || s.Length != 4 || s.Any(v => double.IsNaN(v) || double.IsInfinity(v)))) return false;
            bool Covered(double[] edge, IReadOnlyList<double[]> others)
            {
                double dx = edge[2] - edge[0], dy = edge[3] - edge[1];
                double length = Math.Sqrt(dx * dx + dy * dy);
                if (length <= Tolerance) return false;
                dx /= length; dy /= length;
                var intervals = new List<double[]>();
                foreach (var s in others)
                {
                    if (Math.Abs((s[0] - edge[0]) * dy - (s[1] - edge[1]) * dx) > Tolerance ||
                        Math.Abs((s[2] - edge[0]) * dy - (s[3] - edge[1]) * dx) > Tolerance) continue;
                    double p = (s[0] - edge[0]) * dx + (s[1] - edge[1]) * dy;
                    double q = (s[2] - edge[0]) * dx + (s[3] - edge[1]) * dy;
                    intervals.Add(new[] { Math.Min(p, q), Math.Max(p, q) });
                }
                double reached = 0;
                foreach (var interval in intervals.OrderBy(s => s[0]))
                {
                    if (interval[1] < 0) continue;
                    if (interval[0] > reached + Tolerance) return false;
                    reached = Math.Max(reached, interval[1]);
                    if (reached >= length - Tolerance) return true;
                }
                return false;
            }
            return a.All(s => Covered(s, b)) && b.All(s => Covered(s, a));
        }
        public static double Number(JToken value, string field)
        {
            if (value == null || (value.Type != JTokenType.Float && value.Type != JTokenType.Integer))
                throw new ArgumentException(field + " must be a number.");
            double n = value.Value<double>();
            if (double.IsNaN(n) || double.IsInfinity(n)) throw new ArgumentException(field + " must be finite.");
            return n;
        }
        public static double AbsoluteZ(double z, double? level, string mode)
        {
            if (mode == "absolute") return z;
            if (mode == "level_offset" && level.HasValue) return level.Value + z;
            throw new ArgumentException("coordinate_mode must be explicit: absolute (internal origin) or level_offset (requires level_id).");
        }
        public static double SlopeRatio(double degrees)
        {
            if (double.IsNaN(degrees) || double.IsInfinity(degrees) || degrees < 0 || degrees >= 90)
                throw new ArgumentException("slope_degrees must be finite and in [0,90).");
            return Math.Tan(degrees * Math.PI / 180.0);
        }
        public static List<List<double[]>> HorizontalProfile(JToken token, double scale, double shortCurveTolerance)
        {
            if (!(token is JArray loops) || loops.Count == 0 || loops.Count > 128)
                throw new ArgumentException("profile must contain 1..128 contours of XYZ points.");
            var result = new List<List<double[]>>(); double? plane = null; int total = 0;
            for (int l = 0; l < loops.Count; l++)
            {
                if (!(loops[l] is JArray points) || points.Count < 3 || points.Count > 2000 || (total += points.Count) > 4000)
                    throw new ArgumentException("profile[" + l + "] needs 3..2000 XYZ points; total limit is 4000.");
                var contour = new List<double[]>();
                for (int i = 0; i < points.Count; i++)
                {
                    if (!(points[i] is JArray xyz) || xyz.Count != 3)
                        throw new ArgumentException("profile[" + l + "][" + i + "] must contain exactly three coordinates.");
                    var p = Enumerable.Range(0, 3).Select(c => Number(xyz[c], "profile[" + l + "][" + i + "][" + c + "]") * scale).ToArray();
                    if (plane.HasValue && Math.Abs(p[2] - plane.Value) > Tolerance)
                        throw new ArgumentException("profile[" + l + "][" + i + "] is not on the common horizontal plane.");
                    plane = plane ?? p[2]; contour.Add(p);
                }
                if (Distance(contour[0], contour[contour.Count - 1]) <= Tolerance) contour.RemoveAt(contour.Count - 1);
                if (contour.Count < 3) throw new ArgumentException("profile[" + l + "] has fewer than three distinct points.");
                for (int i = 0; i < contour.Count; i++)
                {
                    var a = contour[i]; var b = contour[(i + 1) % contour.Count];
                    if (Distance(a, b) <= Math.Max(Tolerance, shortCurveTolerance))
                        throw new ArgumentException("profile[" + l + "] edge " + i + " is too short.");
                    for (int j = i + 1; j < contour.Count; j++)
                    {
                        if (j == i + 1 || (i == 0 && j == contour.Count - 1)) continue;
                        if (Intersects(a, b, contour[j], contour[(j + 1) % contour.Count]))
                            throw new ArgumentException("profile[" + l + "] self-intersects at edges " + i + " and " + j + ".");
                    }
                }
                if (Math.Abs(Area(contour)) <= Tolerance * Tolerance) throw new ArgumentException("profile[" + l + "] has zero area.");
                foreach (var other in result)
                    for (int i = 0; i < contour.Count; i++)
                        for (int j = 0; j < other.Count; j++)
                            if (Intersects(contour[i], contour[(i + 1) % contour.Count], other[j], other[(j + 1) % other.Count]))
                                throw new ArgumentException("profile[" + l + "] touches or crosses another contour.");
                if (l > 0 && !Inside(contour[0], result[0])) throw new ArgumentException("profile[" + l + "] hole is outside the perimeter.");
                for (int h = 1; h < result.Count; h++)
                    if (Inside(contour[0], result[h]) || Inside(result[h][0], contour))
                        throw new ArgumentException("profile holes cannot overlap or contain one another.");
                result.Add(contour);
            }
            return result;
        }
        private static double Distance(double[] a, double[] b) => Math.Sqrt(a.Zip(b, (x, y) => (x - y) * (x - y)).Sum());
        private static double Cross(double[] a, double[] b, double[] c) => (b[0] - a[0]) * (c[1] - a[1]) - (b[1] - a[1]) * (c[0] - a[0]);
        private static bool On(double[] a, double[] b, double[] p) => Math.Abs(Cross(a, b, p)) <= Tolerance * Distance(a, b) &&
            p[0] >= Math.Min(a[0], b[0]) - Tolerance && p[0] <= Math.Max(a[0], b[0]) + Tolerance &&
            p[1] >= Math.Min(a[1], b[1]) - Tolerance && p[1] <= Math.Max(a[1], b[1]) + Tolerance;
        private static bool Intersects(double[] a, double[] b, double[] c, double[] d) => On(a, b, c) || On(a, b, d) || On(c, d, a) || On(c, d, b) ||
            ((Cross(a, b, c) > 0) != (Cross(a, b, d) > 0) && (Cross(c, d, a) > 0) != (Cross(c, d, b) > 0));
        private static double Area(List<double[]> p) { double a = 0; for (int i = 0; i < p.Count; i++) { var q = p[(i + 1) % p.Count]; a += p[i][0] * q[1] - q[0] * p[i][1]; } return a / 2; }
        private static bool Inside(double[] p, List<double[]> loop)
        {
            bool inside = false;
            for (int i = 0, j = loop.Count - 1; i < loop.Count; j = i++)
                if ((loop[i][1] > p[1]) != (loop[j][1] > p[1]) && p[0] < (loop[j][0] - loop[i][0]) * (p[1] - loop[i][1]) / (loop[j][1] - loop[i][1]) + loop[i][0]) inside = !inside;
            return inside;
        }
    }
}
