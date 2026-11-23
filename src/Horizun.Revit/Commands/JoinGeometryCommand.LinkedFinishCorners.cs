using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Commands
{
    public sealed partial class JoinGeometryCommand
    {
        private const double FinishContactFeet = 1.0 / 304.8;

        private sealed class LinkedCornerCore
        {
            public long LinkId, WallId;
            public Transform ToHost;
            public Wall Wall;
            public double[] Box;
            public List<Solid> Solids;
        }

        // A box is only the broad phase. Exemption requires nine points of the finish end
        // to project inside ONE bounded vertical core face, within 1 mm. Parallel side
        // faces cannot justify an endpoint termination. Holes/doors remain holes.
        private static JObject ReadLinkedFinishTerminations(Document doc, IList<WallSegment> walls,
            IDictionary<long, Wall> byId, IList<OpenCorner> originalCorners)
        {
            var contacts = new JArray();
            var unreadable = new JArray();
            var eligible = walls.Where(w => IsThinFinish(byId[w.Id])).ToList();
            var cores = new List<LinkedCornerCore>();
            int loaded = 0, endsRead = 0;
            foreach (RevitLinkInstance link in new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)))
            {
                long linkId = Rid.Value(link.Id);
                Document linked = link.GetLinkDocument();
                if (linked == null) { unreadable.Add(new JObject { ["link_instance_id"] = linkId, ["reason"] = "link unloaded" }); continue; }
                loaded++;
                Transform transform = link.GetTotalTransform();
                foreach (Wall core in new FilteredElementCollector(linked).OfClass(typeof(Wall)).Cast<Wall>())
                {
                    if (!IsCornerCore(core)) continue;
                    try
                    {
                        BoundingBoxXYZ box = core.get_BoundingBox(null);
                        if (box == null) throw new InvalidOperationException("core has no readable bounding box");
                        var points = new List<XYZ>();
                        for (int i = 0; i < 8; i++)
                            points.Add(transform.OfPoint(box.Transform.OfPoint(new XYZ((i & 1) == 0 ? box.Min.X : box.Max.X,
                                (i & 2) == 0 ? box.Min.Y : box.Max.Y, (i & 4) == 0 ? box.Min.Z : box.Max.Z))));
                        cores.Add(new LinkedCornerCore { LinkId = linkId, WallId = Rid.Value(core.Id), Wall = core, ToHost = transform,
                            Box = new[] { points.Min(p => p.X), points.Min(p => p.Y), points.Min(p => p.Z),
                                points.Max(p => p.X), points.Max(p => p.Y), points.Max(p => p.Z) } });
                    }
                    catch (Exception ex) { unreadable.Add(new JObject { ["link_instance_id"] = linkId, ["core_wall_id"] = Rid.Value(core.Id), ["reason"] = ex.Message }); }
                }
            }
            foreach (WallSegment segment in eligible)
            {
                Wall finish = byId[segment.Id];
                XYZ along = new XYZ(segment.X1 - segment.X0, segment.Y1 - segment.Y0, 0).Normalize();
                XYZ across = new XYZ(-along.Y, along.X, 0);
                for (int end = 0; end < 2; end++)
                {
                    double x = end == 0 ? segment.X0 : segment.X1, y = end == 0 ? segment.Y0 : segment.Y1;
                    bool readable = true;
                    foreach (LinkedCornerCore core in cores)
                    {
                        double[] box = core.Box;
                        if (x < box[0] - FinishContactFeet || x > box[3] + FinishContactFeet ||
                            y < box[1] - FinishContactFeet || y > box[4] + FinishContactFeet ||
                            segment.ZMin >= box[5] || segment.ZMax <= box[2]) continue;
                        try
                        {
                            if (core.Solids == null) core.Solids = MeasuredSolids(core.Wall);
                            Transform inverse = core.ToHost.Inverse;
                            bool supported = false;
                            double largestGap = 0;
                            foreach (Solid solid in core.Solids)
                            {
                                foreach (Face face in solid.Faces)
                                {
                                    if (!(face is PlanarFace plane)) continue;
                                    XYZ normal = core.ToHost.OfVector(plane.FaceNormal).Normalize();
                                    if (Math.Abs(normal.Z) > 0.001 || Math.Abs(normal.DotProduct(along)) < 0.99) continue;
                                    bool inside = true;
                                    double faceGap = 0;
                                    // Inset 0.1 mm from each cap edge, allowing exact edge contact while
                                    // preventing a thin sliver or a bounding-box overlap from clearing it.
                                    double half = Math.Max(0, finish.Width / 2 - 0.1 / 304.8);
                                    foreach (double fraction in new[] { 0.1, 0.5, 0.9 })
                                        foreach (double lateral in new[] { -half, 0.0, half })
                                        {
                                            XYZ point = new XYZ(x, y, segment.ZMin + (segment.ZMax - segment.ZMin) * fraction) + across * lateral;
                                            IntersectionResult hit = plane.Project(inverse.OfPoint(point));
                                            if (hit == null || !plane.IsInside(hit.UVPoint) || hit.Distance > FinishContactFeet) { inside = false; break; }
                                            faceGap = Math.Max(faceGap, hit.Distance);
                                        }
                                    if (!inside) continue;
                                    supported = true; largestGap = faceGap; break;
                                }
                                if (supported) break;
                            }
                            if (!supported) continue;
                            if (end == 0) segment.LinkedCoreAtStart = true; else segment.LinkedCoreAtEnd = true;
                            contacts.Add(new JObject { ["wall_id"] = segment.Id, ["end"] = end == 0 ? "start" : "end",
                                ["link_instance_id"] = core.LinkId, ["core_wall_id"] = core.WallId,
                                ["state"] = "terminates_on_linked_core", ["contact_gap_mm"] = Math.Round(largestGap * 304.8, 6),
                                ["sample_points"] = 9, ["was_open_corner_candidate"] = originalCorners.Any(c => c.WallId == segment.Id && c.End == end) });
                            break;
                        }
                        catch (Exception ex)
                        {
                            readable = false;
                            unreadable.Add(new JObject { ["wall_id"] = segment.Id, ["end"] = end,
                                ["link_instance_id"] = core.LinkId, ["core_wall_id"] = core.WallId, ["reason"] = ex.Message });
                        }
                    }
                    if (readable || segment.LinkedCoreAt(end)) endsRead++;
                }
            }
            return new JObject { ["method"] = "nine finish-cap samples projected inside one bounded vertical linked core face; transformed to link coordinates",
                ["contact_tolerance_mm"] = 1, ["max_finish_width_mm"] = 100,
                ["loaded_links_read"] = loaded, ["core_walls_read"] = cores.Count,
                ["eligible_finish_walls"] = eligible.Count, ["endpoints_requested"] = eligible.Count * 2, ["endpoints_read"] = endsRead,
                ["coverage_complete"] = unreadable.Count == 0, ["unreadable"] = unreadable,
                ["excluded_open_corner_candidates"] = contacts.Count(c => c.Value<bool>("was_open_corner_candidate")), ["contacts"] = contacts,
                ["scope"] = "Basic walls up to 100 mm with one positive-width layer or only Finish1/Finish2 positive-width layers. Layer function alone is not reliable for template finish types. Nine samples prove local cap contact, not watertight full-face continuity or coordination of the entire model." };
        }

        private static bool IsThinFinish(Wall wall)
        {
            if (wall.WallType.Kind != WallKind.Basic || wall.Width <= 0 || wall.Width > 100.0 / 304.8) return false;
            try
            {
                var layers = wall.WallType.GetCompoundStructure()?.GetLayers().Where(layer => layer.Width > 0).ToList();
                return layers != null && WallCornerRules.ThinFinishCandidate(wall.Width, layers.Count,
                    layers.All(layer => layer.Function == MaterialFunctionAssignment.Finish1 || layer.Function == MaterialFunctionAssignment.Finish2));
            }
            catch { return false; }
        }

        private static bool IsCornerCore(Wall wall)
        {
            try
            {
                return wall.get_Parameter(BuiltInParameter.WALL_STRUCTURAL_SIGNIFICANT)?.AsInteger() == 1 ||
                    wall.WallType.GetCompoundStructure()?.GetLayers().Any(layer => layer.Width > 0 && layer.Function == MaterialFunctionAssignment.Structure) == true;
            }
            catch { return false; }
        }
    }
}
