// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// Grid crossings, column dedup and beam spans - the arithmetic somebody's
// structure stands on. Millimetres in the helpers, feet in the rules.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class StructuralLayoutRulesTests
    {
        private const double Ft = 304.8;

        private static GridSegment G(string name, long id, double x1, double y1, double x2, double y2)
            => new GridSegment { Name = name, ElementId = id, X1 = x1 / Ft, Y1 = y1 / Ft, X2 = x2 / Ft, Y2 = y2 / Ft };

        private static ExistingFramingSegment B(double x1, double y1, double x2, double y2, double widthMm = 0)
            => new ExistingFramingSegment(x1 / Ft, y1 / Ft, x2 / Ft, y2 / Ft, widthMm / Ft);

        [Fact]
        public void A_two_by_two_grid_has_four_crossings_in_deterministic_order()
        {
            var grids = new List<GridSegment>
            {
                G("A", 1, 0, 0, 0, 10000), G("B", 2, 6000, 0, 6000, 10000),
                G("1", 3, -1000, 2000, 8000, 2000), G("2", 4, -1000, 8000, 8000, 8000)
            };
            List<GridIntersection> crossings = StructuralLayoutRules.Intersections(grids);
            Assert.Equal(4, crossings.Count);
            // Ordered by grid pair, then along the first grid.
            Assert.Equal("A", crossings[0].GridA);
            Assert.Equal(2000 / Ft, crossings[0].Y, 6);
            Assert.Equal(8000 / Ft, crossings[1].Y, 6);
        }

        [Fact]
        public void Parallel_grids_cross_nowhere()
        {
            var grids = new List<GridSegment> { G("A", 1, 0, 0, 0, 10000), G("B", 2, 6000, 0, 6000, 10000) };
            Assert.Empty(StructuralLayoutRules.Intersections(grids));
        }

        [Fact]
        public void A_crossing_beyond_a_grids_drawn_extent_is_not_a_place_that_grid_names()
        {
            // The horizontal grid stops at x=5000; the vertical grid at x=6000 would
            // cross its EXTENSION, which the drawing does not show.
            var grids = new List<GridSegment> { G("1", 1, 0, 2000, 5000, 2000), G("B", 2, 6000, 0, 6000, 10000) };
            Assert.Empty(StructuralLayoutRules.Intersections(grids));
        }

        [Fact]
        public void An_existing_column_suppresses_its_crossing_by_distance_not_by_name()
        {
            var grids = new List<GridSegment>
            {
                G("A", 1, 0, 0, 0, 10000), G("1", 2, -1000, 2000, 8000, 2000)
            };
            List<GridIntersection> crossings = StructuralLayoutRules.Intersections(grids);
            var existing = new List<double[]> { new[] { 0.003 / 1, 2000 / Ft } }; // ~1 mm off in x (feet)
            existing[0][0] = 1.0 / Ft; // 1 mm from the crossing
            StructuralLayoutRules.DedupColumns(crossings, existing,
                out List<GridIntersection> place, out List<GridIntersection> present);
            Assert.Empty(place);
            Assert.Single(present);
        }

        [Fact]
        public void Two_crossings_at_one_physical_spot_place_one_column()
        {
            // Three grids through one point: three pairwise crossings, one column.
            var grids = new List<GridSegment>
            {
                G("A", 1, -5000, 0, 5000, 0), G("B", 2, 0, -5000, 0, 5000), G("C", 3, -5000, -5000, 5000, 5000)
            };
            List<GridIntersection> crossings = StructuralLayoutRules.Intersections(grids);
            Assert.Equal(3, crossings.Count);
            StructuralLayoutRules.DedupColumns(crossings, null,
                out List<GridIntersection> place, out List<GridIntersection> present);
            Assert.Single(place);
            Assert.Equal(2, present.Count);
        }

        [Fact]
        public void Beam_spans_join_consecutive_crossings_along_one_grid()
        {
            var grids = new List<GridSegment>
            {
                G("1", 10, 0, 0, 18000, 0),
                G("A", 1, 0, -1000, 0, 1000), G("B", 2, 6000, -1000, 6000, 1000), G("C", 3, 18000, -1000, 18000, 1000)
            };
            List<GridIntersection> crossings = StructuralLayoutRules.Intersections(grids);
            GridBeamPlan plan = StructuralLayoutRules.BeamSpans(crossings, "1", 10, null, 100 / Ft);
            List<BeamSpan> spans = plan.Planned;
            int existing = plan.SuppressedExisting;
            int shortOnes = plan.OmittedShort.Count;
            Assert.Equal(2, spans.Count);
            Assert.Equal(0, existing); Assert.Equal(0, shortOnes);
            Assert.Equal("A", spans[0].FromCrossing);
            Assert.Equal("B", spans[0].ToCrossing);
            Assert.Equal("B", spans[1].FromCrossing);
            Assert.Equal("C", spans[1].ToCrossing);
        }

        [Fact]
        public void An_existing_beam_suppresses_its_span_and_is_counted()
        {
            var grids = new List<GridSegment>
            {
                G("1", 10, 0, 0, 12000, 0),
                G("A", 1, 0, -1000, 0, 1000), G("B", 2, 6000, -1000, 6000, 1000), G("C", 3, 12000, -1000, 12000, 1000)
            };
            List<GridIntersection> crossings = StructuralLayoutRules.Intersections(grids);
            var existingBeams = new[] { B(0, 0, 6000, 0) }; // the A-B span
            GridBeamPlan plan = StructuralLayoutRules.BeamSpans(crossings, "1", 10, existingBeams, 100 / Ft);
            List<BeamSpan> spans = plan.Planned;
            int existing = plan.SuppressedExisting;
            int shortOnes = plan.OmittedShort.Count;
            Assert.Single(spans);
            Assert.Equal(1, existing);
            Assert.Equal("B", spans[0].FromCrossing);
        }

        [Fact]
        public void One_long_beam_suppresses_all_five_spans_it_covers_with_tolerance()
        {
            var grids = new[]
            {
                G("1", 10, 0, 0, 20000, 0), G("A", 1, 0, -1000, 0, 1000),
                G("B", 2, 4000, -1000, 4000, 1000), G("B'", 3, 6600, -1000, 6600, 1000),
                G("C", 4, 8000, -1000, 8000, 1000), G("D", 5, 12500, -1000, 12500, 1000),
                G("E", 6, 17000, -1000, 17000, 1000), G("F", 7, 20000, -1000, 20000, 1000)
            };
            var existingBeams = new[] { B(16996, 4, 4, 4) };
            GridBeamPlan plan = StructuralLayoutRules.BeamSpans(StructuralLayoutRules.Intersections(grids), "1", 10, existingBeams, 100 / Ft);
            List<BeamSpan> spans = plan.Planned;
            int existing = plan.SuppressedExisting;
            Assert.Equal(5, existing);
            BeamSpan remaining = Assert.Single(spans);
            Assert.Equal("E", remaining.FromCrossing);
            Assert.Equal("F", remaining.ToCrossing);
        }

        [Theory]
        [InlineData(0, 50.1, 6000, 50.1)]    // on a parallel line outside the fallback tolerance
        [InlineData(-6000, 0, 0, 0)]         // touches an end with no overlap
        [InlineData(3000, -500, 3000, 500)]  // projects to one station on the bay
        public void A_beam_must_overlap_and_stay_within_lateral_tolerance(double x1, double y1, double x2, double y2)
        {
            var grids = new[]
            {
                G("1", 10, 0, 0, 6000, 0), G("A", 1, 0, -1000, 0, 1000), G("B", 2, 6000, -1000, 6000, 1000)
            };
            GridBeamPlan plan = StructuralLayoutRules.BeamSpans(StructuralLayoutRules.Intersections(grids), "1", 10,
                new[] { B(x1, y1, x2, y2) }, 100 / Ft);
            List<BeamSpan> spans = plan.Planned;
            int existing = plan.SuppressedExisting;
            Assert.Single(spans);
            Assert.Equal(0, existing);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void An_oblique_beam_covers_a_full_bay_and_only_part_of_the_next_bay(bool reverse)
        {
            var grids = new[]
            {
                G("1", 10, 0, 0, 12000, 0), G("A", 1, 0, -1000, 0, 1000),
                G("B", 2, 4000, -1000, 4000, 1000), G("C", 3, 12000, -1000, 12000, 1000)
            };
            var beam = reverse ? B(12000, 240, 0, 0, 300) : B(0, 0, 12000, 240, 300);
            GridBeamPlan plan = StructuralLayoutRules.BeamSpans(StructuralLayoutRules.Intersections(grids), "1", 10,
                new[] { beam }, 300 / Ft);
            List<BeamSpan> spans = plan.Planned;
            int suppressed = plan.SuppressedExisting;
            int shortSpans = plan.OmittedShort.Count;

            // A–B stays within 150 mm of the beam axis. B–C exits through a long side.
            Assert.Equal(1, suppressed);
            Assert.Equal(0, shortSpans);
            BeamSpan remaining = Assert.Single(spans);
            Assert.Equal(("B", "C"), (remaining.FromCrossing, remaining.ToCrossing));
            Assert.Equal(7501.499850, remaining.X1 * Ft, 6);
            Assert.Equal(12000, remaining.X2 * Ft, 6);
            PartialBeamSpan partial = Assert.Single(plan.PartiallyCovered);
            Assert.Equal(3501.499850, partial.CoveredFeet * Ft, 6);
            Assert.Same(remaining, Assert.Single(partial.PlannedFreeSegments));
        }

        [Theory]
        [InlineData(-1000, 1000)]
        [InlineData(1000, -1000)]
        public void An_oblique_beam_crossing_both_lateral_limits_leaves_two_free_segments(double y1, double y2)
        {
            var grids = new[]
            {
                G("1", 10, 0, 0, 6000, 0), G("A", 1, 0, -1000, 0, 1000), G("B", 2, 6000, -1000, 6000, 1000)
            };
            GridBeamPlan plan = StructuralLayoutRules.BeamSpans(StructuralLayoutRules.Intersections(grids), "1", 10,
                new[] { B(0, y1, 6000, y2) }, 300 / Ft);

            Assert.Equal(300, Assert.Single(plan.PartiallyCovered).CoveredFeet * Ft, 6);
            Assert.Collection(plan.Planned, left =>
            {
                Assert.Equal(0, left.X1 * Ft, 6);
                Assert.Equal(2850, left.X2 * Ft, 6);
            }, right =>
            {
                Assert.Equal(3150, right.X1 * Ft, 6);
                Assert.Equal(6000, right.X2 * Ft, 6);
            });
        }

        [Theory]
        [InlineData(300, 149, 0, 1)]  // the measured width allows more than the fallback
        [InlineData(300, -151, 0, 0)] // the grid is outside the beam footprint
        [InlineData(60, 37.5, 0, 0)] // the 50 mm fallback does not override a narrow measured width
        [InlineData(0, 50, 0, 1)]     // inclusive boundary of the fallback for unreadable width
        public void Beam_section_width_controls_lateral_coverage_only(
            double widthMm, double offsetMm, double startMm, int expectedSuppressed)
        {
            var grids = new[]
            {
                G("1", 10, 0, 0, 6000, 0), G("A", 1, 0, -1000, 0, 1000), G("B", 2, 6000, -1000, 6000, 1000)
            };
            GridBeamPlan plan = StructuralLayoutRules.BeamSpans(StructuralLayoutRules.Intersections(grids), "1", 10,
                new[] { B(startMm, offsetMm, 6000, offsetMm, widthMm) }, 300 / Ft);
            List<BeamSpan> spans = plan.Planned;
            int suppressed = plan.SuppressedExisting;
            Assert.Equal(expectedSuppressed, suppressed);
            Assert.Equal(1 - expectedSuppressed, spans.Count);
        }

        [Theory]
        [InlineData(694, 1)] // offset -150 mm, outside the 100 mm band set by the nearby level above
        [InlineData(695, 0)] // same XY and Z, but a different reference level
        public void Only_a_beam_referencing_the_target_level_suppresses_its_span(long referenceLevelId, int expectedSuppressed)
        {
            double[] otherLevels = { 0, 3200 / Ft };
            var existingBeams = new List<ExistingFramingSegment>();
            if (StructuralLayoutRules.BeamBelongsToLevel(referenceLevelId, 694, 2850 / Ft, 3000 / Ft, otherLevels))
                existingBeams.Add(B(0, 0, 6000, 0));
            var grids = new[]
            {
                G("1", 10, 0, 0, 6000, 0), G("A", 1, 0, -1000, 0, 1000), G("B", 2, 6000, -1000, 6000, 1000)
            };
            GridBeamPlan plan = StructuralLayoutRules.BeamSpans(StructuralLayoutRules.Intersections(grids), "1", 10, existingBeams, 100 / Ft);
            List<BeamSpan> spans = plan.Planned;
            int suppressed = plan.SuppressedExisting;
            Assert.Equal(expectedSuppressed, suppressed);
            Assert.Equal(1 - expectedSuppressed, spans.Count);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Full_level_two_plan_has_no_longitudinal_overlap_with_on_axis_existing_beams(bool angledExtension)
        {
            // The 18 crossings recovered from the captured plan fix the grid segments
            // between the outermost crossings. Free grid ends are not in the capture.
            var grids = new[]
            {
                G("A", 1, 0, -3000, 0, 6000), G("B", 2, 4000, -3000, 4000, 6000),
                G("B'", 3, 6600, -3000, 6600, 6000), G("C", 4, 8000, -3000, 8000, 6000),
                G("D", 5, 12500, -3000, 12500, 6000), G("E", 6, 17000, -3000, 17000, 6000),
                G("1", 7, 0, -3000, 17000, -3000), G("2", 8, 0, 0, 17000, 0),
                G("3", 9, 0, 6000, 17000, 6000)
            };
            // The 14 captured beams, by element id, with their XY endpoints in mm.
            // All reference level 694 and have Z=2850 mm, 150 mm below that level.
            var capturedBeams = new Dictionary<long, ExistingFramingSegment>
            {
                [1372162] = B(0, 0, 8000, 0),
                [1372164] = B(0, 6037.5, 8000, 6037.5),
                [1372165] = B(-150, -3000, 8150, -3000),
                [1372166] = B(8000, 0, 17000, 0),
                [1372168] = B(8000, 6000, 17000, 6000),
                [1372169] = B(12500, 0, 12500, 6000),
                [1372171] = B(17000, 0, 17000, 6000),
                [1373017] = B(0, -3000, 0, 0),
                [1373018] = B(4000, -3000, 4000, 4600),
                [1373019] = B(8000, -3000, 8000, 6000),
                [1376473] = B(0, 0, 0, 6000),
                [1377215] = B(0, 4600, 8000, 4600),
                [1377217] = B(1550, 4600, 1550, 6000),
                [1377219] = B(6600, 4600, 6600, 6000)
            };
            // In the synthetic variant an extension leaves the lateral allowance
            // outside the grid, while both bays still qualify over their own overlap.
            if (angledExtension) capturedBeams[1373018] = B(3920, -9000, 4000, 4600);
            const long levelId = 694;
            var existingBeams = new List<ExistingFramingSegment>();
            foreach (ExistingFramingSegment beam in capturedBeams.Values)
                if (StructuralLayoutRules.BeamBelongsToLevel(694, levelId, 2850 / Ft, 3000 / Ft))
                    existingBeams.Add(beam);
            Assert.Equal(14, existingBeams.Count);

            var uncovered = new[]
            {
                ("B", "2", "3"), ("B'", "1", "2"), ("B'", "2", "3"), ("D", "1", "2"),
                ("E", "1", "2"), ("1", "C", "D"), ("1", "D", "E")
            };
            List<GridIntersection> crossings = StructuralLayoutRules.Intersections(grids);
            Assert.Equal(18, crossings.Count);
            var all = new List<(string, string, string)>();
            var planned = new List<(string, string, string)>();
            var partial = new List<(string, string, string, double)>();
            bool checkedAngledBeam = false;
            int suppressed = 0;
            foreach (GridSegment grid in grids)
            {
                GridBeamPlan candidatePlan = StructuralLayoutRules.BeamSpans(crossings, grid.Name, grid.ElementId, null, 300 / Ft);
                List<BeamSpan> candidates = candidatePlan.Planned;
                GridBeamPlan plan = StructuralLayoutRules.BeamSpans(crossings, grid.Name, grid.ElementId, existingBeams, 300 / Ft);
                List<BeamSpan> spans = plan.Planned;
                int existing = plan.SuppressedExisting;
                int shortSpans = plan.OmittedShort.Count;
                all.AddRange(candidates.Select(span => (span.Grid, span.FromCrossing, span.ToCrossing)));
                planned.AddRange(spans.Select(span => (span.Grid, span.FromCrossing, span.ToCrossing)));
                partial.AddRange(plan.PartiallyCovered.Select(span =>
                    (span.Bay.Grid, span.Bay.FromCrossing, span.Bay.ToCrossing, Math.Round(span.CoveredFeet * Ft, 6))));
                foreach (BeamSpan span in spans)
                {
                    BeamSpan bay = candidates.Single(candidate => candidate.FromCrossing == span.FromCrossing &&
                                                                     candidate.ToCrossing == span.ToCrossing);
                    bool vertical = Math.Abs(span.X2 - span.X1) < 1e-9;
                    double axis = vertical ? span.X1 : span.Y1;
                    double spanStart = vertical ? span.Y1 : span.X1;
                    double spanEnd = vertical ? span.Y2 : span.X2;
                    double bayStart = vertical ? bay.Y1 : bay.X1;
                    double bayEnd = vertical ? bay.Y2 : bay.X2;
                    foreach (var entry in capturedBeams)
                    {
                        ExistingFramingSegment beam = entry.Value;
                        double lateralA = vertical ? beam.X1 : beam.Y1;
                        double lateralB = vertical ? beam.X2 : beam.Y2;
                        double alongA = vertical ? beam.Y1 : beam.X1;
                        double alongB = vertical ? beam.Y2 : beam.X2;
                        // Independent check in beam parameter space: find where its
                        // centreline enters and exits the lateral allowance, then project.
                        double allowance = 50 / Ft;
                        double t0 = 0, t1 = 1;
                        if (lateralA == lateralB)
                        {
                            if (Math.Abs(lateralA - axis) > allowance) continue;
                        }
                        else
                        {
                            double atLowerEdge = (axis - allowance - lateralA) / (lateralB - lateralA);
                            double atUpperEdge = (axis + allowance - lateralA) / (lateralB - lateralA);
                            t0 = Math.Max(t0, Math.Min(atLowerEdge, atUpperEdge));
                            t1 = Math.Min(t1, Math.Max(atLowerEdge, atUpperEdge));
                        }
                        if (t1 <= t0) continue;
                        double alongStart = alongA + t0 * (alongB - alongA);
                        double alongEnd = alongA + t1 * (alongB - alongA);
                        double clippedStart = Math.Max(bayStart, Math.Min(alongStart, alongEnd));
                        double clippedEnd = Math.Min(bayEnd, Math.Max(alongStart, alongEnd));
                        if (clippedEnd <= clippedStart) continue;
                        if (span.Grid == "B" && span.FromCrossing == "2" && entry.Key == 1373018 &&
                            Math.Abs(lateralA - axis) * Ft > 50) checkedAngledBeam = true;
                        double overlapMm = (Math.Min(spanEnd, clippedEnd) - Math.Max(spanStart, clippedStart)) * Ft;
                        Assert.True(overlapMm <= 1e-6,
                            $"Grid {span.Grid}, {span.FromCrossing}–{span.ToCrossing}, overlaps beam {entry.Key} by {overlapMm:F3} mm.");
                    }
                }
                suppressed += existing;
                Assert.Equal(0, shortSpans);
            }

            Assert.Equal(27, all.Count);
            Assert.Empty(planned.Intersect(all.Except(uncovered)));
            Assert.Equal(uncovered, planned.ToArray());
            Assert.Equal(20, suppressed);
            Assert.Equal(new[] { ("B", "2", "3", 4600.0), ("B'", "2", "3", 1400.0), ("1", "C", "D", 150.0) }, partial);
            Assert.Equal(angledExtension, checkedAngledBeam);
        }

        [Theory]
        [InlineData("B", 4000, -3000, 4600, 4600, 6000)]
        [InlineData("B'", 6600, 4600, 6000, 0, 4600)]
        public void Measured_partial_beam_leaves_only_the_free_part_of_the_bay(
            string grid, double x, double beamStart, double beamEnd, double freeStart, double freeEnd)
        {
            var grids = new[]
            {
                G(grid, 10, x, 0, x, 6000), G("2", 2, 0, 0, 8000, 0), G("3", 3, 0, 6000, 8000, 6000)
            };
            GridBeamPlan plan = StructuralLayoutRules.BeamSpans(StructuralLayoutRules.Intersections(grids), grid, 10,
                new[] { B(x, beamStart, x, beamEnd) }, 300 / Ft);
            List<BeamSpan> spans = plan.Planned;
            int existing = plan.SuppressedExisting;
            int shortOnes = plan.OmittedShort.Count;
            BeamSpan free = Assert.Single(spans);
            Assert.Equal(freeStart, free.Y1 * Ft, 6);
            Assert.Equal(freeEnd, free.Y2 * Ft, 6);
            Assert.Equal(0, existing);
            Assert.Equal(0, shortOnes);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(5)]
        public void Beams_in_series_cover_the_span_within_longitudinal_tolerance(double gap)
        {
            GridBeamPlan plan = StructuralLayoutRules.BeamSpans(SingleBayCrossings(), "1", 10,
                new[] { B(6000, 0, 3000 + gap, 0), B(0, 0, 3000, 0) }, 0);
            List<BeamSpan> spans = plan.Planned;
            int existing = plan.SuppressedExisting;
            int shortOnes = plan.OmittedShort.Count;
            Assert.Empty(spans);
            Assert.Equal(1, existing);
            Assert.Equal(0, shortOnes);
        }

        [Fact]
        public void Overlapping_existing_beams_leave_a_plannable_middle_gap()
        {
            GridBeamPlan plan = StructuralLayoutRules.BeamSpans(SingleBayCrossings(), "1", 10,
                new[] { B(3900, 0, 6000, 0), B(1000, 0, 2100, 0), B(0, 0, 2000, 0) }, 1800 / Ft);
            List<BeamSpan> spans = plan.Planned;
            int existing = plan.SuppressedExisting;
            int shortOnes = plan.OmittedShort.Count;
            BeamSpan free = Assert.Single(spans);
            Assert.Equal(2100, free.X1 * Ft, 6);
            Assert.Equal(3900, free.X2 * Ft, 6);
            Assert.Equal(0, existing);
            Assert.Equal(0, shortOnes);
        }

        [Fact]
        public void A_free_end_over_the_longitudinal_tolerance_but_below_the_minimum_is_omitted()
        {
            GridBeamPlan plan = StructuralLayoutRules.BeamSpans(SingleBayCrossings(), "1", 10,
                new[] { B(6, 37.5, 6000, 37.5, 300) }, 300 / Ft);
            List<BeamSpan> spans = plan.Planned;
            int existing = plan.SuppressedExisting;
            int shortOnes = plan.OmittedShort.Count;
            Assert.Empty(spans);
            Assert.Equal(0, existing);
            Assert.Equal(1, shortOnes);
        }

        private static List<GridIntersection> SingleBayCrossings()
            => StructuralLayoutRules.Intersections(new[]
            {
                G("1", 10, 0, 0, 6000, 0), G("A", 1, 0, -1000, 0, 1000), G("B", 2, 6000, -1000, 6000, 1000)
            });

        [Fact]
        public void A_readable_other_level_overrides_geometry_even_at_the_target_elevation()
            => Assert.False(StructuralLayoutRules.BeamBelongsToLevel(695, 694, 3000 / Ft, 3000 / Ft));

        [Fact]
        public void A_span_shorter_than_the_minimum_is_omitted_and_counted()
        {
            var grids = new List<GridSegment>
            {
                G("1", 10, 0, 0, 12000, 0),
                G("A", 1, 0, -1000, 0, 1000), G("A2", 2, 20, -1000, 20, 1000), G("C", 3, 12000, -1000, 12000, 1000)
            };
            List<GridIntersection> crossings = StructuralLayoutRules.Intersections(grids);
            GridBeamPlan plan = StructuralLayoutRules.BeamSpans(crossings, "1", 10, null, 100 / Ft);
            List<BeamSpan> spans = plan.Planned;
            int shortOnes = plan.OmittedShort.Count;
            Assert.Single(spans);       // A2 -> C survives
            Assert.Equal(1, shortOnes); // A -> A2 (20 mm) is named, not silently dropped
        }

        private static readonly double[] Storeys = { 0.0, 3000.0, 6000.0 };

        private static double[] Others(double levelMm, params double[] levelsMm)
            => System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Select(
                   System.Linq.Enumerable.Where(levelsMm, z => z != levelMm), z => z / Ft));

        [Theory]
        [InlineData(0.0, 3000.0, 0.0, true)]       // the column of this storey stands here
        [InlineData(0.0, 3000.0, 3000.0, false)]   // the column below stops AT this level: the next storey is empty
        [InlineData(0.0, 3030.0, 3000.0, false)]   // the column below runs 30 mm into the slab above: still empty
        [InlineData(0.0, 3600.0, 3000.0, false)]   // ... 600 mm above the level: the band is inclusive, still the storey below
        [InlineData(0.0, 3601.0, 3000.0, true)]    // ... more than the band above it: it does stand in this storey
        [InlineData(0.0, 6000.0, 3000.0, true)]    // a two-storey column passes through the level
        [InlineData(3000.0, 6000.0, 3000.0, true)]
        [InlineData(3010.0, 6000.0, 3000.0, true)] // a base 10 mm above the level is this storey's
        [InlineData(3030.0, 6000.0, 3000.0, true)] // ... and 30 mm above it too
        [InlineData(6000.0, 9000.0, 3000.0, false)] // the column of the storey above
        public void A_column_makes_a_crossing_present_only_on_the_storey_it_stands_on(double zMinMm, double zMaxMm, double levelMm, bool stands)
            => Assert.Equal(stands, StructuralLayoutRules.ColumnOccupiesLevel(zMinMm / Ft, zMaxMm / Ft, levelMm / Ft, Others(levelMm, Storeys)));

        [Theory]
        [InlineData(3000.0, 3000.0, true)]
        [InlineData(2700.0, 3000.0, true)]   // dropped 300 mm under the slab
        [InlineData(2400.0, 3000.0, true)]   // 600 mm: the edge of the band, inclusive
        [InlineData(2399.0, 3000.0, false)]  // 601 mm: outside it
        [InlineData(3600.0, 3000.0, true)]
        [InlineData(3601.0, 3000.0, false)]
        [InlineData(0.0, 3000.0, false)]     // the beam of the storey below
        public void A_beam_covers_a_span_only_on_its_own_level(double midMm, double levelMm, bool belongs)
            => Assert.Equal(belongs, StructuralLayoutRules.BeamBelongsToLevel(null, 694, midMm / Ft, levelMm / Ft, Others(levelMm, Storeys)));

        [Theory]
        [InlineData(3000.0, 3000.0, true)]
        [InlineData(3400.0, 3000.0, true)]
        [InlineData(3500.0, 3000.0, true)]   // half way to the level 1 m above: the band is half the gap, inclusive
        [InlineData(3501.0, 3000.0, false)]  // nearer the level above: its beam, not this one's
        [InlineData(3501.0, 4000.0, true)]
        [InlineData(3500.0, 4000.0, false)]  // the lower level owns the boundary
        [InlineData(3550.0, 3000.0, false)]  // 550 mm is inside 600 mm but past half of a 1 m gap
        public void Levels_1_m_apart_split_the_band_between_them(double midMm, double levelMm, bool belongs)
            => Assert.Equal(belongs, StructuralLayoutRules.BeamBelongsToLevel(null, 694, midMm / Ft, levelMm / Ft, Others(levelMm, 0.0, 3000.0, 4000.0)));

        [Theory]
        [InlineData(2850, 0, 500, true)]      // a level 200 mm above narrows the standard band to 100 mm
        [InlineData(2500, 0, 500, true)]      // exactly one section depth below
        [InlineData(2499, 0, 500, false)]
        [InlineData(2850, 0, 0, false)]       // no readable depth: use the standard band
        [InlineData(3150, 0, 500, false)]     // depth only extends the band below the level
        [InlineData(2500, 2200, 500, false)]  // a lower neighbour is closer
        public void An_unreadable_reference_uses_the_height_band_and_section_depth(
            double midMm, double lowerLevelMm, double depthMm, bool belongs)
            => Assert.Equal(belongs, StructuralLayoutRules.BeamBelongsToLevel(null, 694, midMm / Ft, 3000 / Ft,
                Others(3000, lowerLevelMm, 3000, 3200), depthMm / Ft));

        [Fact]
        public void A_column_between_levels_1_m_apart_uses_half_the_gap()
        {
            double[] levels = { 3000.0, 4000.0 };
            // Runs 600 mm above the 3000 level: beyond half the 1 m gap (500 mm), so it stands in that storey.
            Assert.True(StructuralLayoutRules.ColumnOccupiesLevel(0, 3600 / Ft, 3000 / Ft, Others(3000.0, levels)));
            Assert.False(StructuralLayoutRules.ColumnOccupiesLevel(0, 3499 / Ft, 3000 / Ft, Others(3000.0, levels)));
            Assert.Equal(500.0, StructuralLayoutRules.LevelBand(3000 / Ft, Others(3000.0, levels)) * Ft, 6);
            Assert.Equal(600.0, StructuralLayoutRules.LevelBand(3000 / Ft, null) * Ft, 6);
        }

        [Fact]
        public void Geometry_is_matched_against_the_internal_level_height_never_the_shared_elevation()
        {
            // A level at +3.00 in internal coordinates whose elevation base is a survey point 2600 m up: Elevation reads
            // 2603 m, ProjectElevation 3 m. Boxes and location curves are internal, so only ProjectElevation is compared.
            double elevation = 2603000 / Ft, project = 3000 / Ft;
            double z = StructuralLayoutRules.GeometryHeight(elevation, project);
            Assert.Equal(3000.0, z * Ft, 6);
            Assert.Equal(3000.0, StructuralLayoutRules.BeamEndZMm(elevation, project), 6);
            // The column of the storey below stops at internal 3000 and the one of this storey starts there.
            Assert.False(StructuralLayoutRules.ColumnOccupiesLevel(0, 3000 / Ft, z));
            Assert.True(StructuralLayoutRules.ColumnOccupiesLevel(3000 / Ft, 6000 / Ft, z));
            Assert.True(StructuralLayoutRules.BeamBelongsToLevel(null, 694, 2950 / Ft, z));
            // Read against Elevation instead, every one of them would be 2600 m off and nothing would match.
            Assert.False(StructuralLayoutRules.ColumnOccupiesLevel(3000 / Ft, 6000 / Ft, elevation));
            Assert.False(StructuralLayoutRules.BeamBelongsToLevel(null, 694, 2950 / Ft, elevation));
        }

        [Theory]
        [InlineData(700)]
        [InlineData(600)]
        [InlineData(400)]
        public void A_beam_body_stopping_at_existing_column_faces_covers_the_bay(double columnMm)
        {
            var grids = new[]
            {
                G("1", 10, 0, 0, 6000, 0), G("A", 1, 0, -1000, 0, 1000), G("B", 2, 6000, -1000, 6000, 1000)
            };
            double h = columnMm / 2;
            // A rotated 200 x 600 section measured by its body: it ends at the column faces, not at the crossings.
            var body = new[]
            {
                new FramingPlanTriangle(h / Ft, -300 / Ft, (6000 - h) / Ft, -300 / Ft, (6000 - h) / Ft, 300 / Ft),
                new FramingPlanTriangle(h / Ft, -300 / Ft, (6000 - h) / Ft, 300 / Ft, h / Ft, 300 / Ft)
            };
            var beam = new ExistingFramingSegment(0, 0, 6000 / Ft, 0, 600 / Ft, 77, "beam-77", null, body,
                FramingFootprintDirection.PlacementAxis);
            var columns = new[]
            {
                new[] { -h / Ft, -h / Ft, h / Ft, h / Ft },
                new[] { (6000 - h) / Ft, -h / Ft, (6000 + h) / Ft, h / Ft }
            };

            GridBeamPlan plan = StructuralLayoutRules.BeamSpans(StructuralLayoutRules.Intersections(grids), "1", 10,
                new[] { beam }, 300 / Ft, columns);

            Assert.Empty(plan.Planned);
            Assert.Empty(plan.OmittedShort);
            Assert.Empty(plan.PartiallyCovered);
            Assert.Equal(1, plan.SuppressedExisting);
        }

        [Fact]
        public void Columns_trim_only_the_free_stretch_they_occupy_and_never_a_bay_without_beams()
        {
            var grids = new[]
            {
                G("1", 10, 0, 0, 12000, 0), G("A", 1, 0, -1000, 0, 1000),
                G("B", 2, 6000, -1000, 6000, 1000), G("C", 3, 12000, -1000, 12000, 1000)
            };
            // The beam body covers 350..3000 of A-B; columns of 700 mm stand at A, B and C.
            var body = new[]
            {
                new FramingPlanTriangle(350 / Ft, -100 / Ft, 3000 / Ft, -100 / Ft, 3000 / Ft, 100 / Ft),
                new FramingPlanTriangle(350 / Ft, -100 / Ft, 3000 / Ft, 100 / Ft, 350 / Ft, 100 / Ft)
            };
            var beam = new ExistingFramingSegment(0, 0, 3000 / Ft, 0, 200 / Ft, 78, "beam-78", null, body,
                FramingFootprintDirection.PlacementAxis);
            var columns = new[] { 0.0, 6000, 12000 }
                .Select(x => new[] { (x - 350) / Ft, -350 / Ft, (x + 350) / Ft, 350 / Ft }).ToArray();

            GridBeamPlan plan = StructuralLayoutRules.BeamSpans(StructuralLayoutRules.Intersections(grids), "1", 10,
                new[] { beam }, 300 / Ft, columns);

            // A-B: only 3000..5650 is free (the column at B occupies 5650..6000).
            PartialBeamSpan partial = Assert.Single(plan.PartiallyCovered);
            BeamSpan free = Assert.Single(partial.PlannedFreeSegments);
            Assert.Equal(3000, free.X1 * Ft, 6);
            Assert.Equal(5650, free.X2 * Ft, 6);
            // B-C has no beam: it is still planned crossing to crossing, as before.
            Assert.Contains(plan.Planned, span => span.FromCrossing == "B" && span.ToCrossing == "C" &&
                                                  Math.Abs(span.X1 * Ft - 6000) < 1e-6 && Math.Abs(span.X2 * Ft - 12000) < 1e-6);
            Assert.Equal(2, plan.Planned.Count);
        }

        [Fact]
        public void A_column_reaches_a_level_from_below_or_above_within_five_mm()
        {
            double level = 3000 / Ft;
            Assert.True(StructuralLayoutRules.ColumnReachesLevel(0, 3000 / Ft, level));
            Assert.True(StructuralLayoutRules.ColumnReachesLevel(3004 / Ft, 6000 / Ft, level));
            Assert.False(StructuralLayoutRules.ColumnReachesLevel(0, 2990 / Ft, level));
            Assert.False(StructuralLayoutRules.ColumnReachesLevel(3010 / Ft, 6000 / Ft, level));
        }
}
}
