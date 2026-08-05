// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// THE PERMISSION TO WRITE PYTHON, as data. The policy shipped first as prose in
// the server instructions, which helps an obedient model and gives a program
// nothing to branch on. These assert the two properties a client leans on:
//
//   - allowed=true appears ONLY for a pre-write capability gap, and
//   - it can never travel with write_started=true.
//
// The second is the expensive one. A granted fallback after a partial write
// invites a second write dressed up as a recovery.
// -----------------------------------------------------------------------------
using System;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class FallbackSignalTests
    {
        [Fact]
        public void An_allowed_signal_names_python_and_never_reports_a_started_write()
        {
            FallbackSignal s = FallbackSignal.Allowed(FallbackSignal.ReasonUnsupportedKind);
            Assert.True(s.IsAllowed);
            Assert.False(s.WriteStarted);

            JObject j = s.ToJson();
            Assert.Equal("horizun_execute_python", (string)j["recommended_tool"]);
            Assert.True((bool)j["allowed"]);
            Assert.False((bool)j["write_started"]);
            Assert.Equal("unsupported_kind", (string)j["reason"]);
        }

        [Fact]
        public void A_signal_must_name_a_reason_a_client_can_branch_on()
        {
            Assert.Throws<ArgumentException>(() => FallbackSignal.Allowed(null));
            Assert.Throws<ArgumentException>(() => FallbackSignal.Allowed("   "));
        }

        [Fact]
        public void A_refusal_after_a_started_write_says_so_and_forbids_the_retry()
        {
            JObject j = FallbackSignal.NotAllowed("write_failed_midway", true).ToJson();
            Assert.False((bool)j["allowed"]);
            Assert.True((bool)j["write_started"]);
            Assert.Contains("SECOND write", (string)j["what_this_means"]);
        }

        [Fact]
        public void A_refusal_with_no_write_points_back_at_the_typed_command()
        {
            JObject j = FallbackSignal.NotAllowed("invalid_arguments", false).ToJson();
            Assert.False((bool)j["allowed"]);
            Assert.False((bool)j["write_started"]);
            Assert.Contains("not a capability gap", (string)j["what_this_means"]);
        }

        [Fact]
        public void Ordinary_failures_carry_no_signal_at_all()
        {
            // Absence is the answer for everything that is not a capability gap: a client
            // that finds no block must not fall back.
            Assert.Null(CommandResult.Fail("units must be mm, m or feet.").Fallback);
            Assert.Null(CommandResult.Ok(new JObject()).Fallback);
        }

        [Fact]
        public void FallbackDecision_is_the_only_sanctioned_granting_path()
        {
            // The old public bypass CommandResult.FailUnsupported was removed: it granted
            // unconditionally, without seeing the rest of the batch, which is the mixed-batch
            // defect one command at a time. The only sanctioned grant now comes from the
            // central decision over the whole batch.
            CommandResult r = FallbackDecision.Refuse(
                "unsupported kind 'sprinkler'",
                FallbackDecision.Decide(new[]
                {
                    new ActionOutcome { Index = 0, Error = "unsupported kind 'sprinkler'",
                                        UnsupportedReason = FallbackSignal.ReasonUnsupportedKind }
                }, writeStarted: false));

            Assert.False(r.Success);
            Assert.NotNull(r.Fallback);
            Assert.True(r.Fallback.IsAllowed);
            Assert.False(r.Fallback.WriteStarted);
            // The human reason survives; the signal rides beside it rather than inside it.
            Assert.Contains("unsupported kind", r.Error);
            Assert.DoesNotContain("recommended_tool", r.Error);
        }

        [Fact]
        public void An_allowed_grant_rejects_a_reason_outside_the_closed_set()
        {
            // A grant that names a reason no client can match is an unbranchable permission.
            Assert.Throws<ArgumentException>(() => FallbackSignal.Allowed("banana"));
            Assert.Throws<ArgumentException>(() => FallbackSignal.Allowed("Unsupported_Kind")); // case matters
            // Every closed-set reason is accepted.
            foreach (string reason in new[]
            {
                FallbackSignal.ReasonUnsupportedCapability, FallbackSignal.ReasonUnsupportedOperation,
                FallbackSignal.ReasonUnsupportedKind, FallbackSignal.ReasonUnsupportedCategory,
                FallbackSignal.ReasonOutOfContract
            })
            {
                Assert.True(FallbackSignal.Allowed(reason).IsAllowed);
                Assert.True(FallbackSignal.IsKnownGapReason(reason));
            }
            Assert.False(FallbackSignal.IsKnownGapReason("banana"));
            Assert.False(FallbackSignal.IsKnownGapReason(null));
        }

        [Fact]
        public void The_unsupported_capability_exception_rejects_an_invented_reason()
        {
            // A non-empty reason that is not in the closed set is a typo or an invented
            // category and must fail at the throw site, not travel as an unbranchable string.
            Assert.Throws<ArgumentException>(
                () => new UnsupportedCapability("nope", "not_a_real_reason"));
        }

        [Fact]
        public void The_unsupported_capability_exception_carries_its_reason_through_a_catch()
        {
            // The whole point of the type: the reason survives the flattening to a message
            // that every typed command's per-item catch performs.
            Exception thrown = new UnsupportedCapability("nope", FallbackSignal.ReasonUnsupportedOperation);
            Assert.Equal("unsupported_operation", UnsupportedCapability.ReasonOf(thrown));

            // And anything else is not a capability gap, however it is worded.
            Assert.Null(UnsupportedCapability.ReasonOf(new ArgumentException("unsupported operation 'x'")));
            Assert.Null(UnsupportedCapability.ReasonOf(new InvalidOperationException("boom")));
        }

        [Fact]
        public void A_reason_defaults_rather_than_arriving_empty_from_the_exception()
        {
            var ex = new UnsupportedCapability("nope", null);
            Assert.Equal(FallbackSignal.ReasonUnsupportedCapability, ex.Reason);
        }
    }
}
