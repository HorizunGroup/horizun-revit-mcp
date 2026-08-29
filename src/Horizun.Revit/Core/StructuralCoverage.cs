// -----------------------------------------------------------------------------
// Horizun Revit MCP - the difference between "none" and "I could not look".
// Original Horizun code, no Revit types.
//
// A structural query returns numbers people order steel with. The most dangerous
// answer such a query can give is ZERO when the truth is "this could not be
// measured": a bar count of zero reads as a host with no reinforcement, and a
// host with no reinforcement reads as a design decision rather than a blind spot.
//
// So every answer carries one of five words, and only one of them means the
// number beside it is the whole truth.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public static class StructuralCoverage
    {
        /// <summary>Everything asked for was measured. The numbers are the whole answer.</summary>
        public const string Complete = "complete";
        /// <summary>Some of it was measured and some was not. The numbers are a floor, never a total.</summary>
        public const string Partial = "partial";
        /// <summary>This Revit cannot answer it at all - the API is absent in this year.</summary>
        public const string Unavailable = "unavailable";
        /// <summary>The API exists and the model would not give an answer: a null, a throw, a closed workset.</summary>
        public const string Unreadable = "unreadable";
        /// <summary>The question does not apply here - a cover on a host that cannot be reinforced.</summary>
        public const string NotApplicable = "not_applicable";

        public static readonly string[] All =
        {
            Complete, Partial, Unavailable, Unreadable, NotApplicable
        };

        public static bool IsKnown(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return false;
            for (int i = 0; i < All.Length; i++)
                if (string.Equals(All[i], s, StringComparison.Ordinal)) return true;
            return false;
        }

        /// <summary>True only for <see cref="Complete"/>. There is no second word that means yes.</summary>
        public static bool IsWholeTruth(string s)
        {
            return string.Equals(s, Complete, StringComparison.Ordinal);
        }

        /// <summary>
        /// The weakest coverage in a set decides the set. One unreadable host in
        /// four hundred makes the total partial, because it is.
        /// </summary>
        public static string Weakest(IEnumerable<string> values)
        {
            if (values == null) return Complete;
            bool any = false, sawPartial = false, sawUnreadable = false, sawUnavailable = false,
                 sawComplete = false, sawNotApplicable = false;
            foreach (string v in values)
            {
                any = true;
                // A WORD THAT IS NOT IN THE VOCABULARY IS NOT SILENTLY DROPPED.
                // It used to fall through every branch, so Weakest(["unreadible"]) -
                // a typo - and Weakest([null]) both answered not_applicable, which
                // says the question did not arise. Declare validates its input and
                // throws; this now holds the same line.
                if (!IsKnown(v))
                    throw new ArgumentException(
                        "coverage must be one of " + string.Join(", ", All) + " - got '" + (v ?? "null") + "'.");
                if (v == Partial) sawPartial = true;
                else if (v == Unreadable) sawUnreadable = true;
                else if (v == Unavailable) sawUnavailable = true;
                else if (v == Complete) sawComplete = true;
                else sawNotApplicable = true;
                // not_applicable does not weaken anything: a question that does not
                // arise was not left unanswered.
            }
            if (!any) return Complete;

            // PARTIAL MEANS SOMETHING WAS MEASURED AND SOMETHING WAS NOT. It used to
            // come back for unavailable + unreadable, where NOTHING was measured -
            // and its published meaning is "every count is a FLOOR", which a reader
            // takes as "at least this many were found". Nothing was found, because
            // nothing was looked at.
            bool measuredSomething = sawComplete || sawPartial;
            if (!measuredSomething)
            {
                if (sawUnreadable) return Unreadable;
                if (sawUnavailable) return Unavailable;
                return sawNotApplicable ? NotApplicable : Complete;
            }
            if (sawUnavailable || sawUnreadable || sawPartial) return Partial;
            return Complete;
        }

        /// <summary>
        /// The block published beside every count. `means` is not decoration: a
        /// caller reading `partial` has to know that the number under it is a floor.
        /// </summary>
        public static JObject Declare(string coverage, int measured, int notMeasured, JArray reasons = null)
        {
            if (!IsKnown(coverage))
                throw new ArgumentException("coverage must be one of " + string.Join(", ", All) + " - got '" + coverage + "'.");
            return new JObject
            {
                ["coverage"] = coverage,
                ["measured"] = measured,
                ["not_measured"] = notMeasured,
                ["is_whole_truth"] = IsWholeTruth(coverage),
                ["reasons"] = reasons ?? new JArray(),
                ["means"] =
                    "complete: every item asked about was measured and the counts are totals. " +
                    "partial: some were not, so every count is a FLOOR and never a total. " +
                    "unavailable: this Revit version has no API for the question. " +
                    "unreadable: the API exists and the model would not answer - a null, a throw, a closed " +
                    "workset. not_applicable: the question does not arise for these elements. " +
                    "Only complete means the number beside it is the whole answer; zero under any other " +
                    "word means nothing was found AND something was not looked at."
            };
        }

        /// <summary>One row explaining why a particular thing was not measured.</summary>
        public static JObject Reason(string what, string why, long? elementId = null)
        {
            var o = new JObject { ["what"] = what, ["why"] = why };
            if (elementId.HasValue) o["element_id"] = elementId.Value;
            return o;
        }
    }
}
