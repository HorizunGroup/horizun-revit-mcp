// -----------------------------------------------------------------------------
// Horizun Server tests - original Horizun code.
//
// HORIZUN_SCHEMA_MODE=compact (3.1). Same tools as full, short to read: a description of at
// most 200 characters and only the common arguments. What these tests hold:
//   1. the mode exists, full stays the default and Contract.Hash does not move;
//   2. compact advertises EXACTLY the tools full advertises, in every posture and toolset;
//   3. every description is <= 200 characters and every schema keeps every `required`;
//   4. the call is still validated against the FULL contract (wire tests, below);
//   5. reads default to the server's short response mode ONLY in compact mode;
//   6. the cost is pinned: a MEASURED budget per configuration, with margin;
//   7. the server instructions are short in compact and unchanged in full and facade.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Horizun.Revit.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;
using Xunit.Abstractions;

namespace Horizun.Server.Tests
{
    [Collection("HorizunSettingsRoot")]
    public class SchemaCompactTests
    {
        private readonly ITestOutputHelper _out;
        public SchemaCompactTests(ITestOutputHelper output) { _out = output; }

        /// <summary>Contract.Hash when compact was written. Compact is server-only: it must never move it.</summary>
        private const string ContractHashAtCompact = "e44f15ba2c5bbc64f7555ae8";

        private static readonly string[] LocalTools =
        {
            "get_document_info", "horizun_health", "horizun_target", "horizun_job_status", "horizun_submit_job",
            "horizun_navigate", "horizun_list_elements", "horizun_query_model", "horizun_model_scan",
            "horizun_model_diff", "horizun_quantities", "horizun_capture_view", "horizun_create_elements",
            "horizun_transform_elements", "horizun_write_params_verified", "horizun_delete_verified",
            "horizun_manage_views", "horizun_undo"
        };

        private static int Chars(JToken t) => t.ToString(Formatting.None).Length;

        // ---- 1. the mode ----------------------------------------------------------------

        [Fact]
        public void Compact_is_a_third_mode_and_full_stays_the_default()
        {
            Assert.Equal(SchemaSurface.Compact, SchemaMode.Resolve(" Compact ", out string source, out string problem));
            Assert.Equal("environment", source);
            Assert.Null(problem);
            Assert.Equal("compact", SchemaMode.Name(SchemaSurface.Compact));
            Assert.Equal(SchemaSurface.Full, SchemaMode.Default);
            Assert.Equal(SchemaSurface.Full, SchemaMode.Resolve(null, out _, out _));
            Assert.Equal(SchemaSurface.Full, SchemaMode.Resolve("compat", out _, out string typo));
            Assert.Contains("compact", typo);
        }

        [Fact]
        public void Compact_does_not_move_the_contract_hash_and_has_its_own()
        {
            Assert.Equal(ContractHashAtCompact, Horizun.Contracts.Contract.Hash);
            Assert.Matches("^[0-9a-f]{24}$", SchemaCompact.Hash);
            Assert.NotEqual(SchemaFacade.Hash, SchemaCompact.Hash);
            Assert.NotEqual(Horizun.Contracts.Contract.Hash, SchemaCompact.Hash);
            Assert.Equal(SchemaCompact.Hash, SchemaCompact.Hash);
        }

        // ---- 2. the same tools as full ---------------------------------------------------

        [Fact]
        public void Compact_advertises_the_same_tools_as_full_in_every_posture_and_toolset()
        {
            string[] profiles = { "read_only", "safe_write", "full_write", "unsafe_code" };
            var selections = new List<string> { null };
            selections.AddRange(ToolPacks.KnownPacks.OrderBy(k => k, StringComparer.Ordinal));
            foreach (string p in profiles)
                foreach (string sel in selections)
                    SchemaFacadeTests.WithDataRoot("{\"permission_profile\":\"" + p + "\"}", sel, () =>
                    {
                        // The real full tools/list, which carries horizun_activate_toolset when it is offered.
                        string[] full = SchemaFacade.List(SchemaSurface.Full, true).Select(t => (string)t["name"]).ToArray();
                        string[] compact = SchemaFacade.List(SchemaSurface.Compact, true).Select(t => (string)t["name"]).ToArray();
                        Assert.True(full.SequenceEqual(compact), p + "/" + (sel ?? "(none)") + ": compact differs from full");
                    });
        }

