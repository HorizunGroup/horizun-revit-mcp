// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// horizun_execute_python read_only=true: the script runs inside a TransactionGroup
// that is ALWAYS rolled back, so an analysis script cannot leave a mark on the
// model. Two things this file exists to be honest about:
//
//   1. A TransactionGroup rollback undoes MODEL writes. It does nothing to a call
//      that writes bytes directly - Document.Save/SaveAs, closing or opening a
//      document - because those happen OUTSIDE any transaction. A script that
//      calls Save() inside a "read-only" run would produce a REAL file on disk
//      that no rollback can take back. So these calls are refused BEFORE the
//      script runs, the same way an unsupported syntax is refused at preflight -
//      not caught afterwards and apologised for.
//
//   2. This is a TEXTUAL SCAN over the masked source (comments/strings blanked,
//      the same technique TypedOverlaps uses in ExecutePythonCommand.cs), not a
//      sandbox. It matches the literal Revit API method names and can be evaded
//      by anyone who tries: getattr(doc, 'Sa' + 've')() sails straight past it.
//      It exists to stop the ORDINARY case - a script that forgot read_only
//      changes nothing about what Save() does - not a hostile one. The honest
//      ceiling is stated on the tool and in the refusal message, not hidden.
//
// Revit-free: pure string matching and pure comparison, both provable in CI
// without a Revit in the room.
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Horizun.Revit.Core
{
    public static class ReadOnlyPythonGuard
    {
        /// <summary>
        /// Calls that write outside any transaction and therefore escape a
        /// TransactionGroup.RollBack(). Matched against MASKED source (comments and
        /// string literals blanked) so a docstring or a comment mentioning "Save" does
        /// not trip the guard - the same false-positive class TypedOverlaps was
        /// hardened against.
        /// </summary>
        private static readonly (Regex Pattern, string Api)[] Forbidden =
        {
            (new Regex(@"\.\s*SaveAs\s*\(", RegexOptions.Compiled), "Document.SaveAs()"),
            (new Regex(@"\.\s*Save\s*\(", RegexOptions.Compiled), "Document.Save()"),
            (new Regex(@"\.\s*Close\s*\(", RegexOptions.Compiled), "Document.Close()"),
            (new Regex(@"\bOpenDocumentFile\s*\(", RegexOptions.Compiled), "Application.OpenDocumentFile()"),
            (new Regex(@"\bOpenAndActivateDocument\s*\(", RegexOptions.Compiled), "UIApplication.OpenAndActivateDocument()"),
            (new Regex(@"\bNewProjectDocument\s*\(", RegexOptions.Compiled), "Application.NewProjectDocument()"),
            // Worksharing and link loading act on files and servers no rollback reaches
            // (review 2026-09-26): a sync to central or a relinquish is not undone by
            // rolling the group back.
            (new Regex(@"\bSynchronizeWithCentral\s*\(", RegexOptions.Compiled), "Document.SynchronizeWithCentral()"),
            (new Regex(@"\bRelinquishOwnership\s*\(", RegexOptions.Compiled), "WorksharingUtils.RelinquishOwnership()"),
            (new Regex(@"\.\s*(Unload|Reload|LoadFrom|ReloadFrom)\s*\(", RegexOptions.Compiled), "RevitLinkType/CADLinkType load state"),
        };

        /// <summary>Every forbidden API this masked source mentions, in table order. Empty when none.</summary>
        public static List<string> Violations(string maskedCode)
        {
            var hits = new List<string>();
            if (string.IsNullOrEmpty(maskedCode)) return hits;
            foreach (var f in Forbidden)
                if (f.Pattern.IsMatch(maskedCode)) hits.Add(f.Api);
            return hits;
        }

        /// <summary>
        /// Did the model actually stay put after the read-only TransactionGroup rolled
        /// back? Compares Document.IsModified and a cheap element/type census taken
        /// before the group opened and after it rolled back. A measurement that could
        /// not be read on either side is reported as UNMEASURED, never silently treated
        /// as "unchanged" - the whole point of read_only is that its guarantee is
        /// checked, not assumed.
        /// </summary>
        public static ReadOnlyOutcome CompareCensus(
            bool? modifiedBefore, bool? modifiedAfter,
            int? instancesBefore, int? instancesAfter,
            int? typesBefore, int? typesAfter)
        {
            var problems = new List<string>();
            if (modifiedBefore == null || modifiedAfter == null)
                problems.Add("Document.IsModified could not be read on one side, so it is UNMEASURED.");
            else if (modifiedBefore != modifiedAfter)
                problems.Add("Document.IsModified changed (" + modifiedBefore + " -> " + modifiedAfter + ").");

            if (instancesBefore == null || instancesAfter == null || typesBefore == null || typesAfter == null)
                problems.Add("the element/type census could not be read on one side, so it is UNMEASURED.");
            else if (instancesBefore != instancesAfter || typesBefore != typesAfter)
                problems.Add("the element/type census changed (instances " + instancesBefore + " -> " + instancesAfter +
                              ", types " + typesBefore + " -> " + typesAfter + ").");

            if (problems.Count == 0)
                return new ReadOnlyOutcome { Ok = true, Reason = "Document.IsModified and the element/type census agree before and after the rollback." };
            return new ReadOnlyOutcome { Ok = false, Reason = string.Join(" ", problems) };
        }
    }

    public sealed class ReadOnlyOutcome
    {
        public bool Ok;
        public string Reason;
    }
}
