// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// The three ways a caller could be handed somebody else's work.
//
// These are not hypotheticals: every one of them was reachable in the dispatcher
// before RequestGate existed, and all three were silent - the reply carried the
// asking caller's own request id, so nothing downstream could tell. They are
// only reachable AFTER a timeout, which is why they survived every live test
// against a small model and would have surfaced first on a real one.
//
// Revit is not involved and cannot be: that is the point of keeping the
// sequencing in a file with no `using Autodesk.*`.
// -----------------------------------------------------------------------------
using System;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class RequestGateTests
    {
        [Fact]
        public void Take_consumes_the_request_so_a_duplicate_raise_runs_nothing()
        {
            // DOUBLE EXECUTION. Revit's ExternalEvent can fire again after the request it
            // was raised for has already been picked up. If the second firing found the
            // same request still sitting there, the command would run twice - for a write,
            // the same edit applied twice.
            var gate = new RequestGate();
            string refusal;
            RequestGate.Request a = gate.Begin("horizun_write_params_verified", "{}", out refusal);

            Assert.NotNull(a);
            Assert.Null(refusal);
            Assert.Same(a, gate.Take());
            Assert.Null(gate.Take());          // the duplicate firing finds nothing to do
        }

        [Fact]
        public void A_request_abandoned_before_it_starts_never_runs()
        {
            // ZOMBIE START. The caller timed out while Revit was stuck on a modal, so the
            // event had not fired yet. Minutes later it fires - and must find nothing,
            // rather than run a write against a model the user has moved on from.
            var gate = new RequestGate();
            string refusal;
            RequestGate.Request a = gate.Begin("horizun_delete_verified", "{}", out refusal);

            gate.Abandon(a);

            Assert.Null(gate.Take());
            Assert.False(a.Started);
        }

        [Fact]
        public void A_caller_is_never_woken_by_another_requests_completion()
        {
            // STALE WAKE - the worst of the three. A times out; B starts; A finishes and
            // signals "done"; B returns A's result as its own. Each request now owns its
            // completion signal, so B cannot be woken by A finishing.
            var gate = new RequestGate();
            string refusal;

            RequestGate.Request a = gate.Begin("horizun_model_scan", "{}", out refusal);
            gate.Take();                       // Revit picked it up
            gate.Abandon(a);                   // ...and the caller gave up waiting

            // While A still holds the UI thread, B cannot even start.
            Assert.Null(gate.Begin("get_document_info", "{}", out refusal));

            a.Result = CommandResult.Ok("A's answer");
            gate.Complete(a);                  // A finally returns

            RequestGate.Request b = gate.Begin("get_document_info", "{}", out refusal);
            Assert.NotNull(b);

            Assert.False(b.Wait(0));           // A's completion did NOT wake B
            Assert.Null(b.Result);             // and B carries no result but its own
        }

        [Fact]
        public void While_a_command_holds_the_thread_new_work_is_refused_not_queued()
        {
            var gate = new RequestGate();
            string refusal;

            RequestGate.Request a = gate.Begin("horizun_model_scan", "{}", out refusal);
            gate.Take();

            RequestGate.Request b = gate.Begin("horizun_health", "{}", out refusal);

            Assert.Null(b);
            Assert.Contains("horizun_model_scan", refusal);
            Assert.Contains("one request at a time", refusal.ToLowerInvariant());
        }

        [Fact]
        public void The_refusal_explains_an_abandoned_run_rather_than_repeating_timed_out()
        {
            // The second caller's failure has a different cause from the first's, and
            // saying "timed out" twice hides it: the thread is held by work that cannot
            // be cancelled, and there is something useful to do about it.
            var gate = new RequestGate();
            string refusal;

            RequestGate.Request a = gate.Begin("horizun_family_apply", "{}", out refusal);
            gate.Take();
            gate.Abandon(a);

            Assert.Null(gate.Begin("horizun_health", "{}", out refusal));
            Assert.Contains("horizun_family_apply", refusal);
            Assert.Contains("gave up waiting", refusal);
            Assert.Contains("horizun_job_status", refusal);      // what to do instead
        }

        [Fact]
        public void A_request_Revit_has_not_picked_up_is_described_as_not_started()
        {
            var gate = new RequestGate();
            string refusal;

            gate.Begin("horizun_quantities", "{}", out refusal);   // never Taken

            Assert.Null(gate.Begin("horizun_health", "{}", out refusal));
            Assert.Contains("has not started it yet", refusal);
            Assert.Contains("modal", refusal);
        }

        [Fact]
        public void Completing_frees_the_thread_for_the_next_caller()
        {
            var gate = new RequestGate();
            string refusal;

            RequestGate.Request a = gate.Begin("horizun_audit_model", "{}", out refusal);
            gate.Take();
            Assert.NotNull(gate.BusyWith());

            a.Result = CommandResult.Ok("done");
            gate.Complete(a);

            Assert.Null(gate.BusyWith());
            RequestGate.Request b = gate.Begin("horizun_health", "{}", out refusal);
            Assert.NotNull(b);
            Assert.Null(refusal);
        }

        [Fact]
        public void An_owner_that_waits_gets_its_own_result_and_only_after_Complete()
        {
            var gate = new RequestGate();
            string refusal;

            RequestGate.Request a = gate.Begin("get_document_info", "{}", out refusal);
            RequestGate.Request taken = gate.Take();

            Assert.False(a.Wait(0));           // nothing signalled yet

            taken.Result = CommandResult.Ok("the document");
            gate.Complete(taken);

            Assert.True(a.Wait(0));
            Assert.Same(taken, a);
            Assert.True(a.Result.Success);
        }

        [Fact]
        public void Tickets_are_distinct_so_two_requests_are_never_the_same_request()
        {
            var gate = new RequestGate();
            string refusal;

            RequestGate.Request a = gate.Begin("horizun_health", "{}", out refusal);
            gate.Take();
            gate.Complete(a);
            RequestGate.Request b = gate.Begin("horizun_health", "{}", out refusal);

            Assert.NotEqual(a.Ticket, b.Ticket);
            Assert.NotSame(a, b);
        }

        [Fact]
        public void An_idle_gate_says_nothing_is_running()
        {
            Assert.Null(new RequestGate().BusyWith());
        }
    }
}
