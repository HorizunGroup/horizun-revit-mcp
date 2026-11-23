// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// The Revit half of Core/PlanimetryGeneralRules.cs: reading a host wall's void
// faces around one opening, and the top faces and edges of a wall as one view
// sees them. Nothing here decides; it reduces geometry to the facts the rules
// decide on, and keeps the Revit objects so the caller can build references.
// Read-only.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    internal sealed class OpeningVoidResult
    {
        public OpeningVoidChoice Choice;
        public Dictionary<string, PlanarFace> Faces = new Dictionary<string, PlanarFace>(StringComparer.Ordinal);
        public XYZ Origin;
        public XYZ Tangent;
        public double LocationZ;

        /// <summary>A model point on a chosen jamb face, at the opening's own height.</summary>
        public XYZ JambPoint(WallVoidFace face) => Origin.Add(Tangent.Multiply(face.Along)).Add(new XYZ(0, 0, LocationZ - Origin.Z));
    }

    internal sealed class WallTopResult
    {
        public List<TopFaceFact> Faces = new List<TopFaceFact>();
        public Dictionary<string, XYZ> Points = new Dictionary<string, XYZ>(StringComparer.Ordinal);
    }

    internal static class PlanimetryGeometryReader
    {
        private static string Stable(Reference reference, Document doc)
        {
            if (reference == null) return null;
            try { return reference.ConvertToStableRepresentation(doc); } catch { return null; }
        }

        private static IEnumerable<Solid> Solids(GeometryElement geometry)
        {
            if (geometry == null) yield break;
            foreach (GeometryObject obj in geometry)
            {
                if (obj is Solid solid && solid.Faces.Size > 0) yield return solid;
                else if (obj is GeometryInstance instance)
                    foreach (Solid s in Solids(instance.GetInstanceGeometry())) yield return s;
            }
        }

        private static List<XYZ> Vertices(Face face)
        {
            var points = new List<XYZ>();
            foreach (EdgeArray loop in face.EdgeLoops)
                foreach (Edge edge in loop)
                    points.AddRange(edge.Tessellate());
            return points;
        }

        /// <summary>
        /// The jambs, head and sill of one hosted opening, measured on the void it cuts
        /// in its wall. Null with an error when the wall is not straight, the opening
        /// has no location, or the rules find no jamb pair.
        /// </summary>
        public static OpeningVoidResult OpeningVoid(Document doc, FamilyInstance opening, out string error)
        {
            error = null;
            var wall = opening?.Host as Wall;
            var line = (wall?.Location as LocationCurve)?.Curve as Line;
            if (line == null) { error = "the opening's host is not a straight wall."; return null; }
            XYZ origin = line.GetEndPoint(0), tangent = line.Direction.Normalize();
            XYZ location = (opening.Location as LocationPoint)?.Point;
            BoundingBoxXYZ box = opening.get_BoundingBox(null);
            if (location == null || box == null) { error = "the opening has no location point or bounds."; return null; }
            var result = new OpeningVoidResult { Origin = origin, Tangent = tangent, LocationZ = location.Z };
            var facts = new List<WallVoidFace>();
            GeometryElement geometry = wall.get_Geometry(new Options { ComputeReferences = true, DetailLevel = ViewDetailLevel.Fine });
            foreach (Solid solid in Solids(geometry))
                foreach (Face face in solid.Faces)
                {
                    if (!(face is PlanarFace planar) || planar.Reference == null) continue;
                    string stable = Stable(planar.Reference, doc);
                    if (stable == null || result.Faces.ContainsKey(stable)) continue;
                    XYZ n = planar.FaceNormal.Normalize();
                    List<XYZ> v = Vertices(planar);
                    if (v.Count == 0) continue;
                    var along = v.Select(p => p.Subtract(origin).DotProduct(tangent)).ToList();
                    facts.Add(new WallVoidFace
                    {
                        Stable = stable,
                        Along = planar.Origin.Subtract(origin).DotProduct(tangent),
                        Z = planar.Origin.Z,
                        NormalAlong = n.DotProduct(tangent), NormalZ = n.Z,
                        AlongMin = along.Min(), AlongMax = along.Max(),
                        ZMin = v.Min(p => p.Z), ZMax = v.Max(p => p.Z)
                    });
                    result.Faces[stable] = planar;
                }
            double? nominal = null;
            foreach (BuiltInParameter bip in new[] { BuiltInParameter.DOOR_WIDTH, BuiltInParameter.WINDOW_WIDTH,
                                                     BuiltInParameter.FAMILY_WIDTH_PARAM })
            {
                Parameter p = opening.Symbol?.get_Parameter(bip) ?? opening.get_Parameter(bip);
                if (p != null && p.HasValue && p.StorageType == StorageType.Double && p.AsDouble() > 0) { nominal = p.AsDouble(); break; }
            }
            double center = location.Subtract(origin).DotProduct(tangent);
            double midZ = (box.Min.Z + box.Max.Z) * 0.5;
            result.Choice = OpeningVoidRules.Choose(facts, center, midZ, nominal, out error);
            return result.Choice == null ? null : result;
        }

        /// <summary>
        /// The upward faces of a wall and their straight edges, in the frame of the
        /// view that will carry the dimension: U along the view's right, Z model
        /// elevation, Depth measured beyond the view plane (positive away from the
        /// viewer). Edge END POINT references are read where Revit offers them.
        /// </summary>
        public static WallTopResult WallTop(Document doc, View view, Wall wall)
        {
            var result = new WallTopResult();
            // The UNCUT model solid, on purpose. View-scoped geometry of a section is the
            // solid cut by the section plane, and its cut edges carry CUT_EDGE references
            // that Revit accepts into a dimension and then measures as ZERO (measured live
            // on Revit 2026, 2026-10-17). Only model edges and vertices follow the wall.
            GeometryElement geometry = wall.get_Geometry(new Options { ComputeReferences = true, DetailLevel = ViewDetailLevel.Fine });
            XYZ origin = view.Origin, right = view.RightDirection.Normalize(), toward = view.ViewDirection.Normalize();
            ViewPointFact Point(XYZ p, Reference reference)
            {
                string stable = Stable(reference, doc);
                if (stable != null) result.Points[stable] = p;
                return new ViewPointFact { U = p.Subtract(origin).DotProduct(right), Z = p.Z,
                    Depth = origin.Subtract(p).DotProduct(toward), Stable = stable };
            }
            foreach (Solid solid in Solids(geometry))
                foreach (Face face in solid.Faces)
                {
                    if (!(face is PlanarFace planar) || planar.Reference == null) continue;
                    XYZ n = planar.FaceNormal.Normalize();
                    if (n.Z <= 0.1) continue;
                    string stable = Stable(planar.Reference, doc);
                    if (stable == null) continue;
                    List<XYZ> v = Vertices(planar);
                    if (v.Count == 0) continue;
                    var depths = v.Select(p => origin.Subtract(p).DotProduct(toward)).ToList();
                    var fact = new TopFaceFact
                    {
                        Stable = stable, NormalZ = n.Z, NormalDotView = Math.Abs(n.DotProduct(toward)),
                        Elevation = planar.Origin.Z, DepthMin = depths.Min(), DepthMax = depths.Max()
                    };
                    result.Points[stable] = planar.Origin;
                    foreach (EdgeArray loop in planar.EdgeLoops)
                        foreach (Edge edge in loop)
                        {
                            if (!(edge.AsCurve() is Line edgeLine)) continue;
                            Reference start = null, end = null;
                            try { start = edge.GetEndPointReference(0); end = edge.GetEndPointReference(1); } catch { }
                            string edgeStable = Stable(edge.Reference, doc);
                            XYZ a = edgeLine.GetEndPoint(0), b = edgeLine.GetEndPoint(1);
                            if (edgeStable != null) result.Points[edgeStable] = a.Add(b).Multiply(0.5);
                            fact.Edges.Add(new TopEdgeFact { Stable = edgeStable, Start = Point(a, start), End = Point(b, end) });
                        }
                    result.Faces.Add(fact);
                }
            return result;
        }
    }
}
