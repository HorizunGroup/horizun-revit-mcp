// -----------------------------------------------------------------------------
// Horizun Revit MCP - steel across JOINED elements whose shapes are not convex.
// Original Horizun code. No Revit types.
//
// In a real model the elements a bar runs into are joined: a beam cut by a round
// column, a footing notched where a foundation beam sits. The cut element is not
// convex, and the union's outer boundary used to be built by clipping every face
// against the OTHER elements' face planes - exact against a convex solid and
// only against one. So every bar crossing a joint was refused.
//
// Inside-ness never needed convexity: it is the winding number of each element.
// What did is the DISTANCE TO AIR, the number the surface and cover checks run
// on. Here it is answered in three steps, cheapest first:
//
//  1. The nearest point of the candidate boundary (every face, clipped against
//     the convex elements only). If the point just outside it is air - outside
//     every element and not inside a seam - the distance is exact to 0.001 mm:
//     the candidate boundary contains the true one, so nothing is nearer.
//  2. Otherwise the nearest face is buried in another element: the sample sits
//     near a joint. Rays are cast in a fixed set of directions and each is
//     followed through every element's triangles to the first place it leaves
//     the union for good. The smallest exit, times the cosine of the set's
//     covering angle, is the clearance: never more than the truth, and short of
//     it by at most R (1 - cos theta) at R mm.
//  3. The exact depth inside any one element that holds the sample is a hard
//     lower bound, and the answer is never below it.
//
// A SEAM is not air. Revit triangulates each element on its own, so two curved
// faces that coincide in the model become two inscribed polygons that do not,
// with slivers between them no thicker than the chord tolerance. A gap between
// two DIFFERENT elements whose middle lies within that tolerance of both is a
// seam; anything wider is air. Meshes that declare no curved face get 0.01 mm.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;

namespace Horizun.Revit.Core
{
    /// <summary>How a jointed union was measured over one bar, for the verdict.</summary>
    internal sealed class JointStats
    {
        public int ExactSamples;
        public int RaySamples;
        public double LargestRayClearanceMm;
        public bool Capped;
    }

    internal sealed class UnionJoints
    {
        /// <summary>A seam no thinner than this is assumed even between exactly meshed elements.</summary>
        public const double MinSeamMm = 0.01;
        /// <summary>The offset at which "just outside the nearest face" is tested.</summary>
        public const double CertificateOffsetMm = 1e-3;
        /// <summary>Closer than this to an element's face, a point is ON it.</summary>
        public const double OnFaceMm = 1e-6;
        /// <summary>The most elements a jointed union measures (a ray crossing is tagged by element in six bits).</summary>
        public const int MaxParts = 64;

        public HostMesh[] Parts;
        public bool[] Outward;
        public double SeamMm;
        /// <summary>For each triangle of the union's candidate boundary, the element it came from.</summary>
        public List<int> TrianglePart = new List<int>();

        // Per element, per usable triangle: vertex a, edges e1 e2, outward unit normal, bounding sphere.
        private double[][] _tri;
        private int[] _triCount;

        public static UnionJoints Create(IList<HostMesh> parts, bool[] outward)
        {
            if (parts.Count > MaxParts) return null;
            var j = new UnionJoints { Parts = new HostMesh[parts.Count], Outward = outward };
            double seam = MinSeamMm;
            for (int i = 0; i < parts.Count; i++)
            {
                j.Parts[i] = parts[i];
                if (parts[i].AnyCurvedFace && parts[i].ChordToleranceMm > seam) seam = parts[i].ChordToleranceMm;
            }
            j.SeamMm = seam;
            j._tri = new double[parts.Count][];
            j._triCount = new int[parts.Count];
            for (int i = 0; i < parts.Count; i++)
            {
                HostMesh m = parts[i];
                var buf = new double[m.Triangles.Count * Stride];
                int n = 0;
                foreach (int[] t in m.Triangles)
                {
                    double[] a = m.Vertices[t[0]], b = m.Vertices[t[1]], c = m.Vertices[t[2]];
                    double e1x = b[0] - a[0], e1y = b[1] - a[1], e1z = b[2] - a[2];
                    double e2x = c[0] - a[0], e2y = c[1] - a[1], e2z = c[2] - a[2];
                    double nx = e1y * e2z - e1z * e2y, ny = e1z * e2x - e1x * e2z, nz = e1x * e2y - e1y * e2x;
                    double nl = Math.Sqrt(nx * nx + ny * ny + nz * nz);
                    if (nl <= 1e-12) continue;
                    double s = outward[i] ? 1.0 / nl : -1.0 / nl;
                    double cx = (a[0] + b[0] + c[0]) / 3, cy = (a[1] + b[1] + c[1]) / 3, cz = (a[2] + b[2] + c[2]) / 3;
                    double r = Math.Max(Dist(cx, cy, cz, a), Math.Max(Dist(cx, cy, cz, b), Dist(cx, cy, cz, c)));
                    int o = n * Stride;
                    buf[o] = a[0]; buf[o + 1] = a[1]; buf[o + 2] = a[2];
                    buf[o + 3] = e1x; buf[o + 4] = e1y; buf[o + 5] = e1z;
                    buf[o + 6] = e2x; buf[o + 7] = e2y; buf[o + 8] = e2z;
                    buf[o + 9] = nx * s; buf[o + 10] = ny * s; buf[o + 11] = nz * s;
                    buf[o + 12] = cx; buf[o + 13] = cy; buf[o + 14] = cz; buf[o + 15] = r;
                    n++;
                }
                j._tri[i] = buf;
                j._triCount[i] = n;
            }

            // FOR THE RAYS, long triangles cut into pieces no wider than RayPieceMm across. The
            // surface is the same; what changes is that a piece far from the sample is dropped
            // before a ray is tested against it. A round column's side arrives as triangles the
            // full height of the column, and every one of them was otherwise "near" every sample.
            j._ray = new double[parts.Count][];
            j._rayCount = new int[parts.Count];
            for (int i = 0; i < parts.Count; i++)
            {
                var pieces = new List<double>();
                double[] buf = j._tri[i];
                for (int k = 0; k < j._triCount[i]; k++)
                {
                    int o = k * Stride;
                    double[] a = { buf[o], buf[o + 1], buf[o + 2] };
                    double[] b = { a[0] + buf[o + 3], a[1] + buf[o + 4], a[2] + buf[o + 5] };
                    double[] c = { a[0] + buf[o + 6], a[1] + buf[o + 7], a[2] + buf[o + 8] };
                    Split(a, b, c, buf[o + 9], buf[o + 10], buf[o + 11], pieces, 0);
                }
                j._ray[i] = pieces.ToArray();
                j._rayCount[i] = pieces.Count / Stride;
            }

            j._lo = new double[parts.Count][];
            j._hi = new double[parts.Count][];
            for (int i = 0; i < parts.Count; i++)
            {
                double[] lo = { double.MaxValue, double.MaxValue, double.MaxValue };
                double[] hi = { double.MinValue, double.MinValue, double.MinValue };
                foreach (double[] v in parts[i].Vertices)
                    for (int k = 0; k < 3; k++)
                    {
                        if (v[k] < lo[k]) lo[k] = v[k];
                        if (v[k] > hi[k]) hi[k] = v[k];
                    }
                j._lo[i] = lo;
                j._hi[i] = hi;
            }
            return j;
        }

