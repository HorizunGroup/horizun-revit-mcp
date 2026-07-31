// -----------------------------------------------------------------------------
// Horizun MCP - original Horizun code.
//
// Work that outlives its request.
//
// A script that takes twenty minutes cannot be answered inside a request: the
// client times out, and then the honest question is what happened to the work.
// The previous answer was "it is still running inside Revit and its result will
// reach nobody" - true, and useless.
//
// AT-MOST-ONCE IS THE WHOLE POINT, because this queue carries MUTATIONS. Take()
// removes the entry under a lock and returns it once; a second raise of the
// external event finds nothing and does nothing. There is no retry, no requeue
// on failure, and no path that runs an entry twice - a re-run of a script that
// already wrote to a model is a second write, not a recovery.
//
// The queue is IN MEMORY on purpose. If Revit dies, the job's record on disk
// stops without a finish line, and horizun_job_status reports exactly that
// rather than guessing between "still running" and "the process died". A queue
// that survived the process would invite replaying a mutation whose outcome
// nobody knows.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;

namespace Horizun.Revit.Core
{
    public sealed class AsyncWork
    {
        public string JobId;
        public string Command;
        public string ParamsJson;
        public Job Record;
        public DateTime QueuedUtc;
    }

    public static class AsyncQueue
    {
        private static readonly object _gate = new object();
        private static readonly Queue<AsyncWork> _pending = new Queue<AsyncWork>();

        /// <summary>How many are waiting.</summary>
        public static int Count { get { lock (_gate) return _pending.Count; } }

        /// <summary>
        /// The cap, and why there is one.
        ///
        /// Entries run ONE AT A TIME on Revit's UI thread, so a queue is a promise
        /// about the future: thirty-two entries of thirty seconds each is sixteen
        /// minutes before the last one starts. An unbounded queue accepts that promise
        /// silently, and a caller in a loop - which is what a retry storm or a script
        /// generating work looks like - can put hours of committed mutations behind a
        /// reply that said "queued" in a few milliseconds.
        ///
        /// Thirty-two is not a measured optimum. It is a number large enough that no
        /// legitimate batch here has reached it and small enough that overshooting it
        /// is visible while it is still cheap to undo.
        /// </summary>
        public const int MaxDepth = 32;

        /// <summary>
        /// Queue it, or say why not. THERE IS NO ADD THAT CANNOT FAIL.
        ///
        /// This replaced a void Add(). A void add on a bounded queue has exactly two
        /// implementations and both are wrong: drop silently, or grow without limit.
        /// The signature is what forces the caller to have an answer for a full queue.
        ///
        /// The check is INSIDE the lock. Commands are serialised by RequestGate today,
        /// so a check-then-add outside it would be correct by circumstance - and the
        /// circumstance is not stated anywhere that would survive a change to it.
        /// </summary>
        public static bool TryAdd(AsyncWork w, out string refusal)
        {
            refusal = null;
            if (w == null) return false;

            lock (_gate)
            {
                if (_pending.Count >= MaxDepth)
                {
                    refusal = "The async queue is full: " + _pending.Count + " of " + MaxDepth + " entries are " +
                              "already waiting, and they run ONE AT A TIME on Revit's UI thread. Nothing was " +
                              "queued and nothing ran. Wait for the outstanding work to finish - poll " +
                              "horizun_job_status - before sending more. If you are retrying because a reply " +
                              "looked slow, send the SAME idempotency_key instead: a retry is recognised and " +
                              "does not take a queue slot.";
                    return false;
                }
                _pending.Enqueue(w);
                return true;
            }
        }

        /// <summary>
        /// Claim the next entry, or null. DESTRUCTIVE: the entry is gone whether or not
        /// what follows succeeds. That is deliberate - see the header. A caller that
        /// takes an entry owns finishing its job record, including on the failure paths.
        /// </summary>
        public static AsyncWork Take()
        {
            lock (_gate) return _pending.Count == 0 ? null : _pending.Dequeue();
        }

        /// <summary>
        /// Everything still waiting when Revit is closing. These never ran, and saying
        /// so on the way down is the difference between a job whose record stops
        /// mid-flight and one that is known never to have started.
        /// </summary>
        public static List<AsyncWork> DrainForShutdown()
        {
            var all = new List<AsyncWork>();
            lock (_gate)
            {
                while (_pending.Count > 0) all.Add(_pending.Dequeue());
            }
            return all;
        }
    }
}
