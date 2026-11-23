// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// THE TWO DIALOGS THE BRIDGE MAY CLOSE ON ITS OWN, and nothing else.
//
// Owner decision (2026-10-27): while an MCP request is waiting for Revit, the bridge
// closes exactly two notices that hold the UI thread and change nothing in a model:
//
//   * "Project Not Saved Recently" (Revit's save reminder) - answered with Cancel,
//     the option that neither saves nor changes the model;
//   * "External Tools - External Tool Failure" (an add-in failed to load or run) -
//     answered with Close.
//
// Field logs: these two were 87 of the ~150 "Revit is on a modal dialog" refusals
// on one machine, and a third of execute_python's "timeouts" in the trace sample
// are this refusal three seconds in. Every OTHER dialog still belongs to the person
// at the keyboard: Save File, Unresolved References, a security prompt, anything
// unknown - those keep the existing behaviour (named, never answered).
//
// Pure: it judges the strings ModalProbe read. The click is ModalProbe's, and every
// close is logged and returned to the caller with the notice's name and the option
// chosen.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public static class ModalAutoClose
    {
        // Shared by the dispatcher and the Win32 integration test: a successful
        // post is only an attempt until the actual window has disappeared.
        internal static JObject Attempt(ModalDialogInfo info, Decision decision, string tool, Action wait,
                                        JArray attempts, JArray closedDialogs)
        {
            if (decision == null || !ModalProbe.TryCancel(info)) return null;
            wait();
            JObject attempt = decision.ToJson(tool, DateTime.UtcNow, ModalProbe.IsClosed(info));
            attempts.Add(attempt);
            if (attempt.Value<bool>("closed_verified")) closedDialogs.Add(attempt.DeepClone());
            return attempt;
        }
        /// <summary>At most this many automatic closes for one request; then the dialog is the person's.</summary>
        public const int MaxClosesPerRequest = 2;

        public sealed class Decision
        {
            public string Kind;
            public string Title;
            /// <summary>The option the bridge chose, in the words the reply and the log use.</summary>
            public string Option;

            public JObject ToJson(string tool, DateTime utc, bool closedVerified = false)
            {
                var report = new JObject
                {
                ["dialog"] = Title,
                ["dialog_kind"] = Kind,
                ["option"] = Option,
                ["while_waiting_for"] = tool,
                ["attempted_utc"] = utc.ToString("o"),
                ["closed_verified"] = closedVerified
                };
                if (closedVerified) report["closed_utc"] = utc.ToString("o");
                return report;
            }
        }

        /// <summary>
        /// Null unless the dialog is one of the two notices above, identified by its
        /// TITLE (a body that merely mentions an add-in is not enough).
        /// </summary>
        public static Decision Decide(string title, IEnumerable<string> texts, IEnumerable<string> buttons,
                                      bool autoCloseExternalToolFailure = true, bool autoCloseSaveReminder = true)
        {
            string t = (title ?? "").Trim();
            if (t.Length == 0) return null;
            string body = string.Join("\n", (texts ?? Enumerable.Empty<string>()).Where(s => !string.IsNullOrWhiteSpace(s)));
            var btns = (buttons ?? Enumerable.Empty<string>()).Where(s => !string.IsNullOrWhiteSpace(s)).ToList();

            // The titles follow Revit's language; the Spanish ones are matched as well (review 2026-11-20, M2),
            // and a dialog whose title is in neither is left alone, as before.
            if (autoCloseSaveReminder && (Has(t, "Not Saved Recently") || Has(t, "guardado recientemente")))
            {
                // Never a dialog that offers ONLY a save: Cancel must exist or the buttons
                // must be unreadable (TaskDialog command links are not Button children).
                if (btns.Count > 0 && !btns.Any(b => Has(b, "Cancel") || Has(b, "Do not save") || Has(b, "Don't save") ||
                                                     Has(b, "Cancelar") || Has(b, "No guardar")))
                    return null;
                return new Decision { Kind = ModalDialogKinds.SaveReminder, Title = t, Option = "Cancel (do not save)" };
            }
            if (autoCloseExternalToolFailure && (Has(t, "External Tool Failure") || (Has(t, "External Tools") && Has(body + t, "Fail")) ||
                                                 (Has(t, "herramienta") && Has(t, "extern") && (Has(body + t, "Error") || Has(body + t, "Fallo")))))
                return new Decision { Kind = ModalDialogKinds.ExternalToolFailure, Title = t, Option = "Close" };
            return null;
        }

        public static Decision Decide(ModalDialogInfo info, bool autoCloseExternalToolFailure = true, bool autoCloseSaveReminder = true) =>
            info == null || !info.DialogWindowFound ? null : Decide(info.Title, info.AllText, info.Buttons, autoCloseExternalToolFailure, autoCloseSaveReminder);

        /// <summary>
        /// DialogBoxShowing has an API id and message rather than a window title.
        /// Null means another dialog; zero means report this notice without answering it.
        /// Positive values are standard TaskDialog/Win32 results: Cancel=2, Close=8.
        /// </summary>
        public static int? EventNoticeResult(string dialogId, string message, bool external, bool reminder)
        {
            string id = (dialogId ?? "").Replace('_', ' ');
            string body = message ?? "";
            if (Has(id, "Not Saved Recently") || Has(id, "NotSavedRecently") ||
                Has(body, "not been saved recently") || Has(body, "Not Saved Recently") ||
                Has(id, "Reminder") && Has(id, "Save"))
                return reminder ? 2 : 0;
            if (Has(id, "External Tool Failure") || Has(id, "ExternalToolFailure") ||
                Has(body, "External Tool Failure") || Has(id, "External Tools") && Has(body, "Fail"))
                return external ? 8 : 0;
            return null;
        }

        private static bool Has(string s, string needle) =>
            !string.IsNullOrEmpty(s) && s.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
    }
}
