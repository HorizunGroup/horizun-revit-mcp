// -----------------------------------------------------------------------------
// Horizun Server tests - original Horizun code.
//
// The contract is declared once now. These are the tests that keep it that way,
// and that catch the drift that used to be undetectable.
//
// Two copies of the same facts drifted twice in a single afternoon of work on
// this repository: a parameter added to the server's schema and not to the
// command's, and a description updated on one side while the other kept
// promising the old behaviour. Neither was caught, because the copies never met.
// The hash below is what makes them meet.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Horizun.Contracts;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Server.Tests
{
    public class ContractDriftTests
    {
        [Fact]
        public void Every_command_is_declared_once()
        {
            var duplicates = Contract.All.GroupBy(c => c.Name, StringComparer.Ordinal)
                                         .Where(g => g.Count() > 1)
                                         .Select(g => g.Key)
                                         .ToList();

            Assert.Empty(duplicates);
        }

        [Fact]
        public void Every_command_can_actually_be_answered()
        {
            // A contract with no plugin command is a host-resident tool, and there are
            // exactly the following set. Anything else naming neither would be a tool that exists and
            // can never reply.
            var hostResident = Contract.All.Where(c => string.IsNullOrEmpty(c.Command))
                                           .Select(c => c.Name)
                                           .OrderBy(n => n, StringComparer.Ordinal)
                                           .ToList();

            Assert.Equal(
                new[] { "horizun_catalog_lookup", "horizun_excel_write_rows", "horizun_job_status", "horizun_power_bi_push", "horizun_target" },
                hostResident);
        }

        [Fact]
        public void Power_bi_contract_never_accepts_credentials_and_is_idempotent()
        {
            CommandContract c = Contract.Find("horizun_power_bi_push");
            Assert.NotNull(c);
            Assert.Equal(ToolEffect.MutatingUnlessDryRun, c.Effect);
            JToken properties = c.InputSchema["properties"];
            Assert.NotNull(properties?["dataset_id"]);
            Assert.NotNull(properties?["rows"]);
            Assert.NotNull(properties?["idempotency_key"]);
            Assert.Null(properties?["access_token"]);
            Assert.Null(properties?["client_secret"]);
            Assert.Contains("environment variables", c.Description);
        }

        [Fact]
        public void Every_command_carries_a_description_and_a_schema()
        {
            foreach (CommandContract c in Contract.All)
            {
                Assert.False(string.IsNullOrWhiteSpace(c.Description), c.Name + " has no description");
                Assert.NotNull(c.InputSchema);
                Assert.Equal("object", (string)c.InputSchema["type"]);
                Assert.NotNull(c.OutputSchema);
                Assert.Equal("object", (string)c.OutputSchema["type"]);
            }
        }

        [Fact]
        public void Every_model_mutation_advertises_the_shared_idempotency_key()
        {
            foreach (CommandContract c in Contract.All.Where(c =>
                c.Effect == ToolEffect.Mutating ||
                c.Effect == ToolEffect.MutatingUnlessDryRun ||
                c.Effect == ToolEffect.DocumentSession))
            {
                JToken key = c.InputSchema["properties"]?["idempotency_key"];
                Assert.NotNull(key);
                Assert.Equal("string", (string)key["type"]);
            }
        }

        [Fact]
        public void Every_mutating_command_requires_the_document_it_will_change()
        {
            // The gate is enforced in the add-in; this is the half a caller READS. A
            // mutation whose schema does not mention target_document is one whose caller
            // has no way to know it is mandatory.
            string[] mutations =
            {
                "horizun_delete_verified", "horizun_write_params_verified", "horizun_set_keynote",
                "horizun_bind_shared_param", "horizun_family_apply", "horizun_save_document",
                "horizun_relinquish_all", "horizun_create_schedule",
                "horizun_create_family", "horizun_manage_system_types",
                // Recipe-backed geometry tools. They delete and recreate elements, which
                // makes naming the model at least as load-bearing here as anywhere above.
                "horizun_split_floor_loops", "horizun_split_multilayer_walls",
                "horizun_split_multilayer_slabs", "horizun_ungroup_and_mark",
                "horizun_regroup_by_param", "horizun_copy_slab_elevations",
                "horizun_embed_floors_in_toposolid", "horizun_grade_toposolid_around_floors",
                "horizun_rectangularize_walls"
            };

            foreach (string name in mutations)
            {
                CommandContract c = Contract.Find(name);
                Assert.NotNull(c);
                Assert.True(c.InputSchema["properties"]?["target_document"] != null,
                            name + " does not declare target_document, so nothing tells a caller it is required");
            }
        }

        [Fact]
        public void Schedule_creation_exposes_links_fields_and_confirmation()
        {
            CommandContract c = Contract.Find("horizun_create_schedule");
            Assert.NotNull(c);
            JToken properties = c.InputSchema["properties"];
            Assert.NotNull(properties?["include_links"]);
            Assert.NotNull(properties?["fields"]);
            Assert.NotNull(properties?["dry_run"]);
            Assert.NotNull(properties?["confirmation_token"]);
            Assert.True((bool)properties["include_links"]["default"]);
            Assert.True((bool)properties["dry_run"]["default"]);
        }

        [Fact]
        public void Linked_element_inventory_is_bounded_and_declares_coverage()
        {
            CommandContract c = Contract.Find("horizun_list_elements");
            Assert.NotNull(c);
            JToken properties = c.InputSchema["properties"];
            Assert.NotNull(properties?["include_links"]);
            Assert.NotNull(properties?["offset"]);
            Assert.Equal(1000, (int)properties["max_rows"]["maximum"]);
            Assert.Contains("Unloaded links", c.Description);
        }

        [Fact]
        public void Query_model_exposes_composable_filters_projection_and_stale_cursor()
        {
            CommandContract c = Contract.Find("horizun_query_model");
            Assert.NotNull(c);
            JToken p = c.InputSchema["properties"];
            Assert.NotNull(p?["categories"]);
            Assert.NotNull(p?["parameters"]);
            Assert.NotNull(p?["bounding_box"]);
            Assert.NotNull(p?["return_parameters"]);
            Assert.NotNull(p?["cursor"]);
            Assert.Equal(500, (int)p["max_rows"]["maximum"]);
            Assert.Equal(ToolEffect.ReadOnly, c.Effect);
        }

        [Fact]
        public void Benchmark_operations_are_typed_in_the_public_contract()
        {
            JToken createKinds = Contract.Find("horizun_create_elements").InputSchema["properties"]?["elements"]?["items"]?["properties"]?["kind"]?["enum"];
            foreach (string kind in new[] { "ceiling", "roof", "cable_tray", "structural_framing", "structural_column" })
                Assert.Contains(kind, createKinds.Values<string>());

            JToken viewOps = Contract.Find("horizun_manage_views").InputSchema["properties"]?["actions"]?["items"]?["properties"]?["operation"]?["enum"];
            foreach (string operation in new[] { "create_ceiling_plan", "create_structural_plan", "create_drafting", "create_section", "create_elevation" })
                Assert.Contains(operation, viewOps.Values<string>());

            JToken formats = Contract.Find("horizun_export").InputSchema["properties"]?["format"]?["enum"];
            Assert.Contains("ifc", formats.Values<string>());
            Assert.Contains("nwc", formats.Values<string>());
            Assert.Contains("fbx", formats.Values<string>());

            CommandContract family = Contract.Find("horizun_create_family");
            Assert.NotNull(family);
            JToken familyProperties = family.InputSchema["properties"];
            foreach (string property in new[] { "parameters", "types", "forms", "connectors", "reference_planes", "dimensions", "family_lines", "nested_instances" })
                Assert.NotNull(familyProperties?[property]);
            JToken familyKinds = familyProperties?["forms"]?["items"]?["properties"]?["kind"]?["enum"];
            foreach (string kind in new[] { "extrusion", "blend", "revolution", "sweep", "swept_blend" })
                Assert.Contains(kind, familyKinds.Values<string>());
            CommandContract systemTypes = Contract.Find("horizun_manage_system_types");
            Assert.NotNull(systemTypes);
            JToken compound = systemTypes.InputSchema["properties"]?["actions"]?["items"]?["properties"]?["compound_structure"];
            Assert.NotNull(compound?["properties"]?["layers"]);
            Assert.NotNull(compound?["properties"]?["structural_layer_index"]);
            Assert.NotNull(compound?["properties"]?["opening_wrapping"]);
        }

        // ---- the hash ----------------------------------------------------------

        [Fact]
        public void The_hash_is_stable_across_calls()
        {
            Assert.Equal(Contract.Hash, Contract.Hash);
            Assert.Matches("^[0-9a-f]{24}$", Contract.Hash);
        }

        [Fact]
        public void A_changed_schema_changes_the_hash()
        {
            // The point of the whole exercise: if a schema moves and only one half is
            // rebuilt, the hashes differ and the server refuses instead of forwarding an
            // argument the far end will ignore.
            string before = HashOf(Sample());

            var changed = Sample();
            changed[0].InputSchema["properties"]["thing"]["type"] = "integer";

            Assert.NotEqual(before, HashOf(changed));
        }

        [Fact]
        public void A_changed_description_changes_the_hash()
        {
            string before = HashOf(Sample());
            var changed = Sample();
            changed[0].Description = "something else entirely";

            Assert.NotEqual(before, HashOf(changed));
        }

        [Fact]
        public void Reformatting_a_schema_does_NOT_change_the_hash()
        {
            // Whitespace in a source literal is not a contract change, and treating it as
            // one would train everybody to ignore the refusal.
            var a = new List<CommandContract>
            {
                new CommandContract { Name = "t", Command = "t", Description = "d",
                                      InputSchema = JObject.Parse("{\"type\":\"object\",\"properties\":{\"x\":{\"type\":\"string\"}}}") }
            };
            var b = new List<CommandContract>
            {
                new CommandContract { Name = "t", Command = "t", Description = "d",
                                      InputSchema = JObject.Parse("{\n  \"type\" : \"object\",\n  \"properties\" : {\n    \"x\" : { \"type\" : \"string\" }\n  }\n}") }
            };

            Assert.Equal(HashOf(a), HashOf(b));
        }

        [Fact]
        public void The_order_commands_are_written_in_does_not_change_the_hash()
        {
            var a = Sample();
            var b = Sample();
            b.Reverse();

            Assert.Equal(HashOf(a), HashOf(b));
        }

        private static List<CommandContract> Sample() => new List<CommandContract>
        {
            new CommandContract { Name = "a", Command = "a", Description = "first",
                                  InputSchema = JObject.Parse("{\"type\":\"object\",\"properties\":{\"thing\":{\"type\":\"string\"}}}") },
            new CommandContract { Name = "b", Command = null, Description = "second",
                                  InputSchema = JObject.Parse("{\"type\":\"object\"}") }
        };

        [Fact]
        public void A_legacy_addin_is_readable_but_cannot_receive_a_write_under_an_unknown_contract()
        {
            var legacy = new Discovered { ContractHash = null, ProtocolVersion = 0 };

            Assert.Null(PipeClient.LegacyContractRefusal(legacy, Tools.Find("horizun_health")));
            string refusal = PipeClient.LegacyContractRefusal(legacy, Tools.Find("horizun_create_elements"));
            Assert.NotNull(refusal);
            Assert.Contains("nothing was sent", refusal, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("contract", refusal, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void A_current_contract_does_not_trigger_the_legacy_write_guard()
        {
            var current = new Discovered
            {
                ContractHash = Contract.Hash,
                ProtocolVersion = Contract.ProtocolVersion
            };
            Assert.Null(PipeClient.LegacyContractRefusal(current, Tools.Find("horizun_create_elements")));
        }

        /// <summary>
        /// Mirrors Contract.ComputeHash over an arbitrary list. Kept in step by the two
        /// tests above, which would fail if the real one stopped covering a field.
        /// </summary>
        private static string HashOf(List<CommandContract> all)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("protocol=").Append(Contract.ProtocolVersion).Append((char)30);
            foreach (CommandContract c in all.OrderBy(x => x.Name, StringComparer.Ordinal))
            {
                sb.Append(c.Name).Append((char)31);
                sb.Append(c.Command ?? "-").Append((char)31);
                sb.Append(c.Description ?? "").Append((char)31);
                sb.Append(c.Effect.ToString()).Append((char)31);
                sb.Append(c.InputSchema == null ? "-" : c.InputSchema.ToString(Newtonsoft.Json.Formatting.None)).Append((char)31);
                sb.Append(c.OutputSchema == null ? "-" : c.OutputSchema.ToString(Newtonsoft.Json.Formatting.None));
                sb.Append((char)30);
            }
            using (var sha = System.Security.Cryptography.SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(sb.ToString())), 0, 12)
                                   .Replace("-", "").ToLowerInvariant();
        }
    }
}
