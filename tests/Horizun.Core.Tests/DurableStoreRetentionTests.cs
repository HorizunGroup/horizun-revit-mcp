// -----------------------------------------------------------------------------
// Horizun Core tests - fail-closed retention for jobs and idempotency records.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
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

        [Fact]
        public void Job_retention_accounts_for_and_removes_its_durable_image_attachment()
        {
            string job = Record("with-image", "{\"event\":\"finish\"}", 40);
            string attachments = Path.Combine(_root, "attachments");
            Directory.CreateDirectory(attachments);
            string image = Path.Combine(attachments, "with-image.png");
            File.WriteAllBytes(image, new byte[4096]);
            long expected = new FileInfo(job).Length + new FileInfo(image).Length;

            DurableStoreRetentionReport report = DurableStoreRetention.Apply(
                _root, DurableStoreKind.Jobs, Settings("job_retention_days", "30"), _now);

            Assert.False(File.Exists(job));
            Assert.False(File.Exists(image));
            Assert.Equal(expected, report.BytesBefore);
            Assert.Equal(0, report.BytesAfter);
        }

        [Fact]
        public void Active_task_lease_protects_job_and_all_companions_until_expiry()
        {
            string job = Record("leased", "{\"event\":\"finish\"}", 40);
            string leases = Path.Combine(_root, "leases");
            string results = Path.Combine(_root, "results");
            Directory.CreateDirectory(leases);
            Directory.CreateDirectory(results);
            string lease = Path.Combine(leases, "leased.json");
            File.WriteAllText(lease, new JObject
            {
                ["schema"] = 1, ["job_id"] = "leased",
                ["retain_until_utc"] = new DateTimeOffset(_now.AddHours(1)).ToString("O")
            }.ToString(Formatting.None));
            string result = Path.Combine(results, "leased.json");
            File.WriteAllText(result, new string('x', 4096));

            DurableStoreRetentionReport protectedReport = DurableStoreRetention.Apply(_root, DurableStoreKind.Jobs,
                Settings("job_retention_days", "1", "job_max_bytes", "1"), _now);
            Assert.True(File.Exists(job), protectedReport.Summary());
            Assert.True(File.Exists(lease));
            Assert.True(File.Exists(result));

            DurableStoreRetention.Apply(_root, DurableStoreKind.Jobs,
                Settings("job_retention_days", "1", "job_max_bytes", "1"), _now.AddHours(2));
            Assert.False(File.Exists(job));
            Assert.False(File.Exists(lease));
            Assert.False(File.Exists(result));
        }

        [Fact]
        public void Corrupt_lease_fails_closed_for_maximum_TTL_then_becomes_reclaimable()
        {
            string job = Record("corrupt-lease", "{\"event\":\"finish\"}", 40);
            string leases = Path.Combine(_root, "leases");
            Directory.CreateDirectory(leases);
            string lease = Path.Combine(leases, "corrupt-lease.json");
            File.WriteAllText(lease, "not-json");

            DurableStoreRetention.Apply(_root, DurableStoreKind.Jobs,
                Settings("job_max_bytes", "1"), _now);
            Assert.True(File.Exists(job));
            Assert.True(File.Exists(lease));

            File.SetLastWriteTimeUtc(lease, _now.AddDays(-8));
            DurableStoreRetentionReport reclaimed = DurableStoreRetention.Apply(
                _root, DurableStoreKind.Jobs, Settings("job_max_bytes", "1"), _now);
            Assert.False(File.Exists(job));
            Assert.False(File.Exists(lease));
            Assert.Contains(reclaimed.Errors, e => e.Contains("expired unreadable", StringComparison.Ordinal));
        }

        [Fact]
        public void Forged_far_future_lease_cannot_pin_store_forever()
        {
            string job = Record("future-lease", "{\"event\":\"finish\"}", 40);
            string leases = Path.Combine(_root, "leases");
            Directory.CreateDirectory(leases);
            string lease = Path.Combine(leases, "future-lease.json");
            File.WriteAllText(lease, new JObject
            {
                ["schema"] = 1, ["job_id"] = "future-lease",
                ["retain_until_utc"] = new DateTimeOffset(_now.AddYears(20)).ToString("O")
            }.ToString(Formatting.None));
            File.SetLastWriteTimeUtc(lease, _now.AddYears(20));

            DurableStoreRetention.Apply(_root, DurableStoreKind.Jobs,
                Settings("job_max_bytes", "1"), _now);
            Assert.True(File.Exists(job));
            Assert.InRange(File.GetLastWriteTimeUtc(lease), _now.AddSeconds(-1), _now.AddSeconds(1));

            DurableStoreRetention.Apply(_root, DurableStoreKind.Jobs,
                Settings("job_max_bytes", "1"), _now.AddDays(8));
            Assert.False(File.Exists(job));
            Assert.False(File.Exists(lease));
        }

        public void Dispose()
        {
            try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { }
        }
    }
}
