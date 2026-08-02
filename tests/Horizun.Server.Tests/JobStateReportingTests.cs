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
        public void An_unrecognised_final_status_is_reported_verbatim()
        {
            Job j = Job.Start("horizun_execute_python");
            j.Finish("something_new", "a status this reader predates");

            JObject reported = Read(j);

            // Mapping it onto a known state would be inventing the one fact the caller
            // wants. It is passed through and flagged as unrecognised instead.
            Assert.Equal("something_new", (string)reported["state"]);
            Assert.Contains("does not " + "recognise", (string)reported["what_this_means"]);
        }
    }
}
