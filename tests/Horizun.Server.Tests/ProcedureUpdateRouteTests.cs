// -----------------------------------------------------------------------------
// Horizun Server tests - original Horizun code.
//
// THE UPDATE ROUTE, DRIVEN THROUGH ITS PUBLIC EXECUTOR.
//
// Campaign 4 asked for dwg-to-bim-update to be proven through horizun_run_procedure
// rather than by the calls it was developed with. These drive the real executor with
// the dispatch seam replaced by a scripted bridge: a revision that decides everything
// alone finishes without a person; one that holds rows waits for ONE versioned
// decision per set and refuses a decision it cannot use; and a write whose reply never
// arrived is reconciled by asking the tool with the same key, never by sending it anew.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Server.Tests
{
    public class ProcedureUpdateRouteTests : IDisposable
    {
        private readonly string _root;
        private readonly string _oldRoot;
        private readonly Func<JObject, CancellationToken, JToken> _oldInvoker;
        private readonly List<string> _calls = new List<string>();
        private readonly List<JObject> _sent = new List<JObject>();
        private int _heldWalls;
        private readonly HashSet<string> _keysSeen = new HashSet<string>();

        public ProcedureUpdateRouteTests()
        {
            _oldRoot = Environment.GetEnvironmentVariable("HORIZUN_DATA_ROOT");
            _root = Path.Combine(Path.GetTempPath(), "hz-upd-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            Environment.SetEnvironmentVariable("HORIZUN_DATA_ROOT", _root);
            _oldInvoker = ProcedureRun.Invoker;
            ProcedureRun.Invoker = (args, token) =>
            {
                string tool = (string)args["name"];
                var a = args["arguments"] as JObject ?? new JObject();
                _calls.Add(tool);
                _sent.Add(a);
                JObject body;
                switch (tool)
                {
                    case "horizun_plan_cad_update":
                        bool walls = (string)a["requirement_set"]?["kind"] == "walls";
                        bool decided = a["resolve"] != null;
                        int held = walls && !decided ? _heldWalls : 0;
                        body = new JObject
                        {
                            ["actions"] = new JArray(), ["candidate_index"] = new JArray(),
                            ["provenance"] = new JObject { ["source_file_sha256"] = "x" },
                            ["needs_a_person"] = held, ["awaiting_a_decision"] = held, ["automatic"] = 0, ["apply_binding"] = new JObject(), ["plan"] = new JArray()
                        };
                        break;
                    case "horizun_apply_cad_update":
                        string key = (string)a["idempotency_key"];
                        bool replay = key != null && !_keysSeen.Add(key);
                        body = new JObject
                        {
                            ["state"] = "nothing_to_apply", ["stages_failed"] = 0, ["actions_failed"] = 0,
                            ["idempotency"] = new JObject
                            {
                                ["key"] = key, ["status"] = replay ? "replayed" : "executed_once",
                                ["command_executed_in_this_call"] = !replay
                            }
                        };
                        break;
                    case "horizun_audit_cad_model":
                        body = new JObject { ["read_only"] = true, ["agrees"] = true, ["matched"] = new JObject() };
                        break;
                    default:
                        body = new JObject { ["status"] = "healthy" };
                        break;
                }
                return new JObject { ["ok"] = true, ["structuredContent"] = body };
            };
        }

        public void Dispose()
        {
            ProcedureRun.Invoker = _oldInvoker;
            Environment.SetEnvironmentVariable("HORIZUN_DATA_ROOT", _oldRoot);
            try { Directory.Delete(_root, true); } catch { }
        }

        private static JObject Call(JObject request) => ProcedureRun.Handle(request, CancellationToken.None);

        private string Start()
        {
            var inputs = new JObject
            {
                ["document"] = "HZ_DOC", ["instance_id"] = 1, ["level_name"] = "Level 1", ["dwg_path"] = "c:/x.dwg",
                ["supersedes_sha256"] = new JArray("a"),
                ["walls_set"] = new JObject { ["kind"] = "walls" },
                ["devices_set"] = new JObject { ["kind"] = "devices" }
            };
            return Call(new JObject
            {
                ["operation"] = "start", ["procedure"] = "dwg-to-bim-update", ["inputs"] = inputs,
                ["target_document"] = "HZ_DOC"
            }).Value<string>("run_id");
        }

        private JObject Advance(string runId) => Call(new JObject { ["operation"] = "advance", ["run_id"] = runId });

        private JObject RunUntilItStops(string runId)
        {
            JObject last = null;
            for (int i = 0; i < 20; i++)
            {
                try { last = Advance(runId); }
                catch (ToolRefusal) { break; }
                string state = last.Value<string>("state");
                if (state == "waiting_for_a_decision" || state == "dispatched_outcome_unknown" || state == "blocked")
                    break;
                if (last.Value<string>("run_state") != "running") break;
            }
            return last;
        }

        [Fact]
        public void A_revision_the_update_decides_alone_finishes_without_a_person()
        {
            string runId = Start();
            JObject last = RunUntilItStops(runId);
            Assert.NotEqual("waiting_for_a_decision", last.Value<string>("state"));
            JObject status = Call(new JObject { ["operation"] = "status", ["run_id"] = runId });
            string text = status.ToString();
            Assert.Equal(11, _calls.Count);
            Assert.Equal(4, _calls.Count(c => c == "horizun_plan_cad_update"));
            // the decisions were recorded as automatic, with the fact that made them so
            Assert.Contains("\"decided_by\": \"automatic\"", text);
            Assert.Contains("awaiting_a_decision = 0", text);
        }

        [Fact]
        public void A_held_revision_waits_for_one_versioned_decision_and_refuses_one_it_cannot_use()
        {
            _heldWalls = 2;
            string runId = Start();
            JObject held = RunUntilItStops(runId);
            Assert.Equal("waiting_for_a_decision", held.Value<string>("state"));
            Assert.Equal(6, held.Value<int>("step"));
            int before = _calls.Count;

            var values = new JObject
            {
                ["accept_pairings"] = new JArray(), ["reject_pairings"] = new JArray(),
                ["resolve"] = new JArray(new JObject { ["element_id"] = 5, ["decision"] = "keep" })
            };
            // no version: refused
            Assert.Throws<ToolRefusal>(() => Call(new JObject
            {
                ["operation"] = "decide", ["run_id"] = runId, ["step"] = 6, ["values"] = values
            }));
            // a key the step does not read: refused
            var extra = (JObject)values.DeepClone();
            extra["delete_everything"] = true;
            Assert.Throws<ToolRefusal>(() => Call(new JObject
            {
                ["operation"] = "decide", ["run_id"] = runId, ["step"] = 6, ["values"] = extra,
                ["decision_version"] = "proposals-3.0.0"
            }));
            Assert.Equal(before, _calls.Count);

            Call(new JObject
            {
                ["operation"] = "decide", ["run_id"] = runId, ["step"] = 6, ["values"] = values,
                ["decision_version"] = "proposals-3.0.0", ["decided_by"] = "test"
            });
            RunUntilItStops(runId);
            JObject resolvedPlan = _sent[_calls.IndexOf("horizun_plan_cad_update", before)];
            Assert.Equal(5, (int)resolvedPlan["resolve"][0]["element_id"]);
            string text = Call(new JObject { ["operation"] = "status", ["run_id"] = runId }).ToString();
            Assert.Contains("proposals-3.0.0", text);
            Assert.Equal(11, _calls.Count);
        }

        [Fact]
        public void A_write_whose_reply_never_arrived_is_asked_again_with_the_same_key()
        {
            string runId = Start();
            Advance(runId);          // health
            Advance(runId);          // walls plan
            Advance(runId);          // walls apply: dispatched and answered
            // THE CRASH: the answer never reached the record.
            string path = Directory.GetFiles(_root, "*.json", SearchOption.AllDirectories)
                                   .Single(f => f.Contains(runId));
            JObject record = JObject.Parse(File.ReadAllText(path));
            var step3 = ((JArray)record["steps"]).OfType<JObject>().Single(s => (int)s["step"] == 3);
            step3["state"] = "pending";
            step3["evidence"] = JValue.CreateNull();
            File.WriteAllText(path, record.ToString());
            int applies = _calls.Count(c => c == "horizun_apply_cad_update");

            JObject held = Advance(runId);
            Assert.Equal("dispatched_outcome_unknown", held.Value<string>("state"));
            Assert.Equal(applies, _calls.Count(c => c == "horizun_apply_cad_update"));

            JObject reconciled = Call(new JObject { ["operation"] = "reconcile", ["run_id"] = runId });
            Assert.Equal(3, reconciled.Value<int>("step"));
            Assert.Equal("replayed", (string)reconciled["reconciled"]["idempotency"]["status"]);
            Assert.False((bool)reconciled["reconciled"]["idempotency"]["command_executed_in_this_call"]);
            var keys = _sent.Where((s, i) => _calls[i] == "horizun_apply_cad_update")
                            .Select(s => (string)s["idempotency_key"]).ToList();
            Assert.Equal(2, keys.Count);
            Assert.Equal(keys[0], keys[1]);
            // and the run goes on
            Assert.Equal("running", reconciled.Value<string>("run_state"));
        }

        [Fact]
        public void A_write_sent_without_a_key_is_not_reconciled()
        {
            string runId = Start();
            Advance(runId);
            Advance(runId);
            Advance(runId);
            string path = Directory.GetFiles(_root, "*.json", SearchOption.AllDirectories)
                                   .Single(f => f.Contains(runId));
            JObject record = JObject.Parse(File.ReadAllText(path));
            var step3 = ((JArray)record["steps"]).OfType<JObject>().Single(s => (int)s["step"] == 3);
            step3["state"] = "pending";
            ((JObject)step3["arguments_sent"]).Remove("idempotency_key");
            File.WriteAllText(path, record.ToString());
            int before = _calls.Count;
            Assert.Throws<ToolRefusal>(() => Call(new JObject { ["operation"] = "reconcile", ["run_id"] = runId }));
            Assert.Equal(before, _calls.Count);
        }
    }
}
