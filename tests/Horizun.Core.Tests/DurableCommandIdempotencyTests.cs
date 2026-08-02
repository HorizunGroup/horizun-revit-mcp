// -----------------------------------------------------------------------------
// Horizun Core tests - durable at-most-once behavior without Revit.
// -----------------------------------------------------------------------------
using System;
using System.IO;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public sealed class DurableCommandIdempotencyTests : IDisposable
    {
        private readonly string _dir = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "horizun-idem-" + Guid.NewGuid().ToString("N"));

        private DurableCommandLedger Ledger(int pid = 10) =>
            new DurableCommandLedger(() => _dir, () => new DateTime(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc), () => pid);

        [Fact]
        public void Completed_operation_replays_after_a_new_ledger_instance()
        {
            DurableCommandDecision first = Ledger().Claim("op-1", "write", "fp-a");
            Assert.Equal(DurableCommandOutcome.Fresh, first.Outcome);
            Ledger().Complete(first, CommandResult.Ok(new JObject { ["changed"] = 3 }));

            DurableCommandDecision retry = Ledger(99).Claim("op-1", "write", "fp-a");
            Assert.Equal(DurableCommandOutcome.Replay, retry.Outcome);
            Assert.True(retry.ReplayResult.Success);
            Assert.Equal(3, (int)((JObject)retry.ReplayResult.Data)["changed"]);
        }

        [Fact]
        public void Claim_without_completion_is_in_doubt_after_restart()
        {
            Ledger(10).Claim("op-2", "delete", "fp-b");
            DurableCommandDecision retry = Ledger(20).Claim("op-2", "delete", "fp-b");

            Assert.Equal(DurableCommandOutcome.InDoubt, retry.Outcome);
            Assert.Contains("will NOT repeat", retry.Message);
        }

        [Fact]
        public void Same_key_for_different_operation_is_refused()
        {
            Ledger().Claim("op-3", "write", "fp-one");
            DurableCommandDecision conflict = Ledger().Claim("op-3", "write", "fp-two");

            Assert.Equal(DurableCommandOutcome.Conflict, conflict.Outcome);
            Assert.Contains("DIFFERENT", conflict.Message);
        }

        [Fact]
        public void Torn_final_line_fails_closed_as_in_doubt()
        {
            DurableCommandDecision first = Ledger().Claim("op-4", "write", "fp-c");
            File.AppendAllText(first.Path, "{this completion was torn");

            DurableCommandDecision retry = Ledger(30).Claim("op-4", "write", "fp-c");
            Assert.Equal(DurableCommandOutcome.InDoubt, retry.Outcome);
        }

        [Fact]
        public void Torn_first_claim_never_looks_like_a_fresh_key()
        {
            Directory.CreateDirectory(_dir);
            string path = System.IO.Path.Combine(_dir, RequestFingerprint.Sha256Hex("op-torn") + ".jsonl");
            File.WriteAllText(path, "{\"event\":\"claimed\"");

            DurableCommandDecision retry = Ledger().Claim("op-torn", "write", "fp");
            Assert.Equal(DurableCommandOutcome.InDoubt, retry.Outcome);
            Assert.Contains("corrupt", retry.Message);
            Assert.Single(File.ReadAllLines(path));
        }

        [Fact]
        public void Failed_result_is_replayed_too()
        {
            DurableCommandDecision first = Ledger().Claim("op-5", "write", "fp-d");
            Ledger().Complete(first, CommandResult.Fail("Revit refused it"));

            DurableCommandDecision retry = Ledger().Claim("op-5", "write", "fp-d");
            Assert.Equal(DurableCommandOutcome.Replay, retry.Outcome);
            Assert.False(retry.ReplayResult.Success);
            Assert.Equal("Revit refused it", retry.ReplayResult.Error);
        }

        public void Dispose()
        {
            try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { }
        }
    }
}
