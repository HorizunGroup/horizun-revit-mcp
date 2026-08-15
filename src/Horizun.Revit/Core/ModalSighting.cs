// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// When a probed modal dialog is DECLARED, out of raw sightings (story 5.19).
//
// The measured failure: a "New Project" dialog left open made three
// horizun_health calls wait the full 600 s each - 30 minutes to learn what the
// bridge's own log said in the first second: "(it never started)". Revit only
// services the ExternalEvent when idle, and a modal dialog means it never will,
// so a queued request that has not started while a modal is up is a request
// waiting for a click nobody is there to give.
//
// Why raw sightings are not declarations: Interference auto-cancels dialogs
// raised WHILE a command runs, usually within an instant - and a probe from the
// transport thread can land inside that instant. Declaring on one sighting
// would fail-fast a queued request over a dialog that was already on its way
// out. A dialog that survives several consecutive probes, seconds apart, is not
// one being auto-cancelled: it predates the work and nobody is answering it.
//
// One instance per waiting request, fed one probe result per slice. Revit-free:
// the rule is arithmetic over strings, and the states that matter (a flapping
// title, a dialog replaced by another) are provable here without a Revit.
// -----------------------------------------------------------------------------
using System;

namespace Horizun.Revit.Core
{
    public sealed class ModalSighting
    {
        /// <summary>
        /// How often a waiting caller re-checks, and the width of one wait slice.
        /// One second keeps the fail-fast answer near-immediate (three sightings
        /// ~= three seconds against a 600,000 ms timeout) without turning the wait
        /// into a busy loop.
        /// </summary>
        public const int ProbeSliceMs = 1000;

        /// <summary>
        /// The SAME dialog must be seen on this many consecutive probes before it
        /// is declared. Two is enough to outlive Interference's auto-cancel
        /// instant; three keeps the declaration boring even when a dialog is
        /// slow to dismiss on a loaded machine.
        /// </summary>
        public const int ConsecutiveSightingsToDeclare = 3;

        private string _last;
        private int _count;

        /// <summary>
        /// Feed one probe result: the dialog's description, or null when no modal
        /// was seen. Returns the description to DECLARE once the same one has been
        /// sighted ConsecutiveSightingsToDeclare times in a row - and null until
        /// then. A gap or a different dialog starts the count over: continuity is
        /// the evidence, and a title that changes is not one dialog persisting.
        /// </summary>
        public string Observe(string described)
        {
            if (string.IsNullOrEmpty(described))
            {
                _last = null;
                _count = 0;
                return null;
            }

            if (!string.Equals(described, _last, StringComparison.Ordinal))
            {
                _last = described;
                _count = 1;
            }
            else
            {
                _count++;
            }

            return _count >= ConsecutiveSightingsToDeclare ? described : null;
        }
    }
}
