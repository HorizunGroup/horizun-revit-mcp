// -----------------------------------------------------------------------------
// Horizun Core tests - FIFO ownership of Revit's single UI thread.
// -----------------------------------------------------------------------------
using System;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class RequestGateTests
    {
        [Fact]
        public void Later_requests_wait_in_fifo_order_instead_of_being_refused()
        {
            var gate = new RequestGate();
            string refusal;
            var a = gate.Begin("a", "{}", out refusal);
            Assert.Same(a, gate.Take());

            var b = gate.Begin("b", "{}", out refusal);
            var c = gate.Begin("c", "{}", out refusal);

            Assert.NotNull(b);
            Assert.NotNull(c);
            Assert.Null(refusal);
            Assert.Equal(1, b.AheadAtAdmission);
            Assert.Equal(2, c.AheadAtAdmission);

            gate.Complete(a);
            Assert.Same(b, gate.Take());
            gate.Complete(b);
            Assert.Same(c, gate.Take());
        }

        [Fact]
        public void Take_consumes_one_entry_so_duplicate_callbacks_do_not_run_it_twice()
        {
            var gate = new RequestGate();
            string refusal;
            var a = gate.Begin("a", "{}", out refusal);

            Assert.Same(a, gate.Take());
            Assert.Null(gate.Take());
            gate.Complete(a);
            Assert.Null(gate.Take());
        }

        [Fact]
        public void Abandoning_a_waiting_request_removes_only_that_request()
        {
            var gate = new RequestGate();
            string refusal;
            var a = gate.Begin("a", "{}", out refusal);
            var b = gate.Begin("b", "{}", out refusal);
            var c = gate.Begin("c", "{}", out refusal);

            gate.Abandon(b);

            Assert.Same(a, gate.Take());
            gate.Complete(a);
            Assert.Same(c, gate.Take());
            Assert.False(b.Started);
            Assert.True(b.CancelledBeforeStart);
        }

        [Fact]
        public void Wire_cancellation_wakes_a_queued_owner_and_proves_it_never_started()
        {
            var gate = new RequestGate();
            string refusal, detail;
            var running = gate.Begin("wire-a", "a", "{}", out refusal);
            gate.Take();
            var waiting = gate.Begin("wire-b", "b", "{}", out refusal);

            Assert.True(gate.CancelQueued("wire-b", out detail));
            Assert.Equal("cancelled_before_start", detail);
            Assert.True(waiting.Wait(0));
            Assert.False(waiting.Started);
            Assert.True(waiting.CancelledBeforeStart);
            Assert.False(waiting.Result.Success);
            Assert.Contains("NEVER STARTED", waiting.Result.Error);

            gate.Complete(running);
            Assert.Null(gate.Take());
        }

        [Fact]
        public void Cancellation_cannot_claim_that_running_work_was_stopped()
        {
            var gate = new RequestGate();
            string refusal, detail;
            var running = gate.Begin("wire-a", "a", "{}", out refusal);
            gate.Take();

            Assert.False(gate.CancelQueued("wire-a", out detail));
            Assert.Equal("already_running", detail);
            Assert.True(running.Started);
            Assert.False(running.CancelledBeforeStart);
        }

        [Fact]
        public void The_queue_is_bounded_and_refusal_changes_nothing()
        {
            var gate = new RequestGate(2);
            string refusal;
            Assert.NotNull(gate.Begin("a", "{}", out refusal));
            Assert.NotNull(gate.Begin("b", "{}", out refusal));

            Assert.Null(gate.Begin("c", "{}", out refusal));
            Assert.Contains("queue is full", refusal);
            Assert.Contains("Nothing was queued", refusal);
            Assert.Equal(2, gate.PendingCount);
        }

        [Fact]
        public void A_caller_is_woken_only_by_its_own_completion()
        {
            var gate = new RequestGate();
            string refusal;
            var a = gate.Begin("a", "{}", out refusal);
            var b = gate.Begin("b", "{}", out refusal);
            gate.Take();

            a.Result = CommandResult.Ok("A");
            gate.Complete(a);

            Assert.True(a.Wait(0));
            Assert.False(b.Wait(0));
            Assert.Null(b.Result);
            Assert.Same(b, gate.Take());
        }

        [Fact]
        public void An_abandoned_running_request_still_owns_the_thread_until_complete()
        {
            var gate = new RequestGate();
            string refusal;
            var a = gate.Begin("a", "{}", out refusal);
            gate.Take();
            gate.Abandon(a);
            var b = gate.Begin("b", "{}", out refusal);

            Assert.NotNull(b);
            Assert.Contains("caller stopped waiting", gate.BusyWith());
            Assert.Null(gate.Take());

            gate.Complete(a);
            Assert.Same(b, gate.Take());
        }

        [Fact]
        public void Shutdown_fails_and_wakes_every_waiting_request_but_not_running_work()
        {
            var gate = new RequestGate();
            string refusal;
            var running = gate.Begin("a", "{}", out refusal);
            gate.Take();
            var b = gate.Begin("b", "{}", out refusal);
            var c = gate.Begin("c", "{}", out refusal);

            Assert.Equal(2, gate.FailQueued("Revit shut down; NEVER RAN."));
            Assert.True(b.Wait(0));
            Assert.True(c.Wait(0));
            Assert.Contains("NEVER RAN", b.Result.Error);
            Assert.False(running.Wait(0));
        }

        [Fact]
        public void Tickets_remain_unique_across_queued_and_completed_work()
        {
            var gate = new RequestGate();
            string refusal;
            var a = gate.Begin("a", "{}", out refusal);
            var b = gate.Begin("b", "{}", out refusal);
            gate.Take();
            gate.Complete(a);
            gate.Take();
            gate.Complete(b);
            var c = gate.Begin("c", "{}", out refusal);

            Assert.NotEqual(a.Ticket, b.Ticket);
            Assert.NotEqual(b.Ticket, c.Ticket);
        }

        [Fact]
        public void Idle_state_is_empty_and_has_no_busy_description()
        {
            var gate = new RequestGate();
            Assert.False(gate.HasPending);
            Assert.Equal(0, gate.PendingCount);
            Assert.Null(gate.BusyWith());
        }
    }
}
