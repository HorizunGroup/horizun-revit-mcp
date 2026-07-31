// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// Who owns Revit's UI thread, and which reply belongs to which caller.
//
// The Revit API runs on one thread and there is NO way to abort a command from
// outside it. So a command that overruns its caller's patience does not stop: it
// keeps running while the caller has already given up and moved on. Everything
// dangerous follows from that one fact.
//
// The rule this file enforces: a caller may only ever be handed the result of
// the request IT made. Not the previous one, not a duplicate of its own.
//
// Three failures are possible without it, and all three are silent:
//
//   1. STALE WAKE. Caller A times out. Caller B starts. A's command finishes and
//      signals "done" - and B, waiting on that same signal, returns A's result as
//      its own. B is told about work it never asked for, with its own request id
//      on the envelope, so nothing downstream can tell.
//
//   2. DOUBLE EXECUTION. Revit's ExternalEvent is raised once per request, but a
//      raise that has not fired yet still fires later. If the pending request has
//      been overwritten in the meantime, the stale raise runs the NEW request -
//      and then the new raise runs it AGAIN. For a write, that is the same edit
//      applied twice.
//
//   3. ZOMBIE START. A request times out before Revit ever picked it up (Revit was
//      on a modal). Minutes later the event fires and the abandoned command runs,
//      against a model the user has since moved on from.
//
// The fix is ownership, not locking. Each request is an object with its own
// completion signal; the UI thread TAKES it (exactly once, or gets nothing); a
// caller that gives up marks its request abandoned and it can never start. While
// something is in flight, new work is REFUSED with a description of what is
// holding the thread - not queued behind it, because queueing behind a run that
// already blew a ten-minute budget only moves the hang.
//
// No `using Autodesk.*` here, on purpose: this is the part that can be tested
// without Revit, and it is the part that must not be wrong.
// -----------------------------------------------------------------------------
using System;

namespace Horizun.Revit.Core
{
    public sealed class RequestGate
    {
        /// <summary>
        /// One request, and the only place its result may be written or read. The
        /// completion signal belongs to the request rather than to the gate, so a
        /// caller physically cannot be woken by somebody else's command finishing.
        /// </summary>
        public sealed class Request
        {
            public long Ticket;
            public string Name;
            public string ParamsJson;
            public DateTime StartedUtc;

            /// <summary>Set by the UI thread, read by the caller that owns this object.</summary>
            public CommandResult Result;

            private readonly System.Threading.ManualResetEventSlim _done =
                new System.Threading.ManualResetEventSlim(false);

            /// <summary>The caller stopped waiting. The work may still be running.</summary>
            public bool Abandoned;

            /// <summary>The UI thread picked this up. False means it never started.</summary>
            public bool Started;

            public bool Wait(int timeoutMs) => _done.Wait(timeoutMs);
            public void Signal() => _done.Set();
        }

        private readonly object _lock = new object();
        private long _nextTicket;

        // Handed to the UI thread but not yet picked up.
        private Request _pending;

        // Picked up by the UI thread and not yet finished. Cannot be cancelled.
        private Request _inFlight;

        /// <summary>
        /// Claim the UI thread. Returns null - with a sentence explaining what is in
        /// the way - when a previous request still holds it. Never blocks: an honest
        /// refusal now beats a wait that ends in a second timeout.
        /// </summary>
        public Request Begin(string name, string paramsJson, out string refusal)
        {
            lock (_lock)
            {
                Request busy = _inFlight ?? _pending;
                if (busy != null)
                {
                    refusal = Describe(busy);
                    return null;
                }

                var r = new Request
                {
                    Ticket = ++_nextTicket,
                    Name = name,
                    ParamsJson = paramsJson,
                    StartedUtc = DateTime.UtcNow
                };
                _pending = r;
                refusal = null;
                return r;
            }
        }

        /// <summary>
        /// Called on Revit's UI thread. Returns the request to run, or null if there is
        /// nothing to do - which is the normal answer for a duplicate event raise, and
        /// for a request whose caller gave up before Revit ever got to it. Taking is
        /// destructive: a request can be taken once and only once.
        /// </summary>
        public Request Take()
        {
            lock (_lock)
            {
                Request r = _pending;
                _pending = null;
                if (r != null)
                {
                    r.Started = true;
                    _inFlight = r;
                }
                return r;
            }
        }

        /// <summary>
        /// Called on the UI thread once the request is finished and its Result is set.
        /// The thread is released BEFORE the caller is woken, so a caller that turns
        /// around and issues its next command does not meet a gate that still looks busy.
        /// </summary>
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
        /// The caller stopped waiting. If Revit never picked the request up, drop it so
        /// it can never start later against a model that has moved on. If it is already
        /// running, it cannot be stopped - mark it, so the next caller is told the truth
        /// about why the thread is busy instead of a generic "timed out".
        /// </summary>
        public void Abandon(Request r)
        {
            if (r == null) return;
            lock (_lock)
            {
                r.Abandoned = true;
                if (_pending == r) _pending = null;
            }
        }

        /// <summary>What is holding the UI thread right now, or null if nothing is.</summary>
        public string BusyWith()
        {
            lock (_lock)
            {
                Request busy = _inFlight ?? _pending;
                return busy == null ? null : Describe(busy);
            }
        }

        private static string Describe(Request busy)
        {
            int seconds = (int)Math.Max(0, (DateTime.UtcNow - busy.StartedUtc).TotalSeconds);

            if (busy.Abandoned)
                return "Revit is still inside '" + busy.Name + "', started " + seconds + " s ago. The call that " +
                       "asked for it already gave up waiting, but the Revit API cannot be interrupted from " +
                       "outside, so the work continues and this thread is not free. Nothing new can run until it " +
                       "returns. Watch its progress with horizun_job_status (it reads from disk and does not need " +
                       "this thread), or wait.";

            if (!busy.Started)
                return "'" + busy.Name + "' was handed to Revit " + seconds + " s ago and Revit has not started it " +
                       "yet - usually a modal dialog holding the UI thread. One request at a time.";

            return "Revit is running '" + busy.Name + "', started " + seconds + " s ago. One request at a time: " +
                   "this one has to finish first.";
        }
    }
}
