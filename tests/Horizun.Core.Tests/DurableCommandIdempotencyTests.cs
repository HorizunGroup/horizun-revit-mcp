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

        // ---------------------------------------------------------------------
        // A REPLAY MUST NOT DESCRIBE A SESSION THAT DIED
        // ---------------------------------------------------------------------

        [Fact]
        public void A_session_command_does_NOT_replay_into_a_different_process()
        {
            // MEASURED 2026-08-27: a harness reused its staging keys across a
            // deliberate Revit restart. All three opens replayed "opened,
            // active_document_verified: true", the harness believed them, and 57
            // probes then ran against a Revit with nothing open - reported as
            // command failures, which is the wrong place to look.
            DurableCommandDecision open = Ledger(pid: 10).Claim("stage-1", "horizun_document_session", "fp-open");
            Assert.Equal(DurableCommandOutcome.Fresh, open.Outcome);
            Ledger(pid: 10).Complete(open, CommandResult.Ok(new JObject
            {
                ["status"] = "opened",
                ["active_document_verified"] = true
            }));

            DurableCommandDecision afterRestart = Ledger(pid: 77).Claim("stage-1", "horizun_document_session", "fp-open");
            Assert.Equal(DurableCommandOutcome.Conflict, afterRestart.Outcome);
            Assert.Null(afterRestart.ReplayResult);
            Assert.Contains("stale_session_replay", afterRestart.Message);
            Assert.Contains("Use a NEW key", afterRestart.Message);
        }

        [Fact]
        public void The_SAME_process_still_replays_a_session_command_after_a_lost_reply()
        {
            // The at-most-once guarantee this exists for is unchanged: a client
            // that never received the answer retries, the session is the same
            // one, and the recorded answer is still true.
            DurableCommandDecision open = Ledger(pid: 10).Claim("stage-2", "horizun_document_session", "fp-open");
            Ledger(pid: 10).Complete(open, CommandResult.Ok(new JObject { ["status"] = "opened" }));

            DurableCommandDecision retry = Ledger(pid: 10).Claim("stage-2", "horizun_document_session", "fp-open");
            Assert.Equal(DurableCommandOutcome.Replay, retry.Outcome);
            Assert.Equal("opened", (string)((JObject)retry.ReplayResult.Data)["status"]);
        }

        [Fact]
        public void A_MODEL_WRITE_still_replays_across_a_restart_because_the_write_is_in_the_file()
        {
            // The distinction is the whole point. A committed write outlived the
            // process that made it, so its recorded answer is still true and
            // running it again would be the duplicate this ledger prevents.
            DurableCommandDecision write = Ledger(pid: 10).Claim("build-1", "horizun_create_elements", "fp-walls");
            Ledger(pid: 10).Complete(write, CommandResult.Ok(new JObject { ["created_verified"] = 3 }));

            DurableCommandDecision afterRestart = Ledger(pid: 88).Claim("build-1", "horizun_create_elements", "fp-walls");
            Assert.Equal(DurableCommandOutcome.Replay, afterRestart.Outcome);
            Assert.Equal(3, (int)((JObject)afterRestart.ReplayResult.Data)["created_verified"]);
        }

        [Fact]
        public void Every_session_scoped_command_is_covered_and_ordinary_ones_are_not()
        {
            foreach (string name in new[] { "horizun_document_session", "horizun_open_document",
                                            "horizun_navigate", "horizun_target",
                                            "horizun_request_python_access" })
                Assert.True(DurableCommandLedger.IsSessionScoped(name), name + " must not replay across processes");

            foreach (string name in new[] { "horizun_create_elements", "horizun_execute_plan",
                                            "horizun_write_params_verified", "horizun_delete_verified",
                                            "horizun_apply_cad_plan" })
                Assert.False(DurableCommandLedger.IsSessionScoped(name), name + " writes the FILE and must still replay");
        }
    }
}
