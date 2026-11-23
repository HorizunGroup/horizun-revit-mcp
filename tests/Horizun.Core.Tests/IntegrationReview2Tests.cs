using System;
using System.Threading;
using System.Threading.Tasks;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    /// <summary>
    /// Second review of int/4.2: m-2 (the claim of a script stop key lives in the dequeue), m-3 (a script that is a step of
    /// a plan is reached by the wire cancel) and the Revit half of m-6 (a synchronous script that already ended).
    /// </summary>
    [Collection("AsyncQueue")]
    public class IntegrationReview2Tests
    {
        private static void Drain() { while (AsyncQueue.Take() != null) { } }

        private static AsyncWork Script(string id) => new AsyncWork
        {
            JobId = id, Command = "horizun_execute_python", ParamsJson = "{}", QueuedUtc = DateTime.UtcNow
        };

        // ------------------------------------------------------------------ m-2

        [Fact]
        public void The_stop_key_is_claimed_by_the_dequeue_itself()
        {
            Drain();
            Assert.True(AsyncQueue.TryAdd(Script("job-m2-a"), out _));
            Assert.Equal("not_running", PythonRunRegistry.Cancel("job-m2-a", "while queued"));   // nothing to flip yet

            AsyncWork taken = AsyncQueue.Take();
            Assert.Equal("job-m2-a", taken.JobId);
            // No dispatcher code ran after Take: the claim must already be there.
            Assert.False(AsyncQueue.TryRemove("job-m2-a", out _));
            Assert.Equal("interrupt_requested", PythonRunRegistry.Cancel("job-m2-a", "between take and register"));

            var control = new PythonRunControl(null);
            PythonRunRegistry.Register("job-m2-a", control);
            Assert.Equal(PythonRunControl.Cancelled, control.Poll());
            PythonRunRegistry.Unregister("job-m2-a");
        }

        [Fact]
        public async Task A_cancel_racing_the_dequeue_is_always_either_a_removal_or_a_kept_request()
        {
            // The cancel path of the add-in: remove the entry if it still waits, otherwise ask the claimed script.
            for (int i = 0; i < 400; i++)
            {
                Drain();
                string id = "job-m2-race-" + i;
                Assert.True(AsyncQueue.TryAdd(Script(id), out _));
                using (var go = new Barrier(2))
                {
                    AsyncWork taken = null;
                    bool removed = false;
                    string asked = null;
                    Task taker = Task.Run(() => { go.SignalAndWait(); taken = AsyncQueue.Take(); });
                    Task canceller = Task.Run(() =>
                    {
                        go.SignalAndWait();
                        removed = AsyncQueue.TryRemove(id, out _);
                        if (!removed) asked = PythonRunRegistry.Cancel(id, "race");
                    });
                    await Task.WhenAll(taker, canceller);
                    if (removed)
                        Assert.Null(taken);
                    else
                    {
                        Assert.NotNull(taken);
                        // The window the review found: dequeued, not removed, and NOT yet claimed -> "not_running".
                        Assert.Equal("interrupt_requested", asked);
                    }
                }
                PythonRunRegistry.Unregister(id);
            }
            Drain();
        }

        [Fact]
        public void Only_scripts_are_claimed_by_the_dequeue()
        {
            Drain();
            AsyncQueue.TryAdd(new AsyncWork { JobId = "job-m2-other", Command = "horizun_list_elements", ParamsJson = "{}", QueuedUtc = DateTime.UtcNow }, out _);
            AsyncQueue.Take();
            Assert.Equal("not_running", PythonRunRegistry.Cancel("job-m2-other", "x"));
        }

        // ------------------------------------------------------------------ m-3

        [Fact]
        public void A_wire_cancel_reaches_the_script_that_is_a_step_of_a_plan()
        {
            var gate = new RequestGate();
            var request = gate.Begin("w-plan", "horizun_execute_plan", "{}", false, out _);
            Assert.Same(request, gate.Take());

            var child = new PythonRunControl(null);
            PythonRunRegistry.Register(PythonRunRegistry.WireKey("w-plan"), child);   // what ExecutePythonCommand does for a step
            Assert.False(gate.CancelQueued("w-plan", out string state));
            Assert.Equal("cancel_requested", state);
            Assert.Equal(PythonRunControl.Cancelled, child.Poll());
            gate.Complete(request);
        }

        [Fact]
        public void A_plan_with_no_script_running_is_reported_as_already_running_and_nothing_is_kept()
        {
            var gate = new RequestGate();
            var request = gate.Begin("w-plan-idle", "horizun_execute_plan", "{}", false, out _);
            gate.Take();
            Assert.False(gate.CancelQueued("w-plan-idle", out string state));
            Assert.Equal("already_running", state);

            // A step that starts afterwards is NOT born cancelled: no request was kept for it.
            var later = new PythonRunControl(null);
            PythonRunRegistry.Register(PythonRunRegistry.WireKey("w-plan-idle"), later);
            Assert.Null(later.Poll());
            gate.Complete(request);
        }

        [Fact]
        public void The_second_script_of_a_plan_registers_after_the_first_one_finished()
        {
            var gate = new RequestGate();
            var request = gate.Begin("w-plan-2", "horizun_execute_plan", "{}", false, out _);
            gate.Take();
            string key = PythonRunRegistry.WireKey("w-plan-2");
            PythonRunRegistry.Register(key, new PythonRunControl(null));
            PythonRunRegistry.Finish(key);
            Assert.False(gate.CancelQueued("w-plan-2", out string between));
            Assert.Equal("already_running", between);

            var second = new PythonRunControl(null);
            PythonRunRegistry.Register(key, second);
            Assert.False(gate.CancelQueued("w-plan-2", out string during));
            Assert.Equal("cancel_requested", during);
            Assert.Equal(PythonRunControl.Cancelled, second.Poll());
            gate.Complete(request);
        }

        // ------------------------------------------------------------------ m-6

        [Fact]
        public void A_synchronous_script_that_already_ended_is_not_reported_as_cancel_requested()
        {
            var gate = new RequestGate();
            var request = gate.Begin("w-ended", "horizun_execute_python", "{}", false, out _);
            gate.Take();
            string key = PythonRunRegistry.WireKey("w-ended");
            PythonRunRegistry.Register(key, new PythonRunControl(null));
            PythonRunRegistry.Finish(key);   // the finally of ExecutePythonCommand; the reply is still being built

            Assert.False(gate.CancelQueued("w-ended", out string state));
            Assert.Equal("not_found_or_finished", state);
            gate.Complete(request);
            Assert.Equal("not_running", PythonRunRegistry.Cancel(key, "late"));
        }
    }
}
