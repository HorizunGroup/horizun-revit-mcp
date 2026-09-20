// -----------------------------------------------------------------------------
// Horizun Revit MCP — original Horizun code.
//
// SELECTING AN ELEMENT THAT LIVES IN A LINK.
//
// horizun_navigate has always been host-only, and honest about it: its own schema
// says "linked element ids are document-local and cannot be selected without
// their link-instance identity". That was a true statement about the tool, and it
// left a hole the clash viewer fell straight into — it took side A's id, called
// Number() on it and sent it as a host id. For a clash between the host model and
// a link, that selects whichever HOST element happens to carry the same number.
// Not nothing, not an error: a different element, highlighted confidently.
//
// THE COMPOSITE IDENTITY IS THE FIX, and Revit has supported it all along:
//
//   new Reference(elementInsideTheLink)      a reference in the LINK's terms
//   .CreateLinkReference(linkInstance)       lifted into the HOST's terms
//   uidoc.Selection.SetReferences(...)       selected
//
// So an element in a link is (link_instance_id, element_id) and never an integer.
// A pair is not an id with an extra field: it is the identity, and dropping half
// of it is what produced the defect.
//
// WHAT THE RE-READ CAN AND CANNOT CONFIRM, said in the reply rather than rounded
// up. Selection.GetElementIds() returns the LINK INSTANCE for a linked selection,
// not the element inside it. So a host selection is verified element by element,
// and a linked one is verified only as far as "the right link instances are
// selected". That is weaker, it is what Revit exposes, and reporting it as
// equivalent would be the kind of claim this bridge refuses everywhere else.
//
// PRESERVING THE EXISTING BEHAVIOUR. `element_ids` still means exactly what it
// meant: host-document ids, resolved and verified the way they always were. The
// new `selections` argument is a separate path, and sending both is refused
// rather than merged - two ways of saying what to select, disagreeing quietly,
// is worse than either.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    /// <summary>One thing to select: an element, and the link instance it lives in if any.</summary>
    public sealed class NavigationTarget
    {
        public long ElementId;
        public long LinkInstanceId = -1;        // -1 = the host document
        public bool InLink => LinkInstanceId >= 0;

        public JObject ToJson() => new JObject
        {
            ["element_id"] = ElementId,
            ["link_instance_id"] = InLink ? (JToken)LinkInstanceId : JValue.CreateNull()
        };
    }

    public static class NavigateLinked
    {
        public const int MaxTargets = 5000;

        /// <summary>
        /// Read the `selections` array. Returns null with a refusal rather than throwing, and
        /// refuses a bare integer: an id with no document is exactly the shape that caused this.
        /// </summary>
        public static List<NavigationTarget> Read(JArray token, out string refusal)
        {
            refusal = null;
            var targets = new List<NavigationTarget>();
            if (token == null || token.Count == 0)
            {
                refusal = "selections is required and must not be empty.";
                return null;
            }
            if (token.Count > MaxTargets)
            {
                refusal = "selections holds " + token.Count + " entries; the bound is " + MaxTargets + ".";
                return null;
            }

            for (int i = 0; i < token.Count; i++)
            {
                var row = token[i] as JObject;
                if (row == null)
                {
                    // A BARE NUMBER IS REFUSED ON PURPOSE. Accepting one here and assuming the
                    // host document would reintroduce the exact defect this path exists to fix,
                    // in the place somebody would look for it least.
                    refusal = "selections[" + i + "] is a " + token[i].Type.ToString().ToLowerInvariant() +
                              " and must be an object: { element_id, link_instance_id? }. A bare " +
                              "number is an element id with no document, and an element id means " +
                              "nothing without one - two documents can hold the same number.";
                    return null;
                }

                long elementId = row.Value<long?>("element_id") ?? -1;
                if (elementId < 0 || !Rid.CanRepresent(elementId))
                {
                    refusal = "selections[" + i + "] carries no usable element_id.";
                    return null;
                }

                long linkId = row.Value<long?>("link_instance_id") ?? -1;
                if (row["link_instance_id"] != null && row["link_instance_id"].Type != JTokenType.Null &&
                    (linkId < 0 || !Rid.CanRepresent(linkId)))
                {
                    refusal = "selections[" + i + "].link_instance_id is not a usable element id.";
                    return null;
                }

                targets.Add(new NavigationTarget { ElementId = elementId, LinkInstanceId = linkId });
            }
            return targets;
        }

        /// <summary>
        /// Resolve every target to a Reference in the HOST's terms.
        ///
        /// Everything that cannot be resolved is collected and reported TOGETHER, and nothing
        /// is selected: a partial selection of a clash pair shows one side lit up and reads as
        /// though the other side is fine.
        /// </summary>
        public static List<Reference> Resolve(Document host, IEnumerable<NavigationTarget> targets,
                                              out JArray problems)
        {
            problems = new JArray();
            var references = new List<Reference>();

            foreach (NavigationTarget target in targets)
            {
                if (!target.InLink)
                {
                    Element element = null;
                    try { element = host.GetElement(Rid.Make(target.ElementId)); } catch { }
                    if (element == null)
                    {
                        problems.Add(Problem(target, "no element with this id in the host document."));
                        continue;
                    }
                    try { references.Add(new Reference(element)); }
                    catch (Exception ex)
                    {
                        problems.Add(Problem(target, "Revit would not make a reference to it: " + ex.Message));
                    }
                    continue;
                }

                RevitLinkInstance instance = null;
                try { instance = host.GetElement(Rid.Make(target.LinkInstanceId)) as RevitLinkInstance; }
                catch { }
                if (instance == null)
                {
                    problems.Add(Problem(target, "link_instance_id " + target.LinkInstanceId +
                                                 " is not a RevitLinkInstance in the host document."));
                    continue;
                }

                Document linked = null;
                try { linked = instance.GetLinkDocument(); } catch { }
                if (linked == null)
                {
                    // AN UNLOADED LINK IS NOT A MISSING ELEMENT. The element may be perfectly
                    // fine; nobody can reach it because the link is not loaded, and saying
                    // "not found" would send somebody looking in the wrong model.
                    problems.Add(Problem(target, "the link instance is present and its document is NOT " +
                                                 "loaded, so nothing inside it can be reached. This is " +
                                                 "a link state, not a missing element."));
                    continue;
                }

                Element inside = null;
                try { inside = linked.GetElement(Rid.Make(target.ElementId)); } catch { }
                if (inside == null)
                {
                    problems.Add(Problem(target, "no element with this id inside linked document '" +
                                                 SafeTitle(linked) + "'."));
                    continue;
                }

                try
                {
                    Reference inLink = new Reference(inside);
                    references.Add(inLink.CreateLinkReference(instance));
                }
                catch (Exception ex)
                {
                    problems.Add(Problem(target, "the link reference could not be built: " + ex.Message));
                }
            }
            return references;
        }

        /// <summary>
        /// Select, then read back what Revit actually holds — and be precise about which half
        /// of the claim the re-read supports.
        /// </summary>
        public static JObject SelectAndVerify(UIDocument uidoc, List<NavigationTarget> targets,
                                              List<Reference> references)
        {
            uidoc.Selection.SetReferences(references);

            var hostWanted = new HashSet<long>(targets.Where(t => !t.InLink).Select(t => t.ElementId));
            var linksWanted = new HashSet<long>(targets.Where(t => t.InLink).Select(t => t.LinkInstanceId));

            var after = new HashSet<long>();
            try { after = new HashSet<long>(uidoc.Selection.GetElementIds().Select(Rid.Value)); }
            catch { }

            bool hostVerified = hostWanted.All(after.Contains);
            bool linksVerified = linksWanted.All(after.Contains);

            return new JObject
            {
                ["requested"] = new JArray(targets.Select(t => t.ToJson())),
                ["selected_after"] = after.Count,
                ["host_elements_verified"] = hostVerified,
                ["link_instances_verified"] = linksVerified,
                ["linked_elements_verified"] = false,
                ["what_the_reread_proves"] =
                    "Selection.GetElementIds() reports the LINK INSTANCE for a linked selection, not " +
                    "the element inside it. So a host element is verified individually and a linked " +
                    "one is verified only as far as 'the right link instance is selected'. That is " +
                    "weaker, it is what Revit exposes, and it is reported as what it is rather than " +
                    "as the same claim."
            };
        }

        private static JObject Problem(NavigationTarget target, string why)
        {
            JObject row = target.ToJson();
            row["problem"] = why;
            return row;
        }

        private static string SafeTitle(Document doc)
        {
            try { return doc == null ? null : doc.Title; } catch { return null; }
        }
    }
}
