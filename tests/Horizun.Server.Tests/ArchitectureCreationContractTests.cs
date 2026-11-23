using System.Linq;
using Horizun.Contracts;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Server.Tests
{
    public class ArchitectureCreationContractTests
    {
        private static JToken Variant(string kind) => ((JArray)Contract.Find("horizun_create_elements")
            .InputSchema["properties"]["elements"]["items"]["oneOf"])
            .Single(v => v["properties"]["kind"].Value<string>("const") == kind);

        [Theory]
        [InlineData("door")]
        [InlineData("window")]
        public void Hosted_joinery_requires_a_point_and_host_but_can_assume_type_level_and_coordinate_mode(string kind)
        {
            var variant = Variant(kind);
            Assert.Equal(new[] { "kind", "point", "host_id" }, ((JArray)variant["required"]).Values<string>().ToArray());
            Assert.NotNull(variant["properties"]["type_name"]);
            Assert.NotNull(variant["properties"]["parameters"]);
            Assert.NotNull(variant["properties"]["source_reference"]);
            Assert.Equal(3, variant["properties"]["point"].Value<int>("minItems"));
            Assert.Null(ToolInputRules.ValidateCreation(JObject.Parse("{'kind':'" + kind + "','point':[0,0,0],'host_id':42}"), kind));
            Assert.NotNull(ToolInputRules.ValidateCreation(new JObject { ["kind"] = kind, ["diameter"] = 20 }, kind));
        }

        [Fact]
        public void Wall_chain_is_a_distinct_recipe_with_open_default_and_no_required_height()
        {
            var variant = Variant("wall_chain");
            Assert.Equal(new[] { "kind", "points", "level_id" }, ((JArray)variant["required"]).Values<string>().ToArray());
            Assert.False(variant["properties"]["closed"].Value<bool>("default"));
            Assert.NotNull(variant["properties"]["join_rule"]);
        }

        [Fact]
        public void Wall_height_is_optional_in_published_schema_and_architectural_type_names_are_accepted()
        {
            Assert.DoesNotContain("height", ((JArray)Variant("wall")["required"]).Values<string>());
            foreach (string kind in new[] { "wall", "floor", "ceiling", "roof" })
            {
                Assert.NotNull(Variant(kind)["properties"]["type_name"]);
                Assert.Null(ToolInputRules.ValidateCreation(new JObject { ["kind"] = kind, ["type_name"] = "name" }, kind));
            }
        }

        [Fact]
        public void String_choice_compaction_preserves_values_and_non_string_types_without_cutting_prose()
        {
            var schema = JObject.Parse("{'type':'object','properties':{'choice':{'type':'string','enum':['a','b'],'description':'Keep every word.'},'count':{'type':'integer','enum':[1,2]},'mixed':{'type':['string','integer'],'enum':['a',1]}}}");
            Tools.DropConstImpliedTypes(schema);
            Assert.Null(schema["properties"]["choice"]["type"]);
            Assert.Equal("Keep every word.", schema["properties"]["choice"].Value<string>("description"));
            Assert.Equal(new[] { "a", "b" }, ((JArray)schema["properties"]["choice"]["enum"]).Values<string>());
            Assert.Equal("integer", schema["properties"]["count"].Value<string>("type"));
            Assert.IsType<JArray>(schema["properties"]["mixed"]["type"]);
        }

        [Fact]
        public void Creation_contract_documents_all_recipes_and_coordinate_safety()
        {
            string text = Contract.Find("horizun_create_elements").Description;
            foreach (string required in new[] { "door", "window", "wall_chain", "tabular_source",
                "XYZ coordinates use the internal origin; point families require coordinate_mode", "Inapplicable fields are refused" })
                Assert.Contains(required, text);
            Assert.Equal(Tools.CompactDescription(text), Tools.Publish(Tools.Find("horizun_create_elements"), true).Value<string>("description"));
        }

        [Fact]
        public void Explicit_point_room_placement_has_the_same_contract_as_omitted_placement()
        {
            var row = JObject.Parse("{'kind':'room','placement':'point','point':[0,0],'level_id':1}");
            Assert.Empty(ContractSchemaCheck.Validate(row, (JObject)Variant("room")));
            Assert.Null(ToolInputRules.ValidateCreation(row, "room"));
            row["phase_id"] = 2;
            Assert.NotEmpty(ContractSchemaCheck.Validate(row, (JObject)Variant("room")));
        }

        [Theory]
        [InlineData("check_variation", -90, true)]
        [InlineData("check_variation", 361, false)]
        [InlineData("add_members", 90, true)]
        [InlineData("add_members", -361, false)]
        [InlineData("list", 0, false)]
        public void Group_rotation_declarations_are_bounded_and_only_valid_for_variation_and_addition(string operation, int degrees, bool accepted)
        {
            var request = new JObject { ["operation"] = operation, ["group_ids"] = operation == "add_members" ? new JArray(10) : new JArray(10, 12),
                ["relative_rotations_degrees"] = new JArray(degrees) };
            if (operation == "add_members") request["element_ids"] = new JArray(23);
            var schema = Contract.Find("horizun_manage_groups").InputSchema;

            Assert.Equal(accepted, ContractSchemaCheck.Validate(request, schema).Count == 0);
        }
    }
}
