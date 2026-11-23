using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Commands
{
    public sealed partial class TransformElementsCommand
    {
        private static string[][] JoinPeers(Wall wall) => Enumerable.Range(0, 2).Select(end =>
            ((LocationCurve)wall.Location).get_ElementsAtJoin(end).Cast<Element>().Select(e => e.UniqueId).OrderBy(s => s, StringComparer.Ordinal).ToArray()).ToArray();

        private static void CaptureJoinNeighbors(Document doc, Plan p)
        {
            foreach (ElementId id in p.Ids)
            {
                var wall = (Wall)doc.GetElement(id);
                p.JoinPeers[Rid.Value(id)] = JoinPeers(wall);
                foreach (string uid in p.JoinPeers[Rid.Value(id)].SelectMany(a => a)) AddJoinNeighbor(doc, p, doc.GetElement(uid));
                foreach (int end in new[] { 0, 1 })
                {
                    XYZ point = p.Curve.GetEndPoint(end), radius = new XYZ(Math.Max(1, wall.Width * 2), Math.Max(1, wall.Width * 2), 1);
                    foreach (Wall candidate in new FilteredElementCollector(doc).OfClass(typeof(Wall))
                        .WherePasses(new BoundingBoxIntersectsFilter(new Outline(point - radius, point + radius))).Cast<Wall>())
                        if (candidate.Id != wall.Id) AddJoinNeighbor(doc, p, candidate);
                }
            }
        }

        private static void AddJoinNeighbor(Document doc, Plan p, Element neighbor)
        {
            NeighborEditGuard.RequireOwned(doc, neighbor);
            if (!p.JoinNeighbors.ContainsKey(neighbor.UniqueId))
                p.JoinNeighbors[neighbor.UniqueId] = ModelEditRunner.Planned(neighbor, PlannedAction.Modify, new JObject { ["operation"] = "set_curve_join_neighbor" });
        }

        private static bool JoinsRestored(Document doc, Plan p, Wall wall, JObject detail)
        {
            long id = Rid.Value(wall.Id);
            bool permissions = p.EndJoins[id].Select((allowed, end) => WallUtils.IsWallJoinAllowedAtEnd(wall, end) == allowed).All(x => x);
            string[][] actual = JoinPeers(wall);
            bool topology = JToken.DeepEquals(JToken.FromObject(p.JoinPeers[id]), JToken.FromObject(actual));
            var neighbors = new JArray();
            foreach (var item in p.JoinNeighbors)
            {
                Element neighbor = doc.GetElement(item.Key);
                bool identity = neighbor != null && neighbor.UniqueId == item.Value.UniqueId;
                if (!identity) { topology = false; continue; }
                NeighborEditGuard.RequireOwned(doc, neighbor);
                var row = new JObject { ["unique_id"] = neighbor.UniqueId, ["element_id"] = Rid.Value(neighbor.Id),
                    ["geometry_after"] = SafePlanGeometry(neighbor), ["geometry_before"] = item.Value.GeometryFingerprint };
                if (neighbor.Location is LocationCurve curve)
                    row["curve_after_feet"] = new JArray(Arr(curve.Curve.GetEndPoint(0)), Arr(curve.Curve.GetEndPoint(1)));
                if (neighbor is Wall neighborWall) row["joined_neighbors_after"] = JToken.FromObject(JoinPeers(neighborWall));
                neighbors.Add(row);
            }
            detail["join_neighbors"] = neighbors;
            detail["joined_neighbors_before"] = JToken.FromObject(p.JoinPeers[id]);
            detail["joined_neighbors_after"] = JToken.FromObject(actual);
            return permissions && topology;
        }
    }
}
