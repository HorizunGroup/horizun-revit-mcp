// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// THE ROLLBACK LIE, which is the bug this piece was extracted to fix.
//
// ExecutePlan's catch used to call group.RollBack() and return the fixed prose
// "EVERY action was rolled back" WITHOUT reading what RollBack() returned. A
// status other than RolledBack (Pending, Error) means the model is in an UNCERTAIN
// state, not a clean one, and the old message asserted clean unconditionally.
//
// So the classification and the wording are proven here, Revit-free: a confirmed
// rollback is ONLY "RolledBack", and the human sentence never promises a clean
// model it did not see. The PipeEnvelope test proves the structured diagnostic
// actually reaches the wire instead of being flattened into the error string.
// -----------------------------------------------------------------------------
using Horizun.Revit.Core;
using Horizun.Revit.Transport;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class PlanFailureTests
    {
        [Theory]
        [InlineData("RolledBack", true)]
        [InlineData("Committed", false)]
        [InlineData("Pending", false)]
        [InlineData("Error", false)]
        [InlineData("Started", false)]
        [InlineData("rolledback", false)]   // case matters - a near miss is not a rollback
        [InlineData("", false)]
        [InlineData(null, false)]
        public void Only_RolledBack_is_a_confirmed_rollback(string status, bool expected)
            => Assert.Equal(expected, PlanFailure.IsConfirmedRollback(status));

        private static JArray Trace() => new JArray
        {
            new JObject { ["index"] = 0, ["key"] = "w",    ["tool"] = "horizun_create_elements",   ["success"] = true,  ["error"] = null },
            new JObject { ["index"] = 1, ["key"] = "boom", ["tool"] = "horizun_transform_elements", ["success"] = false, ["error"] = "cannot succeed" }
        };

        [Fact]
        public void A_rolled_back_group_is_confirmed_and_the_message_says_so()
        {
            JObject d = PlanFailure.Diagnostic(
                transactionGroupStarted: true, transactionGroupStatus: "RolledBack",
                rollbackAttempted: true, rollbackStatus: "RolledBack",
                executionTrace: Trace(), error: "action 'boom' failed");

            Assert.True((bool)d["transaction_group_started"]);
            Assert.True((bool)d["rollback_confirmed"]);
            Assert.Equal("RolledBack", (string)d["rollback_status"]);
            Assert.Equal(2, ((JArray)d["execution_trace"]).Count);
            // The trace names each reached action, its success and its error.
            Assert.True((bool)d["execution_trace"][0]["success"]);
            Assert.False((bool)d["execution_trace"][1]["success"]);

            string msg = PlanFailure.Message(d);
            Assert.Contains("rolled back", msg);
            Assert.DoesNotContain("UNCERTAIN", msg);
        }

        [Fact]
        public void A_rollback_that_returned_Error_is_UNCERTAIN_never_confirmed()
        {
            // We attempted the rollback; Revit returned Error, and the group's final status
            // is Error too. The model is NOT provably clean and the diagnostic must not claim it.
            JObject d = PlanFailure.Diagnostic(
                transactionGroupStarted: true, transactionGroupStatus: "Error",
                rollbackAttempted: true, rollbackStatus: "Error",
                executionTrace: Trace(), error: "action 'boom' failed");

            Assert.False((bool)d["rollback_confirmed"]);
            string msg = PlanFailure.Message(d);
            Assert.Contains("UNCERTAIN", msg);
            Assert.Contains("re-read", msg);
            Assert.DoesNotContain("nothing was retained", msg);
        }

        [Fact]
        public void Confirmation_is_from_the_final_status_not_from_having_attempted()
        {
            // Assimilate found a silent rollback: we did NOT call RollBack again, yet the
            // group's final status is RolledBack, so the model IS clean and the diagnostic
            // may say so. Confirmation tracks the state, not our call.
            JObject d = PlanFailure.Diagnostic(
                transactionGroupStarted: true, transactionGroupStatus: "RolledBack",
                rollbackAttempted: false, rollbackStatus: PlanFailure.NotAttempted,
                executionTrace: Trace(), error: "silent rollback on assimilate");

            Assert.True((bool)d["rollback_confirmed"]);
            Assert.False((bool)d["rollback_attempted"]);
        }

        [Fact]
        public void A_failure_before_the_group_is_not_a_rollback()
        {
            JObject d = PlanFailure.Diagnostic(
                transactionGroupStarted: false, transactionGroupStatus: "not_started",
                rollbackAttempted: false, rollbackStatus: PlanFailure.NotAttempted,
                executionTrace: new JArray(), error: "refused during rehearsal");

            Assert.False((bool)d["rollback_confirmed"]);
            string msg = PlanFailure.Message(d);
            Assert.Contains("before the TransactionGroup began", msg);
            Assert.Contains("nothing was rolled back", msg);
        }

        [Fact]
        public void The_diagnostic_reaches_the_wire_as_structure_not_prose()
        {
            JObject d = PlanFailure.Diagnostic(
                transactionGroupStarted: true, transactionGroupStatus: "RolledBack",
                rollbackAttempted: true, rollbackStatus: "RolledBack",
                executionTrace: Trace(), error: "boom");

            CommandResult failed = CommandResult.FailWithDetail(PlanFailure.Message(d), d);
            JObject reply = PipeEnvelope.Of("id-1", failed);

            Assert.False((bool)reply["success"]);
            Assert.NotNull(reply["detail"]);
            Assert.True((bool)reply["detail"]["transaction_group_started"]);
            Assert.Equal("RolledBack", (string)reply["detail"]["rollback_status"]);
            Assert.True((bool)reply["detail"]["rollback_confirmed"]);
            Assert.Equal(2, ((JArray)reply["detail"]["execution_trace"]).Count);
        }

        [Fact]
        public void An_ordinary_failure_carries_no_detail()
        {
            JObject reply = PipeEnvelope.Of("id-2", CommandResult.Fail("just a plain error"));
            Assert.False((bool)reply["success"]);
            Assert.Null(reply["detail"]);
        }
    }
}