        [Fact]
        public void Everything_but_description_and_schema_is_published_as_in_full()
        {
            foreach (ToolDef t in Tools.All)
            {
                JObject full = Tools.Publish(t, true);
                JObject compact = SchemaCompact.Publish(t, true);
                foreach (JProperty p in full.Properties().Where(p => p.Name != "description" && p.Name != "inputSchema"))
                    Assert.True(JToken.DeepEquals(p.Value, compact[p.Name]), t.Name + "." + p.Name + " differs from full");
                Assert.Equal(full.Properties().Count(), compact.Properties().Count());
            }
        }

        [Fact]
        public void Compact_is_deterministic()
        {
            Assert.Equal(SchemaFacade.List(SchemaSurface.Compact, true).ToString(Formatting.None),
                         SchemaFacade.List(SchemaSurface.Compact, true).ToString(Formatting.None));
        }

        // ---- 3. descriptions and required --------------------------------------------------

        [Fact]
        public void Every_compact_description_is_at_most_200_characters_and_points_at_the_full_schema()
        {
            foreach (ToolDef t in Tools.All)
            {
                string d = (string)SchemaCompact.Publish(t, false)["description"];
                Assert.True(d.Length <= SchemaCompact.DescriptionMax, t.Name + ": description is " + d.Length + " characters");
                Assert.EndsWith("Full schema: horizun://contract/tools/" + t.Name, d);
                Assert.True(d.Length > ("Full schema: horizun://contract/tools/" + t.Name).Length + 20, t.Name + ": no real description");
            }
        }

        [Fact]
        public void The_local_and_core_tools_have_a_hand_written_description_not_a_cut_of_the_contract_one()
        {
            foreach (string name in LocalTools)
            {
                Assert.True(SchemaCompactSpecs.HasSpec(name), name + " has no hand-written compact spec");
                string d = (string)SchemaCompact.Publish(Tools.Find(name), false)["description"];
                string contract = Tools.Find(name).Description;
                Assert.False(contract.StartsWith(d.Substring(0, Math.Min(60, d.Length)), StringComparison.Ordinal), name + ": looks cut, not written");
            }
        }

        [Fact]
        public void Every_compact_schema_keeps_every_required_of_the_contract_and_the_property_it_names()
        {
            foreach (ToolDef t in Tools.All)
            {
                JObject c = SchemaCompact.Publish(t, false)["inputSchema"] as JObject;
                Assert.NotNull(c);
                var want = (t.InputSchema["required"] as JArray ?? new JArray()).Select(x => (string)x).OrderBy(x => x, StringComparer.Ordinal).ToArray();
                var have = (c["required"] as JArray ?? new JArray()).Select(x => (string)x).OrderBy(x => x, StringComparer.Ordinal).ToArray();
                Assert.True(want.SequenceEqual(have), t.Name + ": required differs");
                foreach (string r in want) Assert.NotNull(c["properties"]?[r]);
                AssertNestedRequiredKept(t.Name, t.InputSchema, c);
            }
        }

        // every array-of-objects argument that survives keeps the item's own required
        private static void AssertNestedRequiredKept(string tool, JObject full, JObject compact)
        {
            foreach (JProperty p in ((JObject)compact["properties"]).Properties())
            {
                JToken ci = p.Value["items"], fi = full["properties"][p.Name]?["items"];
                if (!(ci is JObject cio) || !(fi is JObject fio) || fio["required"] == null) continue;
                var want = fio["required"].Select(x => (string)x).OrderBy(x => x, StringComparer.Ordinal).ToArray();
                var have = (cio["required"] as JArray ?? new JArray()).Select(x => (string)x).OrderBy(x => x, StringComparer.Ordinal).ToArray();
                Assert.True(want.SequenceEqual(have), tool + "." + p.Name + "[]: required differs");
                foreach (string r in want) if (fio["properties"]?[r] != null) Assert.NotNull(cio["properties"]?[r]);
            }
        }

