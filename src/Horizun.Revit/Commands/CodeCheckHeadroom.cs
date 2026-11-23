// -----------------------------------------------------------------------------
// Horizun Revit MCP - horizun_code_check operation=headroom. Original Horizun code. READ-ONLY.
//
// Clear height under (direction=down) or over (direction=up) MEP, structural or floor
// elements, measured with vertical rays: ReferenceIntersector in the CALLER'S 3D view, over
// the host document and loaded Revit links, with the ray and link handling of the hanger
// rods (MepRoutingHangers.cs FindSupport): a hit on a RevitLinkInstance is resolved to the
// linked element through GetLinkDocument().GetElement(LinkedElementId) and judged by THAT
// element's category; OST_RvtLinks is in the filter so link hits come back at all.
//
// WHY THE CALLER'S VIEW, AND WHAT IS REFUSED. The intersector returns only what its view
// shows (RevitAPI.xml: hidden elements and elements outside the section box are never
// returned). The hangers build a temporary view of known visibility inside a transaction
// they roll back; a check that must not open one takes the caller's view instead and
// refuses, by name, every case read here that would lose surfaces silently: a view
// template, an active section box, a detail level below Fine (pipes, fittings, conduit and
// tray are single lines with no faces there; CoordinationNavisworksReadiness records the
// same at Coarse), temporary hide/isolate, and a target or source category hidden - or an
// enabled filter hiding elements - in the view or its template. A source element hidden by
// itself is skipped by name. Hidden worksets, links and single target elements are not
// read: the reply states that limit; they are never surfaces.
//
// THE COUNT IS BOUNDED BEFORE ANY RAY: the samples are planned arithmetically (along a
// location curve, else a grid over the bounding box - Core/HeadroomRules) and a call over
// HeadroomRules.MaxRays refuses before casting one.
//
// Each ray starts 3 m beyond the element's bounding box - so a slab or screed the element
// is embedded in is ENTERED and Read sees it on both sides - and crosses the element; its far
// face and the first target surface beyond it give the clear height, decided in
// Core/HeadroomRules.Read. A ray that finds nothing is NOT MEASURED, never a pass.
// NOT MEASURED YET (headroom.probes.ps1 measures own host floors): rays against linked
// surfaces, and a perspective view.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public sealed partial class CodeCheckCommand
    {
        private const double HeadroomMmPerFoot = 304.8;

        // The surfaces a ray may stop at when the caller names none: what one walks on
        // below an element, and what one walks under above one.
        private static readonly BuiltInCategory[] HeadroomDown =
        {
            BuiltInCategory.OST_Floors, BuiltInCategory.OST_Stairs, BuiltInCategory.OST_Ramps, BuiltInCategory.OST_Roofs,
            BuiltInCategory.OST_StructuralFoundation, BuiltInCategory.OST_Topography
        };

        private static readonly BuiltInCategory[] HeadroomUp =
        {
            BuiltInCategory.OST_Floors, BuiltInCategory.OST_Ceilings, BuiltInCategory.OST_Roofs, BuiltInCategory.OST_Stairs,
            BuiltInCategory.OST_StructuralFraming, BuiltInCategory.OST_DuctCurves, BuiltInCategory.OST_DuctFitting,
            BuiltInCategory.OST_DuctAccessory, BuiltInCategory.OST_FlexDuctCurves, BuiltInCategory.OST_PipeCurves,
            BuiltInCategory.OST_PipeFitting, BuiltInCategory.OST_PipeAccessory, BuiltInCategory.OST_FlexPipeCurves,
            BuiltInCategory.OST_CableTray, BuiltInCategory.OST_CableTrayFitting, BuiltInCategory.OST_Conduit,
            BuiltInCategory.OST_ConduitFitting, BuiltInCategory.OST_MechanicalEquipment, BuiltInCategory.OST_LightingFixtures,
            BuiltInCategory.OST_Sprinklers
        };

        private sealed class HeadroomPlan
        {
            public Element Element;
            public BoundingBoxXYZ Box;
            public List<XYZ> Points;
            public double SpacingFt;
            public string Sampling;
        }

        private CommandResult ExecuteHeadroom(UIApplication app, JObject request)
        {
            Document doc = app.ActiveUIDocument.Document;
            CommandResult wrong = DocumentGate.ReadGuard(doc, request, Name);
            if (wrong != null) return wrong;
            foreach (string field in new[] { "requirement_set", "requirement_set_path", "include_passes", "travel", "confirmation_token" })
                if (request[field] != null)
                    return CommandResult.Fail("operation=headroom does not take '" + field + "': it casts rays in a 3D view and writes nothing.");
            if (doc.IsFamilyDocument)
                return CommandResult.Fail("operation=headroom needs a project: a family document has no placed elements to stand clear of.");
            if (!(request["headroom"] is JObject h))
                return CommandResult.Fail("operation=headroom needs headroom: {view_id, element_ids | categories, min_mm, direction?, spacing_mm?, targets?}.");
            int max = Math.Max(1, Math.Min(5000, request.Value<int?>("max_findings") ?? 200));

            double? minMm = HeadroomNumber(h, "min_mm");
            if (minMm == null || !(minMm.Value > 0))
                return CommandResult.Fail("headroom.min_mm is required: the clear height in millimetres (> 0) each measured sample is judged against.");
            string direction = (h.Value<string>("direction") ?? "down").Trim().ToLowerInvariant();
            if (direction != "down" && direction != "up")
                return CommandResult.Fail("headroom.direction must be down (to the next surface below each element) or up (to the next above), not '" + direction + "'.");
            double spacingMm = HeadroomNumber(h, "spacing_mm") ?? HeadroomRules.DefaultSpacingMm;
            if (!(spacingMm >= HeadroomRules.MinSpacingMm))
                return CommandResult.Fail("headroom.spacing_mm must be at least " + HeadroomRules.MinSpacingMm + " mm.");

            // ---- the view: the rays see only what it shows ---------------------------------
            long? viewId = null;
            try { viewId = h.Value<long?>("view_id"); } catch { }
            if (viewId == null)
                return CommandResult.Fail("headroom.view_id is required: a 3D view that is not a template and has no active section box - the rays see only what it shows.");
            Element viewElement = doc.GetElement(Rid.Make(viewId.Value));
            if (!(viewElement is View3D view))
                return CommandResult.Fail("headroom.view_id " + viewId + (viewElement == null ? " names no element" : " is a " + viewElement.GetType().Name + ", not a 3D view") +
                    ": the rays need a View3D.");
            if (view.IsTemplate)
                return CommandResult.Fail("headroom.view_id " + viewId + " ('" + view.Name + "') is a view template, which casts no rays: give a 3D view that is not a template.");
            if (view.IsSectionBoxActive)
                return CommandResult.Fail("headroom.view_id " + viewId + " ('" + view.Name + "') has an active section box: the rays never return what lies outside it, " +
                    "so a surface there would read as nothing. Turn the section box off or give another 3D view.");
            if (view.DetailLevel != ViewDetailLevel.Fine)
                return CommandResult.Fail("headroom.view_id " + viewId + " ('" + view.Name + "') is at detail level " + view.DetailLevel + ": below Fine, pipes, " +
                    "fittings, conduit and cable tray are single lines with no faces, so a ray passes through them. Set it to Fine or give another 3D view.");
            if (view.IsTemporaryHideIsolateActive())
                return CommandResult.Fail("headroom.view_id " + viewId + " ('" + view.Name + "') has temporary hide/isolate on: what it hides is not a surface " +
                    "to the rays. Reset it or give another 3D view.");

            // ---- the surfaces a ray may stop at --------------------------------------------
            var targets = new List<BuiltInCategory>();
            if (h["targets"] != null)
            {
                if (!(h["targets"] is JArray named) || named.Count == 0)
                    return CommandResult.Fail("headroom.targets must be a non-empty array of category names (OST_Floors or Floors), or be omitted for the defaults.");
                var unknown = new List<string>();
                foreach (JToken t in named)
                {
                    Category c = t.Type == JTokenType.String ? ResolveCategory(doc, (string)t) : null;
                    if (c == null || Rid.Value(c.Id) >= 0) unknown.Add(t.ToString());
                    else if (!targets.Contains((BuiltInCategory)Rid.Value(c.Id))) targets.Add((BuiltInCategory)Rid.Value(c.Id));
                }
                if (unknown.Count > 0) return CommandResult.Fail("headroom.targets names no built-in category here: " + string.Join(", ", unknown) + ".");
            }
            else
            {
                targets.AddRange(direction == "down" ? HeadroomDown : HeadroomUp);
                // Toposolid exists from Revit 2024: parsed by name so the 2023 build compiles.
                if (direction == "down" && Enum.TryParse("OST_Toposolid", out BuiltInCategory topo)) targets.Add(topo);
            }
            // Component stairs keep their faces on their runs and landings, not on the Stairs element.
            if (targets.Contains(BuiltInCategory.OST_Stairs))
                foreach (BuiltInCategory part in new[] { BuiltInCategory.OST_StairsRuns, BuiltInCategory.OST_StairsLandings })
                    if (!targets.Contains(part)) targets.Add(part);
            var targetIds = new HashSet<long>(targets.Select(b => (long)b));

            // ---- the elements measured -------------------------------------------------------
            JArray ids = h["element_ids"] as JArray, cats = h["categories"] as JArray;
            bool byId = ids != null && ids.Count > 0, byCategory = cats != null && cats.Count > 0;
            if (byId == byCategory)
                return CommandResult.Fail("Give exactly one of headroom.element_ids or headroom.categories: the elements whose clear height is measured.");
            var sources = new List<Element>();
            var seen = new HashSet<long>();
            var skipped = new JArray();
            if (byId)
            {
                foreach (JToken t in ids)
                {
                    long id;
                    try { id = t.Value<long>(); }
                    catch { return CommandResult.Fail("headroom.element_ids must be integers, not " + t + "."); }
                    Element e = doc.GetElement(Rid.Make(id));
                    string why = e == null ? "no element has this id" : HeadroomUnfit(e);
                    if (why != null) skipped.Add(new JObject { ["element_id"] = id, ["reason"] = why });
                    else if (seen.Add(id)) sources.Add(e);
                }
            }
            else
            {
                var unknown = new List<string>();
                foreach (JToken t in cats)
                {
                    Category c = t.Type == JTokenType.String ? ResolveCategory(doc, (string)t) : null;
                    if (c == null) { unknown.Add(t.ToString()); continue; }
                    // By category, the elements are the ones this view shows: the rays see nothing else.
                    foreach (Element e in new FilteredElementCollector(doc, view.Id).OfCategoryId(c.Id).WhereElementIsNotElementType())
                        if (HeadroomUnfit(e) == null && seen.Add(Rid.Value(e.Id))) sources.Add(e);
                }
                if (unknown.Count > 0) return CommandResult.Fail("headroom.categories names categories that do not resolve here: " + string.Join(", ", unknown) + ".");
            }
            if (sources.Count > HeadroomRules.MaxElements)
                return CommandResult.Fail("headroom would measure " + sources.Count + " elements, over the " + HeadroomRules.MaxElements + " one call may: name fewer. No ray was cast.");
            string hides = HeadroomViewHides(doc, view, targets, sources);
            if (hides != null)
                return CommandResult.Fail("headroom.view_id " + viewId + " ('" + view.Name + "') hides " + hides + ": the rays never return what a view hides, " +
                    "so a surface there would read as open space. Show them or give another 3D view. No ray was cast.");

            // ---- pass 1, arithmetic only: every sample planned and counted --------------------
            double spacingFt = spacingMm / HeadroomMmPerFoot;
            var plans = new List<HeadroomPlan>();
            int rays = 0;
            foreach (Element e in sources)
            {
                BoundingBoxXYZ box = null;
                try { box = e.get_BoundingBox(null); } catch { }
                if (box == null) { skipped.Add(HeadroomSkip(e, "no model bounding box: nothing to cast from")); continue; }
                bool hiddenHere = false;
                try { hiddenHere = e.IsHidden(view); } catch { }
                if (hiddenHere) { skipped.Add(HeadroomSkip(e, "hidden in this view: the rays would not see its own faces")); continue; }
                var plan = new HeadroomPlan { Element = e, Box = box };
                Curve curve = null;
                try { curve = (e.Location as LocationCurve)?.Curve; } catch { }
                if (curve != null && curve.IsBound)
                {
                    XYZ d = curve.GetEndPoint(1) - curve.GetEndPoint(0);
                    if (d.GetLength() > 1e-9 && Math.Abs(d.Normalize().Z) > 0.999)
                    {
                        skipped.Add(HeadroomSkip(e, "vertical (a riser or a column): it has no clear height beneath or above it"));
                        continue;
                    }
                    IList<double> ts = HeadroomRules.AlongCurve(curve.Length, spacingFt, HeadroomRules.MaxSamplesPerElement, out double used);
                    plan.Points = ts.Select(t => curve.Evaluate(t, true)).ToList();
                    plan.SpacingFt = used;
                    plan.Sampling = "along_location_curve";
                }
                else
                {
                    IList<KeyValuePair<double, double>> grid = HeadroomRules.Grid(box.Min.X, box.Min.Y, box.Max.X, box.Max.Y, spacingFt,
                        HeadroomRules.MaxSamplesPerElement, out double used);
                    plan.Points = grid.Select(p => new XYZ(p.Key, p.Value, 0)).ToList();
                    plan.SpacingFt = used;
                    plan.Sampling = "grid_over_bounding_box";
                }
                rays += plan.Points.Count;
                plans.Add(plan);
            }
            if (rays > HeadroomRules.MaxRays)
                return CommandResult.Fail("headroom would cast " + rays + " rays over " + plans.Count + " elements, over the " + HeadroomRules.MaxRays +
                    " one call may: raise spacing_mm or name fewer elements. No ray was cast.");

            // ---- pass 2, the rays ------------------------------------------------------------
            // Every source category is in the filter too: the element's own faces must come back
            // for its far face to be known. They are told apart by id, never as targets.
            var filterCategories = new HashSet<BuiltInCategory>(targets) { BuiltInCategory.OST_RvtLinks };
            foreach (HeadroomPlan p in plans)
                if (Rid.Value(p.Element.Category.Id) < 0) filterCategories.Add((BuiltInCategory)Rid.Value(p.Element.Category.Id));
            ReferenceIntersector ray;
            try
            {
                ray = new ReferenceIntersector(new ElementMulticategoryFilter(filterCategories.ToList()), FindReferenceTarget.Face, view)
                { FindReferencesInRevitLinks = true };
            }
            catch (Exception ex) { return CommandResult.Fail("Revit refused to cast rays in view " + viewId + " ('" + view.Name + "'): " + ex.Message); }

            XYZ dir = direction == "down" ? -XYZ.BasisZ : XYZ.BasisZ;
            // 10 ft (3.05 m) beyond the bounding box, not just past it: an element embedded in a slab
            // or screed must meet that target's near face before its own, or Read cannot tell
            // "inside a target" from "clear to its far face". Targets of other keys met before the
            // element are ignored by Read, so the longer approach adds no false surface.
            const double margin = 10.0;
            double tolerance = 1.0 / HeadroomMmPerFoot;
            var surfaces = new Dictionary<string, JObject>(StringComparer.Ordinal);
            var rows = new List<JObject>();
            var tally = new Dictionary<string, int> { ["passes"] = 0, ["fails"] = 0, ["not_decidable"] = 0, ["not_measured"] = 0 };
            int measuredTotal = 0;
            string firstRayError = null;
            foreach (HeadroomPlan p in plans)
            {
                long ownId = Rid.Value(p.Element.Id);
                double z0 = direction == "down" ? p.Box.Max.Z + margin : p.Box.Min.Z - margin;
                int on = 0, measured = 0, nothing = 0, inside = 0, off = 0, notCast = 0;
                double? minClear = null;
                XYZ governing = null;
                string governingKey = null;
                foreach (XYZ xy in p.Points)
                {
                    var origin = new XYZ(xy.X, xy.Y, z0);
                    IList<ReferenceWithContext> found = null;
                    try { found = ray.Find(origin, dir); }
                    catch (Exception ex) { if (firstRayError == null) firstRayError = ex.GetType().Name + ": " + ex.Message; }
                    if (found == null) { notCast++; if (firstRayError == null) firstRayError = "the intersector returned no result list"; continue; }
                    var hits = new List<HeadroomRules.Hit>();
                    foreach (ReferenceWithContext rc in found)
                    {
                        HeadroomRules.Hit? hit = HeadroomHit(doc, rc, ownId, targetIds, surfaces);
                        if (hit.HasValue) hits.Add(hit.Value);
                    }
                    HeadroomRules.Sample s = HeadroomRules.Read(hits, tolerance);
                    if (s.State == HeadroomRules.SampleState.OffElement) { off++; continue; }
                    on++;
                    if (s.State == HeadroomRules.SampleState.NothingBeyond) { nothing++; continue; }
                    if (s.State == HeadroomRules.SampleState.InsideTarget) { inside++; continue; }
                    measured++;
                    if (minClear == null || s.Clear < minClear.Value)
                    {
                        minClear = s.Clear;
                        governing = origin + dir * s.FarFace;
                        governingKey = s.TargetKey;
                    }
                }

                double? minClearMm = minClear.HasValue ? minClear.Value * HeadroomMmPerFoot : (double?)null;
                int considered = on + notCast;
                string outcome = HeadroomRules.Outcome(considered, measured, minClearMm, minMm.Value);
                tally[outcome]++;
                measuredTotal += measured;
                var row = new JObject
                {
                    ["element_id"] = ownId,
                    ["category"] = p.Element.Category?.Name, ["built_in_category"] = CategoryKey.Of(p.Element),
                    ["outcome"] = outcome,
                    ["min_clear_mm"] = minClearMm.HasValue ? (JToken)Math.Round(minClearMm.Value, 1) : JValue.CreateNull(),
                    ["samples"] = p.Points.Count,
                    ["on_element"] = on,
                    ["measured"] = measured,
                    ["not_measured"] = nothing + inside + notCast,
                    ["coverage"] = considered > 0 ? (JToken)Math.Round((double)measured / considered, 3) : JValue.CreateNull(),
                    ["off_element"] = off,
                    ["sampling"] = p.Sampling,
                    ["spacing_used_mm"] = Math.Round(p.SpacingFt * HeadroomMmPerFoot, 1)
                };
                if (inside > 0) row["inside_target"] = inside;
                if (notCast > 0) row["rays_not_cast"] = notCast;
                if (governing != null)
                    row["governing"] = new JObject
                    {
                        ["point_mm"] = new JArray(HeadroomMm(governing.X), HeadroomMm(governing.Y), HeadroomMm(governing.Z)),
                        ["surface"] = governingKey != null && surfaces.TryGetValue(governingKey, out JObject surface) ? surface.DeepClone() : JValue.CreateNull()
                    };
                if (outcome == "not_measured")
                    row["reason"] = on == 0 && notCast == 0
                        ? "no ray crossed the element: it is hidden in this view, or its geometry lies off its sample points"
                        : on == 0
                            ? "no ray could be cast: " + firstRayError
                            : "the rays crossed the element and found no target surface " + (direction == "down" ? "below" : "above") +
                              " it (none there, or hidden in this view)" + (inside > 0 ? "; at " + inside + " sample(s) it lies inside a target" : "");
                rows.Add(row);
            }

            List<JObject> ordered = rows.OrderBy(r => HeadroomRules.Rank((string)r["outcome"]))
                .ThenBy(r => r.Value<double?>("min_clear_mm") ?? double.MaxValue).ToList();
            var result = new JObject
            {
                ["document"] = doc.Title,
                ["operation"] = "headroom",
                ["writes"] = "nothing: rays only, cast in the caller's 3D view",
                ["view"] = new JObject { ["id"] = viewId.Value, ["name"] = view.Name, ["perspective"] = view.IsPerspective },
                ["direction"] = direction,
                ["min_mm"] = minMm.Value,
                ["spacing_mm"] = spacingMm,
                ["targets"] = new JArray(targets.Select(b => HeadroomCategoryName(doc, b))),
                ["rules"] = new JObject
                {
                    ["ray"] = "vertical, from 3 m beyond the element's bounding box through the element: clear height = its far face to the first target surface beyond it, host or loaded link",
                    ["not_measured"] = "a ray that crosses the element and finds no target beyond it, or finds the element inside a target, is not measured - never a pass; " +
                                       "an element with an unmeasured sample and no failing one is not_decidable",
                    ["visibility"] = "refused unless the view is at Fine detail with no hidden target/source category, hiding filter or temporary hide; " +
                                     "hidden worksets, links and single target elements are not read and are not surfaces here"
                },
                ["summary"] = new JObject
                {
                    ["elements"] = rows.Count, ["passes"] = tally["passes"], ["fails"] = tally["fails"],
                    ["not_decidable"] = tally["not_decidable"], ["not_measured"] = tally["not_measured"],
                    ["skipped"] = skipped.Count, ["rays"] = rays, ["measured_samples"] = measuredTotal
                },
                ["elements"] = new JArray(ordered.Take(max)),
                ["skipped"] = skipped
            };
            if (ordered.Count > max) result["elements_omitted"] = ordered.Count - max;
            if (firstRayError != null) result["ray_error"] = firstRayError;
            return CommandResult.Ok(result);
        }

        /// <summary>One intersector result as own / target / other, remembering each target surface for the reply.</summary>
        private static HeadroomRules.Hit? HeadroomHit(Document doc, ReferenceWithContext rc, long ownId, HashSet<long> targetIds, Dictionary<string, JObject> surfaces)
        {
            Reference r = rc.GetReference();
            if (r == null) return null;
            Element e = doc.GetElement(r.ElementId);
            if (e is RevitLinkInstance link)
            {
                // As MepRoutingHangers.FindSupport: the surface is the LINKED element, judged by its category.
                if (r.LinkedElementId == ElementId.InvalidElementId) return null;
                Element linked = null;
                try { linked = link.GetLinkDocument()?.GetElement(r.LinkedElementId); } catch { }
                if (linked == null) return null;
                string linkKey = "link:" + Rid.Value(link.Id) + ":" + Rid.Value(linked.Id);
                bool linkedTarget = linked.Category != null && targetIds.Contains(Rid.Value(linked.Category.Id));
                if (linkedTarget && !surfaces.ContainsKey(linkKey))
                    surfaces[linkKey] = new JObject
                    {
                        ["kind"] = "linked", ["element_id"] = Rid.Value(linked.Id), ["link_instance_id"] = Rid.Value(link.Id), ["category"] = linked.Category.Name, ["built_in_category"] = CategoryKey.Of(linked)
                    };
                return new HeadroomRules.Hit(rc.Proximity, linkedTarget ? HeadroomRules.HitKind.Target : HeadroomRules.HitKind.Other, linkKey);
            }
            if (e == null) return null;
            if (Rid.Value(e.Id) == ownId) return new HeadroomRules.Hit(rc.Proximity, HeadroomRules.HitKind.Own, "own");
            string key = "host:" + Rid.Value(e.Id);
            bool target = e.Category != null && targetIds.Contains(Rid.Value(e.Category.Id));
            if (target && !surfaces.ContainsKey(key))
                surfaces[key] = new JObject { ["kind"] = "host", ["element_id"] = Rid.Value(e.Id), ["category"] = e.Category.Name, ["built_in_category"] = CategoryKey.Of(e) };
            return new HeadroomRules.Hit(rc.Proximity, target ? HeadroomRules.HitKind.Target : HeadroomRules.HitKind.Other, key);
        }

        /// <summary>Target and source categories hidden in the view or its template, and enabled filters that hide; null when none.</summary>
        private static string HeadroomViewHides(Document doc, View view, IEnumerable<BuiltInCategory> targets, IEnumerable<Element> sources)
        {
            var views = new List<View> { view };
            try { if (doc.GetElement(view.ViewTemplateId) is View template) views.Add(template); } catch { }
            IEnumerable<BuiltInCategory> asked = targets;
            // Links matter only where there are links: their category hidden loses every linked surface.
            if (new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).GetElementCount() > 0)
                asked = asked.Concat(new[] { BuiltInCategory.OST_RvtLinks });
            var categories = new List<Category>();
            foreach (BuiltInCategory b in asked)
            {
                Category c = null;
                try { c = Category.GetCategory(doc, b); } catch { }
                if (c != null && !categories.Any(x => x.Id == c.Id)) categories.Add(c);
            }
            foreach (Element e in sources)
                if (e.Category != null && !categories.Any(x => x.Id == e.Category.Id)) categories.Add(e.Category);
            var hidden = new List<string>();
            foreach (Category c in categories)
                foreach (View v in views)
                {
                    bool off = false;
                    try { off = v.GetCategoryHidden(c.Id); } catch { }
                    if (off) { hidden.Add("category '" + c.Name + "'" + (v.Id == view.Id ? "" : " (in its template)")); break; }
                }
            foreach (View v in views)
            {
                ICollection<ElementId> filters = null;
                try { filters = v.GetFilters(); } catch { }
                foreach (ElementId filterId in filters ?? new List<ElementId>())
                {
                    bool hides = false;
                    try { hides = v.GetIsFilterEnabled(filterId) && !v.GetFilterVisibility(filterId); } catch { }
                    if (!hides) continue;
                    string filterName = null;
                    try { filterName = doc.GetElement(filterId)?.Name; } catch { }
                    hidden.Add("filter '" + (filterName ?? Rid.Value(filterId).ToString()) + "'" + (v.Id == view.Id ? "" : " (in its template)"));
                }
            }
            return hidden.Count == 0 ? null : string.Join(", ", hidden.Distinct());
        }

        private static string HeadroomUnfit(Element e)
        {
            if (e is ElementType || e is View) return "not a model element";
            if (e is RevitLinkInstance) return "a link instance: its elements are surfaces the rays reach, not elements measured here";
            if (e.Category == null || e.Category.CategoryType != CategoryType.Model)
                return "not a model element (" + (e.Category == null ? "no category" : e.Category.Name) + ")";
            return null;
        }

        private static JObject HeadroomSkip(Element e, string reason) =>
            new JObject { ["element_id"] = Rid.Value(e.Id), ["category"] = e.Category?.Name, ["built_in_category"] = CategoryKey.Of(e), ["reason"] = reason };

        private static double? HeadroomNumber(JObject o, string key)
        {
            JToken t = o[key];
            if (t == null || t.Type == JTokenType.Null) return null;
            try { return t.Value<double>(); } catch { return double.NaN; }
        }

        private static string HeadroomCategoryName(Document doc, BuiltInCategory b)
        {
            try { return Category.GetCategory(doc, b)?.Name ?? b.ToString(); } catch { return b.ToString(); }
        }

        private static double HeadroomMm(double feet) => Math.Round(feet * HeadroomMmPerFoot, 1);
    }
}
