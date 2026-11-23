// -----------------------------------------------------------------------------
// Horizun Server tests - original Horizun code.
//
// 2026-10 field-driven additions: horizun_selection_context and horizun_api_help are
// read-only tools in the read toolset; execute_python declares rehearsal; connect_mep
// declares open_ends; health answers several Revit instances with the list.
// -----------------------------------------------------------------------------
using System;
using System.Linq;
using Horizun.Contracts;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Server.Tests
{
    public class SelectionContextContractTests
    {
        private static CommandContract C(string name) => Contract.All.Single(x => x.Name == name);

        [Theory]
        [InlineData("horizun_selection_context")]
        [InlineData("horizun_api_help")]
        public void New_tools_are_read_only_closed_and_in_the_read_toolset(string tool)
        {
            CommandContract c = C(tool);
            Assert.Equal(ToolEffect.ReadOnly, c.Effect);
            Assert.False(c.Destructive);
            Assert.False(c.OpenWorld);
            JObject props = (JObject)c.InputSchema["properties"];
            Assert.Null(props["dry_run"]);
            Assert.Null(props["confirmation_token"]);
            Assert.Null(props["idempotency_key"]);
            Assert.False((bool)c.InputSchema["additionalProperties"]);
            Assert.Contains(tool, ToolsetCatalog.MembersOf("read"));
            Assert.DoesNotContain(tool, ToolsetCatalog.MembersOf("unsafe_code"));
        }

        [Fact]
        public void Selection_context_declares_its_five_operations_and_the_shared_selector()
        {
            JObject s = C("horizun_selection_context").InputSchema;
            Assert.Equal(new[] { "context", "snapshot", "diff", "validate_ids", "view_contents" }, s.SelectToken("properties.operation.enum").Values<string>().ToArray());
            Assert.Equal(new[] { "target_document" }, s["required"].Values<string>().ToArray());
            Assert.Equal(JToken.Parse(C("horizun_measure_geometry").InputSchema.SelectToken("properties.selector").ToString()).ToString(),
                         s.SelectToken("properties.selector").ToString());
            Assert.Equal(new[] { "geometry", "relations", "neighbors", "parameters" }, s.SelectToken("properties.include.items.enum").Values<string>().ToArray());
        }

        [Fact]
        public void Selection_context_pins_the_view_contents_arguments()
        {
            JObject props = (JObject)C("horizun_selection_context").InputSchema["properties"];
            Assert.Equal("integer", (string)props.SelectToken("view_id.type"));
            Assert.Equal("array", (string)props.SelectToken("categories.type"));
            Assert.Equal("string", (string)props.SelectToken("categories.items.type"));
            Assert.Equal("integer", (string)props.SelectToken("max_rows.type"));
            Assert.Equal("integer", (string)props.SelectToken("room_id.type"));
            Assert.Equal("object", (string)props.SelectToken("region.type"));
            Assert.Equal("boolean", (string)props.SelectToken("include_links.type"));
            // the arguments the new operation reuses keep their shape
            Assert.Equal("number", (string)props.SelectToken("tolerance_mm.type"));
            Assert.Equal(new[] { "mm", "m", "feet" }, props.SelectToken("unit.enum").Values<string>().ToArray());
            // the text the schema keeps for the exact-schema resource: checks is validate_ids only, the type may be 'Family: Type'
            Assert.Contains("validate_ids", (string)props.SelectToken("checks.description"));
            Assert.Contains("Family: Type", (string)props.SelectToken("checks.items.properties.expected_type.description"));
        }

        [Fact]
        public void Health_with_several_instances_answers_with_the_list_not_an_error()
        {
            Func<JArray> before = HealthAnswer.ListInstances;
            try
            {
                HealthAnswer.ListInstances = () => new JArray(
                    new JObject { ["pid"] = 101, ["year"] = "2025", ["active_document"] = "A" },
                    new JObject { ["pid"] = 202, ["year"] = "2026", ["active_document"] = null });
                JToken answer = HealthAnswer.Apply(McpResult.Error(
                    "Error: 2 Revit instances are running with the bridge loaded (Revit 2025 pid 101, Revit 2026 pid 202) and nothing says which one you mean.",
                    null, null, null));
                Assert.False((bool)answer["isError"]);
                JObject h = (JObject)answer["structuredContent"];
                Assert.Equal("varias_instancias", (string)h["estado"]);
                Assert.True((bool)h["target_required"]);
                Assert.Equal(new[] { 101, 202 }, ((JArray)h["instances"]).Select(i => (int)i["pid"]).ToArray());
                Assert.Equal(Horizun.Contracts.Contract.Hash, (string)h["server"]["contract_hash"]);
                Assert.Equal("this health request", (string)h["last_error"]["scope"]);
                Assert.Equal("unknown", (string)h["modal_dialog_observation"]);
            }
            finally { HealthAnswer.ListInstances = before; }
        }
    }
}
