// -----------------------------------------------------------------------------
// Horizun Core tests - a synchronous call whose caller gave up still answers.
//
// Field 2026-10-27: an edit_type_members rehearsal was cut at 600 s, kept running ~11
// minutes in Revit and ended in "result discarded (it had failed)" - the failure cause
// lost, and nothing to poll meanwhile. CallProgress keeps a sync call's checkpoints in
// memory, gives the call ONE record when the caller times out (the command's own if it
// opened one), and closes it with the call's result in one transition.
//
// The concurrency tests pin the two review findings: a command opening its record while
// the timeout opens one must end with ONE record for both; and the completion racing the
// timeout must never hand out an id whose record lacks the payload or has a checkpoint
// after its finish line.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public sealed class CallProgressTests
    {
        private sealed class MemorySink : IJobSink
        {
            public readonly List<string> Lines = new List<string>();
            public void EnsureDirectory(string directory) { }
            public void Append(string path, string line) { lock (Lines) Lines.Add(line); }
            public List<JObject> Events() { lock (Lines) return Lines.Select(JObject.Parse).ToList(); }
        }

        private static Job Open(MemorySink sink, string tool = "t") => Job.Start(tool, sink);

        [Fact]
        public void Checkpoints_before_the_timeout_are_replayed_with_their_own_times()
        {
            var p = new CallProgress();
            var t0 = new DateTime(2026, 10, 27, 10, 0, 0);
            p.Write("capture", 1, 5, t0);
            p.Write("instance 1/29", 1, 29, t0.AddMinutes(3));
            var sink = new MemorySink();
            Job record = p.OpenForTimeout(() => Open(sink), "caller timed out", out bool done);
            Assert.False(done);
            Assert.True(p.Abandoned);
            p.Write("instance 2/29", 2, 29);

            var cps = sink.Events().Where(e => (string)e["event"] == "checkpoint").ToList();
            Assert.Equal(4, cps.Count);
            Assert.Equal("capture", (string)cps[0]["label"]);
            Assert.Equal("2026-10-27 10:00:00.000", (string)cps[0]["at"]);
            Assert.Equal("2026-10-27 10:03:00.000", (string)cps[1]["at"]);
            Assert.Equal("caller timed out", (string)cps[2]["label"]);
            Assert.Equal(4, record.Checkpoints);
            Assert.Equal("instance 2/29", p.LastLabel);
        }

        [Fact]
        public void A_caller_that_gave_up_gets_the_full_result_and_nothing_after_it()
        {
            var p = new CallProgress();
            var sink = new MemorySink();
            p.OpenForTimeout(() => Open(sink), "caller timed out", out _);
            p.Complete((r, gaveUp) =>
            {
                Assert.True(gaveUp);
                r.Result(null, null, null, null, "{\"why\":\"Revit refused the commit\"}");
                r.Finish("failed", "commit refused");
            });
            p.Write("too late", null, null);
            p.Complete((r, g) => r.Finish("ok", null));
            var ev = sink.Events();
            Assert.Equal("finish", (string)ev.Last()["event"]);
            Assert.Single(ev, e => (string)e["event"] == "finish");
            Assert.Equal("Revit refused the commit", (string)ev.Single(e => (string)e["event"] == "result")["detail"]["why"]);
        }

        [Fact]
        public void A_timeout_after_the_completion_is_told_the_call_is_done()
        {
            var p = new CallProgress();
            int writes = 0;
            p.Complete((r, g) => writes++);
            Job late = p.OpenForTimeout(() => throw new InvalidOperationException("must not open"), "x", out bool done);
            Assert.True(done);
            Assert.Null(late);
            Assert.Equal(0, writes);
        }

        [Fact]
        public void A_command_record_is_the_one_the_timeout_hands_out()
        {
            var p = new CallProgress();
            var sink = new MemorySink();
            Job own = p.OpenForCommand(() => Open(sink, "horizun_execute_python"));
            Assert.True(p.AdoptedFromCommand);
            own.Write("script: model 3 of 12", 3, 12);
            Job late = p.OpenForTimeout(() => throw new InvalidOperationException("must not open a second record"), "caller timed out", out _);
            Assert.Same(own, late);
            Assert.Same(own, p.OpenForCommand(() => throw new InvalidOperationException("no")));
            p.Complete((r, gaveUp) => { Assert.True(gaveUp); r.Result("{}"); r.Finish("ok", null); });
            Assert.Equal(new[] { "start", "checkpoint", "checkpoint", "result", "finish" }, sink.Events().Select(e => (string)e["event"]));
        }

        [Fact]
        public void A_command_whose_caller_waited_gets_the_finish_line_only()
        {
            var p = new CallProgress();
            var sink = new MemorySink();
            p.OpenForCommand(() => Open(sink));
            p.Complete((r, gaveUp) => { Assert.False(gaveUp); r.Finish("ok", null); });
            Assert.DoesNotContain(sink.Events(), e => (string)e["event"] == "result");
        }

        [Fact]
        public void The_memory_buffer_is_bounded_and_says_what_it_dropped()
        {
            var p = new CallProgress();
            for (int i = 0; i < CallProgress.MaxBuffered + 7; i++) p.Write("step " + i, i, null);
            Assert.Equal(7, p.Dropped);
            var sink = new MemorySink();
            p.OpenForTimeout(() => Open(sink), null, out _);
            var cps = sink.Events().Where(e => (string)e["event"] == "checkpoint").ToList();
            Assert.Equal(CallProgress.MaxBuffered + 1, cps.Count);
            Assert.Contains("7 earlier checkpoint", (string)cps[0]["label"]);
            Assert.Equal("step 7", (string)cps[1]["label"]);
        }

        [Fact]
        public void The_static_entry_prefers_the_async_record_and_never_throws()
        {
            var sink = new MemorySink();
            Job ambient = Open(sink);
            var p = new CallProgress();
            try
            {
                CallProgress.Current = p;
                Job.Ambient = ambient;
                CallProgress.Checkpoint("to the job", 1, 2);
                Job.Ambient = null;
                CallProgress.Checkpoint("to memory");
                CallProgress.Current = null;
                CallProgress.Checkpoint("to nowhere");
            }
            finally { Job.Ambient = null; CallProgress.Current = null; }
            Assert.Equal(1, ambient.Checkpoints);
            Assert.Equal(1, p.Count);
        }

        [Fact]
        public void A_job_appends_nothing_after_its_finish_line()
        {
            var sink = new MemorySink();
            Job j = Open(sink);
            j.Finish("ok", null);
            j.Write("late", null, null);
            j.Result("{}");
            j.Finish("failed", "second");
            Assert.Equal("finish", (string)sink.Events().Last()["event"]);
            Assert.Single(sink.Events(), e => (string)e["event"] == "finish");
        }

        // ---------------------------------------------------------- concurrency
        [Fact]
        public async Task Command_and_timeout_racing_to_open_end_with_one_record()
        {
            for (int i = 0; i < 400; i++)
            {
                var p = new CallProgress();
                int opened = 0;
                Job a = null, b = null;
                using (var go = new Barrier(2))
                {
                    Task ta = Task.Run(() => { go.SignalAndWait(); a = p.OpenForCommand(() => { Interlocked.Increment(ref opened); Thread.SpinWait(200); return Open(new MemorySink()); }); });
                    Task tb = Task.Run(() => { go.SignalAndWait(); b = p.OpenForTimeout(() => { Interlocked.Increment(ref opened); Thread.SpinWait(200); return Open(new MemorySink()); }, "caller timed out", out _); });
                    await Task.WhenAll(ta, tb);
                }
                Assert.Equal(1, opened);
                Assert.Same(a, b);
                Assert.Same(a, p.Record);
            }
        }

        [Fact]
        public async Task Completion_racing_the_timeout_never_hands_out_a_record_without_its_result()
        {
            for (int i = 0; i < 400; i++)
            {
                var p = new CallProgress();
                var sink = new MemorySink();
                p.OpenForCommand(() => Open(sink, "horizun_execute_python"));
                Job late = null; bool finishedFirst = false;
                using (var go = new Barrier(2))
                {
                    Task timeout = Task.Run(() => { go.SignalAndWait(); late = p.OpenForTimeout(() => Open(new MemorySink()), "caller timed out", out finishedFirst); });
                    Task complete = Task.Run(() =>
                    {
                        go.SignalAndWait();
                        p.Complete((r, gaveUp) =>
                        {
                            if (gaveUp) r.Result("{\"ok\":true}");
                            r.Finish("ok", null);
                        });
                    });
                    await Task.WhenAll(timeout, complete);
                }
                var ev = sink.Events();
                Assert.Equal("finish", (string)ev.Last()["event"]);                     // nothing after the finish line
                Assert.Single(ev, e => (string)e["event"] == "finish");
                if (late != null)
                {
                    Assert.False(finishedFirst);
                    Assert.Contains(ev, e => (string)e["event"] == "result");          // the id handed out carries the payload
                }
                else Assert.True(finishedFirst);                                        // else the dispatcher returns the real answer
            }
        }
    }
}
