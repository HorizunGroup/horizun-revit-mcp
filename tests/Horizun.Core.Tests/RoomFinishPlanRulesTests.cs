// horizun_room_finishes without Revit. field cycle ARQ (2026-10-24, A4/A5): one tile floor per storey
// ran through the walls; floors and baseboards by room did not exist.
using System.Collections.Generic;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class RoomFinishPlanRulesTests
    {
        private static JObject Floors() => new JObject
        {
            ["target_document"] = "ARQ", ["operation"] = "floors", ["room_ids"] = new JArray(5, 6),
            ["type_by_use"] = new JObject { ["parameter"] = "Uso", ["map"] = new JObject { ["Baño"] = 101, ["Cocina"] = 102 } },
            ["default_type_id"] = 100
        };

        [Fact]
        public void A_floor_type_is_never_guessed()
        {
            Assert.Null(RoomFinishPlanRules.Validate(Floors()));
            var r = Floors(); r.Remove("type_by_use"); r.Remove("default_type_id");
            Assert.Contains("never guessed", RoomFinishPlanRules.Validate(r));
            r = Floors(); r["type_by_use"] = new JObject { ["parameter"] = "Uso", ["map"] = new JObject { ["Baño"] = "tile" } };
            Assert.Contains("floor type ids", RoomFinishPlanRules.Validate(r));
        }

        [Fact]
        public void Each_operation_takes_its_own_fields()
        {
            var r = Floors(); r["type_id"] = 9;
            Assert.Contains("type_id", RoomFinishPlanRules.Validate(r));
            var b = new JObject { ["target_document"] = "ARQ", ["operation"] = "baseboards", ["room_ids"] = new JArray(5) };
            Assert.Contains("type_id", RoomFinishPlanRules.Validate(b));
            b["type_id"] = 12; b["height_mm"] = 0;
            Assert.Null(RoomFinishPlanRules.Validate(b));
            b["room_ids"] = new JArray(5, 5);
            Assert.Contains("twice", RoomFinishPlanRules.Validate(b));
        }

        [Fact]
        public void The_type_follows_the_use_then_the_default()
        {
            var map = new Dictionary<string, long> { ["Baño"] = 101 };
            Assert.Equal(101, RoomFinishPlanRules.TypeFor("Baño ", map, 100));
            Assert.Equal(100, RoomFinishPlanRules.TypeFor("Alcoba", map, 100));
            Assert.Null(RoomFinishPlanRules.TypeFor("Alcoba", map, null));
            Assert.Equal(100, RoomFinishPlanRules.TypeFor(null, map, 100));
        }

        [Fact]
        public void The_enclosed_area_is_the_outer_loop_less_its_holes()
        {
            var outer = new List<double[]> { new double[] { 0, 0 }, new double[] { 4, 0 }, new double[] { 4, 3 }, new double[] { 0, 3 } };
            var hole = new List<double[]> { new double[] { 1, 1 }, new double[] { 1, 2 }, new double[] { 2, 2 }, new double[] { 2, 1 } };
            Assert.Equal(12, RoomFinishPlanRules.EnclosedArea(new List<IList<double[]>> { outer }), 9);
            Assert.Equal(11, RoomFinishPlanRules.EnclosedArea(new List<IList<double[]>> { hole, outer }), 9);
        }

        [Fact]
        public void A_floor_area_agrees_within_half_a_percent()
        {
            Assert.True(RoomFinishPlanRules.AreaAgrees(100, 100.4));
            Assert.False(RoomFinishPlanRules.AreaAgrees(100, 101));
            Assert.False(RoomFinishPlanRules.AreaAgrees(0, 0));
        }

        [Fact]
        public void Unordered_and_reversed_boundary_segments_form_the_same_loop()
        {
            var segments = new List<double[][]>
            {
                new[] { new double[] {0,0}, new double[] {4,0} },
                new[] { new double[] {0,3}, new double[] {4,3} },
                new[] { new double[] {0,0}, new double[] {0,3} },
                new[] { new double[] {4,0}, new double[] {4,3} }
            };
            var ordered = RoomFinishPlanRules.OrderBoundary(segments, 0.001);
            Assert.True(ordered.Closed);
            Assert.Equal(new[] { 0, 3, 1, 2 }, ordered.Indices);
            Assert.Equal(new[] { false, false, true, true }, ordered.Reversed);
            Assert.Equal(0, ordered.MaximumGap);
        }

        [Theory]
        [InlineData(0.0005, true)]
        [InlineData(0.002, false)]
        public void Boundary_gap_is_measured_including_the_closing_seam(double gap, bool accepted)
        {
            var segments = new List<double[][]>
            {
                new[] { new double[] {0,0}, new double[] {4,0} },
                new[] { new double[] {4,0}, new double[] {4,3} },
                new[] { new double[] {4,3}, new double[] {0,3} },
                new[] { new double[] {0,3}, new double[] {0,gap} }
            };
            var ordered = RoomFinishPlanRules.OrderBoundary(segments, 0.001);
            Assert.Equal(accepted, ordered.Closed);
            Assert.Equal(gap, ordered.MaximumGap, 9);
        }

        [Fact]
        public void The_endpoint_gap_tolerance_is_explicit_and_must_be_positive()
        {
            var r = Floors(); r["boundary_gap_tolerance_mm"] = 0.5;
            Assert.Null(RoomFinishPlanRules.Validate(r));
            r["boundary_gap_tolerance_mm"] = 0;
            Assert.Contains("boundary_gap_tolerance_mm", RoomFinishPlanRules.Validate(r));
            Assert.False(RoomFinishPlanRules.OrderBoundary(new List<double[][]>(), 1).Closed);
        }
        [Theory]
        [InlineData(-0.1, 0, -0.1, 0, true)]
        [InlineData(-0.1, 0, 10, 10.1, false)]
        [InlineData(-0.1, 0, 0, 0.1, false)]
        [InlineData(-0.1, 0, -0.05, 0.05, true)]
        public void Existing_finish_requires_positive_Z_overlap(double a, double b, double c, double d, bool expected) =>
            Assert.Equal(expected, RoomFinishPlanRules.RangesOverlap(a, b, c, d));

        [Fact]
        public void Floor_verification_includes_independent_finish_panels_not_in_the_room_boundary()
        {
            // Four room-bounding walls plus a fifth independent panel crossing the finish floor.
            // The panel is deliberately absent from the room's boundary-owner ids.
            Assert.Equal(new long[] { 1, 2, 3, 4, 5 },
                RoomFinishPlanRules.WallsForFloorVerification(new long[] { 1, 2, 3, 4 }, new long[] { 2, 5 }));
        }

        [Fact]
        public void Floor_verification_keeps_boundary_owners_even_if_their_boxes_are_unreadable()
        {
            Assert.Equal(new long[] { 1, 2 }, RoomFinishPlanRules.WallsForFloorVerification(new long[] { 2, 1 }, null));
            Assert.Equal(new long[] { 5 }, RoomFinishPlanRules.WallsForFloorVerification(null, new long[] { 5, 5 }));
        }

        [Theory]
        [InlineData(0d, 0.0001, "floor_wall_overlap_increased")]
        [InlineData(0.002, 0.0021, "floor_wall_overlap_increased")]
        [InlineData(0.002, 0.002, null)]
        [InlineData(0.002, 0.001, null)]
        [InlineData(0d, 0.0000005, null)]
        [InlineData(null, 0d, "floor_wall_overlap_before_unmeasured")]
        [InlineData(0d, null, "floor_wall_overlap_after_unmeasured")]
        public void Edits_preserve_existing_floor_wall_intersections_but_cannot_increase_them(double? before, double? after, string expected)
        {
            Assert.Equal(expected, RoomFinishPlanRules.FloorWallOverlapChangeProblem(before, after, 0.000001));
        }

        [Fact]
        public void Invalid_floor_wall_measurements_are_unknown_instead_of_clear()
        {
            Assert.Equal("floor_wall_overlap_before_unmeasured", RoomFinishPlanRules.FloorWallOverlapChangeProblem(double.NaN, 0, 0.000001));
            Assert.Equal("floor_wall_overlap_after_unmeasured", RoomFinishPlanRules.FloorWallOverlapChangeProblem(0, double.PositiveInfinity, 0.000001));
            Assert.Equal("floor_wall_overlap_after_unmeasured", RoomFinishPlanRules.FloorWallOverlapChangeProblem(0, -0.01, 0.000001));
        }


        [Theory]
        // floor from 0 to 0.1 ft, tolerance 0.003 ft
        [InlineData(-10, -0.5, "wall_below_floor")]      // wall of the storey below, ends under the floor
        [InlineData(-10, 0.002, "wall_below_floor")]     // ends at the floor bottom within tolerance
        [InlineData(-10, 0.05, "floor_rests_on_wall")]   // ends inside the floor's thickness: the floor sits on it
        [InlineData(-10, 0.102, "floor_rests_on_wall")]  // ends at the floor's top within tolerance
        [InlineData(0.099, 10, "wall_above_floor")]      // starts at the floor's top
        [InlineData(-10, 10, null)]                       // rises through the floor: must be measured
        [InlineData(0.05, 10, null)]                      // starts inside the floor and rises above it: measured
        public void WallVerticalRelationOnlyClearsWallsThatCannotPierceTheFloor(double wallBottom, double wallTop, string expected)
        {
            Assert.Equal(expected, RoomFinishPlanRules.WallVerticalRelation(0, 0.1, wallBottom, wallTop, 0.003));
        }

        [Fact]
        public void WallVerticalRelationWithUnreadableExtentsMeasures()
        {
            Assert.Null(RoomFinishPlanRules.WallVerticalRelation(0, 0.1, double.NaN, 10, 0.003));
            Assert.Null(RoomFinishPlanRules.WallVerticalRelation(0, double.PositiveInfinity, -10, 10, 0.003));
        }
    }
}
