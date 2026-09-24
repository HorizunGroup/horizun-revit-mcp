// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// horizun_code_check, Revit-free: the loader's new refusals, the four outcomes,
// bounds, measure_range, unverified values, exits per level, ramp geometry, and
// the three Colombian example sets loading through the SAME loader as the ISO
// 19650 / IFC / COBie reference sets.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class CodeCheckTests
    {
        private static RequirementSet Load(string rulesJson) => RequirementSet.Load(JObject.Parse(
            "{ 'requirement_set': { 'id': 't', 'version': '1' }, 'rules': " + rulesJson.Replace('\'', '"') + " }"), _ => null);

        private static string Refusal(string rulesJson)
            => Assert.Throws<RequirementSetException>(() => Load(rulesJson)).Message;

        private static CheckedElement Door(long id, double? widthMm) => new CheckedElement
        {
            Id = id, CategoryToken = "OST_Doors", CategoryName = "Puertas", Name = "D" + id,
            Measures = { ["door_clear_width_mm"] = widthMm == null ? MeasuredValue.None("no width") : new MeasuredValue { Value = widthMm, Bound = "upper" } }
        };

        private static string StandardsDir()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, "standards", "co-ntc6047-accesibilidad.json"))) d = d.Parent;
            Assert.True(d != null, "standards/ not found");
            return Path.Combine(d.FullName, "standards");
        }

        // ---- the loader ---------------------------------------------------------------

        [Fact]
        public void A_measure_assertion_loads_and_an_unknown_measure_is_refused_naming_the_known_ones()
        {
            RequirementSet s = Load("[{ 'id': 'r', 'selector': { 'category': 'OST_Doors' }, 'assertion': { 'measure': 'door_clear_width_mm', 'operator': 'gte', 'value': 800 } }]");
            Assert.Equal("door_clear_width_mm", s.Rules[0].AssertionMeasure);
            string msg = Refusal("[{ 'id': 'r', 'selector': { 'category': 'OST_Doors' }, 'assertion': { 'measure': 'door_width', 'operator': 'gte', 'value': 800 } }]");
            Assert.Contains("door_clear_width_mm", msg);
        }

        [Fact]
        public void Parameter_and_measure_together_and_a_text_operator_on_a_measure_are_refused()
        {
            Assert.Contains("exactly one", Refusal("[{ 'id': 'r', 'selector': { 'category': 'x' }, 'assertion': { 'parameter': 'p', 'measure': 'stair_riser_mm', 'operator': 'lte', 'value': 1 } }]"));
            Assert.Contains("is a number", Refusal("[{ 'id': 'r', 'selector': { 'category': 'x' }, 'assertion': { 'measure': 'stair_riser_mm', 'operator': 'equals', 'value': 1 } }]"));
        }

        [Fact]
        public void Unknown_keys_in_rule_selector_or_assertion_are_refused()
        {
            Assert.Contains("unknown key 'sourse'", Refusal("[{ 'id': 'r', 'sourse': 'x', 'selector': { 'category': 'x' }, 'assertion': { 'parameter': 'p', 'operator': 'exists' } }]"));
            Assert.Contains("unknown key 'categroy'", Refusal("[{ 'id': 'r', 'selector': { 'categroy': 'x' }, 'assertion': { 'parameter': 'p', 'operator': 'exists' } }]"));
            Assert.Contains("unknown key 'valeu'", Refusal("[{ 'id': 'r', 'selector': { 'category': 'x' }, 'assertion': { 'parameter': 'p', 'operator': 'gte', 'value': 1, 'valeu': 2 } }]"));
        }

        [Fact]
        public void Between_needs_an_ordered_pair_and_only_an_unverified_rule_may_omit_its_value()
        {
            Assert.Contains("[min, max]", Refusal("[{ 'id': 'r', 'selector': { 'category': 'x' }, 'assertion': { 'measure': 'stair_2r_plus_t_mm', 'operator': 'between', 'value': [660, 600] } }]"));
            Assert.Contains("requires value", Refusal("[{ 'id': 'r', 'selector': { 'category': 'x' }, 'assertion': { 'measure': 'stair_riser_mm', 'operator': 'lte' } }]"));
            RequirementSet s = Load("[{ 'id': 'r', 'unverified_value': true, 'selector': { 'category': 'x' }, 'assertion': { 'measure': 'stair_riser_mm', 'operator': 'lte' } }]");
            Assert.True(s.Rules[0].UnverifiedValue);
            Assert.Null(s.Rules[0].Value);
        }

        [Fact]
        public void The_three_colombian_sets_load_through_the_same_loader_and_cite_a_source_on_every_rule()
        {
            foreach (string name in new[] { "co-ntc6047-accesibilidad.json", "co-nsr10-titulo-k-evacuacion.json", "co-retilap-iluminancia.json" })
            {
                RequirementSet s = RequirementSet.Load(JObject.Parse(File.ReadAllText(Path.Combine(StandardsDir(), name))), _ => null);
                Assert.NotEmpty(s.Rules);
                Assert.All(s.Rules, r => Assert.True(r.Source is JObject src && src["numeral"] != null, name + "/" + r.Id + " cites no numeral"));
                // An unverified rule never carries a number that could be read as the norm.
                Assert.All(s.Rules.Where(r => r.UnverifiedValue && r.AssertionMeasure != "exit_count_minus_required"), r => Assert.Null(r.Value));
            }
        }

        [Fact]
        public void The_nsr10_set_applies_the_k3_values_and_keeps_only_travel_distance_unverified()
        {
            RequirementSet s = RequirementSet.Load(JObject.Parse(File.ReadAllText(Path.Combine(StandardsDir(), "co-nsr10-titulo-k-evacuacion.json"))), _ => null);
            Assert.Equal(new[] { "travel-distance-max" }, s.Rules.Where(r => r.UnverifiedValue).Select(r => r.Id).ToArray());

            // K.3.8.3.4: riser 100-180, tread >= 280, 2C+H 620-640; K.3.8.3.3: width >= 900.
            CheckedElement Stair(long id, double riser, double tread, double width) => new CheckedElement
            {
                Id = id, CategoryToken = "OST_Stairs",
                Measures =
                {
                    ["stair_riser_mm"] = MeasuredValue.Exact(riser, "x"), ["stair_tread_mm"] = MeasuredValue.Exact(tread, "x"),
                    ["stair_2r_plus_t_mm"] = MeasuredValue.Exact(2 * riser + tread, "x"), ["stair_run_width_mm"] = MeasuredValue.Exact(width, "x")
                }
            };
            JObject result = CodeCheckRules.Evaluate(s, new[] { Stair(1, 175, 280, 1200), Stair(2, 190, 230, 850) }, 200, true);
            var rows = result["rules"].ToDictionary(r => (string)r["id"], r => (string)r["verdict"]);
            foreach (string id in new[] { "stair-riser-max", "stair-tread-min", "stair-2r-plus-t", "stair-width-min" })
                Assert.Equal("fails", rows[id]);
            var good = result["findings"].Where(f => (long)f["element_id"] == 1).Select(f => (string)f["outcome"]).Distinct().ToArray();
            Assert.Equal(new[] { "passes" }, good);
            // A stair nobody classified as public / over 50 is not examined by the 1 200 mm rule.
            Assert.Equal("not_decidable", rows["stair-width-public-or-over-50"]);
            Assert.Equal("not_decidable", rows["travel-distance-max"]);
        }

        // ---- outcomes -----------------------------------------------------------------

        [Fact]
        public void An_upper_bound_fails_for_certain_below_a_minimum_and_decides_nothing_above_it()
        {
            RequirementSet s = Load("[{ 'id': 'w', 'selector': { 'category': 'OST_Doors' }, 'assertion': { 'measure': 'door_clear_width_mm', 'operator': 'gte', 'value': 800 } }]");
            JObject r = CodeCheckRules.Evaluate(s, new[] { Door(1, 750), Door(2, 900), Door(3, null) }, 50, true);
            var byId = r["findings"].ToDictionary(f => (long)f["element_id"], f => (string)f["outcome"]);
            Assert.Equal("fails", byId[1]);
            Assert.Equal("not_decidable", byId[2]);
            Assert.Equal("not_decidable", byId[3]);
            Assert.Equal("fails", (string)r["rules"][0]["verdict"]);
        }

        [Fact]
        public void A_rule_that_examined_nothing_is_not_decidable_never_a_pass()
        {
            RequirementSet s = Load("[{ 'id': 'w', 'selector': { 'category': 'OST_Ramps' }, 'assertion': { 'measure': 'ramp_slope_percent', 'operator': 'lte', 'value': 8 } }]");
            JObject r = CodeCheckRules.Evaluate(s, new[] { Door(1, 900) }, 50, false);
            Assert.Equal("not_decidable", (string)r["rules"][0]["verdict"]);
            Assert.Equal(0, (int)r["rules"][0]["examined"]);
            Assert.Equal("not_decidable", (string)r["verdict"]);
        }

        [Fact]
        public void Measure_range_selects_by_run_length_and_an_unmeasurable_selector_is_not_decidable()
        {
            RequirementSet s = Load("[{ 'id': 'short', 'selector': { 'category': 'OST_Ramps', 'measure_range': { 'measure': 'ramp_run_length_mm', 'min': 0, 'max': 2520 } }, 'assertion': { 'measure': 'ramp_slope_percent', 'operator': 'lte', 'value': 8.34 } }]");
            CheckedElement Ramp(long id, double run, double slope) => new CheckedElement
            {
                Id = id, CategoryToken = "OST_Ramps",
                Measures = { ["ramp_run_length_mm"] = MeasuredValue.Exact(run, "g"), ["ramp_slope_percent"] = MeasuredValue.Exact(slope, "g") }
            };
            var noGeo = new CheckedElement { Id = 9, CategoryToken = "OST_Ramps", Measures = { ["ramp_run_length_mm"] = MeasuredValue.None("no faces") } };
            JObject r = CodeCheckRules.Evaluate(s, new[] { Ramp(1, 2000, 8.0), Ramp(2, 2000, 10.0), Ramp(3, 6000, 12.0), noGeo }, 50, true);
            var byId = r["findings"].ToDictionary(f => (long)f["element_id"], f => (string)f["outcome"]);
            Assert.Equal("passes", byId[1]);
            Assert.Equal("fails", byId[2]);
            Assert.False(byId.ContainsKey(3));            // out of range: not this rule's
            Assert.Equal("not_decidable", byId[9]);
        }

        [Fact]
        public void A_declared_parameter_that_is_missing_is_not_decidable_when_the_rule_says_so()
        {
            RequirementSet s = Load("[{ 'id': 'lx', 'selector': { 'category': 'OST_Rooms' }, 'assertion': { 'parameter': 'Lux', 'unit': 'lx', 'operator': 'gte', 'value': 500, 'missing_is': 'not_decidable' } }]");
            var with = new CheckedElement { Id = 1, CategoryToken = "OST_Rooms", Params = { ["Lux|lx"] = new ParamFact { Exists = true, Number = 450, Text = "x" } } };
            var without = new CheckedElement { Id = 2, CategoryToken = "OST_Rooms" };
            var unreadable = new CheckedElement { Id = 3, CategoryToken = "OST_Rooms", Params = { ["Lux|lx"] = new ParamFact { Exists = true, Unreadable = true } } };
            JObject r = CodeCheckRules.Evaluate(s, new[] { with, without, unreadable }, 50, true);
            var byId = r["findings"].ToDictionary(f => (long)f["element_id"], f => (string)f["outcome"]);
            Assert.Equal("fails", byId[1]);
            Assert.Equal("not_decidable", byId[2]);
            Assert.Equal("unreadable", byId[3]);
        }

        [Fact]
        public void Travel_distance_is_always_not_decidable_and_says_where_a_route_is_measured()
        {
            RequirementSet s = Load("[{ 'id': 't', 'selector': { 'category': 'OST_Rooms' }, 'assertion': { 'measure': 'travel_distance_m', 'operator': 'lte', 'value': 45 } }]");
            JObject r = CodeCheckRules.Evaluate(s, new[] { new CheckedElement { Id = 1, CategoryToken = "OST_Rooms" } }, 50, true);
            Assert.Equal("not_decidable", (string)r["findings"][0]["outcome"]);
            Assert.Contains("horizun_audit_access", (string)r["findings"][0]["reason"]);
        }

        [Fact]
        public void Classic_parameter_rules_still_evaluate_as_before()
        {
            RequirementSet s = Load("[{ 'id': 'fr', 'selector': { 'category': 'Walls' }, 'assertion': { 'parameter': 'Fire Rating', 'operator': 'not_empty' } }]");
            var a = new CheckedElement { Id = 1, CategoryName = "Walls", Params = { ["Fire Rating"] = new ParamFact { Exists = true, Text = "EI-60" } } };
            var b = new CheckedElement { Id = 2, CategoryName = "Walls", Params = { ["Fire Rating"] = new ParamFact { Exists = true, Text = " " } } };
            JObject r = CodeCheckRules.Evaluate(s, new[] { a, b }, 50, true);
            Assert.Equal(1, r["totals"].Value<int>("passes"));
            Assert.Equal(1, r["totals"].Value<int>("fails"));
        }
    }
}
