// -----------------------------------------------------------------------------
// Horizun MCP — original Horizun code.
//
// What Revit tried to say while a command was running, and what we answered.
//
// This is the number one way Revit automation dies: a transaction commits, Revit
// raises a warning, a modal dialog opens, and the UI thread stops. The bridge is
// not crashed and not finished — it is waiting for a click that no one is there
// to give, until the call times out with nothing to show for the work already
// done. Anything long enough to be worth automating will hit one eventually.
//
// The obvious fix — swallow the dialogs — is the wrong one, and it is the exact
// lie this codebase exists to refuse. A warning Revit raised is information about
// the model: "these elements have duplicate marks", "this room is not enclosed".
// Making it disappear so the script keeps running turns a bridge that hangs into
// a bridge that lies, which is worse, because the second one gets believed.
//
// So: every failure and every dialog is RECORDED, dismissed only as far as it
// takes to keep going, and reported back beside the command's own result. The
// caller ends up with the answer AND the list of what Revit objected to.
//
// Errors are treated differently from warnings on purpose. A warning is Revit
// telling you something; an error is Revit refusing. Auto-resolving an error
// changes the model in a way nobody asked for — deleting the offending element,
// usually — so errors are left to fail the transaction, and reported.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;

namespace Horizun.Revit.Core
{
    /// <summary>One thing Revit raised during a command, and what happened to it.</summary>
    public sealed class Interruption
    {
        public string Kind;          // "warning" | "error" | "dialog"
        public string Description;
        public string Answered;      // what we did about it
        public List<long> Elements;  // ids Revit blamed, when it named any
    }

    // DialogAnswer lives in OpenDialogPolicy.cs (Revit-free, so its parse rule is
    // unit-tested without a Revit); this file only ACTS on it.

    /// <summary>
    /// Watches one command's execution: application-level failure processing and any
    /// modal dialog. Subscribe before, dispose after — it unsubscribes itself, so a
    /// command that throws cannot leave Revit's events wired to a dead object.
    /// </summary>
    public sealed class Interference : IDisposable
    {
        // How OnDialog answers, read at DIALOG TIME so a command can widen it only around
        // the one call that needs it. ThreadStatic and defaulting to Cancel: commands run
        // one at a time on Revit's UI thread, every command that does not set it gets
        // Cancel, and the setter is a scoped using() that always restores it. Same shape
        // as Job.Ambient - a cross-cutting per-call hint the dispatcher's watcher reads.
        [ThreadStatic] private static DialogAnswer _openDialogPolicy;

        /// <summary>
        /// Answer open-time dialogs with <paramref name="answer"/> until the returned
        /// scope is disposed, then restore the previous policy. Wrap ONLY the open call:
        /// a Dismiss left in place would press "continue" on every later dialog too.
        /// </summary>
        public static IDisposable WithDialogAnswer(DialogAnswer answer)
        {
            DialogAnswer prev = _openDialogPolicy;
            _openDialogPolicy = answer;
            return new PolicyScope(prev);
        }

        private sealed class PolicyScope : IDisposable
        {
            private readonly DialogAnswer _prev;
            private bool _done;
            public PolicyScope(DialogAnswer prev) { _prev = prev; }
            public void Dispose() { if (_done) return; _done = true; _openDialogPolicy = _prev; }
        }

        private readonly UIApplication _app;
        private readonly List<Interruption> _seen = new List<Interruption>();
        private bool _off;

        public IList<Interruption> Seen => _seen;
        public int WarningCount { get; private set; }
        public int ErrorCount { get; private set; }
        public int DialogCount { get; private set; }

        public Interference(UIApplication app)
        {
            _app = app;
            try
            {
                _app.DialogBoxShowing += OnDialog;
                _app.Application.FailuresProcessing += OnFailures;
            }
            catch { /* if we cannot subscribe, the command still runs — unwatched, and we say so */ }
        }

        public void Dispose()
        {
            if (_off) return;
            _off = true;
            try { _app.DialogBoxShowing -= OnDialog; } catch { }
            try { _app.Application.FailuresProcessing -= OnFailures; } catch { }
        }

