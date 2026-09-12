using System.Linq;
using Horizun.Contracts;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Server.Tests
{
    public sealed class GeometryProductionCompatibilityTests
    {
        private static JToken Variant(string kind)
        {
            var item = Contract.Find("horizun_create_elements").InputSchema["properties"]["elements"]["items"];
            return ((JArray)item["oneOf"]).Single(v => (string)v["properties"]["kind"]["const"] == kind);
        }

        [Theory]
        [InlineData("fitting", "elements")]
        [InlineData("slab_opening", "shape")]
        [InlineData("beam_system", "direction")]
        [InlineData("wall_foundation", "wall_id")]
        [InlineData("accessory_inline", "pipe_id")]
        [InlineData("mep_system", "member_element_ids")]
        [InlineData("shaft", "base_level_id")]
        [InlineData("room_separator", "view_id")]
        [InlineData("wall", "arc")]
        [InlineData("floor", "structural")]
        [InlineData("pipe", "diameter")]
        public void Production_creation_arguments_remain_published_and_accepted(string kind, string field)
        {
            Assert.NotNull(Variant(kind)["properties"][field]);
            Assert.Null(ToolInputRules.ValidateCreation(new JObject { ["kind"] = kind, [field] = null }, kind));
        }

        [Fact]
        public void Every_published_kind_has_exactly_one_variant()
        {
            var item = Contract.Find("horizun_create_elements").InputSchema["properties"]["elements"]["items"];
            var kinds = ((JArray)item["properties"]["kind"]["enum"]).Values<string>().ToArray();
            var variants = ((JArray)item["oneOf"]).Select(v => (string)v["properties"]["kind"]["const"]).ToArray();
            Assert.Equal(kinds.Length, kinds.Distinct().Count());
            Assert.Equal(kinds.OrderBy(x => x), variants.OrderBy(x => x));
        }

        [Fact]
        public void Beam_boundaries_and_open_separator_chains_keep_their_own_shapes()
        {
            Assert.Equal("number", (string)Variant("beam_system")["properties"]["profile"]["items"]["items"]["type"]);
            Assert.Equal(2, (int)Variant("room_separator")["properties"]["profile"]["items"]["minItems"]);
            Assert.Equal(3, (int)Variant("floor")["properties"]["profile"]["items"]["minItems"]);
        }

        [Fact]
        public void Wall_join_does_not_repurpose_the_existing_curve_endpoint()
        {
            var props = Contract.Find("horizun_transform_elements").InputSchema["properties"]["operations"]["items"]["properties"];
            Assert.Equal("array", (string)props["end"]["type"]);
            Assert.Equal("integer", (string)props["join_end"]["type"]);
            var operations = ((JArray)props["operation"]["enum"]).Values<string>().ToArray();
            foreach (var operation in new[] { "set_curve", "move_tag_head", "set_tag_leader", "wall_join" })
                Assert.Contains(operation, operations);
        }
    }
}