        [Fact]
        public void Compact_schemas_are_a_subset_of_the_contract_and_never_stricter()
        {
            foreach (ToolDef t in Tools.All)
            {
                JObject c = (JObject)SchemaCompact.Publish(t, false)["inputSchema"];
                foreach (JProperty p in ((JObject)c["properties"]).Properties())
                    Assert.True(t.InputSchema["properties"][p.Name] != null, t.Name + "." + p.Name + " is not in the contract");
                // the full contract refuses unknown arguments; the compact copy must not promise that
                // to a client that validates it, because the server accepts every contract argument
                Assert.True(c["additionalProperties"] == null || (c["additionalProperties"].Type == JTokenType.Boolean && (bool)c["additionalProperties"]), t.Name);
                Assert.Null(c["oneOf"]); Assert.Null(c["anyOf"]); Assert.Null(c["allOf"]);
                Assert.Equal("object", (string)c["type"]);
            }
        }

        [Fact]
        public void The_write_flow_arguments_are_always_kept_when_the_tool_has_them()
        {
            foreach (ToolDef t in Tools.All)
            {
                JObject c = (JObject)SchemaCompact.Publish(t, false)["inputSchema"];
                foreach (string flow in new[] { "target_document", "target_document_title", "dry_run", "confirmation_token", "idempotency_key" })
                    if (t.InputSchema["properties"][flow] != null)
                        Assert.True(c["properties"][flow] != null, t.Name + " lost " + flow);
            }
        }

        [Fact]
        public void The_common_arguments_of_horizun_query_model_are_the_curated_ones()
        {
            JObject c = (JObject)SchemaCompact.Publish(Tools.Find("horizun_query_model"), false)["inputSchema"];
            var have = ((JObject)c["properties"]).Properties().Select(p => p.Name).ToHashSet();
            foreach (string must in new[] { "categories", "element_ids", "level", "name", "type", "return_parameters", "group_by",
                                            "sum_parameters", "response_mode", "max_rows", "include_types", "target_document" })
                Assert.Contains(must, have);
            Assert.DoesNotContain("cooperative", have);
            Assert.DoesNotContain("bounding_box", have);
            Assert.Equal(new[] { "full", "compact", "summary" }, c["properties"]["response_mode"]["enum"].Select(x => (string)x).ToArray());
        }

        [Fact]
        public void Create_elements_keeps_the_whole_kind_enum_and_states_the_measured_traps()
        {
            JObject c = (JObject)SchemaCompact.Publish(Tools.Find("horizun_create_elements"), false)["inputSchema"];
            JObject full = Horizun.Contracts.Contract.Find("horizun_create_elements").InputSchema;
            JObject items = (JObject)c["properties"]["elements"]["items"];
            Assert.Equal(full["properties"]["elements"]["items"]["properties"]["kind"]["enum"].ToString(Formatting.None),
                         items["properties"]["kind"]["enum"].ToString(Formatting.None));
            foreach (string must in new[] { "kind", "level_id", "type_id", "start", "end", "height", "profile", "point",
                                            "elevation", "name", "host_id", "coordinate_mode" })
                Assert.NotNull(items["properties"][must]);
            Assert.Equal(new[] { "kind" }, items["required"].Select(x => (string)x).ToArray());
            string start = (string)items["properties"]["start"]["description"];
            Assert.Contains("ABSOLUTE", start);
            Assert.Contains("level", start);
            string profile = (string)items["properties"]["profile"]["description"];
            Assert.Contains("ABSOLUTE", profile);
            Assert.Contains("loop", profile);
            // profile is a list of loops, each a list of [x,y,z]
            Assert.Equal("array", (string)items["properties"]["profile"]["items"]["type"]);
            Assert.Equal("array", (string)items["properties"]["profile"]["items"]["items"]["type"]);
            Assert.Equal("number", (string)items["properties"]["profile"]["items"]["items"]["items"]["type"]);
            Assert.Contains("mm", (string)c["properties"]["units"]["description"]);
            // far fewer fields than the contract's ~60
            Assert.True(((JObject)items["properties"]).Count <= 20);
        }

