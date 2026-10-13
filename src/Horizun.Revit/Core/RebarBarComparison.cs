// -----------------------------------------------------------------------------
// Horizun Core - original Horizun code. Revit-free.
//
// TWO SETS OF BARS, COMPARED BAR BY BAR (acero 3.0, L10; horizun_query_structure
// mode=compare_bars).
//
// mode=reconcile compares totals per mark with a schedule. This answers a different
// question: is every bar of A ALSO in B - the same steel in the same place - and which
// bars only one side has. A detailer's cage against a generated one, a revision against
// the issue before it.
//
// A BAR is one position of a set: the centreline Revit draws for it (hooks and bends
// included), in millimetres. Two bars can only be the same bar when their NOMINAL
// diameters are equal; the bar type's NAME is reported and never compared, because two
// models routinely name one bar two ways.
//
// THE DISTANCE between two bars is the mean distance from each one's centreline to the
// other's, taken both ways and the larger kept: each polyline is sampled at Samples
// points evenly spaced along its length, each sample's distance to the nearest segment
// of the other polyline is measured in space, and the samples are averaged. Taking both
// directions is what stops a short bar lying on a long one from reading as identical; the
// LENGTH difference (each centreline's own length) is reported and judged separately.
//
// THE TOLERANCES ARE THE CALLER'S AND HAVE NO DEFAULTS: identical_mm, near_mm, length_mm.
//   identical  mean distance <= identical_mm AND |length difference| <= length_mm
//   near       mean distance <= near_mm, and not identical
//   unmatched  no bar of B of the same diameter within near_mm that another bar of A
//              did not already take
//
// ONE-TO-ONE, GREEDY. Every pair of same-diameter bars within near_mm is a candidate.
// Pairs that would be IDENTICAL are assigned first, then the rest; inside each class the
// smaller distance, then the smaller length difference, then the ids in ordinal order.
// A pair is taken when neither bar is taken yet. Greedy is not an optimal assignment, and
// says so: it can leave a bar unmatched that a global assignment would have paired. It is
// deterministic, and an identical pair is never lost to a nearer pair of another length.
//
// THE CANDIDATE SEARCH is a uniform grid over B's bounding boxes; a pair whose boxes are
// further apart than near_mm cannot have a mean distance within near_mm (at least one
// sample would have to be within it), so nothing is pruned that could have matched.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public static class RebarBarComparison
    {
        public const string Identical = "identical", Near = "near", Unmatched = "unmatched", Unreadable = "unreadable";
        public const string DifferencesFound = "differences_found", Incomplete = "incomplete";

        public const string CodeTolerance = "bar_tolerances_not_declared";
        public const string CodeBars = "compare_bars_not_usable";

        /// <summary>Samples per centreline, evenly spaced along its length, ends included.</summary>
        public const int Samples = 33;

        /// <summary>Two nominal diameters within this are one diameter: millimetres read off feet, not a tolerance.</summary>
        public const double DiameterEpsilonMm = 0.001;

        private const double GridCellMm = 500.0;

        public static readonly string[] ToleranceKeys = { "identical_mm", "near_mm", "length_mm" };
        public static readonly string[] BarKeys = { "id", "set_id", "position", "bar_type", "nominal_diameter_mm", "centreline_points_mm", "why" };

        public sealed class Tolerances
        {
            public double IdenticalMm, NearMm, LengthMm;
        }

        public sealed class Bar
        {
            public string Id;
            public long? SetId;
            public int? Position;
            public string BarType;
            public double? DiameterMm;
            /// <summary>Null when the bar could not be read; Why then says why.</summary>
            public List<double[]> Points;
            public string Why;
            public bool Readable => Points != null && DiameterMm.HasValue;
            internal double Length;
            internal List<double[]> Sampled;
            internal double[] Min, Max;
        }

        // ------------------------------------------------------------------ reading

        public static Tolerances ReadTolerances(JToken t, out string code, out string error)
        {
            code = null; error = null;
            var o = t as JObject;
            if (o == null) return BadTol(out code, out error, "bar_tolerances is required: { identical_mm, near_mm, length_mm }. " +
                                                              "Whether two bars are the same bar is a decision, not a constant.");
            foreach (JProperty p in o.Properties())
                if (Array.IndexOf(ToleranceKeys, p.Name) < 0)
                    return BadTol(out code, out error, "bar_tolerances." + p.Name + " is not a known key (" + string.Join(", ", ToleranceKeys) + ").");
            var r = new Tolerances();
            double v;
            if (!Number(o["identical_mm"], out v) || v < 0) return BadTol(out code, out error, "bar_tolerances.identical_mm is required: a number of millimetres, zero or more.");
            r.IdenticalMm = v;
            if (!Number(o["near_mm"], out v) || v < 0) return BadTol(out code, out error, "bar_tolerances.near_mm is required: a number of millimetres, zero or more.");
            r.NearMm = v;
            if (!Number(o["length_mm"], out v) || v < 0) return BadTol(out code, out error, "bar_tolerances.length_mm is required: a number of millimetres, zero or more.");
            r.LengthMm = v;
            if (r.NearMm < r.IdenticalMm)
                return BadTol(out code, out error, "bar_tolerances.near_mm (" + Fmt(r.NearMm) + ") is below identical_mm (" + Fmt(r.IdenticalMm) +
                                                   "): a bar identical to another is near it too.");
            return r;
        }

        /// <summary>
        /// Bar rows in the shape mode=compare_bars builds from the model: id, set_id, position,
        /// bar_type, nominal_diameter_mm, centreline_points_mm ([x,y,z] mm, or null with a why).
        /// Null, with code and error, when a row is not usable; nothing is guessed.
        /// </summary>
        public static List<Bar> ReadBars(JArray rows, string side, out string code, out string error)
        {
            code = null; error = null;
            if (rows == null || rows.Count == 0) return BadBars(out code, out error, "side " + side + " has no bar to compare.");
            var bars = new List<Bar>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < rows.Count; i++)
            {
                string at = "side " + side + ", bar[" + i + "]";
                var o = rows[i] as JObject;
                if (o == null) return BadBars(out code, out error, at + " must be an object.");
                foreach (JProperty p in o.Properties())
                    if (Array.IndexOf(BarKeys, p.Name) < 0) return BadBars(out code, out error, at + "." + p.Name + " is not a known key.");
                JToken id = o["id"];
                if (id == null || id.Type != JTokenType.String || string.IsNullOrWhiteSpace((string)id))
                    return BadBars(out code, out error, at + " needs an id.");
                var b = new Bar { Id = (string)id };
                if (!seen.Add(b.Id)) return BadBars(out code, out error, at + ": the id '" + b.Id + "' is used twice on one side.");
                if (o["set_id"] != null && o["set_id"].Type != JTokenType.Null)
                {
                    if (o["set_id"].Type != JTokenType.Integer) return BadBars(out code, out error, at + ".set_id must be an element id.");
                    b.SetId = (long)o["set_id"];
                }
                if (o["position"] != null && o["position"].Type != JTokenType.Null)
                {
                    if (o["position"].Type != JTokenType.Integer || (long)o["position"] < 0) return BadBars(out code, out error, at + ".position must be a whole number, zero or more.");
                    b.Position = (int)(long)o["position"];
                }
                JToken bt = o["bar_type"];
                if (bt != null && bt.Type != JTokenType.Null)
                {
                    if (bt.Type != JTokenType.String) return BadBars(out code, out error, at + ".bar_type must be a string.");
                    b.BarType = (string)bt;
                }
                JToken d = o["nominal_diameter_mm"];
                if (d != null && d.Type != JTokenType.Null)
                {
                    if (!Number(d, out double dia) || dia <= 0) return BadBars(out code, out error, at + ".nominal_diameter_mm must be a positive number.");
                    b.DiameterMm = dia;
                }
                b.Why = o["why"] != null && o["why"].Type == JTokenType.String ? (string)o["why"] : null;
                JToken pts = o["centreline_points_mm"];
                if (pts != null && pts.Type != JTokenType.Null)
                {
                    var arr = pts as JArray;
                    if (arr == null) return BadBars(out code, out error, at + ".centreline_points_mm must be a list of [x, y, z].");
                    var list = new List<double[]>();
                    foreach (JToken q in arr)
                    {
                        var t = q as JArray;
                        if (t == null || t.Count != 3) return BadBars(out code, out error, at + ".centreline_points_mm holds a point that is not [x, y, z].");
                        var p = new double[3];
                        for (int k = 0; k < 3; k++)
                            if (!Number(t[k], out p[k])) return BadBars(out code, out error, at + ".centreline_points_mm holds a coordinate that is not a number.");
                        list.Add(p);
                    }
                    if (list.Count >= 2) b.Points = list;
                    else if (b.Why == null) b.Why = "the centreline has fewer than two points.";
                }
                if (b.Points == null && b.Why == null) b.Why = "no centreline was given for this bar.";
                if (b.Points != null && !b.DiameterMm.HasValue) b.Why = "no nominal diameter was read, so no bar of the other side can be called the same bar.";
                bars.Add(b);
            }
            return bars;
        }

        // ------------------------------------------------------------------ comparing

        private sealed class Pair
        {
            public int A, B;
            public double Distance, MaxDistance, LengthDiff;
            public bool Identical;
        }

        public static JObject Compare(IList<Bar> a, IList<Bar> b, Tolerances tol, int offset, int maxRows)
        {
            foreach (Bar x in a.Concat(b)) Prepare(x);

            // THE CANDIDATES: B's readable bars in a grid by bounding box.
            var grid = new Dictionary<long, List<int>>();
            for (int j = 0; j < b.Count; j++)
            {
                if (!b[j].Readable) continue;
                foreach (long key in Cells(b[j].Min, b[j].Max, 0)) Bucket(grid, key).Add(j);
            }

            List<double> diametersB = b.Where(y => y.Readable).Select(y => y.DiameterMm.Value).Distinct().ToList();
            var pairs = new List<Pair>();
            var nearestOf = new Dictionary<int, Pair>();
            var sameDiameterExists = new HashSet<int>();
            for (int i = 0; i < a.Count; i++)
            {
                Bar x = a[i];
                if (!x.Readable) continue;
                var tried = new HashSet<int>();
                foreach (List<int> here in Candidates(grid, x, tol.NearMm, b.Count))
                {
                    foreach (int j in here)
                    {
                        if (!tried.Add(j)) continue;
                        Bar y = b[j];
                        if (!y.Readable) continue;
                        if (Math.Abs(x.DiameterMm.Value - y.DiameterMm.Value) > DiameterEpsilonMm) continue;
                        sameDiameterExists.Add(i);
                        if (BoxGap(x, y) > tol.NearMm) continue;
                        double maxD;
                        double d = Distance(x, y, out maxD);
                        double dl = Math.Abs(x.Length - y.Length);
                        var p = new Pair { A = i, B = j, Distance = d, MaxDistance = maxD, LengthDiff = dl,
                                           Identical = d <= tol.IdenticalMm && dl <= tol.LengthMm };
                        Pair best;
                        if (!nearestOf.TryGetValue(i, out best) || Better(p, best, a, b)) nearestOf[i] = p;
                        if (d <= tol.NearMm) pairs.Add(p);
                    }
                }
                if (!sameDiameterExists.Contains(i) && diametersB.Any(d => Math.Abs(x.DiameterMm.Value - d) <= DiameterEpsilonMm))
                    sameDiameterExists.Add(i);
            }

            // ONE TO ONE: identical pairs first, then the rest, each by distance.
            pairs.Sort((p, q) =>
            {
                int c = q.Identical.CompareTo(p.Identical);
                if (c != 0) return c;
                c = p.Distance.CompareTo(q.Distance); if (c != 0) return c;
                c = p.LengthDiff.CompareTo(q.LengthDiff); if (c != 0) return c;
                c = string.CompareOrdinal(a[p.A].Id, a[q.A].Id); if (c != 0) return c;
                return string.CompareOrdinal(b[p.B].Id, b[q.B].Id);
            });
            var matchOfA = new Dictionary<int, Pair>();
            var matchOfB = new Dictionary<int, int>();
            foreach (Pair p in pairs)
            {
                if (matchOfA.ContainsKey(p.A) || matchOfB.ContainsKey(p.B)) continue;
                matchOfA[p.A] = p;
                matchOfB[p.B] = p.A;
            }

            // THE ROWS, in A's order.
            var rows = new List<JObject>();
            int identical = 0, near = 0, unmatchedA = 0, unreadableA = 0;
            for (int i = 0; i < a.Count; i++)
            {
                Bar x = a[i];
                JObject row = Describe(x);
                if (!x.Readable)
                {
                    row["verdict"] = Unreadable; row["why"] = x.Why; unreadableA++;
                    rows.Add(row); continue;
                }
                row["length_mm"] = R(x.Length);
                Pair m;
                if (matchOfA.TryGetValue(i, out m))
                {
                    row["verdict"] = m.Identical ? Identical : Near;
                    if (m.Identical) identical++; else near++;
                    row["match"] = Ref(b[m.B]);
                    row["mean_distance_mm"] = R(m.Distance);
                    row["max_distance_mm"] = R(m.MaxDistance);
                    row["length_difference_mm"] = R(m.LengthDiff);
                    row["length_within_tolerance"] = m.LengthDiff <= tol.LengthMm;
                }
                else
                {
                    row["verdict"] = Unmatched; unmatchedA++;
                    row["match"] = JValue.CreateNull();
                    Pair n = Nearest(i, a, b, nearestOf.TryGetValue(i, out Pair seed) ? seed : null);
                    if (n != null)
                    {
                        var nearest = Ref(b[n.B]);
                        nearest["mean_distance_mm"] = R(n.Distance);
                        nearest["length_difference_mm"] = R(n.LengthDiff);
                        int other;
                        nearest["matched_to"] = matchOfB.TryGetValue(n.B, out other) ? (JToken)a[other].Id : JValue.CreateNull();
                        row["nearest"] = nearest;
                        row["why"] = n.Distance > tol.NearMm
                            ? "the nearest bar of B of the same diameter is " + Fmt(n.Distance) + " mm away on average, beyond near_mm."
                            : "the bar of B it is nearest was paired with another bar of A.";
                    }
                    else
                    {
                        row["nearest"] = JValue.CreateNull();
                        row["why"] = sameDiameterExists.Contains(i)
                            ? "no bar of B of the same diameter lies within near_mm of this one."
                            : "B has no readable bar of this nominal diameter (" + Fmt(x.DiameterMm.Value) + " mm).";
                    }
                }
                rows.Add(row);
            }

            var onlyB = new List<JObject>();
            int unmatchedB = 0, unreadableB = 0;
            for (int j = 0; j < b.Count; j++)
            {
                if (matchOfB.ContainsKey(j)) continue;
                JObject row = Describe(b[j]);
                if (!b[j].Readable) { row["verdict"] = Unreadable; row["why"] = b[j].Why; unreadableB++; }
                else { row["verdict"] = Unmatched; row["length_mm"] = R(b[j].Length); unmatchedB++; }
                onlyB.Add(row);
            }

            string verdict = near + unmatchedA + unmatchedB > 0 ? DifferencesFound
                           : unreadableA + unreadableB > 0 ? Incomplete : Identical;
            int total = Math.Max(rows.Count, onlyB.Count);
            int next = offset + maxRows;
            return new JObject
            {
                ["verdict"] = verdict,
                ["summary"] = new JObject
                {
                    ["bars_a"] = a.Count, ["bars_b"] = b.Count,
                    ["identical"] = identical, ["near"] = near,
                    ["unmatched_a"] = unmatchedA, ["unmatched_b"] = unmatchedB,
                    ["unreadable_a"] = unreadableA, ["unreadable_b"] = unreadableB
                },
                ["tolerances"] = new JObject
                {
                    ["identical_mm"] = tol.IdenticalMm, ["near_mm"] = tol.NearMm, ["length_mm"] = tol.LengthMm
                },
                ["rows"] = new JArray(rows.Skip(offset).Take(maxRows)),
                ["unmatched_b"] = new JArray(onlyB.Skip(offset).Take(maxRows)),
                ["offset"] = offset,
                ["next_offset"] = next < total ? (JToken)next : JValue.CreateNull(),
                ["paging_means"] =
                    "rows (every bar of A, in order) and unmatched_b (every bar of B no bar of A was paired with) are " +
                    "paged together by offset and max_rows; the summary and the verdict are over every bar.",
                ["verdict_means"] =
                    "identical only when every bar of A was paired with a bar of B as identical, no bar of B was left " +
                    "over and every bar on both sides was read; incomplete when nothing disagreed and some bar could " +
                    "not be read; differences_found otherwise.",
                ["distance_means"] =
                    "the mean distance between two centrelines, both ways and the larger kept: each is sampled at " +
                    Samples + " points evenly along its length and each sample measured to the nearest segment of the " +
                    "other. Centrelines are the bars as Revit draws them, hooks and bends included. max_distance_mm is " +
                    "the largest single sample.",
                ["matching"] =
                    "one-to-one, greedy: same nominal diameter only; pairs within near_mm; identical pairs assigned " +
                    "first, then by mean distance, length difference and ids. Greedy is deterministic and not a global " +
                    "optimum: it can leave unmatched a bar an optimal assignment would pair."
            };
        }

        /// <summary>
        /// The nearest same-diameter bar of B to an UNMATCHED bar of A, however far: exact,
        /// by branch and bound. No sample of a centreline is nearer the other bar than the
        /// distance between their boxes, so the mean cannot be either; B is walked in order of
        /// that distance and the walk stops once it exceeds the best mean found.
        /// </summary>
        private static Pair Nearest(int i, IList<Bar> a, IList<Bar> b, Pair seed)
        {
            Bar x = a[i];
            Pair best = seed;
            var order = new List<KeyValuePair<double, int>>();
            for (int j = 0; j < b.Count; j++)
            {
                Bar y = b[j];
                if (!y.Readable || Math.Abs(x.DiameterMm.Value - y.DiameterMm.Value) > DiameterEpsilonMm) continue;
                order.Add(new KeyValuePair<double, int>(BoxDistance(x, y), j));
            }
            order.Sort((p, q) => p.Key != q.Key ? p.Key.CompareTo(q.Key) : p.Value.CompareTo(q.Value));
            foreach (KeyValuePair<double, int> kv in order)
            {
                if (best != null && kv.Key > best.Distance) break;
                Bar y = b[kv.Value];
                double maxD;
                double d = Distance(x, y, out maxD);
                var p = new Pair { A = i, B = kv.Value, Distance = d, MaxDistance = maxD, LengthDiff = Math.Abs(x.Length - y.Length) };
                if (best == null || Better(p, best, a, b)) best = p;
            }
            return best;
        }

        // ------------------------------------------------------------------ geometry

        /// <summary>The Euclidean distance between two boxes - zero when they overlap.</summary>
        private static double BoxDistance(Bar x, Bar y)
        {
            double s = 0;
            for (int k = 0; k < 3; k++)
            {
                double g = Math.Max(0, Math.Max(x.Min[k] - y.Max[k], y.Min[k] - x.Max[k]));
                s += g * g;
            }
            return Math.Sqrt(s);
        }

        private static void Prepare(Bar x)
        {
            if (!x.Readable || x.Sampled != null) return;
            x.Length = Length(x.Points);
            x.Sampled = Resample(x.Points, x.Length, Samples);
            x.Min = new[] { double.MaxValue, double.MaxValue, double.MaxValue };
            x.Max = new[] { double.MinValue, double.MinValue, double.MinValue };
            foreach (double[] p in x.Points)
                for (int k = 0; k < 3; k++) { x.Min[k] = Math.Min(x.Min[k], p[k]); x.Max[k] = Math.Max(x.Max[k], p[k]); }
        }

        private static double Length(List<double[]> pts)
        {
            double s = 0;
            for (int i = 1; i < pts.Count; i++) s += Dist(pts[i - 1], pts[i]);
            return s;
        }

        private static List<double[]> Resample(List<double[]> pts, double length, int n)
        {
            var o = new List<double[]>(n);
            if (length <= 0) { for (int k = 0; k < n; k++) o.Add(pts[0]); return o; }
            int seg = 1;
            double walked = 0;
            for (int k = 0; k < n; k++)
            {
                double s = length * k / (n - 1);
                while (seg < pts.Count - 1 && walked + Dist(pts[seg - 1], pts[seg]) < s)
                {
                    walked += Dist(pts[seg - 1], pts[seg]);
                    seg++;
                }
                double l = Dist(pts[seg - 1], pts[seg]);
                double t = l <= 0 ? 0 : Math.Min(1.0, Math.Max(0.0, (s - walked) / l));
                double[] p0 = pts[seg - 1], p1 = pts[seg];
                o.Add(new[] { p0[0] + (p1[0] - p0[0]) * t, p0[1] + (p1[1] - p0[1]) * t, p0[2] + (p1[2] - p0[2]) * t });
            }
            return o;
        }

        private static double Distance(Bar x, Bar y, out double max)
        {
            double maxXY, maxYX;
            double xy = Directed(x.Sampled, y.Points, out maxXY);
            double yx = Directed(y.Sampled, x.Points, out maxYX);
            max = Math.Max(maxXY, maxYX);
            return Math.Max(xy, yx);
        }

        private static double Directed(List<double[]> samples, List<double[]> poly, out double max)
        {
            double sum = 0; max = 0;
            foreach (double[] p in samples)
            {
                double best = double.MaxValue;
                for (int i = 1; i < poly.Count; i++) best = Math.Min(best, PointToSegment(p, poly[i - 1], poly[i]));
                sum += best;
                max = Math.Max(max, best);
            }
            return sum / samples.Count;
        }

        public static double PointToSegment(double[] p, double[] a, double[] b)
        {
            double dx = b[0] - a[0], dy = b[1] - a[1], dz = b[2] - a[2];
            double len2 = dx * dx + dy * dy + dz * dz;
            double t = len2 <= 0 ? 0 : ((p[0] - a[0]) * dx + (p[1] - a[1]) * dy + (p[2] - a[2]) * dz) / len2;
            t = Math.Max(0, Math.Min(1, t));
            double qx = a[0] + dx * t - p[0], qy = a[1] + dy * t - p[1], qz = a[2] + dz * t - p[2];
            return Math.Sqrt(qx * qx + qy * qy + qz * qz);
        }

        private static double Dist(double[] p, double[] q)
        {
            double dx = q[0] - p[0], dy = q[1] - p[1], dz = q[2] - p[2];
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        /// <summary>The largest per-axis gap between two boxes - zero when they overlap on every axis.</summary>
        private static double BoxGap(Bar x, Bar y)
        {
            double g = 0;
            for (int k = 0; k < 3; k++) g = Math.Max(g, Math.Max(x.Min[k] - y.Max[k], y.Min[k] - x.Max[k]));
            return g;
        }

        private static IEnumerable<long> Cells(double[] min, double[] max, double grow)
        {
            long x0 = Cell(min[0] - grow), x1 = Cell(max[0] + grow);
            long y0 = Cell(min[1] - grow), y1 = Cell(max[1] + grow);
            long z0 = Cell(min[2] - grow), z1 = Cell(max[2] + grow);
            for (long i = x0; i <= x1; i++)
                for (long j = y0; j <= y1; j++)
                    for (long k = z0; k <= z1; k++)
                        yield return ((i & 0x1FFFFF) << 42) | ((j & 0x1FFFFF) << 21) | (k & 0x1FFFFF);
        }

        /// <summary>
        /// The grid buckets a bar's box, grown by near_mm, touches - or, when that box spans more
        /// cells than B has bars (a very large near_mm), every bar of B in one bucket: the same
        /// candidates, without walking an enormous empty grid.
        /// </summary>
        private static IEnumerable<List<int>> Candidates(Dictionary<long, List<int>> grid, Bar x, double grow, int countB)
        {
            double cells = 1;
            for (int k = 0; k < 3; k++) cells *= Cell(x.Max[k] + grow) - Cell(x.Min[k] - grow) + 1;
            if (cells > Math.Max(64, countB))
            {
                yield return Enumerable.Range(0, countB).ToList();
                yield break;
            }
            foreach (long key in Cells(x.Min, x.Max, grow))
            {
                List<int> here;
                if (grid.TryGetValue(key, out here)) yield return here;
            }
        }

        private static long Cell(double v) => (long)Math.Floor(v / GridCellMm);

        private static List<int> Bucket(Dictionary<long, List<int>> grid, long key)
        {
            List<int> l;
            if (!grid.TryGetValue(key, out l)) grid[key] = l = new List<int>();
            return l;
        }

        private static bool Better(Pair p, Pair q, IList<Bar> a, IList<Bar> b)
        {
            int c = p.Distance.CompareTo(q.Distance);
            if (c != 0) return c < 0;
            c = p.LengthDiff.CompareTo(q.LengthDiff);
            if (c != 0) return c < 0;
            return string.CompareOrdinal(b[p.B].Id, b[q.B].Id) < 0;
        }

        // ------------------------------------------------------------------ output

        private static JObject Describe(Bar x) => new JObject
        {
            ["id"] = x.Id,
            ["set_id"] = x.SetId.HasValue ? (JToken)x.SetId.Value : JValue.CreateNull(),
            ["position"] = x.Position.HasValue ? (JToken)x.Position.Value : JValue.CreateNull(),
            ["bar_type"] = x.BarType,
            ["nominal_diameter_mm"] = x.DiameterMm.HasValue ? (JToken)x.DiameterMm.Value : JValue.CreateNull()
        };

        private static JObject Ref(Bar y)
        {
            JObject o = Describe(y);
            o.Remove("nominal_diameter_mm");
            o["length_mm"] = R(y.Length);
            return o;
        }

        private static JToken R(double v) => new JValue(Math.Round(v, 3));

        private static string Fmt(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

        private static bool Number(JToken t, out double v)
        {
            v = 0;
            if (t == null || (t.Type != JTokenType.Integer && t.Type != JTokenType.Float)) return false;
            v = t.Value<double>();
            return !double.IsNaN(v) && !double.IsInfinity(v);
        }

        private static Tolerances BadTol(out string code, out string error, string why)
        {
            code = CodeTolerance; error = why + " Nothing was compared.";
            return null;
        }

        private static List<Bar> BadBars(out string code, out string error, string why)
        {
            code = CodeBars; error = why + " Nothing was compared.";
            return null;
        }
    }
}
