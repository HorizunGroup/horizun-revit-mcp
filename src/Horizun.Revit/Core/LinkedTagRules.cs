// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// TAGS ON ELEMENTS OF A REVIT LINK (field cycle, 2026-10-21).
//
// MEASURED: horizun_annotate operation=tag took only an element_id of the HOST. A
// door, column or wall of a linked model could not be tagged, although Revit allows
// it (IndependentTag.Create over a link Reference). The workaround was a text note
// typed from a query - a label that no longer follows the model.
//
// A linked element is identified by TWO ids: the link instance in the host and the
// element in the linked document. This file decides, without Revit, whether a request
// names one target or the other (never both, never half of the link pair) and whether
// the tag Revit committed points at exactly the pair that was asked for.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public static class LinkedTagRules
    {
        public const string LinkInstanceField = "link_instance_id";
        public const string LinkedElementField = "linked_element_id";

        /// <summary>
        /// Reads which target a tag action names. Returns null and sets error when the
        /// combination is ambiguous or incomplete; linked is true for the link pair.
        /// </summary>
        public static string Check(bool hasElementId, bool hasLinkInstanceId, bool hasLinkedElementId, out bool linked)
        {
            linked = hasLinkInstanceId || hasLinkedElementId;
            if (hasElementId && linked)
                return "tag names two targets: element_id (an element of this document) and " + LinkInstanceField +
                       "/" + LinkedElementField + " (an element of a link). Send one of them.";
            if (hasLinkInstanceId != hasLinkedElementId)
                return "tagging an element of a link needs BOTH " + LinkInstanceField + " (the link instance in this " +
                       "document) and " + LinkedElementField + " (the element id inside the linked model); only " +
                       (hasLinkInstanceId ? LinkInstanceField : LinkedElementField) + " was given.";
            if (!hasElementId && !linked)
                return "tag needs element_id, or " + LinkInstanceField + " + " + LinkedElementField + " for an element of a link.";
            return null;
        }

        /// <summary>True when the pair (instance, linked element) is among the committed tag's tagged references.</summary>
        public static bool PointsAt(IEnumerable<KeyValuePair<long, long>> tagged, long linkInstanceId, long linkedElementId)
        {
            if (tagged == null) return false;
            return tagged.Any(t => t.Key == linkInstanceId && t.Value == linkedElementId);
        }

        /// <summary>The committed tag must point at the asked pair AND at nothing else.</summary>
        public static bool PointsOnlyAt(IEnumerable<KeyValuePair<long, long>> tagged, long linkInstanceId, long linkedElementId)
        {
            if (tagged == null) return false;
            var list = tagged.ToList();
            return list.Count == 1 && list[0].Key == linkInstanceId && list[0].Value == linkedElementId;
        }

        /// <summary>The plan/verification block that names the link target.</summary>
        public static JObject Describe(long linkInstanceId, string linkInstanceName, long linkedElementId, string linkedDocument, string linkedCategory)
        {
            return new JObject
            {
                ["source"] = "link",
                [LinkInstanceField] = linkInstanceId,
                ["link_instance_name"] = linkInstanceName,
                [LinkedElementField] = linkedElementId,
                ["linked_document"] = linkedDocument,
                ["linked_category"] = linkedCategory
            };
        }

        /// <summary>
        /// The BuiltInCategory NAME of the tag category that labels a tagged category
        /// (OST_Doors -> OST_DoorTags), or null when none is known. Pure so it is testable
        /// without Revit; the caller resolves it against the enum.
        /// </summary>
        public static string TagCategoryName(string taggedCategoryName)
        {
            // One map for every tag path (host, link, auto_tags): TagRules.TagCategoryFor.
            return TagRules.TagCategoryFor(taggedCategoryName);
        }
    }
}
