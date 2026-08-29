// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// WHAT HAPPENS TO QUEUED WORK WHEN REVIT WILL NOT TAKE IT.
//
// ExternalEvent.Raise() ANSWERS. Dispatcher.Invoke handles that answer, because
// a caller is blocked on it and a refused raise means the caller waits the full
// timeout for a callback that is never coming. The two places that raise for the
// ASYNC QUEUE did not:
//
//   Execute's finally    logged a warning and carried on
//   RunOneAsync's finally discarded the answer entirely
//
// Nobody is blocked on those, which is exactly why they were easy to leave. The
// cost lands on the job record instead: the entry stays in an in-memory queue
// that will never be pumped again, and its record sits open forever. And an open
// record is not "pending" - horizun_job_status deliberately refuses to guess
// between "still running" and "the process died", so a job that was never even
// scheduled is reported as that same unresolvable ambiguity. The one state the
// system could have known for certain was the one it threw away.
//
// Denied CAN be transient. Measured on Revit 2026 during the v1.1.2 release:
// one command completed, the next sequential caller arrived 4 ms later, and
// Raise() answered Denied while the previous ExternalEvent callback was still
// unwinding. Revit stayed open and served every later command. Treating that one
// answer as shutdown dropped work that was perfectly schedulable.
//
// The caller therefore gives Denied a short, bounded retry window OFF the Revit
// UI thread. If every attempt is denied, or the answer is Unknown, the terminal
// path below still closes each record as not_started. Shutdown itself drains the
// queue directly and never depends on this inference.
//
// Revit-free on purpose. The narrow unwind race cannot be timed reliably in a
// test, but behind IWorkRaiser its measured answer sequence - Denied, then
// Accepted/Pending - is ordinary deterministic input, as is terminal Denied.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;

namespace Horizun.Revit.Core
{
    /// <summary>
    /// Revit's three answers to Raise(), plus the one this code needs for an answer
    /// it does not recognise. Mirrored rather than used directly so the decisions
    /// made from it can be tested without Revit in the room.
    /// </summary>
    public enum RaiseOutcome
    {
        /// <summary>Queued. The callback is coming.</summary>
        Accepted,

        /// <summary>Already queued from an earlier raise. The callback is still coming.</summary>
        Pending,

        /// <summary>Refused. NO CALLBACK IS COMING - Revit is closing, or the event is disposed.</summary>
        Denied,

        /// <summary>Something else, or the raise itself threw. Treated exactly like Denied.</summary>
        Unknown
    }

    /// <summary>The one thing the pump needs from Revit, and the only thing a test has to fake.</summary>
    public interface IWorkRaiser
    {
        RaiseOutcome Raise();
    }

    public sealed class RaiseAttemptResult
    {
        public RaiseOutcome Outcome;
        public int Attempts;
        public bool Scheduled => Outcome == RaiseOutcome.Accepted || Outcome == RaiseOutcome.Pending;
    }

    /// <summary>
    /// The bounded policy between a transient Denied and a terminal refusal.
    /// It is deliberately Revit-free: the production caller supplies Raise(),
    /// and tests supply both the answer sequence and a delay that does not sleep.
    /// Unknown is never retried because an exception or an unrecognised enum is
    /// not evidence that the same ExternalEvent remains usable.
    /// </summary>
    public static class RaiseRetryPolicy
    {
        public static readonly int[] DefaultDelaysMs = { 5, 10, 25, 50, 100, 200 };

        public static RaiseAttemptResult TrySchedule(
            IWorkRaiser raiser,
            Action<int> delay,
            IReadOnlyList<int> delaysMs = null,
            Action<string> warn = null)
        {
            if (raiser == null) throw new ArgumentNullException("raiser");
            if (delay == null) throw new ArgumentNullException("delay");
            IReadOnlyList<int> waits = delaysMs ?? DefaultDelaysMs;
            var result = new RaiseAttemptResult();

            for (int attempt = 0; ; attempt++)
            {
                result.Attempts++;
                try { result.Outcome = raiser.Raise(); }
                catch (Exception ex)
                {
                    result.Outcome = RaiseOutcome.Unknown;
                    warn?.Invoke("raising the external event threw: " + ex.Message);
                }

                if (result.Scheduled || result.Outcome != RaiseOutcome.Denied || attempt >= waits.Count)
                    return result;

                int waitMs = Math.Max(0, waits[attempt]);
                warn?.Invoke("ExternalEvent.Raise() returned Denied; retrying after " + waitMs +
                             " ms because a previous callback may still be unwinding.");
                delay(waitMs);
            }
        }
    }

