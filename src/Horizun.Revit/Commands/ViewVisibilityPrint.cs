// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// horizun_view_visibility: overrides that do not lie about the paper (P5, field report
// 2026-11-17, Revit 2024). The Revit half of OverridePrintRules:
//
//   set_overrides (elements)  before writing, reads per element what else paints it in THIS
//                             view - the filters that apply (the template's when it governs
//                             filters), the category row - whether the view cuts it, whether
//                             it is a FilledRegion, and geometry nested in its family; predicts
//                             prints / partial / masked / will_not_print; unprinted=refuse
//                             (default) stops the write, skip writes only what prints, write
//                             writes all and says so. The category row left by an earlier
//                             method is named (category_overrides_behind) and cleared in the
//                             same write with clear_category_overrides=true.
//   override_report           read-only: per view of view_ids, which of the elements carry the
//                             override (the one given, or any) and which do not, with the same
//                             prediction - overrides live per view.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public sealed partial class ViewVisibilityCommand
    {
        /// <summary>Elements whose family geometry is walked for nested subcategories; beyond, not walked (said so).</summary>
        private const int MaxGeometryWalks = 500;

        private sealed class PrintContext
        {
            public View View, Template, FilterSource;
            public bool TemplateFilters, TemplateModel, TemplateAnnotation;
            public List<ElementId> Filters = new List<ElementId>();
            public int Walks;
            /// <summary>What the view draws (FilteredElementCollector by view): the same set override_report uses.</summary>
            public HashSet<long> InView;
            public ElementId PhaseId = ElementId.InvalidElementId;
            public PhaseFilter PhaseFilter;
            public ElementId Underlay = ElementId.InvalidElementId;
        }

        /// <summary>The view's print context, built ONCE per view per call (filters, collector, phase, underlay) and reused.</summary>
        private static PrintContext CachedContext(Dictionary<long, PrintContext> cache, Document doc, View view)
        {
            long key = Rid.Value(view.Id);
            if (cache == null) return Context(doc, view);
            if (!cache.TryGetValue(key, out PrintContext c)) cache[key] = c = Context(doc, view);
            return c;
        }

        private static PrintContext Context(Document doc, View view)
        {
            var c = new PrintContext { View = view };
            c.Template = view.ViewTemplateId == ElementId.InvalidElementId ? null : doc.GetElement(view.ViewTemplateId) as View;
            c.TemplateFilters = ManageViewsCommand.TemplateGoverns(c.Template, BuiltInParameter.VIS_GRAPHICS_FILTERS);
            c.TemplateModel = ManageViewsCommand.TemplateGoverns(c.Template, BuiltInParameter.VIS_GRAPHICS_MODEL);
            c.TemplateAnnotation = ManageViewsCommand.TemplateGoverns(c.Template, BuiltInParameter.VIS_GRAPHICS_ANNOTATION);
            c.FilterSource = c.TemplateFilters ? c.Template : view;
            try { c.Filters = c.FilterSource.GetOrderedFilters().ToList(); } catch { }
            c.InView = DrawnIn(doc, view);
            try { c.PhaseId = view.get_Parameter(BuiltInParameter.VIEW_PHASE)?.AsElementId() ?? ElementId.InvalidElementId; } catch { }
            try { c.PhaseFilter = doc.GetElement(view.get_Parameter(BuiltInParameter.VIEW_PHASE_FILTER)?.AsElementId() ?? ElementId.InvalidElementId) as PhaseFilter; } catch { }
            try { if (view is ViewPlan plan) c.Underlay = plan.GetUnderlayBaseLevel(); } catch { }
            return c;
        }

        private static PrintFacts Facts(Document doc, PrintContext c, Element e)
        {
            var f = new PrintFacts { IsFilledRegion = e is FilledRegion, Cut = CutInView(doc, c.View, e) };
            try { f.HiddenInView = e.IsHidden(c.View); } catch { }
            try
            {
                if (doc.IsWorkshared && e.WorksetId != WorksetId.InvalidWorksetId && !c.View.IsWorksetVisible(e.WorksetId))
                    f.HiddenWorkset = doc.GetWorksetTable().GetWorkset(e.WorksetId)?.Name ?? e.WorksetId.IntegerValue.ToString();
            }
            catch { }
            f.PhaseStatus = null;
            if (c.PhaseId != ElementId.InvalidElementId)
            {
                try
                {
                    ElementOnPhaseStatus st = e.GetPhaseStatus(c.PhaseId);
                    if (st != ElementOnPhaseStatus.None)
                    {
                        f.PhaseStatus = st.ToString();
                        if (st == ElementOnPhaseStatus.Future) f.Phase = "not_displayed";
                        else if (c.PhaseFilter == null) f.Phase = "unreadable";
                        else
                        {
                            PhaseStatusPresentation pr = c.PhaseFilter.GetPhaseStatusPresentation(st);
                            f.Phase = pr == PhaseStatusPresentation.DontShow ? "not_displayed" : pr == PhaseStatusPresentation.ShowOverriden ? "overridden" : "by_category";
                        }
                    }
                }
                catch { f.Phase = "unreadable"; }
            }
            bool drawn = c.InView == null ? true : c.InView.Contains(Rid.Value(e.Id));
            // Underlay judged whether or not the collector returns the element (review round 2).
            if (c.Underlay != ElementId.InvalidElementId && e.Category?.CategoryType == CategoryType.Model)
            {
                ElementId level = ElementId.InvalidElementId;
                try { level = e.LevelId; } catch { }
                long? viewLevel = null;
                try { if (c.View.GenLevel != null) viewLevel = Rid.Value(c.View.GenLevel.Id); } catch { }
                f.Underlay = OverridePrintRules.Underlay(Rid.Value(c.Underlay), viewLevel,
                    level == ElementId.InvalidElementId ? (long?)null : Rid.Value(level));
            }
            // Not drawn, unless a reason already named says why (hidden, workset, phase, underlay).
            f.InView = c.InView == null ? (bool?)null : drawn ? true
                : (f.HiddenInView || f.HiddenWorkset != null || f.Phase == "not_displayed" || f.Underlay != null) ? (bool?)null : false;
            try
            {
                OverrideGraphicSettings mine = c.View.GetElementOverrides(e.Id);
                if (mine.Halftone || mine.Transparency > 0)
                    f.Layers.Add(new PrintLayer { Label = "the element's current override", Own = true, Halftone = mine.Halftone, Transparency = mine.Transparency });
            }
            catch { }
            if (c.Walks < MaxGeometryWalks) { c.Walks++; f.NestedGeometry = NestedGeometry(doc, c.View, e); }
            foreach (ElementId id in c.Filters)
            {
                FilterElement filter = doc.GetElement(id) as FilterElement;
                bool enabled = true;
                try { enabled = c.FilterSource.GetIsFilterEnabled(id); } catch { }
                if (!enabled || !ManageViewsCommand.Passes(doc, filter, e)) continue;
                var layer = new PrintLayer { Label = "filter '" + (filter?.Name ?? Rid.Value(id).ToString()) + "'", FromTemplate = c.TemplateFilters };
                try { layer.Hides = !c.FilterSource.GetFilterVisibility(id); } catch { }
                try { OverrideGraphicSettings o = c.FilterSource.GetFilterOverrides(id); layer.Halftone = o.Halftone; layer.Transparency = o.Transparency; } catch { }
                if (layer.Hides || layer.Halftone || layer.Transparency > 0) f.Layers.Add(layer);
            }
            Category cat = null;
            try { cat = e.Category; } catch { }
            if (cat != null)
            {
                bool fromTemplate = cat.CategoryType == CategoryType.Annotation ? c.TemplateAnnotation : c.TemplateModel;
                View source = fromTemplate ? c.Template : c.View;
                var layer = new PrintLayer { Label = "category '" + cat.Name + "'", FromTemplate = fromTemplate };
                try { layer.Hides = source.GetCategoryHidden(cat.Id); } catch { }
                try { OverrideGraphicSettings o = source.GetCategoryOverrides(cat.Id); layer.Halftone = o.Halftone; layer.Transparency = o.Transparency; } catch { }
                if (layer.Hides || layer.Halftone || layer.Transparency > 0) f.Layers.Add(layer);
            }
            return f;
        }

        /// <summary>The ids the view draws; null when the view cannot be collected.</summary>
        private static HashSet<long> DrawnIn(Document doc, View view)
        {
            try { return new HashSet<long>(new FilteredElementCollector(doc, view.Id).WhereElementIsNotElementType().ToElementIds().Select(Rid.Value)); }
            catch { return null; }
        }

        /// <summary>Does the view cut the element? From the plan's cut plane or the section's plane against the element's box; null when not known.</summary>
        private static bool? CutInView(Document doc, View view, Element e)
        {
            try
            {
                Category cat = e.Category;
                if (e is FilledRegion || cat == null || cat.CategoryType != CategoryType.Model) return null;
                if (!cat.IsCuttable) return false;
                BoundingBoxXYZ bb = e.get_BoundingBox(null);
                if (bb == null) return null;
                Transform t = bb.Transform ?? Transform.Identity;
                var corners = new List<XYZ>();
                foreach (double x in new[] { bb.Min.X, bb.Max.X })
                    foreach (double y in new[] { bb.Min.Y, bb.Max.Y })
                        foreach (double z in new[] { bb.Min.Z, bb.Max.Z }) corners.Add(t.OfPoint(new XYZ(x, y, z)));
                if (view is ViewPlan plan)
                {
                    PlanViewRange range = plan.GetViewRange();
                    Level level = doc.GetElement(range.GetLevelId(PlanViewPlane.CutPlane)) as Level;
                    if (level == null) return null;
                    double cut = level.ProjectElevation + range.GetOffset(PlanViewPlane.CutPlane);
                    return corners.Min(p => p.Z) < cut && cut < corners.Max(p => p.Z);
                }
                if (view is ViewSection && (view.ViewType == ViewType.Section || view.ViewType == ViewType.Detail))
                {
                    XYZ n = view.ViewDirection, o = view.Origin;
                    var d = corners.Select(p => (p - o).DotProduct(n)).ToList();
                    return d.Min() < 0 && d.Max() > 0;
                }
                if (view is View3D v3) return v3.IsSectionBoxActive ? (bool?)null : false;
            }
            catch { }
            return null;
        }

        private static readonly BuiltInCategory[] StairLike =
            { BuiltInCategory.OST_Stairs, BuiltInCategory.OST_StairsRuns, BuiltInCategory.OST_StairsLandings, BuiltInCategory.OST_StairsRailing };

        /// <summary>Subcategories of geometry nested in the family (or a stair's own parts), "(not overridable)" when the view refuses a category override on it.</summary>
        private static List<string> NestedGeometry(Document doc, View view, Element e)
        {
            var names = new SortedSet<string>(StringComparer.Ordinal);
            Category own = null;
            try { own = e.Category; } catch { }
            if (own == null) return names.ToList();
            bool stair = StairLike.Any(b => own.Id.Equals(new ElementId(b)));
            if (!(e is FamilyInstance) && !stair) return names.ToList();
            void Walk(GeometryElement g, int depth)
            {
                if (g == null || depth > 4 || names.Count >= 20) return;
                foreach (GeometryObject o in g)
                {
                    if (o is GeometryInstance gi) { Walk(gi.GetInstanceGeometry(), depth + 1); continue; }
                    // A FamilyInstance's own symbol geometry is depth 1; nested families start at depth 2.
                    if (!stair && depth < 2) continue;
                    Category c = null;
                    try { c = (doc.GetElement(o.GraphicsStyleId) as GraphicsStyle)?.GraphicsStyleCategory; } catch { }
                    if (c == null || c.Id.Equals(own.Id)) continue;
                    bool overridable = true;
                    try { overridable = view.IsCategoryOverridable(c.Id); } catch { }
                    names.Add((c.Parent != null ? c.Parent.Name + ": " : "") + c.Name + (overridable ? "" : " (not overridable)"));
                }
            }
            try { Walk(e.get_Geometry(new Options { View = view }), 0); } catch { }
            return names.ToList();
        }

        /// <summary>The prediction for every element; <paramref name="blocked"/> receives those that will not print as asked.</summary>
        private static JObject PrintCheck(Document doc, View view, IList<Element> elements, JObject wanted, List<Element> blocked, Dictionary<long, PrintContext> cache)
        {
            PrintContext c = CachedContext(cache, doc, view);
            var items = new JArray();
            var verdicts = new List<string>();
            foreach (Element e in elements)
            {
                JObject p = OverridePrintRules.Predict(wanted, Facts(doc, c, e));
                string v = p.Value<string>("verdict");
                verdicts.Add(v);
                if (OverridePrintRules.Blocks(v)) blocked?.Add(e);
                if (v != OverridePrintRules.Prints && items.Count < 200)
                {
                    p["element_id"] = Rid.Value(e.Id); p["unique_id"] = e.UniqueId; p["category"] = e.Category?.Name; p["built_in_category"] = CategoryKey.Of(e);
                    items.Add(p);
                }
            }
            return new JObject
            {
                ["counts"] = OverridePrintRules.Count(verdicts), ["items"] = items,
                ["nested_geometry_checked"] = Math.Min(elements.Count, MaxGeometryWalks),
                ["means"] = "predicted from the view's settings before writing; verify the paper with horizun_export pdf_qa or horizun_capture_view"
            };
        }

        /// <summary>The category rows still painting behind element overrides (an earlier category method).</summary>
        private static List<KeyValuePair<ElementId, JObject>> CategoriesBehind(Document doc, View view, IEnumerable<Element> elements, Dictionary<long, PrintContext> cache)
        {
            PrintContext c = CachedContext(cache, doc, view);
            var rows = new List<KeyValuePair<ElementId, JObject>>();
            foreach (Category cat in elements.Select(e => { try { return e.Category; } catch { return null; } })
                         .Where(x => x != null).GroupBy(x => Rid.Value(x.Id)).Select(g => g.First()))
            {
                bool fromTemplate = cat.CategoryType == CategoryType.Annotation ? c.TemplateAnnotation : c.TemplateModel;
                IDictionary<string, string> fields;
                try { fields = ManageViewsCommand.OverrideFields(doc, (fromTemplate ? c.Template : view).GetCategoryOverrides(cat.Id)); }
                catch { continue; }
                JObject row = OverridePrintRules.CategoryBehind(cat.Name, fields, fromTemplate);
                if (row != null) rows.Add(new KeyValuePair<ElementId, JObject>(cat.Id, row));
            }
            return rows;
        }

        // ---- override_report -----------------------------------------------------------------

        private static ArchModelEdit OverrideReport(Document doc, JObject r)
        {
            JObject wanted = r["overrides"] as JObject;
            if (wanted != null)
            {
                wanted = (JObject)wanted.DeepClone();
                ManageViewsCommand.ReadOverrides(doc, wanted);   // resolves the pattern ids the comparison needs
            }
            var views = new JArray();
            var contexts = new Dictionary<long, PrintContext>();
            int with = 0, without = 0;
            foreach (JToken t in (JArray)r["view_ids"])
            {
                long raw = t.Value<long>();
                View v = Rid.CanRepresent(raw) ? doc.GetElement(Rid.Make(raw)) as View : null;
                if (v == null || v.IsTemplate) throw new ArgumentException("view_ids: " + raw + " is not a view of this document. Nothing was read.");
                var inView = CachedContext(contexts, doc, v).InView ?? new HashSet<long>();
                List<Element> asked;
                try { asked = Elements(doc, r, v); }
                catch (ArgumentException) { asked = Elements(doc, new JObject { ["element_ids"] = r["element_ids"] }, null); }
                var present = asked.Where(e => inView.Contains(Rid.Value(e.Id))).ToList();
                var has = new JArray(); var lacks = new JArray();
                foreach (Element e in present)
                {
                    OverrideGraphicSettings got = v.GetElementOverrides(e.Id);
                    bool carries = wanted != null ? ManageViewsCommand.OverridesMatch(got, wanted) : !IsCleared(got);
                    (carries ? has : lacks).Add(Rid.Value(e.Id));
                }
                with += has.Count; without += lacks.Count;
                var row = new JObject
                {
                    ["view_id"] = Rid.Value(v.Id), ["view_name"] = v.Name,
                    ["with_override"] = has, ["without_override"] = lacks,
                    ["not_in_view"] = new JArray(asked.Where(e => !inView.Contains(Rid.Value(e.Id))).Select(e => (JToken)Rid.Value(e.Id)))
                };
                if (wanted != null) row["print_check"] = PrintCheck(doc, v, present, wanted, null, contexts);
                views.Add(row);
            }
            return new ArchModelEdit
            {
                ReadResult = new JObject
                {
                    ["read_only"] = true, ["operation"] = "override_report", ["views"] = views,
                    ["with_override"] = with, ["without_override"] = without,
                    ["compared"] = wanted != null ? "the override given (only the fields it sets)" : "any element override",
                    ["means"] = "element overrides live per view: a view not listed here was not looked at"
                }
            };
        }
    }
}
