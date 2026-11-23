using System;
using System.Collections.Generic;
using System.Linq;

namespace Horizun.Revit.Core
{
    internal static class PlanLeaderRules
    {
        // Hidden-line 2D output can report the host document in ElementNode even
        // when its element ID is scoped to the reported link instance.
        internal static TDocument ExportedStrokeDocument<TDocument>(TDocument host, TDocument reported,
            long instanceId, long elementId, Func<long, TDocument> linkedDocument, Func<TDocument, long, bool> hasElement,
            Func<TDocument, TDocument, bool> sameDocument = null)
            where TDocument : class
        {
            if (elementId < 0) return null;
            sameDocument = sameDocument ?? EqualityComparer<TDocument>.Default.Equals;
            TDocument owner = instanceId < 0 ? host : linkedDocument(instanceId);
            if (owner == null || (!sameDocument(reported, owner) &&
                !(instanceId >= 0 && sameDocument(reported, host)))) return null;
            return hasElement(owner, elementId) ? owner : null;
        }

        // A scoped callback can omit LinkInstanceId. The scope still has to identify
        // one placement. HLR callbacks outside any scope use the node's explicit id.
        internal static long? ExportedStrokeInstance(long reported, int linkDepth, long? scoped)
        {
            if (linkDepth == 0) return reported;
            if (linkDepth != 1 || !scoped.HasValue || (reported >= 0 && reported != scoped.Value))
                return null;
            return scoped;
        }

        internal static long? UniqueExportedLinkInstance(IEnumerable<long> matchingIds)
        {
            using (var matches = matchingIds.GetEnumerator())
            {
                if (!matches.MoveNext()) return null;
                long first = matches.Current;
                return matches.MoveNext() ? (long?)null : first;
            }
        }

        internal static T WithLinkedStrokeFallback<T>(T hit, bool complete, bool hasSource, Func<T> readStroke)
            => complete && !hasSource ? readStroke() : hit;
        internal static bool InsideCrop<T>(IEnumerable<T> modelBoundary, T modelPoint, Func<T, CadPoint> project)
        {
            var boundary = modelBoundary.Select(project).ToList();
            CadPoint point = project(modelPoint);
            if (boundary.Count < 3) return false;
            for (int i = 0, previous = boundary.Count - 1; i < boundary.Count; previous = i++)
                if (SegmentDistance(point, boundary[previous], boundary[i]) <= 1e-9) return true;
            return CadTopologyRules.ContainsPoint(boundary, point);
        }

        private static double SegmentDistance(CadPoint point, CadPoint a, CadPoint b)
            => TextNoteLayoutRules.PlanSegmentDistance(point.X, point.Y, a.X, a.Y, b.X, b.Y);

        internal static double? EntryInRange(double a, double b, double depth)
        {
            double entry = Math.Max(0, Math.Min(a, b));
            return Math.Max(a, b) >= 0 && entry <= depth ? entry : (double?)null;
        }

        internal static (T Target, T Observed, string Problem) FirstTarget<T>(IEnumerable<T> candidates,
            Func<T, double> earliest, Func<T, (double? Distance, bool AtCut, string Problem)> measure,
            Func<T, string> describe) where T : class
        {
            const double tie = 1.0 / 304.8;
            T best = null, second = null;
            double bestDistance = double.PositiveInfinity, secondDistance = double.PositiveInfinity;
            double unreadDistance = double.PositiveInfinity;
            bool bestAtCut = false;
            string problem = null;
            foreach (T candidate in candidates.OrderBy(earliest))
            {
                if (earliest(candidate) > (bestAtCut ? 0 : bestDistance + tie)) break;
                var result = measure(candidate);
                if (result.Problem != null)
                {
                    double possible = result.Distance ?? earliest(candidate);
                    if (possible < unreadDistance)
                    {
                        unreadDistance = possible;
                        problem = result.Problem;
                    }
                    continue;
                }
                if (!result.Distance.HasValue || (bestAtCut && !result.AtCut)) continue;
                double distance = result.Distance.Value;
                if (result.AtCut && !bestAtCut)
                {
                    best = candidate;
                    bestDistance = distance;
                    bestAtCut = true;
                    second = null;
                    secondDistance = double.PositiveInfinity;
                }
                else if (distance < bestDistance)
                {
                    second = best;
                    secondDistance = bestDistance;
                    best = candidate;
                    bestDistance = distance;
                }
                else if (distance < secondDistance)
                {
                    second = candidate;
                    secondDistance = distance;
                }
            }
            if (problem != null && unreadDistance <= (bestAtCut ? 0 : bestDistance + tie))
                return (default, best, problem);
            if (second != null && secondDistance - bestDistance <= tie)
                return (default, best, FormattableString.Invariant(
                    $"more than one plan target at {bestDistance * 304.8:0.###} mm: {describe(best)}, {describe(second)}"));
            return (best, best, null);
        }
    }

