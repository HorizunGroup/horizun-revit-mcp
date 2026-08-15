// -----------------------------------------------------------------------------
// Horizun Core tests - a replay is the SAME answer, not a summary of it.
//
// The ledger existed to make a lost reply safe: claim the key, run the mutation,
// record the answer, and hand the recorded answer back to a retry instead of
// writing twice. It recorded four things - success, data, error, revit_said -
// and a CommandResult carries seven. The three it dropped are the three a client
// is supposed to BRANCH on:
//
//   Fallback         whether generating Python is the correct next move
//   CapabilityGaps   which actions of a batch have no typed path
//   Detail           whether a failed plan's rollback actually landed
//
// So the first response could say "no typed capability covers this, and nothing
// was written - go to Python", and the replay of that same operation came back
// as a bare failure with the same sentence and no signal at all. The client that
// retried because it never saw the first answer is precisely the client with no
// other way to learn it. Worse on the rollback path: the first answer said "the
// group rolled back, the model is untouched"; the replay said only that it
// failed, and "did anything land?" became unanswerable.
//
// The oracle here is PipeEnvelope.Of - the actual wire shape - compared with
// DeepEquals. Asserting field by field is what let three fields go missing.
// -----------------------------------------------------------------------------
using System;
using System.IO;
using Horizun.Revit.Core;
using Horizun.Revit.Transport;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public sealed class DurableReplayFidelityTests : IDisposable
    {
        private readonly string _dir = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "horizun-replay-" + Guid.NewGuid().ToString("N"));

        private DurableCommandLedger Ledger(int pid = 10) =>
            new DurableCommandLedger(() => _dir, () => new DateTime(2026, 8, 14, 9, 0, 0, DateTimeKind.Utc), () => pid);

        /// <summary>
        /// The whole property in one line: what the retry receives is byte-for-byte the
        /// envelope the first caller would have received.
        /// </summary>
        private void AssertReplayIsIdentical(string key, string command, string fingerprint, CommandResult first)
        {
            DurableCommandDecision claim = Ledger().Claim(key, command, fingerprint);
            Assert.Equal(DurableCommandOutcome.Fresh, claim.Outcome);
            Ledger().Complete(claim, first);

            DurableCommandDecision retry = Ledger(999).Claim(key, command, fingerprint);
            Assert.Equal(DurableCommandOutcome.Replay, retry.Outcome);

            JObject expected = PipeEnvelope.Of("req-1", first);
            JObject replayed = PipeEnvelope.Of("req-1", retry.ReplayResult);

            Assert.True(JToken.DeepEquals(expected, replayed),
                "The replayed envelope is not the first answer.\nfirst:  " + expected.ToString(Newtonsoft.Json.Formatting.None) +
                "\nreplay: " + replayed.ToString(Newtonsoft.Json.Formatting.None));
        }

        [Fact]
        public void A_granted_fallback_survives_the_replay()
        {
            CommandResult first = CommandResult.FailWithFallback(
                "No typed capability covers kind 'toposolid_grade'.",
                FallbackSignal.Allowed(FallbackSignal.ReasonUnsupportedKind),
                null);

            AssertReplayIsIdentical("fb-allowed", "horizun_create_elements", "fp-1", first);
        }

        [Fact]
        public void A_refused_fallback_after_a_partial_write_survives_the_replay()
        {
            // The dangerous one. If this replays as a bare failure, a client that reads
            // "no signal" the way it reads "no capability" turns a partial write into a
            // second write.
            CommandResult first = CommandResult.FailWithFallback(
                "The write failed after the transaction opened.",
                FallbackSignal.NotAllowed("write_failed_midway", writeStarted: true),
                null);

            AssertReplayIsIdentical("fb-refused", "horizun_write_params_verified", "fp-2", first);

            DurableCommandDecision retry = Ledger().Claim("fb-refused", "horizun_write_params_verified", "fp-2");
            Assert.False(retry.ReplayResult.Fallback.IsAllowed);
            Assert.True(retry.ReplayResult.Fallback.WriteStarted);
        }

        [Fact]
        public void Capability_gaps_survive_the_replay_with_their_indices()
        {
            var gaps = new JArray
            {
                new JObject
                {
                    ["index"] = 1,
                    ["reason"] = FallbackSignal.ReasonUnsupportedKind,
                    ["recommended_tool"] = FallbackSignal.RecommendedTool
                },
                new JObject
                {
                    ["index"] = 4,
                    ["reason"] = FallbackSignal.ReasonUnsupportedCategory,
                    ["recommended_tool"] = FallbackSignal.RecommendedTool
                }
            };

            CommandResult first = CommandResult.FailWithFallback(
                "2 of 5 actions have no typed path.",
                FallbackSignal.NotAllowed("mixed_capability_and_invalid_input", writeStarted: false),
                gaps);

            AssertReplayIsIdentical("gaps-mixed", "horizun_execute_plan", "fp-3", first);

            DurableCommandDecision retry = Ledger().Claim("gaps-mixed", "horizun_execute_plan", "fp-3");
            Assert.Equal(new[] { 1, 4 },
                System.Linq.Enumerable.ToArray(
                    System.Linq.Enumerable.Select(retry.ReplayResult.CapabilityGaps, g => (int)g["index"])));
        }

        [Fact]
        public void A_rollback_diagnostic_survives_the_replay()
        {
            var detail = new JObject
            {
                ["transaction_group_started"] = true,
                ["rollback_status"] = "rolled_back",
                ["actions_committed"] = 0,
                ["model_state"] = "unchanged"
            };

            CommandResult first = CommandResult.FailWithDetail(
                "Action 3 of 6 failed; the group was rolled back.", detail);

            AssertReplayIsIdentical("detail-rollback", "horizun_execute_plan", "fp-4", first);

            DurableCommandDecision retry = Ledger().Claim("detail-rollback", "horizun_execute_plan", "fp-4");
            Assert.Equal("rolled_back", (string)retry.ReplayResult.Detail["rollback_status"]);
        }

        [Fact]
        public void A_success_carrying_a_dry_run_grant_survives_the_replay()
        {
            // dry_run defaults to true and a SUCCESSFUL rehearsal publishes the verdict
            // beside its payload. Success plus a fallback signal is an ordinary shape,
            // not a contradiction, and it has to replay as one.
            CommandResult first = CommandResult.Ok(new JObject { ["planned"] = 12, ["dry_run"] = true });
            FallbackDecision.Attach(first, FallbackDecision.Decide(
                new[]
                {
                    new ActionOutcome
                    {
                        Index = 0,
                        Error = "no typed path for this kind",
                        UnsupportedReason = FallbackSignal.ReasonUnsupportedOperation
                    }
                },
                writeStarted: false));

            AssertReplayIsIdentical("ok-with-grant", "horizun_execute_plan", "fp-5", first);
        }

        [Fact]
        public void Revit_said_still_survives_beside_the_new_fields()
        {
            CommandResult first = CommandResult.Ok(new JObject { ["changed"] = 2 });
            first.RevitSaid = new JObject
            {
                ["warnings"] = new JArray { "Elements have duplicate 'Mark' values." }
            };

            AssertReplayIsIdentical("revit-said", "horizun_write_params_verified", "fp-6", first);
        }

        [Fact]
        public void A_plain_failure_replays_with_no_signal_invented()
        {
            // The other direction: absence must survive too. A replay that manufactured
            // an empty fallback block would be telling a client something the first
            // answer never said.
            CommandResult first = CommandResult.Fail("Element 418394 is pinned.");

            AssertReplayIsIdentical("plain-fail", "horizun_transform_elements", "fp-7", first);

            DurableCommandDecision retry = Ledger().Claim("plain-fail", "horizun_transform_elements", "fp-7");
            Assert.Null(retry.ReplayResult.Fallback);
            Assert.Null(retry.ReplayResult.CapabilityGaps);
            Assert.Null(retry.ReplayResult.Detail);
        }

        [Fact]
        public void An_impossible_recorded_signal_is_in_doubt_rather_than_quietly_repaired()
        {
            // allowed=true with write_started=true cannot be produced by FallbackSignal -
            // the constructor refuses it. If it is in the ledger, the ledger is not
            // describing anything this code produced, and rebuilding it through
            // Allowed() would silently "fix" it into a grant after a partial write.
            DurableCommandDecision claim = Ledger().Claim("corrupt-signal", "horizun_create_elements", "fp-8");
            Ledger().Complete(claim, CommandResult.FailWithFallback(
                "no typed path", FallbackSignal.Allowed(FallbackSignal.ReasonUnsupportedKind), null));

            string text = File.ReadAllText(claim.Path).Replace("\"write_started\":false", "\"write_started\":true");
            File.WriteAllText(claim.Path, text);

            DurableCommandDecision retry = Ledger(77).Claim("corrupt-signal", "horizun_create_elements", "fp-8");
            Assert.Equal(DurableCommandOutcome.InDoubt, retry.Outcome);
        }

        public void Dispose()
        {
            try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
