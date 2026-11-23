using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Commands
{
    public sealed partial class QueryModelCommand
    {
        private static IEnumerable<RevitLinkInstance> QueryLinks(Document host) =>
            new FilteredElementCollector(host).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>();

        internal static IEnumerable<QueryDocument> ReadLinkedSources(Document host, QuerySourceFilter filter,
            Action<JObject> unreadable)
        {
            foreach (RevitLinkInstance link in QueryLinks(host))
            {
                long id = Rid.Value(link.Id);
                Document linked;
                try { linked = link.GetLinkDocument(); }
                catch (Exception ex)
                {
                    unreadable(LinkError(link.Name, id, "link document could not be read: " + ex.Message));
                    continue;
                }
                if (linked == null) continue; // FederatedVisibility reports unloaded links.
                if (!filter.Admits(new QuerySourceFilter.Source { Kind = "link", Title = linked.Title, LinkInstanceId = id }))
                    continue;

                Transform toHost;
                try
                {
                    toHost = link.GetTotalTransform();
                    if (toHost == null) throw new InvalidOperationException("Revit returned no link transform.");
                }
                catch (Exception ex)
                {
                    unreadable(LinkError(linked.Title, id, "link transform could not be read: " + ex.Message));
                    continue;
                }
                yield return new QueryDocument(linked, toHost, null, id);
            }
        }

        private static JObject LinkError(string name, long id, string reason) => new JObject
        {
            ["source_model"] = name, ["link_instance_id"] = id, ["reason"] = reason
        };

        internal sealed class QueryDocument
        {
            internal Document Document { get; }
            internal Transform ToHost { get; }
            internal ElementId ViewId { get; }
            internal long? LinkId { get; }
            internal string Kind => LinkId.HasValue ? "link" : "host";

            internal QueryDocument(Document document, Transform toHost, ElementId viewId, long? linkId)
            {
                Document = document;
                ToHost = toHost;
                ViewId = viewId;
                LinkId = linkId;
            }

            internal IEnumerable<Element> GridCandidates(IEnumerable<Element> candidates, bool requestsGrids, bool hasExplicitIds) =>
                DatumGeometryRules.DocumentGridCandidates(Document, candidates, ViewId == null,
                    requestsGrids, hasExplicitIds,
                    document => new FilteredElementCollector(document).OfClass(typeof(Grid)).Cast<Element>(), e => Rid.Value(e.Id));

            internal void WriteSourceFields(JObject row, HashSet<string> fields)
            {
                if (fields == null || fields.Contains("source_kind") || LinkId.HasValue) row["source_kind"] = Kind;
                if (fields == null || fields.Contains("source_model")) row["source_model"] = Document.Title;
                if (fields == null || fields.Contains("link_instance_id") || LinkId.HasValue)
                    row["link_instance_id"] = LinkId.HasValue ? new JValue(LinkId.Value) : JValue.CreateNull();
            }

            internal DatumGeometryRules.GridReading ReadGridGeometry(Grid grid, bool spatialFilter, double scale, string units) =>
                DatumGeometryRules.ReadGrid(() => grid.IsCurved,
                    curved =>
                    {
                        Curve c = grid.Curve;
                        IList<XYZ> raw = curved ? c.Tessellate() : new[] { c.GetEndPoint(0), c.GetEndPoint(1) };
                        return raw.Select(Coordinates);
                    },
                    p => Coordinates(ToHost.OfPoint(new XYZ(p[0], p[1], p[2]))),
                    () =>
                    {
                        Box model = ElementBox(grid, ToHost);
                        return model == null ? null : new[] { Coordinates(model.Min), Coordinates(model.Max) };
                    },
                    () =>
                    {
                        Outline outline = grid.GetExtents();
                        return outline == null || outline.IsEmpty
                            ? null : new[] { Coordinates(outline.MinimumPoint), Coordinates(outline.MaximumPoint) };
                    }, spatialFilter, scale, units);
        }

        private static Box ElementBox(Element element, Transform transform)
        {
            BoundingBoxXYZ b = element.get_BoundingBox(null);
            if (b == null) return null;
            Transform toHost = transform.Multiply(b.Transform ?? Transform.Identity);
            var points = new List<XYZ>();
            foreach (double x in new[] { b.Min.X, b.Max.X })
                foreach (double y in new[] { b.Min.Y, b.Max.Y })
                    foreach (double z in new[] { b.Min.Z, b.Max.Z })
                        points.Add(toHost.OfPoint(new XYZ(x, y, z)));
            return new Box(
                new XYZ(points.Min(p => p.X), points.Min(p => p.Y), points.Min(p => p.Z)),
                new XYZ(points.Max(p => p.X), points.Max(p => p.Y), points.Max(p => p.Z)));
        }

        private static double[] Coordinates(XYZ p) => new[] { p.X, p.Y, p.Z };

        private sealed class Box
        {
            public readonly XYZ Min, Max;
            public Box(XYZ min, XYZ max) { Min = min; Max = max; }
            public bool Intersects(Box other) =>
                Min.X <= other.Max.X && Max.X >= other.Min.X &&
                Min.Y <= other.Max.Y && Max.Y >= other.Min.Y &&
                Min.Z <= other.Max.Z && Max.Z >= other.Min.Z;
        }
    }
}
