// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// A sweep is one job whose work is an ordered sequence. Two things decide
// whether that is safe, and both are proved here rather than reasoned about:
//
//   THE ALLOWLIST IS THE READ-ONLY GUARANTEE. A submission naming a tool that
//   writes is refused WHOLE, with nothing queued, naming the index - not refused
//   at step nine with eight models already visited.
//
//   A STEP AFTER A FAILURE IS `not_run`. Never omitted, never succeeded. A
//   sequence that stops at step three and returns two steps reads as a two-step
//   sequence that worked, and that is the report somebody signs.
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class JobSequenceAdmissionTests
    {
        private static JObject Entry(string key, string tool, JObject args = null)
        {
            return new JObject
            {
                ["key"] = key,
                ["tool"] = tool,
                ["arguments"] = args ?? new JObject()
            };
        }

        private static JArray Good()
        {
            return new JArray
            {
                Entry("a.open", "horizun_open_document", new JObject { ["path"] = @"C:\a.rvt", ["detach"] = true }),
                Entry("a.audit", "horizun_audit_model", new JObject { ["target_document"] = "A" }),
                Entry("a.close", "horizun_document_session",
                      new JObject { ["operation"] = "close", ["target_document"] = "A" })
            };
        }

        [Fact]
        public void A_read_only_sequence_is_admitted_in_submission_order()
        {
            SequenceAdmission a = JobSequenceRules.Admit(Good(), false);
            Assert.True(a.Ok, a.Refusal);
            Assert.Equal(new[] { "a.open", "a.audit", "a.close" }, a.Entries.Select(e => e.Key).ToArray());
            Assert.All(a.Entries, e => Assert.Equal(StepStatus.Queued, e.Status));
        }

        [Fact]
        public void One_entry_naming_a_writing_tool_refuses_the_whole_submission_and_names_the_index()
        {
            JArray seq = Good();
            seq.Add(Entry("a.write", "horizun_write_params_verified"));

            SequenceAdmission a = JobSequenceRules.Admit(seq, false);
            Assert.False(a.Ok);
            Assert.Empty(a.Entries);
            Assert.Contains("sequence entry 3", a.Refusal);
            Assert.Contains("horizun_write_params_verified", a.Refusal);
            // The refusal has to end the ambiguity a caller most needs settled.
            Assert.Contains("Nothing was queued.", a.Refusal);
        }

        [Fact]
        public void Execute_python_is_not_admissible_in_a_sequence()
        {
            JArray seq = Good();
            seq.Add(Entry("a.py", "horizun_execute_python", new JObject { ["code"] = "pass" }));
            SequenceAdmission a = JobSequenceRules.Admit(seq, false);
            Assert.False(a.Ok);
            Assert.Contains("horizun_execute_python", a.Refusal);
        }

        [Fact]
        public void Document_session_is_admissible_only_for_close()
        {
            foreach (string op in new[] { "open", "save", "save_as", "inspect" })
            {
                var seq = new JArray
                {
                    Entry("x", "horizun_document_session", new JObject { ["operation"] = op })
                };
                SequenceAdmission a = JobSequenceRules.Admit(seq, false);
                Assert.False(a.Ok);
                Assert.Contains("Only 'close' is admissible", a.Refusal);
            }
        }

        [Fact]
        public void Document_session_with_no_operation_is_refused_rather_than_assumed_to_be_close()
        {
            var seq = new JArray { Entry("x", "horizun_document_session") };
            SequenceAdmission a = JobSequenceRules.Admit(seq, false);
            Assert.False(a.Ok);
            Assert.Contains("<none>", a.Refusal);
        }

        [Fact]
        public void Duplicate_keys_are_refused_because_steps_are_reported_by_key()
        {
            var seq = new JArray
            {
                Entry("same", "horizun_audit_model", new JObject { ["target_document"] = "A" }),
                Entry("same", "horizun_audit_model", new JObject { ["target_document"] = "B" })
            };
            SequenceAdmission a = JobSequenceRules.Admit(seq, false);
            Assert.False(a.Ok);
            Assert.Contains("repeats the key", a.Refusal);
        }

        [Fact]
        public void A_submission_carrying_both_a_tool_and_a_sequence_is_refused_rather_than_resolved()
        {
            SequenceAdmission a = JobSequenceRules.Admit(Good(), true);
            Assert.False(a.Ok);
            Assert.Contains("never both", a.Refusal);
        }

        [Fact]
        public void An_empty_sequence_is_refused()
        {
            Assert.False(JobSequenceRules.Admit(new JArray(), false).Ok);
            Assert.False(JobSequenceRules.Admit(null, false).Ok);
        }

        [Fact]
        public void A_sequence_beyond_the_cap_is_refused_rather_than_truncated()
        {
            var seq = new JArray();
            for (int i = 0; i <= JobSequenceRules.MaxEntries; i++)
                seq.Add(Entry("k" + i, "horizun_audit_model", new JObject { ["target_document"] = "A" }));
            SequenceAdmission a = JobSequenceRules.Admit(seq, false);
            Assert.False(a.Ok);
            Assert.Contains("limited to " + JobSequenceRules.MaxEntries, a.Refusal);
        }

        [Fact]
        public void An_entry_without_a_key_or_arguments_is_refused()
        {
            var noKey = new JArray { new JObject { ["tool"] = "horizun_audit_model", ["arguments"] = new JObject() } };
            Assert.Contains("has no 'key'", JobSequenceRules.Admit(noKey, false).Refusal);

            var noArgs = new JArray { new JObject { ["key"] = "k", ["tool"] = "horizun_audit_model" } };
            Assert.Contains("has no 'arguments'", JobSequenceRules.Admit(noArgs, false).Refusal);
        }

        // ---- the step state machine ------------------------------------------

        private static List<SequenceEntry> Five()
        {
            var l = new List<SequenceEntry>();
            for (int i = 1; i <= 5; i++)
                l.Add(new SequenceEntry { Key = "s" + i, Tool = "horizun_audit_model", Status = StepStatus.Queued });
            return l;
        }

        [Fact]
        public void A_sequence_whose_third_step_fails_leaves_four_and_five_not_run_and_the_job_failed()
        {
            List<SequenceEntry> steps = Five();
            steps[0].Status = StepStatus.Succeeded;
            steps[1].Status = StepStatus.Succeeded;
            steps[2].Status = StepStatus.Failed;

            JobSequenceRules.SettleAfterStop(steps, 2);

            Assert.Equal(StepStatus.NotRun, steps[3].Status);
            Assert.Equal(StepStatus.NotRun, steps[4].Status);
            Assert.Equal("failed", JobSequenceRules.TerminalStatus(steps));
            // NEVER OMITTED: all five are in the reply.
            Assert.Equal(5, JobSequenceRules.StepsJson(steps).Count);
        }

        [Fact]
        public void A_step_still_marked_running_when_the_record_settles_never_becomes_succeeded()
        {
            List<SequenceEntry> steps = Five();
            steps[0].Status = StepStatus.Succeeded;
            steps[1].Status = StepStatus.Running;   // the process died here
            JobSequenceRules.SettleAfterStop(steps, 0);

            Assert.Equal(StepStatus.NotRun, steps[1].Status);
            Assert.Equal("failed", JobSequenceRules.TerminalStatus(steps));
        }

        [Fact]
        public void Only_a_sequence_whose_every_step_succeeded_is_ok()
        {
            List<SequenceEntry> steps = Five();
            foreach (SequenceEntry e in steps) e.Status = StepStatus.Succeeded;
            Assert.Equal("ok", JobSequenceRules.TerminalStatus(steps));

            steps[4].Status = StepStatus.NotRun;
            Assert.Equal("failed", JobSequenceRules.TerminalStatus(steps));
            Assert.Equal("failed", JobSequenceRules.TerminalStatus(new List<SequenceEntry>()));
        }

        [Fact]
        public void Every_step_reports_its_key_tool_status_times_result_ref_and_error()
        {
            var e = new SequenceEntry
            {
                Key = "a.audit", Tool = "horizun_audit_model", Status = StepStatus.Succeeded,
                StartedUtc = "2026-01-01T00:00:00Z", FinishedUtc = "2026-01-01T00:01:00Z",
                ResultRef = "job-1.json"
            };
            JObject o = e.ToJson();
            foreach (string k in new[] { "key", "tool", "status", "started_utc", "finished_utc",
                                         "result_ref", "error" })
                Assert.True(o.ContainsKey(k), "steps[] must carry '" + k + "'");
        }

        // ---- a sweep IS a sequence -------------------------------------------

        private static BatchModel M(string id, string title)
        {
            return new BatchModel { Id = id, LocalPath = @"C:\m\" + id + ".rvt", ExpectedTitle = title };
        }

        [Fact]
        public void A_model_list_expands_into_open_audit_close_per_model_in_listed_order()
        {
            BatchPlan plan = BatchAuditRules.Plan(new[] { M("a", "A"), M("b", "B") }, new BatchOptions());
            Assert.True(plan.Ok, plan.Message);

            JArray seq = BatchAuditRules.ToSequence(plan, new BatchOptions { ProfileVersion = "v1" });
            Assert.Equal(
                new[] { "a.open", "a.audit", "a.close", "b.open", "b.audit", "b.close" },
                seq.Select(x => (string)x["key"]).ToArray());

            // And the sequence it produces is admissible: the read-only allowlist is
            // the same one, rather than a second list this expansion happens to match.
            SequenceAdmission a = JobSequenceRules.Admit(seq, false);
            Assert.True(a.Ok, a.Refusal);
        }

        [Fact]
        public void Every_generated_open_is_detached_so_the_sweep_has_no_central_to_write_to()
        {
            BatchPlan plan = BatchAuditRules.Plan(new[] { M("a", "A") }, new BatchOptions());
            JArray seq = BatchAuditRules.ToSequence(plan, new BatchOptions());
            JToken open = seq.First(x => (string)x["key"] == "a.open");
            Assert.True((bool)open["arguments"]["detach"]);
        }

        [Fact]
        public void Every_generated_audit_and_close_names_its_target_document()
        {
            BatchPlan plan = BatchAuditRules.Plan(new[] { M("a", "Tower - Structure") }, new BatchOptions());
            JArray seq = BatchAuditRules.ToSequence(plan, new BatchOptions());

            Assert.Equal("Tower - Structure",
                (string)seq.First(x => (string)x["key"] == "a.audit")["arguments"]["target_document"]);
            Assert.Equal("Tower - Structure",
                (string)seq.First(x => (string)x["key"] == "a.close")["arguments"]["target_document"]);
        }

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

            JArray seq = BatchAuditRules.ToSequence(plan, new BatchOptions());
            var open = (JObject)seq.First(x => (string)x["key"] == "acc.open")["arguments"];
            Assert.Equal("11111111-1111-1111-1111-111111111111", (string)open["cloud_project_guid"]);
            Assert.Equal("US", (string)open["cloud_region"]);
            Assert.Null(open["path"]);
        }

        // ---- the consolidated report ------------------------------------------

        private static SequenceEntry S(string key, string tool, string status, string error = null)
        {
            return new SequenceEntry { Key = key, Tool = tool, Status = status, Error = error };
        }

        [Fact]
        public void A_model_whose_open_failed_is_not_opened_and_its_later_steps_are_not_a_clean_result()
        {
            BatchPlan plan = BatchAuditRules.Plan(new[] { M("a", "A"), M("b", "B") }, new BatchOptions());
            var steps = new[]
            {
                S("a.open", "horizun_open_document", StepStatus.Succeeded),
                S("a.audit", "horizun_audit_model", StepStatus.Succeeded),
                S("a.close", "horizun_document_session", StepStatus.Succeeded),
                S("b.open", "horizun_open_document", StepStatus.Failed, "the file is missing."),
                S("b.audit", "horizun_audit_model", StepStatus.NotRun),
                S("b.close", "horizun_document_session", StepStatus.NotRun)
            };

            BatchRun run = BatchAuditRules.Consolidate(plan, steps);
            Assert.Equal(BatchOutcome.Audited, run.Results.Single(r => r.Id == "a").Outcome);
            Assert.Equal(BatchOutcome.NotOpened, run.Results.Single(r => r.Id == "b").Outcome);
            Assert.Equal(BatchRunStatus.Incomplete, run.Status);

            JObject o = BatchAuditRules.Aggregate(plan, run);
            Assert.Equal(2, (int)o["models_listed"]);
            Assert.Equal(1, (int)o["models_audited"]);
            Assert.False((bool)o["all_models_assessed"]);
        }

        [Fact]
        public void A_close_that_failed_leaves_the_run_saying_a_document_is_open()
        {
            BatchPlan plan = BatchAuditRules.Plan(new[] { M("a", "A") }, new BatchOptions());
            var steps = new[]
            {
                S("a.open", "horizun_open_document", StepStatus.Succeeded),
                S("a.audit", "horizun_audit_model", StepStatus.Succeeded),
                S("a.close", "horizun_document_session", StepStatus.Failed, "Revit refused.")
            };

            BatchRun run = BatchAuditRules.Consolidate(plan, steps);
            Assert.Equal(BatchOutcome.CloseFailed, run.Results[0].Outcome);
            Assert.False(run.Results[0].DocumentClosed);
            Assert.Equal(BatchRunStatus.StoppedDocumentLeftOpen, run.Status);
            Assert.Equal(1, (int)BatchAuditRules.Aggregate(plan, run)["documents_left_open"]);
        }

        [Fact]
        public void A_model_that_opened_and_was_never_audited_is_not_assessed_rather_than_clean()
        {
            BatchPlan plan = BatchAuditRules.Plan(new[] { M("a", "A") }, new BatchOptions());
            var steps = new[]
            {
                S("a.open", "horizun_open_document", StepStatus.Succeeded),
                S("a.audit", "horizun_audit_model", StepStatus.Failed, "the audit named another document."),
                S("a.close", "horizun_document_session", StepStatus.Succeeded)
            };

            BatchRun run = BatchAuditRules.Consolidate(plan, steps);
            Assert.Equal(BatchOutcome.NotAssessed, run.Results[0].Outcome);
            Assert.True(run.Results[0].DocumentClosed);
            Assert.Contains("named another document", run.Results[0].Why);
        }

        [Fact]
        public void A_sweep_that_never_ran_reports_every_model_not_assessed()
        {
            BatchPlan plan = BatchAuditRules.Plan(new[] { M("a", "A"), M("b", "B") }, new BatchOptions());
            BatchRun run = BatchAuditRules.Consolidate(plan, new SequenceEntry[0]);

            Assert.All(run.Results, r => Assert.Equal(BatchOutcome.NotAssessed, r.Outcome));
            JObject o = BatchAuditRules.Aggregate(plan, run);
            Assert.Equal(0, (int)o["models_audited"]);
            Assert.Equal(2, (int)o["models_not_assessed"]);
            Assert.False((bool)o["all_models_assessed"]);
        }
    }
}