        // Per element, its bounding box: a point outside it is outside the element and not on it.
        private double[][] _lo, _hi;
        // Per candidate-boundary triangle: a bounding sphere (cx, cy, cz, r), to skip the far ones.
        private double[] _boundarySpheres;

        /// <summary>Called once the union's candidate boundary is complete.</summary>
        public void Prepare(HostMesh u)
        {
            _boundarySpheres = new double[u.Triangles.Count * 4];
            for (int i = 0; i < u.Triangles.Count; i++)
            {
                int[] t = u.Triangles[i];
                double[] a = u.Vertices[t[0]], b = u.Vertices[t[1]], c = u.Vertices[t[2]];
                double cx = (a[0] + b[0] + c[0]) / 3, cy = (a[1] + b[1] + c[1]) / 3, cz = (a[2] + b[2] + c[2]) / 3;
                _boundarySpheres[i * 4] = cx;
                _boundarySpheres[i * 4 + 1] = cy;
                _boundarySpheres[i * 4 + 2] = cz;
                _boundarySpheres[i * 4 + 3] = Math.Max(Dist(cx, cy, cz, a), Math.Max(Dist(cx, cy, cz, b), Dist(cx, cy, cz, c)));
            }
        }

        private double SphereGap(int i, double[] p)
        {
            int o = i * 4;
            double dx = _boundarySpheres[o] - p[0], dy = _boundarySpheres[o + 1] - p[1], dz = _boundarySpheres[o + 2] - p[2];
            return Math.Sqrt(dx * dx + dy * dy + dz * dz) - _boundarySpheres[o + 3];
        }

        /// <summary>Distance from x to element j's bounding box; zero inside it.</summary>
        private double BoxGap(int j, double[] x)
        {
            double s = 0;
            for (int k = 0; k < 3; k++)
            {
                double d = x[k] < _lo[j][k] ? _lo[j][k] - x[k] : x[k] > _hi[j][k] ? x[k] - _hi[j][k] : 0;
                s += d * d;
            }
            return Math.Sqrt(s);
        }

        /// <summary>Distance from x to element j's boundary, skipping triangles that cannot be nearer.</summary>
        private double PartDistance(int j, double[] x)
        {
            double best = double.MaxValue;
            double[] buf = _tri[j];
            var a = new double[3];
            var b = new double[3];
            var c = new double[3];
            for (int k = 0; k < _triCount[j]; k++)
            {
                int o = k * Stride;
                double dx = buf[o + 12] - x[0], dy = buf[o + 13] - x[1], dz = buf[o + 14] - x[2];
                if (Math.Sqrt(dx * dx + dy * dy + dz * dz) - buf[o + 15] >= best) continue;
                a[0] = buf[o]; a[1] = buf[o + 1]; a[2] = buf[o + 2];
                b[0] = a[0] + buf[o + 3]; b[1] = a[1] + buf[o + 4]; b[2] = a[2] + buf[o + 5];
                c[0] = a[0] + buf[o + 6]; c[1] = a[1] + buf[o + 7]; c[2] = a[2] + buf[o + 8];
                double d = SolidContainment.PointTriangleDistance(x, a, b, c);
                if (d < best) best = d;
            }
            return best;
        }

        /// <summary>
        /// Element j at x: on its face (-1), or its winding number rounded (0 outside, 1 or more
        /// inside); dev is how far that number was from whole. Outside the box: 0 with no work.
        /// </summary>
        private int PartState(int j, double[] x, out double distance, out double dev)
        {
            dev = 0;
            distance = double.NaN;
            double gap = BoxGap(j, x);
            if (gap > OnFaceMm) { distance = gap; return 0; }
            distance = PartDistance(j, x);
            if (!(distance > OnFaceMm)) return -1;
            double w = Math.Abs(SolidContainment.WindingNumber(Parts[j], x));
            if (!SolidContainment.IsFinite(w)) { dev = double.NaN; return 0; }
            double nearest = Math.Round(w);
            dev = Math.Abs(w - nearest);
            return (int)nearest;
        }

        private const int Stride = 16;
        /// <summary>The largest bounding radius of a piece of triangle the rays are tested against.</summary>
        public const double RayPieceMm = 25;

        private double[][] _ray;
        private int[] _rayCount;

