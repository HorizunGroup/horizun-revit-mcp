// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// Revit's "Project Not Saved Recently" reminder, answered while the bridge is in use.
//
// MEASURED 2026-10-22, release gate v3.0.5: Revit raises its save reminder 30
// minutes (the default) after a model's last save, and it raises it while Revit
// is IDLE - between two bridge commands, where Interference is not subscribed.
// Nobody answered it, so every later request found a modal and was refused as
// never started: 15 failures in Revit 2023 and 18 in 2024 (52 refused calls),
// and the release did not reach signing. Any client that keeps an agent working
// on one model for more than half an hour meets the same dialog.
//
// What is answered, and why only this. The reminder is a nag about the user's
// own file, not information about the model, and its non-saving answer changes
// nothing: no file is written and no element is touched. Every other dialog
// still waits for a person (ModalSighting declares it, the request is refused
// as never started), exactly as before. The answer is never "save": writing the
// user's file is a decision the bridge does not take for them.
//
// Which button. NOT the second command link: on this dialog 1002 is "Save the
// project and set reminder intervals". The answer is Cancel (see DoNotSaveResult).
//
// When. Only while the bridge is IN USE - a request is waiting or one finished
// within ActiveWindow. A person working in Revit on their own still gets their
// reminder; it is only answered when an agent is driving the session, and then
// it is RECORDED and handed to the next command's reply (dialogs block), so the
// agent can tell the person the model has unsaved work.
//
// Revit-free: the decision is arithmetic over a dialog id and two clocks.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Threading;

namespace Horizun.Revit.Core
{
    public static class SaveReminderPolicy
    {
        /// <summary>How long after its last request the bridge still counts as in use.</summary>
        public static readonly TimeSpan ActiveWindow = TimeSpan.FromMinutes(10);

        /// <summary>
        /// The non-saving answer: Cancel (IDCANCEL = 2). MEASURED in the Revit 2023 and 2024
        /// journals of the failed gate run, TaskDialog_Project_Not_Saved_Recently offers
        /// 1001 "Save the project", 1002 "Save the project and set reminder intervals",
        /// 1003 "Do not save and set reminder intervals" (which opens the options) and the
        /// common button Cancel. Both 1001 and 1002 SAVE. Cancel closes it without saving;
        /// the same journal shows Interference answering it Cancel during a command and
        /// Revit carrying on.
        /// </summary>
        public const int DoNotSaveResult = 2;

        /// <summary>The reminder's dialog id, as Revit's journal records it.</summary>
        public const string ReminderDialogId = "TaskDialog_Project_Not_Saved_Recently";

        /// <summary>
        /// True for the save reminder's dialog id. Revit names it SaveReminder in its own
        /// binaries; the id it surfaces through DialogBoxShowing is matched on that stem,
        /// case-insensitively and ignoring underscores - never on the localized message.
        /// </summary>
        public static bool IsSaveReminder(string dialogId)
        {
            if (string.IsNullOrEmpty(dialogId)) return false;
            string id = dialogId.Replace("_", "").Replace(" ", "");
            return id.IndexOf("SaveReminder", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   id.IndexOf("NotSavedRecently", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>The bridge is in use: a request is waiting, or one was seen within ActiveWindow.</summary>
        public static bool BridgeInUse(DateTime lastActivityUtc, DateTime nowUtc, bool requestPending)
        {
            if (requestPending) return true;
            if (lastActivityUtc == default(DateTime)) return false;
            TimeSpan since = nowUtc - lastActivityUtc;
            return since >= TimeSpan.Zero && since <= ActiveWindow;
        }

        /// <summary>Answer this dialog for the bridge? Only the reminder, and only while the bridge is in use.</summary>
        public static bool ShouldAnswer(string dialogId, DateTime lastActivityUtc, DateTime nowUtc, bool requestPending)
            => IsSaveReminder(dialogId) && BridgeInUse(lastActivityUtc, nowUtc, requestPending);

        // ---- the bridge's activity clock -------------------------------------------

        private static long _lastActivityTicks;
        private static int _pending;

        /// <summary>A request arrived, started or finished. Called from any thread.</summary>
        public static void MarkActive() => Interlocked.Exchange(ref _lastActivityTicks, DateTime.UtcNow.Ticks);

        /// <summary>A request is now waiting or running (paired with RequestDone).</summary>
        public static void RequestBegun() { Interlocked.Increment(ref _pending); MarkActive(); }

        /// <summary>The request that RequestBegun counted has returned to its caller.</summary>
        public static void RequestDone() { Interlocked.Decrement(ref _pending); MarkActive(); }

        public static DateTime LastActivityUtc
        {
            get
            {
                long t = Interlocked.Read(ref _lastActivityTicks);
                return t == 0 ? default(DateTime) : new DateTime(t, DateTimeKind.Utc);
            }
        }

        public static bool RequestPending => Volatile.Read(ref _pending) > 0;

        // ---- what was answered between commands -----------------------------------

        private static readonly object _gate = new object();
        private static readonly List<Interruption> _answered = new List<Interruption>();
        private const int MaxKept = 20;

        /// <summary>Record a dialog answered between commands, for the next command's reply.</summary>
        public static void Record(Interruption answered)
        {
            if (answered == null) return;
            lock (_gate)
            {
                if (_answered.Count >= MaxKept) _answered.RemoveAt(0);
                _answered.Add(answered);
            }
        }

        /// <summary>Everything recorded since the last drain, oldest first; the list is emptied.</summary>
        public static List<Interruption> Drain()
        {
            lock (_gate)
            {
                var all = new List<Interruption>(_answered);
                _answered.Clear();
                return all;
            }
        }
    }
}
