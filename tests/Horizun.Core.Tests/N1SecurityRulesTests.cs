// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// Frente 4.2-N1, security on models that are not ours. Each fact is proved without a
// Revit: the ownership cursor (CT-P1-1a), the borrow decisions (CT-P1-1b), the sync-only
// permission and the borrowed_only relinquish (CT-P1-2), the capability-aware reminder
// (CT-P0-4b), the internal-dependent rule (LP-P2-10) and the save suggestion (LP-CP-5).
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    [Collection("N1Statics")]
    public class OwnershipPassTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 10, 31, 12, 0, 0, DateTimeKind.Utc);
        private static List<long> Ids(int n) => Enumerable.Range(1, n).Select(i => (long)i * 10).ToList();

        // owned = every id divisible by 20
        private static int Status(long id) => id % 20 == 0 ? 1 : 0;

        private static Func<bool> Budget(int elements)
        {
            int left = elements;
            return () => left-- <= 0;
        }

        [Fact]
        public void A_budget_that_runs_out_resumes_where_it_stopped_and_closes_complete()
        {
            var ids = Ids(10);
            OwnershipPass p1 = OwnershipPassRules.Advance(null, "k", ids, Status, Budget(4), T0);
            Assert.False(p1.Complete);
            Assert.Equal(4, p1.Examined);
            Assert.Equal(2, p1.Owned);
            Assert.Contains("LOWER BOUND", OwnershipPassRules.Note(p1));

            OwnershipPass p2 = OwnershipPassRules.Advance(p1, "k", ids, Status, Budget(4), T0);
            Assert.Same(p1, p2);
            Assert.Equal(8, p2.Examined);
            Assert.False(p2.Complete);

            OwnershipPass p3 = OwnershipPassRules.Advance(p2, "k", ids, Status, Budget(4), T0.AddSeconds(2));
            Assert.True(p3.Complete);
            Assert.Equal(10, p3.Examined);
            Assert.Equal(5, p3.Owned);
            Assert.Equal(3, p3.Calls);
            Assert.NotNull(p3.FinishedUtc);
        }

        [Fact]
        public void A_finished_pass_stays_published_when_the_next_one_starts()
        {
            var ids = Ids(6);
            OwnershipPass done = OwnershipPassRules.Advance(null, "k", ids, Status, () => false, T0);
            Assert.True(done.Complete);
            OwnershipPass next = OwnershipPassRules.Advance(done, "k", ids, Status, Budget(2), T0.AddMinutes(1));
            Assert.NotSame(done, next);
            Assert.False(next.Complete);
            Assert.Equal(2, next.Examined);
            Assert.Same(done, next.LastComplete);
            Assert.Equal(3, next.LastComplete.Owned);
        }

        [Fact]
        public void A_status_that_throws_is_unreadable_and_never_not_owned()
        {
            var ids = Ids(4);
            OwnershipPass p = OwnershipPassRules.Advance(null, "k", ids, id => id == 20 ? throw new InvalidOperationException() : Status(id), () => false, T0);
            Assert.True(p.Complete);
            Assert.Equal(1, p.Unreadable);
            Assert.Equal(1, p.Owned);          // only id 40
            Assert.Contains("NOT free", OwnershipPassRules.Note(p));
        }

        [Fact]
        public void A_zero_budget_examines_nothing_and_says_so()
        {
            OwnershipPass p = OwnershipPassRules.Advance(null, "k", Ids(3), Status, () => true, T0);
            Assert.False(p.Complete);
            Assert.Equal(0, p.Examined);
        }

        [Fact]
        public void Another_document_never_continues_this_cursor()
        {
            OwnershipPass a = OwnershipPassRules.Advance(null, "a", Ids(6), Status, Budget(3), T0);
            OwnershipPass b = OwnershipPassRules.Advance(a, "b", Ids(6), Status, Budget(3), T0);
            Assert.NotSame(a, b);
            Assert.Equal(3, b.Examined);
        }

        [Fact]
        public void An_empty_model_is_a_complete_pass_of_zero_not_a_missing_one()
        {
            OwnershipPass p = OwnershipPassRules.Advance(null, "k", new long[0], Status, () => false, T0);
            Assert.True(p.Complete);
            Assert.Equal(0, p.Examined);
        }

        [Fact]
        public void The_store_keeps_one_cursor_per_document()
        {
            OwnershipPassStore.ClearForTests();
            var p = OwnershipPassRules.Advance(null, "k", Ids(3), Status, Budget(1), T0);
            OwnershipPassStore.Put("k", p);
            Assert.Same(p, OwnershipPassStore.Get("k"));
            Assert.Null(OwnershipPassStore.Get("other"));
            OwnershipPassStore.ClearForTests();
        }
    }

    public class BorrowRulesTests
    {
        [Fact]
        public void Classification_never_calls_unknown_free_or_forces_someone_elses()
        {
            Assert.Equal(BorrowRules.Missing, BorrowRules.Classify(false, CheckoutState.NoOne));
            Assert.Equal(BorrowRules.AlreadyMine, BorrowRules.Classify(true, CheckoutState.Me));
            Assert.Equal(BorrowRules.Borrowable, BorrowRules.Classify(true, CheckoutState.NoOne));
            Assert.Equal(BorrowRules.HeldByOther, BorrowRules.Classify(true, CheckoutState.Others));
            Assert.Equal(BorrowRules.Unreadable, BorrowRules.Classify(true, CheckoutState.Unreadable));
        }

        [Fact]
        public void The_request_is_validated_before_anything_is_asked_of_the_central()
        {
            Assert.NotNull(BorrowRules.Validate(null));
            Assert.NotNull(BorrowRules.Validate(new long[0]));
            Assert.NotNull(BorrowRules.Validate(new long[] { 1, -2 }));
            Assert.NotNull(BorrowRules.Validate(Enumerable.Range(1, BorrowRules.MaxIds + 1).Select(i => (long)i).ToList()));
            Assert.Null(BorrowRules.Validate(new long[] { 1, 2, 3 }));
        }

        [Fact]
        public void A_loan_is_verified_by_the_re_read_not_by_what_the_central_said()
        {
            var after = new Dictionary<long, string> { [1] = CheckoutState.Me, [2] = CheckoutState.Me, [3] = CheckoutState.NoOne };
            BorrowVerdict v = BorrowRules.Verify(new long[] { 1, 2, 3 }, new long[] { 1, 2 }, after);
            Assert.Equal(new long[] { 1, 2 }, v.Borrowed);
            Assert.Equal(new long[] { 3 }, v.NotBorrowed);   // the central refused it: reported, not hidden
            Assert.True(v.Verified);                          // nothing contradicts the answer; the partial is the caller's to judge
        }

        [Fact]
        public void An_element_reported_checked_out_that_re_reads_as_someone_elses_is_a_problem()
        {
            var after = new Dictionary<long, string> { [1] = CheckoutState.Others };
            BorrowVerdict v = BorrowRules.Verify(new long[] { 1 }, new long[] { 1 }, after);
            Assert.False(v.Verified);
            Assert.Contains("re-reads as", v.Problems[0]);
        }

        [Fact]
        public void An_unreadable_status_after_the_call_is_unmeasured_and_not_a_pass()
        {
            BorrowVerdict v = BorrowRules.Verify(new long[] { 7 }, new long[] { 7 }, new Dictionary<long, string>());
            Assert.False(v.Verified);
            Assert.Equal(new long[] { 7 }, v.Unmeasured);
        }

        [Fact]
        public void The_reply_says_how_the_loan_goes_back()
        {
            Assert.Contains("borrowed_only", BorrowRules.ReturnsAt);
        }
    }

    public class SyncOnlyPermissionTests
    {
        [Theory]
        [InlineData("safe_write", true, true)]
        [InlineData("safe_write", false, false)]
        [InlineData("documentation", true, false)]
        [InlineData("read_only", true, false)]
        [InlineData("full_write", false, true)]
        [InlineData("unsafe_code", false, true)]
        public void Only_the_owners_sync_grant_opens_the_sync_to_safe_write_and_nothing_else_does(string profile, bool grant, bool allowed)
        {
            string refusal = OperationPermission.SaveRefusal(profile, "sync_with_central", grant);
            Assert.Equal(allowed, refusal == null);
            if (!allowed) Assert.Contains("permission_profile=" + profile, refusal);
        }

        [Theory]
        [InlineData("save_as")]
        [InlineData(null)]
        public void The_grant_never_widens_which_operations_exist(string operation)
        {
            Assert.NotNull(OperationPermission.SaveRefusal("safe_write", operation, true));
        }

        [Fact]
        public void The_grant_does_not_open_the_other_full_write_exports()
        {
            Assert.NotNull(OperationPermission.ExportRefusal("safe_write", "dwg"));
        }

        [Fact]
        public void The_refusal_says_the_grant_opens_this_operation_and_nothing_else()
        {
            string r = OperationPermission.SaveRefusal("safe_write", "sync_with_central", false);
            Assert.Contains("safe_write with the owner's grant", r);
            Assert.Contains("nothing else of full_write", r);
        }

        [Fact]
        public void The_how_to_enable_text_points_at_save_document_not_the_hidden_session_tool()
        {
            string t = SyncWithCentralRules.HowOwnerEnables("x.json");
            Assert.Contains("horizun_save_document operation=sync_with_central", t);
        }
    }

    public class BorrowedOnlyRelinquishTests
    {
        private static readonly string[] Owned = { "a", "b", "c", "d" };   // a,b via an owned workset; c,d borrowed
        private static readonly string[] Borrowed = { "c", "d" };

        [Fact]
        public void The_choice_parses_and_names_itself()
        {
            Assert.True(SyncWithCentralRules.TryParseRelinquish("borrowed_only", out SyncRelinquish c));
            Assert.Equal(SyncRelinquish.BorrowedOnly, c);
            Assert.Equal("borrowed_only", SyncWithCentralRules.Name(c));
            Assert.False(SyncWithCentralRules.TryParseRelinquish("owned_only", out _));
        }

        [Fact]
        public void Only_the_borrowed_go_back_and_the_worksets_stay()
        {
            SyncVerdict v = SyncWithCentralRules.VerifyOwnership(SyncRelinquish.BorrowedOnly,
                2, Owned, Borrowed, 2, new[] { "a", "b" }, 0, _ => true);
            Assert.True(v.Verified);
            Assert.Empty(v.UnexpectedlyOwned);
            Assert.Empty(v.UnexpectedlyReleased);
        }

        [Fact]
        public void A_borrowed_element_still_held_is_reported_as_not_returned()
        {
            SyncVerdict v = SyncWithCentralRules.VerifyOwnership(SyncRelinquish.BorrowedOnly,
                2, Owned, Borrowed, 2, new[] { "a", "b", "c" }, 0, _ => true);
            Assert.False(v.Verified);
            Assert.Equal(new[] { "c" }, v.UnexpectedlyOwned);
        }

        [Fact]
        public void Giving_up_a_workset_under_borrowed_only_is_a_mismatch()
        {
            SyncVerdict v = SyncWithCentralRules.VerifyOwnership(SyncRelinquish.BorrowedOnly,
                2, Owned, Borrowed, 0, new string[0], 0, _ => true);
            Assert.False(v.Verified);
            Assert.NotEmpty(v.Problems);
        }

        [Fact]
        public void The_precheck_counts_what_goes_back_per_choice()
        {
            JObject all = SyncWithCentralRules.Precheck("msg", 3, 0, false, 10, 4, SyncRelinquish.All);
            JObject only = SyncWithCentralRules.Precheck("msg", 3, 0, false, 10, 4, SyncRelinquish.BorrowedOnly);
            JObject keep = SyncWithCentralRules.Precheck("msg", 3, 0, false, 10, 4, SyncRelinquish.KeepBorrowed);
            JObject none = SyncWithCentralRules.Precheck("msg", 3, 0, false, 10, 4, SyncRelinquish.None);
            Assert.Equal(10, (int)all["elements_that_go_back"]);
            Assert.Equal(4, (int)only["elements_that_go_back"]);
            Assert.Equal(6, (int)keep["elements_that_go_back"]);
            Assert.Equal(0, (int)none["elements_that_go_back"]);
            Assert.Equal(6, (int)only["elements_kept_owned"]);
            Assert.True((bool)all["ok_to_apply"]);
        }

        [Fact]
        public void The_precheck_blocks_on_an_empty_comment_or_unaccepted_unverified_writes()
        {
            JObject noComment = SyncWithCentralRules.Precheck("  ", 1, 0, false, 1, 0, SyncRelinquish.All);
            Assert.False((bool)noComment["ok_to_apply"]);
            Assert.False((bool)noComment["comment_present"]);

            JObject unverified = SyncWithCentralRules.Precheck("msg", 5, 2, false, 1, 0, SyncRelinquish.All);
            Assert.False((bool)unverified["ok_to_apply"]);
            Assert.Equal(2, (int)unverified["session_writes_unverified"]);

            JObject accepted = SyncWithCentralRules.Precheck("msg", 5, 2, true, 1, 0, SyncRelinquish.All);
            Assert.True((bool)accepted["ok_to_apply"]);
        }
    }

    public class ReminderCapabilitiesTests
    {
        [Fact]
        public void With_every_tool_available_the_reminder_names_them()
        {
            string r = ReminderCapabilities.Reminder(2, ReminderCapabilities.All);
            Assert.Contains("horizun_verify_changes", r);
            Assert.Contains("operation=sync_with_central", r);
        }

        [Fact]
        public void Without_verify_changes_the_reminder_does_not_ask_for_it()
        {
            var caps = new ReminderCapabilities { VerifyAvailable = false, SyncAvailable = true, Profile = "safe_write" };
            string r = ReminderCapabilities.Reminder(2, caps);
            Assert.DoesNotContain("re-check them with horizun_verify_changes", r);
            Assert.Contains("is not available in permission_profile=safe_write", r);
            Assert.Contains("accept_findings=true", r);
        }

        [Fact]
        public void Without_the_sync_grant_the_reminder_says_who_grants_it_and_keeps_saving_locally()
        {
            var caps = new ReminderCapabilities { VerifyAvailable = true, SyncAvailable = false, Profile = "safe_write" };
            string r = ReminderCapabilities.Reminder(0, caps);
            Assert.Contains("not allowed in permission_profile=safe_write", r);
            Assert.Contains("save_local", r);
            Assert.DoesNotContain("(dry run, then token)", r);
        }

        [Fact]
        public void Apply_rewrites_the_block_and_publishes_what_it_can_name()
        {
            var block = new JObject { ["sync_blockers"] = 1, ["reminder"] = "old" };
            ReminderCapabilities.Apply(block, new ReminderCapabilities { VerifyAvailable = false, SyncAvailable = true });
            Assert.NotEqual("old", (string)block["reminder"]);
            Assert.False((bool)block["verify_changes_available"]);
            Assert.True((bool)block["sync_available"]);
            ReminderCapabilities.Apply(null, null);   // no block: no-op
        }
    }

    public class InternalDependentRulesTests
    {
        [Theory]
        [InlineData("Sketch", null, true)]
        [InlineData("ModelCurve", "OST_SketchLines", true)]
        [InlineData("DetailLine", "OST_Sketch", true)]
        [InlineData("Wall", "OST_Walls", false)]
        [InlineData("DetailLine", "OST_Lines", false)]
        [InlineData("FilledRegion", null, false)]
        public void Only_sketch_parts_are_sketch_parts(string cls, string bic, bool expected)
        {
            Assert.Equal(expected, InternalDependentRules.IsSketchPart(cls, bic));
        }

        [Fact]
        public void A_dimension_on_the_sketch_and_the_target_is_internal()
        {
            var targets = new HashSet<long> { 100 };
            var sketch = new HashSet<long> { 201, 202 };
            Assert.Equal(InternalDependentRules.ConstrainingDimension,
                InternalDependentRules.Classify("Dimension", "OST_Dimensions", true, new long[] { 201, 202, 100 }, targets, sketch));
        }

        [Fact]
        public void A_dimension_that_also_reaches_somebody_elses_element_is_not_internal()
        {
            var targets = new HashSet<long> { 100 };
            var sketch = new HashSet<long> { 201 };
            Assert.Null(InternalDependentRules.Classify("Dimension", "OST_Dimensions", true, new long[] { 201, 999 }, targets, sketch));
        }

        [Fact]
        public void A_dimension_whose_references_could_not_be_read_is_never_internal()
        {
            Assert.Null(InternalDependentRules.Classify("Dimension", "OST_Dimensions", true, null, new HashSet<long> { 1 }, new HashSet<long>()));
            Assert.Null(InternalDependentRules.Classify("Dimension", "OST_Dimensions", true, new long[0], new HashSet<long> { 1 }, new HashSet<long>()));
        }

        [Fact]
        public void A_non_dimension_non_sketch_dependent_goes_to_the_ownership_evidence()
        {
            Assert.Null(InternalDependentRules.Classify("Wall", "OST_Walls", false, null, new HashSet<long>(), new HashSet<long>()));
            // INT-6 m-8: a sketch is internal only through its owner.
            Assert.Null(InternalDependentRules.Classify("Sketch", "OST_Sketch", false, null, new HashSet<long>(), new HashSet<long>()));
            Assert.Equal(InternalDependentRules.SketchPart,
                InternalDependentRules.Classify("Sketch", "OST_Sketch", false, null, new HashSet<long> { 5 }, new HashSet<long>(), ownerId: 5));
        }
    }

    [Collection("N1Statics")]
    public class SaveSuggestionRulesTests
    {
        [Theory]
        [InlineData(null, 10)]
        [InlineData("", 10)]
        [InlineData("abc", 10)]
        [InlineData("-3", 10)]
        [InlineData("0", 0)]
        [InlineData(" 5 ", 5)]
        public void The_threshold_comes_from_the_environment_or_the_default(string env, int expected)
        {
            Assert.Equal(expected, SaveSuggestionRules.ThresholdFrom(env));
        }

        [Fact]
        public void Below_the_threshold_nothing_is_said_and_from_it_on_the_reply_carries_the_suggestion()
        {
            SaveSuggestionRules.ClearForTests();
            for (int i = 0; i < 4; i++) SaveSuggestionRules.RecordWrite("k");
            Assert.Null(SaveSuggestionRules.Block("k", 5, true, "M"));
            SaveSuggestionRules.RecordWrite("k");
            JObject b = SaveSuggestionRules.Block("k", 5, true, "M");
            Assert.Equal(5, (int)b["writes_since_local_save"]);
            Assert.False((bool)b["saves_by_itself"]);
            Assert.Contains("operation=save_local", (string)b["suggestion"]);
            SaveSuggestionRules.ClearForTests();
        }

        [Fact]
        public void A_save_or_a_manual_save_resets_the_count()
        {
            SaveSuggestionRules.ClearForTests();
            for (int i = 0; i < 6; i++) SaveSuggestionRules.RecordWrite("k");
            SaveSuggestionRules.RecordSave("k");
            Assert.Equal(0, SaveSuggestionRules.WritesSinceSave("k"));
            for (int i = 0; i < 6; i++) SaveSuggestionRules.RecordWrite("k");
            Assert.Null(SaveSuggestionRules.Block("k", 5, false, "M"));          // Revit says nothing is unsaved
            Assert.Equal(0, SaveSuggestionRules.WritesSinceSave("k"));
            SaveSuggestionRules.ClearForTests();
        }

        [Fact]
        public void Zero_turns_it_off_and_documents_are_counted_apart()
        {
            SaveSuggestionRules.ClearForTests();
            for (int i = 0; i < 20; i++) SaveSuggestionRules.RecordWrite("a");
            Assert.Null(SaveSuggestionRules.Block("a", 0, true, "A"));
            Assert.Null(SaveSuggestionRules.Block("b", 5, true, "B"));
            SaveSuggestionRules.ClearForTests();
        }
    }
}
