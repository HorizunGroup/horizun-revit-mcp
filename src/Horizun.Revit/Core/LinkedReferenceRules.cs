// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// WHAT A REFERENCE INTO AN RVT LINK IS, decided as arithmetic.
//
// The bridge used to refuse every reference that resolved into a link, on the
// honest ground that it had not been proven live. Proving it live needs the
// rules to exist first, and the rules are the half that can be wrong silently:
//
//   * THE THREE IDS ARE NOT INTERCHANGEABLE. A RevitLinkInstance id, a
//     RevitLinkType id and a linked element id name three different things in
//     two different documents, and every one of them is an integer. Printing
//     one where another belongs produces a row that looks right, parses, and
//     describes an element that does not exist in the document the caller
//     holds. LinkTarget carries all three, separately, or it does not exist.
//
//   * IDENTITY INCLUDES THE INSTANCE. The same linked file placed twice is two
//     pieces of host geometry with two transforms. A plan bound to "the
//     structure link" rather than to THIS placement of it can be applied
//     against the other one, silently, and be off by the distance between them.
//
//   * A TRANSFORM IS GEOMETRY AND MUST BE FINGERPRINTED LIKE GEOMETRY. It rides
//     the same 0.1 mm grid as every other fact in this codebase, so link jitter
//     from a regeneration keeps the identity and a real nudge changes it. The
//     basis vectors ride the grid too - one documented rule, not a rule per
//     field.
//
//   * A REFUSAL MUST NAME ITS CAUSE. "unloaded link", "the linked element is
//     gone", "that is a nested link" and "Revit would not make the reference"
//     are four different problems with four different fixes, and one shared
//     sentence helps nobody.
//
// Revit-free on purpose: no `using Autodesk`. The command reads the live model
// and feeds this file plain strings and doubles; the tests prove the rules
// without a building, and without a link.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Horizun.Revit.Core
{
    /// <summary>
    /// One caller-named target inside one link instance. The instance is named per
    /// entry ON PURPOSE: two placements of the same file are two entries, so the
    /// bridge never has to choose between them and never silently picks the first.
    /// </summary>
    public sealed class LinkTargetRequest
    {
        public long LinkInstanceId { get; set; }
        public List<long> LinkedElementIds { get; } = new List<long>();
    }

    /// <summary>
    /// A link instance's placement, canonicalised. Every number is INTERNAL FEET
    /// and every number is quantised on the 0.1 mm grid before it is rendered, so
    /// the fingerprint of a transform behaves exactly like the fingerprint of a
    /// face.
    /// </summary>
    public sealed class LinkTransformFacts
    {
        public double[] Origin { get; set; }
        public double[] BasisX { get; set; }
        public double[] BasisY { get; set; }
        public double[] BasisZ { get; set; }

        /// <summary>Revit's own determinant, read off the Transform - never recomputed here.</summary>
        public double Determinant { get; set; }

        public bool IsIdentity { get; set; }

        /// <summary>A left-handed basis: the link was mirrored. Reported, never rejected on its own.</summary>
        public bool HasReflection => Determinant < 0;

        /// <summary>
        /// The basis is not the world basis, ignoring a pure translation. True for a
        /// rotated link and for a mirrored one; false for a link that was only moved.
        /// </summary>
        public bool HasRotation { get; set; }

        public string Handedness => HasReflection ? "left" : "right";
    }

    public static class LinkedReferenceRules
    {
        // ---- refusal codes, closed ------------------------------------------------
        //
        // Every one of these is decided BEFORE a transaction opens, and every one
        // travels in the row so a client branches on the code instead of matching
        // prose that may be translated or reworded.

        public const string CodeNotALinkInstance = "not_a_link_instance";
        public const string CodeLinkUnloaded = "link_unloaded";
        public const string CodeLinkDocumentUnavailable = "link_document_unavailable";
        public const string CodeLinkedElementMissing = "linked_element_missing";
        public const string CodeLinkedElementIsType = "linked_element_is_type";
        public const string CodeNestedLinkNotSupported = "nested_link_not_supported";
        public const string CodeLinkReferenceNotCreatable = "link_reference_not_creatable";
        public const string CodeLinkReferenceUnreadable = "link_reference_unreadable";

        // ---- drift codes, for a plan that was minted and then overtaken -----------

        public const string CodeLinkTransformMoved = "link_transform_moved";
        public const string CodeLinkedDocumentChanged = "linked_document_changed";
        public const string CodeLinkInstanceChanged = "link_instance_changed";
        public const string CodeLinkedElementChanged = "linked_element_changed";

        // ---- limits ---------------------------------------------------------------

        /// <summary>Link instances named in ONE call. Beyond this the answer stops being reviewable.</summary>
        public const int MaxLinkTargets = 50;

        // ---- request validation ---------------------------------------------------

        /// <summary>
        /// Validate the shape of linked_targets before anything is looked up. Returns
        /// an error string, or null and a normalised list with duplicate linked ids
        /// collapsed per entry (the SAME instance named twice stays two entries and is
        /// refused, because two entries for one placement can only be a mistake).
        /// </summary>
        public static string ValidateLinkTargets(IEnumerable<LinkTargetRequest> requested,
                                                 out List<LinkTargetRequest> normalized,
                                                 out int totalTargets)
        {
            normalized = new List<LinkTargetRequest>();
            totalTargets = 0;
            if (requested == null) return null;

            var seenInstances = new HashSet<long>();
            foreach (LinkTargetRequest entry in requested)
            {
                if (entry == null) return "linked_targets entries must be objects.";
                if (!seenInstances.Add(entry.LinkInstanceId))
                    return "linked_targets names link instance " + entry.LinkInstanceId + " more than once. " +
                           "One entry per placement: merge their linked_element_ids, because two entries for one " +
                           "instance cannot be told apart later and the bridge will not guess which you meant.";
                if (entry.LinkedElementIds.Count == 0)
                    return "linked_targets entry for link instance " + entry.LinkInstanceId + " carries no " +
                           "linked_element_ids. A whole linked model is not a default answer to 'what can I " +
                           "dimension here?' - name the elements inside the link.";

                var collapsed = new LinkTargetRequest { LinkInstanceId = entry.LinkInstanceId };
                var seenIds = new HashSet<long>();
                foreach (long id in entry.LinkedElementIds)
                    if (seenIds.Add(id)) collapsed.LinkedElementIds.Add(id);
                normalized.Add(collapsed);
                totalTargets += collapsed.LinkedElementIds.Count;
            }

            if (normalized.Count > MaxLinkTargets)
                return "linked_targets names " + normalized.Count + " link instances; the limit is " +
                       MaxLinkTargets + " per call. Split the request - the ordering is deterministic, so " +
                       "pages compose.";
            return null;
        }

        // ---- the sentences a refusal carries --------------------------------------

        public static string NotALinkInstance(long elementId)
            => "element " + elementId + " is not a RevitLinkInstance, so it names no linked document. " +
               "linked_targets.link_instance_id must be the id of a PLACED link instance in the host " +
               "document; a link TYPE and a linked element are different ids and neither works here.";

        public static string LinkUnloaded(long linkInstanceId, string linkName, string status)
            => "link instance " + linkInstanceId + " (" + Describe(linkName) + ") is " +
               (string.IsNullOrWhiteSpace(status) ? "not loaded" : "in state '" + status + "'") +
               ". An unloaded link has no geometry in this session, so there is nothing to enumerate, nothing " +
               "to fingerprint and nothing a dimension could hang off. Reload the link and ask again; nothing " +
               "was inspected.";

        public static string LinkDocumentUnavailable(long linkInstanceId, string linkName)
            => "link instance " + linkInstanceId + " (" + Describe(linkName) + ") reports itself loaded but " +
               "GetLinkDocument() returned nothing, so its model cannot be read in this session. This is a " +
               "state Revit can hold after a failed reload; nothing was inspected.";

        public static string LinkedElementMissing(long linkInstanceId, long linkedElementId, string documentTitle)
            => "element " + linkedElementId + " does not exist in the linked document " + Describe(documentTitle) +
               " reached through link instance " + linkInstanceId + ". Linked element ids belong to the LINKED " +
               "model, not to the host - an id copied from the host document will usually resolve to nothing, " +
               "or worse, to something else.";

        public static string LinkedElementIsType(long linkInstanceId, long linkedElementId)
            => "element " + linkedElementId + " inside link instance " + linkInstanceId + " is an element TYPE. " +
               "Dimension references belong to placed instances; pass the id of an instance inside the link.";

        public static string NestedLinkNotSupported(long linkInstanceId, long linkedElementId)
            => "element " + linkedElementId + " inside link instance " + linkInstanceId + " is itself a link " +
               "instance, so the reference would live two levels down. Reference.CreateLinkReference lifts a " +
               "reference exactly ONE level into the host document; there is no API that expresses a nested-link " +
               "reference, and manufacturing a representation for it would produce a string that parses and then " +
               "dimensions something else. Link the nested model into the host directly.";

        public static string LinkReferenceNotCreatable(long linkInstanceId, long linkedElementId, string detail)
            => "Revit would not lift a reference on element " + linkedElementId + " into the host through link " +
               "instance " + linkInstanceId +
               (string.IsNullOrWhiteSpace(detail) ? "" : " (" + detail + ")") +
               ". The geometry exists inside the link, but without a host-document reference there is nothing a " +
               "dimension in this document could attach to.";

        public static string LinkReferenceUnreadable(long linkInstanceId, long linkedElementId, string detail)
            => "the reference on element " + linkedElementId + " through link instance " + linkInstanceId +
               " was created but its geometry could not be read back in this view" +
               (string.IsNullOrWhiteSpace(detail) ? "" : " (" + detail + ")") +
               ", so it cannot be fingerprinted. A reference the bridge cannot fingerprint cannot be bound by a " +
               "plan, and a plan that cannot bind it cannot promise stale detection.";

        // ---- drift sentences ------------------------------------------------------

        public static string TransformMoved(long linkInstanceId, string linkName, string was, string now)
            => "THE MODEL MOVED AFTER THE DRY RUN: link instance " + linkInstanceId + " (" + Describe(linkName) +
               ") stood at transform " + Short(was) + " when the plan was minted and stands at " + Short(now) +
               " now. Every reference into it measures from somewhere else than the one you approved. " +
               "Nothing was written - re-run the dry run and approve the CURRENT plan.";

        public static string DocumentChanged(long linkInstanceId, string was, string now)
            => "THE MODEL MOVED AFTER THE DRY RUN: link instance " + linkInstanceId + " pointed at linked " +
               "document " + Describe(was) + " when the plan was minted and points at " + Describe(now) +
               " now. The ids in the plan belong to the previous document. Nothing was written.";

        public static string InstanceChanged(long linkInstanceId, string was)
            => "THE MODEL MOVED AFTER THE DRY RUN: link instance " + linkInstanceId + " (unique id " +
               Short(was) + ") is no longer in the host document, or is no longer that instance. Nothing was " +
               "written.";

        public static string LinkedElementChanged(long linkInstanceId, long linkedElementId, string was)
            => "THE MODEL MOVED AFTER THE DRY RUN: element " + linkedElementId + " inside link instance " +
               linkInstanceId + " (unique id " + Short(was) + ") is gone or is no longer that element. " +
               "Nothing was written.";

        // ---- transform fingerprinting ---------------------------------------------

        /// <summary>
        /// A transform rendered canonically: origin then the three basis vectors, every
        /// component quantised on the 0.1 mm grid, invariant culture, control-character
        /// separators so no component can forge a boundary. The determinant's SIGN is
        /// included (a mirrored link is not the same placement as an unmirrored one)
        /// but not its magnitude beyond the grid, because a scaled link is already
        /// impossible in Revit and a fingerprint should not imply otherwise.
        /// </summary>
        public static string CanonicalTransform(LinkTransformFacts t)
        {
            if (t == null) throw new ArgumentNullException(nameof(t));
            var sb = new StringBuilder();
            AppendVector(sb, "o", t.Origin);
            AppendVector(sb, "x", t.BasisX);
            AppendVector(sb, "y", t.BasisY);
            AppendVector(sb, "z", t.BasisZ);
            sb.Append("h=").Append(t.Handedness).Append((char)31);
            return sb.ToString();
        }

        /// <summary>SHA-256 hex over <see cref="CanonicalTransform"/>. The identity of a placement.</summary>
        public static string TransformFingerprint(LinkTransformFacts t)
            => RequestFingerprint.Sha256Hex(CanonicalTransform(t));

        private static void AppendVector(StringBuilder sb, string name, double[] v)
        {
            sb.Append(name).Append('=');
            if (v == null || v.Length != 3)
                throw new ArgumentException("A link transform needs origin and three basis vectors, each of " +
                                            "exactly three finite components; '" + name + "' was not one. A " +
                                            "fingerprint over a half-read transform would be an identity for a " +
                                            "placement nobody measured.");
            for (int i = 0; i < 3; i++)
            {
                double c = v[i];
                if (double.IsNaN(c) || double.IsInfinity(c))
                    throw new ArgumentException("Link transform component '" + name + "[" + i + "]' is not finite.");
                sb.Append(DimensionReferenceRules.QuantizeFeet(c).ToString(CultureInfo.InvariantCulture));
                sb.Append(i == 2 ? (char)31 : (char)30);
            }
        }

        /// <summary>
        /// The identity of a LINKED DOCUMENT, as a plan binds it: the title plus a hash
        /// of the path. The path is hashed rather than carried so a plan token never
        /// ships somebody's directory layout, and the title travels in clear because a
        /// human reading a refusal needs to recognise which model it is about.
        /// </summary>
        public static string DocumentIdentity(string title, string path)
        {
            string t = (title ?? "").Trim();
            string p = (path ?? "").Trim();
            string hash = p.Length == 0 ? "no-path" : RequestFingerprint.Sha256Hex(p.ToLowerInvariant()).Substring(0, 16);
            return t + "|" + hash;
        }

        /// <summary>
        /// The single comparison behind every drift refusal. Returns the structured code
        /// of the FIRST thing that moved, or null when the placement is exactly the one
        /// the plan was minted against. Order matters: an instance that is gone must not
        /// be reported as a moved transform, because the fixes are different.
        /// </summary>
        public static string DetectDrift(LinkBinding planned, LinkBinding current)
        {
            if (planned == null) return null;
            if (current == null) return CodeLinkInstanceChanged;
            if (!string.Equals(planned.InstanceUniqueId ?? "", current.InstanceUniqueId ?? "", StringComparison.Ordinal))
                return CodeLinkInstanceChanged;
            if (!string.Equals(planned.DocumentIdentity ?? "", current.DocumentIdentity ?? "", StringComparison.Ordinal))
                return CodeLinkedDocumentChanged;
            if (!string.Equals(planned.LinkedElementUniqueId ?? "", current.LinkedElementUniqueId ?? "", StringComparison.Ordinal))
                return CodeLinkedElementChanged;
            if (!string.Equals(planned.TransformFingerprint ?? "", current.TransformFingerprint ?? "", StringComparison.Ordinal))
                return CodeLinkTransformMoved;
            return null;
        }

        /// <summary>
        /// The prose for a drift code, given both sides. Kept beside DetectDrift so a
        /// new code cannot be added without a sentence to explain it.
        /// </summary>
        public static string DriftMessage(string code, LinkBinding planned, LinkBinding current)
        {
            if (planned == null) return null;
            switch (code)
            {
                case CodeLinkInstanceChanged:
                    return InstanceChanged(planned.LinkInstanceId, planned.InstanceUniqueId);
                case CodeLinkedDocumentChanged:
                    return DocumentChanged(planned.LinkInstanceId, planned.DocumentIdentity,
                                           current == null ? null : current.DocumentIdentity);
                case CodeLinkedElementChanged:
                    return LinkedElementChanged(planned.LinkInstanceId, planned.LinkedElementId,
                                                planned.LinkedElementUniqueId);
                case CodeLinkTransformMoved:
                    return TransformMoved(planned.LinkInstanceId, planned.LinkName, planned.TransformFingerprint,
                                          current == null ? null : current.TransformFingerprint);
                default:
                    return null;
            }
        }

        // ---- ordering -------------------------------------------------------------

        /// <summary>
        /// The tiebreak that extends the host ordering over a federated answer: host
        /// rows before linked rows for the same element id, then by link instance, then
        /// by linked element id. Everything ordinal, so no culture can reorder it.
        /// </summary>
        public static int CompareProvenance(LinkBinding a, LinkBinding b)
        {
            bool aLinked = a != null, bLinked = b != null;
            if (aLinked != bLinked) return aLinked ? 1 : -1;
            if (!aLinked) return 0;
            int c = a.LinkInstanceId.CompareTo(b.LinkInstanceId);
            if (c != 0) return c;
            return a.LinkedElementId.CompareTo(b.LinkedElementId);
        }

        private static string Describe(string name)
            => string.IsNullOrWhiteSpace(name) ? "an unnamed link" : "'" + name + "'";

        private static string Short(string hashOrId)
        {
            if (string.IsNullOrWhiteSpace(hashOrId)) return "(unknown)";
            return hashOrId.Length <= 16 ? hashOrId : hashOrId.Substring(0, 16) + "…";
        }
    }

    /// <summary>
    /// Everything a plan binds about ONE linked reference, and everything a row
    /// reports about it. Deliberately a plain record: the command fills it from the
    /// live model, the rules compare two of them, and the tests build them by hand.
    /// </summary>
    public sealed class LinkBinding
    {
        public long LinkInstanceId { get; set; }
        public string InstanceUniqueId { get; set; }
        public long LinkTypeId { get; set; }
        public string LinkName { get; set; }

        public string DocumentTitle { get; set; }
        public string DocumentIdentity { get; set; }

        public long LinkedElementId { get; set; }
        public string LinkedElementUniqueId { get; set; }
        public string LinkedElementCategory { get; set; }
        public string LinkedElementClass { get; set; }

        public string TransformFingerprint { get; set; }
        public LinkTransformFacts Transform { get; set; }
    }
}
