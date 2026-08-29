// -----------------------------------------------------------------------------
// Horizun Revit MCP - is the bar actually INSIDE the concrete.
// Original Horizun code. No Revit types.
//
// The check this replaces projected the bar and the host onto ONE axis and
// compared intervals. That proves a set is not too long for its host. It does
// not prove the bar is inside the concrete, and for a host at an angle it does
// not even prove the first thing, because the interval came from Revit's
// axis-aligned bounding box - which for a beam rotated 30 degrees is larger
// than the beam in every direction.
//
// So this file works against the host's actual boundary, as a triangle mesh,
// and answers a question with five possible answers rather than two:
//
//   inside                 every sampled point of the bar SURFACE is in concrete
//   inside_cover_violated  in concrete, but closer to a face than the declared cover
//   partially_outside      some of the bar is in the air
//   completely_outside     none of the centreline is in concrete
//   not_evaluable          the boundary could not be trusted - NOT a pass
//
// Inside-ness is decided by the WINDING NUMBER rather than by casting a ray.
// A ray that grazes an edge is counted once or twice depending on rounding, and
// the answer flips with no warning. The winding number of a closed mesh is 1
// inside and 0 outside, and anything in between is the mesh telling you it is
// not closed - which is the case this has to detect rather than average away,
// because an open shell silently reads as "outside" under ray parity.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;

namespace Horizun.Revit.Core
{
    /// <summary>A triangulated boundary, in millimetres, in model coordinates.</summary>
    public sealed class HostMesh
    {
        public List<double[]> Vertices = new List<double[]>();
        public List<int[]> Triangles = new List<int[]>();

        /// <summary>True when at least one face was curved and had to be approximated.</summary>
        public bool AnyCurvedFace;

        /// <summary>The chord tolerance the tessellation was asked for, in millimetres.</summary>
        public double ChordToleranceMm;

        /// <summary>How this mesh was obtained. Published so a reader can judge it.</summary>
        public string Source;

        public int AddVertex(double x, double y, double z)
        {
            Vertices.Add(new[] { x, y, z });
            return Vertices.Count - 1;
        }

        public void AddTriangle(int a, int b, int c)
        {
            Triangles.Add(new[] { a, b, c });
        }

        /// <summary>An axis-aligned box, as a mesh, with outward-facing triangles.</summary>
        public static HostMesh Box(double[] min, double[] max)
        {
            var m = new HostMesh { Source = "an axis-aligned box built in code" };
            m.AddVertex(min[0], min[1], min[2]);
            m.AddVertex(max[0], min[1], min[2]);
            m.AddVertex(max[0], max[1], min[2]);
            m.AddVertex(min[0], max[1], min[2]);
            m.AddVertex(min[0], min[1], max[2]);
            m.AddVertex(max[0], min[1], max[2]);
            m.AddVertex(max[0], max[1], max[2]);
            m.AddVertex(min[0], max[1], max[2]);
            int[][] quads =
            {
                new[] { 0, 3, 2, 1 },
                new[] { 4, 5, 6, 7 },
                new[] { 0, 1, 5, 4 },
                new[] { 1, 2, 6, 5 },
                new[] { 2, 3, 7, 6 },
                new[] { 3, 0, 4, 7 }
            };
            foreach (int[] q in quads)
            {
                m.AddTriangle(q[0], q[1], q[2]);
                m.AddTriangle(q[0], q[2], q[3]);
            }
            return m;
        }

        /// <summary>The same box, turned about Z by an angle in radians, about the origin.</summary>
        public HostMesh RotatedAboutZ(double radians)
        {
            double c = Math.Cos(radians), s = Math.Sin(radians);
            var m = new HostMesh
            {
                AnyCurvedFace = AnyCurvedFace,
                ChordToleranceMm = ChordToleranceMm,
                Source = Source + ", rotated about Z"
            };
            foreach (double[] v in Vertices)
                m.AddVertex(v[0] * c - v[1] * s, v[0] * s + v[1] * c, v[2]);
            foreach (int[] t in Triangles) m.AddTriangle(t[0], t[1], t[2]);
            return m;
        }
    }

