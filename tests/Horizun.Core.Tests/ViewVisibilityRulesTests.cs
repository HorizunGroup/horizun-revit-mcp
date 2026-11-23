using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class ViewVisibilityRulesTests
    {
        private static JObject Request(JObject overrides) => new JObject
        {
            ["operation"] = "set_overrides", ["view_id"] = 42,
            ["categories"] = new JArray("OST_Walls"), ["overrides"] = overrides
        };

        [Fact]
        public void Projection_colour_is_a_supported_distinct_override()
            => Assert.Null(ViewVisibilityRules.Validate(Request(new JObject { ["projection_line_color"] = "#FF0000" })));

        [Fact]
        public void Unknown_override_cannot_be_reported_verified()
            => Assert.Contains("Unknown override", ViewVisibilityRules.Validate(Request(new JObject { ["projection_colour"] = "#FF0000" })));

        [Fact]
        public void Ambiguous_projection_colour_is_refused()
            => Assert.Contains("not both", ViewVisibilityRules.Validate(Request(new JObject
            {
                ["line_color"] = "#00FF00", ["projection_line_color"] = "#FF0000"
            })));
    }
}
