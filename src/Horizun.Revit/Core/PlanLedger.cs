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
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public sealed class PlanLedger
    {
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
        /// `stopped_because` says which of the seven states was read.
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
        public JObject SuccessPayload(string groupName, JObject results)
        {
            return new JObject
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
