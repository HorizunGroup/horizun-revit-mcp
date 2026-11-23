using System.Linq;
using Horizun.Contracts;
using Horizun.Server;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Server.Tests
{
    public class ApiHelpSignatureContractTests
    {
        private static CommandContract Help => Contract.Find("horizun_api_help");

        [Theory]
        [InlineData("{'type_name':'ElementId'}")]
        [InlineData("{'operation':'describe','type_name':'ElementId','member_filter':'Val'}")]
        [InlineData("{'operation':'check_signature','type_name':'ElementId','member_filter':'.ctor','argument_types':['System.Int64']}")]
        [InlineData("{'operation':'check_signature','type_name':'Document','member_filter':'Regenerate','argument_types':[]}")]
        public void Contract_preserves_describe_and_accepts_the_approved_signature_requests(string json)
            => Assert.Empty(ContractSchemaCheck.Validate(JObject.Parse(json), Help.InputSchema));

        [Theory]
        [InlineData("{'operation':'invoke','type_name':'ElementId'}")]
        [InlineData("{'operation':'check_signature','type_name':'ElementId','argument_types':[]}")]
        [InlineData("{'operation':'check_signature','type_name':'ElementId','member_filter':'.ctor'}")]
        [InlineData("{'operation':'check_signature','type_name':'ElementId','member_filter':'ctor()','argument_types':[]}")]
        [InlineData("{'operation':'check_signature','type_name':'ElementId','member_filter':'.ctor','argument_types':[null]}")]
        [InlineData("{'operation':'check_signature','type_name':'ElementId','member_filter':'.ctor','argument_types':[32]}")]
        public void Contract_rejects_incomplete_or_executable_inputs(string json)
            => Assert.NotEmpty(ContractSchemaCheck.Validate(JObject.Parse(json), Help.InputSchema));

        [Fact]
        public void Resource_serves_the_changed_schema_and_the_effect_stays_read_only()
        {
            var row = JObject.Parse((string)McpResources.Read(new JObject { ["uri"] = "horizun://contract/tools/horizun_api_help" })["contents"][0]["text"]);
            Assert.True(JToken.DeepEquals(Help.InputSchema, row["input_schema"]));
            Assert.Equal(new[] { "describe", "check_signature" }, Help.InputSchema["properties"]["operation"]["enum"].Select(t => (string)t));
            Assert.Equal("describe", (string)Help.InputSchema["properties"]["operation"]["default"]);
            Assert.Equal(ToolEffect.ReadOnly, Help.Effect);
            Assert.Contains("Never invokes", (string)row["description"]);
            Assert.Equal(32, (int)Help.InputSchema["properties"]["argument_types"]["maxItems"]);
        }
    }
}
