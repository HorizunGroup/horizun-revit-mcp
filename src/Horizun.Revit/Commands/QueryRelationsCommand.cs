// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// horizun_query_relations (read-only): for explicit element ids, what each one is joined to,
// cut by, cutting, hosted by, hosting, near (box distance within radius_mm) and its type.
// Classification lives in Core/RelationRules (tested without Revit). A relation that could
// not be read is listed in `unreadable`; an empty list always means "read, and nothing".
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
    public sealed class QueryRelationsCommand : ICommand
    {
        public string Name => "horizun_query_relations";
        public string Description => "Read-only relations of elements: joins, cuts, host, hosted, neighbours within a radius and type info.";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            JObject request;
            try { request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JsonText.ParseObject(paramsJson); }
            catch (JsonException ex) { return CommandResult.FailCoded(ErrorCodes.EsquemaInvalido, "Parameters must be a JSON object: " + ex.Message); }
            Document doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.FailCoded(ErrorCodes.RevitSinDocumento, "No active Revit document.");
            CommandResult guard = DocumentGate.ReadGuard(doc, request, Name);
            if (guard != null) return guard;
            if (!RelationRules.TryParse(request, out RelationRequest req, out string error))
                return CommandResult.FailCoded(ErrorCodes.EsquemaInvalido, error);
            GeometryMeasureRules.TryUnit(req.Unit, out double k);

            var rows = new JArray();
            var unreadable = new JArray();
            foreach (long id in req.Ids)
            {
                Element e = Rid.CanRepresent(id) ? doc.GetElement(Rid.Make(id)) : null;
                if (e == null) { unreadable.Add(Unread(id, "*", UnreadableReason.ElementNotFound)); continue; }
                var row = new JObject { ["element_id"] = id, ["category"] = Safe(() => e.Category?.Name), ["built_in_category"] = CategoryKey.Of(e) };
                var relations = new JObject();

                bool wantsClassified = req.Relations.Contains(RelationKinds.Joined) || req.Relations.Contains(RelationKinds.CutBy) || req.Relations.Contains(RelationKinds.Cutting);
                if (wantsClassified)
                {
                    try
                    {
                        Dictionary<string, List<RelatedItem>> classified = ReadJoinsAndCuts(doc, e);
                        foreach (string kind in new[] { RelationKinds.Joined, RelationKinds.CutBy, RelationKinds.Cutting })
                            if (req.Relations.Contains(kind)) relations[kind] = Items(classified[kind], k);
                    }
                    catch (Exception ex)
                    {
                        foreach (string kind in new[] { RelationKinds.Joined, RelationKinds.CutBy, RelationKinds.Cutting })
                            if (req.Relations.Contains(kind)) unreadable.Add(Unread(id, kind, UnreadableReason.ReadException, ex.GetType().Name));
                    }
                }
                if (req.Relations.Contains(RelationKinds.Host))
                    Try(id, RelationKinds.Host, unreadable, () => relations[RelationKinds.Host] = Items(ReadHost(doc, e), k));
                if (req.Relations.Contains(RelationKinds.Hosted))
                    Try(id, RelationKinds.Hosted, unreadable, () => relations[RelationKinds.Hosted] = Items(ReadHosted(doc, e), k));
                if (req.Relations.Contains(RelationKinds.Neighbors))
                    Try(id, RelationKinds.Neighbors, unreadable, () =>
                    {
                        List<RelatedItem> near = ReadNeighbors(doc, e, req.RadiusMm / 304.8, out int total);
                        relations[RelationKinds.Neighbors] = Items(near, k);
                        row["neighbors_total"] = total;
                        row["neighbors_radius_mm"] = req.RadiusMm;
                        row["neighbors_truncated"] = total > near.Count;
                        if (total > near.Count) unreadable.Add(Unread(id, RelationKinds.Neighbors, "truncated_at_" + RelationRules.MaxNeighborsPerElement));
                    });
                if (req.Relations.Contains(RelationKinds.TypeInfo))
                    Try(id, RelationKinds.TypeInfo, unreadable, () => relations[RelationKinds.TypeInfo] = ReadTypeInfo(doc, e, k));
                row["relations"] = relations;
                rows.Add(row);
            }

            return CommandResult.Ok(new JObject
            {
                ["target_document"] = doc.Title,
                ["unit"] = req.Unit,
                ["requested"] = req.Ids.Count,
                ["elements"] = rows,
                ["unreadable_total"] = unreadable.Count,
                ["unreadable"] = new JArray(unreadable.Take(200)),
                ["coverage_complete"] = unreadable.Count == 0,
                ["note"] = "joined lists joins without a cutting direction; an element that cuts or is cut in a join appears only under cutting / cut_by. " +
                           "An empty list was read and is empty; a relation that could not be read is in unreadable."
            });
        }

        private static void Try(long id, string relation, JArray unreadable, Action read)
        {
            try { read(); }
            catch (Exception ex) { unreadable.Add(Unread(id, relation, UnreadableReason.ReadException, ex.GetType().Name)); }
        }

        private static JObject Unread(long id, string relation, string reason, string exception = null)
        {
            var o = new JObject { ["element_id"] = id, ["relation"] = relation, ["reason"] = reason };
            if (exception != null) o["exception"] = exception;
            return o;
        }

        internal static JArray Items(IEnumerable<RelatedItem> items, double k)
        {
            var arr = new JArray();
            foreach (RelatedItem i in items)
            {
                var o = new JObject { ["element_id"] = i.Id, ["category"] = i.Category, ["relation_type"] = i.RelationType };
                if (i.DistanceFeet.HasValue) o["distance"] = Math.Round(i.DistanceFeet.Value * k, 3);
                arr.Add(o);
            }
            return arr;
        }

        private static string Safe(Func<string> f) { try { return f(); } catch { return null; } }

        private static string CategoryOf(Document doc, long id)
        {
            try { return doc.GetElement(Rid.Make(id))?.Category?.Name; } catch { return null; }
        }

        internal static Dictionary<string, List<RelatedItem>> ReadJoinsAndCuts(Document doc, Element e)
        {
            var joins = new List<JoinFact>();
            foreach (ElementId other in JoinGeometryUtils.GetJoinedElements(doc, e))
            {
                Element o = doc.GetElement(other);
                if (o == null) continue;
                bool thisCuts = false, otherCuts = false;
                try { thisCuts = JoinGeometryUtils.IsCuttingElementInJoin(doc, e, o); } catch { }
                try { otherCuts = JoinGeometryUtils.IsCuttingElementInJoin(doc, o, e); } catch { }
                joins.Add(new JoinFact { OtherId = Rid.Value(other), ThisCutsOther = thisCuts, OtherCutsThis = otherCuts });
            }
            if (e is Wall && e.Location is LocationCurve lc)
                for (int end = 0; end < 2; end++)
                {
                    ElementArray at = lc.get_ElementsAtJoin(end);
                    if (at == null) continue;
                    foreach (Element o in at)
                        if (o != null && o.Id != e.Id && !joins.Any(j => j.OtherId == Rid.Value(o.Id)))
                            joins.Add(new JoinFact { OtherId = Rid.Value(o.Id), Kind = "end_join" });
                }

            var cutters = new List<long>();
            var targets = new List<long>();
            try { cutters.AddRange(InstanceVoidCutUtils.GetCuttingVoidInstances(e).Select(Rid.Value)); } catch { }
            if (e is FamilyInstance fi)
                try { targets.AddRange(InstanceVoidCutUtils.GetElementsBeingCut(fi).Select(Rid.Value)); } catch { }
            try { cutters.AddRange(SolidSolidCutUtils.GetCuttingSolids(e).Select(Rid.Value)); } catch { }
            try { targets.AddRange(SolidSolidCutUtils.GetSolidsBeingCut(e).Select(Rid.Value)); } catch { }
            return RelationRules.Classify(joins, cutters, targets, id => CategoryOf(doc, id));
        }

        internal static List<RelatedItem> ReadHost(Document doc, Element e)
        {
            var list = new List<RelatedItem>();
            void Add(Element h, string type)
            {
                if (h != null && !list.Any(x => x.Id == Rid.Value(h.Id)))
                    list.Add(new RelatedItem { Id = Rid.Value(h.Id), Category = Safe(() => h.Category?.Name), RelationType = type });
            }
            if (e is FamilyInstance fi)
            {
                Add(fi.Host, "host");
                Add(fi.SuperComponent, "super_component");
            }
            if (e is Opening op) Add(op.Host, "opening_host");
            if (e is Autodesk.Revit.DB.Architecture.Railing rail && rail.HasHost) Add(doc.GetElement(rail.HostId), "railing_host");
            return list;
        }

        internal static List<RelatedItem> ReadHosted(Document doc, Element e)
        {
            var list = new List<RelatedItem>();
            void Add(ElementId id, string type)
            {
                long v = Rid.Value(id);
                if (!list.Any(x => x.Id == v)) list.Add(new RelatedItem { Id = v, Category = CategoryOf(doc, v), RelationType = type });
            }
            if (e is HostObject host)
                foreach (ElementId id in host.FindInserts(true, false, true, false)) Add(id, "insert");
            if (e is FamilyInstance fi)
                foreach (ElementId id in fi.GetSubComponentIds() ?? new List<ElementId>()) Add(id, "sub_component");
            list.Sort((a, b) => a.Id.CompareTo(b.Id));
            return list;
        }

        private static List<RelatedItem> ReadNeighbors(Document doc, Element e, double radiusFeet, out int total)
        {
            BoundingBoxXYZ bb = e.get_BoundingBox(null);
            if (bb == null) throw new InvalidOperationException("no bounding box");
            double[] min = { bb.Min.X, bb.Min.Y, bb.Min.Z }, max = { bb.Max.X, bb.Max.Y, bb.Max.Z };
            return ReadNeighborsAround(doc, min, max, e.Id, radiusFeet, out total);
        }

        /// <summary>Model elements of doc whose box lies within radius of the box [min,max] (host coordinates), nearest first.</summary>
        internal static List<RelatedItem> ReadNeighborsAround(Document doc, double[] min, double[] max, ElementId exclude, double radiusFeet, out int total)
        {
            var outline = new Outline(new XYZ(min[0] - radiusFeet, min[1] - radiusFeet, min[2] - radiusFeet),
                                      new XYZ(max[0] + radiusFeet, max[1] + radiusFeet, max[2] + radiusFeet));
            var found = new List<RelatedItem>();
            foreach (Element o in new FilteredElementCollector(doc).WhereElementIsNotElementType()
                         .WherePasses(new BoundingBoxIntersectsFilter(outline)))
            {
                if (o.Id == exclude || o.Category == null || o.Category.CategoryType != CategoryType.Model) continue;
                BoundingBoxXYZ ob = o.get_BoundingBox(null);
                if (ob == null) continue;
                double d = RelationRules.BoxDistance(min, max, new[] { ob.Min.X, ob.Min.Y, ob.Min.Z }, new[] { ob.Max.X, ob.Max.Y, ob.Max.Z });
                if (d > radiusFeet + 1e-9) continue;
                found.Add(new RelatedItem { Id = Rid.Value(o.Id), Category = o.Category.Name, RelationType = "neighbor", DistanceFeet = d });
            }
            total = found.Count;
            return found.OrderBy(x => x.DistanceFeet).ThenBy(x => x.Id).Take(RelationRules.MaxNeighborsPerElement).ToList();
        }

        internal static JObject ReadTypeInfo(Document doc, Element e, double k)
        {
            Element type = doc.GetElement(e.GetTypeId());
            if (type == null) throw new InvalidOperationException("element has no type");
            var info = new JObject
            {
                ["type_id"] = Rid.Value(type.Id), ["type_name"] = type.Name,
                ["family"] = (type as ElementType)?.FamilyName, ["category"] = Safe(() => type.Category?.Name), ["built_in_category"] = CategoryKey.Of(type),
                ["is_system_type"] = !(type is FamilySymbol)
            };
            try
            {
                if (type is WallType wt) info["width"] = Math.Round(wt.Width * k, 3);
                else if (type is FloorType ft) info["width"] = Math.Round(ft.GetCompoundStructure().GetWidth() * k, 3);
            }
            catch { }
            return info;
        }
    }
}
