// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// THE BOOK A PLAN KEEPS WHILE IT RUNS, and the decision it takes after each
// action: may the next one run, and may this group be assimilated at the end.
//
// WHY IT IS NOT INSIDE ExecutePlanCommand. That command cannot be constructed
// without a Revit - it needs a UIApplication, a Document and a TransactionGroup -
// so every scenario that matters here was, until now, unreachable by any test:
// a child that answers success over a rollback, a partial write followed by a
// DELETE, a failure in the middle of a graph that already wrote. Those are exactly
// the cases where being wrong is most expensive, and exactly the cases a live
// Revit will not produce on demand.
//
// So the Revit stays in the command (transaction group, rollback, the gate) and
// the DECISION lives here, where each case is an ordinary test. The command holds
// one of these and asks it; it does not re-implement any of it, which is what
// keeps the tested rule and the shipped rule the same rule.
//
// THE RULE, in one line: an action may be built on, and a group may be kept, only
// while every action so far declared a full application (ApplicationOutcome).
// Transport success is not that, command success is not that, and neither is a
// row existing in the trace.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public sealed class PlanLedger
    {
        internal const string ExpectedChildPlanField = "__expected_plan_fingerprint";

        internal static JObject ChildArguments(JObject source, string path, string title, bool dryRun,
            string expectedChildPlan = null)
        {
            var child = (JObject)source?.DeepClone() ?? new JObject();
            child["target_document"] = path ?? title;
            child["target_document_title"] = title;
            child["dry_run"] = dryRun;
            child.Remove("confirmation_token");
            child.Remove("idempotency_key");
            child.Remove(ExpectedChildPlanField);
            if (!dryRun && expectedChildPlan != null) child[ExpectedChildPlanField] = expectedChildPlan;
            return child;
        }

        /// <summary>
        /// The graph's materialised plan, built identically from the rehearsal rows and
        /// from the pre-apply recheck rows. One element per action; the fact that binds is
        /// the CHILD's own plan fingerprint where the child materialises one - the same
        /// mechanism, composed. A child that does not (or a row deferred behind a
        /// reference) contributes its status instead, so wired and unwired children are
        /// distinguishable in what the token covers.
        /// </summary>
        internal static ResolvedPlan ResolveGraph(JArray rows, string documentKey, string revitVersion, string documentFingerprint)
        {
            var plan = new ResolvedPlan
            {
                Command = "horizun_execute_plan",
                DocumentKey = documentKey,
                RevitVersion = revitVersion,
                DocumentFingerprint = documentFingerprint
            };
            for (int i = 0; i < rows.Count; i++)
            {
                JObject row = rows[i] as JObject;
                if (row == null) continue;
                string childFp = ReadChildFingerprint(row);
                plan.Elements.Add(new PlannedElement
                {
                    UniqueId = "action:" + (row.Value<int?>("index") ?? i),
                    Category = row.Value<string>("tool"),
                    Action = PlannedAction.Modify,
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

        internal static string ReadChildFingerprint(JObject row)
        {
            try { return (string)row["data"]?["plan_resolved"]?["fingerprint"]; }
            catch { return null; }
        }

        /// <summary>Rows for actions that RAN, in order, each carrying its declared state.</summary>
        public JArray Executed { get; } = new JArray();

        /// <summary>
        /// The row of the action that stopped the plan, or null while nothing has. Kept as
        /// the ROW rather than as a key so a caller reads what happened instead of looking
        /// a name up in the trace it was already handed.
        /// </summary>
        public JObject FailedAction { get; private set; }

        /// <summary>
        /// False as soon as one rehearsal comes back as anything but a clean dry run. It
        /// never returns to true: a graph is previewed as a whole or it is not previewed.
        /// </summary>
        public bool RehearsedCleanly { get; private set; } = true;

        /// <summary>The status a plan stopped by a cancel reports (INT-7 m-e). Stable, like the code that goes with it.</summary>
        public const string CancelledStatus = PlanFailure.CancelledStatus;

        /// <summary>True once the plan stopped because a cancel was asked for its job. The stop is a cancel, not a failure of an action.</summary>
        public bool Cancelled { get; private set; }

        /// <summary>
        /// The check before an action starts (INT-7 m-e). When a cancel was asked for the job, the plan stops HERE: the action that
        /// had not started is named in FailedAction as cancelled and never enters the trace of executed actions (it did not run),
        /// and a rehearsal in progress is no longer clean. Returns true when the plan must stop.
        /// </summary>
        public bool StopBeforeAction(bool cancelRequested, int index, string key, string tool)
        {
            if (!cancelRequested) return false;
            Cancelled = true;
            RehearsedCleanly = false;
            JObject row = Row(index, key, tool, CancelledStatus, false, null,
                "cancelled: cancellation of the job was requested before this action started; it did not run");
            row["started"] = false;
            row["error_code"] = ErrorCodes.Cancelado;
            FailedAction = row;
            return true;
        }

        /// <summary>
        /// The check after the last action and before the group is kept (INT-7 m-e): a cancel that arrived while the last action ran
        /// must roll the group back instead of committing it. Every action ran (they are in the trace); the COMMIT did not happen.
        /// </summary>
        public bool StopBeforeCommit(bool cancelRequested)
        {
            if (!cancelRequested) return false;
            Cancelled = true;
            JObject row = Row(Executed.Count, null, null, CancelledStatus, false, null,
                "cancelled: cancellation of the job was requested before the group was kept; nothing was committed");
            row["started"] = false;
            row["stage"] = "before_commit";
            row["error_code"] = ErrorCodes.Cancelado;
            FailedAction = row;
            return true;
        }

        /// <summary>
        /// Actions whose own reply declared a verified application or a legitimate no-op.
        /// COUNTED, never assumed from Executed.Count - see the note on the success payload.
        /// </summary>
        public int VerifiedActions { get; private set; }

        /// <summary>Of those, the ones that were legitimately nothing to do.</summary>
        public int NoOpActions { get; private set; }

        /// <summary>
        /// The row shape every action gets, whatever happened to it.
        ///
        /// A FAILED CHILD'S STRUCTURED ANSWER TRAVELS TOO. CommandResult carries three
        /// things beside the message that a caller is supposed to branch on rather than
        /// parse: Detail (a nested rollback diagnostic), Fallback (the machine-readable
        /// permission to generate Python) and CapabilityGaps (which actions were uncovered,
        /// by index). Four tools in the plan's own allowlist return a fallback signal -
        /// annotate, create_elements, manage_views, transform_elements - and until this
        /// carried them, a plan that stopped on one handed its caller an English sentence
        /// and nothing else. The whole point of that signal is that it is not a sentence.
        ///
        /// Explicit nulls rather than absent keys, so "the child raised nothing" and "this
        /// row does not report that" stay different facts.
        /// </summary>
        public static JObject Row(int index, string key, string tool, string status,
                                  bool success, object data, string error,
                                  JObject childDetail = null, JToken fallback = null,
                                  JArray capabilityGaps = null)
        {
            return new JObject
            {
                ["index"] = index,
                ["key"] = key,
                ["tool"] = tool,
                ["status"] = status,
                ["success"] = success,
                ["data"] = success ? ToToken(data) : JValue.CreateNull(),
                ["error"] = success ? (JToken)JValue.CreateNull() : new JValue(error),
                ["child_detail"] = childDetail == null ? (JToken)JValue.CreateNull() : childDetail.DeepClone(),
                ["fallback"] = fallback == null ? (JToken)JValue.CreateNull() : fallback.DeepClone(),
                ["capability_gaps"] = capabilityGaps == null ? (JToken)JValue.CreateNull() : capabilityGaps.DeepClone()
            };
        }

        /// <summary>
        /// Record what a child declared onto its row and hand the state back. One place, so
        /// the dry run, the pre-apply recheck and the apply cannot drift into asking three
        /// different questions - and so the row a caller reads carries the verdict the plan
        /// actually acted on.
        ///
        /// `application_declared` is reported beside the state because a child that says
        /// NOTHING and a child that says "uncertain" are both refused, and they are
        /// different bugs to go and fix.
        /// </summary>
        public static ApplicationState Stamp(JObject row, object data)
        {
            ApplicationState state = ApplicationOutcome.Read(data);
            if (row != null)
            {
                row["application_state"] = ApplicationOutcome.Name(state);
                row["fully_applied"] = ApplicationOutcome.IsFullyApplied(state);
                row["application_declared"] = ApplicationOutcome.IsDeclared(data);
            }
            return state;
        }

        /// <summary>
        /// A rehearsed action. Returns the row. A rehearsal that FAILED, or that came back
        /// as anything other than a clean dry run, clears RehearsedCleanly - and that is
        /// what withholds the executable confirmation.
        /// </summary>
        public JObject RecordRehearsal(int index, string key, string tool, bool success, object data, string error,
                                       JObject childDetail = null, JToken fallback = null,
                                       JArray capabilityGaps = null)
        {
            JObject row = Row(index, key, tool, "rehearsed", success, data, error,
                              childDetail, fallback, capabilityGaps);
            if (!success) { RehearsedCleanly = false; FailedAction = row; return row; }
            if (!ApplicationOutcome.IsValidRehearsal(Stamp(row, data)))
            {
                RehearsedCleanly = false;
                if (FailedAction == null) FailedAction = row;
            }
            else if (PartialApplyNotice.Announced(data))
            {
                // The direct call can offer a partial result. The plan requires a full apply from every step.
                row["not_clean_in_plan"] = PartialApplyNotice.PlanRefusal(tool, data);
                RehearsedCleanly = false;
                if (FailedAction == null) FailedAction = row;
            }
            return row;
        }

        /// <summary>
        /// An action whose arguments only resolve after an earlier action creates something.
        /// Nothing was rehearsed, so nothing is claimed about it - it is neither a clean
        /// rehearsal nor a dirty one, and it is the apply-time check that covers it.
        /// </summary>
        public static JObject Deferred(int index, string key, string tool, string reason)
            => new JObject
            {
                ["index"] = index,
                ["key"] = key,
                ["tool"] = tool,
                ["status"] = "deferred_until_execution",
                ["reason"] = reason,
                ["application_state"] = ApplicationOutcome.Name(ApplicationState.Uncertain),
                ["fully_applied"] = false,
                ["application_declared"] = false
            };

        /// <summary>
        /// Record an action that could not be rehearsed because a reference was not yet
        /// resolvable. Unlike the historical static row, this changes the verdict: an
        /// unpreviewed action cannot contribute to an executable confirmation.
        /// </summary>
        public JObject RecordDeferred(int index, string key, string tool, string reason)
        {
            JObject row = Deferred(index, key, tool, reason);
            RehearsedCleanly = false;
            if (FailedAction == null) FailedAction = row;
            return row;
        }

        /// <summary>
        /// An action that RAN inside the confirmed group. Returns true when the plan may
        /// keep going - which is only when this action declared a full application.
        ///
        /// THE WHOLE POINT: false here must stop the graph, because the next action may be
        /// a delete and it would be deleting on top of a model that does not contain what
        /// the previous action reported. On false, FailedAction names this row and
        /// `stopped_because` says which of the eight states was read.
        /// </summary>
        public bool RecordExecuted(int index, string key, string tool, bool success, object data, string error,
                                   out ApplicationState state)
            => RecordExecuted(index, key, tool, success, data, error, null, null, null, out state);

        /// <summary>
        /// Same, carrying whatever structured answer the child returned beside its message.
        /// See Row: a fallback signal a caller must branch on cannot survive as prose.
        /// </summary>
        public bool RecordExecuted(int index, string key, string tool, bool success, object data, string error,
                                   JObject childDetail, JToken fallback, JArray capabilityGaps,
                                   out ApplicationState state)
        {
            JObject row = Row(index, key, tool, "executed", success, data, error,
                              childDetail, fallback, capabilityGaps);
            Executed.Add(row);

            if (!success)
            {
                state = ApplicationState.Failed;
                row["application_state"] = ApplicationOutcome.Name(state);
                row["fully_applied"] = false;
                row["application_declared"] = ApplicationOutcome.IsDeclared(data);
                row["stopped_because"] = "the command returned a failure";
                FailedAction = row;
                return false;
            }

            state = Stamp(row, data);
            if (!ApplicationOutcome.IsFullyApplied(state))
            {
                row["stopped_because"] = ApplicationOutcome.IsDeclared(data)
                    ? "the command answered success but declared '" + ApplicationOutcome.Name(state) + "'"
                    : "the command answered success and declared NOTHING about what it applied";
                FailedAction = row;
                return false;
            }

            VerifiedActions++;
            if (state == ApplicationState.NoOp) NoOpActions++;
            return true;
        }

        /// <summary>
        /// The sentence for an action that stopped the plan. Built here so the message and
        /// the data cannot disagree about which state was read.
        /// </summary>
        public static string StopMessage(string key, string tool, bool declared, ApplicationState state)
        {
            return "action '" + key + "' (" + tool + ") returned success but its application state is '" +
                   ApplicationOutcome.Name(state) + "', not a verified application" +
                   (declared
                       ? ". Read that action's own reply for which rows did not land."
                       : " - the command reported NOTHING about what it applied, which this plan treats exactly " +
                         "as seriously as a reported rollback.") +
                   " The whole group is rolled back rather than built on.";
        }

        /// <summary>
        /// The reply of a plan that finished. actions_verified is COUNTED from what each
        /// action declared, never from Executed.Count.
        ///
        /// The loop above already refuses to finish with an action that is not fully
        /// applied, so today the two agree - and that is exactly why this must not BE
        /// Executed.Count. If the refusal is ever weakened, the number a caller reads as
        /// proof has to stop agreeing with it, loudly, instead of going on reporting a full
        /// verification because a row exists.
        /// </summary>
        public JObject SuccessPayloadFromResults(string groupName, System.Collections.Generic.IDictionary<string, JToken> results)
        {
            // JObject(object) cannot consume a dictionary: it enumerates KeyValuePair
            // values and throws after the Revit group already committed.
            var mapped = new JObject();
            if (results != null)
                foreach (var entry in results) mapped[entry.Key] = entry.Value?.DeepClone() ?? JValue.CreateNull();
            return SuccessPayload(groupName, mapped);
        }

        public JObject SuccessPayload(string groupName, JObject results)
        {
            var payload = new JObject
            {
                ["transaction_status"] = ApplicationOutcome.Committed,
                ["transaction_name"] = groupName,
                ["actions_verified"] = VerifiedActions,
                ["actions_no_op"] = NoOpActions,
                ["actions_executed"] = Executed.Count,
                ["actions_verified_means"] =
                    "actions whose own reply declared a verified application (or a legitimate no-op), counted from " +
                    "that declaration. It is NOT the number of actions that ran: an action answering success over a " +
                    "rollback, a partial write or an unmeasured result rolls this whole group back and never " +
                    "reaches this reply.",
                ["actions"] = Executed,
                ["results"] = results ?? new JObject()
            };

            // THE PLAN'S OWN application BLOCK, so a caller (or an outer composite) can
            // read this reply exactly like any typed tool's, without re-deriving it from
            // actions_verified/actions_executed. This is NOT a second opinion: the loop
            // in ExecutePlanCommand already refuses to reach this payload with a row in
            // Executed that was not fully applied (RecordExecuted throws first), so
            // re-classifying those same rows here through CompositeVerdict always agrees
            // with VerifiedActions/NoOpActions above - it exists so the SHAPE is the
            // standard one, not so the arithmetic can differ.
            ApplicationOutcome.Stamp(payload, CompositeVerdict.Aggregate(ApplicationOutcome.Committed, ExecutedChildren()));
            return payload;
        }

        /// <summary>
        /// THE GROUP COMMITTED AND THE REPLY COULD NOT BE BUILT (FIELD FEEDBACK E1).
        ///
        /// Measured in the field on v0.6.0: horizun_execute_plan answered an
        /// ArgumentException (KeyValuePair) while the model carried every change - a
        /// committed group reported as an error. A caller that believes the error retries,
        /// and the retry writes the same changes a second time. The rule since: once the
        /// group has committed, the answer is success, never an error. When the full reply
        /// cannot be assembled this is what is returned instead - status ok,
        /// transaction_status committed, outcome committed_with_report_error, the failure
        /// that broke the report by type and message, and every part of the re-read that
        /// COULD be carried (each executed action's own verified reply, the results), each
        /// guarded so one bad part cannot take the rest with it.
        /// </summary>
        public JObject CommittedWithReportError(string groupName, JObject results, Exception reportError)
        {
            var payload = new JObject
            {
                ["status"] = "ok",
                ["outcome"] = "committed_with_report_error",
                ["transaction_status"] = ApplicationOutcome.Committed,
                ["transaction_name"] = groupName,
                ["actions_verified"] = VerifiedActions,
                ["actions_no_op"] = NoOpActions,
                ["actions_executed"] = Executed.Count,
                ["report_error"] = new JObject
                {
                    ["type"] = reportError?.GetType().FullName,
                    ["message"] = reportError?.Message,
                    ["means"] = "The atomic group COMMITTED: every action declared a verified application before it " +
                                "was kept. Only assembling this reply failed. Do NOT retry the plan - its changes are " +
                                "in the model. Re-read with the query tools if a field below is missing."
                }
            };
            try { payload["actions"] = Executed.DeepClone(); }
            catch (Exception ex) { payload["actions"] = JValue.CreateNull(); payload["actions_error"] = ex.Message; }
            try { payload["results"] = results == null ? new JObject() : results.DeepClone(); }
            catch (Exception ex) { payload["results"] = JValue.CreateNull(); payload["results_error"] = ex.Message; }
            try { ApplicationOutcome.Stamp(payload, CompositeVerdict.Aggregate(ApplicationOutcome.Committed, ExecutedChildren())); }
            catch (Exception ex) { payload["application_error"] = ex.Message; }
            return payload;
        }

        private IEnumerable<CompositeChild> ExecutedChildren()
        {
            foreach (JToken token in Executed)
            {
                var row = token as JObject;
                if (row == null) continue;
                yield return CompositeChild.Of(row.Value<bool?>("success") == true, row["data"]);
            }
        }

        private static JToken ToToken(object data)
        {
            if (data == null) return JValue.CreateNull();
            var token = data as JToken;
            if (token != null) return token.DeepClone();
            try { return JToken.FromObject(data); }
            catch { return JValue.CreateNull(); }
        }
    }
}
