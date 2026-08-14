// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// MAY THIS ASYNCHRONOUS WORK BE ACCEPTED AT ALL? One answer, in one place.
//
// Both asynchronous entry points - horizun_submit_job and execute_python with
// run_async - open a job record and then queue the work. Both had their own
// version of "did that work?", and both versions were wrong in the same way:
// SubmitJobCommand tested `string.IsNullOrWhiteSpace(job.Id)` against an id that
// was assigned before anything could fail, and ExecutePythonCommand tested
// nothing at all. A record that could not be created was therefore queued, and
// the caller was handed a job_id addressing no file.
//
// The rule is small and the consequence of getting it wrong is not, so it lives
// here, Revit-free, and is proved in CI rather than reasoned about twice. The
// commands need a UIApplication and cannot be tested without a Revit; this can.
// -----------------------------------------------------------------------------
using System;

namespace Horizun.Revit.Core
{
    public static class AsyncJobAdmission
    {
        /// <summary>
        /// Open the durable record this work will be reported through, or refuse.
        ///
        /// On false, <paramref name="job"/> is null and <paramref name="refusal"/> is the
        /// sentence to hand the caller. It always ends by saying that nothing was queued,
        /// because the caller's next decision depends on exactly that: an asynchronous
        /// refusal that is ambiguous about whether the work started is the one thing
        /// worse than a refusal.
        /// </summary>
        public static bool TryOpen(string tool, IJobSink sink, out Job job, out string refusal)
        {
            job = null;
            refusal = null;
            try
            {
                job = Job.Start(tool, sink);
                return true;
            }
            catch (JobRecordException ex)
            {
                refusal = "Could not open the persistent job record for '" + tool + "': " + ex.Message +
                          " Nothing was queued. Asynchronous work is reported ONLY through that record, so " +
                          "queueing it without one would run the command with no way for anyone to learn what " +
                          "it did.";
                return false;
            }
        }

        /// <summary>Production overload: the real filesystem.</summary>
        public static bool TryOpen(string tool, out Job job, out string refusal)
            => TryOpen(tool, null, out job, out refusal);
    }
}
