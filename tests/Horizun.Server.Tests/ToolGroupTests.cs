// -----------------------------------------------------------------------------
// Horizun Server tests - original Horizun code.
//
// 4.0 TOOL GROUPS AND PROFILES, proved in process.
//
//   * the map: every contract tool has exactly ONE group, a tool with no row fails
//     (that is what forces tools from parallel branches into a group at merge time),
//     the sizes are the approved proposal's annex A1;
//   * the core is 16 contract tools + horizun_toolsets, listed with the two meta tools;
//   * profiles fit their token budgets (bytes / 3.5, the proposal's high bound);
//   * a group is visibility, never privilege: activation changes no admission answer,
//     the 3.x toolsets selection is a ceiling no activation crosses, and a hidden tool is
//     reachable through horizun_call_tool exactly as a direct call is;
//   * each change that alters the list emits ONE notifications/tools/list_changed;
//   * health carries the profile, groups, activations, ceiling and list size;
//   * no tool_profile (an upgrade) is the 3.x list byte for byte; a tool_profile (what
//     the installer writes on a new install) is the grouped list; the command line wins
//     over the environment, which wins over settings.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Horizun.Contracts;
using Horizun.Revit.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;
using Xunit.Abstractions;

namespace Horizun.Server.Tests
{
    [Collection("HorizunSettingsRoot")]
    public class ToolGroupTests
    {
        private readonly ITestOutputHelper _out;
        public ToolGroupTests(ITestOutputHelper output) { _out = output; }

        private const string Python = "{\"permission_profile\":\"unsafe_code\",\"enable_execute_python\":true}";
        private const string SafeWrite = "{\"permission_profile\":\"safe_write\"}";

        // ---- the map ------------------------------------------------------------------------

        [Fact]
        public void Every_contract_tool_has_exactly_one_known_group()
        {
            List<string> problems = ToolGroupCatalog.Audit(Contract.All.Select(c => c.Name));
            Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
            foreach (CommandContract c in Contract.All)
                Assert.Contains(ToolGroupCatalog.GroupOf(c.Name), ToolGroupCatalog.Groups);
            int sum = ToolGroupCatalog.Groups.Sum(g => ToolGroupCatalog.MembersOf(g).Length);
            Assert.Equal(Contract.All.Count(), sum);   // one row per tool: no tool counted twice
        }

        [Fact]
        public void A_tool_without_a_group_fails_the_audit()
        {
            // A tool merged in from another branch with no row: this is the failure that forces a choice.
            List<string> problems = ToolGroupCatalog.Audit(
                Contract.All.Select(c => c.Name).Concat(new[] { "horizun_brand_new_tool" }));
            Assert.Contains(problems, p => p.Contains("horizun_brand_new_tool") && p.Contains("has no group"));
        }

        [Fact]
        public void Group_sizes_are_the_approved_annex()
        {
            var expected = new Dictionary<string, int>
            {
                ["nucleo"] = 16, ["lectura"] = 11, ["modelado"] = 13, ["arquitectura"] = 12, ["estructura"] = 7,
                ["mep"] = 0, ["documentacion"] = 16, ["cad_ifc"] = 1, ["auditoria"] = 8, ["coordinacion"] = 1,
                ["nube_cde"] = 3, ["familias"] = 6, ["datos"] = 12, ["admin"] = 4
            };
            Assert.Equal(expected.Keys.OrderBy(k => k), ToolGroupCatalog.Groups.OrderBy(k => k));
            var wrong = expected.Where(kv => kv.Value != ToolGroupCatalog.MembersOf(kv.Key).Length)
                                .Select(kv => kv.Key + ": " + ToolGroupCatalog.MembersOf(kv.Key).Length + " tools, annex says " + kv.Value).ToList();
            Assert.True(wrong.Count == 0, string.Join("; ", wrong));
            Assert.Equal(13, ToolGroupCatalog.OptionalGroups.Count);
        }

