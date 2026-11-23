// -----------------------------------------------------------------------------
// Horizun Server tests - original Horizun code.
//
// What the field cycle residential project video (2026-10-24, MCP-ESPEC N2, N7, N10, N13) added to the
// contract, pinned so a later trim of tools/list cannot drop it silently.
// -----------------------------------------------------------------------------
using System.Linq;
using Horizun.Contracts;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Server.Tests
{
    public class FieldCycleContractTests
    {
        private static CommandContract Tool(string name) => Contract.All.Single(c => c.Name == name);

        [Fact]
        public void Join_geometry_is_a_rehearsed_write_with_its_operations()
        {
            CommandContract c = Tool("horizun_join_geometry");
            Assert.Equal(ToolEffect.MutatingUnlessDryRun, c.Effect);
            Assert.False(c.Destructive);
            Assert.Equal(new[] { "read", "join", "unjoin", "switch_order", "corners", "fix_corners", "auto_priority", "excavate", "unexcavate" },
                c.InputSchema.SelectToken("properties.operation.enum").Values<string>().ToArray());
            Assert.True((bool)c.InputSchema.SelectToken("properties.dry_run.default"));
            Assert.NotNull(c.InputSchema.SelectToken("properties.confirmation_token"));
            Assert.Equal(new[] { "first_cuts", "priority", "keep_revit" },
                c.InputSchema.SelectToken("properties.cut_order.enum").Values<string>().ToArray());
            Assert.Equal(200, (int)c.InputSchema.SelectToken("properties.pairs.maxItems"));
            Assert.Equal(2, (int)c.InputSchema.SelectToken("properties.pairs.items.minItems"));
            Assert.Contains("shared volume", c.Description);
            Assert.False((bool)c.InputSchema["additionalProperties"]);
        }

        [Fact]
        public void Write_params_takes_a_selector_with_one_set_of_values_and_keeps_the_write_contract()
        {
            CommandContract c = Tool("horizun_write_params_verified");
            Assert.Equal("object", (string)c.InputSchema.SelectToken("properties.selector.type"));
            Assert.NotNull(c.InputSchema.SelectToken("properties.selector.properties.category"));
            Assert.Equal(10, (int)c.InputSchema.SelectToken("properties.set.maxProperties"));
            Assert.False((bool)c.InputSchema.SelectToken("properties.only_if_empty.default"));
            Assert.True((bool)c.InputSchema.SelectToken("properties.dry_run.default"));
            Assert.NotNull(c.InputSchema.SelectToken("properties.confirmation_token"));
            Assert.Contains("selector + set", c.Description);
        }

        [Fact]
        public void Room_finishes_offers_floors_and_baseboards_by_room()
        {
            CommandContract c = Tool("horizun_room_finishes");
            Assert.Equal(ToolEffect.MutatingUnlessDryRun, c.Effect);
            Assert.Equal(new[] { "floors", "baseboards" }, c.InputSchema.SelectToken("properties.operation.enum").Values<string>().ToArray());
            Assert.Equal(200, (int)c.InputSchema.SelectToken("properties.room_ids.maxItems"));
            Assert.True((bool)c.InputSchema.SelectToken("properties.dry_run.default"));
            Assert.Contains("FINISH faces", c.Description);
            Assert.Contains("horizun_room_finishes", ToolsetCatalog.MembersOf("architecture"));
        }

        [Fact]
        public void Wall_finish_matches_cores_read_only_and_creates_layers_with_the_original_said()
        {
            CommandContract c = Tool("horizun_wall_finish");
            Assert.Equal(ToolEffect.MutatingUnlessDryRun, c.Effect);
            Assert.True(c.Destructive);
            Assert.Equal(new[] { "match_core", "create_finish_layers", "convert_to_finish", "paint_faces" }, c.InputSchema.SelectToken("properties.operation.enum").Values<string>().ToArray());
            Assert.Equal(new[] { "keep", "delete_if_unhosted" }, c.InputSchema.SelectToken("properties.original_wall.enum").Values<string>().ToArray());
            Assert.Contains("link_instance_id", c.InputSchema.SelectToken("allOf[0].else.required").Values<string>());
            Assert.Contains("pairs", c.InputSchema.SelectToken("allOf[0].then.required").Values<string>());
            Assert.Contains("no overlap", c.Description);
            Assert.Equal(new[] { "target_document", "finish_type_id", "thickness_mm", "side" },
                c.InputSchema.SelectToken("allOf[1].then.required").Values<string>().ToArray());
            Assert.Contains("horizun_wall_finish", ToolsetCatalog.MembersOf("architecture"));
        }

        [Fact]
        public void Wall_finish_accepts_a_complete_paint_faces_request()
        {
            CommandContract c = Tool("horizun_wall_finish");
            var request = JObject.Parse(@"{'operation':'paint_faces','target_document':'A.rvt','pairs':[
                {'element_id':10,'face_reference':'ref-1','action':'paint','material_id':20},
                {'element_id':11,'face_reference':'ref-2','action':'remove'}]}");
            Assert.Empty(ContractSchemaCheck.Validate(request, c.InputSchema));
            Assert.Null(c.InputSchema.SelectToken("properties.pairs.items.allOf"));
        }

        [Fact]
        public void Audit_support_is_read_only_with_its_four_modes()
        {
            CommandContract c = Tool("horizun_audit_support");
            Assert.Equal(ToolEffect.ReadOnly, c.Effect);
            Assert.Null(c.InputSchema.SelectToken("properties.dry_run"));
            Assert.Equal(new[] { "floating", "slab_supports", "loose_beam_ends", "coverage_gaps", "model_offset" },
                c.InputSchema.SelectToken("properties.mode.enum").Values<string>().ToArray());
            Assert.Contains("unreadable", c.Description);
            Assert.Contains("horizun_audit_support", ToolsetCatalog.MembersOf("audit"));
        }

        [Fact]
        public void Planimetry_ops_offers_its_four_fixes_and_rehearses()
        {
            CommandContract c = Tool("horizun_planimetry_ops");
            Assert.Equal(ToolEffect.MutatingUnlessDryRun, c.Effect);
            Assert.Equal(new[] { "datum_extents", "fit_viewport", "resolve_overlaps", "legend_from_families", "stagger_grid_bubbles", "move_note", "note_from_model", "clone_tag", "arrange_column", "set_leader_end", "reseat_leaders", "required_notes", "replace_text", "sync_sheet_references" },
                c.InputSchema.SelectToken("properties.operation.enum").Values<string>().ToArray());
            Assert.True((bool)c.InputSchema.SelectToken("properties.dry_run.default"));
            Assert.Contains("horizun_planimetry_ops", ToolsetCatalog.MembersOf("planimetry"));
        }

        [Fact]
        public void Join_geometry_rides_the_model_architecture_and_structure_toolsets()
        {
            foreach (string set in new[] { "model", "architecture", "structure" })
                Assert.Contains("horizun_join_geometry", ToolsetCatalog.MembersOf(set));
        }
    }
}
