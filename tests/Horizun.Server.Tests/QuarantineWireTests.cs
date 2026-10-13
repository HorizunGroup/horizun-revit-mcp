// -----------------------------------------------------------------------------
// Horizun Server tests - original Horizun code.
//
// THE QUARANTINE ON THE REAL tools/call PATH. RepairMemoryCallGateTests proves the
// gate; this proves Program.cs consults it, through the real server executable over
// the real stdio transport, and that the refusal comes before routing: the server
// runs under a throwaway data root with no discovery file, so a call that got past
// the gate would answer "no Revit is reachable" instead. No Revit is involved, and
// none can be: discovery reads only that empty root.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Server.Tests
{
    public class QuarantineWireTests
    {
        private const string Tool = "horizun_health";

        private static List<JObject> Run(string dataRoot, JObject callArguments)
        {
            var psi = new ProcessStartInfo(JsonRpcErrorCodeTests.ServerExe())
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = new UTF8Encoding(false)
            };
            psi.Environment[HorizunPaths.RootOverrideVariable] = dataRoot;
            psi.Environment.Remove("HORIZUN_REVIT_YEAR");

            var call = new JObject
            {
                ["jsonrpc"] = "2.0", ["id"] = "call-1", ["method"] = "tools/call",
                ["params"] = new JObject { ["name"] = Tool, ["arguments"] = callArguments }
            };
            string[] lines =
            {
                "{\"jsonrpc\":\"2.0\",\"id\":\"init\",\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-11-25\",\"capabilities\":{},\"clientInfo\":{\"name\":\"quarantine-wire\",\"version\":\"1\"}}}",
                "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\",\"params\":{}}",
                call.ToString(Newtonsoft.Json.Formatting.None)
            };
            var replies = new List<JObject>();
            using (var proc = Process.Start(psi))
            {
                var stderr = proc.StandardError.ReadToEndAsync();
                var stdout = proc.StandardOutput.ReadToEndAsync();
                foreach (string l in lines) proc.StandardInput.WriteLine(l);
                proc.StandardInput.Close();
                if (!proc.WaitForExit(60000))
                {
                    try { proc.Kill(); } catch { }
                    throw new Xunit.Sdk.XunitException("Server did not terminate within 60 seconds.");
                }
                stderr.GetAwaiter().GetResult();
                foreach (string raw in stdout.GetAwaiter().GetResult().Split('\n'))
                {
                    string line = raw.Trim();
                    if (line.Length == 0) continue;
                    try { replies.Add(JObject.Parse(line)); } catch { }
                }
            }
            return replies;
        }

        private static JObject CallReply(List<JObject> replies)
        {
            foreach (JObject r in replies) if ((string)r["id"] == "call-1") return r;
            throw new Xunit.Sdk.XunitException("tools/call was never answered: " + string.Join(" | ", replies));
        }

        private static string NewRootWithQuarantine(JObject quarantinedArguments)
        {
            string root = Path.Combine(Path.GetTempPath(), "hz-quarantine-wire-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var entry = new RepairEntry
            {
                Shape = "0123456789abcdef0123456789abcdef", Tool = Tool, FailureClass = "refusal",
                NormalizedMessage = "the add-in crashed on <n>", Occurrences = 3,
                FirstSeenUtc = "2026-10-12T00:00:00Z", LastSeenUtc = "2026-10-12T00:00:00Z",
                State = RepairMemory.StateQuarantined, QuarantineReason = "crashes the session",
                QuarantinedBy = "owner", QuarantinedAtUtc = "2026-10-12T00:00:00Z"
            };
            RepairMemory.AttachCall(entry, RepairMemory.CallFingerprint(Tool, quarantinedArguments));
            RepairMemory.Save(Path.Combine(root, "repair-memory.json"), new[] { entry });
            return root;
        }

        [Fact]
        public void A_quarantined_call_is_refused_by_the_server_before_it_is_routed()
        {
            JObject args = JObject.Parse("{\"detail\":\"full\"}");
            string root = NewRootWithQuarantine(args);
            try
            {
                JObject reply = CallReply(Run(root, args));
                JObject result = (JObject)reply["result"];
                Assert.True(result != null, "expected a tool result: " + reply);
                Assert.True((bool)result["isError"]);
                Assert.Equal("call_quarantined", (string)result["structuredContent"]?["code"]);
                Assert.Equal("0123456789abcdef0123456789abcdef", (string)result["structuredContent"]?["shape"]);
                Assert.False((bool)result["structuredContent"]["ran"]);
                string text = (string)result["content"][0]["text"];
                Assert.Contains("QUARANTINED", text);
                Assert.DoesNotContain("reachable", text);
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }

        [Fact]
        public void A_call_with_other_arguments_is_routed_as_usual()
        {
            string root = NewRootWithQuarantine(JObject.Parse("{\"detail\":\"full\"}"));
            try
            {
                JObject reply = CallReply(Run(root, JObject.Parse("{\"detail\":\"brief\"}")));
                JObject result = (JObject)reply["result"];
                Assert.True(result != null, "expected a tool result: " + reply);
                Assert.NotEqual("call_quarantined", (string)result["structuredContent"]?["code"]);
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }
    }
}
