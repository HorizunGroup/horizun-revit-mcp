// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// ACCEPTED, PENDING AND DENIED - none of which needed Revit to be closed.
//
// The acceptance report carried this line for blocker 4: "Denied needs a Revit
// that is shutting down; it cannot be produced on demand, so this is reasoned and
// compiled." That was true of the CODE SHAPE, not of the problem. Raise() returns
// one of three values and the decision made from it is ordinary logic; what made
// it untestable was that the logic sat inline in a method holding an
// ExternalEvent.
//
// Behind IWorkRaiser, "Revit is shutting down" is a two-line fake. These cover
// all three answers, plus a raiser that throws - because an answer this code
// cannot obtain is not evidence that a callback is coming.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    /// <summary>Revit, reduced to the one thing the pump asks it.</summary>
    internal sealed class FakeRaiser : IWorkRaiser
    {
        private readonly RaiseOutcome _answer;
        private readonly Exception _throw;
        public int Raises;

        public FakeRaiser(RaiseOutcome answer) { _answer = answer; }
        public FakeRaiser(Exception ex) { _throw = ex; }

        public RaiseOutcome Raise()
        {
            Raises++;
            if (_throw != null) throw _throw;
            return _answer;
        }
    }

    public class AsyncLifecycleTests : IDisposable
    {
        private readonly List<string> _paths = new List<string>();
        private readonly string _root;
        private readonly string _savedRoot;

        public AsyncLifecycleTests()
        {
            // Job records are real files - the state under test is what they SAY - so
            // they go under a temp root that is deleted afterwards.
            _savedRoot = Environment.GetEnvironmentVariable(HorizunPaths.RootOverrideVariable);
            _root = Path.Combine(Path.GetTempPath(), "hz-lifecycle-" + Guid.NewGuid().ToString("N"));
            Environment.SetEnvironmentVariable(HorizunPaths.RootOverrideVariable, _root);
            Drain();
        }

        public void Dispose()
        {
            Drain();
            Environment.SetEnvironmentVariable(HorizunPaths.RootOverrideVariable, _savedRoot);
            try { Directory.Delete(_root, true); } catch { }
        }

        private static void Drain() { while (AsyncQueue.Take() != null) { } }

        private AsyncWork QueueOne(string id)
        {
            Job record = Job.Start("horizun_execute_python");
            _paths.Add(record.Path);
            var w = new AsyncWork { JobId = id, Command = "horizun_execute_python", Record = record };
            string refusal;
            Assert.True(AsyncQueue.TryAdd(w, out refusal), refusal);
            return w;
        }

        private static string Text(AsyncWork w) => File.ReadAllText(w.Record.Path);

        // ---- the three answers ------------------------------------------------

        [Fact]
        public void Accepted_schedules_the_work_and_abandons_nothing()
        {
            AsyncWork w = QueueOne("job-1");
            var raiser = new FakeRaiser(RaiseOutcome.Accepted);

            PumpResult r = AsyncPump.Pump(raiser);

            Assert.True(r.Attempted);
            Assert.True(r.Scheduled);
            Assert.Equal(0, r.AbandonedJobs);
            Assert.Equal(1, raiser.Raises);
            // Still queued, waiting for the callback that IS coming.
            Assert.Equal(1, AsyncQueue.Count);
            Assert.DoesNotContain("\"event\":\"finish\"", Text(w));
        }

        [Fact]
        public void Pending_is_scheduled_too()
        {
            AsyncWork w = QueueOne("job-1");

            PumpResult r = AsyncPump.Pump(new FakeRaiser(RaiseOutcome.Pending));

            // Pending means an earlier raise is still queued - the callback is coming.
            // Treating it as a refusal would abandon work that is about to run.
            Assert.True(r.Scheduled);
            Assert.Equal(0, r.AbandonedJobs);
            Assert.Equal(1, AsyncQueue.Count);
            Assert.DoesNotContain("\"event\":\"finish\"", Text(w));
        }

        [Fact]
        public void Denied_closes_every_queued_job_as_not_started()
        {
            AsyncWork a = QueueOne("job-1");
            AsyncWork b = QueueOne("job-2");
            AsyncWork c = QueueOne("job-3");
            var warnings = new List<string>();

            PumpResult r = AsyncPump.Pump(new FakeRaiser(RaiseOutcome.Denied), warnings.Add);

            Assert.False(r.Scheduled);
            Assert.Equal(RaiseOutcome.Denied, r.Outcome);

            // ALL of them, not just the one at the head. Denied is not transient -
            // Revit is closing or the event is disposed - so no later raise rescues
            // the rest of the batch.
            Assert.Equal(3, r.AbandonedJobs);
            Assert.Equal(0, AsyncQueue.Count);

            foreach (AsyncWork w in new[] { a, b, c })
            {
                string text = Text(w);
                Assert.Contains("\"event\":\"finish\"", text);
                // not_started, NOT failed: a failure ran and did something.
                Assert.Contains("\"status\":\"not_started\"", text);
                Assert.DoesNotContain("\"status\":\"failed\"", text);
            }

            Assert.NotEmpty(warnings);
        }

        [Fact]
        public void An_unknown_answer_is_treated_exactly_like_denied()
        {
            AsyncWork w = QueueOne("job-1");

            PumpResult r = AsyncPump.Pump(new FakeRaiser(RaiseOutcome.Unknown));

            // An answer this code does not recognise is not evidence that a callback
            // is coming. Optimism here is a record that never closes.
            Assert.False(r.Scheduled);
            Assert.Equal(1, r.AbandonedJobs);
            Assert.Contains("\"status\":\"not_started\"", Text(w));
        }

        [Fact]
        public void A_raiser_that_throws_is_treated_exactly_like_denied()
        {
            AsyncWork w = QueueOne("job-1");
            var warnings = new List<string>();

            PumpResult r = AsyncPump.Pump(new FakeRaiser(new InvalidOperationException("event disposed")), warnings.Add);

            Assert.Equal(RaiseOutcome.Unknown, r.Outcome);
            Assert.Equal(1, r.AbandonedJobs);
            Assert.Contains("\"status\":\"not_started\"", Text(w));
            Assert.Contains(warnings, s => s.Contains("event disposed"));
        }

        [Fact]
        public void An_empty_queue_is_not_raised_for_at_all()
        {
            var raiser = new FakeRaiser(RaiseOutcome.Accepted);

            PumpResult r = AsyncPump.Pump(raiser);

            // Raising with nothing queued would wake the UI thread for no reason, and
            // on the Denied path would report an abandonment that did not happen.
            Assert.False(r.Attempted);
            Assert.Equal(0, raiser.Raises);
            Assert.Equal(0, r.AbandonedJobs);
        }

        [Fact]
        public void A_second_pump_after_denial_finds_nothing_left_to_abandon()
        {
            QueueOne("job-1");

            AsyncPump.Pump(new FakeRaiser(RaiseOutcome.Denied));
            PumpResult second = AsyncPump.Pump(new FakeRaiser(RaiseOutcome.Denied));

            // The drain is destructive, so a record cannot be closed twice - which
            // would put two finish events in one file and make the first one a lie.
            Assert.False(second.Attempted);
            Assert.Equal(0, second.AbandonedJobs);
        }

        // ---- shutdown ---------------------------------------------------------

        [Fact]
        public void Shutdown_closes_every_queued_job_as_not_started()
        {
            AsyncWork a = QueueOne("job-1");
            AsyncWork b = QueueOne("job-2");

            int closed = AsyncPump.DrainForShutdown();

            // DrainForShutdown existed and returned the list, and nothing called it.
            // The records were simply left open - reported afterwards as "running, or
            // the process died", when the truth was known exactly.
            Assert.Equal(2, closed);
            Assert.Equal(0, AsyncQueue.Count);
            foreach (AsyncWork w in new[] { a, b })
            {
                Assert.Contains("\"status\":\"not_started\"", Text(w));
                Assert.Contains("NEVER RAN", Text(w));
            }
        }

        [Fact]
        public void Shutdown_with_an_empty_queue_closes_nothing()
        {
            Assert.Equal(0, AsyncPump.DrainForShutdown());
        }

        [Fact]
        public void A_queued_entry_with_no_record_does_not_stop_the_others_being_closed()
        {
            string refusal;
            AsyncQueue.TryAdd(new AsyncWork { JobId = "no-record" }, out refusal);
            AsyncWork withRecord = QueueOne("job-2");

            int closed = AsyncPump.DrainForShutdown();

            // One entry that cannot be closed must not cost the rest their finish line.
            Assert.Equal(1, closed);
            Assert.Contains("\"status\":\"not_started\"", Text(withRecord));
        }

        // ---- the cap ----------------------------------------------------------

        [Fact]
        public void The_queue_refuses_past_its_limit_and_says_why()
        {
            for (int i = 0; i < AsyncQueue.MaxDepth; i++)
            {
                string ok;
                Assert.True(AsyncQueue.TryAdd(new AsyncWork { JobId = "j" + i }, out ok));
            }

            string refusal;
            bool added = AsyncQueue.TryAdd(new AsyncWork { JobId = "one-too-many" }, out refusal);

            Assert.False(added);
            Assert.Equal(AsyncQueue.MaxDepth, AsyncQueue.Count);
            Assert.False(string.IsNullOrEmpty(refusal));
            // The refusal has to say what to do instead, or a caller in a retry loop
            // just tightens the loop.
            Assert.Contains("idempotency_key", refusal);
            Assert.Contains("Nothing was queued", refusal);
        }

        [Fact]
        public void Space_freed_by_a_finished_job_can_be_used_again()
        {
            for (int i = 0; i < AsyncQueue.MaxDepth; i++)
            {
                string ok;
                AsyncQueue.TryAdd(new AsyncWork { JobId = "j" + i }, out ok);
            }
            AsyncQueue.Take();

            string refusal;
            // The cap is a limit on OUTSTANDING work, not a lifetime quota.
            Assert.True(AsyncQueue.TryAdd(new AsyncWork { JobId = "next" }, out refusal), refusal);
        }

        [Fact]
        public void A_null_entry_is_refused_rather_than_queued()
        {
            string refusal;
            Assert.False(AsyncQueue.TryAdd(null, out refusal));
            Assert.Equal(0, AsyncQueue.Count);
        }
    }

    /// <summary>
    /// THE DEFECT WAS NOT IN THE FUNCTION. IT WAS THAT NOTHING CALLED IT.
    ///
    /// AsyncQueue.DrainForShutdown existed, was correct, had a test, and was dead
    /// code - so every job still queued when Revit closed kept an open record. No
    /// behavioural test could catch that, because the behaviour under test was never
    /// reached. These read the shipped source, the same technique as
    /// ConfirmationRoundTripTests, and they are the only kind of test that can fail
    /// on a wire that was never connected.
    /// </summary>
    public class AsyncLifecycleWiringTests
    {
        private static string RepoFile(params string[] parts)
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !Directory.Exists(Path.Combine(d.FullName, "src", "Horizun.Revit"))) d = d.Parent;
            Assert.True(d != null, "Could not locate src/Horizun.Revit from " + AppContext.BaseDirectory);
            var all = new List<string> { d.FullName, "src", "Horizun.Revit" };
            all.AddRange(parts);
            return Path.Combine(all.ToArray());
        }

        [Fact]
        public void Shutdown_actually_drains_the_queue()
        {
            string app = File.ReadAllText(RepoFile("App.cs"));

            int shutdown = app.IndexOf("OnShutdown", StringComparison.Ordinal);
            Assert.True(shutdown >= 0, "OnShutdown moved; this test needs updating");

            string body = app.Substring(shutdown);
            Assert.True(body.Contains("AsyncPump.DrainForShutdown"),
                "OnShutdown must drain the async queue. The drain existed for weeks and nothing called it, so " +
                "every job queued when Revit closed kept an open record - reported afterwards as 'running, or " +
                "the process died' when it had provably never started.");
        }

        [Fact]
        public void No_raise_result_is_discarded()
        {
            string dispatcher = File.ReadAllText(RepoFile("Core", "Dispatcher.cs"));

            var discarded = new List<string>();
            string[] lines = dispatcher.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string code = lines[i].Trim();
                if (code.StartsWith("//")) continue;
                // A raise whose answer goes nowhere: `_event.Raise();` as a statement.
                // This is exactly what RunOneAsync did, and it stranded every
                // successive job in a batch without a word anywhere.
                if (code == "_event.Raise();" || code.EndsWith(" _event.Raise();"))
                    discarded.Add("line " + (i + 1) + ": " + code);
            }

            Assert.True(discarded.Count == 0,
                "Raise() ANSWERS, and Revit can refuse. A discarded answer means queued work that never runs and " +
                "records that never close. Route it through AsyncPump.Pump:\n  " + string.Join("\n  ", discarded));
        }

        [Fact]
        public void Both_completion_sites_pump_the_shared_sync_and_async_scheduler()
        {
            string dispatcher = File.ReadAllText(RepoFile("Core", "Dispatcher.cs"));

            // Two places finish work on the UI thread and may leave the queue non-empty:
            // the end of a caller's command, and the end of a queued job. Both have to
            // pump, or either a normal FIFO batch or a run_async batch stops after
            // its first entry. PumpNext arbitrates both queues and alternates them.
            int pumps = System.Text.RegularExpressions.Regex.Matches(dispatcher, @"PumpNext\(\);").Count;
            Assert.True(pumps >= 2,
                "expected the shared pump at both the command-completion and async-completion sites, found " + pumps);
            Assert.Contains("_gate.HasPending", dispatcher);
            Assert.Contains("AsyncQueue.Count", dispatcher);
        }
    }

    /// <summary>
    /// The five states a job record can be in, read the way the server reads them.
    ///
    /// This is asserted over the FILE, because the file is what horizun_job_status
    /// has - it answers without touching Revit, which is the entire reason the
    /// record exists.
    /// </summary>
    public class JobStateTests : IDisposable
    {
        private readonly string _root;
        private readonly string _savedRoot;

        public JobStateTests()
        {
            _savedRoot = Environment.GetEnvironmentVariable(HorizunPaths.RootOverrideVariable);
            _root = Path.Combine(Path.GetTempPath(), "hz-jobstate-" + Guid.NewGuid().ToString("N"));
            Environment.SetEnvironmentVariable(HorizunPaths.RootOverrideVariable, _root);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(HorizunPaths.RootOverrideVariable, _savedRoot);
            try { Directory.Delete(_root, true); } catch { }
        }

        private static List<string> Events(Job j) =>
            File.ReadAllLines(j.Path)
                .Where(l => l.Trim().Length > 0)
                .Select(l => Newtonsoft.Json.Linq.JObject.Parse(l))
                .Select(o => (string)o["event"])
                .ToList();

        [Fact]
        public void A_record_that_was_only_opened_carries_no_running_event()
        {
            Job j = Job.Start("horizun_execute_python");

            // QUEUED: opened, never picked up. The server reads the absence of
            // "running" as exactly that - and it used to be indistinguishable from a
            // job in flight.
            Assert.Equal(new[] { "start" }, Events(j));
        }

        [Fact]
        public void Marking_it_running_is_recorded_once_however_often_it_is_called()
        {
            Job j = Job.Start("horizun_execute_python");
            j.MarkRunning();
            j.MarkRunning();
            j.MarkRunning();

            // The sync path marks it when it opens the record; the async dispatcher
            // marks it when it takes the entry. Both call it, and a record claiming to
            // have started three times would be worse than one that never said so.
            Assert.Equal(new[] { "start", "running" }, Events(j));
        }

        [Fact]
        public void The_full_running_sequence_is_start_running_checkpoint_result_finish()
        {
            Job j = Job.Start("horizun_execute_python");
            j.MarkRunning();
            j.Write("halfway", 1, 2);
            j.Result("{\"ok\":true}");
            j.Finish("ok", null);

            Assert.Equal(new[] { "start", "running", "checkpoint", "result", "finish" }, Events(j));
        }

        [Fact]
        public void A_job_closed_as_not_started_never_carries_a_running_event()
        {
            Job j = Job.Start("horizun_execute_python");
            j.Finish("not_started", "Revit shut down before this ran.");

            // This is what makes not_started safe to act on: the record proves the
            // work was never picked up, so re-sending it cannot be a second write.
            List<string> events = Events(j);
            Assert.DoesNotContain("running", events);
            Assert.Contains("finish", events);
            Assert.Contains("\"status\":\"not_started\"", File.ReadAllText(j.Path));
        }
    }
}
