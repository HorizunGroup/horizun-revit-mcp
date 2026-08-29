// -----------------------------------------------------------------------------
// Horizun Revit MCP - what can a dimension hang off, on these elements, in this
// view? Read-only: it opens no transaction and writes nothing.
//
// The reason this command exists: creating a dimension needs Reference objects,
// and a caller outside Revit has no way to discover them except by guessing
// stable-representation strings. This enumerates the candidates - faces, edges,
// endpoints, centerlines, datums - each with its stable representation, its
// geometry, whether a dimension can actually use it, and a fingerprint that
// makes "the same face as before" a checkable claim rather than a hope.
//
// The honesty rules it inherits from the rest of the bridge:
//   * it never chooses between equivalent candidates - all come back marked
//     ambiguous, in one group, and the caller decides;
//   * a selector that does not apply produces a structured warning, never a
//     substitute reference;
//   * references INTO a loaded RVT link are enumerated, with the link instance,
//     the linked document and the linked element reported as three separate
//     ids - and every way a link can fail to answer (unloaded, unavailable,
//     nested, missing element) is a named code, never a silent skip;
//   * totals and truncation are exact - every candidate is computed first and
//     paged after - and elements that could not be read are named, never
//     silently missing.
//
// The decisions that need no Revit (selector parsing, applicability, ordering,
// paging, fingerprints, ambiguity, the codes) live in
// Core/DimensionReferenceRules.cs, where they are proved without a model.
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
    public sealed class DimensionReferencesCommand : ICommand
    {
        public string Name => "horizun_get_dimension_references";
        public string Description =>
            "Enumerate dimensionable references (faces, edges, endpoints, centerlines, datums) of elements in one " +
            "view, with stable representations, geometry fingerprints and explicit compatibility. Read-only.";

        /// <summary>Endpoint match tolerance for the centerline search, in internal feet.</summary>
        private const double CenterlineMatchToleranceFeet = 1e-6;

        /// <summary>
        /// WHERE an element is being inspected from. Everything that used to be
        /// "the active document and identity transforms" is now explicit, because a
        /// linked element differs from a host one in exactly three ways and each of
        /// them is a way to be quietly wrong:
        ///
        ///   * its geometry is read out of ANOTHER document (GeometryDoc), and a
        ///     host view cannot scope that read - Options.View names a view in the
        ///     element's own document;
        ///   * its coordinates are the linked model's, so every reported point has
        ///     to travel through ToHost before it means anything in the view the
        ///     dimension will live in;
        ///   * its references only exist in the host after CreateLinkReference has
        ///     lifted them, and only the LIFTED reference has a stable representation
        ///     the caller can send back.
        ///
        /// A host scope is the identity case of all three, so one code path serves
        /// both and there is no second implementation to drift.
        /// </summary>
        private sealed class Scope
        {
            /// <summary>The document stable representations are made against - always the host.</summary>
            public Document HostDoc;

            /// <summary>The document the element lives in: the host, or the linked model.</summary>
            public Document GeometryDoc;

            /// <summary>Null for a host element.</summary>
            public RevitLinkInstance Link;

            /// <summary>Linked model to host. Identity for a host element.</summary>
            public Transform ToHost = Transform.Identity;

            /// <summary>Null for a host element; the provenance every linked row carries.</summary>
            public LinkBinding Binding;

            public bool IsLinked => Link != null;

            /// <summary>
            /// The host-document reference for a reference obtained in GeometryDoc.
            /// Identity when there is no link; CreateLinkReference otherwise. Returns
            /// null rather than throwing, because "Revit would not lift it" is a row
            /// with a code, not an exception the caller has to interpret.
            /// </summary>
            public Reference Lift(Reference reference)
            {
                if (reference == null) return null;
                if (!IsLinked) return reference;
                try { return reference.CreateLinkReference(Link); }
                catch { return null; }
            }
        }

        private static Scope HostScope(Document doc) => new Scope { HostDoc = doc, GeometryDoc = doc };

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            JObject request;
            try { request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JObject.Parse(paramsJson); }
            catch (JsonException ex) { return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message); }

            Document doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("No active Revit document.");

            // ---- units: input (probe_point) and output (geometry) share one setting. --
            string units = (request.Value<string>("units") ?? "mm").ToLowerInvariant();
            double toFeet, fromFeet;
            if (!DimensionReferenceRules.TryUnitScales(units, out toFeet, out fromFeet))
                return CommandResult.Fail("units must be mm, m or feet.");

            // ---- the view. Required, because reference geometry is view-dependent: the
            // same wall shows different faces to a plan and a section, and a reference
            // enumerated against no view answers a question no dimension will ever ask.
            JToken viewToken = request["view_id"];
            if (viewToken == null)
                return CommandResult.Fail("view_id is required: dimension references are enumerated against one " +
                                          "view, because that is the view a dimension would live in.");
            if (viewToken.Type != JTokenType.Integer)
                return CommandResult.Fail("view_id must be an integer element id.");
            long rawViewId = viewToken.Value<long>();
            if (!Rid.CanRepresent(rawViewId))
                return CommandResult.Fail("view_id must be a valid element id. " + Rid.RangeError(rawViewId));
            View view = doc.GetElement(Rid.Make(rawViewId)) as View;
            if (view == null)
                return CommandResult.Fail("view_id " + rawViewId + " does not identify a view in the active document.");
            if (view.IsTemplate)
                return CommandResult.Fail("view_id identifies a view TEMPLATE ('" + SafeName(view) + "'). A template " +
                                          "hosts no dimensions; pass a real view.");
            if (view is ViewSchedule || view is ViewSheet)
                return CommandResult.Fail("view_id identifies a " + (view is ViewSchedule ? "schedule" : "sheet") +
                                          " ('" + SafeName(view) + "'). Model references are enumerated against model " +
                                          "views - the kind a dimension can live in.");

            // ---- selectors. Absent = the defaults applicable to each element's class. --
            List<string> requestedSelectors = null;
            if (request["selectors"] != null)
            {
                JArray raw = request["selectors"] as JArray;
                if (raw == null || raw.Any(t => t.Type != JTokenType.String))
                    return CommandResult.Fail("selectors must be an array of strings. Known selectors: " +
                                              string.Join(", ", DimensionReferenceRules.KnownSelectors) + ".");
                string selectorError;
                if (!DimensionReferenceRules.TryParseSelectors(raw.Select(t => (string)t), out requestedSelectors,
                                                               out selectorError))
                    return CommandResult.Fail(selectorError);
            }

            // ---- the probe point, where nearest/farthest make it mandatory. -----------
            XYZ probeFeet = null;
            if (request["probe_point"] != null)
            {
                JArray p = request["probe_point"] as JArray;
                if (p == null || p.Count != 3 || p.Any(t => t.Type != JTokenType.Integer && t.Type != JTokenType.Float))
                    return CommandResult.Fail("probe_point must be an array of exactly three numbers, in the " +
                                              "request's units (" + units + ").");
                probeFeet = new XYZ(p[0].Value<double>() * toFeet, p[1].Value<double>() * toFeet,
                                    p[2].Value<double>() * toFeet);
            }
            string probeError = DimensionReferenceRules.ValidateProbeRequirement(requestedSelectors, probeFeet != null);
            if (probeError != null) return CommandResult.Fail(probeError);

            // ---- paging, refused loudly rather than clamped quietly. ------------------
            int maxResults, offset;
            string pagingError = DimensionReferenceRules.ValidatePaging(request.Value<int?>("max_results"),
                                                                        request.Value<int?>("offset"),
                                                                        out maxResults, out offset);
            if (pagingError != null) return CommandResult.Fail(pagingError);

            bool includeIncompatible = request["include_incompatible"] == null ||
                                       request.Value<bool>("include_incompatible");

            var topWarnings = new JArray();
            if (probeFeet != null && !DimensionReferenceRules.RequiresProbePoint(requestedSelectors))
                topWarnings.Add(Warning(null, null, DimensionReferenceRules.WarningProbePointUnused,
                    "probe_point was provided but no nearest_face/farthest_face selector was requested; it was not used."));

            // ---- the targets: element_ids XOR filter, plus optional linked_targets. ---
            // linked_targets is ADDITIVE rather than a third alternative: the common
            // case is a host grid and a linked wall face in one dimension, so a call
            // that names both is the useful call, not an ambiguous one.
            bool hasIds = request["element_ids"] != null;
            bool hasFilter = request["filter"] != null;
            bool hasLinked = request["linked_targets"] != null;
            if (!hasIds && !hasFilter && hasLinked)
            {
                // Only linked targets: legitimate, and the host XOR rule must not
                // refuse it for naming neither of its two host specifications.
            }
            else
            {
                string choiceError = DimensionReferenceRules.ValidateTargetChoice(hasIds, hasFilter);
                if (choiceError != null) return CommandResult.Fail(choiceError);
            }

            var unreadable = new JArray();
            var targets = new List<Element>();
            int requestedTargets = 0;
            if (hasIds)
            {
                string idError = ResolveIdTargets(doc, request["element_ids"] as JArray, targets, unreadable,
                                                  topWarnings, out requestedTargets);
                if (idError != null) return CommandResult.Fail(idError);
            }
            else if (hasFilter)
            {
                string filterError = ResolveFilterTargets(doc, request["filter"] as JObject, targets, unreadable,
                                                          out requestedTargets);
                if (filterError != null) return CommandResult.Fail(filterError);
            }

            // Deterministic inspection order: ascending element id, always.
            targets.Sort((a, b) => Rid.Value(a.Id).CompareTo(Rid.Value(b.Id)));

            // ---- the linked targets, resolved through their own instances. -----------
            var linkedTargets = new List<LinkedTarget>();
            int linkedRequested = 0;
            if (hasLinked)
            {
                string linkError = ResolveLinkedTargets(doc, request["linked_targets"], linkedTargets, unreadable,
                                                        out linkedRequested);
                if (linkError != null) return CommandResult.Fail(linkError);
                requestedTargets += linkedRequested;
                if (requestedTargets > DimensionReferenceRules.MaxTargets)
                    return CommandResult.Fail(
                        "element_ids and linked_targets together name " + requestedTargets + " targets; the limit " +
                        "is " + DimensionReferenceRules.MaxTargets + " per call. Nothing was inspected - the " +
                        "ordering is deterministic, so pages compose.");
            }

            // ---- inspect. Every candidate is computed BEFORE any paging, so the total
            // and the truncation flag are facts about the whole answer. A target that
            // blows up mid-inspection lands in coverage.unreadable - never in silence.
            var candidates = new List<Candidate>();
            int inspected = 0;
            int hostInspected = 0;
            int linkedInspected = 0;
            foreach (Element element in targets)
            {
                long id = Rid.Value(element.Id);
                try
                {
                    InspectElement(HostScope(doc), view, element, requestedSelectors, probeFeet, fromFeet, candidates,
                                   topWarnings);
                    inspected++;
                    hostInspected++;
                }
                catch (Exception ex)
                {
                    unreadable.Add(new JObject
                    {
                        ["element_id"] = id,
                        ["reason"] = "inspection failed: " + ex.Message
                    });
                }
            }
            foreach (LinkedTarget lt in linkedTargets)
            {
                try
                {
                    topWarnings.Add(Warning(lt.Scope.Binding.LinkInstanceId, null,
                        DimensionReferenceRules.WarningLinkedGeometryNotViewScoped,
                        "element " + lt.Scope.Binding.LinkedElementId + " lives in linked document '" +
                        lt.Scope.Binding.DocumentTitle + "'. Options.View names a view in the element's OWN " +
                        "document, so its geometry was read from the linked model at fine detail, without a " +
                        "view. The references are real and the coordinates are host coordinates; what this read " +
                        "cannot tell you is whether the element is VISIBLE in view " + Rid.Value(view.Id) +
                        " - check that before dimensioning to it there."));
                    InspectElement(lt.Scope, view, lt.Element, requestedSelectors, probeFeet, fromFeet, candidates,
                                   topWarnings);
                    inspected++;
                    linkedInspected++;
                }
                catch (Exception ex)
                {
                    unreadable.Add(new JObject
                    {
                        ["element_id"] = lt.Scope.Binding.LinkInstanceId,
                        ["linked_element_id"] = lt.Scope.Binding.LinkedElementId,
                        ["code"] = LinkedReferenceRules.CodeLinkReferenceUnreadable,
                        ["reason"] = "inspection of the linked element failed: " + ex.Message
                    });
                }
            }

            // ---- ambiguity: a single-answer selector with several equivalent answers
            // returns ALL of them, marked, under one group. The rule lives in Core.
            // The ambiguity group is keyed by the ELEMENT the candidates describe, and
            // for a linked candidate that is the linked element inside its instance -
            // not the link instance, which would pool every element of one link into
            // one tie and mark faces of different walls as equivalent.
            foreach (var group in candidates.GroupBy(c => c.AmbiguityScope()))
            {
                List<Candidate> members = group.ToList();
                bool ambiguous; string groupId;
                DimensionReferenceRules.ShapeAmbiguity(members[0].Selector, members[0].SubjectId(), members.Count,
                                                       out ambiguous, out groupId);
                foreach (Candidate member in members)
                {
                    member.Ambiguous = ambiguous;
                    // A linked group id keeps the instance in it: two placements of the
                    // same link produce two ties, and one shared id would merge them.
                    member.AmbiguityGroup = ambiguous && member.Link != null
                        ? member.Link.LinkInstanceId + "/" + groupId
                        : groupId;
                }
            }

            // Revit 2023 can lift and serialise linked geometry references, but
            // NewDimension cannot consume them. Three distinct live arrangements
            // all measured-rejected with "Invalid number of references". Keep the
            // rows available under include_incompatible so callers can inspect the
            // real reference and its provenance, but never advertise it as writable.
