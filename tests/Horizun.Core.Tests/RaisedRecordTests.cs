// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// What Revit raised, as a record a batch can act on (5.25).
//
// Two properties, and both of them are about a wrong answer that would be silent:
//
//   1. An unobserved run must not read like a quiet one. Both produce zero
//      dialogs; only one of them is evidence.
//   2. The since-window must return the interruptions raised AFTER the caller's
//      bookmark and no others. Off by one, and a cancelled dialog is attributed
//      to the previous model - a wrong answer with a confident shape, in a
//      report somebody sends to a designer.
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class RaisedRecordTests
    {
        [Fact]
        public void A_subscribed_failure_channel_that_throws_is_not_fully_observed()
        {
            var coverage = new ObservationCoverage();
            coverage.SetSubscribed("dialogs", true);
            coverage.SetSubscribed("failures", true);
            Assert.True(coverage.FullyObserved);

            coverage.MarkProcessingFailure("failures", "simulated handler failure");

            Assert.True(coverage.FailuresSubscribed);
            Assert.False(coverage.FailuresProcessingComplete);
            Assert.False(coverage.FullyObserved);
            Assert.Single(coverage.ObserverErrors);
            Assert.Contains("simulated handler failure", coverage.ObserverErrors[0]);
        }

        private static Interruption Dialog(string what, string where = null)
            => new Interruption
            {
                Kind = "dialog",
                Description = what,
                Answered = "cancelled by the bridge (nobody is at the keyboard to answer it)",
                While = where
            };

        private static Interruption Warning(string what, params long[] ids)
            => new Interruption
            {
                Kind = "warning",
                Description = what,
                Answered = "dismissed so the command could finish",
                Elements = new List<long>(ids)
            };

        [Fact]
        public void A_run_nobody_watched_is_not_reported_as_a_run_with_nothing_to_report()
        {
            JObject unwatched = RaisedRecord.Block(null, observed: false);
            JObject quiet = RaisedRecord.Block(new List<Interruption>(), observed: true);

            // Same empty lists...
            Assert.Empty((JArray)unwatched["dialogs"]);
            Assert.Empty((JArray)quiet["dialogs"]);
            // ...and the field that tells them apart.
            Assert.False((bool)unwatched["revit_raised_observed"]);
            Assert.True((bool)quiet["revit_raised_observed"]);
            Assert.Contains("NOT OBSERVED", (string)unwatched["revit_raised_note"]);
            Assert.DoesNotContain("NOT OBSERVED", (string)quiet["revit_raised_note"]);
        }

        [Fact]
        public void Dialogs_and_failures_are_separated_because_they_mean_different_things()
        {
            var seen = new List<Interruption>
            {
                Dialog("Dialog_Revit_DocWarnDialog"),
                Warning("Duplicate marks", 12345),
                new Interruption { Kind = "error", Description = "Cannot delete" }
            };

            JObject block = RaisedRecord.Block(seen, observed: true);

            Assert.Single((JArray)block["dialogs"]);
            Assert.Equal(2, ((JArray)block["failures"]).Count);
            Assert.Equal("Dialog_Revit_DocWarnDialog", (string)block["dialogs"][0]["description"]);
        }

        [Fact]
        public void The_cancelled_open_dialog_is_named_which_is_the_whole_story()
        {
            // The batch case: three models "could not be audited" for a month, because
            // all the script ever saw was Revit's own "Opening was canceled".
            JObject block = RaisedRecord.Block(
                new List<Interruption> { Dialog("Dialog_Revit_DocWarnDialog", "CMP-PMD-SLC-ARR-INT-MOD-001") },
                observed: true);

            JToken d = block["dialogs"][0];
            Assert.Equal("Dialog_Revit_DocWarnDialog", (string)d["description"]);
            Assert.Contains("cancelled by the bridge", (string)d["answered"]);
            Assert.Equal("CMP-PMD-SLC-ARR-INT-MOD-001", (string)d["while"]);
        }

        [Fact]
        public void The_since_window_returns_exactly_what_was_raised_after_the_bookmark()
        {
            var seen = new List<Interruption> { Dialog("first"), Dialog("second"), Dialog("third") };

            // What a driver does: len(revit_raised()) before the open, then ask again.
            JArray afterTwo = RaisedRecord.Window(seen, 2);

            Assert.Single(afterTwo);
            Assert.Equal("third", (string)afterTwo[0]["description"]);
            Assert.Equal(3, RaisedRecord.Window(seen, 0).Count);
        }

        [Fact]
        public void A_bookmark_past_the_end_yields_nothing_and_a_negative_one_yields_everything()
        {
            var seen = new List<Interruption> { Dialog("only") };

            Assert.Empty(RaisedRecord.Window(seen, 9));
            Assert.Single(RaisedRecord.Window(seen, -3));
            Assert.Empty(RaisedRecord.Window(null, 0));
        }

        [Fact]
        public void Elements_and_while_are_explicit_nulls_never_missing_keys()
        {
            // A reader must be able to tell "Revit named no elements" from "this shape
            // does not carry that field".
            JObject d = RaisedRecord.Describe(Dialog("Dialog_Revit_DocWarnDialog"));

            Assert.Equal(JTokenType.Null, d["elements"].Type);
            Assert.Equal(JTokenType.Null, d["while"].Type);

            JObject w = RaisedRecord.Describe(Warning("Duplicate marks", 7, 8));
            Assert.Equal(new long[] { 7, 8 }, w["elements"].ToObject<long[]>());
        }

        [Fact]
        public void The_note_tells_a_caller_how_to_get_past_a_dialog_and_how_to_attribute_one()
        {
            string note = (string)RaisedRecord.Block(new List<Interruption>(), observed: true)["revit_raised_note"];

            Assert.Contains("Opening was canceled", note);
            Assert.Contains("revit_raised(since)", note);
            Assert.Contains("dialog_answer('dismiss')", note);
            // And it must not let 'while' be read as a bridge inference.
            Assert.Contains("cannot see which API call was in flight", note);
        }
    }
}
