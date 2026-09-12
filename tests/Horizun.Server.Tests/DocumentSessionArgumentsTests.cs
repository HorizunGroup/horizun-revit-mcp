using System.Linq;
using Horizun.Contracts;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Server.Tests
{
    public sealed class DocumentSessionArgumentsTests
    {
        [Theory]
        [InlineData("save", "audit")]
        [InlineData("save_as", "save_on_close")]
        [InlineData("close", "overwrite")]
        [InlineData("inspect", "detach")]
        public void Inapplicable_arguments_are_not_ignored(string operation, string field)
        {
            var request = new JObject { ["operation"] = operation, [field] = false };
            Assert.Contains(field, ToolInputRules.ValidateSession(request, operation));
        }
        [Theory]
        [InlineData("save")]
        [InlineData("save_as")]
        [InlineData("close")]
        [InlineData("inspect")]
        public void Dry_run_is_an_explicit_supported_operation_argument(string operation)
        {
            Assert.Null(ToolInputRules.ValidateSession(new JObject { ["operation"] = operation, ["dry_run"] = true }, operation));
        }
        [Fact]
        public void Open_rejects_an_unsupported_dry_run_before_execution()
        {
            Assert.Contains("does not support", ToolInputRules.ValidateSession(new JObject { ["dry_run"] = true }, "open"));
        }
        [Fact]
        public void Published_contract_has_five_disjoint_operation_variants()
        {
            var variants = (JArray)Contract.Find("horizun_document_session").InputSchema["oneOf"];
            Assert.Equal(5, variants.Count);
            foreach (var variant in variants)
                Assert.False((bool)variant["additionalProperties"]);
        }
        [Fact]
        public void Profiles_have_three_array_dimensions_and_categories_reject_ignored_fields()
        {
            var item=Contract.Find("horizun_create_elements").InputSchema["properties"]["elements"]["items"];
            var floor=((JArray)item["oneOf"]).Single(v=>(string)v["properties"]["kind"]["const"]=="floor");
            var profile=floor["properties"]["profile"];
            Assert.Equal("array",(string)profile["type"]);
            Assert.Equal("array",(string)profile["items"]["type"]);
            Assert.Equal("array",(string)profile["items"]["items"]["type"]);
            Assert.Equal("number",(string)profile["items"]["items"]["items"]["type"]);
            Assert.Contains("height",ToolInputRules.ValidateCreation(new JObject { ["kind"]="ceiling",["height"]=9 },"ceiling"));
            Assert.Contains("coordinate_mode",ToolInputRules.ValidateCreation(new JObject { ["kind"]="family_instance" },"family_instance"));
        }
    }
}