        [Fact]
        public void Every_argument_a_table_row_names_exists_in_the_contract()
        {
            // a renamed argument must fail HERE, not silently vanish from the compact schema
            foreach (string name in LocalTools)
            {
                CompactSpec spec = SchemaCompactSpecs.Find(name);
                JObject full = Horizun.Contracts.Contract.Find(name).InputSchema;
                foreach (CompactArg a in spec.Args)
                    Assert.True(full["properties"]?[a.Name] != null, name + "." + a.Name + " is not in the contract");
                foreach (var kv in spec.ItemArgs)
                {
                    Assert.Contains(kv.Key, spec.Args.Select(a => a.Name));
                    JToken itemProps = full["properties"][kv.Key]["items"]["properties"];
                    foreach (CompactArg f in kv.Value)
                        Assert.True(itemProps[f.Name] != null, name + "." + kv.Key + "[]." + f.Name + " is not in the contract");
                }
            }
        }

        [Fact]
        public void The_operations_and_sections_the_table_names_are_real_values_of_the_contract()
        {
            JToken ops = Horizun.Contracts.Contract.Find("horizun_manage_views").InputSchema["properties"]["actions"]["items"]["properties"]["operation"]["enum"];
            foreach (string op in SchemaCompactSpecs.ViewOperations) Assert.Contains(op, ops.Select(x => (string)x));
            JToken sections = Horizun.Contracts.Contract.Find("horizun_model_scan").InputSchema["properties"]["sections"]["items"]["enum"];
            foreach (string s in SchemaCompactSpecs.ScanSections) Assert.Contains(s, sections.Select(x => (string)x));
            // the 46-value enum is replaced by names in the description, in manage_views only
            JObject c = (JObject)SchemaCompact.Publish(Tools.Find("horizun_manage_views"), false)["inputSchema"];
            Assert.Null(c["properties"]["actions"]["items"]["properties"]["operation"]["enum"]);
            Assert.Contains("create_floor_plan", (string)c["properties"]["actions"]["items"]["properties"]["operation"]["description"]);
        }

        [Fact]
        public void Transform_keeps_its_whole_operation_enum()
        {
            JObject c = (JObject)SchemaCompact.Publish(Tools.Find("horizun_transform_elements"), false)["inputSchema"];
            JToken want = Horizun.Contracts.Contract.Find("horizun_transform_elements").InputSchema["properties"]["operations"]["items"]["properties"]["operation"]["enum"];
            Assert.Equal(want.ToString(Formatting.None), c["properties"]["operations"]["items"]["properties"]["operation"]["enum"].ToString(Formatting.None));
        }

        [Fact]
        public void Query_model_advertises_the_default_it_will_really_get_in_this_mode()
        {
            JObject c = (JObject)SchemaCompact.Publish(Tools.Find("horizun_query_model"), false)["inputSchema"];
            Assert.Equal("compact", (string)c["properties"]["response_mode"]["default"]);
            // full mode still says full
            Assert.Equal("full", (string)Tools.CompactSchema("horizun_query_model", Tools.Find("horizun_query_model").InputSchema)["properties"]["response_mode"]["default"]);
        }

        // ---- 5. compact reads --------------------------------------------------------------

        [Fact]
        public void In_compact_mode_a_query_without_response_mode_gets_compact()
        {
            var args = new JObject { ["categories"] = new JArray("Walls") };
            JObject got = SchemaCompact.ApplyReadDefaults("horizun_query_model", args, SchemaSurface.Compact);
            Assert.Equal("compact", (string)got["response_mode"]);
            Assert.Equal("Walls", (string)got["categories"][0]);
            Assert.Null(args["response_mode"]);                     // the caller's object is not touched
        }

        [Fact]
        public void An_explicit_response_mode_is_respected()
        {
            foreach (string mode in new[] { "full", "summary", "compact" })
            {
                var args = new JObject { ["response_mode"] = mode };
                Assert.Equal(mode, (string)SchemaCompact.ApplyReadDefaults("horizun_query_model", args, SchemaSurface.Compact)["response_mode"]);
            }
            var bad = new JObject { ["response_mode"] = "nonsense" };
            Assert.Equal("nonsense", (string)SchemaCompact.ApplyReadDefaults("horizun_query_model", bad, SchemaSurface.Compact)["response_mode"]);
        }

        [Fact]
        public void Outside_compact_mode_nothing_is_defaulted()
        {
            var args = new JObject();
            Assert.Same(args, SchemaCompact.ApplyReadDefaults("horizun_query_model", args, SchemaSurface.Full));
            Assert.Same(args, SchemaCompact.ApplyReadDefaults("horizun_query_model", args, SchemaSurface.Facade));
            Assert.Null(args["response_mode"]);
        }

