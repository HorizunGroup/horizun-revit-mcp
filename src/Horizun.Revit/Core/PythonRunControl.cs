// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code. Revit-free.
//
// execute_python: time limit and cancellation of a script that is already running.
//
// Revit offers no way to stop code on its UI thread from outside, so the stop is
// COOPERATIVE: the script's own checkpoint() / should_stop() / safe_point() ask this
// object, and when it says stop they raise HorizunInterrupted (a BaseException, so a
// script's `except Exception` does not swallow it). The exception unwinds through the
// script, and the bridge then rolls back every transaction the script left open - the
// same path a script error takes. What this CANNOT do is stop a script that never calls
// one of those, undo a transaction the script already committed, or break into a single
// long Revit API call; the reply says so instead of implying otherwise.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Concurrent;
using System.Globalization;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public sealed class PythonRunControl
    {
        public const string Cancelled = "cancelled";
        public const string TimedOut = "timed_out";
        public const double MinLimitSeconds = 1;
        public const double MaxLimitSeconds = 86400;

        private readonly object _gate = new object();
        private readonly Func<DateTime> _clock;
        private readonly DateTime _startedUtc;
        private string _kind;
        private string _reason;
        private DateTime? _trippedUtc;
        private int _polls;

        public PythonRunControl(double? limitSeconds, Func<DateTime> utcNow = null)
        {
            _clock = utcNow ?? (() => DateTime.UtcNow);
            _startedUtc = _clock();
            LimitSeconds = limitSeconds;
        }

        public double? LimitSeconds { get; private set; }
        public string Kind { get { lock (_gate) return _kind; } }
        public string Reason { get { lock (_gate) return _reason; } }
        public int Polls { get { lock (_gate) return _polls; } }
        public double ElapsedSeconds
        {
            get { lock (_gate) return ((_trippedUtc ?? _clock()) - _startedUtc).TotalSeconds; }
        }
        public double ElapsedWallSeconds { get { lock (_gate) return (_clock() - _startedUtc).TotalSeconds; } }

        public JObject NotPreemptible(int checkpoints, double? timeout)
        {
            if (!timeout.HasValue || ElapsedWallSeconds < timeout.Value || checkpoints != 0) return null;
            return new JObject { ["elapsed_s"] = Math.Round(ElapsedWallSeconds, 3),
                ["note"] = "The timeout passed without a checkpoint. The script could not be preempted; inspect the outcome and model." };
        }

        /// <summary>Ask for the script to stop at its next safe point. False when it already was.</summary>
        public bool Cancel(string reason)
        {
            lock (_gate)
            {
                if (_kind != null) return false;
                _kind = Cancelled;
                _reason = string.IsNullOrWhiteSpace(reason) ? "cancellation requested" : reason;
                _trippedUtc = _clock();
                return true;
            }
        }

        /// <summary>Null while the script may continue; otherwise why it must stop. Latched.</summary>
        public string Poll()
        {
            lock (_gate)
            {
                _polls++;
                if (_kind == null && LimitSeconds.HasValue && (_clock() - _startedUtc).TotalSeconds >= LimitSeconds.Value)
                {
                    _kind = TimedOut;
                    _reason = "timeout_s=" + LimitSeconds.Value.ToString("0.###", CultureInfo.InvariantCulture) + " exceeded";
                    _trippedUtc = _clock();
                }
                return _kind;
            }
        }

        public static bool TryParseLimit(JToken token, out double? limit, out string error)
        {
            limit = null; error = null;
            if (token == null || token.Type == JTokenType.Null) return true;
            if (token.Type != JTokenType.Integer && token.Type != JTokenType.Float)
            {
                error = "timeout_s must be a number of seconds. Nothing ran.";
                return false;
            }
            double value = token.Value<double>();
            if (double.IsNaN(value) || value < MinLimitSeconds || value > MaxLimitSeconds)
            {
                error = "timeout_s must be between " + MinLimitSeconds + " and " + MaxLimitSeconds +
                        " seconds. Nothing ran.";
                return false;
            }
            limit = value;
            return true;
        }

        public static bool TryParseDeadline(JToken token, double? timeout, out double? deadline, out string error)
        {
            if (!TryParseLimit(token, out deadline, out error))
            {
                error = error.Replace("timeout_s", "deadline_s");
                return false;
            }
            if (deadline.HasValue && timeout.HasValue && deadline.Value > timeout.Value)
            {
                error = "deadline_s must be less than or equal to timeout_s. Nothing ran.";
                return false;
            }
            return true;
        }

        /// <summary>The terminal status a job record gets: the interruption wins over a plain failure.</summary>
        public static string TerminalStatus(bool success, JObject detail)
        {
            if (success) return "ok";
            string kind = (string)detail?["interrupted"]?["kind"];
            if (kind == TimedOut) return "timed_out";
            if (kind == Cancelled) return "cancelled_running";
            return "failed";
        }

        public static readonly string[] NotRevertedCategories =
        {
            "explicit_checkouts", "files_written_or_exported", "other_documents_opened_or_edited",
            "link_reloads", "active_view", "selection"
        };

        /// <summary>
        /// INT-6 m-7: <c>changes_applied=false</c> is a statement about the MODEL'S TRANSACTIONS, and a script also does things
        /// no transaction undoes. Wherever a reply says changes_applied is false after a script ran, this puts beside it, at
        /// the same level, the scope of that "false" and the categories that were neither measured nor reverted.
        /// A changes_applied that is not false (null, true, absent) is left alone.
        /// </summary>
        public static void StampChangesAppliedScope(JObject block)
        {
            if (block == null) return;
            JToken applied = block["changes_applied"];
            if (applied == null || applied.Type != JTokenType.Boolean || (bool)applied) return;
            block["changes_applied_scope"] = "model transactions only: the changes the script made inside transactions were rolled back. " +
                "Effects outside transactions were neither measured nor reverted: " + NotRevertedList + " (see not_reverted_categories).";
            block["not_reverted_categories"] = new JArray(NotRevertedCategories);
        }

        private const string NotRevertedList =
            "explicit element checkouts (borrowing), files written or exported, other documents opened or edited, " +
            "link reloads, the active view and the selection";

        /// <summary>
        /// What is NOT undone by the stop, said for the scope the script ran in (review M6): an ordinary run keeps what it
        /// already committed; a read_only or documentation_only run happens inside a group that is rolled back as a whole,
        /// so the statement is the opposite - and says so only when that rollback is confirmed.
        /// </summary>
        public static string NotUndoneText(string scope, bool? groupRolledBack)
        {
            switch (scope)
            {
                case "read_only":
                    return groupRolledBack == true
                        ? "The model changes the script made inside transactions were rolled back (the read_only group is always " +
                          "rolled back, re-read and proven with RollbackProven). Effects outside transactions are neither " +
                          "measured nor reverted: " + NotRevertedList + "."
                        : "A read_only run happens inside a group that is always rolled back, but this rollback is NOT confirmed: " +
                          "treat the model as possibly changed and check it. Effects outside transactions are not measured or reverted: " +
                          NotRevertedList + ".";
                case "documentation_only":
                    return groupRolledBack == true
                        ? "The stop rolled back the WHOLE documentation_only group: the model changes the script made inside " +
                          "transactions, including those already committed, were rolled back (re-read and proven with " +
                          "RollbackProven). Effects outside transactions are neither measured nor reverted: " + NotRevertedList + "."
                        : "The documentation_only group's rollback is NOT confirmed: transactions the script already committed may " +
                          "remain. Treat the model as possibly changed and check it. Effects outside transactions are not measured or " +
                          "reverted: " + NotRevertedList + ".";
                default:
                    return "Anything the script already committed before the stop stays committed; only " +
                           "transactions still open when it stopped were rolled back.";
            }
        }

        /// <summary>What the reply and the job declare about the interruption.</summary>
        public JObject Describe(string interruptedAt, string openTransaction, int transactionsRolledBack,
                                string scope = "normal", bool? groupRolledBack = null)
        {
            return new JObject
            {
                ["kind"] = Kind,
                ["reason"] = Reason,
                ["timeout_s"] = LimitSeconds.HasValue ? (JToken)LimitSeconds.Value : JValue.CreateNull(),
                ["elapsed_s"] = Math.Round(ElapsedSeconds, 3),
                ["interrupted_at"] = interruptedAt,
                ["open_transaction"] = openTransaction,
                ["transactions_rolled_back"] = transactionsRolledBack,
                ["scope"] = scope ?? "normal",
                ["stopped_at"] = "a safe point: checkpoint(), safe_point() or should_stop() in the script",
                ["not_undone"] = NotUndoneText(scope, groupRolledBack),
                ["not_reverted_categories"] = new JArray(NotRevertedCategories)
            };
        }
    }

    /// <summary>
    /// Runs in flight, so a control message from another thread can reach one. Keyed by job id (an async job, and every
    /// script's own record) and by "wire:" + request id (a synchronous call, which its caller cancels by request id).
    ///
    /// A cancel that arrives BEFORE the run registers is not lost (review M3): the dispatcher claims the key the moment it
    /// takes the work, a cancel against a claimed key is kept, and Register applies it to the run's control at once.
    /// </summary>
    public static class PythonRunRegistry
    {
        private sealed class Slot
        {
            public PythonRunControl Control;
            public string PendingReason;
            /// <summary>A cancel was asked for this key. Sticky: it outlives the script it reached (INT-6 m-6).</summary>
            public bool CancelRequested;
            /// <summary>The key is a PLAN's job: scripts come and go under it and the key outlives each of them (INT-7 m-e).</summary>
            public bool OutlivesChildren;
            /// <summary>The script ended; the request has not been closed yet (see Finish).</summary>
            public bool Finished;
        }

        private static readonly object Gate = new object();
        private static readonly System.Collections.Generic.Dictionary<string, Slot> Slots =
            new System.Collections.Generic.Dictionary<string, Slot>(StringComparer.Ordinal);

        public static string WireKey(string wireId) => string.IsNullOrEmpty(wireId) ? null : "wire:" + wireId;

        /// <summary>The commands whose job id is claimed the moment the queue hands them to the UI thread.</summary>
        public static bool ClaimsAtTake(string command) =>
            string.Equals(command, "horizun_execute_python", StringComparison.Ordinal) ||
            string.Equals(command, "horizun_execute_plan", StringComparison.Ordinal);

        /// <summary>The commands that run steps of their own under their job id, so the id outlives each step's script.</summary>
        public static bool RunsChildren(string command) => string.Equals(command, "horizun_execute_plan", StringComparison.Ordinal);

        /// <summary>
        /// A script ended. If its key is a PLAN's job (claimed at take), the JOB did not end: the script's control is gone and a
        /// cancel already asked for stays asked for, so the next step sees it (INT-6 m-6); the dispatcher unregisters the key when
        /// the job closes. Any other key was the script's own: it is released, and a cancel that comes after the script ended is
        /// "not_running", as it always was (INT-7 m-e).
        /// </summary>
        public static void ChildEnded(string key)
        {
            if (string.IsNullOrEmpty(key)) return;
            lock (Gate)
            {
                Slot slot;
                if (!Slots.TryGetValue(key, out slot)) return;
                if (slot.OutlivesChildren) slot.Control = null;
                else Slots.Remove(key);
            }
        }

        /// <summary>True once a cancel was asked for this key and the key has not been unregistered: a plan reads it between steps.</summary>
        public static bool IsCancelRequested(string key)
        {
            if (string.IsNullOrEmpty(key)) return false;
            lock (Gate)
            {
                Slot slot;
                return Slots.TryGetValue(key, out slot) && slot.CancelRequested;
            }
        }

        /// <summary>The work was taken for the UI thread and will register in a moment: a cancel from now on is kept.</summary>
        public static void Claim(string key, bool outlivesChildren = false)
        {
            if (string.IsNullOrEmpty(key)) return;
            lock (Gate)
            {
                Slot slot;
                if (!Slots.TryGetValue(key, out slot)) Slots[key] = slot = new Slot();
                if (outlivesChildren) slot.OutlivesChildren = true;
            }
        }

        public static void Register(string key, PythonRunControl control)
        {
            if (string.IsNullOrEmpty(key) || control == null) return;
            string pending;
            lock (Gate)
            {
                Slot slot;
                if (!Slots.TryGetValue(key, out slot)) { slot = new Slot(); Slots[key] = slot; }
                slot.Control = control;
                slot.Finished = false;   // a plan runs one script after another under the same request
                pending = slot.PendingReason;
            }
            if (pending != null) control.Cancel(pending);
        }

        /// <summary>
        /// The script of a SYNCHRONOUS request ended, but its request is not closed yet (the gate removes the key at Complete).
        /// A cancel in that window must not claim a slot nobody will ever register: it is answered "finished" (review m-6).
        /// </summary>
        public static void Finish(string key)
        {
            if (string.IsNullOrEmpty(key)) return;
            lock (Gate)
            {
                Slot slot;
                if (!Slots.TryGetValue(key, out slot)) { slot = new Slot(); Slots[key] = slot; }
                slot.Control = null;
                slot.PendingReason = null;
                slot.Finished = true;
            }
        }

        public static void Unregister(string key)
        {
            if (string.IsNullOrEmpty(key)) return;
            lock (Gate) Slots.Remove(key);
        }

        /// <summary>interrupt_requested, already_interrupted, finished (a synchronous script ended before its request closed), or not_running (never claimed, or unknown).</summary>
        public static string Cancel(string key, string reason)
        {
            if (string.IsNullOrEmpty(key)) return "not_running";
            PythonRunControl control;
            lock (Gate)
            {
                Slot slot;
                if (!Slots.TryGetValue(key, out slot)) return "not_running";
                if (slot.Finished) return "finished";
                slot.CancelRequested = true;
                control = slot.Control;
                if (control == null)
                {
                    // Claimed, not yet registered: keep the request; Register applies it.
                    bool already = slot.PendingReason != null;
                    if (!already) slot.PendingReason = string.IsNullOrWhiteSpace(reason) ? "cancellation requested" : reason;
                    return already ? "already_interrupted" : "interrupt_requested";
                }
            }
            return control.Cancel(reason) ? "interrupt_requested" : "already_interrupted";
        }

        /// <summary>Cancel a request the dispatcher has certainly claimed (it is in flight): claims the key if nothing did.</summary>
        public static string CancelClaimed(string key, string reason)
        {
            Claim(key);   // keeps an existing slot, including a Finished one
            return Cancel(key, reason);
        }
    }
}
