using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    internal static class CopySignatures
    {
        internal static CopyLayout ReadCopyLayout(Document doc, Element element)
        {
            if (element is Wall wall && wall.WallType.Kind == WallKind.Basic && wall.CrossSection == WallCrossSection.Vertical &&
                wall.Location is LocationCurve location &&
                wall.get_Parameter(BuiltInParameter.WALL_TOP_IS_ATTACHED)?.AsInteger() == 0 &&
                wall.get_Parameter(BuiltInParameter.WALL_BOTTOM_IS_ATTACHED)?.AsInteger() == 0)
            {
                Level baseLevel = doc.GetElement(wall.get_Parameter(BuiltInParameter.WALL_BASE_CONSTRAINT).AsElementId()) as Level;
                if (baseLevel == null) return null;
                double bottom = baseLevel.ProjectElevation + wall.get_Parameter(BuiltInParameter.WALL_BASE_OFFSET).AsDouble();
                Level topLevel = doc.GetElement(wall.get_Parameter(BuiltInParameter.WALL_HEIGHT_TYPE).AsElementId()) as Level;
                double top = topLevel == null ? bottom + wall.get_Parameter(BuiltInParameter.WALL_USER_HEIGHT_PARAM).AsDouble()
                    : topLevel.ProjectElevation + wall.get_Parameter(BuiltInParameter.WALL_TOP_OFFSET).AsDouble();
                double[][] axis = CopyCurve(location.Curve);
                if (axis == null || top <= bottom || wall.Width <= 0) return null;
                return new CopyLayout
                {
                    Kind = "wall_axis", Base = bottom, Top = top, Thickness = wall.Width,
                    Facing = Coordinates(wall.Orientation), Curves = new[] { axis }
                };
            }
            if (element is Floor floor && doc.GetElement(floor.SketchId) is Sketch sketch &&
                SlabShapeCommand.Editor(floor)?.IsEnabled != true)
            {
                var topFaces = HostObjectUtils.GetTopFaces(floor).Select(r => floor.GetGeometryObjectFromReference(r) as PlanarFace).ToList();
                if (topFaces.Count == 0 || topFaces.Any(f => f == null || f.FaceNormal.DotProduct(XYZ.BasisZ) < 1 - 1e-9))
                    return null;
                double top = topFaces[0].Origin.Z;
                if (topFaces.Any(f => Math.Abs(f.Origin.Z - top) > CopyRepresentationRules.SolidToleranceFeet))
                    return null;
                double thickness = floor.FloorType.GetCompoundStructure()?.GetWidth() ?? 0;
                var curves = new List<double[][]>();
                foreach (CurveArray loop in sketch.Profile)
                    foreach (Curve curve in loop)
                        curves.Add(CopyCurve(curve));
                if (thickness <= 0 || curves.Count == 0 || curves.Any(c => c == null)) return null;
                return new CopyLayout
                {
                    Kind = "floor_sketch", Base = top - thickness, Top = top, Thickness = thickness, Curves = curves.ToArray()
                };
            }
            return null;
        }

        internal static double[][] CopyCurve(Curve curve)
        {
            if (curve is Line) return new[] { Coordinates(curve.GetEndPoint(0)), Coordinates(curve.GetEndPoint(1)) };
            if (curve is Arc && curve.IsBound)
                return new[] { Coordinates(curve.GetEndPoint(0)), Coordinates(curve.Evaluate(0.5, true)), Coordinates(curve.GetEndPoint(1)) };
            return null;
        }

        internal static double[] Coordinates(XYZ p) => new[] { p.X, p.Y, p.Z };

        internal static CopySignature Signature(Document doc, Element element, XYZ offset = null) =>
            ReadSignature(doc, element, offset, null);

        // The plain signature plus the authored wall axis and build-up, for a wall whose joins may differ after a copy.
        // Null for anything that is not a plain vertical basic wall with a readable layout.
        internal static CopySignature WallAuthoredSignature(Document doc, Element element)
        {
            if (!(element is Wall)) return null;
            try
            {
                CopyLayout layout = ReadCopyLayout(doc, element);
                if (layout == null || layout.Kind != "wall_axis") return null;
                CopySignature signature = ReadSignature(doc, element, null, null);
                signature.Layout = layout;
                signature.Layers = ReadLayers(doc, element);
                return signature;
            }
            catch { return null; }
        }

        internal static CopySignature ReadSignature(Document doc, Element element, XYZ offset, List<Solid> solids)
        {
            Element type = element is ElementType ? element : doc.GetElement(element.GetTypeId());
            XYZ point = null;
            if (!(element is ElementType))
            {
                try
                {
                    if (element.Location is LocationPoint location) point = location.Point;
                    else if (element.Location is LocationCurve curve) point = curve.Curve.Evaluate(0.5, true);
                    else
                    {
                        BoundingBoxXYZ box = element.get_BoundingBox(null);
                        if (box != null) point = box.Transform.OfPoint((box.Min + box.Max) * 0.5);
                    }
                    if (point != null && offset != null) point += offset;
                }
                catch { point = null; }
            }
            double[] min = null, max = null;
            if (!(element is ElementType))
            {
                BoundingBoxXYZ box = element.get_BoundingBox(null);
                if (box != null)
                {
                    XYZ delta = offset ?? XYZ.Zero;
                    var corners = new List<XYZ>();
                    for (int x = 0; x < 2; x++) for (int y = 0; y < 2; y++) for (int z = 0; z < 2; z++)
                        corners.Add(box.Transform.OfPoint(new XYZ(x == 0 ? box.Min.X : box.Max.X, y == 0 ? box.Min.Y : box.Max.Y, z == 0 ? box.Min.Z : box.Max.Z)) + delta);
                    min = new[] { corners.Min(q => q.X), corners.Min(q => q.Y), corners.Min(q => q.Z) };
                    max = new[] { corners.Max(q => q.X), corners.Max(q => q.Y), corners.Max(q => q.Z) };
                }
            }
            return new CopySignature { GeometryPoints = element is ElementType ? null : GeometryPoints(element, offset ?? XYZ.Zero, solids), BoundsMin = min, BoundsMax = max, Id = Rid.Value(element.Id), IsType = element is ElementType, Category = SafeCategory(element),
                Type = type == null ? "<none>" : type.GetType().FullName + "|" + QualifiedName(type),
                Position = point == null ? null : new[] { point.X, point.Y, point.Z } };
        }

        internal static double[][] GeometryPoints(Element element, XYZ offset, List<Solid> solids)
        {
            var points = new List<XYZ>();
            Action<GeometryElement> read = null;
            read = geometry =>
            {
                if (geometry == null) return;
                foreach (GeometryObject item in geometry)
                {
                    if (item is GeometryInstance instance) read(instance.GetInstanceGeometry());
                    else if (item is Solid solid && solid.Faces.Size > 0)
                    {
                        solids?.Add(solid);
                        foreach (Face face in solid.Faces)
                            foreach (XYZ vertex in face.Triangulate().Vertices) points.Add(vertex + offset);
                    }
                    else if (item is Curve curve)
                        foreach (XYZ vertex in curve.Tessellate()) points.Add(vertex + offset);
                }
            };
            read(element.get_Geometry(new Options { DetailLevel = ViewDetailLevel.Fine }));
            if (points.Count == 0) return null;
            return points.GroupBy(q => string.Join("|", new[] { q.X, q.Y, q.Z }.Select(n => Math.Round(n, 6).ToString("R", System.Globalization.CultureInfo.InvariantCulture))))
                .Select(group => new[] { group.First().X, group.First().Y, group.First().Z }).ToArray();
        }

        internal static string QualifiedName(Element e)
        {
            string type = SafeName(e);
            string family = null;
            try { family = (e as ElementType)?.FamilyName; } catch { }
            return string.IsNullOrEmpty(family) ? type : family + ": " + type;
        }

        internal static string SafeName(Element element)
        { try { return element.Name; } catch { return null; } }

        internal static string SafeCategory(Element element)
        { try { return element.Category?.Name; } catch { return null; } }

        internal static XYZ Point(JToken token)
        {
            var array = token as JArray;
            if (array == null || array.Count < 2 || array.Count > 3)
                throw new ArgumentException("offset must be an array of 2 or 3 numbers");
            return new XYZ(array[0].Value<double>(), array[1].Value<double>(),
                           array.Count > 2 ? array[2].Value<double>() : 0);
        }

        internal static CopyLayer[] ReadLayers(Document doc, Element element)
        {
            CompoundStructure structure = ((HostObjAttributes)doc.GetElement(element.GetTypeId())).GetCompoundStructure();
            if (structure == null)
                throw new InvalidOperationException("The host type did not provide a compound structure.");
            int firstCore = structure.GetFirstCoreLayerIndex(), lastCore = structure.GetLastCoreLayerIndex();
            return structure.GetLayers().Select((layer, index) => new CopyLayer
            {
                Width = layer.Width,
                Definition = string.Join("|", layer.Function,
                    layer.MaterialId == ElementId.InvalidElementId ? "<by category>" :
                        (doc.GetElement(layer.MaterialId) as Material)?.Name ?? throw new InvalidOperationException("A layer material could not be read."),
                    index >= firstCore && index <= lastCore,
                    index == structure.VariableLayerIndex,
                    index == structure.StructuralMaterialIndex,
                    (index < firstCore || index > lastCore) && structure.ParticipatesInWrapping(index),
                    layer.Function == MaterialFunctionAssignment.StructuralDeck
                        ? doc.GetElement(layer.DeckProfileId)?.Name + "/" + layer.DeckEmbeddingType : "")
            }).ToArray();
        }
    
    }
}
