// -----------------------------------------------------------------------------
// Horizun Server tests - original Horizun code.
//
// MCP elicitation: requests from the server TO the client, and the intake that
// uses them (horizun_project_context operation=elicit).
//
// Two layers, both real code:
//
//   * an IN-MEMORY client that sits on the other end of McpClientRequests - it sees
//     every line the server writes and answers on its own thread, exactly as the
//     reader thread hands responses over in production. It declares or does not
//     declare the capability, and answers accept / decline / cancel, answers late,
//     or never;
//   * the REAL server executable over REAL stdio, with a client that reads the
//     elicitation/create off stdout and answers on stdin while other requests are
//     in flight - the only way to prove the reader is not blocked by a tool that
//     is waiting for the client.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Server.Tests
{
    /// <summary>
    /// The other end of the wire, in memory. Every request the server writes is handed
    /// to <see cref="Respond"/> on a separate thread; whatever it returns is delivered
    /// back through TryDeliver, the same call the reader makes.
    /// </summary>
    internal sealed class InMemoryClient
    {
        public readonly McpClientRequests Channel;
        public readonly ConcurrentQueue<JObject> Written = new ConcurrentQueue<JObject>();

        /// <summary>request -> response object (with id), or null for "never answer".</summary>
        public Func<JObject, JObject> Respond = _ => null;

        public InMemoryClient()
        {
            Channel = new McpClientRequests(Write);
        }

        private bool Write(JObject message)
        {
            Written.Enqueue((JObject)message.DeepClone());
            if (message["method"] != null && message["id"] != null)
            {
                JObject copy = (JObject)message.DeepClone();
                Task.Run(() =>
                {
                    JObject response = Respond(copy);
                    if (response != null) Channel.TryDeliver(response);
                });
            }
            return true;
        }

        public List<JObject> Requests(string method)
            => Written.Where(m => (string)m["method"] == method && m["id"] != null).ToList();

        public static JObject Result(JObject request, JObject result)
            => new JObject { ["jsonrpc"] = "2.0", ["id"] = request["id"].DeepClone(), ["result"] = result };

        public static JObject Accept(JObject request, JObject content)
            => Result(request, new JObject { ["action"] = "accept", ["content"] = content });

        public static JObject Action(JObject request, string action)
            => Result(request, new JObject { ["action"] = action });
    }

    public sealed class ClientRequestChannelTests
    {
        [Fact]
        public void A_response_is_recognised_by_shape_before_any_request_rule()
        {
            Assert.True(McpClientRequests.IsResponse(JObject.Parse(@"{""jsonrpc"":""2.0"",""id"":""horizun-server-1"",""result"":{}}")));
            Assert.True(McpClientRequests.IsResponse(JObject.Parse(@"{""jsonrpc"":""2.0"",""id"":""horizun-server-1"",""error"":{""code"":-1,""message"":""x""}}")));
            Assert.False(McpClientRequests.IsResponse(JObject.Parse(@"{""jsonrpc"":""2.0"",""id"":1,""method"":""ping""}")));
            Assert.False(McpClientRequests.IsResponse(JObject.Parse(@"{""jsonrpc"":""2.0"",""method"":""notifications/cancelled""}")));
            // No method and no result: a malformed REQUEST, still refused by the request path.
            Assert.False(McpClientRequests.IsResponse(JObject.Parse(@"{""jsonrpc"":""2.0"",""id"":5}")));
        }

        [Fact]
        public void A_request_carries_a_server_owned_id_and_gets_its_own_result()
        {
            var client = new InMemoryClient();
            client.Respond = req => InMemoryClient.Result(req, new JObject { ["echo"] = req["params"]["n"] });

            ClientReply reply = client.Channel.Send("test/echo", new JObject { ["n"] = 7 }, 5000, CancellationToken.None);

            Assert.Equal(ClientReplyOutcome.Result, reply.Outcome);
            Assert.Equal(7, (int)reply.Result["echo"]);
            Assert.StartsWith(McpClientRequests.IdPrefix, reply.RequestId);
            JObject sent = client.Requests("test/echo").Single();
            Assert.Equal(JTokenType.String, sent["id"].Type);
            Assert.Equal("2.0", (string)sent["jsonrpc"]);
            Assert.Equal(0, client.Channel.PendingCount);
        }

        [Fact]
        public async Task Interleaved_answers_reach_the_request_that_owns_them()
        {
            var client = new InMemoryClient();
            // The FIRST request is answered LAST: it waits until the second has been answered.
            var secondAnswered = new ManualResetEventSlim();
            client.Respond = req =>
            {
                int n = (int)req["params"]["n"];
                if (n == 1) secondAnswered.Wait(5000);
                var response = InMemoryClient.Result(req, new JObject { ["n"] = n });
                if (n == 2) { client.Channel.TryDeliver(response); secondAnswered.Set(); return null; }
                return response;
            };

            Task<ClientReply> first = Task.Run(() => client.Channel.Send("test/n", new JObject { ["n"] = 1 }, 10000, CancellationToken.None));
            Thread.Sleep(50);
            Task<ClientReply> second = Task.Run(() => client.Channel.Send("test/n", new JObject { ["n"] = 2 }, 10000, CancellationToken.None));

            ClientReply secondReply = await second;
            ClientReply firstReply = await first;
            Assert.Equal(2, (int)secondReply.Result["n"]);
            Assert.Equal(1, (int)firstReply.Result["n"]);
            Assert.NotEqual(firstReply.RequestId, secondReply.RequestId);
        }

        [Fact]
        public void An_error_response_is_reported_as_the_clients_error()
        {
            var client = new InMemoryClient();
            client.Respond = req => new JObject
            {
                ["jsonrpc"] = "2.0", ["id"] = req["id"].DeepClone(),
                ["error"] = new JObject { ["code"] = -32602, ["message"] = "mode not declared" }
            };
            ClientReply reply = client.Channel.Send("elicitation/create", new JObject(), 5000, CancellationToken.None);
            Assert.Equal(ClientReplyOutcome.Error, reply.Outcome);
            Assert.Equal(-32602, reply.ErrorCode);
            Assert.Equal("mode not declared", reply.ErrorMessage);
        }

        [Fact]
        public void A_timeout_ends_the_wait_and_tells_the_client_it_stopped_waiting()
        {
            var client = new InMemoryClient();          // never answers
            var clock = Stopwatch.StartNew();
            ClientReply reply = client.Channel.Send("elicitation/create", new JObject(), 200, CancellationToken.None);

            Assert.Equal(ClientReplyOutcome.TimedOut, reply.Outcome);
            Assert.True(clock.ElapsedMilliseconds < 5000);
            JObject cancelled = client.Written.Single(m => (string)m["method"] == "notifications/cancelled");
            Assert.Equal(reply.RequestId, (string)cancelled["params"]["requestId"]);
            Assert.Null(cancelled["id"]);
            Assert.Equal(0, client.Channel.PendingCount);

            // A late answer matches nothing and is dropped, never delivered to a later request.
            Assert.False(client.Channel.TryDeliver(new JObject
            {
                ["jsonrpc"] = "2.0", ["id"] = reply.RequestId, ["result"] = new JObject { ["action"] = "accept" }
            }));
        }

        [Fact]
        public void Cancelling_the_tool_call_ends_the_wait()
        {
            var client = new InMemoryClient();
            using (var cts = new CancellationTokenSource(150))
            {
                ClientReply reply = client.Channel.Send("elicitation/create", new JObject(), 30000, cts.Token);
                Assert.Equal(ClientReplyOutcome.Cancelled, reply.Outcome);
            }
            Assert.Contains(client.Written, m => (string)m["method"] == "notifications/cancelled");
        }

        [Fact]
        public async Task Closing_the_channel_fails_every_pending_request_at_once_and_refuses_new_ones()
        {
            var client = new InMemoryClient();
            Task<ClientReply> waiting = Task.Run(() => client.Channel.Send("elicitation/create", new JObject(), 60000, CancellationToken.None));
            SpinWait.SpinUntil(() => client.Channel.PendingCount == 1, 5000);

            client.Channel.FailAll("stdin closed");
            Assert.True(await Task.WhenAny(waiting, Task.Delay(5000)) == waiting,
                "a pending request must end when the channel closes, not at its timeout");
            Assert.Equal(ClientReplyOutcome.ChannelLost, (await waiting).Outcome);

            ClientReply after = client.Channel.Send("elicitation/create", new JObject(), 60000, CancellationToken.None);
            Assert.Equal(ClientReplyOutcome.ChannelLost, after.Outcome);
            Assert.Single(client.Requests("elicitation/create"));
        }

        [Fact]
        public void An_unknown_or_non_string_id_matches_nothing()
        {
            var client = new InMemoryClient();
            Assert.False(client.Channel.TryDeliver(JObject.Parse(@"{""jsonrpc"":""2.0"",""id"":1,""result"":{}}")));
            Assert.False(client.Channel.TryDeliver(JObject.Parse(@"{""jsonrpc"":""2.0"",""id"":""horizun-server-999"",""result"":{}}")));
        }

        [Theory]
        [InlineData("2025-06-18", @"{}", true, false, null)]
        [InlineData("2025-11-25", @"{}", true, false, null)]
        [InlineData("2025-11-25", @"{""form"":{}}", true, false, null)]
        [InlineData("2025-11-25", @"{""form"":{},""url"":{}}", true, true, null)]
        [InlineData("2025-11-25", @"{""url"":{}}", false, true, "form_mode_not_declared")]
        [InlineData("2025-11-25", null, false, false, "client_did_not_declare")]
        [InlineData("2025-03-26", @"{}", false, false, "protocol_version")]
        [InlineData("2024-11-05", @"{}", false, false, "protocol_version")]
        public void The_declared_capability_and_the_negotiated_revision_decide(
            string version, string declared, bool form, bool url, string reason)
        {
            var caps = new JObject();
            if (declared != null) caps["elicitation"] = JObject.Parse(declared);
            ClientElicitationSupport s = ClientElicitationSupport.FromInitialize(version, caps);
            Assert.Equal(reason, s.UnsupportedReason);
            Assert.Equal(reason == null && form, s.CanElicitForm);
            if (reason == null || reason == "form_mode_not_declared")
            {
                Assert.Equal(form, s.Form);
                Assert.Equal(url, s.Url);
            }
        }

        [Fact]
        public void Modern_calls_round_trip_and_task_augmented_calls_are_told_why_they_cannot_elicit()
        {
            // 2026-07-28 elicits through InputRequiredResult (see MrtrElicitationTests), never a request.
            Assert.True(ClientElicitationSupport.FromModernRequest("2026-07-28", new JObject { ["elicitation"] = new JObject() }).Mrtr);
            ClientElicitationSupport ok = ClientElicitationSupport.FromInitialize("2025-11-25", new JObject { ["elicitation"] = new JObject() });
            Assert.Equal("task_augmented_call", ok.ForTask().UnsupportedReason);
            Assert.False(ok.ForTask().CanElicitForm);
        }

        [Fact]
        public void The_form_request_names_its_mode_only_where_the_revision_has_one()
        {
            foreach (string version in new[] { "2025-06-18", "2025-11-25" })
            {
                var client = new InMemoryClient();
                client.Respond = req => InMemoryClient.Action(req, "decline");
                var ctx = new ClientContext(client.Channel,
                    ClientElicitationSupport.FromInitialize(version, new JObject { ["elicitation"] = new JObject() }));
                ctx.ElicitForm("m", new JObject { ["type"] = "object", ["properties"] = new JObject() }, 5000, CancellationToken.None);
                JObject prms = (JObject)client.Requests("elicitation/create").Single()["params"];
                Assert.Equal("m", (string)prms["message"]);
                Assert.NotNull(prms["requestedSchema"]);
                if (version == "2025-11-25") Assert.Equal("form", (string)prms["mode"]);
                else Assert.Null(prms["mode"]);
            }
        }
    }

    public sealed class ProjectContextElicitTests : IDisposable
    {
        private readonly string _dir;
        private readonly string _settingsRoot;
        private readonly string _savedRoot;

        public ProjectContextElicitTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "hz-el-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            _settingsRoot = Path.Combine(Path.GetTempPath(), "hz-el-settings-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_settingsRoot);
            _savedRoot = Environment.GetEnvironmentVariable(HorizunPaths.RootOverrideVariable);
            Environment.SetEnvironmentVariable(HorizunPaths.RootOverrideVariable, _settingsRoot);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(HorizunPaths.RootOverrideVariable, _savedRoot);
            try { Directory.Delete(_dir, true); } catch { }
            try { Directory.Delete(_settingsRoot, true); } catch { }
        }

        private void Profile(string profile)
            => File.WriteAllText(HorizunPaths.SettingsPath(), @"{""permission_profile"":""" + profile + @"""}");

        private static JObject Elicit(InMemoryClient client, string version, JObject args)
        {
            var support = ClientElicitationSupport.FromInitialize(version, new JObject { ["elicitation"] = new JObject() });
            using (ClientContext.Enter(new ClientContext(client.Channel, support)))
            {
                args["operation"] = "elicit";
                return ProjectContext.Handle(args);
            }
        }

        /// <summary>A scripted person: answers each form from a table of question id -> value.</summary>
        private static Func<JObject, JObject> Person(Dictionary<string, JToken> knows, string actionForSection = null,
                                                     string section = null)
            => req =>
            {
                JObject props = (JObject)req["params"]["requestedSchema"]["properties"];
                string message = (string)req["params"]["message"];
                if (section != null && message.Contains(section)) return InMemoryClient.Action(req, actionForSection);
                var content = new JObject();
                foreach (JProperty p in props.Properties())
                    if (knows.TryGetValue(p.Name, out JToken v)) content[p.Name] = v.DeepClone();
                return InMemoryClient.Accept(req, content);
            };

        private static string Reason(JObject reply, string id)
            => (string)((JArray)reply["unanswered"]).Single(u => (string)u["id"] == id)["reason"];

        [Fact]
        public void Without_a_declared_capability_it_refuses_machine_readably_and_asks_nothing()
        {
            // No context at all (a unit call, a procedure step) and a client that did not declare.
            ToolRefusal none = Assert.Throws<ToolRefusal>(() => ProjectContext.Handle(new JObject { ["operation"] = "elicit" }));
            Assert.Equal("elicitation_unsupported", (string)none.Detail["code"]);
            Assert.Equal("ask_in_chat", (string)none.Detail["fallback"]);

            var client = new InMemoryClient();
            var undeclared = ClientElicitationSupport.FromInitialize("2025-11-25", new JObject());
            using (ClientContext.Enter(new ClientContext(client.Channel, undeclared)))
            {
                ToolRefusal refused = Assert.Throws<ToolRefusal>(() => ProjectContext.Handle(new JObject { ["operation"] = "elicit" }));
                Assert.Equal("elicitation_unsupported", (string)refused.Detail["code"]);
                Assert.Equal("client_did_not_declare", (string)refused.Detail["reason"]);
                Assert.StartsWith("elicitation_unsupported", refused.Message);
            }
            Assert.Empty(client.Written);
        }

        [Fact]
        public void Accepted_forms_are_applied_like_draft_and_everything_else_is_listed_not_invented()
        {
            var client = new InMemoryClient();
            client.Respond = Person(new Dictionary<string, JToken>
            {
                ["project_code"] = "HZ01",
                ["project_name"] = "  Test building  ",
                ["appointment_role"] = "lead_appointed_party",
                ["eir_status"] = "missing",
                ["cde_platform"] = "local",
                ["revit_year"] = 2026
            });

            JObject reply = Elicit(client, "2025-11-25", new JObject { ["language"] = "es" });

            Assert.Null((string)reply["stopped"]);
            Assert.True((bool)reply["dry_run"]);
            Assert.False((bool)reply["written"]);
            JObject answers = (JObject)reply["answers"];
            Assert.Equal("HZ01", (string)answers["/project/code"]);
            Assert.Equal("Test building", (string)answers["/project/name"]);          // trimmed, not rewritten
            Assert.Equal("missing", (string)answers["/documents/eir/status"]);
            Assert.Equal(2026, (int)answers["/software/revit_year"]);
            Assert.Equal("HZ01", (string)reply["draft"]["document"]["project"]["code"]);
            Assert.Equal("draft", (string)reply["draft"]["operation"]);

            // Blank fields are unknowns, lists are for the chat, and dependents follow their answer.
            Assert.Equal("left_blank", Reason(reply, "stage"));
            Assert.Equal("not_elicitable", Reason(reply, "task_teams"));
            Assert.DoesNotContain((JArray)reply["unanswered"], u => (string)u["id"] == "eir_path");     // EIR missing: no path
            Assert.DoesNotContain((JArray)reply["unanswered"], u => (string)u["id"] == "cde_project_ref"); // local CDE
            Assert.DoesNotContain(client.Requests("elicitation/create"),
                r => r["params"]["requestedSchema"]["properties"]["eir_path"] != null);

            // Short forms, Spanish texts, typed enums as 2025-11-25 spells them, nothing required.
            List<JObject> forms = client.Requests("elicitation/create");
            Assert.All(forms, f => Assert.True(((JObject)f["params"]["requestedSchema"]["properties"]).Count <= ProjectContext.MaxFieldsPerForm));
            Assert.All(forms, f => Assert.Null(f["params"]["requestedSchema"]["required"]));
            JObject role = (JObject)forms.First(f => f["params"]["requestedSchema"]["properties"]["appointment_role"] != null)
                ["params"]["requestedSchema"]["properties"]["appointment_role"];
            Assert.Equal("string", (string)role["type"]);
            Assert.Contains(role["oneOf"], o => (string)o["const"] == "appointing_party" && ((string)o["title"]).StartsWith("Parte que designa"));
            Assert.StartsWith("¿Qué papel", (string)role["title"]);
            Assert.All(forms, f => Assert.Contains("Arranque ISO 19650", (string)f["params"]["message"]));
        }

        [Fact]
        public void Revision_2025_06_18_gets_enum_with_enumNames_and_no_mode()
        {
            var client = new InMemoryClient();
            client.Respond = Person(new Dictionary<string, JToken> { ["project_code"] = "HZ01" });
            Elicit(client, "2025-06-18", new JObject());
            JObject form = client.Requests("elicitation/create")
                .First(f => f["params"]["requestedSchema"]["properties"]["appointment_role"] != null);
            JObject role = (JObject)form["params"]["requestedSchema"]["properties"]["appointment_role"];
            Assert.Null(form["params"]["mode"]);
            Assert.Null(role["oneOf"]);
            Assert.Equal(new[] { "appointing_party", "lead_appointed_party", "appointed_party" }, role["enum"].Select(t => (string)t));
            Assert.Equal(3, ((JArray)role["enumNames"]).Count);
        }

        [Fact]
        public void A_value_outside_the_options_is_rejected_never_coerced()
        {
            var client = new InMemoryClient();
            client.Respond = Person(new Dictionary<string, JToken>
            {
                ["project_code"] = "HZ01", ["appointment_role"] = "client", ["revit_year"] = "2026"
            });
            JObject reply = Elicit(client, "2025-11-25", new JObject());
            Assert.Equal("not_an_option", Reason(reply, "appointment_role"));
            Assert.Equal("not_an_integer", Reason(reply, "revit_year"));
            Assert.Null(reply["answers"]["/appointment/role"]);
            JObject round = ((JArray)reply["rounds"]).OfType<JObject>().First(r => (string)r["section"] == "appointment");
            Assert.Contains(round["rejected"], x => (string)x["id"] == "appointment_role");
        }

        [Fact]
        public void A_declined_block_stays_open_and_the_intake_moves_on()
        {
            var client = new InMemoryClient();
            client.Respond = Person(new Dictionary<string, JToken> { ["project_code"] = "HZ01", ["stage"] = "Concept" },
                                    "decline", "Designación");
            JObject reply = Elicit(client, "2025-11-25", new JObject { ["language"] = "es" });
            Assert.Null((string)reply["stopped"]);
            Assert.Equal("declined", Reason(reply, "appointment_role"));
            Assert.Equal("declined", Reason(reply, "organisation_code"));
            Assert.Equal("Concept", (string)reply["answers"]["/appointment/stage"]);     // the next topic was still asked
            Assert.Contains(reply["rounds"], r => (string)r["action"] == "decline");
        }

        [Fact]
        public void A_cancelled_form_ends_the_intake_and_nothing_after_it_is_asked()
        {
            var client = new InMemoryClient();
            client.Respond = Person(new Dictionary<string, JToken> { ["project_code"] = "HZ01" }, "cancel", "Appointment");
            JObject reply = Elicit(client, "2025-11-25", new JObject());
            Assert.Equal("cancelled", (string)reply["stopped"]);
            Assert.Equal("cancelled", Reason(reply, "appointment_role"));
            Assert.Equal("not_asked_cancelled", Reason(reply, "stage"));
            Assert.Equal(2, client.Requests("elicitation/create").Count);
            Assert.Equal("HZ01", (string)reply["answers"]["/project/code"]);
        }

        [Fact]
        public void A_form_nobody_answers_times_out_and_is_reported_as_such()
        {
            var client = new InMemoryClient();   // never answers
            JObject reply = Elicit(client, "2025-11-25", new JObject { ["timeout_seconds"] = 10 });
            Assert.Equal("timed_out", (string)reply["stopped"]);
            Assert.Equal("timed_out", Reason(reply, "project_code"));
            Assert.Equal("not_asked_timed_out", Reason(reply, "appointment_role"));
            Assert.Equal(JTokenType.Null, reply["draft"].Type);
            Assert.False((bool)reply["written"]);
            Assert.Contains(client.Written, m => (string)m["method"] == "notifications/cancelled");
        }

        [Fact]
        public void A_dependent_question_is_asked_in_a_follow_up_form_once_its_answer_allows_it()
        {
            var client = new InMemoryClient();
            client.Respond = Person(new Dictionary<string, JToken>
            {
                ["project_code"] = "HZ01", ["eir_status"] = "approved", ["eir_path"] = "docs/eir.pdf"
            });
            JObject reply = Elicit(client, "2025-11-25", new JObject());
            List<JObject> eirRounds = ((JArray)reply["rounds"]).OfType<JObject>().Where(r => (string)r["section"] == "eir").ToList();
            Assert.Equal(2, eirRounds.Count);
            Assert.Equal(new[] { "eir_status" }, eirRounds[0]["asked"].Select(t => (string)t));
            Assert.Equal(new[] { "eir_path" }, eirRounds[1]["asked"].Select(t => (string)t));
            Assert.Equal("docs/eir.pdf", (string)reply["answers"]["/documents/eir/path"]);
        }

        [Fact]
        public void Earlier_answers_are_not_asked_again()
        {
            var client = new InMemoryClient();
            client.Respond = Person(new Dictionary<string, JToken>());
            JObject reply = Elicit(client, "2025-11-25", new JObject
            {
                ["answers"] = new JObject { ["/project/code"] = "HZ01", ["/project/name"] = "Kept" }
            });
            Assert.DoesNotContain(client.Requests("elicitation/create"),
                r => r["params"]["requestedSchema"]["properties"]["project_code"] != null);
            Assert.Equal("Kept", (string)reply["answers"]["/project/name"]);
        }

        [Fact]
        public void A_write_that_would_be_refused_is_refused_before_the_first_form()
        {
            Profile("full_write");
            string path = Path.Combine(_dir, "ctx.json");
            File.WriteAllText(path, @"{ ""schema_version"": 1, ""project"": { ""code"": ""HZ01"" } }");
            var client = new InMemoryClient();
            client.Respond = Person(new Dictionary<string, JToken> { ["project_name"] = "X" });

            ToolRefusal refused = Assert.Throws<ToolRefusal>(() =>
                Elicit(client, "2025-11-25", new JObject { ["path"] = path, ["dry_run"] = false }));
            Assert.Contains("overwrite", refused.Message);
            Assert.Empty(client.Written);

            Profile("safe_write");
            refused = Assert.Throws<ToolRefusal>(() =>
                Elicit(client, "2025-11-25", new JObject { ["path"] = path, ["dry_run"] = false, ["overwrite"] = true }));
            Assert.Contains("profile", refused.Message);
            Assert.Empty(client.Written);
        }

        [Fact]
        public void With_dry_run_false_it_writes_through_draft_and_reads_the_file_back()
        {
            Profile("full_write");
            string path = Path.Combine(_dir, "ctx.json");
            var client = new InMemoryClient();
            client.Respond = Person(new Dictionary<string, JToken> { ["project_code"] = "HZ01", ["project_name"] = "Written" });

            JObject reply = Elicit(client, "2025-11-25", new JObject { ["path"] = path, ["dry_run"] = false });

            Assert.True((bool)reply["written"]);
            Assert.True((bool)reply["draft"]["verification"]["reread"]);
            JObject onDisk = JObject.Parse(File.ReadAllText(path));
            Assert.Equal("Written", (string)onDisk["project"]["name"]);
            Assert.Contains("appointment", onDisk["intake"]["missing"].Select(t => (string)t));
        }
    }

    /// <summary>
    /// The whole path through the real server: initialize with the capability, a
    /// tools/call that elicits, the elicitation/create read off stdout, a ping answered
    /// WHILE the form is open, and the answer sent back on stdin.
    /// </summary>
    public sealed class ElicitationWireTests
    {
        private sealed class LiveServer : IDisposable
        {
            private readonly Process _proc;
            private readonly BlockingCollection<JObject> _lines = new BlockingCollection<JObject>();
            private readonly string _root;

            public LiveServer()
            {
                _root = Path.Combine(Path.GetTempPath(), "hz-el-wire-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(_root);
                var psi = new ProcessStartInfo(JsonRpcErrorCodeTests.ServerExe())
                {
                    RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                    UseShellExecute = false, CreateNoWindow = true, StandardOutputEncoding = new UTF8Encoding(false)
                };
                // A data root of its own: never the machine's settings, discovery or logs.
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

            public void Send(string json) { _proc.StandardInput.WriteLine(json); _proc.StandardInput.Flush(); }

            public void Send(JObject message) => Send(message.ToString(Newtonsoft.Json.Formatting.None));

            /// <summary>The next line satisfying the predicate; the lines skipped are returned too.</summary>
            public JObject Next(Func<JObject, bool> match, List<JObject> seen = null, int timeoutMs = 30000)
            {
                var clock = Stopwatch.StartNew();
                while (clock.ElapsedMilliseconds < timeoutMs)
                {
                    JObject m;
                    if (!_lines.TryTake(out m, 500)) { if (_lines.IsCompleted) break; continue; }
                    seen?.Add(m);
                    if (match(m)) return m;
                }
                throw new Xunit.Sdk.XunitException("the server did not send the expected line within " + timeoutMs + " ms");
            }

            public void Initialize(JObject capabilities)
            {
                Send(new JObject
                {
                    ["jsonrpc"] = "2.0", ["id"] = "init", ["method"] = "initialize",
                    ["params"] = new JObject
                    {
                        ["protocolVersion"] = "2025-11-25", ["capabilities"] = capabilities,
                        ["clientInfo"] = new JObject { ["name"] = "elicitation-wire-tests", ["version"] = "1" }
                    }
                });
                Next(m => (string)m["id"] == "init");
                Send(@"{""jsonrpc"":""2.0"",""method"":""notifications/initialized"",""params"":{}}");
            }

            public void Dispose()
            {
                try { _proc.StandardInput.Close(); } catch { }
                if (!_proc.WaitForExit(30000)) { try { _proc.Kill(); } catch { } }
                _proc.Dispose();
                try { Directory.Delete(_root, true); } catch { }
            }
        }

        private static JObject ElicitCall(string id) => new JObject
        {
            ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = "tools/call",
            ["params"] = new JObject
            {
                ["name"] = "horizun_project_context",
                ["arguments"] = new JObject { ["operation"] = "elicit", ["language"] = "es" }
            }
        };

        [Fact]
        public void Elicit_round_trips_over_stdio_while_the_reader_keeps_answering()
        {
            using (var server = new LiveServer())
            {
                server.Initialize(new JObject { ["elicitation"] = new JObject() });
                server.Send(ElicitCall("call-1"));

                JObject form = server.Next(m => (string)m["method"] == "elicitation/create");
                Assert.StartsWith(McpClientRequests.IdPrefix, (string)form["id"]);
                Assert.Equal("form", (string)form["params"]["mode"]);
                Assert.NotNull(form["params"]["requestedSchema"]["properties"]["project_code"]);

                // THE READER IS NOT BLOCKED: a request sent while the form is open is answered
                // before the form is. A client id that looks like ours is still the client's.
                server.Send(@"{""jsonrpc"":""2.0"",""id"":""p1"",""method"":""ping"",""params"":{}}");
                JObject pong = server.Next(m => (string)m["id"] == "p1");
                Assert.NotNull(pong["result"]);

                server.Send(new JObject
                {
                    ["jsonrpc"] = "2.0", ["id"] = form["id"].DeepClone(),
                    ["result"] = new JObject { ["action"] = "accept", ["content"] = new JObject { ["project_code"] = "HZ01" } }
                });

                // The next form is cancelled: the intake stops and the call answers.
                JObject second = server.Next(m => (string)m["method"] == "elicitation/create");
                Assert.NotEqual((string)form["id"], (string)second["id"]);
                server.Send(new JObject
                {
                    ["jsonrpc"] = "2.0", ["id"] = second["id"].DeepClone(),
                    ["result"] = new JObject { ["action"] = "cancel" }
                });

                JObject reply = server.Next(m => (string)m["id"] == "call-1");
                JObject s = (JObject)reply["result"]["structuredContent"];
                Assert.NotEqual(true, (bool?)reply["result"]["isError"]);
                Assert.Equal("HZ01", (string)s["answers"]["/project/code"]);
                Assert.Equal("cancelled", (string)s["stopped"]);
                Assert.False((bool)s["written"]);
                Assert.Equal("incomplete", (string)s["draft"]["state"]);
            }
        }

        [Fact]
        public void A_client_that_did_not_declare_elicitation_gets_elicitation_unsupported_and_no_request()
        {
            using (var server = new LiveServer())
            {
                server.Initialize(new JObject());
                server.Send(ElicitCall("call-2"));
                var seen = new List<JObject>();
                JObject reply = server.Next(m => (string)m["id"] == "call-2", seen);
                Assert.True((bool)reply["result"]["isError"]);
                Assert.Equal("elicitation_unsupported", (string)reply["result"]["structuredContent"]["code"]);
                Assert.Equal("client_did_not_declare", (string)reply["result"]["structuredContent"]["reason"]);
                Assert.DoesNotContain(seen, m => (string)m["method"] == "elicitation/create");
            }
        }

        [Fact]
        public void A_form_left_open_when_stdin_closes_ends_the_call_instead_of_holding_shutdown()
        {
            var clock = Stopwatch.StartNew();
            using (var server = new LiveServer())
            {
                server.Initialize(new JObject { ["elicitation"] = new JObject() });
                server.Send(ElicitCall("call-3"));
                server.Next(m => (string)m["method"] == "elicitation/create");
            }   // Dispose closes stdin and waits for exit
            Assert.True(clock.ElapsedMilliseconds < 25000,
                "the server must not wait out the form's timeout after stdin closed; took " + clock.ElapsedMilliseconds + " ms");
        }
    }
}
