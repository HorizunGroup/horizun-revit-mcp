using System.Linq;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Server.Tests
{
    [Collection("HorizunSettingsRoot")]
    public class ProEditionTests
    {
        [Fact]
        public void Health_identifies_pro_and_pro_tools_are_registered()
        {
            var health = new JObject();
            ProEdition.StampHealth(health);
            Assert.Equal("pro", (string)health["edition"]);
            Assert.Null(health["upgrade_url"]);
            Assert.Null(health["pro_features"]);

            foreach (string name in new[] {
                "horizun_manage_classification_table", "horizun_room_boundaries", "horizun_template_kit" })
            {
                Assert.Contains(Tools.All, tool => tool.Name == name);
                Assert.NotNull(Tools.Find(name));
            }
        }

        [Fact]
        public void Facade_dispatches_pro_tool_name()
        {
            JObject call = SchemaFacade.UnwrapCall(new JObject
            {
                ["name"] = SchemaFacade.CallToolName,
                ["arguments"] = new JObject
                {
                    ["name"] = "horizun_template_kit",
                    ["arguments"] = new JObject { ["operation"] = "playbook" }
                }
            }, SchemaSurface.Facade);
            Assert.Equal("horizun_template_kit", (string)call["name"]);
            Assert.Equal("playbook", (string)call["arguments"]["operation"]);
        }
    }
}
