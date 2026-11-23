using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class EnclosedRulesTests
    {
        [Fact]
        public void Point_and_topology_placement_have_one_unambiguous_phase_contract()
        {
            var point = JObject.Parse(@"{'kind':'room','level_id':1,'point':[2,3]}");
            Assert.Null(EnclosedRules.PlacementFieldsProblem(point));
            point["phase_id"] = 7;
            Assert.Contains("Revit assigns the phase", EnclosedRules.PlacementFieldsProblem(point));
            point.Remove("phase_id"); point["min_area_m2"] = 1;
            Assert.Contains("requires placement", EnclosedRules.PlacementFieldsProblem(point));
            point.Remove("point"); point["placement"] = "all_enclosed"; point["phase_id"] = 7;
            Assert.Null(EnclosedRules.PlacementFieldsProblem(point));
        }

        [Fact]
        public void Separator_closure_is_opt_in_and_never_duplicates_the_closing_edge()
        {
            var points = new[] { new double[] {0,0,0}, new double[] {3,0,0}, new double[] {3,2,0} };
            Assert.Equal(3, EnclosedRules.SeparatorChain(points, false).Count);
            var closed = EnclosedRules.SeparatorChain(points, true);
            Assert.Equal(4, closed.Count);
            Assert.Equal(closed[0], closed[3]);
            Assert.Equal(4, EnclosedRules.SeparatorChain(closed, true).Count);
            Assert.Throws<ArgumentException>(() => EnclosedRules.SeparatorChain(points.Take(2).ToList(), true));
        }

        [Fact] public void An_empty_circuit_above_min_area_is_created() => Assert.Equal("create", EnclosedRules.CircuitAction("room", false, 12.5, 10));
        [Fact] public void A_circuit_of_exactly_min_area_is_kept() => Assert.Equal("create", EnclosedRules.CircuitAction("space", false, 10, 10));
        [Fact] public void A_smaller_circuit_is_skipped() => Assert.Equal("skipped_min_area", EnclosedRules.CircuitAction("room", false, 2.4, 10));
        // MEASURED 2026-09-27: a second NewSpaces2 over filled regions posts zero-area (redundant) spaces.
        [Fact] public void A_region_with_no_area_is_named_never_created()
        {
            Assert.Equal("skipped_zero_area", EnclosedRules.CircuitAction("space", false, 0, 0));
            Assert.Equal("skipped_zero_area", EnclosedRules.CircuitAction("room", false, double.NaN, 0));
            Assert.Equal("skipped_has_space", EnclosedRules.CircuitAction("space", true, 0, 0));
        }

        [Fact]
        public void An_occupied_circuit_is_skipped_as_occupied_whatever_its_area()
        {
            Assert.Equal("skipped_has_room", EnclosedRules.CircuitAction("room", true, 2.4, 10));
            Assert.Equal("skipped_has_space", EnclosedRules.CircuitAction("space", true, 50, 0));
        }

        [Fact] public void Another_kind_is_a_programming_error() => Assert.Throws<ArgumentOutOfRangeException>(() => EnclosedRules.CircuitAction("area", false, 1, 0));

        [Theory]
        [InlineData(double.NaN)]
        [InlineData(-0.5)]
        [InlineData(double.PositiveInfinity)]
        public void A_min_area_that_is_not_finite_and_non_negative_is_refused(double v) => Assert.NotNull(EnclosedRules.MinAreaProblem(v));

        [Fact] public void Zero_min_area_keeps_every_empty_circuit() => Assert.Null(EnclosedRules.MinAreaProblem(0));

        [Fact]
        public void A_generated_row_is_xy_only_so_the_room_planner_accepts_it()
        {
            // NormalizePlan refuses a room point that is not exactly X,Y ("room.point requires XY only").
            JObject row = EnclosedRules.GeneratedRow("room", 7, 12, 1153.5, 2000.25, 0, 22.0444);
            Assert.Equal(new[] { 1153.5, 2000.25 }, row["point"].Select(t => (double)t));
            Assert.Equal(0, (int)row["enclosed_from"]);
            Assert.Equal(22.044, (double)row["circuit_area_m2"]);
            Assert.Equal(12L, (long)row["phase_id"]);
        }

        [Fact]
        public void All_enclosed_goes_in_a_batch_of_its_own()
        {
            Assert.Null(EnclosedRules.MixProblem(2, 0));
            Assert.Null(EnclosedRules.MixProblem(0, 5));
            Assert.Contains("batch of their own", EnclosedRules.MixProblem(1, 1));
        }

        [Fact]
        public void Nothing_to_fill_lists_in_a_rehearsal_refuses_an_apply_and_names_a_level_with_no_region()
        {
            JArray seen = JArray.Parse("[{ 'index': 0, 'level': 'L1', 'phase': 'New', 'circuits_seen': 2 }]".Replace('\'', '"'));
            JArray none = JArray.Parse(("[{ 'index': 0, 'level': 'L1', 'phase': 'New', 'circuits_seen': 2 }, " +
                                        "{ 'index': 1, 'level': 'L2', 'phase': 'New', 'circuits_seen': 0 }]").Replace('\'', '"'));
            Assert.Null(EnclosedRules.NothingToFillProblem(true, seen));
            Assert.StartsWith("stale_plan", EnclosedRules.NothingToFillProblem(false, seen));
            string refusal = EnclosedRules.NothingToFillProblem(true, none);
            Assert.StartsWith("no_enclosed_circuit", refusal);
            Assert.Contains("elements[1]", refusal);
            Assert.DoesNotContain("elements[0]", refusal);
        }
    }
}
