using System;
using System.Collections.Generic;
using System.Linq;

namespace Horizun.Revit.Core
{
    internal static class WallSlabRules
    {
        public const double DefaultToleranceMm = 5;
        private const double GeometryEpsilonMm = 0.000001;

        internal enum Relation { Ok, Gap, Embedded, Through }

        internal readonly struct VerticalSpan
        {
            public VerticalSpan(double bottom, double top)
            {
                Bottom = bottom;
                Top = top;
            }

            public double Bottom { get; }
            public double Top { get; }
        }

        internal readonly struct SlabHit
        {
            public SlabHit(long id, VerticalSpan span)
            {
                Id = id;
                VerticalSpan = span;
            }

            public long Id { get; }
            public VerticalSpan VerticalSpan { get; }
        }

        internal readonly struct Contact
        {
            public Contact(SlabHit hit, Relation relation, double faceDelta, double distance)
            {
                Hit = hit;
                Relation = relation;
                FaceDelta = faceDelta;
                Distance = distance;
            }

            public SlabHit Hit { get; }
            public Relation Relation { get; }
            // Positive goes into the slab from the face toward which the wall end points.
            public double FaceDelta { get; }
            public double Distance { get; }
        }

        internal readonly struct EndProbe<T>
        {
            public EndProbe(T point, double elevation, Contact? contact)
            {
                Point = point;
                Elevation = elevation;
                Contact = contact;
            }

            public T Point { get; }
            public double Elevation { get; }
            public Contact? Contact { get; }
        }

        public static TResult[] MeasurePage<T, TResult>(IEnumerable<T> orderedWalls, int offset, int size,
                                                         Func<T, TResult> measure) =>
            orderedWalls.Skip(offset).Take(size).Select(measure).ToArray();

        public static bool MayAffectProbe(long? floorOption, long wallOption, bool? insideFloorBounds) =>
            (!floorOption.HasValue || floorOption == -1 || floorOption == wallOption) && insideFloorBounds != false;

        public static (EndProbe<T>? Crown, EndProbe<T>? Base, long[] UnreadableFloors) MeasureStation<T>(
            IEnumerable<T> points, Func<T, IReadOnlyList<VerticalSpan>> readWall,
            Func<T, (IReadOnlyList<SlabHit> Hits, IReadOnlyList<long> Unreadable)> readFloors, double tolerance)
        {
            EndProbe<T>? crown = null;
            EndProbe<T>? foot = null;
            double crownLength = 0;
            double footLength = 0;
            var unreadable = new SortedSet<long>();
            foreach (T point in points)
            {
                IReadOnlyList<VerticalSpan> wall = readWall(point);
                if (wall.Count == 0) continue;
                double length = wall.Sum(span => span.Top - span.Bottom);
                var floors = readFloors(point);
                unreadable.UnionWith(floors.Unreadable);
                ConsiderEnd(true, ref crown, ref crownLength);
                ConsiderEnd(false, ref foot, ref footLength);

                void ConsiderEnd(bool isCrown, ref EndProbe<T>? best, ref double bestLength)
                {
                    Contact? contact = Nearest(wall, floors.Hits, isCrown, tolerance);
                    // A joint within tolerance wins across probes even if another probe is inside a slab.
                    bool withinTolerance = contact?.Relation == Relation.Ok;
                    bool bestWithinTolerance = best?.Contact?.Relation == Relation.Ok;
                    int comparison = bestWithinTolerance.CompareTo(withinTolerance);
                    if (comparison == 0) comparison = CompareContacts(contact, best?.Contact);
                    if (best.HasValue && (comparison > 0 || (comparison == 0 && length <= bestLength))) return;
                    double elevation = isCrown ? wall[wall.Count - 1].Top : wall[0].Bottom;
                    best = new EndProbe<T>(point, elevation, contact);
                    bestLength = length;
                }
            }
            return unreadable.Count == 0 ? (crown, foot, Array.Empty<long>()) : (null, null, unreadable.ToArray());
        }

        private static int CompareContacts(Contact? candidate, Contact? current)
        {
            if (!candidate.HasValue) return current.HasValue ? 1 : 0;
            if (!current.HasValue) return -1;
            Contact a = candidate.Value;
            Contact b = current.Value;
            int distance = a.Distance.CompareTo(b.Distance);
            if (distance != 0) return distance;
            int faceDistance = Math.Abs(a.FaceDelta).CompareTo(Math.Abs(b.FaceDelta));
            return faceDistance != 0 ? faceDistance : a.Hit.Id.CompareTo(b.Hit.Id);
        }

        public static List<VerticalSpan> Merge(IEnumerable<VerticalSpan> spans)
        {
            var merged = new List<VerticalSpan>();
            foreach (VerticalSpan span in spans.OrderBy(s => s.Bottom).ThenBy(s => s.Top))
            {
                if (span.Top - span.Bottom <= GeometryEpsilonMm) continue;
                if (merged.Count == 0 || span.Bottom - merged[merged.Count - 1].Top > GeometryEpsilonMm)
                {
                    merged.Add(span);
                    continue;
                }

                VerticalSpan last = merged[merged.Count - 1];
                merged[merged.Count - 1] = new VerticalSpan(last.Bottom, Math.Max(last.Top, span.Top));
            }
            return merged;
        }

        public static Contact? Nearest(IReadOnlyList<VerticalSpan> wall, IEnumerable<SlabHit> slabs,
                                       bool crown, double tolerance)
        {
            if (wall.Count == 0) return null;
            VerticalSpan endSegment = crown ? wall[wall.Count - 1] : wall[0];
            double end = crown ? endSegment.Top : -endSegment.Bottom;
            double innerEnd = crown ? endSegment.Bottom : -endSegment.Top;
            double oppositeWallEnd = crown ? wall[0].Bottom : -wall[wall.Count - 1].Top;
            Contact? best = null;
            foreach (SlabHit hit in slabs)
            {
                double near = crown ? hit.VerticalSpan.Bottom : -hit.VerticalSpan.Top;
                double far = crown ? hit.VerticalSpan.Top : -hit.VerticalSpan.Bottom;
                if (end > far && oppositeWallEnd >= far) continue;
                double delta = end - near;
                Relation relation;
                if (delta < -tolerance)
                    relation = Relation.Gap;
                else if (Math.Abs(delta) <= tolerance)
                    relation = Relation.Ok;
                else if (end <= far + tolerance)
                    relation = Relation.Embedded;
                // A wall opening cannot connect the material on opposite sides of a slab.
                // A through slab belongs to the closer end (both if exactly centered).
                else if (innerEnd < near - tolerance && end - far <= near - oppositeWallEnd)
                    relation = Relation.Through;
                else
                    continue;

                double distance = Math.Max(0, Math.Max(near - end, end - far));
                var candidate = new Contact(hit, relation, delta, distance);
                if (CompareContacts(candidate, best) < 0) best = candidate;
            }
            return best;
        }
    }
}
