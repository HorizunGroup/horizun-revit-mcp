// -----------------------------------------------------------------------------
// Horizun Server tests - original Horizun code.
//
// P1-4 on the real path. The review of R2Q found that about forty "covered" writers refused the identity fields with
// esquema_invalido (additionalProperties:false) before any guard saw them, while every test called the guard alone.
// These tests send the fields over stdio, through the same Program.cs path a client uses, to EVERY covered tool.
// With no Revit reachable, a call that gets past the argument gate answers "no Revit is reachable": that is how
// "it was routed to the add-in" is told apart from "it was refused as an undeclared argument".
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.IO.Pipes;
using System.Text;
using System.Threading.Tasks;
using Horizun.Contracts;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Server.Tests
{
    public class IdentityWireTests
    {
        private static JObject IdentityArguments() => new JObject
        {
            ["identity"] = new JObject { ["element_id"] = "UID" },
            ["unique_id"] = "UID", ["unique_ids"] = new JArray("UID"),
            ["expected_category"] = "OST_Walls", ["expected_type"] = "Generic"
        };

        public static IEnumerable<object[]> CoveredTools() =>
            ElementIdentityGuard.CoveredTools.OrderBy(t => t, StringComparer.Ordinal).Select(t => new object[] { t });

        [Theory]
        [MemberData(nameof(CoveredTools))]
        public void A_covered_tool_does_not_refuse_the_identity_fields_as_undeclared(string tool)
        {
            Assert.Empty(UndeclaredArguments.Find(tool, IdentityArguments()));
        }

        [Fact]
        public void A_tool_the_guard_does_not_cover_still_refuses_them_by_name()
        {
            // closed schemas, and the tools whose schema is OPEN (an open schema must not let the fields through to be ignored: R2S m-1)
            string[] refused =
            {
                "horizun_audit_model", "horizun_health", "get_document_info",
                "horizun_quantities", "horizun_plan_structure"
            };
            foreach (string tool in refused)
            {
                Assert.False(ElementIdentityGuard.Covers(tool));
                List<string> found = UndeclaredArguments.Find(tool, IdentityArguments());
                foreach (string name in ElementIdentityGuard.AcceptedNames) Assert.Contains(name, found);
            }
        }

        [Fact]
        public void Every_uncovered_tool_with_an_open_schema_refuses_the_identity_fields()
        {
            var openUncovered = Contract.All.Where(c => UndeclaredArguments.Declared(c.InputSchema) == null && !ElementIdentityGuard.Covers(c.Name)).Select(c => c.Name).ToList();
            Assert.NotEmpty(openUncovered);
            foreach (string tool in openUncovered)
                foreach (string name in ElementIdentityGuard.AcceptedNames)
                {
                    JToken schema = Contract.Find(tool).InputSchema;
                    if (ElementIdentityGuard.SchemaNames(schema).Contains(name)) continue;   // the tool declares it itself
                    var args = new JObject { [name] = name == "identity" ? (JToken)new JObject() : "x" };
                    Assert.True(UndeclaredArguments.Find(tool, args).Contains(name), tool + " lets '" + name + "' through");
                }
            // and the refusal names the fields, not an empty list
            string text = (string)UndeclaredArguments.Refusal("horizun_quantities", new List<string> { "unique_id" })["text"];
            Assert.Contains("'unique_id'", text);
            Assert.DoesNotContain("accepts the element-identity fields", text);
        }

        [Fact]
        public void The_open_schema_of_a_covered_tool_still_accepts_the_fields()
        {
            Assert.Empty(UndeclaredArguments.Find("horizun_delete_verified", IdentityArguments()));
            Assert.Empty(UndeclaredArguments.Find("horizun_write_params_verified", IdentityArguments()));
        }

        [Fact]
        public void On_the_wire_every_covered_tool_routes_a_call_that_carries_identity_instead_of_refusing_it()
        {
            string[] tools = ElementIdentityGuard.CoveredTools.OrderBy(t => t, StringComparer.Ordinal).ToArray();
            var calls = tools.Select(t => new KeyValuePair<string, JObject>(t, IdentityArguments())).ToList();
            Dictionary<string, JObject> answers = CallMany(calls);
            var refused = new List<string>();
            foreach (string tool in tools)
            {
                JObject result = answers[tool];
                string text = result == null ? "(no answer)" : (string)result["content"]?[0]?["text"] ?? "";
                string code = (string)result?["structuredContent"]?[ErrorCodes.CodeField];
                // Routed means: it got past the schema and the undeclared-argument checks and failed only because no Revit is attached
                // to the test process, with THAT code. Any other code (esquema_invalido, entrada_invalida, ...) is a refusal of the identity.
                if (code != ErrorCodes.RevitNoDisponible || text.Contains("does not take the argument") || !text.Contains("reachable"))
                    refused.Add(tool + ": code=" + (code ?? "(none)") + " " + (text.Length > 160 ? text.Substring(0, 160) : text));
            }
            Assert.True(refused.Count == 0, "covered tools that did not route a call carrying identity fields:" + Environment.NewLine + string.Join(Environment.NewLine, refused));
        }

        [Fact]
        public void On_the_wire_a_valid_call_with_a_nested_identity_is_routed_and_not_refused_as_undeclared()
        {
            // Valid target_document + operation + a nested identity, to a tool with a closed schema and one with an open schema.
            var calls = new List<KeyValuePair<string, JObject>>
            {
                new KeyValuePair<string, JObject>("horizun_manage_views", JObject.Parse(
                    "{\"target_document\":\"Doc\",\"actions\":[{\"operation\":\"place_view\",\"view_id\":31,\"sheet_id\":30,\"identity\":{\"view_id\":\"UID-V\",\"sheet_id\":{\"unique_id\":\"UID-S\",\"expected_category\":\"OST_Sheets\"}}}]}")),
                new KeyValuePair<string, JObject>("horizun_write_params_verified", JObject.Parse(
                    "{\"target_document\":\"Doc\",\"writes\":[{\"target_id\":7,\"unique_id\":\"UID-7\",\"parameter\":\"Mark\",\"value\":\"A\"}]}"))
            };
            Dictionary<string, JObject> answers = CallMany(calls);
            foreach (KeyValuePair<string, JObject> call in calls)
            {
                string text = (string)answers[call.Key]["content"]?[0]?["text"] ?? "";
                string code = (string)answers[call.Key]["structuredContent"]?[ErrorCodes.CodeField];
                Assert.True(code == ErrorCodes.RevitNoDisponible, call.Key + ": expected " + ErrorCodes.RevitNoDisponible + " (routed, no Revit attached) but got " + (code ?? "(none)") + ": " + text);
                Assert.True(text.Contains("reachable") && !text.Contains("does not take the argument"), call.Key + ": " + text);
                Assert.Empty(UndeclaredArguments.Find(call.Key, call.Value));
            }
        }

        // The seam between the layers, with a fake add-in on a real pipe: what the server sends is what the add-in's pre-pass receives,
        // and the command behind the pre-pass gets the identity consumed. (The full stdio path cannot reach a fake add-in: the server
        // only trusts a discovery file whose pid is a running Revit.)
        [Fact]
        public async Task The_server_forwards_the_identity_untouched_and_the_addin_prepass_consumes_it_before_the_command()
        {
            var args = JObject.Parse("{\"target_document\":\"Doc\",\"actions\":[{\"operation\":\"place_view\",\"view_id\":510,\"sheet_id\":7," +
                                     "\"identity\":{\"view_id\":\"u510\",\"sheet_id\":{\"unique_id\":\"u7\",\"expected_category\":\"Walls\"}}}]}");
            Assert.Empty(UndeclaredArguments.Find("horizun_manage_views", args));
            string name = "hrz-identity-seam-" + Guid.NewGuid().ToString("N");
            string received = null;
            using (var addIn = new NamedPipeServerStream(name, PipeDirection.InOut, 2, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0))
            {
                var peer = Task.Run(() =>
                {
                    addIn.WaitForConnection();
                    using (var reader = new StreamReader(addIn, new UTF8Encoding(false), false, 4096, true))
                        received = reader.ReadLine();
                    byte[] ok = Encoding.UTF8.GetBytes("{\"success\":true,\"data\":{}}\n");
                    addIn.Write(ok, 0, ok.Length);
                    addIn.Flush();
                });
                PipeClient.Send(new Discovered { PipeName = name, Token = "t" }, "horizun_manage_views", args, 20000);
                await peer.WaitAsync(TimeSpan.FromSeconds(10));
            }
            JObject onTheWire = (JObject)JObject.Parse(received)["params"];
            Assert.True(JToken.DeepEquals(args, onTheWire), "the server must forward the arguments untouched");

            // The add-in side: the same text goes through the pre-pass with a fake command.
            Func<long, ElementIdentityFacts> byId = id => id < 1 || id > 999 ? null
                : new ElementIdentityFacts { Id = id, UniqueId = "u" + id, Owned = true, Category = "Walls", CategoryBuiltIn = "OST_Walls", Type = "T", Family = "F" };
            Func<string, ElementIdentityFacts> byUid = uid => uid != null && uid.StartsWith("u") && long.TryParse(uid.Substring(1), out long id) ? byId(id) : null;
            string commandSaw = null;
            CommandResult r = IdentityPrepassCore.Execute("horizun_manage_views", onTheWire.ToString(Newtonsoft.Json.Formatting.None),
                req => IdentityPrepassCore.Early(req, "horizun_manage_views", () => new IdentityLookups { ById = byId, ByUniqueId = byUid }),
                text => { commandSaw = text; return CommandResult.Ok(new JObject { ["done"] = true }); });
            Assert.True(r.Success);
            JObject seen = JObject.Parse(commandSaw);
            Assert.Null(seen["actions"][0]["identity"]);
            Assert.Equal(510, (int)seen["actions"][0]["view_id"]);
            Assert.Equal("unique_id", (string)((JObject)r.Data)["identity_strength"]);
            // and a wrong unique_id never reaches the command
            onTheWire["actions"][0]["identity"]["view_id"] = "u511";
            bool ran = false;
            CommandResult refused = IdentityPrepassCore.Execute("horizun_manage_views", onTheWire.ToString(Newtonsoft.Json.Formatting.None),
                req => IdentityPrepassCore.Early(req, "horizun_manage_views", () => new IdentityLookups { ById = byId, ByUniqueId = byUid }),
                text => { ran = true; return CommandResult.Ok(new JObject()); });
            Assert.False(ran);
            Assert.False(refused.Success);
        }

        [Fact]
        public void On_the_wire_a_tool_the_guard_does_not_cover_refuses_the_same_fields_before_routing()
        {
            JObject result = UndeclaredArgumentsTests.CallOverStdio("get_document_info", new JObject { ["operation"] = "context", ["unique_id"] = "x" });
            Assert.True((bool)result["isError"]);
            Assert.Equal(ErrorCodes.EsquemaInvalido, (string)result["structuredContent"]?[ErrorCodes.CodeField]);
            Assert.DoesNotContain("reachable", (string)result["content"][0]["text"]);
        }

        [Fact]
        public void The_refusal_of_a_covered_tool_says_it_also_accepts_the_identity_fields()
        {
            JObject refusal = UndeclaredArguments.Refusal("horizun_manage_materials", new List<string> { "uniqeId" });
            string text = (string)refusal["text"];
            Assert.Contains("also accepts the element-identity fields", text);
            Assert.Contains("unique_id", text);
            Assert.True((bool)refusal["detail"]["accepts_identity_fields"]);
            Assert.False((bool)UndeclaredArguments.Refusal("get_document_info", new List<string> { "x" })["detail"]["accepts_identity_fields"]);
        }

        [Theory]
        [MemberData(nameof(CoveredTools))]
        public void The_contract_resource_declares_the_identity_fields_of_a_covered_tool(string tool)
        {
            JObject row = McpResources.Row(Contract.Find(tool));
            var props = (JObject)row["identity_fields"]["properties"];
            foreach (string name in ElementIdentityGuard.AcceptedNames) Assert.NotNull(props[name]);
            Assert.Contains("Only unique_id identifies", (string)row["identity_fields"]["summary"]);
            Assert.True(JToken.DeepEquals(Contract.Find(tool).InputSchema, row["input_schema"]));   // the exact schema is untouched
            Assert.Equal(JTokenType.Null, McpResources.Row(Contract.Find("get_document_info"))["identity_fields"].Type);
        }

        [Fact]
        public void tools_list_itself_is_not_made_bigger_by_it()
        {
            // The advertised schema is the contract's own: the identity properties exist only in the resource.
            foreach (string tool in new[] { "horizun_manage_materials", "horizun_wall_sweep" })
            {
                JObject listed = Tools.Find(tool).InputSchema;
                Assert.Null(listed["properties"]["unique_id"]);
                Assert.NotNull(McpResources.Row(Contract.Find(tool))["identity_fields"]["properties"]["unique_id"]);
            }
        }

        // The server refuses a tool call past 64 in flight (by design: "Nothing was started"), and this test
        // writes every call before reading any answer. 5.2.0 took the covered tools to 65, so the 65th got that
        // JSON-RPC error instead of a result. Batches stay well under the limit, one server process each.
        private const int CallsPerProcess = 48;

        private static Dictionary<string, JObject> CallMany(List<KeyValuePair<string, JObject>> calls)
        {
            if (calls.Count <= CallsPerProcess) return CallBatch(calls);
            var all = new Dictionary<string, JObject>(StringComparer.Ordinal);
            for (int i = 0; i < calls.Count; i += CallsPerProcess)
                foreach (var kv in CallBatch(calls.Skip(i).Take(CallsPerProcess).ToList())) all[kv.Key] = kv.Value;
            return all;
        }

        private static Dictionary<string, JObject> CallBatch(List<KeyValuePair<string, JObject>> calls)
        {
            string root = Path.Combine(Path.GetTempPath(), "hz-identity-wire-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                // Every covered tool must be reachable: the default profile hides some writers before the argument gate.
                string saved = Environment.GetEnvironmentVariable(HorizunPaths.RootOverrideVariable);
                try
                {
                    Environment.SetEnvironmentVariable(HorizunPaths.RootOverrideVariable, root);
                    File.WriteAllText(HorizunPaths.SettingsPath(), "{\"permission_profile\":\"unsafe_code\"}");
                }
                finally { Environment.SetEnvironmentVariable(HorizunPaths.RootOverrideVariable, saved); }
                var psi = new ProcessStartInfo(JsonRpcErrorCodeTests.ServerExe())
                {
                    RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                    UseShellExecute = false, CreateNoWindow = true, StandardOutputEncoding = new UTF8Encoding(false)
                };
                psi.Environment[HorizunPaths.RootOverrideVariable] = root;
                psi.Environment.Remove("HORIZUN_REVIT_YEAR");
                var lines = new List<string>
                {
                    "{\"jsonrpc\":\"2.0\",\"id\":\"init\",\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-11-25\",\"capabilities\":{},\"clientInfo\":{\"name\":\"identity-wire\",\"version\":\"1\"}}}",
                    "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\",\"params\":{}}"
                };
                for (int i = 0; i < calls.Count; i++)
                    lines.Add(new JObject
                    {
                        ["jsonrpc"] = "2.0", ["id"] = "call-" + i, ["method"] = "tools/call",
                        ["params"] = new JObject { ["name"] = calls[i].Key, ["arguments"] = calls[i].Value }
                    }.ToString(Newtonsoft.Json.Formatting.None));
                var answers = new Dictionary<string, JObject>(StringComparer.Ordinal);
                using (var proc = Process.Start(psi))
                {
                    var stderr = proc.StandardError.ReadToEndAsync();
                    var stdout = proc.StandardOutput.ReadToEndAsync();
                    foreach (string l in lines) proc.StandardInput.WriteLine(l);
                    proc.StandardInput.Close();
                    if (!proc.WaitForExit(300000))
                    {
                        try { proc.Kill(); } catch { }
                        throw new Xunit.Sdk.XunitException("Server did not terminate within 300 seconds.");
                    }
                    stderr.GetAwaiter().GetResult();
                    foreach (string raw in stdout.GetAwaiter().GetResult().Split('\n'))
                    {
                        string line = raw.Trim();
                        if (line.Length == 0) continue;
                        JObject r;
                        try { r = JObject.Parse(line); } catch { continue; }
                        string id = (string)r["id"];
                        if (id != null && id.StartsWith("call-", StringComparison.Ordinal))
                            answers[calls[int.Parse(id.Substring(5))].Key] = (JObject)r["result"];
                    }
                }
                return answers;
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }
    }
}
