// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// Classification codes, proved by running the rules. The state worth the most
// is group_not_terminal: a REAL code that names a group, which passes every
// existence check and every regex, and which nobody can price. A check that
// only asks "does this code exist" reports it as fine.
// -----------------------------------------------------------------------------
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class ClassificationCatalogueTests
    {
        private static ClassificationCatalogue C(string json) =>
            ClassificationCatalogueRules.Read(JToken.Parse(json));

        private static ClassificationCatalogue Sample() => C(@"{
            ""version"": ""v1"",
            ""name"": ""house standard"",
            ""codes"": { ""D10"": false, ""D10-20"": false, ""D10-20-30"": true }
        }");

        // ---------------------------------------------------- the seven states

        [Fact]
        public void A_group_code_is_real_and_still_not_priceable()
        {
            // THE ONE THAT MATTERS. D10 exists, matches any sensible pattern, and
            // names a group. "Does this code exist" reports it as fine.
            Assert.Equal(CodeStatus.GroupNotTerminal,
                ClassificationCatalogueRules.Classify("D10", Sample()));
            Assert.Equal(CodeStatus.Leaf,
                ClassificationCatalogueRules.Classify("D10-20-30", Sample()));
            Assert.Contains("looks most like success", ClassificationCatalogueRules.GroupMeans);
        }

        [Fact]
        public void A_missing_catalogue_is_apart_from_a_code_missing_from_one()
        {
            // One is a missing argument, the other is a code we looked for and did
            // not find. They lead somewhere different.
            Assert.Equal(CodeStatus.CatalogueNotSupplied,
                ClassificationCatalogueRules.Classify("D10", ClassificationCatalogueRules.Read(null)));
            Assert.Equal(CodeStatus.NotInCatalogue,
                ClassificationCatalogueRules.Classify("ZZZ", Sample()));
        }

        [Fact]
        public void A_broken_catalogue_is_apart_from_a_missing_one()
        {
            ClassificationCatalogue broken = C(@"{ ""codes"": { ""A"": true } }");   // no version
            Assert.False(broken.Ok);
            Assert.False(broken.Absent);
            Assert.Equal(CodeStatus.CatalogueUnreadable,
                ClassificationCatalogueRules.Classify("A", broken));
        }

        [Fact]
        public void An_empty_or_blank_code_is_invalid_rather_than_absent_from_the_catalogue()
        {
            Assert.Equal(CodeStatus.Invalid, ClassificationCatalogueRules.Classify("", Sample()));
            Assert.Equal(CodeStatus.Invalid, ClassificationCatalogueRules.Classify("   ", Sample()));
            Assert.Equal(CodeStatus.Invalid, ClassificationCatalogueRules.Classify(null, Sample()));
        }

        [Fact]
        public void A_code_nobody_asked_about_is_not_required_and_never_invalid()
        {
            Assert.Equal(CodeStatus.NotRequired,
                ClassificationCatalogueRules.Classify("anything", Sample(), required: false));
            // even with no catalogue at all
            Assert.Equal(CodeStatus.NotRequired,
                ClassificationCatalogueRules.Classify("", null, required: false));
        }

        [Fact]
        public void Codes_are_matched_without_surrounding_whitespace_and_without_case()
        {
            Assert.Equal(CodeStatus.Leaf, ClassificationCatalogueRules.Classify("  d10-20-30  ", Sample()));
        }

        // ------------------------------------------------------ what is refused

        [Fact]
        public void No_catalogue_is_compiled_in()
        {
            ClassificationCatalogue none = ClassificationCatalogueRules.Read(null);
            Assert.True(none.Absent);
            Assert.Empty(none.Codes);
            Assert.Contains("belong to somebody and not to everybody", ClassificationCatalogueRules.Means);
        }

        [Fact]
        public void Leafness_is_declared_and_never_inferred_from_the_codes_shape()
        {
            // Prefix inference guesses a taxonomy's structure and guesses wrong on
            // every standard that reuses its separators.
            ClassificationCatalogue bad = C(@"{ ""version"": ""v1"", ""codes"": { ""A-1"": ""leaf"" } }");
            Assert.False(bad.Ok);
            Assert.Equal(CatalogueCodes.BadShape, bad.Code);
            Assert.Contains("prefix inference", bad.Message);
        }

        [Fact]
        public void An_empty_catalogue_is_refused_rather_than_failing_every_code()
        {
            ClassificationCatalogue empty = C(@"{ ""version"": ""v1"", ""codes"": {} }");
            Assert.False(empty.Ok);
            Assert.Equal(CatalogueCodes.EmptyCodes, empty.Code);
            Assert.Contains("Omit the catalogue", empty.Message);
        }

        [Fact]
        public void A_catalogue_without_a_version_is_refused()
        {
            Assert.Equal(CatalogueCodes.NoVersion, C(@"{ ""codes"": { ""A"": true } }").Code);
        }

        // ------------------------------------------------------------- tally

        [Fact]
        public void The_tally_keeps_all_seven_states_and_names_the_catalogue()
        {
            JObject t = ClassificationCatalogueRules.Tally(
                new[] { CodeStatus.Leaf, CodeStatus.GroupNotTerminal, CodeStatus.GroupNotTerminal },
                Sample());

            foreach (string s in CodeStatus.All) Assert.NotNull(t[s]);
            Assert.Equal(1, t.Value<long>(CodeStatus.Leaf));
            Assert.Equal(2, t.Value<long>(CodeStatus.GroupNotTerminal));
            Assert.Equal("ok", t.Value<string>("catalogue"));
            Assert.Equal("house standard", t.Value<string>("catalogue_name"));
            Assert.Equal(3, t.Value<int>("catalogue_codes"));
        }

        [Fact]
        public void With_no_catalogue_the_tally_says_not_supplied_and_counts_no_codes()
        {
            JObject t = ClassificationCatalogueRules.Tally(null, ClassificationCatalogueRules.Read(null));
            Assert.Equal("not_supplied", t.Value<string>("catalogue"));
            Assert.Null(t["catalogue_codes"].Value<int?>());
        }
    }
}
