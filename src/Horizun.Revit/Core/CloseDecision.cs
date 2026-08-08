// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// Whether closing a document would throw away work, and what the caller must have
// said before it is allowed to.
//
// doc.Close(false) discards everything unsaved in the document, returns true, and
// leaves nothing behind that could tell you it happened. IsModified cannot be asked
// of a closed document. The file on disk is untouched, so its bytes and its mtime
// are the same either way. Every signal the close handler already collected - the
// document is gone from Application.Documents, IsValidObject is false, the API
// returned true - reports a clean, successful close. An hour of edits and a
// document nobody had touched produce byte-identical responses.
//
// That is the shape this codebase exists to refuse. Not a failure reported as
// success: a LOSS reported as success, with no way to find out afterwards.
//
// So a close that would discard work needs three things, and none of them
// substitutes for another:
//
//   * discard_unsaved=true, which is the caller saying the words;
//   * a rehearsal, so the caller sees WHAT would be lost before agreeing;
//   * a token bound to that rehearsal, so the agreement covers this document and
//     this request rather than the idea of closing something.
//
// UNKNOWN COUNTS AS MODIFIED. A document whose IsModified could not be read is not
// a document known to be clean, and of the two ways to be wrong here, refusing a
// harmless close costs one extra flag and permitting a silent one costs the work.
//
// Revit-free, so the tri-state can be proved in all three states - which a real
// Revit will not reliably produce on demand.
// -----------------------------------------------------------------------------
namespace Horizun.Revit.Core
{
    /// <summary>What must happen before this close may proceed.</summary>
    public enum CloseRequirement
    {
        /// <summary>Nothing at stake. Close it.</summary>
        Proceed,

        /// <summary>Work would be lost and the caller has not said discard_unsaved.</summary>
        NeedsDiscardFlag,

        /// <summary>The flag is there; the rehearsal and its token are not.</summary>
        NeedsConfirmation
    }

    public sealed class CloseVerdict
    {
        public CloseRequirement Requirement { get; internal set; }

        /// <summary>True when going ahead throws away unsaved changes, or might.</summary>
        public bool WouldDiscard { get; internal set; }

        public bool Ok => Requirement == CloseRequirement.Proceed;
    }

    public static class CloseDecision
    {
        /// <summary>
        /// Decide, out of the three facts that matter.
        /// </summary>
        /// <param name="isModified">
        /// Measured BEFORE the close - it cannot be asked afterwards. null is unknown,
        /// and unknown is treated as modified.
        /// </param>
        /// <param name="saveOnClose">The work is being written, so nothing is discarded.</param>
        /// <param name="discardUnsaved">The caller has said, in as many words, to drop it.</param>
        /// <param name="hasConfirmation">A token from a rehearsal has been presented and checks out.</param>
        public static CloseVerdict Decide(bool? isModified, bool saveOnClose, bool discardUnsaved,
                                          bool hasConfirmation)
        {
            // != false, not == true. A document whose modified flag could not be read is
            // not a document with nothing to lose, and writing this as == true is exactly
            // how the null would quietly become a "no".
            bool wouldDiscard = !saveOnClose && isModified != false;

            var v = new CloseVerdict { WouldDiscard = wouldDiscard };
            if (!wouldDiscard) { v.Requirement = CloseRequirement.Proceed; return v; }

            v.Requirement = !discardUnsaved ? CloseRequirement.NeedsDiscardFlag
                          : !hasConfirmation ? CloseRequirement.NeedsConfirmation
                          : CloseRequirement.Proceed;
            return v;
        }

        /// <summary>
        /// The request fields a close token is bound to. Anything that changes WHICH
        /// document is closed, or WHETHER work is lost, belongs here - and nothing else,
        /// or the token becomes brittle for no gain.
        ///
        /// discard_unsaved IS DELIBERATELY NOT HERE, and leaving it in was a bug that
        /// made the command unreachable. The rehearsal is sent WITHOUT it - that is the
        /// whole point of a rehearsal, you are asking what would happen - and the
        /// execution is sent WITH it. So including it in the plan meant the two hashes
        /// could never match: every token was refused as PlanChanged, and a document with
        /// unsaved changes could not be closed at all, by anyone, ever.
        ///
        /// The distinction is the general one: this list is the PLAN - what will be done
        /// and to what. discard_unsaved is the APPROVAL - the caller saying yes to it.
        /// An approval that has to be present in the thing being approved is a circle,
        /// and three commands in this codebase have shipped with that circle before.
        /// dry_run is absent for the same reason.
        /// </summary>
        /// <remarks>
        /// activate_other IS here: it changes what the command does to the session
        /// (another document becomes active, possibly the bridge's anchor opening),
        /// which is plan, not approval - a rehearsal approved without it must not
        /// authorise an execution that also switches documents.
        /// </remarks>
        public static readonly string[] PlanFields =
        {
            "operation", "target_document", "file_path", "save_on_close", "force_workshared",
            "activate_other"
        };
    }
}