    /// <summary>What a mesh is, before anything is asked of it.</summary>
    public sealed class MeshDiagnosis
    {
        public bool Usable;
        public string Why;
        public int TriangleCount;
        public int DegenerateTriangles;
        public int OpenEdges;
        public double[] MinMm;
        public double[] MaxMm;
    }

    public sealed class ContainmentVerdict
    {
        public string Word = SolidContainment.NotEvaluable;
        public bool Evaluated;
        public string Why;
        public string HowMeasured;

        public int SamplesTested;
        public double SampleStepMm;

        /// <summary>Smallest distance from the CENTRELINE to the boundary. Positive inside.</summary>
        public double MinSignedDistanceMm;
        /// <summary>The same, less the bar radius: the concrete beyond the bar SURFACE.</summary>
        public double MinSurfaceClearanceMm;
        /// <summary>How far the worst part of the bar surface is out in the air. Zero when inside.</summary>
        public double WorstOutsideMm;
        /// <summary>How far short of the declared cover the worst point is. Zero when it is met.</summary>
        public double CoverShortfallMm;

        public double BarRadiusMm;
        public double? RequiredCoverMm;
        public double ToleranceMm;

        public int WorstSampleIndex = -1;
        public double[] WorstPointMm;

        /// <summary>
        /// How far the worst winding number sat from a whole number. Near zero is a
        /// closed mesh answering confidently; near 0.5 is a mesh that is not closed.
        /// </summary>
        public double WorstWindingDeviation;

        public bool CurvedBoundaryApproximated;
        public double ChordToleranceMm;

        /// <summary>
        /// True when the centreline closes on itself - a stirrup. A closed shape has
        /// no flat ends, so it carries its full radius the whole way round.
        /// </summary>
        public bool ClosedLoop;
    }

    public static class SolidContainment
    {
        public const string Inside = "inside";
        public const string InsideCoverViolated = "inside_cover_violated";
        public const string PartiallyOutside = "partially_outside";
        public const string CompletelyOutside = "completely_outside";
        public const string NotEvaluable = "not_evaluable";

        public static readonly string[] AllWords =
        {
            Inside, InsideCoverViolated, PartiallyOutside, CompletelyOutside, NotEvaluable
        };

        /// <summary>Sampling never produces more than this many points, however long the bar.</summary>
        public const int MaxSamples = 4000;

        /// <summary>A winding number further than this from 0 or 1 is not an answer.</summary>
        public const double WindingConfidenceLimit = 0.25;

        /// <summary>Closer to the boundary than this, in millimetres, counts as on it.</summary>
        public const double OnBoundaryToleranceMm = 1e-9;

        private const double FourPi = 4.0 * Math.PI;

        public static bool IsFinite(double v)
        {
            return !double.IsNaN(v) && !double.IsInfinity(v);
        }

        private static bool FinitePoint(double[] p)
        {
            return p != null && p.Length >= 3 && IsFinite(p[0]) && IsFinite(p[1]) && IsFinite(p[2]);
        }

        // ------------------------------------------------------------------ mesh

