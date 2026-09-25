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
        public void A_missing_measure_does_not_match_rather_than_throwing()
        {
            var rule = new JArray
            {
                new JObject { ["when"] = new JObject { ["short_side_mm"] = new JObject { ["lt"] = 300 } }, ["type_id"] = 1 },
                new JObject { ["else"] = true, ["type_id"] = 2 }
            };
            TypeChangeRuleSet set = TypeChangeRuleRules.Parse(rule);
            TypeChangeMatch m = TypeChangeRuleRules.Evaluate(set, new Dictionary<string, double>());
            Assert.True(m.Matched);
            Assert.Equal(2, m.TypeId);   // fell through to else, not a crash
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
    }
}
