// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// The documentary context, proved by running the rules. Two properties carry
// it, and both are about telling apart things that look identical on a title
// block:
//
//   a field that does not exist   vs   a field that exists and is blank
//   "Client" with a GUID          vs   "Client" somebody typed
//
// The outcomes come from Core/ParameterStandardRules rather than a second copy,
// so wrong_guid means the same thing here as it does there.
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class DocumentaryContextTests
    {
        private const string GuidA = "aaaaaaaa-1111-2222-3333-444444444444";
        private const string GuidB = "bbbbbbbb-1111-2222-3333-444444444444";

        private static ParameterProfile P(string json) =>
            ParameterStandardRules.Read(JToken.Parse(json), n => true);

        private static DocumentaryFact F(string field, string value, bool present = true,
                                         bool readable = true, string guid = null)
        {
            return new DocumentaryFact
            {
                Field = field,
                Surface = DocumentarySurface.ProjectInformation,
                Present = present,
                Readable = readable,
                Value = value,
                Guid = guid,
                ElementId = 7
            };
        }

        private static string OutcomeOf(List<DocumentaryVerdict> vs, string field) =>
            vs.Single(v => v.Field == field).Outcome;

        // ------------------------------------------------- absent vs empty

        [Fact]
        public void A_field_that_does_not_exist_is_apart_from_one_that_exists_and_is_blank()
        {
            // THE ONE THAT MATTERS. Both render as an empty title block cell; one is
            // a template that never carried the parameter and the other is somebody
            // who never filled it in.
            ParameterProfile p = P(@"{ ""version"": ""v1"", ""rules"": [
                { ""id"": ""client_name"", ""name"": ""Client Name"", ""categories"": [""project_information""] } ] }");

            List<DocumentaryVerdict> absent = DocumentaryContextRules.EvaluateAll(
                new[] { F("client_name", null, present: false) }, p);
            List<DocumentaryVerdict> blank = DocumentaryContextRules.EvaluateAll(
                new[] { F("client_name", "   ") }, p);

            Assert.Equal(ParameterOutcome.Missing, OutcomeOf(absent, "client_name"));
            Assert.Equal(ParameterOutcome.Empty, OutcomeOf(blank, "client_name"));
            Assert.Contains("different problems", DocumentaryContextRules.AbsentVersusEmptyMeans);
        }

        [Fact]
        public void A_field_that_could_not_be_read_is_neither_absent_nor_blank()
        {
            ParameterProfile p = P(@"{ ""version"": ""v1"", ""rules"": [
                { ""id"": ""client_name"", ""name"": ""Client Name"", ""categories"": [""project_information""] } ] }");
            List<DocumentaryVerdict> v = DocumentaryContextRules.EvaluateAll(
                new[] { F("client_name", null, readable: false) }, p);
            Assert.Equal(ParameterOutcome.Unreadable, OutcomeOf(v, "client_name"));
        }

        // ---------------------------------------------------------- identity

        [Fact]
        public void A_field_with_the_right_name_and_the_wrong_guid_does_not_satisfy_a_guid_rule()
        {
            // Reused from the parameter machinery rather than reimplemented: two
            // parameters called "Client" are two parameters.
            ParameterProfile p = P(@"{ ""version"": ""v1"", ""rules"": [
                { ""id"": ""client_name"", ""name"": ""Client Name"", ""guid"": """ + GuidA + @""",
                  ""categories"": [""project_information""] } ] }");

            Assert.Equal(ParameterOutcome.WrongGuid, OutcomeOf(
                DocumentaryContextRules.EvaluateAll(new[] { F("client_name", "Acme", guid: GuidB) }, p),
                "client_name"));
            Assert.Equal(ParameterOutcome.Present, OutcomeOf(
                DocumentaryContextRules.EvaluateAll(new[] { F("client_name", "Acme", guid: GuidA) }, p),
                "client_name"));
        }

        // ------------------------------------------------------- no profile

        [Fact]
        public void With_no_profile_every_field_is_not_requested_and_that_is_not_a_pass()
        {
            List<DocumentaryVerdict> v = DocumentaryContextRules.EvaluateAll(
                new[] { F("client_name", null, present: false), F("project_number", "") },
                ParameterStandardRules.Read(null, n => true));

            Assert.All(v, x => Assert.Equal(ParameterOutcome.RuleNotRequested, x.Outcome));
            Assert.Contains("NOT a pass", DocumentaryContextRules.NoProfileMeans);
            Assert.Contains("none is compiled in here", DocumentaryContextRules.NoProfileMeans);
        }

        [Fact]
        public void No_corporate_field_is_compiled_in()
        {
            // The surfaces are named; the FIELDS a project must carry are not.
            Assert.Contains(DocumentarySurface.ProjectInformation, DocumentarySurface.All);
            Assert.DoesNotContain("client_name", DocumentarySurface.All);
            Assert.DoesNotContain("project_number", DocumentarySurface.All);
        }

        // ---------------------------------------------------- placeholders

        [Fact]
        public void A_declared_placeholder_is_its_own_outcome_and_not_a_filled_field()
        {
            ParameterProfile p = P(@"{ ""version"": ""v1"", ""rules"": [
                { ""id"": ""project_name"", ""name"": ""Project Name"",
                  ""categories"": [""project_information""],
                  ""placeholders"": [""Project Name"", ""TBD""] } ] }");

            Assert.Equal(ParameterOutcome.Placeholder, OutcomeOf(
                DocumentaryContextRules.EvaluateAll(new[] { F("project_name", "TBD") }, p), "project_name"));
            Assert.Equal(ParameterOutcome.Present, OutcomeOf(
                DocumentaryContextRules.EvaluateAll(new[] { F("project_name", "Tower") }, p), "project_name"));
        }

        [Fact]
        public void A_value_failing_a_declared_pattern_is_invalid()
        {
            ParameterProfile p = P(@"{ ""version"": ""v1"", ""rules"": [
                { ""id"": ""project_number"", ""name"": ""Project Number"",
                  ""categories"": [""project_information""], ""regex"": ""^[0-9]{4}$"" } ] }");
            Assert.Equal(ParameterOutcome.InvalidValue, OutcomeOf(
                DocumentaryContextRules.EvaluateAll(new[] { F("project_number", "12") }, p), "project_number"));
        }

        // -------------------------------------------------------- coverage

        [Fact]
        public void A_rule_about_a_field_nothing_collected_is_named_as_this_tools_gap()
        {
            // Not silently dropped, and not reported as a defect in the model.
            ParameterProfile p = P(@"{ ""version"": ""v1"", ""rules"": [
                { ""id"": ""a_field_nobody_reads"", ""name"": ""X"",
                  ""categories"": [""project_information""] } ] }");

            DocumentaryVerdict v = Assert.Single(DocumentaryContextRules.EvaluateAll(
                new DocumentaryFact[0], p));
            Assert.Equal("a_field_nobody_reads", v.Field);
            Assert.Contains("gap in THIS TOOL", v.Detail);
        }

        [Fact]
        public void A_field_the_profile_does_not_mention_is_not_requested_rather_than_dropped()
        {
            ParameterProfile p = P(@"{ ""version"": ""v1"", ""rules"": [
                { ""id"": ""client_name"", ""name"": ""Client Name"",
                  ""categories"": [""project_information""] } ] }");
            List<DocumentaryVerdict> v = DocumentaryContextRules.EvaluateAll(
                new[] { F("client_name", "Acme"), F("author", "") }, p);

            Assert.Equal(2, v.Count);
            Assert.Equal(ParameterOutcome.RuleNotRequested, OutcomeOf(v, "author"));
        }

        [Fact]
        public void The_tally_keeps_every_outcome_and_names_the_profile_state()
        {
            ParameterProfile p = P(@"{ ""version"": ""v1"", ""rules"": [
                { ""id"": ""client_name"", ""name"": ""Client Name"",
                  ""categories"": [""project_information""] } ] }");
            JObject t = DocumentaryContextRules.Tally(
                DocumentaryContextRules.EvaluateAll(new[] { F("client_name", null, present: false) }, p), p);

            foreach (string o in ParameterOutcome.All) Assert.NotNull(t[o]);
            Assert.Equal(1, t.Value<long>(ParameterOutcome.Missing));
            Assert.Equal("ok", t.Value<string>("profile"));
        }

        [Fact]
        public void A_refused_profile_judges_nothing_and_says_refused()
        {
            ParameterProfile p = P(@"{ ""version"": ""v1"", ""rules"": [
                { ""id"": ""a"", ""name"": ""X"", ""categories"": [""project_information""] },
                { ""id"": ""b"", ""name"": ""Y"", ""regex"": ""[unclosed"", ""categories"": [""project_information""] } ] }");
            Assert.False(p.Ok);

            List<DocumentaryVerdict> v = DocumentaryContextRules.EvaluateAll(new[] { F("a", null, present: false) }, p);
            Assert.Equal(ParameterOutcome.RuleNotRequested, OutcomeOf(v, "a"));
            Assert.Equal("refused", DocumentaryContextRules.Tally(v, p).Value<string>("profile"));
        }
    }
}
