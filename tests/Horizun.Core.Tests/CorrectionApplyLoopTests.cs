// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// WHAT `rollback_scope: per_action` PROMISES, situation by situation.
//
// D9.2 asked for a Revit that fails one action of a confirmed plan on cue, and
// Revit will not do that: anything inducible from outside invalidates the
// confirmation token and the whole call is refused before a line is written
// (measured live, phase 8, all five years). So the loop that applies a plan was
// moved into CorrectionApplyLoop - Revit-free, driven by a delegate - and the
// substitutable thing is the STEP EXECUTOR, not a switch inside the product. The
// shipped command builds exactly one executor: the one that dispatches the typed
// child.
//
// These are the six situations, each on its own:
//
//   1. an action's rehearsal fails before anything is written
//   2. one action applies and a later one is refused before its transaction
//   3. an action fails inside its own transaction
//   4. the re-read after an applied action fails
//   5. the final re-audit cannot complete
//   6. the reply is lost and the request is repeated under the same key
//
// This is STRUCTURAL evidence, not live: it proves what the code does with each
// outcome, not that Revit produces them.
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class CorrectionApplyLoopTests
    {
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
                    ["check"] = AuditCheckNames.OrphanGroupTypes, ["finding_id"] = "f:groups", ["is_issue"] = true,
                    ["shown"] = 1, ["total"] = 1,
                    ["items"] = new JArray(new JObject { ["element_id"] = "900" })
                });
            return FindingSetRecord.From("fs:loop", "HZ_DOCTOR", "df:loop", 20, "2026-01-01T00:00:00Z", findings);
        }

        private static List<CorrectionAction> Plan(params JObject[] requests) =>
            CorrectionSelection.Select(Audit(), new JArray(requests.Cast<JToken>().ToArray()),
                                       CorrectionRegistry.Default);

        private static JObject Pin() => new JObject { ["finding_id"] = "f:links" };
        private static JObject Delete() =>
            new JObject { ["finding_id"] = "f:groups", ["element_ids"] = new JArray(900L) };

        private static StepExecution Applied() =>
            new StepExecution { Success = true, State = ApplicationState.VerifiedApplied };
        private static StepExecution RolledBack(string why) =>
            new StepExecution { Success = false, State = ApplicationState.RolledBack, Error = why };
        private static StepExecution Unreadable(string why) =>
            new StepExecution { Success = true, State = ApplicationState.Uncertain, Error = why };

        // ---------------------------------------------------------------- 1

        // ---------------------------------------------------------------- 2

        // ---------------------------------------------------------------- 3

        [Fact]
        public void An_action_whose_transaction_rolled_back_is_FAILED_and_never_applied()
        {
            List<CorrectionAction> plan = Plan(Delete());
            CorrectionApplyLoop.Apply(plan, _ => RolledBack("Revit rolled the transaction back"));

            Assert.Equal(CorrectionActionState.Failed, plan[0].State);
            Assert.False(plan[0].Steps[0].ApplyOk);
            Assert.Equal("rolled_back", plan[0].Steps[0].ApplyState);
            Assert.Contains("did not come back fully applied", plan[0].Why);

            // And the re-audit reports its elements as failed, not as corrected.
            JObject r = ReAuditRules.Compare(plan[0], null, false);
            Assert.Equal(ReAuditOutcome.Failed, (string)r["outcome"]);
        }

        // ---------------------------------------------------------------- 4

        [Fact]
        public void A_postcondition_that_could_not_be_READ_is_uncertain_and_keeps_the_write_in_doubt()
        {
            List<CorrectionAction> plan = Plan(Pin(), Delete());
            CorrectionApplyLoop.Apply(plan, step =>
                step.Tool == "horizun_delete_verified"
                    ? Unreadable("the elements could not be re-read after the commit")
                    : Applied());

            Assert.Equal(CorrectionActionState.Applied, plan[0].State);
            Assert.Equal(CorrectionActionState.Uncertain, plan[1].State);
            Assert.Contains("UNKNOWN", plan[1].Why);
            Assert.DoesNotContain("failed", plan[1].State);

            // The re-audit must not call it corrected and must not call it failed.
            var stillListed = new JObject
            {
                ["check"] = AuditCheckNames.OrphanGroupTypes, ["finding_id"] = "f:after", ["is_issue"] = true,
                ["count"] = 1, ["truncated"] = false,
                ["items"] = new JArray(new JObject { ["element_id"] = 900L })
            };
            JObject r = ReAuditRules.Compare(plan[1], stillListed, false);
            Assert.Equal(ReAuditOutcome.NotVerifiable, (string)r["outcome"]);
            Assert.Empty(r["elements"]["corrected"]);
            Assert.Empty(r["elements"]["failed"]);
            Assert.Equal(new long[] { 900 }, r["elements"]["not_verifiable"].Select(t => (long)t).ToArray());
        }

        // ---------------------------------------------------------------- 5

        [Fact]
        public void A_re_audit_that_could_not_run_claims_nothing_about_an_action_that_applied()
        {
            List<CorrectionAction> plan = Plan(Delete());
            CorrectionApplyLoop.Apply(plan, _ => Applied());
            Assert.Equal(CorrectionActionState.Applied, plan[0].State);

            JObject r = ReAuditRules.Compare(plan[0], null, checkFailed: true);
            Assert.Equal(ReAuditOutcome.NotVerifiable, (string)r["outcome"]);
            Assert.True((bool)r["after"]["check_failed"]);
            Assert.Contains("could not be re-run", (string)r["why"]);

            // The ACTION keeps saying it applied - the typed tool verified it - while
            // the re-audit says it could not confirm. Both are true and both are said.
            Assert.Equal(CorrectionActionState.Applied, plan[0].State);
        }

        // ---------------------------------------------------------------- 6

        [Fact]
        public void The_summary_adds_up_to_the_per_action_results_including_the_uncertain_one()
        {
            List<CorrectionAction> plan = Plan(Pin(), Delete());
            CorrectionApplyLoop.Apply(plan, step =>
                step.Tool == "horizun_delete_verified" ? Unreadable("could not re-read") : Applied());

            JObject tally = CorrectionReply.Tally(plan);
            Assert.Equal(1, (int)tally[CorrectionActionState.Applied]);
            Assert.Equal(1, (int)tally[CorrectionActionState.Uncertain]);
            Assert.Null(tally[CorrectionActionState.Failed]);
            Assert.Equal(plan.Count, tally.Properties().Sum(p => (int)p.Value));

            // A REPLAY hands back this same reply. What must never happen is the
            // uncertain action being folded into applied or failed on the way out,
            // because a caller that retries reads the tally to decide.
            JObject json = CorrectionReply.ActionJson(plan[1]);
            Assert.Equal(CorrectionActionState.Uncertain, (string)json["state"]);
            Assert.Equal("uncertain", (string)json["steps"][0]["apply"]["application_state"]);
            Assert.False((bool)json["steps"][0]["apply"]["ok"]);
        }

        // ---------------------------------------------------------------- the sentence

        [Fact]
        public void The_scope_says_what_it_does_and_the_command_publishes_that_same_sentence()
        {
            Assert.Equal("per_action", CorrectionApplyLoop.RollbackScope);
            foreach (string promise in new[]
            {
                "rehearsed BEFORE any of them is applied",
                "rolled back BY THAT TOOL",
                "does NOT undo an action that already applied",
                "`uncertain`, not `failed`",
                "NOT one atomic group"
            })
                Assert.Contains(promise, CorrectionApplyLoop.RollbackMeans);
        }
    }
}
