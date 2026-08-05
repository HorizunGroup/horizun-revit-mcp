// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// THE MIXED BATCH, which is the bug this piece was extracted to fix.
//
// The first implementation granted the request-level Python fallback as soon as
// ANY action named a capability the bridge lacks. A batch with one uncovered
// kind and one malformed profile came back allowed=true - telling a client to go
// write a script while the request still held input it should have corrected.
// The permission was true of one entry and published for the whole call.
//
// So the rule is exhaustive here, once, and the four commands call it. Four
// copies of a rule are four chances to get it subtly different, which is exactly
// how the same mistake shipped four times.
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class FallbackDecisionTests
    {
        private static ActionOutcome Ok(int i) => new ActionOutcome { Index = i };

        private static ActionOutcome Gap(int i, string reason = null) => new ActionOutcome
        {
            Index = i,
            Error = "unsupported kind 'sprinkler_head'",
            UnsupportedReason = reason ?? FallbackSignal.ReasonUnsupportedKind
        };

        private static ActionOutcome Invalid(int i) => new ActionOutcome
        {
            Index = i,
            Error = "profile needs at least three points"
        };

        // ---- the granting case -------------------------------------------------

        [Fact]
        public void Only_capability_gaps_grants_the_request_level_fallback()
        {
            FallbackVerdict v = FallbackDecision.Decide(new[] { Ok(0), Gap(1), Ok(2) }, writeStarted: false);

            Assert.True(v.GlobalGrant);
            Assert.Equal(FallbackSignal.ReasonUnsupportedKind, v.Signal.Reason);
            Assert.False(v.Signal.WriteStarted);
            Assert.Single(v.CapabilityGaps);
            Assert.Equal(1, (int)v.CapabilityGaps[0]["index"]);
        }

        [Fact]
        public void Several_gaps_of_the_same_reason_keep_that_reason()
        {
            FallbackVerdict v = FallbackDecision.Decide(new[] { Gap(0), Gap(3), Gap(7) }, writeStarted: false);

            Assert.True(v.GlobalGrant);
            Assert.Equal(FallbackSignal.ReasonUnsupportedKind, v.Signal.Reason);
            Assert.Equal(3, v.CapabilityGaps.Count);
            Assert.Equal(new[] { 0, 3, 7 }, v.CapabilityGaps.Select(g => (int)g["index"]).ToArray());
        }

        [Fact]
        public void Gaps_of_different_reasons_report_the_generic_one_rather_than_one_of_them()
        {
            FallbackVerdict v = FallbackDecision.Decide(
                new[] { Gap(0, FallbackSignal.ReasonUnsupportedKind),
                        Gap(1, FallbackSignal.ReasonUnsupportedOperation) },
                writeStarted: false);

            Assert.True(v.GlobalGrant);
            // Naming one of them at request level would be a claim true of half the batch.
            Assert.Equal(FallbackSignal.ReasonUnsupportedCapability, v.Signal.Reason);
        }

        // ---- the refusing cases ------------------------------------------------

        [Fact]
        public void Only_invalid_arguments_grants_nothing_and_attaches_nothing()
        {
            FallbackVerdict v = FallbackDecision.Decide(new[] { Ok(0), Invalid(1) }, writeStarted: false);

            Assert.False(v.GlobalGrant);
            // Absence is the answer a client reads: no block at all.
            Assert.Null(v.Signal);
            Assert.Null(v.CapabilityGaps);
        }

        /// <summary>THE DEFECT. A mixed batch must not inherit one entry's permission.</summary>
        [Fact]
        public void A_mixed_batch_refuses_the_global_grant_and_names_the_gaps()
        {
            FallbackVerdict v = FallbackDecision.Decide(
                new[] { Ok(0), Gap(1), Invalid(2), Ok(3) }, writeStarted: false);

            Assert.False(v.GlobalGrant);
            Assert.NotNull(v.Signal);
            Assert.False(v.Signal.IsAllowed);
            Assert.Equal("mixed_capability_and_invalid_input", v.Signal.Reason);
            Assert.False(v.Signal.WriteStarted);

            // The caller still learns exactly which action has no typed path - strictly
            // more than the old blanket yes, and none of it permission.
            Assert.Single(v.CapabilityGaps);
            Assert.Equal(1, (int)v.CapabilityGaps[0]["index"]);
            Assert.Equal(FallbackSignal.ReasonUnsupportedKind, (string)v.CapabilityGaps[0]["reason"]);
            Assert.Equal("horizun_execute_python", (string)v.CapabilityGaps[0]["recommended_tool"]);
        }

        [Fact]
        public void A_started_write_forbids_the_fallback_however_the_batch_failed()
        {
            // Even when EVERY failure is a capability gap: if a write may have landed,
            // a Python "retry" is a second write.
            FallbackVerdict v = FallbackDecision.Decide(new[] { Gap(0), Gap(1) }, writeStarted: true);

            Assert.False(v.GlobalGrant);
            Assert.NotNull(v.Signal);
            Assert.False(v.Signal.IsAllowed);
            Assert.True(v.Signal.WriteStarted);
            Assert.Equal("write_may_have_started", v.Signal.Reason);
        }

        [Fact]
        public void A_started_write_with_no_failures_at_all_attaches_nothing()
        {
            FallbackVerdict v = FallbackDecision.Decide(new[] { Ok(0), Ok(1) }, writeStarted: true);
            Assert.Null(v.Signal);
            Assert.Null(v.CapabilityGaps);
        }

        [Fact]
        public void An_all_valid_batch_attaches_nothing()
        {
            FallbackVerdict v = FallbackDecision.Decide(new[] { Ok(0), Ok(1) }, writeStarted: false);
            Assert.Null(v.Signal);
            Assert.Null(v.CapabilityGaps);
        }

        [Fact]
        public void No_outcomes_at_all_is_not_a_grant()
        {
            Assert.Null(FallbackDecision.Decide(new List<ActionOutcome>(), false).Signal);
            Assert.Null(FallbackDecision.Decide(null, false).Signal);
            // A null entry in the list must not be read as a silent failure either.
            Assert.Null(FallbackDecision.Decide(new ActionOutcome[] { null }, false).Signal);
        }

        // ---- what a command hands back -----------------------------------------

        [Fact]
        public void Refuse_keeps_the_human_message_and_carries_the_signal_beside_it()
        {
            CommandResult granted = FallbackDecision.Refuse(
                "2 element plan(s) are invalid.",
                FallbackDecision.Decide(new[] { Gap(0) }, false));

            Assert.False(granted.Success);
            Assert.Equal("2 element plan(s) are invalid.", granted.Error);
            Assert.NotNull(granted.Fallback);
            Assert.True(granted.Fallback.IsAllowed);
            Assert.NotNull(granted.CapabilityGaps);
            // Structure beside the text, never inside it.
            Assert.DoesNotContain("recommended_tool", granted.Error);
        }

        [Fact]
        public void Refuse_on_an_ordinary_failure_produces_a_plain_result()
        {
            CommandResult plain = FallbackDecision.Refuse(
                "units must be mm, m or feet.",
                FallbackDecision.Decide(new[] { Invalid(0) }, false));

            Assert.Null(plain.Fallback);
            Assert.Null(plain.CapabilityGaps);
        }

        [Fact]
        public void The_mixed_batch_result_carries_gaps_without_carrying_permission()
        {
            CommandResult mixed = FallbackDecision.Refuse(
                "Invalid action graph; nothing ran.",
                FallbackDecision.Decide(new[] { Gap(0), Invalid(1) }, false));

            Assert.NotNull(mixed.Fallback);
            Assert.False(mixed.Fallback.IsAllowed);
            Assert.NotNull(mixed.CapabilityGaps);

            JObject asJson = mixed.Fallback.ToJson();
            Assert.False((bool)asJson["allowed"]);
            Assert.Contains("not a capability gap", (string)asJson["what_this_means"]);
        }
    }
}