        /// <summary>
        /// Whether the mesh can be asked anything at all. An open shell is the case
        /// that matters: under ray parity it reads as "everything is outside", which
        /// is a confident wrong answer rather than a refusal.
        /// </summary>
        public static MeshDiagnosis Diagnose(HostMesh mesh)
        {
            var d = new MeshDiagnosis();
            if (mesh == null || mesh.Triangles.Count == 0)
            {
                d.Why = "no host boundary was obtained.";
                return d;
            }

            var edges = new Dictionary<long, int>();
            double[] lo = { double.MaxValue, double.MaxValue, double.MaxValue };
            double[] hi = { double.MinValue, double.MinValue, double.MinValue };
            int n = mesh.Vertices.Count;

            foreach (double[] v in mesh.Vertices)
            {
                if (!FinitePoint(v))
                {
                    d.Why = "the host boundary contained a vertex that was not a finite number.";
                    return d;
                }
                for (int k = 0; k < 3; k++)
                {
                    if (v[k] < lo[k]) lo[k] = v[k];
                    if (v[k] > hi[k]) hi[k] = v[k];
                }
            }

            foreach (int[] t in mesh.Triangles)
            {
                if (t == null || t.Length != 3) { d.Why = "a triangle was malformed."; return d; }
                for (int k = 0; k < 3; k++)
                    if (t[k] < 0 || t[k] >= n)
                    {
                        d.Why = "a triangle referred to a vertex that is not there.";
                        return d;
                    }

                double[] a = mesh.Vertices[t[0]], b = mesh.Vertices[t[1]], c = mesh.Vertices[t[2]];
                if (TwiceArea(a, b, c) <= 1e-12) { d.DegenerateTriangles++; continue; }
                d.TriangleCount++;

                // DIRECTED edges. Counting them without direction says only that
                // each edge is used twice - which a mesh with some faces flipped
                // also satisfies. Measured: with the two triangles of one end face
                // reversed, this reported a closed manifold, and the winding number
                // then read 0.09 inside the beam and 0.91 outside it. A bar entirely
                // in the air came back as sitting in concrete, confidently, because
                // 0.09 is well inside the confidence limit. A consistently oriented
                // closed surface uses every edge once in each direction.
                for (int k = 0; k < 3; k++)
                {
                    int p = t[k], q = t[(k + 1) % 3];
                    long key = (long)p * n + q;    // n, not a constant: 4,000,000 collides above that
                    int had;
                    edges[key] = edges.TryGetValue(key, out had) ? had + 1 : 1;
                }
            }

            foreach (KeyValuePair<long, int> e in edges)
            {
                if (e.Value != 1) { d.OpenEdges++; continue; }
                long p = e.Key / n, q = e.Key % n;
                int back;
                if (!edges.TryGetValue(q * n + p, out back) || back != 1) d.OpenEdges++;
            }

            d.MinMm = lo;
            d.MaxMm = hi;

            if (d.TriangleCount < 4)
            {
                d.Why = "the host boundary had fewer than four usable triangles, which cannot enclose anything.";
                return d;
            }
            if (d.OpenEdges > 0)
            {
                d.Why = "the host boundary is not a consistently oriented closed surface: " + d.OpenEdges +
                        " edge(s) are not used exactly once in each direction. An open shell reads as " +
                        "everything-is-outside rather than as a failure, and a mesh with some faces flipped " +
                        "reads as inside-out with full confidence, so both are refused here.";
                return d;
            }

            d.Usable = true;
            return d;
        }

        /// <summary>The same mesh with its zero-area triangles left out.</summary>
        public static HostMesh WithoutDegenerateTriangles(HostMesh mesh)
        {
            if (mesh == null) return null;
            var clean = new HostMesh
            {
                AnyCurvedFace = mesh.AnyCurvedFace,
                ChordToleranceMm = mesh.ChordToleranceMm,
                Source = mesh.Source + ", without its zero-area triangles"
            };
            foreach (double[] v in mesh.Vertices) clean.AddVertex(v[0], v[1], v[2]);
            foreach (int[] t in mesh.Triangles)
                if (TwiceArea(mesh.Vertices[t[0]], mesh.Vertices[t[1]], mesh.Vertices[t[2]]) > 1e-12)
                    clean.AddTriangle(t[0], t[1], t[2]);
            return clean;
        }

        private static double TwiceArea(double[] a, double[] b, double[] c)
        {
            double ux = b[0] - a[0], uy = b[1] - a[1], uz = b[2] - a[2];
            double vx = c[0] - a[0], vy = c[1] - a[1], vz = c[2] - a[2];
            double cx = uy * vz - uz * vy, cy = uz * vx - ux * vz, cz = ux * vy - uy * vx;
            return Math.Sqrt(cx * cx + cy * cy + cz * cz);
        }

