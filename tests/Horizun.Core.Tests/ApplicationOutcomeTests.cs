// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// The defect these exist for: horizun_execute_plan read CommandResult.Success and
// treated it as "this action was completely applied and verified". It is not, and
// four children in this tree return Success=true over a model they did not change.
// A confirmed plan could therefore roll one action back, keep going, run a DELETE
// behind it, assimilate the group, and answer actions_verified = executed.Count.
//
// Every declaration below is built by the PRODUCTION helpers - StampApplied and
// StampRehearsal - rather than by hand-written JSON, so a test cannot pass over a
// payload no command would ever emit, and a change to the classifier moves the
// tests with it instead of leaving them agreeing with a copy of the old rule.
// -----------------------------------------------------------------------------
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class ApplicationOutcomeTests
    {
        /// <summary>An apply reply shaped exactly as a command stamps one.</summary>
        private static JObject Apply(string txStatus, int requested, int applied, int verified,
                                     int unresolved = 0, int failed = 0, int unknown = 0)
        {
            var payload = new JObject { ["transaction_status"] = txStatus };
            ApplicationOutcome.StampApplied(payload, txStatus, requested, applied, verified,
                                            unresolved, failed, unknown);
            return payload;
        }

        private static JObject Rehearsal(int requested, int unresolved = 0, int failed = 0, int unknown = 0)
        {
            var payload = new JObject { ["transaction_status"] = ApplicationOutcome.NotStarted };
            ApplicationOutcome.StampRehearsal(payload, requested, unresolved, failed, unknown);
            return payload;
        }

        // ---- The three things that must stay apart -------------------------------

        [Fact]
        public void A_committed_batch_with_every_row_verified_is_the_only_plain_full_application()
        {
            Assert.Equal(ApplicationState.VerifiedApplied, ApplicationOutcome.Read(Apply("Committed", 3, 3, 3)));
            Assert.True(ApplicationOutcome.IsFullyApplied(ApplicationState.VerifiedApplied));
        }

        [Fact]
        public void Success_over_a_rolled_back_transaction_is_not_an_application()
        {
            // write_params on_failure='atomic' with a failing row, and its
            // SilentRollbackException path, both answer Ok with this shape.
            JObject payload = Apply("RolledBack", requested: 5, applied: 0, verified: 0);

            Assert.Equal(ApplicationState.RolledBack, ApplicationOutcome.Read(payload));
            Assert.False(ApplicationOutcome.IsFullyApplied(ApplicationState.RolledBack));
            Assert.False(payload[ApplicationOutcome.Key].Value<bool>("fully_applied"));
        }

        [Theory]
        // Revit's TransactionStatus has more members than Committed and RolledBack, and
        // not one of the rest is a commit. Anything unrecognised keeps its uncertainty.
        [InlineData("Pending")]
        [InlineData("Error")]
        [InlineData("Started")]
        [InlineData("Uninitialized")]
        [InlineData("committed")]      // wrong case is not the constant
        [InlineData("Commited")]       // and neither is a typo
        public void A_transaction_that_did_not_commit_is_never_a_full_application(string status)
        {
            ApplicationState state = ApplicationOutcome.Read(Apply(status, 2, 2, 2));

            Assert.Equal(ApplicationState.Uncertain, state);
            Assert.False(ApplicationOutcome.IsFullyApplied(state));
        }

        [Fact]
        public void Write_failure_detail_distinguishes_rollback_pending_and_partial_postcondition()
        {
            JObject rolled = ApplicationOutcome.FailureAfterWrite(
                "schedule_id", 42, "transaction_commit", "RolledBack",
                ApplicationState.RolledBack, objectReread: false);
            JObject pending = ApplicationOutcome.FailureAfterWrite(
                "schedule_id", 42, "transaction_commit", "Pending",
                ApplicationState.Uncertain, objectReread: false);
            JObject mismatch = ApplicationOutcome.FailureAfterWrite(
                "schedule_id", 42, "postcondition", "Committed",
                ApplicationState.Partial, objectReread: true,
                new JObject { ["all_measured"] = true, ["all_verified"] = false });

            Assert.True((bool)rolled["write_started"]);
            Assert.Equal("rolled_back", (string)rolled[ApplicationOutcome.Key]["state"]);
            Assert.Equal("Pending", (string)pending["transaction_status"]);
            Assert.Equal("uncertain", (string)pending[ApplicationOutcome.Key]["state"]);
            Assert.Equal(42, (long)mismatch["schedule_id"]);
            Assert.Equal("partial", (string)mismatch[ApplicationOutcome.Key]["state"]);
            Assert.False((bool)mismatch["evidence"]["all_verified"]);
        }

        [Fact]
        public void A_missing_transaction_status_is_uncertain_not_optimistic()
        {
            Assert.Equal(ApplicationState.Uncertain, ApplicationOutcome.Read(Apply(null, 1, 1, 1)));
            Assert.Equal(ApplicationState.Uncertain, ApplicationOutcome.Read(Apply("", 1, 1, 1)));
        }

        // ---- The four write_params shapes the brief names ------------------------

        [Fact]
        public void Every_row_unresolved_is_a_failure_not_a_quiet_success()
        {
            // "No transaction was opened: not one of the N rows resolved." Ok, today.
            JObject payload = Apply(ApplicationOutcome.NotStarted, requested: 4, applied: 0, verified: 0,
                                    unresolved: 4);

            Assert.Equal(ApplicationState.Failed, ApplicationOutcome.Read(payload));
            Assert.False(ApplicationOutcome.IsFullyApplied(ApplicationState.Failed));
        }

        [Fact]
        public void Some_rows_unresolved_after_a_commit_is_partial()
        {
            Assert.Equal(ApplicationState.Partial,
                         ApplicationOutcome.Read(Apply("Committed", 10, 7, 7, unresolved: 3)));
        }

        [Fact]
        public void Failed_rows_make_a_committed_batch_partial()
        {
            Assert.Equal(ApplicationState.Partial,
                         ApplicationOutcome.Read(Apply("Committed", 10, 8, 8, failed: 2)));
        }

        [Fact]
        public void One_unknown_row_makes_the_whole_batch_uncertain_not_partial()
        {
            // "We could not read it back" is not "it is there" and not "it is absent".
            // It must not be rounded into either, and it must not be softened to partial:
            // partial says the rest is known good, and with an unknown row it is not.
            JObject payload = Apply("Committed", requested: 10, applied: 9, verified: 9, unknown: 1);

            Assert.Equal(ApplicationState.Uncertain, ApplicationOutcome.Read(payload));
        }

        [Fact]
        public void Writes_the_model_did_not_confirm_are_partial_even_when_every_row_was_attempted()
        {
            // applied == requested, but the post-commit re-read confirmed fewer against the
            // caller's own value. That gap is the whole reason `verified` is a second number.
            Assert.Equal(ApplicationState.Partial,
                         ApplicationOutcome.Read(Apply("Committed", 6, 6, 4)));
        }

        [Fact]
        public void Zero_changes_when_changes_were_requested_is_failed()
        {
            Assert.Equal(ApplicationState.Failed, ApplicationOutcome.Read(Apply("Committed", 3, 0, 0)));
        }

        [Fact]
        public void Nothing_requested_is_a_legitimate_no_op_and_stays_assimilable()
        {
            // family_apply's idempotent second run: no transaction opened, nothing to do.
            JObject payload = Apply(ApplicationOutcome.NotStarted, requested: 0, applied: 0, verified: 0);

            Assert.Equal(ApplicationState.NoOp, ApplicationOutcome.Read(payload));
            Assert.True(ApplicationOutcome.IsFullyApplied(ApplicationState.NoOp));
        }

        // ---- Rehearsals ----------------------------------------------------------

        [Fact]
        public void A_dry_run_that_resolved_everything_is_a_valid_rehearsal_and_is_not_an_application()
        {
            ApplicationState state = ApplicationOutcome.Read(Rehearsal(requested: 5));

            Assert.Equal(ApplicationState.Rehearsed, state);
            Assert.True(ApplicationOutcome.IsValidRehearsal(state));
            // The one that would be catastrophic: a dry run inside a confirmed apply means
            // the write never ran, so a rehearsal must never read as applied.
            Assert.False(ApplicationOutcome.IsFullyApplied(state));
        }

        [Fact]
        public void A_dry_run_with_unresolved_rows_is_not_a_valid_rehearsal()
        {
            ApplicationState state = ApplicationOutcome.Read(Rehearsal(requested: 10, unresolved: 3));

            Assert.Equal(ApplicationState.Partial, state);
            Assert.False(ApplicationOutcome.IsValidRehearsal(state));
        }

        [Fact]
        public void A_dry_run_carrying_an_unknown_is_uncertain()
        {
            Assert.False(ApplicationOutcome.IsValidRehearsal(
                ApplicationOutcome.Read(Rehearsal(requested: 4, unknown: 1))));
        }

        // ---- Fail-closed reading -------------------------------------------------

        [Theory]
        [InlineData(null)]
        public void Null_data_is_uncertain(object data)
        {
            Assert.Equal(ApplicationState.Uncertain, ApplicationOutcome.Read(data));
            Assert.False(ApplicationOutcome.IsDeclared(data));
        }

        [Fact]
        public void A_child_that_declares_nothing_is_uncertain_and_says_it_declared_nothing()
        {
            // The fail-closed case that matters most: a command nobody wired, or a new one.
            // It must not fall into a state that lets a plan keep working on top of it, and
            // the two must stay distinguishable so the bug can be found and fixed.
            var undeclared = new JObject { ["transaction_status"] = "Committed", ["writes_confirmed"] = 12 };

            Assert.Equal(ApplicationState.Uncertain, ApplicationOutcome.Read(undeclared));
            Assert.False(ApplicationOutcome.IsDeclared(undeclared));
            Assert.False(ApplicationOutcome.IsFullyApplied(ApplicationOutcome.Read(undeclared)));
        }

        [Fact]
        public void A_declaration_that_is_not_an_object_is_uncertain()
        {
            Assert.Equal(ApplicationState.Uncertain,
                         ApplicationOutcome.Read(new JObject { [ApplicationOutcome.Key] = "verified_applied" }));
        }

        [Fact]
        public void A_state_nobody_recognises_is_uncertain_rather_than_believed()
        {
            var forged = new JObject
            {
                [ApplicationOutcome.Key] = new JObject { ["state"] = "totally_fine", ["fully_applied"] = true }
            };

            Assert.Equal(ApplicationState.Uncertain, ApplicationOutcome.Read(forged));
            ApplicationState parsed;
            Assert.False(ApplicationOutcome.TryParse("totally_fine", out parsed));
            Assert.Equal(ApplicationState.Uncertain, parsed);
        }

        [Fact]
        public void A_non_json_payload_is_uncertain_rather_than_a_throw()
        {
            Assert.Equal(ApplicationState.Uncertain, ApplicationOutcome.Read("a string"));
            Assert.Equal(ApplicationState.Uncertain, ApplicationOutcome.Read(42));
        }

        // ---- The vocabulary itself ----------------------------------------------

        [Theory]
        [InlineData(ApplicationState.VerifiedApplied, true)]
        [InlineData(ApplicationState.NoOp, true)]
        [InlineData(ApplicationState.Rehearsed, false)]
        [InlineData(ApplicationState.Partial, false)]
        [InlineData(ApplicationState.RolledBack, false)]
        [InlineData(ApplicationState.Failed, false)]
        [InlineData(ApplicationState.Uncertain, false)]
        public void Exactly_two_states_may_be_assimilated(ApplicationState state, bool expected)
        {
            Assert.Equal(expected, ApplicationOutcome.IsFullyApplied(state));
        }

        [Theory]
        [InlineData(ApplicationState.VerifiedApplied)]
        [InlineData(ApplicationState.NoOp)]
        [InlineData(ApplicationState.Rehearsed)]
        [InlineData(ApplicationState.Partial)]
        [InlineData(ApplicationState.RolledBack)]
        [InlineData(ApplicationState.Failed)]
        [InlineData(ApplicationState.Uncertain)]
        public void Every_state_round_trips_through_its_wire_name(ApplicationState state)
        {
            ApplicationState back;
            Assert.True(ApplicationOutcome.TryParse(ApplicationOutcome.Name(state), out back));
            Assert.Equal(state, back);
        }

        [Fact]
        public void The_default_of_the_enum_is_the_state_that_lets_nothing_through()
        {
            Assert.Equal(ApplicationState.Uncertain, default(ApplicationState));
            Assert.False(ApplicationOutcome.IsFullyApplied(default(ApplicationState)));
        }

        [Fact]
        public void The_declaration_carries_the_counts_the_verdict_was_taken_on()
        {
            JObject block = (JObject)Apply("Committed", 10, 8, 8, failed: 2)[ApplicationOutcome.Key];

            Assert.Equal("partial", block.Value<string>("state"));
            Assert.False(block.Value<bool>("fully_applied"));
            Assert.Equal(10, block.Value<int>("requested"));
            Assert.Equal(8, block.Value<int>("applied"));
            Assert.Equal(2, block.Value<int>("failed"));
            Assert.Equal("Committed", block.Value<string>("transaction_status"));
            Assert.False(string.IsNullOrWhiteSpace(block.Value<string>("state_means")));
        }
    }
}
