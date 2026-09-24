// -----------------------------------------------------------------------------
// Horizun Server tests - original Horizun code.
//
// MCP 2026-07-28 elicitation: multi round-trip requests (SEP-2322).
//
// horizun_project_context operation=elicit under a modern request returns ONE form
// as an InputRequiredResult (resultType "input_required", inputRequests +
// requestState); the client calls again with inputResponses and the state echoed,
// and the intake advances until it answers with the ordinary result - drafted,
// dry_run by default, re-read when written. The state decides which answers are
// written to which file, so it is proved to be tamper-proof, bound to its
// arguments and client, expiring and single-use.
//
// Two layers: the tool in process under a modern ClientContext, and the REAL
// server over REAL stdio with no initialize at all, as a 2026-07-28 client talks.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Server.Tests
{
    public sealed class MrtrElicitationTests : IDisposable
    {
        private const string Tool = "horizun_project_context";
        private readonly string _dir;
        private readonly string _settingsRoot;
        private readonly string _savedRoot;

        public MrtrElicitationTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "hz-mrtr-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            _settingsRoot = Path.Combine(Path.GetTempPath(), "hz-mrtr-settings-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_settingsRoot);
            _savedRoot = Environment.GetEnvironmentVariable(HorizunPaths.RootOverrideVariable);
            Environment.SetEnvironmentVariable(HorizunPaths.RootOverrideVariable, _settingsRoot);
        }

        public void Dispose()
        {
            MrtrRequestState.Now = () => DateTimeOffset.UtcNow;
            Environment.SetEnvironmentVariable(HorizunPaths.RootOverrideVariable, _savedRoot);
            try { Directory.Delete(_dir, true); } catch { }
            try { Directory.Delete(_settingsRoot, true); } catch { }
        }

        private void Profile(string profile)
            => File.WriteAllText(HorizunPaths.SettingsPath(), @"{""permission_profile"":""" + profile + @"""}");

        private static ClientElicitationSupport Modern(JObject elicitation = null)
            => ClientElicitationSupport.FromModernRequest("2026-07-28",
                new JObject { ["elicitation"] = elicitation ?? new JObject() });

        /// <summary>One modern tools/call of the tool: its result, or the InputRequiredResult it raised.</summary>
        private static JObject Call(JObject args, JObject responses = null, string state = null,
                                    string principal = "mrtr-tests", ClientElicitationSupport support = null,
                                    string tool = Tool)
        {
            var a = (JObject)args.DeepClone();
            a["operation"] = "elicit";
            using (ClientContext.Enter(new ClientContext(support ?? Modern(), tool, responses, state, principal)))
            {
                try { return ProjectContext.Handle(a); }
                catch (InputRequiredException ir) { return new JObject { ["input_required"] = ir.Result }; }
            }
        }

        private static JProperty OnlyForm(JObject reply)
        {
            JObject ir = (JObject)reply["input_required"];
            Assert.NotNull(ir);
            return ((JObject)ir["inputRequests"]).Properties().Single();
        }

        private static JObject Accept(JProperty form, Dictionary<string, JToken> knows)
        {
            var content = new JObject();
            foreach (JProperty p in ((JObject)form.Value["params"]["requestedSchema"]["properties"]).Properties())
                if (knows.TryGetValue(p.Name, out JToken v)) content[p.Name] = v.DeepClone();
            return new JObject { [form.Name] = new JObject { ["action"] = "accept", ["content"] = content } };
        }

        private static JObject Act(JProperty form, string action)
            => new JObject { [form.Name] = new JObject { ["action"] = action } };

        // ---- capability ------------------------------------------------------------

        [Theory]
        [InlineData(@"{}", true, false, null)]
        [InlineData(@"{""form"":{}}", true, false, null)]
        [InlineData(@"{""form"":{},""url"":{}}", true, true, null)]
        [InlineData(@"{""url"":{}}", false, true, "form_mode_not_declared")]
        [InlineData(null, false, false, "client_did_not_declare")]
        public void The_request_own_capabilities_decide(string declared, bool form, bool url, string reason)
        {
            var caps = new JObject();
            if (declared != null) caps["elicitation"] = JObject.Parse(declared);
            ClientElicitationSupport s = ClientElicitationSupport.FromModernRequest("2026-07-28", caps);
            Assert.True(s.Mrtr);
            Assert.Equal(reason, s.UnsupportedReason);
            Assert.Equal(reason == null, s.CanElicitForm);
            Assert.Equal(form, s.Form);
            Assert.Equal(url, s.Url);
            Assert.Equal("input_required_result", (string)s.ToJson()["exchange"]);
            Assert.Equal("task_augmented_call", s.ForTask().UnsupportedReason);
        }

        [Fact]
        public void Without_the_capability_nothing_is_asked_and_the_refusal_is_machine_readable()
        {
            var none = ClientElicitationSupport.FromModernRequest("2026-07-28", new JObject());
            ToolRefusal r = Assert.Throws<ToolRefusal>(() =>
            {
                using (ClientContext.Enter(new ClientContext(none, Tool, null, null, null)))
                    ProjectContext.Handle(new JObject { ["operation"] = "elicit" });
            });
            Assert.Equal("elicitation_unsupported", (string)r.Detail["code"]);
            Assert.Equal("client_did_not_declare", (string)r.Detail["reason"]);
            Assert.Contains("clientCapabilities", r.Message);
        }

        [Fact]
        public void A_call_nested_inside_another_tool_cannot_return_input_required()
        {
            ToolRefusal r = Assert.Throws<ToolRefusal>(() =>
            {
                using (ClientContext.Enter(new ClientContext(Modern(), "horizun_run_procedure", null, null, null)))
                    ProjectContext.Handle(new JObject { ["operation"] = "elicit" });
            });
            Assert.Equal("nested_call", (string)r.Detail["reason"]);
        }

        // ---- the round trip ----------------------------------------------------------

        [Fact]
        public void First_call_returns_one_form_as_an_input_required_result_with_the_same_schema()
        {
            JObject reply = Call(new JObject { ["language"] = "es" });
            JProperty form = OnlyForm(reply);
            Assert.Equal("intake_form_1", form.Name);
            Assert.Equal("elicitation/create", (string)form.Value["method"]);
            Assert.Equal("form", (string)form.Value["params"]["mode"]);
            JObject props = (JObject)form.Value["params"]["requestedSchema"]["properties"];
            Assert.NotNull(props["project_code"]);
            Assert.True(props.Count <= ProjectContext.MaxFieldsPerForm);
            // The 2025-11-25 enum shape (oneOf const/title), not the deprecated enumNames.
            foreach (JProperty p in props.Properties()) Assert.Null(p.Value["enumNames"]);
            Assert.False(string.IsNullOrEmpty((string)reply["input_required"]["requestState"]));
        }

        [Fact]
        public void The_retry_applies_the_answers_asks_the_next_block_and_finally_writes_and_rereads()
        {
            Profile("full_write");
            string path = Path.Combine(_dir, "ctx.json");
            var args = new JObject { ["path"] = path, ["dry_run"] = false };
            var knows = new Dictionary<string, JToken> { ["project_code"] = "HZ01", ["project_name"] = "MRTR" };

            JObject first = Call(args);
            JProperty form1 = OnlyForm(first);
            string state1 = (string)first["input_required"]["requestState"];

            JObject second = Call(args, Accept(form1, knows), state1);
            JProperty form2 = OnlyForm(second);
            Assert.Equal("intake_form_2", form2.Name);
            Assert.False(File.Exists(path));   // nothing is written between rounds

            // The person closes the second form: the intake stops, what was answered is kept.
            JObject done = Call(args, Act(form2, "cancel"), (string)second["input_required"]["requestState"]);
            Assert.Null(done["input_required"]);
            Assert.Equal("cancelled", (string)done["stopped"]);
            Assert.Equal("HZ01", (string)done["answers"]["/project/code"]);
            Assert.True((bool)done["written"]);
            Assert.True((bool)done["draft"]["verification"]["reread"]);
            Assert.Equal("MRTR", (string)JObject.Parse(File.ReadAllText(path))["project"]["name"]);
            Assert.Equal(2, ((JArray)done["rounds"]).Count);
            Assert.Equal("input_required_result", (string)done["client_elicitation"]["exchange"]);
        }

        [Fact]
        public void Dry_run_stays_the_default_and_nothing_is_written()
        {
            string path = Path.Combine(_dir, "ctx.json");
            var args = new JObject { ["path"] = path };
            JObject first = Call(args);
            JObject second = Call(args, Accept(OnlyForm(first), new Dictionary<string, JToken> { ["project_code"] = "HZ01" }),
                                  (string)first["input_required"]["requestState"]);
            JObject done = Call(args, Act(OnlyForm(second), "cancel"), (string)second["input_required"]["requestState"]);
            Assert.True((bool)done["dry_run"]);
            Assert.False((bool)done["written"]);
            Assert.False(File.Exists(path));
            Assert.Equal("HZ01", (string)done["draft"]["document"]["project"]["code"]);
        }

        [Fact]
        public void A_declined_block_stays_open_and_the_next_block_is_asked()
        {
            JObject first = Call(new JObject());
            JProperty form1 = OnlyForm(first);
            JObject second = Call(new JObject(), Act(form1, "decline"), (string)first["input_required"]["requestState"]);
            JProperty form2 = OnlyForm(second);
            // Another section, never the declined one again.
            Assert.Null(form2.Value["params"]["requestedSchema"]["properties"]["project_code"]);
            JObject done = Call(new JObject(), Act(form2, "cancel"), (string)second["input_required"]["requestState"]);
            Assert.Equal("declined", (string)((JArray)done["unanswered"]).First(u => (string)u["id"] == "project_code")["reason"]);
        }

        [Fact]
        public void A_retry_without_the_answer_is_asked_again_and_the_state_is_not_burnt()
        {
            JObject first = Call(new JObject());
            string state = (string)first["input_required"]["requestState"];
            JObject again = Call(new JObject(), new JObject(), state);
            Assert.Equal(state, (string)again["input_required"]["requestState"]);
            Assert.Equal("intake_form_1", OnlyForm(again).Name);
            // Still usable.
            JObject next = Call(new JObject(), Act(OnlyForm(first), "decline"), state);
            Assert.NotNull(next["input_required"]);
        }

        // ---- the state is attacker-controlled input ------------------------------------

        private static string Reject(Func<JObject> call)
        {
            JObject reply = null;
            ToolRefusal r = Assert.Throws<ToolRefusal>(() => reply = call());
            Assert.Equal("request_state_rejected", (string)r.Detail["code"]);
            Assert.False((bool)r.Detail["written"]);
            return (string)r.Detail["reason"];
        }

        [Fact]
        public void A_tampered_state_is_refused_and_nothing_is_applied()
        {
            JObject first = Call(new JObject());
            string state = (string)first["input_required"]["requestState"];
            int dot = state.IndexOf('.');
            // Re-encode the payload with another collected answer, keep the old MAC.
            string payload = state.Substring(0, dot).Replace('-', '+').Replace('_', '/');
            payload += new string('=', (4 - payload.Length % 4) % 4);
            JObject body = JObject.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(payload)));
            body["collected"]["/project/code"] = "FORGED";
            string forged = Convert.ToBase64String(Encoding.UTF8.GetBytes(body.ToString(Newtonsoft.Json.Formatting.None)))
                                .TrimEnd('=').Replace('+', '-').Replace('/', '_') + state.Substring(dot);
            JObject answer = Act(OnlyForm(first), "decline");

            Assert.Equal("tampered", Reject(() => Call(new JObject(), answer, forged)));
            Assert.Equal("malformed", Reject(() => Call(new JObject(), answer, "not-a-state")));
        }

        [Fact]
        public void A_state_cannot_be_moved_to_another_path_or_client()
        {
            Profile("full_write");
            var args = new JObject { ["path"] = Path.Combine(_dir, "a.json") };
            JObject first = Call(args);
            string state = (string)first["input_required"]["requestState"];
            JObject answer = Accept(OnlyForm(first), new Dictionary<string, JToken> { ["project_code"] = "HZ01" });

            var other = new JObject { ["path"] = Path.Combine(_dir, "b.json"), ["dry_run"] = false };
            Assert.Equal("mismatch", Reject(() => Call(other, answer, state)));
            Assert.Equal("mismatch", Reject(() => Call(new JObject { ["path"] = (string)args["path"], ["dry_run"] = false }, answer, state)));
            Assert.Equal("principal_mismatch", Reject(() => Call(args, answer, state, principal: "someone-else")));
            Assert.False(File.Exists(Path.Combine(_dir, "b.json")));
        }

        [Fact]
        public void A_state_is_used_once()
        {
            JObject first = Call(new JObject());
            string state = (string)first["input_required"]["requestState"];
            JObject answer = Act(OnlyForm(first), "decline");
            Assert.NotNull(Call(new JObject(), answer, state)["input_required"]);
            Assert.Equal("replayed", Reject(() => Call(new JObject(), answer, state)));
        }

        [Fact]
        public void An_expired_state_is_refused_and_hands_the_answers_back_without_acting_on_them()
        {
            var args = new JObject { ["timeout_seconds"] = 10 };
            JObject first = Call(args);
            JObject second = Call(args, Accept(OnlyForm(first), new Dictionary<string, JToken> { ["project_code"] = "HZ01" }),
                                  (string)first["input_required"]["requestState"]);
            DateTimeOffset later = DateTimeOffset.UtcNow.AddSeconds(11);
            MrtrRequestState.Now = () => later;

            ToolRefusal r = Assert.Throws<ToolRefusal>(() =>
                Call(args, Act(OnlyForm(second), "cancel"), (string)second["input_required"]["requestState"]));
            Assert.Equal("expired", (string)r.Detail["reason"]);
            Assert.Equal("HZ01", (string)r.Detail["answers"]["/project/code"]);
        }

        [Fact]
        public void The_binding_ignores_property_order_and_nothing_else()
        {
            string a = MrtrRequestState.Binding(Tool, JObject.Parse(@"{""path"":""x.json"",""dry_run"":false}"));
            string b = MrtrRequestState.Binding(Tool, JObject.Parse(@"{""dry_run"":false,""path"":""x.json""}"));
            string c = MrtrRequestState.Binding(Tool, JObject.Parse(@"{""dry_run"":true,""path"":""x.json""}"));
            Assert.Equal(a, b);
            Assert.NotEqual(a, c);
        }
    }

    /// <summary>
    /// The real server over real stdio, as a 2026-07-28 client talks: no initialize,
    /// every request carrying its own version and capabilities.
    /// </summary>
    public sealed class MrtrElicitationWireTests
    {
        private sealed class ModernServer : IDisposable
        {
            private readonly Process _proc;
            private readonly BlockingCollection<JObject> _lines = new BlockingCollection<JObject>();
            private readonly string _root;

            public ModernServer()
            {
                _root = Path.Combine(Path.GetTempPath(), "hz-mrtr-wire-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(_root);
                var psi = new ProcessStartInfo(JsonRpcErrorCodeTests.ServerExe())
                {
                    RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                    UseShellExecute = false, CreateNoWindow = true, StandardOutputEncoding = new UTF8Encoding(false)
                };
                psi.Environment[HorizunPaths.RootOverrideVariable] = _root;
                _proc = Process.Start(psi);
                _proc.ErrorDataReceived += (s, e) => { };
                _proc.BeginErrorReadLine();
                Task.Run(() =>
                {
                    string line;
                    while ((line = _proc.StandardOutput.ReadLine()) != null)
                    {
                        try { _lines.Add(JObject.Parse(line)); } catch { }
                    }
                    _lines.CompleteAdding();
                });
            }

            public JObject Call(string id, JObject prms)
            {
                var message = new JObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = "tools/call", ["params"] = prms };
                _proc.StandardInput.WriteLine(message.ToString(Newtonsoft.Json.Formatting.None));
                _proc.StandardInput.Flush();
                var clock = Stopwatch.StartNew();
                while (clock.ElapsedMilliseconds < 30000)
                {
                    if (!_lines.TryTake(out JObject m, 500)) { if (_lines.IsCompleted) break; continue; }
                    // A server-to-client request would be a violation of the revision.
                    Assert.NotEqual("elicitation/create", (string)m["method"]);
                    if ((string)m["id"] == id) return m;
                }
                throw new Xunit.Sdk.XunitException("no answer to " + id);
            }

            public void Dispose()
            {
                try { _proc.StandardInput.Close(); } catch { }
                if (!_proc.WaitForExit(30000)) { try { _proc.Kill(); } catch { } }
                _proc.Dispose();
                try { Directory.Delete(_root, true); } catch { }
            }
        }

        private static JObject Params(JObject capabilities, JObject extra = null)
        {
            var p = new JObject
            {
                ["name"] = "horizun_project_context",
                ["arguments"] = new JObject { ["operation"] = "elicit", ["language"] = "en" },
                ["_meta"] = new JObject
                {
                    ["io.modelcontextprotocol/protocolVersion"] = "2026-07-28",
                    ["io.modelcontextprotocol/clientCapabilities"] = capabilities,
                    ["io.modelcontextprotocol/clientInfo"] = new JObject { ["name"] = "mrtr-wire", ["version"] = "1" }
                }
            };
            if (extra != null) foreach (JProperty x in extra.Properties()) p[x.Name] = x.Value.DeepClone();
            return p;
        }

        [Fact]
        public void Input_required_then_retry_then_the_complete_result()
        {
            using (var server = new ModernServer())
            {
                var caps = new JObject { ["elicitation"] = new JObject { ["form"] = new JObject() } };
                JObject first = server.Call("m1", Params(caps));
                JObject r1 = (JObject)first["result"];
                Assert.Equal("input_required", (string)r1["resultType"]);
                Assert.Null(r1["content"]);
                Assert.Null(r1["ttlMs"]);   // interim results are not cacheable
                JProperty form = ((JObject)r1["inputRequests"]).Properties().Single();
                Assert.Equal("elicitation/create", (string)form.Value["method"]);

                JObject second = server.Call("m2", Params(caps, new JObject
                {
                    ["inputResponses"] = new JObject
                    {
                        [form.Name] = new JObject { ["action"] = "accept", ["content"] = new JObject { ["project_code"] = "HZ01" } }
                    },
                    ["requestState"] = r1["requestState"]
                }));
                JObject r2 = (JObject)second["result"];
                Assert.Equal("input_required", (string)r2["resultType"]);
                JProperty form2 = ((JObject)r2["inputRequests"]).Properties().Single();

                JObject third = server.Call("m3", Params(caps, new JObject
                {
                    ["inputResponses"] = new JObject { [form2.Name] = new JObject { ["action"] = "cancel" } },
                    ["requestState"] = r2["requestState"]
                }));
                JObject r3 = (JObject)third["result"];
                Assert.Equal("complete", (string)r3["resultType"]);
                Assert.NotEqual(true, (bool?)r3["isError"]);
                JObject s = (JObject)r3["structuredContent"];
                Assert.Equal("HZ01", (string)s["answers"]["/project/code"]);
                Assert.Equal("cancelled", (string)s["stopped"]);
                Assert.False((bool)s["written"]);

                // The same state again: consumed.
                JObject replay = server.Call("m4", Params(caps, new JObject
                {
                    ["inputResponses"] = new JObject { [form2.Name] = new JObject { ["action"] = "cancel" } },
                    ["requestState"] = r2["requestState"]
                }));
                Assert.True((bool)replay["result"]["isError"]);
                Assert.Equal("request_state_rejected", (string)replay["result"]["structuredContent"]["code"]);
                Assert.Equal("replayed", (string)replay["result"]["structuredContent"]["reason"]);
            }
        }

        [Fact]
        public void A_modern_request_without_the_capability_is_told_so_and_a_malformed_retry_is_invalid_params()
        {
            using (var server = new ModernServer())
            {
                JObject refused = server.Call("n1", Params(new JObject()));
                Assert.Equal("complete", (string)refused["result"]["resultType"]);
                Assert.True((bool)refused["result"]["isError"]);
                Assert.Equal("elicitation_unsupported", (string)refused["result"]["structuredContent"]["code"]);
                Assert.Equal("client_did_not_declare", (string)refused["result"]["structuredContent"]["reason"]);

                var caps = new JObject { ["elicitation"] = new JObject() };
                JObject bad = server.Call("n2", Params(caps, new JObject { ["inputResponses"] = new JArray() }));
                Assert.Equal(-32602, (int)bad["error"]["code"]);
                JObject bad2 = server.Call("n3", Params(caps, new JObject { ["requestState"] = 5 }));
                Assert.Equal(-32602, (int)bad2["error"]["code"]);
            }
        }

        [Fact]
        public void An_unknown_log_level_is_invalid_params()
        {
            using (var server = new ModernServer())
            {
                JObject prms = Params(new JObject());
                prms["_meta"]["io.modelcontextprotocol/logLevel"] = "verbose";
                JObject reply = server.Call("l1", prms);
                Assert.Equal(-32602, (int)reply["error"]["code"]);
            }
        }
    }
}
