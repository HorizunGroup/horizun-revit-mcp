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
// Denied is not transient. Revit returns it when the ExternalEvent has been
// disposed or the application is closing down, so there is no later raise that
// would rescue those entries. The only honest response is to take them off the
// queue and close each record as not_started - which is a FACT, and the whole
// point of distinguishing it from a record that stops mid-flight.
//
// Revit-free on purpose. Denied cannot be produced on demand - it needs a Revit
// that is shutting down - and "reasoned and compiled" was the status of this
// path for a reason. Behind IWorkRaiser all three answers are ordinary test
// cases.
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
        /// On Denied or Unknown the queue is DRAINED and every record closed as
        /// not_started. Leaving them would be a queue nothing will ever pump again and
        /// records that never close, reported as the same ambiguity as a process that
        /// died - when in fact the outcome is known exactly.
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

            result.Note = "Revit answered Raise() with " + result.Outcome + ", so no callback is coming and the " +
                          "queued work will never start. Revit is closing down, or the bridge's external event " +
                          "has been disposed.";
            result.AbandonedJobs = CloseEverythingWaiting(
                "Revit refused to schedule this job: Raise() returned " + result.Outcome + ". It NEVER STARTED - " +
                "nothing was executed and nothing was written. This is a known outcome, not a job that stopped " +
                "mid-flight: Denied means Revit is shutting down or the bridge's external event was disposed. " +
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
