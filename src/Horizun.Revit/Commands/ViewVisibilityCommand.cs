// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// horizun_view_visibility - visibility in OUR OWN views only
// (FIELD FEEDBACK 2.6, 4.3).
//
// In a shared model the documentation agent cleaned its views by hand: marks of
// other people's sections enlarged its viewports, and its own detail sections
// showed up in everybody's plans. This command hides/unhides elements and
// categories and sets/clears graphic overrides, ONLY in a view that is ours
// (OwnershipProbe: ownership mark, created this session, or worksharing creator =
// this Revit user); a foreign view is refused by name with the evidence.
//
//   read                  the view's ownership and the markers visible in it, own or not.
//   hide / unhide         element_ids and/or categories, permanently, in view_id.
//   set_overrides         element_ids and/or categories (same override keys as
//   / clear_overrides     horizun_manage_views).
//   hide_foreign_markers  hides, in view_id, every visible section/callout/elevation
//                         marker that is NOT ours.
//   marker_scale          "Hide at scales coarser than" on OUR views in view_ids, so
//                         their markers stay out of other views (1 = everywhere).
//   datum_cleanup         hides, in view_id, the host grids/levels that fall outside its crop and/or that are
//                         not ours. See ViewVisibilityDatums.cs.
//   set_scale             changes OUR view's scale and keeps other people's markers hidden after it.
//   copy_link_overrides   the RVT Links tab of view_id (per-category visibility INSIDE a
//                         link included) onto OUR views in view_ids, through a temporary
//                         view template. See ViewVisibilityLinks.cs.
//   link_datum_cleanup    hides, in OUR view_id, linked grids/levels with a hidden "Type Name not
//                         equal to" filter; reports what it cannot separate. ViewVisibilityLinkDatums.cs.
//   override_report       read: per view of view_ids, which elements carry the override. P5:
//                         set_overrides predicts, per element, whether it will print, before
//                         writing (unprinted, clear_category_overrides). ViewVisibilityPrint.cs.
//
// Through ModelEditRunner: rehearse by default, single-use token, re-read every
// change after commit (IsHidden, GetCategoryHidden, the overrides, the scale) and
// roll the whole edit back on any disagreement.
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
        private static List<Element> Elements(Document doc, JObject r, View view = null)
        {
            var found = ((r["element_ids"] as JArray) ?? new JArray()).Select(t =>
            {
                long raw = t.Value<long>();
                Element e = Rid.CanRepresent(raw) ? doc.GetElement(Rid.Make(raw)) : null;
                if (e == null) throw new ArgumentException("element " + raw + " is not an element of this document.");
                return e;
            }).ToList();
            // 4.2-N4 (CT-P2-2c): a whole family or type, as it appears in THIS view.
            var families = (r["family_names"] as JArray)?.Select(t => t.Value<string>()).ToList();
            var types = (r["type_names"] as JArray)?.Select(t => t.Value<string>()).ToList();
            if (view != null && ((families?.Count ?? 0) > 0 || (types?.Count ?? 0) > 0))
            {
                var byType = new Dictionary<long, bool>();
                int matched = 0;
                foreach (Element e in new FilteredElementCollector(doc, view.Id).WhereElementIsNotElementType())
                {
                    ElementId tid = e.GetTypeId();
                    if (tid == ElementId.InvalidElementId) continue;
                    long key = Rid.Value(tid);
                    if (!byType.TryGetValue(key, out bool hit))
                    {
                        ElementType t = doc.GetElement(tid) as ElementType;
                        hit = t != null && ViewVisibilityRules.SelectsType(t.FamilyName, t.Name, families, types);
                        byType[key] = hit;
                    }
                    if (!hit) continue;
                    found.Add(e);
                    if (++matched > ViewVisibilityRules.MaxIds)
                        throw new ArgumentException("family_names/type_names select more than " + ViewVisibilityRules.MaxIds + " elements in view '" + view.Name + "'. Narrow the selection. Nothing was written.");
                }
                if (matched == 0)
                    throw new ArgumentException("no element in view '" + view.Name + "' belongs to " + string.Join(", ", (families ?? new List<string>()).Concat(types ?? new List<string>())) + ". Nothing was written.");
            }
            return found.GroupBy(e => Rid.Value(e.Id)).Select(g => g.First()).ToList();
        }

        private static List<KeyValuePair<string, ElementId>> Categories(Document doc, JObject r) =>
            ((r["categories"] as JArray) ?? new JArray()).Select(t => new KeyValuePair<string, ElementId>(t.ToString(),
                ManageViewsCommand.ResolveCategory(doc, t.ToString()))).ToList();

        // ---- read ---------------------------------------------------------------------

        private static ArchModelEdit Read(Document doc, View view, HashSet<string> session)
        {
            var markers = new JArray();
            foreach (Element m in new FilteredElementCollector(doc, view.Id).WherePasses(ViewMarkers.Filter()).WhereElementIsNotElementType())
            {
                JObject row = OwnershipProbe.Row(doc, m, session);
                row["name"] = m.Name; row["category"] = m.Category?.Name; row["built_in_category"] = CategoryKey.Of(m);
                try { row["hidden"] = m.IsHidden(view); } catch { }
                markers.Add(row);
            }
            JObject own = OwnershipProbe.Row(doc, view, session);
            var result = new JObject
            {
                ["read_only"] = true, ["view_id"] = Rid.Value(view.Id), ["view_name"] = view.Name,
                ["ownership"] = own, ["hide_marker_coarser_than"] = ViewMarkers.ReadCoarser(view),
                ["markers_in_view"] = markers,
                ["foreign_markers_visible"] = markers.OfType<JObject>().Count(m => m.Value<bool?>("own") == false && m.Value<bool?>("hidden") != true)
            };
            return new ArchModelEdit { ReadResult = result };
        }

        // ---- hide / unhide / overrides ---------------------------------------------------

        private static ArchModelEdit Edit(Document doc, JObject r, string op, View view)
        {
            List<Element> elements = Elements(doc, r, view);
            var cats = Categories(doc, r);
            bool overrides = op == "set_overrides" || op == "clear_overrides";
            JObject wanted = op == "set_overrides" ? (JObject)r["overrides"].DeepClone() : null;
            OverrideGraphicSettings ogs = op == "set_overrides" ? ManageViewsCommand.ReadOverrides(doc, wanted) : new OverrideGraphicSettings();
            if (overrides) ManageViewsCommand.RequireOverridesAllowed(view, op == "set_overrides" ? "overrides" : "an override reset");
            foreach (var c in cats)
            {
                if (overrides)
                {
                    if (!view.IsCategoryOverridable(c.Value)) throw new ArgumentException("category '" + c.Key + "' does not accept overrides in view '" + view.Name + "'. Nothing was written.");
                }
                else ManageViewsCommand.RequireCategoryHideable(doc, view, c.Value, c.Key);
            }
            if (op == "hide")
                foreach (Element e in elements)
                    if (!e.CanBeHidden(view)) throw new ArgumentException("element " + Rid.Value(e.Id) + " cannot be hidden in view '" + view.Name + "'. Nothing was written.");

            // P5: predict, per element, whether the override will reach the paper, before writing it.
            JObject printCheck = null;
            var clearCategories = new List<ElementId>();
            JArray behind = null;
            if (op == "set_overrides" && elements.Count > 0)
            {
                string policy = r.Value<string>("unprinted") ?? OverridePrintRules.PolicyRefuse;
                var blocked = new List<Element>();
                var contexts = new Dictionary<long, PrintContext>();   // one context per view per call
                printCheck = PrintCheck(doc, view, elements, wanted, blocked, contexts);
                printCheck["unprinted"] = policy;
                if (blocked.Count > 0 && policy == OverridePrintRules.PolicyRefuse)
                    throw new ArgumentException(OverridePrintRules.Refusal(blocked.Count, elements.Count, (JObject)printCheck["counts"]) +
                        " print_check: " + printCheck["items"].ToString(Newtonsoft.Json.Formatting.None));
                if (blocked.Count > 0 && policy == OverridePrintRules.PolicySkip)
                {
                    var skip = new HashSet<long>(blocked.Select(e => Rid.Value(e.Id)));
                    elements = elements.Where(e => !skip.Contains(Rid.Value(e.Id))).ToList();
                    printCheck["skipped_ids"] = new JArray(skip.Select(i => (JToken)i));
                    if (elements.Count == 0 && cats.Count == 0)
                        return new ArchModelEdit { ReadResult = new JObject
                        {
                            ["operation"] = op, ["view_id"] = Rid.Value(view.Id), ["state"] = "no_op", ["print_check"] = printCheck,
                            ["means"] = "every element was predicted not to print as asked and unprinted=skip; nothing was written"
                        } };
                }
                behind = new JArray();
                bool clear = r.Value<bool?>("clear_category_overrides") == true;
                foreach (var kv in CategoriesBehind(doc, view, elements, contexts))
                {
                    if (clear && cats.Any(c => c.Value.Equals(kv.Key)))
                    {
                        var clash = new ArgumentException("category '" + kv.Value.Value<string>("category") + "' is in categories (a category override to write) and would be cleared by " +
                            "clear_category_overrides in the same call. Send them in two calls. Nothing was written.");
                        clash.Data["error_code"] = ErrorCodes.EntradaInvalida;
                        throw clash;
                    }
                    if (clear && kv.Value.Value<bool>("from_template"))
                        throw new ArgumentException("category '" + kv.Value.Value<string>("category") + "' takes its override from view template " + Rid.Value(view.ViewTemplateId) +
                            "; clear_category_overrides cannot change the template. Edit the template, or omit clear_category_overrides. Nothing was written.");
                    if (clear) { clearCategories.Add(kv.Key); kv.Value["cleared"] = true; }
                    behind.Add(kv.Value);
                }
            }

            var edit = new ArchModelEdit();
            edit.Summary["operation"] = op; edit.Summary["view_id"] = Rid.Value(view.Id); edit.Summary["view_name"] = view.Name;
            if (printCheck != null) { edit.Summary["print_check"] = printCheck; edit.Evidence["print_check"] = printCheck; }
            if (wanted != null)
            {
                // A fill colour with no pattern paints on the solid drafting fill (ReadOverrides resolves it); said, not hidden.
                JObject defaulted = OverridePrintRules.PatternDefaults((JObject)r["overrides"]);
                if (defaulted.Count > 0) { edit.Summary["pattern_defaulted"] = defaulted; edit.Evidence["pattern_defaulted"] = defaulted; }
            }
            if (behind != null && behind.Count > 0) { edit.Summary["category_overrides_behind"] = behind; edit.Evidence["category_overrides_behind"] = behind; }
            edit.Summary["element_ids"] = new JArray(elements.Select(e => (JToken)Rid.Value(e.Id)));
            edit.Summary["categories"] = new JArray(cats.Select(c => (JToken)c.Key));
            var planned = PlanVisibilityTargets(view, r, elements);
            var unhide = op == "unhide" ? ResolveUnhide(doc, view, elements) : null;
            if (unhide != null)
            {
                var targets = new JArray(unhide.Select(target => target.ToJson()));
                edit.Summary["unhide_targets"] = targets;
                planned.BeforeValues["unhide_targets"] = targets.ToString(Newtonsoft.Json.Formatting.None);
            }
            planned.BeforeValues["category_ids"] = new JArray(cats.Select(c => Rid.Value(c.Value)).Distinct().OrderBy(id => id))
                .ToString(Newtonsoft.Json.Formatting.None);
            planned.BeforeValues["clear_category_ids"] = new JArray(clearCategories.Select(Rid.Value).Distinct().OrderBy(id => id))
                .ToString(Newtonsoft.Json.Formatting.None);
            edit.Planned.Add(planned);
            var ids = elements.Select(e => e.Id).ToList();
            // What each element must read back: set_overrides keeps what the view already held and changes
            // only the fields named; a transparency of 0 over a category transparency is written as 1 (see
            // LinkViewVisibilityRules.OpaqueOverInheritedTransparency) and said so.
            var expect = new Dictionary<long, JObject>();
            var transparencyNotes = new JArray();
            edit.Apply = d =>
            {
                View v = (View)d.GetElement(view.Id);
                if (op == "hide" && ids.Count > 0) v.HideElements(ids);
                if (unhide != null) ApplyUnhide(d, v, unhide);
                if (op == "clear_overrides") foreach (ElementId id in ids) v.SetElementOverrides(id, ogs);
                if (op == "set_overrides")
                    foreach (ElementId id in ids)
                    {
                        var want = (JObject)wanted.DeepClone();
                        int? asked = want.Value<int?>("transparency");
                        Element e = d.GetElement(id);
                        int inherited = 0;
                        try { if (e?.Category != null) inherited = v.GetCategoryOverrides(e.Category.Id).Transparency; } catch { }
                        int? written = asked == null ? null : LinkViewVisibilityRules.OpaqueOverInheritedTransparency(asked.Value, inherited);
                        if (written != null)
                        {
                            want["transparency"] = written.Value;
                            transparencyNotes.Add(new JObject { ["element_id"] = Rid.Value(id), ["asked"] = asked.Value, ["written"] = written.Value,
                                ["category_transparency"] = inherited,
                                ["why"] = "Revit stores 'no transparency override' as 0, so 0 on the element cannot beat the category's " + inherited + "; 1 is the most opaque value it holds." });
                        }
                        expect[Rid.Value(id)] = want;
                        v.SetElementOverrides(id, ManageViewsCommand.ReadOverrides(d, want, v.GetElementOverrides(id)));
                    }
                foreach (var c in cats)
                {
                    if (op == "hide") v.SetCategoryHidden(c.Value, true);
                    else if (op == "unhide") v.SetCategoryHidden(c.Value, false);
                    else if (op == "set_overrides") v.SetCategoryOverrides(c.Value, ManageViewsCommand.ReadOverrides(d, (JObject)wanted.DeepClone(), v.GetCategoryOverrides(c.Value)));
                    else v.SetCategoryOverrides(c.Value, ogs);
                }
                foreach (ElementId c in clearCategories) v.SetCategoryOverrides(c, new OverrideGraphicSettings());
                if (transparencyNotes.Count > 0) edit.Evidence["transparency_adjusted"] = transparencyNotes;
            };
            edit.Verify = d =>
            {
                View v = (View)d.GetElement(view.Id);
                var req = new List<string>();
                req.AddRange(unhide == null ? ids.Select(id => "element:" + Rid.Value(id)) : unhide.Select(target => target.Property));
                req.AddRange(cats.Select(c => "category:" + c.Key));
                req.AddRange(clearCategories.Select(c => "category_cleared:" + Rid.Value(c)));
                var check = new PostconditionCheck(req.ToArray());
                if (unhide != null)
                    foreach (UnhideTarget target in unhide)
                        target.Verify(d, v, check);
                else foreach (ElementId id in ids)
                {
                    Element e = d.GetElement(id);
                    if (overrides)
                    {
                        OverrideGraphicSettings got = v.GetElementOverrides(id);
                        JObject want = expect.TryGetValue(Rid.Value(id), out JObject w) ? w : wanted;
                        check.Record("element:" + Rid.Value(id), want ?? (JToken)"cleared", Describe(got), Matches(got, want));
                    }
                    else
                    {
                        bool hidden = e != null && e.IsHidden(v);
                        check.Compare("element:" + Rid.Value(id), op == "hide", hidden);
                    }
                }
                foreach (var c in cats)
                {
                    if (overrides)
                    {
                        OverrideGraphicSettings got = v.GetCategoryOverrides(c.Value);
                        check.Record("category:" + c.Key, wanted ?? (JToken)"cleared", Describe(got), Matches(got, wanted));
                    }
                    else check.Compare("category:" + c.Key, op == "hide", v.GetCategoryHidden(c.Value));
                }
                foreach (ElementId c in clearCategories)
                    check.Compare("category_cleared:" + Rid.Value(c), true, IsCleared(v.GetCategoryOverrides(c)));
                return check;
            };
            return edit;
        }

        private static PlannedElement PlanVisibilityTargets(View view, JObject request, IEnumerable<Element> elements)
        {
            var planned = ModelEditRunner.Planned(view, PlannedAction.Modify, request);
            // The view is the edited object. Its plan must also bind the exact
            // elements selected for the edit, independently of collector order.
            planned.BeforeValues["resolved_elements"] = new JArray(elements.OrderBy(e => Rid.Value(e.Id))
                .Select(e => new JArray(Rid.Value(e.Id), e.UniqueId))).ToString(Newtonsoft.Json.Formatting.None);
            return planned;
        }

        internal sealed class UnhideTarget
        {
            internal readonly ElementId Id;
            internal readonly string UniqueId;
            internal readonly bool WasHidden;
            internal readonly bool ExpectedHidden;
            internal readonly bool ReleaseTypeFirst;
            internal string Property => "element:" + Rid.Value(Id);

            internal UnhideTarget(Element element, View view, bool expectedHidden, bool releaseTypeFirst = false)
            {
                Id = element.Id;
                UniqueId = element.UniqueId;
                WasHidden = element.IsHidden(view);
                ExpectedHidden = expectedHidden;
                ReleaseTypeFirst = releaseTypeFirst;
                if (ExpectedHidden && !WasHidden && !element.CanBeHidden(view))
                    throw new ArgumentException("Link instance " + Rid.Value(Id) + " cannot be kept hidden in view '" +
                        view.Name + "' after unhiding its type. Nothing was written.");
            }

            internal JObject ToJson() => new JObject { ["element_id"] = Rid.Value(Id), ["unique_id"] = UniqueId,
                ["was_hidden"] = WasHidden, ["expected_hidden"] = ExpectedHidden };

            internal Element Resolve(Document doc)
            {
                Element element = doc.GetElement(Id);
                if (element == null || !string.Equals(element.UniqueId, UniqueId, StringComparison.Ordinal))
                    throw new InvalidOperationException("Element " + Rid.Value(Id) + " no longer resolves to the same UniqueId.");
                return element;
            }

            internal void Verify(Document doc, View view, PostconditionCheck check)
            {
                try { check.Compare(Property, ExpectedHidden, Resolve(doc).IsHidden(view)); }
                catch (Exception ex) { check.Unreadable(Property, ExpectedHidden, ex.Message); }
            }
        }

        private static List<UnhideTarget> ResolveUnhide(Document doc, View view, IEnumerable<Element> elements)
        {
            var requested = elements.ToList();
            var linkTypeIds = new HashSet<long>(requested.OfType<RevitLinkInstance>().Select(e => Rid.Value(e.GetTypeId())));
            var targets = requested.ToDictionary(e => Rid.Value(e.Id),
                e => new UnhideTarget(e, view, false, linkTypeIds.Contains(Rid.Value(e.Id))));
            var hiddenTypes = new HashSet<long>();
            foreach (long typeId in linkTypeIds)
            {
                if (!targets.TryGetValue(typeId, out UnhideTarget typeTarget))
                {
                    Element type = doc.GetElement(Rid.Make(typeId));
                    if (type == null)
                        throw new ArgumentException("Link type " + typeId + " could not be read. Nothing was written.");
                    targets.Add(typeId, typeTarget = new UnhideTarget(type, view, false, releaseTypeFirst: true));
                }
                if (typeTarget.WasHidden) hiddenTypes.Add(typeId);
            }

            // Releasing a hidden type would also expose its unrequested instances.
            // Keep them hidden individually and include already-hidden siblings in the re-read.
            if (hiddenTypes.Count > 0)
                foreach (RevitLinkInstance instance in new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)))
                {
                    long id = Rid.Value(instance.Id);
                    if (hiddenTypes.Contains(Rid.Value(instance.GetTypeId())) && !targets.ContainsKey(id))
                        targets.Add(id, new UnhideTarget(instance, view, true));
                }
            return targets.Values.OrderBy(target => Rid.Value(target.Id)).ToList();
        }

        private static void ApplyUnhide(Document doc, View view, List<UnhideTarget> targets)
        {
            var types = targets.Where(t => t.ReleaseTypeFirst && t.WasHidden).Select(t => t.Id).ToList();
            if (types.Count > 0)
            {
                // IsHidden can include the hide inherited from a link type. Read the instances
                // after releasing the type, when their individual hides can be distinguished.
                view.UnhideElements(types);
                doc.Regenerate();
            }
            var unhide = new List<ElementId>();
            var keepHidden = new List<ElementId>();
            foreach (UnhideTarget target in targets.Where(t => !t.ReleaseTypeFirst))
            {
                Element element = target.Resolve(doc);
                if (element.IsHidden(view) == target.ExpectedHidden) continue;
                if (!target.ExpectedHidden) unhide.Add(target.Id);
                else
                {
                    if (!element.CanBeHidden(view))
                        throw new InvalidOperationException("Link instance " + Rid.Value(target.Id) + " cannot be kept hidden in view '" + view.Name + "'.");
                    keepHidden.Add(target.Id);
                }
            }
            if (unhide.Count > 0) view.UnhideElements(unhide);
            if (keepHidden.Count > 0) view.HideElements(keepHidden);
        }

        internal static List<UnhideTarget> UnhideWithLinkTypes(Document doc, View view, ICollection<ElementId> ids)
        {
            var targets = ResolveUnhide(doc, view, ids.Select(Rid.Value).Distinct().Select(id => doc.GetElement(Rid.Make(id))));
            ApplyUnhide(doc, view, targets);
            return targets;
        }

        private static bool Matches(OverrideGraphicSettings got, JObject wanted) =>
            wanted != null ? ManageViewsCommand.OverridesMatch(got, wanted) : IsCleared(got);

        /// <summary>A cleared override: no colour, no weight, no pattern, no halftone, opaque.</summary>
        private static bool IsCleared(OverrideGraphicSettings g)
        {
            if (g == null) return false;
            try
            {
                return !g.ProjectionLineColor.IsValid && !g.CutLineColor.IsValid && !g.Halftone && g.Transparency == 0 &&
                       g.ProjectionLineWeight == OverrideGraphicSettings.InvalidPenNumber &&
                       g.ProjectionLinePatternId == ElementId.InvalidElementId &&
                       g.SurfaceForegroundPatternId == ElementId.InvalidElementId && g.CutForegroundPatternId == ElementId.InvalidElementId;
            }
            catch { return false; }
        }

        private static JObject Describe(OverrideGraphicSettings g)
        {
            if (g == null) return null;
            string Hex(Color c) { try { return c != null && c.IsValid ? "#" + c.Red.ToString("X2") + c.Green.ToString("X2") + c.Blue.ToString("X2") : null; } catch { return null; } }
            return new JObject
            {
                ["line_color"] = Hex(g.ProjectionLineColor), ["line_weight"] = g.ProjectionLineWeight,
                ["line_pattern_id"] = Rid.Value(g.ProjectionLinePatternId), ["cut_line_color"] = Hex(g.CutLineColor),
                // The fills the comparison checks (OverridesMatch): shown so the evidence names what was matched.
                ["surface_color"] = Hex(g.SurfaceForegroundPatternColor), ["surface_pattern_id"] = Rid.Value(g.SurfaceForegroundPatternId),
                ["surface_pattern_visible"] = g.IsSurfaceForegroundPatternVisible,
                ["cut_color"] = Hex(g.CutForegroundPatternColor), ["cut_pattern_id"] = Rid.Value(g.CutForegroundPatternId),
                ["cut_pattern_visible"] = g.IsCutForegroundPatternVisible,
                ["halftone"] = g.Halftone, ["transparency"] = g.Transparency
            };
        }

        // ---- explain_overrides (4.2-N4, CT-P2-2e) --------------------------------------------

        private static OverrideFacts PatternFacts(OverrideGraphicSettings g)
        {
            var f = new OverrideFacts { Halftone = g.Halftone, Transparency = g.Transparency };
            f.SurfaceForegroundPatternSet = g.SurfaceForegroundPatternId != ElementId.InvalidElementId;
            f.SurfaceForegroundVisible = g.IsSurfaceForegroundPatternVisible;
            f.SurfaceForegroundColorSet = g.SurfaceForegroundPatternColor != null && g.SurfaceForegroundPatternColor.IsValid;
            f.CutForegroundPatternSet = g.CutForegroundPatternId != ElementId.InvalidElementId;
            f.CutForegroundVisible = g.IsCutForegroundPatternVisible;
            f.CutForegroundColorSet = g.CutForegroundPatternColor != null && g.CutForegroundPatternColor.IsValid;
            f.SurfaceBackgroundPatternSet = g.SurfaceBackgroundPatternId != ElementId.InvalidElementId;
            f.SurfaceBackgroundVisible = g.IsSurfaceBackgroundPatternVisible;
            f.CutBackgroundPatternSet = g.CutBackgroundPatternId != ElementId.InvalidElementId;
            f.CutBackgroundVisible = g.IsCutBackgroundPatternVisible;
            return f;
        }

        private static ArchModelEdit ExplainOverrides(Document doc, JObject r, View view)
        {
            List<Element> elements = Elements(doc, r, view);
            var cats = Categories(doc, r);
            bool allowed = view.AreGraphicsOverridesAllowed();
            string filterWarning = null;
            var graphics = new Dictionary<long, JObject>();
            if (allowed && elements.Count > 0)
                try
                {
                    graphics = ManageViewsCommand.ExplainGraphics(doc, view, elements.Select(e => e.Id).Distinct().ToList())
                        .OfType<JObject>().ToDictionary(row => row.Value<long>("element_id"));
                }
                catch (Exception ex) { filterWarning = "View filter precedence could not be read: " + ex.Message; }
            if (filterWarning == null && graphics.Values.Any(row => row["filter_warning"] != null))
                filterWarning = "Some view filter facts could not be read. See the affected items' filter_warning.";
            var catReasons = new Dictionary<long, List<JObject>>();
            List<JObject> CategoryReasons(Category c)
            {
                if (c == null) return new List<JObject>();
                long key = Rid.Value(c.Id);
                if (!catReasons.TryGetValue(key, out List<JObject> list))
                {
                    try { list = PatternBlockerRules.Explain(PatternFacts(view.GetCategoryOverrides(c.Id))); } catch { list = new List<JObject>(); }
                    catReasons[key] = list;
                }
                return list;
            }
            var rows = new JArray();
            foreach (Element e in elements)
            {
                var row = new JObject { ["scope"] = "element", ["element_id"] = Rid.Value(e.Id), ["name"] = e.Name, ["category"] = e.Category?.Name, ["built_in_category"] = CategoryKey.Of(e) };
                row["reasons"] = new JArray(PatternBlockerRules.Explain(PatternFacts(view.GetElementOverrides(e.Id))));
                row["category_reasons"] = new JArray(CategoryReasons(e.Category));
                if (graphics.TryGetValue(Rid.Value(e.Id), out JObject explanation))
                    GraphicsPrecedence.AddFilterExplanation(row, explanation);
                try { row["category_hidden_in_view"] = e.Category != null && view.GetCategoryHidden(e.Category.Id); } catch { }
                try { row["element_hidden_in_view"] = e.IsHidden(view); } catch { }
                rows.Add(row);
            }
            foreach (var c in cats)
            {
                var row = new JObject { ["scope"] = "category", ["category"] = c.Key };
                row["reasons"] = new JArray(PatternBlockerRules.Explain(PatternFacts(view.GetCategoryOverrides(c.Value))));
                row["category_reasons"] = new JArray();
                try { row["category_hidden_in_view"] = view.GetCategoryHidden(c.Value); } catch { }
                rows.Add(row);
            }
            string template = null;
            try { Element t = doc.GetElement(view.ViewTemplateId); if (t != null) template = t.Name; } catch { }
            string style = null;
            try { style = view.DisplayStyle.ToString(); } catch { }
            List<JObject> viewReasons = PatternBlockerRules.ExplainView(new ViewPatternFacts
            { DisplayStyle = style, OverridesAllowed = allowed, TemplateName = template, TemplateControlsGraphics = !allowed && template != null });
            int total = rows.OfType<JObject>().Sum(x => ((JArray)x["reasons"]).Count + ((JArray)x["category_reasons"]).Count) + viewReasons.Count;
            var result = new JObject
            {
                ["read_only"] = true, ["view_id"] = Rid.Value(view.Id), ["view_name"] = view.Name,
                ["view_reasons"] = new JArray(viewReasons), ["items"] = rows, ["reason_count"] = total,
                ["precedence"] = "for each graphic setting, element overrides precede enabled matching filters in view order, then category; any applicable layer can hide the element",
                ["means"] = "conditions that hide or weaken a pattern, read off the settings; they do not prove what prints"
            };
            if (filterWarning != null) result["filter_warning"] = filterWarning;
            return new ArchModelEdit { ReadResult = result };
        }

        // ---- hide_foreign_markers ------------------------------------------------------------

        private static ArchModelEdit HideForeignMarkers(Document doc, View view, HashSet<string> session, JObject r)
        {
            var kept = new JArray();
            List<Element> foreign = ForeignVisibleMarkers(doc, view, session, kept);
            if (foreign.Count == 0)
            {
                // Measured live 2026-10-18: a clean view answered an ERROR here. Nothing to hide
                // is the state the caller wants; it is reported as a read, not as a failure.
                var clean = new ArchModelEdit();
                clean.ReadResult = new JObject
                {
                    ["operation"] = "hide_foreign_markers", ["view_id"] = Rid.Value(view.Id), ["state"] = "no_op",
                    ["foreign_visible"] = 0, ["kept_visible"] = kept,
                    ["means"] = "no foreign section, callout or elevation marker is visible in this view; nothing was written"
                };
                return clean;
            }
            var ids = foreign.Select(e => e.Id).ToList();
            var edit = new ArchModelEdit();
            edit.Summary["operation"] = "hide_foreign_markers"; edit.Summary["view_id"] = Rid.Value(view.Id);
            edit.Summary["hide"] = new JArray(foreign.Select(e => new JObject { ["element_id"] = Rid.Value(e.Id), ["name"] = e.Name, ["category"] = e.Category?.Name, ["built_in_category"] = CategoryKey.Of(e) }));
            edit.Summary["kept_visible"] = kept;
            edit.Planned.Add(PlanVisibilityTargets(view, r, foreign));
            edit.Apply = d => ((View)d.GetElement(view.Id)).HideElements(ids);
            edit.Verify = d =>
            {
                View v = (View)d.GetElement(view.Id);
                var check = new PostconditionCheck(ids.Select(id => "hidden:" + Rid.Value(id)).ToArray());
                foreach (ElementId id in ids) { Element e = d.GetElement(id); check.Compare("hidden:" + Rid.Value(id), true, e != null && e.IsHidden(v)); }
                edit.Evidence["hidden_ids"] = new JArray(ids.Select(i => (JToken)Rid.Value(i)));
                return check;
            };
            return edit;
        }

        // ---- marker_scale -----------------------------------------------------------------

        private static ArchModelEdit MarkerScale(Document doc, JObject r, HashSet<string> session)
        {
            int scale = r.Value<int>("coarser_than");
            var views = ((JArray)r["view_ids"]).Select(t =>
            {
                long raw = t.Value<long>();
                View v = Rid.CanRepresent(raw) ? doc.GetElement(Rid.Make(raw)) as View : null;
                if (v == null) throw new ArgumentException("view_ids: " + raw + " is not a view of this document.");
                RequireOwn(doc, v, session);
                if (ViewMarkers.ReadCoarser(v) == null && !(v is ViewSection))
                    throw new ArgumentException("view '" + v.Name + "' has no marker scale ('Hide at scales coarser than' belongs to sections, callouts and elevations).");
                return v;
            }).ToList();
            var edit = new ArchModelEdit();
            edit.Summary["operation"] = "marker_scale"; edit.Summary["coarser_than"] = scale;
            edit.Summary["views"] = new JArray(views.Select(v => new JObject { ["view_id"] = Rid.Value(v.Id), ["name"] = v.Name, ["current"] = ViewMarkers.ReadCoarser(v) }));
            foreach (View v in views) edit.Planned.Add(ModelEditRunner.Planned(v, PlannedAction.Modify, r));
            edit.Apply = d => { foreach (View v in views) ViewMarkers.SetCoarser((View)d.GetElement(v.Id), scale); };
            edit.Verify = d =>
            {
                var check = new PostconditionCheck(views.Select(v => "coarser_than:" + Rid.Value(v.Id)).ToArray());
                foreach (View v in views)
                {
                    int? got = ViewMarkers.ReadCoarser((View)d.GetElement(v.Id));
                    check.Record("coarser_than:" + Rid.Value(v.Id), scale, got, got == scale);
                }
                return check;
            };
            return edit;
        }
    }
}