        private static void Split(double[] a, double[] b, double[] c, double nx, double ny, double nz,
                                  List<double> outp, int depth)
        {
            double cx = (a[0] + b[0] + c[0]) / 3, cy = (a[1] + b[1] + c[1]) / 3, cz = (a[2] + b[2] + c[2]) / 3;
            double r = Math.Max(Dist(cx, cy, cz, a), Math.Max(Dist(cx, cy, cz, b), Dist(cx, cy, cz, c)));
            if (r > RayPieceMm && depth < 24)
            {
                // Halve the longest edge.
                double ab = Len(a, b), bc = Len(b, c), ca = Len(c, a);
                if (ab >= bc && ab >= ca)
                {
                    double[] m = Mid(a, b);
                    Split(a, m, c, nx, ny, nz, outp, depth + 1);
                    Split(m, b, c, nx, ny, nz, outp, depth + 1);
                }
                else if (bc >= ca)
                {
                    double[] m = Mid(b, c);
                    Split(a, b, m, nx, ny, nz, outp, depth + 1);
                    Split(a, m, c, nx, ny, nz, outp, depth + 1);
                }
                else
                {
                    double[] m = Mid(c, a);
                    Split(a, b, m, nx, ny, nz, outp, depth + 1);
                    Split(m, b, c, nx, ny, nz, outp, depth + 1);
                }
                return;
            }
            outp.Add(a[0]); outp.Add(a[1]); outp.Add(a[2]);
            outp.Add(b[0] - a[0]); outp.Add(b[1] - a[1]); outp.Add(b[2] - a[2]);
            outp.Add(c[0] - a[0]); outp.Add(c[1] - a[1]); outp.Add(c[2] - a[2]);
            outp.Add(nx); outp.Add(ny); outp.Add(nz);
            outp.Add(cx); outp.Add(cy); outp.Add(cz); outp.Add(r);
        }

        private static double[] Mid(double[] a, double[] b) =>
            new[] { (a[0] + b[0]) / 2, (a[1] + b[1]) / 2, (a[2] + b[2]) / 2 };

        private static double Dist(double x, double y, double z, double[] p)
        {
            double dx = p[0] - x, dy = p[1] - y, dz = p[2] - z;
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        // ------------------------------------------------------------ the ray set

        /// <summary>The directions: an icosphere subdivided four times (2562), turned off the axes.</summary>
        public static readonly double[][] Directions;
        /// <summary>The largest angle from any direction in space to the nearest ray, in radians.</summary>
        public static readonly double CoveringAngle;
        public static readonly double CosCovering;

        static UnionJoints()
        {
            List<double[]> v;
            List<int[]> f;
            Icosphere(4, out v, out f);
            // A fixed, irrational-looking rotation, so no ray runs exactly along a box's edge or face.
            double[][] rot = Rotation(0.3141592, 0.2718281, 0.1414213);
            Directions = new double[v.Count][];
            for (int i = 0; i < v.Count; i++) Directions[i] = Mul(rot, v[i]);
            double worst = 0;
            foreach (int[] t in f)
            {
                double[] a = v[t[0]], b = v[t[1]], c = v[t[2]];
                // The spherical circumcentre: the unit normal of the plane through the three.
                double[] n = Normalize(Cross(Sub(b, a), Sub(c, a)));
                if (Dot(n, a) < 0) n = new[] { -n[0], -n[1], -n[2] };
                double ang = Math.Acos(Math.Max(-1, Math.Min(1, Dot(n, a))));
                if (ang > worst) worst = ang;
            }
            CoveringAngle = worst;
            CosCovering = Math.Cos(worst);

            // THE RAYS IN GROUPS, for speed only: the first 162 directions are the icosphere
            // subdivided twice, and every ray belongs to the nearest of them. A triangle seen
            // from the sample inside a cone is tested against the rays of the groups that cone
            // reaches - not against all of them.
            const int parents = 162;
            var groups = new List<int>[parents];
            for (int g = 0; g < parents; g++) groups[g] = new List<int>();
            double reach = 0;
            for (int i = 0; i < Directions.Length; i++)
            {
                int bestG = 0;
                double bestDot = -2;
                for (int g = 0; g < parents; g++)
                {
                    double dd = Dot(Directions[i], Directions[g]);
                    if (dd > bestDot) { bestDot = dd; bestG = g; }
                }
                groups[bestG].Add(i);
                double ang = Math.Acos(Math.Max(-1, Math.Min(1, bestDot)));
                if (ang > reach) reach = ang;
            }
            Groups = new int[parents][];
            for (int g = 0; g < parents; g++) Groups[g] = groups[g].ToArray();
            GroupReach = reach;
        }

        private static readonly int[][] Groups;
        private static readonly double GroupReach;

        private static void Icosphere(int levels, out List<double[]> v, out List<int[]> f)
        {
            double p = (1 + Math.Sqrt(5)) / 2;
            v = new List<double[]>();
            foreach (double[] q in new[]
                     {
                         new[] { -1, p, 0 }, new[] { 1, p, 0 }, new[] { -1, -p, 0 }, new[] { 1, -p, 0 },
                         new[] { 0, -1, p }, new[] { 0, 1, p }, new[] { 0, -1, -p }, new[] { 0, 1, -p },
                         new[] { p, 0, -1 }, new[] { p, 0, 1 }, new[] { -p, 0, -1 }, new[] { -p, 0, 1 }
                     })
                v.Add(Normalize(q));
            f = new List<int[]>
            {
                new[] { 0, 11, 5 }, new[] { 0, 5, 1 }, new[] { 0, 1, 7 }, new[] { 0, 7, 10 }, new[] { 0, 10, 11 },
                new[] { 1, 5, 9 }, new[] { 5, 11, 4 }, new[] { 11, 10, 2 }, new[] { 10, 7, 6 }, new[] { 7, 1, 8 },
                new[] { 3, 9, 4 }, new[] { 3, 4, 2 }, new[] { 3, 2, 6 }, new[] { 3, 6, 8 }, new[] { 3, 8, 9 },
                new[] { 4, 9, 5 }, new[] { 2, 4, 11 }, new[] { 6, 2, 10 }, new[] { 8, 6, 7 }, new[] { 9, 8, 1 }
            };
            for (int l = 0; l < levels; l++)
            {
                var mid = new Dictionary<long, int>();
                var next = new List<int[]>();
                List<double[]> verts = v;
                Func<int, int, int> m = (a, b) =>
                {
                    long key = a < b ? (long)a * 100000 + b : (long)b * 100000 + a;
                    int at;
                    if (mid.TryGetValue(key, out at)) return at;
                    verts.Add(Normalize(new[] { verts[a][0] + verts[b][0], verts[a][1] + verts[b][1], verts[a][2] + verts[b][2] }));
                    mid[key] = verts.Count - 1;
                    return verts.Count - 1;
                };
                foreach (int[] t in f)
                {
                    int ab = m(t[0], t[1]), bc = m(t[1], t[2]), ca = m(t[2], t[0]);
                    next.Add(new[] { t[0], ab, ca });
                    next.Add(new[] { t[1], bc, ab });
                    next.Add(new[] { t[2], ca, bc });
                    next.Add(new[] { ab, bc, ca });
                }
                f = next;
            }
        }

        private static double[][] Rotation(double a, double b, double c)
        {
            double ca = Math.Cos(a), sa = Math.Sin(a), cb = Math.Cos(b), sb = Math.Sin(b), cc = Math.Cos(c), sc = Math.Sin(c);
            double[][] rz = { new[] { ca, -sa, 0 }, new[] { sa, ca, 0 }, new double[] { 0, 0, 1 } };
            double[][] ry = { new[] { cb, 0, sb }, new double[] { 0, 1, 0 }, new[] { -sb, 0, cb } };
            double[][] rx = { new double[] { 1, 0, 0 }, new[] { 0, cc, -sc }, new[] { 0, sc, cc } };
            return MatMul(rz, MatMul(ry, rx));
        }

        private static double[][] MatMul(double[][] x, double[][] y)
        {
            var r = new double[3][];
            for (int i = 0; i < 3; i++)
            {
                r[i] = new double[3];
                for (int k = 0; k < 3; k++) r[i][k] = x[i][0] * y[0][k] + x[i][1] * y[1][k] + x[i][2] * y[2][k];
            }
            return r;
        }

        private static double[] Mul(double[][] m, double[] v) =>
            new[] { Dot(m[0], v), Dot(m[1], v), Dot(m[2], v) };

        private static double[] Sub(double[] a, double[] b) => new[] { a[0] - b[0], a[1] - b[1], a[2] - b[2] };
        private static double Dot(double[] a, double[] b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];
        private static double[] Cross(double[] a, double[] b) =>
            new[] { a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0] };
        private static double[] Normalize(double[] a)
        {
            double l = Math.Sqrt(Dot(a, a));
            return new[] { a[0] / l, a[1] / l, a[2] / l };
        }

