using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public sealed partial class CreateElementsCommand
    {
        private static void PlanProfileWall(Document doc, Plan p)
        {
            p.Level = Need<Level>(doc, p.Input, "level_id"); p.Type = Need<WallType>(doc, p.Input, "type_id");
            if (((WallType)p.Type).Kind != WallKind.Basic) throw new ArgumentException("wall_profile requires a Basic WallType.");
            if (!(p.Input["profile"] is JArray loops) || loops.Count != 1 || !(loops[0] is JArray contour) || contour.Count < 3)
                throw new ArgumentException("wall_profile requires one contour of at least three absolute XYZ points.");
            var points = contour.Select(v => Point(v, p.Scale, true)).ToList();
            if (points[0].DistanceTo(points[points.Count - 1]) <= GeometryInput.Tolerance) points.RemoveAt(points.Count - 1);
            if (points.Count < 3) throw new ArgumentException("Profile has fewer than three distinct points.");
            p.ProfileOrigin = points[0]; XYZ normal = null;
            for (int i = 1; i < points.Count - 1; i++)
            {
                var cross = (points[i] - points[0]).CrossProduct(points[i + 1] - points[0]);
                if (cross.GetLength() > GeometryInput.Tolerance) { normal = cross.Normalize(); break; }
            }
            if (normal == null || Math.Abs(normal.Z) > 1e-8) throw new ArgumentException("Wall profile must lie in a vertical plane.");
            p.ProfileNormal = normal; var u = XYZ.BasisZ.CrossProduct(normal).Normalize();
            if (points.Any(x => Math.Abs((x - p.ProfileOrigin).DotProduct(normal)) > GeometryInput.Tolerance)) throw new ArgumentException("Wall profile is not coplanar.");
            var local = new JArray(points.Select(x => new JArray((x - p.ProfileOrigin).DotProduct(u), x.Z, 0)));
            GeometryInput.HorizontalProfile(new JArray { local }, 1, doc.Application.ShortCurveTolerance);
            p.WallProfile = new List<Curve>();
            for (int i = 0; i < points.Count; i++) p.WallProfile.Add(Line.CreateBound(points[i], points[(i + 1) % points.Count]));
        }
        private static Element CreateProfileWall(Document doc, Plan p)
        {
            var wall = Wall.Create(doc, p.WallProfile, p.Type.Id, p.Level.Id, p.Input.Value<bool?>("structural") == true, p.ProfileNormal);
            double expectedMin = p.WallProfile.Min(c => c.GetEndPoint(0).Z);
            SetDouble(wall, BuiltInParameter.WALL_BASE_OFFSET, expectedMin - p.Level.ProjectElevation);
            doc.Regenerate();
            return wall;
        }
        private static bool ProfileWallMatches(Element e, Plan p)
        {
            var wall = (Wall)e;
            var faces = HostObjectUtils.GetSideFaces(wall, ShellLayerType.Exterior).Select(r => wall.GetGeometryObjectFromReference(r) as PlanarFace).ToList();
            if (faces.Count != 1 || faces[0] == null) return false;
            var actual = faces[0].GetEdgesAsCurveLoops().SelectMany(l => l).ToList();
            if (actual.Count != p.WallProfile.Count) return false;
            XYZ Project(XYZ v) => v - p.ProfileNormal * (v - p.ProfileOrigin).DotProduct(p.ProfileNormal);
            foreach (var expected in p.WallProfile)
            {
                bool Near(XYZ a, XYZ b) => a.DistanceTo(Project(b)) <= GeometryInput.Tolerance;
                int index = actual.FindIndex(c => (Near(expected.GetEndPoint(0), c.GetEndPoint(0)) && Near(expected.GetEndPoint(1), c.GetEndPoint(1))) ||
                    (Near(expected.GetEndPoint(0), c.GetEndPoint(1)) && Near(expected.GetEndPoint(1), c.GetEndPoint(0))));
                if (index < 0) return false; actual.RemoveAt(index);
            }
            return true;
        }
        private static void PlanDisplacement(Document doc, Plan p)
        {
            p.OwnerView = Need<View3D>(doc, p.Input, "view_id");
            if (p.OwnerView.IsTemplate) throw new ArgumentException("Displacement needs a 3D non-template view.");
            p.Displacement = Point(p.Input["displacement"], p.Scale, true);
            if (!(p.Input["element_ids"] is JArray ids) || ids.Count < 1 || ids.Count > 2000) throw new ArgumentException("element_ids needs 1..2000 IDs.");
            p.DisplacedIds = new List<ElementId>();
            foreach (var token in ids)
            {
                if (token.Type != JTokenType.Integer || !Rid.CanRepresent(token.Value<long>())) throw new ArgumentException("Invalid displaced ID.");
                Element element = doc.GetElement(Rid.Make(token.Value<long>()));
                if (element == null || element is ElementType) throw new ArgumentException("Displacement target is missing or is a type.");
                if (DisplacementElement.GetDisplacementElementId(p.OwnerView, element.Id) != ElementId.InvalidElementId)
                    throw new ArgumentException("Target is already displaced; nested/reparented displacement is not implicit.");
                if (p.DisplacedIds.Contains(element.Id)) throw new ArgumentException("Duplicate displacement target.");
                var additional = DisplacementElement.GetAdditionalElementsToDisplace(doc, p.OwnerView, element.Id);
                if (additional.Any(id => !ids.Any(v => v.Value<long>() == Rid.Value(id)))) throw new ArgumentException("Include hosted/dependent elements required for displacement explicitly.");
                p.DisplacedIds.Add(element.Id);
            }
            p.DisplacedState = DisplacementSourceState(doc, p.DisplacedIds);
        }
        private static JObject DisplacementSourceState(Document doc, IEnumerable<ElementId> ids)
        {
            var state = new JObject();
            foreach (var id in ids)
            {
                var element = doc.GetElement(id); var box = element?.get_BoundingBox(null);
                if (box == null) throw new InvalidOperationException("Displaced element has no measurable physical bounds.");
                state[Rid.Value(id).ToString(System.Globalization.CultureInfo.InvariantCulture)] = new JObject
                { ["uid"] = element.UniqueId, ["type_id"] = Rid.Value(element.GetTypeId()), ["min"] = new JArray(box.Min.X, box.Min.Y, box.Min.Z), ["max"] = new JArray(box.Max.X, box.Max.Y, box.Max.Z) };
            }
            return state;
        }
    }
}
