// -----------------------------------------------------------------------------
// Horizun Core tests - fail-closed retention for jobs and idempotency records.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public sealed class DurableStoreRetentionTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "hz-retention-" + Guid.NewGuid().ToString("N"));
        private readonly DateTime _now = new DateTime(2026, 8, 14, 12, 0, 0, DateTimeKind.Utc);

        public DurableStoreRetentionTests() { Directory.CreateDirectory(_root); }

        private string Record(string name, string content, int ageDays, int padding = 0)
        {
            string path = Path.Combine(_root, name + ".jsonl");
            File.WriteAllText(path, content + new string(' ', padding));
            File.SetLastWriteTimeUtc(path, _now.AddDays(-ageDays));
            return path;
        }

        private static Func<string, string> Settings(params string[] pairs)
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int i = 0; i < pairs.Length; i += 2) values[pairs[i]] = pairs[i + 1];
            return key => values.TryGetValue(key, out string value) ? value : null;
        }

        [Fact]
        public void Jobs_remove_only_old_finished_records()
        {
            string finished = Record("finished", "{\"event\":\"start\"}\n{\"event\":\"finish\"}", 40);
            string running = Record("running", "{\"event\":\"start\"}\n{\"event\":\"running\"}", 40);
            string corrupt = Record("corrupt", "{not-json", 40);

            DurableStoreRetentionReport report = DurableStoreRetention.Apply(
                _root, DurableStoreKind.Jobs, Settings("job_retention_days", "30"), _now);

            Assert.False(File.Exists(finished));
            Assert.True(File.Exists(running));
            Assert.True(File.Exists(corrupt));
            Assert.Equal(1, report.RemovedFiles);
            Assert.Equal(2, report.ProtectedFiles);
        }

        [Fact]
        public void Idempotency_never_removes_a_claim_without_completion()
        {
            string completed = Record("completed", "{\"event\":\"claimed\"}\n{\"event\":\"completed\"}", 100);
            string inDoubt = Record("in-doubt", "{\"event\":\"claimed\"}", 100);

            DurableStoreRetention.Apply(
                _root, DurableStoreKind.Idempotency,
                Settings("idempotency_retention_days", "90"), _now);

            Assert.False(File.Exists(completed));
            Assert.True(File.Exists(inDoubt));
        }

        [Fact]
        public void The_current_key_is_protected_even_when_old_and_over_the_cap()
        {
            string current = Record("current", "{\"event\":\"claimed\"}\n{\"event\":\"completed\"}", 100, 500);
            string other = Record("other", "{\"event\":\"claimed\"}\n{\"event\":\"completed\"}", 99, 500);

            DurableStoreRetentionReport report = DurableStoreRetention.Apply(
                _root, DurableStoreKind.Idempotency,
                Settings("idempotency_retention_days", "30", "idempotency_max_bytes", "1"),
                _now, current);

            Assert.True(File.Exists(current));
            Assert.False(File.Exists(other));
            Assert.Contains("remains above", report.Note);
        }

        [Fact]
        public void Size_cap_drops_oldest_terminal_records_first()
        {
            string oldest = Record("oldest", "{\"event\":\"finish\"}", 3, 200);
            string newer = Record("newer", "{\"event\":\"finish\"}", 2, 200);
            string newest = Record("newest", "{\"event\":\"finish\"}", 1, 200);
            long keepTwo = new FileInfo(newer).Length + new FileInfo(newest).Length;

            DurableStoreRetention.Apply(
                _root, DurableStoreKind.Jobs, Settings("job_max_bytes", keepTwo.ToString()), _now);

            Assert.False(File.Exists(oldest));
            Assert.True(File.Exists(newer));
            Assert.True(File.Exists(newest));
        }

        [Fact]
        public void Malformed_policy_deletes_nothing()
        {
            string old = Record("old", "{\"event\":\"finish\"}", 100);
            DurableStoreRetentionReport report = DurableStoreRetention.Apply(
                _root, DurableStoreKind.Jobs, Settings("job_retention_days", "thirty"), _now);

            Assert.True(File.Exists(old));
            Assert.Contains("kept", report.Note, StringComparison.OrdinalIgnoreCase);
        }

        public void Dispose()
        {
            try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { }
        }
    }
}
