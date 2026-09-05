// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// WHAT HAPPENS WHEN ONE ACTION OF A CONFIRMED PLAN FAILS AND THE OTHERS DO NOT.
//
// The live campaign could not induce that on demand, and the reason is the design
// itself: the confirmation token binds the document, so anything that would make
// an apply fail also invalidates the token and the WHOLE call is refused before it
// starts. What remains reachable at apply time - a child that half-applies, a
// tool that stops being permitted between rehearsal and apply, a Revit-side
// failure inside one action - is exactly what these tests pin, because the model
// will not produce them on request.
//
// Two kinds of assertion here, deliberately:
//   * behaviour, over the real reporting functions (CorrectionReply, ReAuditRules,
//     ApplicationOutcome) with a set of actions where one applied and one did not;
//   * a source guard over the apply loop, because ApplyCorrectionsCommand needs a
//     UIApplication and cannot be constructed here. It is coarse, and it is the
//     only thing that catches the loop quietly going back to reading Success.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class CorrectionPartialResultTests
    {
        private static string RepoRoot()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null)
            {
                if (File.Exists(Path.Combine(d.FullName, "AGENTS.md")) &&
                    Directory.Exists(Path.Combine(d.FullName, "src", "Horizun.Revit", "Commands")))
                    return d.FullName;
                d = d.Parent;
            }
            throw new InvalidOperationException("repository root not found from " + AppContext.BaseDirectory);
        }

        private const string SetFp = "fs:partial";
        private const string Doc = "HZ_DOCTOR";
        private const string DocFp = "df:partial";

        private static FindingSetRecord Audit()
        {
            var findings = new JArray(
                new JObject
                {
                    ["check"] = AuditCheckNames.UnpinnedLinks, ["finding_id"] = "f:links", ["is_issue"] = true,
                    ["shown"] = 2, ["total"] = 2,
                    ["items"] = new JArray(new JObject { ["element_id"] = "10" },
                                           new JObject { ["element_id"] = "11" })
                },
                new JObject
                {
                    ["check"] = AuditCheckNames.Links, ["finding_id"] = "f:linktypes", ["is_issue"] = true,
                    ["shown"] = 1, ["total"] = 1,
                    ["items"] = new JArray(new JObject { ["id"] = "70", ["status"] = "Unloaded" })
                });
            return FindingSetRecord.From(SetFp, Doc, DocFp, 20, "2026-01-01T00:00:00Z", findings);
        }

        /// <summary>One action of the plan, with its steps' apply outcome forced.</summary>
        private static CorrectionAction ActionThat(string findingId, bool applied, string error, string state)
        {
            CorrectionAction a = CorrectionSelection
                .Select(Audit(), new JArray(new JObject { ["finding_id"] = findingId }), CorrectionRegistry.Default)
                .Single();
            foreach (CorrectionStep s in a.Steps)
            {
                s.RehearsalOk = true;                       // both rehearsed cleanly: that is the premise
                s.RehearsalState = "verified_applied";
                s.ApplyOk = applied;
                s.ApplyState = state;
                s.ApplyError = error;
            }
            a.State = applied ? CorrectionActionState.Applied : CorrectionActionState.Failed;
            if (!applied) a.Why = "at least one typed call did not come back fully applied and verified";
            return a;
        }

        [Fact]
        public void A_mixed_apply_is_reported_as_mixed_and_never_as_one_verdict_for_the_call()
        {
            CorrectionAction ok = ActionThat("f:links", true, null, "verified_applied");
            CorrectionAction bad = ActionThat("f:linktypes", false, "Revit refused: the element is owned by another user",
                                              "failed");

            JObject tally = CorrectionReply.Tally(new[] { ok, bad });
            Assert.Equal(1, (int)tally[CorrectionActionState.Applied]);
            Assert.Equal(1, (int)tally[CorrectionActionState.Failed]);

            // A reader must be able to say WHICH work stands, without inference.
            JObject okJson = CorrectionReply.ActionJson(ok);
            JObject badJson = CorrectionReply.ActionJson(bad);
            Assert.Equal(CorrectionActionState.Applied, (string)okJson["state"]);
            Assert.Equal(CorrectionActionState.Failed, (string)badJson["state"]);
            Assert.True((bool)okJson["steps"][0]["apply"]["ok"]);
            Assert.False((bool)badJson["steps"][0]["apply"]["ok"]);
            Assert.Contains("owned by another user", (string)badJson["steps"][0]["apply"]["error"]);
            Assert.Contains("fully applied", (string)badJson["why"]);
            Assert.Null((string)okJson["why"]);

            // And both rehearsed: the failure is at apply, which is the whole case.
            Assert.True((bool)okJson["steps"][0]["rehearsal"]["ok"]);
            Assert.True((bool)badJson["steps"][0]["rehearsal"]["ok"]);
        }

        [Fact]
        public void The_re_audit_judges_each_action_against_the_model_not_against_the_call()
        {
            CorrectionAction ok = ActionThat("f:links", true, null, "verified_applied");
            CorrectionAction bad = ActionThat("f:links", false, "Revit refused", "failed");

            // The applied one: the check DID re-run and no longer lists its
            // elements. (A check that could not re-run is not_verifiable, which is
            // a different answer and deliberately not this one.)
            var gone = new JObject
            {
                ["check"] = AuditCheckNames.UnpinnedLinks, ["finding_id"] = "f:after", ["is_issue"] = false,
                ["count"] = 0, ["truncated"] = false, ["items"] = new JArray()
            };
            JObject corrected = ReAuditRules.Compare(ok, gone, false);
            Assert.Equal(ReAuditOutcome.Corrected, (string)corrected["outcome"]);
            Assert.Equal(ReAuditOutcome.NotVerifiable, (string)ReAuditRules.Compare(ok, null, false)["outcome"]);

            // The failed one: still listed, and reported as failed rather than as
            // "persistent" - the distinction is whether the typed call claimed it.
            var still = new JObject
            {
                ["check"] = AuditCheckNames.UnpinnedLinks, ["finding_id"] = "f:after", ["is_issue"] = true,
                ["count"] = 2, ["truncated"] = false,
                ["items"] = new JArray(new JObject { ["element_id"] = 10L }, new JObject { ["element_id"] = 11L })
            };
            JObject failed = ReAuditRules.Compare(bad, still, false);
            Assert.Equal(ReAuditOutcome.Failed, (string)failed["outcome"]);
            Assert.Equal(2, (int)failed["counts"]["failed"]);
        }

        [Fact]
        public void A_child_that_half_applied_is_not_an_applied_action()
        {
            // This is the predicate the apply loop uses. Partial is the state a
            // typed child reports when some of its elements went through and some
            // did not, and it must NOT read as an applied action.
            Assert.False(ApplicationOutcome.IsFullyApplied(ApplicationState.Partial));
            Assert.False(ApplicationOutcome.IsFullyApplied(ApplicationState.Failed));
            Assert.False(ApplicationOutcome.IsFullyApplied(ApplicationState.RolledBack));
            Assert.True(ApplicationOutcome.IsFullyApplied(ApplicationState.VerifiedApplied));
        }

        [Fact]
        public void The_apply_loop_still_scopes_failure_to_one_action_and_judges_by_the_re_read_state()
        {
            string src = File.ReadAllText(Path.Combine(
                RepoRoot(), "src", "Horizun.Revit", "Commands", "ApplyCorrectionsCommand.cs"));

            // The command hands the loop the typed child's own declaration; it does
            // not decide on the call not throwing.
            Assert.Contains("CorrectionApplyLoop.Apply(actions, step =>", src);
            Assert.Contains("State = ApplicationOutcome.Read(applied.Data)", src);
            Assert.Contains("public const string RollbackScope = CorrectionApplyLoop.RollbackScope;", src);

            // The loop itself is in Core and is tested by CorrectionApplyLoopTests;
            // what stays guarded here is that the decision is per ACTION and that a
            // step which could not be re-read is not folded into failed.
            string loop = File.ReadAllText(Path.Combine(
                RepoRoot(), "src", "Horizun.Revit", "Core", "CorrectionApplyLoop.cs"));
            Assert.Contains("step.ApplyOk = outcome.Success && ApplicationOutcome.IsFullyApplied(outcome.State);", loop);
            Assert.Contains("foreach (CorrectionAction action in actions", loop);
            Assert.Contains("bool allOk = true;", loop);
            Assert.Contains("action.State = inDoubt ? CorrectionActionState.Uncertain : CorrectionActionState.Failed;", loop);
        }
    }
}
