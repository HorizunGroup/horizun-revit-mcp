// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// The materialised plan. These are the cases a real model will not produce on
// request: a collector enumerating the same elements in a different order, a
// third party saving a change between a dry run and an apply, somebody else
// setting the very parameter this run was about to set. All of it is arithmetic
// over strings, so all of it can be proved here instead of hoped for.
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class ResolvedPlanTests
    {
        private static PlannedElement El(string uid, PlannedAction action = PlannedAction.Delete,
                                         string typeName = "T", string fireRating = null)
        {
            var e = new PlannedElement
            {
                UniqueId = uid,
                Category = "Walls",
                TypeName = typeName,
                Level = "L1",
                Action = action
            };
            if (fireRating != null)
                e.BeforeValues = new Dictionary<string, string> { { "Fire Rating", fireRating } };
            return e;
        }

        private static ResolvedPlan Plan(params PlannedElement[] els)
        {
            var p = new ResolvedPlan
            {
                Command = "horizun_delete_verified",
                DocumentKey = "doc-1",
                RevitVersion = "2026",
                DocumentFingerprint = "fp-1"
            };
            p.Elements.AddRange(els);
            return p;
        }

        /// <summary>
        /// Revit is free to enumerate a collector in whatever order it likes between two
        /// calls. That is not a change to the plan, and treating it as one would make every
        /// apply fail for a reason nobody could act on.
        /// </summary>
        [Fact]
        public void The_order_elements_come_back_in_is_not_part_of_the_plan()
        {
            string a = Plan(El("u1"), El("u2"), El("u3")).Fingerprint();
            string b = Plan(El("u3"), El("u1"), El("u2")).Fingerprint();
            Assert.Equal(a, b);
        }

        /// <summary>The whole point: one more matching element is a different plan.</summary>
        [Fact]
        public void An_element_that_appears_after_the_dry_run_changes_the_fingerprint()
        {
            string approved = Plan(El("u1"), El("u2")).Fingerprint();
            string now = Plan(El("u1"), El("u2"), El("u3")).Fingerprint();
            Assert.NotEqual(approved, now);
        }

        [Fact]
        public void An_element_that_disappears_changes_the_fingerprint()
        {
            Assert.NotEqual(Plan(El("u1"), El("u2")).Fingerprint(),
                            Plan(El("u1")).Fingerprint());
        }

        /// <summary>
        /// The quiet one. Same elements, same count - but somebody else already wrote the
        /// value this run was about to write. A request hash cannot see it and neither can a
        /// count; only the before-values can.
        /// </summary>
        [Fact]
        public void A_value_edited_by_somebody_else_changes_the_fingerprint()
        {
            string approved = Plan(El("u1", PlannedAction.Modify, fireRating: "60")).Fingerprint();
            string now = Plan(El("u1", PlannedAction.Modify, fireRating: "120")).Fingerprint();
            Assert.NotEqual(approved, now);
        }

        [Fact]
        public void A_type_swap_between_rehearsal_and_apply_changes_the_fingerprint()
        {
            Assert.NotEqual(Plan(El("u1", typeName: "Generic - 200mm")).Fingerprint(),
                            Plan(El("u1", typeName: "Generic - 300mm")).Fingerprint());
        }

        /// <summary>
        /// A plan is never carried across a Revit upgrade or into another document, even if
        /// the elements happen to render the same.
        /// </summary>
        [Fact]
        public void The_document_and_the_revit_version_are_part_of_the_plan()
        {
            var p1 = Plan(El("u1"));
            var p2 = Plan(El("u1")); p2.DocumentKey = "doc-2";
            var p3 = Plan(El("u1")); p3.RevitVersion = "2027";
            var p4 = Plan(El("u1")); p4.DocumentFingerprint = "fp-2";
            Assert.NotEqual(p1.Fingerprint(), p2.Fingerprint());
            Assert.NotEqual(p1.Fingerprint(), p3.Fingerprint());
            Assert.NotEqual(p1.Fingerprint(), p4.Fingerprint());
        }

        /// <summary>
        /// A cascade nobody predicted is a different operation from the one approved, even
        /// when the listed elements are identical.
        /// </summary>
        [Fact]
        public void An_unpredicted_cascade_changes_the_fingerprint()
        {
            var approved = Plan(El("u1")); approved.ExpectedCascadeCount = 0;
            var now = Plan(El("u1")); now.ExpectedCascadeCount = 4;
            Assert.NotEqual(approved.Fingerprint(), now.Fingerprint());
        }

        /// <summary>
        /// Type and parameter names in this domain contain quotes, commas and brackets -
        /// 'Tee 3" x 1 1/2"' is real. Two DIFFERENT plans must not be able to render into the
        /// same string by having a name that contains the separator.
        /// </summary>
        [Fact]
        public void A_name_containing_separators_cannot_forge_another_plan()
        {
            string a = Plan(El("u1", typeName: "Tee 3\" x 1 1/2\""), El("u2", typeName: "X")).Fingerprint();
            string b = Plan(El("u1", typeName: "Tee 3\" x 1 1/2\"|u2|X"), El("u2", typeName: "")).Fingerprint();
            Assert.NotEqual(a, b);
        }

        /// <summary>The refusal has to say WHAT moved, or somebody diffs two runs by hand.</summary>
        [Fact]
        public void The_drift_description_names_what_actually_changed()
        {
            string appeared = ResolvedPlan.DescribeDrift(Plan(El("u1")), Plan(El("u1"), El("u2")));
            Assert.Contains("1 element(s) now match that did not", appeared);

            string gone = ResolvedPlan.DescribeDrift(Plan(El("u1"), El("u2")), Plan(El("u1")));
            Assert.Contains("no longer match", gone);

            string counts = ResolvedPlan.DescribeDrift(Plan(El("u1"), El("u2")), Plan(El("u1")));
            Assert.Contains("deletion count moved from 2 to 1", counts);
        }

        /// <summary>
        /// Same elements, same counts, different value: the description must not go silent,
        /// because this is exactly the case a human would otherwise not understand.
        /// </summary>
        [Fact]
        public void The_drift_description_explains_a_value_only_change()
        {
            string why = ResolvedPlan.DescribeDrift(
                Plan(El("u1", PlannedAction.Modify, fireRating: "60")),
                Plan(El("u1", PlannedAction.Modify, fireRating: "120")));
            Assert.Contains("somebody else may have already", why);
        }

        [Fact]
        public void An_identical_plan_fingerprints_identically()
        {
            Assert.Equal(Plan(El("u1"), El("u2")).Fingerprint(),
                         Plan(El("u1"), El("u2")).Fingerprint());
        }
    }

    public class StalePlanConfirmationTests
    {
        private static ResolvedPlan Plan(int n)
        {
            var p = new ResolvedPlan { Command = "c", DocumentKey = "d", RevitVersion = "2026", DocumentFingerprint = "f" };
            for (int i = 0; i < n; i++)
                p.Elements.Add(new PlannedElement { UniqueId = "u" + i, Action = PlannedAction.Delete });
            return p;
        }

        /// <summary>
        /// The failure this story exists for, end to end: identical request, same document,
        /// live token - and the model moved. It must not execute.
        /// </summary>
        [Fact]
        public void A_token_is_refused_when_the_element_set_moved()
        {
            var store = new ConfirmationStore();
            ResolvedPlan rehearsed = Plan(2);
            Confirmation c = store.Issue("c", "d", "plan-hash", null, rehearsed.Fingerprint());

            ResolvedPlan atApply = Plan(3);   // one more element matches now
            ConfirmationCheck check = store.Validate(c.Token, "c", "d", "plan-hash",
                atApply.Fingerprint(), ResolvedPlan.DescribeDrift(rehearsed, atApply));

            Assert.False(check.Ok);
            Assert.Equal(ConfirmationState.StalePlan, check.State);
            Assert.Contains("THE MODEL MOVED", check.Message);
            Assert.Contains("now match that did not", check.Message);
        }

        [Fact]
        public void A_token_is_accepted_when_the_model_is_unchanged()
        {
            var store = new ConfirmationStore();
            ResolvedPlan p = Plan(2);
            Confirmation c = store.Issue("c", "d", "plan-hash", null, p.Fingerprint());
            ConfirmationCheck check = store.Validate(c.Token, "c", "d", "plan-hash", Plan(2).Fingerprint(), null);
            Assert.True(check.Ok);
            Assert.Null(check.Message);
        }

        /// <summary>
        /// A refused apply must NOT spend the token: the caller re-runs the dry run and
        /// approves the current plan, and a token burned by the refusal would make the
        /// refusal itself destroy the thing needed to recover from it.
        /// </summary>
        [Fact]
        public void A_stale_plan_does_not_consume_the_approval()
        {
            var store = new ConfirmationStore();
            ResolvedPlan rehearsed = Plan(2);
            Confirmation c = store.Issue("c", "d", "plan-hash", null, rehearsed.Fingerprint());

            store.Validate(c.Token, "c", "d", "plan-hash", Plan(3).Fingerprint(), "drift");
            // The model goes back to what was approved - a colleague undid their change.
            ConfirmationCheck again = store.Validate(c.Token, "c", "d", "plan-hash", Plan(2).Fingerprint(), null);
            Assert.True(again.Ok);
        }

        /// <summary>
        /// A command that has not been taught to materialise its plan must not appear to have
        /// been checked against the model. Silence there is the substitution this repository
        /// exists to refuse.
        /// </summary>
        [Fact]
        public void A_command_without_a_materialised_plan_says_so_on_success()
        {
            var store = new ConfirmationStore();
            Confirmation c = store.Issue("c", "d", "plan-hash");   // no fingerprint
            ConfirmationCheck check = store.Validate(c.Token, "c", "d", "plan-hash", null, null);
            Assert.True(check.Ok);
            Assert.Contains("REQUEST only", check.Message);
            Assert.Contains("would not have been detected", check.Message);
        }
    }
}
