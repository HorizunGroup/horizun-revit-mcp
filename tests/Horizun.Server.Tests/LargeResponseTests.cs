using System;
using Horizun.Server;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Server.Tests
{
    public class LargeResponseTests
    {
        [Theory]
        [InlineData("horizun_query_model")]
        [InlineData("horizun_model_scan")]
        [InlineData("horizun_list_elements")]
        [InlineData("horizun_verify_changes")]
        public void Large_payload_is_kept_once_and_Revit_messages_survive(string tool)
        {
            var data = new JObject { ["tool"] = tool, ["rows"] = new JArray() };
            for (int i = 0; i < 5000; i++) ((JArray)data["rows"]).Add(new JObject { ["id"] = i, ["name"] = new string('x', 100) });
            Assert.True(System.Text.Encoding.UTF8.GetByteCount(data.ToString(Formatting.None)) > 512 * 1024);
            string said = "\n\n--- what Revit raised while this ran ---\nwarning";
            var result = McpResult.Structured(data, data.ToString(Formatting.Indented) + said);
            Assert.True(JToken.DeepEquals(data, result["structuredContent"]));
            string text = (string)result["content"][0]["text"];
            Assert.Contains("structuredContent", text);
            Assert.EndsWith(said, text);
            Assert.True(text.Length < 200);
        }

        [Fact]
        public void Payload_of_100_KiB_keeps_the_complete_JSON_in_text()
        {
            var data = new JObject { ["data"] = new string('x', 100 * 1024) };
            string json = data.ToString(Formatting.Indented);
            var result = McpResult.Structured(data, json);
            Assert.Equal(json, (string)result["content"][0]["text"]);
            Assert.True(JToken.DeepEquals(data, result["structuredContent"]));
        }

        [Fact]
        public void A_distinct_explanation_is_not_discarded()
        {
            var data = new JObject { ["data"] = new string('x', 513 * 1024) };
            var result = McpResult.Structured(data, "A distinct explanation");
            Assert.Equal("A distinct explanation", (string)result["content"][0]["text"]);
        }
    }
}
