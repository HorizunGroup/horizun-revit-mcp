// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// The prevention gate, proved by running it. One asymmetry carries the file:
// incomplete coverage may BLOCK and may never ALLOW. Every other test here is
// about refusing to claim an authority the bridge does not have.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class PreventionGateTests
    {
        private static GateInput I(bool coverage = true, bool controlled = true, bool? audit = true,
                                   GateOverride ov = null, params string[] blocking)
        {
            return new GateInput
            {
                Operation = GatedOperation.SyncWithCentral,
                DocumentTitle = "Tower",
                DocumentFingerprint = "fp",
                ProfileVersion = "v1",
                CoverageComplete = coverage,
                OperationIsControlled = controlled,
                AuditSupplied = audit,
                Override = ov,
                BlockingFindings = new List<string>(blocking)
            };
        }

        private static GateOverride Ov(string op = null, string profile = "v1", string expires = null,
                                       params string[] findings)
        {
            return new GateOverride
            {
                Identity = "a.person",
                Reason = "client accepted the open items for this issue",
                TimestampUtc = "2026-01-01T00:00:00Z",
                Operation = op ?? GatedOperation.SyncWithCentral,
                ProfileVersion = profile,
                ExpiresUtc = expires,
                Evidence = "email 2026-01-01",
                FindingsIgnored = new List<string>(findings.Length == 0 ? new[] { "f1" } : findings)
            };
        }

        private const string Now = "2026-06-01T00:00:00Z";

        // ------------------------------------------------------ the asymmetry

        [Fact]
        public void Incomplete_coverage_may_block_but_never_allow()
        {
            // THE ONE THAT MATTERS. A defect found in the examined part is real;
            // "nothing wrong" over half a model is not a pass.
            Assert.Equal(GateDecision.Block,
                PreventionGateRules.Decide(I(coverage: false, blocking: "f1"), Now).Decision);

            GateVerdict clean = PreventionGateRules.Decide(I(coverage: false), Now);
            Assert.Equal(GateDecision.NotAssessable, clean.Decision);
            Assert.Contains("not a pass", clean.Why);
        }

        [Fact]
        public void Complete_coverage_with_nothing_found_is_the_only_way_to_allow()
        {
            GateVerdict v = PreventionGateRules.Decide(I(), Now);
            Assert.Equal(GateDecision.Allow, v.Decision);
            Assert.Contains("covered the whole model", v.Why);
        }

        [Fact]
        public void Incomplete_coverage_cannot_excuse_a_defect_that_was_found()
        {
            GateVerdict v = PreventionGateRules.Decide(I(coverage: false, blocking: "f1"), Now);
            Assert.Contains("cannot excuse a defect", v.Why);
        }

        // -------------------------------------------------- what it will not claim

        [Fact]
        public void An_operation_this_bridge_does_not_control_is_not_assessable_and_not_permission()
        {
            GateVerdict v = PreventionGateRules.Decide(I(controlled: false), Now);
            Assert.Equal(GateDecision.NotAssessable, v.Decision);
            Assert.Contains("That is not permission", v.Why);
        }

        [Fact]
        public void No_audit_is_not_a_clean_audit()
        {
            GateVerdict v = PreventionGateRules.Decide(I(audit: null), Now);
            Assert.Equal(GateDecision.NotAssessable, v.Decision);
            Assert.Contains("An absent audit is not a clean one", v.Why);
        }

        // ---------------------------------------------------------- overrides

        [Fact]
        public void A_complete_override_lets_the_operation_proceed_and_the_findings_stand()
        {
            GateVerdict v = PreventionGateRules.Decide(I(ov: Ov(findings: "f1"), blocking: "f1"), Now);
            Assert.Equal(GateDecision.RequiresOverride, v.Decision);
            Assert.True(v.OverrideAccepted);
            Assert.Contains("The findings stand", v.Why);
        }

        [Fact]
        public void An_override_that_names_only_some_findings_covers_only_those()
        {
            GateVerdict v = PreventionGateRules.Decide(
                I(ov: Ov(findings: "f1"), blocking: "f1", coverage: true), Now);
            Assert.Equal(GateDecision.RequiresOverride, v.Decision);

            GateVerdict partial = PreventionGateRules.Decide(
                I(ov: Ov(findings: "f1"), blocking: new[] { "f1", "f2" }[0], coverage: true), Now);
            Assert.Equal(GateDecision.RequiresOverride, partial.Decision);

            var two = I(ov: Ov(findings: "f1"), coverage: true);
            two.BlockingFindings = new List<string> { "f1", "f2" };
            GateVerdict uncovered = PreventionGateRules.Decide(two, Now);
            Assert.Equal(GateDecision.Block, uncovered.Decision);
            Assert.Contains("accepts what it lists and nothing else", uncovered.OverrideRejectedBecause);
        }

        [Fact]
        public void An_override_missing_its_signature_is_refused()
        {
            var bare = new GateOverride { Operation = GatedOperation.SyncWithCentral };
            GateVerdict v = PreventionGateRules.Decide(I(ov: bare, blocking: "f1"), Now);
            Assert.Equal(GateDecision.Block, v.Decision);
            Assert.Contains("incomplete", v.OverrideRejectedBecause);
            Assert.Contains("signed statement, not a flag", PreventionGateRules.OverrideMeans);
        }

        [Fact]
        public void An_override_for_another_operation_is_not_permission_for_this_one()
        {
            GateVerdict v = PreventionGateRules.Decide(
                I(ov: Ov(op: GatedOperation.Export, findings: "f1"), blocking: "f1"), Now);
            Assert.Equal(GateDecision.Block, v.Decision);
            Assert.Contains("is not permission for another", v.OverrideRejectedBecause);
        }

        [Fact]
        public void An_override_signed_against_another_profile_is_refused()
        {
            // The rules changed, so what was accepted may not be what is being asked.
            GateVerdict v = PreventionGateRules.Decide(
                I(ov: Ov(profile: "v0", findings: "f1"), blocking: "f1"), Now);
            Assert.Equal(GateDecision.Block, v.Decision);
            Assert.Contains("The rules changed", v.OverrideRejectedBecause);
        }

        [Fact]
        public void An_expired_override_is_refused_by_comparison_and_not_by_a_clock()
        {
            GateOverride expired = Ov(expires: "2026-02-01T00:00:00Z", findings: "f1");
            Assert.Equal(GateDecision.Block,
                PreventionGateRules.Decide(I(ov: expired, blocking: "f1"), Now).Decision);
            // and still valid before it expires
            Assert.Equal(GateDecision.RequiresOverride,
                PreventionGateRules.Decide(I(ov: expired, blocking: "f1"), "2026-01-15T00:00:00Z").Decision);
        }

        [Fact]
        public void An_override_with_no_expiry_is_accepted_and_that_is_a_choice_the_signer_made()
        {
            GateVerdict v = PreventionGateRules.Decide(I(ov: Ov(findings: "f1"), blocking: "f1"), Now);
            Assert.Equal(GateDecision.RequiresOverride, v.Decision);
        }

        // -------------------------------------------------------------- shape

        [Fact]
        public void Every_decision_is_one_of_the_four_and_the_reply_carries_the_asymmetry()
        {
            JObject j = PreventionGateRules.ToJson(PreventionGateRules.Decide(I(), Now));
            Assert.Contains(j.Value<string>("decision"), GateDecision.All);
            Assert.Contains("may BLOCK and may never ALLOW", j.Value<string>("asymmetry_means"));
        }

        [Fact]
        public void Nothing_submitted_is_not_assessable_rather_than_allowed()
        {
            Assert.Equal(GateDecision.NotAssessable, PreventionGateRules.Decide(null, Now).Decision);
        }

        // ---- an expiry nobody can evaluate is not a pass ----------------------------

        /// <summary>
        /// THE OVERRIDE THAT NEVER EXPIRED. RejectOverride used to compare the expiry only
        /// when BOTH the expiry and now_utc were present, and now_utc is optional and
        /// comes from the CALLER. So the way to keep an expired override working was to
        /// stop sending one optional field: the expiry stayed in the document, looked
        /// authoritative, and bound nobody.
        ///
        /// The gate still reads no clock - that is what makes an expiry exact in a test
        /// rather than dependent on the day the suite runs - but a caller who will not say
        /// what time it is does not get the benefit of the doubt.
        /// </summary>
        [Fact]
        public void An_override_with_an_expiry_and_no_now_utc_is_refused_not_honoured()
        {
            GateOverride expired = Ov(expires: "2026-01-31T00:00:00Z");

            // The control: with a time to judge against, it is refused as expired.
            string withClock = PreventionGateRules.RejectOverride(expired, I(), Now);
            Assert.NotNull(withClock);
            Assert.Contains("expired at", withClock, StringComparison.Ordinal);

            // The defect: omitting now_utc must not resurrect it.
            foreach (string noClock in new[] { null, "" })
            {
                string why = PreventionGateRules.RejectOverride(expired, I(), noClock);
                Assert.True(why != null,
                    "an override carrying an expiry was accepted because now_utc was absent; " +
                    "omitting one optional field must not turn an expiry into a formality");
                Assert.Contains("now_utc", why, StringComparison.Ordinal);
            }

            // And an override with NO expiry is unaffected: it never claimed to end.
            Assert.Null(PreventionGateRules.RejectOverride(Ov(), I(), null));
        }
}
}
