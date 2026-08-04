// -----------------------------------------------------------------------------
// Horizun Revit MCP - retention and redaction for the operation ledger.
// Original Horizun code.
//
// THE GAP THIS CLOSES, which the project already admitted it had: the durable
// idempotency records are kept forever and they can carry whatever a command
// returned - element ids, parameter values, model titles, file paths. Written
// once and never pruned, on the machine of somebody who audits other people's
// buildings. "We keep everything, indefinitely, unencrypted" is not a policy; it
// is the absence of one.
//
// So: an explicit retention window, an explicit size cap, and redaction the
// OPERATOR configures rather than the tool guessing. Nothing here deletes on a
// hunch - a record is dropped because it is older than a stated window or because
// the store is over a stated cap, and either way the purge REPORTS what it removed.
//
// Revit-free on purpose: dates, sizes and string rules. The facts need Revit, the
// policy does not, which is why every case below - a store over its cap, a
// retention window that would delete everything, a redaction pattern that matches
// the whole value - is provable in a unit test rather than discovered on somebody's
// machine a year from now.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Horizun.Revit.Core
{
    /// <summary>How long records are kept, and how much space they may take.</summary>
    public sealed class RetentionPolicy
    {
        /// <summary>
        /// Days to keep a record. Zero or negative means KEEP FOREVER - which stays the
        /// default, because silently deleting somebody's audit trail because they upgraded
        /// is worse than keeping too much. The operator opts in to forgetting.
        /// </summary>
        public int Days;

        /// <summary>
        /// Total bytes the store may occupy. Zero means no cap. Over the cap, the OLDEST
        /// records go first - a cap that dropped the newest would throw away the receipt for
        /// the operation somebody is asking about right now.
        /// </summary>
        public long MaxBytes;

        /// <summary>
        /// Patterns whose MATCHES are replaced in stored values. Supplied by the operator:
        /// this bridge cannot know which strings in a client's model are sensitive, and a
        /// tool that guessed would both miss things and mangle legitimate data.
        /// </summary>
        public List<string> RedactPatterns = new List<string>();

        public static RetentionPolicy Forever() => new RetentionPolicy { Days = 0, MaxBytes = 0 };

        /// <summary>The three named windows the policy document offers, plus manual.</summary>
        public static RetentionPolicy Days7() => new RetentionPolicy { Days = 7 };
        public static RetentionPolicy Days30() => new RetentionPolicy { Days = 30 };
        public static RetentionPolicy Days90() => new RetentionPolicy { Days = 90 };

        public bool KeepsForever => Days <= 0;
    }

    /// <summary>One record as the purge sees it: when it was written and how big it is.</summary>
    public sealed class ReceiptFile
    {
        public string Path;
        public DateTime WrittenUtc;
        public long Bytes;
    }

    /// <summary>What a purge would do, or did. Always reported, never silent.</summary>
    public sealed class PurgeDecision
    {
        public List<ReceiptFile> Remove = new List<ReceiptFile>();
        public List<ReceiptFile> Keep = new List<ReceiptFile>();

        public long BytesRemoved { get { long n = 0; foreach (var f in Remove) n += f.Bytes; return n; } }
        public long BytesKept { get { long n = 0; foreach (var f in Keep) n += f.Bytes; return n; } }

        /// <summary>Why each removal happened, so a purge can be audited like anything else.</summary>
        public List<string> Reasons = new List<string>();

        public string Summary()
        {
            if (Remove.Count == 0)
                return "Nothing to purge: " + Keep.Count + " record(s), " + BytesKept + " bytes, all within policy.";
            return "Removing " + Remove.Count + " record(s) (" + BytesRemoved + " bytes); keeping " +
                   Keep.Count + " (" + BytesKept + " bytes). " + string.Join(" ", Reasons.ToArray());
        }
    }

    public static class ReceiptRetention
    {
        /// <summary>
        /// Decide what a purge would remove. Pure: it takes the file list and gives back a
        /// decision, so the caller can show it before doing anything - the same dry-run
        /// discipline every write command here follows.
        /// </summary>
        public static PurgeDecision Plan(IEnumerable<ReceiptFile> files, RetentionPolicy policy, DateTime utcNow)
        {
            var decision = new PurgeDecision();
            var all = new List<ReceiptFile>();
            foreach (ReceiptFile f in files ?? new ReceiptFile[0]) if (f != null) all.Add(f);

            // Oldest first, so both rules below walk the same order and a record dropped by
            // age is not then also counted by the cap.
            all.Sort((a, b) => a.WrittenUtc.CompareTo(b.WrittenUtc));

            if (policy == null) policy = RetentionPolicy.Forever();

            var survivors = new List<ReceiptFile>();
            if (policy.KeepsForever)
            {
                survivors.AddRange(all);
            }
            else
            {
                DateTime cutoff = utcNow.AddDays(-policy.Days);
                int aged = 0;
                foreach (ReceiptFile f in all)
                {
                    if (f.WrittenUtc < cutoff) { decision.Remove.Add(f); aged++; }
                    else survivors.Add(f);
                }
                if (aged > 0)
                    decision.Reasons.Add(aged + " older than the " + policy.Days + "-day window (before " +
                                         cutoff.ToString("u", CultureInfo.InvariantCulture) + ").");
            }

            if (policy.MaxBytes > 0)
            {
                long total = 0;
                foreach (ReceiptFile f in survivors) total += f.Bytes;
                int overflowed = 0;
                // Oldest first: a cap that dropped the newest would throw away the receipt
                // for the operation somebody is asking about right now.
                int i = 0;
                while (total > policy.MaxBytes && i < survivors.Count)
                {
                    decision.Remove.Add(survivors[i]);
                    total -= survivors[i].Bytes;
                    overflowed++;
                    i++;
                }
                survivors.RemoveRange(0, i);
                if (overflowed > 0)
                    decision.Reasons.Add(overflowed + " oldest dropped to get under the " +
                                         policy.MaxBytes + "-byte cap.");
            }

            decision.Keep.AddRange(survivors);
            return decision;
        }

        /// <summary>
        /// Apply the operator's redaction patterns to one stored value.
        ///
        /// A pattern that matches EVERYTHING is honoured rather than second-guessed - an
        /// operator who redacts every value has decided that receipts record what happened
        /// and not to what, which is a legitimate choice in a consultancy holding other
        /// people's models. An invalid pattern is the one thing that is not silently
        /// ignored: it is reported, because a redaction rule that never fires is worse than
        /// no rule at all - it reads like protection.
        /// </summary>
        public static string Redact(string value, RetentionPolicy policy, out string patternError)
        {
            patternError = null;
            if (string.IsNullOrEmpty(value) || policy == null || policy.RedactPatterns == null) return value;

            string result = value;
            var bad = new List<string>();
            foreach (string p in policy.RedactPatterns)
            {
                if (string.IsNullOrWhiteSpace(p)) continue;
                try
                {
                    result = Regex.Replace(result, p, "[redacted]", RegexOptions.IgnoreCase,
                                           TimeSpan.FromMilliseconds(250));
                }
                catch (ArgumentException) { bad.Add(p); }
                catch (RegexMatchTimeoutException) { bad.Add(p + " (timed out)"); }
            }
            if (bad.Count > 0)
                patternError = "These redaction patterns did not run and redacted NOTHING: " +
                               string.Join(", ", bad.ToArray()) +
                               ". Fix them - a rule that never fires reads like protection.";
            return result;
        }

        /// <summary>
        /// Read a policy from the settings object the operator edits. Unknown shapes fall
        /// back to keeping forever and SAY why, rather than choosing a window nobody asked
        /// for: deleting an audit trail because a setting was malformed is not a default.
        /// </summary>
        public static RetentionPolicy FromSettings(Func<string, string> get, out string note)
        {
            note = null;
            var policy = RetentionPolicy.Forever();
            if (get == null) return policy;

            string days = get("receipt_retention_days");
            if (!string.IsNullOrWhiteSpace(days))
            {
                int d;
                if (int.TryParse(days, NumberStyles.Integer, CultureInfo.InvariantCulture, out d) && d >= 0)
                    policy.Days = d;
                else
                    note = "receipt_retention_days is not a whole number of days ('" + days +
                           "'), so records are being KEPT FOREVER. Nothing was deleted.";
            }

            // The patterns the redactor applies. RedactPatterns existed on the policy
            // from the start and NOTHING populated it - found by the ledger's own test
            // leaking a document name straight past a "configured" pattern. A JSON array
            // ("[\"proyecto-x\", \"cliente\"]") carries several; any other non-empty
            // text is ONE pattern, taken whole. A value that looks like an array but
            // does not parse is kept as one pattern too - it will fail to compile in
            // Redact, which WITHHOLDS rather than leaks, and that is the safe direction
            // for a string somebody meant as a secret-matcher.
            string patterns = get("receipt_redact_patterns");
            if (!string.IsNullOrWhiteSpace(patterns))
            {
                bool parsedAsArray = false;
                if (patterns.TrimStart().StartsWith("[", StringComparison.Ordinal))
                {
                    try
                    {
                        foreach (var tok in Newtonsoft.Json.Linq.JArray.Parse(patterns))
                            if (tok.Type == Newtonsoft.Json.Linq.JTokenType.String)
                                policy.RedactPatterns.Add((string)tok);
                        parsedAsArray = true;
                    }
                    catch { }
                }
                if (!parsedAsArray) policy.RedactPatterns.Add(patterns);
            }

            string cap = get("receipt_max_bytes");
            if (!string.IsNullOrWhiteSpace(cap))
            {
                long b;
                if (long.TryParse(cap, NumberStyles.Integer, CultureInfo.InvariantCulture, out b) && b >= 0)
                    policy.MaxBytes = b;
                else
                    note = (note == null ? "" : note + " ") +
                           "receipt_max_bytes is not a byte count ('" + cap + "'), so no cap is applied.";
            }
            return policy;
        }
    }
}
