// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// horizun_query_planimetry mode=section_mark_diagnosis (5.2, backlog §7). READ only: no
// transaction. Why does the mark of a section not show in a view? Whether it shows is read
// from the view (the viewer elements it holds); when it does not, each cause is measured
// with its numbers by Core/SectionMarkRules. Nothing is moved or created: MoveElement does
// not move the plane of a section, so the answer is a diagnosis, not a repair.
//
// The viewer of a section (its mark) is found by relation (dependent element) and, failing
// that, by a name that is unique in the document. Filters and phase are evaluated on THAT
// viewer, never on the section view; no viewer found means unknown.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public sealed partial class QueryPlanimetryCommand
    {
        private static readonly BuiltInCategory[] SectionMarkCategories = { BuiltInCategory.OST_Sections, BuiltInCategory.OST_Viewers };

        private static CommandResult SectionMarkDiagnosis(Document doc, JObject request, int maxRows)
        {
            View view = NeedAs<View>(doc, request.Value<long>("view_id"), "view");
            if (view.IsTemplate) return CommandResult.FailCoded(ErrorCodes.EntradaInvalida, "view " + Rid.Value(view.Id) + " is a view template; name the view where the mark is expected.");

            List<ViewSection> sections;
            bool listed = request["section_view_id"] == null;
            if (listed)
                sections = new FilteredElementCollector(doc).OfClass(typeof(ViewSection)).Cast<ViewSection>()
                    .Where(s => !s.IsTemplate && s.ViewType == ViewType.Section && Rid.Value(s.Id) != Rid.Value(view.Id))
                    .OrderBy(s => Rid.Value(s.Id)).ToList();
            else
            {
                long sid = request.Value<long>("section_view_id");
                if (sid == request.Value<long>("view_id"))
                    return CommandResult.FailCoded(ErrorCodes.EntradaInvalida, "section_view_id is the same view as view_id: name the section and the view where its mark is expected.");
                ViewSection one = NeedAs<ViewSection>(doc, sid, "section view");
                if (one.IsTemplate || one.ViewType != ViewType.Section)
                    return CommandResult.FailCoded(ErrorCodes.EntradaInvalida, "section_view_id " + sid + " is not a section view (type " + one.ViewType + ").");
                sections = new List<ViewSection> { one };
            }

            // Every section mark (Sections / Viewers categories) of the document, once, and which of them this view holds.
            var docViewers = new List<Element>();
            var inView = new HashSet<long>();
            string viewersFailure = null;
            try
            {
                foreach (BuiltInCategory bic in SectionMarkCategories)
                {
                    docViewers.AddRange(PlanimetryReadBudget.CheckEach(
                        new FilteredElementCollector(doc).OfCategory(bic).WhereElementIsNotElementType()));
                    foreach (Element e in PlanimetryReadBudget.CheckEach(new FilteredElementCollector(doc, view.Id).OfCategory(bic).WhereElementIsNotElementType())) inView.Add(Rid.Value(e.Id));
                }
            }
            catch (Exception ex) when (!(ex is PlanimetryReadBudget.Interrupted)) { viewersFailure = ex.Message; }

            var viewsByName = new Dictionary<string, List<long>>(StringComparer.Ordinal);
            try
            {
                foreach (View v in PlanimetryReadBudget.CheckEach(new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()).Where(v => !v.IsTemplate))
                {
                    List<long> ids;
                    if (!viewsByName.TryGetValue(v.Name, out ids)) viewsByName[v.Name] = ids = new List<long>();
                    ids.Add(Rid.Value(v.Id));
                }
            }
            catch (Exception ex) when (!(ex is PlanimetryReadBudget.Interrupted)) { }

            JObject plan = new JObject { ["view_id"] = Rid.Value(view.Id), ["name"] = view.Name, ["view_type"] = view.ViewType.ToString(), ["scale"] = view.Scale };
            ViewPlan vp = view is ViewPlan p0 && (view.ViewType == ViewType.FloorPlan || view.ViewType == ViewType.EngineeringPlan || view.ViewType == ViewType.AreaPlan) ? p0 : null;
            ZBound cut = ZBound.Unknown("not a plan view"), top = cut, bottom = cut, depth = cut;
            if (vp != null)
            {
                try
                {
                    PlanViewRange r = vp.GetViewRange();
                    Level assoc = vp.GenLevel;
                    cut = RangePlane(doc, r, PlanViewPlane.CutPlane, assoc);
                    top = RangePlane(doc, r, PlanViewPlane.TopClipPlane, assoc);
                    bottom = RangePlane(doc, r, PlanViewPlane.BottomClipPlane, assoc);
                    depth = RangePlane(doc, r, PlanViewPlane.ViewDepthPlane, assoc);
                }
                catch (Exception ex) { cut = top = bottom = depth = ZBound.Unknown(ex.Message); }
                plan["view_range_z_mm"] = new JObject { ["cut_plane"] = cut.ToJson(), ["top"] = top.ToJson(), ["bottom"] = bottom.ToJson(), ["view_depth"] = depth.ToJson() };
            }
            plan["crop_active"] = view.CropBoxActive;

            var rows = new JArray();
            int visible = 0, notVisible = 0, unknown = 0, conflict = 0;
            bool truncated = false;
            foreach (ViewSection s in PlanimetryReadBudget.CheckEach(sections))
            {
                if (rows.Count >= maxRows) { truncated = true; continue; }
                JObject row = DiagnoseSection(doc, view, vp, s, docViewers, inView, viewersFailure, viewsByName, cut, top, bottom, depth);
                string v = (string)row["verdict"];
                if (v == "visible") visible++; else if (v == "unknown") unknown++; else if (v == "measurement_conflict") conflict++; else notVisible++;
                rows.Add(row);
            }
            return CommandResult.Ok(new JObject
            {
                ["mode"] = "section_mark_diagnosis", ["units"] = "mm", ["view"] = plan,
                ["scope"] = listed ? "all_section_views_of_the_document" : "one_section_view",
                ["sections_total"] = sections.Count, ["truncated"] = truncated,
                ["summary"] = new JObject { ["visible"] = visible, ["not_visible"] = notVisible, ["unknown"] = unknown, ["measurement_conflict"] = conflict },
                ["rows"] = rows,
                ["coverage"] = new JObject
                {
                    ["mark_detection"] = "the viewer of the section is its dependent element (Sections / Viewers), or the only viewer of the document with its name (matched_by); filters and phase are evaluated on that viewer, never on the section view",
                    ["not_read"] = "dependent-view or callout parents, sub-category overrides and the section's own 'hide line' graphics are not evaluated; a viewer that references another view (reference section, callout) is not taken as the mark"
                }
            });
        }

        /// <summary>A viewer that points at ANOTHER view (a reference section or a callout head) is not the mark of this section.</summary>
        private static bool ReferencesOtherView(Document doc, Element viewer, ViewSection s)
        {
            try
            {
                ElementId target = ReferenceableViewUtils.GetReferencedViewId(doc, viewer.Id);
                return target != null && Rid.Value(target) > 0 && Rid.Value(target) != Rid.Value(s.Id);
            }
            catch { return false; }
        }

        private static ZBound RangePlane(Document doc, PlanViewRange range, PlanViewPlane plane, Level assoc)
        {
            ElementId lid = range.GetLevelId(plane);
            double offset = range.GetOffset(plane);
            long id = Rid.Value(lid);
            if (id == Rid.Value(PlanViewRange.Unlimited)) return ZBound.Unlimited();
            Level lv = null;
            if (id == Rid.Value(PlanViewRange.Current))
            {
                lv = assoc;
                if (lv == null) return ZBound.Unknown("the view has no associated level");
            }
            else if (id == Rid.Value(PlanViewRange.LevelAbove) || id == Rid.Value(PlanViewRange.LevelBelow))
            {
                if (assoc == null) return ZBound.Unknown("the view has no associated level to resolve 'level above/below'");
                bool above = id == Rid.Value(PlanViewRange.LevelAbove);
                IEnumerable<Level> levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>();
                lv = above ? levels.Where(l => l.ProjectElevation > assoc.ProjectElevation + 1e-6).OrderBy(l => l.ProjectElevation).FirstOrDefault()
                           : levels.Where(l => l.ProjectElevation < assoc.ProjectElevation - 1e-6).OrderByDescending(l => l.ProjectElevation).FirstOrDefault();
                if (lv == null) return ZBound.Unlimited();       // Revit reads "level above/below" with no such level as no limit
            }
            else lv = doc.GetElement(lid) as Level;
            if (lv == null) return ZBound.Unknown("the level of the plane could not be read");
            return ZBound.At((lv.ProjectElevation + offset) / FtPerMm);
        }

        private static JObject DiagnoseSection(Document doc, View view, ViewPlan vp, ViewSection s, List<Element> docViewers, HashSet<long> inView, string viewersFailure, Dictionary<string, List<long>> viewsByName,
            ZBound cut, ZBound top, ZBound bottom, ZBound depth)
        {
            var row = new JObject { ["entity_kind"] = "section_mark", ["section_view_id"] = Rid.Value(s.Id), ["section_name"] = s.Name };

            // 1. The viewer of the section: its dependent element; the name only when it is unique in the document.
            var candidates = new List<Element>();
            var discarded = new JArray();
            string matchedBy = null, searchReason = viewersFailure;
            long[] markCats = SectionMarkCategories.Select(b => (long)(int)b).ToArray();
            Func<Element, string> discard = e => SectionMarkRules.DiscardReason(e is View,
                e.Category != null && SectionMarkRules.IsMarkCategory(Rid.Value(e.Category.Id), markCats), ReferencesOtherView(doc, e, s),
                Rid.Value(e.OwnerViewId) == Rid.Value(s.Id), string.Equals(e.Name, s.Name, StringComparison.Ordinal),
                viewsByName.TryGetValue(e.Name ?? "", out List<long> sameName) && sameName.Any(v => v != Rid.Value(s.Id)));
            Func<Element, JObject> describe = e => new JObject { ["id"] = Rid.Value(e.Id), ["category"] = e.Category == null ? null : (JToken)(e.Category.Name), ["built_in_category"] = CategoryKey.Of(e), ["name"] = e.Name,
                ["owner_view_id"] = e.OwnerViewId == null || Rid.Value(e.OwnerViewId) < 0 ? null : (JToken)Rid.Value(e.OwnerViewId) };
            if (viewersFailure == null)
            {
                try
                {
                    var seen = new HashSet<long>();
                    foreach (BuiltInCategory bic in SectionMarkCategories)
                        foreach (ElementId did in PlanimetryReadBudget.CheckEach(s.GetDependentElements(new ElementCategoryFilter(bic))))
                        {
                            Element e = doc.GetElement(did);
                            if (e == null || Rid.Value(e.Id) == Rid.Value(s.Id) || !seen.Add(Rid.Value(e.Id))) continue;
                            string why = discard(e);
                            if (why == null) candidates.Add(e); else { JObject d = describe(e); d["why"] = why; discarded.Add(d); }
                        }
                    if (candidates.Count > 0) matchedBy = "dependent_element";
                }
                catch (Exception ex) when (!(ex is PlanimetryReadBudget.Interrupted)) { searchReason = "dependent elements of the section could not be read: " + ex.Message; }
                if (candidates.Count == 0)
                {
                    List<Element> byName = new List<Element>();
                    foreach (Element e in PlanimetryReadBudget.CheckEach(docViewers).Where(e => string.Equals(e.Name, s.Name, StringComparison.Ordinal)))
                    {
                        string why = discard(e);
                        if (why == null) byName.Add(e); else { JObject d = describe(e); d["why"] = why; discarded.Add(d); }
                    }
                    if (byName.Count == 1) { candidates = byName; matchedBy = "name"; searchReason = null; }
                    else searchReason = byName.Count == 0 ? "no viewer of the document is related to this section or carries its name"
                                                          : byName.Count + " viewers of the document carry the name of this section: the mark cannot be told apart";
                }
            }
            string foundReason;
            bool? found = SectionMarkRules.MarkFound(candidates.Count, candidates.Count(e => inView.Contains(Rid.Value(e.Id))), out foundReason);
            if (found == null && searchReason == null) searchReason = foundReason;
            Element probe = candidates.FirstOrDefault(e => inView.Contains(Rid.Value(e.Id))) ?? candidates.FirstOrDefault();
            row["mark"] = new JObject
            {
                ["found_in_view"] = found.HasValue ? (JToken)found.Value : null, ["matched_by"] = matchedBy,
                ["viewer_ids"] = new JArray(candidates.Select(e => (JToken)Rid.Value(e.Id))),
                ["viewers"] = new JArray(candidates.Select(e => (JToken)describe(e)))
            };
            if (discarded.Count > 0) ((JObject)row["mark"])["discarded"] = discarded;

            // 2. The Z extent of the section and its line, from its crop box in model coordinates.
            double? zMin = null, zMax = null; string boxReason = null;
            XYZ lineA = null, lineB = null;
            try
            {
                BoundingBoxXYZ box = s.CropBox;
                Transform t = box.Transform;
                double lo = double.MaxValue, hi = double.MinValue;
                foreach (double x in new[] { box.Min.X, box.Max.X })
                    foreach (double y in new[] { box.Min.Y, box.Max.Y })
                        foreach (double z in new[] { box.Min.Z, box.Max.Z })
                        {
                            double wz = t.OfPoint(new XYZ(x, y, z)).Z;
                            lo = Math.Min(lo, wz); hi = Math.Max(hi, wz);
                        }
                zMin = lo / FtPerMm; zMax = hi / FtPerMm;
                lineA = t.OfPoint(new XYZ(box.Min.X, 0, 0)); lineB = t.OfPoint(new XYZ(box.Max.X, 0, 0));
                row["section_z_extent_mm"] = new JObject { ["min"] = Math.Round(zMin.Value, 1), ["max"] = Math.Round(zMax.Value, 1) };
            }
            catch (Exception ex) { boxReason = "the section's crop box could not be read: " + ex.Message; row["section_z_extent_mm"] = new JObject { ["unknown"] = boxReason }; }

            var causes = new List<JObject>();
            causes.Add(vp == null ? SectionMarkRules.ZRangeNotApplicable(view.ViewType.ToString()) : SectionMarkRules.ZRange(cut, top, bottom, depth, zMin, zMax, boxReason));
            causes.Add(CategoryCause(doc, view));
            causes.Add(ScaleCause(view, s));
            if (candidates.Count > 1) row["filters_and_phase_measured_on_ambiguous_candidate"] = true;
            causes.Add(FilterCause(doc, view, probe));
            causes.Add(PhaseCause(doc, view, probe));
            causes.Add(vp == null ? SectionMarkRules.CropNotMeasured(view.ViewType.ToString()) : CropCause(view, lineA, lineB));
            row["causes"] = new JArray(causes);
            foreach (JProperty pr in SectionMarkRules.Verdict(found, causes, searchReason).Properties()) row[pr.Name] = pr.Value;
            return row;
        }

        private static JObject CategoryCause(Document doc, View view)
        {
            // Read with the raw built-in id, not Category.GetCategory: it can give null for OST_Viewers (inferred from the live
            // symptom in 2025 and 2026: this cause stayed unknown in every plan where Sections was visible).
            // CanCategoryBeHidden says whether the category CAN be changed here, not whether it is hidden (a template that controls
            // V/G may block the change), so the state is always READ - on the view and on its template - and "cannot be hidden"
            // stands for "not hidden" only when no read succeeded. Every row says where its value came from.
            View template = null; string templateName = null;
            try { if (view.ViewTemplateId != null && Rid.Value(view.ViewTemplateId) > 0) { template = doc.GetElement(view.ViewTemplateId) as View; templateName = template?.Name; } } catch { }
            bool? templateControls = null;
            if (template != null)
            {
                try
                {
                    long annotation = (long)(int)BuiltInParameter.VIS_GRAPHICS_ANNOTATION;
                    templateControls = !template.GetNonControlledTemplateParameterIds().Any(id => Rid.Value(id) == annotation);
                }
                catch { templateControls = null; }
            }
            var cats = new List<KeyValuePair<string, bool?>>();
            var evidence = new Dictionary<string, JObject>();
            foreach (BuiltInCategory bic in SectionMarkCategories)
            {
                ElementId cid = Rid.Make((long)(int)bic);
                bool? can = null, onView = null, onTemplate = null;
                try { can = view.CanCategoryBeHidden(cid); } catch { }
                try { onView = view.GetCategoryHidden(cid); } catch { }
                if (template != null) { try { onTemplate = template.GetCategoryHidden(cid); } catch { } }
                // A template imposes only what it controls: its Sections/Viewers state counts only when it controls
                // "V/G Overrides Annotation" (absent from GetNonControlledTemplateParameterIds). Unknown control decides
                // nothing unless the view and the template agree.
                bool? hidden; string from;
                if (template != null && templateControls == true)
                {
                    if (onTemplate.HasValue) { hidden = onTemplate; from = "view_template"; }
                    else if (can == false && onView == false) { hidden = false; from = "cannot_be_hidden"; }
                    else { hidden = null; from = "unreadable"; }
                }
                else if (template != null && templateControls == null)
                {
                    if (onView.HasValue && onTemplate.HasValue && onView == onTemplate) { hidden = onView; from = "view_and_template"; }
                    else { hidden = null; from = "template_control_unreadable"; }
                }
                else if (onView.HasValue) { hidden = onView; from = "view"; }
                else if (can == false) { hidden = false; from = "cannot_be_hidden"; }
                else { hidden = null; from = "unreadable"; }
                cats.Add(new KeyValuePair<string, bool?>(bic.ToString(), hidden));
                evidence[bic.ToString()] = new JObject { ["can_be_hidden"] = can.HasValue ? (JToken)can.Value : null, ["read_from"] = from,
                    ["template_controls_annotation"] = template == null ? null : (templateControls.HasValue ? (JToken)templateControls.Value : "unreadable") };
            }
            return SectionMarkRules.CategoryHidden(cats, templateName, evidence);
        }

        private static JObject ScaleCause(View view, ViewSection s)
        {
            int? limit = null; string reason = null;
            foreach (BuiltInParameter bip in new[] { BuiltInParameter.SECTION_COARSER_SCALE_PULLDOWN_METRIC, BuiltInParameter.SECTION_COARSER_SCALE_PULLDOWN_IMPERIAL })
            {
                Parameter p = s.get_Parameter(bip);
                if (p != null && p.HasValue && p.StorageType == StorageType.Integer) { limit = p.AsInteger(); break; }
            }
            if (!limit.HasValue) reason = "the section has no readable 'hide at scales coarser than' parameter";
            return SectionMarkRules.Scale(view.Scale, limit, reason);
        }

        private static JObject FilterCause(Document doc, View view, Element probe)
        {
            var verdicts = new List<FilterVerdict>();
            if (probe == null)
                return SectionMarkRules.Filters(verdicts, "the viewer of the section was not found, so no filter can be evaluated on it");
            try
            {
                // A view template that controls the filters decides them: read its filters.
                View src = view;
                try { if (view.ViewTemplateId != null && Rid.Value(view.ViewTemplateId) > 0) src = doc.GetElement(view.ViewTemplateId) as View ?? view; } catch { }
                var catIds = SectionMarkCategories.Select(b => (long)(int)b).ToList();
                foreach (ElementId fid in src.GetFilters())
                {
                    Element fe = doc.GetElement(fid);
                    var v = new FilterVerdict { Name = fe?.Name, Visible = src.GetFilterVisibility(fid) };
                    try { v.Enabled = src.GetIsFilterEnabled(fid); } catch (Exception ex) { v.Reason = "enabled state unreadable: " + ex.Message; }
                    if (fe is ParameterFilterElement pfe)
                    {
                        if (!pfe.GetCategories().Any(c => catIds.Contains(Rid.Value(c)))) continue;     // does not cover sections
                        try { ElementFilter ef = pfe.GetElementFilter(); if (ef != null) v.Matches = ef.PassesFilter(doc, probe.Id); else v.Reason = "the filter has no readable rule"; }
                        catch (Exception ex) { v.Reason = ex.Message; }
                    }
                    else if (fe is SelectionFilterElement sfe)
                        v.Matches = new HashSet<long>(sfe.GetElementIds().Select(Rid.Value)).Contains(Rid.Value(probe.Id));
                    else { v.Reason = "filter kind not evaluated"; }
                    verdicts.Add(v);
                }
            }
            catch (Exception ex)
            {
                return SectionMarkRules.Filters(new List<FilterVerdict>(), "the filters of the view could not be read: " + ex.Message);
            }
            return SectionMarkRules.Filters(verdicts, null);
        }

        private static JObject PhaseCause(Document doc, View view, Element probe)
        {
            try
            {
                if (probe == null) return SectionMarkRules.Phase(null, null, null, "the viewer of the section was not found, so its phase cannot be read");
                Parameter phaseP = view.get_Parameter(BuiltInParameter.VIEW_PHASE);
                Parameter filterP = view.get_Parameter(BuiltInParameter.VIEW_PHASE_FILTER);
                if (phaseP == null || filterP == null) return SectionMarkRules.Phase(null, null, null, "the view has no phase or phase filter parameter");
                Element phase = doc.GetElement(phaseP.AsElementId());
                PhaseFilter pf = doc.GetElement(filterP.AsElementId()) as PhaseFilter;
                if (phase == null || pf == null) return SectionMarkRules.Phase(null, phase?.Name, null, "the view's phase or phase filter is not readable");
                ElementOnPhaseStatus status = probe.GetPhaseStatus(phase.Id);
                if (status == ElementOnPhaseStatus.None) return SectionMarkRules.Phase("NotApplicable", phase.Name, pf.Name, null);   // a view element carries no phase
                PhaseStatusPresentation pres = pf.GetPhaseStatusPresentation(status);
                return SectionMarkRules.Phase(pres.ToString(), phase.Name, pf.Name, null);
            }
            catch (Exception ex) { return SectionMarkRules.Phase(null, null, null, ex.Message); }
        }

        private static JObject CropCause(View view, XYZ lineA, XYZ lineB)
        {
            if (!view.CropBoxActive) return SectionMarkRules.Crop(false, false, null, null, null);
            try
            {
                bool? nonRect = null;
                try { nonRect = view.GetCropRegionShapeManager().ShapeSet; } catch { }
                if (lineA == null || lineB == null) return SectionMarkRules.Crop(true, nonRect, null, null, "the section line is unknown (crop box unreadable)");
                BoundingBoxXYZ crop = view.CropBox;
                Transform inv = crop.Transform.Inverse;
                XYZ a = inv.OfPoint(lineA), b = inv.OfPoint(lineB);
                var seg = new[] { a.X / FtPerMm, a.Y / FtPerMm, b.X / FtPerMm, b.Y / FtPerMm };
                var rect = new[] { crop.Min.X / FtPerMm, crop.Min.Y / FtPerMm, crop.Max.X / FtPerMm, crop.Max.Y / FtPerMm };
                return SectionMarkRules.Crop(true, nonRect, seg, rect, null);
            }
            catch (Exception ex) { return SectionMarkRules.Crop(true, null, null, null, ex.Message); }
        }
    }
}
