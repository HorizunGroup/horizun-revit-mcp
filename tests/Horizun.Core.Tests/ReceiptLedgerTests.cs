// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// The receipt ledger: 5.2's writer. The properties under test are the ones that
// make a diary trustworthy: entries copied not inferred, a bad redaction pattern
// withholding rather than leaking, retention never eating today's file, and a
// failed append counted instead of silent.
// -----------------------------------------------------------------------------
using System;
using System.IO;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class ReceiptLedgerTests : IDisposable
    {
        private readonly string _dir;
        public ReceiptLedgerTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "horizun-ledger-" + Guid.NewGuid().ToString("n"));
        }
        public void Dispose()
        {
            try { Directory.Delete(_dir, true); } catch { }
        }

        private static readonly DateTime Now = new DateTime(2026, 8, 4, 23, 0, 0, DateTimeKind.Utc);

        [Fact]
        public void A_receipt_copies_what_the_reply_carried_and_infers_nothing()
        {
            var reply = new JObject
            {
                ["document"] = "Torre.rvt",
                ["document_fingerprint"] = "abc123",
                ["transaction_status"] = "Committed",
                ["plan_resolved"] = new JObject { ["elements"] = 7, ["fingerprint"] = "fp" }
            };
            JObject r = ReceiptLedger.Build("horizun_write_params_verified", true, null, reply, 12, 340, "42", Now);
            Assert.Equal("ok", (string)r["outcome"]);
            Assert.Equal("Torre.rvt", (string)r["document"]);
            Assert.Equal(7, (int)r["plan_elements"]);
            Assert.Equal("fp", (string)r["plan_fingerprint"]);

            // A reply WITHOUT those fields yields a receipt without them - the reader
            // sees the absence instead of a guess.
            JObject bare = ReceiptLedger.Build("horizun_health", true, null, new JObject(), 0, 5, "43", Now);
            Assert.Null(bare["transaction_status"]);
            Assert.Null(bare["plan_fingerprint"]);
        }

        [Fact]
        public void Appends_land_in_one_jsonl_per_utc_day()
        {
            JObject r = ReceiptLedger.Build("t", true, null, null, 0, 1, "1", Now);
            Assert.True(ReceiptLedger.Append(_dir, r, _ => null, Now));
            Assert.True(ReceiptLedger.Append(_dir, r, _ => null, Now));
            string path = Path.Combine(_dir, "receipts-2026-08-04.jsonl");
            Assert.True(File.Exists(path));
            Assert.Equal(2, File.ReadAllLines(path).Length);
        }

        /// <summary>
        /// The pattern existed because something in these lines is sensitive, so a
        /// pattern that does not compile must not ship the unredacted line. The entry
        /// says it withheld itself - a ledger that drops entries silently reads like a
        /// quiet day.
        /// </summary>
        [Fact]
        public void A_broken_redact_pattern_withholds_the_receipt_rather_than_leaking_it()
        {
            JObject r = ReceiptLedger.Build("t", true, null,
                new JObject { ["document"] = "SECRETO.rvt" }, 0, 1, "1", Now);
            Assert.True(ReceiptLedger.Append(_dir, r,
                key => key == "receipt_redact_patterns" ? "[unclosed(" : null, Now));
            string line = File.ReadAllLines(Path.Combine(_dir, "receipts-2026-08-04.jsonl")).Single();
            Assert.DoesNotContain("SECRETO", line);
            Assert.Contains("withheld", line);
            Assert.Contains("NOT written rather than written unredacted", line);
        }

        /// <summary>
        /// Retention acts only on day-files OLDER than today: the file being written is
        /// never a deletion candidate, so a mis-set policy can cost history but never
        /// the operation that just happened.
        /// </summary>
        [Fact]
        public void Retention_never_deletes_today()
        {
            Directory.CreateDirectory(_dir);
            string old = Path.Combine(_dir, "receipts-2026-01-01.jsonl");
            File.WriteAllText(old, "{}\n");
            File.SetLastWriteTimeUtc(old, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

            JObject r = ReceiptLedger.Build("t", true, null, null, 0, 1, "1", Now);
            Assert.True(ReceiptLedger.Append(_dir, r,
                key => key == "receipt_retention_days" ? "7" : null, Now));

            Assert.False(File.Exists(old), "the 7-day policy should have removed January");
            Assert.True(File.Exists(Path.Combine(_dir, "receipts-2026-08-04.jsonl")),
                "today's file must never be a deletion candidate");
        }

        /// <summary>Malformed settings keep everything - the retention module's own rule, respected end to end.</summary>
        [Fact]
        public void Malformed_retention_settings_never_delete()
        {
            Directory.CreateDirectory(_dir);
            string old = Path.Combine(_dir, "receipts-2026-01-01.jsonl");
            File.WriteAllText(old, "{}\n");
            File.SetLastWriteTimeUtc(old, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

            JObject r = ReceiptLedger.Build("t", true, null, null, 0, 1, "1", Now);
            Assert.True(ReceiptLedger.Append(_dir, r,
                key => key == "receipt_retention_days" ? "siete" : null, Now));
            Assert.True(File.Exists(old), "a setting that does not parse must keep history, not guess a policy");
        }

        [Fact]
        public void A_failed_append_is_counted_not_silent()
        {
            long before = ReceiptLedger.AppendFailures;
            // A directory path that is a FILE forces the failure.
            string blocked = Path.Combine(Path.GetTempPath(), "horizun-ledger-blocked-" + Guid.NewGuid().ToString("n"));
            File.WriteAllText(blocked, "not a directory");
            try
            {
                JObject r = ReceiptLedger.Build("t", true, null, null, 0, 1, "1", Now);
                Assert.False(ReceiptLedger.Append(blocked, r, _ => null, Now));
                Assert.True(ReceiptLedger.AppendFailures > before);
                Assert.NotNull(ReceiptLedger.LastAppendError);
            }
            finally { File.Delete(blocked); }
        }
    }
}
