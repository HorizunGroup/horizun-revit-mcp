// -----------------------------------------------------------------------------
// Horizun Revit MCP - the holes in a host, and what a mat does about them.
// Original Horizun code. No Revit types.
//
// A slab with a shaft through it has the shaft ABSENT from its solid, so the
// welded host mesh already carries the hole: the face the mat sits under is a
// polygon with an inner loop. This file reads that loop back out of the mesh -
// boundary edges of the triangles lying in the face plane, chained into rings,
// the largest ring the outline and every other ring an opening - and turns it
// into the one thing a mat rule needs: for a bar at a given station across the
// face, the stretch(es) along the bar where its body would be over the void.
//
// Three policies, all DECLARED and none defaulted, because each is a design
// decision somebody is answerable for:
//   omit    the bar is not built at all
//   trim    the bar stops short of the opening on each side, by a declared
//           clearance, and each remaining stretch is its own bar
//   ignore  the bar is built as declared and the crossing is REPORTED; the
//           containment check then refuses it if it is really over the void
//
// What is deliberately NOT here: trimming bars, extra edge bars, anything that
// replaces the steel an opening removed. That is design, and this bridge does
// not do it - the reply says which bars were dropped or shortened and leaves
// the question of what goes around the hole to the person who owns the answer.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    /// <summary>One closed ring of the host face, in model coordinates.</summary>
    public sealed class FaceLoop
    {
        public List<double[]> PointsMm = new List<double[]>();
        /// <summary>Unsigned area in the face plane.</summary>
        public double AreaMm2;
        public bool Outer;
    }

    /// <summary>What the face looked like once its triangles were walked.</summary>
    public sealed class FaceLoops
    {
        public FaceLoop Outer;
        public List<FaceLoop> Openings = new List<FaceLoop>();
        public int TrianglesInFace;
        /// <summary>Null when the loops were extracted; the reason otherwise. Never silence.</summary>
        public string Why;
        public bool Ok { get { return Why == null; } }
    }

    /// <summary>An opening as one mat component sees it: in that component's (u, v) frame.</summary>
    public sealed class MatOpeningRegion
    {
        public int Index;
        /// <summary>The ring in the component's frame: u along the bars, v across them.</summary>
        public List<double[]> Uv = new List<double[]>();
        /// <summary>The largest distance between two of its vertices - frame independent, which is why it is the size compared with minimum_size_mm.</summary>
        public double DiameterMm;
        public double AreaMm2;
        public double UMin, UMax, VMin, VMax;
        public bool Considered;
        public string Why;

        public JObject ToJson()
        {
            return new JObject
            {
                ["index"] = Index,
                ["considered"] = Considered,
                ["diameter_mm"] = Math.Round(DiameterMm, 3),
                ["area_mm2"] = Math.Round(AreaMm2, 1),
                ["extent_along_bars_mm"] = Math.Round(UMax - UMin, 3),
                ["extent_across_bars_mm"] = Math.Round(VMax - VMin, 3),
                ["vertices"] = Uv.Count,
                ["why"] = Why
            };
        }
    }

    /// <summary>A bar the trim policy shortened, and what is left of it.</summary>
    public sealed class MatTrimmedBar
    {
        public int Bar;
        public double PositionMm;
        /// <summary>Each remaining stretch as [from, to] along the bar, in the component's u.</summary>
        public List<double[]> SegmentsMm = new List<double[]>();
        /// <summary>Each stretch removed, with the clearance applied.</summary>
        public List<double[]> RemovedMm = new List<double[]>();
        /// <summary>A stretch too short to build (under MatOpenings.MinimumSegmentMm), dropped and named.</summary>
        public List<double[]> DroppedMm = new List<double[]>();
    }

    /// <summary>One expanded rule of a component: which bars it carries and, when trimmed, which stretch.</summary>
    public sealed class MatOpeningRun
    {
        public string RuleId;
        public int FirstBar;
        public int LastBar;
        public int Bars;
        public bool Trimmed;
        public double FromMm;
        public double ToMm;
    }

    /// <summary>Every decision the openings policy took for one component. Reported in full, per component.</summary>
    public sealed class MatOpeningReport
    {
        public string Policy;
        public int BarsPlanned;
        public int BarsKept;
        public List<int> BarsOmitted = new List<int>();
        public List<MatTrimmedBar> BarsTrimmed = new List<MatTrimmedBar>();
        /// <summary>Bars whose body would be over a considered opening. Under ignore they are built anyway.</summary>
        public List<int> BarsCrossing = new List<int>();
        public List<MatOpeningRun> Runs = new List<MatOpeningRun>();
        public int OpeningsConsidered;
        public int OpeningsIgnored;

        public JObject ToJson()
        {
            var runs = new JArray();
            foreach (MatOpeningRun r in Runs)
                runs.Add(new JObject
                {
                    ["rule_id"] = r.RuleId,
                    ["first_bar"] = r.FirstBar,
                    ["last_bar"] = r.LastBar,
                    ["bars"] = r.Bars,
                    ["trimmed"] = r.Trimmed,
                    ["from_mm"] = Math.Round(r.FromMm, 3),
                    ["to_mm"] = Math.Round(r.ToMm, 3)
                });
            var trimmed = new JArray();
            foreach (MatTrimmedBar t in BarsTrimmed)
                trimmed.Add(new JObject
                {
                    ["bar"] = t.Bar,
                    ["position_mm"] = Math.Round(t.PositionMm, 3),
                    ["segment_lengths_mm"] = new JArray(t.SegmentsMm.Select(s => (object)Math.Round(s[1] - s[0], 3)).ToArray()),
                    ["segments_mm"] = Pairs(t.SegmentsMm),
                    ["removed_mm"] = Pairs(t.RemovedMm),
                    ["dropped_too_short_mm"] = Pairs(t.DroppedMm)
                });
            return new JObject
            {
                ["policy"] = Policy,
                ["bars_planned"] = BarsPlanned,
                ["bars_kept"] = BarsKept,
                ["bars_omitted"] = new JArray(BarsOmitted.Cast<object>().ToArray()),
                ["bars_trimmed"] = trimmed,
                ["bars_crossing"] = new JArray(BarsCrossing.Cast<object>().ToArray()),
                ["openings_considered"] = OpeningsConsidered,
                ["openings_ignored"] = OpeningsIgnored,
                ["runs"] = runs,
                ["no_replacement_steel"] =
                    "no trimming bars or extra edge bars are added around an opening. What replaces the " +
                    "steel an opening removed is a design decision, and it is left to the person who owns it."
            };
        }

        private static JArray Pairs(List<double[]> pairs)
        {
            var a = new JArray();
            foreach (double[] p in pairs) a.Add(new JArray(Math.Round(p[0], 3), Math.Round(p[1], 3)));
            return a;
        }
    }

    /// <summary>
    /// The openings a component's bars were planned around, carried on every
    /// rule the component expanded into so the apply can check the DRAWN bars
    /// against the same regions after the commit.
    /// </summary>
    public sealed class MatOpeningContext
    {
        public string Policy;
        public double MinimumSizeMm;
        public double ClearanceMm;
        public double BarRadiusMm;
        public double[] Along;
        public double[] Across;
        public List<MatOpeningRegion> Openings = new List<MatOpeningRegion>();
        public MatOpeningReport Report;

        public IEnumerable<MatOpeningRegion> Considered { get { return Openings.Where(o => o.Considered); } }

        public JObject ToJson()
        {
            var o = new JObject
            {
                ["policy"] = Policy,
                ["minimum_size_mm"] = Math.Round(MinimumSizeMm, 3),
                ["clearance_mm"] = Math.Round(ClearanceMm, 3),
                ["bar_radius_mm"] = Math.Round(BarRadiusMm, 3),
                ["openings"] = new JArray(Openings.Select(x => (object)x.ToJson()).ToArray()),
                ["how_found"] = MatOpenings.HowFound
            };
            if (Report != null) o["component"] = Report.ToJson();
            return o;
        }
    }

    /// <summary>What one drawn bar position looks like against the considered openings.</summary>
    public sealed class MatOpeningBarVerdict
    {
        public int Position;
        public double VMm;
        public bool CrossesAnOpening;
        public bool ShortOfClearance;
        /// <summary>The worst overlap of the bar's body with an opening, in mm; zero when none.</summary>
        public double OverlapMm;
        /// <summary>The smallest gap between a bar end and an opening it stops short of; null when it crosses none.</summary>
        public double? GapMm;
    }

    public sealed class MatOpeningCheck
    {
        public bool Evaluated;
        public string Why;
        public int PositionsTested;
        public List<MatOpeningBarVerdict> Verdicts = new List<MatOpeningBarVerdict>();
        public List<int> Crossing = new List<int>();
        public List<int> ShortOfClearance = new List<int>();
        public double WorstOverlapMm;
        public double? MinGapMm;

        public JObject ToJson()
        {
            var o = new JObject
            {
                ["evaluated"] = Evaluated,
                ["positions_tested"] = PositionsTested,
                ["positions_crossing_an_opening"] = new JArray(Crossing.Cast<object>().ToArray()),
                ["positions_short_of_clearance"] = new JArray(ShortOfClearance.Cast<object>().ToArray()),
                ["worst_overlap_mm"] = Math.Round(WorstOverlapMm, 3),
                ["min_gap_mm"] = MinGapMm.HasValue ? (JToken)Math.Round(MinGapMm.Value, 3) : JValue.CreateNull(),
                ["why"] = Why
            };
            return o;
        }
    }

    public static class MatOpenings
    {
        /// <summary>How far off the face plane a vertex may sit and still belong to the face.</summary>
        public const double PlaneToleranceMm = 0.5;

        /// <summary>
        /// A trimmed stretch shorter than this is dropped and named rather than
        /// sent to Revit, whose short-curve tolerance would refuse it as
        /// curve_degenerate after the rehearsal had already passed.
        /// </summary>
        public const double MinimumSegmentMm = 1.0;

        public const string HowFound =
            "the boundary edges of the host mesh's triangles that lie in the face plane, chained into rings. " +
            "The ring with the largest area is the outline; every other ring is an opening. Openings are " +
            "present in the mesh because Revit's solids already subtract them.";

        private static bool Finite(double v)
        {
            return !double.IsNaN(v) && !double.IsInfinity(v);
        }

        // ---------------------------------------------------------- the face

        /// <summary>
        /// The rings of the face at <paramref name="faceAtMm"/> along <paramref name="up"/>.
        /// Fails, with the reason, rather than returning a face with no rings.
        /// </summary>
        public static FaceLoops ExtractFaceLoops(HostMesh mesh, double[] up, double faceAtMm, double planeToleranceMm)
        {
            var r = new FaceLoops();
            if (mesh == null || mesh.Triangles.Count == 0) { r.Why = "no host boundary was obtained."; return r; }
            double[] n = RebarContainment.Unit(up);
            if (n == null) { r.Why = "the face normal is not a usable vector."; return r; }
            double tol = planeToleranceMm > 0 ? planeToleranceMm : PlaneToleranceMm;

            // The triangles IN the face: all three vertices on the plane.
            var inFace = new List<int[]>();
            var onPlane = new bool[mesh.Vertices.Count];
            for (int i = 0; i < mesh.Vertices.Count; i++)
            {
                double[] v = mesh.Vertices[i];
                double along = v[0] * n[0] + v[1] * n[1] + v[2] * n[2];
                onPlane[i] = Finite(along) && Math.Abs(along - faceAtMm) <= tol;
            }
            foreach (int[] t in mesh.Triangles)
            {
                if (t == null || t.Length != 3) continue;
                if (t[0] < 0 || t[1] < 0 || t[2] < 0 || t[0] >= onPlane.Length || t[1] >= onPlane.Length || t[2] >= onPlane.Length) continue;
                if (onPlane[t[0]] && onPlane[t[1]] && onPlane[t[2]]) inFace.Add(t);
            }
            r.TrianglesInFace = inFace.Count;
            if (inFace.Count == 0)
            {
                r.Why = "no triangle of the host boundary lies in the face plane, so the face has no outline to " +
                        "read openings from.";
                return r;
            }

            // Directed edges used once within the face are its boundary - the
            // outline and every hole. An edge shared by two face triangles is
            // interior; an edge shared with a WALL triangle is not in this subset
            // at all, which is exactly why holes show up.
            var count = new Dictionary<long, int>();
            long m = mesh.Vertices.Count;
            foreach (int[] t in inFace)
                for (int k = 0; k < 3; k++)
                {
                    int a = t[k], b = t[(k + 1) % 3];
                    long fwd = a * m + b, back = b * m + a;
                    int had;
                    if (count.TryGetValue(back, out had)) { if (had == 1) count.Remove(back); else count[back] = had - 1; }
                    else count[fwd] = count.TryGetValue(fwd, out had) ? had + 1 : 1;
                }

            var next = new Dictionary<int, List<int>>();
            foreach (KeyValuePair<long, int> e in count)
            {
                if (e.Value != 1)
                {
                    r.Why = "the face boundary uses an edge more than once, so its rings cannot be told apart.";
                    return r;
                }
                int a = (int)(e.Key / m), b = (int)(e.Key % m);
                List<int> outs;
                if (!next.TryGetValue(a, out outs)) { outs = new List<int>(); next[a] = outs; }
                outs.Add(b);
            }
            if (next.Count == 0) { r.Why = "the face triangles have no boundary edge, which is not a face."; return r; }

            var used = new HashSet<long>();
            var loops = new List<FaceLoop>();
            foreach (int start in next.Keys.OrderBy(x => x).ToList())
            {
                foreach (int firstTo in next[start])
                {
                    if (used.Contains(start * m + firstTo)) continue;
                    var ring = new List<int> { start };
                    int at = start, to = firstTo;
                    used.Add(at * m + to);
                    int guard = 0;
                    while (to != start)
                    {
                        ring.Add(to);
                        List<int> outs;
                        if (!next.TryGetValue(to, out outs))
                        {
                            r.Why = "a ring of the face boundary does not close: the edge into vertex " + to +
                                    " has no edge out of it.";
                            return r;
                        }
                        int chosen = -1;
                        foreach (int cand in outs)
                            if (!used.Contains(to * m + cand)) { chosen = cand; break; }
                        if (chosen < 0)
                        {
                            r.Why = "a ring of the face boundary does not close at vertex " + to + ".";
                            return r;
                        }
                        used.Add(to * m + chosen);
                        at = to;
                        to = chosen;
                        if (++guard > next.Count + 2)
                        {
                            r.Why = "a ring of the face boundary never returns to where it started.";
                            return r;
                        }
                    }
                    var loop = new FaceLoop();
                    foreach (int idx in ring) loop.PointsMm.Add(new[] { mesh.Vertices[idx][0], mesh.Vertices[idx][1], mesh.Vertices[idx][2] });
                    loop.PointsMm = DropCollinear(loop.PointsMm);
                    if (loop.PointsMm.Count < 3) continue;
                    loops.Add(loop);
                }
            }
            if (loops.Count == 0) { r.Why = "the face boundary formed no ring with three or more corners."; return r; }

            // Area in the plane, computed from two in-plane axes so it does not
            // depend on which way the face points.
            double[] u = AnyPerpendicular(n);
            double[] w = Cross(n, u);
            foreach (FaceLoop l in loops)
                l.AreaMm2 = Math.Abs(SignedArea(ProjectUv(l.PointsMm, u, w)));
            FaceLoop outer = loops.OrderByDescending(l => l.AreaMm2).First();
            outer.Outer = true;
            r.Outer = outer;
            foreach (FaceLoop l in loops) if (!ReferenceEquals(l, outer)) r.Openings.Add(l);
            return r;
        }

        /// <summary>The ring with straight-through vertices removed, so a grid corner does not count as one.</summary>
        public static List<double[]> DropCollinear(List<double[]> ring)
        {
            if (ring == null || ring.Count < 3) return ring ?? new List<double[]>();
            var outp = new List<double[]>(ring.Count);
            int n = ring.Count;
            for (int i = 0; i < n; i++)
            {
                double[] p = ring[(i + n - 1) % n], q = ring[i], s = ring[(i + 1) % n];
                double[] a = { q[0] - p[0], q[1] - p[1], q[2] - p[2] };
                double[] b = { s[0] - q[0], s[1] - q[1], s[2] - q[2] };
                double[] c = Cross(a, b);
                double la = Math.Sqrt(a[0] * a[0] + a[1] * a[1] + a[2] * a[2]);
                double lb = Math.Sqrt(b[0] * b[0] + b[1] * b[1] + b[2] * b[2]);
                double lc = Math.Sqrt(c[0] * c[0] + c[1] * c[1] + c[2] * c[2]);
                bool straight = la > 0 && lb > 0 && lc <= 1e-6 * la * lb &&
                                (a[0] * b[0] + a[1] * b[1] + a[2] * b[2]) > 0;
                if (!straight) outp.Add(q);
            }
            return outp;
        }

        // ------------------------------------------------------- the frame

        /// <summary>Points as (u, v): u along the bars, v across them.</summary>
        public static List<double[]> ProjectUv(IList<double[]> pointsMm, double[] along, double[] across)
        {
            var uv = new List<double[]>(pointsMm.Count);
            foreach (double[] p in pointsMm)
                uv.Add(new[]
                {
                    p[0] * along[0] + p[1] * along[1] + p[2] * along[2],
                    p[0] * across[0] + p[1] * across[1] + p[2] * across[2]
                });
            return uv;
        }

        public static double SignedArea(IList<double[]> uv)
        {
            double a = 0;
            for (int i = 0; i < uv.Count; i++)
            {
                double[] p = uv[i], q = uv[(i + 1) % uv.Count];
                a += p[0] * q[1] - q[0] * p[1];
            }
            return a / 2.0;
        }

        /// <summary>The largest distance between two vertices of the ring.</summary>
        public static double Diameter(IList<double[]> uv)
        {
            double best = 0;
            for (int i = 0; i < uv.Count; i++)
                for (int k = i + 1; k < uv.Count; k++)
                {
                    double du = uv[i][0] - uv[k][0], dv = uv[i][1] - uv[k][1];
                    double d = Math.Sqrt(du * du + dv * dv);
                    if (d > best) best = d;
                }
            return best;
        }

        /// <summary>An opening ring put into a component's frame, sized, and judged against the declared minimum.</summary>
        public static MatOpeningRegion Region(FaceLoop loop, int index, double[] along, double[] across, double minimumSizeMm)
        {
            var reg = new MatOpeningRegion { Index = index, Uv = ProjectUv(loop.PointsMm, along, across) };
            reg.DiameterMm = Diameter(reg.Uv);
            reg.AreaMm2 = Math.Abs(SignedArea(reg.Uv));
            reg.UMin = reg.Uv.Min(p => p[0]); reg.UMax = reg.Uv.Max(p => p[0]);
            reg.VMin = reg.Uv.Min(p => p[1]); reg.VMax = reg.Uv.Max(p => p[1]);
            reg.Considered = reg.DiameterMm >= minimumSizeMm;
            reg.Why = reg.Considered
                ? "its largest dimension, " + Mm(reg.DiameterMm) + ", is not below the declared minimum_size_mm of " +
                  Mm(minimumSizeMm) + "."
                : "ignored: its largest dimension, " + Mm(reg.DiameterMm) + ", is below the declared " +
                  "minimum_size_mm of " + Mm(minimumSizeMm) + ". The bars run over it as if it were not there.";
            return reg;
        }

        // ---------------------------------------------------- the crossing

        /// <summary>
        /// The stretches of the line v = <paramref name="v"/> that lie within
        /// <paramref name="dilateMm"/> of the ring's interior - which, with the
        /// bar's radius as the dilation, is exactly where a bar of that radius at
        /// that station has some of its body over the void. Merged, ascending.
        ///
        /// Exact for the dilated polygon: the interior by even-odd crossing, and
        /// each edge's capsule (its two end discs and the rectangle between)
        /// intersected with the line. A capsule is convex, so its three parts
        /// give one interval and min/max of them is that interval.
        /// </summary>
        public static List<double[]> CrossingIntervals(IList<double[]> polyUv, double v, double dilateMm)
        {
            var found = new List<double[]>();
            if (polyUv == null || polyUv.Count < 3 || !Finite(v)) return found;
            double d = Finite(dilateMm) && dilateMm > 0 ? dilateMm : 0;
            int n = polyUv.Count;

            // THE INTERIOR, a hair either side of the station and intersected. A
            // bar whose centreline lies exactly along an opening's edge has no
            // body over the void when its radius is zero, and the even-odd rule
            // evaluated AT the edge answers differently for the bottom edge and the
            // top - one counts, the other does not. Asking just above and just
            // below and keeping only what both agree on makes a tangent bar clear
            // on every edge, and leaves a bar strictly inside exactly where it was.
            found.AddRange(Intersect(Interior(polyUv, v - 1e-6), Interior(polyUv, v + 1e-6)));

            if (d > 0)
                for (int i = 0; i < n; i++)
                {
                    double[] p = polyUv[i], q = polyUv[(i + 1) % n];
                    double lo = double.MaxValue, hi = double.MinValue;
                    Disc(p, v, d, ref lo, ref hi);
                    Disc(q, v, d, ref lo, ref hi);
                    double eu = q[0] - p[0], ev = q[1] - p[1];
                    double len = Math.Sqrt(eu * eu + ev * ev);
                    if (len > 1e-12)
                    {
                        double nu = -ev / len * d, nv = eu / len * d;
                        double[][] quad =
                        {
                            new[] { p[0] + nu, p[1] + nv }, new[] { q[0] + nu, q[1] + nv },
                            new[] { q[0] - nu, q[1] - nv }, new[] { p[0] - nu, p[1] - nv }
                        };
                        for (int k = 0; k < 4; k++)
                        {
                            double[] a = quad[k], b = quad[(k + 1) % 4];
                            if ((a[1] > v) != (b[1] > v))
                            {
                                double x = a[0] + (v - a[1]) * (b[0] - a[0]) / (b[1] - a[1]);
                                if (x < lo) lo = x;
                                if (x > hi) hi = x;
                            }
                        }
                    }
                    if (lo <= hi) found.Add(new[] { lo, hi });
                }
            return Merge(found);
        }

        /// <summary>Where the line v crosses the ring's interior, by the even-odd rule, ascending pairs.</summary>
        public static List<double[]> Interior(IList<double[]> polyUv, double v)
        {
            var xs = new List<double>();
            int n = polyUv.Count;
            for (int i = 0; i < n; i++)
            {
                double[] p = polyUv[i], q = polyUv[(i + 1) % n];
                if ((p[1] > v) != (q[1] > v))
                    xs.Add(p[0] + (v - p[1]) * (q[0] - p[0]) / (q[1] - p[1]));
            }
            xs.Sort();
            var outp = new List<double[]>();
            for (int i = 0; i + 1 < xs.Count; i += 2) outp.Add(new[] { xs[i], xs[i + 1] });
            return Merge(outp);
        }

        /// <summary>The stretches two merged interval lists share.</summary>
        public static List<double[]> Intersect(List<double[]> a, List<double[]> b)
        {
            var outp = new List<double[]>();
            int i = 0, k = 0;
            while (i < a.Count && k < b.Count)
            {
                double lo = Math.Max(a[i][0], b[k][0]), hi = Math.Min(a[i][1], b[k][1]);
                if (hi > lo + 1e-9) outp.Add(new[] { lo, hi });
                if (a[i][1] < b[k][1]) i++; else k++;
            }
            return outp;
        }

        private static void Disc(double[] c, double v, double d, ref double lo, ref double hi)
        {
            double dv = v - c[1];
            if (Math.Abs(dv) > d) return;
            double half = Math.Sqrt(Math.Max(0, d * d - dv * dv));
            if (c[0] - half < lo) lo = c[0] - half;
            if (c[0] + half > hi) hi = c[0] + half;
        }

        /// <summary>Overlapping or touching intervals joined, ascending.</summary>
        public static List<double[]> Merge(List<double[]> intervals)
        {
            var outp = new List<double[]>();
            if (intervals == null) return outp;
            foreach (double[] iv in intervals.Where(x => x != null && x.Length == 2 && Finite(x[0]) && Finite(x[1]) && x[1] >= x[0])
                                              .OrderBy(x => x[0]))
            {
                if (outp.Count > 0 && iv[0] <= outp[outp.Count - 1][1] + 1e-9)
                {
                    if (iv[1] > outp[outp.Count - 1][1]) outp[outp.Count - 1][1] = iv[1];
                }
                else outp.Add(new[] { iv[0], iv[1] });
            }
            return outp;
        }

        /// <summary>Each interval widened by <paramref name="byMm"/> at both ends, then merged.</summary>
        public static List<double[]> Widen(List<double[]> intervals, double byMm)
        {
            double b = Finite(byMm) && byMm > 0 ? byMm : 0;
            return Merge(intervals.Select(x => new[] { x[0] - b, x[1] + b }).ToList());
        }

        /// <summary>The intervals cut down to [<paramref name="u0"/>, <paramref name="u1"/>], empties dropped.</summary>
        public static List<double[]> Clip(List<double[]> intervals, double u0, double u1)
        {
            var outp = new List<double[]>();
            foreach (double[] x in intervals)
            {
                double a = Math.Max(x[0], u0), b = Math.Min(x[1], u1);
                if (b > a + 1e-9) outp.Add(new[] { a, b });
            }
            return outp;
        }

        /// <summary>What is left of [<paramref name="u0"/>, <paramref name="u1"/>] once the intervals are removed.</summary>
        public static List<double[]> Complement(List<double[]> removed, double u0, double u1)
        {
            var outp = new List<double[]>();
            double at = u0;
            foreach (double[] x in Merge(removed))
            {
                if (x[1] <= u0 || x[0] >= u1) continue;
                if (x[0] > at) outp.Add(new[] { at, Math.Min(x[0], u1) });
                at = Math.Max(at, x[1]);
            }
            if (at < u1) outp.Add(new[] { at, u1 });
            return outp;
        }

        /// <summary>Two interval lists that describe the same stretches, to a micron.</summary>
        public static bool SameIntervals(List<double[]> a, List<double[]> b)
        {
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
                if (Math.Abs(a[i][0] - b[i][0]) > 1e-6 || Math.Abs(a[i][1] - b[i][1]) > 1e-6) return false;
            return true;
        }

        // -------------------------------------------------- after the commit

        /// <summary>
        /// The drawn bar, at every position Revit computed, against the openings
        /// it was planned around. Same arithmetic as the plan, on the centreline
        /// Revit drew and the offsets Revit reports - so a bar the model put
        /// somewhere else is measured where it is.
        /// </summary>
        public static MatOpeningCheck CheckBars(MatOpeningContext ctx, IList<double[]> centrelineMm,
                                                IList<double> signedOffsetsMm, double toleranceMm)
        {
            var r = new MatOpeningCheck();
            if (ctx == null) { r.Why = "no opening context was carried on this rule."; return r; }
            if (centrelineMm == null || centrelineMm.Count < 2) { r.Why = "the bar's centreline was not available."; return r; }
            double[] along = RebarContainment.Unit(ctx.Along), across = RebarContainment.Unit(ctx.Across);
            if (along == null || across == null) { r.Why = "the component frame was not usable."; return r; }
            double tol = Finite(toleranceMm) && toleranceMm > 0 ? toleranceMm : 0;

            var offsets = new List<double>();
            if (signedOffsetsMm == null || signedOffsetsMm.Count == 0) offsets.Add(0);
            else offsets.AddRange(signedOffsetsMm);

            List<MatOpeningRegion> considered = ctx.Considered.ToList();
            double worst = 0;
            double? minGap = null;
            for (int i = 0; i < offsets.Count; i++)
            {
                double d = offsets[i];
                if (!Finite(d)) { r.Why = "bar position " + i + " was not a finite offset."; return r; }
                var moved = new List<double[]>(centrelineMm.Count);
                foreach (double[] p in centrelineMm)
                    moved.Add(new[] { p[0] + across[0] * d, p[1] + across[1] * d, p[2] + across[2] * d });
                List<double[]> uv = ProjectUv(moved, along, across);
                double u0 = uv.Min(p => p[0]), u1 = uv.Max(p => p[0]);
                double v = uv.Average(p => p[1]);
                var verdict = new MatOpeningBarVerdict { Position = i, VMm = v };
                double overlap = 0;
                double? gap = null;
                foreach (MatOpeningRegion reg in considered)
                {
                    List<double[]> body = CrossingIntervals(reg.Uv, v, ctx.BarRadiusMm);
                    foreach (double[] x in Clip(body, u0, u1)) overlap = Math.Max(overlap, x[1] - x[0]);
                    foreach (double[] x in body)
                    {
                        // The gap from the bar to this stretch of void along u.
                        double g = x[0] > u1 ? x[0] - u1 : (x[1] < u0 ? u0 - x[1] : 0);
                        if (x[0] > u1 || x[1] < u0)
                            if (!gap.HasValue || g < gap.Value) gap = g;
                    }
                    if (ctx.Policy == StructuralMatOpenings.PolicyTrim)
                        foreach (double[] x in Clip(Widen(body, ctx.ClearanceMm), u0, u1))
                            if (x[1] - x[0] > tol) verdict.ShortOfClearance = true;
                }
                verdict.OverlapMm = overlap;
                verdict.CrossesAnOpening = overlap > tol;
                verdict.GapMm = gap;
                if (verdict.CrossesAnOpening) r.Crossing.Add(i);
                if (verdict.ShortOfClearance && !verdict.CrossesAnOpening) r.ShortOfClearance.Add(i);
                if (overlap > worst) worst = overlap;
                if (gap.HasValue && (!minGap.HasValue || gap.Value < minGap.Value)) minGap = gap;
                r.Verdicts.Add(verdict);
                r.PositionsTested++;
            }
            r.Evaluated = true;
            r.WorstOverlapMm = worst;
            r.MinGapMm = minGap;
            r.Why = r.Crossing.Count > 0
                ? r.Crossing.Count + " of " + r.PositionsTested + " drawn bar position(s) have their body over a " +
                  "considered opening; the worst overlap is " + Mm(worst) + "."
                : r.ShortOfClearance.Count > 0
                    ? r.ShortOfClearance.Count + " of " + r.PositionsTested + " drawn bar position(s) stop closer to an " +
                      "opening than the declared clearance of " + Mm(ctx.ClearanceMm) + "."
                    : "no drawn bar position crosses a considered opening" +
                      (ctx.Policy == StructuralMatOpenings.PolicyTrim ? " or stops inside the declared clearance." : ".");
            return r;
        }

        // ------------------------------------------------------------- vectors

        public static double[] Cross(double[] a, double[] b)
        {
            return new[]
            {
                a[1] * b[2] - a[2] * b[1],
                a[2] * b[0] - a[0] * b[2],
                a[0] * b[1] - a[1] * b[0]
            };
        }

        private static double[] AnyPerpendicular(double[] n)
        {
            double[] probe = Math.Abs(n[0]) < 0.9 ? new double[] { 1, 0, 0 } : new double[] { 0, 1, 0 };
            return RebarContainment.Unit(Cross(n, probe));
        }

        private static string Mm(double v)
        {
            return Math.Round(v, 3).ToString(CultureInfo.InvariantCulture) + " mm";
        }
    }
}