        [Fact]
        public void Only_reads_with_a_short_mode_are_defaulted_and_no_write_ever_is()
        {
            foreach (ToolDef t in Tools.All)
            {
                var args = new JObject();
                JObject got = SchemaCompact.ApplyReadDefaults(t.Name, args, SchemaSurface.Compact);
                if (t.Name == "horizun_query_model") { Assert.Equal("compact", (string)got["response_mode"]); continue; }
                Assert.True(JToken.DeepEquals(new JObject(), got), t.Name + " was changed by the read defaults");
            }
            Assert.Equal(new[] { "horizun_query_model" }, SchemaCompact.ReadDefaults.Keys.OrderBy(k => k).ToArray());
            foreach (var kv in SchemaCompact.ReadDefaults)
            {
                var c = Horizun.Contracts.Contract.Find(kv.Key);
                Assert.Equal(Horizun.Contracts.ToolEffect.ReadOnly, c.Effect);
                Assert.Contains(kv.Value, c.InputSchema["properties"]["response_mode"]["enum"].Select(x => (string)x));
            }
        }

        // ---- identity ---------------------------------------------------------------------

        [Fact]
        public void Health_and_build_identity_report_schema_mode_compact()
        {
            SchemaFacadeTests.WithDataRoot("{\"permission_profile\":\"safe_write\"}", "local", () =>
            {
                JObject h = SchemaFacade.HealthBlock();
                Assert.Equal("compact", (string)h["mode"]);
                Assert.Equal("full", (string)h["default"]);
                Assert.Equal("environment", (string)h["source"]);
                Assert.Equal(Horizun.Contracts.Contract.Hash, (string)h["contract_hash"]);
                Assert.Equal(SchemaCompact.Hash, (string)h["compact_hash"]);
                Assert.Equal(SchemaFacade.List(SchemaSurface.Compact, false).Count, (int)h["tools_list"]["tools"]);
                Assert.Contains("compact", (string)h["means"]);
                JObject id = JObject.Parse((string)McpResources.Read(JObject.Parse("{\"uri\":\"horizun://build/identity\"}"))["contents"][0]["text"]);
                Assert.Equal("compact", (string)id["schema_mode"]);
                Assert.Equal(SchemaCompact.Hash, (string)id["compact_hash"]);
                Assert.Equal(Horizun.Contracts.Contract.Hash, (string)id["contract_hash"]);
            }, "compact");
        }

        // ---- 7. instructions ---------------------------------------------------------------

        [Fact]
        public void Compact_instructions_are_short_and_say_the_three_things()
        {
            string h = ServerInstructions.HeadFor(SchemaSurface.Compact);
            Assert.True(h.Length <= 600, "compact instructions are " + h.Length + " characters");
            Assert.Contains("horizun_health", h);
            Assert.Contains("dry_run=false", h);
            Assert.Contains("confirmation_token", h);
            Assert.Contains("idempotency_key", h);
            Assert.Contains("horizun://contract/tools/{tool}", h);
            Assert.StartsWith(h + "\n\n", ServerInstructions.TextFor(SchemaSurface.Compact));
        }

        [Fact]
        public void Full_and_facade_instructions_are_unchanged()
        {
            Assert.Equal(ServerInstructions.Head, ServerInstructions.HeadFor(SchemaSurface.Full));
            string facade = ServerInstructions.HeadFor(SchemaSurface.Facade);
            Assert.Contains("FACADE", facade);
            Assert.Contains("7. tools/list schemas are abridged", ServerInstructions.Head);
            Assert.True(Encoding.UTF8.GetByteCount(ServerInstructions.Head) <= 2048);
        }

        // ---- 6. the budget -----------------------------------------------------------------
        // MEASURED on the build that introduced compact (characters of the tools/list array,
        // Formatting.None; task support advertised). Each budget is the measurement + 10 %:
        // growing past it has to be a decision. The "before" column is full mode, same posture.

