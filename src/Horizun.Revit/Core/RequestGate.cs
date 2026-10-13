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
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public sealed class RequestGate
    {
        public const int MaxDepth = 16;

        /// <summary>How many early cancels (an id cancelled before it reached the gate) are remembered.</summary>
        public const int MaxEarlyCancels = 64;

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

        // Ids cancelled before they reached the gate (backlog #36), oldest first. Bounded:
        // a cancel for a request that already FINISHED lands here too and is never consumed.
        private readonly LinkedList<string> _earlyCancelOrder = new LinkedList<string>();
        private readonly HashSet<string> _earlyCancels = new HashSet<string>(StringComparer.Ordinal);

        public RequestGate(int maxDepth = MaxDepth)
        {
            if (maxDepth < 1) throw new ArgumentOutOfRangeException("maxDepth");
            _maxDepth = maxDepth;
        }

        public int PendingCount { get { lock (_lock) return _pending.Count; } }
        public int Capacity => _maxDepth;
        public bool HasPending { get { lock (_lock) return _pending.Count > 0; } }

        public JObject Observe(string wireId)
        {
            lock(_lock)
            {
                var result=new JObject { ["wire_id"]=wireId,["state"]="not_found_or_finished",["queued_count"]=_pending.Count,["observed_utc"]=DateTime.UtcNow.ToString("o") };
                if(string.IsNullOrWhiteSpace(wireId)) { result["state"]="invalid_request_id"; return result; }
                if(_inFlight!=null && string.Equals(_inFlight.WireId,wireId,StringComparison.Ordinal))
                {
                    result["state"]="executing";
                    result["execution_elapsed_ms"]=Math.Max(0,(DateTime.UtcNow-_inFlight.StartedUtc).TotalMilliseconds);
                    return result;
                }
                int position=0;
                foreach(var request in _pending)
                {
                    position++;
                    if(!string.Equals(request.WireId,wireId,StringComparison.Ordinal)) continue;
                    result["state"]="queued"; result["queue_position"]=position;
                    result["queue_wait_ms"]=Math.Max(0,(DateTime.UtcNow-request.QueuedUtc).TotalMilliseconds);
                    break;
                }
                return result;
            }
        }

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
                // The cancel came first (backlog #36): it travels on its own connection and
                // can overtake this request while it is still being read and authenticated.
                // Refused on arrival, with the same never-started proof as a queued removal.
                if (wireId != null && _earlyCancels.Remove(wireId))
                {
                    _earlyCancelOrder.Remove(wireId);
                    var cancelled = new Request
                    {
                        Ticket = ++_nextTicket,
                        WireId = wireId,
                        Name = name,
                        ParamsJson = paramsJson,
                        QueuedUtc = DateTime.UtcNow,
                        Abandoned = true,
                        CancelledBeforeStart = true,
                        Result = CommandResult.FailWithDetail("'" + name + "' was cancelled before it reached Revit's " +
                            "command queue: the cancellation arrived first, so the request was refused on arrival. It " +
                            "NEVER STARTED: nothing was executed and nothing was written.", NeverStartedDetail())
                    };
                    cancelled.Signal();
                    refusal = null;
                    return cancelled;
                }

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
        ///
        /// Returns TRUE only when the request is proven never to have started - removed
        /// here, or already removed by the cancel control. Decided under the same lock
        /// as Take, so it cannot disagree with the UI thread: a caller that read Started
        /// a moment earlier and then abandoned used to report "NEVER STARTED" for a
        /// request Take had claimed in between (audit 2026-10-08).
        /// </summary>
        public bool Abandon(Request r)
        {
            if (r == null) return false;
            lock (_lock)
            {
                r.Abandoned = true;
                if (!r.Started && r.Node != null)
                {
                    _pending.Remove(r.Node);
                    r.Node = null;
                    r.CancelledBeforeStart = true;
                }
                return r.CancelledBeforeStart && !r.Started;
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
                        r.Result = CommandResult.FailWithDetail("Cancelled while waiting in Revit's command queue. It NEVER " +
                            "STARTED: nothing was executed and nothing was written.", NeverStartedDetail());
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

                // Not here: it finished, or it has not ARRIVED yet. Which cannot be told, so
                // the id is remembered and refused if it does arrive - wire ids are fresh
                // per send, so this can stop a request that would have run after its cancel
                // and nothing else.
                if (_earlyCancels.Add(wireId))
                {
                    _earlyCancelOrder.AddLast(wireId);
                    while (_earlyCancelOrder.Count > MaxEarlyCancels)
                    {
                        _earlyCancels.Remove(_earlyCancelOrder.First.Value);
                        _earlyCancelOrder.RemoveFirst();
                    }
                }
                detail = "not_found_or_finished";
                return false;
            }
        }

        /// <summary>
        /// The answer for a request whose OWN raise Revit refused, but which the UI thread
        /// had already claimed through another raise (Abandon returned false): it runs,
        /// and its result is what the caller gets.
        ///
        /// The wait's answer is READ (backlog #38). It used to be ignored, so a command
        /// still running when the wait expired came back as an ordinary "produced no
        /// result" failure - nothing saying it had started and may still be writing.
        /// </summary>
        public static CommandResult AwaitClaimed(Request req, string name, int timeoutMs)
        {
            if (!req.Wait(timeoutMs))
                return CommandResult.FailWithDetail(
                    "'" + name + "' timed out after " + timeoutMs + " ms. Revit refused to queue this request's own " +
                    "ExternalEvent raise, but the UI thread had already claimed it through another one, so it " +
                    "STARTED. The command is STILL RUNNING inside Revit - it cannot be cancelled from here, and " +
                    "whatever it does will complete unseen. Do not resend it assuming nothing happened.",
                    new JObject
                    {
                        ["never_started"] = false,
                        ["still_running"] = true,
                        ["transaction_status"] = "unknown"
                    });
            return req.Result ?? CommandResult.Fail("'" + name + "' produced no result.");
        }

        /// <summary>
        /// The structured proof a never-started result carries. The server decides
        /// "never started" on this field alone, never on the wording (audit 2026-10-08),
        /// so every path that removes a request before it starts must attach it.
        /// </summary>
        public static Newtonsoft.Json.Linq.JObject NeverStartedDetail() => new Newtonsoft.Json.Linq.JObject
        {
            ["never_started"] = true,
            ["transaction_status"] = "not_started"
        };

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
                    r.Result = CommandResult.FailWithDetail(reason, NeverStartedDetail());
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
