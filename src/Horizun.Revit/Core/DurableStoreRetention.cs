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
            public DateTime WrittenUtc;
            public long Bytes;
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
            var report = new DurableStoreRetentionReport();
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return report;

            string prefix = kind == DurableStoreKind.Jobs ? "job" : "idempotency";
            int days;
            long maxBytes;
            string policyError;
            if (!TryPolicy(setting, prefix, out days, out maxBytes, out policyError))
            {
                report.Note = policyError + " Every record was kept.";
                return InventoryOnly(directory, kind, protectedPath, report);
            }

            // Zero/zero is an explicit KEEP FOREVER policy. Avoid reading every payload
            // on each command when the operator has not opted into cleanup.
            if (days == 0 && maxBytes == 0)
            {
                report.Note = prefix + " retention keeps records forever (both limits are 0).";
                return InventoryOnly(directory, kind, protectedPath, report);
            }

            List<Candidate> files = Inventory(directory, kind, protectedPath, report);
            DateTime now = utcNow ?? DateTime.UtcNow;

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
                    report.BytesAfter -= file.Bytes;
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

        private static DurableStoreRetentionReport InventoryOnly(
            string directory, DurableStoreKind kind, string protectedPath, DurableStoreRetentionReport report)
        {
            Inventory(directory, kind, protectedPath, report);
            return report;
        }

        private static List<Candidate> Inventory(
            string directory, DurableStoreKind kind, string protectedPath, DurableStoreRetentionReport report)
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
                        Protected = protectedFull != null &&
                                    string.Equals(Full(path), protectedFull, StringComparison.OrdinalIgnoreCase)
                    };
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
