// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// The dialogs Revit raises BETWEEN commands, while the bridge is in use.
//
// Interference watches one command, subscribed before it and gone after it. A
// dialog Revit raises while idle - its save reminder, measured on the v3.0.5
// release gate - reached nobody, and every later request was refused behind it.
// This watcher lives as long as the add-in. It answers exactly what
// SaveReminderPolicy allows (the save reminder, with "do not save", only while
// the bridge is in use) and leaves every other dialog for a person, as before.
// What it answers is recorded and handed to the next command's reply.
// -----------------------------------------------------------------------------
using System;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;

namespace Horizun.Revit.Core
{
    public static class IdleDialogWatcher
    {
        private static bool _attached;

        /// <summary>Subscribe for the add-in's lifetime. Call once, from OnStartup.</summary>
        public static void Attach(UIControlledApplication app)
        {
            if (_attached) return;
            app.DialogBoxShowing += OnDialog;
            _attached = true;
            Log.Info("idle dialog watcher attached: Revit's save reminder is answered 'do not save' while the bridge is in use");
        }

        public static void Detach(UIControlledApplication app)
        {
            if (!_attached) return;
            try { app.DialogBoxShowing -= OnDialog; } catch { }
            _attached = false;
        }

        private static void OnDialog(object sender, DialogBoxShowingEventArgs e)
        {
            try
            {
                // A command is running: its own watcher (Interference) records and
                // answers what Revit raises. Answering twice would be one too many.
                if (Interference.Current != null) return;

                string dialogId = null;
                string message = null;
                try
                {
                    var td = e as TaskDialogShowingEventArgs;
                    dialogId = td != null ? td.DialogId : e.DialogId;
                    if (td != null) message = td.Message;
                }
                catch { }

                DateTime now = DateTime.UtcNow;
                bool inUse = SaveReminderPolicy.BridgeInUse(
                    SaveReminderPolicy.LastActivityUtc, now, SaveReminderPolicy.RequestPending);
                // The id only: a dialog's text can name the user's files, and a log is
                // not the place for that.
                if (inUse) Log.Info("dialog raised between commands while the bridge is in use: id '" + (dialogId ?? "(none)") + "'");

                if (!SaveReminderPolicy.ShouldAnswer(dialogId, SaveReminderPolicy.LastActivityUtc, now,
                                                     SaveReminderPolicy.RequestPending))
                    return;

                string answered;
                try
                {
                    e.OverrideResult(SaveReminderPolicy.DoNotSaveResult);
                    answered = "cancelled (not saved) by the bridge between commands: Revit's save reminder blocks " +
                               "every request while it is open, and nobody is at the keyboard. Nothing was saved and " +
                               "nothing in the model changed - the model still has unsaved work; save it when appropriate";
                }
                catch (Exception ex)
                {
                    answered = "could NOT be answered (" + ex.Message + ") - requests will be refused until a person closes it";
                }

                Log.Warn("Revit's save reminder ('" + dialogId + "') raised between commands: " + answered);
                SaveReminderPolicy.Record(new Interruption
                {
                    Kind = "dialog",
                    Description = dialogId + (string.IsNullOrEmpty(message) ? "" : ": " + message),
                    Answered = answered,
                    While = "between commands (Revit was idle)"
                });
            }
            catch (Exception ex)
            {
                try { Log.Warn("idle dialog watcher failed: " + ex.Message); } catch { }
            }
        }
    }
}