        // -------------------------------------------------------------- winding

        /// <summary>
        /// The solid angle the mesh subtends at p, over 4 pi. One inside a closed
        /// mesh, zero outside, and the sign follows the triangle orientation - which
        /// is why the magnitude is what gets used.
        /// </summary>
        public static double WindingNumber(HostMesh mesh, double[] p)
        {
            double total = 0;
            foreach (int[] t in mesh.Triangles)
            {
                double[] va = mesh.Vertices[t[0]], vb = mesh.Vertices[t[1]], vc = mesh.Vertices[t[2]];
                double ax = va[0] - p[0], ay = va[1] - p[1], az = va[2] - p[2];
                double bx = vb[0] - p[0], by = vb[1] - p[1], bz = vb[2] - p[2];
                double cx = vc[0] - p[0], cy = vc[1] - p[1], cz = vc[2] - p[2];

                double la = Math.Sqrt(ax * ax + ay * ay + az * az);
                double lb = Math.Sqrt(bx * bx + by * by + bz * bz);
                double lc = Math.Sqrt(cx * cx + cy * cy + cz * cz);
                if (la < 1e-12 || lb < 1e-12 || lc < 1e-12) return double.NaN;

                double nx = by * cz - bz * cy, ny = bz * cx - bx * cz, nz = bx * cy - by * cx;
                double num = ax * nx + ay * ny + az * nz;
                double den = la * lb * lc
                             + (ax * bx + ay * by + az * bz) * lc
                             + (ax * cx + ay * cy + az * cz) * lb
                             + (bx * cx + by * cy + bz * cz) * la;
                total += 2.0 * Math.Atan2(num, den);
            }
            return total / FourPi;
        }

        // ------------------------------------------------------------- distance

        /// <summary>Distance from a point to the nearest place on the boundary, in millimetres.</summary>
        public static double DistanceToBoundary(HostMesh mesh, double[] p)
        {
            double best = double.MaxValue;
            foreach (int[] t in mesh.Triangles)
            {
                double[] a = mesh.Vertices[t[0]], b = mesh.Vertices[t[1]], c = mesh.Vertices[t[2]];
                // THE SAME TRIANGLES THE DIAGNOSIS ACCEPTED. Diagnose skips
                // zero-area triangles from the manifold test and leaves them in the
                // list; measuring to them made a sliver spanning the middle of a
                // beam into "the boundary", and a bar dead-centre in the concrete
                // came back with 8 mm of steel in the air. Two functions disagreeing
                // about what the boundary is.
                if (TwiceArea(a, b, c) <= 1e-12) continue;
                double d = PointTriangleDistance(p, a, b, c);
                if (d < best) best = d;
            }
            return best;
        }

