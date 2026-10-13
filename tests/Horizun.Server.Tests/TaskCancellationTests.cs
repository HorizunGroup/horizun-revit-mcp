// -----------------------------------------------------------------------------
// Horizun Server tests - original Horizun code.
//
// Audit 2026-10-08: tasks/cancel recorded the request and its status message said
// queued work "is removed before it starts" - but nothing ever asked Revit to
// remove it, so a queued job ran anyway. Cancellation now asks the add-in to take
// the durable job out of its queue, and the task becomes `cancelled` ONLY when the
// add-in answers that it did; a job already running stays working and says so.
// -----------------------------------------------------------------------------
using System;
using System.IO;
using System.Threading;
using Horizun.Revit.Core;
using Horizun.Server;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Server.Tests
{
    public sealed class TaskCancellationTests
    {
        private static void WithDataRoot(Action action)
        {
            string saved = Environment.GetEnvironmentVariable(HorizunPaths.RootOverrideVariable);
            Func<int, bool> savedProbe = PipeClient.LivenessProbe;
            string temp = Path.Combine(Path.GetTempPath(), "hz-task-cancel-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(temp);
                Environment.SetEnvironmentVariable(HorizunPaths.RootOverrideVariable, temp);
                PipeClient.LivenessProbe = _ => true;
                action();
            }
            finally
            {
                PipeClient.LivenessProbe = savedProbe;
                Environment.SetEnvironmentVariable(HorizunPaths.RootOverrideVariable, saved);
                try { Directory.Delete(temp, true); } catch { }
            }
        }

        private static string NewTask(out Job job)
        {
            Job created = null;
            JObject reply = McpTasks.Create(new JObject
            {
                ["name"] = "horizun_model_scan",
                ["arguments"] = new JObject(),
                ["task"] = new JObject { ["ttl"] = 600000 }
            }, (request, token) =>
            {
                created = Job.Start("horizun_model_scan");
                return McpResult.Structured(new JObject { ["job_id"] = created.Id, ["status"] = "queued" }, "queued");
            }, CancellationToken.None);
            job = created;
            return (string)reply["task"]["taskId"];
        }

        [Fact]
        public void A_queued_job_removed_by_the_add_in_makes_the_task_cancelled()
        {
            WithDataRoot(() =>
            {
                string taskId = NewTask(out Job job);
                string asked = null;
                McpTasks.RequestCancellation(taskId, jobId =>
                {
                    asked = jobId;
                    job.Finish("cancelled", "Removed from the queue before it started, on request.");   // what the add-in does
                    return new McpTasks.JobCancelAnswer { Removed = true, State = "removed_before_start" };
                });

                Assert.Equal(job.Id, asked);
                JObject state = McpTasks.Get(new JObject { ["taskId"] = taskId });
                Assert.Equal("cancelled", (string)state["status"]);
            });
        }

        [Fact]
        public void A_running_job_is_never_reported_cancelled()
        {
            WithDataRoot(() =>
            {
                string taskId = NewTask(out Job job);
                job.MarkRunning();
                McpTasks.RequestCancellation(taskId,
                    jobId => new McpTasks.JobCancelAnswer { Removed = false, State = "not_in_queue" });

                JObject state = McpTasks.Get(new JObject { ["taskId"] = taskId });
                Assert.Equal("working", (string)state["status"]);
                JObject record = JObject.Parse(File.ReadAllText(Path.Combine(HorizunPaths.DataRoot(), "mcp-tasks", taskId + ".json")));
                Assert.NotNull(record["cancellation_requested_at"]);
                Assert.Contains("not_in_queue", (string)record["cancellation_answer"]);
            });
        }

        // ---- 2026-10-08 audit: a submission that MAY have reached Revit was declared failed ----
        // and its idempotency key thrown away, so the job it may have created could never be
        // found again - while it ran.

        [Fact]
        public void A_submission_that_may_have_reached_revit_stays_reconcilable()
        {
            WithDataRoot(() =>
            {
                JObject created = McpTasks.Create(new JObject
                {
                    ["name"] = "horizun_model_scan",
                    ["arguments"] = new JObject(),
                    ["task"] = new JObject { ["ttl"] = 600000 }
                }, (request, token) => new JObject
                {
                    ["isError"] = true,
                    ["content"] = new JArray(new JObject { ["type"] = "text", ["text"] = "Error talking to Revit: pipe closed before a reply" }),
                    ["structuredContent"] = new JObject
                    {
                        ["code"] = "revit_transport_failed",
                        ["request_may_have_been_submitted"] = true,
                        ["transaction_status"] = "unknown"
                    }
                }, CancellationToken.None);

                string taskId = (string)created["task"]["taskId"];
                Assert.Equal("working", (string)created["task"]["status"]);
                JObject sidecar = JObject.Parse(File.ReadAllText(Path.Combine(HorizunPaths.DataRoot(), "mcp-tasks", taskId + ".json")));
                Assert.NotNull(sidecar["submit_idempotency_key"]);          // kept, so the job can be found by replay
                Assert.Equal("working", (string)McpTasks.Get(new JObject { ["taskId"] = taskId })["status"]);

                // The reconciliation: the same key replays and names the job Revit did create.
                Job job = null;
                string replayedKey = null;
                McpTasks.WaitResult(new JObject { ["taskId"] = taskId }, (request, token) =>
                {
                    replayedKey = (string)request["arguments"]["idempotency_key"];
                    job = Job.Start("horizun_model_scan");
                    job.MarkRunning(); job.Result(@"{""complete"":true}"); job.Finish("ok", null);
                    return McpResult.Structured(new JObject { ["job_id"] = job.Id, ["status"] = "queued" }, "queued");
                }, CancellationToken.None);
                Assert.Equal((string)sidecar["submit_idempotency_key"], replayedKey);
                Assert.Equal("completed", (string)McpTasks.Get(new JObject { ["taskId"] = taskId })["status"]);
            });
        }

        [Fact]
        public void A_submission_proven_not_to_have_reached_revit_is_still_terminal()
        {
            WithDataRoot(() =>
            {
                JObject created = McpTasks.Create(new JObject
                {
                    ["name"] = "horizun_model_scan",
                    ["arguments"] = new JObject(),
                    ["task"] = new JObject { ["ttl"] = 600000 }
                }, (request, token) => new JObject
                {
                    ["isError"] = true,
                    ["content"] = new JArray(new JObject { ["type"] = "text", ["text"] = "Could not connect. Nothing was sent." }),
                    ["structuredContent"] = new JObject { ["request_may_have_been_submitted"] = false }
                }, CancellationToken.None);
                Assert.Equal("failed", (string)created["task"]["status"]);
            });
        }

        [Fact]
        public void Tasks_get_answers_promptly_while_another_call_holds_the_task()
        {
            // tasks/get is answered on the server's READER thread. Submit holds the task
            // mutex for its whole round trip to Revit, and Describe used to wait for it
            // without limit - stalling every other message (audit 2026-10-08).
            WithDataRoot(() =>
            {
                string taskId = NewTask(out Job job);
                var held = new ManualResetEventSlim();
                var release = new ManualResetEventSlim();
                var holder = new Thread(() =>
                {
                    using (var m = new Mutex(false, "Local\\Horizun.McpTask." + taskId))
                    {
                        m.WaitOne();
                        held.Set();
                        release.Wait();
                        m.ReleaseMutex();
                    }
                });
                holder.Start();
                held.Wait();
                try
                {
                    var clock = System.Diagnostics.Stopwatch.StartNew();
                    JObject state = McpTasks.Get(new JObject { ["taskId"] = taskId });
                    clock.Stop();
                    Assert.True(clock.ElapsedMilliseconds < 5000, clock.ElapsedMilliseconds + " ms");
                    Assert.Equal("working", (string)state["status"]);
                }
                finally { release.Set(); holder.Join(); }
            });
        }

        [Fact]
        public void Tasks_cancel_answers_promptly_while_a_submission_holds_the_task()
        {
            // Backlog #22: tasks/cancel is answered on the server's READER thread and took
            // the task mutex WITHOUT a bound, while Submit holds it for its whole round trip
            // to Revit (up to 600 s) - so nothing else was answered meanwhile. The cancel
            // now returns at once and the request is recorded, and the removal asked for,
            // as soon as the submission lets go of the task.
            WithDataRoot(() =>
            {
                string taskId = NewTask(out Job job);
                var held = new ManualResetEventSlim();
                var release = new ManualResetEventSlim();
                var holder = new Thread(() =>
                {
                    using (var m = new Mutex(false, "Local\\Horizun.McpTask." + taskId))
                    {
                        m.WaitOne();
                        held.Set();
                        release.Wait(TimeSpan.FromSeconds(30));
                        m.ReleaseMutex();
                    }
                });
                holder.Start();
                held.Wait();
                var asked = new ManualResetEventSlim();
                string askedFor = null;
                try
                {
                    var clock = System.Diagnostics.Stopwatch.StartNew();
                    McpTasks.RequestCancellation(taskId, jobId =>
                    {
                        askedFor = jobId;
                        asked.Set();
                        return new McpTasks.JobCancelAnswer { Removed = false, State = "not_in_queue" };
                    });
                    clock.Stop();
                    Assert.True(clock.ElapsedMilliseconds < 5000, "tasks/cancel blocked " + clock.ElapsedMilliseconds + " ms");
                }
                finally { release.Set(); holder.Join(); }

                // Deferred, not dropped: once the task is free the intent is durable and asked for.
                Assert.True(asked.Wait(TimeSpan.FromSeconds(15)), "the deferred cancellation never asked Revit");
                Assert.Equal(job.Id, askedFor);
                string path = Path.Combine(HorizunPaths.DataRoot(), "mcp-tasks", taskId + ".json");
                JObject record = null;
                var deadline = DateTime.UtcNow.AddSeconds(15);
                while (DateTime.UtcNow < deadline)
                {
                    record = JObject.Parse(File.ReadAllText(path));
                    if ((string)record["cancellation_answer"] == "not_in_queue") break;
                    Thread.Sleep(50);
                }
                Assert.NotNull(record["cancellation_requested_at"]);
                Assert.Equal("not_in_queue", (string)record["cancellation_answer"]);
            });
        }

        [Fact]
        public void Tasks_cancel_for_an_unknown_task_still_fails_on_the_call()
        {
            WithDataRoot(() =>
                Assert.ThrowsAny<Exception>(() =>
                    McpTasks.RequestCancellation(Guid.NewGuid().ToString("N"), jobId => null)));
        }

        [Fact]
        public void An_unanswered_cancel_claims_nothing()
        {
            WithDataRoot(() =>
            {
                string taskId = NewTask(out Job job);
                McpTasks.RequestCancellation(taskId, jobId => null);   // control unavailable (older add-in, Revit closed)
                JObject state = McpTasks.Get(new JObject { ["taskId"] = taskId });
                Assert.Equal("working", (string)state["status"]);
                JObject record = JObject.Parse(File.ReadAllText(Path.Combine(HorizunPaths.DataRoot(), "mcp-tasks", taskId + ".json")));
                Assert.Contains("could not confirm", (string)record["status_message"]);
            });
        }
    }
}
