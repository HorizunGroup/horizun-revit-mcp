using System;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public sealed class CategoryNameRulesTests
    {
        [Theory]
        // OST_Viewers on Revit 2025: a defined built-in with no Category object still filters by its id (DOC-30c).
        [InlineData(false, true, "RawBuiltIn")]
        // A localized or ordinary name the model resolves.
        [InlineData(true, false, "Found")]
        [InlineData(true, true, "Found")]
        // An invented name, or a number that is no BuiltInCategory.
        [InlineData(false, false, "Unknown")]
        public void Classifies_a_requested_category(bool found, bool definedBuiltIn, string expected)
        {
            Assert.Equal(expected, CategoryNameRules.Classify(found, definedBuiltIn).ToString());
        }

        [Fact]
        public void A_name_no_source_resolves_is_reported_once_even_when_only_links_are_read()
        {
            // source_models: ["ARQ.rvt"] reads one link and no host: a typo is still reported, once.
            var tally = new CategoryNameTally();
            tally.Read("Wals", CategoryResolution.Unknown, tableReadFailed: false);
            tally.Read("Wals", CategoryResolution.Unknown, tableReadFailed: false);
            Assert.Equal(new[] { "Wals" }, tally.UnresolvedEverywhere());
        }

        [Fact]
        public void A_name_one_source_resolves_is_not_reported_in_either_order()
        {
            // An import category present in one link only: the host lacking it lost nothing, and the reverse.
            var hostFirst = new CategoryNameTally();
            hostFirst.Read("plan.dwg", CategoryResolution.Unknown, false);
            hostFirst.Read("plan.dwg", CategoryResolution.Found, false);
            Assert.Empty(hostFirst.UnresolvedEverywhere());
            var linkFirst = new CategoryNameTally();
            linkFirst.Read("plan.dwg", CategoryResolution.Found, false);
            linkFirst.Read("plan.dwg", CategoryResolution.Unknown, false);
            Assert.Empty(linkFirst.UnresolvedEverywhere());
        }

        [Fact]
        public void A_raw_built_in_or_a_failed_table_read_is_not_reported_as_unknown()
        {
            var tally = new CategoryNameTally();
            tally.Read("OST_Viewers", CategoryResolution.RawBuiltIn, false);
            tally.Read("Muros", CategoryResolution.Unknown, tableReadFailed: true);
            Assert.Empty(tally.UnresolvedEverywhere());
        }
    }
}
