// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// Retention deletes somebody's audit trail. Every rule below is therefore proved
// rather than trusted, including the two that must NOT happen: a malformed setting
// must never be read as permission to delete, and a size cap must never drop the
// newest receipt - the one somebody is asking about right now.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class ReceiptRetentionTests
    {
        private static readonly DateTime Now = new DateTime(2026, 8, 4, 12, 0, 0, DateTimeKind.Utc);

        private static ReceiptFile F(string name, int daysOld, long bytes = 1000) =>
            new ReceiptFile { Path = name, WrittenUtc = Now.AddDays(-daysOld), Bytes = bytes };

        [Fact]
        public void Keeping_forever_is_the_default_and_removes_nothing()
        {
            PurgeDecision d = ReceiptRetention.Plan(
                new[] { F("a", 3000), F("b", 1) }, RetentionPolicy.Forever(), Now);
            Assert.Empty(d.Remove);
            Assert.Equal(2, d.Keep.Count);
            Assert.Contains("all within policy", d.Summary());
        }

        [Fact]
        public void A_window_removes_only_what_is_older_than_it()
        {
            PurgeDecision d = ReceiptRetention.Plan(
                new[] { F("old", 31), F("edge", 29), F("new", 1) }, RetentionPolicy.Days30(), Now);
            Assert.Single(d.Remove);
            Assert.Equal("old", d.Remove[0].Path);
            Assert.Equal(2, d.Keep.Count);
            Assert.Contains("30-day window", d.Summary());
        }

        /// <summary>
        /// A cap that dropped the newest would throw away the receipt for the operation
        /// somebody is asking about right now. Oldest first, always.
        /// </summary>
        [Fact]
        public void A_size_cap_drops_the_oldest_never_the_newest()
        {
            var policy = new RetentionPolicy { Days = 0, MaxBytes = 2500 };
            PurgeDecision d = ReceiptRetention.Plan(
                new[] { F("oldest", 10), F("middle", 5), F("newest", 1) }, policy, Now);

            Assert.Single(d.Remove);
            Assert.Equal("oldest", d.Remove[0].Path);
            Assert.Contains(d.Keep, k => k.Path == "newest");
            Assert.Contains("cap", d.Summary());
        }

        [Fact]
        public void A_store_already_under_the_cap_is_left_alone()
        {
            var policy = new RetentionPolicy { Days = 0, MaxBytes = 10000 };
            PurgeDecision d = ReceiptRetention.Plan(new[] { F("a", 2), F("b", 1) }, policy, Now);
            Assert.Empty(d.Remove);
        }

        /// <summary>
        /// Age and cap must not double-count: a record removed for being old is not then
        /// also counted against the cap, which would make the purge remove more than either
        /// rule asked for.
        /// </summary>
        [Fact]
        public void Age_and_cap_together_do_not_remove_the_same_record_twice()
        {
            var policy = new RetentionPolicy { Days = 30, MaxBytes = 1500 };
            PurgeDecision d = ReceiptRetention.Plan(
                new[] { F("ancient", 90), F("recent", 2), F("newest", 1) }, policy, Now);

            var removed = new List<string>();
            foreach (var f in d.Remove) removed.Add(f.Path);
            Assert.Equal(removed.Count, new HashSet<string>(removed).Count);   // no duplicates
            Assert.Contains("ancient", removed);
            Assert.Contains(d.Keep, k => k.Path == "newest");
        }

        [Fact]
        public void The_purge_always_reports_what_it_removed_and_why()
        {
            PurgeDecision d = ReceiptRetention.Plan(new[] { F("old", 100) }, RetentionPolicy.Days7(), Now);
            Assert.Contains("Removing 1 record", d.Summary());
            Assert.NotEmpty(d.Reasons);
        }

        [Fact]
        public void An_empty_store_is_not_an_error()
        {
            PurgeDecision d = ReceiptRetention.Plan(new ReceiptFile[0], RetentionPolicy.Days30(), Now);
            Assert.Empty(d.Remove);
            Assert.Empty(d.Keep);
        }

        // ---- redaction ----

        [Fact]
        public void A_supplied_pattern_redacts_its_matches()
        {
            var p = new RetentionPolicy { RedactPatterns = new List<string> { @"TOWER_[A-Z]" } };
            string err;
            string outv = ReceiptRetention.Redact("wall in TOWER_A level 3", p, out err);
            Assert.Equal("wall in [redacted] level 3", outv);
            Assert.Null(err);
        }

        /// <summary>
        /// An operator who redacts everything has decided that receipts record what happened
        /// and not to what. That is a legitimate choice for a consultancy holding other
        /// people's models, and it is honoured rather than second-guessed.
        /// </summary>
        [Fact]
        public void A_pattern_that_matches_everything_is_honoured()
        {
            var p = new RetentionPolicy { RedactPatterns = new List<string> { ".+" } };
            string err;
            Assert.Equal("[redacted]", ReceiptRetention.Redact("anything at all", p, out err));
            Assert.Null(err);
        }

        /// <summary>
        /// The one thing that must not pass quietly: a rule that never fires reads exactly
        /// like protection.
        /// </summary>
        [Fact]
        public void An_invalid_pattern_is_reported_and_not_silently_ignored()
        {
            var p = new RetentionPolicy { RedactPatterns = new List<string> { "([unclosed" } };
            string err;
            string outv = ReceiptRetention.Redact("TOWER_A", p, out err);
            Assert.Equal("TOWER_A", outv);
            Assert.NotNull(err);
            Assert.Contains("redacted NOTHING", err);
        }

        [Fact]
        public void No_patterns_means_the_value_is_untouched()
        {
            string err;
            Assert.Equal("v", ReceiptRetention.Redact("v", RetentionPolicy.Forever(), out err));
            Assert.Null(err);
        }

        // ---- settings ----

        /// <summary>
        /// THE RULE THAT MATTERS MOST HERE: a malformed setting must never be read as
        /// permission to delete. Deleting an audit trail because a number was mistyped is
        /// not a default anybody chose.
        /// </summary>
        [Fact]
        public void A_malformed_retention_setting_keeps_everything_and_says_why()
        {
            string note;
            RetentionPolicy p = ReceiptRetention.FromSettings(
                k => k == "receipt_retention_days" ? "thirty" : null, out note);

            Assert.True(p.KeepsForever);
            Assert.NotNull(note);
            Assert.Contains("FOREVER", note);
            Assert.Contains("Nothing was deleted", note);
        }

        [Fact]
        public void A_valid_setting_is_read()
        {
            string note;
            RetentionPolicy p = ReceiptRetention.FromSettings(
                k => k == "receipt_retention_days" ? "30" : (k == "receipt_max_bytes" ? "1048576" : null), out note);
            Assert.Equal(30, p.Days);
            Assert.Equal(1048576, p.MaxBytes);
            Assert.Null(note);
        }

        [Fact]
        public void No_settings_at_all_keeps_forever()
        {
            string note;
            RetentionPolicy p = ReceiptRetention.FromSettings(k => null, out note);
            Assert.True(p.KeepsForever);
            Assert.Equal(0, p.MaxBytes);
        }

        [Fact]
        public void A_malformed_cap_leaves_the_cap_off_and_says_so()
        {
            string note;
            RetentionPolicy p = ReceiptRetention.FromSettings(
                k => k == "receipt_max_bytes" ? "1GB" : null, out note);
            Assert.Equal(0, p.MaxBytes);
            Assert.NotNull(note);
            Assert.Contains("no cap is applied", note);
        }
    }
}
