using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Horizun.Core.Tests;
using Horizun.Revit.Commands;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Sync.Integration.Tests
{
    public class SyncRefusalTests
    {
        [Theory]
        [InlineData("partial")]
        [InlineData("not_measured")]
        public void Actual_sync_refuses_incomplete_spatial_check_before_and_after_501_writes(string status)
        {
            RailingSyncScenario.ExerciseSpatial(status, AssertSyncRefused);
        }

        // INT-6 m-12: the private data root of the run is deleted when the run ends - that root, never a pattern over %TEMP%.
        [Fact]
        public void The_private_data_root_goes_with_the_run_and_nothing_else_in_temp_does()
        {
            Assert.True(SyncSettings.RootExists);
            string other = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "hz-sync-tests-someone-elses-" + System.Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(other);
            try
            {
                SyncSettings.Cleanup();
                Assert.False(SyncSettings.RootExists);
                Assert.True(System.IO.Directory.Exists(other));
            }
            finally
            {
                try { System.IO.Directory.Delete(other, true); } catch { }
                SyncSettings.Init();   // the tests after this one still need their root
            }
            Assert.True(SyncSettings.RootExists);
        }

        [Fact]
        public void Actual_sync_refuses_a_post_commit_exception()
        {
            RailingSyncScenario.ExercisePostCommit(AssertSyncRefused);
        }

        // M10. The settings file is shared process state, so these tests live in this class (xunit runs a
        // class sequentially) and always restore the defaults.
        private static CommandResult SyncRun(Document doc, JObject extra)
        {
            var app = new UIApplication { ActiveUIDocument = new UIDocument { Document = doc } };
            var request = new JObject { ["operation"] = "sync_with_central", ["target_document"] = doc.Title };
            request.Merge(extra);
            return new SaveDocumentCommand().Execute(app, request.ToString());
        }

        private static JObject Sync(Document doc, JObject extra)
        {
            CommandResult result = SyncRun(doc, extra);
            Assert.False(result.Success);
            return result.Detail;
        }

        private static void WithSyncOnly(bool flag, string profile, System.Action body)
        {
            SyncSettings.Set(flag, profile);
            try { body(); }
            finally { SyncSettings.Set(false, "full_write"); }
        }

        [Fact]
        public void Sync_only_flag_on_refuses_accept_findings()
        {
            RailingSyncScenario.ExercisePostCommit(doc =>
            {
                var accept = new JObject { ["comment"] = "c", ["accept_findings"] = true };
                // Flag on + safe_write: the strict precheck refuses it before anything is measured.
                WithSyncOnly(true, "safe_write", () => Assert.Equal("sync_only_no_accept_findings", (string)Sync(doc, accept)["code"]));
            });
        }

        [Fact]
        public void Sync_only_flag_on_refuses_unverified_writes_and_never_reaches_the_central()
        {
            RailingSyncScenario.ExercisePostCommit(doc =>
                WithSyncOnly(true, "safe_write", () =>
                {
                    CommandResult r = SyncRun(doc, new JObject { ["comment"] = "c" });
                    Assert.False(r.Success);
                    Assert.Equal("sync_blocked_unverified", (string)r.Detail["code"]);
                    Assert.False((bool)r.Detail["write_started"]);
                    Assert.Contains("accept_findings_ids", r.Error);
                    Assert.Contains("failed_check", r.Detail["blocker_kinds"].Values<string>());
                }));
        }

        [Fact]
        public void Sync_only_flag_on_refuses_a_relinquish_that_keeps_the_loan_and_flag_off_does_not()
        {
            RailingSyncScenario.ExercisePostCommit(doc =>
            {
                var keep = new JObject { ["comment"] = "c", ["relinquish"] = "none", ["accept_findings"] = false };
                WithSyncOnly(true, "safe_write", () => Assert.Equal("sync_only_relinquish", (string)Sync(doc, keep)["code"]));
                // Flag off: the blockers gate answers first, as it always did.
                WithSyncOnly(false, "full_write", () => Assert.Equal("sync_blocked_unverified", (string)Sync(doc, keep)["code"]));
            });
        }

        // ---- M10 scenarios through the real command, with a scripted host double ----
        // Elements 1-3 are borrowed by the current user (no workset is owned), 4 is held by another user, 5 is free.
        private static Document ScenarioDoc()
        {
            var doc = new Document { Title = System.Guid.NewGuid().ToString(), IsWorkshared = true, IsModified = true };
            for (long i = 1; i <= 5; i++) doc.Elements[i] = new Element { Id = new ElementId(i) };
            WorksharingUtils.Status.Clear();
            for (long i = 1; i <= 3; i++) WorksharingUtils.Status[i] = CheckoutStatus.OwnedByCurrentUser;
            WorksharingUtils.Status[4] = CheckoutStatus.OwnedByOtherUser;
            WorksharingUtils.Status[5] = CheckoutStatus.NotOwned;
            Document.UniqueIdLookup = (d, uid) => d.Elements.Values.FirstOrDefault(e => e.UniqueId == uid);
            // a faithful sync: gives back what the user borrowed and saves the local
            Document.SyncHook = d =>
            {
                for (long i = 1; i <= 3; i++) WorksharingUtils.Status[i] = CheckoutStatus.NotOwned;
                d.IsModified = false;
            };
            return doc;
        }

        private static void EndScenario()
        {
            Document.UniqueIdLookup = null; Document.SyncHook = null; WorksharingUtils.Status.Clear(); WorksharingUtils.UnreadableId = null;
        }

        private static JObject Req(string comment, string token = null, string relinquish = "all")
        {
            var r = new JObject { ["comment"] = comment, ["relinquish"] = relinquish };
            if (token != null) { r["dry_run"] = false; r["confirmation_token"] = token; }
            return r;
        }

        [Fact]
        public void Incomplete_loan_census_refuses_preview_and_keeps_ledger()
        {
            WithSyncOnly(true, "safe_write", () =>
            {
                Document doc = ScenarioDoc();
                string key = ChangeWatch.Key(doc);
                try
                {
                    SyncLedgerRules.RecordWrite(key, new SyncLedgerEntry { Tool = "write", AtUtc = System.DateTime.UtcNow, SpatialStatus = "clean" });
                    WorksharingUtils.UnreadableId = 2;
                    CommandResult result = SyncRun(doc, Req("check loans"));
                    Assert.False(result.Success);
                    Assert.Equal("ownership_census_incomplete", (string)result.Detail["code"]);
                    Assert.Single(SyncLedgerRules.Writes(key));
                }
                finally { SyncLedgerRules.MarkSynced(key, System.DateTime.UtcNow); EndScenario(); }
            });
        }

        [Fact]
        public void Unverified_sync_keeps_the_session_ledger()
        {
            WithSyncOnly(true, "safe_write", () =>
            {
                Document doc = ScenarioDoc();
                try
                {
                    string key = ChangeWatch.Key(doc);
                    SyncLedgerRules.RecordWrite(key, new SyncLedgerEntry { Tool = "test_write", AtUtc = System.DateTime.UtcNow, SpatialStatus = "clean", Ids = new long[] { 1 } });
                    Document.SyncHook = d =>
                    {
                        var now = System.DateTime.UtcNow;
                        SyncLedgerRules.BeginSynchronization(key, now);
                        SyncLedgerRules.EndSynchronization(key, true, now.AddMilliseconds(1), true, false);
                    };
                    string token = (string)((JObject)SyncRun(doc, Req("close levels")).Data)["confirmation_token"];
                    Assert.False(SyncRun(doc, Req("close levels", token)).Success);
                    Assert.Single(SyncLedgerRules.Writes(key));
                }
                finally { EndScenario(); }
            });
        }

        [Fact]
        public void Sync_only_preview_lists_what_the_user_holds_and_the_apply_re_reads_a_clean_sync()
        {
            WithSyncOnly(true, "safe_write", () =>
            {
                Document doc = ScenarioDoc();
                try
                {
                    CommandResult preview = SyncRun(doc, Req("close levels"));
                    Assert.True(preview.Success, preview.Error);
                    var data = (JObject)preview.Data;
                    JObject block = (JObject)data["sync_only"];
                    Assert.Equal(3, (int)block["borrowed"]["count"]);
                    Assert.Equal(3, ((JArray)block["borrowed"]["sample_unique_ids"]).Count);
                    Assert.Equal(1, (int)block["held_by_others"]);

                    CommandResult applied = SyncRun(doc, Req("close levels", (string)data["confirmation_token"]));
                    Assert.True(applied.Success, applied.Error);
                    var report = (JObject)applied.Data;
                    Assert.True((bool)report["sync_verified"]);
                    var post = (JObject)report["sync_only_postcheck"];
                    Assert.True((bool)post["verified"]);
                    Assert.Equal(0, (int)post["borrowed_elements_left"]);
                    Assert.False((bool)post["pending_local_changes"]);
                }
                finally { EndScenario(); }
            });
        }

        [Fact]
        public void Sync_only_apply_is_refused_when_the_comment_or_the_borrowed_list_changed_after_the_preview()
        {
            WithSyncOnly(true, "safe_write", () =>
            {
                Document doc = ScenarioDoc();
                try
                {
                    string token = (string)((JObject)SyncRun(doc, Req("close levels")).Data)["confirmation_token"];
                    CommandResult otherComment = SyncRun(doc, Req("something else", token));
                    Assert.False(otherComment.Success);
                    Assert.Equal("confirmation_rejected", (string)otherComment.Detail["code"]);

                    string token2 = (string)((JObject)SyncRun(doc, Req("close levels")).Data)["confirmation_token"];
                    WorksharingUtils.Status[5] = CheckoutStatus.OwnedByCurrentUser;   // one more element borrowed after the preview
                    CommandResult moved = SyncRun(doc, Req("close levels", token2));
                    Assert.False(moved.Success);
                    Assert.Equal("confirmation_rejected", (string)moved.Detail["code"]);
                }
                finally { EndScenario(); }
            });
        }

        [Theory]
        [InlineData("safe_write")]
        [InlineData("full_write")]
        [InlineData("unsafe_code")]
        public void Strict_profiles_bind_the_loan_list_and_never_apply_a_stale_preview(string profile)
        {
            WithSyncOnly(true, profile, () =>
            {
                Document doc = ScenarioDoc();
                try
                {
                    CommandResult preview = SyncRun(doc, Req("checked loans"));
                    Assert.True(preview.Success, preview.Error);
                    string token = (string)((JObject)preview.Data)["confirmation_token"];
                    bool called = false;
                    Document.SyncHook = d => called = true;
                    WorksharingUtils.Status[5] = CheckoutStatus.OwnedByCurrentUser;
                    CommandResult apply = SyncRun(doc, Req("checked loans", token));
                    Assert.False(apply.Success);
                    Assert.Equal("confirmation_rejected", (string)apply.Detail["code"]);
                    Assert.False(called);
                }
                finally { EndScenario(); }
            });
        }

        [Fact]
        public void Sync_only_re_read_reports_a_borrowed_leftover_and_a_pending_change_as_failures()
        {
            WithSyncOnly(true, "safe_write", () =>
            {
                Document doc = ScenarioDoc();
                try
                {
                    // a sync that "returns" but leaves element 3 borrowed and the local modified
                    Document.SyncHook = d => { WorksharingUtils.Status[1] = CheckoutStatus.NotOwned; WorksharingUtils.Status[2] = CheckoutStatus.NotOwned; };
                    string token = (string)((JObject)SyncRun(doc, Req("close levels")).Data)["confirmation_token"];
                    CommandResult applied = SyncRun(doc, Req("close levels", token));
                    Assert.False(applied.Success);
                    var report = (JObject)applied.Detail;
                    Assert.False((bool?)report["sync_verified"] ?? false);
                    var post = (JObject)report["sync_only_postcheck"];
                    Assert.False((bool?)post["verified"] ?? true);
                    Assert.Equal(1, (int)post["borrowed_elements_left"]);
                    Assert.True((bool)post["pending_local_changes"]);
                    Assert.Contains("still borrowed", post["problems"].ToString());
                }
                finally { EndScenario(); }
            });
        }

        [Fact]
        public void Sync_only_flag_off_has_no_sync_only_block_in_the_preview()
        {
            WithSyncOnly(false, "full_write", () =>
            {
                Document doc = ScenarioDoc();
                try
                {
                    CommandResult preview = SyncRun(doc, Req("close levels"));
                    Assert.True(preview.Success, preview.Error);
                    Assert.Null(((JObject)preview.Data)["sync_only"]);
                }
                finally { EndScenario(); }
            });
        }

        // ---- INT-11 D.5 through the real command: accept_findings accepts only unverified scripts ----

        private static Document FailedCheckDoc()
        {
            Document doc = ScenarioDoc();
            // The live case: two duplicated walls whose spatial_check found a conflict.
            SyncLedgerRules.RecordWrite(doc.Title, new SyncLedgerEntry
            {
                Tool = "horizun_create_elements", AtUtc = System.DateTime.UtcNow, Ids = new long[] { 1, 2 }, SpatialStatus = "conflicts", SpatialErrors = 1
            });
            return doc;
        }

        private static JObject Ids(JObject request, params long[] ids) { request["accept_findings_ids"] = new JArray(ids); return request; }

        [Fact]
        public void Int11_accept_findings_does_not_accept_a_failed_check_and_names_its_ids_and_both_ways_out()
        {
            WithSyncOnly(false, "full_write", () =>
            {
                Document doc = FailedCheckDoc();
                try
                {
                    JObject request = Req("close walls"); request["accept_findings"] = true;
                    CommandResult r = SyncRun(doc, request);
                    Assert.False(r.Success);
                    Assert.Equal("sync_blocked_unverified", (string)r.Detail["code"]);
                    Assert.False((bool)r.Detail["write_started"]);
                    Assert.False((bool)r.Detail["changes_applied"]);
                    Assert.Contains("1, 2", r.Error);
                    Assert.Contains("horizun_verify_changes", r.Error);
                    Assert.Contains("accept_findings_ids", r.Error);
                    JObject with = (JObject)r.Detail["accept_findings_with"];
                    Assert.Equal(new long[] { 1, 2 }, with["failed_checks"]["missing_ids"].Values<long>());
                    Assert.Equal("failed_check", (string)r.Detail["session_changes"]["blockers"][0]["kind"]);
                    // the apply is refused the same way, before anything reaches the central
                    request["dry_run"] = false; request["confirmation_token"] = "x";
                    Assert.Equal("sync_blocked_unverified", (string)SyncRun(doc, request).Detail["code"]);
                }
                finally { SyncLedgerRules.MarkSynced(doc.Title, System.DateTime.UtcNow); EndScenario(); }
            });
        }

        [Fact]
        public void Int11_accept_findings_ids_naming_every_id_previews_and_applies_and_incomplete_ids_are_refused()
        {
            WithSyncOnly(false, "full_write", () =>
            {
                Document doc = FailedCheckDoc();
                try
                {
                    CommandResult unknown = SyncRun(doc, Ids(Req("close walls"), 1, 2, 99));
                    Assert.False(unknown.Success);
                    Assert.Equal("invalid_accept_findings_ids", (string)unknown.Detail["code"]);
                    Assert.False((bool)unknown.Detail["write_started"]);
                    CommandResult partial = SyncRun(doc, Ids(Req("close walls"), 1));
                    Assert.False(partial.Success);
                    Assert.Equal("sync_blocked_unverified", (string)partial.Detail["code"]);
                    Assert.False((bool)partial.Detail["write_started"]);
                    Assert.Equal(new long[] { 2 }, partial.Detail["accept_findings_with"]["failed_checks"]["missing_ids"].Values<long>());
                    Assert.Contains("2", partial.Error);

                    CommandResult preview = SyncRun(doc, Ids(Req("close walls"), 2, 1));
                    Assert.True(preview.Success, preview.Error);
                    string token = (string)((JObject)preview.Data)["confirmation_token"];
                    CommandResult applied = SyncRun(doc, Ids(Req("close walls", token), 1, 2));
                    Assert.True(applied.Success, applied.Error);
                }
                finally { SyncLedgerRules.MarkSynced(doc.Title, System.DateTime.UtcNow); EndScenario(); }
            });
        }

        [Fact]
        public void Int11_the_token_is_bound_to_the_accepted_ids()
        {
            WithSyncOnly(false, "full_write", () =>
            {
                Document doc = FailedCheckDoc();
                try
                {
                    CommandResult preview = SyncRun(doc, Ids(Req("close walls"), 1, 2));
                    Assert.True(preview.Success, preview.Error);
                    string token = (string)((JObject)preview.Data)["confirmation_token"];
                    SyncLedgerRules.RecordWrite(doc.Title, new SyncLedgerEntry { Tool = "test", AtUtc = System.DateTime.UtcNow, Ids = new long[] { 3 }, SpatialStatus = "conflicts", SpatialErrors = 1 });
                    CommandResult other = SyncRun(doc, Ids(Req("close walls", token), 1, 2, 3));
                    Assert.False(other.Success);
                    Assert.Equal("confirmation_rejected", (string)other.Detail["code"]);
                }
                finally { SyncLedgerRules.MarkSynced(doc.Title, System.DateTime.UtcNow); EndScenario(); }
            });
        }

        [Fact]
        public void Int11_unverified_python_is_still_accepted_with_accept_findings()
        {
            WithSyncOnly(false, "full_write", () =>
            {
                Document doc = ScenarioDoc();
                SyncLedgerRules.RecordWrite(doc.Title, new SyncLedgerEntry
                {
                    Tool = "horizun_execute_python", AtUtc = System.DateTime.UtcNow, Ids = new long[] { 1 }, EvidenceStatus = "completed_unverified"
                });
                try
                {
                    JObject request = Req("scripted"); request["accept_findings"] = true;
                    CommandResult preview = SyncRun(doc, request);
                    Assert.True(preview.Success, preview.Error);
                    Assert.Equal(1, (int)((JObject)preview.Data)["accepted_findings"]);
                }
                finally { SyncLedgerRules.MarkSynced(doc.Title, System.DateTime.UtcNow); EndScenario(); }
            });
        }

        [Fact]
        public void Int11_the_strict_path_refuses_all_acceptance_and_blocks_the_failed_check()
        {
            WithSyncOnly(true, "safe_write", () =>
            {
                Document doc = FailedCheckDoc();
                try
                {
                    foreach (var request in new[] { Ids(Req("close walls"), 1, 2, 3), Ids(Req("close walls"), 1, 2), Ids(Req("close walls")) })
                    {
                        CommandResult refused = SyncRun(doc, request);
                        Assert.False(refused.Success);
                        Assert.Equal("sync_only_no_accept_findings", (string)refused.Detail["code"]);
                        Assert.False((bool)refused.Detail["write_started"]);
                    }
                    AssertSyncRefused(doc);
                }
                finally { SyncLedgerRules.MarkSynced(doc.Title, System.DateTime.UtcNow); EndScenario(); }
            });
        }

        private static void AssertSyncRefused(Document doc)
        {
            var app = new UIApplication { ActiveUIDocument = new UIDocument { Document = doc } };
            var request = new JObject { ["operation"] = "sync_with_central", ["target_document"] = doc.Title, ["comment"] = "test" };
            var result = new SaveDocumentCommand().Execute(app, request.ToString());
            Assert.False(result.Success);
            Assert.Equal("sync_blocked_unverified", (string)result.Detail["code"]);
            Assert.False((bool)result.Detail["write_started"]);
            Assert.False((bool)result.Detail["changes_applied"]);
        }
    }
}
