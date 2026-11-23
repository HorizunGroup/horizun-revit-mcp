using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    /// <summary>INT-11 D.5: accept_findings=true accepts only unverified scripts; a failed check needs every one of its ids named.</summary>
    public class Int11AcceptanceTests
    {
        private static SyncLedgerEntry Python() => new SyncLedgerEntry { Tool = "horizun_execute_python", EvidenceStatus = "completed_unverified", Ids = new long[] { 7 } };
        private static SyncLedgerEntry Check(params long[] ids) => new SyncLedgerEntry { Tool = "horizun_create_elements", SpatialStatus = "conflicts", SpatialErrors = 1, Ids = ids };
        private static SyncLedgerEntry PostCommit(params long[] ids) => new SyncLedgerEntry { Tool = "horizun_create_railing", VerificationFailed = true, Ids = ids };

        [Fact]
        public void Accept_findings_true_does_not_accept_a_failed_check()
        {
            var a = SyncLedgerRules.Accept(new List<SyncLedgerEntry> { Check(11, 12) }, acceptFindings: true, acceptIds: null, strict: false);
            Assert.False(a.Proceed);
            Assert.Equal("sync_blocked_unverified", a.Code);
            Assert.Equal(new long[] { 11, 12 }, a.MissingIds);
            string message = SyncLedgerRules.FailedChecksMessage("L2", a, new SyncOnlyGrantRules.BlockedWayOut { AcceptFindingsOffered = true });
            Assert.Contains("11, 12", message);
            Assert.Contains("horizun_verify_changes", message);
            Assert.Contains("accept_findings_ids", message);
            Assert.Contains("Nothing was synchronized", message);
        }

        [Fact]
        public void Accept_findings_ids_naming_every_id_accepts_the_failed_checks()
        {
            var blockers = new List<SyncLedgerEntry> { Check(11, 12), PostCommit(13) };
            var a = SyncLedgerRules.Accept(blockers, acceptFindings: false, acceptIds: new long[] { 13, 12, 11 }, strict: false);
            Assert.True(a.Proceed);
            Assert.Null(a.Code);
            Assert.Equal(2, a.AcceptedChecks);
            Assert.Empty(a.MissingIds);
        }

        [Fact]
        public void Incomplete_ids_are_refused_naming_the_missing_ones()
        {
            var a = SyncLedgerRules.Accept(new List<SyncLedgerEntry> { Check(11, 12), Check(20) }, acceptFindings: true, acceptIds: new long[] { 11, 20, 99 }, strict: false);
            Assert.False(a.Proceed);
            Assert.Equal("sync_blocked_unverified", a.Code);
            Assert.Equal(new long[] { 12 }, a.MissingIds);
            Assert.Equal(new long[] { 99 }, a.UnusedIds);
            JObject with = SyncLedgerRules.AcceptFindingsWith(a);
            Assert.Equal(new long[] { 12 }, with["failed_checks"]["missing_ids"].Values<long>());
            Assert.Equal("accept_findings_ids", (string)with["failed_checks"]["argument"]);
            Assert.Contains("unverified_python", (string)with["accepts"]);
        }

        [Fact]
        public void Unverified_python_is_still_accepted_by_accept_findings_and_only_by_it()
        {
            var accepted = SyncLedgerRules.Accept(new List<SyncLedgerEntry> { Python() }, acceptFindings: true, acceptIds: null, strict: false);
            Assert.True(accepted.Proceed);
            Assert.Equal(1, accepted.AcceptedPython);
            var byIds = SyncLedgerRules.Accept(new List<SyncLedgerEntry> { Python() }, acceptFindings: false, acceptIds: new long[] { 7 }, strict: false);
            Assert.False(byIds.Proceed);
            Assert.Equal("sync_blocked_unverified", byIds.Code);
            // Both kinds: each acceptance takes its own.
            var both = SyncLedgerRules.Accept(new List<SyncLedgerEntry> { Python(), Check(5) }, acceptFindings: true, acceptIds: new long[] { 5 }, strict: false);
            Assert.True(both.Proceed);
        }

        [Fact]
        public void A_failed_check_without_ids_is_blocked_and_strict_accepts_no_findings()
        {
            var noIds = SyncLedgerRules.Accept(new List<SyncLedgerEntry> { Check() }, acceptFindings: true, acceptIds: new long[] { 1 }, strict: false);
            Assert.False(noIds.Proceed);
            Assert.Equal(1, noIds.ChecksWithoutIds);
            var strict = SyncLedgerRules.Accept(new List<SyncLedgerEntry> { Python(), Check(5) }, acceptFindings: true, acceptIds: new long[] { 5 }, strict: true);
            Assert.False(strict.Proceed);
            Assert.Equal(2, strict.Remaining.Count);
            Assert.Equal("unverified_python", strict.Remaining[0].BlockKind);
            Assert.Equal("sync_only_no_accept_findings", SyncOnlyGrantRules.RequestRefusal("c", SyncRelinquish.All, false, acceptFindingsIds: true).Code);
            Assert.Equal("sync_only_no_accept_findings", SyncOnlyGrantRules.RequestRefusal("c", SyncRelinquish.All, true).Code);
            Assert.False(SyncLedgerRules.Accept(new List<SyncLedgerEntry> { Check(5) }, false, new long[] { 5 }, true).Proceed);
        }

        [Fact]
        public void Accept_findings_ids_is_read_as_integers_only_and_normalized()
        {
            Assert.True(SyncLedgerRules.TryReadAcceptIds(null, out List<long> none, out _));
            Assert.Empty(none);
            Assert.True(SyncLedgerRules.TryReadAcceptIds(new JArray(3, 1, 3), out List<long> ids, out _));
            Assert.Equal(new long[] { 1, 3 }, ids);
            Assert.False(SyncLedgerRules.TryReadAcceptIds(new JArray("1"), out _, out string e1));
            Assert.Contains("accept_findings_ids", e1);
            Assert.False(SyncLedgerRules.TryReadAcceptIds(new JValue(1), out _, out _));
        }

        [Fact]
        public void The_generic_refusal_names_the_acceptance_of_each_kind()
        {
            var offered = new SyncOnlyGrantRules.BlockedWayOut { AcceptFindingsOffered = true };
            string checks = SyncLedgerRules.BlockedMessage("M", new List<SyncLedgerEntry> { Check(1) }, offered);
            Assert.Contains("accept_findings_ids", checks);
            Assert.Contains("does not accept a failed check", checks);
            string python = SyncLedgerRules.BlockedMessage("M", new List<SyncLedgerEntry> { Python() }, offered);
            Assert.Contains("accept_findings=true", python);
            Assert.DoesNotContain("accept_findings_ids", python);
        }
    }
}
