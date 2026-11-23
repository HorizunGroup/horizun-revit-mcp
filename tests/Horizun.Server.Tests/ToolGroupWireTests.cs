// -----------------------------------------------------------------------------
// Horizun Server tests - original Horizun code.
//
// 4.0 tool groups over the REAL executable and the REAL stdio transport, one request
// at a time (an activation must be answered before the next tools/list is read):
//
//   * a profile from the environment lists core + its groups + the three meta tools;
//   * --profile on the command line wins over the environment;
//   * horizun_toolsets activate emits exactly one notifications/tools/list_changed and
//     the next tools/list carries the group;
//   * horizun_call_tool reaches a tool of an inactive group with the SAME reply a direct
//     call gets, and a tool the permission profile refuses is refused through it too;
//   * with no profile at all, tools/list is the committed 3.x ledger.
//
// No Revit is reached: a plugin tool answering "no Revit is reachable" proves the call
// got past every gate.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Server.Tests
{
    public class ToolGroupWireTests
    {
        private const string SafeWrite = "{\"permission_profile\":\"safe_write\"}";

        [Fact]
        public void A_profile_lists_core_its_groups_and_the_meta_tools_and_activation_notifies_once()
        {
            using (var s = new Session(SafeWrite, env: new Dictionary<string, string> { ["HORIZUN_PROFILE"] = "auditor" }))
            {
                string[] before = Names(s.Call("tools/list", new JObject()));
                Assert.Contains("horizun_audit_model", before);
                Assert.Contains("horizun_manage_views", before);
                Assert.DoesNotContain("horizun_quantities", before);
                Assert.Equal(new[] { "horizun_toolsets", "horizun_describe_tool", "horizun_call_tool" }, before.Skip(before.Length - 3));

                int notes = s.Notifications("notifications/tools/list_changed");
                JObject activated = s.Tool("horizun_toolsets", new JObject { ["action"] = "activate", ["groups"] = new JArray("datos") });
                Assert.NotEqual(true, (bool?)activated["result"]["isError"]);
                Assert.True((bool)activated["result"]["structuredContent"]["tools_list_changed"]);
                s.Tool("horizun_health_noop_ping_unknown", new JObject());                    // a round trip to flush notifications
                Assert.Equal(notes + 1, s.Notifications("notifications/tools/list_changed"));

                string[] after = Names(s.Call("tools/list", new JObject()));
                Assert.Contains("horizun_quantities", after);
                Assert.Contains("horizun_excel_read_rows", after);
            }
        }

        [Fact]
        public void Call_tool_reaches_a_hidden_tool_exactly_like_a_direct_call_and_never_around_the_profile()
        {
            using (var s = new Session(SafeWrite, args: "--profile nucleo",
                                       env: new Dictionary<string, string> { ["HORIZUN_PROFILE"] = "completo" }))
            {
                string[] listed = Names(s.Call("tools/list", new JObject()));
                // nucleo, the command line beating HORIZUN_PROFILE=completo. Under safe_write the
                // ceiling still removes what it always removed (execute_python, open_document...).
                Assert.True(listed.Length < 19, string.Join(",", listed));
                Assert.Contains("horizun_query_model", listed);
                Assert.DoesNotContain("horizun_execute_python", listed);
                Assert.Equal(new[] { "horizun_toolsets", "horizun_describe_tool", "horizun_call_tool" }, listed.Skip(listed.Length - 3));
                Assert.DoesNotContain("horizun_measure_geometry", listed);

                var args = new JObject { ["element_ids"] = new JArray(1) };
                JObject through = s.Tool("horizun_call_tool", new JObject { ["name"] = "horizun_measure_geometry", ["arguments"] = args });
                JObject direct = s.Tool("horizun_measure_geometry", args);
                Assert.Equal(Text(direct), Text(through));
                Assert.DoesNotContain("Unknown tool", Text(through));

                JObject refused = s.Tool("horizun_call_tool", new JObject { ["name"] = "horizun_power_bi_push", ["arguments"] = new JObject() });
                JObject refusedDirect = s.Tool("horizun_power_bi_push", new JObject());
                Assert.True((bool)refused["result"]["isError"]);
                Assert.Contains("safe_write", Text(refused));
                Assert.Equal(Text(refusedDirect), Text(refused));

                JObject described = s.Tool("horizun_describe_tool", new JObject { ["name"] = "horizun_measure_geometry" });
                Assert.Equal("lectura", (string)described["result"]["structuredContent"]["group"]);
                Assert.False((bool)described["result"]["structuredContent"]["listed"]);
            }
        }

        [Fact]
        public void Without_a_profile_the_wire_list_is_the_committed_3x_ledger_and_toolsets_still_answers()
        {
            JObject ledger = JObject.Parse(File.ReadAllText(Path.Combine(
                Path.GetDirectoryName(typeof(ToolGroupWireTests).Assembly.Location), "..", "..", "..", "tools-list-ledger.json")));
            using (var s = new Session(SafeWrite))
            {
                JArray tools = (JArray)s.Call("tools/list", new JObject())["result"]["tools"];
                Assert.Equal((int)ledger["profiles"]["permission:safe_write"]["tools"], tools.Count);
                Assert.Equal((int)ledger["profiles"]["permission:safe_write"]["bytes"], SchemaFacadeTests.Bytes(tools));
                JObject list = s.Tool("horizun_toolsets", new JObject());
                Assert.Equal("completo", (string)list["result"]["structuredContent"]["profile"]);
                Assert.Equal("default", (string)list["result"]["structuredContent"]["profile_source"]);
            }
        }

        // ---- plumbing ----------------------------------------------------------------------------

        private static string[] Names(JObject reply) =>
            ((JArray)reply["result"]["tools"]).Select(t => (string)t["name"]).ToArray();

        private static string Text(JObject reply) =>
            string.Concat(((JArray)reply["result"]?["content"] ?? new JArray()).Select(c => (string)c["text"]));

        private sealed class Session : IDisposable
        {
            private readonly Process _proc;
            private readonly string _root;
            private readonly List<JObject> _notes = new List<JObject>();
            private int _id = 10;

            public Session(string settings, string args = null, Dictionary<string, string> env = null)
            {
                _root = Path.Combine(Path.GetTempPath(), "hz-groups-wire-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(_root);
                File.WriteAllText(Path.Combine(_root, "settings.json"), settings);
                var psi = new ProcessStartInfo(JsonRpcErrorCodeTests.ServerExe(), args ?? "")
                {
                    RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                    UseShellExecute = false, CreateNoWindow = true,
                    StandardOutputEncoding = new UTF8Encoding(false)
                };
                psi.Environment[Horizun.Revit.Core.HorizunPaths.RootOverrideVariable] = _root;
                foreach (string v in new[] { "HORIZUN_TOOLSETS", "HORIZUN_TOOL_PACKS", "HORIZUN_REVIT_YEAR", "HORIZUN_PROFILE",
                                             "HORIZUN_RESPONSE", "HORIZUN_SCHEMA_MODE", "HORIZUN_COMPACT_SCHEMAS" })
                    psi.Environment.Remove(v);
                if (env != null) foreach (var kv in env) psi.Environment[kv.Key] = kv.Value;
                _proc = Process.Start(psi);
                _proc.ErrorDataReceived += (o, e) => { };
                _proc.BeginErrorReadLine();
                Call("initialize", JObject.Parse("{\"protocolVersion\":\"2025-11-25\",\"capabilities\":{},\"clientInfo\":{\"name\":\"groups-wire-tests\",\"version\":\"1\"}}"));
                _proc.StandardInput.WriteLine("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");
            }

            public JObject Tool(string name, JObject arguments) =>
                Call("tools/call", new JObject { ["name"] = name, ["arguments"] = arguments });

            public JObject Call(string method, JObject prms)
            {
                int id = ++_id;
                _proc.StandardInput.WriteLine(new JObject
                {
                    ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = prms
                }.ToString(Formatting.None));
                var deadline = DateTime.UtcNow.AddSeconds(90);
                while (DateTime.UtcNow < deadline)
                {
                    string line = _proc.StandardOutput.ReadLine();
                    if (line == null) break;
                    JObject msg;
                    try { msg = JObject.Parse(line); } catch { continue; }
                    if (msg["method"] != null && msg["id"] == null) { _notes.Add(msg); continue; }
                    if (msg["id"] != null && msg["method"] == null && (int)msg["id"] == id) return msg;
                }
                throw new Xunit.Sdk.XunitException("no reply to " + method + " id " + id);
            }

            public int Notifications(string method) => _notes.Count(n => (string)n["method"] == method);

            public void Dispose()
            {
                try { _proc.StandardInput.Close(); } catch { }
                try { if (!_proc.WaitForExit(20000)) _proc.Kill(); } catch { }
                _proc.Dispose();
                try { Directory.Delete(_root, true); } catch { }
            }
        }
    }
}
