// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// M10: the "sync only" permission - OFF by default, behind the owner's flag. The flag OFF must
// leave the permission exactly as it shipped; the flag ON adds a strict precheck.
// -----------------------------------------------------------------------------
using System;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class SyncOnlyFlagOffTests
    {
        // The rule as it stood at 95b1d548, copied here so the comparison cannot drift with the code.
        private static bool OldAllows(string profile, string operation, bool grant)
        {
            if (operation != "save_local" && operation != "sync_with_central" && operation != "relinquish_own") return false;
            if (operation != "sync_with_central") return true;
            if (profile == "full_write" || profile == "unsafe_code") return true;
            return profile == "safe_write" && grant;
        }

        [Theory]
        [InlineData("read_only")]
        [InlineData("documentation")]
        [InlineData("safe_write")]
        [InlineData("full_write")]
        [InlineData("unsafe_code")]
        [InlineData(null)]
        public void With_the_flag_off_every_profile_operation_and_grant_decides_as_before(string profile)
        {
            foreach (string op in new[] { "save_local", "sync_with_central", "relinquish_own", "save_as", null })
                foreach (bool grant in new[] { false, true })
                {
                    string now = OperationPermission.SaveRefusal(profile, op, grant, syncOnlyFlag: false);
                    string defaulted = OperationPermission.SaveRefusal(profile, op, grant);
                    Assert.Equal(OldAllows(profile, op, grant), now == null);
                    Assert.Equal(defaulted, now);
                }
        }

        [Theory]
        [InlineData("safe_write")]
        [InlineData("full_write")]
        [InlineData("documentation")]
        public void With_the_flag_off_the_sync_is_never_strict(string profile)
        {
            Assert.False(SyncOnlyGrantRules.Strict(false, profile));
            Assert.Same(SyncWithCentralRules.ModelFields, SyncOnlyGrantRules.ModelFields(false));
        }

        [Fact]
        public void With_the_flag_off_the_reminder_is_the_earlier_sentence()
        {
            var caps = new ReminderCapabilities { VerifyAvailable = true, SyncAvailable = true, Profile = "safe_write" };
            Assert.Equal(
                "Workshared model: these changes are saved LOCALLY only. When the task is verified and the person confirms, synchronize once with " +
                "horizun_save_document operation=sync_with_central (dry run, then token), relinquishing what you borrowed.",
                ReminderCapabilities.Reminder(0, caps));
            var block = new JObject { ["sync_blockers"] = 0 };
            ReminderCapabilities.Apply(block, caps);
            Assert.Null(block["sync_only"]);
        }

        [Fact]
        public void With_the_flag_off_and_no_grant_the_reminder_points_at_the_owners_grant_and_names_the_option()
        {
            var caps = new ReminderCapabilities { SyncAvailable = false, Profile = "safe_write" };
            string text = ReminderCapabilities.Reminder(0, caps);
            Assert.Contains("Advanced options > Synchronize with central", text);
            Assert.Contains("sync-only option, off by default", text);
        }
    }

    public class SyncOnlyFlagOnTests
    {
        [Fact]
        public void The_flag_opens_the_sync_to_safe_write_without_the_other_grant_and_to_nothing_else()
        {
            Assert.Null(OperationPermission.SaveRefusal("safe_write", "sync_with_central", false, syncOnlyFlag: true));
            Assert.NotNull(OperationPermission.SaveRefusal("documentation", "sync_with_central", false, syncOnlyFlag: true));
            Assert.NotNull(OperationPermission.SaveRefusal("read_only", "sync_with_central", false, syncOnlyFlag: true));
            Assert.NotNull(OperationPermission.SaveRefusal("safe_write", "save_as", false, syncOnlyFlag: true));
            Assert.NotNull(OperationPermission.ExportRefusal("safe_write", "dwg"));
        }

        [Theory]
        [InlineData(true, "safe_write", true)]
        [InlineData(true, "full_write", true)]
        [InlineData(true, "unsafe_code", true)]
        [InlineData(true, "documentation", false)]
        [InlineData(true, "read_only", false)]
        [InlineData(false, "safe_write", false)]
        [InlineData(false, "full_write", false)]
        public void Strict_means_flag_on_and_safe_write(bool flag, string profile, bool strict)
        {
            Assert.Equal(strict, SyncOnlyGrantRules.Strict(flag, profile));
        }

        [Fact]
        public void The_reminder_names_the_strict_route_when_it_applies()
        {
            var caps = new ReminderCapabilities { SyncAvailable = true, SyncOnly = true, Profile = "safe_write" };
            string text = ReminderCapabilities.Reminder(0, caps);
            Assert.Contains("sync-only permission", text);
            Assert.Contains("relinquish=all or borrowed_only", text);
            var block = new JObject { ["sync_blockers"] = 2 };
            ReminderCapabilities.Apply(block, caps);
            Assert.True((bool)block["sync_only"]);
            Assert.Contains("does not accept findings", (string)block["reminder"]);
            Assert.DoesNotContain("repeat with accept_findings", (string)block["reminder"]);
        }

        [Fact]
        public void An_empty_or_blank_comment_is_refused()
        {
            foreach (string comment in new[] { null, "", "   ", "\t\n" })
                Assert.Equal("sync_only_comment_required",
                    SyncOnlyGrantRules.RequestRefusal(comment, SyncRelinquish.All, false).Code);
        }

        [Fact]
        public void Accepting_findings_is_refused_because_the_own_diff_must_be_zero()
        {
            Assert.Equal("sync_only_no_accept_findings",
                SyncOnlyGrantRules.RequestRefusal("c", SyncRelinquish.All, true).Code);
        }

        [Theory]
        [InlineData(SyncRelinquish.None)]
        [InlineData(SyncRelinquish.KeepBorrowed)]
        public void A_relinquish_that_leaves_borrowed_elements_is_refused(SyncRelinquish choice)
        {
            Assert.Equal("sync_only_relinquish", SyncOnlyGrantRules.RequestRefusal("c", choice, false).Code);
        }

        [Theory]
        [InlineData(SyncRelinquish.All)]
        [InlineData(SyncRelinquish.BorrowedOnly)]
        public void A_complete_request_passes_the_request_checks(SyncRelinquish choice)
        {
            Assert.Null(SyncOnlyGrantRules.RequestRefusal("close levels", choice, false));
        }

        [Fact]
        public void The_preview_lists_what_the_user_holds_without_stating_a_verdict_of_its_own()
        {
            JObject b = SyncOnlyGrantRules.PreviewBlock(SyncRelinquish.All, new long[] { 10, 11, 12 },
                new[] { "u10", "u11", "u12" }, new long[] { 10, 11, 12, 13 }, new[] { "Shared Levels", "Shared Grids" },
                new[] { 4, 5 }, 7);
            Assert.Equal(3, (int)b["borrowed"]["count"]);
            Assert.Equal(3, ((JArray)b["borrowed"]["sample_unique_ids"]).Count);
            Assert.Equal(2, (int)b["owned_worksets"]["count"]);
            Assert.Equal(4, (int)b["owned_elements"]);
            Assert.Equal(7, (int)b["held_by_others"]);
            Assert.Null(b["ok_to_apply"]);
            Assert.Null(b["cedes_only_what_the_user_owns"]);
        }

        // ---- ONE decision for the command and for pending_sync, over profile x flag x older grant ----

        private static readonly string[] Profiles = { "read_only", "documentation", "safe_write", "full_write", "unsafe_code" };

        [Fact]
        public void The_reminder_and_the_command_use_the_same_decision_over_the_whole_matrix()
        {
            foreach (string profile in Profiles)
                foreach (bool flag in new[] { false, true })
                    foreach (bool grant in new[] { false, true })
                    {
                        // expected, written out independently of the code under test
                        bool canSyncProfile = profile == "safe_write" || profile == "full_write" || profile == "unsafe_code";
                        bool strict = flag && canSyncProfile;
                        bool allowed = canSyncProfile && (grant || flag);
                        var d = SyncOnlyGrantRules.Decide(profile, grant, flag);
                        Assert.Equal(strict, d.Strict);
                        Assert.Equal(allowed, d.Allowed);
                        ReminderCapabilities caps = ReminderCapabilities.For(profile, grant, flag, verifyAvailable: true, saveToolAllowed: true);
                        Assert.Equal(allowed, caps.SyncAvailable);
                        Assert.Equal(strict, caps.SyncOnly);
                        Assert.Equal(profile, caps.Profile);
                        // a promise is only made when the permission check itself would not refuse
                        if (caps.SyncAvailable)
                            Assert.Null(OperationPermission.SaveRefusal(profile, "sync_with_central", grant, flag));
                        // the tool being hidden by the profile removes the promise
                        Assert.False(ReminderCapabilities.For(profile, grant, flag, true, saveToolAllowed: false).SyncAvailable);
                    }
        }

        [Fact]
        public void A_higher_profile_never_gets_less_than_safe_write()
        {
            foreach (bool grant in new[] { false, true })
                foreach (string higher in new[] { "full_write", "unsafe_code" })
                    if (SyncOnlyGrantRules.Decide("safe_write", grant, true).Allowed) Assert.True(SyncOnlyGrantRules.Decide(higher, grant, true).Allowed);
        }

        [Fact]
        public void The_flag_plus_the_older_grant_leaves_safe_write_strict()
        {
            Assert.True(SyncOnlyGrantRules.Decide("safe_write", ownerSyncGrant: true, flagOn: true).Strict);
            Assert.False(SyncOnlyGrantRules.Decide("safe_write", ownerSyncGrant: true, flagOn: false).Strict);
        }

        // ---- the token: it covers the comment and the list ----

        private static JObject Estimate(string comment, string borrowedDigest)
        {
            return new JObject
            {
                ["document"] = "doc-key", ["comment"] = comment, ["relinquish"] = "all", ["compact"] = false,
                ["owned_worksets"] = 2, ["owned_elements"] = 4, ["borrowed_elements"] = 3, ["is_modified"] = false,
                ["has_all_changes_from_central"] = true, ["sample_status_counts"] = new JObject(),
                ["borrowed_digest"] = borrowedDigest, ["owned_digest"] = "o", ["owned_workset_ids"] = "w"
            };
        }

        [Fact]
        public void The_token_changes_when_the_comment_changes()
        {
            string a = ConfirmationStore.PlanHash(Estimate("close levels", "d"), SyncWithCentralRules.RequestFields);
            string b = ConfirmationStore.PlanHash(Estimate("close levels now", "d"), SyncWithCentralRules.RequestFields);
            Assert.NotEqual(a, b);
        }

        [Fact]
        public void The_token_changes_when_the_borrowed_list_changes_only_in_strict_mode()
        {
            string[] strict = SyncOnlyGrantRules.ModelFields(true);
            string a = ConfirmationStore.PlanHash(Estimate("c", SyncOnlyGrantRules.Digest(new[] { "x", "y" })), strict);
            string b = ConfirmationStore.PlanHash(Estimate("c", SyncOnlyGrantRules.Digest(new[] { "x", "z" })), strict);
            Assert.NotEqual(a, b);
            string[] plain = SyncOnlyGrantRules.ModelFields(false);
            Assert.Equal(ConfirmationStore.PlanHash(Estimate("c", "1"), plain), ConfirmationStore.PlanHash(Estimate("c", "2"), plain));
        }

        [Fact]
        public void The_drift_message_names_the_list_that_moved()
        {
            var approved = new System.Collections.Generic.Dictionary<string, string> { ["borrowed_digest"] = "\"a\"" };
            var now = new System.Collections.Generic.Dictionary<string, string> { ["borrowed_digest"] = "\"b\"" };
            string text = SyncWithCentralRules.DescribeEstimateDrift(approved, now, SyncOnlyGrantRules.ModelFields(true));
            Assert.Contains("borrowed_digest", text);
            Assert.Null(SyncWithCentralRules.DescribeEstimateDrift(approved, now));
        }

        [Fact]
        public void The_digest_ignores_order_and_duplicates_and_sees_every_member()
        {
            Assert.Equal(SyncOnlyGrantRules.Digest(new[] { "a", "b" }), SyncOnlyGrantRules.Digest(new[] { "b", "a", "a" }));
            Assert.NotEqual(SyncOnlyGrantRules.Digest(new[] { "a", "b" }), SyncOnlyGrantRules.Digest(new[] { "a" }));
            Assert.NotEqual(SyncOnlyGrantRules.Digest(new string[0]), SyncOnlyGrantRules.Digest(new[] { "a" }));
        }

        // ---- the re-read after the sync ----

        private static SyncVerdict Post(SyncRelinquish choice = SyncRelinquish.All, int owned = 0, int borrowed = 0, int? worksets = 0,
            bool? modified = false, bool? hasAll = true, string[] before = null, string[] after = null, string[] others = null)
            => SyncOnlyGrantRules.PostCheck(choice, owned, borrowed, worksets, modified, hasAll,
                before ?? new[] { "a", "b" }, after ?? new string[0], others ?? new[] { "z" });

        [Fact]
        public void A_clean_sync_verifies()
        {
            SyncVerdict v = Post();
            Assert.True(v.Verified);
            Assert.Empty(v.Problems);
        }

        [Fact]
        public void What_others_held_before_and_no_longer_hold_is_listed_and_not_a_failure()
        {
            SyncVerdict v = SyncOnlyGrantRules.PostCheck(SyncRelinquish.All, 0, 0, 0, false, true,
                new[] { "a" }, new string[0], new[] { "z1", "z2", "z3" }, new[] { "z1" }, uid => uid != "z3");
            Assert.True(v.Verified);
            Assert.Equal(new[] { "z2" }, v.UnexpectedlyReleased.ToArray());
        }

        [Fact]
        public void A_borrowed_leftover_is_a_failure_not_a_success()
        {
            SyncVerdict v = Post(borrowed: 2, owned: 2, after: new[] { "a", "b" });
            Assert.False(v.Verified);
            Assert.Contains(v.Problems, p => p.Contains("still borrowed"));
        }

        [Fact]
        public void A_leftover_owned_element_or_workset_after_relinquish_all_fails()
        {
            Assert.False(Post(owned: 1, after: new[] { "a" }).Verified);
            Assert.False(Post(worksets: 1).Verified);
        }

        [Fact]
        public void Borrowed_only_may_keep_the_owned_worksets_but_not_the_borrowed_elements()
        {
            Assert.True(Post(SyncRelinquish.BorrowedOnly, owned: 5, worksets: 2, after: new[] { "w1", "w2" }).Verified);
            Assert.False(Post(SyncRelinquish.BorrowedOnly, owned: 5, borrowed: 1, worksets: 2).Verified);
        }

        [Fact]
        public void Pending_local_changes_fail_and_an_unreadable_flag_is_unmeasured()
        {
            Assert.False(Post(modified: true).Verified);
            Assert.Null(Post(modified: null).Verified);
        }

        [Fact]
        public void The_update_status_against_the_central_is_measured_and_not_assumed()
        {
            Assert.Null(Post(hasAll: null).Verified);
            Assert.Null(Post(hasAll: false).Verified);
        }

        [Fact]
        public void An_element_held_by_someone_else_is_never_ceded_by_the_user()
        {
            // "z" was held by someone else; it must not appear among what the user gave back...
            SyncVerdict ceded = Post(before: new[] { "a", "z" }, after: new string[0], others: new[] { "z" });
            Assert.False(ceded.Verified);
            Assert.Contains(ceded.Problems, p => p.Contains("held by someone else") && p.Contains("z"));
            // ...nor read as the user's afterwards.
            SyncVerdict gained = Post(owned: 1, after: new[] { "z" }, others: new[] { "z" }, borrowed: 0);
            Assert.False(gained.Verified);
        }

        [Fact]
        public void The_flag_key_is_its_own_key()
        {
            Assert.Equal("allow_sync_only_grant", SyncOnlyGrantRules.SettingsKey);
            Assert.NotEqual("sync_with_central_owner_granted", SyncOnlyGrantRules.SettingsKey);
        }
    }
}
