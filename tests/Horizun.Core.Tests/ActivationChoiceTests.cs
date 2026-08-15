// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// How a close reaches a document Revit will let it close (story 5.13).
//
// Revit's API cannot close the ACTIVE document. The manual way out is the decoy
// dance - open a document you do not want so the target stops being active -
// measured three times in one session on 2026-08-05 and twice more at batch
// scale on 2026-08-07, where the last model of a 54-model batch stayed open and
// a relaunched batch SKIPPED it. ActivationChoice decides the way out; these
// tests prove every branch, including the ones a live Revit will not produce on
// demand (every open document detached, an unreadable candidate list).
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class ActivationChoiceTests
    {
        private static ActivationCandidate OnDisk(string title, string path)
            => new ActivationCandidate { Title = title, Path = path, PathExistsOnDisk = true };

        private static ActivationCandidate Detached(string title)
            => new ActivationCandidate { Title = title, Path = title + "_detached.rvt", PathExistsOnDisk = false };

        // ---- no activation needed ---------------------------------------------

        [Fact]
        public void A_non_active_target_needs_no_activation_whatever_the_flag_says()
        {
            var withFlag = ActivationChoice.Decide(targetIsActive: false, activateOther: true,
                candidates: new List<ActivationCandidate> { OnDisk("A", @"C:\m\A.rvt") });
            var withoutFlag = ActivationChoice.Decide(targetIsActive: false, activateOther: false,
                candidates: new List<ActivationCandidate>());

            Assert.Equal(ActivationAction.NotNeeded, withFlag.Action);
            Assert.Equal(ActivationAction.NotNeeded, withoutFlag.Action);
        }

        // ---- the refusal stays the refusal ------------------------------------

        [Fact]
        public void An_active_target_without_the_flag_is_refused_even_with_candidates_available()
        {
            // Activation changes what the user is looking at. Having an easy candidate
            // does not make it something to do uninvited - the flag is the invitation.
            var plan = ActivationChoice.Decide(targetIsActive: true, activateOther: false,
                candidates: new List<ActivationCandidate> { OnDisk("A", @"C:\m\A.rvt") });

            Assert.Equal(ActivationAction.RefusedNotAsked, plan.Action);
            Assert.Null(plan.Chosen);
        }

        // ---- choosing an open document ----------------------------------------

        [Fact]
        public void The_first_candidate_whose_path_exists_wins_and_is_named()
        {
            var second = OnDisk("B", @"C:\m\B.rvt");
            var plan = ActivationChoice.Decide(targetIsActive: true, activateOther: true,
                candidates: new List<ActivationCandidate>
                {
                    Detached("A"),                 // open, but no file a re-open could resolve
                    second,
                    OnDisk("C", @"C:\m\C.rvt")     // qualifies too, but B was first
                });

            Assert.Equal(ActivationAction.ActivateOpenDocument, plan.Action);
            Assert.Same(second, plan.Chosen);
        }

        // ---- the anchor ---------------------------------------------------------

        [Fact]
        public void No_other_document_at_all_means_the_anchor()
        {
            // The 2026-08-07 case: the last model of the batch is the only document
            // open. The way out cannot be another model - there is none.
            var plan = ActivationChoice.Decide(targetIsActive: true, activateOther: true,
                candidates: new List<ActivationCandidate>());

            Assert.Equal(ActivationAction.OpenAnchor, plan.Action);
            Assert.Null(plan.Chosen);
        }

        [Fact]
        public void Candidates_that_only_exist_in_memory_do_not_count()
        {
            // Every other open document is detached or never saved: activating one
            // would mean OpenAndActivateDocument on a path that is not on disk. The
            // anchor is a file the bridge OWNS; a synthetic path is a gamble.
            var plan = ActivationChoice.Decide(targetIsActive: true, activateOther: true,
                candidates: new List<ActivationCandidate> { Detached("A"), Detached("B") });

            Assert.Equal(ActivationAction.OpenAnchor, plan.Action);
        }

        [Fact]
        public void A_null_candidate_list_reads_as_no_candidates_not_as_a_crash()
        {
            var plan = ActivationChoice.Decide(targetIsActive: true, activateOther: true, candidates: null);

            Assert.Equal(ActivationAction.OpenAnchor, plan.Action);
        }

        [Fact]
        public void A_null_entry_inside_the_list_is_skipped_not_dereferenced()
        {
            var real = OnDisk("B", @"C:\m\B.rvt");
            var plan = ActivationChoice.Decide(targetIsActive: true, activateOther: true,
                candidates: new List<ActivationCandidate> { null, real });

            Assert.Equal(ActivationAction.ActivateOpenDocument, plan.Action);
            Assert.Same(real, plan.Chosen);
        }

        // ---- the token binds the activation decision ---------------------------

        [Fact]
        public void Activate_other_is_part_of_the_close_plan_a_token_is_bound_to()
        {
            // A rehearsal approved WITHOUT activate_other must not authorise an
            // execution that also switches the active document: that execution does
            // more than the one the caller saw. Plan, not approval - unlike
            // discard_unsaved, which CloseDecisionTests proves stays out.
            Assert.Contains("activate_other", CloseDecision.PlanFields);
        }
    }
}
