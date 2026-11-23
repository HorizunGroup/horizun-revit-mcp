using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Server.Tests
{
    public class SchemaDefaultCompactionTests
    {
        [Fact]
        public void Only_semantically_redundant_defaults_are_removed_from_schema_locations()
        {
            var schema=JObject.Parse(@"{type:'object',additionalProperties:true,minProperties:0,
                properties:{additionalProperties:{type:'boolean',default:true},text:{type:'string',minLength:0,description:'keep every word'},
                array:{type:'array',minItems:0},closed:{type:'object',additionalProperties:false},nonempty:{type:'array',minItems:1}},
                oneOf:[{additionalProperties:true}],default:{additionalProperties:true,minItems:0},enum:[{additionalProperties:true}]}" );
            Tools.DropSchemaDefaults(schema);
            Assert.Null(schema["additionalProperties"]); Assert.Null(schema["minProperties"]);
            Assert.Equal("boolean",(string)schema["properties"]["additionalProperties"]["type"]);
            Assert.True((bool)schema["properties"]["additionalProperties"]["default"]);
            Assert.Equal("keep every word",(string)schema["properties"]["text"]["description"]);
            Assert.Null(schema["properties"]["text"]["minLength"]); Assert.Null(schema["properties"]["array"]["minItems"]);
            Assert.False((bool)schema["properties"]["closed"]["additionalProperties"]);
            Assert.Equal(1,(int)schema["properties"]["nonempty"]["minItems"]);
            Assert.Null(schema["oneOf"][0]["additionalProperties"]);
            Assert.True((bool)schema["default"]["additionalProperties"]);
            Assert.True((bool)schema["enum"][0]["additionalProperties"]);
        }

        [Fact]
        public void Sheet_layout_advertisement_keeps_the_restored_guidance()
        {
            var tool=System.Linq.Enumerable.Single(Tools.All,t=>t.Name=="horizun_sheet_layout");
            string description=(string)Tools.Publish(tool,true)["description"];
            foreach (string phrase in new[] { "warns off sheet or over a datum", "margins [l,r,t,b]", "notes_width", "refused if they do not fit", "require_title_fit" })
                Assert.Contains(phrase,description);
        }

        [Fact]
        public void Sheet_layout_schema_publishes_the_effect_of_omitting_scale_and_of_new_or_worsened_overlaps()
        {
            const string name = "horizun_sheet_layout";
            var tool = System.Linq.Enumerable.Single(Tools.All, t => t.Name == name);
            JObject published = Tools.Publish(tool, advertiseTaskSupport: true);
            var resource = McpResources.Read(new JObject { ["uri"] = "horizun://contract/tools/" + name });
            var exact = JObject.Parse((string)resource["contents"][0]["text"]);
            foreach (JToken schema in new[] { published["inputSchema"], exact["input_schema"] })
            {
                var properties = schema["properties"];
                Assert.Equal("set_view_scale: N of 1:N. Omit to keep current scale (SUPUESTO).", (string)properties["scale"]["description"]);
                Assert.Equal("Move outside viewports. Refuse new or worse overlaps. Default false.", (string)properties["repair_outside"]["description"]);
            }
            Assert.Contains("label_offset_mm", (string)published["description"]);
        }

        [Fact]
        public void Manage_views_schema_publishes_the_title_block_default_and_rehearsal()
        {
            const string name = "horizun_manage_views";
            var tool = System.Linq.Enumerable.Single(Tools.All, t => t.Name == name);
            JObject published = Tools.Publish(tool, advertiseTaskSupport: true);
            var resource = McpResources.Read(new JObject { ["uri"] = "horizun://contract/tools/" + name });
            var exact = JObject.Parse((string)resource["contents"][0]["text"]);
            foreach (JToken schema in new[] { published["inputSchema"], exact["input_schema"] })
            {
                var titleBlock = schema["properties"]["actions"]["items"]["properties"]["title_block_type_id"];
                Assert.Equal("create_sheet: omit for no title block. Rehearsed and re-read.", (string)titleBlock["description"]);
            }
        }
    }
}
