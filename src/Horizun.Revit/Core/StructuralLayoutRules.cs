// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// Structural layout arithmetic without Revit: where grid lines cross, which
// crossings already carry a column, and which consecutive crossings along one
// grid make a beam span. The planner feeds this file plain segments and
// existing positions; it takes back points, spans and named omissions - and
// like every planner in this repository, an ambiguity is a refusal, not a
// guess someone's structure inherits.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;

namespace Horizun.Revit.Core
{
    /// <summary>A grid line in plan: a straight segment with a name. Feet.</summary>
    public sealed class GridSegment
    {
        public string Name;
        public long ElementId;
        public double X1, Y1, X2, Y2;
    }

    public sealed class GridIntersection
    {
        public double X, Y;
        public string GridA, GridB;
        public long GridAId, GridBId;
        /// <summary>Parameter along each grid, for deterministic ordering.</summary>
        public double TA, TB;
    }

    public sealed class BeamSpan
    {
        public string Grid;
        public long GridId;
        public double X1, Y1, X2, Y2;
        public string FromCrossing, ToCrossing;
    }

    public enum FramingFootprintDirection
    {
        Unfiltered,
        PlacementAxis,
        MeasuredExtents
    }

    /// <summary>Projected axis and width, or a measured footprint, of existing framing. Feet.</summary>
    public readonly struct ExistingFramingSegment
    {
        public readonly double X1, Y1, X2, Y2;
        public readonly double WidthFeet;

        public readonly long ElementId;
        public readonly string UniqueId;
        public readonly string NonBeamStructuralType;
        public readonly IList<FramingPlanTriangle> Footprint;
        public readonly FramingFootprintDirection FootprintDirection;

        public ExistingFramingSegment(double x1, double y1, double x2, double y2, double widthFeet = 0,
                                   long elementId = 0, string uniqueId = null, string nonBeamStructuralType = null,
                                   IList<FramingPlanTriangle> footprint = null,
                                   FramingFootprintDirection footprintDirection = FramingFootprintDirection.Unfiltered)
        {
            X1 = x1; Y1 = y1;
            X2 = x2; Y2 = y2;
            WidthFeet = widthFeet;
            ElementId = elementId;
            UniqueId = uniqueId;
            NonBeamStructuralType = nonBeamStructuralType;
            Footprint = footprint;
            FootprintDirection = footprintDirection;
        }
    }

