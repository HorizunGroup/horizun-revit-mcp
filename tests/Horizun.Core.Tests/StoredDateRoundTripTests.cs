// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// Audit 2026-10-08: four stores wrote timestamps as ISO 8601 text and read the
// file back with JObject.Parse, whose default DateParseHandling turns that text
// into a DateTime. Then:
//
//   * SnapshotStore re-hashed the content with the date re-printed WITHOUT its
//     trailing zeros ("...56.1234560Z" -> "...56.123456Z") and refused its own
//     file as hash_mismatch - about one run in ten.
//   * Value<string>() on a DateTime token prints the INVARIANT DateTime format
//     ("12/31/2026 23:00:00"), so UndoJournal.Last, ordering that text ordinally,
//     took December 2026 for newer than January 2027, the coordination ledger's
//     BCF CreationDate stopped being an xs:dateTime, and the repair memory's
//     newest-first trim kept the wrong entries.
//
// Content is built with JObject.FromObject, as production builds it - the old
// SnapshotStore tests built it with JObject.Parse and so never saw the bug.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public sealed class StoredDateRoundTripTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "hz-dates-" + Guid.NewGuid().ToString("N"));

        public StoredDateRoundTripTests() { Directory.CreateDirectory(_dir); }
        public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

        private sealed class Snapshot
        {
            public string schema = "horizun.diagnostics-snapshot/1";
            public string taken_utc;
            public int count = 5;
        }

        [Theory]
        [InlineData("2026-10-08T12:34:56.1234560Z")]   // "o" format, fraction ends in 0
        [InlineData("2026-10-08T12:34:56.0000000Z")]
        [InlineData("2026-10-08T12:34:56Z")]
        public void A_snapshot_written_from_an_object_reads_back_verified(string takenUtc)
        {
            JObject content = JObject.FromObject(new Snapshot { taken_utc = takenUtc });
            string written = null;
            SnapshotWriteResult w = SnapshotStore.Write(_dir, "s.json", content, (p, text) => { written = text; return true; });
            Assert.True(w.Ok, w.Message);

            SnapshotReadResult r = SnapshotStore.Read(written);
            Assert.True(r.Ok, r.Code + ": " + r.Message);
            Assert.Equal(takenUtc, r.Content.Value<string>("taken_utc"));
        }

        [Fact]
        public void Undo_last_takes_january_2027_over_december_2026_after_a_reload()
        {
            string path = Path.Combine(_dir, "undo.json");
            UndoJournalStore.Save(path, new List<UndoBatch>
            {
                new UndoBatch { Id = "jan", Tool = "t", CreatedUtc = "2027-01-01T01:00:00.0000000Z" },
                new UndoBatch { Id = "dec", Tool = "t", CreatedUtc = "2026-12-31T23:00:00.0000000Z" }
            });
            List<UndoBatch> loaded = UndoJournalStore.Load(path);
            Assert.Equal("2027-01-01T01:00:00.0000000Z", loaded.Single(b => b.Id == "jan").CreatedUtc);
            UndoBatch last = UndoRules.Last(loaded, out string refusal);
            Assert.Null(refusal);
            Assert.Equal("jan", last.Id);
        }

        [Fact]
        public void Undo_last_orders_by_instant_even_when_the_offsets_differ()
        {
            var batches = new List<UndoBatch>
            {
                new UndoBatch { Id = "later", Tool = "t", CreatedUtc = "2026-10-08T10:00:00-05:00" },   // 15:00Z
                new UndoBatch { Id = "earlier", Tool = "t", CreatedUtc = "2026-10-08T14:00:00Z" }
            };
            Assert.Equal("later", UndoRules.Last(batches, out _).Id);
        }

        [Fact]
        public void A_reloaded_coordination_finding_keeps_its_dates_and_the_bcf_date_is_xs_datetime()
        {
            string path = Path.Combine(_dir, "coord.json");
            var findings = new Dictionary<string, CoordinationFinding>
            {
                ["0123456789abcdef0123456789abcdef"] = new CoordinationFinding
                {
                    Id = "0123456789abcdef0123456789abcdef", SideA = "a", SideB = "b", CategoryA = "Walls", CategoryB = "Ducts",
                    FirstSeenUtc = "2026-12-31T23:00:00.0000000Z", LastSeenUtc = "2027-01-01T01:00:00.0000000Z", TimesSeen = 2
                }
            };
            CoordinationLedger.Save(path, "doc", findings);
            CoordinationFinding f = CoordinationLedger.Load(path, out _)["0123456789abcdef0123456789abcdef"];
            Assert.Equal("2026-12-31T23:00:00.0000000Z", f.FirstSeenUtc);
            string xml = CoordinationRules.BcfMarkupXml(f, "doc");
            Match m = Regex.Match(xml, "<CreationDate>([^<]*)</CreationDate>");
            Assert.True(m.Success);
            Assert.Matches(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}", m.Groups[1].Value);
        }

        [Fact]
        public void The_repair_memory_keeps_its_dates_and_trims_oldest_first()
        {
            string path = Path.Combine(_dir, "repair.json");
            var entries = new List<RepairEntry>();
            for (int i = 0; i < RepairMemory.MaxEntries; i++)
                entries.Add(new RepairEntry { Shape = "old" + i, Tool = "t", FirstSeenUtc = "2026-12-01T00:00:00Z", LastSeenUtc = "2026-12-01T00:00:00Z" });
            entries.Add(new RepairEntry { Shape = "newest", Tool = "t", FirstSeenUtc = "2027-01-02T00:00:00.0000000Z", LastSeenUtc = "2027-01-02T00:00:00.0000000Z" });
            RepairMemory.Save(path, entries);
            Dictionary<string, RepairEntry> loaded = RepairMemory.Load(path);
            Assert.True(loaded.ContainsKey("newest"));
            Assert.Equal("2027-01-02T00:00:00.0000000Z", loaded["newest"].LastSeenUtc);

            // Saved again from the RELOADED entries, the newest must still survive the trim.
            var reloaded = loaded.Values.ToList();
            reloaded.Add(new RepairEntry { Shape = "older-than-all", Tool = "t", FirstSeenUtc = "2026-01-01T00:00:00Z", LastSeenUtc = "2026-01-01T00:00:00Z" });
            RepairMemory.Save(path, reloaded);
            Assert.True(RepairMemory.Load(path).ContainsKey("newest"));
            Assert.False(RepairMemory.Load(path).ContainsKey("older-than-all"));
        }
    }
}
