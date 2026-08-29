// -----------------------------------------------------------------------------
// Horizun Revit MCP - does this set actually FIT, and what should come back out.
// Original Horizun code. No Revit types.
//
// The question this file answers is the one Revit will not ask on your behalf.
// A rebar set whose array is longer than its host is created without complaint:
// the element exists, the host is right, the type is right, and some of the
// steel is in the air outside the beam. Nothing in the reply distinguishes it
// from a correct one.
//
// So the plan projects the host, the bar and every bar POSITION onto the
// distribution direction and compares them - all of them, not the two ends -
// before a transaction is opened. And it says out loud that this is a projection
// onto one axis, not a solid containment test, because a claim about geometry
// that does not say how it was measured is not worth much.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Horizun.Revit.Core
{
    /// <summary>An interval on the distribution axis, in millimetres.</summary>
    public struct Span
    {
        public double Min;
        public double Max;
        public Span(double min, double max) { Min = Math.Min(min, max); Max = Math.Max(min, max); }
        public double Length { get { return Max - Min; } }
    }

    public sealed class RebarFitVerdict
    {
        public bool Fits;
        public string Code;
        public string Why;
        /// <summary>Indices of the bar positions that do not fit. Empty when it fits.</summary>
        public List<int> OutsideIndices = new List<int>();
        /// <summary>How far the worst offender sticks out, in millimetres. Zero when it fits.</summary>
        public double WorstOvershootMm;
        public Span HostSpan;
        public Span BarSpan;
        public Span SetSpan;
    }

    public static class RebarPlanRules
    {
        public const string CodeFits = "fits";
        public const string CodeSetOutsideHost = "set_outside_host";
        public const string CodeBarOutsideHost = "bar_outside_host";
        public const string CodeHostNotMeasured = "host_extent_not_measured";
        public const string CodeBarNotMeasured = "bar_extent_not_measured";
        public const string CodeNormalDegenerate = "normal_degenerate";

        /// <summary>
        /// Project a point onto a direction. The direction need not be normalised;
        /// it is normalised here, because a requirement set writes [0, 1, 0] and
        /// also writes [0, 4000, 0] meaning the same axis.
        /// </summary>
        public static double Project(double[] point, double[] direction)
        {
            double n = Norm(direction);
            if (n < 1e-12) return double.NaN;
            return (point[0] * direction[0] + point[1] * direction[1] + point[2] * direction[2]) / n;
        }

        public static double Norm(double[] v)
        {
            return Math.Sqrt(v[0] * v[0] + v[1] * v[1] + v[2] * v[2]);
        }

        /// <summary>The interval a set of points occupies along a direction.</summary>
        public static Span SpanOf(IList<double[]> points, double[] direction)
        {
            double lo = double.MaxValue, hi = double.MinValue;
            foreach (double[] p in points)
            {
                double d = Project(p, direction);
                if (double.IsNaN(d)) continue;
                if (d < lo) lo = d;
                if (d > hi) hi = d;
            }
            if (lo > hi) return new Span(0, 0);
            return new Span(lo, hi);
        }

        /// <summary>
        /// The eight corners of an axis-aligned box, so a box can be projected onto
        /// any direction rather than only onto the axis it was measured on.
        /// </summary>
        public static List<double[]> BoxCorners(double[] min, double[] max)
        {
            var pts = new List<double[]>();
            for (int i = 0; i < 8; i++)
                pts.Add(new[]
                {
                    (i & 1) == 0 ? min[0] : max[0],
                    (i & 2) == 0 ? min[1] : max[1],
                    (i & 4) == 0 ? min[2] : max[2]
                });
            return pts;
        }

        /// <summary>
        /// Does every bar position of this set land inside the host, measured along
        /// the distribution direction?
        ///
        /// This is a PROJECTION onto one axis. It proves a set is too long for its
        /// host; it does not prove a bar is inside the concrete in the other two
        /// directions, and the reply says so rather than letting `fits: true` be
        /// read as containment.
        /// </summary>
        public static RebarFitVerdict Fit(IList<double[]> barPoints, IList<double[]> hostCorners,
                                          double[] direction, IList<double> positionsMm, double toleranceMm)
        {
            var v = new RebarFitVerdict();
            if (direction == null || Norm(direction) < 1e-12)
            {
                v.Code = CodeNormalDegenerate;
                v.Why = "the distribution direction is the zero vector, so nothing can be projected onto it.";
                return v;
            }
            // THE BAR'S OWN EXTENT, with the same standard as the host's. An empty
            // list projected to Span(0,0) made the bar a point at the origin, and a
            // point at the origin is inside almost any host - so an unmeasurable bar
            // came back `fits`, from the same function whose host arm refuses to
            // call an unmeasured extent a pass.
            if (barPoints == null || barPoints.Count == 0)
            {
                v.Code = CodeBarNotMeasured;
                v.Why = "the bar's own extent could not be measured, so whether it fits inside the host is " +
                        "UNKNOWN. That is not the same as a bar that fits, and it is not reported as one.";
                return v;
            }
            if (hostCorners == null || hostCorners.Count == 0)
            {
                v.Code = CodeHostNotMeasured;
                v.Why = "the host extent could not be measured, so whether the set fits inside it is UNKNOWN. " +
                        "That is not the same as a set that fits, and it is not reported as one.";
                return v;
            }

            v.HostSpan = SpanOf(hostCorners, direction);
            v.BarSpan = SpanOf(barPoints, direction);

            // THE INTERVAL THE SET ACTUALLY OCCUPIES, from the smallest and largest
            // position rather than the first and the last. A set marching AGAINST the
            // declared normal has descending positions - which is exactly what the
            // audit produces when it re-bases measured positions on bar 0 - and
            // first/last then described an interval the set is not in.
            double lowest = 0.0, highest = 0.0;
            if (positionsMm != null && positionsMm.Count > 0)
            {
                lowest = positionsMm[0]; highest = positionsMm[0];
                foreach (double d in positionsMm)
                {
                    if (d < lowest) lowest = d;
                    if (d > highest) highest = d;
                }
            }
            v.SetSpan = new Span(v.BarSpan.Min + lowest, v.BarSpan.Max + highest);

            // THE BASE BAR FIRST. A bar already outside its host is a different
            // finding from a set that runs off the end, and fixing the array length
            // would not help.
            if (v.BarSpan.Min < v.HostSpan.Min - toleranceMm || v.BarSpan.Max > v.HostSpan.Max + toleranceMm)
            {
                v.Code = CodeBarOutsideHost;
                v.Why = "the bar itself lies outside the host along the distribution direction, before the set " +
                        "is arrayed at all: the bar occupies " + Mm(v.BarSpan.Min) + " to " + Mm(v.BarSpan.Max) +
                        " and the host runs " + Mm(v.HostSpan.Min) + " to " + Mm(v.HostSpan.Max) + ".";
                v.WorstOvershootMm = Math.Max(v.HostSpan.Min - v.BarSpan.Min, v.BarSpan.Max - v.HostSpan.Max);
                return v;
            }

            // EVERY POSITION. Checking the two ends would pass a set whose middle
            // was fine and whose declaration was not, and it is one line either way.
            if (positionsMm != null)
                for (int i = 0; i < positionsMm.Count; i++)
                {
                    double lo = v.BarSpan.Min + positionsMm[i];
                    double hi = v.BarSpan.Max + positionsMm[i];
                    double over = Math.Max(v.HostSpan.Min - lo, hi - v.HostSpan.Max);
                    if (over > toleranceMm)
                    {
                        v.OutsideIndices.Add(i);
                        if (over > v.WorstOvershootMm) v.WorstOvershootMm = over;
                    }
                }

            if (v.OutsideIndices.Count > 0)
            {
                v.Code = CodeSetOutsideHost;
                v.Why = v.OutsideIndices.Count + " of " + (positionsMm == null ? 0 : positionsMm.Count) +
                        " bar positions land outside the host along the distribution direction; the worst is " +
                        Mm(v.WorstOvershootMm) + " past it. The host runs " + Mm(v.HostSpan.Min) + " to " +
                        Mm(v.HostSpan.Max) + " on that axis and the set would occupy " + Mm(v.SetSpan.Min) +
                        " to " + Mm(v.SetSpan.Max) + ".";
                return v;
            }

            v.Fits = true;
            v.Code = CodeFits;
            v.Why = "every bar position lies within the host measured ALONG THE DISTRIBUTION DIRECTION. This is " +
                    "a projection onto one axis: it proves the set is not too long for its host, and it does " +
                    "not prove the bar is inside the concrete in the other two directions.";
            return v;
        }

        /// <summary>
        /// The length of one bar, from its declared centreline. Hooks are NOT in
        /// this number - Revit adds them and reports the result, and guessing at
        /// them here would produce an expectation the model can never match.
        /// </summary>
        /// <summary>
        /// Below this much reach out of the line, a polyline has no plane worth
        /// naming. Half a millimetre is under any tolerance this work uses and well
        /// above the noise a tessellation or a three-decimal rounding introduces.
        /// </summary>
        public const double MinimumInPlaneReachMm = 0.5;

        public static double CentrelineLengthMm(IList<double[]> points, bool closed)
        {
            if (points == null || points.Count < 2) return 0;
            double total = 0;
            for (int i = 1; i < points.Count; i++) total += Distance(points[i - 1], points[i]);
            if (closed) total += Distance(points[points.Count - 1], points[0]);
            return total;
        }

        public static double Distance(double[] a, double[] b)
        {
            double dx = a[0] - b[0], dy = a[1] - b[1], dz = a[2] - b[2];
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        /// <summary>
        /// How far each declared point lies off the least-squares plane of the run,
        /// in millimetres. Revit refuses a non-planar shape-driven bar deep inside
        /// its own geometry engine with a message about nothing in particular, so
        /// this is measured before the call.
        ///
        /// IT NAMES NO CULPRIT, on purpose. The first version took the plane of the
        /// first three points, which means a typo in point 2 DEFINES the plane and
        /// point 3 gets blamed for it. The best-fit plane does not have that bug and
        /// has a different one: displace one vertex of a rectangle and all four end
        /// up equidistant from the fitted plane, so there is no point to accuse.
        /// What is true is the deviation of every point, and that is what comes back.
        ///
        /// THE NORMAL IS THE SMALLEST EIGENVECTOR OF THE COVARIANCE, not Newell's.
        /// Newell's vector area is the signed area of the projected polygon, and it
        /// CANCELS TO ZERO for a run that folds back on itself - measured: the
        /// six-point run (100,0,10) (0,100,-20) (-100,0,40) (100,0,-40) (0,-100,20)
        /// (-100,0,-10) has a Newell normal of exactly (0,0,0) and points lying
        /// 25 mm off any plane through them. Reported as planar, it goes straight to
        /// the Revit call this check exists to pre-empt.
        /// </summary>
        public static List<double> PlanarityDeviationsMm(IList<double[]> points)
        {
            var result = new List<double>();
            if (points == null) return result;
            if (points.Count < 4)
            {
                // Three points define a plane; fewer cannot be non-planar.
                for (int i = 0; i < points.Count; i++) result.Add(0.0);
                return result;
            }

            int n = points.Count;
            var centroid = new double[3];
            foreach (double[] p in points)
            {
                if (p == null || p.Length < 3 || !Finite(p))
                {
                    // A point that is not a point cannot be measured, and reporting
                    // zero deviation for it would be a claim.
                    for (int i = 0; i < n; i++) result.Add(double.NaN);
                    return result;
                }
                centroid[0] += p[0]; centroid[1] += p[1]; centroid[2] += p[2];
            }
            centroid[0] /= n; centroid[1] /= n; centroid[2] /= n;

            // The symmetric covariance of the points about their centroid. Its
            // smallest eigenvector is the normal of the plane that minimises the
            // sum of squared distances - which is what "best fit" means.
            double xx = 0, xy = 0, xz = 0, yy = 0, yz = 0, zz = 0;
            foreach (double[] p in points)
            {
                double dx = p[0] - centroid[0], dy = p[1] - centroid[1], dz = p[2] - centroid[2];
                xx += dx * dx; xy += dx * dy; xz += dx * dz;
                yy += dy * dy; yz += dy * dz; zz += dz * dz;
            }

            double[] normal = SmallestEigenvector(xx, xy, xz, yy, yz, zz);
            double len = Norm(normal);
            if (len < 1e-12)
            {
                // Every point coincident: there is no plane to be off, and no
                // deviation either.
                for (int i = 0; i < n; i++) result.Add(0.0);
                return result;
            }

            foreach (double[] p in points)
            {
                double dx = p[0] - centroid[0], dy = p[1] - centroid[1], dz = p[2] - centroid[2];
                result.Add(Math.Abs(dx * normal[0] + dy * normal[1] + dz * normal[2]) / len);
            }
            return result;
        }

        /// <summary>
        /// The unit normal of the plane that best fits these points, or null when
        /// there is no plane to speak of - fewer than three points, a point that is
        /// not a point, or every point on one line. Same machinery as the planarity
        /// check, exposed because the audit compares the PLANE two bars lie in and
        /// a bar rotated in its own plane is a different bar with the same length.
        /// </summary>
        public static double[] BestFitNormal(IList<double[]> points)
        {
            if (points == null || points.Count < 3) return null;
            int n = points.Count;
            var centroid = new double[3];
            foreach (double[] p in points)
            {
                if (p == null || p.Length < 3 || !Finite(p)) return null;
                centroid[0] += p[0]; centroid[1] += p[1]; centroid[2] += p[2];
            }
            centroid[0] /= n; centroid[1] /= n; centroid[2] /= n;

            double xx = 0, xy = 0, xz = 0, yy = 0, yz = 0, zz = 0;
            foreach (double[] p in points)
            {
                double dx = p[0] - centroid[0], dy = p[1] - centroid[1], dz = p[2] - centroid[2];
                xx += dx * dx; xy += dx * dy; xz += dx * dz;
                yy += dy * dy; yz += dy * dz; zz += dz * dz;
            }

            // A straight bar has no plane: two of the three eigenvalues are zero and
            // the "normal" is whichever of an infinite family the arithmetic lands
            // on. Saying nothing is the honest answer.
            double trace = xx + yy + zz;
            if (trace < 1e-12) return null;
            double[] normal = SmallestEigenvector(xx, xy, xz, yy, yz, zz);
            double len = Norm(normal);
            if (len < 1e-12) return null;

            double spread = 0;
            foreach (double[] p in points)
            {
                double dx = p[0] - centroid[0], dy = p[1] - centroid[1], dz = p[2] - centroid[2];
                double along = (dx * normal[0] + dy * normal[1] + dz * normal[2]) / len;
                double perp2 = dx * dx + dy * dy + dz * dz - along * along;
                if (perp2 > spread) spread = perp2;
            }
            // COLLINEAR: everything is within rounding of the line, so no plane.
            //
            // This guard compared SQUARED lengths at a ratio of 1e-9, which is a
            // ratio of 3.2e-5 on lengths: for a metre-long bar it only refused a
            // plane when the out-of-line reach was under 0.016 mm. Anything above
            // that returned a "plane" whose normal was defined entirely by a
            // sub-millimetre wobble - and Revit's bend radius smooths that wobble
            // out of the drawn bar, whose own normal then came from tessellation
            // noise in an unrelated direction. Two unrelated normals compared at a
            // hundredth of a degree is a plane_differs finding about a correct bar.
            double reach = Math.Sqrt(SecondMoment(points, centroid, normal, len));
            if (reach < MinimumInPlaneReachMm || reach < 1e-3 * Math.Sqrt(Math.Max(1.0, spread)))
                return null;

            return new[] { normal[0] / len, normal[1] / len, normal[2] / len };
        }

        /// <summary>How much the points spread in the plane, perpendicular to the line they mostly follow.</summary>
        private static double SecondMoment(IList<double[]> points, double[] centroid, double[] normal, double len)
        {
            // The direction the points mostly follow.
            double[] axis = null;
            double best = -1;
            foreach (double[] p in points)
            {
                double dx = p[0] - centroid[0], dy = p[1] - centroid[1], dz = p[2] - centroid[2];
                double m = dx * dx + dy * dy + dz * dz;
                if (m > best) { best = m; axis = new[] { dx, dy, dz }; }
            }
            if (axis == null || best < 1e-18) return 0;
            double an = Math.Sqrt(best);
            axis = new[] { axis[0] / an, axis[1] / an, axis[2] / an };

            // The remaining in-plane direction, and how far the points reach along it.
            double[] inPlane = Cross(new[] { normal[0] / len, normal[1] / len, normal[2] / len }, axis);
            double ipn = Norm(inPlane);
            if (ipn < 1e-12) return 0;
            double worst = 0;
            foreach (double[] p in points)
            {
                double dx = p[0] - centroid[0], dy = p[1] - centroid[1], dz = p[2] - centroid[2];
                double d = Math.Abs(dx * inPlane[0] + dy * inPlane[1] + dz * inPlane[2]) / ipn;
                if (d > worst) worst = d;
            }
            return worst * worst;
        }

        /// <summary>
        /// The eigenvector of the smallest eigenvalue of a symmetric 3x3 matrix,
        /// in closed form. No iteration, no library: the characteristic polynomial
        /// of a symmetric 3x3 has three real roots and a known trigonometric
        /// solution, and the eigenvector follows from the cross products of the
        /// rows of (A - lambda I).
        /// </summary>
        private static double[] SmallestEigenvector(double xx, double xy, double xz,
                                                    double yy, double yz, double zz)
        {
            double p1 = xy * xy + xz * xz + yz * yz;
            double q = (xx + yy + zz) / 3.0;
            if (p1 < 1e-18)
            {
                // Already diagonal: the smallest eigenvalue is the smallest diagonal
                // entry and its eigenvector is that axis.
                if (xx <= yy && xx <= zz) return new[] { 1.0, 0.0, 0.0 };
                return yy <= zz ? new[] { 0.0, 1.0, 0.0 } : new[] { 0.0, 0.0, 1.0 };
            }
            double p2 = (xx - q) * (xx - q) + (yy - q) * (yy - q) + (zz - q) * (zz - q) + 2 * p1;
            double p = Math.Sqrt(p2 / 6.0);
            double bxx = (xx - q) / p, byy = (yy - q) / p, bzz = (zz - q) / p;
            double bxy = xy / p, bxz = xz / p, byz = yz / p;
            double det = bxx * (byy * bzz - byz * byz)
                       - bxy * (bxy * bzz - byz * bxz)
                       + bxz * (bxy * byz - byy * bxz);
            double r = det / 2.0;
            if (r < -1.0) r = -1.0; else if (r > 1.0) r = 1.0;
            double phi = Math.Acos(r) / 3.0;
            // eig1 >= eig2 >= eig3; the smallest is eig3.
            double eig3 = q + 2 * p * Math.Cos(phi + 2.0 * Math.PI / 3.0);

            // (A - eig3 I) has rank 2; the null space is the cross product of two
            // independent rows. Take the largest of the three candidates so a
            // near-degenerate pair does not decide it.
            double[] r0 = { xx - eig3, xy, xz };
            double[] r1 = { xy, yy - eig3, yz };
            double[] r2 = { xz, yz, zz - eig3 };
            double[] c01 = Cross(r0, r1), c02 = Cross(r0, r2), c12 = Cross(r1, r2);
            double n01 = Norm(c01), n02 = Norm(c02), n12 = Norm(c12);
            if (n01 >= n02 && n01 >= n12) return c01;
            return n02 >= n12 ? c02 : c12;
        }

        private static bool Finite(double[] p)
        {
            for (int i = 0; i < p.Length; i++)
                if (double.IsNaN(p[i]) || double.IsInfinity(p[i])) return false;
            return true;
        }

        /// <summary>True when no declared point is further than the tolerance off the best-fit plane.</summary>
        public static bool IsPlanar(IList<double[]> points, double toleranceMm, out double worstMm)
        {
            worstMm = 0;
            foreach (double d in PlanarityDeviationsMm(points))
            {
                // NaN is not "no deviation": it is a point that could not be measured,
                // and it must not pass a tolerance test.
                if (double.IsNaN(d)) { worstMm = double.NaN; return false; }
                if (d > worstMm) worstMm = d;
            }
            return worstMm <= toleranceMm;
        }

        private static double[] Sub(double[] a, double[] b)
        {
            return new[] { a[0] - b[0], a[1] - b[1], a[2] - b[2] };
        }

        private static double[] Cross(double[] u, double[] w)
        {
            return new[]
            {
                u[1] * w[2] - u[2] * w[1],
                u[2] * w[0] - u[0] * w[2],
                u[0] * w[1] - u[1] * w[0]
            };
        }

        private static string Mm(double v)
        {
            return v.ToString("0.##", CultureInfo.InvariantCulture) + " mm";
        }
    }
}
