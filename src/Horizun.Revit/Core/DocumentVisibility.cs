// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// Reading how much of a model is actually loaded. The half that needs Revit.
//
// The whole measurement is two numbers - how many user worksets there are, and how
// many are open - and the reason it is worth its own file is that getting them is
// the ONLY way to know. There is no other signal. A closed workset's elements are
// absent from the document rather than filtered out of it, so nothing downstream
// can detect them: not a count, not an exception, not a comparison against a total,
// because the total is measured over the same partial model.
//
// See DocumentVisibilityCoverage.cs for what the numbers mean and every sentence
// this reports; that half carries no Revit and is where the states are tested.
// -----------------------------------------------------------------------------
using System;
using Autodesk.Revit.DB;

namespace Horizun.Revit.Core
{
    public static class DocumentVisibility
    {
        /// <summary>
        /// Measure a document's coverage. Never throws: a command that cannot report
        /// coverage must still report its findings, with coverage stated as unknown -
        /// which this reports as INCOMPLETE, because a scan that cannot say what it saw
        /// has not earned the benefit of the doubt.
        /// </summary>
        public static DocumentVisibilityCoverage Measure(Document doc)
        {
            if (doc == null) return DocumentVisibilityCoverage.Unreadable("there is no document to measure");

            bool workshared;
            try { workshared = doc.IsWorkshared; }
            catch (Exception ex)
            {
                return DocumentVisibilityCoverage.Unreadable("Document.IsWorkshared threw: " + ex.Message);
            }

            // A single-user model has no user worksets and loads all of itself. The
            // question genuinely does not arise, which is different from arising and
            // being answered "fine".
            if (!workshared) return DocumentVisibilityCoverage.NotWorkshared();

            try
            {
                int total = 0, open = 0;
                foreach (Workset w in new FilteredWorksetCollector(doc).OfKind(WorksetKind.UserWorkset))
                {
                    total++;
                    // IsOpen is the only thing that says whether this workset's elements
                    // are in the document. A workset that is merely not VISIBLE in a view
                    // is a different thing entirely and does not belong here.
                    if (w.IsOpen) open++;
                }
                return DocumentVisibilityCoverage.From(total, open);
            }
            catch (Exception ex)
            {
                return DocumentVisibilityCoverage.Unreadable("the workset collector threw: " + ex.Message);
            }
        }
    }
}
