using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public sealed partial class ManageLinksCommand
    {
        private static CommandResult VisibleElements(UIApplication app, JObject request)
        {
            Document host = app.ActiveUIDocument?.Document;
            if (host == null) return CommandResult.Fail("No active document.");
            CommandResult wrong = DocumentGate.ReadGuard(host, request, "horizun_manage_links");
            if (wrong != null) return wrong;
            long linkRaw = request.Value<long?>("link_instance_id") ?? -1;
            long viewRaw = request.Value<long?>("view_id") ?? -1;
            if (!Rid.CanRepresent(linkRaw) || !Rid.CanRepresent(viewRaw))
                return CommandResult.Fail("visible_elements needs link_instance_id and view_id from the active host document.");
            var link = host.GetElement(Rid.Make(linkRaw)) as RevitLinkInstance;
            var view = host.GetElement(Rid.Make(viewRaw)) as ViewPlan;
            if (link == null || view == null || view.IsTemplate)
                return CommandResult.Fail("The ids must name a loaded RVT link instance and a non-template host plan view.");
            Document linked = null;
            try { linked = link.GetLinkDocument(); } catch { }
            if (linked == null)
                return CommandResult.FailWithDetail("The RVT link is unloaded; no linked elements were inspected.",
                    new JObject { ["code"] = "link_unloaded", ["link_instance_id"] = linkRaw });
            int limit = request.Value<int?>("max_rows") ?? 200;
            if (limit < 1 || limit > 1000) return CommandResult.Fail("max_rows must be 1..1000.");

            double bottom, top;
            string rangeError = ViewZRange(host, view, out bottom, out top);
            if (rangeError != null) return CommandResult.Fail(rangeError);
            bool linkOpen = !host.IsWorkshared || host.GetWorksetTable().GetWorkset(link.WorksetId).IsOpen;
            bool linkHidden = false;
            try { linkHidden = link.IsHidden(view) || view.GetCategoryHidden(link.Category.Id); }
            catch { return CommandResult.Fail("Host view/link visibility could not be read; no visibility claim is made."); }
            var rows = new JArray();
            int scanned = 0, candidates = 0, excluded = 0, uncertain = 0;
            var reasons = new Dictionary<string, int>(StringComparer.Ordinal);
            Transform transform = link.GetTotalTransform();
            ElementId[] filters = view.GetFilters().ToArray();
            var hiddenFilters = new List<ElementFilter>();
            foreach (ElementId filterId in filters)
            {
                try
                {
                    if (view.GetFilterVisibility(filterId)) continue;
                    var parameterFilter = host.GetElement(filterId) as ParameterFilterElement;
                    if (parameterFilter == null) { uncertain++; continue; }
                    hiddenFilters.Add(parameterFilter.GetElementFilter());
                }
                catch { uncertain++; }
            }
            int closedLinkedWorksets = linked.IsWorkshared
                ? new FilteredWorksetCollector(linked).OfKind(WorksetKind.UserWorkset)
                    .Count(w => !w.IsOpen) : 0;

            // Collect the LINK DOCUMENT itself. Never use Revit's
            // FilteredElementCollector(host, viewId, linkId) overload.
            foreach (Element element in new FilteredElementCollector(linked).WhereElementIsNotElementType())
            {
                scanned++;
                string reason = null;
                if (!linkOpen || linkHidden) reason = !linkOpen ? "instance_workset_closed" : "link_hidden_in_view";
                else if (linked.IsWorkshared && !linked.GetWorksetTable().GetWorkset(element.WorksetId).IsOpen)
                    reason = "linked_workset_closed";
                else if (element.Category == null) reason = "no_category";
                else
                {
                    try { if (view.GetCategoryHidden(element.Category.Id)) reason = "category_hidden"; }
                    catch { reason = "category_visibility_unreadable"; }
                    if (reason == null)
                    {
                        BoundingBoxXYZ box = null;
                        try { box = element.get_BoundingBox(null); } catch { }
                        if (box == null) reason = "bounding_box_unreadable";
                        else
                        {
                            double minZ = double.PositiveInfinity, maxZ = double.NegativeInfinity;
                            foreach (double x in new[] { box.Min.X, box.Max.X })
                            foreach (double y in new[] { box.Min.Y, box.Max.Y })
                            foreach (double z in new[] { box.Min.Z, box.Max.Z })
                            {
                                double hostZ = transform.OfPoint(new XYZ(x, y, z)).Z;
                                minZ = Math.Min(minZ, hostZ); maxZ = Math.Max(maxZ, hostZ);
                            }
                            if (!LinkViewVisibilityRules.OverlapsZ(minZ, maxZ, bottom, top)) reason = "outside_view_z";
                        }
                    }
                    if (reason == null)
                        foreach (ElementFilter filter in hiddenFilters)
                            try { if (filter.PassesFilter(linked, element.Id)) { reason = "hidden_by_view_filter"; break; } }
                            catch { reason = "filter_unreadable"; break; }
                }
                if (reason == null)
                {
                    candidates++;
                    if (rows.Count < limit) rows.Add(new JObject { ["element_id"] = Rid.Value(element.Id),
                        ["category"] = element.Category?.Name, ["name"] = element.Name,
                        ["result"] = "passes_measured_gates" });
                }
                else
                {
                    if (reason.EndsWith("unreadable", StringComparison.Ordinal) || reason == "no_category") uncertain++;
                    else excluded++;
                    if (!reasons.ContainsKey(reason)) reasons[reason] = 0;
                    reasons[reason]++;
                }
            }
            var byReason = new JObject();
            foreach (var pair in reasons.OrderBy(x => x.Key, StringComparer.Ordinal)) byReason[pair.Key] = pair.Value;
            return CommandResult.Ok(new JObject { ["operation"] = "visible_elements",
                ["view_id"] = viewRaw, ["link_instance_id"] = linkRaw,
                ["scanned"] = scanned, ["passes_measured_gates"] = candidates,
                ["excluded"] = excluded, ["unreadable_or_uncertain"] = uncertain,
                ["reasons"] = byReason, ["rows"] = rows, ["rows_truncated"] = candidates > rows.Count,
                ["closed_linked_worksets"] = closedLinkedWorksets,
                ["view_z_feet"] = new JArray(bottom, top), ["hidden_filters_evaluated"] = hiddenFilters.Count,
                ["visibility_proven"] = false,
                ["remaining_limits"] = "Crop XY, phases, design options, per-link display settings and Revit's final rendering are not proven by this independent read. Passes measured gates means candidate, not confirmed visible.",
                ["collector_overload_used"] = "linked_document_model_only" });
        }

        private static string ViewZRange(Document host, ViewPlan view, out double bottom, out double top)
        {
            bottom = top = 0;
            try
            {
                PlanViewRange range = view.GetViewRange();
                bottom = PlaneZ(host, view, range, PlanViewPlane.BottomClipPlane);
                top = PlaneZ(host, view, range, PlanViewPlane.TopClipPlane);
                return bottom <= top ? null : "The host plan view has an inverted Z range; no visibility claim is made.";
            }
            catch (Exception ex) { return "The host plan view range cannot be measured: " + ex.Message; }
        }

        private static double PlaneZ(Document doc, ViewPlan view, PlanViewRange range, PlanViewPlane plane)
        {
            ElementId id = range.GetLevelId(plane);
            var level = doc.GetElement(id) as Level ?? view.GenLevel;
            if (level == null) throw new InvalidOperationException("View range level unavailable.");
            return level.Elevation + range.GetOffset(plane);
        }
    }
}
