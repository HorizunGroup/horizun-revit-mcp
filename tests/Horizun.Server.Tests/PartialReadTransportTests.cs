using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Server.Tests
{
    public sealed class PartialReadTransportTests
    {
        [Fact]
        public void Older_addin_clash_read_is_partial_success_with_coverage_preserved()
        {
            // The actual ClashCommand reply shape: result (not status), coverage,
            // visibility_coverage, paging and geometry counts beside the findings.
            var data = new JObject
            {
                ["clash_count"] = 0, ["result"] = "partial", ["elements_a"] = 5, ["elements_b"] = 2,
                ["bbox_candidates"] = 4, ["solid_tests"] = 3, ["pairs_tested"] = 3,
                ["truncated"] = false, ["offset"] = 0, ["next_offset"] = JValue.CreateNull(),
                ["coverage"] = new JObject { ["geometry_failures"] = new JArray { new JObject { ["element_id"] = 12 } } },
                ["visibility_coverage"] = new JObject { ["complete"] = false, ["note"] = "closed workset" },
                ["clashes"] = new JArray()
            };
            var original = data.DeepClone();
            JObject result = McpResult.FromPluginReply(new JObject { ["success"] = true, ["data"] = data }, "\nRevit warning preserved");
            var structured = (JObject)result["structuredContent"];
            Assert.False((bool)result["isError"]);
            Assert.True((bool)structured["partial"]);
            Assert.Equal("partial", (string)structured["read_coverage"]["state"]);
            Assert.False((bool)structured["read_coverage"]["complete"]);
            Assert.True(JToken.DeepEquals(data["coverage"], structured["read_coverage"]["details"]["coverage"]));
            Assert.True(JToken.DeepEquals(data["visibility_coverage"], structured["read_coverage"]["details"]["visibility_coverage"]));
            Assert.Contains("Revit warning preserved", (string)result["content"][0]["text"]);
            Assert.True(JToken.DeepEquals(original, data));
        }

        [Fact]
        public void Addin_and_server_publish_the_same_partial_declaration()
        {
            CommandResult addon = CommandResult.Ok(new { result = "partial", coverage = new { measured = 4, unresolved = 1 } });
            Assert.True(addon.Success);
            JObject data = JObject.FromObject(addon.Data);
            Assert.True((bool)data["partial"]);
            JObject server = McpResult.Structured(data, "read");
            Assert.True(JToken.DeepEquals(data["read_coverage"], server["structuredContent"]["read_coverage"]));
        }
    }
}