        private const long LocalSafeWriteMeasured = 20884;      // full: 99 213
        private const long LocalFullWriteMeasured = 21699;      // full: 101 825
        private const long UnrestrictedSafeWriteMeasured = 157290;   // full: 421 094
        // The owner's orientative target was ~16 000 characters (~4 000 tokens). It is NOT met, and the test says
        // so instead of hiding it: 18 tools carry ~270 characters each that are not description or schema
        // (name, title, outputSchema, annotations, execution) and ~3 100 of hand-written description,
        // which leaves ~12 000 for schemas that include the 33-value kind enum. The hard ceiling is half of that
        // plus the margin: local+compact must stay under a quarter of full.
        private const long OwnerTargetCharacters = 16000;

        [Fact]
        public void Compact_tools_list_stays_within_its_measured_budgets()
        {
            long localSafe = 0, localFull = 0, openSafe = 0;
            long fullLocalSafe = 0, fullLocalFull = 0, fullOpenSafe = 0;
            SchemaFacadeTests.WithDataRoot("{\"permission_profile\":\"safe_write\"}", "local", () =>
            {
                localSafe = Chars(SchemaFacade.List(SchemaSurface.Compact, true));
                fullLocalSafe = Chars(Tools.List(true));
            });
            SchemaFacadeTests.WithDataRoot("{\"permission_profile\":\"full_write\"}", "local", () =>
            {
                localFull = Chars(SchemaFacade.List(SchemaSurface.Compact, true));
                fullLocalFull = Chars(Tools.List(true));
            });
            SchemaFacadeTests.WithDataRoot("{\"permission_profile\":\"safe_write\"}", null, () =>
            {
                openSafe = Chars(SchemaFacade.List(SchemaSurface.Compact, true));
                fullOpenSafe = Chars(Tools.List(true));
            });
            _out.WriteLine("MEASURED compact local safe_write   : " + localSafe + " chars (full " + fullLocalSafe + ")");
            _out.WriteLine("MEASURED compact local full_write   : " + localFull + " chars (full " + fullLocalFull + ")");
            _out.WriteLine("owner target " + OwnerTargetCharacters + " characters; local+compact safe_write is " + (localSafe * 100 / OwnerTargetCharacters) + " % of it");
            _out.WriteLine("MEASURED compact unrestricted safe  : " + openSafe + " chars (full " + fullOpenSafe + ")");

            Assert.True(localSafe <= LocalSafeWriteMeasured + LocalSafeWriteMeasured / 10, "local safe_write grew: " + localSafe);
            Assert.True(localFull <= LocalFullWriteMeasured + LocalFullWriteMeasured / 10, "local full_write grew: " + localFull);
            Assert.True(openSafe <= UnrestrictedSafeWriteMeasured + UnrestrictedSafeWriteMeasured / 10, "unrestricted grew: " + openSafe);
            Assert.True(localSafe * 4 < fullLocalSafe, "compact should be well under a quarter of full");
        }

        // ---- 2 and 4 on the wire -----------------------------------------------------------
        // The REAL executable. No Revit is reachable, so a plugin tool answering "no Revit is
        // reachable" proves the call got past the facade, the gates and the argument handling.

        private const string SafeWrite = "{\"permission_profile\":\"safe_write\"}";

        [Fact]
        public void On_the_wire_compact_lists_the_same_names_and_serves_the_short_instructions()
        {
            Dictionary<string, JObject> f = Run("full", "local", SafeWrite, Req(2, "tools/list", new JObject()));
            Dictionary<string, JObject> c = Run("compact", "local", SafeWrite, Req(2, "tools/list", new JObject()));
            string[] fn = ((JArray)f["2"]["result"]["tools"]).Select(t => (string)t["name"]).ToArray();
            string[] cn = ((JArray)c["2"]["result"]["tools"]).Select(t => (string)t["name"]).ToArray();
            Assert.Equal(fn, cn);
            Assert.Equal(ServerInstructions.HeadFor(SchemaSurface.Compact), (string)c["1"]["result"]["instructions"]);
            Assert.Equal(ServerInstructions.Head, (string)f["1"]["result"]["instructions"]);
            _out.WriteLine("wire: local+compact " + SchemaFacadeTests.Bytes((JArray)c["2"]["result"]["tools"]) + " B, local+full " + SchemaFacadeTests.Bytes((JArray)f["2"]["result"]["tools"]) + " B");
        }

