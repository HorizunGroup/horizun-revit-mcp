// -----------------------------------------------------------------------------
// Horizun MCP server - original Horizun code.
//
// One writer, one reply per request, and a register of what is still running.
//
// The server used to read a line, do the whole job, write the answer, and only
// then read the next line. While a Revit command ran - and model_scan on a real
// project takes over two minutes - nothing else could be answered. Not ping, not
// health, not job_status, which exists PRECISELY to be askable while Revit is
// busy and was unreachable exactly when it was needed. Measured: a job_status
// sent immediately after a three second script answered after the script.
//
// Making that concurrent introduces two hazards that this file exists to remove:
//
//   INTERLEAVED JSON. Two tasks writing to stdout at once produce one corrupt
//   line and a client that disconnects. Every write goes through one lock here.
//
//   TWO REPLIES TO ONE REQUEST. A timeout path and a completion path can both
//   answer. JSON-RPC allows exactly one response per id, and a second one is a
//   loose message the client will either mis-attribute or choke on.
//
// The first version enforced the second rule by having the WRITER remember every
// id it had ever answered, forever. That is a stronger rule than JSON-RPC has, and
// the difference is not academic: an id is only reserved WHILE its request is
// outstanding, and a client that numbers its requests 1..n and reconnects, or one
// that simply reuses a small pool of ids, starts again at 1. Measured: the second
// request to arrive with id 7 was executed in full - the model was touched - and
// its reply was then dropped on the floor by the writer, leaving the client
// waiting forever for an answer to work that had already happened. Silence after a
// completed write is the worst failure this codebase can produce.
//
// So exactly-once is now scoped to the REQUEST rather than to the id. Each
// dispatched call gets a ReplySlot, a one-shot latch it owns; whichever path
// reaches it first answers and the others are refused. The writer serialises and
// nothing else, so an id is reusable the moment its request is done - which is
// exactly what the protocol says.
//
// All of it is Revit-free, so all of it is provable without a building.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Horizun.Server
{
    /// <summary>
    /// The only thing allowed to write to the client. It serialises messages, and that
    /// is ALL it does: one lock, one complete line at a time, never a torn one.
    ///
    /// It deliberately keeps no memory of which ids it has answered. That memory used to
    /// live here and it outlived the requests it was about, so an id could never be used
    /// twice in one session - see the header. "One answer per request" is the request's
    /// property, and it is enforced by the request's own ReplySlot.
    /// </summary>
    internal sealed class OutboundWriter
    {
        private readonly TextWriter _out;
        private readonly object _lock = new object();
        private long _written;

        public OutboundWriter(TextWriter output) { _out = output; }

        /// <summary>How many messages have gone out. For tests and reporting.</summary>
        public long WrittenCount { get { lock (_lock) return _written; } }

        /// <summary>
        /// A one-shot handle for answering ONE request. Every path that can answer it -
        /// completion, timeout, cancellation, an unexpected throw - goes through the same
        /// slot, and only the first of them writes.
        /// </summary>
        public ReplySlot Slot(object id) => new ReplySlot(this, id);

        /// <summary>
        /// Answer a request that is being handled inline, on the reader thread, where
        /// there is exactly one path to an answer and nothing to race. Returns whether
        /// anything was written, so the shape matches ReplySlot's.
        /// </summary>
        public bool TryReply(object id, JToken result) => Write(Envelope(id, "result", result));

        public bool TryError(object id, int code, string message) =>
            Write(Envelope(id, "error", new JObject { ["code"] = code, ["message"] = message }));

        /// <summary>A notification carries no id and is never an answer to anything.</summary>
        public void Notify(string method, JObject parameters)
        {
            var msg = new JObject { ["jsonrpc"] = "2.0", ["method"] = method };
            if (parameters != null) msg["params"] = parameters;
            Write(msg);
        }

        internal bool Write(JObject message)
        {
            lock (_lock)
            {
                try { _out.WriteLine(message.ToString(Formatting.None)); }
                catch { /* the client is gone; the reader will notice */ }
                _written++;
                return true;
            }
        }

        internal static JObject Envelope(object id, string field, JToken payload)
        {
            var o = new JObject { ["jsonrpc"] = "2.0", ["id"] = id == null ? null : JToken.FromObject(id) };
            o[field] = payload;
            return o;
        }

        /// <summary>
        /// Identity of a request id for deduplication. 1 and "1" are DIFFERENT ids in
        /// JSON-RPC, so the type is part of the key - collapsing them would make a client
        /// that uses both lose one of its answers.
        /// </summary>
        public static string Key(object id)
        {
            if (id == null) return null;
            return id.GetType().Name + ":" + Convert.ToString(id, System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
    /// The right to answer ONE request, exactly once.
    ///
    /// A dispatched tool call has several paths to an answer and two of them can be
    /// taken at the same time: the work completes at the moment cancellation lands, or
    /// it throws while the shutdown drain has already given up on it. JSON-RPC allows
    /// one response per request, so one of them has to lose - and it has to lose
    /// SILENTLY, without the loser having to know the winner existed.
    ///
    /// The latch belongs to the request, not to the id. That distinction is the whole
    /// fix in this file: an id is reserved only while its request is outstanding, so the
    /// NEXT request to use id 7 gets a fresh slot and its own answer, instead of being
    /// mistaken for a duplicate of a request that finished ten minutes ago.
    /// </summary>
    internal sealed class ReplySlot
    {
        private readonly OutboundWriter _writer;
        private readonly object _id;
        private int _claimed;

        internal ReplySlot(OutboundWriter writer, object id) { _writer = writer; _id = id; }

        /// <summary>True once something has answered. For reporting, never for deciding.</summary>
        public bool Answered => Volatile.Read(ref _claimed) != 0;

        public bool TryReply(JToken result) =>
            Claim() && _writer.Write(OutboundWriter.Envelope(_id, "result", result));

        public bool TryError(int code, string message) =>
            Claim() && _writer.Write(OutboundWriter.Envelope(
                _id, "error", new JObject { ["code"] = code, ["message"] = message }));

        private bool Claim() => Interlocked.CompareExchange(ref _claimed, 1, 0) == 0;
    }

    /// <summary>
    /// What is running right now, so it can be cancelled and so a repeated id can be
    /// refused instead of quietly starting the same work twice.
    /// </summary>
    internal sealed class InFlight
    {
        private sealed class Entry
        {
            public CancellationTokenSource Cts;
            public string Tool;
            public DateTime StartedUtc;
            // When this request is GUARANTEED to have answered, one way or the other.
            // Not a hope: PipeClient.Send enforces its own timeout, so past this instant
            // the task has either replied or thrown a TimeoutException and replied to
            // that. It is what makes the shutdown drain terminate on a derived bound
            // instead of an invented one.
            public DateTime DeadlineUtc;
        }

        private readonly Dictionary<string, Entry> _running = new Dictionary<string, Entry>(StringComparer.Ordinal);
        private readonly object _lock = new object();

        public int Count { get { lock (_lock) return _running.Count; } }

        /// <summary>
        /// Claim an id. Returns false when the client reused an id that is STILL RUNNING:
        /// two live requests under one id have two results and one address to send them
        /// to, so one of them would have to be dropped.
        ///
        /// Reuse AFTER the first has finished is not a duplicate and is not refused - it
        /// is ordinary JSON-RPC, and a client that numbers its requests from a small pool
        /// does it constantly. Finish() is what releases the id, and it runs on every
        /// exit path including the ones that threw.
        /// </summary>
        public bool TryStart(string key, string tool, CancellationTokenSource cts, out string refusal,
                             int deadlineMs = 0)
        {
            refusal = null;
            if (key == null) return true;      // a notification: nothing to track
            lock (_lock)
            {
                if (_running.TryGetValue(key, out Entry existing))
                {
                    int secs = (int)(DateTime.UtcNow - existing.StartedUtc).TotalSeconds;
                    refusal = "Request id already in flight: '" + existing.Tool + "' has been running for " + secs +
                              " s under this same id. Ids must be unique while they are outstanding - starting the " +
                              "work again would produce two results for one id and one of them would be dropped. " +
                              "Nothing was started.";
                    return false;
                }
                DateTime now = DateTime.UtcNow;
                _running[key] = new Entry
                {
                    Cts = cts,
                    Tool = tool,
                    StartedUtc = now,
                    DeadlineUtc = now.AddMilliseconds(deadlineMs > 0 ? deadlineMs : 0)
                };
                return true;
            }
        }

        /// <summary>
        /// The instant by which EVERY outstanding request must have answered, or null
        /// when nothing is outstanding.
        ///
        /// This exists so shutdown can drain on a bound it DERIVES rather than one it
        /// invents. The first version of the drain waited a flat 120 s, which is both
        /// too long for a host-resident call that answered in 3 ms and too short for a
        /// scan of a 200k-element model that is entitled to ten minutes - so it could
        /// discard exactly the answer it was added to protect.
        /// </summary>
        public DateTime? DrainDeadlineUtc()
        {
            lock (_lock)
            {
                DateTime? latest = null;
                foreach (Entry e in _running.Values)
                    if (latest == null || e.DeadlineUtc > latest.Value) latest = e.DeadlineUtc;
                return latest;
            }
        }

        public void Finish(string key)
        {
            if (key == null) return;
            lock (_lock)
            {
                if (_running.TryGetValue(key, out Entry e))
                {
                    _running.Remove(key);
                    try { e.Cts?.Dispose(); } catch { }
                }
            }
        }

        /// <summary>Cancel one request. False when there is nothing by that id.</summary>
        public bool Cancel(string key)
        {
            if (key == null) return false;
            lock (_lock)
            {
                if (!_running.TryGetValue(key, out Entry e)) return false;
                try { e.Cts?.Cancel(); } catch { }
                return true;
            }
        }

        public void CancelAll()
        {
            lock (_lock)
                foreach (var e in _running.Values) { try { e.Cts?.Cancel(); } catch { } }
        }

        /// <summary>What is running, for a message that has to describe it.</summary>
        public string Describe()
        {
            lock (_lock)
            {
                if (_running.Count == 0) return "nothing is running";
                var parts = new List<string>();
                foreach (var kv in _running)
                    parts.Add("'" + kv.Value.Tool + "' (" + (int)(DateTime.UtcNow - kv.Value.StartedUtc).TotalSeconds + " s)");
                return string.Join(", ", parts);
            }
        }
    }
}
