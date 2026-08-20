// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// WHAT REVIT RAISED, as a record a program can read (story 5.25).
//
// Interference.cs catches the warnings, errors and modal dialogs - that needs a
// Revit. Turning them into the answer a caller reads does not, and the two rules
// worth proving are exactly the ones a live Revit will not produce on demand:
//
//   1. AN EMPTY LIST FROM A WATCHER THAT NEVER SUBSCRIBED IS NOT A QUIET RUN.
//      Both come back as zero dialogs. One means "Revit raised nothing", the
//      other means "nobody was looking", and this codebase does not spell the
//      second like the first. Hence `observed`, and a note that says which.
//
//   2. THE SINCE-WINDOW. A batch that opens 250 models in one script gets one
//      list at the end, with nothing tying a cancelled dialog to a model. The
//      script can instead record the count before an open and ask for everything
//      after that index, which attributes each dialog exactly. Off-by-one here
//      would silently attribute a dialog to the wrong model - the quietest wrong
//      answer this feature could produce - so the arithmetic lives where it can
//      be pinned.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    /// <summary>One thing Revit raised during a command, and what happened to it.</summary>
    public sealed class Interruption
    {
        public string Kind;          // "warning" | "error" | "dialog"
        public string Description;
        public string Answered;      // what we did about it
        public List<long> Elements;  // ids Revit blamed, when it named any

        /// <summary>
        /// WHAT THE COMMAND SAID IT WAS DOING when this was raised, asked at that
        /// instant - for execute_python, the script's own last checkpoint() label.
        ///
        /// It is the honest form of the field a batch really wants ("which model was
        /// this?"). The bridge cannot see which API call is in flight, so it does not
        /// pretend to: it publishes the running script's own most recent statement
        /// about where it is, and null when the script never made one.
        /// </summary>
        public string While;
    }

    /// <summary>
    /// Subscription is only admission to an observation channel. Processing remains a
    /// separate sticky fact: if an event handler throws once, the run is incomplete even
    /// when the subscription itself succeeded and later events happen to work.
    /// </summary>
    public sealed class ObservationCoverage
    {
        private readonly List<string> _errors = new List<string>();
        public bool DialogsSubscribed { get; private set; }
        public bool FailuresSubscribed { get; private set; }
        public bool DialogsProcessingComplete { get; private set; } = true;
        public bool FailuresProcessingComplete { get; private set; } = true;
        public bool FullyObserved => DialogsSubscribed && FailuresSubscribed &&
                                     DialogsProcessingComplete && FailuresProcessingComplete;
        public IList<string> ObserverErrors => _errors.AsReadOnly();

        public void SetSubscribed(string channel, bool subscribed)
        {
            if (string.Equals(channel, "dialogs", StringComparison.Ordinal)) DialogsSubscribed = subscribed;
            else if (string.Equals(channel, "failures", StringComparison.Ordinal)) FailuresSubscribed = subscribed;
            else throw new ArgumentException("Unknown observation channel: " + channel, nameof(channel));
        }

        public void MarkProcessingFailure(string channel, string error)
        {
            if (string.Equals(channel, "dialogs", StringComparison.Ordinal)) DialogsProcessingComplete = false;
            else if (string.Equals(channel, "failures", StringComparison.Ordinal)) FailuresProcessingComplete = false;
            else throw new ArgumentException("Unknown observation channel: " + channel, nameof(channel));
            _errors.Add(channel + ": " + (string.IsNullOrWhiteSpace(error) ? "unknown observer failure" : error));
        }
    }

    public static class RaisedRecord
    {
        public const string DialogKind = "dialog";

        /// <summary>One interruption, in the single shape every surface reports it in.</summary>
        public static JObject Describe(Interruption i)
        {
            if (i == null) return null;
            var o = new JObject
            {
                ["kind"] = i.Kind,
                ["description"] = i.Description,
                ["answered"] = i.Answered
            };
            // Explicit nulls, not absent keys: a reader must be able to tell "Revit named
            // no elements" from a field this shape does not have.
            o["elements"] = i.Elements != null && i.Elements.Count > 0
                ? new JArray(i.Elements.ToArray())
                : (JToken)JValue.CreateNull();
            o["while"] = i.While == null ? (JToken)JValue.CreateNull() : i.While;
            return o;
        }

        /// <summary>
        /// Everything from index <paramref name="since"/> onwards, in order. A negative
        /// index is read as 0 and an index past the end yields nothing - a script that
        /// mislays its bookmark gets too much or nothing, never somebody else's dialog.
        /// </summary>
        public static JArray Window(IList<Interruption> all, int since)
        {
            var items = new JArray();
            if (all == null) return items;
            if (since < 0) since = 0;
            for (int i = since; i < all.Count; i++) items.Add(Describe(all[i]));
            return items;
        }

        public const string NotObservedNote =
            "NOT OBSERVED. The bridge could not subscribe to Revit's dialog and failure events for this run, so " +
            "these lists are empty because nothing was watched - NOT because Revit was quiet. Do not read them " +
            "as evidence of a clean run.";

        public const string ObservedNote =
            "Everything Revit raised while this script ran. A dialog means Revit stopped for a human and the " +
            "bridge answered it - by default Cancel, which is why an open comes back to the script as 'Opening " +
            "was canceled' with no other clue. 'while' is the script's OWN last checkpoint() label at that " +
            "instant: the bridge cannot see which API call was in flight and does not pretend to, so checkpoint " +
            "per item if you want each dialog attributed. From inside the script, revit_raised(since) returns " +
            "the same records DURING the run - take len(revit_raised()) before an open and pass it back after " +
            "to get exactly what that open raised. To let an open continue past its dialog instead of " +
            "cancelling: `with dialog_answer('dismiss'): doc = app.OpenDocumentFile(...)` - around THAT call only.";

        /// <summary>
        /// The block that travels beside a script's __output__: dialogs (a modal the
        /// bridge answered) apart from failures (Revit's warnings and errors), plus the
        /// one field that keeps an empty answer honest.
        /// </summary>
        public static JObject Block(IList<Interruption> seen, bool observed)
        {
            var dialogs = new JArray();
            var failures = new JArray();

            if (seen != null)
            {
                foreach (Interruption i in seen)
                {
                    if (i == null) continue;
                    if (string.Equals(i.Kind, DialogKind, StringComparison.Ordinal)) dialogs.Add(Describe(i));
                    else failures.Add(Describe(i));
                }
            }

            return new JObject
            {
                ["dialogs"] = dialogs,
                ["failures"] = failures,
                ["revit_raised_observed"] = observed,
                ["revit_raised_note"] = observed ? ObservedNote : NotObservedNote
            };
        }
    }
}
