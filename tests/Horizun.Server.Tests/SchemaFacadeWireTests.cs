// -----------------------------------------------------------------------------
// Horizun Server tests - original Horizun code.
//
// The schema facade over the REAL executable and the REAL stdio transport. The
// in-process tests (SchemaFacadeTests) prove the rules; these prove Program.cs wires
// them: what tools/list carries in each mode, that a call through horizun_call_tool is
// the same call as a direct one (same gate, same refusal, same schema_help), and that
// full mode answers the committed ledger byte for byte.
//
// NO REVIT IS EVER REACHED. Every run gets a throwaway HORIZUN_DATA_ROOT, so there is
// no discovery file to route to: a plugin tool answers "no Revit is reachable", which is
// exactly the proof that the call got PAST the facade and the gates.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Server.Tests
{
    public class SchemaFacadeWireTests
    {
        private const string SafeWrite = "{\"permission_profile\":\"safe_write\"}";
        private const string ReadOnly = "{\"permission_profile\":\"read_only\"}";

        [Fact]
        public void Full_mode_tools_list_over_stdio_is_the_committed_ledger()
        {
            JObject ledger = JObject.Parse(File.ReadAllText(LedgerPath()));
            Dictionary<string, JObject> r = Run("full", SafeWrite, Req(2, "tools/list", new JObject()));
            JArray tools = (JArray)r["2"]["result"]["tools"];
            Assert.Equal((int)ledger["profiles"]["permission:safe_write"]["tools"], tools.Count);
            Assert.Equal((int)ledger["profiles"]["permission:safe_write"]["bytes"], SchemaFacadeTests.Bytes(tools));
        }

        [Fact]
        public void The_facade_lists_describes_and_calls_end_to_end_when_selected()
        {
            foreach (string mode in new[] { "facade" })
            {
                Dictionary<string, JObject> r = Run(mode, SafeWrite,
                    Req(2, "tools/list", new JObject()),
                    ToolCall(3, SchemaFacade.ListToolsName, new JObject()),
                    ToolCall(4, SchemaFacade.DescribeToolName, new JObject { ["name"] = "horizun_target" }),
                    ToolCall(5, SchemaFacade.CallToolName, new JObject { ["name"] = "horizun_target", ["arguments"] = new JObject() }),
                    ToolCall(6, SchemaFacade.CallToolName, new JObject { ["name"] = "get_document_info", ["arguments"] = new JObject() }),
                    ToolCall(7, "get_document_info", new JObject()));
                string m = mode ?? "(default)";

                Assert.Equal(new[] { "horizun_health", SchemaFacade.ListToolsName, SchemaFacade.DescribeToolName, SchemaFacade.CallToolName },
                             ((JArray)r["2"]["result"]["tools"]).Select(t => (string)t["name"]).ToArray());

                JObject listed = (JObject)r["3"]["result"]["structuredContent"];
                Assert.NotNull(listed["tools"]["horizun_target"]);
                Assert.NotNull(listed["tools"]["horizun_write_params_verified"]);
                Assert.Null(listed["tools"]["horizun_execute_python"]); // safe_write: not reachable

                JObject described = (JObject)r["4"]["result"]["structuredContent"];
                Assert.True(JToken.DeepEquals(Horizun.Contracts.Contract.Find("horizun_target").InputSchema, described["input_schema"]), m);

                Assert.NotEqual(true, (bool?)r["5"]["result"]["isError"]);                    // host tool answered
                Assert.True((bool)r["6"]["result"]["isError"]);
                Assert.Contains("no Revit is reachable", Text(r["6"]));                       // past every gate
                Assert.Equal(Text(r["7"]), Text(r["6"]));                                    // a direct call still works, identically
            }
        }

        [Fact]
        public void The_default_wire_list_is_the_full_contract()
        {
            Dictionary<string, JObject> r = Run(null, SafeWrite,
                Req(2, "tools/list", new JObject()));
            string[] names = ((JArray)r["2"]["result"]["tools"])
                .Select(t => (string)t["name"]).ToArray();
            Assert.Contains("horizun_target", names);
            Assert.DoesNotContain(SchemaFacade.ListToolsName, names);
            Assert.True(names.Length > 100, "the default must expose the full safe_write catalog");
        }

        [Fact]
        public void The_facade_is_not_a_way_around_the_profile()
        {
            Dictionary<string, JObject> r = Run("facade", ReadOnly,
                ToolCall(2, SchemaFacade.CallToolName, new JObject { ["name"] = "horizun_delete_verified", ["arguments"] = new JObject() }),
                ToolCall(3, "horizun_delete_verified", new JObject()),
                ToolCall(4, SchemaFacade.CallToolName, new JObject { ["name"] = "horizun_execute_python", ["arguments"] = new JObject { ["code"] = "1" } }),
                ToolCall(5, SchemaFacade.CallToolName, new JObject { ["name"] = SchemaFacade.CallToolName, ["arguments"] = new JObject() }));
            Assert.True((bool)r["2"]["result"]["isError"]);
            Assert.Contains("read_only", Text(r["2"]));
            Assert.Equal(Text(r["3"]), Text(r["2"]));                 // the same refusal a direct call gets
            Assert.True((bool)r["4"]["result"]["isError"]);
            Assert.DoesNotContain("no Revit is reachable", Text(r["4"])); // refused BEFORE routing
            Assert.True((bool)r["5"]["result"]["isError"]);
            Assert.Contains("itself", Text(r["5"]));
        }

        [Fact]
        public void Contract_errors_name_the_path_and_the_uri_in_both_modes()
        {
            var bad = new JObject { ["bogus_argument"] = 1 };
            Dictionary<string, JObject> full = Run("full", SafeWrite, ToolCall(2, "get_document_info", bad));
            Dictionary<string, JObject> facade = Run("facade", SafeWrite,
                ToolCall(2, SchemaFacade.CallToolName, new JObject { ["name"] = "get_document_info", ["arguments"] = bad }));
            foreach (var r in new[] { full, facade })
            {
                string text = Text(r["2"]);
                Assert.Contains("Arguments violate the contract at /bogus_argument", text);
                Assert.Contains("horizun://contract/tools/get_document_info", text);
            }
            Assert.Equal(Text(full["2"]), Text(facade["2"]));
        }

        [Fact]
        public void The_facade_cannot_bypass_a_disabled_toolset_on_the_wire()
        {
            const string coreOnly = "{\"permission_profile\":\"unsafe_code\",\"enable_execute_python\":true,\"tool_packs\":[\"core\"]}";
            Dictionary<string, JObject> r = Run("facade", coreOnly,
                ToolCall(2, SchemaFacade.ListToolsName, new JObject()),
                ToolCall(3, SchemaFacade.DescribeToolName, new JObject { ["name"] = "horizun_create_elements" }),
                ToolCall(4, SchemaFacade.CallToolName, new JObject { ["name"] = "horizun_create_elements", ["arguments"] = new JObject() }),
                ToolCall(5, "horizun_create_elements", new JObject()));
            Assert.Null(r["2"]["result"]["structuredContent"]["tools"]["horizun_create_elements"]);
            Assert.False((bool)r["3"]["result"]["structuredContent"]["callable"]);
            Assert.True((bool)r["4"]["result"]["isError"]);
            Assert.Equal(Text(r["5"]), Text(r["4"]));
            Assert.DoesNotContain("no Revit is reachable", Text(r["4"]));
        }

        [Fact]
        public void Initialize_sends_the_head_of_the_surface_it_serves()
        {
            Assert.Equal(ServerInstructions.Head, (string)Run("full", SafeWrite)["1"]["result"]["instructions"]);
            Assert.Equal(ServerInstructions.HeadFor(SchemaSurface.Facade),
                         (string)Run("facade", SafeWrite)["1"]["result"]["instructions"]);
        }

        [Fact]
        public void In_full_mode_the_facade_tools_do_not_exist()
        {
            Dictionary<string, JObject> r = Run("full", SafeWrite,
                ToolCall(2, SchemaFacade.CallToolName, new JObject { ["name"] = "horizun_target", ["arguments"] = new JObject() }));
            Assert.Equal(-32602, (int)r["2"]["error"]["code"]);
            Assert.Contains("Unknown tool", (string)r["2"]["error"]["message"]);
        }

        [Fact]
        public void The_guidance_resource_describes_the_surface_actually_served()
        {
            foreach (string mode in new[] { "full", "facade" })
            {
                var replies = Run(mode, SafeWrite,
                    Req(2, "resources/read", new JObject { ["uri"] = "horizun://guidance/typed-first" }));
                string text = (string)replies["2"]["result"]["contents"][0]["text"];
                if (mode == "full") Assert.Equal(ServerInstructions.Text, text);
                else
                {
                    Assert.StartsWith(ServerInstructions.HeadFor(SchemaSurface.Facade), text);
                    Assert.Contains(SchemaFacade.DescribeToolName, text);
                    Assert.DoesNotContain("tools/list schemas are abridged", text);
                }
            }
        }

        // ---- plumbing -------------------------------------------------------------------------

        private static string Text(JObject reply) =>
            string.Join("\n", ((JArray)reply["result"]["content"]).Where(c => (string)c["type"] == "text").Select(c => (string)c["text"]));

        private static JObject Req(int id, string method, JObject prms) =>
            new JObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = prms };

        private static JObject ToolCall(int id, string tool, JObject args) =>
            Req(id, "tools/call", new JObject { ["name"] = tool, ["arguments"] = args });

        private static string LedgerPath([CallerFilePath] string here = null) =>
            Path.Combine(Path.GetDirectoryName(here), "tools-list-ledger.json");

        /// <summary>One server process, its own data root and settings, replies keyed by id.</summary>
        private static Dictionary<string, JObject> Run(string mode, string settings, params JObject[] requests)
        {
            string root = Path.Combine(Path.GetTempPath(), "hz-facade-wire-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "settings.json"), settings);
            try
            {
                var psi = new ProcessStartInfo(JsonRpcErrorCodeTests.ServerExe())
                {
                    RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                    UseShellExecute = false, CreateNoWindow = true,
                    StandardOutputEncoding = new UTF8Encoding(false)  // measured bytes, not the console code page
                };
                psi.Environment[Horizun.Revit.Core.HorizunPaths.RootOverrideVariable] = root;
                psi.Environment.Remove(Horizun.Revit.Core.ToolPacks.ToolsetsEnvironmentVariable);
                psi.Environment.Remove(Horizun.Revit.Core.ToolPacks.EnvironmentOverride);
                psi.Environment.Remove("HORIZUN_REVIT_YEAR");
                if (mode == null) psi.Environment.Remove(SchemaMode.EnvironmentVariable);
                else psi.Environment[SchemaMode.EnvironmentVariable] = mode;

                var lines = new List<string>
                {
                    "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-11-25\",\"capabilities\":{},\"clientInfo\":{\"name\":\"facade-wire-tests\",\"version\":\"1\"}}}",
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
                    foreach (JObject q in requests)
                        Assert.True(replies.ContainsKey((string)q["id"]),
                            "no reply to id " + q["id"] + " (" + q["method"] + "); stderr: " + err);
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
