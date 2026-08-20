// -----------------------------------------------------------------------------
// Horizun Server tests - original Horizun code.
//
// FIVE STATES, NOT TWO.
//
// horizun_job_status reported `finished` and, when that was false, one sentence:
// "either it is still running, or the process died - a log cannot tell those
// apart, and this will not guess." That refusal was right about the case it was
// written for and wrong about two others it silently absorbed:
//
//   QUEUED       opened, never picked up. Knowable exactly.
//   NOT_STARTED  Revit refused to schedule it, shut down first, or the queue was
//                full. Knowable exactly.
//
// Reporting a job that provably never ran as "running, or the process died" is
// not caution - it is discarding a fact the system had, and it points a caller
// at the most alarming of the possibilities. A not_started job is safe to send
// again; a "might be running" one is not. That difference is the whole reason
// for the field.
//
// Asserted through the SERVER'S reader over real files on disk, because that is
// what the tool has: it answers while Revit's UI thread is inside the very
// command these describe.
// -----------------------------------------------------------------------------
using System;
using System.IO;
using System.Linq;
using System.Text;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Server.Tests
{
    public class JobStateReportingTests : IDisposable
    {
        private readonly string _root;
        private readonly string _savedRoot;

        // What the liveness probe answers. Jobs in these tests are written by the TEST
        // process, whose name is not "revit" - without the probe every open job would
        // read as a dead process, which is the scenario under test, not the default.
        private bool _processAlive = true;

        public JobStateReportingTests()
        {
            _savedRoot = Environment.GetEnvironmentVariable(HorizunPaths.RootOverrideVariable);
            _root = Path.Combine(Path.GetTempPath(), "hz-jobstates-" + Guid.NewGuid().ToString("N"));
            Environment.SetEnvironmentVariable(HorizunPaths.RootOverrideVariable, _root);
            PipeClient.LivenessProbe = pid => _processAlive;
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(HorizunPaths.RootOverrideVariable, _savedRoot);
            PipeClient.LivenessProbe = null;
            try { Directory.Delete(_root, true); } catch { }
        }

        /// <summary>Ask the server about one job, the way the tool does.</summary>
        private static JObject Read(Job job)
        {
            JObject reply = JobStatus.Handle(new JObject { ["job_id"] = job.Id });
            var jobs = (JArray)reply["jobs"];
            Assert.Single(jobs);
            return (JObject)jobs[0];
        }

        [Fact]
        public void A_job_waiting_for_its_turn_is_queued()
        {
            Job j = Job.Start("horizun_execute_python");

            JObject reported = Read(j);

            Assert.Equal("queued", (string)reported["state"]);
            Assert.False((bool)reported["finished"]);
            Assert.Null((string)reported["running_since"]);
            // It has to say the safe thing, because this is the state where a caller
            // is most tempted to re-send.
            Assert.Contains("QUEUED", (string)reported["what_this_means"]);
            Assert.Contains("Do NOT re-send", (string)reported["what_this_means"]);
        }

        [Fact]
        public void A_job_on_the_ui_thread_is_running_and_keeps_the_real_ambiguity()
        {
            Job j = Job.Start("horizun_execute_python");
            j.MarkRunning();
            j.Write("halfway", 1, 2);

            JObject reported = Read(j);

            Assert.Equal("running", (string)reported["state"]);
            Assert.False((bool)reported["finished"]);
            Assert.False(string.IsNullOrEmpty((string)reported["running_since"]));
            // With the process alive, the ambiguity that IS real is kept: a slow step
            // and a hang look the same from a log, and this does not guess. What it no
            // longer says is "or the process died" - the OS was asked and it did not.
            Assert.True((bool)reported["process_alive"]);
            Assert.Contains("or hung", (string)reported["what_this_means"]);
            Assert.DoesNotContain("process died", (string)reported["what_this_means"]);
        }

        [Fact]
        public void A_running_job_whose_process_died_says_so_as_a_fact()
        {
            // The 2026-07-31 batch, in miniature: Revit crashed mid-job three times,
            // job_status said "running, or the process died" all three times, and the
            // caller had to leave the MCP and ask Windows. The record carries the pid;
            // this is the answer it could have given.
            Job j = Job.Start("horizun_execute_python");
            j.MarkRunning();
            j.Write("model 12 of 31", 12, 31);
            _processAlive = false;

            JObject reported = Read(j);

            Assert.Equal("running", (string)reported["state"]);
            Assert.False((bool)reported["process_alive"]);
            string means = (string)reported["what_this_means"];
            Assert.Contains("PROCESS DIED", means);
            Assert.Contains("NEVER finish", means);
            // The work already checkpointed is not undone by the crash, and re-running
            // on top of it is the second-write risk the caller has to weigh.
            Assert.Contains("second write", means);
        }

        [Fact]
        public void A_queued_job_whose_process_died_will_never_run_and_is_safe_to_resend()
        {
            Job j = Job.Start("horizun_execute_python");
            _processAlive = false;

            JObject reported = Read(j);

            Assert.Equal("queued", (string)reported["state"]);
            Assert.False((bool)reported["process_alive"]);
            string means = (string)reported["what_this_means"];
            // Nothing ran and nothing will: unlike a live queue, this is safe to send
            // again - the opposite advice from the queued-and-alive case.
            Assert.Contains("NEVER RUN", means);
            Assert.Contains("safe to send again", means);
            Assert.Contains("NEW idempotency_key", means);
        }

        [Fact]
        public void A_record_without_a_pid_keeps_the_old_honest_ambiguity()
        {
            // Records written before the pid was stamped exist on disk. For them,
            // liveness genuinely is not knowable, and inventing it would be the exact
            // guess this tool refuses to make.
            string dir = HorizunPaths.JobsDir();
            Directory.CreateDirectory(dir);
            File.WriteAllLines(Path.Combine(dir, "legacy-job.jsonl"), new[]
            {
                "{\"event\":\"start\",\"tool\":\"horizun_execute_python\",\"at\":\"2026-07-30 10:00:00.000\"}",
                "{\"event\":\"running\",\"at\":\"2026-07-30 10:00:01.000\"}"
            });

            JObject reply = JobStatus.Handle(new JObject { ["job_id"] = "legacy-job" });
            var reported = (JObject)((JArray)reply["jobs"])[0];

            Assert.Equal("running", (string)reported["state"]);
            Assert.Equal(JTokenType.Null, reported["pid"].Type);
            Assert.Equal(JTokenType.Null, reported["process_alive"].Type);
            Assert.Contains("or the process died", (string)reported["what_this_means"]);
        }

        [Fact]
        public void A_finished_job_does_not_report_liveness()
        {
            // The record already answers everything about a finished job; whether the
            // process that ran it is still up adds nothing and would invite reading
            // meaning into it.
            Job j = Job.Start("horizun_execute_python");
            j.MarkRunning();
            j.Finish("ok", null);
            _processAlive = false;

            JObject reported = Read(j);

            Assert.Equal("ok", (string)reported["state"]);
            Assert.NotEqual(JTokenType.Null, reported["pid"].Type);
            Assert.Equal(JTokenType.Null, reported["process_alive"].Type);
        }

        [Fact]
        public void A_finished_job_is_ok_and_carries_its_result()
        {
            Job j = Job.Start("horizun_execute_python");
            j.MarkRunning();
            j.Result("{\"done\":true}");
            j.Finish("ok", null);

            JObject reported = Read(j);

            Assert.Equal("ok", (string)reported["state"]);
            Assert.True((bool)reported["finished"]);
            Assert.True((bool)reported["result_present"]);
            Assert.True((bool)reported["result"]["done"]);
        }

        [Fact]
        public void An_async_job_carries_revit_said_beside_its_result()
        {
            // 5.21: the sync path attaches revit_said to every reply; the async path
            // dropped it, so the dialog/warning telemetry that diagnoses a batch failure
            // vanished for exactly the run_async work batches are made of. It is stored
            // on the result event now and surfaced here, the same sibling of the payload
            // a synchronous caller sees.
            Job j = Job.Start("horizun_execute_python");
            j.MarkRunning();
            j.Result("{\"opened\":false}",
                     "{\"dialogs\":1,\"items\":[{\"kind\":\"dialog\",\"description\":\"Dialog_Revit_DocWarnDialog\"," +
                     "\"answered\":\"cancelled by the bridge (nobody is at the keyboard to answer it)\"}]}");
            j.Finish("ok", null);

            JObject reported = Read(j);

            Assert.Equal(JTokenType.Object, reported["revit_said"].Type);
            Assert.Equal(1, (int)reported["revit_said"]["dialogs"]);
            Assert.Equal("Dialog_Revit_DocWarnDialog",
                         (string)reported["revit_said"]["items"][0]["description"]);
        }

        [Fact]
        public void A_job_that_recorded_no_revit_said_reports_null_not_a_dropped_field()
        {
            // Absent must read as "Revit raised nothing", never as "it was dropped" -
            // the confusion 5.21 removed. A plain result with no revit_said is null here,
            // exactly like a synchronous reply that carried none.
            Job j = Job.Start("horizun_execute_python");
            j.MarkRunning();
            j.Result("{\"done\":true}");
            j.Finish("ok", null);

            JObject reported = Read(j);

            Assert.Equal(JTokenType.Null, reported["revit_said"].Type);
        }

        [Fact]
        public void A_failed_async_job_still_carries_what_revit_said()
        {
            // revit_said is usually the REASON a job failed, and the async caller has no
            // other channel to learn it. It travels on the failure path too.
            Job j = Job.Start("horizun_execute_python");
            j.MarkRunning();
            j.Result(null, "{\"errors\":1,\"items\":[{\"kind\":\"error\",\"description\":\"Opening was canceled\"}]}");
            j.Finish("failed", "the open was cancelled");

            JObject reported = Read(j);

            Assert.Equal("failed", (string)reported["state"]);
            Assert.Equal(1, (int)reported["revit_said"]["errors"]);
            Assert.Equal("Opening was canceled",
                         (string)reported["revit_said"]["items"][0]["description"]);
        }

        [Fact]
        public void A_job_that_ran_and_failed_is_failed()
        {
            Job j = Job.Start("horizun_execute_python");
            j.MarkRunning();
            j.Finish("failed", "the script raised");

            JObject reported = Read(j);

            Assert.Equal("failed", (string)reported["state"]);
            // A failure is not a rollback, and the difference decides what the caller
            // does next.
            Assert.Contains("not a rollback", (string)reported["what_this_means"]);
        }

        [Fact]
        public void A_job_revit_never_scheduled_is_not_started_and_says_it_is_safe_to_resend()
        {
            Job j = Job.Start("horizun_execute_python");
            j.Finish("not_started", "Revit shut down before this job started.");

            JObject reported = Read(j);

            Assert.Equal("not_started", (string)reported["state"]);
            Assert.True((bool)reported["finished"]);
            Assert.Null((string)reported["running_since"]);

            // THE POINT. Nothing ran, so re-sending cannot be a second write - and
            // this used to be reported as the same ambiguity as a process that died.
            string means = (string)reported["what_this_means"];
            Assert.Contains("NEVER RAN", means);
            Assert.Contains("safe to send again", means);
            Assert.Contains("NEW idempotency_key", means);
        }

        [Fact]
        public void The_five_states_are_all_distinct()
        {
            var states = new System.Collections.Generic.List<string>();

            Job queued = Job.Start("horizun_execute_python");
            states.Add((string)Read(queued)["state"]);

            Job running = Job.Start("horizun_execute_python");
            running.MarkRunning();
            states.Add((string)Read(running)["state"]);

            Job ok = Job.Start("horizun_execute_python");
            ok.MarkRunning(); ok.Finish("ok", null);
            states.Add((string)Read(ok)["state"]);

            Job failed = Job.Start("horizun_execute_python");
            failed.MarkRunning(); failed.Finish("failed", "boom");
            states.Add((string)Read(failed)["state"]);

            Job never = Job.Start("horizun_execute_python");
            never.Finish("not_started", "Revit refused to schedule it.");
            states.Add((string)Read(never)["state"]);

            Assert.Equal(new[] { "queued", "running", "ok", "failed", "not_started" }, states);
            Assert.Equal(5, states.Distinct().Count());
        }

        [Fact]
        public void An_unrecognised_final_status_makes_the_record_explicitly_truncated()
        {
            Job j = Job.Start("horizun_execute_python");
            j.Finish("something_new", "a status this reader predates");

            JObject reported = Read(j);

            Assert.Equal("record_truncated", (string)reported["state"]);
            Assert.False((bool)reported["record_complete"]);
            Assert.True((bool)reported["finished"]); // terminal line seen, semantics not trusted
            Assert.Equal(1, (int)reported["semantic_invalid_records_omitted"]);
            Assert.Contains("NOT a complete account", (string)reported["what_this_means"]);
        }

        [Fact]
        public void Missing_unknown_out_of_order_and_duplicate_events_never_form_a_clean_record()
        {
            string at = "2026-01-01 00:00:00.000";

            Job missingStatus = Job.Start("horizun_execute_python");
            File.AppendAllText(missingStatus.Path,
                "{\"event\":\"finish\",\"at\":\"" + at + "\"}" + Environment.NewLine);
            JObject missing = Read(missingStatus);
            Assert.Equal("record_truncated", (string)missing["state"]);
            Assert.True((bool)missing["finished"]);

            Job unknown = Job.Start("horizun_execute_python");
            File.AppendAllText(unknown.Path,
                "{\"event\":\"surprise\",\"at\":\"" + at + "\"}" + Environment.NewLine);
            JObject unknownReported = Read(unknown);
            Assert.Equal("record_truncated", (string)unknownReported["state"]);

            Job outOfOrder = Job.Start("horizun_execute_python");
            File.AppendAllText(outOfOrder.Path,
                "{\"event\":\"result\",\"payload\":\"{}\",\"at\":\"" + at + "\"}" + Environment.NewLine);
            JObject outOfOrderReported = Read(outOfOrder);
            Assert.Equal("record_truncated", (string)outOfOrderReported["state"]);

            Job duplicateTerminal = Job.Start("horizun_execute_python");
            duplicateTerminal.MarkRunning();
            duplicateTerminal.Finish("ok", null);
            File.AppendAllText(duplicateTerminal.Path,
                "{\"event\":\"finish\",\"status\":\"failed\",\"at\":\"" + at + "\"}" + Environment.NewLine);
            JObject duplicate = Read(duplicateTerminal);
            Assert.Equal("record_truncated", (string)duplicate["state"]);
            Assert.Equal("ok", (string)duplicate["final_status"]); // first valid terminal kept as evidence

            Job missingPayload = Job.Start("horizun_execute_python");
            missingPayload.MarkRunning();
            File.AppendAllText(missingPayload.Path,
                "{\"event\":\"result\",\"at\":\"" + at + "\"}" + Environment.NewLine);
            missingPayload.Finish("ok", null);
            JObject missingPayloadReported = Read(missingPayload);
            Assert.Equal("record_truncated", (string)missingPayloadReported["state"]);
            Assert.False((bool)missingPayloadReported["result_present"]);

            Job objectPayload = Job.Start("horizun_execute_python");
            objectPayload.MarkRunning();
            File.AppendAllText(objectPayload.Path,
                "{\"event\":\"result\",\"payload\":{},\"at\":\"" + at + "\"}" + Environment.NewLine);
            objectPayload.Finish("ok", null);
            JObject objectPayloadReported = Read(objectPayload);
            Assert.Equal("record_truncated", (string)objectPayloadReported["state"]);

            string checkpoint = "{\"event\":\"checkpoint\",\"n\":1,\"label\":\"proof\",\"done\":1,\"total\":1,\"at\":\"" + at + "\"}";
            Job prematureCheckpoint = Job.Start("horizun_execute_python");
            File.AppendAllText(prematureCheckpoint.Path, checkpoint + Environment.NewLine);
            JObject premature = Read(prematureCheckpoint);
            Assert.Equal("record_truncated", (string)premature["state"]);
            Assert.DoesNotContain("safe to send again", (string)premature["what_this_means"],
                StringComparison.OrdinalIgnoreCase);

            Job checkpointAfterResult = Job.Start("horizun_execute_python");
            checkpointAfterResult.MarkRunning();
            checkpointAfterResult.Result("{}");
            File.AppendAllText(checkpointAfterResult.Path, checkpoint + Environment.NewLine);
            checkpointAfterResult.Finish("ok", null);
            JObject afterResult = Read(checkpointAfterResult);
            Assert.Equal("record_truncated", (string)afterResult["state"]);

            Job mismatchedFinishCount = Job.Start("horizun_execute_python");
            mismatchedFinishCount.MarkRunning();
            mismatchedFinishCount.Write("one", 1, 1);
            File.AppendAllText(mismatchedFinishCount.Path,
                "{\"event\":\"finish\",\"status\":\"ok\",\"checkpoints\":0,\"note\":null,\"at\":\"" + at + "\"}" + Environment.NewLine);
            JObject mismatch = Read(mismatchedFinishCount);
            Assert.Equal("record_truncated", (string)mismatch["state"]);

            foreach (JObject report in new[] { missing, unknownReported, outOfOrderReported, duplicate,
                                                missingPayloadReported, objectPayloadReported, premature,
                                                afterResult, mismatch })
            {
                Assert.False((bool)report["record_complete"]);
                Assert.True((int)report["semantic_invalid_records_omitted"] > 0);
                Assert.True((bool)report["record_truncated"]);
            }
        }

        [Fact]
        public void Async_machine_readable_fallback_gaps_and_detail_survive_the_job_record()
        {
            Job j = Job.Start("horizun_create_elements");
            j.MarkRunning();
            j.Result("{\"invalid\":1}", "{\"warnings\":[]}",
                "{\"allowed\":true,\"reason\":\"unsupported_kind\",\"write_started\":false}",
                "[{\"index\":0,\"reason\":\"unsupported_kind\"}]",
                "{\"transaction_group_started\":true,\"rollback_status\":\"rolled_back\"}");
            j.Finish("ok", null);

            JObject reported = Read(j);
            Assert.True((bool)reported["fallback"]["allowed"]);
            Assert.Equal("unsupported_kind", (string)reported["capability_gaps"][0]["reason"]);
            Assert.Equal("rolled_back", (string)reported["detail"]["rollback_status"]);
            Assert.NotNull(reported["revit_said"]);
        }

        [Fact]
        public void A_transient_result_append_failure_is_durably_visible_and_cannot_report_clean_ok()
        {
            var sink = new FailOneAppendSink(3); // start, running, RESULT fails; marker + finish recover
            Job j = Job.Start("horizun_create_elements", sink);
            j.MarkRunning();
            j.Result("{\"verified\":true}");
            j.Finish("ok", null);

            JObject reported = Read(j);
            Assert.Equal("record_incomplete", (string)reported["state"]);
            Assert.False((bool)reported["record_complete"]);
            Assert.Contains("simulated append failure", (string)reported["record_fault"]);
            Assert.Contains("do not retry", (string)reported["what_this_means"], StringComparison.OrdinalIgnoreCase);
            Assert.Equal("ok", (string)reported["final_status"]); // preserved as evidence, not trusted as state
        }

        [Fact]
        public void Job_status_caps_the_number_of_checkpoint_rows_kept_in_memory()
        {
            Job j = Job.Start("horizun_execute_python");
            j.MarkRunning();
            File.AppendAllLines(j.Path, Enumerable.Range(1, JobStatus.MaxCheckpointsPerJob + 1)
                .Select(i => "{\"event\":\"checkpoint\",\"n\":" + i +
                             ",\"label\":\"x\",\"done\":null,\"total\":null," +
                             "\"at\":\"2026-01-01 00:00:00.000\"}"));

            JObject reply = JobStatus.Handle(new JObject
            {
                ["job_id"] = j.Id,
                ["checkpoints"] = int.MaxValue,
                ["limit"] = int.MaxValue
            });
            JObject reported = (JObject)((JArray)reply["jobs"])[0];
            Assert.Equal(JobStatus.MaxCheckpointsPerJob, ((JArray)reported["recent_checkpoints"]).Count);
            Assert.Equal(JobStatus.MaxJobsPerCall, (int)reply["limits"]["jobs_per_call"]);
        }

        [Fact]
        public void Oversized_result_record_is_omitted_explicitly_without_breaking_json()
        {
            Job j = Job.Start("horizun_execute_python");
            string hugePayload = new string('p', JobStatus.MaxRecordBytes + 1024);
            File.AppendAllText(j.Path, new JObject
            {
                ["event"] = "result",
                ["payload"] = hugePayload,
                ["at"] = "2026-01-01 00:00:00.000"
            }.ToString(Newtonsoft.Json.Formatting.None) + Environment.NewLine);

            JObject reply = JobStatus.Handle(new JObject { ["job_id"] = j.Id });
            string serialized = reply.ToString(Newtonsoft.Json.Formatting.None);
            JObject reparsed = JObject.Parse(serialized);
            JObject reported = (JObject)reparsed["jobs"][0];

            Assert.Equal("record_truncated", (string)reported["state"]);
            Assert.False((bool)reported["record_complete"]);
            Assert.True((int)reported["oversized_records_omitted"] > 0);
            Assert.Contains("NOT a complete account", (string)reported["what_this_means"]);
        }

        [Fact]
        public void One_hundred_jobs_with_large_labels_obey_the_aggregate_response_budget()
        {
            string jobsDir = HorizunPaths.JobsDir();
            Directory.CreateDirectory(jobsDir);
            string label = new string('L', 60 * 1024);
            for (int i = 0; i < JobStatus.MaxJobsPerCall; i++)
            {
                string path = Path.Combine(jobsDir, "budget-" + i.ToString("D3") + ".jsonl");
                File.WriteAllText(path,
                    "{\"event\":\"start\",\"tool\":\"horizun_execute_python\",\"at\":\"2026-01-01 00:00:00.000\"}" + Environment.NewLine +
                    "{\"event\":\"running\",\"at\":\"2026-01-01 00:00:00.001\"}" + Environment.NewLine +
                    new JObject
                    {
                        ["event"] = "checkpoint",
                        ["n"] = 1,
                        ["label"] = label,
                        ["done"] = JValue.CreateNull(),
                        ["total"] = JValue.CreateNull(),
                        ["at"] = "2026-01-01 00:00:00.000"
                    }.ToString(Newtonsoft.Json.Formatting.None) + Environment.NewLine);
            }

            JObject reply = JobStatus.Handle(new JObject
            {
                ["limit"] = JobStatus.MaxJobsPerCall,
                ["checkpoints"] = 1
            });
            string serialized = reply.ToString(Newtonsoft.Json.Formatting.None);

            Assert.True(Encoding.UTF8.GetByteCount(serialized) <= JobStatus.MaxResponseBytes);
            Assert.Equal(JobStatus.MaxJobsPerCall, (int)reply["job_count"]);
            Assert.True((bool)reply["response_truncated"]);
            Assert.True((int)reply["jobs_omitted_for_response_budget"] > 0);
            Assert.Equal((int)reply["jobs_returned"], ((JArray)reply["jobs"]).Count);
            Assert.NotNull(JObject.Parse(serialized));
        }

        [Fact]
        public void An_unreadable_job_record_is_never_reported_as_complete_or_queued_cleanly()
        {
            Job j = Job.Start("horizun_execute_python");
            using (var locked = new FileStream(j.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                JObject reported = Read(j);

                Assert.Equal("record_incomplete", (string)reported["state"]);
                Assert.False((bool)reported["record_complete"]);
                Assert.False(string.IsNullOrWhiteSpace((string)reported["read_error"]));
                Assert.Contains("do not retry", (string)reported["what_this_means"],
                    StringComparison.OrdinalIgnoreCase);
            }
        }

        private sealed class FailOneAppendSink : IJobSink
        {
            private readonly int _failure;
            private int _append;
            public FailOneAppendSink(int failure) { _failure = failure; }
            public void EnsureDirectory(string directory) => Directory.CreateDirectory(directory);
            public void Append(string path, string line)
            {
                _append++;
                if (_append == _failure) throw new IOException("simulated append failure");
                File.AppendAllText(path, line + Environment.NewLine);
            }
        }
    }
}
