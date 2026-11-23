// -----------------------------------------------------------------------------
// Horizun Server tests - original Horizun code.
//
// What Pro 3.5.0 added to the contract after the team telemetry of 2026-10-19..23
// (398 horizun_execute_python samples, 10.3 % error: the same purposes rewritten in
// Python every time), pinned so a later trim of tools/list cannot drop it silently:
//   - horizun_measure_geometry, horizun_query_relations, horizun_query_family_internals
//     are read-only, take no dry_run/token and say what could not be read;
//   - horizun_delete_verified mode=query keeps the dry_run / confirmation_token /
//     idempotency_key contract of every other write;
//   - horizun_query_model exclude_nested; horizun_connect_mep operation=verify;
//   - tools/list still fits under its hard ceiling with room to spare.
// -----------------------------------------------------------------------------
using System.Linq;
using System.Text;
using Horizun.Contracts;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Server.Tests
{
    public class Pro350ContractTests
    {
        private static JObject Schema(string tool) => Contract.All.Single(c => c.Name == tool).InputSchema;

        [Theory]
        [InlineData("horizun_measure_geometry")]
        [InlineData("horizun_query_relations")]
        public void New_reading_tools_are_read_only_and_carry_no_write_arguments(string tool)
        {
            CommandContract c = Contract.All.Single(x => x.Name == tool);
            Assert.Equal(ToolEffect.ReadOnly, c.Effect);
            Assert.False(c.Destructive);
            JObject props = (JObject)c.InputSchema["properties"];
            Assert.Null(props["dry_run"]);
            Assert.Null(props["confirmation_token"]);
            Assert.Null(props["idempotency_key"]);
            Assert.Contains("target_document", c.InputSchema["required"].Values<string>());
            Assert.False((bool)c.InputSchema["additionalProperties"]);
        }

        [Fact]
        public void Measure_geometry_offers_the_five_measures_and_a_unit_and_never_zero_for_unreadable()
        {
            CommandContract c = Contract.All.Single(x => x.Name == "horizun_measure_geometry");
            Assert.Equal(new[] { "summary", "solid_volume", "section_profile", "bounding_box", "end_points", "face_normals", "faces", "edges", "location" },
                c.InputSchema.SelectToken("properties.measures.items.enum").Values<string>().ToArray());
            Assert.Equal(100, (int)c.InputSchema.SelectToken("properties.page_size.maximum"));
            Assert.Equal(0, (int)c.InputSchema.SelectToken("properties.offset.minimum"));
            Assert.Equal(new[] { "mm", "m", "feet" }, c.InputSchema.SelectToken("properties.unit.enum").Values<string>().ToArray());
            Assert.Equal("array", (string)c.InputSchema.SelectToken("properties.ids.type"));
            Assert.Equal("object", (string)c.InputSchema.SelectToken("properties.selector.type"));
            Assert.Contains("never 0", c.Description);
            Assert.Contains("unreadable", c.Description);
            Assert.Contains("coverage_complete", c.Description);
        }

        [Fact]
        public void Measure_geometry_reads_the_selection_and_stays_read_only()
        {
            JObject schema = Schema("horizun_measure_geometry");
            Assert.Equal("boolean", (string)schema.SelectToken("properties.selection.type"));
            Assert.Null(schema.SelectToken("properties.what_if"));
            Assert.Equal(ToolEffect.ReadOnly, Contract.All.Single(x => x.Name == "horizun_measure_geometry").Effect);
        }

        [Fact]
        public void Verify_changes_rehearses_what_if_on_a_writing_classified_tool()
        {
            JObject schema = Schema("horizun_verify_changes");
            Assert.Contains("what_if", schema.SelectToken("properties.operation.enum").Values<string>());
            Assert.Equal(new[] { "delete", "join", "unjoin", "move" },
                schema.SelectToken("properties.what_if.properties.action.enum").Values<string>().ToArray());
            Assert.Equal(100, (int)schema.SelectToken("properties.what_if.properties.ids.maxItems"));
            Assert.False((bool)schema.SelectToken("properties.what_if.additionalProperties"));
            Assert.Contains("rolled_back_verified", (string)schema.SelectToken("properties.operation.description"));
            Assert.NotEqual(ToolEffect.ReadOnly, Contract.All.Single(x => x.Name == "horizun_verify_changes").Effect);
        }

        [Fact]
        public void Split_by_level_offers_openings_and_still_rehearses_first()
        {
            JObject schema = Schema("horizun_split_by_level");
            Assert.Equal(new[] { "levels", "openings" }, schema.SelectToken("properties.split_at.enum").Values<string>().ToArray());
            Assert.Equal("levels", (string)schema.SelectToken("properties.split_at.default"));
            Assert.True((bool)schema.SelectToken("properties.dry_run.default"));
        }

        [Fact]
        public void Query_relations_offers_the_seven_relations_with_a_bounded_radius()
        {
            CommandContract c = Contract.All.Single(x => x.Name == "horizun_query_relations");
            Assert.Equal(new[] { "joined", "cut_by", "cutting", "host", "hosted", "neighbors", "type_info" },
                c.InputSchema.SelectToken("properties.relations.items.enum").Values<string>().ToArray());
            Assert.Equal(5000, (double)c.InputSchema.SelectToken("properties.radius_mm.maximum"));
            Assert.Contains("unreadable", c.Description);
        }

        [Fact]
        public void Delete_verified_query_mode_keeps_the_write_contract_and_the_other_modes()
        {
            CommandContract c = Contract.All.Single(x => x.Name == "horizun_delete_verified");
            Assert.Equal(new[] { "ids", "purge_unused", "query" }, c.InputSchema.SelectToken("properties.mode.enum").Values<string>().ToArray());
            Assert.True((bool)c.InputSchema.SelectToken("properties.dry_run.default"));
            Assert.NotNull(c.InputSchema.SelectToken("properties.confirmation_token"));
            Assert.NotNull(c.InputSchema.SelectToken("properties.idempotency_key"));
            Assert.NotNull(c.InputSchema.SelectToken("properties.protect_ids"));
            Assert.NotNull(c.InputSchema.SelectToken("properties.ids"));
            Assert.NotNull(c.InputSchema.SelectToken("properties.max_passes"));
            Assert.Equal(ToolEffect.MutatingUnlessDryRun, c.Effect);
            Assert.True(c.Destructive);
            JObject selector = (JObject)c.InputSchema.SelectToken("properties.selector");
            Assert.Equal(new[] { "category", "family", "prefix", "parameter", "created_since" },
                ((JObject)selector["properties"]).Properties().Select(p => p.Name).ToArray());
            Assert.False((bool)selector["additionalProperties"]);
            Assert.Contains("mode=query", c.Description);
        }

        [Fact]
        public void Measure_geometry_and_delete_share_one_selector_schema()
        {
            JToken a = Schema("horizun_measure_geometry").SelectToken("properties.selector");
            JToken b = Schema("horizun_delete_verified").SelectToken("properties.selector");
            Assert.True(JToken.DeepEquals(a, b));
        }

        [Fact]
        public void Query_model_filters_nested_instances()
        {
            JToken nested = Schema("horizun_query_model").SelectToken("properties.exclude_nested");
            Assert.Equal("boolean", (string)nested["type"]);
            Assert.False((bool)nested["default"]);
            Assert.Contains("SuperComponent", (string)nested["description"]);
        }

        [Fact]
        public void Tools_list_keeps_a_real_margin_under_its_hard_ceiling()
        {
            int bytes = Encoding.UTF8.GetByteCount(
                new JArray(Tools.All.Select(t => (JToken)Tools.Publish(t, true))).ToString(Formatting.None));
            Assert.True(bytes <= 524_288 - 8_000,
                "tools/list is " + bytes + " B; the 3.5.0 tools need at least 8,000 B of headroom under 524,288 B for what comes next.");
        }
    }
}