        [Fact]
        public void An_argument_the_compact_schema_omits_but_the_contract_has_is_accepted()
        {
            // include_diagnostics and bounding_box are in the contract and not in the compact schema
            var args = new JObject { ["categories"] = new JArray("Walls"), ["include_diagnostics"] = true,
                                     ["bounding_box"] = new JObject { ["min"] = new JArray(0, 0, 0), ["max"] = new JArray(1, 1, 1) } };
            Dictionary<string, JObject> r = Run("compact", null, SafeWrite, ToolCall(2, "horizun_query_model", args));
            JObject res = (JObject)r["2"]["result"];
            Assert.True((bool)res["isError"]);
            Assert.Contains("no Revit is reachable", Text(r["2"]));       // past every gate
            Assert.Null(res["structuredContent"]?["schema_help"]);        // the contract found nothing wrong with it
            Assert.DoesNotContain("schema_help", Text(r["2"]));
        }

        [Fact]
        public void Invalid_arguments_get_the_same_refusal_and_schema_help_as_in_full()
        {
            var calls = new[]
            {
                ToolCall(2, "horizun_query_model", new JObject { ["bogus_argument"] = 1 }),
                ToolCall(3, "horizun_create_elements", new JObject { ["target_document"] = "X", ["elements"] = new JArray(new JObject { ["kind"] = "wall", ["wibble"] = 1 }) }),
                ToolCall(4, "horizun_write_params_verified", new JObject { ["writes"] = "not an array" }),
                ToolCall(5, "horizun_transform_elements", new JObject { ["target_document"] = "X" })
            };
            Dictionary<string, JObject> f = Run("full", null, SafeWrite, calls);
            Dictionary<string, JObject> c = Run("compact", null, SafeWrite, calls);
            foreach (string id in new[] { "2", "3", "4", "5" })
            {
                Assert.True(JToken.DeepEquals(f[id]["result"], c[id]["result"]),
                    "call " + id + " answers differently in compact:\nfull   : " + f[id]["result"].ToString(Formatting.None) +
                    "\ncompact: " + c[id]["result"].ToString(Formatting.None));
            }
            Assert.Contains("horizun://contract/tools/horizun_query_model", c["2"]["result"].ToString(Formatting.None));
        }

        // ---- helpers -----------------------------------------------------------------------

        private static string Text(JObject reply) =>
            string.Concat(((JArray)reply["result"]["content"]).Select(c => (string)c["text"]));

        private static JObject Req(int id, string method, JObject prms) =>
            new JObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = prms };

        private static JObject ToolCall(int id, string tool, JObject args) =>
            Req(id, "tools/call", new JObject { ["name"] = tool, ["arguments"] = args });

