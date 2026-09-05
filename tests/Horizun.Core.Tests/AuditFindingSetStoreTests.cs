// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// The record an apply reads instead of the caller's copy of the findings.
// -----------------------------------------------------------------------------
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class AuditFindingSetStoreTests
    {
        private static JArray Findings()
        {
            return new JArray(
                new JObject
                {
                    ["check"] = "unpinned_links", ["finding_id"] = "f:1", ["is_issue"] = true,
                    ["shown"] = 2, ["total"] = 2,
                    ["items"] = new JArray(new JObject { ["element_id"] = "10" }, new JObject { ["element_id"] = "11" })
                },
                new JObject
                {
                    ["check"] = "rooms", ["finding_id"] = "f:2", ["is_issue"] = true,
                    ["shown"] = 1, ["total"] = 40,
                    ["items"] = new JArray(new JObject { ["id"] = "20", ["problem_code"] = "unplaced" })
                },
                new JObject
                {
                    ["check"] = "warnings", ["finding_id"] = "f:3", ["is_issue"] = false,
                    ["shown"] = 0, ["total"] = 0, ["items"] = new JArray()
                });
        }

        [Fact]
        public void The_record_reads_ids_scope_and_truncation_off_the_published_findings()
        {
            FindingSetRecord r = FindingSetRecord.From("fs:x", "Tower", "doc-1", 20, "2026-01-01T00:00:00Z", Findings());

            Assert.Equal(3, r.Findings.Count);
            RecordedFinding links = r.Find("f:1");
            Assert.Equal("unpinned_links", links.Check);
            Assert.True(links.IsIssue);
            Assert.False(links.Truncated);
            Assert.Equal(new long[] { 10, 11 }, links.ElementIds.ToArray());

            // shown 1 of 40: the scope is unknown.
            Assert.True(r.Find("f:2").Truncated);
            Assert.False(r.Find("f:3").IsIssue);
            Assert.Null(r.Find("f:nope"));
            Assert.Equal("f:2", r.FindByCheck("rooms").FindingId);
        }

        [Fact]
        public void A_recorded_set_is_found_by_its_fingerprint_and_an_unknown_one_is_not()
        {
            var store = new AuditFindingSetStore();
            store.Record(FindingSetRecord.From("fs:a", "Tower", "doc-1", 20, null, Findings()));

            FindingSetRecord got;
            Assert.True(store.TryGet("fs:a", out got));
            Assert.Equal("doc-1", got.DocumentFingerprint);
            Assert.False(store.TryGet("fs:b", out got));
            Assert.False(store.TryGet(null, out got));
        }

        [Fact]
        public void Re_recording_a_fingerprint_replaces_it_and_the_store_is_bounded()
        {
            var store = new AuditFindingSetStore();
            for (int i = 0; i < AuditFindingSetStore.Capacity + 10; i++)
                store.Record(FindingSetRecord.From("fs:" + i, "T", "d", 20, null, new JArray()));
            Assert.Equal(AuditFindingSetStore.Capacity, store.Count);
            FindingSetRecord got;
            // The oldest went; the newest stayed.
            Assert.False(store.TryGet("fs:0", out got));
            Assert.True(store.TryGet("fs:" + (AuditFindingSetStore.Capacity + 9), out got));

            store.Record(FindingSetRecord.From("fs:5", "T", "d", 99, null, new JArray()));
            Assert.True(store.TryGet("fs:5", out got));
            Assert.Equal(99, got.Top);
            Assert.Equal(AuditFindingSetStore.Capacity, store.Count);
        }
    }
}
