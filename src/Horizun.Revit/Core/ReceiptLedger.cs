// -----------------------------------------------------------------------------
// Horizun Revit MCP — the operation receipt ledger. Original code.
//
// Story 5.2's missing middle: RetentionPolicy existed, Redact existed, and no
// receipt had ever been written for either to act on. This is the writer.
//
// A receipt answers, after the fact, the question the reply answered in the
// moment: WHAT ran, against WHICH document, with WHAT outcome, and how long it
// took. It records what the command's own reply carried and nothing it did not -
// a receipt that "enriches" its record beyond what was verified would be the
// substitution again, one file downstream of where it usually happens.
//
// Shape on disk: one JSONL file per UTC day, receipts-YYYY-MM-DD.jsonl, in
// %USERPROFILE%\.horizun\receipts. Append-only; a failed append NEVER fails the
// operation it records - the answer the caller is waiting on outranks the diary -
// but it is COUNTED, and the count is readable, because a ledger that drops
// entries silently reads like a quiet day.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public static class ReceiptLedger
    {
        private static readonly object Gate = new object();
        private static long _appendFailures;
        private static string _lastAppendError;

        /// <summary>How many receipts could not be written, and the last reason. For health.</summary>
        public static long AppendFailures => Interlocked.Read(ref _appendFailures);
        public static string LastAppendError
        {
            get { lock (Gate) { return _lastAppendError; } }
        }

        /// <summary>
        /// Build a receipt from what the operation's own reply carried. Every field is
        /// COPIED, never inferred: a reply without a transaction_status yields a receipt
        /// without one, and the reader sees the absence instead of a guess.
        /// </summary>
        public static JObject Build(string tool, bool success, string error, JObject replyData,
                                    long waitedMs, long totalMs, string correlationId, DateTime utcNow)
        {
            var receipt = new JObject
            {
                ["operation_id"] = Guid.NewGuid().ToString("d"),
                ["correlation_id"] = correlationId,
                ["utc"] = utcNow.ToString("o", CultureInfo.InvariantCulture),
                ["tool"] = tool,
                ["outcome"] = success ? "ok" : "failed",
                ["waited_ms"] = waitedMs,
                ["total_ms"] = totalMs
            };
            if (!success && error != null)
                receipt["error"] = error;
            if (replyData != null)
            {
                // The identity block the gate stamps on every mutation reply, and the
                // verification facts, when present. Copied by name; unknown structure is
                // deliberately not swept in - a receipt is a record, not a mirror of the
                // payload, and payloads carry model content that retention may be asked
                // to redact.
                // Copy(): assigning null to a JObject indexer CREATES the property as a
                // JSON null, and this method's whole promise is that absence stays
                // absence. Found by this class's own test before it shipped.
                Copy(receipt, "document", replyData["document"]);
                Copy(receipt, "document_fingerprint", replyData["document_fingerprint"]);
                Copy(receipt, "transaction_status", replyData["transaction_status"]);
                Copy(receipt, "dry_run", replyData["dry_run"]);
                JToken planResolved = replyData["plan_resolved"];
                if (planResolved != null)
                {
                    Copy(receipt, "plan_elements", planResolved["elements"]);
                    Copy(receipt, "plan_fingerprint", planResolved["fingerprint"]);
                }
                Copy(receipt, "all_verified", replyData["all_verified"]);
            }
            return receipt;
        }

        private static void Copy(JObject receipt, string name, JToken value)
        {
            if (value != null && value.Type != JTokenType.Null) receipt[name] = value.DeepClone();
        }

        /// <summary>
        /// Append a receipt and apply retention. The settings reader is injected (same
        /// contract as ReceiptRetention.FromSettings) so tests can drive policy without a
        /// real settings file. Returns true when the receipt reached disk.
        ///
        /// Retention runs AFTER the append, on the day-files older than today: the file
        /// being written is never a deletion candidate, so a mis-set policy can cost
        /// history but never the operation that just happened.
        /// </summary>
        public static bool Append(string directory, JObject receipt, Func<string, string> settings, DateTime utcNow)
        {
            try
            {
                lock (Gate)
                {
                    Directory.CreateDirectory(directory);
                    RetentionPolicy policy = ReceiptRetention.FromSettings(settings, out _);

                    string line = receipt.ToString(Formatting.None);
                    string redacted = ReceiptRetention.Redact(line, policy, out string patternError);
                    if (patternError != null)
                    {
                        // A redaction pattern that does not compile must not ship the
                        // UNREDACTED line - the pattern existed because something in these
                        // lines is sensitive. The receipt records that it withheld itself.
                        redacted = new JObject
                        {
                            ["operation_id"] = receipt["operation_id"]?.DeepClone(),
                            ["utc"] = receipt["utc"]?.DeepClone(),
                            ["tool"] = receipt["tool"]?.DeepClone(),
                            ["withheld"] = "a redact pattern in settings does not compile (" + patternError +
                                           "); the full receipt was NOT written rather than written unredacted"
                        }.ToString(Formatting.None);
                    }

                    string path = Path.Combine(directory,
                        "receipts-" + utcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".jsonl");
                    File.AppendAllText(path, redacted + Environment.NewLine);

                    ApplyRetention(directory, policy, utcNow);
                    return true;
                }
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _appendFailures);
                lock (Gate) { _lastAppendError = ex.Message; }
                return false;
            }
        }

        private static void ApplyRetention(string directory, RetentionPolicy policy, DateTime utcNow)
        {
            if (policy.KeepsForever && policy.MaxBytes <= 0) return;
            string today = "receipts-" + utcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".jsonl";
            var files = new List<ReceiptFile>();
            foreach (string f in Directory.GetFiles(directory, "receipts-*.jsonl"))
            {
                if (string.Equals(Path.GetFileName(f), today, StringComparison.OrdinalIgnoreCase)) continue;
                var info = new FileInfo(f);
                files.Add(new ReceiptFile { Path = f, WrittenUtc = info.LastWriteTimeUtc, Bytes = info.Length });
            }
            PurgeDecision decision = ReceiptRetention.Plan(files, policy, utcNow);
            foreach (ReceiptFile doomed in decision.Remove)
            {
                // Best effort per file: one locked day-file must not stop the others.
                try { File.Delete(doomed.Path); } catch { }
            }
        }

        /// <summary>The default ledger directory. Overridable for tests, never guessed at.</summary>
        public static string DefaultDirectory() =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".horizun", "receipts");
    }
}