        private static Dictionary<string, JObject> Run(string mode, string toolsets, string settings, params JObject[] requests)
        {
            string root = Path.Combine(Path.GetTempPath(), "hz-compact-wire-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "settings.json"), settings);
            try
            {
                var psi = new ProcessStartInfo(JsonRpcErrorCodeTests.ServerExe())
                {
                    RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                    UseShellExecute = false, CreateNoWindow = true,
                    StandardOutputEncoding = new UTF8Encoding(false)
                };
                psi.Environment[HorizunPaths.RootOverrideVariable] = root;
                psi.Environment.Remove(ToolPacks.EnvironmentOverride);
                psi.Environment.Remove("HORIZUN_REVIT_YEAR");
                if (toolsets == null) psi.Environment.Remove(ToolPacks.ToolsetsEnvironmentVariable);
                else psi.Environment[ToolPacks.ToolsetsEnvironmentVariable] = toolsets;
                psi.Environment[SchemaMode.EnvironmentVariable] = mode;

                var lines = new List<string>
                {
                    "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-11-25\",\"capabilities\":{},\"clientInfo\":{\"name\":\"compact-tests\",\"version\":\"1\"}}}",
                    "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}"
                };
                lines.AddRange(requests.Select(q => q.ToString(Formatting.None)));

                var replies = new Dictionary<string, JObject>(StringComparer.Ordinal);
                using (var proc = Process.Start(psi))
                {
                    var stderr = proc.StandardError.ReadToEndAsync();
                    var stdout = proc.StandardOutput.ReadToEndAsync();
                    foreach (string l in lines) proc.StandardInput.WriteLine(l);
                    proc.StandardInput.Close();
                    if (!proc.WaitForExit(120000))
                    {
                        try { proc.Kill(); } catch { }
                        throw new Xunit.Sdk.XunitException("server did not exit in 120 s");
                    }
                    string err = stderr.GetAwaiter().GetResult();
                    foreach (string raw in stdout.GetAwaiter().GetResult().Split('\n'))
                    {
                        string line = raw.Trim();
                        if (line.Length == 0) continue;
                        JObject msg;
                        try { msg = JObject.Parse(line); } catch { continue; }
                        if (msg["id"] != null && msg["method"] == null) replies[(string)msg["id"]] = msg;
                    }
                    foreach (string id in new[] { "1" }.Concat(requests.Select(q => (string)q["id"])))
                        Assert.True(replies.ContainsKey(id), "no reply to id " + id + "; stderr: " + err);
                }
                return replies;
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
            }
        }

        // ---- 8. the argument traps a small model fell into (battery, 2026-10-31) ---------------
        //
        // MEASURED with qwen3:8b over Ollama, 27 tasks, HORIZUN_TOOLSETS=local + compact: it picked
        // the right tool 85 % of the time and still failed, because it filled arguments with
        // placeholders the short schema did not rule out - target_document "Active Document",
        // "ACTIVE" or "", view_name "Active View", view_id 0, level_id 0, target="project_info"
        // beside a wall's target_id, `units` inside each element - and stopped at a clean
        // rehearsal the user had asked to apply. Each note below names where the value comes from.

        private static string Note(JObject inputSchema, params string[] path)
        {
            JToken node = inputSchema["properties"];
            for (int i = 0; i < path.Length; i++)
            {
                node = node?[path[i]];
                if (i < path.Length - 1) node = node?["items"]?["properties"];
            }
            return (string)node?["description"] ?? "";
        }

        [Fact]
        public void Target_document_says_exact_title_and_forbids_placeholders()
        {
            foreach (string name in new[] { "horizun_create_elements", "horizun_write_params_verified", "horizun_capture_view" })
            {
                var c = (JObject)SchemaCompact.Publish(Tools.Find(name), false)["inputSchema"];
                if (c["properties"]?["target_document"] == null) continue;
                string d = Note(c, "target_document");
                Assert.Contains("EXACT", d);
                Assert.Contains("horizun_health", d);
                Assert.Contains("placeholder", d);
            }
        }

        [Fact]
        public void Capture_view_says_omit_the_view_for_the_active_one()
        {
            JObject t = SchemaCompact.Publish(Tools.Find("horizun_capture_view"), false);
            Assert.Contains("ACTIVE view", (string)t["description"]);
            var c = (JObject)t["inputSchema"];
            Assert.Contains("Omit", Note(c, "view_id"));
            Assert.Contains("never 0", Note(c, "view_id"));
            Assert.Contains("Omit", Note(c, "view_name"));
        }

        [Fact]
        public void Create_elements_says_where_a_level_id_comes_from_and_where_units_go()
        {
            var c = (JObject)SchemaCompact.Publish(Tools.Find("horizun_create_elements"), false)["inputSchema"];
            Assert.Contains("Top level", Note(c, "units"));
            string level = Note(c, "elements", "level_id");
            Assert.Contains("OST_Levels", level);
            Assert.Contains("Never 0", level);
        }

        [Fact]
        public void Write_params_says_target_is_only_for_project_info_and_names_are_in_the_model_language()
        {
            var c = (JObject)SchemaCompact.Publish(Tools.Find("horizun_write_params_verified"), false)["inputSchema"];
            Assert.Contains("Omit for elements", Note(c, "writes", "target"));
            Assert.Contains("language", Note(c, "writes", "parameter"));
        }

        [Fact]
        public void Compact_instructions_forbid_placeholders_and_say_when_to_apply()
        {
            string s = ServerInstructions.CompactHead;
            Assert.Contains("never invent placeholders", s);
            Assert.Contains("If the user asked for it and the rehearsal is clean, apply", s);
            Assert.True(s.Length <= 600, "compact instructions are " + s.Length + " characters");
        }
    }
}
