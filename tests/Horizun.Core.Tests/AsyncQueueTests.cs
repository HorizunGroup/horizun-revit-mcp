using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    /// <summary>
    /// AT MOST ONCE, which is the only reason run_async is safe to point at a
    /// mutation.
    ///
    /// The queue carries scripts that write to models. If an entry could be claimed
    /// twice — by a duplicate raise of the external event, by a retry after a
    /// failure, by two threads racing — the second run is a SECOND WRITE, not a
    /// recovery, and nothing downstream could tell the difference. The queue shipped
    /// with no test at all; these pin the property the design rests on.
    /// </summary>
    public class AsyncQueueTests
    {
        private static AsyncWork Work(string id) => new AsyncWork
        {
            JobId = id,
            Command = "horizun_execute_python",
            ParamsJson = "{}",
            QueuedUtc = DateTime.UtcNow
        };

        private static void Drain() { while (AsyncQueue.Take() != null) { } }

        [Fact]
        public void An_entry_is_claimed_exactly_once()
        {
            Drain();
            AsyncQueue.TryAdd(Work("only-one"), out _);

            AsyncWork first = AsyncQueue.Take();
            AsyncWork second = AsyncQueue.Take();

            Assert.NotNull(first);
            Assert.Equal("only-one", first.JobId);
            // The second raise of the external event finds nothing. This is what stops
            // a queued write running twice.
            Assert.Null(second);
        }

        [Fact]
        public void Order_is_preserved()
        {
            Drain();
            AsyncQueue.TryAdd(Work("a"), out _);
            AsyncQueue.TryAdd(Work("b"), out _);
            AsyncQueue.TryAdd(Work("c"), out _);

            Assert.Equal("a", AsyncQueue.Take().JobId);
            Assert.Equal("b", AsyncQueue.Take().JobId);
            Assert.Equal("c", AsyncQueue.Take().JobId);
            Assert.Null(AsyncQueue.Take());
        }

        // async/await rather than Task.WaitAll: xUnit1031 flags blocking on a task inside
        // a test because it can deadlock on a synchronization context. The race is
        // unchanged - all eight takers still start together on the same signal.
        [Fact]
        public async Task Concurrent_takers_never_get_the_same_entry()
        {
            Drain();
            // Was 200, which the queue now REFUSES - it is capped at MaxDepth, because
            // entries run one at a time on the UI thread and an unbounded queue is a
            // silent promise of hours of committed mutations. The test follows the cap
            // rather than the cap following the test: 32 entries and 8 threads is the
            // same race, and asserting the adds succeeded means a future change to the
            // cap cannot make this pass by queueing fewer than it counts.
            int n = AsyncQueue.MaxDepth;
            for (int i = 0; i < n; i++)
            {
                string refusal;
                Assert.True(AsyncQueue.TryAdd(Work("job-" + i), out refusal), refusal);
            }

            var claimed = new System.Collections.Concurrent.ConcurrentBag<string>();
            var start = new ManualResetEventSlim(false);

            var takers = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
            {
                start.Wait();
                AsyncWork w;
                while ((w = AsyncQueue.Take()) != null) claimed.Add(w.JobId);
            })).ToArray();

            start.Set();
            await Task.WhenAll(takers);

            // Every entry claimed once, none twice, none lost. Eight threads racing
            // for one mutation each is the shape a duplicate Raise() produces.
            Assert.Equal(n, claimed.Count);
            Assert.Equal(n, claimed.Distinct().Count());
        }

        [Fact]
        public void Adding_null_is_ignored_rather_than_queued()
        {
            Drain();
            AsyncQueue.TryAdd(null, out _);
            Assert.Equal(0, AsyncQueue.Count);
            Assert.Null(AsyncQueue.Take());
        }

        [Fact]
        public void Shutdown_drain_returns_what_never_ran_and_empties_the_queue()
        {
            Drain();
            AsyncQueue.TryAdd(Work("x"), out _);
            AsyncQueue.TryAdd(Work("y"), out _);

            List<AsyncWork> left = AsyncQueue.DrainForShutdown();

            // These never started. Saying so on the way down is the difference between
            // a job record that stops mid-flight and one known never to have begun.
            Assert.Equal(2, left.Count);
            Assert.Equal(0, AsyncQueue.Count);
            Assert.Null(AsyncQueue.Take());
        }
    }

    /// <summary>
    /// The job record, which for an async run is the ONLY place the answer exists —
    /// its caller got a job_id and went away.
    /// </summary>
    public class JobRecordTests
    {
        [Fact]
        public void The_result_is_written_before_the_finish_line()
        {
            Job job = Job.Start("horizun_execute_python");
            Assert.False(string.IsNullOrEmpty(job.Path), "the record should have a path");
            try
            {
                job.Write("halfway", 1, 2);
                job.Result("{\"ok\":true}");
                job.Finish("ok", null);

                string[] lines = File.ReadAllLines(job.Path).Where(l => l.Trim().Length > 0).ToArray();
                int resultAt = Array.FindIndex(lines, l => l.Contains("\"event\":\"result\""));
                int finishAt = Array.FindIndex(lines, l => l.Contains("\"event\":\"finish\""));

                Assert.True(resultAt >= 0, "the result must be recorded");
                Assert.True(finishAt >= 0, "the finish must be recorded");
                // A reader that sees the finish line can rely on the result already
                // being there. The other order would make "finished" arrive before the
                // answer it announces.
                Assert.True(resultAt < finishAt,
                    "the result must be written BEFORE the finish line, not after it");
            }
            finally { try { File.Delete(job.Path); } catch { } }
        }

        [Fact]
        public void A_job_with_no_finish_line_stays_open()
        {
            Job job = Job.Start("horizun_execute_python");
            try
            {
                job.Write("started", null, null);
                string text = File.ReadAllText(job.Path);

                // No finish event. horizun_job_status reports that as an ambiguity -
                // still running, or the process died - and refuses to resolve it. A
                // record that closed itself on the way out would erase that distinction.
                Assert.DoesNotContain("\"event\":\"finish\"", text);
                Assert.Contains("\"event\":\"checkpoint\"", text);
            }
            finally { try { File.Delete(job.Path); } catch { } }
        }

        [Fact]
        public void Checkpoints_are_counted_and_numbered()
        {
            Job job = Job.Start("horizun_execute_python");
            try
            {
                for (int i = 0; i < 15; i++) job.Write("step", i + 1, 15);
                Assert.Equal(15, job.Checkpoints);

                string text = File.ReadAllText(job.Path);
                Assert.Contains("\"n\":15", text);
            }
            finally { try { File.Delete(job.Path); } catch { } }
        }
    }
}
