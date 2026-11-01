// -----------------------------------------------------------------------------
// Horizun Server tests - original Horizun code.
//
// TOOL PROFILES (product owner, 2026-10-31): tools/list shows the tools of a ROLE,
// not all 113, while every reachable tool stays callable. Proved from the outside:
//   1. no profile is the list as it always was, byte for byte (upgrades keep it);
//   2. a profile (settings "tool_profile", or HORIZUN_PROFILE in the client, which
//      wins) lists its toolsets' tools plus list / describe / call / activate;
//   3. it is VISIBILITY ONLY: a tool it does not list is still called by name or
//      through horizun_call_tool, under the same gates - and a tool the permission
//      profile or HORIZUN_TOOLSETS refuses stays refused;
//   4. horizun_activate_toolset shows one more toolset for this session;
//   5. an unknown profile lists everything and says why (visibility never loses a
//      capability); names accept Spanish or English, any case, with or without accents.
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
    public class ToolProfilesTests
    {
        private readonly ITestOutputHelper _out;
        public ToolProfilesTests(ITestOutputHelper output) { _out = output; }

        private const string SafeWrite = "{\"permission_profile\":\"safe_write\"}";
        private static readonly string[] Meta =
            { "horizun_list_tools", "horizun_describe_tool", "horizun_call_tool", "horizun_activate_toolset" };

        private static string[] Names(JArray list) => list.Select(t => (string)t["name"]).ToArray();
        private static string Settings(string profile) =>
            "{\"permission_profile\":\"safe_write\",\"tool_profile\":\"" + profile + "\"}";

        private static void With(string settings, string profileEnv, Action action, string mode = null, string toolsets = null)
        {
            string saved = Environment.GetEnvironmentVariable(ToolProfiles.EnvironmentVariable);
            try
            {
                Environment.SetEnvironmentVariable(ToolProfiles.EnvironmentVariable, profileEnv);
                ToolProfiles.ResetSessionForTests();
                ToolPacks.ResetSessionForTests();
                SchemaFacadeTests.WithDataRoot(settings, toolsets, action, mode);
            }
            finally
            {
                ToolProfiles.ResetSessionForTests();
                ToolPacks.ResetSessionForTests();
                Environment.SetEnvironmentVariable(ToolProfiles.EnvironmentVariable, saved);
            }
        }

        // ---- 1. no profile: nothing changes ----------------------------------------------

        [Fact]
        public void Without_a_profile_the_list_is_the_unfiltered_list_byte_for_byte()
        {
            With(SafeWrite, null, () =>
            {
                Assert.False(ToolProfiles.Current().Restricting);
                Assert.Equal(Tools.List(true).ToString(Formatting.None),
                             SchemaFacade.List(SchemaSurface.Full, true).ToString(Formatting.None));
            });
        }

        [Fact]
        public void Completo_and_its_aliases_are_the_unfiltered_list()
        {
            foreach (string name in new[] { "completo", "full", "Complete", "COMPLETO" })
                With(Settings(name), null, () =>
                {
                    Assert.False(ToolProfiles.Current().Restricting, name);
                    Assert.Equal(Names(Tools.List(false)), Names(SchemaFacade.List(SchemaSurface.Full, false)));
                });
        }

        // ---- 2. a profile lists its role plus the four meta tools -------------------------

        [Fact]
        public void Esencial_lists_the_local_tools_and_the_four_meta_tools()
        {
            With(Settings("esencial"), null, () =>
            {
                string[] listed = Names(SchemaFacade.List(SchemaSurface.Full, false));
                var expected = ToolPacks.MembersOf("local")
                    .Where(n => Tools.IsCallable(n))          // safe_write hides capture_view
                    .Concat(Meta).OrderBy(n => n, StringComparer.Ordinal);
                Assert.Equal(expected, listed.OrderBy(n => n, StringComparer.Ordinal));
                Assert.Equal(listed.Length, listed.Distinct().Count());
            });
        }

        [Fact]
        public void Each_role_profile_lists_its_toolsets_and_not_the_others()
        {
            With(Settings("modelado"), null, () =>
            {
                string[] listed = Names(SchemaFacade.List(SchemaSurface.Full, false));
                Assert.Contains("horizun_create_elements", listed);
                Assert.Contains("horizun_create_railing", listed);          // architecture
                Assert.DoesNotContain("horizun_manage_views", listed);      // documentation
                Assert.DoesNotContain("horizun_clash", listed);             // coordination
            });
            With(Settings("documentacion"), null, () =>
            {
                string[] listed = Names(SchemaFacade.List(SchemaSurface.Full, false));
                Assert.Contains("horizun_manage_views", listed);
                Assert.DoesNotContain("horizun_create_railing", listed);
            });
            With(Settings("auditoria"), null, () =>
            {
                string[] listed = Names(SchemaFacade.List(SchemaSurface.Full, false));
                Assert.Contains("horizun_audit_model", listed);
                Assert.Contains("horizun_clash", listed);
                Assert.DoesNotContain("horizun_create_railing", listed);
            });
            With(Settings("consulta"), null, () =>
            {
                string[] listed = Names(SchemaFacade.List(SchemaSurface.Full, false));
                Assert.Contains("horizun_quantities", listed);
                Assert.DoesNotContain("horizun_create_railing", listed);
            });
        }

        [Fact]
        public void Every_profile_is_smaller_than_the_full_list_and_keeps_health()
        {
            int full;
            JArray all = null;
            With(SafeWrite, null, () => all = SchemaFacade.List(SchemaSurface.Full, false));
            full = all.ToString(Formatting.None).Length;
            foreach (string profile in ToolProfiles.RoleProfiles)
                With(Settings(profile), null, () =>
                {
                    JArray list = SchemaFacade.List(SchemaSurface.Full, false);
                    int size = list.ToString(Formatting.None).Length;
                    _out.WriteLine(profile + ": " + list.Count + " tools, " + size + " characters (full " + full + ")");
                    Assert.True(size < full, profile);
                    Assert.Contains("horizun_health", Names(list));
                });
        }

        [Fact]
        public void Names_accept_spanish_english_case_and_accents_and_a_toolset_list()
        {
            string[] Listed(string p)
            {
                string[] r = null;
                With(Settings(p), null, () => r = Names(SchemaFacade.List(SchemaSurface.Full, false))
                                                  .OrderBy(n => n, StringComparer.Ordinal).ToArray());
                return r;
            }
            Assert.Equal(Listed("esencial"), Listed("Essential"));
            Assert.Equal(Listed("modelado"), Listed("modeling"));
            Assert.Equal(Listed("documentacion"), Listed("Documentación"));
            Assert.Equal(Listed("auditoria"), Listed("audit"));
            Assert.Equal(Listed("consulta"), Listed("query"));
            // a comma-separated list of toolsets is a profile too
            string[] custom = Listed("documentacion, read");
            Assert.Contains("horizun_manage_views", custom);
            Assert.Contains("horizun_quantities", custom);
            Assert.DoesNotContain("horizun_create_railing", custom);
        }

        [Fact]
        public void The_client_environment_wins_over_settings()
        {
            With(Settings("modelado"), "esencial", () =>
            {
                ToolProfiles.Resolution r = ToolProfiles.Current();
                Assert.Equal("esencial", r.Name);
                Assert.Equal(ToolProfiles.EnvironmentVariable, r.Source);
                Assert.DoesNotContain("horizun_create_railing", Names(SchemaFacade.List(SchemaSurface.Full, false)));
            });
        }

        [Fact]
        public void An_unknown_profile_lists_everything_and_says_why()
        {
            With(Settings("arquitecto-senior"), null, () =>
            {
                ToolProfiles.Resolution r = ToolProfiles.Current();
                Assert.False(r.Restricting);
                Assert.Contains("arquitecto-senior", r.Problem);
                Assert.Equal(Names(Tools.List(false)), Names(SchemaFacade.List(SchemaSurface.Full, false)));
            });
        }

        [Fact]
        public void Compact_lists_the_same_names_as_full_under_a_profile()
        {
            With(Settings("esencial"), null, () =>
            {
                Assert.Equal(Names(SchemaFacade.List(SchemaSurface.Full, false)).OrderBy(n => n, StringComparer.Ordinal),
                             Names(SchemaFacade.List(SchemaSurface.Compact, false)).OrderBy(n => n, StringComparer.Ordinal));
                foreach (JObject t in SchemaFacade.List(SchemaSurface.Compact, false).OfType<JObject>())
                    Assert.True(((string)t["description"]).Length <= SchemaCompact.DescriptionMax, (string)t["name"]);
            });
        }

        [Fact]
        public void Facade_mode_is_not_changed_by_a_profile()
        {
            string plain = null, profiled = null;
            With(SafeWrite, null, () => plain = SchemaFacade.List(SchemaSurface.Facade, false).ToString(Formatting.None));
            With(Settings("esencial"), null, () => profiled = SchemaFacade.List(SchemaSurface.Facade, false).ToString(Formatting.None));
            Assert.Equal(plain, profiled);
        }

        // ---- 3. visibility only ---------------------------------------------------------

        [Fact]
        public void A_tool_the_profile_does_not_list_is_still_reachable()
        {
            With(Settings("esencial"), null, () =>
            {
                Assert.DoesNotContain("horizun_manage_views", Names(SchemaFacade.List(SchemaSurface.Full, false)));
                Assert.Null(Tools.DisabledReason("horizun_manage_views"));
                Assert.Contains("horizun_manage_views", Tools.Advertisable().Select(d => d.Name));
            });
        }

        [Fact]
        public void A_profile_never_lists_what_the_permission_profile_or_toolsets_refuse()
        {
            // safe_write hides capture_view; HORIZUN_TOOLSETS=local refuses manage_views
            With(Settings("documentacion"), null, () =>
            {
                Assert.DoesNotContain("horizun_capture_view", Names(SchemaFacade.List(SchemaSurface.Full, false)));
                Assert.DoesNotContain("horizun_execute_python", Names(SchemaFacade.List(SchemaSurface.Full, false)));
            });
            With(Settings("documentacion"), null, () =>
            {
                Assert.DoesNotContain("horizun_manage_views", Names(SchemaFacade.List(SchemaSurface.Full, false)));
                Assert.NotNull(Tools.DisabledReason("horizun_manage_views"));
            }, toolsets: "local");
        }

        [Fact]
        public void Over_the_wire_a_hidden_tool_is_called_by_name_and_through_call_tool()
        {
            Dictionary<string, JObject> r = Wire(Settings("esencial"), null, null,
                Req(2, "tools/list", new JObject()),
                ToolCall(3, "horizun_manage_views", new JObject { ["operation"] = "list" }),
                ToolCall(4, "horizun_call_tool", new JObject
                {
                    ["name"] = "horizun_manage_views", ["arguments"] = new JObject { ["operation"] = "list" }
                }));
            string[] listed = r["2"]["result"]["tools"].Select(t => (string)t["name"]).ToArray();
            Assert.DoesNotContain("horizun_manage_views", listed);
            Assert.Contains("horizun_call_tool", listed);
            foreach (string id in new[] { "3", "4" })
            {
                // No Revit is running here: the call must get as far as the bridge, which means the
                // profile did not refuse it. Neither an unknown tool nor a toolset refusal.
                Assert.Null(r[id]["error"]);
                string text = (string)r[id]["result"]["content"][0]["text"];
                Assert.DoesNotContain("Unknown tool", text);
                Assert.DoesNotContain("toolset", text, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("not one of", text);
            }
        }

        // ---- 4. activation shows a toolset for this session ----------------------------------

        [Fact]
        public void Activate_toolset_shows_a_toolset_for_this_session_only()
        {
            With(Settings("esencial"), null, () =>
            {
                Assert.Contains(ToolsetActivation.Name, Names(SchemaFacade.List(SchemaSurface.Full, false)));
                JObject answer = SchemaFacade.Answer(ToolsetActivation.Name, new JObject { ["toolset"] = "documentacion" });
                _out.WriteLine(answer.ToString(Formatting.None));
                Assert.Contains("horizun_manage_views", answer["newly_listed"].Select(t => (string)t));
                Assert.Contains("horizun_manage_views", Names(SchemaFacade.List(SchemaSurface.Full, false)));
                Assert.Contains("documentation", ToolProfiles.Current().ShownThisSession);
                Assert.DoesNotContain("tool_profile\":\"esencial,", File.ReadAllText(HorizunPaths.SettingsPath()));
            });
            // a new process (here: a reset) forgets it
            With(Settings("esencial"), null, () =>
                Assert.DoesNotContain("horizun_manage_views", Names(SchemaFacade.List(SchemaSurface.Full, false))));
        }

        [Fact]
        public void Over_the_wire_activation_announces_list_changed_and_lists_the_toolset()
        {
            List<JObject> all;
            Dictionary<string, JObject> r = Wire(Settings("esencial"), null, null, out all,
                ToolCall(2, ToolsetActivation.Name, new JObject { ["toolset"] = "documentation" }),
                Req(3, "tools/list", new JObject()));
            Assert.False((bool?)r["2"]["result"]["isError"] ?? false, r["2"].ToString());
            Assert.Contains("horizun_manage_views", r["3"]["result"]["tools"].Select(t => (string)t["name"]));
            Assert.Contains(all, m => (string)m["method"] == "notifications/tools/list_changed");
        }

        // ---- 5. health says which profile and why --------------------------------------------

        [Fact]
        public void Health_block_names_the_profile_its_source_and_what_is_hidden_but_callable()
        {
            With(Settings("esencial"), null, () =>
            {
                JObject h = ToolProfiles.HealthBlock();
                _out.WriteLine(h.ToString(Formatting.None));
                Assert.Equal("esencial", (string)h["name"]);
                Assert.Equal(ToolProfiles.SettingsKey, (string)h["source"]);
                Assert.True((int)h["listed"] < (int)h["callable"]);
                Assert.Contains("horizun_activate_toolset", (string)h["note"]);
                Assert.Contains("completo", h["profiles"].Select(t => (string)t));
            });
            With(SafeWrite, null, () =>
            {
                JObject h = ToolProfiles.HealthBlock();
                Assert.Equal("completo", (string)h["name"]);
                Assert.Equal("default", (string)h["source"]);
            });
        }

        // ---- the wire -----------------------------------------------------------------------

        private static JObject Req(int id, string method, JObject prms) =>
            new JObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = prms };

        private static JObject ToolCall(int id, string tool, JObject args) =>
            Req(id, "tools/call", new JObject { ["name"] = tool, ["arguments"] = args });

        private static Dictionary<string, JObject> Wire(string settings, string profileEnv, string mode, params JObject[] requests)
            => Wire(settings, profileEnv, mode, out _, requests);

        private static Dictionary<string, JObject> Wire(string settings, string profileEnv, string mode,
                                                        out List<JObject> all, params JObject[] requests)
        {
            string root = Path.Combine(Path.GetTempPath(), "hz-profile-wire-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "settings.json"), settings);
            all = new List<JObject>();
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
                psi.Environment.Remove(ToolPacks.ToolsetsEnvironmentVariable);
                psi.Environment.Remove("HORIZUN_REVIT_YEAR");
                if (profileEnv == null) psi.Environment.Remove(ToolProfiles.EnvironmentVariable);
                else psi.Environment[ToolProfiles.EnvironmentVariable] = profileEnv;
                if (mode == null) psi.Environment.Remove(SchemaMode.EnvironmentVariable);
                else psi.Environment[SchemaMode.EnvironmentVariable] = mode;

                var lines = new List<string>
                {
                    "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-11-25\",\"capabilities\":{},\"clientInfo\":{\"name\":\"profile-tests\",\"version\":\"1\"}}}",
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
                        all.Add(msg);
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
    }
}
