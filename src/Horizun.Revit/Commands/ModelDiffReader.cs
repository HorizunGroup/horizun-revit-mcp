// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// horizun_model_diff: READING a document into a snapshot. Model elements only
// (category type Model, not element types, not view-specific), with their
// identity, classification, placement, normalised parameter values and a cheap
// geometry hash. Nothing here writes. An element that throws while being read is
// COUNTED as unreadable, never dropped silently.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Commands
{
    public sealed partial class ModelDiffCommand
    {
        internal sealed class ReadScope
        {
            public HashSet<string> Categories;   // BuiltInCategory names or category names; null = all
            public int MaxElements = 50000;
            public bool Parameters = true;
            public int MaxParameters = 300;
        }

        internal static DiffSnapshot ReadDocument(Document doc, string revitVersion, ReadScope scope, string source)
        {
            var s = new DiffSnapshot { TakenUtc = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture) };
            s.Document = DocumentFacts(doc, revitVersion, source);
            s.Scope = new JObject
            {
                ["categories"] = scope.Categories == null ? JValue.CreateNull() : new JArray(scope.Categories.OrderBy(x => x)),
                ["max_elements"] = scope.MaxElements,
                ["parameters"] = scope.Parameters
            };

            var levels = new Dictionary<long, string>();
            var phases = new Dictionary<long, string>();
            var types = new Dictionary<long, DiffType>();
            WorksetTable wst = null;
            try { if (doc.IsWorkshared) wst = doc.GetWorksetTable(); } catch { wst = null; }

            foreach (Element e in new FilteredElementCollector(doc).WhereElementIsNotElementType())
            {
                Category cat;
                try
                {
                    cat = e.Category;
                    if (cat == null || cat.CategoryType != CategoryType.Model) continue;
                    if (e.ViewSpecific) continue;
                    if (e is ElementType) continue;
                }
                catch { continue; }

                string bic = BicName(cat);
                if (scope.Categories != null && !scope.Categories.Contains(bic) && !scope.Categories.Contains(cat.Name)) continue;
                s.ElementsSeen++;
                if (s.Elements.Count >= scope.MaxElements) { s.Truncated = true; continue; }
                try
                {
                    s.Elements.Add(ReadElement(doc, e, cat, bic, scope, levels, phases, types, wst));
                }
                catch { s.Unreadable++; }
            }
            s.Types = types.Values.OrderBy(t => t.UniqueId, StringComparer.Ordinal).ToList();
            s.Elements = s.Elements.OrderBy(x => x.UniqueId, StringComparer.Ordinal).ToList();
            return s;
        }

        private static DiffElement ReadElement(Document doc, Element e, Category cat, string bic, ReadScope scope,
                                               Dictionary<long, string> levels, Dictionary<long, string> phases,
                                               Dictionary<long, DiffType> types, WorksetTable wst)
        {
            var d = new DiffElement
            {
                UniqueId = e.UniqueId, Id = Rid.Value(e.Id), BuiltInCategory = bic, Category = cat.Name
            };
            ElementId typeId = ElementId.InvalidElementId;
            try { typeId = e.GetTypeId(); } catch { }
            if (typeId != null && typeId != ElementId.InvalidElementId)
            {
                long key = Rid.Value(typeId);
                DiffType t;
                if (!types.TryGetValue(key, out t))
                {
                    var et = doc.GetElement(typeId) as ElementType;
                    t = new DiffType { UniqueId = et?.UniqueId, Category = cat.Name, Family = Safe(() => et?.FamilyName), Name = Safe(() => et?.Name) };
                    if (et != null && scope.Parameters) ReadParameters(et, t.Parameters, scope.MaxParameters);
                    types[key] = t;
                }
                d.TypeUniqueId = t.UniqueId; d.Family = t.Family; d.Type = t.Name;
            }

            ElementId levelId = ElementId.InvalidElementId;
            try { levelId = e.LevelId; } catch { }
            if (levelId == null || levelId == ElementId.InvalidElementId)
            {
                try { levelId = e.get_Parameter(BuiltInParameter.SCHEDULE_LEVEL_PARAM)?.AsElementId() ?? ElementId.InvalidElementId; }
                catch { levelId = ElementId.InvalidElementId; }
            }
            d.Level = NameOf(doc, levelId, levels);
            if (wst != null) d.Workset = Safe(() => wst.GetWorkset(e.WorksetId)?.Name);
            d.PhaseCreated = Safe(() => NameOf(doc, e.CreatedPhaseId, phases));
            d.PhaseDemolished = Safe(() => NameOf(doc, e.DemolishedPhaseId, phases));

            BoundingBoxXYZ bb = null;
            try { bb = e.get_BoundingBox(null); } catch { }
            if (bb != null) d.BoundingBox = new[] { R(bb.Min.X), R(bb.Min.Y), R(bb.Min.Z), R(bb.Max.X), R(bb.Max.Y), R(bb.Max.Z) };
            try
            {
                if (e.Location is LocationPoint lp) d.Location = new[] { R(lp.Point.X), R(lp.Point.Y), R(lp.Point.Z) };
                else if (e.Location is LocationCurve lc && lc.Curve != null && lc.Curve.IsBound)
                {
                    XYZ p0 = lc.Curve.GetEndPoint(0), p1 = lc.Curve.GetEndPoint(1);
                    d.Location = new[] { R(p0.X), R(p0.Y), R(p0.Z), R(p1.X), R(p1.Y), R(p1.Z) };
                }
            }
            catch { d.Location = null; }

            d.GeometryHash = ModelDiffRules.GeometryHash(
                Number(e, BuiltInParameter.HOST_VOLUME_COMPUTED), Number(e, BuiltInParameter.HOST_AREA_COMPUTED), d.BoundingBox);
            if (scope.Parameters) ReadParameters(e, d.Parameters, scope.MaxParameters);
            return d;
        }

        private static void ReadParameters(Element e, SortedDictionary<string, string> into, int max)
        {
            foreach (Parameter p in e.Parameters)
            {
                if (into.Count >= max) return;
                try
                {
                    if (p?.Definition == null) continue;
                    var internalDef = p.Definition as InternalDefinition;
                    // Who last borrowed an element changes on every sync; it is not a change to the model.
                    if (internalDef != null && internalDef.BuiltInParameter == BuiltInParameter.EDITED_BY) continue;
                    string value;
                    if (!p.HasValue) value = ModelDiffRules.NoValue;
                    else switch (p.StorageType)
                    {
                        case StorageType.Double: value = ModelDiffRules.NormalizeDouble(p.AsDouble()); break;
                        case StorageType.Integer: value = ModelDiffRules.NormalizeInteger(p.AsInteger()); break;
                        case StorageType.String: value = ModelDiffRules.NormalizeString(p.AsString()); break;
                        case StorageType.ElementId: value = ModelDiffRules.NormalizeElementId(Rid.Value(p.AsElementId())); break;
                        default: continue;
                    }
                    string name = p.Definition.Name ?? "";
                    if (into.ContainsKey(name)) name = name + "#" + Rid.Value(p.Id).ToString(CultureInfo.InvariantCulture);
                    into[name] = value;
                }
                catch { /* one unreadable parameter does not make the element unreadable */ }
            }
        }

        internal static JObject DocumentFacts(Document doc, string revitVersion, string source)
        {
            var o = new JObject
            {
                ["title"] = Safe(() => doc.Title),
                ["path"] = Safe(() => doc.PathName),
                ["source"] = source,
                ["revit_version"] = revitVersion,
                ["is_workshared"] = SafeBool(() => doc.IsWorkshared),
                ["project_info_unique_id"] = Safe(() => doc.ProjectInformation?.UniqueId)
            };
            try
            {
                DocumentVersion v = Document.GetDocumentVersion(doc);
                if (v != null) { o["version_guid"] = v.VersionGUID.ToString(); o["number_of_saves"] = v.NumberOfSaves; }
            }
            catch { o["version_guid"] = JValue.CreateNull(); }
            try
            {
                string path = doc.PathName;
                if (!string.IsNullOrEmpty(path) && System.IO.File.Exists(path))
                    o["saved_format"] = BasicFileInfo.Extract(path).Format;
            }
            catch { }
            return o;
        }

        internal static string BicName(Category cat)
        {
            // Category.BuiltInCategory exists from Revit 2022, so every supported year has it;
            // INVALID for a user-defined category, which is reported as such.
            try { return cat.BuiltInCategory.ToString(); }
            catch { return null; }
        }

        private static string NameOf(Document doc, ElementId id, Dictionary<long, string> cache)
        {
            if (id == null || id == ElementId.InvalidElementId) return null;
            long key = Rid.Value(id);
            string name;
            if (!cache.TryGetValue(key, out name)) { name = Safe(() => doc.GetElement(id)?.Name); cache[key] = name; }
            return name;
        }

        private static double? Number(Element e, BuiltInParameter bip)
        {
            try
            {
                Parameter p = e.get_Parameter(bip);
                if (p == null || !p.HasValue || p.StorageType != StorageType.Double) return null;
                return p.AsDouble();
            }
            catch { return null; }
        }

        private static double R(double v) => Math.Round(v, 6);
        private static string Safe(Func<string> f) { try { return f(); } catch { return null; } }
        private static JToken SafeBool(Func<bool> f) { try { return f(); } catch { return JValue.CreateNull(); } }
    }
}
