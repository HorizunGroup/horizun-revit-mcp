using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    /// <summary>
    /// THE NEAR-MISS IS THE FINDING.
    ///
    /// An exact duplicate is something Revit will often stop you making. Two
    /// levels named "L02" and "Level 02" one millimetre apart collide on neither
    /// name nor elevation, so nothing anywhere warns about them - and every
    /// element on the second is invisible to every schedule filtered on the
    /// first. That is the case these rules exist for.
    ///
    /// The other half is the false positive: a building rotated thirty degrees
    /// has every grid off the world axes and nothing wrong with it. The dominant
    /// angle is measured from the grids themselves so the rule reports the odd
    /// one out rather than the site plan.
    /// </summary>
    public class DatumRulesTests
    {
        private static LevelFact L(long id, string name, double? mm, int? views = null, long? elements = null)
        {
            return new LevelFact
            {
                ElementId = id, Name = name, NameReadable = name != null,
                ElevationMm = mm, ViewCount = views, ElementCount = elements
            };
        }

        private static GridFact G(long id, string name, double x1, double y1, double x2, double y2)
        {
            return new GridFact
            {
                ElementId = id, Name = name, NameReadable = true, GeometryReadable = true,
                X1Mm = x1, Y1Mm = y1, X2Mm = x2, Y2Mm = y2
            };
        }

        // ------------------------------------------------------------- levels

        [Fact]
        public void Two_levels_a_millimetre_apart_are_found_although_nothing_else_would_mention_them()
        {
            var levels = new[] { L(1, "L02", 3000.0), L(2, "Level 02", 3001.0) };
            var found = DatumRules.CoincidentLevels(levels, DatumRules.DefaultLevelCoincidenceMm);

            var one = Assert.Single(found);
            Assert.Equal(DatumRules.CodeNearCoincident, one.Code);
            Assert.Equal(1.0, one.SeparationMm.Value, 6);
            Assert.Contains("Revit will never mention it", one.Why);
        }

        [Fact]
        public void Exactly_coincident_and_nearly_coincident_carry_different_codes()
        {
            var exact = DatumRules.CoincidentLevels(new[] { L(1, "A", 3000.0), L(2, "B", 3000.0) }, 1.0);
            Assert.Equal(DatumRules.CodeCoincident, Assert.Single(exact).Code);

            var near = DatumRules.CoincidentLevels(new[] { L(1, "A", 3000.0), L(2, "B", 3000.5) }, 1.0);
            Assert.Equal(DatumRules.CodeNearCoincident, Assert.Single(near).Code);
        }

        [Fact]
        public void Levels_further_apart_than_the_tolerance_are_not_a_finding()
        {
            Assert.Empty(DatumRules.CoincidentLevels(new[] { L(1, "A", 0.0), L(2, "B", 3000.0) }, 1.0));
        }

        [Fact]
        public void The_tolerance_is_the_callers_and_changes_the_answer()
        {
            var levels = new[] { L(1, "A", 3000.0), L(2, "B", 3005.0) };
            Assert.Empty(DatumRules.CoincidentLevels(levels, 1.0));
            Assert.Single(DatumRules.CoincidentLevels(levels, 10.0));
        }

        [Fact]
        public void A_level_with_no_elevation_is_skipped_rather_than_treated_as_zero()
        {
            // Two levels at 0 would be coincident. One of them has no elevation at
            // all, and inventing 0 for it would manufacture a finding.
            var levels = new[] { L(1, "A", 0.0), L(2, "B", null) };
            Assert.Empty(DatumRules.CoincidentLevels(levels, 1.0));
        }

        [Fact]
        public void Duplicate_level_names_are_compared_ordinally_because_Revit_does()
        {
            Assert.Single(DatumRules.DuplicateLevelNames(new[] { L(1, "L1", 0.0), L(2, "L1", 3000.0) }));
            // "L1" and "l1" are two different names to Revit. Calling them a
            // duplicate would be this tool inventing a convention it was not given.
            Assert.Empty(DatumRules.DuplicateLevelNames(new[] { L(1, "L1", 0.0), L(2, "l1", 3000.0) }));
        }

        [Fact]
        public void A_level_whose_name_could_not_be_read_is_not_compared_with_anything()
        {
            var levels = new[] { L(1, "L1", 0.0), new LevelFact { ElementId = 2, NameReadable = false } };
            Assert.Empty(DatumRules.DuplicateLevelNames(levels));
        }

        [Fact]
        public void Levels_nothing_draws_and_levels_nothing_stands_on_are_different_questions()
        {
            var levels = new[]
            {
                L(1, "used", 0.0, views: 3, elements: 40),
                L(2, "no views", 3000.0, views: 0, elements: 12),
                L(3, "no elements", 6000.0, views: 2, elements: 0),
                L(4, "not measured", 9000.0, views: null, elements: null),
            };

            long viewsNotMeasured, elementsNotMeasured;
            var noViews = DatumRules.LevelsWithoutViews(levels, out viewsNotMeasured);
            var noElements = DatumRules.LevelsWithoutElements(levels, out elementsNotMeasured);

            Assert.Equal("no views", Assert.Single(noViews).Name);
            Assert.Equal("no elements", Assert.Single(noElements).Name);
            // The unmeasured one is counted apart, never folded into either list.
            Assert.Equal(1, viewsNotMeasured);
            Assert.Equal(1, elementsNotMeasured);
        }

        // -------------------------------------------------------------- grids

        [Fact]
        public void Two_grids_on_the_same_line_are_found()
        {
            var grids = new[] { G(1, "A", 0, 0, 10000, 0), G(2, "A2", 0, 0.5, 10000, 0.5) };
            var found = DatumRules.CoincidentGrids(grids, DatumRules.DefaultGridCoincidenceMm,
                                                   DatumRules.DefaultGridAxisToleranceDegrees);
            var one = Assert.Single(found);
            Assert.Equal(0.5, one.SeparationMm.Value, 6);
        }

        [Fact]
        public void Parallel_grids_a_bay_apart_are_not_coincident()
        {
            var grids = new[] { G(1, "A", 0, 0, 10000, 0), G(2, "B", 0, 6000, 10000, 6000) };
            Assert.Empty(DatumRules.CoincidentGrids(grids, 1.0, 0.5));
        }

        [Fact]
        public void Perpendicular_grids_crossing_at_a_point_are_not_coincident()
        {
            // They touch, and their perpendicular distance is zero - but they are not
            // parallel, so they are two grids doing their job.
            var grids = new[] { G(1, "A", 0, 0, 10000, 0), G(2, "1", 0, 0, 0, 10000) };
            Assert.Empty(DatumRules.CoincidentGrids(grids, 1.0, 0.5));
        }

        [Fact]
        public void A_curved_grid_is_not_compared_and_never_reported_as_clear()
        {
            var curved = new GridFact
            {
                ElementId = 9, Name = "curve", NameReadable = true, GeometryReadable = true, IsCurved = true
            };
            var grids = new List<GridFact> { G(1, "A", 0, 0, 10000, 0), curved };
            Assert.Empty(DatumRules.CoincidentGrids(grids, 1.0, 0.5));

            double? dominant;
            var off = DatumRules.GridsOffAxis(grids, 0.5, out dominant);
            Assert.DoesNotContain(off, g => g.ElementId == 9);
        }

        [Fact]
        public void A_building_rotated_thirty_degrees_has_no_grids_off_axis()
        {
            // THE FALSE POSITIVE. Every grid is off the world axes and the building
            // is perfectly orthogonal to itself.
            double c = System.Math.Cos(30 * System.Math.PI / 180), s = System.Math.Sin(30 * System.Math.PI / 180);
            var grids = new List<GridFact>();
            for (int i = 0; i < 3; i++)
                grids.Add(G(i + 1, "A" + i, 0, i * 6000, 20000 * c, i * 6000 + 20000 * s));
            for (int i = 0; i < 3; i++)
                grids.Add(G(10 + i, "N" + i, i * 6000, 0, i * 6000 - 20000 * s, 20000 * c));

            double? dominant;
            var off = DatumRules.GridsOffAxis(grids, 0.5, out dominant);

            Assert.Empty(off);
            Assert.Equal(30.0, dominant.Value, 3);
        }

        [Fact]
        public void The_one_grid_that_disagrees_with_the_building_is_the_finding()
        {
            var grids = new List<GridFact>
            {
                G(1, "A", 0, 0, 10000, 0),
                G(2, "B", 0, 6000, 10000, 6000),
                G(3, "C", 0, 12000, 10000, 12000),
                G(4, "1", 0, 0, 0, 10000),
                G(5, "odd", 0, 0, 10000, 3000),   // about 16.7 degrees
            };
            double? dominant;
            var off = DatumRules.GridsOffAxis(grids, 0.5, out dominant);

            Assert.Equal("odd", Assert.Single(off).Name);
            Assert.Equal(0.0, dominant.Value, 3);
        }

        [Fact]
        public void With_no_straight_grids_at_all_nothing_is_off_axis_and_no_axis_is_claimed()
        {
            double? dominant;
            var off = DatumRules.GridsOffAxis(new List<GridFact>(), 0.5, out dominant);
            Assert.Empty(off);
            Assert.Null(dominant);
        }

        [Fact]
        public void A_zero_length_grid_cannot_produce_a_distance_and_returns_NaN_not_zero()
        {
            var a = G(1, "degenerate", 5000, 5000, 5000, 5000);
            var b = G(2, "B", 0, 0, 10000, 0);
            // Zero would read as "these two are on top of each other".
            Assert.True(double.IsNaN(DatumRules.PerpendicularDistanceMm(a, b)));
        }

        [Fact]
        public void The_angle_gap_folds_so_a_grid_and_its_reverse_are_the_same_direction()
        {
            Assert.Equal(0.0, DatumRules.AngleGap(0, 180), 6);
            Assert.Equal(0.0, DatumRules.AngleGap(30, 210), 6);
            Assert.Equal(90.0, DatumRules.AngleGap(0, 90), 6);
            Assert.Equal(1.0, DatumRules.AngleGap(179, 0), 6);
        }
    }
}
