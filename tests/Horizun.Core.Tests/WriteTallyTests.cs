// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// Two arithmetic defects review found in the first pass of the application
// contract. Both produced well-formed counts that described something other than
// the model, so ApplicationOutcome classified them faithfully and was still wrong:
//
//   1. create_schedule called tx.Commit(), threw the returned TransactionStatus
//      away, and stamped the literal "Committed". Revit answers RolledBack or
//      Pending WITHOUT throwing.
//   2. set_keynote derived `requested` as targets + failed.Count, where `failed`
//      is one array fed by three different places - so a target whose write was
//      refused was counted twice and reported as an unresolved id.
//
// Neither is reachable from a test against a live Revit: a Commit that returns
// Pending and a batch with one refused Set plus one failed read-back are both
// states you cannot ask a real Revit to produce. Hence the rules live here.
// -----------------------------------------------------------------------------
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class WriteTallyTests
    {
        private static ApplicationState State(JObject declaration)
        {
            var payload = new JObject();
            ApplicationOutcome.Stamp(payload, declaration);
            return ApplicationOutcome.Read(payload);
        }

        // ---- One object: the status is never assumed --------------------------

        [Fact]
        public void A_confirmed_commit_with_the_postcondition_held_is_the_only_application()
        {
            Assert.Equal(ApplicationState.VerifiedApplied,
                         State(WriteTally.OneObject("Committed", postconditionVerified: true)));
        }

        [Fact]
        public void A_commit_that_returned_rolled_back_is_a_rollback_even_if_something_was_read_back()
        {
            // The exact create_schedule shape: Commit() answered RolledBack without
            // throwing. Nothing measured after it may be believed, and the element id it
            // captured can be reused by Revit - so even postconditionVerified:true here
            // must not produce an application.
            Assert.Equal(ApplicationState.RolledBack,
                         State(WriteTally.OneObject("RolledBack", postconditionVerified: true)));
        }

        [Theory]
        [InlineData("Pending")]
        [InlineData("Error")]
        [InlineData("Uninitialized")]
        [InlineData("Proceed")]
        [InlineData("Started")]
        [InlineData("committed")]     // wrong case is not the constant
        [InlineData("Commited")]      // and neither is a typo
        [InlineData("")]
        [InlineData(null)]
        public void Any_status_that_is_not_a_confirmed_commit_is_uncertain(string status)
        {
            Assert.Equal(ApplicationState.Uncertain, State(WriteTally.OneObject(status, true)));
            Assert.Equal(ApplicationState.Uncertain, State(WriteTally.OneObject(status, false)));
        }

        [Fact]
        public void A_confirmed_commit_whose_postcondition_did_not_hold_is_a_failure_not_a_caveat()
        {
            Assert.Equal(ApplicationState.Failed,
                         State(WriteTally.OneObject("Committed", postconditionVerified: false)));
        }

        [Fact]
        public void No_status_at_all_can_reach_a_full_application()
        {
            foreach (string status in new[] { "Committed", "RolledBack", "Pending", "Error", null, "" })
            foreach (bool verified in new[] { true, false })
            {
                ApplicationState state = State(WriteTally.OneObject(status, verified));
                bool mayBeKept = ApplicationOutcome.IsFullyApplied(state);
                Assert.Equal(status == "Committed" && verified, mayBeKept);
            }
        }

        // ---- Per target: three failures, each counted once ---------------------

        [Fact]
        public void Every_target_verified_is_a_full_application()
        {
            Assert.Equal(ApplicationState.VerifiedApplied,
                         State(WriteTally.PerTarget("Committed", resolvedTargets: 4, unresolvedIds: 0,
                                                    verifiedTargets: 4, unverifiedTargets: 0)));
        }

        [Fact]
        public void An_id_that_never_resolved_is_unresolved_and_adds_to_what_was_requested()
        {
            // Three ids sent, two became targets, one did not resolve at all.
            JObject d = WriteTally.PerTarget("Committed", resolvedTargets: 2, unresolvedIds: 1,
                                             verifiedTargets: 2, unverifiedTargets: 0);

            Assert.Equal(3, d.Value<int>("requested"));
            Assert.Equal(1, d.Value<int>("unresolved"));
            Assert.Equal(0, d.Value<int>("failed"));
            Assert.Equal(ApplicationState.Partial, State(d));
        }

        [Fact]
        public void A_refused_Set_is_a_failure_and_is_not_counted_as_an_unresolved_id()
        {
            // THE DOUBLE COUNT. Two targets resolved; Revit refused the write on one, so
            // the committed model does not carry it and the post-commit read says so.
            // Before the fix this arrived as requested=3 (2 targets + 1 "failure") with
            // unresolved=1, describing a request nobody made against an id that resolved
            // perfectly well.
            JObject d = WriteTally.PerTarget("Committed", resolvedTargets: 2, unresolvedIds: 0,
                                             verifiedTargets: 1, unverifiedTargets: 1);

            Assert.Equal(2, d.Value<int>("requested"));
            Assert.Equal(0, d.Value<int>("unresolved"));
            Assert.Equal(1, d.Value<int>("failed"));
            Assert.Equal(ApplicationState.Partial, State(d));
        }

        [Fact]
        public void A_write_that_landed_but_did_not_verify_is_a_failure_not_an_application()
        {
            // Set() was accepted, the commit was confirmed, and the post-commit read did
            // not come back carrying the value. The in-transaction acceptance is not
            // evidence and must not raise the verified count.
            JObject d = WriteTally.PerTarget("Committed", resolvedTargets: 1, unresolvedIds: 0,
                                             verifiedTargets: 0, unverifiedTargets: 1);

            Assert.Equal(1, d.Value<int>("requested"));
            Assert.Equal(0, d.Value<int>("applied"));
            Assert.Equal(1, d.Value<int>("failed"));
            Assert.Equal(ApplicationState.Failed, State(d));
        }

        [Fact]
        public void All_three_failures_at_once_stay_apart_and_none_is_counted_twice()
        {
            // Five ids: 1 never resolved, 4 became targets. Of those, 1 was refused by
            // Set(), 1 wrote but failed its read-back, 2 verified. The refused and the
            // unverified are BOTH unverified targets - a refused write leaves the old
            // value, and the read-back is what proves it - so failed is 2, not 3.
            JObject d = WriteTally.PerTarget("Committed", resolvedTargets: 4, unresolvedIds: 1,
                                             verifiedTargets: 2, unverifiedTargets: 2);

            Assert.Equal(5, d.Value<int>("requested"));
            Assert.Equal(1, d.Value<int>("unresolved"));
            Assert.Equal(2, d.Value<int>("failed"));
            Assert.Equal(2, d.Value<int>("verified"));
            Assert.Equal(0, d.Value<int>("unknown"));
            // requested is the sum of what happened to each thing asked for, once each.
            Assert.Equal(d.Value<int>("requested"),
                         d.Value<int>("unresolved") + d.Value<int>("failed") + d.Value<int>("verified"));
            Assert.Equal(ApplicationState.Partial, State(d));
        }

        [Fact]
        public void A_target_that_was_never_measured_becomes_unknown_rather_than_being_absorbed()
        {
            // The fail-closed property: the write loop can `continue` past a target. Four
            // resolved, only three accounted for. The missing one is not zero and not a
            // failure - nobody looked - and one of those makes the batch uncertain.
            JObject d = WriteTally.PerTarget("Committed", resolvedTargets: 4, unresolvedIds: 0,
                                             verifiedTargets: 3, unverifiedTargets: 0);

            Assert.Equal(1, d.Value<int>("unknown"));
            Assert.Equal(ApplicationState.Uncertain, State(d));
        }

        [Fact]
        public void A_batch_whose_transaction_did_not_commit_is_never_an_application()
        {
            foreach (string status in new[] { "RolledBack", "Pending", "Error", null })
                Assert.False(ApplicationOutcome.IsFullyApplied(
                    State(WriteTally.PerTarget(status, 3, 0, 3, 0))));
        }

        [Fact]
        public void Nothing_resolved_at_all_is_a_failure_not_a_no_op()
        {
            // Every id sent failed to resolve. Requested is what the caller asked for, so
            // this cannot collapse into "nothing was requested".
            JObject d = WriteTally.PerTarget("Committed", resolvedTargets: 0, unresolvedIds: 3,
                                             verifiedTargets: 0, unverifiedTargets: 0);

            Assert.Equal(3, d.Value<int>("requested"));
            Assert.Equal(ApplicationState.Failed, State(d));
        }

        // ---- Impossible counts are uncertain, never repaired --------------------
        //
        // The first version of this file clamped negatives to zero and let over-counts
        // through, and BOTH turned corrupt input into a state PlanLedger accepts:
        // (-5,-1,-2,-3) clamped to (0,0,0,0), which is NoOp; and resolved=1 with
        // verified=2 sailed past as verified_applied. A clamp is a repair, and there is
        // nothing here to repair.

        [Theory]
        [InlineData(-1, 0, 0, 0, "resolved_targets")]
        [InlineData(0, -1, 0, 0, "unresolved_ids")]
        [InlineData(0, 0, -1, 0, "verified_targets")]
        [InlineData(0, 0, 0, -1, "unverified_targets")]
        [InlineData(-5, -1, -2, -3, "resolved_targets")]
        public void Any_negative_count_is_uncertain_and_names_itself(int resolved, int unresolved,
                                                                    int verified, int unverified, string named)
        {
            JObject d = WriteTally.PerTarget("Committed", resolved, unresolved, verified, unverified);

            Assert.Equal(ApplicationState.Uncertain, State(d));
            Assert.False(ApplicationOutcome.IsFullyApplied(State(d)));
            Assert.Contains(named, d.Value<string>("counts_contradict"));
        }

        [Fact]
        public void More_verified_than_resolved_is_uncertain_not_a_full_application()
        {
            // The over-count the clamp could not see: the classifier only ever asks whether
            // verified reaches requested, so 2-of-1 satisfied it.
            JObject d = WriteTally.PerTarget("Committed", resolvedTargets: 1, unresolvedIds: 0,
                                             verifiedTargets: 2, unverifiedTargets: 0);

            Assert.Equal(ApplicationState.Uncertain, State(d));
            // The diagnostic must name the ACTUAL problem. The sum rule would also fire here
            // and its sentence also contains "exceeds resolved_targets", so asserting only
            // that substring let the specific check be deleted without a test noticing -
            // measured. A caller reading "at least one target was counted twice" when the
            // real fault is a verified count larger than the batch goes looking in the
            // wrong place.
            Assert.StartsWith("verified_targets (2) exceeds resolved_targets (1)",
                              d.Value<string>("counts_contradict"));
        }

        [Fact]
        public void More_unverified_than_resolved_is_uncertain()
        {
            JObject d = WriteTally.PerTarget("Committed", resolvedTargets: 2, unresolvedIds: 0,
                                             verifiedTargets: 0, unverifiedTargets: 3);

            Assert.Equal(ApplicationState.Uncertain, State(d));
            Assert.StartsWith("unverified_targets (3) exceeds resolved_targets (2)",
                              d.Value<string>("counts_contradict"));
        }

        [Fact]
        public void A_sum_greater_than_resolved_means_a_target_was_counted_twice()
        {
            // Neither number exceeds resolved on its own; together they cannot both be true.
            JObject d = WriteTally.PerTarget("Committed", resolvedTargets: 3, unresolvedIds: 0,
                                             verifiedTargets: 2, unverifiedTargets: 2);

            Assert.Equal(ApplicationState.Uncertain, State(d));
            Assert.Contains("counted twice", d.Value<string>("counts_contradict"));
        }

        [Fact]
        public void A_sum_smaller_than_resolved_leaves_the_difference_as_unknown()
        {
            // Not a contradiction - a gap. Four resolved, three accounted for: the fourth
            // was never measured, and that is unknown rather than agreement.
            JObject d = WriteTally.PerTarget("Committed", resolvedTargets: 4, unresolvedIds: 0,
                                             verifiedTargets: 2, unverifiedTargets: 1);

            Assert.Null(d["counts_contradict"]);
            Assert.Equal(1, d.Value<int>("unknown"));
            Assert.Equal(ApplicationState.Uncertain, State(d));
        }

        [Fact]
        public void Impossible_counts_are_published_exactly_as_they_were_passed()
        {
            // No silent repair: a caller has to be able to see what it actually handed over,
            // or the bug that produced it is invisible in the reply that reports it.
            JObject d = WriteTally.PerTarget("Committed", resolvedTargets: 1, unresolvedIds: 0,
                                             verifiedTargets: 5, unverifiedTargets: 0);

            Assert.Equal(5, d.Value<int>("verified"));
            Assert.Equal(1, d.Value<int>("resolved_targets"));
            Assert.Equal(ApplicationState.Uncertain, State(d));
        }

        [Fact]
        public void Only_four_zeroes_may_be_a_no_op()
        {
            Assert.Equal(ApplicationState.NoOp,
                         State(WriteTally.PerTarget("Committed", 0, 0, 0, 0)));

            // Everything one step away from it is not.
            Assert.NotEqual(ApplicationState.NoOp, State(WriteTally.PerTarget("Committed", 1, 0, 0, 0)));
            Assert.NotEqual(ApplicationState.NoOp, State(WriteTally.PerTarget("Committed", 0, 1, 0, 0)));
            Assert.NotEqual(ApplicationState.NoOp, State(WriteTally.PerTarget("Committed", 0, 0, 1, 0)));
            Assert.NotEqual(ApplicationState.NoOp, State(WriteTally.PerTarget("Committed", 0, 0, 0, 1)));
        }

        [Fact]
        public void A_zero_batch_with_an_impossible_count_is_uncertain_not_a_no_op()
        {
            // The exact shape the clamp used to produce: nothing resolved, yet something
            // claims to have been verified.
            Assert.Equal(ApplicationState.Uncertain,
                         State(WriteTally.PerTarget("Committed", 0, 0, 1, 0)));
            Assert.Equal(ApplicationState.Uncertain,
                         State(WriteTally.PerTarget("Committed", 0, 0, 0, 1)));
        }

        [Fact]
        public void A_complete_valid_batch_is_still_a_full_application()
        {
            // The strictness must not have cost the ordinary case.
            JObject d = WriteTally.PerTarget("Committed", resolvedTargets: 6, unresolvedIds: 0,
                                             verifiedTargets: 6, unverifiedTargets: 0);

            Assert.Null(d["counts_contradict"]);
            Assert.Equal(0, d.Value<int>("unknown"));
            Assert.Equal(ApplicationState.VerifiedApplied, State(d));
            Assert.True(ApplicationOutcome.IsFullyApplied(State(d)));
        }

        [Fact]
        public void No_impossible_combination_can_reach_a_state_a_plan_would_keep()
        {
            // Swept rather than sampled: nothing in this neighbourhood may be assimilable
            // unless the counts genuinely add up.
            for (int resolved = -2; resolved <= 3; resolved++)
            for (int unresolved = -2; unresolved <= 3; unresolved++)
            for (int verified = -2; verified <= 3; verified++)
            for (int unverified = -2; unverified <= 3; unverified++)
            {
                ApplicationState state = State(WriteTally.PerTarget("Committed", resolved, unresolved,
                                                                    verified, unverified));
                if (!ApplicationOutcome.IsFullyApplied(state)) continue;

                // The only assimilable shapes: everything zero (NoOp), or every resolved
                // target verified with nothing unresolved and nothing unverified.
                bool legitimate =
                    (resolved == 0 && unresolved == 0 && verified == 0 && unverified == 0) ||
                    (resolved > 0 && unresolved == 0 && unverified == 0 && verified == resolved);

                Assert.True(legitimate,
                    "assimilable from impossible counts: resolved=" + resolved + " unresolved=" + unresolved +
                    " verified=" + verified + " unverified=" + unverified + " -> " + ApplicationOutcome.Name(state));
            }
        }

        // ---- Overflow, and what dominates when two things are wrong at once ----

        [Fact]
        public void A_requested_total_that_does_not_fit_an_int_is_uncertain_rather_than_wrapped()
        {
            // int arithmetic here does not produce a big number, it produces a NEGATIVE one,
            // and a negative requested classifies as a no-op - the assimilable state.
            JObject d = WriteTally.PerTarget("Committed", resolvedTargets: int.MaxValue, unresolvedIds: 1,
                                             verifiedTargets: 0, unverifiedTargets: 0);

            Assert.Equal(ApplicationState.Uncertain, State(d));
            Assert.Contains("does not fit", d.Value<string>("counts_contradict"));
            Assert.True(d.Value<int>("requested") >= 0, "a wrapped negative reached the reply");
        }

        [Fact]
        public void Int_max_on_its_own_is_arithmetic_not_a_contradiction()
        {
            JObject d = WriteTally.PerTarget("Committed", resolvedTargets: int.MaxValue, unresolvedIds: 0,
                                             verifiedTargets: int.MaxValue, unverifiedTargets: 0);

            Assert.Null(d["counts_contradict"]);
            Assert.Equal(ApplicationState.VerifiedApplied, State(d));
        }

        [Fact]
        public void A_pair_that_would_overflow_the_accounted_sum_is_still_caught()
        {
            // verified + unverified computed in int would wrap; in long it exceeds resolved.
            JObject d = WriteTally.PerTarget("Committed", resolvedTargets: int.MaxValue, unresolvedIds: 0,
                                             verifiedTargets: int.MaxValue, unverifiedTargets: int.MaxValue);

            Assert.Equal(ApplicationState.Uncertain, State(d));
            Assert.NotNull(d["counts_contradict"]);
        }

        [Theory]
        [InlineData("RolledBack")]
        [InlineData("Pending")]
        [InlineData("Committed")]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("  Committed  ")]
        [InlineData("COMMITTED")]
        public void Contradictory_counters_are_uncertain_whatever_the_transaction_said(string status)
        {
            // Two problems at once: the counts cannot describe a batch AND the transaction
            // may not have committed. The plan must refuse in every combination, and the
            // reply must keep the more actionable diagnosis rather than flattening to one.
            JObject d = WriteTally.PerTarget(status, resolvedTargets: 1, unresolvedIds: 0,
                                             verifiedTargets: 5, unverifiedTargets: 0);

            Assert.Equal(ApplicationState.Uncertain, State(d));
            Assert.False(ApplicationOutcome.IsFullyApplied(State(d)));
            Assert.Contains("exceeds resolved_targets", d.Value<string>("counts_contradict"));
            // The status is preserved beside it, so neither fact is lost.
            Assert.Equal(status, d.Value<string>("transaction_status"));
        }

        [Theory]
        [InlineData("  Committed  ")]
        [InlineData("COMMITTED")]
        [InlineData("committed ")]
        public void A_status_that_is_not_exactly_the_constant_is_uncertain_even_with_perfect_counts(string status)
        {
            Assert.Equal(ApplicationState.Uncertain,
                         State(WriteTally.PerTarget(status, 3, 0, 3, 0)));
        }

        [Fact]
        public void Unresolved_ids_with_no_resolved_targets_is_a_failure_not_a_no_op()
        {
            JObject d = WriteTally.PerTarget("Committed", resolvedTargets: 0, unresolvedIds: 4,
                                             verifiedTargets: 0, unverifiedTargets: 0);

            Assert.Equal(4, d.Value<int>("requested"));
            Assert.Equal(ApplicationState.Failed, State(d));
        }

        // ---- The purge branch that could not look -----------------------------

        [Fact]
        public void A_purge_that_could_not_be_examined_is_uncertain_and_blocks_both_gates()
        {
            // DeleteCommand's GetUnused-unsupported branch. No transaction was opened, so
            // a reader of transaction_status alone sees the same shape as a legitimate
            // no-op - and this is its opposite: a no-op measured that there was nothing to
            // do, this measured nothing at all.
            var payload = new JObject();
            ApplicationOutcome.Stamp(payload, ApplicationOutcome.Declare(
                ApplicationState.Uncertain, ApplicationOutcome.NotStarted,
                requested: 1, applied: 0, verified: 0, unresolved: 0, failed: 0, unknown: 1));

            ApplicationState state = ApplicationOutcome.Read(payload);
            Assert.Equal(ApplicationState.Uncertain, state);
            Assert.False(ApplicationOutcome.IsFullyApplied(state));   // rolls a plan back
            Assert.False(ApplicationOutcome.IsValidRehearsal(state)); // withholds the token
        }
    }
}
