using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class QueryGroupKeysTests
    {
        [Theory]
        [InlineData("parameter:Comments", "Comments")]
        [InlineData("parameter: ALL_MODEL_INSTANCE_COMMENTS ", "ALL_MODEL_INSTANCE_COMMENTS")]
        [InlineData("parameter:", null)]
        [InlineData("level", null)]
        [InlineData("Parameter:Comments", null)]
        public void Only_a_named_parameter_key_is_a_parameter_key(string key, string expected)
            => Assert.Equal(expected, QueryGroupKeys.ParameterOf(key));

        [Fact]
        public void Absent_unreadable_and_empty_values_are_named_groups()
        {
            Assert.Equal("(unreadable)", QueryGroupKeys.ValueOf(null));
            Assert.Equal("(unreadable)", QueryGroupKeys.ValueOf(new JObject { ["read_error"] = "x" }));
            Assert.Equal("(absent)", QueryGroupKeys.ValueOf(new JObject { ["exists"] = false }));
            Assert.Equal("(empty)", QueryGroupKeys.ValueOf(new JObject { ["exists"] = true, ["raw"] = "", ["display"] = null }));
        }

        [Fact]
        public void The_displayed_value_is_preferred_over_the_raw_one()
        {
            Assert.Equal("WA-01", QueryGroupKeys.ValueOf(new JObject { ["exists"] = true, ["raw"] = "WA-01", ["display"] = null }));
            Assert.Equal("150 mm", QueryGroupKeys.ValueOf(new JObject { ["exists"] = true, ["raw"] = 0.492, ["display"] = "150 mm" }));
        }
    }
}