        // ------------------------------------------------------- signed distance

        /// <summary>
        /// Positive inside the union, negative outside, NaN when an element will not say. capMm:
        /// clearances beyond it are not looked for, and come back as at least cos(theta) x cap.
        /// </summary>
        public double SignedDistance(HostMesh union, double[] p, double capMm, JointStats stats, out double windingDeviation)
        {
            return SignedDistance(union, p, capMm, stats, false, out windingDeviation);
        }

        /// <summary>How far a sample lying ON an element's face is moved into it before it is measured.</summary>
        public const double NudgeMm = 1e-3;

        private double SignedDistance(HostMesh union, double[] p, double capMm, JointStats stats, bool nudged,
                                      out double windingDeviation)
        {
            windingDeviation = double.NaN;
            int m = union.Triangles.Count;
            if (_boundarySpheres == null || _boundarySpheres.Length != m * 4) Prepare(union);

            // The nearest point of the candidate boundary, far triangles skipped by their spheres.
            int nearestTri = -1;
            double ds = double.MaxValue;
            double[] q0 = null;
            for (int i = 0; i < m; i++)
            {
                if (SphereGap(i, p) >= ds) continue;
                int[] t = union.Triangles[i];
                double[] cp = ClosestPoint(p, union.Vertices[t[0]], union.Vertices[t[1]], union.Vertices[t[2]]);
                if (cp == null) continue;
                double d = Len(p, cp);
                if (d < ds) { ds = d; nearestTri = i; q0 = cp; }
            }
            if (nearestTri < 0 || !SolidContainment.IsFinite(ds)) return double.NaN;

            int n = Parts.Length;
            var state = new int[n];
            var onFace = new bool[n];
            bool anyInside = false, anyOnFace = false;
            double depth = 0, worstDev = 0;
            for (int j = 0; j < n; j++)
            {
                double dj, dev;
                int s = PartState(j, p, out dj, out dev);
                if (!SolidContainment.IsFinite(dj) || double.IsNaN(dev)) return double.NaN;
                if (s < 0) { onFace[j] = true; anyOnFace = true; continue; }
                if (dev > worstDev) worstDev = dev;
                if (dev > SolidContainment.WindingConfidenceLimit) { windingDeviation = dev; return double.NaN; }
                state[j] = s;
                if (s >= 1) { anyInside = true; if (dj > depth) depth = dj; }
            }
            windingDeviation = worstDev;

            // ON AN ELEMENT'S FACE - a dowel sampled exactly where it crosses the footing's top -
            // the sample is moved 0.001 mm into that element and measured there. Clearance changes
            // by no more than the move, so the answer is taken that much smaller: never more than
            // the truth. Otherwise every ray would need the element's winding number to start.
            if (anyOnFace && !nudged)
            {
                for (int j = 0; j < n; j++)
                {
                    if (!onFace[j]) continue;
                    double[] nrm = NearestNormal(j, p);
                    if (nrm == null) break;
                    double[] inner = { p[0] - nrm[0] * NudgeMm, p[1] - nrm[1] * NudgeMm, p[2] - nrm[2] * NudgeMm };
                    double dev2;
                    double sd = SignedDistance(union, inner, capMm, stats, true, out dev2);
                    if (!SolidContainment.IsFinite(sd)) break;
                    windingDeviation = Math.Max(worstDev, SolidContainment.IsFinite(dev2) ? dev2 : 0);
                    return sd >= 0 ? Math.Max(0, sd - NudgeMm) : sd - NudgeMm;
                }
            }

            bool seam = !anyInside && !anyOnFace && IsSeam(p);
            if (!anyInside && !anyOnFace && !seam) return -ds;

            // 1. The nearest face, if air is just beyond it: exact.
            if (anyInside && !anyOnFace && Certified(union, nearestTri, q0, p))
            {
                if (stats != null) stats.ExactSamples++;
                return ds;
            }

            double cap = SolidContainment.IsFinite(capMm) && capMm > 0 ? capMm : double.PositiveInfinity;

            // 2. The nearest face point that IS beside air. Where the nearest air is an edge two
            //    elements make - a footing's top meeting a beam's side - a face's own nearest point
            //    is there, and this finds it exactly. Faces buried in another element are passed over.
            var order = new List<KeyValuePair<double, int>>();
            var near = new Dictionary<int, double[]>();
            for (int i = 0; i < m; i++)
            {
                if (SphereGap(i, p) >= cap) continue;
                int[] t = union.Triangles[i];
                double[] cp = ClosestPoint(p, union.Vertices[t[0]], union.Vertices[t[1]], union.Vertices[t[2]]);
                if (cp == null) continue;
                double d = Len(p, cp);
                if (d >= cap) continue;
                order.Add(new KeyValuePair<double, int>(d, i));
                near[i] = cp;
            }
            order.Sort((a, b) => a.Key.CompareTo(b.Key));
            double byFace = double.PositiveInfinity;
            foreach (KeyValuePair<double, int> c in order)
                if (Certified(union, c.Value, near[c.Value], p)) { byFace = c.Key; break; }

            // 3. Rays, for air that is nearer than any face's own nearest point: the inside of a
            //    face that is partly buried. Followed only as far as could still matter.
            double rayLimit = Math.Min(cap, byFace / CosCovering);
            bool reached;
            double exit = RayClearance(p, state, onFace, rayLimit, out reached);
            bool capped = false;
            double byRays;
            if (!reached) byRays = CosCovering * exit;
            else if (double.IsInfinity(byFace) && !double.IsInfinity(cap)) { byRays = CosCovering * cap; capped = true; exit = cap; }
            else byRays = double.PositiveInfinity;

            double found = Math.Min(byFace, byRays);
            if (stats != null)
            {
                if (byFace <= byRays) stats.ExactSamples++;
                else
                {
                    stats.RaySamples++;
                    if (capped) stats.Capped = true;
                    else if (exit > stats.LargestRayClearanceMm) stats.LargestRayClearanceMm = exit;
                }
            }
            // 4. Never below what is certain: the depth inside any one element that holds it.
            double lower = anyInside ? Math.Max(ds, depth) : 0;
            return Math.Max(lower, found);
        }