        [Fact]
        public void The_core_is_sixteen_contract_tools_plus_toolsets_and_lists_nineteen()
        {
            Assert.Equal(new[]
            {
                "horizun_health", "horizun_target", "horizun_job_status", "horizun_submit_job", "horizun_query_model",
                "horizun_list_elements", "horizun_navigate", "horizun_capture_view", "horizun_write_params_verified",
                "horizun_delete_verified", "horizun_verify_changes", "horizun_undo", "horizun_execute_python",
                "horizun_request_python_access", "horizun_save_document", "horizun_open_document"
            }, ToolGroupCatalog.MembersOf("nucleo"));
            Assert.Equal(new[] { "horizun_toolsets", "horizun_describe_tool", "horizun_call_tool" }, ToolGroupCatalog.ServerCoreTools);
            Assert.Equal("nucleo", ToolGroupCatalog.GroupOf("horizun_toolsets"));

            WithRoot(Python, () =>
            {
                string[] names = Names(ToolVisibility.ListForProfile("nucleo", true));
                Assert.Equal(19, names.Length);
                Assert.Equal(new[] { "horizun_toolsets", "horizun_describe_tool", "horizun_call_tool" }, names.Skip(16).ToArray());
            });
        }

        [Fact]
        public void Every_profile_and_alias_resolves_and_the_3x_names_map_onto_groups()
        {
            foreach (string p in ToolGroupCatalog.Profiles) Assert.Equal(p, ToolGroupCatalog.CanonicalProfile(p));
            Assert.Equal("completo", ToolGroupCatalog.CanonicalProfile("FULL"));
            Assert.Equal("analista_datos", ToolGroupCatalog.CanonicalProfile("Analista-Datos"));
            Assert.Equal(new[] { "documentacion" }, ToolGroupCatalog.ResolveGroupName("documentation"));
            Assert.Equal(new[] { "documentacion" }, ToolGroupCatalog.ResolveGroupName("Documentación"));
            Assert.Equal(new[] { "cad_ifc", "datos" }, ToolGroupCatalog.ResolveGroupName("interoperability"));
            foreach (string pack in ToolPacks.KnownPacks)
                Assert.True(ToolGroupCatalog.ResolveGroupName(pack) != null, "3.x toolset '" + pack + "' maps to no group");
            foreach (string alias in ToolsetCatalog.Aliases.Keys)
                Assert.True(ToolGroupCatalog.ResolveGroupName(alias) != null, "3.x alias '" + alias + "' maps to no group");
        }

        // ---- budgets -------------------------------------------------------------------------

        [Fact]
        public void Profile_lists_stay_within_their_token_budgets()
        {
            // High-bound tokens (UTF-8 bytes / 3.5, the proposal's calibration) at the widest
            // posture (unsafe_code + Python). Budgets: the approved proposal's upper bound for each
            // profile plus the three meta tools; a profile that grows past it is a deliberate
            // decision to make in review, not drift.
            // auditor 56000 -> 56100 (int/4.2 integration): the cooperative budget of audit_model/query_planimetry (C2) and the
            // identity fields and validate_ids (R2P) landed in this profile together; the integration trimmed what it could
            // (short forms, fragment) and the remaining ~100 tokens are a deliberate decision, recorded in INT-4.2-correcciones.md.
            // Q90 R2 restores the complete upgrade/central/PDF/COBie warnings instead
            // of deleting argument prose. Measured auditor: 196829 B / 3.5 = 56237
            // tokens. Reserve 200 more tokens for that restored safety text; the
            // independent all-tools ceiling still requires 8000 B headroom.
            var budget = new Dictionary<string, int>
            {
                ["nucleo"] = 16000, ["analista_datos"] = 44000, ["auditor"] = 56300, ["documentador"] = 58000,
                ["modelador_arquitectura"] = 52000, ["modelador_estructura"] = 52000, ["modelador_mep"] = 52000,
                ["coordinador"] = 74000, ["modelador"] = 97000, ["completo"] = 150000
            };
            WithRoot(Python, () =>
            {
                long full = Bytes(ToolVisibility.ListForProfile("completo", true));
                foreach (string p in ToolGroupCatalog.Profiles)
                {
                    long bytes = Bytes(ToolVisibility.ListForProfile(p, true));
                    long high = (long)Math.Round(bytes / 3.5);
                    _out.WriteLine(p + ": " + bytes + " B, ~" + Math.Round(bytes / 4.4) + "-" + high + " tokens, " +
                                   Math.Round(100.0 * (full - bytes) / full) + " % below completo");
                    Assert.True(high <= budget[p], p + " lists ~" + high + " tokens (high bound), over its budget of " + budget[p]);
                }
            });
        }

