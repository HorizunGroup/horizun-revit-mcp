using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Autodesk.Revit.DB
{
    public partial class XYZ
    {
        public static XYZ operator /(XYZ point, double divisor) => point.Multiply(1 / divisor);
    }

    public partial class Document
    {
        internal Action<List<ElementId>> TestWallFinishDeleting;
        public ICollection<ElementId> Delete(ICollection<ElementId> ids)
        {
            var cascade = ids.ToList();
            TestWallFinishDeleting?.Invoke(cascade);
            foreach (ElementId id in cascade) Elements.Remove(id.Value);
            return cascade;
        }
    }

    public sealed partial class Wall
    {
        public double Width { get; set; }
        public XYZ Orientation => new XYZ(0, 1, 0);
        public IList<ElementId> FindInserts(bool openings, bool shadows, bool embedded, bool shared) =>
            Array.Empty<ElementId>();
        public static Wall Create(Document doc, Curve curve, ElementId type, ElementId level, double height,
            double offset, bool flipped, bool structural)
        {
            var wall = new Wall
            {
                Id = new ElementId(doc.NextElementId++),
                Document = doc,
                Location = new LocationCurve { Curve = curve }
            };
            doc.Elements.Add(wall.Id.Value, wall);
            return wall;
        }
    }

    public enum WallKind { Basic }
    public class WallType : Element { public WallKind Kind => WallKind.Basic; public double Width { get; set; } }
    public static class WallUtils { public static void DisallowWallJoinAtEnd(Wall wall, int end) { } }
    public enum BooleanOperationsType { Intersect }
    public static class BooleanOperationsUtils
    {
        public static Solid ExecuteBooleanOperation(Solid a, Solid b, BooleanOperationsType operation) =>
            throw new NotSupportedException();
    }
    public static class SolidUtils
    {
        public static Solid CreateTransformed(Solid solid, Transform transform) => throw new NotSupportedException();
    }
}

namespace Horizun.Revit.Commands
{
    using Autodesk.Revit.DB;

    public sealed partial class WallFinishCommand
    {
        private static ArchModelEdit PlanConversion(Document doc, JObject request, List<Pair> pairs, JArray rows,
            Document linked, Transform transform) => throw new NotSupportedException();
        private static ArchModelEdit PlanPaintFaces(Document doc, JObject request) => throw new NotSupportedException();
    }

    internal static class JoinGeometryCommand
    {
        internal static List<Solid> SolidsOf(Element element) => throw new NotSupportedException();
    }
}
