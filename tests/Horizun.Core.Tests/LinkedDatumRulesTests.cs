using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class LinkedDatumRulesTests
    {
        [Theory]
        [InlineData("grid")]
        [InlineData("level")]
        [InlineData("reference_plane")]
        public void Linked_datums_are_reported_not_cleaned(string kind)
        {
            var result = LinkedDatumRules.Cleanup(kind);
            Assert.Equal("linked_not_controllable", result.Reason);
            Assert.False(result.Cleaned);
            Assert.Contains("visibility overrides", result.Suggestion);
        }
        [Fact]
        public void Host_link_reference_is_refused_by_name()
        {
            Assert.Contains("references[2]", LinkedDatumRules.ReferenceRefusal("references[2]", true, false, false));
            Assert.Null(LinkedDatumRules.ReferenceRefusal("references[2]", true, true, true));
            Assert.NotNull(LinkedDatumRules.ReferenceRefusal("references[2]", true, true, false));
        }

        [Fact]
        public void Reference_identity_includes_instance_element_and_type()
        {
            Assert.True(LinkedDatumRules.SameReference("instance-a", "element-a", "SURFACE", "instance-a", "element-a", "SURFACE"));
            Assert.False(LinkedDatumRules.SameReference("instance-a", "element-a", "SURFACE", "instance-b", "element-a", "SURFACE"));
            Assert.False(LinkedDatumRules.SameReference("instance-a", "element-a", "SURFACE", "instance-a", "element-b", "SURFACE"));
            Assert.False(LinkedDatumRules.SameReference("instance-a", "element-a", "SURFACE", "instance-a", "element-a", "LINEAR"));
        }    }
}
