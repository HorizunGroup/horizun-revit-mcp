using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    internal static partial class LeaderTargetReader
    {
        private sealed class PlanCandidate
        {
            public Element Element { get; }
            public RevitLinkInstance Link { get; }
            public Transform FromHost { get; }
            public string Proof { get; set; }
            public double Earliest { get; set; }
            public double ProbeEnd { get; set; }
            public double Distance { get; set; }
            public double? DoorStroke { get; set; }
            public string Limitation { get; set; }
            public string Description => "element " + Rid.Value(Element.Id) +
                (Link == null ? "" : " in link instance " + Rid.Value(Link.Id));

            public PlanCandidate(Element element, RevitLinkInstance link, Transform fromHost)
            {
                Element = element;
                Link = link;
                FromHost = fromHost;
            }

            public Options GeometryOptions(View view) => PlanGeometryOptions(view, Link != null);
        }

        private static Options PlanGeometryOptions(View view, bool linked) => !linked
            ? new Options { View = view }
            : new Options { DetailLevel = view.DetailLevel };

        private static IEnumerable<Curve> DoorStrokes(Element door, View view, GeometryElement geometry)
        {
            foreach (Curve curve in GeometryParts(geometry).OfType<Curve>())
            {
                if (!curve.IsBound || !(curve is Line || curve is Arc)) continue;
                Category subcategory = (door.Document.GetElement(curve.GraphicsStyleId) as GraphicsStyle)?.GraphicsStyleCategory;
                if (subcategory != null && subcategory.get_AllowsVisibilityControl(view) && view.GetCategoryHidden(subcategory.Id))
                    continue;
                if (curve is Arc arc && Math.Abs(arc.Normal.Z) < 1 - 1e-9) continue;
                yield return curve;
            }
        }

        private static double StrokeDistance(Curve stroke, XYZ origin)
        {
            XYZ a = stroke.GetEndPoint(0), b = stroke.GetEndPoint(1);
            if (stroke is Line)
                return TextNoteLayoutRules.PlanSegmentDistance(origin.X, origin.Y, a.X, a.Y, b.X, b.Y);
            var arc = (Arc)stroke;
            var onPlane = new XYZ(origin.X, origin.Y, arc.Center.Z);
            if (onPlane.DistanceTo(arc.Center) <= 1e-9) return arc.Radius;
            return arc.Distance(onPlane);
        }

        private static BoundingBoxIntersectsFilter AroundProbe(XYZ start, XYZ end)
        {
            var delta = new XYZ(CutProbeFeet, CutProbeFeet, CutProbeFeet);
            return new BoundingBoxIntersectsFilter(new Outline(
                new XYZ(Math.Min(start.X, end.X), Math.Min(start.Y, end.Y), Math.Min(start.Z, end.Z)) - delta,
                new XYZ(Math.Max(start.X, end.X), Math.Max(start.Y, end.Y), Math.Max(start.Z, end.Z)) + delta));
        }

        private static IEnumerable<PlanCandidate> PlanCandidates(Document doc, ViewPlan view, XYZ origin, XYZ direction,
            double depth, PlanLinkGraphics graphics)
        {
            using (var hosts = new FilteredElementCollector(doc, view.Id).WhereElementIsNotElementType())
            {
                // Doors can have plan-only graphics outside the vertical range of their model box.
                if (!double.IsPositiveInfinity(depth))
                    hosts.WherePasses(new LogicalOrFilter(AroundProbe(origin, origin + direction * depth),
                        new ElementCategoryFilter(BuiltInCategory.OST_Doors)));
                foreach (Element element in hosts)
                {
                    if (!IsModelTarget(element) || !HostElementIsDisplayed(view, element)) continue;
                    var candidate = new PlanCandidate(element, null, Transform.Identity);
                    if (ReadBounds(candidate, element.get_BoundingBox(view), origin, direction, depth))
                    {
                        if (IsDoor(element)) candidate.Earliest = 0;
                        yield return candidate;
                    }
                    else if (IsDoor(element) && DoorBoxMayOverlap(element, view, origin))
                    {
                        candidate.ProbeEnd = -1; // Only the plan stroke can reach this point.
                        yield return candidate;
                    }
                }
            }

            using (var links = new FilteredElementCollector(doc, view.Id).OfClass(typeof(RevitLinkInstance)))
                foreach (RevitLinkInstance link in links)
                {
                    if (!LinkIsDisplayed(view, link)) continue;
                    Document linkedDoc = link.GetLinkDocument();
                    Transform fromHost = link.GetTotalTransform().Inverse;
                    var bounds = new PlanCandidate(link, null, Transform.Identity);
                    bool solidPossible = ReadBounds(bounds, link.get_BoundingBox(null), origin, direction, depth);
                    var doors = ReadLinkedDoorStrokes(link, linkedDoc, origin, graphics, out string strokeProblem);
                    if (!solidPossible && doors.Count == 0 && strokeProblem == null) continue;
                    string limitation;
                    try { limitation = LinkedPlanLimitation(view, link); }
                    catch (Exception ex) { limitation = "link display settings could not be read: " + ex.Message; }
                    if (limitation != null)
                    {
                        bounds.Limitation = limitation;
                        if (doors.Count > 0 || strokeProblem != null) bounds.Earliest = 0;
                        yield return bounds;
                        continue;
                    }
                    if (strokeProblem != null)
                        yield return new PlanCandidate(link, null, Transform.Identity) { Limitation = strokeProblem };
                    // A drawn door symbol can reach the point even outside the solid probe and link box.
                    foreach (var door in doors.Values)
                        yield return new PlanCandidate(door.Element, link, fromHost) { DoorStroke = door.Distance };
                    if (!solidPossible) continue;
                    // Avoid the host/view/link collector on a plan that has never been drawn.
                    using (var elements = new FilteredElementCollector(linkedDoc).WhereElementIsNotElementType())
                    {
                        if (!double.IsPositiveInfinity(depth))
                            elements.WherePasses(AroundProbe(fromHost.OfPoint(origin), fromHost.OfPoint(origin + direction * depth)));
                        foreach (Element element in elements)
                        {
                            if (!IsModelTarget(element) && !(element is RevitLinkInstance)) continue;
                            if (doors.ContainsKey(element.Id)) continue;
                            var candidate = new PlanCandidate(element, link, fromHost);
                            if (ReadBounds(candidate, element.get_BoundingBox(null), origin, direction, depth))
                                yield return candidate;
                        }
                    }
                }
        }

        // A box may bound the first possible hit, but it cannot prove a hit or visibility.
        private static bool ReadBounds(PlanCandidate candidate, BoundingBoxXYZ box, XYZ origin, XYZ direction, double depth)
        {
            candidate.ProbeEnd = depth;
            if (box == null)
            {
                if (double.IsPositiveInfinity(depth)) candidate.Limitation = "no bounds to measure the candidate in an unlimited view range";
                return true;
            }
            Transform local = box.Transform.Inverse.Multiply(candidate.FromHost);
            XYZ o = local.OfPoint(origin), d = local.OfVector(direction);
            var min = new[] { box.Min.X - CutProbeFeet, box.Min.Y - CutProbeFeet, box.Min.Z - CutProbeFeet };
            var max = new[] { box.Max.X + CutProbeFeet, box.Max.Y + CutProbeFeet, box.Max.Z + CutProbeFeet };
            var interval = TextNoteLayoutRules.RayBoxInterval(new[] { o.X, o.Y, o.Z }, new[] { d.X, d.Y, d.Z }, min, max);
            if (!interval.HasValue || interval.Value.Entry > depth || interval.Value.Exit < 0) return false;
            candidate.Earliest = Math.Max(0, interval.Value.Entry);
            candidate.ProbeEnd = Math.Min(depth, interval.Value.Exit);
            return true;
        }

        private static bool DoorBoxMayOverlap(Element door, View view, XYZ origin)
        {
            BoundingBoxXYZ box = door.get_BoundingBox(view);
            if (box == null || !box.Transform.IsIdentity) return true;
            return origin.X >= box.Min.X - CutProbeFeet && origin.X <= box.Max.X + CutProbeFeet &&
                   origin.Y >= box.Min.Y - CutProbeFeet && origin.Y <= box.Max.Y + CutProbeFeet;
        }

        private static bool HostElementIsDisplayed(View view, Element e) =>
            !e.IsHidden(view) && !view.GetCategoryHidden(e.Category.Id) &&
            (!view.IsInTemporaryViewMode(TemporaryViewMode.TemporaryHideIsolate) ||
                view.IsElementVisibleInTemporaryViewMode(TemporaryViewMode.TemporaryHideIsolate, e.Id));

        private static bool LinkIsDisplayed(View view, RevitLinkInstance link) =>
            link.GetLinkDocument() != null && HostElementIsDisplayed(view, link);

        private static string LinkedPlanLimitation(View view, RevitLinkInstance link)
        {
#if REVIT2023
            return "Revit 2023 cannot read the plan display settings of link instance " + Rid.Value(link.Id);
#else
            var settings = view.GetLinkOverrides(link.Id) ?? view.GetLinkOverrides(link.GetTypeId());
            return settings == null || settings.LinkVisibilityType == LinkVisibility.ByHostView
                ? null : "plan geometry for link instance " + Rid.Value(link.Id) +
                    " uses " + settings.LinkVisibilityType + " display settings and could not be measured";
#endif
        }

        private static bool InsidePlanCrop(ViewPlan view, XYZ point)
        {
            if (!view.CropBoxActive) return true;
            var regions = view.GetModelToProjectionTransforms();
            if (regions.Count != 1)
                throw new InvalidOperationException("the offsets of the split model crop have not been measured");
            using (var region = regions[0])
            using (CurveLoop boundary = region.GetBoundary())
            {
                if (boundary == null) throw new InvalidOperationException("the active model crop has no readable boundary");
                Transform projection = region.GetModelToProjectionTransform();
                return PlanLeaderRules.InsideCrop(boundary.Select(curve => curve.GetEndPoint(0)),
                    point, model =>
                    {
                        XYZ projected = projection.OfPoint(model);
                        return new CadPoint(projected.X, projected.Y, 0);
                    });
            }
        }

        private static double PlanDepth(ViewPlan view, XYZ origin)
        {
            using (PlanViewRange range = view.GetViewRange())
            {
                ElementId id = range.GetLevelId(PlanViewPlane.ViewDepthPlane);
                if (id == PlanViewRange.Unlimited) return double.PositiveInfinity;
                Level level = id == PlanViewRange.Current ? view.GenLevel :
                    id == null || id == ElementId.InvalidElementId ? null : view.Document.GetElement(id) as Level;
                if (level == null) throw new InvalidOperationException("the plan view depth could not be read");
                double depth = level.ProjectElevation + range.GetOffset(PlanViewPlane.ViewDepthPlane);
                return Math.Max(0, (origin.Z - depth) * view.ViewDirection.Z);
            }
        }
        private static LeaderHit ReadPlanTarget(Document doc, ViewPlan view, XYZ end, PlanLinkGraphics graphics)
        {
            try
            {
                if (!InsidePlanCrop(view, end))
                    return LeaderHit.Partial("the leader end is outside the model crop of view " + Rid.Value(view.Id), false, null);
                XYZ direction = view.ViewDirection.Negate();
                XYZ origin = null;
                LeaderHit hit = ReadPlanEvidence<PlanCandidate>(Rid.Value(view.Id), graphics.PlanRegionId(), () =>
                {
                    if (!TryCutPlanePoint(doc, view, end, out origin))
                        throw new InvalidOperationException("the view's cut plane could not be read");
                    double depth = PlanDepth(view, origin);
                    var candidates = PlanCandidates(doc, view, origin, direction, depth, graphics).ToList();
                    return (candidates, () => candidates.Select(c => c.Proof).FirstOrDefault(p => p != null) ??
                        FormattableString.Invariant($"no plan target along the probe from the cut plane through {depth * 304.8:0.##} mm of view depth"));
                }, c => c.Earliest, c => MeasurePlanCandidate(c, view, origin, direction, graphics), c => c.Description,
                    c => PlanHit(c, origin + direction * c.Distance));
                return PlanLeaderRules.WithLinkedStrokeFallback(hit, hit.Coverage == "complete", hit.Source != null,
                    () => ReadLinkedStrokeTarget(doc, view, end, graphics, hit));
            }
            catch (Exception ex)
            {
                return LeaderHit.Partial("geometry in the annotation plan could not be checked: " + ex.Message, true, null);
            }
        }

        private static (double? Distance, bool AtCut, string Problem) MeasurePlanCandidate(PlanCandidate candidate,
            ViewPlan view, XYZ origin, XYZ direction, PlanLinkGraphics graphics)
        {
            (double?, bool, string) Unknown(string reason, double? distance = null) => (distance, false,
                FormattableString.Invariant($"{candidate.Description} could not be verified (possible at {(distance ?? candidate.Earliest) * 304.8:0.###} mm along the probe): {reason}"));
            try
            {
                if (candidate.Limitation != null) return Unknown(candidate.Limitation);
                if (candidate.DoorStroke.HasValue)
                {
                    candidate.Proof = FormattableString.Invariant($"drawn linked door segment ({candidate.DoorStroke.Value * 304.8:0.###} mm from the end)");
                    return (0, true, null);
                }
                Element element = candidate.Element;
                if (element is RevitLinkInstance) return Unknown("nested plan geometry is unavailable");
                GeometryElement geometry = element.get_Geometry(candidate.GeometryOptions(view));
                if (geometry == null) return Unknown("no geometry was returned");
                if (candidate.Link == null && IsDoor(element))
                {
                    double distance = DoorStrokes(element, view, geometry).Select(c => StrokeDistance(c, origin))
                        .DefaultIfEmpty(double.PositiveInfinity).Min();
                    if (distance <= TextNoteLayoutRules.PlanStrokeToleranceFeet)
                    {
                        candidate.Proof = FormattableString.Invariant($"door plan stroke ({distance * 304.8:0.###} mm from the end)");
                        return (0, true, null);
                    }
                }
                bool atCut = false;
                if (candidate.Earliest == 0 && candidate.ProbeEnd >= 0)
                {
                    bool? inside = SolidContains(geometry, candidate.FromHost, origin);
                    if (!inside.HasValue) return Unknown("solid intersection at the cut plane could not be read");
                    atCut = inside.Value;
                }
                double? hit = atCut ? 0 : FirstSolidEntry(geometry, candidate, origin, direction, view.Document.Application.ShortCurveTolerance);
                if (!hit.HasValue)
                {
                    candidate.Proof = FormattableString.Invariant($"{candidate.Description}: no solid intersects the probe within {candidate.ProbeEnd * 304.8:0.###} mm of the cut plane");
                    return (null, false, null);
                }
                candidate.Distance = hit.Value;
                candidate.Proof = atCut ? "solid contains the cut-plane point" : "first solid entry measured in the annotation plan";
                if (candidate.Link != null)
                {
                    double stroke;
                    try { stroke = graphics.Distance(candidate.Link, element, origin); }
                    catch (Exception ex) { return Unknown(ex.Message, hit); }
                    if (stroke > TextNoteLayoutRules.PlanStrokeToleranceFeet)
                        return Unknown(double.IsPositiveInfinity(stroke)
                            ? "the element emitted no attributable 2D segment"
                            : FormattableString.Invariant($"nearest drawn segment is {stroke * 304.8:0.###} mm from the end (limit 0.1 mm)"), hit);
                    candidate.Proof += FormattableString.Invariant($"; drawn linked segment is {stroke * 304.8:0.###} mm from the end");
                }
                return (hit, atCut, null);
            }
            catch (Exception ex) { return Unknown(ex.Message); }
        }

        private static double? FirstSolidEntry(GeometryElement geometry, PlanCandidate candidate, XYZ origin,
            XYZ direction, double shortCurveTolerance)
        {
            if (candidate.ProbeEnd < 0) return null;
            double pad = Math.Max(shortCurveTolerance, CutProbeFeet);
            var ends = TextNoteLayoutRules.ProbeInElementSpace(origin - direction * pad,
                origin + direction * (candidate.ProbeEnd + pad), candidate.FromHost.OfPoint);
            XYZ start = candidate.FromHost.OfPoint(origin), forward = candidate.FromHost.OfVector(direction);
            Line probe = Line.CreateBound(ends.Start, ends.End);
            double nearest = double.PositiveInfinity;
            using (var options = new SolidCurveIntersectionOptions { ResultType = SolidCurveIntersectionMode.CurveSegmentsInside })
            {
                foreach (GeometryObject part in GeometryParts(geometry))
                {
                    if (part is Mesh)
                        throw new InvalidOperationException("the candidate contains a mesh whose first impact was not measured");
                    if (!(part is Solid solid) || solid.Faces.Size == 0 || solid.Volume <= 0) continue;
                    using (SolidCurveIntersection hit = solid.IntersectWithCurve(probe, options))
                    {
                        if (hit == null) throw new InvalidOperationException("no solid intersection result was returned");
                        for (int i = 0; i < hit.SegmentCount; i++)
                        {
                            Curve segment = hit.GetCurveSegment(i);
                            double a = (segment.GetEndPoint(0) - start).DotProduct(forward);
                            double b = (segment.GetEndPoint(1) - start).DotProduct(forward);
                            double? entry = PlanLeaderRules.EntryInRange(a, b, candidate.ProbeEnd);
                            if (entry.HasValue) nearest = Math.Min(nearest, entry.Value);
                        }
                    }
                }
            }
            return double.IsPositiveInfinity(nearest) ? (double?)null : nearest;
        }
    }
}
