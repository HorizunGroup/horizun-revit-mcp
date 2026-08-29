// -----------------------------------------------------------------------------
// Horizun MCP — original Horizun code.
//
// Topology: turning a heap of segments into the shapes a building is made of.
//
// This is where a DWG stops being lines and starts being arguable. Every routine
// here answers a question a draughtsman answers by looking, and every one of
// them can be wrong - so each returns the EVIDENCE for its answer alongside it,
// and none of them decides anything a caller did not ask for.
//
// The four questions:
//
//   WHERE DOES THIS LINE END?   Gaps are the normal state of a real drawing.
//                               Nodes are quantized so near-coincident endpoints
//                               become one node, and the snap distance is the
//                               caller's declared tolerance, never a default
//                               chosen here.
//
//   IS THIS A CLOSED SHAPE?     A room, a slab and a shaft are all "a loop", and
//                               a loop that is one 3 mm gap away from closing is
//                               the single most common thing in a CAD plan.
//
//   IS THIS A WALL?             Two parallel lines, overlapping along their own
//                               direction, a plausible thickness apart. The
//                               centreline between them is what a model wants,
//                               and the thickness is measured rather than typed.
//
//   IS THIS THE SAME AS BEFORE? Two issues of the same drawing must be comparable
//                               without a handle, which is what makes an
//                               incremental update possible at all.
//
// Revit-free. Feed it CadSegments, get shapes and evidence back.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Horizun.Revit.Core
{
    /// <summary>A node in the snapped graph: one place where segment ends meet.</summary>
    public sealed class CadNode
    {
        public string Key { get; }
        public CadPoint Point { get; }
        public List<int> SegmentIndices { get; } = new List<int>();
        public CadNode(string key, CadPoint point) { Key = key; Point = point; }
        public int Degree => SegmentIndices.Count;
    }

    /// <summary>
    /// The snapped node graph, looked up BY DISTANCE.
    ///
    /// This exists because the obvious implementation is wrong, and a test
    /// caught it: quantizing each point onto a grid of the tolerance does NOT
    /// mean "endpoints within tolerance are the same node". Two points 3 mm
    /// apart, snapped to a 5 mm grid, land on 0 and 5 - the gap the caller
    /// asked to close is the reason they end up further apart than before. A
    /// grid is an accelerator, not a proximity test.
    ///
    /// So the grid buckets and the DISTANCE decides: an endpoint joins the
    /// nearest existing node within tolerance, searching its own cell and the
    /// neighbours, with ties broken by node key so the answer does not depend
    /// on which segment Revit happened to enumerate first.
    /// </summary>
    public sealed class CadNodeIndex
    {
        private readonly double _tolerance;
        private readonly Dictionary<string, List<CadNode>> _cells =
            new Dictionary<string, List<CadNode>>(StringComparer.Ordinal);
        private readonly List<CadNode> _all = new List<CadNode>();
        private readonly HashSet<string> _keys = new HashSet<string>(StringComparer.Ordinal);

        public CadNodeIndex(double toleranceMm) { _tolerance = Math.Max(toleranceMm, 1e-9); }

        public IList<CadNode> Nodes => _all;
        public int Count => _all.Count;
        public double ToleranceMm => _tolerance;

        private string Cell(long cx, long cy, long cz) =>
            string.Format(CultureInfo.InvariantCulture, "{0}|{1}|{2}", cx, cy, cz);

        private IEnumerable<CadNode> Neighbourhood(CadPoint p)
        {
            long cx = (long)Math.Floor(p.X / _tolerance);
            long cy = (long)Math.Floor(p.Y / _tolerance);
            long cz = (long)Math.Floor(p.Z / _tolerance);
            for (long dx = -1; dx <= 1; dx++)
                for (long dy = -1; dy <= 1; dy++)
                    for (long dz = -1; dz <= 1; dz++)
                    {
                        List<CadNode> bucket;
                        if (_cells.TryGetValue(Cell(cx + dx, cy + dy, cz + dz), out bucket))
                            foreach (CadNode n in bucket) yield return n;
                    }
        }

        /// <summary>The node this point belongs to, or null when nothing is within tolerance.</summary>
        public CadNode Find(CadPoint p)
        {
            CadNode best = null;
            double bestDistance = double.MaxValue;
            foreach (CadNode n in Neighbourhood(p))
            {
                double d = n.Point.DistanceTo(p);
                if (d > _tolerance) continue;
                if (d < bestDistance ||
                    (d == bestDistance && best != null && string.CompareOrdinal(n.Key, best.Key) < 0))
                { best = n; bestDistance = d; }
            }
            return best;
        }

        /// <summary>Find the node for this point, creating it when nothing is near enough.</summary>
        public CadNode Add(CadPoint p, int segmentIndex)
        {
            CadNode node = Find(p);
            if (node == null)
            {
                // Two distinct nodes CAN quantize to one key when they sit more
                // than a tolerance apart across a cell edge; the key is a name,
                // so it is made unique rather than allowed to merge two corners.
                string key = p.Key(_tolerance);
                string unique = key;
                int suffix = 1;
                while (_keys.Contains(unique))
                    unique = key + "#" + (++suffix).ToString(CultureInfo.InvariantCulture);
                node = new CadNode(unique, p);
                _keys.Add(unique);
                _all.Add(node);
                long cx = (long)Math.Floor(p.X / _tolerance);
                long cy = (long)Math.Floor(p.Y / _tolerance);
                long cz = (long)Math.Floor(p.Z / _tolerance);
                List<CadNode> bucket;
                string cell = Cell(cx, cy, cz);
                if (!_cells.TryGetValue(cell, out bucket)) _cells[cell] = bucket = new List<CadNode>();
                bucket.Add(node);
            }
            if (segmentIndex >= 0) node.SegmentIndices.Add(segmentIndex);
            return node;
        }
    }

    /// <summary>A closed ring of points, with the facts a caller needs to trust it.</summary>
    public sealed class CadLoop
    {
        public IList<CadPoint> Points { get; }
        public string Layer { get; }
        /// <summary>Signed plan area in mm^2. Positive is counter-clockwise.</summary>
        public double SignedArea { get; }
        public double Area => Math.Abs(SignedArea);
        public bool IsCounterClockwise => SignedArea > 0;
        /// <summary>The largest gap that had to be closed to make this ring, in mm. 0 means it was already closed.</summary>
        public double LargestClosedGapMm { get; }
        public IList<int> SegmentIndices { get; }

        public CadLoop(IList<CadPoint> points, string layer, double largestClosedGapMm, IList<int> segmentIndices)
        {
            Points = points;
            Layer = layer;
            LargestClosedGapMm = largestClosedGapMm;
            SegmentIndices = segmentIndices ?? new List<int>();
            SignedArea = ShoelaceArea(points);
        }

        public static double ShoelaceArea(IList<CadPoint> pts)
        {
            if (pts == null || pts.Count < 3) return 0;
            double twice = 0;
            for (int i = 0; i < pts.Count; i++)
            {
                CadPoint a = pts[i], b = pts[(i + 1) % pts.Count];
                twice += a.X * b.Y - b.X * a.Y;
            }
            return twice / 2.0;
        }

        /// <summary>The loop as it reads counter-clockwise. Winding is a convention, so it is stated and normalised, never assumed.</summary>
        public CadLoop AsCounterClockwise()
        {
            if (IsCounterClockwise) return this;
            var reversed = Points.Reverse().ToList();
            return new CadLoop(reversed, Layer, LargestClosedGapMm, SegmentIndices);
        }

        /// <summary>
        /// The loop as it reads CLOCKWISE - the winding a HOLE takes.
        ///
        /// Revit reads the direction of a profile ring as the statement of whether
        /// it adds material or removes it, so which way round a hole runs is not a
        /// cosmetic detail: a hole wound the same way as its outer ring is a second
        /// slab standing in the opening.
        /// </summary>
        public CadLoop AsClockwise()
        {
            if (!IsCounterClockwise) return this;
            var reversed = Points.Reverse().ToList();
            return new CadLoop(reversed, Layer, LargestClosedGapMm, SegmentIndices);
        }
    }

    /// <summary>
    /// A wall read out of two drawn lines: the centreline a model would build,
    /// the thickness measured between the faces, and how much of the two lines
    /// actually agreed.
    /// </summary>
    public sealed class CadDoubleLine
    {
        public CadPoint Start { get; }
        public CadPoint End { get; }
        public double ThicknessMm { get; }
        public string Layer { get; }
        /// <summary>How far the two lines run alongside each other, in mm.</summary>
        public double OverlapLengthMm { get; }
        /// <summary>Overlap as a fraction of the SHORTER line: 1.0 means the shorter line is fully paired.</summary>
        public double OverlapFraction { get; }
        /// <summary>The measured angle between the two lines, in degrees. Zero is exactly parallel.</summary>
        public double AngleDeviationDegrees { get; }
        public int SegmentIndexA { get; }
        public int SegmentIndexB { get; }

        public CadDoubleLine(CadPoint start, CadPoint end, double thicknessMm, string layer,
                             double overlapLengthMm, double overlapFraction,
                             double angleDeviationDegrees, int indexA, int indexB)
        {
            Start = start; End = end; ThicknessMm = thicknessMm; Layer = layer;
            OverlapLengthMm = overlapLengthMm; OverlapFraction = overlapFraction;
            AngleDeviationDegrees = angleDeviationDegrees;
            SegmentIndexA = indexA; SegmentIndexB = indexB;
        }

        public double LengthMm => Start.PlanDistanceTo(End);
    }

    public static class CadTopologyRules
    {
        /// <summary>
        /// Is <paramref name="inner"/> a boundary INSIDE <paramref name="outer"/>
        /// rather than a wall of its own?
        ///
        /// MEASURED, not assumed. A Revit compound wall exported to DWG arrives as
        /// one line per material-layer boundary: the fixture's 352.4 mm wall came
        /// back as SIX parallel lines, and those six admit ten thickness-valid
        /// pairings. Refusing to reuse a face is not enough to sort them out - the
        /// outer faces take one pairing and the two innermost boundaries are still
        /// free to pair with each other, so every wall in the drawing was proposed
        /// twice, once at its true thickness and once at the width of its core.
        ///
        /// The test that separates them is containment: a pairing whose whole band
        /// lies between an accepted wall's faces, parallel to it and running
        /// alongside it, is that wall's inside. Two DISTINCT walls cannot be in
        /// that relation - one would be inside the other - so nothing real is lost
        /// by absorbing it.
        /// </summary>
        public static bool IsInnerBoundaryOf(CadDoubleLine inner, CadDoubleLine outer,
                                             double angleToleranceDegrees, double toleranceMm)
        {
            if (inner == null || outer == null || ReferenceEquals(inner, outer)) return false;
            // Only a NARROWER reading can be an inside. Equal widths are two walls
            // face to face, which is a real thing a drawing can say.
            if (inner.ThicknessMm >= outer.ThicknessMm - toleranceMm) return false;

            CadVector du = UnitOf(outer.Start, outer.End);
            CadVector di = UnitOf(inner.Start, inner.End);
            if (IsZero(du) || IsZero(di)) return false;
            if (du.UndirectedAngleDegrees(di) > angleToleranceDegrees) return false;

            CadVector n = du.PerpendicularLeft();
            CadPoint om = Mid(outer.Start, outer.End);
            CadPoint im = Mid(inner.Start, inner.End);
            double offset = Math.Abs((im.X - om.X) * n.X + (im.Y - om.Y) * n.Y);

            // BOTH of the inner pair's faces must fall within the outer band.
            if (offset + inner.ThicknessMm / 2.0 > outer.ThicknessMm / 2.0 + toleranceMm) return false;

            // And it must actually run alongside: a short stub crossing the band
            // somewhere else along the wall is not the wall's inside.
            double oa = Along(outer.Start, om, du), ob = Along(outer.End, om, du);
            double ia = Along(inner.Start, om, du), ib = Along(inner.End, om, du);
            double lo = Math.Max(Math.Min(oa, ob), Math.Min(ia, ib));
            double hi = Math.Min(Math.Max(oa, ob), Math.Max(ia, ib));
            return hi - lo > toleranceMm;
        }

        /// <summary>
        /// Is this single line one of <paramref name="wall"/>'s material-layer
        /// boundaries rather than something the reading has not accounted for?
        ///
        /// The innermost boundaries of a compound wall often cannot pair with
        /// anything: the fixture's two core lines are 19 mm apart, below any
        /// sane wall thickness, so no pairing claims them. They are still the
        /// wall, and reporting them as unclaimed geometry would send a reviewer
        /// hunting for a wall that was already built.
        /// </summary>
        public static bool IsInsideBandOf(CadSegment segment, CadDoubleLine wall,
                                          double angleToleranceDegrees, double toleranceMm)
        {
            if (segment == null || wall == null) return false;
            CadVector du = UnitOf(wall.Start, wall.End);
            CadVector ds = UnitOf(segment.A, segment.B);
            if (IsZero(du) || IsZero(ds)) return false;
            if (du.UndirectedAngleDegrees(ds) > angleToleranceDegrees) return false;

            CadVector n = du.PerpendicularLeft();
            CadPoint wm = Mid(wall.Start, wall.End);
            double half = wall.ThicknessMm / 2.0 + toleranceMm;
            foreach (CadPoint p in new[] { segment.A, segment.B })
                if (Math.Abs((p.X - wm.X) * n.X + (p.Y - wm.Y) * n.Y) > half) return false;

            double wa = Along(wall.Start, wm, du), wb = Along(wall.End, wm, du);
            double sa = Along(segment.A, wm, du), sb = Along(segment.B, wm, du);
            double lo = Math.Max(Math.Min(wa, wb), Math.Min(sa, sb));
            double hi = Math.Min(Math.Max(wa, wb), Math.Max(sa, sb));
            return hi - lo > toleranceMm;
        }

        private static CadVector UnitOf(CadPoint a, CadPoint b)
        {
            double dx = b.X - a.X, dy = b.Y - a.Y;
            double len = Math.Sqrt(dx * dx + dy * dy);
            if (len <= 0) return new CadVector(0, 0);
            return new CadVector(dx / len, dy / len);
        }

        private static bool IsZero(CadVector v) => v.X == 0 && v.Y == 0;

        private static CadPoint Mid(CadPoint a, CadPoint b) =>
            new CadPoint((a.X + b.X) / 2.0, (a.Y + b.Y) / 2.0, (a.Z + b.Z) / 2.0);

        private static double Along(CadPoint p, CadPoint origin, CadVector unit) =>
            (p.X - origin.X) * unit.X + (p.Y - origin.Y) * unit.Y;

        // ---------------------------------------------------------------------
        // Nodes and chains
        // ---------------------------------------------------------------------

        /// <summary>
        /// Build the snapped node graph. Endpoints closer than the tolerance
        /// become ONE node - which is the whole of "gap closing", done once, in
        /// the place everything else reads from.
        /// </summary>
        public static CadNodeIndex BuildNodes(IList<CadSegment> segments, double toleranceMm)
        {
            var index = new CadNodeIndex(toleranceMm);
            if (segments == null) return index;
            for (int i = 0; i < segments.Count; i++)
            {
                CadSegment s = segments[i];
                if (s == null) continue;
                index.Add(s.A, i);
                index.Add(s.B, i);
            }
            return index;
        }

        /// <summary>
        /// Merge runs of segments that continue in the same direction through a
        /// node of degree two. A polyline drawn as forty collinear pieces is one
        /// wall, and the count of pieces is an artefact of drawing, not a fact
        /// about the building.
        /// </summary>
        public static List<CadSegment> MergeCollinear(IList<CadSegment> segments, double toleranceMm,
                                                      double angleToleranceDegrees, out int mergedAway)
        {
            mergedAway = 0;
            if (segments == null || segments.Count == 0) return new List<CadSegment>();

            var used = new bool[segments.Count];
            var result = new List<CadSegment>();
            CadNodeIndex nodes = BuildNodes(segments, toleranceMm);

            for (int i = 0; i < segments.Count; i++)
            {
                if (used[i] || segments[i] == null) continue;
                used[i] = true;
                CadSegment current = segments[i];
                CadPoint head = current.A, tail = current.B;
                int absorbed = 0;

                // Walk both ends while the continuation is collinear and unambiguous.
                for (int direction = 0; direction < 2; direction++)
                {
                    bool growing = true;
                    while (growing)
                    {
                        growing = false;
                        CadPoint end = direction == 0 ? tail : head;
                        CadNode node = nodes.Find(end);
                        if (node == null) break;
                        // Degree two, or the join is a corner and belongs to nobody.
                        var candidates = node.SegmentIndices.Where(ix => !used[ix] && segments[ix] != null).ToList();
                        if (node.Degree != 2 || candidates.Count != 1) break;
                        int nextIndex = candidates[0];
                        CadSegment next = segments[nextIndex];
                        if (!string.Equals(next.Layer, current.Layer, StringComparison.Ordinal)) break;

                        CadVector? a = current.PlanDirection, b = next.PlanDirection;
                        if (a == null || b == null) break;
                        if (a.Value.UndirectedAngleDegrees(b.Value) > angleToleranceDegrees) break;

                        CadPoint far = next.A.PlanDistanceTo(end) <= toleranceMm ? next.B : next.A;
                        if (direction == 0) tail = far; else head = far;
                        used[nextIndex] = true;
                        absorbed++;
                        growing = true;
                    }
                }

                mergedAway += absorbed;
                result.Add(absorbed == 0
                    ? current
                    : new CadSegment(head, tail, current.Layer, current.SourceKind, current.SourceIndex));
            }
            return result;
        }

        // ---------------------------------------------------------------------
        // Loops
        // ---------------------------------------------------------------------

        /// <summary>
        /// Find closed rings. A component whose every node has degree two IS a
        /// ring; anything else is reported as an open chain rather than forced
        /// shut, because closing a shape nobody drew is how a slab ends up over
        /// a corridor.
        /// </summary>
        public static List<CadLoop> FindLoops(IList<CadSegment> segments, double toleranceMm,
                                              out List<IList<CadPoint>> openChains)
        {
            var loops = new List<CadLoop>();
            openChains = new List<IList<CadPoint>>();
            if (segments == null || segments.Count == 0) return loops;

            CadNodeIndex nodes = BuildNodes(segments, toleranceMm);
            var visitedSegment = new bool[segments.Count];

            for (int seed = 0; seed < segments.Count; seed++)
            {
                if (visitedSegment[seed] || segments[seed] == null) continue;

                // Collect the connected component this segment belongs to.
                var component = new List<int>();
                var stack = new Stack<int>();
                stack.Push(seed);
                visitedSegment[seed] = true;
                while (stack.Count > 0)
                {
                    int ix = stack.Pop();
                    component.Add(ix);
                    CadSegment s = segments[ix];
                    foreach (CadPoint p in new[] { s.A, s.B })
                    {
                        CadNode n = nodes.Find(p);
                        if (n == null) continue;
                        foreach (int other in n.SegmentIndices)
                            if (!visitedSegment[other] && segments[other] != null)
                            { visitedSegment[other] = true; stack.Push(other); }
                    }
                }

                var componentNodes = new HashSet<CadNode>();
                foreach (int ix in component)
                {
                    CadNode na = nodes.Find(segments[ix].A);
                    CadNode nb = nodes.Find(segments[ix].B);
                    if (na != null) componentNodes.Add(na);
                    if (nb != null) componentNodes.Add(nb);
                }
                var componentSet = new HashSet<int>(component);
                bool everyNodeDegreeTwo = componentNodes.All(n =>
                    n.SegmentIndices.Count(ix => componentSet.Contains(ix)) == 2);

                IList<CadPoint> ordered = OrderComponent(segments, component, nodes, toleranceMm, out double largestGap);
                if (ordered == null) { continue; }

                if (everyNodeDegreeTwo && ordered.Count >= 3)
                    loops.Add(new CadLoop(ordered, segments[component[0]].Layer, largestGap, component));
                else
                    openChains.Add(ordered);
            }
            return loops;
        }

        private static IList<CadPoint> OrderComponent(IList<CadSegment> segments, List<int> component,
                                                      CadNodeIndex nodes, double toleranceMm,
                                                      out double largestGapMm)
        {
            largestGapMm = 0;
            if (component.Count == 0) return null;
            var remaining = new HashSet<int>(component);
            int first = component[0];
            var points = new List<CadPoint> { segments[first].A, segments[first].B };
            remaining.Remove(first);

            bool extended = true;
            while (extended && remaining.Count > 0)
            {
                extended = false;
                CadPoint tail = points[points.Count - 1];
                CadNode node = nodes.Find(tail);
                if (node != null)
                {
                    foreach (int ix in node.SegmentIndices)
                    {
                        if (!remaining.Contains(ix)) continue;
                        CadSegment s = segments[ix];
                        double gapA = s.A.PlanDistanceTo(tail), gapB = s.B.PlanDistanceTo(tail);
                        CadPoint far = gapA <= gapB ? s.B : s.A;
                        double gap = Math.Min(gapA, gapB);
                        if (gap > largestGapMm) largestGapMm = gap;
                        points.Add(far);
                        remaining.Remove(ix);
                        extended = true;
                        break;
                    }
                }
                if (extended) continue;

                // Nothing continues from the tail: try growing from the head instead.
                CadPoint head = points[0];
                node = nodes.Find(head);
                if (node != null)
                {
                    foreach (int ix in node.SegmentIndices)
                    {
                        if (!remaining.Contains(ix)) continue;
                        CadSegment s = segments[ix];
                        double gapA = s.A.PlanDistanceTo(head), gapB = s.B.PlanDistanceTo(head);
                        CadPoint far = gapA <= gapB ? s.B : s.A;
                        double gap = Math.Min(gapA, gapB);
                        if (gap > largestGapMm) largestGapMm = gap;
                        points.Insert(0, far);
                        remaining.Remove(ix);
                        extended = true;
                        break;
                    }
                }
            }

            // A ring comes back with its first point repeated at the end; drop it,
            // because a ring is defined by its corners and not by saying one twice.
            //
            // The closing distance is a GAP and is recorded as one. It is the gap
            // that matters most - the drawing that stops 3 mm short of closing is
            // the commonest thing in a real plan, and a loop that does not admit
            // it was snapped shut is a loop nobody can audit.
            if (points.Count > 2)
            {
                double closing = points[0].PlanDistanceTo(points[points.Count - 1]);
                if (closing <= toleranceMm)
                {
                    if (closing > largestGapMm) largestGapMm = closing;
                    points.RemoveAt(points.Count - 1);
                }
            }
            return points;
        }

        /// <summary>Ray casting in plan. On-edge is deliberately NOT decided here - a caller who cares must ask about distance.</summary>
        public static bool ContainsPoint(IList<CadPoint> loop, CadPoint p)
        {
            if (loop == null || loop.Count < 3) return false;
            bool inside = false;
            for (int i = 0, j = loop.Count - 1; i < loop.Count; j = i++)
            {
                double xi = loop[i].X, yi = loop[i].Y, xj = loop[j].X, yj = loop[j].Y;
                bool straddles = (yi > p.Y) != (yj > p.Y);
                if (!straddles) continue;
                double x = (xj - xi) * (p.Y - yi) / (yj - yi) + xi;
                if (p.X < x) inside = !inside;
            }
            return inside;
        }

        // ---------------------------------------------------------------------
        // Double lines: the wall question
        // ---------------------------------------------------------------------

        /// <summary>
        /// Find pairs of parallel segments that read as the two faces of a wall.
        ///
        /// A pair qualifies when all four are true, and each is a number the
        /// caller declared rather than one chosen here:
        ///   - the angle between them is within angleToleranceDegrees;
        ///   - the perpendicular distance is within [minThicknessMm, maxThicknessMm];
        ///   - they overlap along their own direction by at least minOverlapMm;
        ///   - that overlap is at least minOverlapFraction of the shorter line.
        ///
        /// The last one is what stops a 6 m wall pairing with a 300 mm stub that
        /// happens to sit beside it. Every qualifying pair carries its measured
        /// overlap and angle so a reviewer can disagree with the thresholds
        /// without re-running anything.
        /// </summary>
        public static List<CadDoubleLine> FindDoubleLines(IList<CadSegment> segments,
                                                          double minThicknessMm, double maxThicknessMm,
                                                          double angleToleranceDegrees,
                                                          double minOverlapMm, double minOverlapFraction,
                                                          bool sameLayerOnly = true)
        {
            var found = new List<CadDoubleLine>();
            if (segments == null || segments.Count < 2) return found;

            for (int i = 0; i < segments.Count; i++)
            {
                CadSegment a = segments[i];
                if (a == null) continue;
                CadVector? da = a.PlanDirection;
                if (da == null) continue;

                for (int j = i + 1; j < segments.Count; j++)
                {
                    CadSegment b = segments[j];
                    if (b == null) continue;
                    if (sameLayerOnly && !string.Equals(a.Layer, b.Layer, StringComparison.Ordinal)) continue;
                    CadVector? db = b.PlanDirection;
                    if (db == null) continue;

                    double angle = da.Value.UndirectedAngleDegrees(db.Value);
                    if (angle > angleToleranceDegrees) continue;

                    // Perpendicular separation, measured from a's line to b's midpoint.
                    double separation = PerpendicularDistance(a, b.Midpoint);
                    if (separation < minThicknessMm || separation > maxThicknessMm) continue;

                    // Overlap along a's direction.
                    double ta0 = Project(a, a.A), ta1 = Project(a, a.B);
                    double tb0 = Project(a, b.A), tb1 = Project(a, b.B);
                    double aLo = Math.Min(ta0, ta1), aHi = Math.Max(ta0, ta1);
                    double bLo = Math.Min(tb0, tb1), bHi = Math.Max(tb0, tb1);
                    double lo = Math.Max(aLo, bLo), hi = Math.Min(aHi, bHi);
                    double overlap = hi - lo;
                    if (overlap < minOverlapMm) continue;

                    double shorter = Math.Min(a.PlanLength, b.PlanLength);
                    double fraction = shorter <= 0 ? 0 : overlap / shorter;
                    if (fraction < minOverlapFraction) continue;

                    // The centreline: the overlapping span, halfway between the faces.
                    CadPoint aAtLo = PointAt(a, lo), aAtHi = PointAt(a, hi);
                    CadPoint bAtLo = ClosestOn(b, aAtLo), bAtHi = ClosestOn(b, aAtHi);
                    var start = new CadPoint((aAtLo.X + bAtLo.X) / 2, (aAtLo.Y + bAtLo.Y) / 2, (aAtLo.Z + bAtLo.Z) / 2);
                    var end = new CadPoint((aAtHi.X + bAtHi.X) / 2, (aAtHi.Y + bAtHi.Y) / 2, (aAtHi.Z + bAtHi.Z) / 2);

                    found.Add(new CadDoubleLine(start, end, separation, a.Layer, overlap, fraction, angle, i, j));
                }
            }
            return found;
        }

        /// <summary>Signed position of a point along a segment's direction, in mm from its A end.</summary>
        public static double Project(CadSegment s, CadPoint p)
        {
            CadVector? d = s.PlanDirection;
            if (d == null) return 0;
            return (p.X - s.A.X) * d.Value.X + (p.Y - s.A.Y) * d.Value.Y;
        }

        public static CadPoint PointAt(CadSegment s, double distanceFromA)
        {
            CadVector? d = s.PlanDirection;
            if (d == null) return s.A;
            return new CadPoint(s.A.X + d.Value.X * distanceFromA,
                                s.A.Y + d.Value.Y * distanceFromA,
                                s.A.Z);
        }

        /// <summary>Perpendicular distance from a segment's infinite line to a point, in plan.</summary>
        public static double PerpendicularDistance(CadSegment s, CadPoint p)
        {
            CadVector? d = s.PlanDirection;
            if (d == null) return s.A.PlanDistanceTo(p);
            double dx = p.X - s.A.X, dy = p.Y - s.A.Y;
            return Math.Abs(dx * d.Value.Y - dy * d.Value.X);
        }

        /// <summary>The point on a segment's infinite line nearest to p.</summary>
        public static CadPoint ClosestOn(CadSegment s, CadPoint p)
        {
            CadVector? d = s.PlanDirection;
            if (d == null) return s.A;
            double t = (p.X - s.A.X) * d.Value.X + (p.Y - s.A.Y) * d.Value.Y;
            return new CadPoint(s.A.X + d.Value.X * t, s.A.Y + d.Value.Y * t, s.A.Z);
        }

        /// <summary>
        /// Do two segments cross in plan, and where? Endpoint touching counts as
        /// an intersection - a T-junction is exactly how a partition meets a
        /// facade, and pretending otherwise loses the junction.
        /// </summary>
        public static bool Intersect(CadSegment a, CadSegment b, double toleranceMm, out CadPoint at)
        {
            at = default(CadPoint);
            if (a == null || b == null) return false;
            double x1 = a.A.X, y1 = a.A.Y, x2 = a.B.X, y2 = a.B.Y;
            double x3 = b.A.X, y3 = b.A.Y, x4 = b.B.X, y4 = b.B.Y;
            double den = (x1 - x2) * (y3 - y4) - (y1 - y2) * (x3 - x4);
            if (Math.Abs(den) < 1e-12) return false;    // parallel: not a crossing
            double t = ((x1 - x3) * (y3 - y4) - (y1 - y3) * (x3 - x4)) / den;
            double u = ((x1 - x3) * (y1 - y2) - (y1 - y3) * (x1 - x2)) / den;
            double lenA = a.PlanLength, lenB = b.PlanLength;
            double slackA = lenA > 0 ? toleranceMm / lenA : 0;
            double slackB = lenB > 0 ? toleranceMm / lenB : 0;
            if (t < -slackA || t > 1 + slackA) return false;
            if (u < -slackB || u > 1 + slackB) return false;
            at = new CadPoint(x1 + t * (x2 - x1), y1 + t * (y2 - y1), a.A.Z);
            return true;
        }

        // ---------------------------------------------------------------------
        // Clustering and comparison
        // ---------------------------------------------------------------------

        /// <summary>
        /// Group points that sit within a radius of each other. Used to fold the
        /// several marks a draughtsman makes for one thing - a door swing, a
        /// symbol drawn twice - into one candidate.
        /// </summary>
        public static List<List<int>> ClusterPoints(IList<CadPoint> points, double radiusMm)
        {
            var clusters = new List<List<int>>();
            if (points == null || points.Count == 0) return clusters;
            var assigned = new bool[points.Count];
            for (int i = 0; i < points.Count; i++)
            {
                if (assigned[i]) continue;
                var cluster = new List<int> { i };
                assigned[i] = true;
                // Breadth-first so a chain of near points becomes one cluster.
                for (int scan = 0; scan < cluster.Count; scan++)
                {
                    int seed = cluster[scan];
                    for (int j = 0; j < points.Count; j++)
                    {
                        if (assigned[j]) continue;
                        if (points[seed].PlanDistanceTo(points[j]) <= radiusMm)
                        { assigned[j] = true; cluster.Add(j); }
                    }
                }
                clusters.Add(cluster);
            }
            return clusters;
        }

        /// <summary>The plan bounding box of a set of points, or null when there are none.</summary>
        public static Tuple<CadPoint, CadPoint> BoundingBox(IEnumerable<CadPoint> points)
        {
            double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
            bool any = false;
            foreach (CadPoint p in points ?? Enumerable.Empty<CadPoint>())
            {
                any = true;
                if (p.X < minX) minX = p.X;
                if (p.Y < minY) minY = p.Y;
                if (p.Z < minZ) minZ = p.Z;
                if (p.X > maxX) maxX = p.X;
                if (p.Y > maxY) maxY = p.Y;
                if (p.Z > maxZ) maxZ = p.Z;
            }
            if (!any) return null;
            return Tuple.Create(new CadPoint(minX, minY, minZ), new CadPoint(maxX, maxY, maxZ));
        }
    }
}