        private static double Len(double[] a, double[] b)
        {
            double dx = a[0] - b[0], dy = a[1] - b[1], dz = a[2] - b[2];
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        /// <summary>
        /// Whether the point just beyond q - 0.001 mm further along the line from p through q, or
        /// along the face normal when p is on it - is AIR: outside every element and not a seam.
        /// Then |pq| is an upper bound on the clearance, reached.
        /// </summary>
        private bool Certified(HostMesh u, int tri, double[] q, double[] p)
        {
            double[] dir = Sub(q, p);
            double l = Math.Sqrt(Dot(dir, dir));
            if (l <= 1e-9)
            {
                int[] t = u.Triangles[tri];
                double[] a = u.Vertices[t[0]], b = u.Vertices[t[1]], c = u.Vertices[t[2]];
                dir = Cross(Sub(b, a), Sub(c, a));
                if (!Outward[TrianglePart[tri]]) dir = new[] { -dir[0], -dir[1], -dir[2] };
                l = Math.Sqrt(Dot(dir, dir));
                if (l <= 1e-12) return false;
            }
            double s = CertificateOffsetMm / l;
            double[] x = { q[0] + dir[0] * s, q[1] + dir[1] * s, q[2] + dir[2] * s };
            for (int j = 0; j < Parts.Length; j++)
            {
                double dj, dev;
                int st = PartState(j, x, out dj, out dev);
                if (st != 0 || double.IsNaN(dev) || dev > SolidContainment.WindingConfidenceLimit) return false;
            }
            return !IsSeam(x);
        }

        /// <summary>
        /// A point outside every element that lies in a SEAM: within the seam width of two
        /// different elements, and BETWEEN them - their nearest points more than 120 degrees
        /// apart as seen from it. Just outside an exterior face where two elements meet flush,
        /// both nearest points are on the same side, and that is air.
        /// </summary>
        private bool IsSeam(double[] x)
        {
            var dirs = new List<double[]>();
            var owner = new List<int>();
            for (int j = 0; j < Parts.Length; j++)
            {
                if (BoxGap(j, x) > SeamMm) continue;
                double[] q;
                double dj = Nearest(j, x, out q);
                if (!(dj <= SeamMm) || dj <= 1e-9) continue;
                double[] u = { (q[0] - x[0]) / dj, (q[1] - x[1]) / dj, (q[2] - x[2]) / dj };
                for (int k = 0; k < dirs.Count; k++)
                    if (owner[k] != j && Dot(dirs[k], u) < -0.5) return true;
                dirs.Add(u);
                owner.Add(j);
            }
            return false;
        }

        /// <summary>The outward unit normal of the face of element j nearest to x.</summary>
        private double[] NearestNormal(int j, double[] x)
        {
            double best = double.MaxValue;
            int at = -1;
            double[] buf = _tri[j];
            var a = new double[3];
            var b = new double[3];
            var c = new double[3];
            for (int k = 0; k < _triCount[j]; k++)
            {
                int o = k * Stride;
                double dx = buf[o + 12] - x[0], dy = buf[o + 13] - x[1], dz = buf[o + 14] - x[2];
                if (Math.Sqrt(dx * dx + dy * dy + dz * dz) - buf[o + 15] >= best) continue;
                a[0] = buf[o]; a[1] = buf[o + 1]; a[2] = buf[o + 2];
                b[0] = a[0] + buf[o + 3]; b[1] = a[1] + buf[o + 4]; b[2] = a[2] + buf[o + 5];
                c[0] = a[0] + buf[o + 6]; c[1] = a[1] + buf[o + 7]; c[2] = a[2] + buf[o + 8];
                double d = SolidContainment.PointTriangleDistance(x, a, b, c);
                if (d < best) { best = d; at = o; }
            }
            return at < 0 ? null : new[] { buf[at + 9], buf[at + 10], buf[at + 11] };
        }

        /// <summary>The nearest point of element j's boundary to x, far triangles skipped.</summary>
        private double Nearest(int j, double[] x, out double[] q)
        {
            q = null;
            double best = double.MaxValue;
            double[] buf = _tri[j];
            for (int k = 0; k < _triCount[j]; k++)
            {
                int o = k * Stride;
                double dx = buf[o + 12] - x[0], dy = buf[o + 13] - x[1], dz = buf[o + 14] - x[2];
                if (Math.Sqrt(dx * dx + dy * dy + dz * dz) - buf[o + 15] >= best) continue;
                double[] a = { buf[o], buf[o + 1], buf[o + 2] };
                double[] b = { a[0] + buf[o + 3], a[1] + buf[o + 4], a[2] + buf[o + 5] };
                double[] c = { a[0] + buf[o + 6], a[1] + buf[o + 7], a[2] + buf[o + 8] };
                double[] cp = ClosestPoint(x, a, b, c);
                if (cp == null) continue;
                double d = Len(x, cp);
                if (d < best) { best = d; q = cp; }
            }
            return best;
        }

        private bool InsideAny(double[] x, out bool unsure)
        {
            unsure = false;
            for (int j = 0; j < Parts.Length; j++)
            {
                double dj, dev;
                int st = PartState(j, x, out dj, out dev);
                if (st < 0) return true;
                if (double.IsNaN(dev) || dev > SolidContainment.WindingConfidenceLimit) { unsure = true; continue; }
                if (st >= 1) return true;
            }
            return false;
        }

        // ------------------------------------------------------------------ rays

        private struct Hit
        {
            public double T;
            public int Part;
            public int Sign;   // +1 entering, -1 leaving
        }

        private double RayClearance(double[] p, int[] state, bool[] onFace, double capMm, out bool capped)
        {
            double cap = SolidContainment.IsFinite(capMm) && capMm > 0 ? capMm : double.PositiveInfinity;
            int n = Parts.Length;

            // Every piece that can be met within the limit goes to the rays that can meet it: the
            // rays inside the cone the piece's bounding sphere fills, seen from the sample.
            // Reused from one sample to the next, per thread.
            List<int>[] perRay = _perRay ?? (_perRay = new List<int>[Directions.Length]);
            foreach (List<int> l in perRay) if (l != null) l.Clear();
            double[] u = new double[3];
            for (int j = 0; j < n; j++)
            {
                double[] buf = _ray[j];
                for (int k = 0; k < _rayCount[j]; k++)
                {
                    int o = k * Stride;
                    double vx = buf[o + 12] - p[0], vy = buf[o + 13] - p[1], vz = buf[o + 14] - p[2];
                    double dist = Math.Sqrt(vx * vx + vy * vy + vz * vz), r = buf[o + 15];
                    if (dist - r > cap) continue;
                    // A piece whose plane passes through the sample can only be met where the ray
                    // starts - and a crossing there is not counted - or by a ray lying in it.
                    double h = (buf[o] - p[0]) * buf[o + 9] + (buf[o + 1] - p[1]) * buf[o + 10] + (buf[o + 2] - p[2]) * buf[o + 11];
                    if (Math.Abs(h) <= OnFaceMm) continue;
                    int code = k * 64 + j;
                    if (dist <= r * 1.0000001 + 1e-9)
                    {
                        for (int i = 0; i < perRay.Length; i++) Add(perRay, i, code);
                        continue;
                    }
                    u[0] = vx / dist; u[1] = vy / dist; u[2] = vz / dist;
                    double half = Math.Asin(Math.Min(1, r / dist));
                    double cosRay = Math.Cos(Math.Min(Math.PI, half + 1e-9));
                    double cosGroup = Math.Cos(Math.Min(Math.PI, half + GroupReach + 1e-9));
                    for (int g = 0; g < Groups.Length; g++)
                    {
                        if (Dot(Directions[g], u) < cosGroup) continue;
                        foreach (int i in Groups[g])
                            if (Dot(Directions[i], u) >= cosRay) Add(perRay, i, code);
                    }
                }
            }

            double best = cap;
            var hits = new List<Hit>();
            for (int i = 0; i < Directions.Length; i++)
            {
                if (perRay[i] == null || perRay[i].Count == 0) continue;   // nothing to cross within the limit
                double t = RayExit(p, Directions[i], state, onFace, perRay[i], best, hits);
                if (t < best) best = t;
                if (best <= 0) break;
            }
            capped = double.IsInfinity(cap) ? false : best >= cap;
            return best;
        }

        [ThreadStatic] private static List<int>[] _perRay;

        private static void Add(List<int>[] lists, int i, int code)
        {
            if (lists[i] == null) lists[i] = new List<int>();
            lists[i].Add(code);
        }

        /// <summary>
        /// The first parameter at which the ray from p along d leaves the union for good -
        /// into air, not a seam. Returns limit when it does not within limit.
        /// </summary>
        private double RayExit(double[] p, double[] d, int[] state, bool[] onFace, List<int> cand,
                               double limit, List<Hit> hits)
        {
            int n = Parts.Length;
            const double Tau = 1e-3;
            hits.Clear();
            {
                foreach (int code in cand)
                {
                    int j = code % 64, k = code / 64;
                    double tmin = onFace[j] ? Tau : 1e-9;
                    double[] buf = _ray[j];
                    int o = k * Stride;
                    double cx = buf[o + 12] - p[0], cy = buf[o + 13] - p[1], cz = buf[o + 14] - p[2];
                    if (Math.Sqrt(cx * cx + cy * cy + cz * cz) - buf[o + 15] > limit) continue;
                    double t;
                    if (!Intersect(p, d, buf, o, out t)) continue;
                    if (t <= tmin || t > limit) continue;
                    double dn = d[0] * buf[o + 9] + d[1] * buf[o + 10] + d[2] * buf[o + 11];
                    if (Math.Abs(dn) < 1e-12) continue;
                    hits.Add(new Hit { T = t, Part = j, Sign = dn < 0 ? 1 : -1 });
                }
            }
            hits.Sort((x, y) => x.T.CompareTo(y.T));

            // A ray through an edge or a vertex meets every triangle there at the same parameter:
            // one crossing, counted once per element and direction.
            var unique = new List<Hit>(hits.Count);
            foreach (Hit h in hits)
            {
                bool dup = false;
                for (int k = unique.Count - 1; k >= 0 && h.T - unique[k].T <= 1e-7 * (1 + h.T); k--)
                    if (unique[k].Part == h.Part && unique[k].Sign == h.Sign) { dup = true; break; }
                if (!dup) unique.Add(h);
            }

            var count = new int[n];
            for (int j = 0; j < n; j++)
            {
                if (!onFace[j]) { count[j] = state[j]; continue; }
                double[] x = { p[0] + d[0] * Tau, p[1] + d[1] * Tau, p[2] + d[2] * Tau };
                double w = Math.Abs(SolidContainment.WindingNumber(Parts[j], x));
                double nearest = Math.Round(w);
                if (!SolidContainment.IsFinite(w) || Math.Abs(w - nearest) > SolidContainment.WindingConfidenceLimit)
                    return ExitByWinding(p, d, unique, limit);
                count[j] = (int)nearest;
            }

            double airFrom = Inside(count) ? double.NaN : 0;
            int i = 0;
            while (true)
            {
                if (!double.IsNaN(airFrom))
                {
                    // In air from airFrom. Where does the ray come back into concrete?
                    int k = i;
                    var probe = (int[])count.Clone();
                    double back = double.NaN;
                    while (k < unique.Count)
                    {
                        double tg = unique[k].T;
                        while (k < unique.Count && unique[k].T - tg <= 1e-7 * (1 + tg)) { probe[unique[k].Part] += unique[k].Sign; k++; }
                        if (Inside(probe)) { back = tg; break; }
                    }
                    if (double.IsNaN(back))
                        return SeamRunsTo(p, d, airFrom, limit);
                    double mid = (airFrom + back) / 2;
                    if (!IsSeam(new[] { p[0] + d[0] * mid, p[1] + d[1] * mid, p[2] + d[2] * mid }))
                        return airFrom;
                    // A seam: concrete on both sides of a gap thinner than the seam width.
                    for (; i < k; i++) count[unique[i].Part] += unique[i].Sign;
                    airFrom = double.NaN;
                    continue;
                }
                if (i >= unique.Count) return limit;
                double tgroup = unique[i].T;
                while (i < unique.Count && unique[i].T - tgroup <= 1e-7 * (1 + tgroup))
                {
                    count[unique[i].Part] += unique[i].Sign;
                    i++;
                }
                for (int j = 0; j < n; j++)
                    if (count[j] < 0) return ExitByWinding(p, d, unique, limit);
                if (!Inside(count)) airFrom = tgroup;
            }
        }

        private static bool Inside(int[] count)
        {
            foreach (int c in count) if (c > 0) return true;
            return false;
        }

        /// <summary>
        /// An air run with no way back within the limit. It is air from where it starts unless
        /// it starts in a seam; then it is air from where the seam ends.
        /// </summary>
        private double SeamRunsTo(double[] p, double[] d, double from, double limit)
        {
            double step = Math.Max(SeamMm / 2, 0.01);
            double t = from + step;
            for (int k = 0; k < 4000 && t < limit; k++, t += step)
                if (!IsSeam(new[] { p[0] + d[0] * t, p[1] + d[1] * t, p[2] + d[2] * t }))
                    return k == 0 ? from : t - step;
            return Math.Min(t, limit);
        }

        /// <summary>
        /// The same ray when crossing counts did not add up (a grazing hit, a vertex): every gap
        /// between crossings is classified by the winding numbers at its middle instead.
        /// </summary>
        private double ExitByWinding(double[] p, double[] d, List<Hit> hits, double limit)
        {
            var ts = new List<double> { 0 };
            foreach (Hit h in hits) if (h.T - ts[ts.Count - 1] > 1e-7 * (1 + h.T)) ts.Add(h.T);
            if (!double.IsInfinity(limit)) ts.Add(limit);
            else ts.Add(ts[ts.Count - 1] + 10 * Math.Max(SeamMm, 1));
            for (int k = 0; k + 1 < ts.Count; k++)
            {
                double mid = (ts[k] + ts[k + 1]) / 2;
                double[] x = { p[0] + d[0] * mid, p[1] + d[1] * mid, p[2] + d[2] * mid };
                bool unsure;
                if (InsideAny(x, out unsure) || unsure) continue;
                if (IsSeam(x)) continue;
                return ts[k];
            }
            return limit;
        }

        /// <summary>Moller-Trumbore, edges inclusive. False when parallel or behind.</summary>
        private static bool Intersect(double[] p, double[] d, double[] b, int o, out double t)
        {
            t = 0;
            double e1x = b[o + 3], e1y = b[o + 4], e1z = b[o + 5], e2x = b[o + 6], e2y = b[o + 7], e2z = b[o + 8];
            double px = d[1] * e2z - d[2] * e2y, py = d[2] * e2x - d[0] * e2z, pz = d[0] * e2y - d[1] * e2x;
            double det = e1x * px + e1y * py + e1z * pz;
            if (Math.Abs(det) < 1e-14) return false;
            double inv = 1 / det;
            double sx = p[0] - b[o], sy = p[1] - b[o + 1], sz = p[2] - b[o + 2];
            double u = (sx * px + sy * py + sz * pz) * inv;
            const double E = 1e-9;
            if (u < -E || u > 1 + E) return false;
            double qx = sy * e1z - sz * e1y, qy = sz * e1x - sx * e1z, qz = sx * e1y - sy * e1x;
            double v = (d[0] * qx + d[1] * qy + d[2] * qz) * inv;
            if (v < -E || u + v > 1 + E) return false;
            t = (e2x * qx + e2y * qy + e2z * qz) * inv;
            return t > 0;
        }

        /// <summary>The closest point of a triangle to p, by region. Null for a degenerate triangle.</summary>
        internal static double[] ClosestPoint(double[] p, double[] a, double[] b, double[] c)
        {
            double abx = b[0] - a[0], aby = b[1] - a[1], abz = b[2] - a[2];
            double acx = c[0] - a[0], acy = c[1] - a[1], acz = c[2] - a[2];
            double nx = aby * acz - abz * acy, ny = abz * acx - abx * acz, nz = abx * acy - aby * acx;
            if (Math.Sqrt(nx * nx + ny * ny + nz * nz) <= 1e-12) return null;
            double apx = p[0] - a[0], apy = p[1] - a[1], apz = p[2] - a[2];
            double d1 = abx * apx + aby * apy + abz * apz, d2 = acx * apx + acy * apy + acz * apz;
            if (d1 <= 0 && d2 <= 0) return new[] { a[0], a[1], a[2] };
            double bpx = p[0] - b[0], bpy = p[1] - b[1], bpz = p[2] - b[2];
            double d3 = abx * bpx + aby * bpy + abz * bpz, d4 = acx * bpx + acy * bpy + acz * bpz;
            if (d3 >= 0 && d4 <= d3) return new[] { b[0], b[1], b[2] };
            double vc = d1 * d4 - d3 * d2;
            if (vc <= 0 && d1 >= 0 && d3 <= 0)
            {
                double v = d1 / (d1 - d3);
                return new[] { a[0] + abx * v, a[1] + aby * v, a[2] + abz * v };
            }
            double cpx = p[0] - c[0], cpy = p[1] - c[1], cpz = p[2] - c[2];
            double d5 = abx * cpx + aby * cpy + abz * cpz, d6 = acx * cpx + acy * cpy + acz * cpz;
            if (d6 >= 0 && d5 <= d6) return new[] { c[0], c[1], c[2] };
            double vb = d5 * d2 - d1 * d6;
            if (vb <= 0 && d2 >= 0 && d6 <= 0)
            {
                double w = d2 / (d2 - d6);
                return new[] { a[0] + acx * w, a[1] + acy * w, a[2] + acz * w };
            }
            double va = d3 * d6 - d5 * d4;
            if (va <= 0 && (d4 - d3) >= 0 && (d5 - d6) >= 0)
            {
                double w = (d4 - d3) / ((d4 - d3) + (d5 - d6));
                return new[] { b[0] + (c[0] - b[0]) * w, b[1] + (c[1] - b[1]) * w, b[2] + (c[2] - b[2]) * w };
            }
            double denom = va + vb + vc;
            double vv = vb / denom, ww = vc / denom;
            return new[] { a[0] + abx * vv + acx * ww, a[1] + aby * vv + acy * ww, a[2] + abz * vv + acz * ww };
        }

        // ------------------------------------------------------- anchored length

        /// <summary>
        /// The length of a polyline inside ONE closed element of any shape: each segment cut at
        /// every crossing of the element's triangles, each piece judged by the winding number at
        /// its middle. A piece lying in a face counts as inside, as the convex clip counts it.
        /// NaN when the element will not say.
        /// </summary>
        public static double LengthInsideAnyShape(HostMesh m, IList<double[]> polyline)
        {
            if (m == null || polyline == null) return double.NaN;
            if (!SolidContainment.Diagnose(m).Usable) return double.NaN;
            double total = 0;
            for (int s = 1; s < polyline.Count; s++)
            {
                double[] a = polyline[s - 1], b = polyline[s];
                double[] d = { b[0] - a[0], b[1] - a[1], b[2] - a[2] };
                double len = Math.Sqrt(Dot(d, d));
                if (len <= 1e-12) continue;
                var ts = new List<double> { 0, 1 };
                foreach (int[] t in m.Triangles)
                {
                    double[] v0 = m.Vertices[t[0]], v1 = m.Vertices[t[1]], v2 = m.Vertices[t[2]];
                    double[] buf =
                    {
                        v0[0], v0[1], v0[2], v1[0] - v0[0], v1[1] - v0[1], v1[2] - v0[2], v2[0] - v0[0], v2[1] - v0[1], v2[2] - v0[2]
                    };
                    double tt;
                    if (Intersect(a, d, buf, 0, out tt) && tt > 0 && tt < 1) ts.Add(tt);
                }
                ts.Sort();
                for (int k = 0; k + 1 < ts.Count; k++)
                {
                    double t0 = ts[k], t1 = ts[k + 1];
                    if ((t1 - t0) * len <= 1e-9) continue;
                    double tm = (t0 + t1) / 2;
                    double[] x = { a[0] + d[0] * tm, a[1] + d[1] * tm, a[2] + d[2] * tm };
                    bool inside;
                    if (SolidContainment.DistanceToBoundary(m, x) <= OnFaceMm) inside = true;
                    else
                    {
                        double w = Math.Abs(SolidContainment.WindingNumber(m, x));
                        if (!SolidContainment.IsFinite(w)) return double.NaN;
                        double nearest = Math.Round(w);
                        if (Math.Abs(w - nearest) > SolidContainment.WindingConfidenceLimit) return double.NaN;
                        inside = nearest >= 1;
                    }
                    if (inside) total += (t1 - t0) * len;
                }
            }
            return total;
        }
    }
}