    // The exporter supplies only drawn segments. Keep their bounded extents and their exact
    // owner so a line from another instance or elsewhere on the same element cannot prove a hit.
    internal sealed class PlanStrokeIndex<TDocument>
    {
        private readonly Dictionary<(TDocument, long), Dictionary<long, List<(CadPoint A, CadPoint B)>>> _strokes =
            new Dictionary<(TDocument, long), Dictionary<long, List<(CadPoint A, CadPoint B)>>>();
        private readonly List<(CadPoint A, CadPoint B)> _unattributed = new List<(CadPoint A, CadPoint B)>();
        internal bool Measured => _strokes.Count != 0;

        internal void Add(TDocument document, long element, long instance, bool attributable, CadPoint a, CadPoint b)
        {
            if (Math.Abs(a.X - b.X) <= 1e-9 && Math.Abs(a.Y - b.Y) <= 1e-9) return;
            if (!attributable) { _unattributed.Add((a, b)); return; }
            var key = (document, instance);
            if (!_strokes.TryGetValue(key, out var owners))
                _strokes.Add(key, owners = new Dictionary<long, List<(CadPoint A, CadPoint B)>>());
            if (!owners.TryGetValue(element, out var segments))
                owners.Add(element, segments = new List<(CadPoint A, CadPoint B)>());
            segments.Add((a, b));
        }

        internal IEnumerable<CadPoint> AimPoints(TDocument document, long element, long instance)
        {
            if (!_strokes.TryGetValue((document, instance), out var owners) ||
                !owners.TryGetValue(element, out var segments)) yield break;
            foreach (var segment in segments)
                foreach (double t in new[] { .5, .25, .75 })
                    yield return new CadPoint(segment.A.X + t * (segment.B.X - segment.A.X),
                        segment.A.Y + t * (segment.B.Y - segment.A.Y), segment.A.Z + t * (segment.B.Z - segment.A.Z));
        }

        internal double? Distance(TDocument document, long element, long instance, CadPoint point)
        {
            if (HasUnattributedStroke(point)) return null;
            return _strokes.TryGetValue((document, instance), out var owners) && owners.TryGetValue(element, out var segments)
                ? segments.Min(s => DistanceTo(point, s))
                : double.PositiveInfinity;
        }

        internal (TDocument Document, long ElementId, long InstanceId)? UniqueLinkedStroke(CadPoint point,
            out bool ambiguous)
        {
            ambiguous = false;
            (TDocument, long, long)? hit = null;
            int owners = 0;
            foreach (var group in _strokes)
                foreach (var entry in group.Value)
                {
                    if (!entry.Value.Any(s => DistanceTo(point, s) <= TextNoteLayoutRules.PlanStrokeToleranceFeet))
                        continue;
                    owners++;
                    if (group.Key.Item2 >= 0) hit = (group.Key.Item1, entry.Key, group.Key.Item2);
                }
            ambiguous = HasUnattributedStroke(point) || (hit.HasValue && owners != 1);
            return ambiguous ? null : hit;
        }

        internal IEnumerable<(long ElementId, double Distance)> NearbyElements(TDocument document, long instance, CadPoint point)
        {
            if (HasUnattributedStroke(point))
                throw new InvalidOperationException("a drawn segment within 0.1 mm of the end has no measurable element or link instance identity");
            if (!_strokes.TryGetValue((document, instance), out var owners)) yield break;
            foreach (var entry in owners)
            {
                double distance = entry.Value.Min(s => DistanceTo(point, s));
                if (distance <= TextNoteLayoutRules.PlanStrokeToleranceFeet) yield return (entry.Key, distance);
            }
        }

        private bool HasUnattributedStroke(CadPoint point)
            => _unattributed.Any(s => DistanceTo(point, s) <= TextNoteLayoutRules.PlanStrokeToleranceFeet);

        private static double DistanceTo(CadPoint point, (CadPoint A, CadPoint B) segment)
            => TextNoteLayoutRules.PlanSegmentDistance(point.X, point.Y, segment.A.X, segment.A.Y, segment.B.X, segment.B.Y);
    }
}
