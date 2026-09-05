// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// HOW MUCH OF THE MODEL THE ANSWER IS ABOUT.
//
// A closed workset is not a workset whose elements are hidden. Its elements are
// NOT IN THE DOCUMENT: Revit never loaded them, so a FilteredElementCollector does
// not skip them, it never sees them at all. There is no flag on the result, no
// exception, and no count that comes back short - because "short" is relative to a
// total that is itself measured over the same partial model.
//
// Every read this bridge offers is therefore capable of the same failure, in the
// same shape: model_scan reports 0 imported CAD instances, audit_model reports no
// in-place families, quantities totals 4,200 m3 of concrete, clash finds nothing.
// All four are true statements about what got loaded, presented as statements about
// the building. The caller has no way to tell the difference, and neither did we.
//
// This is measured once and travels with every one of those answers. Two numbers do
// the work: how many user worksets exist, and how many of them are open. When they
// differ, coverage_complete is FALSE, and it means exactly one thing - do not read
// any absence in this reply as evidence of absence in the model.
//
// UNREADABLE IS NOT COMPLETE. If the workset list could not be read, coverage is
// incomplete too. Of the two ways to be wrong, "we may have missed something" costs
// a re-run and "we saw everything" costs a decision made on a model nobody saw.
//
// The arithmetic and the sentences are here, Revit-free, so every state can be
// tested - including the one a real Revit will not produce on request, which is the
// workset collector throwing. DocumentVisibility.cs does the reading.
// -----------------------------------------------------------------------------
using System;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public sealed class DocumentVisibilityCoverage
    {
        /// <summary>False for a single-user model, where the question does not arise.</summary>
        public bool IsWorkshared { get; private set; }

        /// <summary>User worksets in the model. Null when the list could not be read.</summary>
        public int? WorksetsTotal { get; private set; }

        /// <summary>Of those, how many are OPEN - the only ones whose elements are loaded.</summary>
        public int? WorksetsOpen { get; private set; }

        /// <summary>The rest. Their elements are not in the document at all.</summary>
        public int? WorksetsClosed { get; private set; }

        /// <summary>Why the worksets could not be read, when they could not. Null otherwise.</summary>
        public string ReadError { get; private set; }

        /// <summary>
        /// Is this answer about the whole model? False when any workset is closed, and
        /// false when nobody could find out. A reader that checks nothing else must be
        /// able to check this.
        /// </summary>
        public bool CoverageComplete { get; private set; }

        /// <summary>A single-user model: everything in it is loaded, and that is the answer.</summary>
        public static DocumentVisibilityCoverage NotWorkshared() =>
            new DocumentVisibilityCoverage { IsWorkshared = false, CoverageComplete = true };

        /// <summary>
        /// The worksets could not be listed. Coverage is UNKNOWN, which is reported as
        /// incomplete - a scan that cannot say how much of the model it saw has not
        /// earned the benefit of the doubt.
        /// </summary>
        public static DocumentVisibilityCoverage Unreadable(string why) =>
            new DocumentVisibilityCoverage
            {
                IsWorkshared = true,
                ReadError = string.IsNullOrWhiteSpace(why) ? "no reason given" : why,
                CoverageComplete = false
            };

        public static DocumentVisibilityCoverage From(int total, int open)
        {
            if (total < 0) throw new ArgumentOutOfRangeException(nameof(total));
            if (open < 0 || open > total) throw new ArgumentOutOfRangeException(nameof(open));

            return new DocumentVisibilityCoverage
            {
                IsWorkshared = true,
                WorksetsTotal = total,
                WorksetsOpen = open,
                WorksetsClosed = total - open,
                CoverageComplete = total == open
            };
        }

        /// <summary>
        /// What this means for whoever is reading the numbers beside it. Never "some
        /// worksets are closed" on its own: that is a fact about Revit, and the thing the
        /// caller needs is what it does to their answer.
        /// </summary>
        public string Note()
        {
            if (!IsWorkshared)
                return "This model is not workshared, so every element in it is loaded and this answer covers all " +
                       "of it.";

            if (ReadError != null)
                return "COVERAGE UNKNOWN. The worksets could not be read (" + ReadError + "), so there is no way " +
                       "to say how much of this model was loaded when it was measured. Treat every count here as " +
                       "a lower bound and every absence as unproven. This is reported as incomplete rather than " +
                       "complete because a scan that cannot say what it saw has not earned the benefit of the doubt.";

            if (CoverageComplete)
                return "All " + WorksetsTotal + " workset(s) are open, so every element of this model was loaded " +
                       "and this answer covers all of it.";

            return "INCOMPLETE COVERAGE: " + WorksetsClosed + " of " + WorksetsTotal + " workset(s) are CLOSED. " +
                   "The elements on a closed workset are not hidden - they are NOT IN THE DOCUMENT, so nothing " +
                   "here skipped them, nothing counted them, and no total below is short by a knowable amount. " +
                   "Every count is a count of what was loaded. DO NOT READ AN ABSENCE HERE AS AN ABSENCE IN THE " +
                   "MODEL: 'no imported CAD', 'no clashes', 'no in-place families' and a quantity total are all " +
                   "statements about the open worksets only. Re-open the model with all worksets open and run " +
                   "this again before deciding anything on it.";
        }

        /// <summary>
        /// The block every read-only command carries. One shape, so a caller learns to
        /// look for it once rather than per tool.
        /// </summary>
        /// <summary>
        /// WHAT THIS MEASUREMENT IS WORTH FOR A LINKED DOCUMENT, and it is less than
        /// for the host.
        ///
        /// MEASURED on Revit 2026, 2026-09-04: a link created with
        /// RevitLinkOptions(false, WorksetConfiguration) closing one workset by id
        /// reports, in the LINKED document, every workset IsOpen=true - and the 392
        /// elements of the workset it was asked to close are queryable through the
        /// link, exactly as they are when the same type is reloaded with
        /// OpenAllWorksets. The numbers are identical either way.
        ///
        /// So for a link these two numbers are the linked document's own state as
        /// Revit reports it, and nothing here can confirm the configuration the link
        /// was LOADED with. A takeoff must not read an absence in a link as a closed
        /// workset, and must not claim a link's coverage is incomplete on the
        /// strength of a flag Revit answers open.
        /// </summary>
        public const string LinkedDocumentMeans =
            "for a LINKED document these numbers are the linked model's own workset state as Revit reports it. " +
            "The API exposes no way to read back the WorksetConfiguration a link was loaded with: a link created " +
            "with a workset closed still reports every workset open and still hands over that workset's elements " +
            "(measured, Revit 2026). So an absence inside a link is NOT evidence of a closed workset, and this " +
            "coverage is not evidence that everything the linked model holds was loaded.";

        public JObject ToJson() => new JObject
        {
            ["coverage_complete"] = CoverageComplete,
            ["is_workshared"] = IsWorkshared,
            ["worksets_total"] = WorksetsTotal.HasValue ? (JToken)WorksetsTotal.Value : null,
            ["worksets_open"] = WorksetsOpen.HasValue ? (JToken)WorksetsOpen.Value : null,
            ["worksets_closed"] = WorksetsClosed.HasValue ? (JToken)WorksetsClosed.Value : null,
            ["worksets_read_error"] = ReadError,
            ["note"] = Note()
        };
    }
}
