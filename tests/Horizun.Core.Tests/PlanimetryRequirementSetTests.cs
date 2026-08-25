// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// The planimetry requirement-set loader, and every refusal it owes.
//
// One property runs through all of them: A RULE THAT DOES NOT RUN REPORTS NO
// FINDINGS, AND NO FINDINGS READS AS A CLEAN MODEL. So a set that is wrong in a
// way the loader could shrug off - an unknown operator, a field the entity does
// not have, a regex that will not compile, a rule id used twice, a selector left
// empty by an edit - is refused WHOLE, with a sentence its author can act on,
// rather than accepted with one clause silently inert.
//
// The other property: the set's identity travels. Its id, version and SHA-256
// are on every finding, so a report can prove which document produced it, and
// the same document always hashes the same however its keys were ordered.
// -----------------------------------------------------------------------------
using System;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class PlanimetryRequirementSetTests
    {
        private static JObject Doc(string rules, string header = null)
        {
            return JObject.Parse("{\"requirement_set\":" +
                (header ?? "{\"id\":\"acme-sheets\",\"version\":\"1.2.0\",\"title\":\"Acme sheets\"}") +
                ",\"rules\":" + rules + "}");
        }

        private const string OneGoodRule =
            "[{\"id\":\"sheet-number\",\"entity\":\"sheet\",\"severity\":\"blocking\"," +
            "\"selector\":{\"applies_to_all\":true}," +
            "\"assertion\":{\"field\":\"sheet_number\",\"operator\":\"matches\",\"value\":\"^A-[0-9]{3}$\"}}]";

        private static PlanimetryRequirementSetException Refused(Func<PlanimetryRequirementSet> load)
        {
            return Assert.Throws<PlanimetryRequirementSetException>(() => load());
        }

        // ---- the happy path, and what it carries -------------------------------

        [Fact]
        public void A_well_formed_set_loads_with_its_identity_and_a_hash()
        {
            PlanimetryRequirementSet set = PlanimetryRequirementSet.Load(Doc(OneGoodRule));
            Assert.Equal("acme-sheets", set.Id);
            Assert.Equal("1.2.0", set.Version);
            Assert.Single(set.Rules);
            Assert.True(set.Rules[0].Blocking);
            Assert.Equal("sheet", set.Rules[0].Entity);
            Assert.Matches("^[0-9a-f]{64}$", set.Sha256);
        }

        [Fact]
        public void The_hash_is_canonical_so_key_order_does_not_change_it()
        {
            string a = "{\"requirement_set\":{\"id\":\"x\",\"version\":\"1.0.0\"},\"rules\":" + OneGoodRule + "}";
            string b = "{\"rules\":" + OneGoodRule + ",\"requirement_set\":{\"version\":\"1.0.0\",\"id\":\"x\"}}";
            Assert.Equal(PlanimetryRequirementSet.Load(JObject.Parse(a)).Sha256,
                         PlanimetryRequirementSet.Load(JObject.Parse(b)).Sha256);
        }

        [Fact]
        public void A_changed_rule_changes_the_hash()
        {
            PlanimetryRequirementSet a = PlanimetryRequirementSet.Load(Doc(OneGoodRule));
            PlanimetryRequirementSet b = PlanimetryRequirementSet.Load(Doc(
                OneGoodRule.Replace("^A-[0-9]{3}$", "^B-[0-9]{3}$")));
            Assert.NotEqual(a.Sha256, b.Sha256);
        }

        [Fact]
        public void Severity_defaults_to_advisory_and_never_to_blocking()
        {
            PlanimetryRequirementSet set = PlanimetryRequirementSet.Load(Doc(
                "[{\"id\":\"r\",\"entity\":\"view\",\"selector\":{\"applies_to_all\":true}," +
                "\"assertion\":{\"field\":\"name\",\"operator\":\"not_empty\"}}]"));
            Assert.False(set.Rules[0].Blocking);
        }

        // ---- the header ---------------------------------------------------------

        [Fact]
        public void A_set_without_an_id_or_a_version_is_refused()
        {
            Assert.Contains("id is required", Refused(() => PlanimetryRequirementSet.Load(
                Doc(OneGoodRule, "{\"version\":\"1.0.0\"}"))).Message);
            Assert.Contains("version is required", Refused(() => PlanimetryRequirementSet.Load(
                Doc(OneGoodRule, "{\"id\":\"x\"}"))).Message);
        }

        [Fact]
        public void A_set_with_no_rules_is_refused_because_examined_nothing_is_not_passed()
        {
            Assert.Contains("must never look like 'passed'",
                Refused(() => PlanimetryRequirementSet.Load(Doc("[]"))).Message);
        }

        [Fact]
        public void An_empty_document_is_refused()
        {
            Assert.Contains("empty", Refused(() => PlanimetryRequirementSet.Load(null)).Message);
        }

        [Fact]
        public void An_unknown_top_level_or_header_key_is_refused_rather_than_ignored()
        {
            JObject doc = Doc(OneGoodRule);
            doc["extra"] = "typo";
            Assert.Contains("Unknown top-level key 'extra'",
                Refused(() => PlanimetryRequirementSet.Load(doc)).Message);

            JObject header = Doc(OneGoodRule, "{\"id\":\"x\",\"version\":\"1.0.0\",\"titel\":\"typo\"}");
            Assert.Contains("Unknown requirement_set key 'titel'",
                Refused(() => PlanimetryRequirementSet.Load(header)).Message);
        }

        // ---- rules --------------------------------------------------------------

        [Fact]
        public void A_duplicated_rule_id_is_refused()
        {
            string rules = "[" +
                "{\"id\":\"same\",\"entity\":\"sheet\",\"selector\":{\"applies_to_all\":true}," +
                "\"assertion\":{\"field\":\"name\",\"operator\":\"not_empty\"}}," +
                "{\"id\":\"same\",\"entity\":\"view\",\"selector\":{\"applies_to_all\":true}," +
                "\"assertion\":{\"field\":\"name\",\"operator\":\"not_empty\"}}]";
            Assert.Contains("'same' is duplicated",
                Refused(() => PlanimetryRequirementSet.Load(Doc(rules))).Message);
        }

        [Fact]
        public void An_unknown_entity_is_refused()
        {
            string rules = "[{\"id\":\"r\",\"entity\":\"wall\",\"selector\":{\"applies_to_all\":true}," +
                           "\"assertion\":{\"field\":\"name\",\"operator\":\"not_empty\"}}]";
            Assert.Contains("entity 'wall' is unknown",
                Refused(() => PlanimetryRequirementSet.Load(Doc(rules))).Message);
        }

        [Fact]
        public void An_unknown_operator_is_refused_and_the_message_lists_the_known_ones()
        {
            string rules = "[{\"id\":\"r\",\"entity\":\"sheet\",\"selector\":{\"applies_to_all\":true}," +
                           "\"assertion\":{\"field\":\"name\",\"operator\":\"startswith\",\"value\":\"A\"}}]";
            string message = Refused(() => PlanimetryRequirementSet.Load(Doc(rules))).Message;
            Assert.Contains("operator 'startswith' is unknown", message);
            Assert.Contains("matches", message);
            Assert.Contains("a skipped rule reports a clean model", message);
        }

        [Fact]
        public void A_field_the_entity_does_not_have_is_refused()
        {
            string rules = "[{\"id\":\"r\",\"entity\":\"sheet\",\"selector\":{\"applies_to_all\":true}," +
                           "\"assertion\":{\"field\":\"scale\",\"operator\":\"equals\",\"value\":50}}]";
            string message = Refused(() => PlanimetryRequirementSet.Load(Doc(rules))).Message;
            Assert.Contains("'scale' is not a field of a sheet", message);
        }

        [Fact]
        public void A_selector_field_the_entity_does_not_have_is_refused()
        {
            string rules = "[{\"id\":\"r\",\"entity\":\"view\",\"selector\":{\"sheet_number_matches\":\"^A\"}," +
                           "\"assertion\":{\"field\":\"name\",\"operator\":\"not_empty\"}}]";
            string message = Refused(() => PlanimetryRequirementSet.Load(Doc(rules))).Message;
            Assert.Contains("names field 'sheet_number'", message);
            Assert.Contains("a rule that selects nothing reports a clean model", message);
        }

        [Fact]
        public void Parameter_fields_are_open_on_sheets_and_views_and_closed_elsewhere()
        {
            PlanimetryRequirementSet ok = PlanimetryRequirementSet.Load(Doc(
                "[{\"id\":\"r\",\"entity\":\"sheet\",\"selector\":{\"applies_to_all\":true}," +
                "\"assertion\":{\"field\":\"parameter:Drawn By\",\"operator\":\"not_empty\"}}]"));
            Assert.Equal("parameter:Drawn By", ok.Rules[0].AssertionField);

            string onATag = "[{\"id\":\"r\",\"entity\":\"tag\",\"selector\":{\"applies_to_all\":true}," +
                            "\"assertion\":{\"field\":\"parameter:Anything\",\"operator\":\"not_empty\"}}]";
            Assert.Contains("not a field of a tag",
                Refused(() => PlanimetryRequirementSet.Load(Doc(onATag))).Message);
        }

        // ---- selectors ----------------------------------------------------------

        [Fact]
        public void An_empty_selector_is_refused_and_the_deliberate_form_is_named()
        {
            string rules = "[{\"id\":\"r\",\"entity\":\"sheet\",\"selector\":{}," +
                           "\"assertion\":{\"field\":\"name\",\"operator\":\"not_empty\"}}]";
            string message = Refused(() => PlanimetryRequirementSet.Load(Doc(rules))).Message;
            Assert.Contains("applies_to_all", message);
            Assert.Contains("indistinguishable", message);
        }

        [Fact]
        public void A_missing_selector_is_refused_the_same_way()
        {
            string rules = "[{\"id\":\"r\",\"entity\":\"sheet\"," +
                           "\"assertion\":{\"field\":\"name\",\"operator\":\"not_empty\"}}]";
            Assert.Contains("has no selector",
                Refused(() => PlanimetryRequirementSet.Load(Doc(rules))).Message);
        }

        [Fact]
        public void Applies_to_all_false_with_nothing_else_selects_nothing_and_is_refused()
        {
            string rules = "[{\"id\":\"r\",\"entity\":\"sheet\",\"selector\":{\"applies_to_all\":false}," +
                           "\"assertion\":{\"field\":\"name\",\"operator\":\"not_empty\"}}]";
            Assert.Contains("selects nothing at all",
                Refused(() => PlanimetryRequirementSet.Load(Doc(rules))).Message);
        }

        [Fact]
        public void Applies_to_takes_explicit_ids()
        {
            PlanimetryRequirementSet set = PlanimetryRequirementSet.Load(Doc(
                "[{\"id\":\"r\",\"entity\":\"viewport\",\"selector\":{\"applies_to\":[12,34]}," +
                "\"assertion\":{\"operator\":\"inside_extent\",\"value\":10}}]"));
            Assert.Single(set.Rules[0].Selectors);
            Assert.Equal("applies_to", set.Rules[0].Selectors[0].Operator);

            string notIntegers = "[{\"id\":\"r\",\"entity\":\"viewport\",\"selector\":{\"applies_to\":[\"12\"]}," +
                                 "\"assertion\":{\"operator\":\"inside_extent\",\"value\":10}}]";
            Assert.Contains("integers only",
                Refused(() => PlanimetryRequirementSet.Load(Doc(notIntegers))).Message);
        }

        // ---- regex --------------------------------------------------------------

        [Fact]
        public void A_regex_that_does_not_compile_is_refused_at_LOAD()
        {
            string rules = "[{\"id\":\"r\",\"entity\":\"sheet\",\"selector\":{\"applies_to_all\":true}," +
                           "\"assertion\":{\"field\":\"name\",\"operator\":\"matches\",\"value\":\"([unclosed\"}}]";
            Assert.Contains("not a valid regex",
                Refused(() => PlanimetryRequirementSet.Load(Doc(rules))).Message);
        }

        [Fact]
        public void A_selector_regex_that_does_not_compile_is_refused_too()
        {
            string rules = "[{\"id\":\"r\",\"entity\":\"sheet\",\"selector\":{\"name_matches\":\"(\"}," +
                           "\"assertion\":{\"field\":\"name\",\"operator\":\"not_empty\"}}]";
            Assert.Contains("not a valid regex",
                Refused(() => PlanimetryRequirementSet.Load(Doc(rules))).Message);
        }

        [Fact]
        public void Every_compiled_pattern_carries_the_match_timeout()
        {
            PlanimetryRequirementSet set = PlanimetryRequirementSet.Load(Doc(OneGoodRule));
            Assert.Equal(PlanimetryRequirementSet.RegexTimeout, set.Rules[0].Pattern.MatchTimeout);
        }

        [Fact]
        public void A_catastrophic_pattern_times_out_and_is_reported_rather_than_thrown()
        {
            // The classic exponential backtracker against a subject that cannot match. With
            // no timeout this does not return; with one, IsMatch says so, and the caller
            // turns that into `unknown` for the element rather than a pass or a crash.
            PlanimetryRequirementSet set = PlanimetryRequirementSet.Load(Doc(
                "[{\"id\":\"r\",\"entity\":\"sheet\",\"selector\":{\"applies_to_all\":true}," +
                "\"assertion\":{\"field\":\"name\",\"operator\":\"matches\",\"value\":\"^(a+)+$\"}}]"));
            bool timedOut;
            bool matched = PlanimetryRequirementSet.IsMatch(
                set.Rules[0].Pattern, new string('a', 40) + "!", out timedOut);
            Assert.False(matched);
            Assert.True(timedOut, "the pattern must report a timeout instead of running forever");
        }

        [Fact]
        public void A_pattern_that_finishes_reports_no_timeout()
        {
            PlanimetryRequirementSet set = PlanimetryRequirementSet.Load(Doc(OneGoodRule));
            bool timedOut;
            Assert.True(PlanimetryRequirementSet.IsMatch(set.Rules[0].Pattern, "A-201", out timedOut));
            Assert.False(timedOut);
            Assert.False(PlanimetryRequirementSet.IsMatch(set.Rules[0].Pattern, "SK-1", out timedOut));
            Assert.False(timedOut);
        }

        // ---- operator/value agreement -------------------------------------------

        [Fact]
        public void An_operator_that_needs_a_value_is_refused_without_one()
        {
            string rules = "[{\"id\":\"r\",\"entity\":\"sheet\",\"selector\":{\"applies_to_all\":true}," +
                           "\"assertion\":{\"field\":\"name\",\"operator\":\"equals\"}}]";
            Assert.Contains("requires value",
                Refused(() => PlanimetryRequirementSet.Load(Doc(rules))).Message);
        }

        [Fact]
        public void An_operator_that_takes_no_value_is_refused_with_one_rather_than_ignoring_it()
        {
            string rules = "[{\"id\":\"r\",\"entity\":\"sheet\",\"selector\":{\"applies_to_all\":true}," +
                           "\"assertion\":{\"field\":\"name\",\"operator\":\"not_empty\",\"value\":\"x\"}}]";
            Assert.Contains("silently ignored",
                Refused(() => PlanimetryRequirementSet.Load(Doc(rules))).Message);
        }

        [Fact]
        public void In_list_requires_a_non_empty_list()
        {
            string scalar = "[{\"id\":\"r\",\"entity\":\"sheet\",\"selector\":{\"applies_to_all\":true}," +
                            "\"assertion\":{\"field\":\"name\",\"operator\":\"in_list\",\"value\":\"A\"}}]";
            Assert.Contains("non-empty list",
                Refused(() => PlanimetryRequirementSet.Load(Doc(scalar))).Message);
        }

        [Fact]
        public void Between_requires_two_ordered_numbers()
        {
            string one = "[{\"id\":\"r\",\"entity\":\"view\",\"selector\":{\"applies_to_all\":true}," +
                         "\"assertion\":{\"field\":\"scale\",\"operator\":\"between\",\"value\":[50]}}]";
            Assert.Contains("exactly two numbers",
                Refused(() => PlanimetryRequirementSet.Load(Doc(one))).Message);

            string inverted = "[{\"id\":\"r\",\"entity\":\"view\",\"selector\":{\"applies_to_all\":true}," +
                              "\"assertion\":{\"field\":\"scale\",\"operator\":\"between\",\"value\":[100,50]}}]";
            Assert.Contains("matches nothing",
                Refused(() => PlanimetryRequirementSet.Load(Doc(inverted))).Message);
        }

        // ---- whole-entity operators ---------------------------------------------

        [Fact]
        public void A_whole_entity_operator_with_a_field_is_refused_rather_than_ignoring_the_field()
        {
            string rules = "[{\"id\":\"r\",\"entity\":\"viewport\",\"selector\":{\"applies_to_all\":true}," +
                           "\"assertion\":{\"field\":\"title\",\"operator\":\"minimum_gap\",\"value\":5}}]";
            Assert.Contains("takes no field",
                Refused(() => PlanimetryRequirementSet.Load(Doc(rules))).Message);
        }

        [Fact]
        public void A_whole_entity_operator_aimed_at_an_entity_it_cannot_measure_is_refused()
        {
            string rules = "[{\"id\":\"r\",\"entity\":\"sheet\",\"selector\":{\"applies_to_all\":true}," +
                           "\"assertion\":{\"operator\":\"minimum_gap\",\"value\":5}}]";
            string message = Refused(() => PlanimetryRequirementSet.Load(Doc(rules))).Message;
            Assert.Contains("cannot be measured on a sheet", message);
            Assert.Contains("viewport", message);
        }

        [Fact]
        public void Minimum_gap_and_inside_extent_need_a_non_negative_number()
        {
            string negative = "[{\"id\":\"r\",\"entity\":\"viewport\",\"selector\":{\"applies_to_all\":true}," +
                              "\"assertion\":{\"operator\":\"minimum_gap\",\"value\":-5}}]";
            Assert.Contains("non-negative number",
                Refused(() => PlanimetryRequirementSet.Load(Doc(negative))).Message);
        }

        [Fact]
        public void Allowed_scale_takes_numbers_and_allowed_template_takes_names()
        {
            PlanimetryRequirementSet ok = PlanimetryRequirementSet.Load(Doc(
                "[{\"id\":\"s\",\"entity\":\"view\",\"selector\":{\"view_type\":\"FloorPlan\"}," +
                "\"assertion\":{\"operator\":\"allowed_scale\",\"value\":[50,100]}}," +
                "{\"id\":\"t\",\"entity\":\"view\",\"selector\":{\"applies_to_all\":true}," +
                "\"assertion\":{\"operator\":\"allowed_template\",\"value\":[\"ARQ-PLANTA\"]}}]"));
            Assert.Equal(2, ok.Rules.Count);

            string wrong = "[{\"id\":\"s\",\"entity\":\"view\",\"selector\":{\"applies_to_all\":true}," +
                           "\"assertion\":{\"operator\":\"allowed_scale\",\"value\":[\"1:50\"]}}]";
            Assert.Contains("list of numbers",
                Refused(() => PlanimetryRequirementSet.Load(Doc(wrong))).Message);
        }

        [Fact]
        public void Forbid_numeric_override_takes_no_value()
        {
            PlanimetryRequirementSet set = PlanimetryRequirementSet.Load(Doc(
                "[{\"id\":\"o\",\"entity\":\"dimension\",\"selector\":{\"applies_to_all\":true}," +
                "\"assertion\":{\"operator\":\"forbid_numeric_override\"}}]"));
            Assert.Equal("forbid_numeric_override", set.Rules[0].Operator);
        }

        // ---- requires_tag, with exclusions --------------------------------------

        [Fact]
        public void Requires_tag_collects_the_categories_the_inventory_must_gather()
        {
            PlanimetryRequirementSet set = PlanimetryRequirementSet.Load(Doc(
                "[{\"id\":\"tags\",\"entity\":\"view\",\"selector\":{\"view_type\":\"FloorPlan\"}," +
                "\"assertion\":{\"operator\":\"requires_tag\",\"value\":[\"OST_Doors\",\"OST_Windows\"]}}]"));
            Assert.Equal(new[] { "OST_Doors", "OST_Windows" }, set.TagCoverageCategories.ToArray());
            Assert.Empty(set.TagCoverageExcludeParameters);
            Assert.Equal(2, set.Rules[0].TagRequirements.Count);
        }

        [Fact]
        public void Requires_tag_accepts_exclusions_and_registers_the_parameter_they_need()
        {
            PlanimetryRequirementSet set = PlanimetryRequirementSet.Load(Doc(
                "[{\"id\":\"tags\",\"entity\":\"view\",\"selector\":{\"applies_to_all\":true}," +
                "\"assertion\":{\"operator\":\"requires_tag\",\"value\":[{" +
                "\"category\":\"OST_Doors\"," +
                "\"exclude_types\":[\"P-01\"]," +
                "\"exclude_families\":[\"Hueco\"]," +
                "\"exclude_type_matches\":\"^TMP-\"," +
                "\"exclude_when_parameter_set\":\"NO_TAG\"}]}}]"));
            TagRequirement req = set.Rules[0].TagRequirements[0];
            Assert.Equal("OST_Doors", req.Category);
            Assert.Equal(new[] { "P-01" }, req.ExcludeTypes.ToArray());
            Assert.Equal(new[] { "Hueco" }, req.ExcludeFamilies.ToArray());
            Assert.NotNull(req.ExcludeTypeMatches);
            Assert.Equal("NO_TAG", req.ExcludeWhenParameterSet);
            Assert.Equal(new[] { "NO_TAG" }, set.TagCoverageExcludeParameters.ToArray());
        }

        [Fact]
        public void A_requires_tag_entry_with_an_unknown_key_or_no_category_is_refused()
        {
            string unknownKey = "[{\"id\":\"t\",\"entity\":\"view\",\"selector\":{\"applies_to_all\":true}," +
                "\"assertion\":{\"operator\":\"requires_tag\",\"value\":[{\"category\":\"OST_Doors\"," +
                "\"exclude_everything\":true}]}}]";
            Assert.Contains("unknown key 'exclude_everything'",
                Refused(() => PlanimetryRequirementSet.Load(Doc(unknownKey))).Message);

            string noCategory = "[{\"id\":\"t\",\"entity\":\"view\",\"selector\":{\"applies_to_all\":true}," +
                "\"assertion\":{\"operator\":\"requires_tag\",\"value\":[{\"exclude_types\":[\"x\"]}]}}]";
            Assert.Contains("has no category",
                Refused(() => PlanimetryRequirementSet.Load(Doc(noCategory))).Message);
        }

        // ---- limits -------------------------------------------------------------

        [Fact]
        public void Too_many_rules_are_refused_with_the_limit_named()
        {
            var rules = new JArray();
            for (int i = 0; i <= PlanimetryRequirementSet.MaxRules; i++)
                rules.Add(JObject.Parse(
                    "{\"id\":\"r" + i + "\",\"entity\":\"sheet\",\"selector\":{\"applies_to_all\":true}," +
                    "\"assertion\":{\"field\":\"name\",\"operator\":\"not_empty\"}}"));
            var doc = JObject.Parse("{\"requirement_set\":{\"id\":\"x\",\"version\":\"1.0.0\"},\"rules\":[]}");
            doc["rules"] = rules;
            string message = Refused(() => PlanimetryRequirementSet.Load(doc)).Message;
            Assert.Contains("the limit is " + PlanimetryRequirementSet.MaxRules, message);
        }

        [Fact]
        public void An_oversized_document_is_refused_before_anything_is_parsed()
        {
            var doc = JObject.Parse(
                "{\"requirement_set\":{\"id\":\"x\",\"version\":\"1.0.0\",\"title\":\"\"},\"rules\":" +
                OneGoodRule + "}");
            doc["requirement_set"]["title"] =
                new string('x', PlanimetryRequirementSet.MaxDocumentChars + 10);
            string message = Refused(() => PlanimetryRequirementSet.Load(doc)).Message;
            Assert.Contains("the limit is " + PlanimetryRequirementSet.MaxDocumentChars, message);
        }

        [Fact]
        public void An_over_long_list_value_is_refused()
        {
            var values = new JArray();
            for (int i = 0; i <= PlanimetryRequirementSet.MaxListValues; i++) values.Add("t" + i);
            var rule = JObject.Parse(
                "{\"id\":\"r\",\"entity\":\"viewport\",\"selector\":{\"applies_to_all\":true}," +
                "\"assertion\":{\"operator\":\"allowed_type\",\"value\":[]}}");
            rule["assertion"]["value"] = values;
            var doc = JObject.Parse("{\"requirement_set\":{\"id\":\"x\",\"version\":\"1.0.0\"},\"rules\":[]}");
            ((JArray)doc["rules"]).Add(rule);
            Assert.Contains("the limit is " + PlanimetryRequirementSet.MaxListValues,
                Refused(() => PlanimetryRequirementSet.Load(doc)).Message);
        }

        // ---- shape --------------------------------------------------------------

        [Fact]
        public void An_unknown_rule_or_assertion_key_is_refused()
        {
            string ruleKey = "[{\"id\":\"r\",\"entity\":\"sheet\",\"selector\":{\"applies_to_all\":true}," +
                             "\"assert\":{\"field\":\"name\",\"operator\":\"not_empty\"}}]";
            Assert.Contains("Unknown rule key 'assert'",
                Refused(() => PlanimetryRequirementSet.Load(Doc(ruleKey))).Message);

            string assertionKey = "[{\"id\":\"r\",\"entity\":\"sheet\",\"selector\":{\"applies_to_all\":true}," +
                "\"assertion\":{\"field\":\"name\",\"operator\":\"not_empty\",\"values\":[1]}}]";
            Assert.Contains("Unknown assertion key 'values'",
                Refused(() => PlanimetryRequirementSet.Load(Doc(assertionKey))).Message);
        }

        [Fact]
        public void Every_published_entity_has_a_field_table()
        {
            foreach (string entity in PlanimetryRequirementSet.Entities)
            {
                Assert.True(PlanimetryRequirementSet.Fields.ContainsKey(entity),
                    entity + " is offered as an entity but has no field table, so every rule about it " +
                    "would be refused as naming an unknown field.");
                Assert.NotEmpty(PlanimetryRequirementSet.Fields[entity]);
            }
        }

        [Fact]
        public void The_entity_list_and_the_contract_enum_agree()
        {
            // The schema publishes the entities a client may write; the loader decides which
            // it accepts. A client told about an entity the loader refuses is a client whose
            // valid-looking set fails at the far end.
            var contract = Horizun.Contracts.Contract.Find("horizun_audit_planimetry");
            Assert.NotNull(contract);
            JToken enumToken = contract.InputSchema["properties"]["requirement_set"]["properties"]["rules"]
                ["items"]["properties"]["entity"]["enum"];
            string[] published = enumToken.Select(t => (string)t).OrderBy(x => x, StringComparer.Ordinal).ToArray();
            Assert.Equal(PlanimetryRequirementSet.Entities.OrderBy(x => x, StringComparer.Ordinal).ToArray(),
                         published);
        }
    }
}
