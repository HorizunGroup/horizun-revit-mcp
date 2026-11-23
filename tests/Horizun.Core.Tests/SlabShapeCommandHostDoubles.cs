using System;
using System.Collections.Generic;

namespace Autodesk.Revit.DB
{
    public partial class XYZ
    {
        public double DotProduct(XYZ other) => X * other.X + Y * other.Y + Z * other.Z;
        public XYZ CrossProduct(XYZ other) => new XYZ(Y * other.Z - Z * other.Y, Z * other.X - X * other.Z, X * other.Y - Y * other.X);
        public double GetLength() => Math.Sqrt(DotProduct(this));
        public XYZ Normalize() => Multiply(1 / GetLength());
    }

    public partial class Element { public ElementId LevelId { get; set; } = ElementId.InvalidElementId; }
    public class Reference
    {
        internal Face Face;
        public string ConvertToStableRepresentation(Document doc) => throw new System.NotSupportedException();
    }

    public class HostObject : Element
    {
        public RectangularSlabFace TopFace { get; set; } = new RectangularSlabFace(8);
        public RectangularSlabFace BottomFace { get; set; } = new RectangularSlabFace(7.5);
        public override object GetGeometryObjectFromReference(Reference reference) => reference.Face;
    }

    public class Floor : HostObject
    {
        public bool EditorInitiallyUnavailable, EditorInitiallyThrows;
        public SlabShapeCreaseType SupportCreaseType;
        public Exception SupportFailure;
        public double TopFaceShiftAfterSupport;
        public int EditorReads { get; private set; }
        private SlabShapeEditor shape;
        public SlabShapeEditor Shape => shape ?? (shape = new SlabShapeEditor(this));

        public SlabShapeEditor GetSlabShapeEditor()
        {
            EditorReads++;
            if (EditorInitiallyUnavailable && Document.Regenerations == 0)
            {
                if (EditorInitiallyThrows) throw new InvalidOperationException("Floor geometry is not regenerated.");
                return null;
            }
            return Shape;
        }
    }

    public class RoofBase : HostObject { public SlabShapeEditor GetSlabShapeEditor() => throw new NotSupportedException(); }
    public class HostObjAttributes : ElementType { public CompoundStructure GetCompoundStructure() => null; }
    public class CompoundStructure { public double GetWidth() => throw new NotSupportedException(); }

    public static class HostObjectUtils
    {
        public static Reference[] GetTopFaces(HostObject host) => new[] { new Reference { Face = host.TopFace } };
        public static Reference[] GetBottomFaces(HostObject host) => new[] { new Reference { Face = host.BottomFace } };
    }

    public sealed class RectangularSlabFace : PlanarFace
    {
        private readonly CurveLoop boundary;
        private readonly double xMin, yMin, xMax, yMax;
        public CurveLoop Hole;
        public RectangularSlabFace(double z, double xMin = 0, double yMin = 0, double xMax = 4, double yMax = 3)
            : base(Array.Empty<Curve>(), z: z)
        {
            (this.xMin, this.yMin, this.xMax, this.yMax) = (xMin, yMin, xMax, yMax);
            boundary = Loop(xMin, yMin, xMax, yMax, z);
        }
        public override IList<CurveLoop> GetEdgesAsCurveLoops() =>
            Hole == null ? new[] { boundary } : new[] { boundary, Hole };
        public override IntersectionResult Project(XYZ p) =>
            p.X > xMin && p.X < xMax && p.Y > yMin && p.Y < yMax ? new IntersectionResult { XYZPoint = p } : null;

        public static CurveLoop Loop(double x0, double y0, double x1, double y1, double z)
        {
            var a = new XYZ(x0, y0, z); var b = new XYZ(x1, y0, z);
            var c = new XYZ(x1, y1, z); var d = new XYZ(x0, y1, z);
            return new CurveLoop { Line.CreateBound(a, b), Line.CreateBound(b, c), Line.CreateBound(c, d), Line.CreateBound(d, a) };
        }
    }

    public enum SlabShapeVertexType { Interior, Edge }
    public enum SlabShapeCreaseType { UserDrawn, Boundary, Auto }
    public class SlabShapeVertex { public XYZ Position; public SlabShapeVertexType VertexType = SlabShapeVertexType.Interior; }
    public class SlabShapeCrease
    {
        private Curve curve;
        private SlabShapeVertex[] endpoints;
        public Exception CurveFailure;
        public Exception EndPointsFailure;
        public Curve Curve { get => CurveFailure == null ? curve : throw CurveFailure; set => curve = value; }
        public SlabShapeVertex[] EndPoints
        {
            get => EndPointsFailure != null ? throw EndPointsFailure : endpoints ?? new[]
            {
                new SlabShapeVertex { Position = curve.GetEndPoint(0) },
                new SlabShapeVertex { Position = curve.GetEndPoint(1) }
            };
            set => endpoints = value;
        }
        private SlabShapeCreaseType creaseType;
        public Exception CreaseTypeFailure;
        public SlabShapeCreaseType CreaseType
        {
            get => CreaseTypeFailure == null ? creaseType : throw CreaseTypeFailure;
            set => creaseType = value;
        }
    }

