using System;
using System.IO;
using System.Linq;
using Horizun.Contracts;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Server.Tests
{
    /// <summary>The server half of the Python time limit and cancellation (4.2-d).</summary>
    public class PythonInterruptServerTests : IDisposable
    {
        private readonly string _root;
        private readonly string _savedRoot;

        public PythonInterruptServerTests()
        {
            _savedRoot = Environment.GetEnvironmentVariable(HorizunPaths.RootOverrideVariable);
            _root = Path.Combine(Path.GetTempPath(), "hz-pyint-" + Guid.NewGuid().ToString("N"));
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
            return (JObject)((JArray)reply["jobs"])[0];
        }

        [Theory]
        [InlineData("timed_out", "timeout_s")]
        [InlineData("cancelled_running", "cancellation")]
        public void An_interrupted_job_is_its_own_state_and_says_what_was_and_was_not_undone(string status, string word)
        {
            Job j = Job.Start("horizun_execute_python");
            j.MarkRunning();
            j.Finish(status, "stopped at a checkpoint");

            JObject reported = Read(j);

            Assert.Equal(status, (string)reported["state"]);
            Assert.True((bool)reported["finished"]);
            string means = (string)reported["what_this_means"];
            Assert.Contains(word, means);
            Assert.Contains("rolled back", means);
            Assert.Contains("already committed", means);
        }

        [Fact]
        public void The_execute_python_schema_offers_a_time_limit_with_honest_bounds()
        {
            JObject props = (JObject)Contract.All.First(c => c.Name == "horizun_execute_python").InputSchema["properties"];
            JObject t = (JObject)props["timeout_s"];
            Assert.Equal("number", (string)t["type"]);
            Assert.Equal(1, (double)t["minimum"]);
            Assert.Equal(86400, (double)t["maximum"]);
            Assert.Contains("checkpoint", (string)t["description"]);
        }

        [Fact]
        public void job_status_can_ask_for_a_cancellation_and_says_it_is_only_a_request()
        {
            JObject props = (JObject)Contract.All.First(c => c.Name == "horizun_job_status").InputSchema["properties"];
            JObject c = (JObject)props["cancel"];
            Assert.Equal("boolean", (string)c["type"]);
            Assert.Contains("job_id", (string)c["description"]);
        }

        [Fact]
        public void Cancelling_a_job_that_was_never_found_is_refused_not_guessed()
        {
            var ex = Assert.Throws<ArgumentException>(() => JobStatus.Handle(new JObject { ["cancel"] = true }));
            Assert.Contains("job_id", ex.Message);
        }

        [Fact]
        public void An_answer_that_interrupts_a_running_script_is_reported_as_a_request_not_a_stop()
        {
            Job j = Job.Start("horizun_execute_python");
            j.MarkRunning();
            JObject reply = JobStatus.Handle(new JObject { ["job_id"] = j.Id, ["cancel"] = true },
                System.Threading.CancellationToken.None, (id, pid) => new McpTasks.JobCancelAnswer { Removed = false, State = "interrupt_requested", InterruptRequested = true });
            JObject ask = (JObject)reply["cancel_request"];
            Assert.Equal("interrupt_requested", (string)ask["state"]);
            Assert.False((bool)ask["stopped"]);
            Assert.Contains("next checkpoint", (string)ask["what_this_means"]);
        }

        [Fact]
        public void A_running_job_the_script_stopped_becomes_a_cancelled_task_and_a_timeout_a_failed_one()
        {
            Assert.Equal("cancelled", McpTasks.TaskStatusFor("cancelled_running", out string m1));
            Assert.Contains("interrupted", m1);
            Assert.Equal("failed", McpTasks.TaskStatusFor("timed_out", out string m2));
            Assert.Contains("timeout_s", m2);
        }
    }
}
