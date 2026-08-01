// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// FIFO ownership of Revit's single UI thread.
//
// Revit can execute only one API command at a time. That does not require every
// later caller to fail: it requires a bounded queue in which every request owns
// its own completion signal, can be taken exactly once, and can be removed while
// it is still waiting. Once a request starts, the Revit API cannot interrupt it.
//
// The queue prevents four silent failures:
//
//   * STALE WAKE: one caller can never receive another caller's result.
//   * DOUBLE EXECUTION: duplicate ExternalEvent callbacks cannot take one entry twice.
//   * ZOMBIE START: a timed-out/cancelled entry is removed before it can run later.
//   * UNBOUNDED PROMISE: a retry storm cannot queue hours of future model edits.
//
// No Autodesk references here. Sequencing and cancellation are tested without
// Revit because this is the part that must be correct before the UI thread exists.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;

namespace Horizun.Revit.Core
{
    public sealed class RequestGate
    {
        public const int MaxDepth = 16;

        public sealed class Request
        {
            public long Ticket;
            public string WireId;
            public string Name;
            public string ParamsJson;
            public DateTime QueuedUtc;
            public DateTime StartedUtc;
            public int AheadAtAdmission;

            public CommandResult Result;

            private readonly System.Threading.ManualResetEventSlim _done =
                new System.Threading.ManualResetEventSlim(false);

            public volatile bool Abandoned;
            public volatile bool Started;
            public volatile bool CancelledBeforeStart;

            internal LinkedListNode<Request> Node;

            public bool Wait(int timeoutMs) => _done.Wait(timeoutMs);
            public void Signal() => _done.Set();
        }

        private readonly object _lock = new object();
        private readonly LinkedList<Request> _pending = new LinkedList<Request>();
        private readonly int _maxDepth;
        private long _nextTicket;
        private Request _inFlight;

        public RequestGate(int maxDepth = MaxDepth)
        {
            if (maxDepth < 1) throw new ArgumentOutOfRangeException("maxDepth");
            _maxDepth = maxDepth;
        }

        public int PendingCount { get { lock (_lock) return _pending.Count; } }
        public int Capacity => _maxDepth;
        public bool HasPending { get { lock (_lock) return _pending.Count > 0; } }

        public Request Begin(string name, string paramsJson, out string refusal)
            => Begin(null, name, paramsJson, out refusal);

        /// <summary>
        /// Enqueue a request. Rejection is now backpressure only: malformed/null work
        /// is handled by the caller, and a valid request is refused here only when the
        /// bounded queue is full.
        /// </summary>
        public Request Begin(string wireId, string name, string paramsJson, out string refusal)
        {
            lock (_lock)
            {
                if (_pending.Count >= _maxDepth)
                {
                    refusal = "Revit's command queue is full: " + _pending.Count + " of " + _maxDepth +
                              " waiting slots are occupied" +
                              (_inFlight == null ? "." : " while " + DescribeRunning(_inFlight)) +
                              " Nothing was queued and nothing ran. Wait for outstanding calls to finish instead " +
                              "of retrying: retries consume more queue slots and cannot make Revit run in parallel.";
                    return null;
                }

                int ahead = _pending.Count + (_inFlight == null ? 0 : 1);
                var r = new Request
                {
                    Ticket = ++_nextTicket,
                    WireId = wireId,
                    Name = name,
                    ParamsJson = paramsJson,
                    QueuedUtc = DateTime.UtcNow,
                    AheadAtAdmission = ahead
                };
                r.Node = _pending.AddLast(r);
                refusal = null;
                return r;
            }
        }

        /// <summary>Claim the oldest live entry exactly once.</summary>
        public Request Take()
        {
            lock (_lock)
            {
                // ExternalEvent is not re-entrant, but refusing to manufacture a second
                // in-flight owner makes that assumption explicit and testable.
                if (_inFlight != null) return null;

                while (_pending.Count > 0)
                {
                    LinkedListNode<Request> node = _pending.First;
                    _pending.RemoveFirst();
                    Request r = node.Value;
                    r.Node = null;
                    if (r.Abandoned || r.CancelledBeforeStart) continue;

                    r.Started = true;
                    r.StartedUtc = DateTime.UtcNow;
                    _inFlight = r;
                    return r;
                }
                return null;
            }
        }

