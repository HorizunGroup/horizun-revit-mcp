using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    internal static partial class LeaderTargetReader
    {
        private static LeaderHit ReadPlanEvidence<T>(long viewId, long? regionId,
            Func<(IEnumerable<T> Candidates, Func<string> Miss)> probe, Func<T, double> earliest,
            Func<T, (double? Distance, bool AtCut, string Problem)> measure,
            Func<T, string> describe, Func<T, LeaderHit> evidence) where T : class
        {
            if (regionId.HasValue)
                return LeaderHit.Partial($"plan region {regionId.Value} in view {viewId}: Revit does not expose the region boundary and local view range for verification", false, null);

            var input = probe();
            var result = PlanLeaderRules.FirstTarget(input.Candidates, earliest, measure, describe);
            if (result.Problem != null)
                return LeaderHit.Partial(result.Problem, true, result.Observed == null ? null : evidence(result.Observed));
            if (result.Target != null) return evidence(result.Target);
            return new LeaderHit { Cast = true, Coverage = "complete", Uncertainty = input.Miss() };
        }

        private static bool IsModelTarget(Element e) => e != null && !(e is SpatialElement || e is DatumPlane ||
            e is View || e is RevitLinkInstance) && e.Category?.CategoryType == CategoryType.Model &&
            e.Category.Id != new ElementId(BuiltInCategory.OST_Mass);

        private static LeaderHit ReadLinkedStrokeTarget(Document doc, ViewPlan view, XYZ end,
            PlanLinkGraphics graphics, LeaderHit hit)
        {
            using (var links = new FilteredElementCollector(doc, view.Id).OfClass(typeof(RevitLinkInstance)))
                if (!links.Cast<RevitLinkInstance>().Any(link => LinkIsDisplayed(view, link))) return hit;

            // An arrow on the projected edge/centerline can miss every solid: e.g.
            // line-only display or a stroke outside the geometry probe's depth.
            // Use only an unambiguous segment from the annotation view's own output.
            var stroke = graphics.UniqueLinkedStroke(end, out bool ambiguous);
            if (ambiguous) return LeaderHit.Partial("multiple or unattributed drawn segments meet the leader end", true, null);
            if (!stroke.HasValue) return hit;
            var link = doc.GetElement(Rid.Make(stroke.Value.InstanceId)) as RevitLinkInstance;
            Element linked = stroke.Value.Document.GetElement(Rid.Make(stroke.Value.ElementId));
            if (link == null || !LinkIsDisplayed(view, link) || !OpenGuard.SameDocument(link.GetLinkDocument(), stroke.Value.Document) ||
                !IsModelTarget(linked)) return hit;
            return PlanHitIdentity(linked, link, "unique drawn linked segment at the leader end; no solid depth measured",
                0, depthMeasured: false, out _);
        }

        private static LeaderHit PlanHitIdentity(Element el, RevitLinkInstance link, string proof,
            double distance, bool depthMeasured, out ElementType type)
        {
            Document elementDocument = el.Document;
            var hit = new LeaderHit { Cast = true, Coverage = "complete", Source = link == null ? "host" : "link", Element = el, ElementDocument = elementDocument };
            if (link != null)
            {
                hit.LinkInstanceId = Rid.Value(link.Id);
                hit.LinkInstanceUniqueId = link.UniqueId;
                hit.LinkPath = new[] { hit.LinkInstanceUniqueId };
                hit.LinkDepth = 1;
            }
            hit.ElementId = Rid.Value(el.Id);
            try { hit.UniqueId = el.UniqueId; } catch { }
            hit.Category = el.Category?.Name;
            type = elementDocument.GetElement(el.GetTypeId()) as ElementType;
            hit.TypeName = type == null ? null : (type.FamilyName + " : " + type.Name);
            try { if (elementDocument.IsWorkshared) hit.LastChangedBy = WorksharingUtils.GetWorksharingTooltipInfo(elementDocument, el.Id)?.LastChangedBy; } catch { }
            hit.DepthMeasured = depthMeasured;
            hit.DistanceFeet = distance;
            hit.Note = proof;
            return hit;
        }

        private static bool IsDoor(Element element) => element is FamilyInstance &&
            element.Category?.Id is ElementId category && Rid.Value(category) == (long)BuiltInCategory.OST_Doors;

        private static Dictionary<ElementId, (Element Element, double Distance)> ReadLinkedDoorStrokes(
            RevitLinkInstance link, Document linkedDoc, XYZ origin, PlanLinkGraphics graphics, out string problem)
        {
            problem = null;
            var doors = new Dictionary<ElementId, (Element, double)>();
            try
            {
                foreach (var stroke in graphics.NearbyElements(link, origin))
                {
                    Element door = linkedDoc.GetElement(Rid.Make(stroke.ElementId));
                    if (IsDoor(door)) doors.Add(door.Id, (door, stroke.Distance));
                }
            }
            catch (Exception ex)
            {
                // A drawn door symbol can extend beyond its model box and reach the probe at distance zero.
                using (var candidates = new FilteredElementCollector(linkedDoc).OfCategory(BuiltInCategory.OST_Doors).WhereElementIsNotElementType())
                    if (candidates.Any())
                        problem = "nearby linked door strokes could not be verified: " + ex.Message;
                doors.Clear();
            }
            return doors;
        }

        internal static View3D RayViewFor(View view, View3D fallback)
            => view is View3D own && !own.IsTemplate && !own.IsPerspective ? own : fallback;

        /// <summary>Resolve a leader from its point alone. The ray can miss an element containing its origin at a plan's cut plane.</summary>
        public static LeaderHit CastForLeader(Document doc, View view, View3D v3, XYZ end, PlanLinkGraphics graphics = null)
        {
            LeaderHit hit;
            if (view is ViewPlan plan)
            {
                graphics = graphics ?? new PlanLinkGraphics(plan);
                hit = ReadPlanTarget(doc, plan, end, graphics);
            }
            else hit = CastRay(doc, view, v3, Origin(doc, view, end, backOff3D: true));
            return hit.AmbiguousRay ? LeaderHit.Partial(hit.Uncertainty, true, hit) : hit;
        }

    }
}