        /// <summary>Closest distance from a point to a triangle, by region.</summary>
        public static double PointTriangleDistance(double[] p, double[] a, double[] b, double[] c)
        {
            double abx = b[0] - a[0], aby = b[1] - a[1], abz = b[2] - a[2];
            double acx = c[0] - a[0], acy = c[1] - a[1], acz = c[2] - a[2];
            double apx = p[0] - a[0], apy = p[1] - a[1], apz = p[2] - a[2];

            double d1 = abx * apx + aby * apy + abz * apz;
            double d2 = acx * apx + acy * apy + acz * apz;
            if (d1 <= 0 && d2 <= 0) return Len(apx, apy, apz);

            double bpx = p[0] - b[0], bpy = p[1] - b[1], bpz = p[2] - b[2];
            double d3 = abx * bpx + aby * bpy + abz * bpz;
            double d4 = acx * bpx + acy * bpy + acz * bpz;
            if (d3 >= 0 && d4 <= d3) return Len(bpx, bpy, bpz);

            double vc = d1 * d4 - d3 * d2;
            if (vc <= 0 && d1 >= 0 && d3 <= 0)
            {
                double v = d1 / (d1 - d3);
                return Len(apx - abx * v, apy - aby * v, apz - abz * v);
            }

            double cpx = p[0] - c[0], cpy = p[1] - c[1], cpz = p[2] - c[2];
            double d5 = abx * cpx + aby * cpy + abz * cpz;
            double d6 = acx * cpx + acy * cpy + acz * cpz;
            if (d6 >= 0 && d5 <= d6) return Len(cpx, cpy, cpz);

            double vb = d5 * d2 - d1 * d6;
            if (vb <= 0 && d2 >= 0 && d6 <= 0)
            {
                double w = d2 / (d2 - d6);
                return Len(apx - acx * w, apy - acy * w, apz - acz * w);
            }

            double va = d3 * d6 - d5 * d4;
            if (va <= 0 && (d4 - d3) >= 0 && (d5 - d6) >= 0)
            {
                double w = (d4 - d3) / ((d4 - d3) + (d5 - d6));
                return Len(p[0] - (b[0] + (c[0] - b[0]) * w),
                           p[1] - (b[1] + (c[1] - b[1]) * w),
                           p[2] - (b[2] + (c[2] - b[2]) * w));
            }

            double denom = va + vb + vc;
            if (Math.Abs(denom) < 1e-20) return Len(apx, apy, apz);
            double vv = vb / denom, ww = vc / denom;
            return Len(apx - (abx * vv + acx * ww), apy - (aby * vv + acy * ww), apz - (abz * vv + acz * ww));
        }

        private static double Len(double x, double y, double z)
        {
            return Math.Sqrt(x * x + y * y + z * z);
        }

        /// <summary>Positive inside the host, negative outside, NaN when the mesh will not say.</summary>
        public static double SignedDistance(HostMesh mesh, double[] p, out double windingDeviation)
        {
            windingDeviation = double.NaN;

            double d = DistanceToBoundary(mesh, p);
            if (!IsFinite(d)) return double.NaN;

            // ON the boundary the sign is not a question worth asking, and it is the
            // one place the winding number cannot answer it: a point exactly on a
            // face gives one half, which is the same number an open shell gives.
            // A bar that ends flush with the end of its beam lands here on every
            // run, so refusing it would refuse the ordinary case.
            if (d <= OnBoundaryToleranceMm) { windingDeviation = 0; return 0; }

            double w = WindingNumber(mesh, p);
            if (!IsFinite(w)) return double.NaN;

            // THE NEAREST WHOLE NUMBER, not "zero or one". A host whose geometry
            // Revit returns as two OVERLAPPING solids - an in-place family, joined
            // framing - gives a winding number of 2 in the overlap, and comparing
            // that against zero-or-one made every bar in it unmeasurable. Two is as
            // inside as one is.
            double aw = Math.Abs(w);
            double nearest = Math.Round(aw);
            double dev = Math.Abs(aw - nearest);
            windingDeviation = dev;
            if (dev > WindingConfidenceLimit) return double.NaN;

            return nearest >= 1 ? d : -d;
        }

        // ------------------------------------------------------------- sampling

        /// <summary>
        /// Points along a polyline: every vertex, plus enough between them that no
        /// gap is longer than the step. The cap is not a silent truncation - the step
        /// widens instead, and the verdict publishes the step that was used.
        /// </summary>
        public static List<double[]> Sample(IList<double[]> polyline, double stepMm, out double actualStepMm)
        {
            actualStepMm = stepMm;
            var outp = new List<double[]>();
            if (polyline == null || polyline.Count == 0) return outp;
            if (!IsFinite(stepMm) || stepMm <= 0) return outp;

            double total = 0;
            for (int i = 1; i < polyline.Count; i++)
                total += Len(polyline[i][0] - polyline[i - 1][0],
                             polyline[i][1] - polyline[i - 1][1],
                             polyline[i][2] - polyline[i - 1][2]);

            if (IsFinite(total) && total > 0)
            {
                double needed = total / stepMm + polyline.Count;
                if (needed > MaxSamples) actualStepMm = total / Math.Max(1, MaxSamples - polyline.Count);
            }

            outp.Add(polyline[0]);
            for (int i = 1; i < polyline.Count; i++)
            {
                double[] a = polyline[i - 1], b = polyline[i];
                double len = Len(b[0] - a[0], b[1] - a[1], b[2] - a[2]);
                int n = 1;
                if (IsFinite(len) && len > actualStepMm)
                {
                    double want = Math.Ceiling(len / actualStepMm);
                    n = want > MaxSamples ? MaxSamples : (int)want;
                }
                for (int k = 1; k <= n; k++)
                {
                    double f = (double)k / n;
                    outp.Add(new[]
                    {
                        a[0] + (b[0] - a[0]) * f,
                        a[1] + (b[1] - a[1]) * f,
                        a[2] + (b[2] - a[2]) * f
                    });
                }
            }
            return outp;
        }