        // ---- visibility, never privilege ------------------------------------------------------

        [Fact]
        public void Activation_changes_no_admission_answer()
        {
            WithRoot(SafeWrite, () =>
            {
                Dictionary<string, string> before = Contract.All.ToDictionary(c => c.Name, c => Tools.DisabledReason(c.Name));
                ToolVisibility.Answer(new JObject { ["action"] = "profile", ["profile"] = "nucleo" });
                Assert.Equal(before, Contract.All.ToDictionary(c => c.Name, c => Tools.DisabledReason(c.Name)));
                ToolVisibility.Answer(new JObject { ["action"] = "activate", ["groups"] = new JArray(ToolGroupCatalog.OptionalGroups) });
                Assert.Equal(before, Contract.All.ToDictionary(c => c.Name, c => Tools.DisabledReason(c.Name)));
                // Python stays off under safe_write whatever is listed.
                Assert.NotNull(Tools.DisabledReason("horizun_execute_python"));
                Assert.DoesNotContain("horizun_execute_python", Names(SchemaFacade.List(SchemaSurface.Full, false)));
            });
        }

        [Fact]
        public void The_3x_toolsets_selection_is_a_ceiling_no_activation_crosses()
        {
            WithRoot(SafeWrite, () =>
            {
                ToolVisibility.Answer(new JObject { ["action"] = "activate", ["groups"] = new JArray("datos") });
                string[] listed = Names(SchemaFacade.List(SchemaSurface.Full, false));
                // read pack: list_schedules and get_schedule_data are in it, power_bi_push is not.
                Assert.Contains("horizun_list_schedules", listed);
                Assert.DoesNotContain("horizun_power_bi_push", listed);
                Assert.NotNull(Tools.DisabledReason("horizun_power_bi_push"));
                JObject reply = ToolVisibility.Answer(new JObject { ["action"] = "list" });
                JObject datos = reply["groups"].OfType<JObject>().First(g => (string)g["group"] == "datos");
                Assert.True((int)datos["reachable"] < (int)datos["tools"]);
                Assert.Equal("HORIZUN_TOOLSETS", (string)reply["ceiling"]["toolsets_source"]);
            }, toolsets: "read");
        }

        [Fact]
        public void A_hidden_tool_is_reached_through_call_tool_under_the_same_gates()
        {
            WithRoot(SafeWrite, () =>
            {
                ToolVisibility.Answer(new JObject { ["action"] = "profile", ["profile"] = "nucleo" });
                string[] listed = Names(SchemaFacade.List(SchemaSurface.Full, false));
                Assert.DoesNotContain("horizun_quantities", listed);
                Assert.Contains(SchemaFacade.CallToolName, listed);

                // The call is unwrapped into the direct call in FULL mode too, so Program dispatches
                // it through the same code: same name, same arguments, same gates.
                var call = new JObject
                {
                    ["name"] = SchemaFacade.CallToolName,
                    ["arguments"] = new JObject { ["name"] = "horizun_quantities", ["arguments"] = new JObject { ["x"] = 1 } },
                    ["_meta"] = new JObject { ["progressToken"] = "p" }
                };
                JObject inner = SchemaFacade.UnwrapCall(call, SchemaSurface.Full);
                Assert.Equal("horizun_quantities", (string)inner["name"]);
                Assert.Equal(1, (int)inner["arguments"]["x"]);
                Assert.Equal("p", (string)inner["_meta"]["progressToken"]);
                Assert.Null(Tools.DisabledReason("horizun_quantities"));             // hidden, not refused

                // A tool refused by the permission profile is refused whichever way it is called.
                Assert.NotNull(Tools.DisabledReason("horizun_power_bi_push"));
                Assert.True(SchemaFacade.IsMetaTool(ToolVisibility.ToolsetsToolName, SchemaSurface.Full));
                Assert.True(SchemaFacade.IsMetaTool(SchemaFacade.CallToolName, SchemaSurface.Full));

                JObject described = SchemaFacade.Describe(new JObject { ["name"] = "horizun_quantities" });
                Assert.Equal("datos", (string)described["group"]);
                Assert.False((bool)described["listed"]);
                Assert.True((bool)described["callable"]);

                // listed is what tools/list shows, permissions included: a core tool the
                // permission profile hides (Python on safe_write) is not listed though its
                // group is active.
                Assert.DoesNotContain("horizun_execute_python", listed);
                Assert.False((bool)SchemaFacade.Describe(new JObject { ["name"] = "horizun_execute_python" })["listed"]);
                Assert.True((bool)SchemaFacade.Describe(new JObject { ["name"] = "horizun_health" })["listed"]);
            });
        }

