// -----------------------------------------------------------------------------
// Horizun Server tests - original Horizun code.
//
// What Pro 3.4.0 added to the contract after the field cycles (2026-10-21),
// pinned so a later trim of tools/list cannot drop it silently. Each fact here
// answers one friction in the field log:
//   - horizun_annotate tag takes an element of a Revit link (link_instance_id +
//     linked_element_id) instead of element_id;
//   - horizun_create_elements kind=grid takes extent_3d (default maximize);
//   - horizun_manage_views operation=extend_grids_3d with grid_ids;
//   - horizun_manage_views create_3d takes start/end/up and display_style, and
//     create_elevation honours depth; horizun_capture_view display_style takes words.
// -----------------------------------------------------------------------------
using System.Linq;
using Horizun.Contracts;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Server.Tests
{
    public class Pro340ContractTests
    {
        private static JObject Schema(string tool) => Contract.All.Single(c => c.Name == tool).InputSchema;

        [Fact]
        public void Annotate_tag_names_either_a_host_element_or_a_link_pair()
        {
            JObject item = (JObject)Schema("horizun_annotate").SelectToken("properties.actions.items");
            Assert.Equal("integer", (string)item.SelectToken("properties.link_instance_id.type"));
            Assert.Equal("integer", (string)item.SelectToken("properties.linked_element_id.type"));
            JToken tagRule = item["allOf"].Single(r => (string)r.SelectToken("if.properties.operation.const") == "tag");
            Assert.DoesNotContain("element_id", tagRule.SelectToken("then.required").Values<string>());
            JArray alternatives = (JArray)tagRule.SelectToken("then.anyOf");
            Assert.Equal(2, alternatives.Count);
            Assert.Contains(alternatives, a => a["required"].Values<string>().SequenceEqual(new[] { "element_id" }));
            Assert.Contains(alternatives, a => a["required"].Values<string>().SequenceEqual(new[] { "link_instance_id", "linked_element_id" }));
        }

        [Fact]
        public void Grid_creation_offers_the_vertical_extent_and_defaults_to_maximize()
        {
            var item = Contract.Find("horizun_create_elements").InputSchema["properties"]["elements"]["items"];
            JToken grid = ((JArray)item["oneOf"]).Single(v => (string)v["properties"]["kind"]["const"] == "grid");
            Assert.Equal("maximize", (string)grid.SelectToken("properties.extent_3d.default"));
            Assert.Equal(new[] { "maximize", "none" }, grid.SelectToken("properties.extent_3d.enum").Values<string>().ToArray());
        }

        [Fact]
        public void Existing_grids_can_be_extended_through_manage_views()
        {
            JObject s = Schema("horizun_manage_views");
            Assert.Contains("extend_grids_3d", s.SelectToken("properties.actions.items.properties.operation.enum").Values<string>());
            Assert.Equal("array", (string)s.SelectToken("properties.actions.items.properties.grid_ids.type"));
            Assert.Equal("integer", (string)s.SelectToken("properties.actions.items.properties.grid_ids.items.type"));
        }

        [Fact]
        public void Views_3d_take_a_camera_and_a_visual_style()
        {
            JObject props = (JObject)Schema("horizun_manage_views").SelectToken("properties.actions.items.properties");
            Assert.Equal("array", (string)props["start"]["type"]);
            Assert.Equal("array", (string)props["end"]["type"]);
            Assert.Contains("create_3d", (string)props["up"]["description"]);
            Assert.Equal("string", (string)props["display_style"]["type"]);
            Assert.Contains("shaded", (string)props["display_style"]["description"]);
            Assert.Contains("Far Clip", (string)props["depth"]["description"]);
        }

        [Fact]
        public void Capture_view_style_is_a_word_not_a_closed_enum_of_revit_names()
        {
            JToken style = Schema("horizun_capture_view").SelectToken("properties.display_style");
            Assert.Equal("string", (string)style["type"]);
            Assert.Null(style["enum"]);
            Assert.Contains("shaded", (string)style["description"]);
        }

        [Fact]
        public void Surface_and_cut_overrides_name_their_fill_pattern()
        {
            JObject overrides = (JObject)Schema("horizun_manage_views").SelectToken("properties.actions.items.properties.overrides.properties");
            Assert.Equal("string", (string)overrides["surface_pattern"]["type"]);
            Assert.Equal("string", (string)overrides["cut_pattern"]["type"]);
            Assert.Contains("solid", (string)overrides["surface_pattern"]["description"]);
        }

        [Fact]
        public void Detail_2d_regions_can_simplify_tiny_edges_and_a_dry_run_says_it_applied_nothing()
        {
            CommandContract detail = Contract.All.Single(c => c.Name == "horizun_detail_2d");
            JToken tolerance = detail.InputSchema.SelectToken("properties.actions.items.properties.simplify_tolerance_mm");
            Assert.Equal("number", (string)tolerance["type"]);
            Assert.Equal(50, (double)tolerance["maximum"]);
            Assert.Contains("applied=false", detail.Description);
        }

        [Fact]
        public void Query_model_offset_is_a_declared_start_position_not_an_ignored_argument()
        {
            JToken offset = Schema("horizun_query_model").SelectToken("properties.offset");
            Assert.Equal("integer", (string)offset["type"]);
            Assert.Equal(0, (int)offset["minimum"]);
            Assert.Contains("cursor", (string)offset["description"]);
        }

        [Fact]
        public void Detail_lines_can_grant_a_bounded_join_tolerance()
        {
            JToken join = Schema("horizun_detail_2d").SelectToken("properties.actions.items.properties.join_tolerance_mm");
            Assert.Equal("number", (string)join["type"]);
            Assert.Equal(5, (double)join["maximum"]);
            Assert.Contains("ONLY for a line at risk", (string)join["description"]);
        }

        [Fact]
        public void Query_model_can_list_every_parameter_of_a_type()
        {
            string description = (string)Schema("horizun_query_model").SelectToken("properties.return_parameters.description");
            Assert.Contains("'*'", description);
            Assert.Contains("parameter_format=full", description);
        }
    }
}
