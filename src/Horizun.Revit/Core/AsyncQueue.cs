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
using System.Threading;

namespace Horizun.Revit.Core
{
    public sealed class AsyncWork
    {
        public string JobId;
        public string Command;
        public string ParamsJson;
        public Job Record;
        public DateTime QueuedUtc;
        // A yielded continuation may be waiting even though earlier slices ran.
        // The queue owns this flag after the UI thread sets it before requeueing.
        public bool HasStarted;

        // Opt-in only: ordinary commands remain non-interruptible once claimed.
        // The UI thread observes this flag between indivisible read units.
        public bool CooperativeCancellation;
        private int _cancelRequested;
        public bool CancellationRequested => Volatile.Read(ref _cancelRequested) != 0;
        internal void RequestCancellation() => Interlocked.Exchange(ref _cancelRequested, 1);

        // Immutable JSON snapshot, produced on the Revit thread before yielding.
        // The queue lock transfers ownership to either the next callback or the
        // cancelling pipe thread; the latter must never inspect Revit objects.
        public string PartialResultJson;
        public string PartialRevitSaidJson;
        // Revit-thread-owned continuation; never inspected by the pipe thread.
        internal object Continuation { get; set; }
        internal RequestGate.Request SyncRequest { get; set; }
        internal Action<string, string> OnInterrupted { get; set; }
        private int _syncCompleted;
        internal bool TryCompleteSync() => Interlocked.CompareExchange(ref _syncCompleted, 1, 0) == 0;

        /// <summary>
        /// Whether the cancellation's record reached disk: true, false, or null when that is not the question. A synchronous
        /// cooperative job's caller receives the outcome in its own reply, and its record may be non-durable by design (review m5).
        /// </summary>
        internal bool? RecordPersisted()
        {
            if (Record != null && Record.RecordIsComplete) return true;
            return SyncRequest != null ? (bool?)null : false;
        }

        public void FinishInterrupted(string status, string reason)
        {
            try
            {
                if (Record != null)
                {
                    if (HasStarted && PartialResultJson != null) Record.Result(PartialResultJson, PartialRevitSaidJson);
                    Record.Finish(status, reason);
                }
            }
            finally { OnInterrupted?.Invoke(status, reason); }
        }

        // A guard may reject a subsequent callback before it reaches Begin.
        // Earlier callbacks still ran, so neither their result nor that fact may
        // be replaced with a never-started record.
        public void FinishBeforeCallback(string reason)
        {
            if (HasStarted)
            {
                MarkInterrupted(CooperativeInterruption.Failed, reason);
                FinishInterrupted("failed", reason);
            }
            else
            {
                try { Record?.Finish("not_started", reason); }
                finally { OnInterrupted?.Invoke("not_started", reason); }
            }
        }

        // The pipe thread cannot inspect the active document or its change epoch.
        // A snapshot cancelled while waiting is evidence of earlier work, but its
        // continued validity has not been established at cancellation time.
        public void FinishCancelledWhileWaiting(string reason)
        {
            if (HasStarted) MarkInterrupted(CooperativeInterruption.Cancelled, reason);
            FinishInterrupted(HasStarted ? "cancelled_after_start" : "cancelled", reason);
        }

        // Set by a continuation that can compare its document stamp from any thread. It returns why the
        // snapshot is no longer valid, or null while it still is.
        internal Func<string> Revalidate { get; set; }

        // ONE treatment for a run that ends early, whatever ended it and at whatever moment: a cancel
        // that finds the job waiting, one observed in the callback, a change or a failure. Validity comes
        // from the stamp (the evidence), never from when the cancel arrived (R2-N M4).
        internal void MarkInterrupted(string kind, string reason)
        {
            string stale;
            if (kind != CooperativeInterruption.Cancelled) stale = reason;
            else if (Revalidate == null) stale = "Cancelled while waiting; document state could not be revalidated.";
            else
            {
                try { stale = Revalidate(); }
                catch (Exception ex) { stale = "The snapshot could not be revalidated: " + ex.Message; }
            }
            if (PartialResultJson != null)
                PartialResultJson = CooperativeInterruption.Apply(PartialResultJson, kind, reason, JobId, stale);
        }

        internal void InvalidateSnapshot(string reason)
        {
                // A guard prevented revalidation of this snapshot. Do not leave an
                // earlier valid=true in a result that can no longer be trusted.
                if (PartialResultJson != null)
                {
                    try
                    {
                        var partial = Newtonsoft.Json.Linq.JObject.Parse(PartialResultJson);
                        partial["result"] = "partial";
                        partial["valid"] = false;
                        partial["invalid_reason"] = reason;
                        PartialResultJson = partial.ToString(Newtonsoft.Json.Formatting.None);
                    }
                    catch (Newtonsoft.Json.JsonException)
                    {
                        // A malformed snapshot is not evidence and must not hide the
                        // reason the continuation was stopped.
                        PartialResultJson = null;
                    }
                }
        }

        /// <summary>
        /// Set when this job's work is an ordered SEQUENCE rather than a single call -
        /// a read-only sweep over many models, admitted by JobSequenceRules. Null for
        /// every ordinary submission, which is why nothing about the single-call path
        /// changes.
        /// </summary>
        public System.Collections.Generic.List<SequenceEntry> Sequence;
        public string DocumentFingerprint;
    }

    public static class AsyncQueue
    {
        public sealed class Cancellation
        {
            public AsyncWork Work { get; internal set; }
            public bool Requested { get; internal set; }
            public bool Cancelled => Work != null;
            public bool BeforeStart => Cancelled && !Work.HasStarted;
            public string State => Requested ? "cancel_requested" : !Cancelled ? "not_in_queue" : BeforeStart ? "removed_before_start" : "cancelled_after_start";
        }

