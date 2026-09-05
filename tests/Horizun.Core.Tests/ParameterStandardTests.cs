// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// Parameter standards, proved by running the rules. The test that matters most
// is the homonym pair: a parameter called the right thing with the wrong GUID
// must NOT satisfy a rule keyed by GUID. A model full of the wrong "Fire
// Rating" looks compliant and will not schedule, and no other check in this
// area catches it.
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class ParameterStandardTests
    {
        private const string GuidA = "11111111-2222-3333-4444-555555555555";
        private const string GuidB = "99999999-8888-7777-6666-555555555555";

        private static readonly string[] BuiltIns = { "ALL_MODEL_MARK", "DOOR_NUMBER" };

        private static ParameterProfile P(string json) =>
            ParameterStandardRules.Read(JToken.Parse(json), n => BuiltIns.Contains(n));

        private static ParameterObservation O(bool present = true, string value = "A-1",
                                              string guid = null, bool isType = false,
                                              string category = "Doors", string storage = "String")
        {
            return new ParameterObservation
            {
                ElementId = 7,
                Category = category,
                IsType = isType,
                Present = present,
                Guid = guid,
                IsShared = guid != null,
                StorageType = storage,
                ValueAsString = value,
                HasValue = value != null,
                Binding = isType ? ParameterScope.Type : ParameterScope.Instance
            };
        }

        private static string Outcome(ParameterProfile p, ParameterObservation o) =>
            ParameterStandardRules.Evaluate(p.Rules[0], o).Outcome;

        // --------------------------------------------------------- identity

        [Fact]
        public void The_right_name_with_the_wrong_guid_does_not_satisfy_a_guid_rule()
        {
            // THE ONE THAT MATTERS. Two parameters of one name are two parameters.
            ParameterProfile p = P(@"{ ""version"": ""v1"", ""rules"": [
                { ""id"": ""fire"", ""name"": ""Fire Rating"", ""guid"": """ + GuidA + @""",
                  ""categories"": [""Doors""] } ] }");
            Assert.True(p.Ok, p.Message);

            Assert.Equal(ParameterOutcome.WrongGuid, Outcome(p, O(guid: GuidB)));
            Assert.Equal(ParameterOutcome.Present, Outcome(p, O(guid: GuidA)));
        }

        [Fact]
        public void A_project_parameter_with_no_guid_does_not_satisfy_a_guid_rule()
        {
            // Not shared at all: the guid is absent, not merely different, and the
            // message has to say which so somebody can act on it.
            ParameterProfile p = P(@"{ ""version"": ""v1"", ""rules"": [
                { ""id"": ""fire"", ""name"": ""Fire Rating"", ""guid"": """ + GuidA + @""",
                  ""categories"": [""Doors""] } ] }");
            ParameterVerdict v = ParameterStandardRules.Evaluate(p.Rules[0], O(guid: null));
            Assert.Equal(ParameterOutcome.WrongGuid, v.Outcome);
            Assert.Contains("not shared", v.Detail);
        }

        [Fact]
        public void A_rule_keyed_by_name_alone_is_satisfied_by_the_name()
        {
            ParameterProfile p = P(@"{ ""version"": ""v1"", ""rules"": [
                { ""id"": ""mark"", ""name"": ""Mark"", ""categories"": [""Doors""] } ] }");
            Assert.Equal(ParameterOutcome.Present, Outcome(p, O(guid: null)));
        }

        [Fact]
        public void The_reply_says_why_a_name_is_not_an_identity()
        {
            Assert.Contains("a parameter is NOT its name", ParameterStandardRules.IdentityMeans);
            Assert.Contains("will not schedule", ParameterStandardRules.IdentityMeans);
        }

        // ------------------------------------------------------------ scope

        [Fact]
        public void A_type_rule_read_on_an_instance_is_wrong_scope_and_not_a_missing_parameter()
        {
            // "Missing" would send somebody looking for a parameter that is there,
            // on the type, where the rule did not look.
            ParameterProfile p = P(@"{ ""version"": ""v1"", ""rules"": [
                { ""id"": ""t"", ""name"": ""X"", ""scope"": ""type"", ""categories"": [""Doors""] } ] }");
            Assert.Equal(ParameterOutcome.WrongScope, Outcome(p, O(isType: false)));
            Assert.Equal(ParameterOutcome.Present, Outcome(p, O(isType: true)));
        }

        [Fact]
        public void A_type_parameter_is_judged_once_and_carries_its_instance_count()
        {
            // One wrong type must not become four hundred findings.
            ParameterProfile p = P(@"{ ""version"": ""v1"", ""rules"": [
                { ""id"": ""t"", ""name"": ""X"", ""scope"": ""type"", ""categories"": [""Doors""] } ] }");
            ParameterObservation o = O(isType: true, value: null);
            o.AffectedInstanceIds.AddRange(new long[] { 1, 2, 3, 4 });

            ParameterVerdict v = ParameterStandardRules.Evaluate(p.Rules[0], o);
            Assert.Equal(ParameterOutcome.Empty, v.Outcome);
            Assert.Equal(4, v.AffectedInstances);
            Assert.True(v.IsType);
            Assert.Equal(4, ParameterStandardRules.ToJson(v).Value<int>("affected_instances"));
            Assert.Contains("not once per instance", ParameterStandardRules.TypeEvaluationMeans);
        }

        // --------------------------------------------------------- outcomes

        [Fact]
        public void Missing_empty_and_placeholder_are_three_different_answers()
        {
            ParameterProfile p = P(@"{ ""version"": ""v1"", ""rules"": [
                { ""id"": ""m"", ""name"": ""Mark"", ""categories"": [""Doors""],
                  ""placeholders"": [""TBD""] } ] }");

            Assert.Equal(ParameterOutcome.Missing, Outcome(p, O(present: false)));
            Assert.Equal(ParameterOutcome.Empty, Outcome(p, O(value: "   ")));
            Assert.Equal(ParameterOutcome.Placeholder, Outcome(p, O(value: "TBD")));
            Assert.Equal(ParameterOutcome.Present, Outcome(p, O(value: "D-01")));
        }

        [Fact]
        public void An_unreadable_parameter_is_neither_missing_nor_satisfied()
        {
            ParameterProfile p = P(@"{ ""version"": ""v1"", ""rules"": [
                { ""id"": ""m"", ""name"": ""Mark"", ""categories"": [""Doors""] } ] }");
            ParameterObservation o = O();
            o.Readable = false;
            Assert.Equal(ParameterOutcome.Unreadable, Outcome(p, o));
        }

        [Fact]
        public void A_category_the_rule_does_not_name_is_not_applicable_rather_than_passing()
        {
            ParameterProfile p = P(@"{ ""version"": ""v1"", ""rules"": [
                { ""id"": ""m"", ""name"": ""Mark"", ""categories"": [""Doors""] } ] }");
            Assert.Equal(ParameterOutcome.CategoryNotApplicable, Outcome(p, O(category: "Windows")));
        }

        [Fact]
        public void An_optional_parameter_that_is_absent_is_not_requested_rather_than_missing()
        {
            ParameterProfile p = P(@"{ ""version"": ""v1"", ""rules"": [
                { ""id"": ""m"", ""name"": ""Mark"", ""required"": false, ""categories"": [""Doors""] } ] }");
            Assert.Equal(ParameterOutcome.RuleNotRequested, Outcome(p, O(present: false)));
        }

        [Fact]
        public void An_empty_value_passes_only_where_the_caller_allowed_it()
        {
            ParameterProfile p = P(@"{ ""version"": ""v1"", ""rules"": [
                { ""id"": ""m"", ""name"": ""Mark"", ""allow_empty"": true, ""categories"": [""Doors""] } ] }");
            Assert.Equal(ParameterOutcome.Present, Outcome(p, O(value: "")));
        }

        [Fact]
        public void A_wrong_storage_type_and_a_wrong_specification_are_different_findings()
        {
            ParameterProfile p = P(@"{ ""version"": ""v1"", ""rules"": [
                { ""id"": ""m"", ""name"": ""H"", ""storage_type"": ""Double"", ""categories"": [""Doors""] } ] }");
            Assert.Equal(ParameterOutcome.WrongStorageType, Outcome(p, O(storage: "String")));

            ParameterProfile q = P(@"{ ""version"": ""v1"", ""rules"": [
                { ""id"": ""m"", ""name"": ""H"", ""specification"": ""autodesk.spec.aec:length"",
                  ""categories"": [""Doors""] } ] }");
            ParameterObservation o = O();
            o.Specification = "autodesk.spec.aec:area";
            Assert.Equal(ParameterOutcome.WrongSpecification, Outcome(q, o));
        }

        [Fact]
        public void A_wrong_binding_is_reported_as_such()
        {
            ParameterProfile p = P(@"{ ""version"": ""v1"", ""rules"": [
                { ""id"": ""m"", ""name"": ""X"", ""scope"": ""type"", ""expected_binding"": ""type"",
                  ""categories"": [""Doors""] } ] }");
            ParameterObservation o = O(isType: true);
            o.Binding = ParameterScope.Instance;
            Assert.Equal(ParameterOutcome.WrongBinding, Outcome(p, o));
        }

        [Fact]
        public void Allowed_forbidden_and_regex_all_report_an_invalid_value()
        {
            Assert.Equal(ParameterOutcome.InvalidValue, Outcome(P(@"{ ""version"": ""v1"", ""rules"": [
                { ""id"": ""a"", ""name"": ""X"", ""allowed_values"": [""OK""], ""categories"": [""Doors""] } ] }"),
                O(value: "NO")));

            Assert.Equal(ParameterOutcome.InvalidValue, Outcome(P(@"{ ""version"": ""v1"", ""rules"": [
                { ""id"": ""b"", ""name"": ""X"", ""forbidden_values"": [""NO""], ""categories"": [""Doors""] } ] }"),
                O(value: "NO")));

            Assert.Equal(ParameterOutcome.InvalidValue, Outcome(P(@"{ ""version"": ""v1"", ""rules"": [
                { ""id"": ""c"", ""name"": ""X"", ""regex"": ""^[0-9]+$"", ""categories"": [""Doors""] } ] }"),
                O(value: "12a")));
        }

        [Fact]
        public void A_range_is_applied_and_a_value_that_is_not_a_number_is_unreadable()
        {
            ParameterProfile p = P(@"{ ""version"": ""v1"", ""rules"": [
                { ""id"": ""r"", ""name"": ""H"", ""storage_type"": ""Double"", ""minimum"": 10, ""maximum"": 20,
                  ""categories"": [""Doors""] } ] }");

            ParameterObservation low = O(storage: "Double", value: "5");
            low.ValueAsDouble = 5;
            Assert.Equal(ParameterOutcome.InvalidValue, Outcome(p, low));

            ParameterObservation good = O(storage: "Double", value: "15");
            good.ValueAsDouble = 15;
            Assert.Equal(ParameterOutcome.Present, Outcome(p, good));

            // A range was declared and nothing numeric came back. Unknown, not invalid.
            ParameterObservation text = O(storage: "Double", value: "tall");
            Assert.Equal(ParameterOutcome.Unreadable, Outcome(p, text));
        }

        [Fact]
        public void An_explicit_exception_makes_the_rule_not_requested()
        {
            ParameterProfile p = P(@"{ ""version"": ""v1"", ""rules"": [
                { ""id"": ""m"", ""name"": ""Mark"", ""categories"": [""Doors""], ""exceptions"": [""7""] } ] }");
            Assert.Equal(ParameterOutcome.RuleNotRequested, Outcome(p, O(present: false)));
        }

        [Fact]
        public void The_tally_keeps_all_thirteen_outcomes_apart()
        {
            JObject t = ParameterStandardRules.Tally(new List<ParameterVerdict>
            {
                new ParameterVerdict { Outcome = ParameterOutcome.Missing },
                new ParameterVerdict { Outcome = ParameterOutcome.Empty },
                new ParameterVerdict { Outcome = ParameterOutcome.Empty }
            });
            foreach (string o in ParameterOutcome.All) Assert.NotNull(t[o]);
            Assert.Equal(1, t.Value<long>(ParameterOutcome.Missing));
            Assert.Equal(2, t.Value<long>(ParameterOutcome.Empty));
            Assert.Equal(0, t.Value<long>(ParameterOutcome.Present));
        }

        // ----------------------------------------------------------- refusals

        [Fact]
        public void With_no_profile_nothing_is_evaluated()
        {
            ParameterProfile p = ParameterStandardRules.Read(null, n => true);
            Assert.True(p.Absent);
            Assert.Contains("NOT a pass", p.Message);
            Assert.Empty(ParameterStandardRules.Evaluate(new[] { O(present: false) }, p));
        }

        [Fact]
        public void A_rule_that_names_no_parameter_is_refused()
        {
            ParameterProfile p = P(@"{ ""version"": ""v1"", ""rules"": [
                { ""id"": ""x"", ""categories"": [""Doors""] } ] }");
            Assert.Equal(ParameterRuleCodes.NoIdentity, p.Code);
        }

        [Fact]
        public void An_invalid_guid_and_an_unknown_built_in_are_refused()
        {
            Assert.Equal(ParameterRuleCodes.BadGuid, P(@"{ ""version"": ""v1"", ""rules"": [
                { ""id"": ""x"", ""guid"": ""not-a-guid"", ""categories"": [""Doors""] } ] }").Code);

            Assert.Equal(ParameterRuleCodes.UnknownBuiltIn, P(@"{ ""version"": ""v1"", ""rules"": [
                { ""id"": ""x"", ""built_in_parameter"": ""NO_SUCH_PARAM"", ""categories"": [""Doors""] } ] }").Code);
        }

        [Fact]
        public void An_invalid_regex_is_refused_rather_than_skipped()
        {
            // A rule that silently does not run reports every value as acceptable.
            ParameterProfile p = P(@"{ ""version"": ""v1"", ""rules"": [
                { ""id"": ""x"", ""name"": ""X"", ""regex"": ""[unclosed"", ""categories"": [""Doors""] } ] }");
            Assert.Equal(ParameterRuleCodes.BadRegex, p.Code);
            Assert.Contains("silently does not run", p.Message);
        }

        [Fact]
        public void An_incoherent_range_and_an_incompatible_unit_are_refused()
        {
            Assert.Equal(ParameterRuleCodes.BadRange, P(@"{ ""version"": ""v1"", ""rules"": [
                { ""id"": ""x"", ""name"": ""X"", ""minimum"": 10, ""maximum"": 2,
                  ""categories"": [""Doors""] } ] }").Code);

            // A numeric bound on text is a contradiction, not a stricter rule.
            Assert.Equal(ParameterRuleCodes.BadRange, P(@"{ ""version"": ""v1"", ""rules"": [
                { ""id"": ""y"", ""name"": ""X"", ""storage_type"": ""String"", ""minimum"": 1,
                  ""categories"": [""Doors""] } ] }").Code);

            Assert.Equal(ParameterRuleCodes.BadUnit, P(@"{ ""version"": ""v1"", ""rules"": [
                { ""id"": ""z"", ""name"": ""X"", ""storage_type"": ""String"", ""unit"": ""millimeters"",
                  ""categories"": [""Doors""] } ] }").Code);
        }

        [Fact]
        public void A_contradictory_scope_and_binding_is_refused()
        {
            // Reads the type, expects an instance binding: no model satisfies both.
            ParameterProfile p = P(@"{ ""version"": ""v1"", ""rules"": [
                { ""id"": ""x"", ""name"": ""X"", ""scope"": ""type"", ""expected_binding"": ""instance"",
                  ""categories"": [""Doors""] } ] }");
            Assert.Equal(ParameterRuleCodes.BadScope, p.Code);
            Assert.Contains("Nothing can satisfy both", p.Message);
        }

        [Fact]
        public void A_rule_that_applies_to_nothing_is_refused()
        {
            ParameterProfile p = P(@"{ ""version"": ""v1"", ""rules"": [
                { ""id"": ""x"", ""name"": ""X"", ""categories"": [] } ] }");
            Assert.Equal(ParameterRuleCodes.EmptyCategories, p.Code);
        }

        [Fact]
        public void Two_rules_with_one_id_are_refused()
        {
            ParameterProfile p = P(@"{ ""version"": ""v1"", ""rules"": [
                { ""id"": ""dup"", ""name"": ""A"", ""categories"": [""Doors""] },
                { ""id"": ""dup"", ""name"": ""B"", ""categories"": [""Doors""] } ] }");
            Assert.Equal(ParameterRuleCodes.DuplicateId, p.Code);
        }

        [Fact]
        public void An_unknown_key_is_refused_at_the_profile_and_at_the_rule()
        {
            Assert.Equal(ParameterRuleCodes.UnknownKey,
                P(@"{ ""version"": ""v1"", ""rules"": [], ""bogus"": 1 }").Code);
            Assert.Equal(ParameterRuleCodes.UnknownKey, P(@"{ ""version"": ""v1"", ""rules"": [
                { ""id"": ""x"", ""name"": ""X"", ""categories"": [""Doors""], ""bogus"": 1 } ] }").Code);
        }

        [Fact]
        public void A_refused_profile_is_not_evaluated_even_though_it_read_earlier_rules()
        {
            // The mandate's own case: rules parsed before the bad one must not be
            // enforced against a model the caller was told was not being judged.
            ParameterProfile p = P(@"{ ""version"": ""v1"", ""rules"": [
                { ""id"": ""good"", ""name"": ""Mark"", ""categories"": [""Doors""] },
                { ""id"": ""bad"", ""name"": ""X"", ""regex"": ""[unclosed"", ""categories"": [""Doors""] } ] }");
            Assert.False(p.Ok);
            Assert.Single(p.Rules);                       // it really did read one
            Assert.Empty(ParameterStandardRules.Evaluate(new[] { O(present: false) }, p));
        }

        [Fact]
        public void Nothing_from_a_profile_is_executed_and_the_reply_says_so()
        {
            Assert.Contains("No expression, script or code", ParameterStandardRules.NothingIsExecuted);
            Assert.Contains("runs with a timeout", ParameterStandardRules.NothingIsExecuted);
            Assert.True(ParameterStandardRules.RegexTimeout.TotalMilliseconds > 0);
        }
    }
}
