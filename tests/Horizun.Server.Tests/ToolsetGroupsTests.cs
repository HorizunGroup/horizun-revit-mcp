// -----------------------------------------------------------------------------
// Horizun Server tests - original Horizun code.
//
// TOOLSET GROUPS FOR SMALL MODELS (product-owner decisions, 2026-10). Proved from the outside:
//   1. local is ten tools, and when it is the WHOLE selection the core tools target /
//      job_status / submit_job are neither advertised nor callable (health stays);
//      with any other toolset added, or with no restriction, the core is welded as before;
//   2. horizun_activate_toolset widens the selection for THIS server process only, never
//      past the permission profile / denied_tools / allowed_tools, and says what opened
//      and what stayed blocked and why;
//   3. the Spanish aliases select exactly what their English toolset selects, ignoring
//      case and accents, and are never toolsets themselves;
//   4. nothing the agent is told to do points at a tool that is hidden in that session.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Horizun.Contracts;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Server.Tests
{
    [Collection("HorizunSettingsRoot")]
    public class ToolsetGroupsTests
    {
        private const string Activate = "horizun_activate_toolset";

        private static readonly string[] LocalTen =
        {
            "horizun_health", "get_document_info", "horizun_list_elements", "horizun_query_model",
            "horizun_capture_view", "horizun_create_elements", "horizun_write_params_verified",
            "horizun_delete_verified", "horizun_transform_elements", "horizun_undo"
        };

        private static readonly string[] CoreHidden = { "horizun_target", "horizun_job_status", "horizun_submit_job" };

        private static string[] Names(JArray list) => list.Select(t => (string)t["name"]).ToArray();

        // ---- 1. local = ten, and the core exception ------------------------------------

        [Fact]
        public void Local_is_exactly_ten_tools_and_the_five_removed_stay_in_their_other_toolsets()
        {
            Assert.Equal(LocalTen.OrderBy(n => n, StringComparer.Ordinal),
                         ToolPacks.MembersOf("local").OrderBy(n => n, StringComparer.Ordinal));
            foreach (string gone in new[] { "horizun_model_scan", "horizun_model_diff", "horizun_quantities",
                                            "horizun_navigate", "horizun_manage_views" })
            {
                Assert.DoesNotContain(gone, ToolPacks.MembersOf("local"));
                Assert.NotEmpty(Contract.Find(gone).Toolsets);
            }
            Assert.Contains("horizun_model_scan", ToolPacks.MembersOf("read"));
            Assert.Contains("horizun_model_scan", ToolPacks.MembersOf("audit"));
            Assert.Contains("horizun_model_diff", ToolPacks.MembersOf("audit"));
            Assert.Contains("horizun_quantities", ToolPacks.MembersOf("read"));
            Assert.Contains("horizun_navigate", ToolPacks.MembersOf("read"));
            Assert.Contains("horizun_manage_views", ToolPacks.MembersOf("documentation"));
        }

        [Fact]
        public void Local_alone_advertises_ten_without_target_job_status_and_submit_job()
        {
            using (Session.With("{\"permission_profile\":\"full_write\"}", toolsets: "local"))
            {
                Assert.Equal(LocalTen.OrderBy(n => n, StringComparer.Ordinal),
                             Names(Tools.List(false)).OrderBy(n => n, StringComparer.Ordinal));
                foreach (string hidden in CoreHidden)
                {
                    string why = Tools.DisabledReason(hidden);
                    Assert.NotNull(why);                      // HIDDEN MEANS UNREACHABLE
                    Assert.Contains("local", why);
                }
                Assert.Null(Tools.DisabledReason("horizun_health"));
                Assert.True(ToolPacks.Resolve("local", null, false).Tools().SetEquals(LocalTen));
            }
        }

        [Fact]
        public void Local_with_any_other_toolset_or_no_restriction_keeps_the_core_welded()
        {
            foreach (string toolsets in new[] { "local,documentation", "local,lectura", null })
                using (Session.With("{\"permission_profile\":\"full_write\"}", toolsets: toolsets))
                {
                    var names = Names(Tools.List(false));
                    foreach (string core in ToolPacks.MembersOf("core")) Assert.Contains(core, names);
                }
            Assert.Contains("horizun_target", ToolPacks.Resolve("local,read", null, false).Tools());
        }

        [Fact]
        public void The_local_exception_does_not_touch_core_alone_or_the_malformed_fallback()
        {
            Assert.True(ToolPacks.Resolve("core", null, false).Tools().SetEquals(ToolPacks.MembersOf("core")));
            ToolPacks.Resolution bad = ToolPacks.Resolve("local,plumbing", null, false);
            Assert.Equal(ToolPacks.SelectionSource.Malformed, bad.Source);
            Assert.True(bad.Tools().SetEquals(ToolPacks.MembersOf("core")));
        }

        [Fact]
        public void Unrestricted_full_mode_tools_list_is_what_it_always_was()
        {
            using (Session.With("{\"permission_profile\":\"full_write\"}", toolsets: null))
            {
                JArray today = Tools.List(true);
                JArray served = SchemaFacade.List(SchemaSurface.Full, true);
                Assert.True(JToken.DeepEquals(today, served));
                Assert.DoesNotContain(Activate, Names(served));
                Assert.Equal(4, SchemaFacade.List(SchemaSurface.Facade, true).Count);
            }
        }

        [Fact]
        public void Health_shows_core_not_active_when_local_is_alone()
        {
            using (Session.With(null, toolsets: "local"))
            {
                JObject h = ToolsetReport.HealthBlock();
                Assert.DoesNotContain("core", h["active"].Select(x => (string)x));
                Assert.Contains("core", h["toolsets_not_active"].Select(x => (string)x));
                Assert.Contains("local", h["active"].Select(x => (string)x));
                Assert.Empty((JArray)h["activated_this_session"]);
            }
        }

        // ---- 2. horizun_activate_toolset -----------------------------------------------

        [Fact]
        public void Activate_is_advertised_only_while_a_restriction_is_active_and_is_not_a_contract_row()
        {
            Assert.Null(Contract.Find(Activate));
            using (Session.With(null, toolsets: "local"))
            {
                JArray full = SchemaFacade.List(SchemaSurface.Full, true);
                Assert.Equal(Activate, Names(full).Last());
                JToken def = full.Last;
                Assert.Equal("toolset", (string)def["inputSchema"]["required"][0]);
                Assert.Equal("string", (string)def["inputSchema"]["properties"]["toolset"]["type"]);
                Assert.Equal(Names(Tools.List(true)), Names(full).Take(full.Count - 1));

                JArray facade = SchemaFacade.List(SchemaSurface.Facade, true);
                Assert.Equal(new[] { "horizun_health", SchemaFacade.ListToolsName, SchemaFacade.DescribeToolName,
                                     SchemaFacade.CallToolName, Activate }, Names(facade));
            }
            using (Session.With(null, toolsets: null))
                Assert.DoesNotContain(Activate, Names(SchemaFacade.List(SchemaSurface.Full, true)));
        }

        [Fact]
        public void Activating_widens_this_process_only_and_reports_what_opened_and_what_stayed_blocked()
        {
            using (Session.With("{\"permission_profile\":\"safe_write\"}", toolsets: "local"))
            {
                string settingsBefore = File.ReadAllText(Path.Combine(Session.Dir, "settings.json"));
                string envBefore = Environment.GetEnvironmentVariable(ToolPacks.ToolsetsEnvironmentVariable);
                Assert.DoesNotContain("horizun_manage_views", Names(Tools.List(false)));

                JObject r = SchemaFacade.Answer(Activate, new JObject { ["toolset"] = "documentation" });

                var after = Names(Tools.List(false));
                Assert.Contains("horizun_manage_views", after);          // documentation opened
                Assert.Contains("horizun_target", after);                // local is no longer alone: core is welded
                Assert.Contains("horizun_list_elements", after);
                Assert.Contains("horizun_manage_views", r["newly_available"].Select(x => (string)x));
                Assert.DoesNotContain("horizun_health", r["newly_available"].Select(x => (string)x));
                Assert.Equal("documentation", (string)r["toolset"]);
                Assert.Contains("read", r["activated"].Select(x => (string)x));
                Assert.Contains("documentation", r["activated"].Select(x => (string)x));

                // Persistence: nothing written anywhere.
                Assert.Equal(settingsBefore, File.ReadAllText(Path.Combine(Session.Dir, "settings.json")));
                Assert.Equal(envBefore, Environment.GetEnvironmentVariable(ToolPacks.ToolsetsEnvironmentVariable));

                // Health distinguishes the owner's selection from the session's.
                JObject h = ToolsetReport.HealthBlock();
                Assert.Equal(new[] { "local" }, h["chosen"].Select(x => (string)x).ToArray());
                Assert.Contains("documentation", h["activated_this_session"].Select(x => (string)x));

                // "Restart": the session is gone.
                ToolPacks.ResetSessionForTests();
                Assert.DoesNotContain("horizun_manage_views", Names(Tools.List(false)));
                Assert.Empty((JArray)ToolsetReport.HealthBlock()["activated_this_session"]);
            }
        }

        [Fact]
        public void Activating_never_opens_what_the_profile_or_the_lists_switch_off_and_says_why()
        {
            using (Session.With("{\"permission_profile\":\"safe_write\",\"denied_tools\":[\"horizun_delete_verified\"]}",
                                toolsets: "local"))
            {
                JObject r = SchemaFacade.Answer(Activate, new JObject { ["toolset"] = "administration" });
                Assert.DoesNotContain("horizun_save_document", Names(Tools.List(false)));
                JObject blocked = (JObject)((JArray)r["still_blocked"])
                    .First(b => (string)b["tool"] == "horizun_save_document");
                Assert.Contains("safe_write", (string)blocked["reason"]);

                JObject r2 = SchemaFacade.Answer(Activate, new JObject { ["toolset"] = "modelado" });
                Assert.DoesNotContain("horizun_delete_verified", Names(Tools.List(false)));
                JObject denied = (JObject)((JArray)r2["still_blocked"])
                    .First(b => (string)b["tool"] == "horizun_delete_verified");
                Assert.Contains("denied_tools", (string)denied["reason"]);
            }
            using (Session.With("{\"permission_profile\":\"full_write\",\"allowed_tools\":[\"horizun_health\",\"get_document_info\"]}",
                                toolsets: "local"))
            {
                JObject r = SchemaFacade.Answer(Activate, new JObject { ["toolset"] = "read" });
                Assert.Equal(new[] { "get_document_info", "horizun_health" },
                             Names(Tools.List(false)).OrderBy(n => n, StringComparer.Ordinal));
                Assert.Contains(((JArray)r["still_blocked"]).OfType<JObject>(),
                    b => (string)b["tool"] == "horizun_query_model" && ((string)b["reason"]).Contains("allowed_tools"));
            }
        }

        [Fact]
        public void Activate_accepts_aliases_and_refuses_unknown_names_and_bad_arguments()
        {
            using (Session.With("{\"permission_profile\":\"full_write\"}", toolsets: "local"))
            {
                JObject r = SchemaFacade.Answer(Activate, new JObject { ["toolset"] = "Documentación" });
                Assert.Equal("documentation", (string)r["toolset"]);
                Assert.Throws<ToolRefusal>(() => SchemaFacade.Answer(Activate, new JObject { ["toolset"] = "plumbing" }));
                Assert.Throws<ToolRefusal>(() => SchemaFacade.Answer(Activate, new JObject()));
                Assert.Throws<ToolRefusal>(() => SchemaFacade.Answer(Activate, new JObject { ["toolset"] = 3 }));
                Assert.Throws<ToolRefusal>(() => SchemaFacade.Answer(Activate,
                    new JObject { ["toolset"] = "read", ["extra"] = 1 }));
            }
        }

        [Fact]
        public void Activate_refuses_when_the_owner_settings_file_decides_because_the_addin_would_still_refuse()
        {
            using (Session.With("{\"permission_profile\":\"full_write\",\"toolsets\":[\"local\"]}", toolsets: null))
            {
                Assert.DoesNotContain(Activate, Names(SchemaFacade.List(SchemaSurface.Full, true)));
                var ex = Assert.Throws<ToolRefusal>(() => SchemaFacade.Answer(Activate, new JObject { ["toolset"] = "read" }));
                Assert.Contains("settings", ex.Message);
                Assert.Empty(ToolPacks.SessionActivatedForTests());
            }
        }

        [Fact]
        public void Activate_without_a_restriction_changes_nothing_and_says_so()
        {
            using (Session.With(null, toolsets: null))
            {
                JObject r = SchemaFacade.Answer(Activate, new JObject { ["toolset"] = "read" });
                Assert.False((bool)r["restricting"]);
                Assert.Empty(ToolPacks.SessionActivatedForTests());
            }
        }

        [Fact]
        public void Activating_asks_the_server_to_announce_tools_list_changed()
        {
            int calls = 0;
            Action old = ToolsetActivation.AfterActivation;
            using (Session.With("{\"permission_profile\":\"full_write\"}", toolsets: "local"))
            {
                try
                {
                    ToolsetActivation.AfterActivation = () => calls++;
                    SchemaFacade.Answer(Activate, new JObject { ["toolset"] = "documentation" });
                    Assert.Equal(1, calls);
                }
                finally { ToolsetActivation.AfterActivation = old; }
            }

            // And the monitor that does the announcing sees an activation as a change.
            using (Session.With("{\"permission_profile\":\"full_write\"}", toolsets: "local"))
            {
                var notices = new List<string>();
                using (var monitor = new ToolListMonitor((m, p) => notices.Add(m), watch: false))
                {
                    SchemaFacade.Answer(Activate, new JObject { ["toolset"] = "documentation" });
                    monitor.CheckNow();
                }
                Assert.Equal(new[] { "notifications/tools/list_changed" }, notices.ToArray());
            }
        }

        [Fact]
        public void A_search_tells_the_agent_how_to_activate_what_a_toolset_hides()
        {
            using (Session.With("{\"permission_profile\":\"full_write\"}", toolsets: "local"))
            {
                JObject r = SchemaFacade.ListTools(new JObject { ["query"] = "manage views create sheet" });
                var off = (JArray)r["matches_not_active"];
                Assert.NotEmpty(off);
                foreach (JObject m in off)
                {
                    string how = (string)m["activate"];
                    Assert.Contains(Activate, how);
                    string toolset = (string)m["enable_toolsets"][0];
                    Assert.Contains(toolset, how);
                }
            }
            using (Session.With("{\"permission_profile\":\"full_write\",\"toolsets\":[\"local\"]}", toolsets: null))
            {
                JObject r = SchemaFacade.ListTools(new JObject { ["query"] = "manage views create sheet" });
                foreach (JObject m in (JArray)r["matches_not_active"]) Assert.Null(m["activate"]);
            }
        }

        // ---- 3. Spanish aliases ----------------------------------------------------------

        private static readonly Dictionary<string, string> SpanishAliases = new Dictionary<string, string>
        {
            ["consulta"] = "read", ["lectura"] = "read", ["modelado"] = "model", ["modelo"] = "model",
            ["arquitectura"] = "architecture", ["estructura"] = "structure", ["documentacion"] = "documentation",
            ["planimetria"] = "planimetry", ["auditoria"] = "audit", ["coordinacion"] = "coordination",
            ["tablas"] = "schedules", ["familia"] = "family", ["interoperabilidad"] = "interoperability",
            ["administracion"] = "administration"
        };

        [Fact]
        public void Spanish_aliases_select_exactly_the_toolset_they_name_ignoring_case_and_accents()
        {
            foreach (var pair in SpanishAliases)
            {
                var viaAlias = ToolPacks.Resolve(pair.Key, null, false);
                var viaName = ToolPacks.Resolve(pair.Value, null, false);
                Assert.True(viaAlias.Problem == null, pair.Key + ": " + viaAlias.Problem);
                Assert.Equal(viaName.ActivePacks, viaAlias.ActivePacks);
                Assert.True(viaAlias.Tools().SetEquals(viaName.Tools()));
            }
            foreach (string spelled in new[] { "Documentación", "DOCUMENTACION", " documentación ", "documentacion" })
                Assert.Equal(ToolPacks.Resolve("documentation", null, false).ActivePacks,
                             ToolPacks.Resolve(spelled, null, false).ActivePacks);
            Assert.Equal(ToolPacks.Resolve("planimetry", null, false).ActivePacks,
                         ToolPacks.Resolve("Planimetría", null, false).ActivePacks);
            Assert.Equal(ToolPacks.Resolve("interoperability", null, false).ActivePacks,
                         ToolPacks.Resolve(null, new[] { "Interoperabilidad" }, false).ActivePacks);
            Assert.Equal(new[] { "local" }, ToolPacks.Resolve("LOCAL", null, false).ActivePacks.ToArray());
        }

        [Fact]
        public void Aliases_are_not_toolsets_and_cannot_widen_a_membership()
        {
            foreach (string alias in SpanishAliases.Keys)
            {
                Assert.DoesNotContain(alias, ToolPacks.KnownPacks);
                Assert.DoesNotContain(alias, Contract.All.SelectMany(c => c.Toolsets));
                Assert.Equal(new[] { SpanishAliases[alias] }, ToolsetCatalog.Aliases[alias]);
            }
            Assert.Empty(ToolsetCatalog.Audit(Contract.All.Select(c => c.Name)));
            Assert.Equal(new[] { "family" }, ToolsetCatalog.Aliases["families"]);
            Assert.Equal(new[] { "schedules", "interoperability", "powerbi" }, ToolsetCatalog.Aliases["data"]);
        }

        [Fact]
        public void Aliases_work_in_every_spelling_of_the_selection_and_in_list_tools()
        {
            string[] viaEnglish;
            using (Session.With("{\"permission_profile\":\"full_write\"}", toolsets: "documentation"))
                viaEnglish = Names(Tools.List(false));
            using (Session.With("{\"permission_profile\":\"full_write\"}", toolsets: "Documentación"))
                Assert.Equal(viaEnglish, Names(Tools.List(false)));
            using (Session.With("{\"permission_profile\":\"full_write\",\"tool_packs\":[\"Lectura\"]}", toolsets: null))
                Assert.Equal(ToolPacks.Resolve("read", null, false).Tools().OrderBy(n => n, StringComparer.Ordinal),
                             Settings.ActivePackResolution().Tools().OrderBy(n => n, StringComparer.Ordinal));
            using (Session.With("{\"permission_profile\":\"full_write\"}", toolsets: null))
            {
                JObject viaAlias = SchemaFacade.ListTools(new JObject { ["toolset"] = "Documentación" });
                JObject viaName = SchemaFacade.ListTools(new JObject { ["toolset"] = "documentation" });
                Assert.True(JToken.DeepEquals(viaName["tools"], viaAlias["tools"]));
                Assert.Equal("documentation", (string)viaAlias["toolset"]);
                Assert.Throws<ToolRefusal>(() => SchemaFacade.ListTools(new JObject { ["toolset"] = "fontaneria" }));
            }
        }

        // ---- 4. nothing sends the agent to a hidden tool -------------------------------------

        [Fact]
        public void Retry_advice_does_not_name_submit_job_when_it_is_not_callable()
        {
            string v = CancellationAdvice.SameKeyReplays;
            string with = CancellationAdvice.Sentence(v, 3, CancellationAdvice.LongWaitMs, submitJobAvailable: true);
            Assert.Contains("horizun_submit_job", with);
            string without = CancellationAdvice.Sentence(v, 3, CancellationAdvice.LongWaitMs, submitJobAvailable: false);
            Assert.DoesNotContain("submit_job", without);
            Assert.DoesNotContain("job_status", without);
            Assert.Null(CancellationAdvice.ToJson(v, true, 3, CancellationAdvice.LongWaitMs, submitJobAvailable: false)["prefer"]);
            Assert.Equal("horizun_submit_job",
                (string)CancellationAdvice.ToJson(v, true, 3, CancellationAdvice.LongWaitMs, submitJobAvailable: true)["prefer"]);
        }

        [Fact]
        public void Task_augmentation_is_not_offered_when_the_queue_behind_it_is_hidden()
        {
            using (Session.With("{\"permission_profile\":\"full_write\"}", toolsets: "local"))
            {
                Assert.False(McpTasks.SupportsHere(Tools.Find("horizun_create_elements")));
                JToken published = Tools.List(true).First(t => (string)t["name"] == "horizun_create_elements");
                Assert.Equal("forbidden", (string)published["execution"]["taskSupport"]);
            }
            using (Session.With("{\"permission_profile\":\"full_write\"}", toolsets: "local,documentation"))
                Assert.True(McpTasks.SupportsHere(Tools.Find("horizun_create_elements")));
            using (Session.With("{\"permission_profile\":\"full_write\"}", toolsets: null))
                Assert.True(McpTasks.SupportsHere(Tools.Find("horizun_create_elements")));
        }

        [Fact]
        public void Routing_refusals_do_not_send_the_agent_to_a_hidden_horizun_target()
        {
            using (Session.With("{\"permission_profile\":\"full_write\"}", toolsets: "local"))
            {
                string hint = PipeClient.TargetAdvice();
                Assert.DoesNotContain("horizun_target", hint);
                Assert.Contains("HORIZUN_REVIT_YEAR", hint);
            }
            using (Session.With("{\"permission_profile\":\"full_write\"}", toolsets: null))
                Assert.Contains("horizun_target", PipeClient.TargetAdvice());
        }

        [Fact]
        public void The_health_first_prompt_does_not_name_a_hidden_horizun_target()
        {
            using (Session.With("{\"permission_profile\":\"full_write\"}", toolsets: "local"))
            {
                string text = McpPrompts.Get(new JObject { ["name"] = "health-first" }).ToString();
                Assert.DoesNotContain("horizun_target", text);
            }
            using (Session.With("{\"permission_profile\":\"full_write\"}", toolsets: null))
                Assert.Contains("horizun_target", McpPrompts.Get(new JObject { ["name"] = "health-first" }).ToString());
        }

        // ---- plumbing ------------------------------------------------------------------------------

        private sealed class Session : IDisposable
        {
            public static string Dir;
            private readonly string _root, _packs, _sets;

            private Session(string settings, string toolsets)
            {
                _root = Environment.GetEnvironmentVariable("HORIZUN_DATA_ROOT");
                _packs = Environment.GetEnvironmentVariable(ToolPacks.EnvironmentOverride);
                _sets = Environment.GetEnvironmentVariable(ToolPacks.ToolsetsEnvironmentVariable);
                Dir = Path.Combine(Path.GetTempPath(), "hz-groups-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(Dir);
                Environment.SetEnvironmentVariable("HORIZUN_DATA_ROOT", Dir);
                Environment.SetEnvironmentVariable(ToolPacks.EnvironmentOverride, null);
                Environment.SetEnvironmentVariable(ToolPacks.ToolsetsEnvironmentVariable, toolsets);
                File.WriteAllText(Path.Combine(Dir, "settings.json"), settings ?? "{}");
                ToolPacks.ResetSessionForTests();
            }

            public static Session With(string settings, string toolsets) => new Session(settings, toolsets);

            public void Dispose()
            {
                ToolPacks.ResetSessionForTests();
                Environment.SetEnvironmentVariable("HORIZUN_DATA_ROOT", _root);
                Environment.SetEnvironmentVariable(ToolPacks.EnvironmentOverride, _packs);
                Environment.SetEnvironmentVariable(ToolPacks.ToolsetsEnvironmentVariable, _sets);
                try { Directory.Delete(Dir, true); } catch { }
            }
        }
    }
}
