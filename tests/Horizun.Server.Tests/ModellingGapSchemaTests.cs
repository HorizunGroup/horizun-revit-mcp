// Six small modelling gaps close inside existing tools. The schema must declare each new kind, operation and
// field - or the bridge refuses the argument - and must keep refusing what does not belong to it.
using System.Linq;
using Horizun.Contracts;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Server.Tests
{
    public class ModellingGapSchemaTests
    {
        private static JObject Variant(string kind) =>
            Contract.Find("horizun_create_elements").InputSchema["properties"]["elements"]["items"]["oneOf"]
                .Cast<JObject>().Single(v => (string)v["properties"]["kind"]["const"] == kind);

        [Fact]
        public void Reference_plane_is_a_kind_with_its_own_lean_variant()
        {
            JToken kinds = Contract.Find("horizun_create_elements").InputSchema["properties"]["elements"]["items"]["properties"]["kind"]["enum"];
            Assert.Contains("reference_plane", kinds.Select(k => (string)k));
            JObject v = Variant("reference_plane");
            Assert.Equal(new[] { "end", "kind", "name", "start", "view_id" }, ((JObject)v["properties"]).Properties().Select(p => p.Name).OrderBy(n => n).ToArray());
            Assert.Equal(new[] { "kind", "name", "start", "end", "view_id" }.OrderBy(x => x), v["required"].Select(t => (string)t).OrderBy(x => x));
            Assert.Null(v["properties"]["source_reference"]);   // the lean kinds do not carry the 1.2 KB trace
        }

        [Fact]
        public void Reference_plane_rows_pass_the_input_rules_and_refuse_foreign_fields()
        {
            Assert.Null(ToolInputRules.ValidateCreation(JObject.Parse("{\"kind\":\"reference_plane\",\"start\":[0,0,0],\"end\":[1000,0,0],\"name\":\"P\",\"view_id\":5}"), "reference_plane"));
            Assert.Contains("level_id", ToolInputRules.ValidateCreation(JObject.Parse("{\"kind\":\"reference_plane\",\"start\":[0,0,0],\"end\":[1,0,0],\"name\":\"P\",\"view_id\":5,\"level_id\":3}"), "reference_plane"));
        }

        [Fact]
        public void A_roof_takes_several_contours_and_per_edge_overhang_and_offset()
        {
            JObject roof = Variant("roof");
            Assert.True((int)roof["properties"]["profile"]["maxItems"] > 1);   // perimeter plus holes
            JToken edge = roof["properties"]["edge_slopes"]["items"]["properties"];
            Assert.Equal("number", (string)edge["overhang"]["type"]);
            Assert.Equal("number", (string)edge["offset"]["type"]);
            Assert.Contains("ALL contours", (string)roof["properties"]["edge_slopes"]["description"]);
        }

        [Fact]
        public void A_wall_profile_still_takes_exactly_one_contour()
        {
            Assert.Equal(1, (int)Variant("wall_profile")["properties"]["profile"]["maxItems"]);
        }

        [Fact]
        public void A_floor_declares_the_slope_arrow_and_both_slope_units()
        {
            JObject props = (JObject)Variant("floor")["properties"];
            foreach (string field in new[] { "slope_degrees", "slope_ratio", "slope_arrow_start", "slope_arrow_end" })
                Assert.NotNull(props[field]);
            Assert.Null(props["expected_arrow_elevations"]);   // a ceiling's check, not a floor's
            Assert.Null(ToolInputRules.ValidateCreation(JObject.Parse(
                "{\"kind\":\"floor\",\"profile\":[[[0,0,0],[1,0,0],[1,1,0]]],\"level_id\":1,\"slope_ratio\":0.1,\"slope_arrow_start\":[0,0,0],\"slope_arrow_end\":[1,0,0]}"), "floor"));
        }

        [Fact]
        public void Transform_elements_lists_the_column_operations_and_the_end_field()
        {
            JObject item = (JObject)Contract.Find("horizun_transform_elements").InputSchema["properties"]["operations"]["items"]["properties"];
            var ops = item["operation"]["enum"].Select(t => (string)t).ToList();
            Assert.Contains("column_attach", ops);
            Assert.Contains("column_detach", ops);
            Assert.Equal(new[] { "base", "top" }, item["column_end"]["enum"].Select(t => (string)t).ToArray());
            Assert.NotNull(item["attachment_target_id"]);
        }

        [Fact]
        public void Join_geometry_lists_excavate_and_requires_pairs_and_a_document_for_it()
        {
            JObject schema = Contract.Find("horizun_join_geometry").InputSchema;
            var ops = schema["properties"]["operation"]["enum"].Select(t => (string)t).ToList();
            Assert.Contains("excavate", ops);
            Assert.Contains("unexcavate", ops);
            string all = schema["allOf"].ToString();
            Assert.Contains("excavate", all);
            Assert.Contains("excavate: [toposolid, excavator]", (string)schema["properties"]["pairs"]["description"]);
        }

    }
}
