// -----------------------------------------------------------------------------
// Horizun MCP - running one of the catalogue's procedures. Original Horizun code.
//
// G13 HAD TWENTY ROUTES AND NO WAY TO FOLLOW ONE. The catalogue said what to call,
// in what order, with what from the previous step - and a caller still had to hold
// all of that in their head, remember which step they were on, and decide for
// themselves whether the result was acceptable. A route nobody can follow is
// documentation.
//
// WHAT THIS IS, and the boundary matters: A RUN RECORD WITH A DISPATCHER, NOT AN
// AUTOMATION PLATFORM. It holds a run's state and it can advance a step - through
// the SAME entry point tools/call uses. There is no second tool registry here, no
// second permission check and no second route to the add-in, which is what keeps
// the guarantees intact:
//
//   - every write keeps its own dry-run, confirmation token and target_document,
//     because the call this dispatches is literally the call a client would make.
//   - a step that needs a human decision STOPS the run and says what it needs.
//     Selecting "all the findings" because nobody was asked is a decision, and it
//     would be made on somebody else's model.
//   - a procedure whose route is prose cannot be dispatched at all: without an
//     argument template there is nothing to send but a guess.
//   - a step already recorded is never dispatched again, so resuming a run cannot
//     repeat a write.
//
// THE STATE THAT MAKES IT RESUMABLE is a file per run, in the same durable
// directory the job records use. A conversation that dies at step 5 of 8 is
// resumed by id, not restarted - which is the whole reason a procedure with eight
// steps needs a record at all.
//
// EVIDENCE PER STEP, AND A VERDICT THAT CAN SAY "I DO NOT KNOW". A step records
// what came back and whether it satisfied what the catalogue said to read back. A
// step whose evidence cannot be judged from here is `not_evaluated` with the
// reason - never `ok`, because a run of eight steps where three were assumed is a
// run nobody can rely on.
//
// NOT RUN. Written during an implementation-only phase; no procedure has been
// started, advanced or completed.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Server
{
    internal static class ProcedureRun
    {
        public const string Pending = "pending";
        public const string Ok = "ok";
        public const string Failed = "failed";
        public const string Skipped = "skipped";
        public const string NotEvaluated = "not_evaluated";

        /// <summary>A run older than this is swept: a record nobody resumed is a record nobody will.</summary>
        private const int RetentionDays = 30;

        /// <summary>
        /// How a step is dispatched: the SAME entry point tools/call uses.
        ///
        /// Set once by Program at start-up, exactly as Tools.LiveBridge is. It is a
        /// delegate rather than a reference so that this file holds no second tool
        /// registry, no second permission check and no second route to the add-in - a step
        /// is dispatched by the code that dispatches every other call, or it is not
        /// dispatched.
        /// </summary>
        internal static Func<JObject, System.Threading.CancellationToken, JToken> Invoker;

        /// <summary>
        /// ONE RUN AT A TIME, per run.
        ///
        /// Two `advance` calls on one run used to race: both loaded the record, both saw
        /// the same pending step, and both dispatched it. A procedure whose third step
        /// creates four hundred elements created eight hundred, and the record - written
        /// by whichever finished last - showed one dispatch.
        ///
        /// The second caller is REFUSED rather than queued. Queueing would make the second
        /// advance succeed after the first, dispatching the NEXT step, which is work the
        /// caller did not ask for at a moment they did not choose; and a caller who sent
        /// two advances by accident wants to be told, not obeyed twice.
        /// </summary>
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, object> Locks =
            new System.Collections.Concurrent.ConcurrentDictionary<string, object>(StringComparer.Ordinal);

        private static object LockFor(string runId) => Locks.GetOrAdd(runId ?? "", _ => new object());

        private static string Root()
        {
            string dir = Path.Combine(HorizunPaths.DataRoot(), "procedure-runs");
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static string PathOf(string runId) => Path.Combine(Root(), Safe(runId) + ".json");

        // =====================================================================

        public static JObject Handle(JObject request, System.Threading.CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            request = request ?? new JObject();

            string operation = (request.Value<string>("operation") ?? "start").ToLowerInvariant();
            switch (operation)
            {
                case "start": return Start(request);
                case "advance": return Serialised(request, () => Advance(request, cancellationToken));
                case "decide": return Serialised(request, () => Decide(request));
                case "record": return Serialised(request, () => Record(request));
                case "reconcile": return Serialised(request, () => Reconcile(request, cancellationToken));
                case "status": return Status(request);
                case "abandon": return Serialised(request, () => Abandon(request));
                default:
                    throw new ToolRefusal(
                        "operation must be start, advance, decide, record, reconcile, status or abandon; '" +
                        operation + "' is not one.");
            }
        }

        // =====================================================================

        private static JObject Start(JObject request)
        {
            string procedureId = request.Value<string>("procedure");
            if (string.IsNullOrWhiteSpace(procedureId))
                throw new ToolRefusal("procedure is required: the id of one in horizun_workflows.");

            JObject procedure = McpWorkflowCatalog.Find(procedureId);
            if (procedure == null)
                throw new ToolRefusal("no procedure with id '" + procedureId + "'. horizun_workflows lists them.");

            var steps = procedure["steps"] as JArray;
            if (steps == null || steps.Count == 0)
                throw new ToolRefusal(
                    "'" + procedureId + "' carries no steps, so there is no route to follow. It is a tool " +
                    "list rather than a procedure, and the catalogue reports which are which under `detail`.");

            // THE INPUTS ARE THE CALLER'S AND ARE NOT INVENTED, and for an EXECUTABLE
            // procedure they are checked against a schema rather than against prose. A run
            // that started without a required input would fail at step 2 with a message
            // about a tool rather than about the thing that was missing.
            JObject inputs = request["inputs"] as JObject ?? new JObject();
            string detail = (string)procedure["detail"];
            var schema = procedure["input_schema"] as JObject;
            var missing = new JArray();
            var defaulted = new JArray();

            if (schema != null)
            {
                foreach (JToken required in schema["required"] as JArray ?? new JArray())
                {
                    string key = (string)required;
                    if (key != null && inputs[key] == null) missing.Add(key);
                }

                // THE SCHEMA'S DEFAULTS, APPLIED ONCE, HERE.
                //
                // A template referencing an OPTIONAL input resolves against what the
                // caller supplied, and when they supplied nothing the reference does
                // not resolve and the step BLOCKS. So an optional input with a
                // declared default was a trap: the default made the procedure look
                // usable without it and the run stopped on the first step that used
                // it. Which values the caller chose and which the schema supplied is
                // recorded, because "I never passed that" and "the schema passed it
                // for me" are different conversations afterwards.
                var properties = schema["properties"] as JObject;
                if (properties != null)
                    foreach (JProperty property in properties.Properties())
                    {
                        var definition = property.Value as JObject;
                        if (definition == null) continue;
                        JToken fallback = definition["default"];
                        if (fallback == null) continue;
                        if (inputs[property.Name] != null) continue;
                        inputs[property.Name] = fallback.DeepClone();
                        defaulted.Add(property.Name);
                    }
                if (missing.Count > 0)
                    throw new ToolRefusal(
                        "'" + procedureId + "' is EXECUTABLE and its input schema requires " +
                        string.Join(", ", missing.Select(m => (string)m)) + ", which this call did not " +
                        "supply. The run is NOT started: starting one that cannot reach step 2 leaves a " +
                        "record of a procedure nobody ran. The schema is in horizun_workflows under " +
                        "input_schema.");
            }
            else
            {
                // A procedure whose inputs are prose. It can still be FOLLOWED by hand and
                // recorded; what it cannot do is dispatch, and Advance says so by name.
                foreach (JToken declared in procedure["inputs"] as JArray ?? new JArray())
                {
                    string text = (string)declared;
                    if (text == null) continue;
                    string key = InputKey(text);
                    if (key != null && inputs[key] == null) missing.Add(text);
                }
            }

            string runId = "run-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) +
                           "-" + Guid.NewGuid().ToString("N").Substring(0, 6);

            var record = new JObject
            {
                ["schema"] = "horizun.procedure-run/1",
                ["run_id"] = runId,
                ["procedure"] = procedureId,
                ["procedure_version"] = procedure["version"],
                ["started_utc"] = DateTime.UtcNow.ToString("o"),
                ["target_document"] = request.Value<string>("target_document"),
                ["inputs"] = inputs,
                // WHICH VALUES THE CALLER CHOSE AND WHICH THE SCHEMA SUPPLIED. A run
                // that behaved unexpectedly is read afterwards, and "I never passed
                // that" and "the schema passed it for me" are different conversations.
                ["inputs_defaulted_from_the_schema"] = defaulted,
                ["inputs_not_supplied"] = missing,
                ["detail"] = detail,
                ["executable"] = detail == "executable",
                ["decisions"] = new JObject(),
                ["acceptance_checks"] = procedure["acceptance_checks"] ?? new JArray(),
                ["state"] = "running",
                ["steps"] = new JArray(steps.Select((s, i) => (JToken)new JObject
                {
                    ["step"] = s["step"] ?? i + 1,
                    ["tool"] = s["tool"],
                    ["purpose"] = s["purpose"],
                    ["needs_from_earlier_steps"] = s["needs_from_earlier_steps"],
                    ["preconditions"] = s["preconditions"],
                    ["on_error"] = s["on_error"],
                    ["reads_back"] = s["reads_back"],
                    ["state"] = Pending,
                    ["executable"] = s["executable"],
                    ["requires_decision"] = s["requires_decision"],
                    ["decision_needed"] = s["decision_needed"],
                    ["decision_unless"] = s["decision_unless"],
                    ["arguments_template"] = s["arguments_template"],
                    ["evidence"] = JValue.CreateNull(),
                    ["dispatched"] = false,
                    ["note"] = JValue.CreateNull()
                })),
                ["acceptance"] = procedure["acceptance"],
                ["limits"] = procedure["limits"]
            };

            Save(record);
            Sweep();

            return new JObject
            {
                ["run_id"] = runId,
                ["procedure"] = procedureId,
                ["steps"] = ((JArray)record["steps"]).Count,
                ["inputs_not_supplied"] = missing,
                ["inputs_defaulted_from_the_schema"] = defaulted,
                ["defaulted_means"] = defaulted.Count == 0
                    ? "every input this run uses was supplied by the caller"
                    : "these were not supplied and were taken from the procedure's own input schema. Without " +
                      "that, a step referencing an optional input would have blocked on a value the schema " +
                      "declares a default for.",
                ["inputs_warning"] = missing.Count == 0
                    ? (JToken)JValue.CreateNull()
                    : "this procedure declares " + missing.Count + " input(s) this run did not supply. It is " +
                      "started anyway - the catalogue's input list is prose and a step may not need all of " +
                      "it - but a step that fails for a missing value will fail for one of these.",
                ["next"] = NextCall(record),
                ["means"] = detail == "executable"
                    ? "EXECUTABLE. operation=advance dispatches the next step through the same entry point " +
                      "tools/call uses - so a step that writes carries its own dry_run, confirmation token " +
                      "and target_document, because it is the same call a client would make. A step that " +
                      "needs somebody to decide STOPS the run and says what it needs. You can still follow " +
                      "it by hand and use operation=record instead."
                    : "THIS PROCEDURE IS NOT EXECUTABLE: its route is written for a person and its steps " +
                      "carry no argument templates, so nothing can dispatch it without guessing arguments. " +
                      "It hands you the next call to make; you make it and send the result back with " +
                      "operation=record."
            };
        }

        /// <summary>
        /// Run the next step: resolve its arguments, dispatch it, record what came back.
        ///
        /// ONE STEP PER CALL, on purpose. A procedure that ran to completion inside one
        /// tool call would be a batch of writes nobody watched, and the caller could not
        /// stop it between steps. The caller advances; the executor does exactly one thing
        /// and reports.
        /// </summary>
        private static JObject Advance(JObject request, System.Threading.CancellationToken cancellationToken)
        {
            JObject record = Load(request.Value<string>("run_id"));
            if ((string)record["state"] != "running")
                throw new ToolRefusal("this run is '" + (string)record["state"] + "'. Only a running one advances.");

            if ((bool?)record["executable"] != true)
                throw new ToolRefusal(
                    "'" + (string)record["procedure"] + "' is not executable: its steps carry no argument " +
                    "templates, so there is nothing to dispatch but a guess. Follow it by hand and use " +
                    "operation=record. horizun_workflows reports which procedures are executable.");

            JObject step = ((JArray)record["steps"]).OfType<JObject>()
                .FirstOrDefault(x => (string)x["state"] == Pending);
            if (step == null)
                throw new ToolRefusal("every step of this run is already recorded.");

            int number = (int)step["step"];

            // A STEP ALREADY IN FLIGHT IS NEVER SENT AGAIN.
            //
            // The record is saved with dispatched=true before the call, which is what
            // makes a crash mid-step visible. What was missing is the other half: the step
            // stays `pending`, so the NEXT advance picked the same step and sent it a
            // second time. Every crash, every timeout, every reconnecting client turned
            // one write into two, and the record showed one dispatch.
            //
            // So a pending step that was already dispatched HOLDS the run. Only a person
            // can say what the tool did - the executor cannot, which is the whole reason
            // the situation exists.
            if ((bool?)step["dispatched"] == true)
                return new JObject
                {
                    ["run_id"] = record["run_id"],
                    ["step"] = number,
                    ["tool"] = step["tool"],
                    ["state"] = "dispatched_outcome_unknown",
                    ["dispatched_utc"] = step["dispatched_utc"],
                    ["arguments_sent"] = step["arguments_sent"],
                    ["how"] =
                        "operation=reconcile asks the tool itself when the step can be asked safely (a read, or " +
                        "a write sent with an idempotency key). Otherwise look at the model and send " +
                        "operation=record with run_id, step=" + number + ", outcome and result - or " +
                        "operation=abandon with a reason. It is NOT advanced automatically.",
                    ["means"] =
                        "this step was sent to '" + (string)step["tool"] + "' and its reply never reached this " +
                        "record - the run was interrupted while it was in flight. Whether the tool did its " +
                        "work is UNKNOWN. Re-sending it would be a second write on a model where the first " +
                        "may have landed, and nothing here can tell."
                };

            // A DECISION STOPS THE RUN. Not a warning, not a default: the run does not
            // advance until somebody supplies it.
            // A DECISION NOBODY HAS TO MAKE is recorded as such, with the fact that made it
            // unnecessary - never as a choice. The step it guards decided nothing to hold.
            if ((bool?)step["requires_decision"] == true && !HasDecision(record, step))
                AutomaticDecision(record, step);
            if ((bool?)step["requires_decision"] == true && !HasDecision(record, step))
                return new JObject
                {
                    ["run_id"] = record["run_id"],
                    ["step"] = number,
                    ["state"] = "waiting_for_a_decision",
                    ["decision_needed"] = step["decision_needed"],
                    ["how"] = "operation=decide, run_id, step=" + number + ", values={…}. Nothing has been " +
                              "dispatched and nothing will be until then.",
                    ["means"] = "the run is HELD, not failed. What is missing is a choice this executor may " +
                                "not make on somebody else's model."
                };

            // RESOLVED BEFORE ANYTHING IS SENT. A reference that does not resolve holds the
            // step rather than sending null, which would reach the tool looking deliberate.
            string unresolved;
            JObject arguments = Resolve(step["arguments_template"], record, number, out unresolved);
            if (unresolved != null)
                return new JObject
                {
                    ["run_id"] = record["run_id"],
                    ["step"] = number,
                    ["state"] = "blocked",
                    ["unresolved"] = unresolved,
                    ["means"] = "an argument could not be built from what this run holds, so NOTHING was " +
                                "dispatched. Sending the argument as null would reach the tool looking like a " +
                                "deliberate value."
                };

            // IDEMPOTENCY FOR THE WRITES, and two things the first version got wrong.
            //
            // IT ONLY RECOGNISED A WRITE BY dry_run=false. A tool that writes without a
            // rehearsal flag - saving, relinquishing, anything whose schema has no dry_run
            // - got no key at all, which is precisely the case where a repeat is silent.
            // A write is now identified from the CONTRACT: a tool that requires
            // target_document changes a model, and that is the bridge's own rule rather
            // than a guess made here.
            //
            // AND IT SENT THE KEY WITHOUT CHECKING THE TOOL TAKES ONE. Nearly every schema
            // here declares additionalProperties:false, so injecting idempotency_key made
            // the call INVALID - the executor's own safety measure was what refused the
            // step, and the refusal read like a bad argument template.
            string tool = (string)step["tool"];
            bool writes = Writes(tool) || ((bool?)step["writes"] == true) ||
                          (arguments["dry_run"] != null && (bool?)arguments["dry_run"] == false);
            step["identified_as_a_write"] = writes;

            if (writes)
            {
                if (AcceptsIdempotencyKey(tool))
                {
                    if (arguments["idempotency_key"] == null)
                        arguments["idempotency_key"] = (string)record["run_id"] + "-step" + number;
                    step["idempotency"] = "sent as '" + (string)arguments["idempotency_key"] +
                                          "', so an explicit retry of this step reaches the tool as the same " +
                                          "work rather than as new work";
                }
                else
                {
                    step["idempotency"] =
                        "NOT SENT: '" + tool + "' does not declare idempotency_key in its schema, and most " +
                        "schemas here refuse an argument they do not declare - sending it would turn a write " +
                        "into an invalid call. This step is therefore NOT protected against a repeat by the " +
                        "tool, which is why a dispatched step whose reply never arrived holds the run instead " +
                        "of being retried.";
                }
            }

            if (Invoker == null)
                throw new ToolRefusal(
                    "this server has no dispatcher wired, so no step can be run. That is a defect in the " +
                    "server's start-up, not in this run.");

            step["dispatched"] = true;
            step["dispatched_utc"] = DateTime.UtcNow.ToString("o");
            step["arguments_sent"] = arguments.DeepClone();
            Save(record);      // BEFORE the call: a crash mid-step must not look un-dispatched

            JToken result;
            try
            {
                result = Invoker(new JObject
                {
                    ["name"] = step["tool"],
                    ["arguments"] = arguments
                }, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                step["state"] = NotEvaluated;
                step["evaluation"] =
                    "the caller cancelled while this step was in flight. Whether the tool did its work is " +
                    "UNKNOWN - it is not retried automatically, because a write whose outcome is uncertain " +
                    "must not be repeated by anything that cannot tell.";
                step["recorded_utc"] = DateTime.UtcNow.ToString("o");
                AdvanceState(record);
                Save(record);
                throw;
            }
            catch (Exception ex)
            {
                step["state"] = Failed;
                step["evaluation"] = "the call threw: " + ex.Message;
                step["recorded_utc"] = DateTime.UtcNow.ToString("o");
                AdvanceState(record);
                Save(record);
                return new JObject
                {
                    ["run_id"] = record["run_id"],
                    ["step"] = number,
                    ["state"] = Failed,
                    ["evaluation"] = step["evaluation"],
                    ["on_error"] = step["on_error"],
                    ["run_state"] = record["state"],
                    ["summary"] = Summary(record)
                };
            }

            JObject structured = Structured(result);
            step["evidence"] = structured ?? result ?? JValue.CreateNull();
            step["recorded_utc"] = DateTime.UtcNow.ToString("o");

            // SELF-REPORTED STAYS SELF-REPORTED. A reply from horizun_execute_python is the
            // script's testimony; folding it into the run's evidence without the label
            // would launder it into the bridge's finding.
            if (string.Equals((string)step["tool"], "horizun_execute_python", StringComparison.Ordinal))
                step["evidence_is"] = "SELF-REPORTED by the script. host_verified is false on that path and " +
                                      "nothing here changes that.";

            string judged, why;
            Judge(Ok, structured ?? result, out judged, out why);
            step["state"] = judged;
            step["evaluation"] = why;
            step["assessment"] = Assess((string)step["tool"], structured ?? result);

            AdvanceState(record);
            Save(record);

            return new JObject
            {
                ["run_id"] = record["run_id"],
                ["step"] = number,
                ["tool"] = step["tool"],
                ["state"] = judged,
                ["evaluation"] = why,
                ["result"] = step["evidence"],
                ["run_state"] = record["state"],
                ["next"] = NextCall(record),
                ["summary"] = Summary(record)
            };
        }

        /// <summary>
        /// The decision a step does not need: its `decision_unless` names an earlier result
        /// ({step, path, equals}) and the values that stand for "nothing to decide".
        /// </summary>
        private static void AutomaticDecision(JObject record, JObject step)
        {
            var unless = step["decision_unless"] as JObject;
            if (unless == null) return;
            JObject earlier = StepOf(record, unless.Value<int?>("step") ?? -1);
            if (earlier == null || (string)earlier["state"] == Pending) return;
            JToken observed = Dig(earlier["evidence"], unless.Value<string>("path"));
            if (observed == null || !JToken.DeepEquals(observed, unless["equals"])) return;
            var decisions = record["decisions"] as JObject ?? new JObject();
            decisions[((int)step["step"]).ToString(CultureInfo.InvariantCulture)] = new JObject
            {
                ["values"] = (unless["values"] as JObject ?? new JObject()).DeepClone(),
                ["decided_utc"] = DateTime.UtcNow.ToString("o"),
                ["decided_by"] = "automatic",
                ["decision_version"] = "automatic",
                ["because"] = "step " + (int)earlier["step"] + " reported " + unless.Value<string>("path") + " = " +
                              observed.ToString(Newtonsoft.Json.Formatting.None) + ": there was nothing to decide"
            };
            record["decisions"] = decisions;
            Save(record);
        }

        /// <summary>The decision keys a step's template reads.</summary>
        private static List<string> DecisionKeys(JToken template)
        {
            var keys = new List<string>();
            void Walk(JToken t)
            {
                if (t is JObject o)
                {
                    if (o["$decision"] != null && o["from_step"] == null) keys.Add((string)o["$decision"]);
                    foreach (JProperty p in o.Properties()) Walk(p.Value);
                }
                else if (t is JArray a) foreach (JToken x in a) Walk(x);
            }
            Walk(template);
            return keys;
        }

        /// <summary>Supply what a step was waiting for.</summary>
        private static JObject Decide(JObject request)
        {
            JObject record = Load(request.Value<string>("run_id"));
            int number = request.Value<int?>("step") ?? -1;
            JObject step = StepOf(record, number);
            if (step == null) throw new ToolRefusal("this run has no step " + number + ".");
            if ((bool?)step["requires_decision"] != true)
                throw new ToolRefusal("step " + number + " needs no decision.");
            // A DECISION TAKEN AFTER THE STEP WENT OUT would describe work already sent.
            if ((bool?)step["dispatched"] == true || (string)step["state"] != Pending)
                throw new ToolRefusal("step " + number + " has already been dispatched; a decision now would not " +
                                      "be the one it was sent with.");

            var values = request["values"] as JObject;
            if (values == null || !values.Properties().Any())
                throw new ToolRefusal(
                    "values is required and must carry what the step asked for: " +
                    (string)step["decision_needed"]);
            // THE DECISION NAMES ITS OWN VERSION, so a record read later says which one was used.
            string version = request.Value<string>("decision_version");
            if (string.IsNullOrWhiteSpace(version))
                throw new ToolRefusal("decision_version is required: the identity of this decision (for example " +
                                      "the version of the proposal it answers), so the run records which one it used.");
            List<string> allowed = DecisionKeys(step["arguments_template"]);
            var unknown = values.Properties().Select(p => p.Name).Where(n => !allowed.Contains(n)).ToList();
            if (allowed.Count > 0 && unknown.Count > 0)
                throw new ToolRefusal("values carries " + string.Join(", ", unknown) + ", which step " + number +
                                      " does not read (it reads " + string.Join(", ", allowed) + "). Nothing was " +
                                      "recorded: a key nobody reads would look decided.");

            var decisions = record["decisions"] as JObject ?? new JObject();
            var forStep = new JObject { ["values"] = values, ["decided_utc"] = DateTime.UtcNow.ToString("o"),
                                        ["decision_version"] = version };
            if (request["decided_by"] != null) forStep["decided_by"] = request["decided_by"];
            decisions[number.ToString(CultureInfo.InvariantCulture)] = forStep;
            record["decisions"] = decisions;
            Save(record);

            return new JObject
            {
                ["run_id"] = record["run_id"],
                ["step"] = number,
                ["decided"] = values,
                ["next"] = NextCall(record),
                ["means"] = "recorded against this step and kept with the run. It is NOT applied to any other " +
                            "step unless that step's template asks for it by name."
            };
        }

        private static bool HasDecision(JObject record, JObject step)
        {
            var decisions = record["decisions"] as JObject;
            return decisions != null &&
                   decisions[((int)step["step"]).ToString(CultureInfo.InvariantCulture)] != null;
        }

        // =====================================================================
        // Resolving a template
        // =====================================================================

        /// <summary>
        /// Fill the three kinds of hole. Everything else is a literal.
        ///
        /// `unresolved` is set to the FIRST thing that could not be built, and the caller
        /// sends nothing at all. Partially resolving and sending the rest is how a tool
        /// receives an argument that looks deliberate and is a gap.
        /// </summary>
        internal static JObject Resolve(JToken template, JObject record, int step, out string unresolved)
        {
            unresolved = null;
            var source = template as JObject;
            if (source == null) return new JObject();

            var built = new JObject();
            foreach (JProperty property in source.Properties())
            {
                string problem;
                JToken value = ResolveOne(property.Value, record, step, out problem);
                if (problem != null) { unresolved = "'" + property.Name + "': " + problem; return null; }

                // AN OMITTED PROPERTY IS NOT A NULL ONE. It is not in the object at
                // all, which is what a schema with additionalProperties:false and an
                // optional argument actually wants.
                if (value != null && value.Type == JTokenType.Undefined) continue;
                built[property.Name] = value;
            }
            return built;
        }

        private static JToken ResolveOne(JToken token, JObject record, int step, out string problem)
        {
            problem = null;
            var holder = token as JObject;
            if (holder == null) return token == null ? JValue.CreateNull() : token.DeepClone();

            JToken input = holder["$input"];
            if (input != null)
            {
                string key = (string)input;
                JToken value = (record["inputs"] as JObject)?[key];
                if (value == null)
                {
                    // ABSENT, NULL AND OMITTED ARE THREE DIFFERENT THINGS, and a
                    // template could only say the first.
                    //
                    // That made a genuinely optional argument impossible to
                    // express: the outfall on a drainage conversion is needed by
                    // the drawings that drain and meaningless for the ones that do
                    // not, and putting it in the template refused every
                    // pressure-system run. The procedure had to say "add these two
                    // arguments yourself" in prose, which is the manual step this
                    // whole catalogue exists to remove.
                    //
                    //   fail (the default) - the run is missing something it needs
                    //   omit               - the property is not sent at all
                    //   null               - the property is sent, as null
                    //
                    // omit and null are not interchangeable: a schema with
                    // additionalProperties:false takes the first and refuses the
                    // second, and a tool that treats null as "clear this" would act
                    // on it.
                    string whenMissing = (holder.Value<string>("when_missing") ?? "fail").ToLowerInvariant();
                    switch (whenMissing)
                    {
                        case "omit": return JValue.CreateUndefined();
                        case "null": return JValue.CreateNull();
                        case "fail": break;
                        default:
                            problem = "when_missing must be fail, omit or null; '" + whenMissing +
                                      "' is not one. A marker this resolver does not implement would read " +
                                      "as a promise the template does not keep.";
                            return null;
                    }
                    problem = "the run has no input called '" + key + "'.";
                    return null;
                }
                return value.DeepClone();
            }

            JToken decision = holder["$decision"];
            if (decision != null)
            {
                string key = (string)decision;
                // A decision belongs to the step that asked for it; `from_step` lets a later
                // step reuse it WITHOUT asking again, which is the difference between
                // carrying a decision forward and making a second one silently.
                int owner = holder.Value<int?>("from_step") ?? step;
                JToken values = (record["decisions"] as JObject)?
                    [owner.ToString(CultureInfo.InvariantCulture)]?["values"];
                JToken value = values?[key];
                if (value == null)
                {
                    problem = "no decision '" + key + "' recorded for step " + owner + ".";
                    return null;
                }
                return value.DeepClone();
            }

            JToken reference = holder["$ref"] as JObject;
            if (reference != null)
            {
                int from = reference.Value<int?>("step") ?? -1;
                string path = reference.Value<string>("path");
                JObject earlier = StepOf(record, from);
                if (earlier == null) { problem = "this run has no step " + from + "."; return null; }
                if ((string)earlier["state"] == Pending)
                { problem = "step " + from + " has not run yet."; return null; }

                JToken value = Dig(earlier["evidence"], path);
                if (value == null || value.Type == JTokenType.Null)
                {
                    problem = "step " + from + "'s result carries nothing at '" + path + "'. The step ran; " +
                              "what it returned does not have that field, which is a difference between what " +
                              "this procedure expects and what the tool produced.";
                    return null;
                }
                return value.DeepClone();
            }

            // A nested object of literals and holes.
            var nested = new JObject();
            foreach (JProperty property in holder.Properties())
            {
                string inner;
                JToken value = ResolveOne(property.Value, record, step, out inner);
                if (inner != null) { problem = inner; return null; }
                nested[property.Name] = value;
            }
            return nested;
        }

        /// <summary>A dotted path into a result. Supports one level of [index].</summary>
        internal static JToken Dig(JToken root, string path)
        {
            if (root == null || string.IsNullOrWhiteSpace(path)) return null;
            JToken cursor = root;
            foreach (string raw in path.Split('.'))
            {
                if (cursor == null) return null;
                string name = raw;
                int index = -1;
                int bracket = raw.IndexOf('[');
                if (bracket > 0 && raw.EndsWith("]", StringComparison.Ordinal))
                {
                    name = raw.Substring(0, bracket);
                    int.TryParse(raw.Substring(bracket + 1, raw.Length - bracket - 2),
                                 NumberStyles.Integer, CultureInfo.InvariantCulture, out index);
                }
                cursor = cursor[name];
                if (index >= 0) cursor = (cursor as JArray)?.ElementAtOrDefault(index);
            }
            return cursor;
        }

        /// <summary>
        /// The structured payload of a tool reply, when there is one.
        ///
        /// A tools/call result is content plus structuredContent. The structured half is
        /// what a later step can reference; digging into rendered text would make a
        /// procedure depend on how a message was worded.
        /// </summary>
        private static JObject Structured(JToken result)
        {
            var body = result as JObject;
            if (body == null) return null;
            return body["structuredContent"] as JObject ?? body;
        }

        /// <summary>
        /// A step whose reply never arrived, asked of the TOOL rather than guessed.
        ///
        /// Only two kinds are asked again: a step that does not write (asking again changes
        /// nothing), and a write that went out with an idempotency key (the bridge answers
        /// the same key with the recorded result if the first call landed, and runs it once
        /// if it never started). A write without a key is refused: only a look at the model
        /// can say what it did. The answer is recorded as the step's evidence, saying so.
        /// </summary>
        private static JObject Reconcile(JObject request, System.Threading.CancellationToken cancellationToken)
        {
            JObject record = Load(request.Value<string>("run_id"));
            JObject step = ((JArray)record["steps"]).OfType<JObject>()
                .FirstOrDefault(x => (string)x["state"] == Pending && (bool?)x["dispatched"] == true);
            if (step == null)
                throw new ToolRefusal("no step of this run is dispatched and unrecorded; there is nothing to reconcile.");
            int number = (int)step["step"];
            string tool = (string)step["tool"];
            var sent = step["arguments_sent"] as JObject;
            bool writes = (bool?)step["identified_as_a_write"] ?? Writes(tool);
            string key = sent?.Value<string>("idempotency_key");
            if (sent == null || (writes && string.IsNullOrWhiteSpace(key)))
                throw new ToolRefusal("step " + number + " (" + tool + ") " +
                    (sent == null ? "has no recorded arguments" : "is a write sent without an idempotency key") +
                    ": nothing can ask the tool what it did without risking a second write. Look at the model " +
                    "and use operation=record, or operation=abandon.");
            if (Invoker == null)
                throw new ToolRefusal("this server has no dispatcher wired, so nothing can be asked.");

            JToken result = Invoker(new JObject { ["name"] = tool, ["arguments"] = sent.DeepClone() }, cancellationToken);
            JObject structured = Structured(result);
            JObject idem = structured?["idempotency"] as JObject;
            if (writes && idem == null)
                throw new ToolRefusal("the tool answered without an idempotency record, so whether this was the " +
                                      "first execution cannot be told. Nothing was recorded.");
            step["evidence"] = structured ?? result ?? JValue.CreateNull();
            step["recorded_utc"] = DateTime.UtcNow.ToString("o");
            step["reconciled"] = new JObject
            {
                ["how"] = writes ? "the same call sent again with the same idempotency key" : "a read-only step asked again",
                ["idempotency"] = idem?.DeepClone(),
                ["reconciled_utc"] = DateTime.UtcNow.ToString("o")
            };
            string judged, why;
            Judge(Ok, structured ?? result, out judged, out why);
            step["state"] = judged;
            step["evaluation"] = "reconciled: " + why;
            step["assessment"] = Assess(tool, structured ?? result);
            AdvanceState(record);
            Save(record);
            return new JObject
            {
                ["run_id"] = record["run_id"],
                ["step"] = number,
                ["tool"] = tool,
                ["state"] = judged,
                ["reconciled"] = step["reconciled"],
                ["run_state"] = record["state"],
                ["next"] = NextCall(record),
                ["summary"] = Summary(record)
            };
        }

        private static JObject Record(JObject request)
        {
            JObject record = Load(request.Value<string>("run_id"));
            int number = request.Value<int?>("step") ?? -1;
            JObject step = StepOf(record, number);
            if (step == null)
                throw new ToolRefusal("this run has no step " + number + ".");

            if (step.Value<string>("state") != Pending)
                throw new ToolRefusal(
                    "step " + number + " is already '" + step.Value<string>("state") + "'. A step is recorded " +
                    "once: recording it twice would overwrite the evidence of what actually happened.");

            string outcome = (request.Value<string>("outcome") ?? "").ToLowerInvariant();
            if (outcome != Ok && outcome != Failed && outcome != Skipped)
                throw new ToolRefusal("outcome must be ok, failed or skipped.");

            JToken result = request["result"];
            step["evidence"] = result ?? JValue.CreateNull();
            step["recorded_utc"] = DateTime.UtcNow.ToString("o");
            step["note"] = request.Value<string>("note");

            // THE CALLER SAYS WHAT HAPPENED; THIS CHECKS WHAT IT CAN. A reply that carries
            // its own verdict is read rather than believed - and where there is nothing to
            // read, the step is not_evaluated rather than ok.
            string judged, why;
            Judge(outcome, result, out judged, out why);
            step["state"] = judged;
            step["evaluation"] = why;
            step["assessment"] = Assess((string)step["tool"], result);

            AdvanceState(record);
            Save(record);

            return new JObject
            {
                ["run_id"] = record["run_id"],
                ["step"] = number,
                ["state"] = judged,
                ["evaluation"] = why,
                ["run_state"] = record["state"],
                ["next"] = NextCall(record),
                ["summary"] = Summary(record)
            };
        }

        /// <summary>
        /// What a step's own result says about itself.
        ///
        /// THREE ANSWERS, and the third is the one that keeps this honest. A typed reply
        /// from this bridge carries `verified`, `host_verified` or an application-outcome
        /// block; those are read. A reply that carries none of them cannot be judged from
        /// here, and calling it ok would turn the caller's word into evidence.
        /// </summary>
        internal static void Judge(string claimed, JToken result, out string state, out string why)
        {
            if (claimed == Skipped)
            {
                state = Skipped;
                why = "the caller skipped this step. A skipped step is not a passed one, and the acceptance " +
                      "check below counts it as unmet.";
                return;
            }
            if (claimed == Failed)
            {
                state = Failed;
                why = "the caller reported a failure.";
                return;
            }

            var body = result as JObject;
            if (body == null)
            {
                state = NotEvaluated;
                why = "the caller reported success and sent no result to read. Nothing here can confirm it, " +
                      "so it is recorded as not evaluated rather than as ok.";
                return;
            }

            bool? verified = (bool?)body["verified"] ?? (bool?)body["host_verified"];
            if (verified == false)
            {
                state = Failed;
                why = "the reply itself says it was not verified, whatever the caller reported.";
                return;
            }

            if ((bool?)body["isError"] == true)
            {
                state = Failed;
                why = "the reply is an error result.";
                return;
            }

            if ((bool?)body["coverage_complete"] == false)
            {
                state = NotEvaluated;
                why = "the reply says its coverage was INCOMPLETE. That is neither a pass nor a failure: part " +
                      "of the scope was not looked at, and which part matters to whoever reads this run.";
                return;
            }

            if (verified == true)
            {
                state = Ok;
                why = "the reply re-read its own work and says so.";
                return;
            }

            string declared = (string)(body["application_outcome"] is JObject
                ? body["application_outcome"]["state"] : null) ?? (string)body["state"];
            if (declared != null && declared.IndexOf("partial", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                state = NotEvaluated;
                why = "the reply declares a PARTIAL application. Some rows landed and some did not, which is " +
                      "not a step that passed.";
                return;
            }
            if (declared != null && declared.IndexOf("verified", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                state = Ok;
                why = "the reply declares '" + declared + "'.";
                return;
            }

            // THE CAD CONVERSION'S OWN FIELDS. MEASURED: a batch of three units through
            // dwg-to-bim-unit came back "not evaluated" on all thirty steps, although each
            // apply said how many stages failed and the inventory said whether it adds up.
            if ((bool?)body["reconciles"] == false)
            {
                state = Failed;
                why = "the reply says its totals do NOT reconcile: the reading is not an inventory.";
                return;
            }
            JToken stagesFailed = body["stages_failed"];
            if (stagesFailed != null && stagesFailed.Type == JTokenType.Integer)
            {
                if ((int)stagesFailed > 0)
                {
                    state = Failed;
                    why = "the reply says " + (int)stagesFailed + " stage(s) failed.";
                    return;
                }
                if ((bool?)body["stopped_early"] != true &&
                    (declared == "applied" || declared == "rehearsed" || declared == "nothing_to_apply"))
                {
                    state = Ok;
                    why = "the reply declares '" + declared + "' with no failed stage; each stage re-reads what " +
                          "it built.";
                    return;
                }
            }
            if ((bool?)body["reconciles"] == true)
            {
                state = Ok;
                why = "the reply says every total reconciles.";
                return;
            }
            // A PLAN: the operation succeeded when it produced a plan and blocked nothing.
            // What it withdrew or holds for review is intervention, recorded in the
            // assessment - not a failed operation, and not a complete unit either.
            if (string.Equals((string)body["mode"], "plan", StringComparison.Ordinal) &&
                body["execute_plan_request"] is JObject)
            {
                int blocked = (int?)body["blocked"] ?? 0;
                if (blocked > 0)
                {
                    state = Failed;
                    why = "the plan says " + blocked + " candidate(s) are BLOCKED.";
                    return;
                }
                state = Ok;
                why = "the reply is a plan with nothing blocked; what it withdrew or holds for review is in the " +
                      "assessment.";
                return;
            }
            // AN UPDATE PLAN: the operation succeeded when it produced one. What it holds for a
            // decision or for information is intervention, in the assessment.
            if (body["apply_binding"] is JObject && body["plan"] is JArray && body["needs_a_person"] != null)
            {
                state = Ok;
                why = "the reply is an update plan; what it holds for a person is in the assessment.";
                return;
            }
            // AN AUDIT: the operation succeeded when it read the model against the drawing.
            // Whether the built elements agree is the GEOMETRIC verdict, kept apart.
            if ((bool?)body["read_only"] == true && body["match_states"] is JObject)
            {
                state = Ok;
                why = "the reply is an audit that read the model against the drawing; whether what is built " +
                      "agrees is in the assessment.";
                return;
            }
            if (string.Equals((string)body["status"], "healthy", StringComparison.Ordinal))
            {
                state = Ok;
                why = "the reply says the bridge is healthy.";
                return;
            }

            state = NotEvaluated;
            why = "the reply carries no field this build knows how to read as evidence - no 'verified', no " +
                  "application outcome, no coverage statement. The result is kept; the verdict is not " +
                  "invented.";
        }

        /// <summary>
        /// FOUR QUESTIONS, NEVER ONE. Whether the tool ran, whether what it built agrees
        /// with the drawing, how much of the drawing it covered, and what a person must
        /// still do - each from the reply's own fields, null where the reply says nothing.
        /// </summary>
        internal static JObject Assess(string tool, JToken result)
        {
            var body = result as JObject;
            var a = new JObject { ["tool"] = tool };
            if (body == null) return a;
            if (body["stages_failed"] != null)
            {
                a["operation"] = (int?)body["stages_failed"] == 0 ? "ok" : "failed";
                a["built_verified"] = body["created_verified"];
                a["provenance_written"] = body["provenance_written"];
            }
            if (string.Equals((string)body["mode"], "plan", StringComparison.Ordinal))
            {
                var withdrawn = (body["withdrawn"] as JObject)?["rows"] as JArray;
                int rows = 0;
                if (body["execute_plan_request"]?["actions"] is JArray actions)
                    foreach (JToken act in actions) rows += (act["arguments"]?["elements"] as JArray)?.Count ?? 0;
                a["operation"] = ((int?)body["blocked"] ?? 0) == 0 ? "ok" : "blocked";
                a["planned_rows"] = rows;
                a["coverage_of_drawn_geometry"] = body["coverage"]?["fraction"];
                var intervention = new JObject
                {
                    ["needing_review"] = body["candidates_needing_review"],
                    ["withdrawn"] = withdrawn?.Count ?? 0
                };
                if (withdrawn != null)
                {
                    var byReason = new JObject();
                    foreach (JToken w in withdrawn)
                    {
                        string reason = (string)w["reason"] ?? "unstated";
                        byReason[reason] = ((int?)byReason[reason] ?? 0) + 1;
                    }
                    intervention["withdrawn_by_reason"] = byReason;
                }
                a["intervention"] = intervention;
            }
            if (body["apply_binding"] is JObject && body["plan"] is JArray && body["needs_a_person"] != null)
            {
                a["operation"] = "ok";
                a["intervention"] = new JObject
                {
                    ["automatic_actions"] = body["automatic"],
                    ["awaiting_a_decision"] = body["awaiting_a_decision"],
                    ["held_for_information"] = body["held_for_information"],
                    ["pairings_offered"] = (body["pairings_offered"] as JArray)?.Count ?? 0,
                    ["splits"] = (body["splits"] as JArray)?.Count ?? 0
                };
            }
            if ((bool?)body["read_only"] == true && body["match_states"] is JObject states)
            {
                a["operation"] = "ok";
                a["geometry"] = new JObject
                {
                    ["matched"] = body["matched"]?["total"],
                    ["agrees"] = states["agrees"],
                    ["differs"] = states["differs"],
                    ["verdict"] = ((int?)states["differs"] ?? 0) == 0 ? "every_built_match_agrees" : "some_built_differ"
                };
                a["coverage"] = body["candidate_coverage"] is JObject cc
                    ? new JObject
                    {
                        ["read"] = cc["read"], ["matched"] = cc["matched"], ["not_built"] = cc["not_built"],
                        ["not_built_eligible"] = cc["not_built_eligible"], ["needing_review"] = cc["needing_review"]
                    }
                    : null;
                a["complete"] = (bool?)body["agrees"];
                a["not_measured"] = body["not_measured"];
            }
            if (body["reconciles"] != null)
            {
                a["operation"] = (bool?)body["reconciles"] == true ? "ok" : "failed";
                a["inventory"] = new JObject
                {
                    ["total_rows"] = body["total_rows"],
                    ["by_outcome"] = body["by_outcome"]
                };
            }
            return a;
        }

        private static void AdvanceState(JObject record)
        {
            var steps = (JArray)record["steps"];
            bool anyPending = steps.Any(s => (string)s["state"] == Pending);
            bool anyFailed = steps.Any(s => (string)s["state"] == Failed);

            if (anyFailed) record["state"] = "failed";
            else record["state"] = anyPending ? "running" : "finished";

            // THE ACCEPTANCE VERDICT IS TAKEN ONCE, WHEN THE RUN REACHES ITS END, AND KEPT.
            //
            // It used to be recomputed on every status call from whatever the record held
            // at that moment. A run that finished green and was read back later - after a
            // step's evidence had been re-recorded, or after the checks themselves changed
            // - reported a different verdict than the one it finished with, with nothing
            // in the record saying which was which. The verdict a run ENDED on is a fact
            // about that run, so it is written down at the transition.
            if (record["acceptance_at_end"] == null &&
                ((string)record["state"] == "finished" || (string)record["state"] == "failed"))
            {
                record["acceptance_at_end"] = new JObject
                {
                    ["evaluated_utc"] = DateTime.UtcNow.ToString("o"),
                    ["run_state"] = record["state"],
                    ["checks"] = EvaluateChecks(record),
                    ["means"] = "the acceptance as it stood when this run reached its end. A later status " +
                                "call re-reads the checks against the record as it is NOW and reports both, " +
                                "so a verdict that changed is visible rather than silently replaced."
                };
            }
        }

        /// <summary>
        /// Does this tool CHANGE a model?
        ///
        /// Answered from the contract, not from the step's own wording: this bridge makes
        /// target_document mandatory for every command that writes, so requiring it is the
        /// bridge's own statement that a tool writes. A tool the contract does not know is
        /// treated as a write - the pessimistic reading, because the cost of a wrong
        /// "read-only" is a repeated write and the cost of a wrong "write" is one unused
        /// idempotency key.
        /// </summary>
        internal static bool Writes(string tool)
        {
            if (string.IsNullOrWhiteSpace(tool)) return true;
            Horizun.Contracts.CommandContract contract = Horizun.Contracts.Contract.Find(tool);
            if (contract == null || contract.InputSchema == null) return true;
            var required = contract.InputSchema["required"] as JArray;
            if (required == null) return false;
            foreach (JToken t in required)
                if (string.Equals((string)t, "target_document", StringComparison.Ordinal)) return true;
            return false;
        }

        /// <summary>
        /// Will this tool accept an idempotency_key, or refuse the call for carrying one?
        ///
        /// Nearly every schema here sets additionalProperties:false, so this is not a
        /// nicety: sending the key to a tool that does not declare it turns a write into
        /// an invalid call, and the refusal reads like a bad argument template.
        /// </summary>
        internal static bool AcceptsIdempotencyKey(string tool)
        {
            if (string.IsNullOrWhiteSpace(tool)) return false;
            Horizun.Contracts.CommandContract contract = Horizun.Contracts.Contract.Find(tool);
            if (contract == null || contract.InputSchema == null) return false;
            var properties = contract.InputSchema["properties"] as JObject;
            if (properties != null && properties["idempotency_key"] != null) return true;
            // A schema that permits extra properties takes the key harmlessly.
            JToken extra = contract.InputSchema["additionalProperties"];
            return extra != null && extra.Type == JTokenType.Boolean && (bool)extra;
        }

        /// <summary>
        /// Run one mutating operation with this run held against every other caller.
        ///
        /// TryEnter with no wait, so a second concurrent advance is REFUSED rather than
        /// queued: queueing would let it succeed after the first and dispatch the NEXT
        /// step, which is work nobody asked for at a moment nobody chose.
        /// </summary>
        private static JObject Serialised(JObject request, Func<JObject> body)
        {
            string runId = request.Value<string>("run_id");
            if (string.IsNullOrWhiteSpace(runId)) throw new ToolRefusal("run_id is required.");
            object gate = LockFor(runId);
            if (!System.Threading.Monitor.TryEnter(gate))
                throw new ToolRefusal(
                    "another operation is already in flight on run '" + runId + "'. Two callers advancing one " +
                    "run both dispatch the same step, and the record - written by whichever finishes last - " +
                    "shows one dispatch. Nothing was done here; wait for the other call and read the result " +
                    "with operation=status.");
            try { return body(); }
            finally { System.Threading.Monitor.Exit(gate); }
        }

        private static JObject Status(JObject request)
        {
            JObject record = Load(request.Value<string>("run_id"));
            var reply = new JObject
            {
                ["run"] = record,
                ["next"] = NextCall(record),
                ["summary"] = Summary(record)
            };

            // BOTH VERDICTS, when the run has ended: the one it ended on, kept at the
            // transition, and the one the record produces now. They normally agree, and
            // when they do not, the disagreement is the finding.
            JObject atEnd = record["acceptance_at_end"] as JObject;
            if (atEnd != null)
            {
                reply["acceptance_at_end"] = atEnd;
                JArray now = EvaluateChecks(record);
                reply["acceptance_now"] = now;
                reply["acceptance_agrees"] =
                    JToken.DeepEquals(atEnd["checks"] ?? new JArray(), now);
                if (!(bool)reply["acceptance_agrees"])
                    reply["acceptance_disagreement_means"] =
                        "the acceptance this run ENDED on and the acceptance its record produces NOW are not " +
                        "the same. Something about the record changed after the run finished. The one it " +
                        "ended on is what happened.";
            }
            return reply;
        }

        private static JObject Abandon(JObject request)
        {
            JObject record = Load(request.Value<string>("run_id"));
            string why = request.Value<string>("reason");
            if (string.IsNullOrWhiteSpace(why))
                throw new ToolRefusal(
                    "abandon requires a reason. A run abandoned without one leaves the next person unable to " +
                    "tell an unfinished procedure from one that was deliberately stopped.");

            record["state"] = "abandoned";
            record["abandoned_utc"] = DateTime.UtcNow.ToString("o");
            record["abandoned_because"] = why;
            Save(record);

            return new JObject
            {
                ["run_id"] = record["run_id"],
                ["state"] = "abandoned",
                ["summary"] = Summary(record),
                ["means"] = "the record is kept. What was done before the abandonment stays done in the model: " +
                            "this tool never undoes anything, because it never did anything."
            };
        }

        // =====================================================================

        /// <summary>The next call to make, with what the catalogue said about it.</summary>
        private static JToken NextCall(JObject record)
        {
            if ((string)record["state"] != "running") return JValue.CreateNull();
            JObject step = ((JArray)record["steps"]).OfType<JObject>()
                .FirstOrDefault(s => (string)s["state"] == Pending);
            if (step == null) return JValue.CreateNull();

            return new JObject
            {
                ["step"] = step["step"],
                ["tool"] = step["tool"],
                ["purpose"] = step["purpose"],
                ["needs_from_earlier_steps"] = step["needs_from_earlier_steps"],
                ["preconditions"] = step["preconditions"],
                ["on_error"] = step["on_error"],
                ["reads_back"] = step["reads_back"],
                ["target_document"] = record["target_document"],
                ["how"] =
                    "Call this tool yourself, with its own arguments. A write keeps its dry_run and its " +
                    "confirmation token: nothing about being inside a procedure changes what a write requires. " +
                    "Then send the reply back with operation=record, step=" + step["step"] + "."
            };
        }

        private static JObject Summary(JObject record)
        {
            var steps = (JArray)record["steps"];
            var byState = new JObject();
            foreach (IGrouping<string, JToken> group in steps.GroupBy(s => (string)s["state"]))
                byState[group.Key] = group.Count();

            int ok = steps.Count(s => (string)s["state"] == Ok);
            int unmet = steps.Count - ok;

            JArray checks = EvaluateChecks(record);
            bool checksPassed = checks.OfType<JObject>().All(c => (bool?)c["passed"] == true);
            int checksRun = checks.Count;

            // THE UNIT, NOT THE CALLS: what the recorded assessments say together.
            var assessments = steps.Select(s => s["assessment"] as JObject).Where(x => x != null).ToList();
            var geometry = assessments.Where(x => x["geometry"] is JObject).Select(x => (JObject)x["geometry"]).ToList();
            int withdrawnTotal = 0, reviewTotal = 0;
            foreach (JObject x in assessments)
            {
                withdrawnTotal += (int?)x["intervention"]?["withdrawn"] ?? 0;
                reviewTotal += (int?)x["intervention"]?["needing_review"] ?? 0;
            }
            var unit = new JObject
            {
                ["operations_ok"] = assessments.Count(x => (string)x["operation"] == "ok"),
                ["operations_assessed"] = assessments.Count(x => x["operation"] != null),
                ["built_matches_differing"] = geometry.Sum(g => (int?)g["differs"] ?? 0),
                ["audits_complete"] = geometry.Count == 0 ? null
                    : (JToken)assessments.Where(x => x["geometry"] != null).All(x => (bool?)x["complete"] == true),
                ["withdrawn_rows"] = withdrawnTotal,
                ["rows_needing_review"] = reviewTotal,
                ["means"] = "operations_ok says the calls did their job; built_matches_differing says whether what " +
                            "was built agrees with the drawing; audits_complete says whether the drawing is fully " +
                            "built; withdrawn and review rows are the intervention a person still owes. stages_failed " +
                            "= 0 answers only the first."
            };

            return new JObject
            {
                ["steps"] = steps.Count,
                ["by_state"] = byState,
                ["unit_assessment"] = unit,
                ["all_steps_ok"] = unmet == 0,
                ["steps_means"] = unmet == 0
                    ? "every step was recorded and carried evidence this build could read."
                    : unmet + " step(s) are pending, skipped, failed or could not be evaluated.",

                // THE TWO THINGS THAT ARE NOT THE SAME, kept apart on purpose.
                ["acceptance"] = record["acceptance"],
                ["acceptance_checks"] = checks,
                ["acceptance_checks_passed"] = checksRun > 0 && checksPassed,
                ["acceptance_means"] = checksRun == 0
                    ? "THIS PROCEDURE DECLARES NO EVALUABLE CHECKS. Its acceptance criterion is the prose " +
                      "above and reading it is somebody's job: nothing here has evaluated it, and the step " +
                      "states are not a substitute - every step can be ok and the criterion still unmet."
                    : checksPassed && unmet == 0
                        ? checksRun + " declared check(s) passed against the recorded results. That is NOT " +
                          "the prose criterion above being met: these look at what the tools RETURNED, and " +
                          "the criterion is about the MODEL. It narrows what a person has to read; it does " +
                          "not replace it."
                        : "the declared checks did not all pass, or a step did not complete. The procedure " +
                          "has not been completed, whatever the individual replies said."
            };
        }

        /// <summary>
        /// Evaluate the checks this procedure declared, against what its steps returned.
        ///
        /// A check whose step has not run, or whose field is absent, is NOT PASSED and says
        /// which of the two it was. Treating an absent field as satisfied is how a criterion
        /// comes to pass against a reply that never carried it.
        /// </summary>
        private static JArray EvaluateChecks(JObject record)
        {
            var results = new JArray();
            foreach (JObject check in (record["acceptance_checks"] as JArray ?? new JArray()).OfType<JObject>())
            {
                int number = check.Value<int?>("step") ?? -1;
                string path = check.Value<string>("path");
                string expect = (check.Value<string>("expect") ?? "").ToLowerInvariant();

                var row = new JObject
                {
                    ["step"] = number,
                    ["path"] = path,
                    ["expect"] = expect,
                    ["why"] = check["why"]
                };

                JObject step = StepOf(record, number);
                if (step == null || (string)step["state"] == Pending)
                {
                    row["passed"] = false;
                    row["observed"] = JValue.CreateNull();
                    row["means"] = "step " + number + " has not run, so this check has not been evaluated - " +
                                   "which is not the same as it failing, and is not a pass either.";
                    results.Add(row);
                    continue;
                }

                JToken value = Dig(step["evidence"], path);
                row["observed"] = value == null ? JValue.CreateNull() : value.DeepClone();

                if (value == null || value.Type == JTokenType.Null)
                {
                    row["passed"] = false;
                    row["means"] = "step " + number + "'s result carries nothing at '" + path + "'. An absent " +
                                   "field is not a satisfied one: this check cannot be met by a reply that " +
                                   "never made the claim.";
                    results.Add(row);
                    continue;
                }

                bool passed;
                switch (expect)
                {
                    case "true": passed = value.Type == JTokenType.Boolean && (bool)value; break;
                    case "false": passed = value.Type == JTokenType.Boolean && !(bool)value; break;

                    // ZERO, for the commonest acceptance criterion this bridge has:
                    // a COUNT of things that went wrong, which passes when it is 0.
                    //
                    // Without it, a check on stages_failed or open_connectors_total
                    // had to be written as "false" - and the false predicate
                    // requires a Boolean, so an integer 0 failed it. A check that
                    // can never pass is worse than no check: it reports a clean run
                    // as unacceptable, on exactly the routes whose value is being
                    // trustworthy about whether the model is finished.
                    //
                    // STRICT ABOUT TYPE on purpose. A `zero` check against a string
                    // or an array is a MISMATCH rather than a pass, because a field
                    // that changed shape is a finding.
                    case "zero":
                        passed = (value.Type == JTokenType.Integer && (long)value == 0) ||
                                 (value.Type == JTokenType.Float && Math.Abs((double)value) <= double.Epsilon);
                        break;

                    case "non_empty": passed = NonEmpty(value); break;
                    case "all_true":
                        passed = value is JArray array && array.Count > 0 &&
                                 array.All(v => v.Type == JTokenType.Boolean && (bool)v);
                        break;
                    default:
                        passed = false;
                        row["means"] = "'" + expect + "' is not an expectation this build evaluates.";
                        break;
                }

                row["passed"] = passed;
                if (row["means"] == null)
                    row["means"] = passed ? "satisfied by the recorded result."
                                          : "the recorded result does not satisfy it.";
                results.Add(row);
            }
            return results;
        }

        /// <summary>Present, and not the empty version of whatever it is.</summary>
        private static bool NonEmpty(JToken value)
        {
            switch (value.Type)
            {
                case JTokenType.Array: return ((JArray)value).Count > 0;
                case JTokenType.String: return !string.IsNullOrWhiteSpace((string)value);
                case JTokenType.Integer: return (long)value != 0;
                case JTokenType.Float: return Math.Abs((double)value) > double.Epsilon;
                case JTokenType.Boolean: return (bool)value;
                case JTokenType.Object: return ((JObject)value).Properties().Any();
                default: return false;
            }
        }

        private static JObject StepOf(JObject record, int number) =>
            ((JArray)record["steps"]).OfType<JObject>()
                .FirstOrDefault(s => (int?)s["step"] == number);

        /// <summary>The key a caller would use for an input the catalogue describes in prose.</summary>
        internal static string InputKey(string described)
        {
            if (string.IsNullOrWhiteSpace(described)) return null;
            // The catalogue writes inputs as sentences ("the sheet size and title block type,
            // by id"). The key is the first quoted or snake_case token where there is one;
            // otherwise there is nothing to match and the input is not checked.
            string[] words = described.Split(new[] { ' ', ',', '.', ':', ';' },
                                             StringSplitOptions.RemoveEmptyEntries);
            return words.FirstOrDefault(w => w.Contains("_") && w.All(
                c => char.IsLetterOrDigit(c) || c == '_'));
        }

        // =====================================================================

        private static JObject Load(string runId)
        {
            if (string.IsNullOrWhiteSpace(runId)) throw new ToolRefusal("run_id is required.");
            string path = PathOf(runId);
            if (!File.Exists(path))
                throw new ToolRefusal("no run with id '" + runId + "'. Runs older than " + RetentionDays +
                                      " days are swept.");
            try { return JObject.Parse(File.ReadAllText(path)); }
            catch (Exception ex)
            {
                throw new ToolRefusal("the run record for '" + runId + "' could not be read (" + ex.Message +
                                      "). It is not repaired here: a half-read procedure run is worse than a " +
                                      "missing one.");
            }
        }

        private static void Save(JObject record)
        {
            string target = PathOf((string)record["run_id"]);
            string temporary = target + ".tmp";
            File.WriteAllText(temporary, record.ToString(Formatting.Indented),
                              new System.Text.UTF8Encoding(false));
            if (File.Exists(target)) File.Replace(temporary, target, null);
            else File.Move(temporary, target);
        }

        /// <summary>Remove runs nobody came back to. Best effort; never fails a call.</summary>
        private static void Sweep()
        {
            try
            {
                DateTime cutoff = DateTime.UtcNow.AddDays(-RetentionDays);
                foreach (FileInfo file in new DirectoryInfo(Root()).GetFiles("*.json"))
                    if (file.LastWriteTimeUtc < cutoff)
                        try { file.Delete(); } catch { }
            }
            catch { }
        }

        private static string Safe(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) throw new ToolRefusal("run_id is required.");
            foreach (char c in id)
                if (!char.IsLetterOrDigit(c) && c != '-' && c != '_')
                    throw new ToolRefusal("run_id may hold letters, digits, '-' and '_' only.");
            return id;
        }
    }
}
