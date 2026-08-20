// -----------------------------------------------------------------------------
// Compose verified typed commands into one confirmed, atomic Revit operation.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public sealed class ExecutePlanCommand : ICommand
    {
        private readonly Func<string, ICommand> _resolve;
        private static readonly HashSet<string> Allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "horizun_write_params_verified", "horizun_delete_verified", "horizun_create_schedule",
            "horizun_set_keynote", "horizun_family_apply", "horizun_bind_shared_param",
            "horizun_create_elements", "horizun_manage_system_types", "horizun_transform_elements", "horizun_manage_views", "horizun_annotate",
            "horizun_split_floor_loops", "horizun_split_multilayer_walls", "horizun_split_multilayer_slabs",
            "horizun_ungroup_and_mark", "horizun_regroup_by_param", "horizun_copy_slab_elevations",
            "horizun_embed_floors_in_toposolid", "horizun_grade_toposolid_around_floors",
            "horizun_rectangularize_walls"
        };

        public ExecutePlanCommand(Func<string, ICommand> resolve) { _resolve = resolve; }
        public string Name => "horizun_execute_plan";
        public string Description => "Run an ordered graph of typed Revit writes as one atomic, verified plan.";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            JObject request;
            try { request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JObject.Parse(paramsJson); }
            catch (JsonException ex) { return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message); }
            GateResult gate = DocumentGate.ForMutation(app, request, Name);
            if (!gate.Ok) return gate.Refusal;
            JArray actions = request["actions"] as JArray;
            if (actions == null || actions.Count == 0 || actions.Count > 100)
                return CommandResult.Fail("actions must contain 1..100 entries.");

            var keys = new HashSet<string>(StringComparer.Ordinal);
            var errors = new JArray();
            for (int i = 0; i < actions.Count; i++)
            {
                JObject action = actions[i] as JObject;
                string key = action?.Value<string>("key");
                string tool = action?.Value<string>("tool");
                if (action == null) errors.Add(Error(i, "action is not an object"));
                else if (string.IsNullOrWhiteSpace(key)) errors.Add(Error(i, "key is required"));
                else if (!keys.Add(key)) errors.Add(Error(i, "key '" + key + "' is duplicated"));
                else if (!Allowed.Contains(tool ?? "")) errors.Add(Error(i, "tool '" + tool + "' is not allowed in an atomic plan"));
                else if (_resolve(tool) == null) errors.Add(Error(i, "tool '" + tool + "' is not installed in this add-in"));
                else if (!(action["arguments"] is JObject)) errors.Add(Error(i, "arguments must be an object"));
                else foreach (string referenced in PlanReferences.ReferenceKeys(action["arguments"]))
                    if (string.Equals(referenced, key, StringComparison.Ordinal) || !keys.Contains(referenced))
                        errors.Add(Error(i, "reference to '" + referenced + "' is not a prior action"));
            }

            bool dryRun = request["dry_run"] == null || request.Value<bool>("dry_run");
            string planHash = DocumentGate.PlanHash(request, "actions");
            if (errors.Count > 0)
                return CommandResult.Fail("Invalid atomic plan; nothing ran: " + errors.ToString(Formatting.None));

            if (dryRun)
            {
                var known = new Dictionary<string, JToken>(StringComparer.Ordinal);
                var ledger = new PlanLedger();
                var rows = new JArray();
                for (int i = 0; i < actions.Count; i++)
                {
                    JObject action = (JObject)actions[i];
                    string key = action.Value<string>("key");
                    JObject original = (JObject)action["arguments"];
                    JObject supplied = original;
                    JObject referenceBinding = null;
                    if (PlanReferences.HasReference(original))
                    {
                        string resolveError;
                        JToken resolved = PlanReferences.Resolve(original, known, out resolveError);
                        if (resolveError != null)
                        {
                            rows.Add(ledger.RecordDeferred(i, key, action.Value<string>("tool"), resolveError));
                            continue;
                        }
                        supplied = (JObject)resolved;
                        referenceBinding = PlanReferences.DescribeBinding(original, supplied);
                    }
                    JObject child = ChildArguments(supplied, gate, true);
                    CommandResult rehearsal = _resolve(action.Value<string>("tool")).Execute(app, child.ToString(Formatting.None));
                    // A rehearsal that ANSWERED is not the same as a rehearsal that RESOLVED
                    // what it was given. A child that could not resolve half its rows returns
                    // Ok and says so in its own counts; the ledger reads that declaration, and
                    // a graph carrying one does not get an executable token below.
                    JObject row = ledger.RecordRehearsal(i, key, action.Value<string>("tool"),
                                                         rehearsal.Success, rehearsal.Data, rehearsal.Error,
                                                         rehearsal.Detail, FallbackJson(rehearsal),
                                                         rehearsal.CapabilityGaps);
                    if (referenceBinding != null) row["reference_binding"] = referenceBinding;
                    rows.Add(row);
                    if (!rehearsal.Success)
                        return BeforeGroupFailure("dry_run", rows, ledger.FailedAction,
                            "Atomic plan rehearsal failed at action '" + key + "': " + rehearsal.Error,
                            rehearsal);
                    known[key] = ToToken(rehearsal.Data);
                }
                bool rehearsedCleanly = ledger.RehearsedCleanly;

                // ACTIONS THAT WERE NEVER REHEARSED AT ALL, counted and named.
                //
                // A deferred action's arguments contain ${key.path} pointing at something an
                // earlier action has not produced in rehearsal. Until a typed symbolic
                // contract can bind its type/cardinality/provenance, it dirties the rehearsal
                // and withholds the outer token. Disclosure is not authorization.
                //
                // What replaces the preview for these, and it is the whole of it: the graph
                // shape and their position in it are bound by the token; their arguments are
                // resolved inside the confirmed group; and the apply-time rule refuses to run
                // anything after one, or to keep the group, unless it comes back fully applied
                // and verified. What is NOT covered is WHICH elements they will name - a
                // deferred horizun_delete_verified computes its targets at apply time.
                var notRehearsed = new JArray(rows.OfType<JObject>()
                    .Where(r => r.Value<string>("status") == "deferred_until_execution")
                    .Select(r => (JToken)new JObject
                    {
                        ["index"] = r["index"], ["key"] = r["key"], ["tool"] = r["tool"], ["reason"] = r["reason"]
                    }));

                var data = new JObject { ["dry_run"] = true, ["transaction_status"] = "not_started",
                    ["actions"] = rows,
                    ["rehearsed_cleanly"] = rehearsedCleanly,
                    ["confirmation_withheld"] = !rehearsedCleanly,
                    ["actions_not_rehearsed"] = notRehearsed.Count,
                    ["not_rehearsed"] = notRehearsed,
                    ["rehearsed_cleanly_means"] =
                        "every action was concretely rehearsed, every reference resolved, and every child declared " +
                        "a clean rehearsal. Any action in not_rehearsed makes this false and withholds confirmation.",
                    ["note"] = rehearsedCleanly
                        ? "Every action was semantically rehearsed. References that resolved are bound to their " +
                          "exact canonical values and are checked again before their consumer executes."
                        : "NO EXECUTABLE CONFIRMATION WAS ISSUED. At least one action rehearsed to something other " +
                          "than a clean dry run - read each row's application_state. A rehearsal that could not " +
                          "resolve what it was given has not previewed this plan, and a token over it would " +
                          "authorise an apply nobody saw. Fix those actions and rehearse again." };
                // The recorded plan and the token travel together, and neither is issued over
                // a rehearsal that did not resolve. Same shape the other commands use: the
                // flag that gates the token is the flag that gates the plan.
                if (rehearsedCleanly) DocumentGate.RecordResolvedPlan(GraphPlan(app, gate, rows, actions));
                DocumentGate.StampConfirmation(data, gate, Name, planHash, rehearsedCleanly,
                    "one token authorizes this exact ordered graph, AND it is bound to what each independent " +
                    "action's own rehearsal resolved: the apply re-rehearses them (read-only) and refuses as a " +
                    "stale plan if any resolves differently. A reference that cannot resolve during rehearsal " +
                    "withholds confirmation; references that do resolve are bound to the exact canonical value " +
                    "seen then. Every action must come back fully applied and verified or the whole group rolls " +
                    "back. External I/O and arbitrary code are never allowed inside a plan.");
                return CommandResult.Ok(data);
            }

            // ---- The materialised plan, RECOMPUTED: re-rehearse every independent action
            // read-only before anything runs. Inside the confirmed group the children's
            // own plan checks degrade to a document-identity check (EnterConfirmedAtomicPlan),
            // so this is the moment the element-level guarantee is enforced for a graph.
            // The cost is running each child's dry run twice per apply; the alternative is
            // an approval that froze the words of the graph and nothing it resolved to.
            var recheck = new JArray();
            var expectedBindings = new Dictionary<int, JObject>();
            var expectedChildPlans = new Dictionary<int, string>();
            {
                var known = new Dictionary<string, JToken>(StringComparer.Ordinal);
                var recheckLedger = new PlanLedger();
                for (int i = 0; i < actions.Count; i++)
                {
                    JObject action = (JObject)actions[i];
                    string key = action.Value<string>("key");
                    JObject original = (JObject)action["arguments"];
                    JObject supplied = original;
                    JObject referenceBinding = null;
                    if (PlanReferences.HasReference(original))
                    {
                        string resolveError;
                        JToken resolvedArgs = PlanReferences.Resolve(original, known, out resolveError);
                        if (resolveError != null)
                        {
                            JObject deferred = recheckLedger.RecordDeferred(i, key, action.Value<string>("tool"), resolveError);
                            recheck.Add(deferred);
                            return BeforeGroupFailure("pre_apply_recheck", recheck, deferred,
                                "Pre-apply reference resolution failed at action '" + key + "': " + resolveError, null);
                        }
                        supplied = (JObject)resolvedArgs;
                        referenceBinding = PlanReferences.DescribeBinding(original, supplied);
                        expectedBindings[i] = referenceBinding;
                    }
                    JObject childArgs = ChildArguments(supplied, gate, true);
                    CommandResult again = _resolve(action.Value<string>("tool")).Execute(app, childArgs.ToString(Formatting.None));
                    // The same bar as the dry run, applied at the moment the token is about to
                    // be spent: an action that resolved cleanly when it was approved and does
                    // not now is exactly the drift this re-rehearsal exists to catch.
                    JObject againRow = recheckLedger.RecordRehearsal(i, key, action.Value<string>("tool"),
                                                                     again.Success, again.Data, again.Error,
                                                                     again.Detail, FallbackJson(again),
                                                                     again.CapabilityGaps);
                    if (referenceBinding != null) againRow["reference_binding"] = referenceBinding;
                    string expectedChildPlan = null;
                    try { expectedChildPlan = (string)againRow["data"]?["plan_resolved"]?["fingerprint"]; }
                    catch { expectedChildPlan = null; }
                    if (!string.IsNullOrWhiteSpace(expectedChildPlan))
                        expectedChildPlans[i] = expectedChildPlan;
                    recheck.Add(againRow);
                    if (!again.Success)
                        return BeforeGroupFailure("pre_apply_recheck", recheck, recheckLedger.FailedAction,
                            "Pre-apply rehearsal failed at action '" + key + "': " + again.Error, again);
                    ApplicationState againState = ApplicationOutcome.Read(again.Data);
                    if (!recheckLedger.RehearsedCleanly)
                        return BeforeGroupFailure("pre_apply_recheck", recheck, recheckLedger.FailedAction,
                            "Pre-apply rehearsal of action '" + key + "' came back '" +
                            ApplicationOutcome.Name(againState) + "', not a clean dry run. Nothing ran, and no " +
                            "TransactionGroup was opened. The graph approved a rehearsal that resolved; this one " +
                            "does not, so applying it would write something nobody previewed.", again);
                    known[key] = ToToken(again.Data);
                }
            }
            CommandResult refusal = DocumentGate.RequireConfirmation(app, gate, request, Name, planHash,
                                                                     GraphPlan(app, gate, recheck, actions), null);
            if (refusal != null) return refusal;
            string groupName = request.Value<string>("transaction_name");
            if (string.IsNullOrWhiteSpace(groupName)) groupName = "Horizun: atomic plan";
            var results = new Dictionary<string, JToken>(StringComparer.Ordinal);
            // The book this apply keeps: the executed rows, the continue/stop decision after
            // each one, the row that stopped it, and the verified count at the end. It is
            // Revit-free on purpose - see PlanLedger - because these are the cases a live
            // Revit will not produce on demand.
            var applyLedger = new PlanLedger();
            JArray executed = applyLedger.Executed;
            using (var group = new TransactionGroup(gate.Document, groupName))
            {
                if (group.Start() != TransactionStatus.Started)
                    return CommandResult.Fail("Revit refused to start the atomic TransactionGroup. Nothing ran.");
                try
                {
                    using (DocumentGate.EnterConfirmedAtomicPlan())
                    {
                        for (int i = 0; i < actions.Count; i++)
                        {
                            JObject action = (JObject)actions[i];
                            string key = action.Value<string>("key");
                            string resolveError;
                            JObject original = action["arguments"] as JObject;
                            JObject child = PlanReferences.Resolve(original, results, out resolveError) as JObject;
                            if (resolveError != null)
                            {
                                JObject referenceFailure = new JObject
                                {
                                    ["code"] = "reference_resolution_failed",
                                    ["reason"] = resolveError
                                };
                                ApplicationState ignored;
                                applyLedger.RecordExecuted(i, key, action.Value<string>("tool"), false, null,
                                    "reference_resolution_failed: " + resolveError,
                                    referenceFailure, null, null, out ignored);
                                throw new InvalidOperationException("action '" + key + "': " + resolveError);
                            }
                            if (PlanReferences.HasReference(original))
                            {
                                JObject expected;
                                expectedBindings.TryGetValue(i, out expected);
                                JObject comparison = PlanReferences.CompareBinding(expected, original, child);
                                if (!comparison.Value<bool>("matches"))
                                {
                                    ApplicationState ignored;
                                    applyLedger.RecordExecuted(i, key, action.Value<string>("tool"), false, null,
                                        "reference_binding_changed: the resolved arguments differ from the approved rehearsal",
                                        comparison, null, null, out ignored);
                                    throw new InvalidOperationException("action '" + key +
                                        "': reference_binding_changed; the consumer was not executed");
                                }
                            }
                            child = ChildArguments(child, gate, false);
                            string expectedChildPlan;
                            if (expectedChildPlans.TryGetValue(i, out expectedChildPlan))
                                child["__expected_plan_fingerprint"] = expectedChildPlan;
                            CommandResult result = _resolve(action.Value<string>("tool")).Execute(app, child.ToString(Formatting.None));

                            // THE CHECK THIS WHOLE CHANGE EXISTS FOR. Success means the child
                            // ANSWERED. Only a declared full application means the model carries
                            // what was asked for - and only that may let the NEXT action, which
                            // may well be a delete, run on top of it, or let this group be kept.
                            ApplicationState applied;
                            if (!applyLedger.RecordExecuted(i, key, action.Value<string>("tool"),
                                                            result.Success, result.Data, result.Error,
                                                            result.Detail, FallbackJson(result),
                                                            result.CapabilityGaps, out applied))
                            {
                                throw new InvalidOperationException(result.Success
                                    ? PlanLedger.StopMessage(key, action.Value<string>("tool"),
                                                             ApplicationOutcome.IsDeclared(result.Data), applied)
                                    : "action '" + key + "' failed: " + result.Error);
                            }
                            results[key] = ToToken(result.Data);
                        }
                    }
                    Guard.Assimilate(group, groupName);
                }
                catch (Exception ex)
                {
                    // The group started (Start() succeeded above, or we would have returned). Roll
                    // it back ONLY if it is still open, and report the status Revit ACTUALLY
                    // returned - never the fixed prose "everything was rolled back". If Assimilate
                    // itself found a silent rollback the group is already closed; we do not call
                    // RollBack again, but the group's final status still tells us whether the model
                    // is clean. rollback_confirmed is computed from that final status, so a RollBack
                    // that returned Error surfaces as UNCERTAIN, not as done.
                    // NOTHING IN HERE MAY THROW ITS WAY OUT. Every read below is a call into
                    // Revit at the moment Revit has already misbehaved once, and an exception
                    // escaping this block would take the whole structured diagnostic with it -
                    // no execution_trace, no failed_action, no rollback_confirmed - leaving the
                    // caller a bare message at exactly the moment the model's state is least
                    // certain. A rollback that throws is not less information than a rollback
                    // that fails; it is more, and it has to reach the reply.
                    bool rollbackAttempted = false;
                    string rollbackStatus = PlanFailure.NotAttempted;
                    string rollbackError = null;

                    TransactionStatus statusBeforeRollback;
                    string statusReadError = null;
                    try { statusBeforeRollback = group.GetStatus(); }
                    catch (Exception readEx)
                    {
                        statusBeforeRollback = TransactionStatus.Uninitialized;
                        statusReadError = readEx.Message;
                    }

                    if (statusReadError == null && statusBeforeRollback == TransactionStatus.Started)
                    {
                        rollbackAttempted = true;
                        try { rollbackStatus = Guard.RollBack(group).StatusName; }
                        catch (Exception rollbackEx)
                        {
                            // Attempted, and we do not know what it did. Anything other than a
                            // confirmed RolledBack leaves rollback_confirmed false, which is the
                            // honest answer here.
                            rollbackStatus = "threw";
                            rollbackError = rollbackEx.Message;
                        }
                    }

                    string finalStatus;
                    try { finalStatus = group.GetStatus().ToString(); }
                    catch (Exception readEx)
                    {
                        finalStatus = "unreadable";
                        if (statusReadError == null) statusReadError = readEx.Message;
                    }

                    JObject diag = PlanFailure.Diagnostic(
                        transactionGroupStarted: true,
                        transactionGroupStatus: finalStatus,
                        rollbackAttempted: rollbackAttempted,
                        rollbackStatus: rollbackStatus,
                        executionTrace: executed,
                        error: ex.Message,
                        failedAction: applyLedger.FailedAction);

                    // Explicit nulls when nothing went wrong, so "the rollback threw" and "this
                    // reply does not carry that field" stay different facts.
                    diag["rollback_error"] = rollbackError == null ? (JToken)JValue.CreateNull() : rollbackError;
                    diag["transaction_group_status_error"] =
                        statusReadError == null ? (JToken)JValue.CreateNull() : statusReadError;

                    return CommandResult.FailWithDetail(PlanFailure.Message(diag), diag);
                }
            }
            return CommandResult.Ok(applyLedger.SuccessPayload(groupName, new JObject(results)));
        }

        /// <summary>
        /// The graph's materialised plan, built identically from the rehearsal rows and
        /// from the pre-apply recheck rows. One element per action; the fact that binds is
        /// the CHILD's own plan fingerprint where the child materialises one - the same
        /// mechanism, composed. A child that does not (or a row deferred behind a
        /// reference) contributes its status instead, so wired and unwired children are
        /// distinguishable in what the token covers.
        /// </summary>
        private static Core.ResolvedPlan GraphPlan(UIApplication app, GateResult gate, JArray rows, JArray actions)
        {
            var plan = new Core.ResolvedPlan
            {
                Command = "horizun_execute_plan",
                DocumentKey = gate.Fingerprint,
                RevitVersion = SafeVersion(app),
                DocumentFingerprint = gate.Identity?.FingerprintDigest()
            };
            for (int i = 0; i < rows.Count; i++)
            {
                JObject row = rows[i] as JObject;
                if (row == null) continue;
                string childFp = null;
                try { childFp = (string)row["data"]?["plan_resolved"]?["fingerprint"]; } catch { }
                plan.Elements.Add(new Core.PlannedElement
                {
                    UniqueId = "action:" + (row.Value<int?>("index") ?? i),
                    Category = row.Value<string>("tool"),
                    Action = Core.PlannedAction.Modify,
                    BeforeValues = new Dictionary<string, string>
                    {
                        { "key", row.Value<string>("key") ?? "" },
                        { "child", childFp ?? (row.Value<string>("status") == "deferred_until_execution"
                                                  ? "deferred" : "no_child_plan") },
                        { "reference_original", row["reference_binding"]?.Value<string>("original_hash") ?? "" },
                        { "reference_resolved", row["reference_binding"]?.Value<string>("resolved_hash") ?? "" }
                    }
                });
            }
            return plan;
        }

        private static string SafeVersion(UIApplication app)
        {
            try { return app?.Application?.VersionNumber; } catch { return null; }
        }

        private static JObject ChildArguments(JObject source, GateResult gate, bool dryRun)
        {
            JObject child = source == null ? new JObject() : (JObject)source.DeepClone();
            child["target_document"] = gate.Identity.Path ?? gate.Identity.Title;
            child["target_document_title"] = gate.Identity.Title;
            child["dry_run"] = dryRun;
            child.Remove("confirmation_token"); child.Remove("idempotency_key");
            return child;
        }

        /// <summary>
        /// A child's fallback signal as JSON, or null when it carried none. Serialized the
        /// same way the transport does it, so what a plan's trace shows and what a direct
        /// call shows are the same block rather than two renderings of one idea.
        /// </summary>
        private static JToken FallbackJson(CommandResult result)
        {
            if (result?.Fallback == null) return null;
            try { return JToken.FromObject(result.Fallback); }
            catch { return null; }
        }

        private static CommandResult BeforeGroupFailure(string phase, JArray trace, JObject failedAction,
                                                        string error, CommandResult child)
        {
            JObject detail = PlanFailure.BeforeGroup(phase, trace, failedAction, error);
            return CommandResult.FailWithDetail(error + ". Nothing ran.", detail,
                                                child?.Fallback, child?.CapabilityGaps);
        }

        private static JObject Error(int index, string error) => new JObject { ["index"] = index, ["error"] = error };
        private static JToken ToToken(object data) => data == null ? JValue.CreateNull() :
            (data as JToken)?.DeepClone() ?? JToken.FromObject(data);
    }
}