        // ---- list_changed ----------------------------------------------------------------------

        [Fact]
        public void Each_change_that_alters_the_list_emits_exactly_one_list_changed()
        {
            WithRoot(SafeWrite, () =>
            {
                int fired = 0;
                using (new ToolListMonitor((m, p) => { if (m == "notifications/tools/list_changed") fired++; }, watch: false))
                {
                    JObject r = ToolVisibility.Answer(new JObject { ["action"] = "profile", ["profile"] = "nucleo" });
                    Assert.Equal(1, fired);
                    Assert.True((bool)r["tools_list_changed"]);

                    r = ToolVisibility.Answer(new JObject { ["action"] = "activate", ["groups"] = new JArray("datos") });
                    Assert.Equal(2, fired);
                    JObject datos = r["groups"].OfType<JObject>().First(g => (string)g["group"] == "datos");
                    Assert.NotNull(datos["catalog"]["horizun_quantities"]);           // the one-line catalog

                    r = ToolVisibility.Answer(new JObject { ["action"] = "activate", ["groups"] = new JArray("data") });
                    Assert.Equal(2, fired);                                             // nothing new: no notification
                    Assert.False((bool)r["tools_list_changed"]);

                    ToolVisibility.Answer(new JObject { ["action"] = "deactivate", ["groups"] = new JArray("datos") });
                    Assert.Equal(3, fired);
                    ToolVisibility.Answer(new JObject { ["action"] = "reset" });
                    Assert.Equal(4, fired);                                             // back to completo
                    Assert.True(ToolVisibility.AllVisible());
                }
            });
        }

        [Fact]
        public void Toolsets_refuses_what_it_cannot_do_and_changes_nothing()
        {
            WithRoot(SafeWrite, () =>
            {
                Assert.Throws<ToolRefusal>(() => ToolVisibility.Answer(new JObject { ["action"] = "activate", ["groups"] = new JArray("nope") }));
                Assert.Throws<ToolRefusal>(() => ToolVisibility.Answer(new JObject { ["action"] = "deactivate", ["groups"] = new JArray("nucleo") }));
                Assert.Throws<ToolRefusal>(() => ToolVisibility.Answer(new JObject { ["action"] = "activate", ["groups"] = new JArray("auditor") }));
                Assert.Throws<ToolRefusal>(() => ToolVisibility.Answer(new JObject { ["action"] = "profile", ["profile"] = "nope" }));
                Assert.Throws<ToolRefusal>(() => ToolVisibility.Answer(new JObject { ["action"] = "fly" }));
                Assert.Throws<ToolRefusal>(() => ToolVisibility.Answer(new JObject { ["bogus"] = 1 }));
                Assert.True(ToolVisibility.AllVisible());
                string settings = File.ReadAllText(HorizunPaths.SettingsPath());
                ToolVisibility.Answer(new JObject { ["action"] = "profile", ["profile"] = "auditor" });
                Assert.Equal(settings, File.ReadAllText(HorizunPaths.SettingsPath()));  // never writes settings
            });
        }

        // ---- health --------------------------------------------------------------------------

