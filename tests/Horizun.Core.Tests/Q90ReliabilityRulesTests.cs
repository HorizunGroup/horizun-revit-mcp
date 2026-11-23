using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class Q90ReliabilityRulesTests
    {
        [Fact]
        public void Graphics_report_needs_views_and_refuses_write_arguments()
        {
            var r = new JObject { ["operation"] = "graphics_report", ["view_ids"] = new JArray(10, 11) };
            Assert.Null(ViewVisibilityRules.Validate(r));
            r["overrides"] = new JObject(); Assert.NotNull(ViewVisibilityRules.Validate(r));
            r.Remove("overrides"); r["view_ids"] = new JArray("10"); Assert.NotNull(ViewVisibilityRules.Validate(r));
        }

        [Fact]
        public void Graphics_copy_has_safe_defaults_and_no_silent_include_or_element_scope()
        {
            var r = new JObject { ["operation"] = "copy_view_graphics", ["view_id"] = 10, ["view_ids"] = new JArray(11) };
            Assert.Equal(new[] { "filters", "categories" }, ViewVisibilityRules.GraphicsIncludes(r));
            Assert.Null(ViewVisibilityRules.Validate(r));
            r["element_ids"] = new JArray(7); Assert.NotNull(ViewVisibilityRules.Validate(r));
            r["include"] = new JArray("elements"); Assert.Null(ViewVisibilityRules.Validate(r));
            r["include"] = new JArray("filters", "filters"); Assert.NotNull(ViewVisibilityRules.Validate(r));
            r["include"] = new JArray("unknown"); Assert.NotNull(ViewVisibilityRules.Validate(r));
            r.Remove("include"); r.Remove("element_ids"); r["view_ids"] = new JArray(10); Assert.NotNull(ViewVisibilityRules.Validate(r));
        }

        

        [Fact]
        public void Mesh_input_does_not_ignore_format_specific_arguments()
        {
            var r = new JObject { ["format"] = "mesh_json", ["categories"] = new JArray("OST_Walls"), ["include_links"] = true };
            Assert.Null(ExportDeliveryRules.MeshArguments(r));
            r["categories"] = new JArray("Walls"); Assert.NotNull(ExportDeliveryRules.MeshArguments(r));
            r["categories"] = new JArray("OST_Walls", "OST_Walls"); Assert.NotNull(ExportDeliveryRules.MeshArguments(r));
            r.Remove("categories"); r["include_links"] = "true"; Assert.NotNull(ExportDeliveryRules.MeshArguments(r));
            r["include_links"] = true; r["view_ids"] = new JArray(10); Assert.NotNull(ExportDeliveryRules.MeshArguments(r));
        }
    }
}
