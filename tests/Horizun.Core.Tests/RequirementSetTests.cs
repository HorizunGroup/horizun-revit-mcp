// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// The requirement-set loader, tested against its own contract: every refusal rule
// in docs/requirement-set.md, plus the operator semantics. These are the pure half
// of story 4.1 - none of them needs Revit, which is exactly why the loader exists
// as its own class. The tests mirror the document's own list of refusals, so if a
// refusal is ever removed from the code the document has to change in the same
// commit, or this file names the drift.
// -----------------------------------------------------------------------------
using System;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class RequirementSetTests
    {
        // ---- a minimal valid document to mutate per test -------------------------

        private static JObject Valid()
        {
            return JObject.Parse(@"{
              'requirement_set': { 'id': 'test-set', 'version': '1.0.0', 'title': 'Test' },
              'rules': [ {
                'id': 'wall-fire-rating',
                'selector': { 'category': 'Walls' },
                'assertion': { 'parameter': 'Fire Rating', 'operator': 'not_empty' }
              } ]
            }".Replace('\'', '"'));
        }

        private static RequirementSet Load(JObject doc) => RequirementSet.Load(doc, _ => null);

        [Fact]
        public void A_valid_set_loads_with_its_rule()
        {
            RequirementSet set = Load(Valid());
            Assert.Equal("test-set", set.Id);
            Assert.Single(set.Rules);
            Assert.False(set.Rules[0].Blocking);   // default advisory, as documented
        }

        // ---- the refusal rules, one by one, in the document's order --------------

        /// <summary>
        /// "A rule with no selector is refused. Not 'warned about' - a rule that matches
        /// everything can drive a remediation across an entire model."
        /// </summary>
        [Fact]
        public void A_rule_with_no_selector_is_refused()
        {
            JObject doc = Valid();
            ((JObject)doc["rules"][0]).Remove("selector");
            var ex = Assert.Throws<RequirementSetException>(() => Load(doc));
            Assert.Contains("no selector", ex.Message);

            doc = Valid();
            doc["rules"][0]["selector"] = new JObject();   // present but empty selects nothing too
            Assert.Throws<RequirementSetException>(() => Load(doc));
        }

        /// <summary>
        /// "An unknown operator is refused, naming the operator and listing the known
        /// ones. Silently skipping it would report a clean model."
        /// </summary>
        [Fact]
        public void An_unknown_operator_is_refused_naming_it_and_listing_the_known_ones()
        {
            JObject doc = Valid();
            doc["rules"][0]["assertion"]["operator"] = "approximately";
            var ex = Assert.Throws<RequirementSetException>(() => Load(doc));
            Assert.Contains("approximately", ex.Message);
            Assert.Contains("is_leaf_of", ex.Message);     // the list is IN the message
            Assert.Contains("not_empty", ex.Message);
        }

        /// <summary>"A comparing operator with no value is refused."</summary>
        [Fact]
        public void A_comparing_operator_with_no_value_is_refused()
        {
            foreach (string op in new[] { "equals", "matches", "in_list", "is_leaf_of", "gt", "lte" })
            {
                JObject doc = Valid();
                doc["rules"][0]["assertion"]["operator"] = op;
                var ex = Assert.Throws<RequirementSetException>(() => Load(doc));
                Assert.Contains("requires value", ex.Message);
            }
            // and the non-comparing ones are fine without one
            foreach (string op in new[] { "exists", "not_exists", "not_empty" })
            {
                JObject doc = Valid();
                doc["rules"][0]["assertion"]["operator"] = op;
                _ = Load(doc);
            }
        }

        /// <summary>
        /// "An unresolvable tables source is refused at load, not at first use: a
        /// classification check that quietly passes because its table is missing is
        /// worse than no check."
        /// </summary>
        [Fact]
        public void An_unresolvable_table_source_is_refused_at_load()
        {
            JObject doc = Valid();
            doc["tables"] = JArray.Parse(@"[ { 'id': 't', 'source': './missing.csv' } ]".Replace('\'', '"'));
            var ex = Assert.Throws<RequirementSetException>(() => RequirementSet.Load(doc, _ => null));
            Assert.Contains("did not resolve", ex.Message);
        }

        /// <summary>"A duplicate rule id is refused. Findings are keyed by it."</summary>
        [Fact]
        public void A_duplicate_rule_id_is_refused()
        {
            JObject doc = Valid();
            ((JArray)doc["rules"]).Add(doc["rules"][0].DeepClone());
            var ex = Assert.Throws<RequirementSetException>(() => Load(doc));
            Assert.Contains("duplicated", ex.Message);
        }

        /// <summary>
        /// "An unknown top-level key is refused ... so a typo is a refusal and not a rule
        /// nobody notices is missing."
        /// </summary>
        [Fact]
        public void An_unknown_top_level_key_is_refused()
        {
            JObject doc = Valid();
            doc["ruels"] = new JArray();   // the typo the rule exists for
            var ex = Assert.Throws<RequirementSetException>(() => Load(doc));
            Assert.Contains("ruels", ex.Message);
        }

        [Fact]
        public void A_set_with_no_rules_is_refused()
        {
            JObject doc = Valid();
            doc["rules"] = new JArray();
            var ex = Assert.Throws<RequirementSetException>(() => Load(doc));
            Assert.Contains("examines nothing", ex.Message);
        }

        [Fact]
        public void A_remediation_must_name_a_tool()
        {
            JObject doc = Valid();
            doc["rules"][0]["remediation"] = new JObject { ["arguments"] = new JObject() };
            var ex = Assert.Throws<RequirementSetException>(() => Load(doc));
            Assert.Contains("no tool", ex.Message);
        }

        [Fact]
        public void An_invalid_selector_regex_is_refused_at_load_not_at_first_element()
        {
            JObject doc = Valid();
            doc["rules"][0]["selector"]["type_name_matches"] = "^EXT-(";
            var ex = Assert.Throws<RequirementSetException>(() => Load(doc));
            Assert.Contains("regex", ex.Message);
        }

        // ---- is_leaf_of: what makes classification checkable without teaching the
        //      bridge any classification system --------------------------------------

        private static RequirementSet WithTable()
        {
            JObject doc = Valid();
            doc["tables"] = JArray.Parse(@"[ { 'id': 'omniclass-22', 'entries': [
                { 'code': '22-01' },
                { 'code': '22-01 10', 'parent': '22-01' },
                { 'code': '22-01 10 10', 'parent': '22-01 10' }
            ] } ]".Replace('\'', '"'));
            doc["rules"][0]["assertion"]["operator"] = "is_leaf_of";
            doc["rules"][0]["assertion"]["value"] = "omniclass-22";
            return Load(doc);
        }

        [Fact]
        public void Is_leaf_of_passes_only_a_last_level_leaf()
        {
            RequirementSet set = WithTable();
            Requirement rule = set.Rules[0];
            Assert.True(set.Passes(rule, true, "22-01 10 10"));   // leaf
            Assert.False(set.Passes(rule, true, "22-01 10"));     // has children
            Assert.False(set.Passes(rule, true, "22-01"));        // root with children
            Assert.False(set.Passes(rule, true, "99-99"));        // not in the table
            Assert.False(set.Passes(rule, false, "22-01 10 10")); // parameter absent
        }

        [Fact]
        public void Is_leaf_of_naming_an_uncarried_table_is_refused_at_load()
        {
            JObject doc = Valid();
            doc["rules"][0]["assertion"]["operator"] = "is_leaf_of";
            doc["rules"][0]["assertion"]["value"] = "uniclass";    // no tables section at all
            var ex = Assert.Throws<RequirementSetException>(() => Load(doc));
            Assert.Contains("uniclass", ex.Message);
        }

        [Fact]
        public void A_csv_table_loads_through_the_injected_resolver()
        {
            JObject doc = Valid();
            doc["tables"] = JArray.Parse(@"[ { 'id': 't', 'source': './t.csv' } ]".Replace('\'', '"'));
            RequirementSet set = RequirementSet.Load(doc,
                _ => "code,title,parent\nA,Root,\nA1,Child,A\n");
            Assert.True(set.Tables["t"].IsLeaf("A1"));
            Assert.False(set.Tables["t"].IsLeaf("A"));
        }

        // ---- operator semantics the measuring half will lean on --------------------

        [Fact]
        public void Numeric_comparisons_parse_invariant_and_fail_closed_on_non_numbers()
        {
            RequirementSet set = Load(Valid());
            var rule = new Requirement { Operator = "gte", Value = new JValue(120.0) };
            Assert.True(set.Passes(rule, true, "120"));
            Assert.True(set.Passes(rule, true, "150.5"));
            Assert.False(set.Passes(rule, true, "90"));
            // A value that is not a number cannot PASS a numeric bar. It does not fail
            // open: gt/gte against prose is false, and the measuring half reports the
            // parameter's text so the reader sees why.
            Assert.False(set.Passes(rule, true, "two hundred"));
            Assert.False(set.Passes(rule, false, "120"));
        }

        [Fact]
        public void Not_empty_distinguishes_absent_from_blank()
        {
            RequirementSet set = Load(Valid());
            Requirement rule = set.Rules[0];   // not_empty
            Assert.True(set.Passes(rule, true, "120"));
            Assert.False(set.Passes(rule, true, ""));
            Assert.False(set.Passes(rule, true, "   "));
            Assert.False(set.Passes(rule, false, null));
        }

        [Fact]
        public void Equality_is_case_insensitive_as_documented()
        {
            RequirementSet set = Load(Valid());
            var rule = new Requirement { Operator = "equals", Value = new JValue("EI-120") };
            Assert.True(set.Passes(rule, true, "ei-120"));
            Assert.False(set.Passes(rule, true, "EI-60"));
        }
    }
}
