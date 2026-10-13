// -----------------------------------------------------------------------------
// Horizun Server tests - original Horizun code.
//
// THE SCHEMA FACADE (3.0.0, phase 1). tools/list can advertise the whole contract
// (HORIZUN_SCHEMA_MODE=full, the 2.x surface byte for byte) or a facade: horizun_health
// plus three tools that list, describe and call every other tool on demand. These tests
// hold the five properties the facade is only worth having with:
//   1. it is small, under a ceiling declared HERE (a ledger refresh cannot move it);
//   2. nothing becomes unreachable - every tool the full list would advertise, in every
//      permission profile and every toolset selection, is listed, described EXACTLY and
//      callable through the facade;
//   3. full is the committed ledger, byte for byte, in every profile and pack;
//   4. a tool the profile or toolset switches off stays uncallable through the facade -
//      the facade is a second door to the SAME gate, never a way around it;
//   5. contract errors keep naming the failing path and the exact-schema URI (wire tests,
//      SchemaFacadeWireTests).
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using Horizun.Revit.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Server.Tests
{
    [Collection("HorizunSettingsRoot")]
    public class SchemaFacadeTests
    {
        /// <summary>
        /// THE FACADE'S CEILING, declared here and nowhere else. tools/list in facade mode is
        /// horizun_health plus three meta-tools; 8 KiB is ~1.6 % of the 2.x worst case and
        /// leaves room for the descriptions to be read by a model, not compressed for one.
        /// </summary>
        internal const int FacadeCeilingBytes = 8 * 1024;

        /// <summary>
        /// horizun_list_tools with no filter (every tool, one line each). It enters the
        /// context once per session, so it is budgeted like a tool list.
        /// </summary>
        internal const int ListToolsCeilingBytes = 40 * 1024;

        private static readonly string[] Profiles = { "read_only", "safe_write", "full_write", "unsafe_code" };

        private static IEnumerable<(string label, string settings)> Postures()
        {
            foreach (string p in Profiles)
                yield return ("permission:" + p, "{\"permission_profile\":\"" + p + "\"}");
            yield return ("permission:unsafe_code+python",
                          "{\"permission_profile\":\"unsafe_code\",\"enable_execute_python\":true}");
        }

        /// <summary>No selection, then every known toolset alone (with its dependencies and core).</summary>
        private static IEnumerable<string> Selections()
        {
            yield return null;
            foreach (string pack in ToolPacks.KnownPacks.OrderBy(k => k, StringComparer.Ordinal)) yield return pack;
        }

        // ---- the mode and its single default --------------------------------------------

        [Fact]
        public void The_default_is_full_until_the_agent_battery_can_be_measured()
        {
            Assert.Equal(SchemaSurface.Full, SchemaMode.Default);
            Assert.Equal(SchemaMode.Default, SchemaMode.Resolve(null, out string source, out string problem));
            Assert.Equal("default", source);
            Assert.Null(problem);
            Assert.Equal(SchemaSurface.Full, SchemaMode.Resolve("full", out source, out problem));
            Assert.Equal("environment", source);
            Assert.Equal(SchemaSurface.Facade, SchemaMode.Resolve(" FACADE ", out _, out problem));
            Assert.Null(problem);

            // A typo must not silently pick a surface: it falls back to the default AND says so.
            Assert.Equal(SchemaMode.Default, SchemaMode.Resolve("ful", out source, out problem));
            Assert.NotNull(problem);
            Assert.Contains(SchemaMode.EnvironmentVariable, problem);
        }

        // ---- 1. the facade is small --------------------------------------------------------

        [Fact]
        public void Facade_tools_list_fits_its_declared_ceiling_in_every_posture()
        {
            foreach (var (label, settings) in Postures())
                WithDataRoot(settings, null, () =>
                {
                    JArray facade = SchemaFacade.List(SchemaSurface.Facade, true);
                    int bytes = Bytes(facade);
                    Assert.True(bytes <= FacadeCeilingBytes,
                        label + ": the facade tools/list is " + bytes + " B, over its " + FacadeCeilingBytes + " B ceiling");
                    Assert.Equal(new[] { "horizun_health", SchemaFacade.ListToolsName, SchemaFacade.DescribeToolName,
                                         SchemaFacade.CallToolName },
                                 facade.Select(t => (string)t["name"]).ToArray());

                    // horizun_health is advertised exactly as the full list advertises it.
                    JToken health = Tools.List(true).First(t => (string)t["name"] == "horizun_health");
                    Assert.True(JToken.DeepEquals(health, facade[0]), label + ": horizun_health differs from the full list");

                    int listBytes = Bytes(SchemaFacade.ListTools(new JObject()));
                    Assert.True(listBytes <= ListToolsCeilingBytes,
                        label + ": horizun_list_tools answers " + listBytes + " B, over its " + ListToolsCeilingBytes + " B ceiling");
                });
        }

        [Fact]
        public void The_facade_tools_are_not_contract_rows_so_the_contract_hash_cannot_move()
        {
            foreach (string meta in new[] { SchemaFacade.ListToolsName, SchemaFacade.DescribeToolName, SchemaFacade.CallToolName })
            {
                Assert.Null(Horizun.Contracts.Contract.Find(meta));
                Assert.Null(Tools.Find(meta));
                Assert.True(SchemaFacade.IsMetaTool(meta, SchemaSurface.Facade));
                Assert.False(SchemaFacade.IsMetaTool(meta, SchemaSurface.Full)); // full = today: they do not exist
            }
            Assert.Matches("^[0-9a-f]{24}$", SchemaFacade.Hash);
            Assert.NotEqual(Horizun.Contracts.Contract.Hash, SchemaFacade.Hash);
        }

        // ---- 2. nothing is unreachable ------------------------------------------------------

        [Fact]
        public void Every_tool_the_full_list_advertises_is_listed_described_and_callable_through_the_facade()
        {
            int checkedTools = 0;
            foreach (var (label, settings) in Postures())
                foreach (string selection in Selections())
                    WithDataRoot(settings, selection, () =>
                    {
                        string where = label + " / toolsets=" + (selection ?? "(none)");
                        string[] reachable = Tools.List(true).Select(t => (string)t["name"]).ToArray();

                        JObject listed = SchemaFacade.ListTools(new JObject());
                        string[] names = ((JObject)listed["tools"]).Properties().Select(p => p.Name).ToArray();
                        Assert.True(reachable.SequenceEqual(names),
                            where + ": horizun_list_tools differs from the full list. Missing: " +
                            string.Join(", ", reachable.Except(names)) + "; extra: " + string.Join(", ", names.Except(reachable)));

                        foreach (string pack in ToolPacks.KnownPacks)
                        {
                            JObject filtered = SchemaFacade.ListTools(new JObject { ["toolset"] = pack });
                            string[] expected = reachable.Where(name => Tools.Find(name).Toolsets.Contains(pack)).ToArray();
                            Assert.Equal(expected, ((JObject)filtered["tools"]).Properties().Select(p => p.Name).ToArray());
                        }

                        foreach (string tool in reachable)
                        {
                            JObject d = SchemaFacade.Describe(new JObject { ["name"] = tool });
                            Horizun.Contracts.CommandContract c = Horizun.Contracts.Contract.Find(tool);
                            Assert.True(JToken.DeepEquals(c.InputSchema, d["input_schema"]),
                                where + ": " + tool + " is not described with its EXACT contract schema");
                            Assert.Equal(c.Description, (string)d["description"]);
                            Assert.Equal(McpResources.ContractToolPrefix + tool, (string)d["contract_uri"]);
                            Assert.True((bool)d["callable"], where + ": " + tool + " described as not callable");

                            var args = new JObject { ["probe"] = 1, ["nested"] = new JObject { ["x"] = "y" } };
                            JObject inner = SchemaFacade.UnwrapCall(Call(tool, args), SchemaSurface.Facade);
                            Assert.Equal(tool, (string)inner["name"]);
                            Assert.True(JToken.DeepEquals(args, inner["arguments"]), where + ": " + tool + " arguments altered");
                            Assert.Null(Tools.DisabledReason(tool));
                            checkedTools++;
                        }
                    });
            Assert.True(checkedTools > 0);
        }

        [Fact]
        public void Every_variant_is_described_verbatim_through_the_facade()
        {
            foreach (VariantSite site in ContractVariants.All)
                foreach (string value in site.Values)
                {
                    JObject d = SchemaFacade.Describe(new JObject { ["name"] = site.Tool, ["variant"] = value });
                    Assert.True(JToken.DeepEquals(site.Describe(value), d["schema"]),
                        site.Tool + "/" + value + " is not the verbatim variant schema");
                    Assert.Equal(site.Uri(value), (string)d["uri"]);
                }
        }

        [Fact]
        public void A_call_through_the_facade_carries_the_request_metadata_with_it()
        {
            var prms = Call("horizun_query_model", new JObject { ["kind"] = "count" });
            prms["_meta"] = new JObject { ["progressToken"] = "p1" };
            prms["task"] = new JObject { ["ttl"] = 1000 };
            JObject inner = SchemaFacade.UnwrapCall(prms, SchemaSurface.Facade);
            Assert.Equal("horizun_query_model", (string)inner["name"]);
            Assert.Equal("p1", (string)inner["_meta"]["progressToken"]);
            Assert.Equal(1000, (int)inner["task"]["ttl"]);

            // In full mode the facade does not exist: nothing is unwrapped.
            Assert.Same(prms, SchemaFacade.UnwrapCall(prms, SchemaSurface.Full));
        }

        // ---- 3. full is today ------------------------------------------------------------------

        [Fact]
        public void Full_mode_is_the_committed_ledger_byte_for_byte()
        {
            JObject ledger = JObject.Parse(File.ReadAllText(LedgerPath()));
            foreach (var (label, settings) in Postures())
                WithDataRoot(settings, null, () =>
                {
                    JArray full = SchemaFacade.List(SchemaSurface.Full, true);
                    Assert.True(JToken.DeepEquals(Tools.List(true), full), label + ": full mode is not Tools.List");
                    Assert.Equal((int)ledger["profiles"][label]["bytes"], Bytes(full));
                    Assert.Equal((int)ledger["profiles"][label]["tools"], full.Count);
                });
            WithDataRoot("{\"permission_profile\":\"unsafe_code\",\"enable_execute_python\":true}", null, () =>
            {
                foreach (string pack in ToolPacks.KnownPacks)
                    WithDataRoot("{\"permission_profile\":\"unsafe_code\",\"enable_execute_python\":true}", pack, () =>
                        Assert.Equal((int)ledger["profiles"]["pack:" + pack]["bytes"], Bytes(SchemaFacade.List(SchemaSurface.Full, true))));
            });
        }

        // ---- 4. no escalation ---------------------------------------------------------------------

        [Fact]
        public void A_tool_the_profile_or_toolset_switches_off_stays_uncallable_through_the_facade()
        {
            int refused = 0;
            foreach (var (label, settings) in Postures())
                foreach (string selection in Selections())
                    WithDataRoot(settings, selection, () =>
                    {
                        string where = label + " / toolsets=" + (selection ?? "(none)");
                        var reachable = new HashSet<string>(Tools.List(true).Select(t => (string)t["name"]), StringComparer.Ordinal);
                        JObject listed = SchemaFacade.ListTools(new JObject());
                        foreach (Horizun.Contracts.CommandContract c in Horizun.Contracts.Contract.All)
                        {
                            if (reachable.Contains(c.Name)) continue;
                            Assert.Null(listed["tools"]?[c.Name]);
                            JObject d = SchemaFacade.Describe(new JObject { ["name"] = c.Name });
                            Assert.False((bool)d["callable"], where + ": " + c.Name + " described as callable");
                            string gate = Tools.DisabledReason(c.Name);
                            Assert.NotNull(gate);
                            Assert.Equal(gate, (string)d["refusal"]);
                            // The facade forwards the NAME; the gate that refuses a direct call
                            // refuses this one, because it is the same call.
                            JObject inner = SchemaFacade.UnwrapCall(Call(c.Name, new JObject()), SchemaSurface.Facade);
                            Assert.Equal(c.Name, (string)inner["name"]);
                            refused++;
                        }
                    });
            Assert.True(refused > 0, "no posture disabled anything, so nothing was proven");

            WithDataRoot("{\"permission_profile\":\"safe_write\"}", null, () =>
            {
                Assert.NotNull(Tools.DisabledReason("horizun_execute_python"));
                Assert.False((bool)SchemaFacade.Describe(new JObject { ["name"] = "horizun_execute_python" })["callable"]);
            });
        }

        [Fact]
        public void The_call_tool_cannot_call_itself()
        {
            JObject prms = Call(SchemaFacade.CallToolName, new JObject { ["name"] = "horizun_health" });
            // Not unwrapped: CallToolCore answers it as an invalid facade call.
            JObject inner = SchemaFacade.UnwrapCall(prms, SchemaSurface.Facade);
            Assert.Equal(SchemaFacade.CallToolName, (string)inner["name"]);
            var ex = Assert.Throws<ToolRefusal>(() => SchemaFacade.Answer(SchemaFacade.CallToolName, (JObject)inner["arguments"]));
            Assert.Contains("itself", ex.Message);

            foreach (JObject bad in new[]
            {
                new JObject(), new JObject { ["name"] = 3 }, new JObject { ["name"] = "nope_tool" },
                new JObject { ["name"] = "horizun_health", ["arguments"] = "x" }
            })
                Assert.Throws<ToolRefusal>(() => SchemaFacade.Answer(SchemaFacade.CallToolName, bad));
        }

        // ---- the notification and the identity ---------------------------------------------------

        [Fact]
        public void Facade_arguments_obey_the_published_nonnullable_types()
        {
            Assert.Throws<ToolRefusal>(() => SchemaFacade.ListTools(new JObject { ["toolset"] = JValue.CreateNull() }));
            Assert.Throws<ToolRefusal>(() => SchemaFacade.Describe(new JObject { ["name"] = "horizun_target", ["variant"] = JValue.CreateNull() }));
            JObject call = Call("horizun_target", null);
            Assert.Same(call, SchemaFacade.UnwrapCall(call, SchemaSurface.Facade));
            Assert.Throws<ToolRefusal>(() => SchemaFacade.Answer(SchemaFacade.CallToolName, (JObject)call["arguments"]));
        }

        [Fact]
        public void List_changed_still_fires_in_facade_mode_when_the_reachable_set_changes()
        {
            // In facade mode tools/list itself barely changes; what changes is what
            // horizun_list_tools answers. The monitor watches the REACHABLE set, so a
            // client is still told to look again.
            WithDataRoot("{\"permission_profile\":\"safe_write\"}", null, () =>
            {
                int fired = 0;
                using (var monitor = new ToolListMonitor((m, p) => { if (m == "notifications/tools/list_changed") fired++; }, watch: false))
                {
                    File.WriteAllText(HorizunPaths.SettingsPath(), "{\"permission_profile\":\"read_only\"}");
                    monitor.CheckNow();
                }
                Assert.Equal(1, fired);
            }, mode: "facade");
        }

        [Fact]
        public void Health_reports_the_mode_both_hashes_and_what_tools_list_costs()
        {
            WithDataRoot("{\"permission_profile\":\"safe_write\"}", null, () =>
            {
                JObject h = SchemaFacade.HealthBlock();
                Assert.Equal("facade", (string)h["mode"]);
                Assert.Equal(SchemaMode.Name(SchemaMode.Default), (string)h["default"]);
                Assert.Equal("environment", (string)h["source"]);
                Assert.Equal(Horizun.Contracts.Contract.Hash, (string)h["contract_hash"]);
                Assert.Equal(SchemaFacade.Hash, (string)h["facade_hash"]);
                Assert.Equal(4, (int)h["tools_list"]["tools"]);
                Assert.Equal(Bytes(SchemaFacade.List(SchemaSurface.Facade, false)), (int)h["tools_list"]["utf8_bytes"]);
                Assert.Equal(Tools.List(false).Count, (int)h["reachable_tools"]);
            }, mode: "facade");
        }

        [Fact]
        public void The_facade_instructions_fit_the_head_budget_and_name_the_three_tools()
        {
            Assert.Equal(ServerInstructions.Head, ServerInstructions.HeadFor(SchemaSurface.Full));
            string h = ServerInstructions.HeadFor(SchemaSurface.Facade);
            Assert.True(Encoding.UTF8.GetByteCount(h) <= 2048, "facade head is " + Encoding.UTF8.GetByteCount(h) + " B");
            foreach (string marker in new[]
            {
                SchemaFacade.ListToolsName, SchemaFacade.DescribeToolName, SchemaFacade.CallToolName,
                "horizun://contract/tools/{tool}", "horizun://contract/tools/{tool}/{variant}", "structuredContent.schema_help",
                "Call horizun_health FIRST", "disabled toolset"
            })
                Assert.True(h.Contains(marker), "facade head lost: " + marker);
        }

        // ---- helpers ------------------------------------------------------------------------------

        internal static JObject Call(string tool, JObject arguments) => new JObject
        {
            ["name"] = SchemaFacade.CallToolName,
            ["arguments"] = new JObject { ["name"] = tool, ["arguments"] = arguments }
        };

        internal static int Bytes(JToken t) => Encoding.UTF8.GetByteCount(t.ToString(Formatting.None));

        private static string LedgerPath([CallerFilePath] string here = null) =>
            Path.Combine(Path.GetDirectoryName(here), "tools-list-ledger.json");

        internal static void WithDataRoot(string settingsJson, string toolsets, Action action, string mode = null)
        {
            string saved = Environment.GetEnvironmentVariable(HorizunPaths.RootOverrideVariable);
            string savedPacks = Environment.GetEnvironmentVariable(ToolPacks.EnvironmentOverride);
            string savedToolsets = Environment.GetEnvironmentVariable(ToolPacks.ToolsetsEnvironmentVariable);
            string savedMode = Environment.GetEnvironmentVariable(SchemaMode.EnvironmentVariable);
            string temp = Path.Combine(Path.GetTempPath(), "hz-facade-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(temp);
                Environment.SetEnvironmentVariable(HorizunPaths.RootOverrideVariable, temp);
                Environment.SetEnvironmentVariable(ToolPacks.EnvironmentOverride, null);
                Environment.SetEnvironmentVariable(ToolPacks.ToolsetsEnvironmentVariable, toolsets);
                Environment.SetEnvironmentVariable(SchemaMode.EnvironmentVariable, mode);
                File.WriteAllText(HorizunPaths.SettingsPath(), settingsJson);
                action();
            }
            finally
            {
                Environment.SetEnvironmentVariable(HorizunPaths.RootOverrideVariable, saved);
                Environment.SetEnvironmentVariable(ToolPacks.EnvironmentOverride, savedPacks);
                Environment.SetEnvironmentVariable(ToolPacks.ToolsetsEnvironmentVariable, savedToolsets);
                Environment.SetEnvironmentVariable(SchemaMode.EnvironmentVariable, savedMode);
                try { Directory.Delete(temp, true); } catch { }
            }
        }
    }
}
