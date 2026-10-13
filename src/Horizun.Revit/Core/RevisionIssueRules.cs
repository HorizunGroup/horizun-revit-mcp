// -----------------------------------------------------------------------------
// Horizun Core - original Horizun code. Revit-free.
//
// AN ISSUED REVISION IS LOCKED, SO "ISSUED" IS WRITTEN LAST.
//
// Revit locks an issued revision's description, date, issued-by/to and clouds.
// horizun_manage_revisions wrote `issued` in the middle of the edits, so a batch
// that issued a revision and set its description failed on the description, and
// an update of an issued revision could never change anything (audit 2026-10-08,
// 6.15). The order is now: un-issue first when the caller states `issued` and the
// revision is issued; every edit; `issued` last.
// -----------------------------------------------------------------------------
namespace Horizun.Revit.Core
{
    public static class RevisionIssueRules
    {
        /// <summary>Clear Issued before the edits? Only when the caller states 'issued' and the revision is issued now.</summary>
        public static bool UnissueFirst(bool currentlyIssued, bool? wanted) => currentlyIssued && wanted.HasValue;

        /// <summary>The Issued value to write after every other edit, or null to leave it.</summary>
        public static bool? IssuedLast(bool? wanted) => wanted;
    }
}
