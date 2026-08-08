// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// How a close reaches a document Revit will let it close.
//
// Revit's API cannot close the ACTIVE document. The field workaround is the
// decoy dance: open a document you do not want, so the one you do want to close
// stops being active. Measured 2026-08-05 (three times in one session) and again
// 2026-08-07 at batch scale, where it grew a consequence: the last model of a
// 54-model batch stays open, so relaunching the batch SKIPS it. Story 5.13.
//
// This file decides WHICH way out exists, out of facts the command hands it:
//
//   * the target is not active - nothing to decide, close it;
//   * the caller did not ask for help (activate_other=false) - refuse, exactly
//     as before, because activating a different document changes what the user
//     is looking at and must be asked for, never a side effect;
//   * another open document can be activated - activate that one. Only a
//     document whose path exists on disk qualifies: activation goes through
//     OpenAndActivateDocument(path), and a detached or never-saved document has
//     no path a re-open could resolve;
//   * nothing else is open (or nothing qualifies) - open the bridge's own
//     ANCHOR, a deliberately empty project in the bridge's data directory, so
//     the decoy is a file Horizun owns rather than whatever model was nearby.
//
// Revit-free on purpose: every branch is provable in CI, which a real Revit
// with a real set of open documents will not reliably produce on demand.
// -----------------------------------------------------------------------------
using System.Collections.Generic;

namespace Horizun.Revit.Core
{
    /// <summary>One open document that could serve as the activation target.</summary>
    public sealed class ActivationCandidate
    {
        public string Title;
        public string Path;

        /// <summary>
        /// True only when the path names a file that is really on disk. A detached
        /// document's synthetic '&lt;name&gt;_detached.rvt' fails this on purpose:
        /// OpenAndActivateDocument would try to OPEN it, not switch to it.
        /// </summary>
        public bool PathExistsOnDisk;
    }

    public enum ActivationAction
    {
        /// <summary>The target is not active; no activation is needed to close it.</summary>
        NotNeeded,

        /// <summary>Active target, and the caller did not pass activate_other. Refuse.</summary>
        RefusedNotAsked,

        /// <summary>Activate the chosen already-open document (Chosen says which).</summary>
        ActivateOpenDocument,

        /// <summary>Nothing else qualifies: open (creating it first if needed) the bridge's anchor.</summary>
        OpenAnchor
    }

    public sealed class ActivationPlan
    {
        public ActivationAction Action { get; internal set; }

        /// <summary>Set only for ActivateOpenDocument.</summary>
        public ActivationCandidate Chosen { get; internal set; }
    }

    public static class ActivationChoice
    {
        /// <summary>
        /// Decide how the close gets a non-active target. Candidates must already
        /// exclude the target itself and linked documents; order is the caller's
        /// enumeration order and the FIRST qualifying candidate wins, so the choice
        /// is deterministic and reportable rather than whichever iteration luck picks.
        /// </summary>
        public static ActivationPlan Decide(bool targetIsActive, bool activateOther,
                                            IList<ActivationCandidate> candidates)
        {
            if (!targetIsActive)
                return new ActivationPlan { Action = ActivationAction.NotNeeded };

            if (!activateOther)
                return new ActivationPlan { Action = ActivationAction.RefusedNotAsked };

            if (candidates != null)
                foreach (ActivationCandidate c in candidates)
                    if (c != null && c.PathExistsOnDisk)
                        return new ActivationPlan { Action = ActivationAction.ActivateOpenDocument, Chosen = c };

            // No other document, or none whose path a re-open could resolve. The way
            // out is the bridge's own anchor - never some other open document "close
            // enough", because an activation that OPENS a file must open a file
            // Horizun owns, not gamble on a synthetic path.
            return new ActivationPlan { Action = ActivationAction.OpenAnchor };
        }
    }
}