        [Fact]
        public void Health_reports_profile_groups_activations_ceiling_and_list_size()
        {
            WithRoot("{\"permission_profile\":\"safe_write\",\"tool_profile\":\"auditor\"}", () =>
            {
                ToolVisibility.Answer(new JObject { ["action"] = "activate", ["groups"] = new JArray("datos") });
                JObject h = ToolVisibility.HealthBlock();
                Assert.Null(h["error"]);
                Assert.Equal("auditor", (string)h["profile"]);
                Assert.Equal("settings", (string)h["profile_source"]);
                Assert.Equal(new[] { "nucleo", "lectura", "documentacion", "auditoria", "datos" }, h["active_groups"].Select(t => (string)t));
                Assert.Contains("modelado", h["inactive_groups"].Select(t => (string)t));
                Assert.Equal("activate", (string)h["session_activations"][0]["action"]);
                Assert.Equal("safe_write", (string)h["ceiling"]["permission_profile"]);
                Assert.False((bool)h["ceiling"]["toolsets_restricting"]);
                JArray listed = SchemaFacade.List(SchemaSurface.Full, false);
                Assert.Equal(listed.Count, (int)h["tools_list"]["tools"]);
                Assert.True((long)h["tools_list"]["approx_tokens"] > 0);
                Assert.True((long)h["tools_list"]["estimated_tokens_range"]["high"] > (long)h["tools_list"]["estimated_tokens_range"]["low"]);
                Assert.False((bool)h["compact_schemas"]);
            });
        }

        // ---- where the profile comes from: upgrade vs new install ------------------------------

        [Fact]
        public void An_upgrade_without_tool_profile_keeps_the_3x_list_byte_for_byte()
        {
            WithRoot(SafeWrite, () =>
            {
                ToolVisibility.Initial i = ToolVisibility.ResolveInitial();
                Assert.Equal("completo", i.Profile);
                Assert.Equal("default", i.Source);
                Assert.True(ToolVisibility.AllVisible());
                Assert.True(JToken.DeepEquals(Tools.List(true), SchemaFacade.List(SchemaSurface.Full, true)));
            });
        }

        [Fact]
        public void A_new_install_tool_profile_lists_core_plus_that_profile()
        {
            WithRoot("{\"permission_profile\":\"safe_write\",\"tool_profile\":\"documentador\"}", () =>
            {
                string[] names = Names(SchemaFacade.List(SchemaSurface.Full, false));
                Assert.Contains("horizun_manage_views", names);    // documentacion
                Assert.Contains("horizun_quantities", names);      // datos
                Assert.Contains("horizun_model_scan", names);      // lectura
                Assert.Contains("horizun_query_model", names);     // core
                Assert.DoesNotContain("horizun_create_elements", names);
                Assert.Equal(new[] { "horizun_toolsets", "horizun_describe_tool", "horizun_call_tool" }, names.Skip(names.Length - 3));
            });
        }

        [Fact]
        public void Command_line_beats_environment_beats_settings_and_garbage_lists_everything()
        {
            WithRoot("{\"tool_profile\":\"auditor\"}", () =>
            {
                string savedEnv = Environment.GetEnvironmentVariable(ToolVisibility.ProfileEnvironmentVariable);
                try
                {
                    Assert.Equal("auditor", ToolVisibility.ResolveInitial().Profile);
                    Environment.SetEnvironmentVariable(ToolVisibility.ProfileEnvironmentVariable, "coordinador");
                    Assert.Equal("coordinador", ToolVisibility.ResolveInitial().Profile);
                    Assert.Equal("environment", ToolVisibility.ResolveInitial().Source);
                    ToolVisibility.CommandLineProfile = "nucleo";
                    Assert.Equal("nucleo", ToolVisibility.ResolveInitial().Profile);
                    Assert.Equal("command_line", ToolVisibility.ResolveInitial().Source);
                    ToolVisibility.CommandLineProfile = null;

                    // HORIZUN_PROFILE=local is still the local schema surface, not a group profile.
                    Environment.SetEnvironmentVariable(ToolVisibility.ProfileEnvironmentVariable, "local");
                    Assert.Equal("auditor", ToolVisibility.ResolveInitial().Profile);

                    // A list of groups is a custom profile; garbage lists everything and says why.
                    Environment.SetEnvironmentVariable(ToolVisibility.ProfileEnvironmentVariable, "datos, documentation");
                    ToolVisibility.Initial custom = ToolVisibility.ResolveInitial();
                    Assert.Equal("custom", custom.Profile);
                    Assert.Equal(new[] { "datos", "documentacion" }, custom.Groups);
                    Environment.SetEnvironmentVariable(ToolVisibility.ProfileEnvironmentVariable, "zzz");
                    ToolVisibility.Initial bad = ToolVisibility.ResolveInitial();
                    Assert.Equal("completo", bad.Profile);
                    Assert.Contains("zzz", bad.Problem);
                }
                finally
                {
                    ToolVisibility.CommandLineProfile = null;
                    Environment.SetEnvironmentVariable(ToolVisibility.ProfileEnvironmentVariable, savedEnv);
                }
            });
        }

