using System.Collections.Generic;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class Detail2DOutcomeRulesTests
    {
        private static DetailSegment Seg(int index, double ax, double ay, double bx, double by) =>
            new DetailSegment { Index = index, A = new[] { ax, ay, 0.0 }, B = new[] { bx, by, 0.0 } };

        [Fact]
        public void A_rehearsal_says_it_applied_nothing()
        {
            var result = new JObject();
            Detail2DOutcomeRules.StampNotApplied(result, 0, true);
            Assert.False((bool)result["applied"]);
            Assert.False((bool)result["write_started"]);
            Assert.Equal("rehearsal_only", (string)result["status"]);
            Assert.True((bool)result["can_apply"]);
            Assert.Contains("dry_run=false", (string)result["applied_note"]);
        }

        [Fact]
        public void A_rehearsal_with_invalid_actions_cannot_be_applied_and_says_how_many()
        {
            var result = new JObject();
            Detail2DOutcomeRules.StampNotApplied(result, 2, false);
            Assert.False((bool)result["applied"]);
            Assert.False((bool)result["can_apply"]);
            Assert.Contains("2 action(s) are invalid", (string)result["applied_note"]);
        }

        [Fact]
        public void An_unconstructible_rehearsal_is_not_appliable_either()
        {
            var result = new JObject();
            Detail2DOutcomeRules.StampNotApplied(result, 0, false);
            Assert.False((bool)result["can_apply"]);
            Assert.Contains("could not construct", (string)result["applied_note"]);
        }

        private static JArray Rows() => JArray.Parse(@"[
          { ""index"": 0, ""operation"": ""create_detail_line"", ""verification"": { ""all_match"": true, ""checks"": [
              { ""field"": ""elements_count"", ""requested"": 1, ""read"": 1, ""match"": true } ] } },
          { ""index"": 1, ""operation"": ""create_detail_line"", ""verification"": { ""all_match"": false, ""checks"": [
              { ""field"": ""elements_count"", ""requested"": 1, ""read"": 1, ""match"": true },
              { ""field"": ""endpoints_carry_request"", ""requested"": ""(0,0) -> (0.001,0)"", ""read"": ""(0,0) -> (0.004,0)"", ""match"": false } ] } },
          { ""index"": 2, ""operation"": ""create_filled_region"", ""verification"": { ""all_match"": false, ""checks"": [
              { ""field"": ""loops"", ""requested"": 1, ""read"": 2, ""match"": false } ] } }
        ]");

        [Fact]
        public void Failed_checks_name_the_action_the_property_and_both_values()
        {
            JArray failed = Detail2DOutcomeRules.FailedChecks(Rows());
            Assert.Equal(2, failed.Count);
            Assert.Equal(1, (int)failed[0]["index"]);
            Assert.Equal("endpoints_carry_request", (string)failed[0]["field"]);
            Assert.Equal("(0,0) -> (0.004,0)", (string)failed[0]["read"]);
            Assert.Equal("loops", (string)failed[1]["field"]);
        }

        [Fact]
        public void The_headline_carries_the_first_differences_and_counts_the_rest()
        {
            string headline = Detail2DOutcomeRules.Headline(Detail2DOutcomeRules.FailedChecks(Rows()), 1);
            Assert.Contains("action 1 (create_detail_line) endpoints_carry_request", headline);
            Assert.Contains("asked (0,0) -> (0.001,0), read (0,0) -> (0.004,0)", headline);
            Assert.Contains("1 more in failed_checks", headline);
            Assert.Contains("no differing check", Detail2DOutcomeRules.Headline(new JArray()));
        }

        [Fact]
        public void No_rows_or_all_matching_rows_have_no_failed_checks()
        {
            Assert.Empty(Detail2DOutcomeRules.FailedChecks(null));
            Assert.Empty(Detail2DOutcomeRules.FailedChecks(JArray.Parse(@"[{""index"":0,""verification"":{""checks"":[{""field"":""a"",""match"":true}]}}]")));
        }

        [Fact]
        public void A_line_of_a_couple_of_millimetres_is_a_join_hazard()
        {
            double mm = 1 / 304.8;
            var hazards = Detail2DOutcomeRules.LineHazards(new[] { Seg(0, 0, 0, 2 * mm, 0), Seg(1, 0, 1, 100 * mm, 1) }, 1e-5);
            Assert.Single(hazards);
            Assert.Contains("shorter than 3 mm", hazards[0]);
        }

        [Fact]
        public void Overlapping_collinear_lines_are_hazards_for_both_and_named_to_each_other()
        {
            double mm = 1 / 304.8;
            var hazards = Detail2DOutcomeRules.LineHazards(new[] { Seg(4, 0, 0, 100 * mm, 0), Seg(7, 60 * mm, 0, 160 * mm, 0) }, 1e-5);
            Assert.Equal(2, hazards.Count);
            Assert.Contains("collinear with action 7 over 40 mm", hazards[4]);
            Assert.Contains("collinear with action 4", hazards[7]);
        }

        [Fact]
        public void Lines_that_only_touch_or_run_parallel_apart_are_not_hazards()
        {
            double mm = 1 / 304.8;
            Assert.Empty(Detail2DOutcomeRules.LineHazards(new[] { Seg(0, 0, 0, 100 * mm, 0), Seg(1, 100 * mm, 0, 200 * mm, 0) }, 1e-5)); // end to end
            Assert.Empty(Detail2DOutcomeRules.LineHazards(new[] { Seg(0, 0, 0, 100 * mm, 0), Seg(1, 0, 10 * mm, 100 * mm, 10 * mm) }, 1e-5)); // parallel
            Assert.Empty(Detail2DOutcomeRules.LineHazards(new[] { Seg(0, 0, 0, 100 * mm, 0), Seg(1, 50 * mm, -50 * mm, 50 * mm, 50 * mm) }, 1e-5)); // crossing
        }

        [Fact]
        public void A_failed_check_on_a_hazardous_action_gets_its_likely_cause()
        {
            double mm = 1 / 304.8;
            var hazards = Detail2DOutcomeRules.LineHazards(new[] { Seg(1, 0, 0, 2 * mm, 0) }, 1e-5);
            JArray risk = Detail2DOutcomeRules.AtRisk(Detail2DOutcomeRules.FailedChecks(Rows()), hazards);
            Assert.Single(risk);
            Assert.Equal(1, (int)risk[0]["index"]);
            Assert.Contains("shorter than 3 mm", (string)risk[0]["likely_cause"]);
        }

        [Fact]
        public void Simplifying_a_loop_drops_edges_below_the_tolerance_and_closes_cleanly()
        {
            double mm = 1 / 304.8;
            var square = new List<double[]>
            {
                new[] { 0.0, 0.0, 0.0 }, new[] { 0.2 * mm, 0.0, 0.0 }, new[] { 10 * mm, 0.0, 0.0 },
                new[] { 10 * mm, 10 * mm, 0.0 }, new[] { 0.0, 10 * mm, 0.0 }, new[] { 0.0, 0.3 * mm, 0.0 }
            };
            var kept = Detail2DOutcomeRules.SimplifyLoop(square, 1.0 * mm, out int removed);
            Assert.Equal(4, kept.Count);
            Assert.Equal(2, removed);
            Assert.Equal(10 * mm, kept[1][0], 12);
        }

        [Fact]
        public void A_loop_with_nothing_to_drop_comes_back_untouched()
        {
            double mm = 1 / 304.8;
            var tri = new List<double[]> { new[] { 0.0, 0.0, 0.0 }, new[] { 5 * mm, 0.0, 0.0 }, new[] { 0.0, 5 * mm, 0.0 } };
            var kept = Detail2DOutcomeRules.SimplifyLoop(tri, 1.0 * mm, out int removed);
            Assert.Equal(0, removed);
            Assert.Equal(3, kept.Count);
        }

        [Fact]
        public void The_filled_regions_token_matches_by_class_not_by_category()
        {
            const long regionToken = -2000150;
            var onlyRegions = new HashSet<long> { regionToken };
            // A placed region reports the Detail Items category (-2000100 here), not the token's.
            Assert.True(Detail2DQueryRules.PassesCategoryFilter(onlyRegions, -2000100, true, regionToken));
            Assert.False(Detail2DQueryRules.PassesCategoryFilter(onlyRegions, -2000100, false, regionToken));
            Assert.True(Detail2DQueryRules.PassesCategoryFilter(null, -2000100, true, regionToken));
            Assert.True(Detail2DQueryRules.PassesCategoryFilter(new HashSet<long>(), -1, false, regionToken));
            var lines = new HashSet<long> { -2000051 };
            Assert.True(Detail2DQueryRules.PassesCategoryFilter(lines, -2000051, false, regionToken));
            Assert.False(Detail2DQueryRules.PassesCategoryFilter(lines, -2000100, true, regionToken));
        }

        [Theory]
        [InlineData(0.0)]
        [InlineData(-1.0)]
        [InlineData(5.01)]
        [InlineData(double.NaN)]
        public void A_join_tolerance_outside_zero_to_five_mm_is_refused(double mm)
        {
            Assert.Contains("(0, 5] mm", Detail2DOutcomeRules.ParseJoinTolerance(mm, out double feet));
            Assert.Equal(0, feet);
        }

        [Fact]
        public void A_join_tolerance_is_converted_to_feet()
        {
            Assert.Null(Detail2DOutcomeRules.ParseJoinTolerance(3.048, out double feet));
            Assert.Equal(0.01, feet, 12);
        }

        [Fact]
        public void The_displacement_is_measured_in_the_better_orientation()
        {
            double mm = 1 / 304.8;
            var a = new[] { 0.0, 0, 0 }; var b = new[] { 2 * mm, 0, 0 };
            // Revit reversed the line and moved one end 1 mm.
            var readA = new[] { 3 * mm, 0, 0 }; var readB = new[] { 0.0, 0, 0 };
            Assert.Equal(1 * mm, Detail2DOutcomeRules.EndpointDisplacement(a, b, readA, readB), 12);
            Assert.Equal(0, Detail2DOutcomeRules.EndpointDisplacement(a, b, a, b), 12);
        }

        [Fact]
        public void A_granted_join_tolerance_rescues_only_a_line_that_is_at_risk()
        {
            double mm = 1 / 304.8, strict = 0.0001 * mm;
            // at risk + granted + within: accepted, and says it was the join route
            Assert.True(Detail2DOutcomeRules.EndpointsAcceptable(1.0 * mm, strict, 2.0 * mm, true, out bool joined));
            Assert.True(joined);
            // not at risk: the tolerance does NOT apply, the move is still a failure
            Assert.False(Detail2DOutcomeRules.EndpointsAcceptable(1.0 * mm, strict, 2.0 * mm, false, out joined));
            Assert.False(joined);
            // at risk but moved further than granted
            Assert.False(Detail2DOutcomeRules.EndpointsAcceptable(3.0 * mm, strict, 2.0 * mm, true, out joined));
            // nothing granted
            Assert.False(Detail2DOutcomeRules.EndpointsAcceptable(1.0 * mm, strict, 0, true, out joined));
            // exact: passes without the join route
            Assert.True(Detail2DOutcomeRules.EndpointsAcceptable(0, strict, 0, false, out joined));
            Assert.False(joined);
        }
    }
}
