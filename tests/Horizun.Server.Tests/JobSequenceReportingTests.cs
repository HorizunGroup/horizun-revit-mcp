// -----------------------------------------------------------------------------
// Horizun Server tests - original Horizun code.
//
// A SWEEP IS ONE JOB, AND THE CALLER READS IT THROUGH horizun_job_status.
//
// The reply went away with the job_id, so this record is the only channel. What
// it must never do is make a stopped sweep look like a complete one - which is
// exactly what happens if the steps that never ran are omitted, or if a step
// left `running` by a dead process is later read as anything but unfinished.
//
// Asserted through the SERVER'S reader over real files on disk, because that is
// what the tool has while Revit is busy inside the very sweep these describe.
// -----------------------------------------------------------------------------
using System;
using System.IO;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Server.Tests
{
    public class JobSequenceReportingTests : IDisposable
    {
        private readonly string _root;
        private readonly string _savedRoot;

        public JobSequenceReportingTests()
        {
            _savedRoot = Environment.GetEnvironmentVariable(HorizunPaths.RootOverrideVariable);
            _root = Path.Combine(Path.GetTempPath(), "hz-jobseq-" + Guid.NewGuid().ToString("N"));
            Environment.SetEnvironmentVariable(HorizunPaths.RootOverrideVariable, _root);
            PipeClient.LivenessProbe = pid => true;
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(HorizunPaths.RootOverrideVariable, _savedRoot);
            PipeClient.LivenessProbe = null;
            try { Directory.Delete(_root, true); } catch { }
        }

        private static JObject Read(Job job)
        {
            JObject reply = JobStatus.Handle(new JObject { ["job_id"] = job.Id });
            var jobs = (JArray)reply["jobs"];
            Assert.Single(jobs);
            return (JObject)jobs[0];
        }

        private static JArray Steps(JObject job) => (JArray)job["steps"];

        private static string StatusOf(JObject job, string key)
        {
            JToken step = Steps(job).FirstOrDefault(s => (string)s["key"] == key);
            return step == null ? null : (string)step["status"];
        }

        [Fact]
        public void Every_submitted_step_is_reported_in_submission_order_including_the_ones_that_never_ran()
        {
            Job job = Job.Start("horizun_submit_job");
            job.MarkRunning();
            job.Step("a.open", "horizun_open_document", "running", null, null);
            job.Step("a.open", "horizun_open_document", "succeeded", "r1.json", null);
            job.Step("a.audit", "horizun_audit_model", "running", null, null);
            job.Step("a.audit", "horizun_audit_model", "failed", null, "the model would not read.");
            job.Step("a.close", "horizun_document_session", "succeeded", null, "ran so nothing was left open.");
            job.Step("b.open", "horizun_open_document", "not_run", null, "an earlier step failed.");
            job.Step("b.audit", "horizun_audit_model", "not_run", null, "an earlier step failed.");
            job.Step("b.close", "horizun_document_session", "not_run", null, "an earlier step failed.");
            job.Result("{}");
            job.Finish("failed", "step a.audit failed.");

            JObject read = Read(job);
            Assert.Equal(
                new[] { "a.open", "a.audit", "a.close", "b.open", "b.audit", "b.close" },
                Steps(read).Select(s => (string)s["key"]).ToArray());
            Assert.Equal(6, (int)read["step_count"]);

            // THE ASSERTION THIS FILE EXISTS FOR: the three steps that never ran are
            // present and say so. Omitting them would make a sweep that stopped at
            // model one read as a one-model sweep that worked.
            Assert.Equal("not_run", StatusOf(read, "b.open"));
            Assert.Equal("not_run", StatusOf(read, "b.audit"));
            Assert.Equal("not_run", StatusOf(read, "b.close"));
            Assert.Equal("failed", read.Value<string>("final_status"));
        }

        [Fact]
        public void The_last_state_written_for_a_key_wins_because_the_record_is_append_only()
        {
            Job job = Job.Start("horizun_submit_job");
            job.MarkRunning();
            job.Step("a.open", "horizun_open_document", "running", null, null);
            job.Step("a.open", "horizun_open_document", "succeeded", "r1.json", null);

            JObject read = Read(job);
            Assert.Single(Steps(read));
            Assert.Equal("succeeded", StatusOf(read, "a.open"));
            Assert.Equal("r1.json", (string)Steps(read)[0]["result_ref"]);
        }

        [Fact]
        public void A_step_left_running_by_a_process_that_died_is_never_read_as_succeeded()
        {
            Job job = Job.Start("horizun_submit_job");
            job.MarkRunning();
            job.Step("a.open", "horizun_open_document", "succeeded", "r1.json", null);
            job.Step("a.audit", "horizun_audit_model", "running", null, null);
            // No finish line: Revit died inside the audit.

            JObject read = Read(job);
            Assert.Equal("running", StatusOf(read, "a.audit"));
            Assert.False(read.Value<bool>("finished"));
            // And the record itself says which of "still working" and "died" this is,
            // which is what the caller needs before resubmitting anything.
            Assert.NotNull(read.Value<string>("state"));
        }

        [Fact]
        public void A_step_carrying_a_status_nobody_defined_is_counted_invalid_rather_than_passed_through()
        {
            Job job = Job.Start("horizun_submit_job");
            job.MarkRunning();
            job.Step("a.open", "horizun_open_document", "probably_fine", null, null);

            JObject read = Read(job);
            Assert.Empty(Steps(read));
            Assert.True(read.Value<int>("semantic_invalid_records_omitted") > 0);
            Assert.False(read.Value<bool>("record_complete"));
        }

        [Fact]
        public void A_job_that_is_not_a_sequence_reports_no_steps_rather_than_an_absent_field()
        {
            Job job = Job.Start("horizun_model_scan");
            job.MarkRunning();
            job.Write("scanning", 1, 2);
            job.Result("{}");
            job.Finish("ok", null);

            JObject read = Read(job);
            Assert.NotNull(read["steps"]);
            Assert.Empty(Steps(read));
            Assert.Equal(0, (int)read["step_count"]);
        }

        // ---- the contract, both directions ----------------------------------

        private static JObject SubmitSchema()
        {
            Horizun.Contracts.CommandContract c = Horizun.Contracts.Contract.Find("horizun_submit_job");
            Assert.NotNull(c);
            return c.InputSchema;
        }

        [Fact]
        public void The_schema_enum_and_the_admission_allowlist_are_the_same_set_in_both_directions()
        {
            // A schema that offers a tool admission refuses is a documented option the
            // server accepts and the queue then rejects; a tool admission allows but
            // the schema omits is refused before anyone sees the reason. Both are the
            // same failure, so both directions are asserted.
            var enumerated = ((JArray)SubmitSchema()["properties"]["sequence"]["items"]["properties"]["tool"]["enum"])
                .Select(x => (string)x).OrderBy(x => x, StringComparer.Ordinal).ToArray();
            var allowed = JobSequenceRules.Allowed.OrderBy(x => x, StringComparer.Ordinal).ToArray();

            Assert.NotEmpty(allowed);
            Assert.Equal(allowed, enumerated);
        }

        [Fact]
        public void The_submission_shapes_are_mutually_exclusive_in_the_schema()
        {
            JObject schema = SubmitSchema();
            var oneOf = (JArray)schema["oneOf"];
            Assert.NotNull(oneOf);

            var shapes = oneOf.Select(o => string.Join("+", ((JArray)o["required"]).Select(x => (string)x)))
                              .OrderBy(x => x, StringComparer.Ordinal).ToArray();
            Assert.Equal(new[] { "models", "sequence", "tool+arguments" }, shapes);

            // And the schema stays CLOSED: an unknown key must be refused rather than
            // carried into a sequence entry nobody validated.
            Assert.False(schema.Value<bool>("additionalProperties"));
            Assert.False(((JObject)schema["properties"]["sequence"]["items"]).Value<bool>("additionalProperties"));
            Assert.False(((JObject)schema["properties"]["models"]["items"]).Value<bool>("additionalProperties"));
        }

        [Fact]
        public void A_swept_model_must_declare_the_title_its_audit_and_close_will_name()
        {
            var required = ((JArray)SubmitSchema()["properties"]["models"]["items"]["required"])
                .Select(x => (string)x).ToArray();
            Assert.Contains("id", required);
            Assert.Contains("expected_title", required);
        }

        [Fact]
        public void The_sequence_cap_in_the_schema_is_the_cap_admission_enforces()
        {
            Assert.Equal(JobSequenceRules.MaxEntries,
                (int)SubmitSchema()["properties"]["sequence"]["maxItems"]);
        }

        [Fact]
        public void A_step_written_before_the_job_was_running_is_refused()
        {
            // The append-only lifecycle is start -> running -> steps -> result -> finish.
            // A step accepted while the record still reads "queued" would tell a caller
            // that work began when it had not.
            Job job = Job.Start("horizun_submit_job");
            job.Step("a.open", "horizun_open_document", "running", null, null);

            JObject read = Read(job);
            Assert.Empty(Steps(read));
            Assert.True(read.Value<int>("semantic_invalid_records_omitted") > 0);
        }
    }
}