        public static Cancellation CancelWaiting(string jobId)
        {
            lock (_gate)
            {
                if (TryRemove(jobId, out AsyncWork work)) return new Cancellation { Work = work };
                if (!string.IsNullOrEmpty(jobId) && _activeCooperative.TryGetValue(jobId, out work))
                {
                    work.RequestCancellation();
                    return new Cancellation { Requested = true };
                }
                return new Cancellation();
            }
        }

        private static readonly object _gate = new object();
        private static readonly Queue<AsyncWork> _pending = new Queue<AsyncWork>();
        private static readonly Dictionary<string, AsyncWork> _activeCooperative = new Dictionary<string, AsyncWork>(StringComparer.Ordinal);

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
            if (w.CooperativeCancellation && string.IsNullOrWhiteSpace(w.JobId))
            {
                refusal = "A cooperatively cancellable job must have an id.";
                return false;
            }

            lock (_gate)
            {
                if (!string.IsNullOrEmpty(w.JobId))
                {
                    if (_activeCooperative.ContainsKey(w.JobId))
                    {
                        refusal = "A cooperative job with this id is already active.";
                        return false;
                    }
                    foreach (AsyncWork pending in _pending)
                        if ((w.CooperativeCancellation || pending.CooperativeCancellation) &&
                            string.Equals(pending.JobId, w.JobId, StringComparison.Ordinal))
                        {
                            refusal = "A cooperative job with this id is already queued.";
                            return false;
                        }
                }
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
        /// Return an already-started slice to the tail, even if new admissions filled
        /// the queue while it was running. One extra slot bounds that race; duplicate
        /// continuations are refused so a repeated scheduling attempt cannot run twice.
        /// The caller still owns terminal reporting if this method refuses.
        /// </summary>
        public static bool TryRequeueContinuation(AsyncWork work, out string refusal)
        {
            refusal = null;
            lock (_gate)
            {
                if (work == null || !work.HasStarted || string.IsNullOrEmpty(work.JobId))
                    refusal = "Only an identified, already-started job may be requeued as a continuation.";
                else if (work.CooperativeCancellation &&
                    (!_activeCooperative.TryGetValue(work.JobId, out AsyncWork active) || !ReferenceEquals(work, active)))
                    refusal = "The cooperative continuation is not the active owner of this job.";
                else if (work.CooperativeCancellation && work.CancellationRequested)
                    refusal = "Cancellation was requested while the continuation was active.";
                else if (_pending.Count >= MaxDepth + 1)
                    refusal = "The reserved continuation slot is already occupied.";
                else
                {
                    foreach (AsyncWork pending in _pending)
                        if (string.Equals(pending.JobId, work.JobId, StringComparison.Ordinal))
                        {
                            refusal = "This job already has a queued continuation.";
                            break;
                        }
                }
                if (refusal != null) return false;
                if (work.CooperativeCancellation) _activeCooperative.Remove(work.JobId);
                _pending.Enqueue(work);
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
            lock (_gate)
            {
                if (_pending.Count == 0) return null;
                AsyncWork work = _pending.Dequeue();
                if (work.CooperativeCancellation && !string.IsNullOrEmpty(work.JobId))
                    _activeCooperative.Add(work.JobId, work);
                // Review m-2: a script's stop key is claimed in the SAME critical section that dequeues it. A cancel that comes in
                // after this lock is released finds the claim (and is kept until the script registers); one that came before it
                // found the entry still queued and removed it (TryRemove). There is no instant in which it is neither.
                // INT-6 m-6: the same for an execute_plan, whose steps may run a script under the plan's job id and which has
                // gaps between steps: a cancel that comes in before a child registers, or between two steps, is kept.
                if (PythonRunRegistry.ClaimsAtTake(work.Command) && !string.IsNullOrEmpty(work.JobId))
                    PythonRunRegistry.Claim(work.JobId, PythonRunRegistry.RunsChildren(work.Command));
                return work;
            }
        }

        // The terminal decision and cancellation request are linearized under
        // the same lock. After this returns, cancellation cannot claim the job.
        public static bool CompleteCooperative(AsyncWork work)
        {
            lock (_gate)
            {
                if (work == null || !work.CooperativeCancellation || string.IsNullOrEmpty(work.JobId) ||
                    !_activeCooperative.TryGetValue(work.JobId, out AsyncWork active) || !ReferenceEquals(work, active))
                    throw new InvalidOperationException("The cooperative job is not active.");
                _activeCooperative.Remove(work.JobId);
                return work.CancellationRequested;
            }
        }

        /// <summary>
        /// Take ONE waiting entry out by its job id, or say it is not waiting. Under the
        /// same lock as Take, so an entry is either removed here or claimed by the UI
        /// thread - never both: "not waiting" means it started (or finished, or never
        /// existed here), and the caller must not report it as cancelled. Used by
        /// tasks/cancel (audit 2026-10-08), which used to promise this removal and never
        /// asked for it.
        /// </summary>
        public static bool TryRemove(string jobId, out AsyncWork removed)
        {
            removed = null;
            if (string.IsNullOrEmpty(jobId)) return false;
            lock (_gate)
            {
                var kept = new Queue<AsyncWork>();
                while (_pending.Count > 0)
                {
                    AsyncWork w = _pending.Dequeue();
                    if (removed == null && string.Equals(w.JobId, jobId, StringComparison.Ordinal)) removed = w;
                    else kept.Enqueue(w);
                }
                while (kept.Count > 0) _pending.Enqueue(kept.Dequeue());
            }
            return removed != null;
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
