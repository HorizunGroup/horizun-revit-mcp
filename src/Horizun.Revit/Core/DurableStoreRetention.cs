// -----------------------------------------------------------------------------
// Horizun Revit MCP - retention for jobs and durable idempotency records.
// Original Horizun code.
//
// Only records whose terminal state is PROVED may be removed. A job without a
// finish event and an idempotency claim without a matching completion carry an
// unknown outcome; deleting either would turn uncertainty into permission to run
// again. Malformed settings and malformed records therefore keep everything.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public enum DurableStoreKind { Jobs, Idempotency }

    public sealed class DurableStoreRetentionReport
    {
        public int FilesSeen;
        public int TerminalFiles;
        public int ProtectedFiles;
        public int RemovedFiles;
        public long BytesBefore;
        public long BytesAfter;
        public string Note;
        public readonly List<string> Errors = new List<string>();

        public string Summary()
        {
            string text = RemovedFiles + " terminal record(s) removed; " + FilesSeen + " seen; " +
                          BytesBefore + " bytes before, " + BytesAfter + " after; " +
                          ProtectedFiles + " active/in-doubt/protected record(s) retained.";
            if (!string.IsNullOrEmpty(Note)) text += " " + Note;
            if (Errors.Count > 0) text += " " + Errors.Count + " cleanup error(s): " + string.Join(" | ", Errors.ToArray());
            return text;
        }
    }

    public static class DurableStoreRetention
    {
        private sealed class Candidate
        {
            public string Path;
            public string AttachmentPath;
            public string ResultPath;
            public string LeasePath;
            public DateTime WrittenUtc;
            public long Bytes;
            public long RecordBytes;
            public long AttachmentBytes;
            public long ResultBytes;
            public long LeaseBytes;
            public bool Terminal;
            public bool Protected;
            public bool Remove;
        }

        public static DurableStoreRetentionReport Apply(
            string directory,
            DurableStoreKind kind,
            Func<string, string> setting = null,
            DateTime? utcNow = null,
            string protectedPath = null)
        {
            if (kind != DurableStoreKind.Jobs)
                return ApplyUnlocked(directory, kind, setting, utcNow, protectedPath);
            using (AcquireJobStoreMutex())
                return ApplyUnlocked(directory, kind, setting, utcNow, protectedPath);
        }

        private static DurableStoreRetentionReport ApplyUnlocked(
            string directory,
            DurableStoreKind kind,
            Func<string, string> setting,
            DateTime? utcNow,
            string protectedPath)
        {
            var report = new DurableStoreRetentionReport();
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return report;

            DateTime now = utcNow ?? DateTime.UtcNow;
            if (kind == DurableStoreKind.Jobs) CleanupOrphanArtifacts(directory, report);

            string prefix = kind == DurableStoreKind.Jobs ? "job" : "idempotency";
            int days;
            long maxBytes;
            string policyError;
            if (!TryPolicy(setting, prefix, out days, out maxBytes, out policyError))
            {
                report.Note = policyError + " Every record was kept.";
                return InventoryOnly(directory, kind, protectedPath, report, now);
            }

            // Zero/zero is an explicit KEEP FOREVER policy. Avoid reading every payload
            // on each command when the operator has not opted into cleanup.
            if (days == 0 && maxBytes == 0)
            {
                report.Note = prefix + " retention keeps records forever (both limits are 0).";
                return InventoryOnly(directory, kind, protectedPath, report, now);
            }

            List<Candidate> files = Inventory(directory, kind, protectedPath, report, now);

            if (days > 0)
            {
                DateTime cutoff = now.AddDays(-days);
                foreach (Candidate file in files)
                    if (file.Terminal && !file.Protected && file.WrittenUtc < cutoff) file.Remove = true;
            }

            if (maxBytes > 0)
            {
                long remaining = report.BytesBefore;
                foreach (Candidate file in files) if (file.Remove) remaining -= file.Bytes;

                var removable = files.FindAll(f => f.Terminal && !f.Protected && !f.Remove);
                removable.Sort((a, b) => a.WrittenUtc.CompareTo(b.WrittenUtc));
                foreach (Candidate file in removable)
                {
                    if (remaining <= maxBytes) break;
                    file.Remove = true;
                    remaining -= file.Bytes;
                }
            }

            foreach (Candidate file in files)
            {
                if (!file.Remove) continue;
                try
                {
                    File.Delete(file.Path);
                    report.RemovedFiles++;
                    report.BytesAfter -= file.RecordBytes;
                    if (!string.IsNullOrEmpty(file.AttachmentPath) && File.Exists(file.AttachmentPath))
                    {
                        try
                        {
                            File.Delete(file.AttachmentPath);
                            report.BytesAfter -= file.AttachmentBytes;
                        }
                        catch (Exception attachmentError)
                        {
                            report.Errors.Add(System.IO.Path.GetFileName(file.AttachmentPath) + ": " +
                                              attachmentError.Message);
                        }
                    }
                    DeleteCompanion(file.ResultPath, file.ResultBytes, report);
                    DeleteCompanion(file.LeasePath, file.LeaseBytes, report);
                }
                catch (Exception ex)
                {
                    report.Errors.Add(System.IO.Path.GetFileName(file.Path) + ": " + ex.Message);
                }
            }

            if (maxBytes > 0 && report.BytesAfter > maxBytes)
                report.Note = "The store remains above its " + maxBytes + "-byte cap because only terminal records " +
                              "may be removed; active, in-doubt, corrupt and protected records are never sacrificed.";
            return report;
        }

        /// <summary>
        /// Cross-process boundary shared by retention and creation of task leases.
        /// A lease writer verifies its JSONL while holding this mutex; retention cannot
        /// delete between that check and the durable lease rename.
        /// </summary>
        internal static IDisposable AcquireJobStoreMutex()
        {
            var mutex = new Mutex(false, "Local\\Horizun.JobStore.V1");
            try
            {
                try { mutex.WaitOne(); }
                catch (AbandonedMutexException) { }
                return new MutexLease(mutex);
            }
            catch { mutex.Dispose(); throw; }
        }

        private sealed class MutexLease : IDisposable
        {
            private Mutex _mutex;
            public MutexLease(Mutex mutex) { _mutex = mutex; }
            public void Dispose()
            {
                Mutex mutex = Interlocked.Exchange(ref _mutex, null);
                if (mutex == null) return;
                try { mutex.ReleaseMutex(); } finally { mutex.Dispose(); }
            }
        }

        private static DurableStoreRetentionReport InventoryOnly(
            string directory, DurableStoreKind kind, string protectedPath,
            DurableStoreRetentionReport report, DateTime now)
        {
            Inventory(directory, kind, protectedPath, report, now);
            return report;
        }

        private static List<Candidate> Inventory(
            string directory, DurableStoreKind kind, string protectedPath,
            DurableStoreRetentionReport report, DateTime now)
        {
            var files = new List<Candidate>();
            string protectedFull = string.IsNullOrWhiteSpace(protectedPath) ? null : Full(protectedPath);
            foreach (string path in Directory.GetFiles(directory, "*.jsonl", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    var info = new FileInfo(path);
                    var file = new Candidate
                    {
                        Path = path,
                        WrittenUtc = info.LastWriteTimeUtc,
                        Bytes = info.Length,
                        RecordBytes = info.Length,
                        Protected = protectedFull != null &&
                                    string.Equals(Full(path), protectedFull, StringComparison.OrdinalIgnoreCase)
                    };
                    if (kind == DurableStoreKind.Jobs)
                    {
                        string attachment = Path.Combine(directory, "attachments",
                            Path.GetFileNameWithoutExtension(path) + ".png");
                        if (File.Exists(attachment))
                        {
                            file.AttachmentPath = attachment;
                            file.AttachmentBytes = new FileInfo(attachment).Length;
                            file.Bytes += file.AttachmentBytes;
                        }
                        string result = Path.Combine(directory, "results",
                            Path.GetFileNameWithoutExtension(path) + ".json");
                        if (File.Exists(result))
                        {
                            file.ResultPath = result;
                            file.ResultBytes = new FileInfo(result).Length;
                            file.Bytes += file.ResultBytes;
                        }
                        string lease = Path.Combine(directory, "leases",
                            Path.GetFileNameWithoutExtension(path) + ".json");
                        if (File.Exists(lease))
                        {
                            file.LeasePath = lease;
                            file.LeaseBytes = new FileInfo(lease).Length;
                            file.Bytes += file.LeaseBytes;
                            string leaseError;
                            bool? active = LeaseActive(lease, Path.GetFileNameWithoutExtension(path), now,
                                                       out leaseError);
                            // An unreadable lease fails closed: retention may cost disk,
                            // never a still-entitled task's only answer.
                            if (!active.HasValue || active.Value) file.Protected = true;
                            if (leaseError != null) report.Errors.Add(Path.GetFileName(lease) + ": " + leaseError);
                        }
                    }
                    file.Terminal = Terminal(path, kind);
                    files.Add(file);
                    report.FilesSeen++;
                    report.BytesBefore += file.Bytes;
                    report.BytesAfter += file.Bytes;
                    if (file.Terminal) report.TerminalFiles++;
                    if (!file.Terminal || file.Protected) report.ProtectedFiles++;
                }
                catch (Exception ex)
                {
                    report.Errors.Add(System.IO.Path.GetFileName(path) + ": " + ex.Message);
                }
            }
            return files;
        }

        private static void CleanupOrphanArtifacts(string directory, DurableStoreRetentionReport report)
        {
            CleanupOrphans(directory, Path.Combine(directory, "attachments"), "*.png", report);
            CleanupOrphans(directory, Path.Combine(directory, "results"), "*.json", report);
            CleanupOrphans(directory, Path.Combine(directory, "leases"), "*.json", report);
        }

        private static void CleanupOrphans(string jobsDirectory, string artifactDirectory, string pattern,
                                           DurableStoreRetentionReport report)
        {
            if (!Directory.Exists(artifactDirectory)) return;
            foreach (string artifact in Directory.GetFiles(artifactDirectory, pattern, SearchOption.TopDirectoryOnly))
            {
                string job = Path.Combine(jobsDirectory, Path.GetFileNameWithoutExtension(artifact) + ".jsonl");
                if (File.Exists(job)) continue;
                try { File.Delete(artifact); }
                catch (Exception ex)
                {
                    long bytes = 0;
                    try { bytes = new FileInfo(artifact).Length; } catch { }
                    report.BytesBefore += bytes;
                    report.BytesAfter += bytes;
                    report.Errors.Add(Path.GetFileName(artifact) + ": orphan cleanup failed: " + ex.Message);
                }
            }
        }

        private static bool? LeaseActive(string path, string expectedJobId, DateTime now, out string error)
        {
            error = null;
            try
            {
                var info = new FileInfo(path);
                if (info.Length <= 0 || info.Length > 4096) throw new InvalidDataException("lease exceeds 4096 bytes");
                JObject lease = JObject.Parse(ReadBoundedUtf8(path, 4096));
                if ((int?)lease["schema"] != 1 || (string)lease["job_id"] != expectedJobId)
                    throw new InvalidDataException("lease identity does not match its job");
                DateTimeOffset until = LeaseInstant(lease["retain_until_utc"]);
                DateTime utcNow = now.Kind == DateTimeKind.Utc ? now : now.ToUniversalTime();
                DateTime written = File.GetLastWriteTimeUtc(path);
                TimeSpan maximum = TimeSpan.FromMilliseconds(
                    Horizun.Contracts.Contract.MaxTaskTtlMilliseconds);
                if (written > utcNow.AddMinutes(2) ||
                    until > new DateTimeOffset(written, TimeSpan.Zero).Add(maximum).AddMinutes(2))
                    throw new InvalidDataException("lease timestamp exceeds the maximum task TTL");
                return until > new DateTimeOffset(utcNow, TimeSpan.Zero);
            }
            catch (Exception ex)
            {
                // Fail closed for the complete interval any valid MCP task could still
                // own. After the maximum seven-day lease plus clock/rename grace, this
                // unreadable file cannot represent a live entitlement and must stop
                // pinning disk forever. A future timestamp remains protected.
                DateTime utcNow = now.Kind == DateTimeKind.Utc ? now : now.ToUniversalTime();
                try
                {
                    DateTime written = File.GetLastWriteTimeUtc(path);
                    if (written > utcNow.AddMinutes(2))
                    {
                        // Normalize a corrupt/future timestamp once. Otherwise a
                        // malformed lease dated years ahead could pin the job forever.
                        File.SetLastWriteTimeUtc(path, utcNow);
                        written = utcNow;
                    }
                    if (written <= utcNow.AddMilliseconds(
                            -Horizun.Contracts.Contract.MaxTaskTtlMilliseconds).AddMinutes(-2))
                    {
                        error = "expired unreadable retention lease no longer protects its job: " + ex.Message;
                        return false;
                    }
                }
                catch { }
                error = "retention lease is unreadable and was kept fail-closed: " + ex.Message;
                return null;
            }
        }

        private static DateTimeOffset LeaseInstant(JToken token)
        {
            if (token?.Type == JTokenType.Date && token is JValue value)
            {
                if (value.Value is DateTimeOffset dto) return dto.ToUniversalTime();
                if (value.Value is DateTime dt)
                {
                    if (dt.Kind == DateTimeKind.Unspecified) dt = DateTime.SpecifyKind(dt, DateTimeKind.Utc);
                    return new DateTimeOffset(dt).ToUniversalTime();
                }
            }
            DateTimeOffset parsed;
            if (token?.Type != JTokenType.String ||
                !DateTimeOffset.TryParse((string)token, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out parsed))
                throw new InvalidDataException("lease has an invalid retain_until_utc");
            return parsed.ToUniversalTime();
        }

        private static void DeleteCompanion(string path, long bytes, DurableStoreRetentionReport report)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
            try { File.Delete(path); report.BytesAfter -= bytes; }
            catch (Exception ex) { report.Errors.Add(Path.GetFileName(path) + ": " + ex.Message); }
        }

        private static string ReadBoundedUtf8(string path, int maxBytes)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                                               FileShare.Read | FileShare.Delete))
            {
                long length = stream.Length;
                if (length <= 0 || length > maxBytes) throw new InvalidDataException("file exceeds its bounded size");
                var bytes = new byte[(int)length];
                int offset = 0;
                while (offset < bytes.Length)
                {
                    int read = stream.Read(bytes, offset, bytes.Length - offset);
                    if (read == 0) throw new EndOfStreamException("file ended while it was read");
                    offset += read;
                }
                if (stream.ReadByte() != -1 || stream.Length != length)
                    throw new InvalidDataException("file changed while it was read");
                return new UTF8Encoding(false, true).GetString(bytes);
            }
        }

        private static bool Terminal(string path, DurableStoreKind kind)
        {
            string[] lines;
            try { lines = File.ReadAllLines(path, new UTF8Encoding(false, true)); }
            catch { return false; }

            bool claimed = false;
            bool completed = false;
            foreach (string line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                JObject record;
                try { record = JObject.Parse(line); }
                catch { return false; }
                string ev = (string)record["event"];

                if (kind == DurableStoreKind.Jobs)
                {
                    if (completed) return false; // nothing valid follows finish
                    if (ev == "finish") completed = true;
                    continue;
                }

                if (ev == "claimed")
                {
                    if (claimed || completed) return false;
                    claimed = true;
                }
                else if (ev == "completed")
                {
                    if (!claimed || completed) return false;
                    completed = true;
                }
                else return false;
            }
            return completed;
        }

        private static bool TryPolicy(Func<string, string> get, string prefix,
                                      out int days, out long maxBytes, out string error)
        {
            days = 0;
            maxBytes = 0;
            error = null;
            if (get == null) return true;

            string daysText = get(prefix + "_retention_days");
            string bytesText = get(prefix + "_max_bytes");
            if (!string.IsNullOrWhiteSpace(daysText) &&
                (!int.TryParse(daysText, NumberStyles.Integer, CultureInfo.InvariantCulture, out days) || days < 0))
            {
                error = prefix + "_retention_days must be a non-negative whole number, not '" + daysText + "'.";
                return false;
            }
            if (!string.IsNullOrWhiteSpace(bytesText) &&
                (!long.TryParse(bytesText, NumberStyles.Integer, CultureInfo.InvariantCulture, out maxBytes) || maxBytes < 0))
            {
                error = prefix + "_max_bytes must be a non-negative byte count, not '" + bytesText + "'.";
                return false;
            }
            return true;
        }

        private static string Full(string path)
        {
            try { return System.IO.Path.GetFullPath(path); }
            catch { return path; }
        }
    }
}
