// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// WHAT THE PLAN'S ELEMENT FINGERPRINT ACTUALLY BINDS, measured on the third
// adversarial pass.
//
// execute_plan's token carries a ResolvedPlan fingerprint, and Confirmations
// .Validate compares it at apply: if the graph resolves differently, the apply is
// refused as a stale plan. That mechanism works. What it covers depends entirely
// on what GraphPlan puts into each row, and it puts one of three things:
//
//   the child's own plan_resolved.fingerprint   when the child materialised a plan
//   "deferred"                                  when the row could not be rehearsed
//   "no_child_plan"                             when the child materialises none
//
// The first binds the child's resolved element set. The other two are CONSTANTS:
// two graphs that will touch completely different elements produce the same
// fingerprint, so the stale-plan check cannot see the difference.
//
// Eleven of the twelve tools materialise a plan. The exception is
// horizun_delete_verified - the destructive one, and the one whose purge_unused
// mode derives its targets from the model at apply time.
//
// These build the exact PlannedElement shapes ExecutePlanCommand.GraphPlan builds
// and hash them with the production ResolvedPlan.Fingerprint(), so what is proved
// here is the real binding and not a description of it.
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class GraphPlanBindingTests
    {
        /// <summary>One row of the graph plan, exactly as GraphPlan composes it.</summary>
        private static PlannedElement Row(int index, string tool, string key, string child)
            => new PlannedElement
            {
                UniqueId = "action:" + index,
                Category = tool,
                Action = PlannedAction.Modify,
                BeforeValues = new Dictionary<string, string>
                {
                    { "key", key }, { "child", child },
                    { "reference_original", "" }, { "reference_resolved", "" }
                }
            };

        private static string Fingerprint(params PlannedElement[] rows)
        {
            var plan = new ResolvedPlan
            {
                Command = "horizun_execute_plan",
                DocumentKey = "doc-A",
                RevitVersion = "2026",
                DocumentFingerprint = "fp-A"
            };
            plan.Elements.AddRange(rows);
            return plan.Fingerprint();
        }

        // ---- The mechanism works for a child that materialises its plan ---------

        [Fact]
        public void A_child_whose_resolved_elements_moved_changes_the_graph_fingerprint()
        {
            string approved = Fingerprint(
                Row(0, "horizun_write_params_verified", "codes", "childfp-AAA"));
            string atApply = Fingerprint(
                Row(0, "horizun_write_params_verified", "codes", "childfp-BBB"));

            Assert.NotEqual(approved, atApply);
        }

        [Fact]
        public void The_shape_of_the_graph_is_bound_too()
        {
            string two = Fingerprint(
                Row(0, "horizun_create_elements", "walls", "childfp-AAA"),
                Row(1, "horizun_write_params_verified", "codes", "childfp-BBB"));
            string one = Fingerprint(
                Row(0, "horizun_create_elements", "walls", "childfp-AAA"));

            Assert.NotEqual(two, one);
        }

        [Fact]
        public void A_key_or_tool_swapped_at_the_same_index_changes_the_fingerprint()
        {
            string approved = Fingerprint(Row(0, "horizun_write_params_verified", "codes", "childfp-AAA"));

            Assert.NotEqual(approved, Fingerprint(Row(0, "horizun_write_params_verified", "otro", "childfp-AAA")));
            Assert.NotEqual(approved, Fingerprint(Row(0, "horizun_delete_verified", "codes", "childfp-AAA")));
        }

        // ---- Deletes now contribute their materialised blast radius --------------

        [Fact]
        public void A_delete_that_will_touch_different_elements_changes_the_graph_fingerprint()
        {
            string approved = Fingerprint(
                Row(0, "horizun_create_elements", "walls", "childfp-AAA"),
                Row(1, "horizun_delete_verified", "purge", "deletefp-three-elements"));

            string atApplyOverADifferentSet = Fingerprint(
                Row(0, "horizun_create_elements", "walls", "childfp-AAA"),
                Row(1, "horizun_delete_verified", "purge", "deletefp-three-thousand-elements"));

            Assert.NotEqual(approved, atApplyOverADifferentSet);
        }

        [Fact]
        public void A_deferred_row_is_identifiable_but_is_not_confirmable()
        {
            // Same shape, different cause: nothing was rehearsed, so there is no child
            // fingerprint to carry. Already reported; proved here at the level where the
            // binding is actually taken.
            string approved = Fingerprint(
                Row(0, "horizun_create_elements", "walls", "childfp-AAA"),
                Row(1, "horizun_delete_verified", "cleanup", "deferred"));

            string atApply = Fingerprint(
                Row(0, "horizun_create_elements", "walls", "childfp-AAA"),
                Row(1, "horizun_delete_verified", "cleanup", "deferred"));

            Assert.Equal(approved, atApply);
            var ledger = new PlanLedger();
            ledger.RecordDeferred(1, "cleanup", "horizun_delete_verified", "reference unavailable");
            Assert.False(ledger.RehearsedCleanly);
        }

        [Fact]
        public void The_three_row_kinds_are_at_least_distinguishable_from_each_other()
        {
            // A row that resolved, one that deferred and one from a command with no plan
            // must not hash alike: they are three different amounts of evidence, and a
            // caller re-reading the plan is entitled to see which it got.
            string resolved = Fingerprint(Row(0, "horizun_delete_verified", "purge", "childfp-AAA"));
            string deferred = Fingerprint(Row(0, "horizun_delete_verified", "purge", "deferred"));
            string noPlan = Fingerprint(Row(0, "horizun_delete_verified", "purge", "no_child_plan"));

            Assert.NotEqual(resolved, deferred);
            Assert.NotEqual(resolved, noPlan);
            Assert.NotEqual(deferred, noPlan);
        }

        [Fact]
        public void A_graph_of_only_unbound_rows_cannot_mint_confirmation()
        {
            // The worst case stated plainly: every row a constant, so the token covers the
            // list of tools and keys and not one element any of them will touch.
            string approved = Fingerprint(
                Row(0, "horizun_delete_verified", "a", "no_child_plan"),
                Row(1, "horizun_delete_verified", "b", "deferred"));

            string atApply = Fingerprint(
                Row(0, "horizun_delete_verified", "a", "no_child_plan"),
                Row(1, "horizun_delete_verified", "b", "deferred"));

            Assert.Equal(approved, atApply);

            var ledger = new PlanLedger();
            ledger.RecordDeferred(0, "a", "horizun_delete_verified", "unbound");
            ledger.RecordDeferred(1, "b", "horizun_delete_verified", "unbound");
            Assert.False(ledger.RehearsedCleanly);
        }

        // ---- What still holds even for an unbound row ---------------------------

        [Fact]
        public void An_unbound_row_is_still_gated_at_apply_by_the_application_declaration()
        {
            // The guarantee that survives: whatever a delete resolves to, it has to come
            // back fully applied and verified or the group rolls back. The gap is about
            // WHICH elements were approved, never about whether the work was verified.
            var ledger = new PlanLedger();
            var partial = new JObjectPartial();

            ApplicationState state;
            Assert.False(ledger.RecordExecuted(0, "purge", "horizun_delete_verified",
                                               true, partial.Payload, null, out state));
            Assert.Equal(ApplicationState.Partial, state);
            Assert.Equal(0, ledger.VerifiedActions);
        }

        /// <summary>A delete reply that purged some of what it resolved.</summary>
        private sealed class JObjectPartial
        {
            public readonly Newtonsoft.Json.Linq.JObject Payload = new Newtonsoft.Json.Linq.JObject();
            public JObjectPartial()
            {
                ApplicationOutcome.StampApplied(Payload, "Committed", 40, 31, 31, 0, 9, 0);
            }
        }
    }
}