        public void Complete(Request r)
        {
            if (r == null) return;
            lock (_lock)
            {
                if (_inFlight == r) _inFlight = null;
            }
            r.Signal();
        }

        /// <summary>
        /// The local pipe worker stopped waiting. Remove work that has not started;
        /// already-running work is only marked because Revit cannot interrupt it.
        /// </summary>
        public void Abandon(Request r)
        {
            if (r == null) return;
            lock (_lock)
            {
                r.Abandoned = true;
                if (!r.Started && r.Node != null)
                {
                    _pending.Remove(r.Node);
                    r.Node = null;
                    r.CancelledBeforeStart = true;
                }
            }
        }

        /// <summary>
        /// Cancellation propagated from the MCP server over a separate control
        /// connection. True means the request was still queued and therefore NEVER RAN.
        /// False means it is running, finished, or unknown; none of those may be called
        /// cancelled safely.
        /// </summary>
        public bool CancelQueued(string wireId, out string detail)
        {
            detail = null;
            if (string.IsNullOrWhiteSpace(wireId))
            {
                detail = "No request id was supplied.";
                return false;
            }

            lock (_lock)
            {
                LinkedListNode<Request> node = _pending.First;
                while (node != null)
                {
                    LinkedListNode<Request> next = node.Next;
                    Request r = node.Value;
                    if (string.Equals(r.WireId, wireId, StringComparison.Ordinal))
                    {
                        _pending.Remove(node);
                        r.Node = null;
                        r.Abandoned = true;
                        r.CancelledBeforeStart = true;
                        r.Result = CommandResult.Fail("Cancelled while waiting in Revit's command queue. It NEVER " +
                            "STARTED: nothing was executed and nothing was written.");
                        r.Signal();
                        detail = "cancelled_before_start";
                        return true;
                    }
                    node = next;
                }

                if (_inFlight != null && string.Equals(_inFlight.WireId, wireId, StringComparison.Ordinal))
                {
                    detail = "already_running";
                    return false;
                }

                detail = "not_found_or_finished";
                return false;
            }
        }

        /// <summary>Fail and wake everything that is still waiting during shutdown.</summary>
        public int FailQueued(string reason)
        {
            var wake = new List<Request>();
            lock (_lock)
            {
                while (_pending.Count > 0)
                {
                    Request r = _pending.First.Value;
                    _pending.RemoveFirst();
                    r.Node = null;
                    r.CancelledBeforeStart = true;
                    r.Result = CommandResult.Fail(reason);
                    wake.Add(r);
                }
            }
            foreach (Request r in wake) r.Signal();
            return wake.Count;
        }

        public string BusyWith()
        {
            lock (_lock)
            {
                if (_inFlight != null)
                    return DescribeRunning(_inFlight) + QueueSuffix(_pending.Count);
                if (_pending.Count > 0)
                    return DescribeWaiting(_pending.First.Value) + QueueSuffix(Math.Max(0, _pending.Count - 1));
                return null;
            }
        }

        private static string QueueSuffix(int waiting)
            => waiting <= 0 ? "" : " " + waiting + " more request(s) are waiting in FIFO order.";

        private static string DescribeRunning(Request busy)
        {
            DateTime since = busy.StartedUtc == default(DateTime) ? busy.QueuedUtc : busy.StartedUtc;
            int seconds = (int)Math.Max(0, (DateTime.UtcNow - since).TotalSeconds);
            if (busy.Abandoned)
                return "Revit is still inside '" + busy.Name + "', started " + seconds + " s ago. Its caller " +
                       "stopped waiting, but the Revit API cannot interrupt work already on the UI thread.";
            return "Revit is running '" + busy.Name + "', started " + seconds + " s ago.";
        }

        private static string DescribeWaiting(Request waiting)
        {
            int seconds = (int)Math.Max(0, (DateTime.UtcNow - waiting.QueuedUtc).TotalSeconds);
            return "'" + waiting.Name + "' has waited " + seconds + " s for Revit's UI thread and has not started.";
        }
    }
}