    public readonly struct FramingPlanTriangle
    {
        private readonly (double X, double Y) a, b, c;

        public FramingPlanTriangle(double ax, double ay, double bx, double by, double cx, double cy)
        {
            a = (ax, ay); b = (bx, by); c = (cx, cy);
        }

        public bool HasArea
        {
            get
            {
                double area2 = (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
                return !double.IsInfinity(area2) && Math.Abs(area2) > 1e-12;
            }
        }

        internal (double Min, double Max) ProjectedExtent(double ux, double uy)
        {
            double p = a.X * ux + a.Y * uy;
            double q = b.X * ux + b.Y * uy;
            double r = c.X * ux + c.Y * uy;
            return (Math.Min(p, Math.Min(q, r)), Math.Max(p, Math.Max(q, r)));
        }

        internal bool TryClip(BeamSpan bay, double length, double ux, double uy,
                              out double start, out double end)
        {
            double first = double.PositiveInfinity, last = double.NegativeInfinity;
            var p = Project(a);
            var q = Project(b);
            var r = Project(c);
            Cross(p, q);
            Cross(q, r);
            Cross(r, p);
            start = Math.Max(0, first);
            end = Math.Min(length, last);
            return end >= start;

            (double Along, double Side) Project((double X, double Y) v)
            {
                double x = v.X - bay.X1, y = v.Y - bay.Y1;
                return (x * ux + y * uy, x * uy - y * ux);
            }

            void Cross((double Along, double Side) from, (double Along, double Side) to)
            {
                if (Math.Abs(from.Side) <= StructuralLayoutRules.BeamGeometryEpsilon) Include(from.Along);
                // Every vertex is the start of one edge. Strict sign changes find the other intersections.
                if ((from.Side < 0 && to.Side > 0) || (from.Side > 0 && to.Side < 0))
                    Include(from.Along + (to.Along - from.Along) * from.Side / (from.Side - to.Side));
            }

            void Include(double along)
            {
                first = Math.Min(first, along);
                last = Math.Max(last, along);
            }
        }
    }

    public sealed class PartialBeamSpan
    {
        public BeamSpan Bay;
        public double CoveredFeet;
        public readonly List<(long ElementId, string UniqueId)> CoveringBeams = new List<(long, string)>();
        public readonly List<BeamSpan> PlannedFreeSegments = new List<BeamSpan>();
    }

    public sealed class GridBeamPlan
    {
        public readonly List<BeamSpan> Planned = new List<BeamSpan>();
        public readonly List<BeamSpan> OmittedShort = new List<BeamSpan>();
        public readonly List<(BeamSpan Span, long BeamId, string UniqueId, string CrossingGrid)> OmittedByBeamBox
            = new List<(BeamSpan, long, string, string)>();
        public readonly List<PartialBeamSpan> PartiallyCovered = new List<PartialBeamSpan>();
        public readonly List<(BeamSpan Bay, ExistingFramingSegment Framing)> OtherFramingOnAxis
            = new List<(BeamSpan, ExistingFramingSegment)>();
        public int SuppressedExisting;
    }

    public static class StructuralLayoutRules
    {
        /// <summary>Two positions are the same column location within this. 5 mm.</summary>
        public const double SamePlaceToleranceFeet = 5.0 / 304.8;
        internal const double BeamGeometryEpsilon = 1e-9;
        private const double AxisParallelEpsilon = 1e-12;
        internal const double GridBoxToleranceFeet = 0.01 / 304.8;
        internal const double DefaultMinSpanMm = 150.01;
        private const double DuplicateOverlapFeet = (150.0 + 0.01) / 304.8;

        internal static bool IntersectsFramingDatumBand(double minZ, double maxZ, double datum)
            => minZ <= datum + 10 / 304.8 + BeamGeometryEpsilon &&
               maxZ >= datum - 1000 / 304.8 - BeamGeometryEpsilon;

        /// <summary>Lateral allowance from the beam's location line when its section width is unreadable. 50 mm.</summary>
        public const double UnknownBeamWidthToleranceFeet = 50.0 / 304.8;

        /// <summary>Grids meeting shallower than this are treated as parallel. Degrees.</summary>
        public const double MinCrossingAngleDegrees = 1.0;

        /// <summary>
        /// A framing member within this of a level's height belongs to that level's framing - unless the next level is
        /// closer than twice this, and then the band is half the distance to it. 600 mm.
        /// </summary>
        public const double BeamLevelToleranceFeet = 600.0 / 304.8;

        /// <summary>
        /// THE LEVEL HEIGHT GEOMETRY IS MEASURED AGAINST. Revit's Level.Elevation is reported from the project's elevation
        /// base, which can be the SHARED survey point: a level at +3.00 on a site 2600 m above sea level reads 2603 m there,
        /// while every bounding box, location curve and point create_elements takes is in internal (project-origin)
        /// coordinates. Level.ProjectElevation is the internal one, and it is the only one compared with geometry here.
        /// </summary>
        public static double GeometryHeight(double elevationFeet, double projectElevationFeet) => projectElevationFeet;

        /// <summary>
        /// How far from a level (feet, internal coordinates) a member still counts as that level's: the beam tolerance,
        /// or half the distance to the nearest OTHER level when that is less, so two close levels never share a member.
        /// </summary>
        public static double LevelBand(double levelZ, IEnumerable<double> otherLevelZs)
        {
            double band = BeamLevelToleranceFeet;
            if (otherLevelZs != null)
                foreach (double z in otherLevelZs)
                {
                    double gap = Math.Abs(z - levelZ);
                    if (gap < 1e-9) continue;   // the level itself, or a twin at the same height
                    if (gap / 2 < band) band = gap / 2;
                }
            return band;
        }

        /// <summary>
        /// A column occupies the storey that starts at <paramref name="levelZ"/> when its base is at or below that level
        /// (within the level band) and its top reaches more than the band above it. A column of the storey below that stops
        /// at the level - or a little above it, its top offset into the slab - does not: the storey above is still empty.
        /// Heights in feet, internal coordinates (see <see cref="GeometryHeight"/>).
        /// </summary>
        public static bool ColumnOccupiesLevel(double zMin, double zMax, double levelZ, IEnumerable<double> otherLevelZs = null)
        {
            double band = LevelBand(levelZ, otherLevelZs);
            return zMin <= levelZ + band && zMax > levelZ + band;
        }

        /// <summary>
        /// A readable reference level decides membership. Otherwise use the height band, allowing a drop of up to
        /// one section depth below the level. A lower neighbour owns the midpoint between levels and everything below it.
        /// </summary>
        public static bool BeamBelongsToLevel(long? referenceLevelId, long targetLevelId, double midZ, double levelZ,
                                              IEnumerable<double> otherLevelZs = null, double depthFeet = 0)
        {
            if (referenceLevelId.HasValue) return referenceLevelId.Value == targetLevelId;

            const double epsilon = 1e-9;
            double band = LevelBand(levelZ, otherLevelZs);
            double drop = levelZ - midZ;
            if (drop <= 0) return -drop <= band + epsilon;
            if (drop > Math.Max(band, depthFeet) + epsilon) return false;
            if (otherLevelZs != null)
                foreach (double otherZ in otherLevelZs)
                {
                    double gap = levelZ - otherZ;
                    if (gap > epsilon && drop >= gap / 2 - epsilon) return false;
                }
            return true;
        }

        /// <summary>The height, in mm and internal coordinates, a beam planned for a level is drawn at.</summary>
        public static double BeamEndZMm(double elevationFeet, double projectElevationFeet)
            => Math.Round(GeometryHeight(elevationFeet, projectElevationFeet) * 304.8, 1);

        public const string CodeAlreadyPresent = "already_present";
        public const string CodeNoIntersections = "no_grid_intersections";
        public const string CodeSpanTooShort = "span_too_short";

        /// <summary>
        /// Every pairwise crossing of the segments, WITHIN both extents. Parallel and
        /// near-parallel pairs cross nowhere; a crossing outside a grid's drawn extent
        /// is not a place that grid names. Deterministic order: by grid pair, then
        /// along the first grid.
        /// </summary>
        public static List<GridIntersection> Intersections(IList<GridSegment> grids)
        {
            var result = new List<GridIntersection>();
            if (grids == null) return result;
            for (int i = 0; i < grids.Count; i++)
                for (int j = i + 1; j < grids.Count; j++)
                {
                    GridIntersection crossing = Cross(grids[i], grids[j]);
                    if (crossing != null) result.Add(crossing);
                }
            result.Sort((a, b) =>
            {
                int byGrid = string.CompareOrdinal(a.GridA, b.GridA);
                if (byGrid != 0) return byGrid;
                int byOther = string.CompareOrdinal(a.GridB, b.GridB);
                if (byOther != 0) return byOther;
                return a.TA.CompareTo(b.TA);
            });
            return result;
        }

        private static GridIntersection Cross(GridSegment g1, GridSegment g2)
        {
            double d1x = g1.X2 - g1.X1, d1y = g1.Y2 - g1.Y1;
            double d2x = g2.X2 - g2.X1, d2y = g2.Y2 - g2.Y1;
            double denom = d1x * d2y - d1y * d2x;
            double len1 = Math.Sqrt(d1x * d1x + d1y * d1y), len2 = Math.Sqrt(d2x * d2x + d2y * d2y);
            if (len1 < 1e-9 || len2 < 1e-9) return null;
            double sinAngle = Math.Abs(denom) / (len1 * len2);
            if (sinAngle < Math.Sin(MinCrossingAngleDegrees * Math.PI / 180.0)) return null;
            double t = ((g2.X1 - g1.X1) * d2y - (g2.Y1 - g1.Y1) * d2x) / denom;
            double u = ((g2.X1 - g1.X1) * d1y - (g2.Y1 - g1.Y1) * d1x) / denom;
            if (t < -1e-9 || t > 1 + 1e-9 || u < -1e-9 || u > 1 + 1e-9) return null;
            return new GridIntersection
            {
                X = g1.X1 + t * d1x, Y = g1.Y1 + t * d1y,
                GridA = g1.Name, GridB = g2.Name, GridAId = g1.ElementId, GridBId = g2.ElementId,
                TA = t, TB = u
            };
        }

        /// <summary>
        /// Split the crossings into the ones to place and the ones a column already
        /// occupies. "Occupied" is measured by distance to an existing column position,
        /// never by name - the model is the authority on where its columns are.
        /// </summary>
        public static void DedupColumns(IList<GridIntersection> crossings, IList<double[]> existingXY,
                                        out List<GridIntersection> toPlace, out List<GridIntersection> alreadyPresent)
        {
            toPlace = new List<GridIntersection>();
            alreadyPresent = new List<GridIntersection>();
            var claimed = new List<double[]>();
            foreach (GridIntersection crossing in crossings ?? new List<GridIntersection>())
            {
                bool occupied = false;
                if (existingXY != null)
                    foreach (double[] existing in existingXY)
                        if (Distance(crossing.X, crossing.Y, existing[0], existing[1]) <= SamePlaceToleranceFeet)
                        { occupied = true; break; }
                // Two grids crossing a third at nearly one point: the SECOND crossing
                // of the same spot is a duplicate of the first, not a second column.
                if (!occupied)
                    foreach (double[] mine in claimed)
                        if (Distance(crossing.X, crossing.Y, mine[0], mine[1]) <= SamePlaceToleranceFeet)
                        { occupied = true; break; }
                if (occupied) alreadyPresent.Add(crossing);
                else { toPlace.Add(crossing); claimed.Add(new[] { crossing.X, crossing.Y }); }
            }
        }

        /// <summary>
        /// Consecutive crossings make bays. Measure the union of all qualifying beam overlaps,
        /// then plan its free segments. Model boxes use the 0.01 mm grid tolerance.
        /// Legacy framing measurements treat gaps up to 5 mm as continuous. Non-beam framing only contributes warnings.
        /// </summary>
        public static GridBeamPlan BeamSpans(IList<GridIntersection> crossings, string gridName, long gridId,
                                             IList<ExistingFramingSegment> existingFraming, double minSpanFeet,
                                             IList<double[]> columnBoxes = null,
                                             IList<(double[] Box, long Id, string UniqueId)> beamBoxes = null,
                                             IReadOnlyDictionary<long, GridSegment> grids = null, double proposedWidth = 0,
                                             bool preserveUnsupportedGaps = false)
        {
            var plan = new GridBeamPlan();
            var along = new List<GridIntersection>();
            foreach (GridIntersection crossing in crossings ?? new List<GridIntersection>())
            {
                if (crossing.GridAId == gridId || crossing.GridBId == gridId) along.Add(crossing);
            }
            along.Sort((a, b) => Param(a, gridId).CompareTo(Param(b, gridId)));
            for (int i = 0; i + 1 < along.Count; i++)
            {
                GridIntersection from = along[i], to = along[i + 1];
                double length = Distance(from.X, from.Y, to.X, to.Y);
                if (length <= SamePlaceToleranceFeet) continue;

                // Measure nearby crossings against the actual bay endpoint.
                // The open span is wider than the tolerance, so neighbors can only lie outward.
                int firstAtStart = i, lastAtEnd = i + 1;
                if (beamBoxes != null && proposedWidth > 0 && grids != null)
                {
                    while (firstAtStart > 0 &&
                           Distance(from.X, from.Y, along[firstAtStart - 1].X, along[firstAtStart - 1].Y) <= SamePlaceToleranceFeet)
                        firstAtStart--;
                    while (lastAtEnd + 1 < along.Count &&
                           Distance(to.X, to.Y, along[lastAtEnd + 1].X, along[lastAtEnd + 1].Y) <= SamePlaceToleranceFeet)
                        lastAtEnd++;
                }

                double ux = (to.X - from.X) / length, uy = (to.Y - from.Y) / length;
                var bay = new BeamSpan
                {
                    Grid = gridName, GridId = gridId,
                    X1 = from.X, Y1 = from.Y, X2 = to.X, Y2 = to.Y,
                    FromCrossing = OtherGrid(from, gridId), ToCrossing = OtherGrid(to, gridId)
                };
                var intervals = new List<(double Start, double End, long BeamId, string UniqueId,
                    IList<FramingPlanTriangle> Footprint)>();
                if (beamBoxes != null)
                    foreach (var beam in beamBoxes)
                    {
                        if (TryClipBeamBox(bay, length, ux, uy, beam.Box, out double first, out double last) &&
                            last - first > DuplicateOverlapFeet + BeamGeometryEpsilon)
                            RecordBoxCoverage(first, last, null);

                        RecordCrossingCoverage(firstAtStart, i, fromStart: true);
                        RecordCrossingCoverage(i + 1, lastAtEnd, fromStart: false);

                        void RecordCrossingCoverage(int firstIndex, int lastIndex, bool fromStart)
                        {
                            if (proposedWidth <= 0 || grids == null) return;
                            for (int index = firstIndex; index <= lastIndex; index++)
                            {
                                GridIntersection crossing = along[index];
                                long otherId = crossing.GridAId == gridId ? crossing.GridBId : crossing.GridAId;
                                if (!grids.TryGetValue(otherId, out GridSegment other)) continue;
                                double trim = CrossingTrim(bay, length, ux, uy, proposedWidth, other, beam.Box, fromStart);
                                if (trim > 0)
                                    RecordBoxCoverage(fromStart ? 0 : length - trim, fromStart ? trim : length, other.Name);
                            }
                        }

                        void RecordBoxCoverage(double start, double end, string crossingGrid)
                        {
                            intervals.Add((start, end, beam.Id, beam.UniqueId, null));
                            plan.OmittedByBeamBox.Add((Subspan(start, end), beam.Id, beam.UniqueId, crossingGrid));
                        }
                    }
                bool hasBoxCoverage = intervals.Count > 0;
                if (existingFraming != null)
                    foreach (ExistingFramingSegment framing in existingFraming)
                    {
                        if (framing.Footprint != null)
                        {
                            bool isBeam = framing.NonBeamStructuralType == null;
                            if (isBeam && IsTransverseFootprint(framing, ux, uy)) continue;
                            foreach (FramingPlanTriangle triangle in framing.Footprint)
                            {
                                if (!triangle.TryClip(bay, length, ux, uy, out double first, out double last))
                                    continue;
                                if (isBeam)
                                {
                                    if (last - first > BeamGeometryEpsilon)
                                        intervals.Add((first, last, framing.ElementId, framing.UniqueId, framing.Footprint));
                                }
                                else
                                {
                                    plan.OtherFramingOnAxis.Add((bay, framing));
                                    break;
                                }
                            }
                            continue;
                        }
                        if (!TryOverlap(framing, bay, length, ux, uy, out double start, out double end)) continue;
                        if (framing.NonBeamStructuralType == null)
                            intervals.Add((start, end, framing.ElementId, framing.UniqueId, null));
                        else
                            plan.OtherFramingOnAxis.Add((bay, framing));
                    }

                if (intervals.Count == 0)
                {
                    bool omitShort = length + BeamGeometryEpsilon < minSpanFeet &&
                        (!preserveUnsupportedGaps || ColumnIntervals(columnBoxes, bay, length, ux, uy,
                            GridBoxToleranceFeet).Count > 0);
                    (omitShort ? plan.OmittedShort : plan.Planned).Add(bay);
                    continue;
                }

                intervals.Sort((a, b) =>
                {
                    int byStart = a.Start.CompareTo(b.Start);
                    return byStart != 0 ? byStart : a.BeamId.CompareTo(b.BeamId);
                });
                var partial = new PartialBeamSpan { Bay = bay };
                var supports = ColumnIntervals(columnBoxes, bay, length, ux, uy,
                    hasBoxCoverage ? GridBoxToleranceFeet : 0);
                var coveringFootprints = new HashSet<IList<FramingPlanTriangle>>();
                var coveringBeams = new HashSet<(long BeamId, string UniqueId)>();
                double coveredEnd = 0;
                bool hasGap = false;
                foreach (var interval in intervals)
                {
                    if ((interval.Footprint == null || coveringFootprints.Add(interval.Footprint)) &&
                        coveringBeams.Add((interval.BeamId, interval.UniqueId)))
                        partial.CoveringBeams.Add((interval.BeamId, interval.UniqueId));
                    RecordFreeSegment(coveredEnd, interval.Start);
                    partial.CoveredFeet += Math.Max(0, interval.End - Math.Max(coveredEnd, interval.Start));
                    coveredEnd = Math.Max(coveredEnd, interval.End);
                }
                RecordFreeSegment(coveredEnd, length);
                if (hasGap) plan.PartiallyCovered.Add(partial);
                else plan.SuppressedExisting++;

                // An existing column standing on the bay's axis is not free: a beam measured by its physical body stops at the
                // column face, so the stretch between that face and the crossing must not become a new short beam.
                void RecordFreeSegment(double start, double end)
                {
                    double cursor = start;
                    foreach (var column in supports)
                    {
                        if (column.End <= cursor) continue;
                        if (column.Start >= end) break;
                        if (column.Start > cursor) RecordPiece(cursor, column.Start);
                        cursor = Math.Max(cursor, column.End);
                        if (cursor >= end) return;
                    }
                    RecordPiece(cursor, end);
                }

                void RecordPiece(double start, double end)
                {
                    double freeLength = end - start;
                    double tolerance = hasBoxCoverage ? GridBoxToleranceFeet : SamePlaceToleranceFeet;
                    if (freeLength <= tolerance + BeamGeometryEpsilon) return;
                    hasGap = true;
                    BeamSpan free = Subspan(start, end);
                    bool keepGap = preserveUnsupportedGaps && !TouchesSupport(start, end);
                    if (freeLength + BeamGeometryEpsilon < minSpanFeet && !keepGap)
                        plan.OmittedShort.Add(free);
                    else
                    {
                        plan.Planned.Add(free);
                        partial.PlannedFreeSegments.Add(free);
                    }
                }

                bool TouchesSupport(double start, double end)
                {
                    foreach (var support in supports)
                        if (Math.Abs(support.End - start) <= GridBoxToleranceFeet ||
                            Math.Abs(support.Start - end) <= GridBoxToleranceFeet) return true;
                    return false;
                }

                BeamSpan Subspan(double start, double end) => new BeamSpan
                {
                    Grid = bay.Grid, GridId = bay.GridId,
                    FromCrossing = bay.FromCrossing, ToCrossing = bay.ToCrossing,
                    X1 = bay.X1 + start * ux, Y1 = bay.Y1 + start * uy,
                    X2 = end == length ? bay.X2 : bay.X1 + end * ux,
                    Y2 = end == length ? bay.Y2 : bay.Y1 + end * uy
                };
            }
            return plan;
        }

        /// <summary>A column body (internal Z, feet) that reaches the level height within 5 mm.</summary>
        public static bool ColumnReachesLevel(double minZ, double maxZ, double levelZ)
            => minZ <= levelZ + SamePlaceToleranceFeet && maxZ >= levelZ - SamePlaceToleranceFeet;

        /// <summary>
        /// Sorted, merged stretches of the bay axis (distance from the bay start, feet) that lie inside an existing
        /// column's plan box [minX, minY, maxX, maxY].
        /// </summary>
        internal static List<(double Start, double End)> ColumnIntervals(IList<double[]> columnBoxes, BeamSpan bay,
                                                                          double length, double ux, double uy,
                                                                          double tolerance = 0)
        {
            var raw = new List<(double Start, double End)>();
            if (columnBoxes != null)
                foreach (double[] box in columnBoxes)
                {
                    if (box == null || box.Length < 4) continue;
                    if (TryClipBox(bay, length, ux, uy, box, tolerance, out double lo, out double hi) &&
                        hi - lo > BeamGeometryEpsilon)
                        raw.Add((lo, hi));
                }
            raw.Sort((a, b) => a.Start.CompareTo(b.Start));
            var merged = new List<(double Start, double End)>();
            foreach (var interval in raw)
            {
                if (merged.Count > 0 && interval.Start <= merged[merged.Count - 1].End)
                    merged[merged.Count - 1] = (merged[merged.Count - 1].Start, Math.Max(merged[merged.Count - 1].End, interval.End));
                else merged.Add(interval);
            }
            return merged;

        }

        private static double CrossingTrim(BeamSpan bay, double length, double ux, double uy, double width,
                                           GridSegment crossing, double[] existingBox, bool fromStart)
        {
            double axisLength = Distance(crossing.X1, crossing.Y1, crossing.X2, crossing.Y2);
            if (axisLength <= BeamGeometryEpsilon) return 0;
            double vx = (crossing.X2 - crossing.X1) / axisLength, vy = (crossing.Y2 - crossing.Y1) / axisLength;
            var axis = new BeamSpan { X1 = crossing.X1, Y1 = crossing.Y1 };
            if (!TryClipBeamBox(axis, axisLength, vx, vy, existingBox, out double first, out double last) ||
                last - first <= DuplicateOverlapFeet + BeamGeometryEpsilon) return 0;

            double halfX = Math.Abs(uy) * width / 2, halfY = Math.Abs(ux) * width / 2;
            if (!Duplicates(0)) return 0;

            // Shortening a centered prism nests its plan boxes. Each trim removes
            // overlap monotonically, so locate the first safe endpoint.
            double unsafeTrim = 0, safeTrim = length;
            while (safeTrim - unsafeTrim > BeamGeometryEpsilon)
            {
                double trim = unsafeTrim + (safeTrim - unsafeTrim) / 2;
                if (Duplicates(trim)) unsafeTrim = trim;
                else safeTrim = trim;
            }
            return Math.Min(length, safeTrim + 2 * BeamGeometryEpsilon);

            bool Duplicates(double trim)
            {
                double ax = bay.X1 + (fromStart ? trim * ux : 0), ay = bay.Y1 + (fromStart ? trim * uy : 0);
                double bx = bay.X2 - (fromStart ? 0 : trim * ux), by = bay.Y2 - (fromStart ? 0 : trim * uy);
                var box = new[] { Math.Min(ax, bx) - halfX, Math.Min(ay, by) - halfY,
                                  Math.Max(ax, bx) + halfX, Math.Max(ay, by) + halfY };
                return TryClipBeamBox(axis, axisLength, vx, vy, box, out double start, out double end) &&
                       Math.Min(last, end) - Math.Max(first, start) > DuplicateOverlapFeet + BeamGeometryEpsilon;
            }
        }

        private static bool TryClipBeamBox(BeamSpan axis, double length, double ux, double uy, double[] box,
                                           out double lo, out double hi)
        {
            // On an aligned grid the tolerance applies to its fixed coordinate.
            // An oblique cut uses its actual chord to avoid inflating longitudinal overlap.
            double lateralTolerance = Math.Abs(ux) < AxisParallelEpsilon || Math.Abs(uy) < AxisParallelEpsilon
                ? GridBoxToleranceFeet : 0;
            return TryClipBox(axis, length, ux, uy, box, lateralTolerance, out lo, out hi);
        }

        private static bool TryClipBox(BeamSpan axis, double length, double ux, double uy, double[] box,
                                       double tolerance, out double lo, out double hi)
        {
            lo = 0;
            hi = length;
            return Slab(axis.X1, ux, box[0], box[2], tolerance, ref lo, ref hi) &&
                   Slab(axis.Y1, uy, box[1], box[3], tolerance, ref lo, ref hi);

            static bool Slab(double origin, double direction, double min, double max, double tolerance,
                             ref double lo, ref double hi)
            {
                if (Math.Abs(direction) < AxisParallelEpsilon) return origin >= min - tolerance && origin <= max + tolerance;
                double t1 = (min - origin) / direction, t2 = (max - origin) / direction;
                if (t1 > t2) (t1, t2) = (t2, t1);
                lo = Math.Max(lo, t1);
                hi = Math.Min(hi, t2);
                return hi >= lo;
            }
        }

        private static bool IsTransverseFootprint(ExistingFramingSegment framing, double ux, double uy)
        {
            double dx = framing.X2 - framing.X1, dy = framing.Y2 - framing.Y1;
            double axisAlong = Math.Abs(dx * ux + dy * uy), axisAcross = Math.Abs(dx * uy - dy * ux);
            if (framing.FootprintDirection == FramingFootprintDirection.PlacementAxis)
                return axisAlong <= axisAcross;

            // A wide or rotated section can look transverse even when its reference line runs along the bay.
            // An unknown placement mode can also move the body away from its datum or turn it, so require
            // both the line and the measured body to indicate a crossing before discarding the footprint.
            if (framing.FootprintDirection != FramingFootprintDirection.MeasuredExtents ||
                axisAcross <= axisAlong + BeamGeometryEpsilon) return false;

            double alongMin = double.PositiveInfinity, alongMax = double.NegativeInfinity;
            double sideMin = double.PositiveInfinity, sideMax = double.NegativeInfinity;
            foreach (FramingPlanTriangle triangle in framing.Footprint)
            {
                var along = triangle.ProjectedExtent(ux, uy);
                var side = triangle.ProjectedExtent(uy, -ux);
                alongMin = Math.Min(alongMin, along.Min);
                alongMax = Math.Max(alongMax, along.Max);
                sideMin = Math.Min(sideMin, side.Min);
                sideMax = Math.Max(sideMax, side.Max);
            }
            return sideMax - sideMin > Math.Max(alongMax - alongMin, framing.WidthFeet) + BeamGeometryEpsilon;
        }

        /// <summary>
        /// Tests an unmeasured member's model bounds along a proposed span.
        /// A nominal width gives no lateral clearance without the family's actual placement.
        /// </summary>
        public static bool BoundsMayOverlapBeamLongitudinally(BeamSpan span, double minX, double minY,
                                                               double maxX, double maxY)
        {
            if (!ValidBounds(minX, maxX) || !ValidBounds(minY, maxY)) return true;

            double dx = span.X2 - span.X1, dy = span.Y2 - span.Y1;
            double length = Math.Sqrt(dx * dx + dy * dy);
            if (length <= BeamGeometryEpsilon) return true;
            double ux = dx / length, uy = dy / length;
            double first = ((ux >= 0 ? minX : maxX) - span.X1) * ux +
                           ((uy >= 0 ? minY : maxY) - span.Y1) * uy;
            double last = ((ux >= 0 ? maxX : minX) - span.X1) * ux +
                          ((uy >= 0 ? maxY : minY) - span.Y1) * uy;
            // Allow end-to-end contact, while any positive overlap beyond numerical noise matters.
            return last > BeamGeometryEpsilon && first < length - BeamGeometryEpsilon;
        }

        private static bool ValidBounds(double min, double max) =>
            min <= max && !double.IsInfinity(min) && !double.IsInfinity(max);

        private static bool TryOverlap(ExistingFramingSegment beam, BeamSpan bay, double length, double ux, double uy,
                                       out double overlapStart, out double overlapEnd)
        {
            double x1 = beam.X1 - bay.X1, y1 = beam.Y1 - bay.Y1;
            double x2 = beam.X2 - bay.X1, y2 = beam.Y2 - bay.Y1;
            double along1 = x1 * ux + y1 * uy, along2 = x2 * ux + y2 * uy;
            overlapStart = overlapEnd = 0;
            double lateral1 = x1 * uy - y1 * ux, lateral2 = x2 * uy - y2 * ux;
            double alongRun = along2 - along1, lateralRun = lateral2 - lateral1;
            bool isBeam = beam.NonBeamStructuralType == null;
            // A beam must run farther along the grid than across it to cover the bay.
            // A transverse member can still intersect the strip and produce a nonzero projection.
            if (isBeam && Math.Abs(alongRun) <= Math.Abs(lateralRun)) return false;
            bool hasWidth = beam.WidthFeet > 0 && !double.IsInfinity(beam.WidthFeet);
            double lateralTolerance = hasWidth ? beam.WidthFeet / 2 : UnknownBeamWidthToleranceFeet;
            overlapStart = Math.Max(Math.Min(along1, along2), 0);
            overlapEnd = Math.Min(Math.Max(along1, along2), length);
            if (Math.Abs(alongRun) <= BeamGeometryEpsilon)
                return !isBeam && overlapEnd >= overlapStart - BeamGeometryEpsilon &&
                       Math.Min(lateral1, lateral2) <= lateralTolerance + BeamGeometryEpsilon &&
                       Math.Max(lateral1, lateral2) >= -lateralTolerance - BeamGeometryEpsilon;

            double lateralChangePerFoot = lateralRun / alongRun;
            if (isBeam && hasWidth)
            {
                // The nominal footprint is a rectangle around the adjusted axis. Its end faces
                // meet the grid where the vector from an endpoint is perpendicular to the beam.
                double firstEnd = along1 + lateral1 * lateralChangePerFoot;
                double lastEnd = along2 + lateral2 * lateralChangePerFoot;
                overlapStart = Math.Max(0, Math.Min(firstEnd, lastEnd));
                overlapEnd = Math.Min(length, Math.Max(firstEnd, lastEnd));
                // Convert perpendicular half-width to lateral distance measured across the grid.
                lateralTolerance *= Math.Sqrt(1 + lateralChangePerFoot * lateralChangePerFoot);
            }
            if (overlapEnd - overlapStart <= BeamGeometryEpsilon) return false;

            // Clip the remaining interval at the two side boundaries.
            if (Math.Abs(lateralRun) <= BeamGeometryEpsilon)
                return Math.Max(Math.Abs(lateral1), Math.Abs(lateral2)) <= lateralTolerance + BeamGeometryEpsilon;
            double atNegativeLimit = along1 + (-lateralTolerance - lateral1) / lateralChangePerFoot;
            double atPositiveLimit = along1 + (lateralTolerance - lateral1) / lateralChangePerFoot;
            overlapStart = Math.Max(overlapStart, Math.Min(atNegativeLimit, atPositiveLimit));
            overlapEnd = Math.Min(overlapEnd, Math.Max(atNegativeLimit, atPositiveLimit));
            return overlapEnd - overlapStart > BeamGeometryEpsilon;
        }

        private static double Param(GridIntersection crossing, long gridId) =>
            crossing.GridAId == gridId ? crossing.TA : crossing.TB;

        private static string OtherGrid(GridIntersection crossing, long gridId) =>
            crossing.GridAId == gridId ? crossing.GridB : crossing.GridA;

        private static double Distance(double x1, double y1, double x2, double y2)
        {
            double dx = x1 - x2, dy = y1 - y2;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        public static string Describe(GridIntersection crossing) =>
            (crossing.GridA ?? "?") + " x " + (crossing.GridB ?? "?") + " at (" +
            (crossing.X * 304.8).ToString("0.0", CultureInfo.InvariantCulture) + ", " +
            (crossing.Y * 304.8).ToString("0.0", CultureInfo.InvariantCulture) + ") mm";
    }
}