#if REVIT2023
            foreach (Candidate candidate in candidates)
                if (candidate.Link != null && candidate.Compatible)
                {
                    candidate.Compatible = false;
                    candidate.Incompatibility = DimensionReferenceRules.LinkedGeometryRejectedByRevit2023();
                }
#endif

            int incompatibleExcluded = 0;
            if (!includeIncompatible)
            {
                incompatibleExcluded = candidates.Count(c => !c.Compatible);
                candidates = candidates.Where(c => c.Compatible).ToList();
            }

            candidates.Sort(Candidate.Compare);

            PageSlice page = DimensionReferenceRules.Page(candidates.Count, maxResults, offset);
            List<Candidate> shown = candidates.Skip(page.Offset).Take(page.Count).ToList();

            long viewId = Rid.Value(view.Id);
            var result = new JObject
            {
                ["document"] = doc.Title,
                ["view_id"] = viewId,
                ["view_name"] = SafeName(view),
                ["units"] = units,
                ["selectors_requested"] = requestedSelectors == null
                    ? JValue.CreateNull() : (JToken)new JArray(requestedSelectors),
                ["selectors_defaulted"] = requestedSelectors == null,
                ["coverage"] = new JObject
                {
                    ["requested"] = requestedTargets,
                    ["inspected"] = inspected,
                    // A federated answer split by provenance. Without this a caller
                    // reading "128 candidates" over a model with an unloaded structural
                    // link has no way to tell a complete answer from a host-only one.
                    ["host_targets_inspected"] = hostInspected,
                    ["linked_targets_named"] = linkedRequested,
                    ["linked_targets_inspected"] = linkedInspected,
                    ["link_instances_named"] = linkedTargets
                        .Select(t => t.Scope.Binding.LinkInstanceId).Distinct().Count(),
                    ["unreadable"] = unreadable
                },
                ["linked_candidates"] = candidates.Count(c => c.Link != null),
                ["total_candidates"] = page.Total,
                ["returned"] = shown.Count,
                ["offset"] = page.Offset,
                ["truncated"] = page.Truncated,
                ["include_incompatible"] = includeIncompatible,
                ["incompatible_excluded"] = incompatibleExcluded,
                ["ordering"] = DimensionReferenceRules.OrderingNote,
                ["fingerprint_note"] = DimensionReferenceRules.RoundingNote,
                ["warnings"] = topWarnings,
                ["rows"] = new JArray(shown.Select(c => c.ToJson(doc.Title, viewId, fromFeet)))
            };
            return CommandResult.Ok(result);
        }

        // =====================================================================
        // Target resolution
        // =====================================================================

        private static string ResolveIdTargets(Document doc, JArray ids, List<Element> targets, JArray unreadable,
                                               JArray topWarnings, out int requestedTargets)
        {
            requestedTargets = 0;
            if (ids == null || ids.Any(t => t.Type != JTokenType.Integer))
                return "element_ids must be an array of integer element ids.";
            string countError = DimensionReferenceRules.ValidateElementIdCount(ids.Count);
            if (countError != null) return countError;

            var distinct = new List<long>();
            var seen = new HashSet<long>();
            foreach (JToken t in ids)
            {
                long id = t.Value<long>();
                if (seen.Add(id)) distinct.Add(id);
            }
            if (distinct.Count < ids.Count)
                topWarnings.Add(Warning(null, null, DimensionReferenceRules.WarningDuplicateElementIds,
                    (ids.Count - distinct.Count) + " duplicate element_ids were collapsed; each element is " +
                    "inspected once."));

            requestedTargets = distinct.Count;
            foreach (long id in distinct)
            {
                if (!Rid.CanRepresent(id))
                {
                    unreadable.Add(new JObject { ["element_id"] = id, ["reason"] = Rid.RangeError(id) });
                    continue;
                }
                Element element = doc.GetElement(Rid.Make(id));
                if (element == null)
                {
                    unreadable.Add(new JObject
                    { ["element_id"] = id, ["reason"] = "element " + id + " does not exist in the active document." });
                    continue;
                }
                if (element is RevitLinkInstance)
                {
                    unreadable.Add(new JObject
                    {
                        ["element_id"] = id,
                        ["code"] = DimensionReferenceRules.CodeLinkReferencesNotSupported,
                        ["reason"] = DimensionReferenceRules.LinkReferencesMessage(id)
                    });
                    continue;
                }
                if (element is ElementType)
                {
                    unreadable.Add(new JObject
                    {
                        ["element_id"] = id,
                        ["reason"] = "element " + id + " is an element type; dimension references belong to placed " +
                                     "instances. Pass the id of a placed instance."
                    });
                    continue;
                }
                targets.Add(element);
            }
            return null;
        }

        /// <summary>One element inside one link instance, with everything already resolved.</summary>
        private sealed class LinkedTarget { public Element Element; public Scope Scope; }

        /// <summary>
        /// Resolve linked_targets. EVERY failure below is a row in coverage.unreadable
        /// with a structured code, never a silent skip: a federation half of which was
        /// never looked at must not report like a federation all of which was.
        /// The only hard refusals are shape errors in the request itself.
        /// </summary>
        private static string ResolveLinkedTargets(Document doc, JToken raw, List<LinkedTarget> resolved,
                                                   JArray unreadable, out int requestedTargets)
        {
            requestedTargets = 0;
            var array = raw as JArray;
            if (array == null) return "linked_targets must be an array of objects.";

            var requests = new List<LinkTargetRequest>();
            foreach (JToken entryToken in array)
            {
                var entry = entryToken as JObject;
                if (entry == null) return "linked_targets entries must be JSON objects.";
                foreach (JProperty p in entry.Properties())
                    if (p.Name != "link_instance_id" && p.Name != "linked_element_ids")
                        return "linked_targets field '" + p.Name + "' is not one this command understands. " +
                               "Known: link_instance_id, linked_element_ids.";
                JToken instanceToken = entry["link_instance_id"];
                if (instanceToken == null || instanceToken.Type != JTokenType.Integer)
                    return "linked_targets entries require an integer link_instance_id - the id of a PLACED " +
                           "RevitLinkInstance in the host document.";
                var idsToken = entry["linked_element_ids"] as JArray;
                if (idsToken == null || idsToken.Any(t => t.Type != JTokenType.Integer))
                    return "linked_targets.linked_element_ids must be an array of integer element ids from " +
                           "inside the linked document.";
                var req = new LinkTargetRequest { LinkInstanceId = instanceToken.Value<long>() };
                foreach (JToken t in idsToken) req.LinkedElementIds.Add(t.Value<long>());
                requests.Add(req);
            }

            List<LinkTargetRequest> normalized;
            int total;
            string shapeError = LinkedReferenceRules.ValidateLinkTargets(requests, out normalized, out total);
            if (shapeError != null) return shapeError;
            requestedTargets = total;

            foreach (LinkTargetRequest req in normalized)
            {
                long instanceId = req.LinkInstanceId;
                if (!Rid.CanRepresent(instanceId))
                { AddLinkFailure(unreadable, instanceId, null, LinkedReferenceRules.CodeNotALinkInstance,
                                 Rid.RangeError(instanceId), req.LinkedElementIds); continue; }

                var link = doc.GetElement(Rid.Make(instanceId)) as RevitLinkInstance;
                if (link == null)
                { AddLinkFailure(unreadable, instanceId, null, LinkedReferenceRules.CodeNotALinkInstance,
                                 LinkedReferenceRules.NotALinkInstance(instanceId), req.LinkedElementIds); continue; }

                string linkName = SafeName(link);
                string status = LinkStatus(doc, link);
                if (!string.Equals(status, "Loaded", StringComparison.Ordinal))
                { AddLinkFailure(unreadable, instanceId, null, LinkedReferenceRules.CodeLinkUnloaded,
                                 LinkedReferenceRules.LinkUnloaded(instanceId, linkName, status),
                                 req.LinkedElementIds); continue; }

                Document linked = null;
                try { linked = link.GetLinkDocument(); } catch { linked = null; }
                if (linked == null)
                { AddLinkFailure(unreadable, instanceId, null, LinkedReferenceRules.CodeLinkDocumentUnavailable,
                                 LinkedReferenceRules.LinkDocumentUnavailable(instanceId, linkName),
                                 req.LinkedElementIds); continue; }

                Transform toHost;
                LinkTransformFacts facts;
                string transformFingerprint;
                if (!TryReadPlacement(link, out toHost, out facts, out transformFingerprint))
                { AddLinkFailure(unreadable, instanceId, null, LinkedReferenceRules.CodeLinkDocumentUnavailable,
                                 "link instance " + instanceId + " (" + (linkName ?? "unnamed") + ") did not " +
                                 "report a readable total transform, so nothing it carries could be placed in " +
                                 "host coordinates or fingerprinted. Nothing was inspected.",
                                 req.LinkedElementIds); continue; }

                string documentIdentity = LinkedReferenceRules.DocumentIdentity(linked.Title, SafePath(linked));
                long typeId = SafeTypeId(link);

                foreach (long linkedElementId in req.LinkedElementIds)
                {
                    if (!Rid.CanRepresent(linkedElementId))
                    { AddLinkFailure(unreadable, instanceId, linkedElementId,
                                     LinkedReferenceRules.CodeLinkedElementMissing, Rid.RangeError(linkedElementId),
                                     null); continue; }
                    Element inside = null;
                    try { inside = linked.GetElement(Rid.Make(linkedElementId)); } catch { inside = null; }
                    if (inside == null)
                    { AddLinkFailure(unreadable, instanceId, linkedElementId,
                                     LinkedReferenceRules.CodeLinkedElementMissing,
                                     LinkedReferenceRules.LinkedElementMissing(instanceId, linkedElementId,
                                                                               linked.Title), null); continue; }
                    if (inside is ElementType)
                    { AddLinkFailure(unreadable, instanceId, linkedElementId,
                                     LinkedReferenceRules.CodeLinkedElementIsType,
                                     LinkedReferenceRules.LinkedElementIsType(instanceId, linkedElementId),
                                     null); continue; }
                    if (inside is RevitLinkInstance)
                    { AddLinkFailure(unreadable, instanceId, linkedElementId,
                                     LinkedReferenceRules.CodeNestedLinkNotSupported,
                                     LinkedReferenceRules.NestedLinkNotSupported(instanceId, linkedElementId),
                                     null); continue; }

                    resolved.Add(new LinkedTarget
                    {
                        Element = inside,
                        Scope = new Scope
                        {
                            HostDoc = doc,
                            GeometryDoc = linked,
                            Link = link,
                            ToHost = toHost,
                            Binding = new LinkBinding
                            {
                                LinkInstanceId = instanceId,
                                InstanceUniqueId = Safe(() => link.UniqueId),
                                LinkTypeId = typeId,
                                LinkName = linkName,
                                DocumentTitle = linked.Title,
                                DocumentIdentity = documentIdentity,
                                LinkedElementId = linkedElementId,
                                LinkedElementUniqueId = Safe(() => inside.UniqueId),
                                LinkedElementCategory = Safe(() => inside.Category?.Name),
                                LinkedElementClass = inside.GetType().Name,
                                TransformFingerprint = transformFingerprint,
                                Transform = facts
                            }
                        }
                    });
                }
            }

            // Deterministic: instance ascending, then linked element ascending.
            resolved.Sort((a, b) => LinkedReferenceRules.CompareProvenance(a.Scope.Binding, b.Scope.Binding));
            return null;
        }

        private static void AddLinkFailure(JArray unreadable, long instanceId, long? linkedElementId, string code,
                                           string reason, List<long> coveredIds)
        {
            if (coveredIds != null && coveredIds.Count > 0)
            {
                // The whole instance failed. Every element the caller asked about inside
                // it is unreadable, and each gets its own row: a caller reconciling ids
                // must never have to infer which of its targets a single row stood for.
                foreach (long id in coveredIds)
                    unreadable.Add(new JObject
                    {
                        ["element_id"] = instanceId,
                        ["linked_element_id"] = id,
                        ["code"] = code,
                        ["reason"] = reason
                    });
                return;
            }
            var row = new JObject { ["element_id"] = instanceId, ["code"] = code, ["reason"] = reason };
            if (linkedElementId.HasValue) row["linked_element_id"] = linkedElementId.Value;
            unreadable.Add(row);
        }

        /// <summary>The link TYPE's loaded status as a plain string, or "Unknown" when it cannot be read.</summary>
        private static string LinkStatus(Document doc, RevitLinkInstance link)
        {
            try
            {
                var type = doc.GetElement(link.GetTypeId()) as RevitLinkType;
                if (type == null) return "Unknown";
                return type.GetLinkedFileStatus().ToString();
            }
            catch { return "Unknown"; }
        }

        /// <summary>
        /// The instance's placement, read once: the Transform used for every coordinate
        /// and the facts fingerprinted into the plan. GetTotalTransform is the one that
        /// includes the shared-coordinates position of the linked model, which is what
        /// a caller sees on screen; GetTransform alone would be right only for a link
        /// placed origin-to-origin.
        /// </summary>
        private static bool TryReadPlacement(RevitLinkInstance link, out Transform toHost,
                                             out LinkTransformFacts facts, out string fingerprint)
        {
            toHost = null; facts = null; fingerprint = null;
            try
            {
                toHost = link.GetTotalTransform();
                if (toHost == null) return false;
                facts = new LinkTransformFacts
                {
                    Origin = new[] { toHost.Origin.X, toHost.Origin.Y, toHost.Origin.Z },
                    BasisX = new[] { toHost.BasisX.X, toHost.BasisX.Y, toHost.BasisX.Z },
                    BasisY = new[] { toHost.BasisY.X, toHost.BasisY.Y, toHost.BasisY.Z },
                    BasisZ = new[] { toHost.BasisZ.X, toHost.BasisZ.Y, toHost.BasisZ.Z },
                    Determinant = toHost.Determinant,
                    IsIdentity = toHost.IsIdentity,
                    HasRotation = !toHost.BasisX.IsAlmostEqualTo(XYZ.BasisX) ||
                                  !toHost.BasisY.IsAlmostEqualTo(XYZ.BasisY) ||
                                  !toHost.BasisZ.IsAlmostEqualTo(XYZ.BasisZ)
                };
                fingerprint = LinkedReferenceRules.TransformFingerprint(facts);
                return true;
            }
            catch { toHost = null; facts = null; fingerprint = null; return false; }
        }

        private static long SafeTypeId(Element element)
        { try { return Rid.Value(element.GetTypeId()); } catch { return 0; } }

        private static string SafePath(Document document)
        { try { return document.PathName; } catch { return null; } }

        private static string ResolveFilterTargets(Document doc, JObject filter, List<Element> targets,
                                                   JArray unreadable, out int requestedTargets)
        {
            requestedTargets = 0;
            if (filter == null) return "filter must be a JSON object.";
            foreach (JProperty p in filter.Properties())
                if (p.Name != "categories" && p.Name != "family" && p.Name != "type" &&
                    p.Name != "name" && p.Name != "level")
                    return "filter field '" + p.Name + "' is not one this command understands. Known: categories, " +
                           "family, type, name, level.";

            HashSet<long> categoryIds = null;
            JArray categories = filter["categories"] as JArray;
            if (categories != null && categories.Count > 0)
            {
                categoryIds = new HashSet<long>();
                foreach (JToken t in categories)
                {
                    string categoryText = t.Type == JTokenType.String ? (string)t : null;
                    if (string.IsNullOrWhiteSpace(categoryText))
                        return "filter.categories entries must be non-empty strings (OST_ tokens or display names).";
                    Category category = ResolveCategory(doc, categoryText);
                    if (category == null)
                        return "filter category '" + categoryText + "' was not found in the active document. " +
                               "Use a BuiltInCategory token (OST_...) or the display name.";
                    categoryIds.Add(Rid.Value(category.Id));
                }
            }

            string familyWant = filter.Value<string>("family");
            string typeWant = filter.Value<string>("type");
            string nameWant = filter.Value<string>("name");
            string levelWant = filter.Value<string>("level");

            foreach (Element element in new FilteredElementCollector(doc).WhereElementIsNotElementType())
            {
                long id = Rid.Value(element.Id);
                try
                {
                    if (categoryIds != null)
                    {
                        Category c = element.Category;
                        if (c == null || !categoryIds.Contains(Rid.Value(c.Id))) continue;
                    }
                    Element type = doc.GetElement(element.GetTypeId());
                    string family = type is ElementType et ? Safe(() => et.FamilyName) : null;
                    if (!Contains(SafeName(element), nameWant) || !Contains(family, familyWant) ||
                        !Contains(type == null ? null : SafeName(type), typeWant) ||
                        !Contains(LevelName(doc, element), levelWant)) continue;

                    if (element is RevitLinkInstance)
                    {
                        // The filter WOULD include it, so its exclusion must be visible: a
                        // link instance is never inspected, and a silent skip would make the
                        // coverage look complete over a set it quietly shrank.
                        requestedTargets++;
                        unreadable.Add(new JObject
                        {
                            ["element_id"] = id,
                            ["code"] = DimensionReferenceRules.CodeLinkReferencesNotSupported,
                            ["reason"] = DimensionReferenceRules.LinkReferencesMessage(id)
                        });
                        continue;
                    }
                    requestedTargets++;
                    targets.Add(element);
                }
                catch (Exception ex)
                {
                    requestedTargets++;
                    unreadable.Add(new JObject
                    { ["element_id"] = id, ["reason"] = "filter match could not be evaluated: " + ex.Message });
                }
            }

            if (requestedTargets > DimensionReferenceRules.MaxTargets)
                return DimensionReferenceRules.FilterTooBroadError(requestedTargets);
            return null;
        }

        // =====================================================================
        // Per-element inspection
        // =====================================================================

        private static void InspectElement(Scope scope, View view, Element element, List<string> requestedSelectors,
                                           XYZ probeFeet, double fromFeet, List<Candidate> rows, JArray topWarnings)
        {
            // The id a ROW carries is a host-document id: for a linked element that is
            // the link instance, and the linked element's own id travels in the link
            // block beside it. Putting the linked id here would describe an element the
            // caller's document does not contain.
            long id = scope.IsLinked ? scope.Binding.LinkInstanceId : Rid.Value(element.Id);
            string uniqueId = scope.IsLinked ? Safe(() => scope.Link.UniqueId) : Safe(() => element.UniqueId);

            var traits = new ElementTraits
            {
                IsGrid = element is Grid,
                IsLevel = element is Level,
                IsReferencePlane = element is ReferencePlane,
                IsHostObject = element is HostObject,
                HasLocationCurve = (element.Location as LocationCurve)?.Curve != null
            };

            // One geometry read per element, references on, scoped to the requested
            // view. When the view yields nothing (the element may simply not show
            // there) the DOCUMENTED fallback is fine detail with no view - and every
            // candidate built from it says so, because a reference enumerated outside
            // the view is a weaker claim than one enumerated in it.
            var solids = new List<SolidRec>();
            var curves = new List<CurveRec>();
            bool fellBack = false;
            if (!traits.IsDatum)
            {
                GeometryElement ge = ReadGeometry(scope, element, view, includeNonVisible: false,
                                                  fellBack: out fellBack);
                // The accumulated transform STARTS at the link's placement, so every
                // point, curve and face this walk produces is already in host
                // coordinates and no later step has to remember to convert.
                CollectGeometry(ge, scope.ToHost, solids, curves, 0);
                traits.HasSolidGeometry = solids.Any(s => s.Solid.Faces.Size > 0);
                traits.HasCurveGeometry = curves.Count > 0;
            }

            IReadOnlyList<string> selectors;
            if (requestedSelectors == null)
            {
                selectors = DimensionReferenceRules.ApplicableSelectors(traits);
                if (selectors.Count == 0)
                    topWarnings.Add(Warning(id, null, DimensionReferenceRules.WarningNoApplicableSelectors,
                        "element " + id + " has no applicable selectors (no location curve, host faces, solid or " +
                        "curve geometry); no candidates were produced for it."));
            }
            else
            {
                var applicable = new List<string>();
                foreach (string selector in requestedSelectors)
                {
                    if (DimensionReferenceRules.SelectorApplies(selector, traits)) applicable.Add(selector);
                    else
                        topWarnings.Add(Warning(id, selector, DimensionReferenceRules.WarningSelectorNotApplicable,
                            DimensionReferenceRules.WhyNotApplicable(selector, id, traits)));
                }
                selectors = applicable;
            }

            foreach (string selector in selectors)
            {
                switch (selector)
                {
                    case DimensionReferenceRules.SelectorGrid:
                    case DimensionReferenceRules.SelectorLevel:
                    case DimensionReferenceRules.SelectorReferencePlane:
                        ProduceDatum(scope, element, id, uniqueId, selector, rows, topWarnings);
                        break;
                    case DimensionReferenceRules.SelectorCenterline:
                        ProduceCenterline(scope, view, element, id, uniqueId, rows, topWarnings);
                        break;
                    case DimensionReferenceRules.SelectorExteriorFace:
                    case DimensionReferenceRules.SelectorInteriorFace:
                        ProduceSideFaces(scope, element, id, uniqueId, selector, fellBack, rows, topWarnings);
                        break;
                    case DimensionReferenceRules.SelectorEdge:
                        ProduceEdges(scope, id, uniqueId, solids, fellBack, rows, topWarnings);
                        break;
                    case DimensionReferenceRules.SelectorEndpoint:
                        ProduceEndpoints(scope, id, uniqueId, curves, fellBack, rows, topWarnings);
                        break;
                    case DimensionReferenceRules.SelectorNearestFace:
                    case DimensionReferenceRules.SelectorFarthestFace:
                        ProduceProbedFaces(scope, id, uniqueId, selector, solids, probeFeet, fellBack, rows,
                                           topWarnings);
                        break;
                }
            }
        }

        // ---- datums ---------------------------------------------------------

        private static void ProduceDatum(Scope scope, Element element, long id, string uniqueId, string selector,
                                         List<Candidate> rows, JArray topWarnings)
        {
            try
            {
                if (element is Grid grid)
                {
                    var c = new Candidate(id, uniqueId, selector, "grid", scope.Binding);
                    if (!TryStable(scope, new Reference(element), c, topWarnings)) return;
                    ShapeCurveGeometry(c, grid.Curve, scope.ToHost);
                    MarkLinkedDatum(scope, c);
                    rows.Add(c);
                }
                else if (element is Level level)
                {
                    var c = new Candidate(id, uniqueId, selector, "level", scope.Binding);
                    if (!TryStable(scope, new Reference(element), c, topWarnings)) return;
                    // A level IS a horizontal plane at its project elevation; that is
                    // exactly what a spot elevation or a linear dimension measures to.
                    // Through a link the plane travels with the placement: a linked
                    // level sits at the host elevation the link puts it at, not at the
                    // number the linked model prints on it.
                    XYZ origin = scope.ToHost.OfPoint(new XYZ(0, 0, level.ProjectElevation));
                    XYZ normal = scope.ToHost.OfVector(XYZ.BasisZ).Normalize();
                    ShapePlaneGeometry(c, origin, normal, area: null);
                    MarkLinkedDatum(scope, c);
                    rows.Add(c);
                }
                else if (element is ReferencePlane rp)
                {
                    var c = new Candidate(id, uniqueId, selector, "reference_plane", scope.Binding);
                    if (!TryStable(scope, rp.GetReference(), c, topWarnings)) return;
                    Plane plane = rp.GetPlane();
                    ShapePlaneGeometry(c, scope.ToHost.OfPoint(plane.Origin),
                                       scope.ToHost.OfVector(plane.Normal).Normalize(), area: null);
                    MarkLinkedDatum(scope, c);
                    rows.Add(c);
                }
            }
            catch (Exception ex)
            {
                topWarnings.Add(Warning(id, selector, DimensionReferenceRules.WarningCandidateUnreadable,
                    "datum reference could not be read: " + ex.Message));
            }
        }

        // ---- centerline -----------------------------------------------------

        private static void ProduceCenterline(Scope scope, View view, Element element, long id, string uniqueId,
                                              List<Candidate> rows, JArray topWarnings)
        {
            const string selector = DimensionReferenceRules.SelectorCenterline;
            Curve located = (element.Location as LocationCurve)?.Curve;
            if (located == null) return; // applicability already required it
            // In HOST space from here on, so the endpoint match below and every fact
            // reported compare inside one coordinate system.
            Curve location;
            try { location = located.CreateTransformed(scope.ToHost); } catch { location = located; }
            if (!location.IsBound)
            {
                rows.Add(NegativeCenterline(id, uniqueId, location, scope.Binding,
                    DimensionReferenceRules.NoStableCenterline("the location curve is unbound")));
                return;
            }

            // The stable centerline reference lives in the NON-VISIBLE geometry: the
            // line Revit itself keeps under the element. Read with references on and
            // non-visible objects included, and match by endpoints against the
            // location curve - within 1e-6 ft, in either direction.
            bool fellBack;
            GeometryElement ge = ReadGeometry(scope, element, view, includeNonVisible: true, fellBack: out fellBack);
            var solids = new List<SolidRec>();
            var curves = new List<CurveRec>();
            CollectGeometry(ge, scope.ToHost, solids, curves, 0);

            XYZ a = location.GetEndPoint(0), b = location.GetEndPoint(1);
            var matches = new List<Candidate>();
            var seenStable = new HashSet<string>(StringComparer.Ordinal);
            foreach (CurveRec rec in curves)
            {
                if (rec.Reference == null) continue;
                Curve world;
                try { world = rec.Curve.CreateTransformed(rec.Tx); } catch { continue; }
                if (!world.IsBound) continue;
                XYZ p = world.GetEndPoint(0), q = world.GetEndPoint(1);
                bool same = p.DistanceTo(a) <= CenterlineMatchToleranceFeet &&
                            q.DistanceTo(b) <= CenterlineMatchToleranceFeet;
                bool reversed = p.DistanceTo(b) <= CenterlineMatchToleranceFeet &&
                                q.DistanceTo(a) <= CenterlineMatchToleranceFeet;
                if (!same && !reversed) continue;

                var c = new Candidate(id, uniqueId, selector, "centerline", scope.Binding);
                if (!TryStable(scope, rec.Reference, c, topWarnings)) continue;
                if (!seenStable.Add(c.StableRepresentation)) continue; // one row per reference
                ShapeCurveGeometry(c, rec.Curve, rec.Tx);
                if (fellBack) c.Warnings.Add(FallbackWarning());
                // Measured live (2025, 2026-08-24): NewDimension refuses MEP-curve
                // centerline references outright. The row still travels - the reference
                // is real - but compatible_with_dimension must not promise a creation
                // Revit will refuse.
                if (element is MEPCurve)
                {
                    c.Compatible = false;
                    c.Incompatibility = DimensionReferenceRules.MepCenterlineRejected();
                }
                matches.Add(c);
            }

            if (matches.Count == 0)
            {
                // No guess: the row exists so the caller learns WHY, with a code, and
                // can dimension to faces or edges instead.
                rows.Add(NegativeCenterline(id, uniqueId, location, scope.Binding,
                                            DimensionReferenceRules.NoStableCenterline(null)));
                return;
            }
            rows.AddRange(matches); // several equivalents: ambiguity shaping marks them all
        }

        /// <summary>
        /// The measured 2026-08-26 rule: a datum reference THROUGH a link is real,
        /// stable, and rejected by NewDimension ('Invalid number of references'),
        /// while linked geometry references construct. The row still travels - the
        /// reference has other uses - but compatible_with_dimension must not promise
        /// a creation Revit will refuse.
        /// </summary>
        private static void MarkLinkedDatum(Scope scope, Candidate c)
        {
            if (!scope.IsLinked) return;
            c.Compatible = false;
            c.Incompatibility = DimensionReferenceRules.LinkedDatumRejected();
        }

        private static Candidate NegativeCenterline(long id, string uniqueId, Curve hostSpaceLocation,
                                                    LinkBinding binding, IncompatibilityReason reason)
        {
            var c = new Candidate(id, uniqueId, DimensionReferenceRules.SelectorCenterline, "centerline", binding);
            ShapeCurveGeometry(c, hostSpaceLocation, Transform.Identity);
            c.Compatible = false;
            c.Incompatibility = reason;
            return c;
        }

        // ---- side faces (walls and other host objects) ----------------------

        private static void ProduceSideFaces(Scope scope, Element element, long id, string uniqueId, string selector,
                                             bool fellBack, List<Candidate> rows, JArray topWarnings)
        {
            var host = element as HostObject;
            if (host == null) return; // applicability already required it
            IList<Reference> sideRefs;
            try
            {
                sideRefs = HostObjectUtils.GetSideFaces(host,
                    selector == DimensionReferenceRules.SelectorExteriorFace
                        ? ShellLayerType.Exterior : ShellLayerType.Interior);
            }
            catch (Exception ex)
            {
                topWarnings.Add(Warning(id, selector, DimensionReferenceRules.WarningCandidateUnreadable,
                    "side faces could not be read: " + ex.Message));
                return;
            }
            foreach (Reference r in sideRefs)
            {
                if (r == null) continue;
                try
                {
                    var face = element.GetGeometryObjectFromReference(r) as Face;
                    if (face == null)
                    {
                        topWarnings.Add(Warning(id, selector, DimensionReferenceRules.WarningCandidateUnreadable,
                            "a side-face reference did not resolve to a face."));
                        continue;
                    }
                    var c = new Candidate(id, uniqueId, selector, "face", scope.Binding);
                    if (!TryStable(scope, r, c, topWarnings)) continue;
                    ShapeFaceGeometry(c, face, scope.ToHost);
                    if (fellBack) c.Warnings.Add(FallbackWarning());
                    rows.Add(c);
                }
                catch (Exception ex)
                {
                    topWarnings.Add(Warning(id, selector, DimensionReferenceRules.WarningCandidateUnreadable,
                        "side face could not be shaped: " + ex.Message));
                }
            }
        }

        // ---- edges ----------------------------------------------------------

        private static void ProduceEdges(Scope scope, long id, string uniqueId, List<SolidRec> solids, bool fellBack,
                                         List<Candidate> rows, JArray topWarnings)
        {
            const string selector = DimensionReferenceRules.SelectorEdge;
            foreach (SolidRec rec in solids)
            {
                foreach (Edge edge in rec.Solid.Edges)
                {
                    try
                    {
                        if (edge.Reference == null) continue;
                        Curve curve = edge.AsCurve();
                        if (curve == null) continue;
                        var c = new Candidate(id, uniqueId, selector, "edge", scope.Binding);
                        if (!TryStable(scope, edge.Reference, c, topWarnings)) continue;
                        ShapeCurveGeometry(c, curve, rec.Tx);
                        if (fellBack) c.Warnings.Add(FallbackWarning());
                        rows.Add(c);
                    }
                    catch (Exception ex)
                    {
                        topWarnings.Add(Warning(id, selector, DimensionReferenceRules.WarningCandidateUnreadable,
                            "an edge could not be shaped: " + ex.Message));
                    }
                }
            }
        }

        // ---- endpoints ------------------------------------------------------

        private static void ProduceEndpoints(Scope scope, long id, string uniqueId, List<CurveRec> curves,
                                             bool fellBack, List<Candidate> rows, JArray topWarnings)
        {
            const string selector = DimensionReferenceRules.SelectorEndpoint;
            foreach (CurveRec rec in curves)
            {
                if (rec.Reference == null || !rec.Curve.IsBound) continue;
                for (int end = 0; end < 2; end++)
                {
                    try
                    {
                        Reference epRef = rec.Curve.GetEndPointReference(end);
                        if (epRef == null) continue;
                        var c = new Candidate(id, uniqueId, selector, "endpoint", scope.Binding);
                        if (!TryStable(scope, epRef, c, topWarnings)) continue;
                        ShapePointGeometry(c, rec.Tx.OfPoint(rec.Curve.GetEndPoint(end)));
                        if (fellBack) c.Warnings.Add(FallbackWarning());
                        rows.Add(c);
                    }
                    catch (Exception ex)
                    {
                        topWarnings.Add(Warning(id, selector,
                            DimensionReferenceRules.WarningEndpointReferenceUnavailable,
                            "endpoint " + end + " of a curve has no usable reference: " + ex.Message));
                    }
                }
            }
        }

        // ---- nearest / farthest face ----------------------------------------

        private static void ProduceProbedFaces(Scope scope, long id, string uniqueId, string selector,
                                               List<SolidRec> solids, XYZ probeFeet, bool fellBack,
                                               List<Candidate> rows, JArray topWarnings)
        {
            if (probeFeet == null) return; // validated up front; kept as a hard guard
            var found = new List<ProbedFace>();
            foreach (SolidRec rec in solids)
            {
                // Distance to the FACE, not to its infinite plane. A probe 3 m from the
                // physical face of one wing of an L can sit 0 mm from that face's PLANE,
                // and plane distance would rank the wrong face as nearest with nothing
                // marking the substitution. Face.Project answers the real question when
                // the projection lands on the face; when it falls off, the closest point
                // of the face lies on its boundary, so the minimum distance to the edge
                // curves IS the face distance. Both are measured in the solid's own
                // space, where a rigid instance transform preserves distances.
                Transform inverse;
                try { inverse = rec.Tx.Inverse; }
                catch { inverse = null; }
                XYZ localProbe = inverse == null ? probeFeet : inverse.OfPoint(probeFeet);
                foreach (Face face in rec.Solid.Faces)
                {
                    try
                    {
                        var planar = face as PlanarFace;
                        if (planar == null || face.Reference == null) continue; // planar faces only, by contract
                        double distance;
                        IntersectionResult onFace = face.Project(localProbe);
                        if (onFace != null) distance = onFace.Distance;
                        else
                        {
                            distance = double.MaxValue;
                            foreach (EdgeArray loop in face.EdgeLoops)
                                foreach (Edge edge in loop)
                                {
                                    Curve boundary = edge.AsCurve();
                                    if (boundary == null) continue;
                                    double d = boundary.Distance(localProbe);
                                    if (d < distance) distance = d;
                                }
                            if (distance == double.MaxValue)
                                throw new InvalidOperationException(
                                    "the projection fell off the face and no boundary edge could be measured");
                        }
                        found.Add(new ProbedFace { Reference = face.Reference, Face = face, Tx = rec.Tx,
                                                   DistanceFeet = distance });
                    }
                    catch (Exception ex)
                    {
                        topWarnings.Add(Warning(id, selector, DimensionReferenceRules.WarningCandidateUnreadable,
                            "a face could not be measured against the probe point: " + ex.Message));
                    }
                }
            }
            if (found.Count == 0) return;

            // The tie rule lives in Core: everything within 0.1 mm of the winner comes
            // back, and the ambiguity shaping marks the set. Picking one of two faces
            // the measurement cannot separate would be a guess presented as an answer.
            List<int> tied = DimensionReferenceRules.TiedIndices(
                found.Select(f => f.DistanceFeet).ToList(),
                selector == DimensionReferenceRules.SelectorFarthestFace);
            foreach (int index in tied)
            {
                ProbedFace pf = found[index];
                var c = new Candidate(id, uniqueId, selector, "face", scope.Binding);
                if (!TryStable(scope, pf.Reference, c, topWarnings)) continue;
                ShapeFaceGeometry(c, pf.Face, pf.Tx);
                c.DistanceFeet = pf.DistanceFeet;
                if (fellBack) c.Warnings.Add(FallbackWarning());
                rows.Add(c);
            }
        }

        // =====================================================================
        // Geometry reading and shaping
        // =====================================================================

        private static GeometryElement ReadGeometry(Scope scope, Element element, View view, bool includeNonVisible,
                                                    out bool fellBack)
        {
            fellBack = false;
            GeometryElement ge = null;
            // Options.View names a view in the ELEMENT'S OWN document. For a linked
            // element the requested view belongs to the host, so scoping the read to
            // it is not merely unhelpful - it is a cross-document argument Revit is
            // entitled to reject. The linked read goes straight to fine detail, and
            // InspectElement has already published one warning saying so; it does NOT
            // set fellBack, because that flag means "the view showed nothing", which
            // is a different and weaker claim than "no view could apply here".
            if (!scope.IsLinked)
            {
                try
                {
                    ge = element.get_Geometry(new Options
                    {
                        ComputeReferences = true,
                        IncludeNonVisibleObjects = includeNonVisible,
                        View = view
                    });
                }
                catch { ge = null; }
                if (ge != null && ge.Any()) return ge;
            }

            // The documented fallback: fine detail, no view. Weaker - the element may
            // not even show in the requested view - which is why every candidate built
            // from it carries a warning instead of looking view-scoped.
            try
            {
                GeometryElement fine = element.get_Geometry(new Options
                {
                    ComputeReferences = true,
                    IncludeNonVisibleObjects = includeNonVisible,
                    DetailLevel = ViewDetailLevel.Fine
                });
                if (fine != null && fine.Any()) { fellBack = !scope.IsLinked; return fine; }
            }
            catch { /* reported by the caller as "no geometry" via zero candidates */ }
            return ge;
        }

        private static void CollectGeometry(GeometryElement ge, Transform tx, List<SolidRec> solids,
                                            List<CurveRec> curves, int depth)
        {
            if (ge == null || depth > 8) return;
            foreach (GeometryObject go in ge)
            {
                if (go is Solid solid)
                {
                    if (solid.Faces.Size > 0 || solid.Edges.Size > 0)
                        solids.Add(new SolidRec { Solid = solid, Tx = tx });
                }
                else if (go is Curve curve)
                {
                    curves.Add(new CurveRec { Curve = curve, Reference = curve.Reference, Tx = tx });
                }
                else if (go is GeometryInstance instance)
                {
                    // SYMBOL geometry carries the references (instance geometry carries
                    // none); its coordinates are symbol-space, so the accumulated
                    // transform travels alongside for every reported point.
                    CollectGeometry(instance.GetSymbolGeometry(), tx.Multiply(instance.Transform),
                                    solids, curves, depth + 1);
                }
                else if (go is GeometryElement nested)
                {
                    CollectGeometry(nested, tx, solids, curves, depth + 1);
                }
            }
        }

        private static void ShapeFaceGeometry(Candidate c, Face face, Transform tx)
        {
            double? area = null;
            try { area = face.Area; } catch { /* stays unmeasured; the fingerprint simply omits it */ }
            if (face is PlanarFace planar)
            {
                XYZ normal = tx.OfVector(planar.FaceNormal).Normalize();
                XYZ origin = tx.OfPoint(planar.Origin);
                ShapePlaneGeometry(c, origin, normal, area);
                XYZ mid = FaceMidpoint(face, tx);
                if (mid != null) c.Representative = mid;
                return;
            }
            c.GeometryKind = FaceKind(face);
            c.Geometry = new JObject { ["kind"] = c.GeometryKind };
            c.Representative = FaceMidpoint(face, tx);
            var facts = new GeometryFacts().Add("kind", c.GeometryKind);
            if (c.Representative != null)
                facts.AddXyz("representative", c.Representative.X, c.Representative.Y, c.Representative.Z);
            if (area.HasValue) facts.Add("area", area.Value);
            c.Fingerprint = DimensionReferenceRules.GeometryFingerprint(facts);
            ApplyCompatibility(c);
        }

        private static void ShapePlaneGeometry(Candidate c, XYZ origin, XYZ normal, double? area)
        {
            c.GeometryKind = "plane";
            c.Geometry = new JObject
            {
                ["kind"] = "plane",
                ["origin"] = PointToken(origin),   // scaled at serialisation time
                ["normal"] = VectorToken(normal)
            };
            c.Representative = origin;
            // The plane's identity is orientation + signed offset from the model
            // origin: stable however the face's parameterisation shifts, and distinct
            // for the two sides of a wall because the normals oppose.
            var facts = new GeometryFacts()
                .Add("kind", "plane")
                .AddXyz("normal", normal.X, normal.Y, normal.Z)
                .Add("offset", origin.DotProduct(normal));
            if (area.HasValue) facts.Add("area", area.Value);
            c.Fingerprint = DimensionReferenceRules.GeometryFingerprint(facts);
            ApplyCompatibility(c);
        }

        private static void ShapeCurveGeometry(Candidate c, Curve curve, Transform tx)
        {
            Curve world;
            try { world = curve.CreateTransformed(tx); } catch { world = curve; }
            if (world is Line line && world.IsBound)
            {
                XYZ start = line.GetEndPoint(0), end = line.GetEndPoint(1);
                c.GeometryKind = "line";
                c.Geometry = new JObject
                {
                    ["kind"] = "line",
                    ["start"] = PointToken(start),
                    ["end"] = PointToken(end),
                    ["direction"] = VectorToken(line.Direction)
                };
                c.Representative = world.Evaluate(0.5, true);
                // Endpoints are ordered canonically before hashing so the same line
                // enumerated the other way round keeps its identity.
                XYZ first = start, second = end;
                if (!LexBefore(first, second)) { first = end; second = start; }
                c.Fingerprint = DimensionReferenceRules.GeometryFingerprint(new GeometryFacts()
                    .Add("kind", "line")
                    .AddXyz("p0", first.X, first.Y, first.Z)
                    .AddXyz("p1", second.X, second.Y, second.Z));
            }
            else if (world is Arc arc && world.IsBound)
            {
                XYZ start = arc.GetEndPoint(0), end = arc.GetEndPoint(1);
                c.GeometryKind = "arc";
                c.Geometry = new JObject
                {
                    ["kind"] = "arc",
                    ["center"] = PointToken(arc.Center),
                    ["radius"] = arc.Radius,   // scaled at serialisation time
                    ["normal"] = VectorToken(arc.Normal),
                    ["start"] = PointToken(start),
                    ["end"] = PointToken(end)
                };
                c.Representative = world.Evaluate(0.5, true);
                XYZ axis = CanonicalAxis(arc.Normal);
                XYZ first = start, second = end;
                if (!LexBefore(first, second)) { first = end; second = start; }
                c.Fingerprint = DimensionReferenceRules.GeometryFingerprint(new GeometryFacts()
                    .Add("kind", "arc")
                    .AddXyz("center", arc.Center.X, arc.Center.Y, arc.Center.Z)
                    .Add("radius", arc.Radius)
                    .AddXyz("axis", axis.X, axis.Y, axis.Z)
                    .AddXyz("p0", first.X, first.Y, first.Z)
                    .AddXyz("p1", second.X, second.Y, second.Z));
            }
            else
            {
                c.GeometryKind = CurveKind(world);
                c.Geometry = new JObject { ["kind"] = c.GeometryKind };
                var facts = new GeometryFacts().Add("kind", c.GeometryKind);
                if (world.IsBound)
                {
                    XYZ start = world.GetEndPoint(0), end = world.GetEndPoint(1);
                    c.Geometry["start"] = PointToken(start);
                    c.Geometry["end"] = PointToken(end);
                    c.Representative = world.Evaluate(0.5, true);
                    XYZ first = start, second = end;
                    if (!LexBefore(first, second)) { first = end; second = start; }
                    facts.AddXyz("p0", first.X, first.Y, first.Z).AddXyz("p1", second.X, second.Y, second.Z);
                }
                c.Fingerprint = DimensionReferenceRules.GeometryFingerprint(facts);
            }
            ApplyCompatibility(c);
        }

        private static void ShapePointGeometry(Candidate c, XYZ point)
        {
            c.GeometryKind = "point";
            c.Geometry = new JObject { ["kind"] = "point", ["point"] = PointToken(point) };
            c.Representative = point;
            c.Fingerprint = DimensionReferenceRules.GeometryFingerprint(new GeometryFacts()
                .Add("kind", "point").AddXyz("point", point.X, point.Y, point.Z));
            ApplyCompatibility(c);
        }

        private static void ApplyCompatibility(Candidate c)
        {
            IncompatibilityReason reason =
                DimensionReferenceRules.ClassifyForDimension(c.ReferenceType, c.GeometryKind);
            if (reason != null) { c.Compatible = false; c.Incompatibility = reason; }
        }

        // =====================================================================
        // Small helpers
        // =====================================================================

        /// <summary>
        /// Lift the reference into the host document (a no-op when there is no link)
        /// and serialise it there. Both halves can fail and they fail for different
        /// reasons, so they are reported separately: a reference Revit would not lift
        /// is a capability limit of CreateLinkReference, while one that lifts and will
        /// not serialise is a reference the caller could never send back.
        /// </summary>
        private static bool TryStable(Scope scope, Reference reference, Candidate c, JArray topWarnings)
        {
            Reference lifted = scope.Lift(reference);
            if (lifted == null)
            {
                topWarnings.Add(LinkWarning(c, LinkedReferenceRules.CodeLinkReferenceNotCreatable,
                    LinkedReferenceRules.LinkReferenceNotCreatable(
                        scope.Binding == null ? 0 : scope.Binding.LinkInstanceId,
                        scope.Binding == null ? 0 : scope.Binding.LinkedElementId,
                        "a " + c.ReferenceType + " reference")));
                return false;
            }
            try
            {
                c.StableRepresentation = lifted.ConvertToStableRepresentation(scope.HostDoc);
                return c.StableRepresentation != null;
            }
            catch (Exception ex)
            {
                // A reference the caller cannot re-send is not an answer; the warning
                // says so instead of a row carrying an unusable identity.
                topWarnings.Add(Warning(c.ElementId, c.Selector,
                    DimensionReferenceRules.WarningStableRepresentationUnavailable,
                    "a " + c.ReferenceType + " reference could not be serialised: " + ex.Message));
                return false;
            }
        }

        private static JObject LinkWarning(Candidate c, string code, string message)
        {
            var w = new JObject { ["code"] = code, ["message"] = message, ["element_id"] = c.ElementId };
            if (c.Selector != null) w["selector"] = c.Selector;
            if (c.Link != null) w["linked_element_id"] = c.Link.LinkedElementId;
            return w;
        }

        private static XYZ FaceMidpoint(Face face, Transform tx)
        {
            try
            {
                BoundingBoxUV bb = face.GetBoundingBox();
                var mid = new UV((bb.Min.U + bb.Max.U) / 2, (bb.Min.V + bb.Max.V) / 2);
                return tx.OfPoint(face.Evaluate(mid));
            }
            catch { return null; }
        }

        private static string FaceKind(Face face)
        {
            if (face is PlanarFace) return "plane";
            if (face is CylindricalFace) return "cylindrical_face";
            if (face is ConicalFace) return "conical_face";
            if (face is RevolvedFace) return "revolved_face";
            if (face is RuledFace) return "ruled_face";
            if (face is HermiteFace) return "hermite_face";
            return face == null ? "unknown_face" : face.GetType().Name.ToLowerInvariant();
        }

        private static string CurveKind(Curve curve)
        {
            if (curve is Line) return "line";
            if (curve is Arc) return "arc";
            if (curve is Ellipse) return "ellipse";
            if (curve is HermiteSpline) return "hermite_spline";
            if (curve is NurbSpline) return "nurb_spline";
            return curve == null ? "unknown_curve" : curve.GetType().Name.ToLowerInvariant();
        }

        private static XYZ CanonicalAxis(XYZ n)
        {
            long x = DimensionReferenceRules.QuantizeFeet(n.X);
            long y = DimensionReferenceRules.QuantizeFeet(n.Y);
            long z = DimensionReferenceRules.QuantizeFeet(n.Z);
            long first = x != 0 ? x : (y != 0 ? y : z);
            return first < 0 ? n.Negate() : n;
        }

        private static bool LexBefore(XYZ a, XYZ b)
        {
            long ax = DimensionReferenceRules.QuantizeFeet(a.X), bx = DimensionReferenceRules.QuantizeFeet(b.X);
            if (ax != bx) return ax < bx;
            long ay = DimensionReferenceRules.QuantizeFeet(a.Y), by = DimensionReferenceRules.QuantizeFeet(b.Y);
            if (ay != by) return ay < by;
            return DimensionReferenceRules.QuantizeFeet(a.Z) <= DimensionReferenceRules.QuantizeFeet(b.Z);
        }

        private static JObject FallbackWarning() => new JObject
        {
            ["code"] = DimensionReferenceRules.WarningViewGeometryFallback,
            ["message"] = "the requested view produced no geometry for this element, so this candidate was " +
                          "enumerated from fine-detail geometry WITHOUT a view. Verify the element actually " +
                          "shows in the view before dimensioning to it there."
        };

        private static JObject Warning(long? elementId, string selector, string code, string message)
        {
            var w = new JObject { ["code"] = code, ["message"] = message };
            if (elementId != null) w["element_id"] = elementId.Value;
            if (selector != null) w["selector"] = selector;
            return w;
        }

        private static JToken PointToken(XYZ p)
            => p == null ? (JToken)JValue.CreateNull() : new JArray(p.X, p.Y, p.Z); // feet; scaled at serialisation

        private static JToken VectorToken(XYZ v)
            => v == null ? (JToken)JValue.CreateNull() : new JArray(v.X, v.Y, v.Z); // unit vector: unitless

        private static Category ResolveCategory(Document doc, string text)
        {
            BuiltInCategory bic;
            if (Enum.TryParse(text, true, out bic))
                try { Category byToken = Category.GetCategory(doc, bic); if (byToken != null) return byToken; } catch { }
            try
            {
                foreach (Category category in doc.Settings.Categories)
                    if (string.Equals(category.Name, text, StringComparison.OrdinalIgnoreCase)) return category;
            }
            catch { }
            return null;
        }

        private static string LevelName(Document doc, Element element)
        {
            // The same most-specific-first chain query_model uses: walls keep their
            // level in the base constraint and expose no LEVEL_PARAM at all.
            Parameter p = element.get_Parameter(BuiltInParameter.LEVEL_PARAM) ??
                          element.get_Parameter(BuiltInParameter.WALL_BASE_CONSTRAINT) ??
                          element.get_Parameter(BuiltInParameter.FAMILY_BASE_LEVEL_PARAM) ??
                          element.get_Parameter(BuiltInParameter.RBS_START_LEVEL_PARAM) ??
                          element.get_Parameter(BuiltInParameter.FAMILY_LEVEL_PARAM) ??
                          element.get_Parameter(BuiltInParameter.SCHEDULE_LEVEL_PARAM);
            if (p == null) return null;
            if (p.StorageType == StorageType.ElementId)
            {
                Element level = doc.GetElement(p.AsElementId());
                if (level != null) return SafeName(level);
            }
            return Safe(() => p.AsValueString());
        }

        private static bool Contains(string have, string want) =>
            string.IsNullOrWhiteSpace(want) ||
            (have != null && have.IndexOf(want, StringComparison.OrdinalIgnoreCase) >= 0);

        private static string SafeName(Element element) { try { return element.Name; } catch { return null; } }
        private static string Safe(Func<string> f) { try { return f(); } catch { return null; } }

        // =====================================================================
        // Row types
        // =====================================================================

        private sealed class SolidRec { public Solid Solid; public Transform Tx; }
        private sealed class CurveRec { public Curve Curve; public Reference Reference; public Transform Tx; }
        private sealed class ProbedFace { public Reference Reference; public Face Face; public Transform Tx; public double DistanceFeet; }

        private sealed class Candidate
        {
            public Candidate(long elementId, string uniqueId, string selector, string referenceType,
                             LinkBinding link = null)
            {
                ElementId = elementId; UniqueId = uniqueId; Selector = selector; ReferenceType = referenceType;
                Link = link;
            }

            /// <summary>Null for a host candidate; the full provenance for a linked one.</summary>
            public readonly LinkBinding Link;

            /// <summary>
            /// The element this candidate is ABOUT. For a host row that is ElementId;
            /// for a linked row it is the element inside the link, because ElementId
            /// carries the link INSTANCE and every element of one link would otherwise
            /// share a subject.
            /// </summary>
            public long SubjectId() => Link == null ? ElementId : Link.LinkedElementId;

            /// <summary>The grouping key ambiguity is shaped over: one subject, one selector.</summary>
            public string AmbiguityScope()
                => (Link == null ? "h" : "l" + Link.LinkInstanceId) + ":" + SubjectId() + ":" + Selector;

            public long ElementId { get; }
            public string UniqueId { get; }
            public string Selector { get; }
            public string ReferenceType { get; }
            public string StableRepresentation;   // null only on the negative centerline row
            public string GeometryKind;
            public JObject Geometry;              // shaped in feet; scaled once at serialisation
            public XYZ Representative;
            public double? DistanceFeet;
            public bool Compatible = true;
            public IncompatibilityReason Incompatibility;
            public string Fingerprint;
            public bool Ambiguous;
            public string AmbiguityGroup;
            public JArray Warnings = new JArray();

            public CandidateKey Key() => new CandidateKey
            {
                ElementId = ElementId,
                Selector = Selector,
                ReferenceType = ReferenceType,
                Fingerprint = Fingerprint,
                StableRepresentation = StableRepresentation
            };

            /// <summary>
            /// The full comparison: the documented host order first, and only where it
            /// ties does provenance decide. A stable representation already differs
            /// between two links, so this is a tiebreak that will rarely fire - it
            /// exists so the order is TOTAL rather than nearly total, and two identical
            /// calls over a federation cannot disagree.
            /// </summary>
            public static int Compare(Candidate a, Candidate b)
            {
                int c = DimensionReferenceRules.CompareCandidates(a.Key(), b.Key());
                return c != 0 ? c : LinkedReferenceRules.CompareProvenance(a.Link, b.Link);
            }

            public JObject ToJson(string sourceModel, long viewId, double fromFeet)
            {
                return new JObject
                {
                    ["stable_representation"] = StableRepresentation == null
                        ? JValue.CreateNull() : new JValue(StableRepresentation),
                    ["element_id"] = ElementId,
                    ["unique_id"] = UniqueId,
                    // For a linked row the source is the LINKED document, because that
                    // is where the geometry lives; the host document is what
                    // stable_representation is expressed in, and the two are different
                    // facts that used to be one field.
                    ["source_model"] = Link == null ? sourceModel : Link.DocumentTitle,
                    ["host_model"] = sourceModel,
                    ["linked"] = Link != null,
                    ["link"] = LinkJson(fromFeet),
                    // Kept for callers written against the previous shape: the link
                    // instance id, or null on a host row, exactly as before.
                    ["link_instance"] = Link == null ? (JToken)JValue.CreateNull() : new JValue(Link.LinkInstanceId),
                    ["reference_type"] = ReferenceType,
                    ["selector"] = Selector,
                    ["ambiguous"] = Ambiguous,
                    ["ambiguity_group"] = AmbiguityGroup == null ? JValue.CreateNull() : new JValue(AmbiguityGroup),
                    ["geometry"] = ScaledGeometry(fromFeet),
                    ["representative_point"] = Representative == null
                        ? (JToken)JValue.CreateNull()
                        : new JArray(Representative.X * fromFeet, Representative.Y * fromFeet,
                                     Representative.Z * fromFeet),
                    ["distance"] = DistanceFeet == null
                        ? (JToken)JValue.CreateNull() : new JValue(DistanceFeet.Value * fromFeet),
                    ["view_id"] = viewId,
                    ["compatible_with_dimension"] = Compatible,
                    ["incompatibility_reason"] = Incompatibility == null
                        ? (JToken)JValue.CreateNull()
                        : new JObject { ["code"] = Incompatibility.Code, ["message"] = Incompatibility.Message },
                    ["geometry_fingerprint"] = Fingerprint,
                    ["warnings"] = Warnings
                };
            }

            /// <summary>
            /// The three ids, the placement and the linked document - or null on a host
            /// row. Every id is labelled by WHICH document it belongs to, because they
            /// are all integers and the whole class of linked-dimension bugs starts by
            /// reading one of them as another.
            /// </summary>
            private JToken LinkJson(double fromFeet)
            {
                if (Link == null) return JValue.CreateNull();
                var t = Link.Transform;
                return new JObject
                {
                    ["link_instance_id"] = Link.LinkInstanceId,
                    ["link_instance_unique_id"] = Link.InstanceUniqueId,
                    ["link_type_id"] = Link.LinkTypeId,
                    ["link_name"] = Link.LinkName,
                    ["linked_document_title"] = Link.DocumentTitle,
                    ["linked_document_identity"] = Link.DocumentIdentity,
                    ["linked_element_id"] = Link.LinkedElementId,
                    ["linked_element_unique_id"] = Link.LinkedElementUniqueId,
                    ["linked_element_category"] = Link.LinkedElementCategory,
                    ["linked_element_class"] = Link.LinkedElementClass,
                    ["transform_fingerprint"] = Link.TransformFingerprint,
                    ["transform"] = t == null ? (JToken)JValue.CreateNull() : new JObject
                    {
                        // The origin is a POINT and scales with the request's units; the
                        // basis vectors are unit vectors and do not.
                        ["origin"] = new JArray(t.Origin[0] * fromFeet, t.Origin[1] * fromFeet,
                                                t.Origin[2] * fromFeet),
                        ["basis_x"] = new JArray(t.BasisX[0], t.BasisX[1], t.BasisX[2]),
                        ["basis_y"] = new JArray(t.BasisY[0], t.BasisY[1], t.BasisY[2]),
                        ["basis_z"] = new JArray(t.BasisZ[0], t.BasisZ[1], t.BasisZ[2]),
                        ["determinant"] = t.Determinant,
                        ["handedness"] = t.Handedness,
                        ["identity"] = t.IsIdentity,
                        ["has_rotation"] = t.HasRotation,
                        ["has_reflection"] = t.HasReflection
                    }
                };
            }

            /// <summary>
            /// The geometry block, converted from internal feet to the requested units
            /// in one place. Direction/normal vectors stay unitless; every point and
            /// the radius scale.
            /// </summary>
            private JObject ScaledGeometry(double fromFeet)
            {
                if (Geometry == null) return null;
                var scaled = new JObject();
                foreach (JProperty p in Geometry.Properties())
                {
                    if (p.Name == "kind" || p.Name == "normal" || p.Name == "direction")
                    {
                        scaled[p.Name] = p.Value.DeepClone();
                        continue;
                    }
                    if (p.Value is JArray point && point.Count == 3)
                    {
                        scaled[p.Name] = new JArray(point[0].Value<double>() * fromFeet,
                                                    point[1].Value<double>() * fromFeet,
                                                    point[2].Value<double>() * fromFeet);
                        continue;
                    }
                    if (p.Value.Type == JTokenType.Float || p.Value.Type == JTokenType.Integer)
                    {
                        scaled[p.Name] = p.Value.Value<double>() * fromFeet;
                        continue;
                    }
                    scaled[p.Name] = p.Value.DeepClone();
                }
                return scaled;
            }
        }
    }
}
