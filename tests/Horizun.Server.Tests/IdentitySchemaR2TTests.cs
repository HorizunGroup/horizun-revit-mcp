using System;
using System.Collections.Generic;
using System.Linq;
using Horizun.Contracts;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Server.Tests
{
    // R2T m-4: the schemas of the four tools that declare expected_category / expected_type themselves must not offer the category as
    // a sufficient check. Short text only: tools/list has to keep its margin (ToolsListLedger).
    public class IdentitySchemaR2TTests
    {
        private static IEnumerable<JProperty> Declarations(JToken node)
        {
            if (node is JObject o)
            {
                if (o["properties"] is JObject props)
                    foreach (JProperty p in props.Properties())
                    {
                        if (p.Name == "expected_category" || p.Name == "expected_type") yield return p;
                        foreach (JProperty inner in Declarations(p.Value)) yield return inner;
                    }
                foreach (JProperty q in o.Properties().Where(x => x.Name != "properties"))
                    foreach (JProperty inner in Declarations(q.Value)) yield return inner;
            }
            else if (node is JArray a)
                foreach (JToken t in a)
                    foreach (JProperty inner in Declarations(t)) yield return inner;
        }

        [Theory]
        [InlineData("horizun_delete_verified")]
        [InlineData("horizun_write_params_verified")]
        [InlineData("horizun_set_keynote")]
        [InlineData("horizun_transform_elements")]
        public void The_schema_says_a_category_or_type_alone_is_not_enough_and_asks_for_the_unique_id(string tool)
        {
            List<JProperty> declared = Declarations(Contract.Find(tool).InputSchema).ToList();
            Assert.True(declared.Count >= 2, tool + " declares expected_category and expected_type");
            foreach (JProperty p in declared)
            {
                string text = (string)p.Value["description"] ?? "";
                Assert.True(text.Contains("unique_id"), tool + "." + p.Name + " does not point to unique_id: \"" + text + "\"");
                Assert.True(text.IndexOf("alone", StringComparison.OrdinalIgnoreCase) >= 0 || text.IndexOf("not enough", StringComparison.OrdinalIgnoreCase) >= 0,
                    tool + "." + p.Name + " does not say it is not enough alone: \"" + text + "\"");
            }
        }

        [Theory]
        [InlineData("horizun_delete_verified")]
        [InlineData("horizun_write_params_verified")]
        [InlineData("horizun_set_keynote")]
        [InlineData("horizun_transform_elements")]
        public void The_contract_resource_says_it_too_for_the_properties_the_tool_declares(string tool)
        {
            JObject row = McpResources.Row(Contract.Find(tool));
            foreach (JProperty p in Declarations(row["input_schema"]))
                Assert.Contains("unique_id", (string)p.Value["description"] ?? "");
            Assert.Contains("unique_id", (string)row["identity_fields"]["properties"]["expected_category"]["description"]);
        }
    }
}
