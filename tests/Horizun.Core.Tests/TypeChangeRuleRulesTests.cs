// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// The per-instance rule engine behind horizun_transform_elements'
// change_type_by_rule: first match wins, "else" must be last, and the short
// side of a face's UV bounding box is whichever of the two extents is
// smaller - proved without a Document.
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class TypeChangeRuleRulesTests
    {
        [Fact]
        public void Narrow_instance_matches_the_lt_rule_wide_one_falls_to_else()
        {
            var rule = new JArray
            {
                new JObject { ["when"] = new JObject { ["short_side_mm"] = new JObject { ["lt"] = 300 } }, ["type_id"] = 111 },
                new JObject { ["else"] = true, ["type_id"] = 222 }
            };
            TypeChangeRuleSet set = TypeChangeRuleRules.Parse(rule);
            Assert.True(set.Ok);

            TypeChangeMatch narrow = TypeChangeRuleRules.Evaluate(set, new Dictionary<string, double> { ["short_side_mm"] = 250 });
            Assert.True(narrow.Matched);
            Assert.Equal(0, narrow.RuleIndex);
            Assert.Equal(111, narrow.TypeId);

            TypeChangeMatch wide = TypeChangeRuleRules.Evaluate(set, new Dictionary<string, double> { ["short_side_mm"] = 900 });
            Assert.True(wide.Matched);
            Assert.Equal(1, wide.RuleIndex);
            Assert.Equal(222, wide.TypeId);
        }

        [Fact]
        public void First_match_wins_when_two_rules_could_both_hold()
        {
            var rule = new JArray
            {
                new JObject { ["when"] = new JObject { ["short_side_mm"] = new JObject { ["lt"] = 500 } }, ["type_id"] = 1 },
                new JObject { ["when"] = new JObject { ["short_side_mm"] = new JObject { ["lt"] = 1000 } }, ["type_id"] = 2 }
            };
            TypeChangeRuleSet set = TypeChangeRuleRules.Parse(rule);
            TypeChangeMatch m = TypeChangeRuleRules.Evaluate(set, new Dictionary<string, double> { ["short_side_mm"] = 100 });
            Assert.Equal(1, m.TypeId);
        }

        [Fact]
        public void No_rule_matches_and_no_else_reports_unmatched_not_a_default()
        {
            var rule = new JArray { new JObject { ["when"] = new JObject { ["short_side_mm"] = new JObject { ["lt"] = 300 } }, ["type_id"] = 1 } };
            TypeChangeRuleSet set = TypeChangeRuleRules.Parse(rule);
            TypeChangeMatch m = TypeChangeRuleRules.Evaluate(set, new Dictionary<string, double> { ["short_side_mm"] = 900 });
            Assert.False(m.Matched);
        }

        [Fact]
        public void An_instance_missing_only_ONE_named_measure_falls_through_to_else()
        {
            // Two named measures exist for OTHER instances, but this one only has area_m2 -
            // its short_side_mm/long_side_mm rule cannot hold, yet it is NOT unmeasured (the
            // dictionary is non-empty), so 'else' is a legitimate catch for it.
            var rule = new JArray
            {
                new JObject { ["when"] = new JObject { ["short_side_mm"] = new JObject { ["lt"] = 300 } }, ["type_id"] = 1 },
                new JObject { ["else"] = true, ["type_id"] = 2 }
            };
            TypeChangeRuleSet set = TypeChangeRuleRules.Parse(rule);
            TypeChangeMatch m = TypeChangeRuleRules.Evaluate(set, new Dictionary<string, double> { ["area_m2"] = 4.2 });
            Assert.True(m.Matched);
            Assert.Equal(2, m.TypeId);   // fell through to else, not a crash
        }

        [Fact]
        public void An_UNMEASURED_instance_matches_NOTHING_not_even_else()
        {
            // This is the 2026-09-25 review fix: an instance whose face could not be measured
            // at all (measured is EMPTY, not merely missing one named key) must be rejected,
            // never silently classified by a catch-all 'else' - that would hide a mismeasured
            // element behind a type change that looks deliberate.
            var rule = new JArray
            {
                new JObject { ["when"] = new JObject { ["short_side_mm"] = new JObject { ["lt"] = 300 } }, ["type_id"] = 1 },
                new JObject { ["else"] = true, ["type_id"] = 2 }
            };
            TypeChangeRuleSet set = TypeChangeRuleRules.Parse(rule);
            TypeChangeMatch m = TypeChangeRuleRules.Evaluate(set, new Dictionary<string, double>());
            Assert.False(m.Matched);
            Assert.Contains("unmeasured", m.Reason);
        }

        [Fact]
        public void A_null_measured_dictionary_also_matches_nothing()
        {
            var rule = new JArray { new JObject { ["else"] = true, ["type_id"] = 1 } };
            TypeChangeRuleSet set = TypeChangeRuleRules.Parse(rule);
            TypeChangeMatch m = TypeChangeRuleRules.Evaluate(set, null);
            Assert.False(m.Matched);
        }

        [Fact]
        public void A_rule_after_else_is_refused_as_unreachable()
        {
            var rule = new JArray
            {
                new JObject { ["else"] = true, ["type_id"] = 1 },
                new JObject { ["when"] = new JObject { ["short_side_mm"] = new JObject { ["lt"] = 300 } }, ["type_id"] = 2 }
            };
            TypeChangeRuleSet set = TypeChangeRuleRules.Parse(rule);
            Assert.False(set.Ok);
            Assert.Contains("unreachable", set.Error);
        }

        [Fact]
        public void An_entry_with_both_when_and_else_is_refused()
        {
            var rule = new JArray
            {
                new JObject { ["when"] = new JObject { ["short_side_mm"] = new JObject { ["lt"] = 300 } }, ["else"] = true, ["type_id"] = 1 }
            };
            TypeChangeRuleSet set = TypeChangeRuleRules.Parse(rule);
            Assert.False(set.Ok);
        }

        [Fact]
        public void An_unsupported_operator_is_refused()
        {
            var rule = new JArray
            {
                new JObject { ["when"] = new JObject { ["short_side_mm"] = new JObject { ["between"] = 300 } }, ["type_id"] = 1 }
            };
            TypeChangeRuleSet set = TypeChangeRuleRules.Parse(rule);
            Assert.False(set.Ok);
        }

        [Theory]
        [InlineData(3.5, 7.0, 3.5, 7.0)]
        [InlineData(7.0, 3.5, 3.5, 7.0)]
        [InlineData(5.0, 5.0, 5.0, 5.0)]
        public void ShortLong_orders_the_two_extents_regardless_of_input_order(double u, double v, double expectShort, double expectLong)
        {
            var (shortSide, longSide) = ToPair(TypeChangeRuleRules.ShortLong(u, v));
            Assert.Equal(expectShort, shortSide, 6);
            Assert.Equal(expectLong, longSide, 6);
        }

        private static (double, double) ToPair(System.Tuple<double, double> t) => (t.Item1, t.Item2);

        // ---------------------------------------------------------------------
        // IsRectangularFace: the gate MeasureInstance applies before trusting a
        // face's UV bounding box as its true short/long side and area. Pure
        // math - no Revit Face needed to prove the three independent checks.
        // ---------------------------------------------------------------------
        [Fact]
        public void A_plain_rectangle_passes()
        {
            // 3m x 5m rectangle: area exactly u*v.
            Assert.True(TypeChangeRuleRules.IsRectangularFace(1, 4, 15.0, 3.0, 5.0));
        }

        [Fact]
        public void A_face_with_a_hole_fails_on_loop_count_even_with_a_matching_area()
        {
            Assert.False(TypeChangeRuleRules.IsRectangularFace(2, 4, 15.0, 3.0, 5.0));
        }

        [Fact]
        public void A_rectangle_whose_sides_are_split_into_collinear_edges_still_passes()
        {
            // Measured live 2026-09-26: a wall's exterior face has its sides split where other
            // walls and floors meet it. Six edges, one loop, area exactly u*v: a rectangle.
            Assert.True(TypeChangeRuleRules.IsRectangularFace(1, 6, 15.0, 3.0, 5.0));
            Assert.True(TypeChangeRuleRules.IsRectangularFace(1, 8, 15.0, 3.0, 5.0));
        }

        [Fact]
        public void An_L_shape_fails_on_area_whatever_its_edge_count()
        {
            // 3x5 box with a 1x4 notch: six edges, area 11 against 15.
            Assert.False(TypeChangeRuleRules.IsRectangularFace(1, 6, 11.0, 3.0, 5.0));
            Assert.Contains("area", TypeChangeRuleRules.WhyNotRectangular(1, 6, 11.0, 3.0, 5.0));
        }

        [Fact]
        public void Fewer_than_four_edges_or_a_hole_is_named_in_the_reason()
        {
            Assert.Contains("only 3 edges", TypeChangeRuleRules.WhyNotRectangular(1, 3, 7.5, 3.0, 5.0));
            Assert.Contains("2 edge loops", TypeChangeRuleRules.WhyNotRectangular(2, 4, 15.0, 3.0, 5.0));
            Assert.Null(TypeChangeRuleRules.WhyNotRectangular(1, 4, 15.0, 3.0, 5.0));
        }

        [Fact]
        public void A_parallelogram_shaped_like_its_bbox_fails_on_area_tolerance()
        {
            // 4 edges, 1 loop, but its real area is well under u*v (a skewed parallelogram).
            Assert.False(TypeChangeRuleRules.IsRectangularFace(1, 4, 10.0, 3.0, 5.0));
        }

        [Fact]
        public void Area_comfortably_within_the_default_2_percent_tolerance_passes()
        {
            // u*v = 15.0; 14.85 is 1% under - well inside the 2% band.
            Assert.True(TypeChangeRuleRules.IsRectangularFace(1, 4, 14.85, 3.0, 5.0));
        }

        [Fact]
        public void Area_comfortably_outside_the_default_2_percent_tolerance_fails()
        {
            // u*v = 15.0; 14.5 is over 3% under - clearly outside the 2% band.
            Assert.False(TypeChangeRuleRules.IsRectangularFace(1, 4, 14.5, 3.0, 5.0));
        }

        [Fact]
        public void A_zero_area_bounding_box_fails_rather_than_dividing_by_zero()
        {
            Assert.False(TypeChangeRuleRules.IsRectangularFace(1, 4, 0.0, 0.0, 5.0));
        }
    }
}