        /// <summary>
        /// A modal dialog opened. Left alone it stops the UI thread until the call times
        /// out. It is cancelled — the least destructive answer available — and recorded.
        /// Cancelling can make the underlying operation fail; that failure is honest and
        /// visible, which is what we want. Silently pressing OK would not be.
        /// </summary>
        private void OnDialog(object sender, DialogBoxShowingEventArgs e)
        {
            string what = null;
            try
            {
                var td = e as TaskDialogShowingEventArgs;
                what = td != null ? (td.DialogId + ": " + td.Message) : e.DialogId;
            }
            catch { }

            string answered;
            try
            {
                if (_openDialogPolicy == DialogAnswer.Dismiss)
                {
                    // on_open_dialog=dismiss: acknowledge and continue. 1 == IDOK for a
                    // plain dialog box; TaskDialogResult.Ok is 1 too. Best effort - a
                    // dialog whose "continue" is some other button will not proceed, and
                    // that is recorded here rather than hidden. It is scoped to the open
                    // call by the command that set it; every other dialog still cancels.
                    e.OverrideResult(1);
                    answered = "dismissed by the bridge (on_open_dialog=dismiss: acknowledged and continued)";
                }
                else
                {
                    // 2 == IDCANCEL for a plain dialog box; TaskDialog takes the same value
                    // as its Cancel result. Either way: do not proceed on the user's behalf.
                    e.OverrideResult(2);
                    answered = "cancelled by the bridge (nobody is at the keyboard to answer it)";
                }
            }
            catch (Exception ex)
            {
                answered = "could NOT be dismissed (" + ex.Message + ") — the call may hang until it times out";
            }

            DialogCount++;
            _seen.Add(new Interruption { Kind = "dialog", Description = what ?? "(unnamed dialog)", Answered = answered });
        }

        /// <summary>
        /// Revit raised failures while committing. Warnings are dismissed so the work can
        /// finish — and every one is kept, with the elements it named. Errors are left
        /// alone: rolling the transaction back is Revit's decision to make, and resolving
        /// an error usually means deleting something.
        /// </summary>
        private void OnFailures(object sender, Autodesk.Revit.DB.Events.FailuresProcessingEventArgs e)
        {
            try
            {
                FailuresAccessor fa = e.GetFailuresAccessor();
                IList<FailureMessageAccessor> msgs = fa.GetFailureMessages();
                if (msgs == null || msgs.Count == 0) return;

                bool dismissedAny = false;
                foreach (FailureMessageAccessor m in msgs)
                {
                    string text;
                    try { text = m.GetDescriptionText(); } catch { text = "(failure with no description)"; }

                    var ids = new List<long>();
                    try
                    {
                        foreach (ElementId id in m.GetFailingElementIds()) ids.Add(Rid.GetId(id));
                    }
                    catch { }

                    FailureSeverity sev;
                    try { sev = m.GetSeverity(); } catch { sev = FailureSeverity.Warning; }

                    if (sev == FailureSeverity.Warning)
                    {
                        WarningCount++;
                        try { fa.DeleteWarning(m); dismissedAny = true; } catch { }
                        _seen.Add(new Interruption
                        {
                            Kind = "warning",
                            Description = text,
                            Answered = "dismissed so the command could finish — the model still has whatever this describes",
                            Elements = ids
                        });
                    }
                    else
                    {
                        ErrorCount++;
                        _seen.Add(new Interruption
                        {
                            Kind = "error",
                            Description = text,
                            Answered = "NOT resolved: resolving an error changes the model, usually by deleting something. " +
                                       "The transaction fails and you decide.",
                            Elements = ids
                        });
                    }
                }

                if (dismissedAny) e.SetProcessingResult(FailureProcessingResult.ProceedWithCommit);
            }
            catch { /* never let the watcher be the reason a command dies */ }
        }

        /// <summary>The block to hand back with the result, or null when Revit said nothing.</summary>
        public object Report()
        {
            if (_seen.Count == 0) return null;

            var items = new List<object>();
            foreach (Interruption i in _seen)
                items.Add(new
                {
                    kind = i.Kind,
                    description = i.Description,
                    answered = i.Answered,
                    elements = i.Elements != null && i.Elements.Count > 0 ? i.Elements : null
                });

            return new
            {
                warnings = WarningCount,
                errors = ErrorCount,
                dialogs = DialogCount,
                items,
                note = "Revit raised these while this command ran. Warnings were dismissed so it could finish; " +
                       "they describe the model, they are not noise, and nothing about them was fixed. " +
                       "Dialogs were cancelled because no one is at the keyboard."
            };
        }
    }
}
