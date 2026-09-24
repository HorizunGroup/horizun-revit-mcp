// -----------------------------------------------------------------------------
// Horizun Server tests - original Horizun code.
//
// TOOLSETS ARE TOOL PACKS, declared in the contract. Proved from the outside: every
// tool declares its toolsets beside its other declarations and a new tool without a
// row fails here; the declaration IS the pack membership (no second list);
// HORIZUN_TOOLSETS / "toolsets" select exactly like the pack spellings and never
// override them; the convenience names expand to real packs; an unconfigured server
// advertises what it always did; and what a selection saves is a measured number.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Horizun.Contracts;
using Horizun.Revit.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;
using Xunit.Abstractions;

namespace Horizun.Server.Tests
{
    public class ToolsetTests
    {
        private readonly ITestOutputHelper _out;
        public ToolsetTests(ITestOutputHelper output) { _out = output; }

        // ---- the map, declared in the contract ----------------------------------

        [Fact]
        public void Every_contract_tool_declares_at_least_one_known_toolset()
        {
            // THE GUARD FOR A NEW TOOL. A tool added without a ToolsetCatalog row would
            // be reachable only when nothing is restricted; this fails the build instead.
            List<string> problems = ToolsetCatalog.Audit(Contract.All.Select(c => c.Name));
            Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));

