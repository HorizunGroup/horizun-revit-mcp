// -----------------------------------------------------------------------------
// Horizun Server tests — original Horizun code.
//
// A RESUME THAT REPEATS A WRITE IS WORSE THAN A RUN THAT STOPPED.
//
// The executor's whole promise is that a procedure interrupted half way can be
// picked up without doing anything twice. Until now nothing drove it: the
// catalogue had tests and the RUNNER had none, so "resume without repeating"
// was a design intention rather than a measured property.
//
// These drive the real executor through its own public entry point, with the
// dispatch seam replaced by a recorder. What they measure is what the seam saw:
// how many times each tool was actually invoked, which is the only place a
// duplicated write would show.
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
    public class ProcedureResumeTests : IDisposable
    {
        private readonly string _root;
        private readonly string _oldRoot;
        private readonly Func<JObject, CancellationToken, JToken> _oldInvoker;
        private readonly List<string> _calls = new List<string>();
        private readonly List<JObject> _sent = new List<JObject>();

        public ProcedureResumeTests()
        {
            _oldRoot = Environment.GetEnvironmentVariable("HORIZUN_DATA_ROOT");
            _root = Path.Combine(Path.GetTempPath(), "hz-proc-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            Environment.SetEnvironmentVariable("HORIZUN_DATA_ROOT", _root);

            _oldInvoker = ProcedureRun.Invoker;
            ProcedureRun.Invoker = (args, token) =>
            {
                // WHAT THE SEAM SAW. A duplicated write is a second entry here and
                // nothing else; every other signal in the reply is the executor
                // describing its own bookkeeping.
                // The envelope names the tool under "name" - checked against the
                // executor rather than guessed, because a recorder that labels
                // every call "(no tool)" makes two different steps look like one
                // step run twice, which is the exact conclusion these cases are
                // here to draw.
                _calls.Add((string)args["name"] ?? "(no tool)");
                _sent.Add(args["arguments"] as JObject ?? new JObject());
                return new JObject
                {
                    ["ok"] = true,
                    ["structuredContent"] = new JObject { ["status"] = "healthy" }
                };
            };
        }

        public void Dispose()
        {
            ProcedureRun.Invoker = _oldInvoker;
            Environment.SetEnvironmentVariable("HORIZUN_DATA_ROOT", _oldRoot);
            try { Directory.Delete(_root, true); } catch { }
        }

        private static JObject Call(JObject request) =>
            ProcedureRun.Handle(request, CancellationToken.None);

        private static JObject Obj(params object[] pairs)
        {
            var o = new JObject();
            for (int i = 0; i + 1 < pairs.Length; i += 2) o[(string)pairs[i]] = JToken.FromObject(pairs[i + 1]);
            return o;
        }

        /// <summary>The first procedure the catalogue calls executable, so this does not pin one by name.</summary>
        private static JObject AnExecutableProcedure()
        {
            foreach (McpWorkflowCatalog.Procedure p in McpWorkflowCatalog.Procedures)
            {
                JObject found = McpWorkflowCatalog.Find(p.Id);
                if (found == null) continue;
                var steps = found["steps"] as JArray;
                if (steps == null || steps.Count < 2) continue;
                if ((string)found["detail"] != "executable") continue;
                return found;
            }
            return null;
        }

        [Fact]
        public void A_run_that_starts_records_a_run_id_and_dispatches_nothing_yet()
        {
            JObject procedure = AnExecutableProcedure();
            Assert.NotNull(procedure);

            JObject started = Call(Obj("operation", "start", "procedure", (string)procedure["id"],
                                       "inputs", InputsFor(procedure)));
            Assert.NotNull(started.Value<string>("run_id"));

            // STARTING IS NOT DOING. A start that dispatched its first step would
            // make "start" a write, and a caller who started a run to read its
            // plan would have changed the model.
            Assert.Empty(_calls);
        }

        [Fact]
        public void Advancing_twice_over_one_step_dispatches_it_once()
        {
            JObject procedure = AnExecutableProcedure();
            Assert.NotNull(procedure);
            string id = (string)procedure["id"];

            JObject started = Call(Obj("operation", "start", "procedure", id, "inputs", InputsFor(procedure)));
            string runId = started.Value<string>("run_id");

            JObject first = Call(Obj("operation", "advance", "run_id", runId));
            int afterFirst = _calls.Count;
            Assert.True(afterFirst >= 1, "the first advance dispatched nothing at all");

            // THE INTERRUPTION. A caller that never saw the reply - the process
            // died, the transport dropped - sends the same advance again.
            JObject second = Call(Obj("operation", "advance", "run_id", runId));

            // The step that already ran must not run again. Whatever the second
            // advance did, it did not repeat the first step's dispatch.
            string firstTool = _calls[0];
            int timesFirstToolRan = _calls.Count(c => c == firstTool);
            Assert.True(timesFirstToolRan == 1,
                "the first step's tool was dispatched " + timesFirstToolRan + " times across two advances: " +
                string.Join(", ", _calls));
        }

        [Theory]
        [InlineData("{\"state\":\"applied\",\"stages_failed\":0,\"stopped_early\":false,\"created_verified\":21}", "ok")]
        [InlineData("{\"state\":\"rehearsed\",\"stages_failed\":0,\"stopped_early\":false}", "ok")]
        [InlineData("{\"state\":\"partial\",\"stages_failed\":1}", "not_evaluated")]
        [InlineData("{\"state\":\"applied\",\"stages_failed\":0,\"stopped_early\":true}", "not_evaluated")]
        [InlineData("{\"state\":\"applied_nothing\",\"stages_failed\":0}", "not_evaluated")]
        [InlineData("{\"stages_failed\":2}", "failed")]
        [InlineData("{\"reconciles\":true,\"total_rows\":2560}", "ok")]
        [InlineData("{\"reconciles\":false}", "failed")]
        [InlineData("{\"status\":\"healthy\"}", "ok")]
        [InlineData("{\"status\":\"degraded\"}", "not_evaluated")]
        [InlineData("{\"read_only\":true,\"agrees\":false}", "not_evaluated")]
        public void A_step_is_judged_by_the_fields_its_own_reply_carries(string reply, string expected)
        {
            ProcedureRun.Judge(ProcedureRun.Ok, JObject.Parse(reply), out string state, out string why);
            Assert.Equal(expected, state);
            Assert.False(string.IsNullOrEmpty(why));
        }

        [Fact]
        public void A_run_id_nobody_issued_is_refused_rather_than_started()
        {
            // Resuming a run that does not exist must not quietly become a new one:
            // a caller retrying after a crash would start a second run and both
            // would write.
            Assert.ThrowsAny<Exception>(() => Call(Obj("operation", "advance", "run_id", "hz-run-nothing")));
        }

        [Fact]
        public void Status_reports_the_run_without_dispatching_anything()
        {
            JObject procedure = AnExecutableProcedure();
            string id = (string)procedure["id"];
            string runId = Call(Obj("operation", "start", "procedure", id,
                                    "inputs", InputsFor(procedure))).Value<string>("run_id");

            Call(Obj("operation", "advance", "run_id", runId));
            int afterAdvance = _calls.Count;

            Call(Obj("operation", "status", "run_id", runId));
            Assert.Equal(afterAdvance, _calls.Count);
        }

        [Fact]
        public void An_abandoned_run_does_not_advance_again()
        {
            JObject procedure = AnExecutableProcedure();
            string id = (string)procedure["id"];
            string runId = Call(Obj("operation", "start", "procedure", id,
                                    "inputs", InputsFor(procedure))).Value<string>("run_id");

            Call(Obj("operation", "abandon", "run_id", runId, "reason", "the campaign finished with it"));
            int afterAbandon = _calls.Count;

            try { Call(Obj("operation", "advance", "run_id", runId)); } catch { }
            Assert.Equal(afterAbandon, _calls.Count);
        }

        [Fact]
        public void An_optional_input_nobody_supplied_is_omitted_rather_than_sent_as_null()
        {
            // ABSENT, NULL AND OMITTED ARE THREE DIFFERENT THINGS. A template could
            // only say the first, so an argument only some runs supply - the
            // outfall of a drainage conversion - could not be expressed at all and
            // the procedure asked for it in prose.
            JObject procedure = McpWorkflowCatalog.Find("dwg-mep-unit-conversion");
            Assert.NotNull(procedure);

            string runId = Call(Obj("operation", "start", "procedure", "dwg-mep-unit-conversion",
                                    "inputs", InputsFor(procedure))).Value<string>("run_id");

            // Advance until the planning step has been dispatched, or the run stops.
            for (int i = 0; i < 6 && !_calls.Contains("horizun_plan_from_cad"); i++)
            {
                try { Call(Obj("operation", "advance", "run_id", runId)); }
                catch { break; }
            }

            int at = _calls.IndexOf("horizun_plan_from_cad");
            Assert.True(at >= 0, "the planning step was never dispatched: " + string.Join(", ", _calls));

            JObject sent = _sent[at];
            Assert.NotNull(sent["instance_id"]);

            // NOT PRESENT AT ALL - not present-and-null. horizun_plan_from_cad
            // declares additionalProperties:false and takes the first; a null would
            // be a value it has to interpret.
            Assert.Null(sent.Property("outfall"));
            Assert.Null(sent.Property("outfall_invert_mm"));
        }

        /// <summary>
        /// Every input the procedure's schema requires, filled with something of
        /// the right shape. The point is to reach the executor, not to be a
        /// realistic run: what is measured is dispatch counting, and a run that
        /// refuses at the door measures nothing.
        /// </summary>
        private static JObject InputsFor(JObject procedure)
        {
            var inputs = new JObject();
            var schema = procedure["input_schema"] as JObject;
            if (schema == null) return inputs;
            var properties = schema["properties"] as JObject;
            foreach (JToken required in schema["required"] as JArray ?? new JArray())
            {
                string key = (string)required;
                if (key == null) continue;
                string type = properties?[key]?["type"]?.ToString();
                switch (type)
                {
                    case "integer": inputs[key] = 1; break;
                    case "number": inputs[key] = 1.0; break;
                    case "boolean": inputs[key] = true; break;
                    case "array": inputs[key] = new JArray(); break;
                    case "object": inputs[key] = new JObject(); break;
                    default: inputs[key] = "x"; break;
                }
            }
            return inputs;
        }
    }
}
