using System;
using Autodesk.Revit.DB;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    internal static class NeighborEditGuard
    {
        // Neighbors are resolved from live topology, never an old numeric id.
        // The resolved plan binds their UniqueId and geometry to the token.
        internal static void RequireOwned(Document doc, Element element)
        {
            if (element == null || string.IsNullOrEmpty(element.UniqueId) || doc.GetElement(element.UniqueId)?.Id != element.Id)
                throw new ArgumentException("Neighbor identity could not be established.");
            var verdict = OwnershipProbe.Of(doc, element, OwnershipProbe.SessionAdded(doc), out _);
            if (!verdict.Own) throw new ArgumentException("Neighbor " + Rid.Value(element.Id) + " is not ours: " + verdict.Evidence);
            if (element.Pinned || element.GroupId != ElementId.InvalidElementId)
                throw new ArgumentException("Neighbor is pinned or belongs to a group.");
            if (doc.IsWorkshared && WorksharingUtils.GetCheckoutStatus(doc, element.Id) == CheckoutStatus.OwnedByOtherUser)
                throw new ArgumentException("Neighbor is checked out by another user.");
        }
    }
}
