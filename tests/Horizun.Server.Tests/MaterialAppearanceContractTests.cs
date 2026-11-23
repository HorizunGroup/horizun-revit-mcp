using Horizun.Contracts;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Server.Tests
{
    public class MaterialAppearanceContractTests
    {
        [Fact]
        public void Schema_exposes_operation_and_appearance_arguments()
        {
            var root = Contract.Find("horizun_manage_materials").InputSchema;
            var props = (JObject)root["properties"]["actions"]["items"]["properties"];
            Assert.Contains("set_appearance", ((JArray)props["operation"]["enum"]).Values<string>());
            foreach (string name in new[] { "generic_diffuse", "match_shading_color", "remove_diffuse_texture", "transparency" })
                Assert.NotNull(props[name]);
            Assert.NotNull(root["properties"]["idempotency_key"]);
        }
    }
}
