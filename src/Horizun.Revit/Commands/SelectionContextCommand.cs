// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// horizun_selection_context (read-only). The first call of a modelling session:
//
//   context   the current selection (or ids) - INCLUDING elements picked inside a link -
//             with category, type, level, key parameters, location, box, volume, the
//             largest faces, joins and who cuts whom, host and hosted, and the model
//             neighbours within a radius, each neighbour with its join relation to it.
//   snapshot  a named photograph of a set of elements (ids, selector or selection):
//             type, level, location samples, box, volume, chosen parameters.
//   diff      that photograph against the model now (or against a second photograph):
//             added, removed, changed (with the fields and the displacement), unchanged,
//             and what could not be compared.
//
// It reuses measure_geometry's and query_relations' readers; the rules (request,
// faces, neighbour relations, fingerprints, comparison, store) live in
// Core/SelectionContextRules and are tested without Revit. No transaction is opened;
// snapshots live in this Revit session's memory only.
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
    public sealed partial class SelectionContextCommand : ICommand
    {
        public string Name => "horizun_selection_context";
        public string Description => "Read-only: the selection (also inside links) with type, level, parameters, geometry, joins, host/hosted and neighbours; named snapshots and their diff.";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            JObject request;
            try { request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JsonText.ParseObject(paramsJson); }
            catch (JsonException ex) { return CommandResult.FailCoded(ErrorCodes.EsquemaInvalido, "Parameters must be a JSON object: " + ex.Message); }
            UIDocument uidoc = app.ActiveUIDocument;
            Document doc = uidoc?.Document;
            if (doc == null) return CommandResult.FailCoded(ErrorCodes.RevitSinDocumento, "No active Revit document.");
            CommandResult guard = DocumentGate.ReadGuard(doc, request, Name);
            if (guard != null) return guard;
            // an argument of another operation would be ignored without a word: refuse it by name, in both directions
            string operationName = ((string)request["operation"] ?? SelectionContextOps.Context).Trim().ToLowerInvariant();
            string foreign = ViewContentsRules.ForeignArgument(operationName, request);
            if (foreign != null)
                return CommandResult.FailCoded(ErrorCodes.EntradaInvalida, ViewContentsRules.ForeignArgumentMessage(operationName, foreign));
            if (string.Equals(((string)request["operation"] ?? "").Trim(), SelectionContextOps.ValidateIds, StringComparison.OrdinalIgnoreCase))
                return ValidateIds(doc, request);
            if (string.Equals(((string)request["operation"] ?? "").Trim(), SelectionContextOps.ViewContents, StringComparison.OrdinalIgnoreCase))
                return ViewContents(uidoc, doc, request);
            if (!SelectionContextRules.TryParse(request, out SelectionContextRequest req, out string error))
                return CommandResult.FailCoded(ErrorCodes.EsquemaInvalido, error);

            switch (req.Operation)
            {
                case SelectionContextOps.Snapshot: return Snapshot(uidoc, doc, req);
                case SelectionContextOps.Diff: return Diff(doc, req);
                default: return Context(uidoc, doc, req);
            }
        }

        // ================================================================ validate_ids

        // Read-only. After a synchronize with central the ids a caller read earlier may name other
        // elements (field 2026-10-30: notes 7166946-7166948 became 7167882-7167884, and the old ids were
        // left in other licences' texts). For each check the answer is coincide, cambio or no_existe, with
        // what the model holds now and, when the unique_id survived a renumbering, the id it lives under.
        private CommandResult ValidateIds(Document doc, JObject request)
        {
            JArray checks = request["checks"] as JArray;
            if (checks == null || checks.Count == 0)
                return CommandResult.FailCoded(ErrorCodes.EsquemaInvalido,
                    "validate_ids needs checks: a non-empty array of {id and/or unique_id, expected_category, expected_type}.");
            if (checks.Count > ElementIdentityGuard.MaxValidateChecks)
                return CommandResult.FailCoded(ErrorCodes.EsquemaInvalido,
                    "validate_ids takes at most " + ElementIdentityGuard.MaxValidateChecks + " checks; " + checks.Count + " were sent.");
            JObject result = ElementIdentityGuard.Validate(checks,
                id => Rid.CanRepresentElementId(id) ? DocumentGate.IdentityFacts(doc, doc.GetElement(Rid.Make(id))) : null,
                uid => DocumentGate.IdentityFacts(doc, doc.GetElement(uid)));
            result["target_document"] = doc.Title;
            result["operation"] = SelectionContextOps.ValidateIds;
            result["note"] = ElementIdentityGuard.ValidateIdsNote;
            return CommandResult.Ok(result);
        }

        // ================================================================ context

        private sealed class Picked
        {
            public Element Element;
            public Document Document;          // host or link document
            public RevitLinkInstance Link;     // null for host elements
            public Transform ToHost;           // identity for host elements
        }

        private CommandResult Context(UIDocument uidoc, Document doc, SelectionContextRequest req)
        {
            var unreadable = new JArray();
            var picked = new List<Picked>();
            bool truncated = false;
            int selectedTotal;
            if (req.Selection)
            {
                List<Picked> all = ReadSelection(uidoc, doc, unreadable, out string why);
                if (why != null) return CommandResult.FailCoded(ErrorCodes.EsquemaInvalido, why);
                if (all.Count == 0)
                    return CommandResult.FailCoded(ErrorCodes.EsquemaInvalido,
                        "Nothing is selected in '" + doc.Title + "' (active view: '" + Safe(() => uidoc.ActiveView?.Name) +
                        "'). Ask the person to select the elements, or send ids.");
                selectedTotal = all.Count;
                truncated = all.Count > SelectionContextRules.MaxContextElements;
                picked = all.Take(SelectionContextRules.MaxContextElements).ToList();
            }
            else
            {
                selectedTotal = req.Ids.Count;
                foreach (long id in req.Ids)
                {
                    Element e = Rid.CanRepresent(id) ? doc.GetElement(Rid.Make(id)) : null;
                    if (e == null) { unreadable.Add(Unread(id, "*", UnreadableReason.ElementNotFound)); continue; }
                    picked.Add(new Picked { Element = e, Document = doc, ToHost = Transform.Identity });
                }
            }

            double k = req.LengthFromFeet;
            var rows = new JArray();
            foreach (Picked p in picked)
                rows.Add(p.Link == null ? HostRow(doc, p.Element, req, unreadable) : LinkedRow(doc, p, req, unreadable));

            bool complete = unreadable.Count == 0 && !truncated;
            var result = new JObject
            {
                ["target_document"] = doc.Title,
                ["operation"] = SelectionContextOps.Context,
                ["source"] = req.Selection ? "selection" : "ids",
                ["unit"] = req.Unit,
                ["volume_unit"] = "m3",
                ["area_unit"] = "m2",
                ["include"] = new JArray(SelectionContextSections.All.Where(req.Sections.Contains)),
                ["radius_mm"] = req.RadiusMm,
                ["selected_total"] = selectedTotal,
                ["returned"] = rows.Count,
                ["truncated"] = truncated,
                ["elements"] = rows,
                ["unreadable_total"] = unreadable.Count,
                ["unreadable"] = new JArray(unreadable.Take(200)),
                ["coverage_complete"] = complete,
                ["note"] = "Read-only, no transaction. Host elements are measured in the host; an element picked inside a link is read " +
                           "in its link document and its box and location are carried into host coordinates (relations ids are LINK ids). " +
                           "neighbors are HOST model elements whose box lies within radius_mm of the element's box, nearest first, each " +
                           "with relation_to_element (joined, cuts_this, cut_by_this, or empty = near, not joined). faces are the largest " +
                           "by area. A value that could not be read is null and named in unreadable, never 0."
            };
            return CommandResult.Ok(result);
        }

        private static List<Picked> ReadSelection(UIDocument uidoc, Document doc, JArray unreadable, out string why)
        {
            why = null;
            var list = new List<Picked>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            ICollection<ElementId> ids;
            try { ids = uidoc.Selection.GetElementIds(); }
            catch (Exception ex) { why = "The current selection could not be read: " + ex.Message; return list; }
            foreach (ElementId id in ids ?? new List<ElementId>())
            {
                Element e = doc.GetElement(id);
                if (e == null) continue;
                if (seen.Add("h" + Rid.Value(id))) list.Add(new Picked { Element = e, Document = doc, ToHost = Transform.Identity });
            }
            // Elements picked INSIDE a link travel as references (Revit 2023+): the host selection
            // above only names the link instance, so a selected linked beam would be lost.
            IList<Reference> refs = null;
            try { refs = uidoc.Selection.GetReferences(); }
            catch (Exception ex) { unreadable.Add(new JObject { ["element_id"] = null, ["section"] = "linked_selection", ["reason"] = UnreadableReason.ReadException, ["exception"] = ex.GetType().Name }); }
            foreach (Reference r in refs ?? new List<Reference>())
            {
                try
                {
                    if (r == null || r.LinkedElementId == ElementId.InvalidElementId) continue;
                    var link = doc.GetElement(r.ElementId) as RevitLinkInstance;
                    Document linkDoc = link?.GetLinkDocument();
                    Element le = linkDoc?.GetElement(r.LinkedElementId);
                    if (le == null)
                    {
                        unreadable.Add(new JObject { ["element_id"] = Rid.Value(r.LinkedElementId), ["link_instance_id"] = Rid.Value(r.ElementId), ["section"] = "*", ["reason"] = "link_not_loaded_or_element_not_found" });
                        continue;
                    }
                    if (!seen.Add("l" + Rid.Value(link.Id) + ":" + Rid.Value(le.Id))) continue;
                    list.Add(new Picked { Element = le, Document = linkDoc, Link = link, ToHost = link.GetTotalTransform() });
                }
                catch (Exception ex) { unreadable.Add(new JObject { ["element_id"] = null, ["section"] = "linked_selection", ["reason"] = UnreadableReason.ReadException, ["exception"] = ex.GetType().Name }); }
            }
            return list;
        }

        private static JObject HostRow(Document doc, Element e, SelectionContextRequest req, JArray unreadable)
        {
            long id = Rid.Value(e.Id);
            double k = req.LengthFromFeet;
            JObject row = BaseFacts(doc, e, req, unreadable, id);
            row["source"] = "host";

            if (req.Sections.Contains(SelectionContextSections.Geometry))
            {
                var measures = new List<string> { MeasureKinds.Location, MeasureKinds.BoundingBox, MeasureKinds.SolidVolume };
                bool curve = Safe(() => e.Location is LocationCurve);
                if (curve) measures.Add(MeasureKinds.SectionProfile);
                if (req.MaxFaces > 0) measures.Add(MeasureKinds.Faces);
                var mreq = new MeasureRequest { Measures = measures, Unit = req.Unit, LengthFromFeet = k };
                var local = new JArray();
                JObject measured = MeasureGeometryCommand.MeasureAll(doc, new List<long> { id }, mreq, local).OfType<JObject>().FirstOrDefault();
                foreach (JObject u in local.OfType<JObject>())
                {
                    // Elements with no solid (a line, an annotation) are not a failure of this tool;
                    // they are named once, with the reason.
                    u["section"] = SelectionContextSections.Geometry;
                    unreadable.Add(u);
                }
                var geometry = new JObject();
                if (measured != null)
                {
                    geometry["location"] = measured[MeasureKinds.Location];
                    geometry["bounding_box"] = measured[MeasureKinds.BoundingBox];
                    geometry["solid_volume"] = measured[MeasureKinds.SolidVolume];
                    if (curve) geometry["section_profile"] = measured[MeasureKinds.SectionProfile];
                    if (req.MaxFaces > 0) geometry["main_faces"] = MainFaces(measured[MeasureKinds.Faces] as JObject, req.MaxFaces);
                }
                row["geometry"] = geometry;
            }

            List<long> joined = new List<long>(), cutBy = new List<long>(), cutting = new List<long>();
            if (req.Sections.Contains(SelectionContextSections.Relations) || req.Sections.Contains(SelectionContextSections.Neighbors))
            {
                var relations = new JObject();
                try
                {
                    Dictionary<string, List<RelatedItem>> classified = QueryRelationsCommand.ReadJoinsAndCuts(doc, e);
                    joined = classified[RelationKinds.Joined].Select(x => x.Id).ToList();
                    cutBy = classified[RelationKinds.CutBy].Select(x => x.Id).ToList();
                    cutting = classified[RelationKinds.Cutting].Select(x => x.Id).ToList();
                    relations[RelationKinds.Joined] = QueryRelationsCommand.Items(classified[RelationKinds.Joined], k);
                    relations[RelationKinds.CutBy] = QueryRelationsCommand.Items(classified[RelationKinds.CutBy], k);
                    relations[RelationKinds.Cutting] = QueryRelationsCommand.Items(classified[RelationKinds.Cutting], k);
                }
                catch (Exception ex) { unreadable.Add(Unread(id, "joins", UnreadableReason.ReadException, ex.GetType().Name)); }
                if (req.Sections.Contains(SelectionContextSections.Relations))
                {
                    try { relations[RelationKinds.Host] = QueryRelationsCommand.Items(QueryRelationsCommand.ReadHost(doc, e), k); }
                    catch (Exception ex) { unreadable.Add(Unread(id, RelationKinds.Host, UnreadableReason.ReadException, ex.GetType().Name)); }
                    try { relations[RelationKinds.Hosted] = QueryRelationsCommand.Items(QueryRelationsCommand.ReadHosted(doc, e), k); }
                    catch (Exception ex) { unreadable.Add(Unread(id, RelationKinds.Hosted, UnreadableReason.ReadException, ex.GetType().Name)); }
                    row["relations"] = relations;
                }
            }

            if (req.Sections.Contains(SelectionContextSections.Neighbors))
            {
                BoundingBoxXYZ bb = Safe(() => e.get_BoundingBox(null));
                if (bb == null) unreadable.Add(Unread(id, SelectionContextSections.Neighbors, UnreadableReason.NoBoundingBox));
                else
                    Neighbors(doc, row, new[] { bb.Min.X, bb.Min.Y, bb.Min.Z }, new[] { bb.Max.X, bb.Max.Y, bb.Max.Z },
                              e.Id, req, joined, cutBy, cutting, unreadable, id);
            }
            return row;
        }

        private static JObject LinkedRow(Document host, Picked p, SelectionContextRequest req, JArray unreadable)
        {
            Element e = p.Element;
            long id = Rid.Value(e.Id);
            double k = req.LengthFromFeet;
            JObject row = BaseFacts(p.Document, e, req, unreadable, id);
            row["source"] = "link";
            row["link_instance_id"] = Rid.Value(p.Link.Id);
            row["link_document"] = Safe(() => p.Document.Title);

            double[] min = null, max = null;
            BoundingBoxXYZ bb = Safe(() => e.get_BoundingBox(null));
            if (bb != null)
            {
                IEnumerable<double[]> corners = SelectionContextRules.Corners(new[] { bb.Min.X, bb.Min.Y, bb.Min.Z }, new[] { bb.Max.X, bb.Max.Y, bb.Max.Z })
                    .Select(c => { XYZ t = p.ToHost.OfPoint(new XYZ(c[0], c[1], c[2])); return new[] { t.X, t.Y, t.Z }; });
                SelectionContextRules.Envelope(corners, out min, out max);
            }
            if (req.Sections.Contains(SelectionContextSections.Geometry))
            {
                var geometry = new JObject { ["coordinates"] = "host" };
                if (min != null)
                    geometry["bounding_box"] = new JObject { ["min"] = Vec(min, k), ["max"] = Vec(max, k), ["size"] = Vec(new[] { max[0] - min[0], max[1] - min[1], max[2] - min[2] }, k) };
                else { geometry["bounding_box"] = JValue.CreateNull(); unreadable.Add(Unread(id, MeasureKinds.BoundingBox, UnreadableReason.NoBoundingBox)); }
                try
                {
                    Location loc = e.Location;
                    if (loc is LocationPoint lp) geometry["location"] = new JObject { ["kind"] = "point", ["point"] = Vec(P(p.ToHost.OfPoint(lp.Point)), k) };
                    else if (loc is LocationCurve lc && lc.Curve != null && lc.Curve.IsBound)
                        geometry["location"] = new JObject
                        {
                            ["kind"] = "curve",
                            ["start"] = Vec(P(p.ToHost.OfPoint(lc.Curve.GetEndPoint(0))), k),
                            ["end"] = Vec(P(p.ToHost.OfPoint(lc.Curve.GetEndPoint(1))), k),
                            ["length"] = Math.Round(lc.Curve.Length * k, 3)
                        };
                    else { geometry["location"] = JValue.CreateNull(); unreadable.Add(Unread(id, MeasureKinds.Location, UnreadableReason.NoLocation)); }
                }
                catch (Exception ex) { geometry["location"] = JValue.CreateNull(); unreadable.Add(Unread(id, MeasureKinds.Location, UnreadableReason.ReadException, ex.GetType().Name)); }
                // Volume and section do not depend on the transform: measured in the link document.
                var measures = new List<string> { MeasureKinds.SolidVolume };
                if (Safe(() => e.Location is LocationCurve)) measures.Add(MeasureKinds.SectionProfile);
                var local = new JArray();
                JObject measured = MeasureGeometryCommand.MeasureAll(p.Document, new List<long> { id },
                    new MeasureRequest { Measures = measures, Unit = req.Unit, LengthFromFeet = k }, local).OfType<JObject>().FirstOrDefault();
                foreach (JObject u in local.OfType<JObject>()) { u["section"] = SelectionContextSections.Geometry; unreadable.Add(u); }
                if (measured != null)
                {
                    geometry["solid_volume"] = measured[MeasureKinds.SolidVolume];
                    if (measured[MeasureKinds.SectionProfile] != null) geometry["section_profile"] = measured[MeasureKinds.SectionProfile];
                }
                row["geometry"] = geometry;
            }
            List<long> joined = new List<long>(), cutBy = new List<long>(), cutting = new List<long>();
            if (req.Sections.Contains(SelectionContextSections.Relations))
            {
                var relations = new JObject { ["ids_in"] = "link_document" };
                try
                {
                    Dictionary<string, List<RelatedItem>> classified = QueryRelationsCommand.ReadJoinsAndCuts(p.Document, e);
                    foreach (string kind in new[] { RelationKinds.Joined, RelationKinds.CutBy, RelationKinds.Cutting })
                        relations[kind] = QueryRelationsCommand.Items(classified[kind], k);
                }
                catch (Exception ex) { unreadable.Add(Unread(id, "joins", UnreadableReason.ReadException, ex.GetType().Name)); }
                try { relations[RelationKinds.Host] = QueryRelationsCommand.Items(QueryRelationsCommand.ReadHost(p.Document, e), k); }
                catch (Exception ex) { unreadable.Add(Unread(id, RelationKinds.Host, UnreadableReason.ReadException, ex.GetType().Name)); }
                try { relations[RelationKinds.Hosted] = QueryRelationsCommand.Items(QueryRelationsCommand.ReadHosted(p.Document, e), k); }
                catch (Exception ex) { unreadable.Add(Unread(id, RelationKinds.Hosted, UnreadableReason.ReadException, ex.GetType().Name)); }
                row["relations"] = relations;
            }
            if (req.Sections.Contains(SelectionContextSections.Neighbors))
            {
                if (min == null) unreadable.Add(Unread(id, SelectionContextSections.Neighbors, UnreadableReason.NoBoundingBox));
                else Neighbors(host, row, min, max, p.Link.Id, req, joined, cutBy, cutting, unreadable, id);
            }
            return row;
        }

        private static JObject BaseFacts(Document doc, Element e, SelectionContextRequest req, JArray unreadable, long id)
        {
            var row = new JObject { ["element_id"] = id };
            row["category"] = Safe(() => e.Category?.Name); row["built_in_category"] = CategoryKey.Of(e);
            row["name"] = Safe(() => e.Name);
            try
            {
                Element type = doc.GetElement(e.GetTypeId());
                row["type_id"] = type == null ? (JToken)JValue.CreateNull() : Rid.Value(type.Id);
                row["family"] = (type as ElementType)?.FamilyName;
                row["type"] = type?.Name;
            }
            catch (Exception ex) { unreadable.Add(Unread(id, "type", UnreadableReason.ReadException, ex.GetType().Name)); }
            row["level"] = LevelOf(doc, e);
            if (req.Sections.Contains(SelectionContextSections.Parameters))
            {
                var pars = new JObject
                {
                    ["Mark"] = BuiltIn(e, BuiltInParameter.ALL_MODEL_MARK),
                    ["Comments"] = BuiltIn(e, BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)
                };
                var missing = new JArray();
                foreach (string name in req.Parameters)
                {
                    if (TryNamed(doc, e, name, out string value, out string where)) pars[name] = new JObject { ["value"] = value, ["from"] = where };
                    else missing.Add(name);
                }
                row["parameters"] = pars;
                if (missing.Count > 0) row["parameters_not_found"] = missing;
            }
            return row;
        }

        private static void Neighbors(Document host, JObject row, double[] min, double[] max, ElementId exclude, SelectionContextRequest req,
                                      List<long> joined, List<long> cutBy, List<long> cutting, JArray unreadable, long id)
        {
            try
            {
                List<RelatedItem> near = QueryRelationsCommand.ReadNeighborsAround(host, min, max, exclude, req.RadiusMm / 304.8, out int total);
                var arr = new JArray();
                foreach (RelatedItem n in near)
                {
                    Element ne = Safe(() => host.GetElement(Rid.Make(n.Id)));
                    Element nt = ne == null ? null : Safe(() => host.GetElement(ne.GetTypeId()));
                    arr.Add(new JObject
                    {
                        ["element_id"] = n.Id,
                        ["category"] = n.Category,
                        ["type"] = nt?.Name,
                        ["distance"] = n.DistanceFeet.HasValue ? Math.Round(n.DistanceFeet.Value * req.LengthFromFeet, 3) : (JToken)JValue.CreateNull(),
                        ["relation_to_element"] = new JArray(SelectionContextRules.NeighborRelations(n.Id, joined, cutBy, cutting))
                    });
                }
                row["neighbors"] = arr;
                row["neighbors_total"] = total;
                row["neighbors_truncated"] = total > near.Count;
                if (total > near.Count) unreadable.Add(Unread(id, SelectionContextSections.Neighbors, "truncated_at_" + RelationRules.MaxNeighborsPerElement));
            }
            catch (Exception ex) { unreadable.Add(Unread(id, SelectionContextSections.Neighbors, UnreadableReason.ReadException, ex.GetType().Name)); }
        }

        private static JToken MainFaces(JObject faces, int max)
        {
            if (faces == null) return JValue.CreateNull();
            List<JObject> list = (faces["faces"] as JArray)?.OfType<JObject>().ToList() ?? new List<JObject>();
            List<int> top = SelectionContextRules.TopFaces(list.Select(f => (double?)f["area_m2"] ?? double.NaN).ToList(), max);
            return new JObject
            {
                ["faces_total"] = faces["count"],
                ["faces_read"] = list.Count,
                ["shown"] = top.Count,
                ["largest"] = new JArray(top.Select(i => (JToken)list[i]))
            };
        }

        private static JToken LevelOf(Document doc, Element e)
        {
            try
            {
                ElementId lid = e.LevelId;
                Level lv = lid != null && lid != ElementId.InvalidElementId ? doc.GetElement(lid) as Level : null;
                if (lv != null) return new JObject { ["id"] = Rid.Value(lv.Id), ["name"] = lv.Name, ["elevation_mm"] = Math.Round(lv.Elevation * 304.8, 1), ["source"] = "Element.LevelId" };
                LevelResolution r = new ElementLevelReader(doc).Resolve(e);
                if (r.Resolved) return new JObject { ["name"] = r.LevelName, ["source"] = r.Source };
            }
            catch { }
            return JValue.CreateNull();
        }

        private static JToken BuiltIn(Element e, BuiltInParameter bip)
        {
            try
            {
                Parameter p = e.get_Parameter(bip);
                return p == null ? JValue.CreateNull() : (JToken)ValueText(p);
            }
            catch { return JValue.CreateNull(); }
        }

        /// <summary>A named parameter: instance first, then type. The text Revit shows.</summary>
        internal static bool TryNamed(Document doc, Element e, string name, out string value, out string where)
        {
            value = null; where = null;
            try
            {
                Parameter p = e.LookupParameter(name);
                if (p != null) { value = ValueText(p); where = "instance"; return true; }
                Element type = doc.GetElement(e.GetTypeId());
                p = type?.LookupParameter(name);
                if (p != null) { value = ValueText(p); where = "type"; return true; }
            }
            catch { }
            return false;
        }

        private static string ValueText(Parameter p)
        {
            if (!p.HasValue) return null;
            switch (p.StorageType)
            {
                case StorageType.String: return p.AsString();
                case StorageType.ElementId:
                    return p.AsValueString() ?? Rid.Value(p.AsElementId()).ToString(System.Globalization.CultureInfo.InvariantCulture);
                default:
                    return p.AsValueString() ?? (p.StorageType == StorageType.Integer
                        ? p.AsInteger().ToString(System.Globalization.CultureInfo.InvariantCulture)
                        : p.AsDouble().ToString("R", System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        // ================================================================ snapshot / diff

        private static string DocumentKey(Document doc)
        {
            string path = Safe(() => doc.PathName);
            return string.IsNullOrEmpty(path) ? "title:" + Safe(() => doc.Title) : "path:" + path;
        }

        private CommandResult Snapshot(UIDocument uidoc, Document doc, SelectionContextRequest req)
        {
            var unreadable = new JArray();
            List<long> ids;
            bool truncated = false;
            string source;
            if (req.Selection)
            {
                source = "selection";
                ICollection<ElementId> sel;
                try { sel = uidoc.Selection.GetElementIds(); }
                catch (Exception ex) { return CommandResult.FailCoded(ErrorCodes.EsquemaInvalido, "The current selection could not be read: " + ex.Message); }
                ids = (sel ?? new List<ElementId>()).Select(Rid.Value).Distinct().OrderBy(x => x).ToList();
                if (ids.Count == 0)
                    return CommandResult.FailCoded(ErrorCodes.EsquemaInvalido, "selection=true but nothing is selected in '" + doc.Title + "'. Nothing was stored.");
                if (ids.Count > SelectionContextRules.MaxSnapshotElements) { ids = ids.Take(SelectionContextRules.MaxSnapshotElements).ToList(); truncated = true; }
            }
            else if (req.Selector != null)
            {
                source = "selector";
                SelectorResolution found = SelectorResolver.Resolve(doc, req.Selector, SelectionContextRules.MaxSnapshotElements);
                ids = found.Matched;
                truncated = found.Truncated;
                foreach (long u in found.Unknown.Take(50)) unreadable.Add(Unread(u, "selector", UnreadableReason.ReadException));
            }
            else { source = "ids"; ids = req.Ids.OrderBy(x => x).ToList(); }

            Dictionary<long, ElementFingerprint> prints = ReadPrints(doc, ids, req, unreadable, out List<long> missing);
            var snap = new StoredSnapshot
            {
                Name = req.SnapshotName, DocumentKey = DocumentKey(doc), TakenUtc = DateTime.UtcNow, Source = source,
                Selector = req.Selector, ParameterNames = req.Parameters.ToList(), Elements = prints, Truncated = truncated,
                Hash = SelectionContextRules.Hash(prints.Values)
            };
            string dropped = ElementSnapshotStore.Put(snap);
            return CommandResult.Ok(new JObject
            {
                ["target_document"] = doc.Title,
                ["operation"] = SelectionContextOps.Snapshot,
                ["snapshot_name"] = snap.Name,
                ["source"] = source,
                ["taken_utc"] = snap.TakenUtc.ToString("o"),
                ["elements"] = prints.Count,
                ["by_category"] = SelectionContextRules.ByCategory(prints.Values.Select(x => x.Category)),
                ["not_found"] = new JArray(missing.Take(SelectionContextRules.MaxListedIds)),
                ["truncated"] = truncated,
                ["fingerprint"] = snap.Hash,
                ["parameters"] = new JArray(new[] { "Mark", "Comments" }.Concat(req.Parameters)),
                ["dropped_oldest_snapshot"] = dropped,
                ["snapshots_in_session"] = new JArray(ElementSnapshotStore.Names(snap.DocumentKey)),
                ["unreadable_total"] = unreadable.Count,
                ["unreadable"] = new JArray(unreadable.Take(200)),
                ["coverage_complete"] = unreadable.Count == 0 && !truncated && missing.Count == 0,
                ["note"] = "Kept in this Revit session's memory only (nothing written to the model or to disk), per document, at most " +
                           SelectionContextRules.MaxSnapshotsPerDocument + ". Compare with operation=diff snapshot_name=" + snap.Name + "."
            });
        }

        private CommandResult Diff(Document doc, SelectionContextRequest req)
        {
            string key = DocumentKey(doc);
            StoredSnapshot before = ElementSnapshotStore.Get(key, req.SnapshotName);
            if (before == null)
                return CommandResult.FailCoded(ErrorCodes.PrecondicionNoCumplida,
                    "No snapshot named '" + req.SnapshotName + "' for '" + doc.Title + "' in this Revit session (snapshots: " +
                    string.Join(", ", ElementSnapshotStore.Names(key)) + "). Take one with operation=snapshot first; they do not survive a Revit restart.");
            var unreadable = new JArray();
            Dictionary<long, ElementFingerprint> after;
            string againstLabel;
            if (!string.IsNullOrEmpty(req.Against))
            {
                StoredSnapshot other = ElementSnapshotStore.Get(key, req.Against);
                if (other == null)
                    return CommandResult.FailCoded(ErrorCodes.PrecondicionNoCumplida, "No snapshot named '" + req.Against + "' for '" + doc.Title + "' in this Revit session.");
                after = other.Elements;
                againstLabel = "snapshot:" + other.Name;
            }
            else
            {
                // The same elements, read now - and, for a selector snapshot, whatever the selector
                // matches now, so elements the step CREATED are seen as added.
                var ids = new HashSet<long>(before.Elements.Keys);
                if (before.Selector != null)
                {
                    SelectorResolution found = SelectorResolver.Resolve(doc, before.Selector, SelectionContextRules.MaxSnapshotElements);
                    foreach (long id in found.Matched) ids.Add(id);
                    foreach (long u in found.Unknown.Take(50)) unreadable.Add(Unread(u, "selector", UnreadableReason.ReadException));
                }
                var copy = new SelectionContextRequest { Parameters = before.ParameterNames.ToList(), Sections = req.Sections };
                after = ReadPrints(doc, ids.OrderBy(x => x).ToList(), copy, unreadable, out _);
                againstLabel = "model_now";
            }
            double k = req.LengthFromFeet;
            FingerprintDiff d = SelectionContextRules.Compare(before.Elements, after, req.ToleranceMm / 304.8);
            Func<IEnumerable<long>, IDictionary<long, ElementFingerprint>, JObject> block = (list, side) =>
            {
                List<long> l = list.ToList();
                return new JObject
                {
                    ["count"] = l.Count,
                    ["by_category"] = SelectionContextRules.ByCategory(l.Select(x => side.TryGetValue(x, out ElementFingerprint f) ? f.Category : null)),
                    ["ids"] = new JArray(l.Take(SelectionContextRules.MaxListedIds)),
                    ["ids_truncated"] = l.Count > SelectionContextRules.MaxListedIds
                };
            };
            var changed = new JArray(d.Changed.Take(SelectionContextRules.MaxListedIds).Select(c =>
            {
                var o = new JObject { ["element_id"] = c.Id, ["category"] = c.Category, ["fields"] = new JArray(c.Fields) };
                if (c.MaxDisplacementFeet.HasValue) o["max_displacement"] = Math.Round(c.MaxDisplacementFeet.Value * k, 3);
                if (c.VolumeDeltaCubicFeet.HasValue) o["volume_delta_m3"] = Math.Round(c.VolumeDeltaCubicFeet.Value * GeometryMeasureRules.CubicMetresPerCubicFoot, 6);
                if (c.ParamChanges.Count > 0)
                {
                    var pc = new JObject();
                    foreach (var kv in c.ParamChanges) pc[kv.Key] = new JObject { ["before"] = kv.Value[0], ["after"] = kv.Value[1] };
                    o["parameter_changes"] = pc;
                }
                return (JToken)o;
            }));
            return CommandResult.Ok(new JObject
            {
                ["target_document"] = doc.Title,
                ["operation"] = SelectionContextOps.Diff,
                ["snapshot_name"] = before.Name,
                ["snapshot_taken_utc"] = before.TakenUtc.ToString("o"),
                ["against"] = againstLabel,
                ["unit"] = req.Unit,
                ["tolerance_mm"] = req.ToleranceMm,
                ["added"] = block(d.Added, after),
                ["removed"] = block(d.Removed, before.Elements),
                ["changed_count"] = d.Changed.Count,
                ["changed_by_category"] = SelectionContextRules.ByCategory(d.Changed.Select(c => c.Category)),
                ["changed"] = changed,
                ["changed_truncated"] = d.Changed.Count > SelectionContextRules.MaxListedIds,
                ["unchanged"] = d.Unchanged,
                ["not_comparable"] = new JArray(d.NotComparable.Take(SelectionContextRules.MaxListedIds)),
                ["fingerprint_before"] = before.Hash,
                ["fingerprint_after"] = SelectionContextRules.Hash(after.Values),
                ["unreadable_total"] = unreadable.Count,
                ["unreadable"] = new JArray(unreadable.Take(200)),
                ["coverage_complete"] = unreadable.Count == 0 && d.NotComparable.Count == 0 && !before.Truncated,
                ["note"] = "location (point or curve ends) and box corners compare within tolerance_mm; parameters compare as the text Revit shows; " +
                           "removed = in the snapshot and not in the model now (deleted). An element with a fact unreadable on either side is not_comparable, never unchanged."
            });
        }

        private static Dictionary<long, ElementFingerprint> ReadPrints(Document doc, List<long> ids, SelectionContextRequest req, JArray unreadable, out List<long> missing)
        {
            missing = new List<long>();
            var prints = new Dictionary<long, ElementFingerprint>();
            bool geometry = req.Sections.Contains(SelectionContextSections.Geometry);
            foreach (long id in ids)
            {
                Element e = Rid.CanRepresent(id) ? doc.GetElement(Rid.Make(id)) : null;
                if (e == null) { missing.Add(id); continue; }
                prints[id] = Print(doc, e, req.Parameters, geometry);
                if (prints[id].Unreadable.Count > 0 && unreadable.Count < 200)
                    unreadable.Add(new JObject { ["element_id"] = id, ["facts"] = new JArray(prints[id].Unreadable) });
            }
            return prints;
        }

        private static ElementFingerprint Print(Document doc, Element e, List<string> parameterNames, bool volume)
        {
            var f = new ElementFingerprint { Id = Rid.Value(e.Id) };
            f.Category = Safe(() => e.Category?.Name);
            try { ElementId t = e.GetTypeId(); f.TypeId = t == null || t == ElementId.InvalidElementId ? (long?)null : Rid.Value(t); }
            catch { f.Unreadable.Add("type"); }
            try { ElementId l = e.LevelId; f.LevelId = l == null || l == ElementId.InvalidElementId ? (long?)null : Rid.Value(l); }
            catch { f.Unreadable.Add("level"); }
            try
            {
                if (e.Location is LocationPoint lp) f.Points = new List<double[]> { P(lp.Point) };
                else if (e.Location is LocationCurve lc && lc.Curve != null && lc.Curve.IsBound)
                    f.Points = new List<double[]> { P(lc.Curve.GetEndPoint(0)), P(lc.Curve.GetEndPoint(1)) };
                else if (e is TextNote tn) f.Points = new List<double[]> { P(tn.Coord) };
            }
            catch { f.Unreadable.Add("location"); }
            try
            {
                BoundingBoxXYZ bb = e.get_BoundingBox(null);
                if (bb == null && e.OwnerViewId != ElementId.InvalidElementId) bb = e.get_BoundingBox(doc.GetElement(e.OwnerViewId) as View);
                if (bb != null) { f.BoxMin = P(bb.Min); f.BoxMax = P(bb.Max); }
            }
            catch { f.Unreadable.Add("bounding_box"); }
            if (volume)
            {
                try
                {
                    double v = 0; bool any = false;
                    var ge = e.get_Geometry(new Options { DetailLevel = ViewDetailLevel.Fine });
                    if (ge != null) foreach (Solid s in Solids(ge)) { v += s.Volume; any = true; }
                    if (any) f.VolumeCubicFeet = v;
                }
                catch { f.Unreadable.Add("volume"); }
            }
            foreach (string name in new[] { "Mark", "Comments" }.Concat(parameterNames ?? new List<string>()))
            {
                string value;
                if (name == "Mark") { JToken t = BuiltIn(e, BuiltInParameter.ALL_MODEL_MARK); if (t.Type == JTokenType.Null) continue; value = (string)t; }
                else if (name == "Comments") { JToken t = BuiltIn(e, BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS); if (t.Type == JTokenType.Null) continue; value = (string)t; }
                else if (!TryNamed(doc, e, name, out value, out _)) continue;
                f.Params[name] = value;
            }
            return f;
        }

        private static IEnumerable<Solid> Solids(GeometryElement ge)
        {
            foreach (GeometryObject o in ge)
            {
                if (o is Solid s && s.Volume > 1e-9) yield return s;
                else if (o is GeometryInstance gi)
                {
                    GeometryElement inner = gi.GetInstanceGeometry();
                    if (inner != null) foreach (Solid x in Solids(inner)) yield return x;
                }
            }
        }

        // ================================================================ helpers

        private static double[] P(XYZ p) => new[] { p.X, p.Y, p.Z };

        private static JArray Vec(double[] v, double k) =>
            new JArray(Math.Round(v[0] * k, 4), Math.Round(v[1] * k, 4), Math.Round(v[2] * k, 4));

        private static JObject Unread(long id, string section, string reason, string exception = null)
        {
            var o = new JObject { ["element_id"] = id, ["section"] = section, ["reason"] = reason };
            if (exception != null) o["exception"] = exception;
            return o;
        }

        private static T Safe<T>(Func<T> f) { try { return f(); } catch { return default(T); } }
    }
}
