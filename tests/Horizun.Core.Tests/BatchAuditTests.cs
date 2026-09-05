// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// A read-only sweep over many models, proved at a desk.
//
// THE FAKE HERE IS A STEP LIST. The sweep is a job sequence: open, audit, close
// per model. What Revit does to those steps cannot be produced on request - a
// model that will not open, a dialog nobody is there to answer, a close that
// fails - so the steps are written by hand and the CONSOLIDATION is measured.
// That is the half that decides whether the report is honest, and it is the
// half that runs identically whether Revit cooperated or not.
//
// The tests are deliberately written against the SAME functions the bridge
// calls: BatchAuditRules.Plan and ToSequence are what SubmitJobCommand runs,
// JobSequenceRules.SettleAfterStop is what Dispatcher.RunSequence runs, and
// Consolidate reads exactly the SequenceEntry list those produce. An earlier
// draft tested an in-process sequencer that nothing called; it was deleted.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class BatchAuditTests
    {
        private static BatchModel Local(string id, string title = null)
        {
            return new BatchModel
            {
                Id = id,
                Origin = ModelOrigin.Local,
                LocalPath = @"C:\models\" + id + ".rvt",
                ExpectedTitle = title ?? id
            };
        }

        private static BatchPlan PlanOf(params BatchModel[] models)
        {
            BatchPlan p = BatchAuditRules.Plan(models, new BatchOptions { ProfileVersion = "v1" });
            Assert.True(p.Ok, p.Message);
            return p;
        }

        /// <summary>
        /// What the runner would have produced: every step succeeded. This is the
        /// baseline the failure cases below deviate from, one at a time.
        /// </summary>
        private static List<SequenceEntry> AllSucceeded(BatchPlan plan)
        {
            var steps = new List<SequenceEntry>();
            foreach (JToken t in BatchAuditRules.ToSequence(plan, new BatchOptions { ProfileVersion = "v1" }))
                steps.Add(new SequenceEntry
                {
                    Key = (string)t["key"],
                    Tool = (string)t["tool"],
                    Status = StepStatus.Succeeded,
                    ResultRef = (string)t["key"] + ".json"
                });
            return steps;
        }

        private static SequenceEntry Step(List<SequenceEntry> steps, string key)
        {
            return steps.Single(s => s.Key == key);
        }

        /// <summary>
        /// Fail one step the way the runner would, and settle the rest: everything
        /// after it becomes not_run. This is JobSequenceRules.SettleAfterStop - the
        /// same call Dispatcher.RunSequence makes - rather than a re-implementation.
        /// </summary>
        private static void FailAt(List<SequenceEntry> steps, string key, string error)
        {
            int i = steps.FindIndex(s => s.Key == key);
            Assert.True(i >= 0, "no such step: " + key);
            steps[i].Status = StepStatus.Failed;
            steps[i].Error = error;
            steps[i].ResultRef = null;
            // Everything after the failure is back to what the runner would have
            // left it as: QUEUED, never started. Leaving them succeeded here would
            // make a stopped sweep read as a complete one - the exact falsification
            // these tests exist to catch, and it caught this helper first.
            for (int j = i + 1; j < steps.Count; j++)
            {
                steps[j].Status = StepStatus.Queued;
                steps[j].ResultRef = null;
            }
            JobSequenceRules.SettleAfterStop(steps, i);
        }

        private static BatchModelResult Of(BatchRun run, string id)
        {
            return run.Results.Single(r => r.Id == id);
        }

        // ---- 1: three correct models ----------------------------------------

        [Fact]
        public void Three_good_models_expand_to_open_audit_close_each_and_all_report_audited()
        {
            BatchPlan plan = PlanOf(Local("A"), Local("B"), Local("C"));
            List<SequenceEntry> steps = AllSucceeded(plan);

            Assert.Equal(9, steps.Count);
            BatchRun run = BatchAuditRules.Consolidate(plan, steps);
            Assert.Equal(BatchRunStatus.Completed, run.Status);
            Assert.All(run.Results, r => Assert.Equal(BatchOutcome.Audited, r.Outcome));
            Assert.All(run.Results, r => Assert.True(r.DocumentClosed));

            JObject o = BatchAuditRules.Aggregate(plan, run);
            Assert.True((bool)o["all_models_assessed"]);
            Assert.Equal(0, (int)o["documents_left_open"]);
        }

        [Fact]
        public void The_sweep_visits_one_document_at_a_time()
        {
            BatchPlan plan = PlanOf(Local("A"), Local("B"));
            var keys = BatchAuditRules.ToSequence(plan, new BatchOptions())
                .Select(t => (string)t["key"]).ToArray();

            // A's close precedes B's open. Interleaving would show up here, and it is
            // the whole reason the sweep is one ordered sequence rather than a fan-out.
            Assert.Equal(new[] { "A.open", "A.audit", "A.close", "B.open", "B.audit", "B.close" }, keys);
        }

        // ---- 2: one that will not open --------------------------------------

        [Fact]
        public void A_model_that_will_not_open_is_not_opened_and_never_clean()
        {
            BatchPlan plan = PlanOf(Local("A"), Local("B"), Local("C"));
            List<SequenceEntry> steps = AllSucceeded(plan);
            // Only B fails; A already ran and C's steps are settled to not_run.
            Step(steps, "A.open").Status = StepStatus.Succeeded;
            FailAt(steps, "B.open", "the file is missing.");

            BatchRun run = BatchAuditRules.Consolidate(plan, steps);
            Assert.Equal(BatchOutcome.Audited, Of(run, "A").Outcome);
            Assert.Equal(BatchOutcome.NotOpened, Of(run, "B").Outcome);
            // C was never reached. That is not_assessed - not clean, and not broken.
            Assert.Equal(BatchOutcome.NotAssessed, Of(run, "C").Outcome);
            Assert.Contains("never opened", Of(run, "C").Why);
        }

        // ---- 3: a timeout ----------------------------------------------------

        [Fact]
        public void A_model_whose_audit_timed_out_was_opened_and_not_examined()
        {
            BatchPlan plan = PlanOf(Local("A"));
            List<SequenceEntry> steps = AllSucceeded(plan);
            FailAt(steps, "A.audit", "the audit ran out of time.");

            BatchRun run = BatchAuditRules.Consolidate(plan, steps);
            // Opened, so not not_opened; not audited, so not audited. The third state
            // is the one that says what actually happened.
            Assert.Equal(BatchOutcome.NotAssessed, Of(run, "A").Outcome);
            Assert.Contains("ran out of time", Of(run, "A").Why);
        }

        // ---- 4: cancellation --------------------------------------------------

        [Fact]
        public void A_cancelled_sweep_reports_the_models_it_never_reached_as_not_assessed()
        {
            BatchPlan plan = PlanOf(Local("A"), Local("B"), Local("C"));
            List<SequenceEntry> steps = AllSucceeded(plan);
            // Cancelled after A: B and C were never started.
            foreach (SequenceEntry s in steps.Where(s => !s.Key.StartsWith("A.", StringComparison.Ordinal)))
            {
                s.Status = StepStatus.Queued;
                s.ResultRef = null;
            }
            JobSequenceRules.SettleAfterStop(steps, 2);

            BatchRun run = BatchAuditRules.Consolidate(plan, steps);
            Assert.Equal(BatchOutcome.Audited, Of(run, "A").Outcome);
            Assert.Equal(BatchOutcome.NotAssessed, Of(run, "B").Outcome);
            Assert.Equal(BatchOutcome.NotAssessed, Of(run, "C").Outcome);
            Assert.Equal(BatchRunStatus.Incomplete, run.Status);
        }

        // ---- 5: resumption ----------------------------------------------------

        [Fact]
        public void A_resumed_sweep_skips_what_was_audited_and_retries_what_was_not()
        {
            BatchPlan plan = PlanOf(Local("A"), Local("B"), Local("C"));
            var done = new[]
            {
                new BatchModelResult { Id = "A", Outcome = BatchOutcome.Audited },
                // B opened and was never audited, so the previous run learned nothing.
                new BatchModelResult { Id = "B", Outcome = BatchOutcome.NotAssessed }
            };

            Assert.Equal(new[] { "B", "C" },
                BatchAuditRules.Remaining(plan, done).Select(m => m.Id).ToArray());
        }

        [Fact]
        public void A_model_whose_close_failed_is_retried_because_it_was_never_a_clean_result()
        {
            BatchPlan plan = PlanOf(Local("A"));
            var done = new[] { new BatchModelResult { Id = "A", Outcome = BatchOutcome.CloseFailed } };
            Assert.Single(BatchAuditRules.Remaining(plan, done));
        }

        // ---- 6: an audit whose snapshot could not be trusted --------------------

        [Fact]
        public void An_audit_that_failed_its_own_integrity_check_leaves_no_result_reference()
        {
            BatchPlan plan = PlanOf(Local("A"));
            List<SequenceEntry> steps = AllSucceeded(plan);
            FailAt(steps, "A.audit", "the snapshot did not match its own hash.");

            BatchRun run = BatchAuditRules.Consolidate(plan, steps);
            Assert.Equal(BatchOutcome.NotAssessed, Of(run, "A").Outcome);
            // No result_ref: a number nobody can reproduce is worse than no number.
            Assert.Null(Of(run, "A").ResultRef);
        }

        [Fact]
        public void An_audited_model_carries_the_reference_to_its_stored_reply()
        {
            BatchPlan plan = PlanOf(Local("A"));
            List<SequenceEntry> steps = AllSucceeded(plan);

            BatchRun run = BatchAuditRules.Consolidate(plan, steps);
            // Without it the report says a model was audited and gives nobody a way
            // to read what the audit said.
            Assert.Equal("A.audit.json", Of(run, "A").ResultRef);
            Assert.Equal("A.audit.json", (string)BatchAuditRules.ToJson(Of(run, "A"))["result_ref"]);
        }

        // ---- 7: a dialog nobody is there to answer ------------------------------

        [Fact]
        public void A_model_blocked_by_a_dialog_carries_the_reason_rather_than_a_bare_failure()
        {
            BatchPlan plan = PlanOf(Local("A"));
            List<SequenceEntry> steps = AllSucceeded(plan);
            FailAt(steps, "A.open", "Opening was canceled - a modal dialog was raised: Unresolved References.");

            BatchRun run = BatchAuditRules.Consolidate(plan, steps);
            Assert.Equal(BatchOutcome.NotOpened, Of(run, "A").Outcome);
            Assert.Contains("Unresolved References", Of(run, "A").Why);
        }

        // ---- 8: a close that failed ---------------------------------------------

        [Fact]
        public void A_close_that_failed_leaves_the_run_saying_a_document_is_open()
        {
            BatchPlan plan = PlanOf(Local("A"));
            List<SequenceEntry> steps = AllSucceeded(plan);
            Step(steps, "A.close").Status = StepStatus.Failed;
            Step(steps, "A.close").Error = "Revit refused to close it.";

            BatchRun run = BatchAuditRules.Consolidate(plan, steps);
            // The audit worked, and the sweep still did not end cleanly. Reporting
            // this model as audited would hide a document sitting in somebody's Revit.
            Assert.Equal(BatchOutcome.CloseFailed, Of(run, "A").Outcome);
            Assert.False(Of(run, "A").DocumentClosed);
            Assert.Equal(BatchRunStatus.StoppedDocumentLeftOpen, run.Status);
            Assert.Equal(1, (int)BatchAuditRules.Aggregate(plan, run)["documents_left_open"]);
        }

        // ---- 9: release - a model never opened left nothing behind ---------------

        [Fact]
        public void A_model_that_was_never_opened_is_not_reported_as_a_document_left_open()
        {
            BatchPlan plan = PlanOf(Local("A"), Local("B"));
            List<SequenceEntry> steps = AllSucceeded(plan);
            FailAt(steps, "A.open", "the file is missing.");

            BatchRun run = BatchAuditRules.Consolidate(plan, steps);
            Assert.All(run.Results, r => Assert.True(r.DocumentClosed));
            Assert.Equal(0, (int)BatchAuditRules.Aggregate(plan, run)["documents_left_open"]);
        }

        // ---- 10: the aggregate ----------------------------------------------------

        [Fact]
        public void The_aggregate_counts_every_model_listed_and_never_calls_a_partial_sweep_complete()
        {
            BatchPlan plan = PlanOf(Local("A"), Local("B"), Local("C"));
            List<SequenceEntry> steps = AllSucceeded(plan);
            FailAt(steps, "C.open", "the file is missing.");

            BatchRun run = BatchAuditRules.Consolidate(plan, steps);
            JObject o = BatchAuditRules.Aggregate(plan, run);

            Assert.Equal(3, (int)o["models_listed"]);
            Assert.Equal(2, (int)o["models_audited"]);
            // TWO OUT OF THREE IS NOT A CLEAN SWEEP. This is the assertion the whole
            // file exists for: eleven clean out of eleven, when twelve were listed.
            Assert.False((bool)o["all_models_assessed"]);
            Assert.Equal(1, (int)o["by_outcome"][BatchOutcome.NotOpened]);
        }

        [Fact]
        public void A_sweep_that_reported_nothing_leaves_every_model_not_assessed()
        {
            BatchPlan plan = PlanOf(Local("A"), Local("B"));
            BatchRun run = BatchAuditRules.Consolidate(plan, new SequenceEntry[0]);

            JObject o = BatchAuditRules.Aggregate(plan, run);
            Assert.Equal(2, (int)o["models_listed"]);
            Assert.Equal(0, (int)o["models_audited"]);
            Assert.Equal(2, (int)o["models_not_assessed"]);
            Assert.False((bool)o["all_models_assessed"]);
        }

        [Fact]
        public void A_model_listed_and_never_reported_is_counted_as_not_assessed()
        {
            // A RUN WHOSE RESULTS ARE SHORT. Consolidate always emits one result per
            // listed model, so this is the contract Aggregate owes a caller that
            // hands it something else - and the denominator is every model LISTED,
            // never the number of results somebody happened to produce.
            BatchPlan plan = PlanOf(Local("A"), Local("B"), Local("C"));
            var run = new BatchRun
            {
                Status = BatchRunStatus.Completed,
                Results = new List<BatchModelResult>
                {
                    new BatchModelResult { Id = "A", Outcome = BatchOutcome.Audited, DocumentClosed = true }
                }
            };

            JObject o = BatchAuditRules.Aggregate(plan, run);
            Assert.Equal(3, (int)o["models_listed"]);
            Assert.Equal(1, (int)o["models_audited"]);
            // B and C never appeared in the results at all. Two, not zero.
            Assert.Equal(2, (int)o["models_not_assessed"]);
            Assert.False((bool)o["all_models_assessed"]);
        }

        // ---- 11: zero models --------------------------------------------------------

        [Fact]
        public void An_empty_sweep_is_refused_rather_than_reported_as_finding_nothing()
        {
            BatchPlan p = BatchAuditRules.Plan(new BatchModel[0], new BatchOptions());
            Assert.False(p.Ok);
            Assert.Equal(BatchRefusal.NoModels, p.Code);

            // And it produces no sequence, so nothing could be queued from it.
            Assert.Empty(BatchAuditRules.ToSequence(p, new BatchOptions()));
            Assert.Equal(BatchRunStatus.Refused, BatchAuditRules.Consolidate(p, null).Status);
        }

        // ---- 12: duplicate ids --------------------------------------------------------

        [Fact]
        public void Two_models_sharing_an_id_are_refused_before_anything_opens()
        {
            BatchPlan p = BatchAuditRules.Plan(new[] { Local("A"), Local("A") }, new BatchOptions());
            Assert.False(p.Ok);
            Assert.Equal(BatchRefusal.DuplicateId, p.Code);
        }

        [Fact]
        public void A_model_with_no_id_is_refused_because_a_result_could_not_be_attributed()
        {
            BatchModel m = Local("A");
            m.Id = "  ";
            Assert.Equal(BatchRefusal.NoIdentifier,
                BatchAuditRules.Plan(new[] { m }, new BatchOptions()).Code);
        }

        // ---- 13: the wrong document -----------------------------------------------------

        [Fact]
        public void Every_generated_audit_and_close_names_its_target_document()
        {
            BatchPlan plan = PlanOf(Local("a", "Tower - Structure"));
            JArray seq = BatchAuditRules.ToSequence(plan, new BatchOptions());

            // horizun_audit_model refuses when the active document is not the one
            // named, so naming it here is what makes a wrong-document audit impossible
            // rather than merely unlikely.
            Assert.Equal("Tower - Structure",
                (string)seq.Single(x => (string)x["key"] == "a.audit")["arguments"]["target_document"]);
            Assert.Equal("Tower - Structure",
                (string)seq.Single(x => (string)x["key"] == "a.close")["arguments"]["target_document"]);
        }

        [Fact]
        public void A_model_that_does_not_say_what_it_should_be_called_is_refused()
        {
            BatchModel m = Local("a");
            m.ExpectedTitle = null;
            BatchPlan p = BatchAuditRules.Plan(new[] { m }, new BatchOptions());
            Assert.False(p.Ok);
            Assert.Equal(BatchRefusal.NoExpectedTitle, p.Code);
        }

        // ---- 14: a different profile per model -------------------------------------------

        [Fact]
        public void Each_model_is_judged_by_its_own_profile_and_the_run_default_fills_the_gap()
        {
            BatchModel a = Local("A"); a.ProfileVersion = "structural-v2";
            BatchModel b = Local("B");   // no profile of its own
            BatchPlan plan = PlanOf(a, b);

            JArray seq = BatchAuditRules.ToSequence(plan, new BatchOptions { ProfileVersion = "default-v1" });
            Assert.Equal("structural-v2",
                (string)seq.Single(x => (string)x["key"] == "A.audit")["arguments"]["profile_version"]);
            Assert.Equal("default-v1",
                (string)seq.Single(x => (string)x["key"] == "B.audit")["arguments"]["profile_version"]);
        }

        // ---- 15: an attempt to save or synchronise ------------------------------------------

        [Fact]
        public void A_generated_sweep_names_no_tool_that_could_write_to_a_model()
        {
            BatchPlan plan = PlanOf(Local("A"), Local("B"));
            JArray seq = BatchAuditRules.ToSequence(plan, new BatchOptions());

            // STRUCTURAL, not a promise: the sequence a sweep produces is admitted by
            // the same allowlist as any other, and that list contains nothing that
            // writes. A save smuggled in here is refused before anything is queued.
            SequenceAdmission a = JobSequenceRules.Admit(seq, false);
            Assert.True(a.Ok, a.Refusal);

            foreach (JToken t in seq)
            {
                string tool = (string)t["tool"];
                Assert.Contains(tool, JobSequenceRules.Allowed);
                if (tool == "horizun_document_session")
                    Assert.Equal("close", (string)t["arguments"]["operation"]);
            }
        }

        [Fact]
        public void Every_generated_close_asks_to_activate_another_document_first()
        {
            // WITHOUT THIS THE SWEEP CANNOT CLOSE ANYTHING IT OPENED, and every run
            // stopped after its first model with that model left open in somebody's
            // Revit - while the reply said read_only.
            //
            // The chain is unavoidable: horizun_open_document uses
            // OpenAndActivateDocument, so what it opens is ACTIVE;
            // horizun_audit_model refuses unless its target is the active document,
            // so it stays active; and horizun_document_session cannot close the
            // active document without being asked to activate another first.
            BatchPlan plan = PlanOf(Local("A"), Local("B"));
            JArray seq = BatchAuditRules.ToSequence(plan, new BatchOptions());

            foreach (JToken t in seq.Where(x => (string)x["tool"] == "horizun_document_session"))
            {
                Assert.Equal("close", (string)t["arguments"]["operation"]);
                Assert.True((bool)t["arguments"]["activate_other"],
                    "the close for " + (string)t["key"] + " does not ask to activate another document, " +
                    "so Revit will refuse it: the model being closed is always the active one.");
            }
        }

        [Fact]
        public void Every_generated_open_is_detached_so_the_sweep_has_no_central_to_write_to()
        {
            BatchPlan plan = PlanOf(Local("A"));
            JArray seq = BatchAuditRules.ToSequence(plan, new BatchOptions());
            Assert.True((bool)seq.Single(x => (string)x["key"] == "A.open")["arguments"]["detach"]);
        }

        [Fact]
        public void A_sweep_that_asks_to_open_attached_is_refused()
        {
            BatchPlan p = BatchAuditRules.Plan(new[] { Local("A") }, new BatchOptions { Detach = false });
            Assert.False(p.Ok);
            Assert.Equal(BatchRefusal.NotDetached, p.Code);
        }

        // ---- 16: cloud identity is typed, never a downloaded copy ----------------------------

        [Fact]
        public void A_cloud_model_expands_to_its_typed_guids_and_never_to_a_path()
        {
            var m = new BatchModel
            {
                Id = "acc", Origin = ModelOrigin.Cloud, ExpectedTitle = "Tower",
                CloudProjectGuid = "11111111-1111-1111-1111-111111111111",
                CloudModelGuid = "22222222-2222-2222-2222-222222222222",
                CloudRegion = "US"
            };
            BatchPlan plan = BatchAuditRules.Plan(new[] { m }, new BatchOptions());
            Assert.True(plan.Ok, plan.Message);

            var open = (JObject)BatchAuditRules.ToSequence(plan, new BatchOptions())
                .Single(x => (string)x["key"] == "acc.open")["arguments"];
            Assert.Equal("11111111-1111-1111-1111-111111111111", (string)open["cloud_project_guid"]);
            Assert.Equal("US", (string)open["cloud_region"]);
            Assert.Null(open["path"]);
        }

        [Fact]
        public void A_downloaded_copy_is_not_the_cloud_model()
        {
            var m = new BatchModel
            {
                Id = "acc", Origin = ModelOrigin.Cloud, ExpectedTitle = "Tower",
                CloudProjectGuid = Guid.NewGuid().ToString(),
                CloudModelGuid = Guid.NewGuid().ToString(),
                CloudRegion = "US",
                LocalPath = @"C:\downloads\Tower.rvt"
            };
            BatchPlan p = BatchAuditRules.Plan(new[] { m }, new BatchOptions());
            Assert.False(p.Ok);
            Assert.Equal(BatchRefusal.LocalPathAsCloud, p.Code);
        }

        [Fact]
        public void A_cloud_identity_that_is_not_a_guid_is_refused_now_rather_than_at_open_time()
        {
            var m = new BatchModel
            {
                Id = "acc", Origin = ModelOrigin.Cloud, ExpectedTitle = "Tower",
                CloudProjectGuid = "Tower Project",
                CloudModelGuid = Guid.NewGuid().ToString(),
                CloudRegion = "EMEA"
            };
            Assert.Equal(BatchRefusal.CloudIdentityNotAGuid,
                BatchAuditRules.Plan(new[] { m }, new BatchOptions()).Code);
        }

        [Fact]
        public void A_cloud_model_without_a_region_is_refused()
        {
            var m = new BatchModel
            {
                Id = "acc", Origin = ModelOrigin.Cloud, ExpectedTitle = "Tower",
                CloudProjectGuid = Guid.NewGuid().ToString(),
                CloudModelGuid = Guid.NewGuid().ToString()
            };
            Assert.Equal(BatchRefusal.CloudWithoutIdentity,
                BatchAuditRules.Plan(new[] { m }, new BatchOptions()).Code);
        }

        [Fact]
        public void An_origin_that_is_neither_local_nor_cloud_is_refused_rather_than_guessed()
        {
            BatchModel m = Local("A");
            m.Origin = "nas";
            Assert.Equal(BatchRefusal.UnknownOrigin,
                BatchAuditRules.Plan(new[] { m }, new BatchOptions()).Code);
        }
    }
}