        // ------------------------------------------------------------- the check

        /// <summary>
        /// The one definition of "is this bar in the concrete", shared by the plan,
        /// the apply and the audit. The plan hands it the centreline it is about to
        /// ask for; the apply and the audit hand it the centreline Revit drew.
        /// </summary>
        public static ContainmentVerdict Classify(HostMesh mesh, IList<double[]> centrelineMm,
            double barRadiusMm, double? requiredCoverMm, double toleranceMm, double sampleStepMm)
        {
            return Classify(mesh, centrelineMm, false, barRadiusMm, requiredCoverMm, toleranceMm, sampleStepMm);
        }

        /// <summary>
        /// The same, told explicitly whether the bar CLOSES.
        ///
        /// Closedness used to be inferred from the last point equalling the first -
        /// and a requirement set is REFUSED for repeating its first point, because
        /// `closed` adds the last segment. So every legally declared stirrup was
        /// measured with one whole side missing, and with the radius tapered off the
        /// two corners that side joins. Measured: a stirrup with 3 mm of steel out
        /// through one face came back `inside`, over 1006 mm of a 1518 mm bar.
        /// </summary>
        public static ContainmentVerdict Classify(HostMesh mesh, IList<double[]> centrelineMm,
            bool declaredClosed, double barRadiusMm, double? requiredCoverMm,
            double toleranceMm, double sampleStepMm)
        {
            var v = new ContainmentVerdict
            {
                BarRadiusMm = barRadiusMm,
                RequiredCoverMm = requiredCoverMm,
                ToleranceMm = toleranceMm,
                CurvedBoundaryApproximated = mesh != null && mesh.AnyCurvedFace,
                ChordToleranceMm = mesh == null ? 0 : mesh.ChordToleranceMm,
                HowMeasured = "every sampled point of the centreline against the host's triangulated " +
                              "boundary: inside-ness by winding number, distance by closest point on the " +
                              "boundary, and the bar radius taken off to get the surface - tapering to " +
                              "nothing over the last radius-worth of an open bar, because a rebar has flat " +
                              "ends rather than hemispherical ones."
            };

            MeshDiagnosis d = Diagnose(mesh);
            if (!d.Usable) { v.Why = d.Why; return v; }

            // ONE SET OF TRIANGLES for both questions. Diagnose skips zero-area
            // triangles from the manifold test and leaves them in the list, and both
            // the distance and the winding number then saw a boundary the diagnosis
            // had never accepted. A collinear sliver contributes exactly half a turn
            // to the winding number whenever the determinant comes out negative -
            // enough to make a bar in the middle of a beam unmeasurable - and it is
            // the nearest "face" to points nowhere near the surface.
            if (d.DegenerateTriangles > 0) mesh = WithoutDegenerateTriangles(mesh);

            if (!IsFinite(barRadiusMm) || barRadiusMm < 0)
            {
                v.Why = "the bar radius was not a finite, non-negative number.";
                return v;
            }
            if (!IsFinite(toleranceMm) || toleranceMm < 0)
            {
                v.Why = "the tolerance was not a finite, non-negative number.";
                return v;
            }
            if (requiredCoverMm.HasValue && (!IsFinite(requiredCoverMm.Value) || requiredCoverMm.Value < 0))
            {
                v.Why = "the declared cover was not a finite, non-negative number.";
                return v;
            }
            if (centrelineMm == null || centrelineMm.Count == 0)
            {
                v.Why = "no centreline was given, so there was nothing to test.";
                return v;
            }
            foreach (double[] p in centrelineMm)
                if (!FinitePoint(p))
                {
                    v.Why = "the centreline contained a point that was not three finite numbers.";
                    return v;
                }
            if (barRadiusMm <= 0)
            {
                v.Why = "the bar's model diameter is not available, so the surface of the bar cannot be " +
                        "located and only its centreline could be tested. That is not the question this " +
                        "answers, and answering the easier one silently would report a bar 20 mm across as " +
                        "inside when 7 mm of it is in the air.";
                return v;
            }

            // A DECLARED CLOSED SHAPE gets its closing segment before anything is
            // sampled. A shape that already repeats its first point is left alone.
            IList<double[]> path = centrelineMm;
            if (declaredClosed && centrelineMm.Count >= 3)
            {
                double[] first = centrelineMm[0], last = centrelineMm[centrelineMm.Count - 1];
                if (Len(last[0] - first[0], last[1] - first[1], last[2] - first[2]) > 1e-6)
                {
                    var shut = new List<double[]>(centrelineMm) { new[] { first[0], first[1], first[2] } };
                    path = shut;
                }
            }

            // A BAR WITH NO LENGTH HAS NO SURFACE. One point, or every point on top
            // of the others: the taper then zeroes the radius everywhere and a
            // centreline half a millimetre inside a face came back `inside` for a
            // bar 20 mm across.
            double totalDeclared = 0;
            for (int i = 1; i < path.Count; i++)
                totalDeclared += Len(path[i][0] - path[i - 1][0],
                                     path[i][1] - path[i - 1][1],
                                     path[i][2] - path[i - 1][2]);
            if (totalDeclared <= 1e-9)
            {
                v.Why = "the centreline has no length - a single point, or every point on top of the others - " +
                        "so it has no surface to test and no direction to have one.";
                return v;
            }

            double step = IsFinite(sampleStepMm) && sampleStepMm > 0 ? sampleStepMm : 25.0;
            double used;
            List<double[]> samples = Sample(path, step, out used);
            v.SampleStepMm = used;
            v.SamplesTested = samples.Count;
            if (samples.Count == 0) { v.Why = "the centreline could not be sampled."; return v; }

            // THE BAR HAS FLAT ENDS. Treating it as a capsule - every point of the
            // centreline carrying a full-radius ball - puts a hemisphere past each
            // end that no rebar has, and a bar finishing flush with the end of its
            // beam would then read as half a diameter out in the air. That is the
            // ordinary case, not an error, so the radius tapers to nothing over the
            // last radius-worth of an open bar at each end. The solid tested is a
            // subset of the real cylinder, which is stated rather than implied:
            // within one radius of an end this becomes a centreline test.
            //
            // A closed shape - a stirrup - has no ends and carries its full radius
            // the whole way round.
            var arc = new double[samples.Count];
            for (int i = 1; i < samples.Count; i++)
                arc[i] = arc[i - 1] + Len(samples[i][0] - samples[i - 1][0],
                                          samples[i][1] - samples[i - 1][1],
                                          samples[i][2] - samples[i - 1][2]);
            double totalLen = arc[samples.Count - 1];
            bool closed = declaredClosed ||
                          (samples.Count > 2 &&
                           Len(samples[samples.Count - 1][0] - samples[0][0],
                               samples[samples.Count - 1][1] - samples[0][1],
                               samples[samples.Count - 1][2] - samples[0][2]) <= 1e-6);
            v.ClosedLoop = closed;

            double minSigned = double.MaxValue, maxSigned = double.MinValue, worstDev = 0;
            double minClearance = double.MaxValue;
            int worstIndex = -1;
            double[] worstPoint = null;

            for (int i = 0; i < samples.Count; i++)
            {
                double dev;
                double sd = SignedDistance(mesh, samples[i], out dev);
                if (!IsFinite(sd))
                {
                    v.SamplesTested = i;
                    v.WorstWindingDeviation = IsFinite(dev) ? dev : 0.5;
                    v.WorstSampleIndex = i;
                    v.WorstPointMm = samples[i];
                    v.Why = "the host boundary would not say whether a point of the bar is inside it" +
                            (IsFinite(dev)
                                ? " (winding number " + dev.ToString("0.###", CultureInfo.InvariantCulture) +
                                  " away from a whole number)"
                                : "") + ". Unknown is not a pass.";
                    return v;
                }
                if (IsFinite(dev) && dev > worstDev) worstDev = dev;
                if (sd < minSigned) minSigned = sd;
                if (sd > maxSigned) maxSigned = sd;

                double effective = barRadiusMm;
                if (!closed)
                {
                    double fromEnd = Math.Min(arc[i], totalLen - arc[i]);
                    if (fromEnd < effective) effective = Math.Max(0, fromEnd);
                }
                double clearance = sd - effective;
                if (clearance < minClearance) { minClearance = clearance; worstIndex = i; worstPoint = samples[i]; }
            }

            v.Evaluated = true;
            v.WorstWindingDeviation = worstDev;
            v.MinSignedDistanceMm = minSigned;
            v.MinSurfaceClearanceMm = minClearance;
            v.WorstSampleIndex = worstIndex;
            v.WorstPointMm = worstPoint;

            // STRICTLY outside. A bar lying exactly IN a face of its host has every
            // sampled point at distance zero, and calling that "completely outside"
            // overstates it: half the diameter is in the concrete. It falls through
            // to the clearance test below and comes out partially_outside, which is
            // what it is.
            if (maxSigned < 0)
            {
                v.Word = CompletelyOutside;
                v.WorstOutsideMm = -minClearance;
                v.Why = "no sampled point of the centreline is inside the host.";
                return v;
            }

            if (v.MinSurfaceClearanceMm < -toleranceMm)
            {
                v.Word = PartiallyOutside;
                v.WorstOutsideMm = -v.MinSurfaceClearanceMm;
                v.Why = "part of the bar surface is outside the host by " +
                        v.WorstOutsideMm.ToString("0.###", CultureInfo.InvariantCulture) + " mm.";
                return v;
            }

            if (requiredCoverMm.HasValue && v.MinSurfaceClearanceMm < requiredCoverMm.Value - toleranceMm)
            {
                v.Word = InsideCoverViolated;
                v.CoverShortfallMm = requiredCoverMm.Value - v.MinSurfaceClearanceMm;
                v.Why = "the bar is inside the host, but its surface comes within " +
                        v.MinSurfaceClearanceMm.ToString("0.###", CultureInfo.InvariantCulture) +
                        " mm of a face where " +
                        requiredCoverMm.Value.ToString("0.###", CultureInfo.InvariantCulture) +
                        " mm was declared.";
                return v;
            }

            v.Word = Inside;
            v.Why = "every sampled point of the bar surface is inside the host" +
                    (requiredCoverMm.HasValue ? " and meets the declared cover." : ".");
            return v;
        }

        /// <summary>The worst of several verdicts - the answer for a whole set.</summary>
        public static string Weakest(IEnumerable<string> words)
        {
            bool any = false, notEval = false, outside = false, partial = false, cover = false;
            foreach (string w in words)
            {
                any = true;
                if (w == NotEvaluable) notEval = true;
                else if (w == CompletelyOutside) outside = true;
                else if (w == PartiallyOutside) partial = true;
                else if (w == InsideCoverViolated) cover = true;
                else if (w != Inside) throw new ArgumentException("unknown containment word '" + w + "'");
            }
            if (!any) return NotEvaluable;
            if (outside) return CompletelyOutside;
            if (partial) return PartiallyOutside;
            if (notEval) return NotEvaluable;
            if (cover) return InsideCoverViolated;
            return Inside;
        }
    }
}
