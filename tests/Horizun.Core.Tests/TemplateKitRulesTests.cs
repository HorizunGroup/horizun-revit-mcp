using System;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class TemplateKitRulesTests
    {
        [Theory]
        [InlineData(false, null, "absent")]
        [InlineData(true, "", "empty")]
        [InlineData(true, "   ", "empty")]
        [InlineData(true, "pending", "not_allowed")]
        [InlineData(true, "checked", null)]
        public void Traceability_distinguishes_absence_empty_and_disallowed(bool exists, string value, string expected)
            => Assert.Equal(expected, TemplateKitRules.TraceIssue(exists, value, new[] { "checked" }));

        [Fact]
        public void Old_kits_without_allowed_values_accept_any_nonblank_value()
            => Assert.Null(TemplateKitRules.TraceIssue(true, "drawing A-101", null));

        [Theory]
        [InlineData("vendor_test door", true)]
        [InlineData("a TEMP__ type", true)]
        [InlineData("ordinary door", false)]
        public void Forbidden_names_are_kit_supplied_regexes_and_literal_fragments(string text, bool forbidden)
            => Assert.Equal(forbidden, TemplateKitRules.ForbiddenName(text, new[] { "^vendor_[a-z]+" }, new[] { "TEMP__" }));

        [Fact]
        public void Invalid_regex_is_not_a_clean_audit()
            => Assert.ThrowsAny<ArgumentException>(() => TemplateKitRules.ForbiddenName("", new[] { "[" }, null));
    }
}