    public sealed class PumpResult
    {
        /// <summary>False when the queue was empty - there was nothing to raise for.</summary>
        public bool Attempted;

        public RaiseOutcome Outcome;

        /// <summary>The callback is coming.</summary>
        public bool Scheduled => Attempted && (Outcome == RaiseOutcome.Accepted || Outcome == RaiseOutcome.Pending);

        /// <summary>Entries taken off the queue and closed as not_started because it is not.</summary>
        public int AbandonedJobs;

        /// <summary>A sentence for the log. Null when nothing went wrong.</summary>
        public string Note;
    }

    public static class AsyncPump
    {
        /// <summary>
        /// Ask Revit to come back for the queue, and deal with a no.
        ///
        /// Called from every place that finishes work on the UI thread, so a queue that
        /// gained an entry during a command gets its turn as soon as the reply is on
        /// its way - rather than waiting for whatever the caller happens to ask next.
        ///
        /// This method receives a TERMINAL answer: callers must first exhaust
        /// RaiseRetryPolicy off the UI thread. On terminal Denied or Unknown the queue
        /// is DRAINED and every record closed as not_started. Leaving them would be a
        /// queue nothing will ever pump again and records that never close.
        /// </summary>
        public static PumpResult Pump(IWorkRaiser raiser, Action<string> warn = null)
        {
            var result = new PumpResult();
            if (raiser == null) throw new ArgumentNullException("raiser");
            if (AsyncQueue.Count == 0) return result;

            result.Attempted = true;
            try { result.Outcome = raiser.Raise(); }
            catch (Exception ex)
            {
                result.Outcome = RaiseOutcome.Unknown;
                if (warn != null) warn("raising the external event threw: " + ex.Message);
            }

            if (result.Scheduled) return result;

            result.Note = "Revit kept answering Raise() with " + result.Outcome +
                          " after the bounded retry window, so no callback is coming and the " +
                          "queued work will never start. Revit is closing down, or the bridge's external event " +
                          "has been disposed.";
            result.AbandonedJobs = CloseEverythingWaiting(
                "Revit refused to schedule this job: Raise() returned " + result.Outcome + ". It NEVER STARTED - " +
                "nothing was executed and nothing was written. This is a known outcome, not a job that stopped " +
                "mid-flight: repeated Denied means Revit is shutting down or the bridge's external event was disposed. " +
                "Restart Revit and send the request again - with a NEW idempotency_key, because this one is bound " +
                "to a process that is going away.",
                warn);

            if (warn != null)
                warn(result.Note + " " + result.AbandonedJobs + " queued job(s) closed as not_started.");

            return result;
        }

        /// <summary>
        /// Revit is closing. Everything still waiting never ran, and saying so is the
        /// difference between a record known never to have begun and one that stops
        /// without a finish line.
        ///
        /// DrainForShutdown existed and returned the list. Nothing called it - the
        /// records were left open, which is the one case where the honest answer was
        /// available and discarded.
        /// </summary>
        public static int DrainForShutdown(Action<string> warn = null)
        {
            return CloseEverythingWaiting(
                "Revit shut down before this job started. It NEVER RAN - nothing was executed and nothing was " +
                "written. The queue is in memory and is not persisted, deliberately: nothing replays a mutation " +
                "whose outcome nobody knows. Send the request again in the new session, with a NEW " +
                "idempotency_key - keys are bound to the Revit process that issued them.",
                warn);
        }

        /// <summary>
        /// The shared dispatcher already attempted the ExternalEvent raise and received
        /// a terminal refusal. Close async records with the same reason used to wake
        /// synchronous queued callers, without raising a second time.
        /// </summary>
        public static int FailEverythingWaiting(string reason, Action<string> warn = null)
        {
            if (string.IsNullOrWhiteSpace(reason))
                reason = "Queued work could not be scheduled. It NEVER STARTED.";
            return CloseEverythingWaiting(reason, warn);
        }

        private static int CloseEverythingWaiting(string reason, Action<string> warn)
        {
            List<AsyncWork> left = AsyncQueue.DrainForShutdown();
            int closed = 0;

            foreach (AsyncWork w in left)
            {
                if (w == null) continue;
                try
                {
                    if (w.Record != null)
                    {
                        // not_started is its own status, distinct from failed. A job that
                        // failed ran and did something; this one did not run at all, and a
                        // caller deciding whether to re-send needs that difference.
                        w.Record.Finish("not_started", reason);
                        closed++;
                    }
                }
                catch (Exception ex)
                {
                    if (warn != null)
                        warn("could not close the record for queued job " + w.JobId + ": " + ex.Message);
                }
            }

            return closed;
        }
    }
}
