using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

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
        /// The segments of these (one layer's) that a DECLARED figure rule leaves unclaimed: the loops that match
        /// it, with their chords. Without a rule nothing is a figure - a loop is then held for review, which is
        /// <see cref="Loops"/>'s business and the caller's, not this method's.
        /// </summary>
        public static HashSet<CadSegment> Figure(IList<CadSegment> segments, double toleranceMm,
                                                 CadClosedFigureRule rule, out int rings, out int chords)
        {
            var figure = new HashSet<CadSegment>();
            rings = 0;
            chords = 0;
            if (rule == null || !rule.Declares) return figure;
            foreach (CadLoop loop in Loops(segments, toleranceMm))
            {
                JObject why;
                if (!loop.MatchesDeclaredFigure(rule, out why)) continue;
                rings++;
                chords += loop.Chords.Count;
                foreach (CadSegment s in loop.All) figure.Add(s);
            }
            return figure;
        }

        /// <summary>One closed loop of a layer's line work, and what the drawing says about what it MEANS.</summary>
        public sealed class CadLoop
        {
            /// <summary>The loop itself, in walk order.</summary>
            public List<CadSegment> Boundary = new List<CadSegment>();
            /// <summary>Segments joining two of its nodes THROUGH its inside.</summary>
            public List<CadSegment> Chords = new List<CadSegment>();
            /// <summary>Every segment of the loop and its chords.</summary>
            public IEnumerable<CadSegment> All => Boundary.Concat(Chords);
            /// <summary>True when the whole loop came from one closed polyline, which is how a box is usually drawn.</summary>
            public bool OneClosedPolyline;
            public double PerimeterMm => Boundary.Sum(s => s.PlanLength);
            /// <summary>How many segments of the same layer touch this loop without being part of it or its chords.</summary>
            public int AttachedRuns;

            /// <summary>
            /// A CHORD IS NOT A MEANING. It is a property of the line work - a closed circuit with an interior
            /// connection has one too - so it never decides anything by itself. It is one of the facts a DECLARED
            /// rule may ask about (<see cref="MatchesDeclaredFigure"/>); nothing else in this class judges.
            /// </summary>
            public bool HasChords => Chords.Count > 0;

            /// <summary>
            /// Whether this loop matches what the CALLER declared a figure looks like. Every named threshold must
            /// hold; the reply says which ones did. No rule, no match: a loop is then held, never excluded.
            /// </summary>
            public bool MatchesDeclaredFigure(CadClosedFigureRule rule, out JObject why)
            {
                why = new JObject();
                if (rule == null || !rule.Declares) return false;
                bool ok = true;
                if (rule.MaxPerimeterMm.HasValue)
                {
                    bool hit = PerimeterMm <= rule.MaxPerimeterMm.Value;
                    why["perimeter_mm"] = Math.Round(PerimeterMm, 1);
                    why["perimeter_within"] = hit;
                    ok &= hit;
                }
                if (rule.MinChords.HasValue)
                {
                    bool hit = Chords.Count >= rule.MinChords.Value;
                    why["chords"] = Chords.Count;
                    why["chords_enough"] = hit;
                    ok &= hit;
                }
                if (rule.MaxEdges.HasValue)
                {
                    bool hit = Boundary.Count <= rule.MaxEdges.Value;
                    why["edges"] = Boundary.Count;
                    why["edges_within"] = hit;
                    ok &= hit;
                }
                if (rule.NothingElseAttached)
                {
                    bool hit = AttachedRuns == 0;
                    why["attached_runs"] = AttachedRuns;
                    why["nothing_else_attached"] = hit;
                    ok &= hit;
                }
                if (rule.OneClosedPolyline.HasValue)
                {
                    bool hit = OneClosedPolyline == rule.OneClosedPolyline.Value;
                    why["one_closed_polyline"] = OneClosedPolyline;
                    why["polyline_as_declared"] = hit;
                    ok &= hit;
                }
                why["matches"] = ok;
                return ok;
            }

            public JObject ToJson() => new JObject
            {
                ["edges"] = Boundary.Count,
                ["chords"] = Chords.Count,
                ["attached_runs"] = AttachedRuns,
                ["perimeter_mm"] = Math.Round(PerimeterMm, 1),
                ["one_closed_polyline"] = OneClosedPolyline,
                ["at_mm"] = Boundary.Count > 0
                    ? new JArray(Math.Round(Boundary[0].A.X, 1), Math.Round(Boundary[0].A.Y, 1))
                    : new JArray(),
                ["means"] = "a closed loop of this layer's line work, with the facts a declared rule can judge it " +
                            "by. Nothing here is a meaning: a chord, a perimeter or a polyline say what was DRAWN, " +
                            "and only geometry.closed_loops (review | runs | figures_by_rule + closed_figure) says " +
                            "what this reading does with it."
            };
        }

        /// <summary>
        /// The closed loops of one layer's segments, from the segments THEMSELVES - so the same figure drawn as one
        /// closed polyline or as four separate lines reads the same. Leaves (branches) are stripped first: what
        /// remains carries every cycle. Bounded: a component with more than <paramref name="maxEdges"/> segments is
        /// left alone and reported by the caller rather than walked.
        /// </summary>
        public static List<CadLoop> Loops(IList<CadSegment> segments, double toleranceMm, int maxEdges = 64)
        {
            var found = new List<CadLoop>();
            if (segments == null) return found;
            List<CadSegment> edges = segments.Where(s => s != null && s.PlanLength > 1e-9).ToList();
            if (edges.Count < 3) return found;
            double tol = Math.Max(toleranceMm, VertexToleranceMm);

            var nodes = new List<CadPoint>();
            Func<CadPoint, int> nodeOf = p =>
            {
                for (int i = 0; i < nodes.Count; i++) if (nodes[i].PlanDistanceTo(p) <= tol) return i;
                nodes.Add(p);
                return nodes.Count - 1;
            };
            var ends = edges.Select(s => Tuple.Create(nodeOf(s.A), nodeOf(s.B))).ToList();
            var alive = Enumerable.Repeat(true, edges.Count).ToArray();
            for (int i = 0; i < edges.Count; i++) if (ends[i].Item1 == ends[i].Item2) alive[i] = false;

            // strip leaves until every remaining node has two or more edges: what is left is where cycles are
            bool stripped = true;
            while (stripped)
            {
                stripped = false;
                var degree = new int[nodes.Count];
                for (int i = 0; i < edges.Count; i++)
                    if (alive[i]) { degree[ends[i].Item1]++; degree[ends[i].Item2]++; }
                for (int i = 0; i < edges.Count; i++)
                {
                    if (!alive[i]) continue;
                    if (degree[ends[i].Item1] <= 1 || degree[ends[i].Item2] <= 1) { alive[i] = false; stripped = true; }
                }
            }
            var core = Enumerable.Range(0, edges.Count).Where(i => alive[i]).ToList();
            if (core.Count < 3) return found;

            // components of what is left
            var seen = new HashSet<int>();
            foreach (int start in core)
            {
                if (seen.Contains(start)) continue;
                var component = new List<int>();
                var queue = new Queue<int>();
                queue.Enqueue(start);
                seen.Add(start);
                while (queue.Count > 0)
                {
                    int e = queue.Dequeue();
                    component.Add(e);
                    foreach (int other in core)
                    {
                        if (seen.Contains(other)) continue;
                        if (ends[other].Item1 == ends[e].Item1 || ends[other].Item1 == ends[e].Item2 ||
                            ends[other].Item2 == ends[e].Item1 || ends[other].Item2 == ends[e].Item2)
                        { seen.Add(other); queue.Enqueue(other); }
                    }
                }
                if (component.Count < 3 || component.Count > maxEdges) continue;
                CadLoop loop = Walk(component, edges, ends, nodes);
                if (loop == null) continue;
                // WHAT ELSE TOUCHES IT. A box fed by a duct and a ring main with branches both have this; it is a
                // fact a declared rule may ask about, and never a verdict of its own.
                var own = new HashSet<CadSegment>(loop.All);
                foreach (CadSegment other in edges)
                {
                    if (own.Contains(other)) continue;
                    if (loop.Boundary.Any(e => Touches(e, other.A, tol) || Touches(e, other.B, tol)))
                        loop.AttachedRuns++;
                }
                found.Add(loop);
            }
            return found;
        }

        /// <summary>The outer boundary of one component, walked by always taking the sharpest right turn, and its chords.</summary>
        private static CadLoop Walk(List<int> component, List<CadSegment> edges, List<Tuple<int, int>> ends, List<CadPoint> nodes)
        {
            int startNode = component.SelectMany(e => new[] { ends[e].Item1, ends[e].Item2 }).Distinct()
                .OrderBy(n => nodes[n].X).ThenBy(n => nodes[n].Y).First();
            var boundary = new List<int>();
            int node = startNode, previous = -1, guard = component.Count * 2 + 4;
            // the walk turns clockwise from the REVERSED incoming direction, which is what keeps it on the outer
            // boundary; the first step pretends it arrived from below, so it starts looking downwards
            double heading = -Math.PI / 2;
            while (guard-- > 0)
            {
                int bestEdge = -1, bestNode = -1;
                double bestTurn = double.MaxValue;
                foreach (int e in component)
                {
                    if (e == previous) continue;
                    int a = ends[e].Item1, b = ends[e].Item2;
                    if (a != node && b != node) continue;
                    int other = a == node ? b : a;
                    double dir = Math.Atan2(nodes[other].Y - nodes[node].Y, nodes[other].X - nodes[node].X);
                    double turn = heading - dir;
                    while (turn <= 0) turn += 2 * Math.PI;
                    while (turn > 2 * Math.PI) turn -= 2 * Math.PI;
                    if (turn < bestTurn) { bestTurn = turn; bestEdge = e; bestNode = other; }
                }
                if (bestEdge < 0) return null;
                boundary.Add(bestEdge);
                heading = Math.Atan2(nodes[node].Y - nodes[bestNode].Y, nodes[node].X - nodes[bestNode].X);
                previous = bestEdge;
                node = bestNode;
                if (node == startNode) break;
            }
            if (boundary.Count < 3 || node != startNode) return null;
            var loop = new CadLoop();
            loop.Boundary.AddRange(boundary.Select(i => edges[i]));
            var ring = boundary.Select(i => edges[i]).ToList();
            List<CadPoint> corners = BoundaryPoints(boundary, ends, nodes);
            foreach (int e in component)
            {
                if (boundary.Contains(e)) continue;
                if (Inside(corners, edges[e].Midpoint)) loop.Chords.Add(edges[e]);
            }
            loop.OneClosedPolyline = ring.All(s => s.ClosedRing && s.SourceCurveId != null) &&
                                     ring.Select(s => s.SourceCurveId).Distinct(StringComparer.Ordinal).Count() == 1;
            return loop;
        }

        private static List<CadPoint> BoundaryPoints(List<int> boundary, List<Tuple<int, int>> ends, List<CadPoint> nodes)
        {
            var pts = new List<CadPoint>();
            for (int i = 0; i < boundary.Count; i++)
            {
                int e = boundary[i], next = boundary[(i + 1) % boundary.Count];
                int shared = (ends[e].Item1 == ends[next].Item1 || ends[e].Item1 == ends[next].Item2) ? ends[e].Item1 : ends[e].Item2;
                pts.Add(nodes[shared]);
            }
            return pts;
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
                if (rule?.Geometry == null || rule.Geometry.Source != CadGeometrySource.SingleLines) continue;
                if (rule.Geometry.ClosedLoops != CadClosedLoopPolicy.FiguresByRule) continue;
                int r, c;
                foreach (CadSegment s in Figure(layer.ToList(), set.PointToleranceMm, rule.Geometry.ClosedFigure, out r, out c))
                    drop.Add(s);
                rings += r;
                chords += c;
            }
            leftOut = drop.Count;
            return drop.Count == 0 ? segments : segments.Where(s => !drop.Contains(s)).ToList();
        }

        /// <summary>Whether a point lies on a segment (its ends included), within the tolerance.</summary>
        private static bool Touches(CadSegment s, CadPoint p, double tol)
        {
            double dx = s.B.X - s.A.X, dy = s.B.Y - s.A.Y;
            double len2 = dx * dx + dy * dy;
            if (len2 <= 1e-12) return s.A.PlanDistanceTo(p) <= tol;
            double t = Math.Max(0, Math.Min(1, ((p.X - s.A.X) * dx + (p.Y - s.A.Y) * dy) / len2));
            double qx = s.A.X + t * dx, qy = s.A.Y + t * dy;
            return Math.Sqrt((p.X - qx) * (p.X - qx) + (p.Y - qy) * (p.Y - qy)) <= tol;
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
