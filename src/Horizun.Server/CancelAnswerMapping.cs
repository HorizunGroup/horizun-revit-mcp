// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code. Revit-free.
//
// ONE reading of what the add-in answered to a job cancellation. horizun_job_status
// cancel=true and tasks/cancel used to map the same answer two ways (review M4): the
// cooperative states (cancel_requested, cancelled_after_start) were "it runs to its
// end" in one and correct in the other. Both now go through here, so an answer can
// never mean two things depending on the door it came in by.
// -----------------------------------------------------------------------------
namespace Horizun.Server
{
    internal enum CancelKind
    {
        /// <summary>Revit did not answer (closed, busy past the control timeout, an older add-in).</summary>
        Unanswered,
        /// <summary>The queued work was removed, but its cancellation record could not be persisted.</summary>
        RecordUnavailable,
        /// <summary>Taken out of the queue before it started. Nothing ran.</summary>
        RemovedBeforeStart,
        /// <summary>A running script was asked to stop at its next checkpoint.</summary>
        InterruptRequested,
        /// <summary>The script had already been asked to stop (an earlier request, or its own time limit): nothing new was asked.</summary>
        AlreadyInterrupted,
        /// <summary>A cooperative job stopped between callbacks after it had started.</summary>
        CancelledAfterStart,
        /// <summary>A cooperative job was told to stop at its next safe point.</summary>
        CancelRequested,
        /// <summary>No longer waiting and not interruptible: it runs to its end.</summary>
        NotInterruptible
    }

    internal static class CancelAnswerMapping
    {
        public static CancelKind Classify(McpTasks.JobCancelAnswer answer)
        {
            if (answer == null) return CancelKind.Unanswered;
            if (answer.State == "cancelled_record_unavailable") return CancelKind.RecordUnavailable;
            if (answer.Removed) return CancelKind.RemovedBeforeStart;
            if (answer.State == "already_interrupted") return CancelKind.AlreadyInterrupted;
            if (answer.InterruptRequested || answer.State == "interrupt_requested")
                return CancelKind.InterruptRequested;
            if (answer.State == "cancelled_after_start") return CancelKind.CancelledAfterStart;
            if (answer.State == "cancel_requested") return CancelKind.CancelRequested;
            return CancelKind.NotInterruptible;
        }

        /// <summary>The state name both doors publish.</summary>
        public static string StateOf(McpTasks.JobCancelAnswer answer)
            => answer == null ? "unanswered" : (answer.State ?? "unknown");

        /// <summary>One sentence per kind. Never says "stopped": a cancellation is a request until the job's own record says so.</summary>
        public static string Meaning(McpTasks.JobCancelAnswer answer)
        {
            switch (Classify(answer))
            {
                case CancelKind.Unanswered:
                    return "Revit did not answer the cancellation request (closed, busy past the control timeout, or an older " +
                           "add-in), so it could not confirm removing or interrupting the job. Nothing is claimed: the job may still run.";
                case CancelKind.RecordUnavailable:
                    return "The queued work was removed, but its cancellation record could not be persisted; the partial result is not guaranteed to be available.";
                case CancelKind.RemovedBeforeStart:
                    return "Removed from Revit's queue before it started, on request. Nothing ran.";
                case CancelKind.InterruptRequested:
                    return "The running script was ASKED to stop. It stops at its next checkpoint()/safe_point(), rolls back " +
                           "the transactions it left open and the job finishes as cancelled_running; a script that never " +
                           "reaches one keeps running. Poll for the job's own state before believing it stopped.";
                case CancelKind.AlreadyInterrupted:
                    return "The running script had ALREADY been asked to stop (an earlier cancellation or its own time limit); nothing new " +
                           "was asked. It stops at its next checkpoint()/safe_point() and the job finishes as cancelled_running or timed_out; " +
                           "a script that never reaches one keeps running. Poll for the job's own state.";
                case CancelKind.CancelledAfterStart:
                    return "Cancelled after work started, between callbacks. Check the job result for a partial snapshot.";
                case CancelKind.CancelRequested:
                    return "Cancellation was accepted for this cooperative job. It will stop at a safe point; keep polling for " +
                           "its terminal state and partial result.";
                default:
                    return "The job was no longer waiting in Revit's queue (" + StateOf(answer) + "): it is running or has " +
                           "finished, and work on Revit's UI thread cannot be interrupted. It will reach a terminal state on its own.";
            }
        }
    }
}
