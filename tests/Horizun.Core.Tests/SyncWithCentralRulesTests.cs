using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class SyncWithCentralRulesTests
    {
        private static string[] U(params string[] uids) => uids;

        [Theory]
        [InlineData(null, SyncRelinquish.All)]
        [InlineData("all", SyncRelinquish.All)]
        [InlineData("KEEP_BORROWED", SyncRelinquish.KeepBorrowed)]
        [InlineData(" none ", SyncRelinquish.None)]
        public void Relinquish_parses_the_three_choices(string text, SyncRelinquish expected)
        {
            Assert.True(SyncWithCentralRules.TryParseRelinquish(text, out var choice));
            Assert.Equal(expected, choice);
        }

        [Fact]
        public void Relinquish_refuses_anything_else()
        {
            Assert.False(SyncWithCentralRules.TryParseRelinquish("some", out _));
        }

        [Fact]
        public void A_detached_copy_is_refused_by_name()
        {
            var r = SyncWithCentralRules.DocumentRefusal(true, true);
            Assert.Equal("detached_copy", r.Code);
            Assert.Contains("Nothing ran", r.Message);
        }

        [Theory]
        [InlineData(false, false, "not_workshared")]
        [InlineData(null, false, "workshared_state_unreadable")]
        [InlineData(true, null, "detached_state_unreadable")]
        public void Non_central_or_unknown_documents_are_refused(bool? workshared, bool? detached, string code)
        {
            Assert.Equal(code, SyncWithCentralRules.DocumentRefusal(workshared, detached).Code);
        }

        [Fact]
        public void A_workshared_local_passes_the_shape_check()
        {
            Assert.Null(SyncWithCentralRules.DocumentRefusal(true, false));
        }

        [Fact]
        public void Documented_preconditions_refuse_before_the_call_and_unknowns_are_left_to_it()
        {
            Assert.Equal("document_read_only", SyncWithCentralRules.PreconditionRefusal(true, false, true).Code);
            // IsModifiable is TRUE inside an open transaction: that is what the call refuses.
            Assert.Equal("transaction_open", SyncWithCentralRules.PreconditionRefusal(false, true, true).Code);
            Assert.Equal("no_central_path", SyncWithCentralRules.PreconditionRefusal(false, false, false).Code);
            Assert.Null(SyncWithCentralRules.PreconditionRefusal(false, false, true));
            Assert.Null(SyncWithCentralRules.PreconditionRefusal(null, null, null));
        }

        [Fact]
        public void Off_by_default_and_the_refusal_names_how_the_owner_enables_it()
        {
            var r = SyncWithCentralRules.AuthorisationRefusal(false, false, @"C:\x\settings.json");
            Assert.Equal("sync_not_authorised", r.Code);
            Assert.Contains("Advanced options > Synchronize with central", r.Message);
            Assert.Contains("full_write", r.Message);
            Assert.Contains(@"C:\x\settings.json", r.Message);
        }

        [Fact]
        public void The_workshared_read_only_policy_wins_over_the_grant_and_names_its_real_label()
        {
            var r = SyncWithCentralRules.AuthorisationRefusal(true, true, "s");
            Assert.Equal("force_read_only_on_workshared", r.Code);
            Assert.Contains("Advanced options > " + SyncWithCentralRules.ProtectSharedModelsLabel, r.Message);
            Assert.Null(SyncWithCentralRules.AuthorisationRefusal(true, false, "s"));
        }

        private static SyncRefusal Py(bool granted, bool protectedShared, params string[] maskedSources) =>
            SyncWithCentralRules.PythonSyncRefusal(maskedSources, granted, protectedShared);

        [Fact]
        public void A_python_sync_is_refused_while_the_owner_switch_is_off()
        {
            string code = "doc.SynchronizeWithCentral(t, o)";
            SyncRefusal off = Py(false, false, code);
            Assert.Equal("sync_not_authorised", off.Code);
            Assert.Contains("OFF on this machine", off.Message);
            Assert.Null(Py(true, false, code));
            Assert.Null(Py(false, false, "WorksharingUtils.RelinquishOwnership(doc, r, t)"));
            Assert.Null(Py(false, false, "x = 1  #                                "));
            // The options type alone is not a sync.
            Assert.Null(Py(false, false, "o = SynchronizeWithCentralOptions()"));
        }

        [Theory]
        [InlineData("s = doc.SynchronizeWithCentral\ns(t, o)")]
        [InlineData("uiapp.PostCommand(RevitCommandId.LookupPostableCommandId(PostableCommand.SynchronizeNow))")]
        [InlineData("cmd = PostableCommand.SynchronizeAndModifySettings")]
        public void An_aliased_or_posted_sync_is_caught_too(string masked)
        {
            Assert.Equal("sync_not_authorised", Py(false, false, masked).Code);
        }

        [Fact]
        public void A_sync_inside_an_include_is_refused_like_one_in_the_main_script()
        {
            SyncRefusal r = Py(false, false, "go(doc)", "def go(d):\n    d.SynchronizeWithCentral(t, o)");
            Assert.NotNull(r);
            Assert.Equal("sync_not_authorised", r.Code);
        }

        [Fact]
        public void For_python_the_workshared_protection_wins_over_the_grant()
        {
            SyncRefusal r = Py(true, true, "for d in app.Documents: d.SynchronizeWithCentral(t, o)");
            Assert.Equal("force_read_only_on_workshared", r.Code);
            Assert.Contains(SyncWithCentralRules.ProtectSharedModelsLabel, r.Message);
            Assert.Equal("force_read_only_on_workshared", Py(false, true, "doc.SynchronizeWithCentral(t, o)").Code);
            // No sync in the script: neither switch matters.
            Assert.Null(Py(false, true, "print(doc.Title)"));
        }

        [Fact]
        public void A_local_opened_from_a_read_only_file_is_refused_before_the_call()
        {
            Assert.Equal("local_file_read_only", SyncWithCentralRules.PreconditionRefusal(false, false, true, true).Code);
            Assert.Null(SyncWithCentralRules.PreconditionRefusal(false, false, true, false));
        }

        [Fact]
        public void A_precondition_throw_is_nothing_moved_only_when_the_re_read_proves_it()
        {
            Assert.False(SyncWithCentralRules.WriteStartedAfterPreconditionThrow(true, true, true, false));
            // The central file's write time advanced: the central was written.
            Assert.True(SyncWithCentralRules.WriteStartedAfterPreconditionThrow(true, true, true, true));
            // IsModified went true -> false: the local was saved.
            Assert.True(SyncWithCentralRules.WriteStartedAfterPreconditionThrow(true, true, false, false));
            Assert.True(SyncWithCentralRules.WriteStartedAfterPreconditionThrow(false, false, false, false));
            // A server central (write time not measurable): unknown, never "nothing moved".
            Assert.Null(SyncWithCentralRules.WriteStartedAfterPreconditionThrow(true, false, false, null));
            Assert.Null(SyncWithCentralRules.WriteStartedAfterPreconditionThrow(null, false, false, false));
            Assert.True(SyncWithCentralRules.OwnershipUnchanged(1, U("a"), 1, U("a"), 0));
            Assert.False(SyncWithCentralRules.OwnershipUnchanged(1, U("a"), 0, U(), 0));
            Assert.Null(SyncWithCentralRules.OwnershipUnchanged(1, U("a"), 1, U("a"), 2));
        }

        [Fact]
        public void None_does_not_count_elements_the_reload_brought_into_an_owned_workset()
        {
            // "n" arrived with the reload (absent from the before census) into a still-owned workset.
            var v = SyncWithCentralRules.VerifyOwnership(SyncRelinquish.None, 1, U("a"), U(),
                1, U("a", "n"), 0, _ => true, knownBefore: U("a", "x"), borrowedAfter: U());
            Assert.True(v.Verified);
            Assert.Equal(U("n"), v.ArrivedOwned);
            // An arrival that reads BORROWED is not explained by an owned workset.
            var b = SyncWithCentralRules.VerifyOwnership(SyncRelinquish.None, 1, U("a"), U(),
                1, U("a", "n"), 0, _ => true, knownBefore: U("a"), borrowedAfter: U("n"));
            Assert.False(b.Verified);
            Assert.Equal(U("n"), b.UnexpectedlyOwned);
            // relinquish=all releases every workset: an owned arrival stays unexpected.
            var all = SyncWithCentralRules.VerifyOwnership(SyncRelinquish.All, 1, U("a"), U(),
                0, U("n"), 0, _ => true, knownBefore: U("a"), borrowedAfter: U());
            Assert.False(all.Verified);
        }

        [Fact]
        public void A_later_sync_by_someone_else_is_unmeasured_not_a_failure()
        {
            var moved = SyncWithCentralRules.VerifyUpdates(new Dictionary<string, string>
            { ["a"] = "CurrentWithCentral", ["b"] = "UpdatedInCentral", ["c"] = "DeletedInCentral" });
            Assert.Null(moved.Verified);
            Assert.Equal(U("b", "c"), moved.MovedInCentral);
            Assert.Empty(moved.UnexpectedlyOwned);
        }

        [Fact]
        public void The_sample_is_deterministic_spread_plus_borrowed_then_newest_owned()
        {
            var all = Enumerable.Range(1, 1000).Select(i => (long)i).ToList();
            var borrowed = new long[] { 5 };
            var owned = new long[] { 3, 5, 7, 998, 999 };
            var a = SyncWithCentralRules.Sample(all, borrowed, owned, 10, 3);
            var b = SyncWithCentralRules.Sample(all.AsEnumerable().Reverse(), borrowed, owned.AsEnumerable().Reverse(), 10, 3);
            Assert.Equal(a, b);
            Assert.Contains(1L, a);     // the spread
            Assert.Contains(5L, a);     // borrowed first
            Assert.Contains(999L, a);   // then the newest owned
            Assert.Contains(998L, a);
            Assert.DoesNotContain(3L, a); // the oldest owned are not where a local's changes live
            Assert.True(a.Count <= 13);
        }

        [Fact]
        public void The_token_binds_the_request_as_plan_and_the_estimate_as_model_fingerprint()
        {
            Assert.Equal(new[] { "document", "comment", "relinquish", "compact", "accept_findings_ids" }, SyncWithCentralRules.RequestFields);
            foreach (string field in new[] { "owned_worksets", "owned_elements", "borrowed_elements", "is_modified",
                                             "has_all_changes_from_central", "sample_status_counts" })
            {
                Assert.Contains(field, SyncWithCentralRules.ModelFields);
                Assert.Contains(field, SyncWithCentralRules.EstimateFields);
            }
            Assert.Empty(SyncWithCentralRules.RequestFields.Intersect(SyncWithCentralRules.ModelFields));
        }

        [Fact]
        public void Estimate_drift_names_the_fields_that_moved()
        {
            var approved = new Dictionary<string, string> { ["owned_elements"] = "10", ["is_modified"] = "false" };
            var now = new Dictionary<string, string> { ["owned_elements"] = "12", ["is_modified"] = "false" };
            string d = SyncWithCentralRules.DescribeEstimateDrift(approved, now);
            Assert.Contains("owned_elements 10 -> 12", d);
            Assert.DoesNotContain("is_modified", d);
            Assert.Null(SyncWithCentralRules.DescribeEstimateDrift(approved, approved));
            Assert.Null(SyncWithCentralRules.DescribeEstimateDrift(null, now));
        }

        [Fact]
        public void Relinquish_all_holds_only_when_nothing_is_left_owned()
        {
            var ok = SyncWithCentralRules.VerifyOwnership(SyncRelinquish.All, 2, U("a", "b"), U("b"),
                0, U(), 0, _ => true);
            Assert.True(ok.Verified);

            var bad = SyncWithCentralRules.VerifyOwnership(SyncRelinquish.All, 2, U("a", "b"), U("b"),
                0, U("b"), 0, _ => true);
            Assert.False(bad.Verified);
            Assert.Equal(U("b"), bad.UnexpectedlyOwned);
        }

        [Fact]
        public void Keep_borrowed_keeps_exactly_the_borrowed_that_still_exist()
        {
            var ok = SyncWithCentralRules.VerifyOwnership(SyncRelinquish.KeepBorrowed, 1, U("a", "b", "c"),
                U("b", "c"), 0, U("b"), 0, uid => uid != "c");
            Assert.True(ok.Verified);

            var released = SyncWithCentralRules.VerifyOwnership(SyncRelinquish.KeepBorrowed, 1, U("a", "b"),
                U("b"), 0, U(), 0, _ => true);
            Assert.False(released.Verified);
            Assert.Equal(U("b"), released.UnexpectedlyReleased);

            var worksetKept = SyncWithCentralRules.VerifyOwnership(SyncRelinquish.KeepBorrowed, 1, U("b"),
                U("b"), 1, U("b"), 0, _ => true);
            Assert.False(worksetKept.Verified);
        }

        [Fact]
        public void A_renumbered_element_is_the_same_element_because_keys_are_unique_ids()
        {
            // The borrowed element had id 1000 before and 2000 after; its UniqueId did not move,
            // so a wrongly released borrowed element is caught instead of dropping out of "expected".
            var released = SyncWithCentralRules.VerifyOwnership(SyncRelinquish.KeepBorrowed, 0, U("uid-new"),
                U("uid-new"), 0, U(), 0, uid => uid == "uid-new");
            Assert.False(released.Verified);
            Assert.Equal(U("uid-new"), released.UnexpectedlyReleased);
        }

        [Fact]
        public void None_keeps_worksets_and_owned_elements()
        {
            var ok = SyncWithCentralRules.VerifyOwnership(SyncRelinquish.None, 3, U("a", "b"), U("b"),
                3, U("a", "b"), 0, _ => true);
            Assert.True(ok.Verified);
            var lost = SyncWithCentralRules.VerifyOwnership(SyncRelinquish.None, 3, U("a", "b"), U("b"),
                0, U("a", "b"), 0, _ => true);
            Assert.False(lost.Verified);
        }

        [Fact]
        public void Unreadable_ownership_is_unmeasured_not_a_pass()
        {
            var v = SyncWithCentralRules.VerifyOwnership(SyncRelinquish.All, 1, U(), U(), null, U(), 0, _ => true);
            Assert.Null(v.Verified);
            var w = SyncWithCentralRules.VerifyOwnership(SyncRelinquish.All, 1, U(), U(), 0, U(), 4, _ => true);
            Assert.Null(w.Verified);
        }

        [Fact]
        public void After_a_sync_every_sampled_present_element_is_current()
        {
            var ok = SyncWithCentralRules.VerifyUpdates(new Dictionary<string, string>
            { ["a"] = "CurrentWithCentral", ["b"] = null });
            Assert.True(ok.Verified);

            var stale = SyncWithCentralRules.VerifyUpdates(new Dictionary<string, string>
            { ["a"] = "CurrentWithCentral", ["b"] = "NotYetInCentral" });
            Assert.False(stale.Verified);
            Assert.Equal(U("b"), stale.UnexpectedlyOwned);

            var unread = SyncWithCentralRules.VerifyUpdates(new Dictionary<string, string>
            { ["a"] = "CurrentWithCentral", ["b"] = SyncWithCentralRules.Unreadable });
            Assert.Null(unread.Verified);
        }

        [Fact]
        public void A_sync_that_threw_is_never_verified_whatever_the_re_read_says()
        {
            var problems = new List<string>();
            Assert.False(SyncWithCentralRules.OverallVerdict(true, true, true, true, false, true, problems));
            Assert.NotEmpty(problems);
        }

        [Fact]
        public void Verified_needs_the_document_current_with_central_and_a_witnessed_save()
        {
            Assert.True(SyncWithCentralRules.OverallVerdict(false, true, true, true, false, null, new List<string>()));
            Assert.True(SyncWithCentralRules.OverallVerdict(false, true, true, true, true, true, new List<string>()));
            // HasAllChangesFromCentral()==false describes the central now (a later sync by someone
            // else makes it false): unmeasured, never a contradiction of this sync.
            Assert.Null(SyncWithCentralRules.OverallVerdict(false, true, true, false, false, true, new List<string>()));
            Assert.Null(SyncWithCentralRules.OverallVerdict(false, true, true, null, false, true, new List<string>()));
            // Neither witness measurable: unmeasured, never a pass.
            Assert.Null(SyncWithCentralRules.OverallVerdict(false, true, true, true, null, null, new List<string>()));
            // Both witnesses measured and both negative: contradicted.
            Assert.False(SyncWithCentralRules.OverallVerdict(false, true, true, true, true, false, new List<string>()));
            Assert.False(SyncWithCentralRules.OverallVerdict(false, false, true, true, false, true, new List<string>()));
        }

        [Theory]
        [InlineData("Autodesk.Revit.Exceptions.InvalidOperationException", SyncFailureKind.NotStarted)]
        [InlineData("Autodesk.Revit.Exceptions.ArgumentNullException", SyncFailureKind.NotStarted)]
        [InlineData("Autodesk.Revit.Exceptions.CentralModelContentionException", SyncFailureKind.CentralLocked)]
        [InlineData("Autodesk.Revit.Exceptions.CentralFileCommunicationException", SyncFailureKind.Unknown)]
        [InlineData("Autodesk.Revit.Exceptions.CentralModelException", SyncFailureKind.Unknown)]
        [InlineData("System.InvalidOperationException", SyncFailureKind.Unknown)]
        public void Thrown_syncs_are_classified_by_exception_family(string type, SyncFailureKind expected)
        {
            Assert.Equal(expected, SyncWithCentralRules.ClassifyFailure(type));
        }

        [Fact]
        public void Status_counts_are_ordinal_and_name_gone_elements()
        {
            var c = SyncWithCentralRules.Counts(new[] { "b", "a", null, "a" });
            Assert.Equal(new[] { "a", "b", "gone" }, c.Keys.ToArray());
            Assert.Equal(2, c["a"]);
        }
    }
}
