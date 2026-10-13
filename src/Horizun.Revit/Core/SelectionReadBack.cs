// -----------------------------------------------------------------------------
// Horizun Core - original Horizun code. Revit-free.
//
// WHAT REVIT'S SELECTION HOLDS, READ BACK BY REFERENCE.
//
// horizun_navigate selects through Selection.SetReferences, so a target in a link
// is (link instance, element inside it). The re-read used to be
// Selection.GetElementIds(), on the belief that a linked selection reports its link
// instance there. MEASURED 2026-10-09 in Revit 2026: it reports NOTHING - a linked
// element selected alone reads back as an empty selection. Verified by element id,
// a linked selection could never pass, and once an unverified selection became a
// refusal (audit 2026-10-08, 6.12) every linked selection was refused.
//
// Selection.GetReferences() returns what SetReferences put there: a host element as
// its id, a linked one as the link instance's id plus the LinkedElementId inside it.
// That verifies a linked element individually - a stronger claim than "the right
// link instance", and the one the reply can now make.
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using System.Linq;

namespace Horizun.Revit.Core
{
    /// <summary>One target asked for: a host element, or an element inside a link instance.</summary>
    public sealed class SelectionWanted
    {
        public long ElementId;
        public long LinkInstanceId = -1;   // -1 = the host document
        public bool InLink => LinkInstanceId >= 0;
    }

    /// <summary>One reference the selection holds: ElementId, and LinkedElementId when it points into a link.</summary>
    public sealed class SelectionHeld
    {
        public long ElementId;
        public long LinkedElementId = -1;  // -1 = not a linked reference
    }

    public sealed class SelectionVerdict
    {
        public bool HostElementsVerified, LinkInstancesVerified, LinkedElementsVerified;
        public int HeldCount;
        public bool AllVerified => HostElementsVerified && LinkInstancesVerified && LinkedElementsVerified;
    }

    public static class SelectionReadBack
    {
        /// <summary>
        /// What the selection may keep across a load-state change of the given link instances:
        /// everything except the references INTO them. MEASURED 2026-10-10, Revit 2026: a linked
        /// element held by reference while its link is unloaded or reloaded crashes Revit a few
        /// seconds later (0xC0000005 in RevitDB.dll on the main thread); with nothing selected the
        /// same reload is fine. The link instance itself and everything else stay selected.
        /// </summary>
        public static List<SelectionHeld> KeepForLinkChange(IEnumerable<SelectionHeld> held, IEnumerable<long> linkInstanceIds,
                                                            out int dropped)
        {
            var links = new HashSet<long>(linkInstanceIds ?? Enumerable.Empty<long>());
            List<SelectionHeld> all = (held ?? Enumerable.Empty<SelectionHeld>()).ToList();
            List<SelectionHeld> keep = all.Where(h => !(h.LinkedElementId >= 0 && links.Contains(h.ElementId))).ToList();
            dropped = all.Count - keep.Count;
            return keep;
        }

        public static SelectionVerdict Verify(IEnumerable<SelectionWanted> wanted, IEnumerable<SelectionHeld> held)
        {
            List<SelectionWanted> w = (wanted ?? Enumerable.Empty<SelectionWanted>()).ToList();
            List<SelectionHeld> h = (held ?? Enumerable.Empty<SelectionHeld>()).ToList();
            var hostHeld = new HashSet<long>(h.Where(r => r.LinkedElementId < 0).Select(r => r.ElementId));
            var linksHeld = new HashSet<long>(h.Where(r => r.LinkedElementId >= 0).Select(r => r.ElementId));
            var linkedHeld = new HashSet<(long, long)>(h.Where(r => r.LinkedElementId >= 0).Select(r => (r.ElementId, r.LinkedElementId)));
            return new SelectionVerdict
            {
                HeldCount = h.Count,
                HostElementsVerified = w.Where(t => !t.InLink).All(t => hostHeld.Contains(t.ElementId)),
                LinkInstancesVerified = w.Where(t => t.InLink).All(t => linksHeld.Contains(t.LinkInstanceId)),
                // THE PAIR, never the number: a host element with the same id is not the linked one.
                LinkedElementsVerified = w.Where(t => t.InLink).All(t => linkedHeld.Contains((t.LinkInstanceId, t.ElementId)))
            };
        }
    }
}
