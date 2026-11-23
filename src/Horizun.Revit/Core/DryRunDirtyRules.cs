// -----------------------------------------------------------------------------
// Horizun Core - original Horizun code.
//
// A REHEARSAL MUST LEAVE THE DOCUMENT AS CLEAN AS IT FOUND IT.
//
// Field report: a dry run of a write left Revit's System Browser view flagged as modified
// - the document became dirty although nothing was written. The rehearsals here commit
// inner transactions inside a TransactionGroup and roll the whole group back, and Revit
// can still mark the document (or a view it owns, the MEP system browser among them) as
// changed when the group closes. That cannot be proven without a live Revit, so the
// dispatcher MEASURES it: Document.IsModified before and after every call whose reply says
// dry_run, and the reply says so when a rehearsal turned a clean document dirty.
// Revit-free.
// -----------------------------------------------------------------------------
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public static class DryRunDirtyRules
    {
        /// <summary>
        /// The block to attach to a reply, or null when there is nothing to say: not a dry run, the document was
        /// already dirty, it is still clean, or either reading is unknown.
        /// </summary>
        public static JObject Evaluate(bool dryRunReply, bool? modifiedBefore, bool? modifiedAfter)
        {
            if (!dryRunReply) return null;
            if (modifiedBefore != false || modifiedAfter != true) return null;
            return new JObject
            {
                ["document_modified_before"] = false,
                ["document_modified_after"] = true,
                ["means"] = "this rehearsal wrote nothing, yet Revit now reports the document as MODIFIED. The " +
                            "rehearsal commits inside a transaction group and rolls the group back; Revit can mark " +
                            "the document (or a view it owns, such as the system browser) as changed when the group " +
                            "closes. Nothing was kept in the model. Do not save on the strength of this flag " +
                            "alone: compare with horizun_model_diff or read the change ledger first."
            };
        }

        /// <summary>True when a reply's data says it was a rehearsal.</summary>
        public static bool IsDryRunReply(JObject data) => data != null && data.Value<bool?>("dry_run") == true;
    }
}
