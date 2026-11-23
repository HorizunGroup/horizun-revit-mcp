// -----------------------------------------------------------------------------
// Horizun Core tests - saved Python outputs (<jobs>\outputs\<job_id>.<page_chars>.json) belong to
// their job record: they count in its bytes against the cap, are deleted with it, and an orphan
// (or a half-written temp) of a job that no longer exists is cleaned.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public sealed class DurableStoreOutputsRetentionTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "hz-outret-" + Guid.NewGuid().ToString("N"));
        private readonly DateTime _now = new DateTime(2026, 11, 18, 12, 0, 0, DateTimeKind.Utc);

        public DurableStoreOutputsRetentionTests() { Directory.CreateDirectory(OutputPages.Folder(_root)); }
        public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

        private static Func<string, string> Settings(params string[] pairs)
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int i = 0; i < pairs.Length; i += 2) values[pairs[i]] = pairs[i + 1];
            return key => values.TryGetValue(key, out string value) ? value : null;
        }

        private string Job(string id, int ageDays)
        {
            string path = Path.Combine(_root, id + ".jsonl");
            File.WriteAllText(path, "{\"event\":\"start\"}\n{\"event\":\"finish\"}");
            File.SetLastWriteTimeUtc(path, _now.AddDays(-ageDays));
            return path;
        }

        private string Output(string id, int pageChars = 20000, int bytes = 100)
        {
            string path = OutputPages.FilePath(_root, id, pageChars);
            File.WriteAllText(path, new string('x', bytes));
            return path;
        }

        [Fact]
        public void The_over_four_megabyte_full_file_counts_and_goes_with_its_job()
        {
            string job = Job("big", 40);
            string full = Path.Combine(OutputPages.Folder(_root), OutputPages.FullFileName("big"));
            File.WriteAllText(full, new string('x', 7000));
            Job("keep", 1);
            string keepFull = Path.Combine(OutputPages.Folder(_root), OutputPages.FullFileName("keep"));
            File.WriteAllText(keepFull, new string('x', 7000));

            DurableStoreRetentionReport report = DurableStoreRetention.Apply(_root, DurableStoreKind.Jobs, Settings("job_retention_days", "30"), _now);

            Assert.True(report.BytesBefore >= 14000);                       // both full files are in the total
            Assert.False(File.Exists(job));
            Assert.False(File.Exists(full));                                // deleted with its old job
            Assert.True(File.Exists(keepFull));
        }

        [Fact]
        public void The_output_is_deleted_together_with_its_old_job_record()
        {
            string job = Job("old", 40);
            string output = Output("old");
            Job("recent", 1);
            string kept = Output("recent");

            DurableStoreRetention.Apply(_root, DurableStoreKind.Jobs, Settings("job_retention_days", "30"), _now);

            Assert.False(File.Exists(job));
            Assert.False(File.Exists(output));
            Assert.True(File.Exists(kept));
        }

        [Fact]
        public void The_output_bytes_count_against_the_cap()
        {
            Job("a", 3);
            Output("a", bytes: 5000);
            Job("b", 2);
            Output("b", bytes: 5000);

            DurableStoreRetentionReport report = DurableStoreRetention.Apply(
                _root, DurableStoreKind.Jobs, Settings("job_retention_days", "0", "job_max_bytes", "6000"), _now);

            Assert.True(report.BytesBefore > 10000);                        // the two outputs are in the total
            Assert.False(File.Exists(OutputPages.FilePath(_root, "a", 20000)));   // the oldest goes first, with its output
            Assert.True(File.Exists(OutputPages.FilePath(_root, "b", 20000)));
        }

        [Fact]
        public void An_orphan_output_and_a_leftover_temp_of_a_missing_job_are_cleaned_but_a_live_jobs_are_not()
        {
            Job("live", 1);
            string liveOutput = Output("live");
            string liveTemp = Path.Combine(OutputPages.Folder(_root), "live.20000.ab12cd34.tmp");
            File.WriteAllText(liveTemp, "partial");
            string orphan = Output("gone");
            string orphanTemp = Path.Combine(OutputPages.Folder(_root), "gone.20000.ab12cd34.tmp");
            File.WriteAllText(orphanTemp, "partial");

            DurableStoreRetention.Apply(_root, DurableStoreKind.Jobs, Settings("job_retention_days", "30"), _now);

            Assert.True(File.Exists(liveOutput));
            Assert.True(File.Exists(liveTemp));
            Assert.False(File.Exists(orphan));
            Assert.False(File.Exists(orphanTemp));
        }
    }
}
