// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// Closing a document without throwing work away by accident.
//
// doc.Close(false) discards everything unsaved, returns true, and leaves nothing
// behind that could tell you. IsModified cannot be asked of a closed document; the
// file on disk is untouched either way. So every signal the close handler collects
// - gone from Application.Documents, IsValidObject false, the API returned true -
// reads identically whether an hour of edits went with it or nothing did.
//
// That is not a failure reported as success. It is a LOSS reported as success, and
// the response cannot be distinguished from the harmless one afterwards by anybody,
// including the handler that wrote it.
//
// The tri-state is the part worth testing hardest, and the part a real Revit cannot
// be asked to demonstrate: "IsModified could not be read" does not happen on
// request. Here it is one argument.
// -----------------------------------------------------------------------------
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class CloseDecisionTests
    {
        // ---- nothing at stake --------------------------------------------------

        [Fact]
        public void Closing_an_unmodified_document_needs_nothing_at_all()
        {
            CloseVerdict v = CloseDecision.Decide(isModified: false, saveOnClose: false,
                                                  discardUnsaved: false, hasConfirmation: false);

            Assert.True(v.Ok);
            Assert.False(v.WouldDiscard);
        }

        [Fact]
        public void Saving_on_close_discards_nothing_so_it_needs_no_permission()
        {
            // The work is being written, not dropped. Demanding discard_unsaved here
            // would be a guard firing on the safe path, which is how callers learn to
            // pass the dangerous flag by reflex.
            CloseVerdict v = CloseDecision.Decide(isModified: true, saveOnClose: true,
                                                  discardUnsaved: false, hasConfirmation: false);

            Assert.True(v.Ok);
            Assert.False(v.WouldDiscard);
        }

        // ---- work at stake -----------------------------------------------------

        [Fact]
        public void Closing_a_modified_document_without_saving_needs_the_flag_first()
        {
            CloseVerdict v = CloseDecision.Decide(isModified: true, saveOnClose: false,
                                                  discardUnsaved: false, hasConfirmation: false);

            Assert.False(v.Ok);
            Assert.True(v.WouldDiscard);
            Assert.Equal(CloseRequirement.NeedsDiscardFlag, v.Requirement);
        }

        [Fact]
        public void The_flag_alone_is_not_enough_a_rehearsal_has_to_have_happened()
        {
            // Saying the words is one thing. Having SEEN what would be lost, and having
            // the approval bound to this document and this request, is another.
            CloseVerdict v = CloseDecision.Decide(isModified: true, saveOnClose: false,
                                                  discardUnsaved: true, hasConfirmation: false);

            Assert.False(v.Ok);
            Assert.Equal(CloseRequirement.NeedsConfirmation, v.Requirement);
        }

        [Fact]
        public void With_the_flag_and_a_confirmation_the_close_goes_ahead()
        {
            CloseVerdict v = CloseDecision.Decide(isModified: true, saveOnClose: false,
                                                  discardUnsaved: true, hasConfirmation: true);

            Assert.True(v.Ok);
            Assert.True(v.WouldDiscard);   // it still says what it is about to do
        }

        [Fact]
        public void A_confirmation_without_the_flag_is_still_refused()
        {
            // Neither substitutes for the other. A token proves a rehearsal happened; it
            // does not contain the sentence "yes, throw it away".
            CloseVerdict v = CloseDecision.Decide(isModified: true, saveOnClose: false,
                                                  discardUnsaved: false, hasConfirmation: true);

            Assert.False(v.Ok);
            Assert.Equal(CloseRequirement.NeedsDiscardFlag, v.Requirement);
        }

        // ---- the tri-state, which is the whole point ---------------------------

        /// <summary>
        /// UNKNOWN IS NOT CLEAN. Document.IsModified can throw, and a document whose
        /// modified flag could not be read is not a document known to have nothing to
        /// lose. Written as `isModified == true` instead of `!= false`, this branch
        /// waves the unknown case straight through - and the unknown case is precisely
        /// the one where nobody can check afterwards.
        /// </summary>
        [Fact]
        public void An_unreadable_modified_flag_is_treated_as_modified()
        {
            CloseVerdict v = CloseDecision.Decide(isModified: null, saveOnClose: false,
                                                  discardUnsaved: false, hasConfirmation: false);

            Assert.False(v.Ok);
            Assert.True(v.WouldDiscard);
            Assert.Equal(CloseRequirement.NeedsDiscardFlag, v.Requirement);
        }

        [Fact]
        public void An_unreadable_modified_flag_still_needs_the_full_ceremony()
        {
            Assert.Equal(CloseRequirement.NeedsConfirmation,
                         CloseDecision.Decide(null, false, true, false).Requirement);
            Assert.True(CloseDecision.Decide(null, false, true, true).Ok);
        }

        [Fact]
        public void An_unreadable_modified_flag_is_irrelevant_when_the_work_is_being_saved()
        {
            Assert.True(CloseDecision.Decide(isModified: null, saveOnClose: true,
                                             discardUnsaved: false, hasConfirmation: false).Ok);
        }

        // ---- what the token is bound to ----------------------------------------

        /// <summary>
        /// The fields that decide WHETHER work is lost and WHICH document loses it. If
        /// save_on_close were outside the approval, a token minted by rehearsing a save
        /// would authorise a discard - the two requests differ in exactly one flag, and
        /// it is the flag that decides whether the hour survives.
        /// </summary>
        [Fact]
        public void Flipping_save_on_close_is_a_different_plan()
        {
            JObject Request(bool saveOnClose, bool discard) => new JObject
            {
                ["operation"] = "close",
                ["target_document"] = @"C:\models\Tower.rvt",
                ["save_on_close"] = saveOnClose,
                ["discard_unsaved"] = discard
            };

            string rehearsedSave = ConfirmationStore.PlanHash(Request(true, false), CloseDecision.PlanFields);
            string actualDiscard = ConfirmationStore.PlanHash(Request(false, true), CloseDecision.PlanFields);

            Assert.NotEqual(rehearsedSave, actualDiscard);
        }

        [Fact]
        public void A_token_for_one_document_does_not_close_another()
        {
            JObject Request(string target) => new JObject
            {
                ["operation"] = "close",
                ["target_document"] = target,
                ["save_on_close"] = false,
                ["discard_unsaved"] = true
            };

            Assert.NotEqual(ConfirmationStore.PlanHash(Request(@"C:\models\Tower.rvt"), CloseDecision.PlanFields),
                            ConfirmationStore.PlanHash(Request(@"C:\models\Podium.rvt"), CloseDecision.PlanFields));
        }

        /// <summary>
        /// THE WHOLE SEQUENCE, AS A CALLER SENDS IT. Two DIFFERENT request objects: the
        /// rehearsal without discard_unsaved, the execution with it. That difference is
        /// the point of a rehearsal, and it is exactly what the first version got wrong.
        ///
        /// discard_unsaved was in the plan fields, so the rehearsal hashed a request where
        /// it was absent and the execution hashed one where it was true. The two could
        /// never match. Every token came back PlanChanged and a document with unsaved
        /// changes could not be closed AT ALL - the guard did not protect the work, it
        /// removed the command.
        ///
        /// The test that shipped beside the bug built ONE request object and hashed it
        /// twice, which is not the sequence and cannot fail on it. This one is.
        /// </summary>
        [Fact]
        public void The_rehearsal_token_opens_the_execution_that_follows_it()
        {
            const string doc = "rvt:2026|guid:abc|path:x|title:tower";
            var store = new ConfirmationStore();

            // 1. The caller asks what would happen. No discard_unsaved: they have not
            //    agreed to anything yet.
            var rehearsal = new JObject
            {
                ["operation"] = "close",
                ["target_document"] = @"C:\models\Tower.rvt",
                ["dry_run"] = true
            };
            CloseVerdict planned = CloseDecision.Decide(isModified: true, saveOnClose: false,
                                                        discardUnsaved: false, hasConfirmation: false);
            Assert.True(planned.WouldDiscard);

            Confirmation issued = store.Issue("horizun_document_session:close", doc,
                                              ConfirmationStore.PlanHash(rehearsal, CloseDecision.PlanFields));

            // 2. The caller says yes, and sends the token back.
            var execution = new JObject
            {
                ["operation"] = "close",
                ["target_document"] = @"C:\models\Tower.rvt",
                ["discard_unsaved"] = true,
                ["confirmation_token"] = issued.Token
            };
            CloseVerdict now = CloseDecision.Decide(isModified: true, saveOnClose: false,
                                                    discardUnsaved: true, hasConfirmation: false);
            Assert.Equal(CloseRequirement.NeedsConfirmation, now.Requirement);

            ConfirmationCheck check = store.Validate(
                issued.Token, "horizun_document_session:close", doc,
                ConfirmationStore.PlanHash(execution, CloseDecision.PlanFields));

            Assert.True(check.Ok, "the rehearsal's own token was refused: " + check.Message);
        }

        [Fact]
        public void Saying_yes_is_not_part_of_the_plan_being_approved()
        {
            // The general shape of the bug above: an approval that has to be present in
            // the thing being approved is a circle. Three commands in this codebase have
            // shipped with that circle before this one made it four.
            Assert.DoesNotContain("discard_unsaved", CloseDecision.PlanFields);
            Assert.DoesNotContain("dry_run", CloseDecision.PlanFields);
            Assert.DoesNotContain("confirmation_token", CloseDecision.PlanFields);
        }

        /// <summary>
        /// And the token still has to be worth something. Dropping discard_unsaved from
        /// the plan must not drop the fields that decide what actually happens.
        /// </summary>
        [Fact]
        public void A_rehearsal_of_a_save_does_not_open_a_discard()
        {
            const string doc = "rvt:2026|guid:abc|path:x|title:tower";
            var store = new ConfirmationStore();

            var rehearsedSave = new JObject
            {
                ["operation"] = "close",
                ["target_document"] = @"C:\models\Tower.rvt",
                ["save_on_close"] = true
            };
            Confirmation issued = store.Issue("horizun_document_session:close", doc,
                                              ConfirmationStore.PlanHash(rehearsedSave, CloseDecision.PlanFields));

            var actuallyDiscard = new JObject
            {
                ["operation"] = "close",
                ["target_document"] = @"C:\models\Tower.rvt",
                ["save_on_close"] = false,
                ["discard_unsaved"] = true
            };

            ConfirmationCheck check = store.Validate(
                issued.Token, "horizun_document_session:close", doc,
                ConfirmationStore.PlanHash(actuallyDiscard, CloseDecision.PlanFields));

            Assert.False(check.Ok);
            Assert.Equal(ConfirmationState.PlanChanged, check.State);
        }

        [Fact]
        public void The_close_token_round_trips_for_the_request_that_was_rehearsed()
        {
            var request = new JObject
            {
                ["operation"] = "close",
                ["target_document"] = @"C:\models\Tower.rvt",
                ["save_on_close"] = false,
                ["discard_unsaved"] = true
            };
            string plan = ConfirmationStore.PlanHash(request, CloseDecision.PlanFields);

            var store = new ConfirmationStore();
            const string doc = "rvt:2026|guid:abc|path:x|title:tower";
            Confirmation issued = store.Issue("horizun_document_session:close", doc, plan);

            // The document moved out from under the approval.
            Assert.False(store.Validate(issued.Token, "horizun_document_session:close", "other-doc", plan).Ok);
            // A different command cannot spend a close token.
            Assert.False(store.Validate(issued.Token, "horizun_delete_verified", doc, plan).Ok);

            Assert.True(store.Validate(issued.Token, "horizun_document_session:close", doc, plan).Ok);
            // Single use: one approval closes one document, once.
            Assert.False(store.Validate(issued.Token, "horizun_document_session:close", doc, plan).Ok);
        }

        [Fact]
        public void The_plan_binds_which_document_and_what_will_be_done_to_it()
        {
            // This used to assert discard_unsaved was in the list, which is how the bug
            // above survived: the test encoded the defect as the requirement. What the
            // plan must pin is the TARGET and the OUTCOME - not the caller's consent to it.
            Assert.Contains("target_document", CloseDecision.PlanFields);
            Assert.Contains("file_path", CloseDecision.PlanFields);
            Assert.Contains("save_on_close", CloseDecision.PlanFields);
            Assert.Contains("force_workshared", CloseDecision.PlanFields);
        }
    }
}