    public sealed class SlabShapeEditor
    {
        private readonly Floor floor;
        private int generationAtEnable;
        private readonly List<SlabShapeCrease> creases = new List<SlabShapeCrease>();
        public SlabShapeEditor(Floor floor) { this.floor = floor; }
        public int EnableCalls { get; private set; }
        public bool IsEnabled => EnableCalls > 0;
        public List<SlabShapeCrease> TestCreases => creases;
        public List<SlabShapeVertex> TestVertices { get; } = new List<SlabShapeVertex>();
        public Action<SlabShapeVertex, SlabShapeVertex> TestSplit { get; set; }
        public Action<Line> TestSupport { get; set; }
        public Exception TestCreasesFailure;
        public Action<XYZ> TestDrawPoint;
        public Action<SlabShapeVertex, SlabShapeVertex> TestLegacySplit;
        public Action CaptureState()
        {
            int enableCalls = EnableCalls, enabledAt = generationAtEnable;
            var oldCreases = creases.ToArray();
            var oldVertices = TestVertices.ToArray();
            var top = floor.TopFace;
            return () =>
            {
                EnableCalls = enableCalls;
                generationAtEnable = enabledAt;
                creases.Clear();
                creases.AddRange(oldCreases);
                TestVertices.Clear();
                TestVertices.AddRange(oldVertices);
                floor.TopFace = top;
            };
        }
        public void Enable() { EnableCalls++; generationAtEnable = floor.Document.Regenerations; }
        public IEnumerable<SlabShapeVertex> SlabShapeVertices
        {
            get
            {
                if (IsEnabled && floor.Document.Regenerations <= generationAtEnable)
                    throw new InvalidOperationException("Enabling shape editing requires regeneration.");
                return TestVertices;
            }
        }
        public IEnumerable<SlabShapeCrease> SlabShapeCreases => TestCreasesFailure == null ? creases : throw TestCreasesFailure;
        public void PickSupport(Line line)
        {
            if (TestSupport != null) { TestSupport(line); return; }
            if (floor.SupportFailure != null) throw floor.SupportFailure;
            XYZ start = line.GetEndPoint(0), end = line.GetEndPoint(1);
            if (start.Z != floor.BottomFace.Origin.Z || end.Z != floor.BottomFace.Origin.Z)
                throw new InvalidOperationException("Support must lie on the underside.");
            var thickness = new XYZ(0, 0, floor.TopFace.Origin.Z - floor.BottomFace.Origin.Z);
            creases.Add(new SlabShapeCrease { Curve = Line.CreateBound(start + thickness, end + thickness), CreaseType = floor.SupportCreaseType });
            if (floor.TopFaceShiftAfterSupport != 0)
                floor.TopFace = new RectangularSlabFace(floor.TopFace.Origin.Z + floor.TopFaceShiftAfterSupport);
        }
        public void AddPoint(XYZ point) => TestVertices.Add(new SlabShapeVertex { Position = point });
        public SlabShapeVertex DrawPoint(XYZ point)
        {
            TestDrawPoint?.Invoke(point);
            var vertex = new SlabShapeVertex { Position = point, VertexType = SlabShapeVertexType.Edge };
            TestVertices.Add(vertex);
            return vertex;
        }
        public void DrawSplitLine(SlabShapeVertex a, SlabShapeVertex b) => (TestLegacySplit ?? AddSplitLine)(a, b);
        public void AddSplitLine(SlabShapeVertex a, SlabShapeVertex b) =>
            (TestSplit ?? throw new NotSupportedException())(a, b);
        public void ModifySubElement(object part, double offset) => throw new NotSupportedException();
        public void ResetSlabShape() => throw new NotSupportedException();
    }
}

namespace Autodesk.Revit.UI
{
    public partial class TestApplication { public double ShortCurveTolerance => 0.002; }
}

namespace Autodesk.Revit.Exceptions
{
    public class ApplicationException : System.Exception { }
    public class InvalidObjectException : System.Exception
    {
        public InvalidObjectException(string message) : base(message) { }
    }
}