        // ---- compact announcement (switch, default off) ----------------------------------------

        [Fact]
        public void Compact_announcement_is_off_by_default_and_shrinks_only_the_five_when_on()
        {
            string saved = Environment.GetEnvironmentVariable(CompactAnnouncement.EnvironmentVariable);
            try
            {
                Environment.SetEnvironmentVariable(CompactAnnouncement.EnvironmentVariable, null);
                Assert.False(CompactAnnouncement.Enabled);
                Dictionary<string, int> off = Tools.All.ToDictionary(t => t.Name, t => (int)Bytes(Tools.Publish(t, true)));

                Environment.SetEnvironmentVariable(CompactAnnouncement.EnvironmentVariable, "1");
                Assert.True(CompactAnnouncement.Enabled);
                long savedBytes = 0;
                foreach (ToolDef t in Tools.All)
                {
                    int on = (int)Bytes(Tools.Publish(t, true));
                    if (CompactAnnouncement.Heaviest.Contains(t.Name))
                    {
                        _out.WriteLine(t.Name + ": " + off[t.Name] + " -> " + on + " B");
                        Assert.True(on < off[t.Name] * 0.7, t.Name + " did not shrink enough: " + off[t.Name] + " -> " + on);
                        savedBytes += off[t.Name] - on;
                    }
                    else Assert.Equal(off[t.Name], on);
                }
                _out.WriteLine("saved " + savedBytes + " B (~" + Math.Round(savedBytes / 3.5) + " tokens high bound)");

                // The variant values are announced; the exact schema is one describe away and is what validates.
                JObject ce = Tools.Publish(Tools.Find("horizun_create_elements"), false);
                string text = ce["inputSchema"].ToString(Formatting.None);
                Assert.Contains("horizun_describe_tool", text);
                foreach (VariantSite site in ContractVariants.For("horizun_create_elements"))
                    foreach (string v in site.Values) Assert.Contains("\"" + v + "\"", text);
                Assert.True(JToken.DeepEquals(Contract.Find("horizun_create_elements").InputSchema,
                    SchemaFacade.Describe(new JObject { ["name"] = "horizun_create_elements" })["input_schema"]));
            }
            finally
            {
                Environment.SetEnvironmentVariable(CompactAnnouncement.EnvironmentVariable, saved);
            }
        }

        // ---- helpers ---------------------------------------------------------------------------

        internal static string[] Names(JArray list) => list.OfType<JObject>().Select(t => (string)t["name"]).ToArray();

        internal static long Bytes(JToken t) => Encoding.UTF8.GetByteCount(t.ToString(Formatting.None));

        private static void WithRoot(string settingsJson, Action action, string toolsets = null)
        {
            string savedEnvProfile = Environment.GetEnvironmentVariable(ToolVisibility.ProfileEnvironmentVariable);
            try
            {
                Environment.SetEnvironmentVariable(ToolVisibility.ProfileEnvironmentVariable, null);
                ToolVisibility.CommandLineProfile = null;
                ToolVisibility.ResetSession();
                SchemaFacadeTests.WithDataRoot(settingsJson, toolsets, action);
            }
            finally
            {
                ToolVisibility.ResetSession();
                ToolVisibility.CommandLineProfile = null;
                Environment.SetEnvironmentVariable(ToolVisibility.ProfileEnvironmentVariable, savedEnvProfile);
            }
        }
    }
}
