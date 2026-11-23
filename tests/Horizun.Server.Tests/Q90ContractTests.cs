using System.Linq;
using Horizun.Contracts;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Server.Tests
{
    public class Q90ContractTests
    {
        [Fact]
        public void Published_critical_argument_prose_is_complete_and_new_operations_are_named()
        {
            var session = Contract.Find("horizun_document_session");
            var advertised = Tools.CompactSchema(session.Name, session.InputSchema);
            foreach (string field in new[] { "allow_upgrade", "expected_version", "detach", "open_central" })
                Assert.Equal((string)session.InputSchema["properties"][field]["description"], (string)advertised["properties"][field]["description"]);
            Assert.Contains("CANNOT be undone", (string)advertised["properties"]["allow_upgrade"]["description"]);
            Assert.Contains("upgrades it and there is no downgrade", (string)Tools.Publish(Tools.Find(session.Name), true)["description"]);
            var export = Contract.Find("horizun_export"); advertised = Tools.CompactSchema(export.Name, export.InputSchema);
            foreach (string field in new[] { "output_path", "pdf_print", "cobie" })
                Assert.Equal((string)export.InputSchema["properties"][field]["description"], (string)advertised["properties"][field]["description"]);
            Assert.Contains("copy_view_graphics", Contract.Find("horizun_view_visibility").Description);
            Assert.Contains("mesh_json", export.Description);
            Assert.Contains("copy_view_graphics", (string)Tools.Publish(Tools.Find("horizun_view_visibility"), true)["description"]);
            Assert.Contains("mesh_json", (string)Tools.Publish(Tools.Find("horizun_export"), true)["description"]);
        }

        [Fact]
        public void Closed_branch_compression_preserves_constraints_and_prose()
        {
            var full = JObject.Parse("{'type':'object','properties':{'a':{'type':'string'},'another_long_argument':{'type':'integer'},'third_long_argument':{'type':'integer'},'fourth_long_argument':{'type':'integer'}},'oneOf':[{'properties':{'a':{'type':'string','const':'yes','description':'Keep this entire text.'},'another_long_argument':{'type':'integer'},'third_long_argument':{'type':'integer'},'fourth_long_argument':{'type':'integer'}},'required':['a'],'additionalProperties':false}]}");
            var compact = (JObject)full.DeepClone(); Tools.CompactClosedBranches(compact);
            Assert.NotNull(compact["oneOf"][0]["propertyNames"]);
            Assert.Equal("Keep this entire text.", (string)compact["oneOf"][0]["properties"]["a"]["description"]);
            foreach (string json in new[] { "{'a':'yes'}", "{'a':'no'}", "{'a':'yes','extra':1}", "{'a':'yes','another_long_argument':'bad'}", "{'a':'yes','another_long_argument':1}" })
                Assert.Equal(ContractSchemaCheck.Validate(JObject.Parse(json), full).Count == 0, ContractSchemaCheck.Validate(JObject.Parse(json), compact).Count == 0);
        }
        [Fact]
        public void Graphics_operations_are_discoverable_and_read_has_no_element_identity_subject()
        {
            var schema = Contract.Find("horizun_view_visibility").InputSchema;
            Assert.Contains("graphics_report", schema["properties"]["operation"]["enum"].Values<string>());
            Assert.Contains("copy_view_graphics", schema["properties"]["operation"]["enum"].Values<string>());
            Assert.Empty(ContractSchemaCheck.Validate(JObject.Parse("{'operation':'graphics_report','view_ids':[1,2]}"), schema));
            Assert.Empty(ContractSchemaCheck.Validate(JObject.Parse("{'operation':'copy_view_graphics','target_document':'M','view_id':1,'view_ids':[2],'include':['filters','categories']}"), schema));
            Assert.Contains(IdentityCoverage.ReadOperations, pair => pair.Key == "horizun_view_visibility" && pair.Value.Contains("graphics_report"));
        }

    }
}
