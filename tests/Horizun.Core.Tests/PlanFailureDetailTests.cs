// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// What reaches the caller of execute_plan when a plan stops. Two gaps the audit
// found, both in the SYNCHRONOUS reply:
//
//   1. PlanLedger.Row kept only `error` for a failed child. Four tools in the
//      plan's own allowlist - annotate, create_elements, manage_views,
//      transform_elements - can fail carrying a machine-readable fallback signal,
//      and AGENTS.md is explicit that a caller must branch on that block and never
//      on the wording of an error. It was arriving as prose only.
//
//   2. The catch that builds the diagnostic called group.GetStatus() and
//      Guard.RollBack() unguarded. A throw from either took the whole structured
//      answer with it - no execution_trace, no failed_action, no
//      rollback_confirmed - at the exact moment the model's state is least certain.
// -----------------------------------------------------------------------------
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class PlanFailureDetailTests
    {
        private static JObject Verified()
        {
            var payload = new JObject();
            ApplicationOutcome.StampApplied(payload, "Committed", 2, 2, 2, 0, 0, 0);
            return payload;
        }

        // ---- The child's structured answer survives -----------------------------

        [Fact]
        public void A_failed_child_s_fallback_signal_reaches_the_trace_as_data()
        {
            var ledger = new PlanLedger();
            var fallback = new JObject
            {
                ["recommended_tool"] = "horizun_execute_python",
                ["allowed"] = true,
                ["reason"] = "unsupported_kind",
                ["write_started"] = false
            };
            var gaps = new JArray(new JObject { ["index"] = 0, ["reason"] = "unsupported_kind" });

            ApplicationState state;
            ledger.RecordExecuted(0, "tags", "horizun_annotate", false, null,
                                  "no typed capability covers this", null, fallback, gaps, out state);

            JObject row = ledger.FailedAction;
            Assert.True(row["fallback"].Value<bool>("allowed"));
            Assert.Equal("horizun_execute_python", row["fallback"].Value<string>("recommended_tool"));
            Assert.False(row["fallback"].Value<bool>("write_started"));
            Assert.Single((JArray)row["capability_gaps"]);
        }

        [Fact]
        public void A_failed_child_s_own_diagnostic_reaches_the_trace()
        {
            var ledger = new PlanLedger();
            JObject childDiagnostic = PlanFailure.Diagnostic(true, "RolledBack", true, "RolledBack",
                                                             new JArray(), "the child's own rollback", null);

            ApplicationState state;
            ledger.RecordExecuted(0, "codes", "horizun_write_params_verified", false, null,
                                  "write batch failed", childDiagnostic, null, null, out state);

            Assert.True(ledger.FailedAction["child_detail"].Value<bool>("rollback_confirmed"));
            Assert.Equal("RolledBack", ledger.FailedAction["child_detail"].Value<string>("transaction_group_status"));
        }

        [Fact]
        public void A_child_that_carried_nothing_reports_explicit_nulls_not_absent_keys()
        {
            // "The child raised nothing" and "this row does not report that" are different
            // facts, and a reader must be able to tell them apart.
            var ledger = new PlanLedger();
            ApplicationState state;
            ledger.RecordExecuted(0, "a", "horizun_set_keynote", false, null, "boom", out state);

            JObject row = ledger.FailedAction;
            Assert.Equal(JTokenType.Null, row["child_detail"].Type);
            Assert.Equal(JTokenType.Null, row["fallback"].Type);
            Assert.Equal(JTokenType.Null, row["capability_gaps"].Type);
        }

        [Fact]
        public void The_structured_answer_is_cloned_so_the_trace_cannot_change_under_the_caller()
        {
            var fallback = new JObject { ["allowed"] = true };
            var ledger = new PlanLedger();
            ApplicationState state;
            ledger.RecordExecuted(0, "a", "horizun_annotate", false, null, "boom", null, fallback, null, out state);

            fallback["allowed"] = false;

            Assert.True(ledger.FailedAction["fallback"].Value<bool>("allowed"));
        }

        [Fact]
        public void A_rehearsal_carries_the_same_structured_answer()
        {
            // The dry run needs it too: a fallback grant arrives on an ordinary rehearsal,
            // and a plan whose rehearsal was refused for a capability gap must be able to
            // say which action and why.
            var ledger = new PlanLedger();
            var gaps = new JArray(new JObject { ["index"] = 2 });

            ledger.RecordRehearsal(2, "beams", "horizun_create_elements", false, null,
                                   "kind not supported", null, null, gaps);

            Assert.False(ledger.RehearsedCleanly);
            Assert.Single((JArray)ledger.FailedAction["capability_gaps"]);
        }

        // ---- The diagnostic itself ---------------------------------------------

        [Fact]
        public void The_diagnostic_carries_everything_a_caller_branches_on()
        {
            var ledger = new PlanLedger();
            ApplicationState state;
            ledger.RecordExecuted(0, "walls", "horizun_create_elements", true, Verified(), null, out state);

            var partial = new JObject();
            ApplicationOutcome.StampApplied(partial, "Committed", 8, 5, 5, 0, 3, 0);
            ledger.RecordExecuted(1, "codes", "horizun_write_params_verified", true, partial, null, out state);

            JObject diag = PlanFailure.Diagnostic(
                transactionGroupStarted: true, transactionGroupStatus: "RolledBack",
                rollbackAttempted: true, rollbackStatus: "RolledBack",
                executionTrace: ledger.Executed,
                error: PlanLedger.StopMessage("codes", "horizun_write_params_verified", true, ApplicationState.Partial),
                failedAction: ledger.FailedAction);

            Assert.True(diag.Value<bool>("transaction_group_started"));
            Assert.Equal("RolledBack", diag.Value<string>("transaction_group_status"));
            Assert.True(diag.Value<bool>("rollback_attempted"));
            Assert.True(diag.Value<bool>("rollback_confirmed"));
            Assert.Equal(2, ((JArray)diag["execution_trace"]).Count);
            Assert.Equal("codes", diag["failed_action"].Value<string>("key"));

            // The trace distinguishes the CHILD's own commit from the GROUP's fate: the
            // child committed, the group did not survive.
            JToken childRow = diag["execution_trace"][1];
            Assert.Equal("Committed", childRow["data"][ApplicationOutcome.Key].Value<string>("transaction_status"));
            Assert.Equal("RolledBack", diag.Value<string>("transaction_group_status"));
        }

        [Theory]
        [InlineData("Pending")]
        [InlineData("Error")]
        [InlineData("threw")]
        [InlineData("unreadable")]
        [InlineData("not_attempted")]
        public void A_rollback_that_did_not_land_is_never_reported_as_confirmed(string status)
        {
            JObject diag = PlanFailure.Diagnostic(true, status, true, status, new JArray(), "boom", null);

            Assert.False(diag.Value<bool>("rollback_confirmed"));
        }

        [Fact]
        public void A_plan_that_never_reached_an_action_says_so_with_an_explicit_null()
        {
            JObject diag = PlanFailure.Diagnostic(true, "RolledBack", true, "RolledBack",
                                                  new PlanLedger().Executed, "the group would not start", null);

            Assert.Equal(JTokenType.Null, diag["failed_action"].Type);
            Assert.Empty((JArray)diag["execution_trace"]);
        }
    }
}
