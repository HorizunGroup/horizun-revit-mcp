// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// The plan-level half of the same defect: what execute_plan does with each child's
// answer. ExecutePlanCommand needs a UIApplication, a Document and a
// TransactionGroup, so none of the scenarios below could be reached by a test
// while the decision lived inside it - which is why the decision now lives in
// PlanLedger and the command holds one.
//
// The scenario that motivated the whole change is Partial_child_stops_the_plan_
// before_the_delete_runs: a write that half-landed, followed by a delete that
// would have run over a model nobody had verified.
// -----------------------------------------------------------------------------
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class PlanLedgerTests
    {
        private static JObject Apply(string txStatus, int requested, int applied, int verified,
                                     int unresolved = 0, int failed = 0, int unknown = 0)
        {
            var payload = new JObject { ["transaction_status"] = txStatus };
            ApplicationOutcome.StampApplied(payload, txStatus, requested, applied, verified,
                                            unresolved, failed, unknown);
            return payload;
        }

        private static JObject Verified(int n = 2) => Apply("Committed", n, n, n);
        private static JObject Rehearsed(int requested, int unresolved = 0)
        {
            var payload = new JObject();
            ApplicationOutcome.StampRehearsal(payload, requested, unresolved, 0, 0);
            return payload;
        }

        /// <summary>Record one executed action the way the command's loop does.</summary>
        private static bool Run(PlanLedger ledger, int index, string key, string tool, JObject data)
        {
            ApplicationState state;
            return ledger.RecordExecuted(index, key, tool, true, data, null, out state);
        }

        // ---- Apply: what may be built on ----------------------------------------

        [Fact]
        public void Every_action_fully_verified_keeps_the_group_and_counts_them_all()
        {
            var ledger = new PlanLedger();

            Assert.True(Run(ledger, 0, "params", "horizun_write_params_verified", Verified(4)));
            Assert.True(Run(ledger, 1, "keynote", "horizun_set_keynote", Verified(2)));
            Assert.True(Run(ledger, 2, "purge", "horizun_delete_verified", Verified(7)));

            Assert.Null(ledger.FailedAction);
            Assert.Equal(3, ledger.VerifiedActions);

            JObject payload = ledger.SuccessPayload("Horizun: atomic plan", new JObject());
            Assert.Equal(3, payload.Value<int>("actions_verified"));
            Assert.Equal(3, payload.Value<int>("actions_executed"));
            Assert.Equal("Committed", payload.Value<string>("transaction_status"));
        }

        [Fact]
        public void A_child_that_answers_success_over_a_rolled_back_transaction_stops_the_plan()
        {
            var ledger = new PlanLedger();

            ApplicationState state;
            bool mayContinue = ledger.RecordExecuted(0, "params", "horizun_write_params_verified",
                                                     true, Apply("RolledBack", 5, 0, 0), null, out state);

            Assert.False(mayContinue);
            Assert.Equal(ApplicationState.RolledBack, state);
            Assert.Equal(0, ledger.VerifiedActions);
            Assert.NotNull(ledger.FailedAction);
            Assert.Equal("params", ledger.FailedAction.Value<string>("key"));
            // The row keeps success:true, because that IS what the child returned. The plan
            // stopped on the declaration beside it, and both facts stay readable.
            Assert.True(ledger.FailedAction.Value<bool>("success"));
            Assert.False(ledger.FailedAction.Value<bool>("fully_applied"));
        }

        [Fact]
        public void Every_row_unresolved_stops_the_plan()
        {
            var ledger = new PlanLedger();

            Assert.False(Run(ledger, 0, "params", "horizun_write_params_verified",
                             Apply(ApplicationOutcome.NotStarted, 4, 0, 0, unresolved: 4)));
            Assert.Equal(0, ledger.VerifiedActions);
        }

        [Fact]
        public void Failed_rows_stop_the_plan()
        {
            var ledger = new PlanLedger();

            Assert.False(Run(ledger, 0, "params", "horizun_write_params_verified",
                             Apply("Committed", 10, 8, 8, failed: 2)));
            Assert.Equal("partial", ledger.FailedAction.Value<string>("application_state"));
        }

        [Fact]
        public void Unknown_rows_stop_the_plan()
        {
            var ledger = new PlanLedger();

            Assert.False(Run(ledger, 0, "params", "horizun_write_params_verified",
                             Apply("Committed", 10, 9, 9, unknown: 1)));
            Assert.Equal("uncertain", ledger.FailedAction.Value<string>("application_state"));
        }

        [Fact]
        public void An_undeclared_child_stops_the_plan_and_the_row_says_it_declared_nothing()
        {
            var ledger = new PlanLedger();
            var undeclared = new JObject { ["transaction_status"] = "Committed", ["writes_confirmed"] = 9 };

            Assert.False(Run(ledger, 0, "mystery", "horizun_write_params_verified", undeclared));
            Assert.False(ledger.FailedAction.Value<bool>("application_declared"));
            Assert.Contains("declared NOTHING", ledger.FailedAction.Value<string>("stopped_because"));
        }

        // ---- The scenario the whole change is for -------------------------------

        [Fact]
        public void Partial_child_stops_the_plan_before_the_delete_runs()
        {
            // A best_effort write that kept the rows that landed, followed by a delete.
            // Before this change: the write answered Success, the plan continued, the
            // delete ran against a model nobody had verified, and the group assimilated.
            var ledger = new PlanLedger();

            bool mayContinue = Run(ledger, 0, "codes", "horizun_write_params_verified",
                                   Apply("Committed", 40, 31, 31, failed: 9));

            Assert.False(mayContinue);

            // The caller MUST stop here. What the ledger guarantees is that nothing
            // afterwards can be mistaken for verified work: the delete was never recorded,
            // the trace holds one row, and the verified count is zero.
            Assert.Single(ledger.Executed);
            Assert.Equal(0, ledger.VerifiedActions);
            Assert.Equal("codes", ledger.FailedAction.Value<string>("key"));
            Assert.Equal("horizun_write_params_verified", ledger.FailedAction.Value<string>("tool"));
        }

        [Fact]
        public void A_first_child_failure_stops_the_plan_with_nothing_verified()
        {
            var ledger = new PlanLedger();

            ApplicationState state;
            bool mayContinue = ledger.RecordExecuted(0, "first", "horizun_create_elements",
                                                     false, null, "Revit refused the type", out state);

            Assert.False(mayContinue);
            Assert.Equal(0, ledger.VerifiedActions);
            Assert.Single(ledger.Executed);
            Assert.False(ledger.FailedAction.Value<bool>("success"));
            Assert.Equal("Revit refused the type", ledger.FailedAction.Value<string>("error"));
            Assert.Equal("the command returned a failure", ledger.FailedAction.Value<string>("stopped_because"));
        }

        [Fact]
        public void A_middle_child_failure_after_a_verified_one_stops_the_plan_and_names_the_middle_action()
        {
            var ledger = new PlanLedger();

            Assert.True(Run(ledger, 0, "walls", "horizun_create_elements", Verified(12)));

            ApplicationState state;
            Assert.False(ledger.RecordExecuted(1, "tags", "horizun_annotate",
                                               false, null, "no tag type for that category", out state));

            // The earlier action really did land - that is not in dispute and the count
            // says so. It is the GROUP that cannot be kept, and the caller rolls it back.
            Assert.Equal(1, ledger.VerifiedActions);
            Assert.Equal(2, ledger.Executed.Count);
            Assert.Equal("tags", ledger.FailedAction.Value<string>("key"));
            Assert.Equal(1, ledger.FailedAction.Value<int>("index"));
        }

        // ---- actions_verified ----------------------------------------------------

        [Fact]
        public void Actions_verified_counts_neither_rehearsals_nor_partials_nor_rollbacks_nor_uncertain()
        {
            // Each of these is recorded on its own ledger, because a real plan stops at the
            // first one. The property under test is that not one of them is ever counted.
            JObject[] notApplications =
            {
                Apply("RolledBack", 3, 0, 0),                       // reverted
                Apply("Committed", 10, 6, 6, failed: 4),            // partial
                Apply("Committed", 10, 9, 9, unknown: 1),           // uncertain
                Apply("Committed", 3, 0, 0),                        // zero changes
                Apply("Pending", 3, 3, 3),                          // not committed
                Rehearsed(5),                                       // a dry run
                new JObject { ["transaction_status"] = "Committed" } // declared nothing
            };

            foreach (JObject data in notApplications)
            {
                var ledger = new PlanLedger();
                Assert.False(Run(ledger, 0, "a", "horizun_write_params_verified", data));
                Assert.Equal(0, ledger.VerifiedActions);
                Assert.Equal(0, ledger.SuccessPayload("g", new JObject()).Value<int>("actions_verified"));
            }
        }

        [Fact]
        public void Actions_verified_is_not_the_number_of_rows_in_the_trace()
        {
            // The regression this pins: actions_verified used to BE executed.Count. Here the
            // trace holds two rows and exactly one of them is a verified application, so the
            // two numbers must disagree.
            var ledger = new PlanLedger();
            Run(ledger, 0, "ok", "horizun_set_keynote", Verified(3));
            Run(ledger, 1, "half", "horizun_write_params_verified", Apply("Committed", 8, 5, 5, failed: 3));

            JObject payload = ledger.SuccessPayload("g", new JObject());

            Assert.Equal(2, payload.Value<int>("actions_executed"));
            Assert.Equal(1, payload.Value<int>("actions_verified"));
            Assert.NotEqual(payload.Value<int>("actions_executed"), payload.Value<int>("actions_verified"));
        }

        [Fact]
        public void A_legitimate_no_op_counts_as_verified_and_is_reported_separately()
        {
            var ledger = new PlanLedger();

            Assert.True(Run(ledger, 0, "already", "horizun_family_apply",
                            Apply(ApplicationOutcome.NotStarted, 0, 0, 0)));

            JObject payload = ledger.SuccessPayload("g", new JObject());
            Assert.Equal(1, payload.Value<int>("actions_verified"));
            Assert.Equal(1, payload.Value<int>("actions_no_op"));
        }

        // ---- Rehearsals and the executable confirmation --------------------------

        [Fact]
        public void A_graph_whose_actions_all_rehearse_cleanly_stays_clean()
        {
            var ledger = new PlanLedger();

            ledger.RecordRehearsal(0, "a", "horizun_write_params_verified", true, Rehearsed(5), null);
            ledger.RecordRehearsal(1, "b", "horizun_set_keynote", true, Rehearsed(2), null);

            Assert.True(ledger.RehearsedCleanly);
        }

        [Fact]
        public void A_partially_resolvable_rehearsal_withholds_the_executable_confirmation()
        {
            var ledger = new PlanLedger();

            ledger.RecordRehearsal(0, "a", "horizun_write_params_verified", true, Rehearsed(5), null);
            // Ok, and three of ten rows did not resolve. A token over this authorises an
            // apply nobody previewed.
            ledger.RecordRehearsal(1, "b", "horizun_write_params_verified", true, Rehearsed(10, unresolved: 3), null);

            Assert.False(ledger.RehearsedCleanly);
        }

        [Fact]
        public void A_rehearsal_verdict_never_returns_to_clean_once_it_is_dirty()
        {
            var ledger = new PlanLedger();

            ledger.RecordRehearsal(0, "bad", "horizun_write_params_verified", true, Rehearsed(4, unresolved: 4), null);
            ledger.RecordRehearsal(1, "good", "horizun_set_keynote", true, Rehearsed(1), null);

            Assert.False(ledger.RehearsedCleanly);
        }

        [Fact]
        public void An_applied_child_answering_a_rehearsal_slot_is_not_a_clean_rehearsal()
        {
            // The inverse mistake: a child that WROTE during what should have been a dry run.
            var ledger = new PlanLedger();

            ledger.RecordRehearsal(0, "a", "horizun_write_params_verified", true, Verified(3), null);

            Assert.False(ledger.RehearsedCleanly);
        }

        [Fact]
        public void A_failed_rehearsal_is_not_clean_and_names_the_action()
        {
            var ledger = new PlanLedger();

            ledger.RecordRehearsal(0, "a", "horizun_delete_verified", false, null, "id 12 does not resolve");

            Assert.False(ledger.RehearsedCleanly);
            Assert.Equal("a", ledger.FailedAction.Value<string>("key"));
        }

        [Fact]
        public void A_deferred_action_claims_nothing_about_itself()
        {
            // Its arguments only exist after an earlier action creates something, so there
            // is no rehearsal to report. It must not read as verified, and the apply-time
            // check is what covers it.
            JObject row = PlanLedger.Deferred(2, "tags", "horizun_annotate", "${walls.rows.0.element_id} is not known yet");

            Assert.Equal("deferred_until_execution", row.Value<string>("status"));
            Assert.False(row.Value<bool>("fully_applied"));
            Assert.False(row.Value<bool>("application_declared"));
            Assert.Equal("uncertain", row.Value<string>("application_state"));
        }

        // ---- The diagnostic ------------------------------------------------------

        [Fact]
        public void The_rollback_diagnostic_keeps_its_whole_structure()
        {
            var ledger = new PlanLedger();
            Run(ledger, 0, "walls", "horizun_create_elements", Verified(12));
            Run(ledger, 1, "codes", "horizun_write_params_verified", Apply("Committed", 8, 5, 5, failed: 3));

            JObject diag = PlanFailure.Diagnostic(
                transactionGroupStarted: true,
                transactionGroupStatus: "RolledBack",
                rollbackAttempted: true,
                rollbackStatus: "RolledBack",
                executionTrace: ledger.Executed,
                error: PlanLedger.StopMessage("codes", "horizun_write_params_verified", true, ApplicationState.Partial),
                failedAction: ledger.FailedAction);

            Assert.True(diag.Value<bool>("transaction_group_started"));
            Assert.Equal("RolledBack", diag.Value<string>("transaction_group_status"));
            Assert.True(diag.Value<bool>("rollback_attempted"));
            Assert.Equal("RolledBack", diag.Value<string>("rollback_status"));
            Assert.True(diag.Value<bool>("rollback_confirmed"));
            Assert.Equal(2, ((JArray)diag["execution_trace"]).Count);
            Assert.Equal("codes", diag["failed_action"].Value<string>("key"));
            Assert.Contains("partial", diag.Value<string>("error"));
        }

        [Fact]
        public void A_rollback_that_did_not_land_is_not_reported_as_confirmed()
        {
            JObject diag = PlanFailure.Diagnostic(true, "Pending", true, "Error",
                                                  new PlanLedger().Executed, "boom", null);

            Assert.False(diag.Value<bool>("rollback_confirmed"));
            // An explicit null, so "no action was reached" stays distinguishable from
            // "an action failed and we did not say which".
            Assert.Equal(JTokenType.Null, diag["failed_action"].Type);
        }
    }
}
