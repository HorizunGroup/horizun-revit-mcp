// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// Workshared models: work locally, synchronize once at the end. The session ledger that
// lists what a sync would carry, blocks it on failed or unmeasured checks, clears a
// blocker on a clean re-check, and reminds while local work is unsynced.
// -----------------------------------------------------------------------------
using System;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class SyncLedgerRulesTests
    {
        [Fact]
        public void Successful_event_with_unreadable_central_or_pending_local_changes_keeps_ledger()
        {
            string key = Guid.NewGuid().ToString();
            var start = DateTime.UtcNow;
            SyncLedgerRules.RecordWrite(key, new SyncLedgerEntry { Tool = "write", AtUtc = start.AddSeconds(-1) });
            SyncLedgerRules.BeginSynchronization(key, start);
            SyncLedgerRules.EndSynchronization(key, true, start.AddSeconds(1), null, false);
            Assert.Single(SyncLedgerRules.Writes(key));
            SyncLedgerRules.BeginSynchronization(key, start);
            SyncLedgerRules.EndSynchronization(key, true, start.AddSeconds(2), true, true);
            Assert.Single(SyncLedgerRules.Writes(key));
        }

        [Fact]
        public void Verified_command_sync_preserves_writes_after_its_start()
        {
            string key = Guid.NewGuid().ToString();
            var start = DateTime.UtcNow;
            SyncLedgerRules.RecordWrite(key, new SyncLedgerEntry { Tool = "before", AtUtc = start.AddSeconds(-1) });
            SyncLedgerRules.RecordWrite(key, new SyncLedgerEntry { Tool = "after", AtUtc = start.AddSeconds(1) });
            SyncLedgerRules.MarkSyncedThrough(key, start, start.AddSeconds(2));
            Assert.Equal("after", Assert.Single(SyncLedgerRules.Writes(key)).Tool);
            Assert.Equal("horizun", SyncLedgerRules.LastSyncBy(key));
        }
        [Fact]
        public void Manual_sync_clears_only_the_successful_document_and_later_writes_remain_pending()
        {
            string a = Guid.NewGuid().ToString(), b = Guid.NewGuid().ToString();
            SyncLedgerRules.RecordWrite(a, W("write", "clean", 0, null, 1));
            SyncLedgerRules.RecordWrite(b, W("write", "clean", 0, null, 2));
            SyncLedgerRules.ObserveSynchronization(a, false, DateTime.UtcNow);
            Assert.Single(SyncLedgerRules.Writes(a));
            SyncLedgerRules.ObserveSynchronization(a, true, DateTime.UtcNow);
            Assert.Empty(SyncLedgerRules.Writes(a));
            Assert.Single(SyncLedgerRules.Writes(b));
            SyncLedgerRules.RecordWrite(a, W("later", "clean", 0, null, 3));
            Assert.Single(SyncLedgerRules.Writes(a));
        }

        [Fact]
        public void A_sync_event_does_not_erase_entries_recorded_after_its_cutoff()
        {
            string key = Guid.NewGuid().ToString();
            DateTime cutoff = DateTime.UtcNow;
            var old = W("old", "clean", 0, null, 1);
            old.AtUtc = cutoff.AddTicks(-1);
            var later = W("later", "clean", 0, null, 2);
            later.AtUtc = cutoff.AddTicks(1);
            SyncLedgerRules.RecordWrite(key, old);
            SyncLedgerRules.RecordWrite(key, later);
            SyncLedgerRules.ObserveSynchronization(key, true, cutoff);
            Assert.Equal("later", Assert.Single(SyncLedgerRules.Writes(key)).Tool);
            Assert.NotNull(SyncLedgerRules.PendingBlock(key, true, false));
        }
        [Fact]
        public void Sync_completion_uses_start_cutoff_and_ignores_unmatched_or_failed_events()
        {
            string key = Guid.NewGuid().ToString();
            DateTime start = DateTime.UtcNow;
            var before = W("before", "clean", 0, null, 1); before.AtUtc = start.AddTicks(-1);
            var during = W("during", "clean", 0, null, 2); during.AtUtc = start.AddTicks(1);
            SyncLedgerRules.RecordWrite(key, before);
            SyncLedgerRules.RecordWrite(key, during);
            SyncLedgerRules.EndSynchronization(key, true, start.AddSeconds(1));
            Assert.Equal(2, SyncLedgerRules.Writes(key).Count);
            SyncLedgerRules.BeginSynchronization(key, start);
            SyncLedgerRules.EndSynchronization(key, false, start.AddSeconds(1));
            Assert.Equal(2, SyncLedgerRules.Writes(key).Count);
            SyncLedgerRules.BeginSynchronization(key, start);
            SyncLedgerRules.EndSynchronization(key, true, start.AddSeconds(2));
            Assert.Equal("during", Assert.Single(SyncLedgerRules.Writes(key)).Tool);
            Assert.Equal(start.AddSeconds(2), SyncLedgerRules.LastSyncUtc(key));
        }
        [Fact]
        public void Manual_sync_seen_from_revit_clears_the_reminder_and_says_who_synchronized()
        {
            string key = Guid.NewGuid().ToString();
            DateTime start = DateTime.UtcNow;
            var w = W("tool", "clean", 0, null, 1); w.AtUtc = start.AddTicks(-1);
            SyncLedgerRules.RecordWrite(key, w);
            Assert.NotNull(SyncLedgerRules.PendingBlock(key, true, false));
            SyncLedgerRules.BeginSynchronization(key, start);
            SyncLedgerRules.EndSynchronization(key, true, start.AddSeconds(3));
            Assert.Null(SyncLedgerRules.PendingBlock(key, true, false));
            Assert.Equal("revit_event", SyncLedgerRules.LastSyncBy(key));
            SyncLedgerRules.RecordWrite(key, W("later", "clean", 0, null, 2));
            Assert.Equal("revit_event", (string)SyncLedgerRules.PendingBlock(key, true, false)["last_sync_by"]);
            SyncLedgerRules.MarkSynced(key, DateTime.UtcNow);
            Assert.Equal("horizun", SyncLedgerRules.LastSyncBy(key));
        }
        [Theory]
        [InlineData("partial")]
        [InlineData("not_measured")]
        public void Incomplete_spatial_checks_block_sync(string status)
        {
            SyncLedgerRules.ClearForTests();
            SyncLedgerRules.RecordWrite("c3-incomplete", W("write", status, 0, null, 1));
            Assert.Single(SyncLedgerRules.Blockers("c3-incomplete"));
        }

        [Fact]
        public void History_limit_must_not_evict_unresolved_blockers()
        {
            SyncLedgerRules.ClearForTests();
            SyncLedgerRules.RecordWrite("c3-cap", W("write", "conflicts", 1, null, 1));
            for (int i = 0; i < SyncLedgerRules.HistoryCap; i++)
                SyncLedgerRules.RecordWrite("c3-cap", W("write", "clean", 0, null, i + 2));
            Assert.Single(SyncLedgerRules.Blockers("c3-cap"));
        }

        [Fact]
        public void Warning_with_partial_coverage_blocks_and_cannot_be_cleared_by_partial_check()
        {
            SyncLedgerRules.ClearForTests();
            var entry = W("write", "warnings", 0, null, 1);
            entry.SpatialPartial = true;
            SyncLedgerRules.RecordWrite("c3-warning", entry);
            Assert.Single(SyncLedgerRules.Blockers("c3-warning"));
            Assert.False(SyncLedgerRules.IsCleanSpatialCheck(JObject.Parse("{\"status\":\"warnings\",\"partial\":true}")));
        }

        [Fact]
        public void Failed_result_with_committed_changes_records_unverified_blocker()
        {
            var result = JObject.Parse("{\"changes_applied\":true}");
            Assert.True(SyncLedgerRules.ShouldRecordResult(false, result));
            var entry = SyncLedgerRules.FromResult("stairs", false, result, new long[] { 12 });
            Assert.NotNull(entry.BlockReason);
            Assert.False(SyncLedgerRules.ShouldRecordResult(false, JObject.Parse("{\"changes_applied\":false}")));
        }

        [Fact]
        public void Spatial_recheck_cannot_clear_a_failed_postcommit_verification()
        {
            SyncLedgerRules.ClearForTests();
            var entry = SyncLedgerRules.FromResult("stairs", false, JObject.Parse("{\"changes_applied\":true}"), new long[] { 12 });
            SyncLedgerRules.RecordWrite("postcommit", entry);
            Assert.Equal(0, SyncLedgerRules.ClearVerified("postcommit", new long[] { 12 }, false));
            Assert.Single(SyncLedgerRules.Blockers("postcommit"));
        }

        private static SyncLedgerEntry W(string tool, string spatial, int errors = 0, string evidence = null, params long[] ids) =>
            new SyncLedgerEntry { Tool = tool, AtUtc = DateTime.UtcNow, SpatialStatus = spatial, SpatialErrors = errors, EvidenceStatus = evidence, Ids = ids };

        [Fact]
        public void Int10_pending_sync_separates_unverified_python_from_failed_checks()
        {
            SyncLedgerRules.ClearForTests();
            SyncLedgerRules.RecordWrite("k", W("horizun_transform_elements", "conflicts", 2, null, 10));
            SyncLedgerRules.RecordWrite("k", W("horizun_create_elements", "not_measured", 0, null, 11));
            SyncLedgerRules.RecordWrite("k", W("horizun_execute_python", null, 0, "completed_unverified", 20));
            SyncLedgerRules.RecordWrite("k", W("horizun_execute_python", null, 0, "self_reported_verified", 30));
            JObject b = SyncLedgerRules.PendingBlock("k", true, true);
            Assert.Equal(3, (int)b["sync_blockers"]);
            Assert.Equal(1, (int)b["blockers_unverified_python"]);
            Assert.Equal(2, (int)b["blockers_failed_checks"]);
            JArray rows = (JArray)SyncLedgerRules.SessionChanges("k")["blockers"];
            Assert.Equal(new[] { "failed_check", "failed_check", "unverified_python" }, rows.Select(r => (string)r["kind"]).ToArray());
            SyncLedgerRules.ClearForTests();
        }

        [Fact]
        public void A_clean_write_is_pending_but_does_not_block()
        {
            SyncLedgerRules.ClearForTests();
            SyncLedgerRules.RecordWrite("k", W("horizun_create_elements", "clean", 0, null, 1, 2));
            Assert.Empty(SyncLedgerRules.Blockers("k"));
            JObject b = SyncLedgerRules.PendingBlock("k", true, true);
            Assert.True((bool)b["local_changes_unsynced"]);
            Assert.Equal(1, (int)b["writes_since_sync"]);
            Assert.Equal(0, (int)b["sync_blockers"]);
            Assert.Contains("sync_with_central", (string)b["reminder"]);
            SyncLedgerRules.ClearForTests();
        }

        [Fact]
        public void Conflicts_and_unverified_scripts_block_and_a_clean_check_clears_what_it_covers()
        {
            SyncLedgerRules.ClearForTests();
            SyncLedgerRules.RecordWrite("k", W("horizun_transform_elements", "conflicts", 2, null, 10, 11));
            SyncLedgerRules.RecordWrite("k", W("horizun_execute_python", null, 0, "completed_unverified", 20));
            SyncLedgerRules.RecordWrite("k", W("horizun_execute_python", null, 0, "self_reported_verified", 30));
            Assert.Equal(new[] { "spatial_check_conflicts", "script_not_verified:completed_unverified" },
                         SyncLedgerRules.Blockers("k").Select(e => e.BlockReason).ToArray());
            Assert.Equal(0, SyncLedgerRules.ClearVerified("k", new long[] { 10 }, false)); // partial cover clears nothing
            Assert.Equal(1, SyncLedgerRules.ClearVerified("k", new long[] { 10, 11, 99 }, false));
            Assert.Single(SyncLedgerRules.Blockers("k"));
            Assert.Equal(1, SyncLedgerRules.ClearVerified("k", null, true));
            Assert.Empty(SyncLedgerRules.Blockers("k"));
            SyncLedgerRules.ClearForTests();
        }

        [Fact]
        public void A_sync_ends_the_reminder_and_a_non_workshared_model_never_gets_one()
        {
            SyncLedgerRules.ClearForTests();
            SyncLedgerRules.RecordWrite("k", W("horizun_write_params_verified", "clean", 0, null, 5));
            Assert.Null(SyncLedgerRules.PendingBlock("k", false, true));
            DateTime t = new DateTime(2026, 10, 27, 12, 0, 0, DateTimeKind.Utc);
            SyncLedgerRules.MarkSynced("k", t);
            Assert.Null(SyncLedgerRules.PendingBlock("k", true, false));
            Assert.Equal(t, SyncLedgerRules.LastSyncUtc("k"));
            // Revit still reports unsaved changes (an edit by hand): reminded, with no session writes.
            JObject b = SyncLedgerRules.PendingBlock("k", true, true);
            Assert.Equal(0, (int)b["writes_since_sync"]);
            SyncLedgerRules.ClearForTests();
        }

        [Fact]
        public void The_sync_estimate_lists_session_writes_blockers_and_what_cannot_be_known()
        {
            SyncLedgerRules.ClearForTests();
            SyncLedgerRules.RecordWrite("k", W("horizun_create_elements", "clean", 0, null, 1, 2));
            SyncLedgerRules.RecordWrite("k", W("horizun_create_elements", "conflicts", 1, null, 2, 3));
            JObject c = SyncLedgerRules.SessionChanges("k");
            Assert.Equal(2, (int)c["writes"]);
            Assert.Equal(3, (int)c["elements_touched"]);
            Assert.Equal(2, (int)c["by_tool"]["horizun_create_elements"]);
            Assert.Single((JArray)c["blockers"]);
            Assert.Contains("does not list", (string)c["not_knowable"]);
            SyncLedgerRules.ClearForTests();
        }
    }
}
