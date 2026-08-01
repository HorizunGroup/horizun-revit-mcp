using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Server.Tests
{
    /// <summary>
    /// The JSON-RPC error codes, driven through the REAL server executable over the
    /// REAL stdio transport.
    ///
    /// Every other test in this project links a source file and exercises it in
    /// isolation, which is right for a pure rule and wrong for this: the codes are
    /// decided in Program.cs, on the path between reading a line and writing a reply,
    /// and a unit test of a helper cannot tell you that a malformed line gets answered
    /// at all. It used to not be - a line that failed to parse was logged and dropped,
    /// so a client that sent one waited forever for a reply that was never coming, and
    /// no test could see it because no test ever sent a line.
    ///
    /// Slow, by the standards of the rest of this file, and worth it.
    /// </summary>
    public class JsonRpcErrorCodeTests
    {
        private static string ServerExe()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !Directory.Exists(Path.Combine(d.FullName, "src", "Horizun.Server")))
                d = d.Parent;
            Assert.True(d != null, "Could not locate src/Horizun.Server");

            // BOTH NAMES. The apphost carries .exe on Windows and nothing on Linux, and
            // this looked only for the Windows one - so on the ubuntu runner every test
            // in this file threw "not built" against a server that had just built fine.
            // CI had been red on main for that reason alone, which is worse than a
            // failing test: a permanently red gate is a gate people stop reading.
            foreach (string cfg in new[] { "Release", "Debug" })
                foreach (string exe in new[] { "horizun-mcp.exe", "horizun-mcp" })
                {
                    string p = Path.Combine(d.FullName, "src", "Horizun.Server", "bin", cfg, "net8.0", exe);
                    if (File.Exists(p)) return p;
                }

            // Deliberately a failure and not a skip: a test that quietly passes when it
            // could not run is the shape of defect this whole exercise is about.
            throw new Xunit.Sdk.XunitException(
                "The horizun-mcp server executable is not built, so the wire codes were NOT tested. " +
                "Looked for 'horizun-mcp.exe' and 'horizun-mcp' under src/Horizun.Server/bin/{Release,Debug}/net8.0. " +
                "Run: dotnet build src/Horizun.Server -c Release");
        }

        /// <summary>Send raw lines, read every reply line back. One process, one round.</summary>
        private static List<JObject> Exchange(params string[] lines)
        {
            var psi = new ProcessStartInfo(ServerExe())
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = new UTF8Encoding(false)
            };

            var replies = new List<JObject>();
            using (var proc = Process.Start(psi))
            {
                foreach (string l in lines) proc.StandardInput.WriteLine(l);
                proc.StandardInput.Close();

                string line;
                while ((line = proc.StandardOutput.ReadLine()) != null)
                {
                    line = line.Trim();
                    if (line.Length == 0) continue;
                    try { replies.Add(JObject.Parse(line)); } catch { /* not a JSON-RPC line */ }
                }
                if (!proc.WaitForExit(60000)) { try { proc.Kill(); } catch { } }
            }
            return replies;
        }

        private static JObject FindError(List<JObject> replies, int code)
        {
            foreach (JObject r in replies)
                if (r["error"] is JObject e && (int?)e["code"] == code) return r;
            return null;
        }

        private static string Codes(List<JObject> replies)
        {
            var seen = new List<string>();
            foreach (JObject r in replies)
                seen.Add(r["error"] is JObject e ? "error " + e["code"] : "result");
            return seen.Count == 0 ? "(no replies at all)" : string.Join(", ", seen);
        }

        [Fact]
        public void A_line_that_is_not_JSON_is_answered_with_32700_not_dropped()
        {
            var replies = Exchange("{this is not json");

            JObject err = FindError(replies, -32700);
            Assert.True(err != null,
                "A malformed line must be answered with -32700 (Parse error). Got: " + Codes(replies));

            // id null: the id is precisely what could not be read.
            Assert.Equal(JTokenType.Null, err["id"]?.Type ?? JTokenType.Null);

            // And it must not echo the offending text back - it can carry a path or a token.
            Assert.DoesNotContain("this is not json", err.ToString(), StringComparison.Ordinal);
        }

        [Fact]
        public void An_unknown_method_is_32601_and_a_bad_tools_call_is_32602()
        {
            var replies = Exchange(
                "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"no/such/method\"}",
                "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"horizun_health\",\"arguments\":\"not-an-object\"}}",
                "{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"tools/call\",\"params\":{}}");

            Assert.True(FindError(replies, -32601) != null,
                "An unknown method must be -32601 (Method not found). Got: " + Codes(replies));

            JObject badArgs = FindError(replies, -32602);
            Assert.True(badArgs != null,
                "'arguments' of the wrong type, and a missing 'name', must both be -32602 (Invalid params) - " +
                "not silently replaced with an empty object. Got: " + Codes(replies));
        }

        [Fact]
        public void A_request_whose_id_cannot_be_echoed_is_refused_as_32600()
        {
            var replies = Exchange("{\"jsonrpc\":\"2.0\",\"id\":{\"not\":\"usable\"},\"method\":\"tools/list\"}");

            Assert.True(FindError(replies, -32600) != null,
                "An id that is neither string, number nor null cannot be matched back to the request, so the " +
                "request must be refused with -32600 rather than run. Got: " + Codes(replies));
        }

        /// <summary>
        /// The version field was never checked. A caller announcing 1.0, or announcing
        /// nothing, was served as though it had agreed to this protocol - and the first
        /// thing it would disagree about is how errors come back, which is exactly when
        /// a caller can least afford a surprise.
        /// </summary>
        [Fact]
        public void A_jsonrpc_version_that_is_not_2_0_is_refused_with_32600()
        {
            var replies = Exchange(
                "{\"jsonrpc\":\"1.0\",\"id\":1,\"method\":\"tools/list\"}",
                "{\"id\":2,\"method\":\"tools/list\"}",
                "{\"jsonrpc\":2.0,\"id\":3,\"method\":\"tools/list\"}");

            int refusals = 0;
            foreach (JObject r in replies)
                if (r["error"] is JObject e && (int?)e["code"] == -32600) refusals++;

            Assert.True(refusals == 3,
                "All three must be refused with -32600: the string \"1.0\", an absent field, and the NUMBER 2.0 " +
                "(the spec says the string \"2.0\"). Got " + refusals + " refusals from: " + Codes(replies));

            // And none of them was served.
            foreach (JObject r in replies)
                Assert.Null(r["result"]);
        }

        [Fact]
        public void A_correct_2_0_request_is_still_served()
        {
            var replies = Exchange("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\"}");

            bool served = false;
            foreach (JObject r in replies)
                if ((int?)r["id"] == 1 && r["result"]?["tools"] != null) served = true;

            Assert.True(served, "The version check must not refuse a correct request. Got: " + Codes(replies));
        }

        [Fact]
        public void Initialize_negotiates_old_clients_and_offers_current_protocol_to_unknown_clients()
        {
            var replies = Exchange(
                "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2024-11-05\",\"capabilities\":{},\"clientInfo\":{\"name\":\"test\",\"version\":\"1\"}}}",
                "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"1900-01-01\",\"capabilities\":{},\"clientInfo\":{\"name\":\"test\",\"version\":\"1\"}}}");
            Assert.Equal("2024-11-05", (string)replies[0]["result"]["protocolVersion"]);
            Assert.Equal("2025-11-25", (string)replies[1]["result"]["protocolVersion"]);
        }

        [Fact]
        public void Listed_tools_publish_modern_schemas_and_behavior_annotations()
        {
            var replies = Exchange("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\"}");
            JArray tools = replies[0]["result"]["tools"] as JArray;
            Assert.NotNull(tools);
            Assert.NotEmpty(tools);
            foreach (JObject tool in tools)
            {
                Assert.Equal("object", (string)tool["outputSchema"]["type"]);
                Assert.NotNull(tool["annotations"]["readOnlyHint"]);
                Assert.NotNull(tool["annotations"]["idempotentHint"]);
                Assert.False(string.IsNullOrWhiteSpace((string)tool["title"]));
            }
        }

        [Fact]
        public void Successful_host_tool_returns_structured_content_and_legacy_text()
        {
            var replies = Exchange(
                "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"horizun_target\",\"arguments\":{}}}");
            JObject result = replies[0]["result"] as JObject;
            Assert.NotNull(result?["structuredContent"]);
            Assert.Equal(JTokenType.Object, result["structuredContent"].Type);
            Assert.Equal("text", (string)result["content"][0]["type"]);
            Assert.False((bool)result["isError"]);
        }

        [Fact]
        public void One_malformed_line_does_not_stop_the_ones_after_it()
        {
            var replies = Exchange(
                "{not json at all",
                "{\"jsonrpc\":\"2.0\",\"id\":9,\"method\":\"tools/list\"}");

            Assert.True(FindError(replies, -32700) != null, "Expected the parse error. Got: " + Codes(replies));

            bool listed = false;
            foreach (JObject r in replies)
                if ((int?)r["id"] == 9 && r["result"]?["tools"] != null) listed = true;

            Assert.True(listed,
                "A single unparseable line must not cost the caller the requests behind it. Got: " + Codes(replies));
        }
    }
}
