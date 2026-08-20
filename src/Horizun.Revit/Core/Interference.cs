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
    // Interruption, and the shaping of what Revit raised into the answer a caller
    // reads, live in RaisedRecord.cs - Revit-free, so the two rules that matter
    // (an unobserved run is not a quiet one; the since-window attributes a dialog
    // to the right model) are unit-tested without a Revit. This file CATCHES.
    //
    // DialogAnswer lives in OpenDialogPolicy.cs for the same reason; this file
    // only ACTS on it.

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

        /// <summary>
        /// THE WATCHER OF THE COMMAND RUNNING RIGHT NOW, so a command can read what
        /// Revit has raised so far INSIDE itself (5.25).
        ///
        /// Until this existed, everything here was written for the reply and only for
        /// the reply: the dispatcher attached the report after the command returned. For
        /// a batch driver that opens 250 models in one execute_python call, that is the
        /// wrong end - the script writes its per-model verdict DURING the run, so a
        /// cancelled dialog reached it as nothing but Revit's "Opening was canceled" and
        /// three models were reported unauditable with no cause, for the second month
        /// running. A script that can read the list can attribute each dialog to the
        /// model it was raised on, which no amount of after-the-fact reporting can do.
        ///
        /// [ThreadStatic] for the same reason as Job.Ambient: commands run one at a time
        /// on Revit's UI thread, and this is set and cleared around exactly one of them.
        /// </summary>
        [ThreadStatic] private static Interference _current;

        /// <summary>The watcher for the command on this thread, or null when there is none.</summary>
        public static Interference Current => _current;

        private readonly UIApplication _app;
        private readonly List<Interruption> _seen = new List<Interruption>();
        private readonly ObservationCoverage _coverage = new ObservationCoverage();
        private bool _off;

        public bool DialogsSubscribed => _coverage.DialogsSubscribed;
        public bool FailuresSubscribed => _coverage.FailuresSubscribed;
        public bool DialogsProcessingComplete => _coverage.DialogsProcessingComplete;
        public bool FailuresProcessingComplete => _coverage.FailuresProcessingComplete;
        public bool DialogsObserved => DialogsSubscribed && DialogsProcessingComplete;
        public bool FailuresObserved => FailuresSubscribed && FailuresProcessingComplete;
        public bool FullyObserved => _coverage.FullyObserved;

        public IList<Interruption> Seen => _seen;
        public int WarningCount { get; private set; }
        public int ErrorCount { get; private set; }
        public int DialogCount { get; private set; }

        /// <summary>
        /// Asked at the instant Revit raises something, for the Interruption's While.
        /// Set by a command that knows where it is - execute_python points it at the
        /// script's last checkpoint. Never called for its side effects, and a throwing
        /// locator costs nothing but the field.
        /// </summary>
        public Func<string> Locator { get; set; }

        public Interference(UIApplication app)
        {
            _app = app;
            // Independent subscriptions and independent facts. One failed subscription
            // must not prevent attempting the other, and a partially watched run must
            // never be rendered as fully observed merely because a watcher object exists.
            try { _app.DialogBoxShowing += OnDialog; _coverage.SetSubscribed("dialogs", true); }
            catch { _coverage.SetSubscribed("dialogs", false); }
            try { _app.Application.FailuresProcessing += OnFailures; _coverage.SetSubscribed("failures", true); }
            catch { _coverage.SetSubscribed("failures", false); }
            _current = this;
        }

        public void Dispose()
        {
            if (_off) return;
            _off = true;
            if (DialogsSubscribed) try { _app.DialogBoxShowing -= OnDialog; } catch { }
            if (FailuresSubscribed) try { _app.Application.FailuresProcessing -= OnFailures; } catch { }
            // Only if it is still ours: clearing another watcher's slot would leave the
            // running command unable to see its own dialogs.
            if (ReferenceEquals(_current, this)) _current = null;
        }

        private string Where()
        {
            Func<string> locator = Locator;
            if (locator == null) return null;
            try { return locator(); } catch { return null; }
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
            _seen.Add(new Interruption
            {
                Kind = "dialog",
                Description = what ?? "(unnamed dialog)",
                Answered = answered,
                While = Where()
            });
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
                            Elements = ids,
                            While = Where()
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
                            Elements = ids,
                            While = Where()
                        });
                    }
                }

                if (dismissedAny) e.SetProcessingResult(FailureProcessingResult.ProceedWithCommit);
            }
            catch (Exception ex)
            {
                // Sticky degradation. Subscription success does not make a handler that
                // threw complete; later callers must not read an empty/partial list as all
                // failures observed.
                _coverage.MarkProcessingFailure("failures", ex.GetType().Name + ": " + ex.Message);
                _seen.Add(new Interruption
                {
                    Kind = "observer_error",
                    Description = "Failure-processing observer threw: " + ex.GetType().Name + ": " + ex.Message,
                    Answered = "observation coverage degraded; this run is NOT fully observed",
                    While = Where()
                });
            }
        }

        /// <summary>The block to hand back with the result, or null when Revit said nothing.</summary>
        public object Report()
        {
            // Preserve the compact null for a genuinely quiet, fully watched command.
            // With partial observation, however, an empty list is not evidence of quiet:
            // publish the coverage facts even when the subscribed channel saw nothing.
            if (_seen.Count == 0 && FullyObserved) return null;

            var items = new List<object>();
            foreach (Interruption i in _seen) items.Add(RaisedRecord.Describe(i));

            return new
            {
                observation_complete = FullyObserved,
                dialogs_subscribed = DialogsSubscribed,
                failures_subscribed = FailuresSubscribed,
                dialogs_processing_complete = DialogsProcessingComplete,
                failures_processing_complete = FailuresProcessingComplete,
                dialogs_observed = DialogsObserved,
                failures_observed = FailuresObserved,
                observer_errors = new Newtonsoft.Json.Linq.JArray(_coverage.ObserverErrors),
                warnings = WarningCount,
                errors = ErrorCount,
                dialogs = DialogCount,
                items,
                note = FullyObserved
                    ? "Revit raised these while this command ran. Warnings were dismissed so it could finish; " +
                      "they describe the model, they are not noise, and nothing about them was fixed. " +
                      "Dialogs were cancelled because no one is at the keyboard. 'while' is the running " +
                      "command's own statement of where it was (for a script, its last checkpoint) - the " +
                      "bridge cannot see which API call was in flight and does not pretend to."
                    : "Observation was PARTIAL: dialogs_observed=" + DialogsObserved +
                      ", failures_observed=" + FailuresObserved + ". Items cover only subscribed channels; " +
                      "an empty list is not evidence that Revit raised nothing on the unobserved channel."
            };
        }

        /// <summary>
        /// Everything raised from index <paramref name="since"/> onwards, in order.
        ///
        /// The INDEX is the contract: a script that records the count before an open and
        /// asks again after it gets exactly the interruptions that open produced, which
        /// is the attribution a 250-model batch needs and a whole-run list cannot give.
        /// </summary>
        public Newtonsoft.Json.Linq.JArray Since(int since) => RaisedRecord.Window(_seen, since);

        /// <summary>How many have been raised so far. The handle for Since().</summary>
        public int Count => _seen.Count;

        /// <summary>
        /// The dialogs/failures block for a command that wants it INSIDE its own payload,
        /// where a program reading structuredContent will find it. A null watcher answers
        /// the same shape with observed=false, so a caller never has to know whether one
        /// existed to be told that nothing was watched.
        /// </summary>
        public static Newtonsoft.Json.Linq.JObject BlockOf(Interference watch)
        {
            Newtonsoft.Json.Linq.JObject block = RaisedRecord.Block(
                watch == null ? null : watch.Seen,
                watch != null && watch.FullyObserved);
            block["dialogs_observed"] = watch != null && watch.DialogsObserved;
            block["failures_observed"] = watch != null && watch.FailuresObserved;
            block["observation_complete"] = watch != null && watch.FullyObserved;
            block["dialogs_subscribed"] = watch != null && watch.DialogsSubscribed;
            block["failures_subscribed"] = watch != null && watch.FailuresSubscribed;
            block["dialogs_processing_complete"] = watch != null && watch.DialogsProcessingComplete;
            block["failures_processing_complete"] = watch != null && watch.FailuresProcessingComplete;
            block["observer_errors"] = watch == null
                ? new Newtonsoft.Json.Linq.JArray()
                : new Newtonsoft.Json.Linq.JArray(watch._coverage.ObserverErrors);
            block["observation_note"] = watch == null
                ? "No watcher existed for this run; dialogs and failures were not observed."
                : (watch.FullyObserved
                    ? "Both Revit dialog and failure-processing channels were observed."
                    : "Observation was PARTIAL: dialogs_observed=" + watch.DialogsObserved +
                      ", failures_observed=" + watch.FailuresObserved + ". An empty list is not evidence that " +
                      "nothing was raised on an unobserved channel.");
            return block;
        }
    }
}