            foreach (CommandContract c in Contract.All)
            {
                Assert.True(c.Toolsets != null && c.Toolsets.Length > 0, c.Name + " declares no toolset");
                foreach (string s in c.Toolsets)
                    Assert.Contains(s, ToolPacks.KnownPacks);
            }
        }

        [Fact]
        public void The_audit_names_a_tool_without_a_row()
        {
            List<string> problems = ToolsetCatalog.Audit(
                Contract.All.Select(c => c.Name).Concat(new[] { "horizun_brand_new_tool" }));
            Assert.Contains(problems, p => p.Contains("horizun_brand_new_tool") && p.Contains("declares no toolset"));
        }

        [Fact]
        public void The_contract_declaration_is_the_pack_membership_with_no_second_list()
        {
            foreach (string pack in ToolPacks.KnownPacks)
            {
                var declared = Contract.All.Where(c => c.Toolsets.Contains(pack)).Select(c => c.Name)
                                       .OrderBy(n => n, StringComparer.Ordinal).ToArray();
                var members = ToolPacks.MembersOf(pack).OrderBy(n => n, StringComparer.Ordinal).ToArray();
                Assert.Equal(declared, members);
            }
            Assert.Equal(ToolsetCatalog.Known.OrderBy(k => k, StringComparer.Ordinal),
                         ToolPacks.KnownPacks.OrderBy(k => k, StringComparer.Ordinal));
        }

        [Fact]
        public void The_requested_disciplines_are_all_selectable()
        {
            // Every name of the requested vocabulary resolves to something real: most are
            // packs, cad is a new pack, and families/data/admin expand to existing packs.
            foreach (string name in new[] { "core", "documentation", "architecture", "structure", "mep", "cad",
                                            "families", "coordination", "data", "admin" })
            {
                ToolPacks.Resolution r = ToolPacks.Resolve(name, null, false);
                Assert.True(r.Problem == null, name + ": " + r.Problem);
            }
            Assert.Equal(new[] { "horizun_apply_cad_plan", "horizun_apply_cad_update", "horizun_audit_cad_model",
                                 "horizun_cad_connect", "horizun_cad_extract", "horizun_cad_networks",
                                 "horizun_cad_review", "horizun_cad_symbols", "horizun_cad_unit_instances",
                                 "horizun_manage_cad_links", "horizun_plan_cad_update", "horizun_plan_from_cad",
                                 "horizun_query_cad" },
                         ToolPacks.MembersOf("cad").OrderBy(n => n, StringComparer.Ordinal).ToArray());
        }

        [Fact]
        public void Aliases_expand_to_real_packs_and_cannot_widen_the_guarded_ones()
        {
            ToolPacks.Resolution admin = ToolPacks.Resolve(null, new[] { "admin" }, false);
            Assert.Equal(new[] { "administration", "unsafe_code" }, admin.ChosenPacks.ToArray());

            ToolPacks.Resolution data = ToolPacks.Resolve("data", null, false);
            Assert.Contains("powerbi", data.ActivePacks);
            Assert.Contains("schedules", data.ActivePacks);
            Assert.Contains("interoperability", data.ActivePacks);

            // An alias is not a pack: nothing declares membership in one.
            foreach (string alias in ToolsetCatalog.Aliases.Keys)
            {
                Assert.DoesNotContain(alias, ToolPacks.KnownPacks);
                Assert.DoesNotContain(Contract.All, c => c.Toolsets.Contains(alias));
            }
        }

        // ---- the two spellings ----------------------------------------------------

        [Fact]
        public void Without_configuration_tools_list_is_exactly_what_it_was()
        {
            WithIsolation(null, null, null, () =>
            {
                Assert.False(Settings.ActivePackResolution().Restricting);
                Assert.Equal(Tools.ListIgnoringPacks(false).ToString(Formatting.None),
                             Tools.List(false).ToString(Formatting.None));
            });
        }

        [Fact]
        public void HORIZUN_TOOLSETS_selects_like_the_pack_variable_and_the_refusal_names_the_toolset()
        {
            WithIsolation(null, null, "core", () =>
            {
                ToolPacks.Resolution r = Settings.ActivePackResolution();
                Assert.Equal(ToolPacks.SelectionSource.Environment, r.Source);
                Assert.Equal("HORIZUN_TOOLSETS", r.SourceName);

                var names = Tools.List(false).OfType<JObject>().Select(t => (string)t["name"]).ToList();
                Assert.Equal(ToolPacks.MembersOf("core").OrderBy(n => n, StringComparer.Ordinal),
                             names.OrderBy(n => n, StringComparer.Ordinal));

                string refusal = Tools.DisabledReason("horizun_cad_extract");
                Assert.NotNull(refusal);
                Assert.Contains("cad", refusal);
                Assert.Contains("HORIZUN_TOOLSETS", refusal);
                Assert.Contains("hidden by the active tool packs", refusal);
            });
        }

        [Fact]
        public void The_pack_spelling_wins_when_both_are_present()
        {
            WithIsolation(null, "documentation", "cad", () =>
            {
                ToolPacks.Resolution r = Settings.ActivePackResolution();
                Assert.Equal("HORIZUN_TOOL_PACKS", r.SourceName);
                Assert.Contains("documentation", r.ActivePacks);
                Assert.DoesNotContain("cad", r.ActivePacks);
            });

            WithIsolation("{\"tool_packs\":[\"read\"],\"toolsets\":[\"cad\"]}", null, null, () =>
            {
                ToolPacks.Resolution r = Settings.ActivePackResolution();
                Assert.Equal("tool_packs", r.SourceName);
                Assert.Equal(new[] { "read" }, r.ActivePacks.ToArray());
            });
        }

        [Fact]
        public void The_toolsets_settings_key_selects_and_a_toolset_never_elevates()
        {
            WithIsolation("{\"toolsets\":[\"mep\"],\"permission_profile\":\"read_only\"}", null, null, () =>
            {
                ToolPacks.Resolution r = Settings.ActivePackResolution();
                Assert.Equal("toolsets", r.SourceName);
                var names = Tools.List(false).OfType<JObject>().Select(t => (string)t["name"]).ToHashSet();
                Assert.Contains("horizun_query_model", names);           // read, via mep -> model -> read
                Assert.Contains("horizun_cad_networks", names);          // a read tool of mep
                Assert.DoesNotContain("horizun_connect_mep", names);     // a write: read_only wins
                Assert.DoesNotContain("horizun_pack_sheets", names);     // another discipline
            });

            WithIsolation("{\"toolsets\":[\"admin\"]}", null, null, () =>
            {
                var names = Tools.List(false).OfType<JObject>().Select(t => (string)t["name"]).ToHashSet();
                // The alias reaches unsafe_code; the OWNER switch still gates Python.
                Assert.DoesNotContain("horizun_execute_python", names);
                Assert.Contains("horizun_request_python_access", names);
            });
        }

        [Fact]
        public void A_toolsets_setting_change_announces_tools_list_changed()
        {
            WithIsolation(null, null, null, () =>
            {
                int notified = 0;
                using (var monitor = new ToolListMonitor((m, p) => notified++, watch: false))
                {
                    WriteSettings("{\"toolsets\":[\"cad\"]}");
                    monitor.CheckNow();
                    Assert.Equal(1, notified);
                    monitor.CheckNow();
                    Assert.Equal(1, notified); // unchanged selection: no second notice
                }
            });
        }

        // ---- the measured saving --------------------------------------------------

        [Fact]
        public void The_saving_of_core_and_of_each_discipline_over_the_full_list_is_measured()
        {
            // Measured at the shipped default (safe_write) and at full_write, where every
            // typed tool is visible. The numbers are printed for the record.
            foreach (string profile in new[] { null, "{\"permission_profile\":\"full_write\"}" })
            {
                WithIsolation(profile, null, null, () =>
                {
                    JObject full = ToolsetReport.Size(Tools.List(false));
                    _out.WriteLine((profile ?? "default safe_write") + " all: " + full.ToString(Formatting.None));
                    foreach (string set in new[] { "core", "read", "documentation", "architecture", "structure",
                                                   "mep", "cad", "families", "coordination", "data", "admin" })
                    {
                        JObject alone = ToolsetReport.Size(Tools.ListForPacks(
                            ToolPacks.Resolve(set, null, false).ChosenPacks));
                        _out.WriteLine("  " + set + ": " + alone.ToString(Formatting.None));
                    }

                    Environment.SetEnvironmentVariable(ToolPacks.ToolsetsEnvironmentVariable, "core");
                    JObject core = ToolsetReport.Size(Tools.List(false));
                    long fullChars = (long)full["characters"], coreChars = (long)core["characters"];
                    double reduction = 100.0 * (fullChars - coreChars) / fullChars;
                    _out.WriteLine("  HORIZUN_TOOLSETS=core -> " + core.ToString(Formatting.None) +
                                   ", reduction " + reduction.ToString("F1") + "%");

                    Assert.True((int)core["tools"] < (int)full["tools"]);
                    Assert.True(reduction >= 90.0, "core should cut the list by >= 90%; measured " + reduction);
                    Assert.Equal(ToolsetReport.ApproxTokens(coreChars), (long)core["approx_tokens"]);

                    // The resource reports the same numbers the client pays.
                    JObject doc = ToolsetReport.Document();
                    Assert.True((bool)doc["measured"], doc.ToString());
                    Assert.Equal(coreChars, (long)doc["advertised"]["characters"]);
                    Assert.Equal(fullChars, (long)doc["without_selection"]["characters"]);
                    Assert.Equal(ToolPacks.KnownPacks.Count, ((JArray)doc["toolsets"]).Count);
                    Assert.Equal("HORIZUN_TOOLSETS", (string)doc["selection"]["source_name"]);

                    JObject health = ToolsetReport.HealthBlock();
                    Assert.Equal(new JArray("core"), health["active"]);
                    Assert.Equal(core["approx_tokens"], health["tools_list_approx_tokens"]);
                });
            }
        }

        [Fact]
        public void The_toolsets_resource_is_listed_and_readable()
        {
            WithIsolation(null, null, null, () =>
            {
                JObject list = McpResources.List(null);
                Assert.Contains(((JArray)list["resources"]).OfType<JObject>(),
                                r => (string)r["uri"] == ToolsetReport.ResourceUri);
                JObject read = McpResources.Read(new JObject { ["uri"] = ToolsetReport.ResourceUri });
                JObject doc = JObject.Parse((string)read["contents"][0]["text"]);
                Assert.False((bool)doc["selection"]["restricting"]);
                Assert.Equal("all", (string)doc["selection"]["active"]);
                Assert.Equal(0, (long)doc["characters_saved"]);
            });
        }

        // ---- plumbing --------------------------------------------------------------

        private static string _dir;

        private static void WriteSettings(string json)
            => File.WriteAllText(Path.Combine(_dir, "settings.json"), json);

        private static void WithIsolation(string settingsJson, string packsEnv, string toolsetsEnv, Action action)
        {
            string oldRoot = Environment.GetEnvironmentVariable("HORIZUN_DATA_ROOT");
            string oldPacks = Environment.GetEnvironmentVariable(ToolPacks.EnvironmentOverride);
            string oldSets = Environment.GetEnvironmentVariable(ToolPacks.ToolsetsEnvironmentVariable);
            _dir = Path.Combine(Path.GetTempPath(), "hz-toolsets-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            try
            {
                Environment.SetEnvironmentVariable("HORIZUN_DATA_ROOT", _dir);
                Environment.SetEnvironmentVariable(ToolPacks.EnvironmentOverride, packsEnv);
                Environment.SetEnvironmentVariable(ToolPacks.ToolsetsEnvironmentVariable, toolsetsEnv);
                if (settingsJson != null) WriteSettings(settingsJson);
                action();
            }
            finally
            {
                Environment.SetEnvironmentVariable("HORIZUN_DATA_ROOT", oldRoot);
                Environment.SetEnvironmentVariable(ToolPacks.EnvironmentOverride, oldPacks);
                Environment.SetEnvironmentVariable(ToolPacks.ToolsetsEnvironmentVariable, oldSets);
                try { Directory.Delete(_dir, true); } catch { }
            }
        }
    }
}
