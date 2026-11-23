// Deterministic API doubles for production sheet command response tests.
// They do not establish Autodesk geometry behaviour.
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
namespace Autodesk.Revit.DB
{
    public partial class Element
    {
        public BuiltInCategory? TestCategory;
        public long TestOwnerView { get => OwnerViewId?.Value ?? 0; set => OwnerViewId = new ElementId(value); }
        public bool TestHiddenInOwnerView;
        public Document Document;
        public bool Pinned;
    }
    public partial class XYZ
    {
        public static XYZ Zero => new XYZ(0, 0, 0);
        public static XYZ operator *(XYZ p, double factor) => new XYZ(p.X * factor, p.Y * factor, p.Z * factor);
    }
    public class Outline
    {
        public XYZ MinimumPoint, MaximumPoint; public bool IsEmpty => false;
        public Outline(XYZ a, XYZ b) { MinimumPoint=a; MaximumPoint=b; }
    }
    public partial class Transform
    {
        private readonly Func<XYZ, XYZ> map;
        public Transform(Func<XYZ, XYZ> map = null) { this.map = map; }
        public static Transform Identity => new Transform();
        public XYZ BasisX { get; set; } = new XYZ(1, 0, 0);
        public XYZ BasisY { get; set; } = new XYZ(0, 1, 0);
        public bool HasReflection { get; set; }
        public XYZ TestTranslation = XYZ.Zero;
        public XYZ OfPoint(XYZ p) => (map?.Invoke(p) ?? p) + TestTranslation;
        public Transform Multiply(Transform other) => new Transform(p => OfPoint(other.OfPoint(p)));
    }
    public class TransformWithBoundary { public Transform GetModelToProjectionTransform() => Transform.Identity; }
    public partial class View
    {
        public int Scale = 50; public bool IsTemplate, CropBoxActive;
        public bool TestCropTransformUnreadable;
        public BoundingBoxXYZ TestCropBox;
        public ElementId ViewTemplateId = ElementId.InvalidElementId;
        public BoundingBoxXYZ CropBox => TestCropBox ?? new BoundingBoxXYZ { Min=new XYZ(0,0,0), Max=new XYZ(1,1,0) };
        public IList<TransformWithBoundary> GetModelToProjectionTransforms() => TestCropTransformUnreadable
            ? throw new InvalidOperationException("crop transform unavailable") : new[] { new TransformWithBoundary() };
        public ViewCropRegionShapeManager GetCropRegionShapeManager() => new ViewCropRegionShapeManager();
        public ICollection<ElementId> GetNonControlledTemplateParameterIds() => new List<ElementId>();
    }
    public class ViewCropRegionShapeManager { public IList<CurveLoop> GetCropShape() => new List<CurveLoop>(); }
    public partial class View3D : View { public bool IsPerspective { get; set; } }
    public class ViewSheet : View
    {
        public string SheetNumber = "S1";
        public BoundingBoxUV TestOutline = new BoundingBoxUV();
        public BoundingBoxUV Outline => TestOutline;
        public ICollection<ElementId> GetAllViewports() => Document.Elements.Values.OfType<Viewport>().Where(v=>v.SheetId.Value==Id.Value).Select(v=>v.Id).ToList();

        public static ViewSheet Create(Document doc, ElementId titleBlockType)
        {
            var sheet = new ViewSheet { Id = new ElementId(doc.NextElementId++), Document = doc };
            doc.Elements.Add(sheet.Id.Value, sheet);
            ElementId actualType = doc.TestSubstituteTitleBlockType ?? titleBlockType;
            if (actualType != ElementId.InvalidElementId) doc.AddTitleBlock(sheet, actualType);
            return sheet;
        }
    }
    public class UV { public double U, V; }
    public class BoundingBoxUV { public UV Min = new UV(); public UV Max = new UV { U=1,V=1 }; }
    public class Viewport : Element
    {
        public ElementId ViewId, SheetId;
        public double[] TestBox = { 0,0,0.1,0.1 }; public double[] TestLabel;
        public bool ThrowOnMove, UnreadableBox, UnreadableLabel, UnreadableAfterMove; public int MoveCalls;
        public bool UnreadableLabelOffset;
        private XYZ labelOffset = XYZ.Zero;
        public XYZ LabelOffset
        {
            get => UnreadableLabelOffset ? throw new InvalidOperationException("title offset unavailable") : labelOffset;
            set => labelOffset = value;
        }
        public double LabelLineLength;
        public int TestLabelReadsUntilError;
        public Outline GetBoxOutline() => UnreadableBox || (UnreadableAfterMove && MoveCalls > 0) ? throw new InvalidOperationException("box unavailable") : new Outline(new XYZ(TestBox[0],TestBox[1],0),new XYZ(TestBox[2],TestBox[3],0));
        public Outline GetLabelOutline()
        {
            if (UnreadableLabel || (TestLabelReadsUntilError > 0 && --TestLabelReadsUntilError == 0))
                throw new InvalidOperationException("label unavailable");
            return TestLabel == null ? null : new Outline(new XYZ(TestLabel[0],TestLabel[1],0),new XYZ(TestLabel[2],TestLabel[3],0));
        }
        public int TestBoxCenterReadsUntilError;
        public XYZ GetBoxCenter()
        {
            if (TestBoxCenterReadsUntilError > 0 && --TestBoxCenterReadsUntilError == 0)
                throw new InvalidOperationException("Viewport center unavailable");
            return new XYZ((TestBox[0]+TestBox[2])/2,(TestBox[1]+TestBox[3])/2,0);
        }
        public void SetBoxCenter(XYZ p)
        {
            if (ThrowOnMove) throw new InvalidOperationException("move failed");
            MoveCalls++; XYZ c=GetBoxCenter(); double x=p.X-c.X,y=p.Y-c.Y;
            TestBox=new[] { TestBox[0]+x,TestBox[1]+y,TestBox[2]+x,TestBox[3]+y };
            if (TestLabel != null) TestLabel=new[] { TestLabel[0]+x,TestLabel[1]+y,TestLabel[2]+x,TestLabel[3]+y };
        }
        public Transform GetProjectionToSheetTransform() => new Transform { TestTranslation = GetBoxCenter() };
    }
    public enum DatumEnds { End0, End1 }
    public enum DatumExtentType { ViewSpecific }
    public class DatumPlane : Element
    {
        public IList<Curve> GetCurvesInView(DatumExtentType t, View v) => new List<Curve>();
        public bool IsBubbleVisibleInView(DatumEnds e, View v) => false;
    }
    public class Grid : DatumPlane
    {
        public Curve Curve { get; set; }
        public bool IsCurved;
        public Outline TestExtents;
        public Outline GetExtents() => TestExtents;
    }
    public class GeometryObject { }
    public class GeometryElement : List<GeometryObject> { }
    public class GeometryInstance : GeometryObject
    {
        public GeometryElement TestInstanceGeometry = new GeometryElement();
        public GeometryElement GetInstanceGeometry() => TestInstanceGeometry;
    }
    public class Solid : GeometryObject { public double Volume => throw new NotSupportedException(); public List<Face> Faces = new List<Face>(); public IList<Edge> Edges => Array.Empty<Edge>(); }
    public class Face
    {
        public Mesh TestMesh;
        public Mesh Triangulate() => TestMesh;
        public virtual IList<CurveLoop> GetEdgesAsCurveLoops() => throw new NotSupportedException();
        public bool IsInside(UV point) => throw new NotSupportedException();
        public virtual IntersectionResult Project(XYZ point) => throw new NotSupportedException();
    }
    public class Mesh : GeometryObject
    {
        public List<MeshTriangle> TestTriangles = new List<MeshTriangle>();
        public int NumTriangles => TestTriangles.Count;
        public MeshTriangle get_Triangle(int i) => TestTriangles[i];
        public IList<XYZ> Vertices => Array.Empty<XYZ>();
    }
    public class MeshTriangle
    {
        public XYZ[] TestVertices;
        public XYZ get_Vertex(int i) => TestVertices[i];
    }
    public class PolyLine : GeometryObject { public IList<XYZ> GetCoordinates() => new List<XYZ>(); }
    public class Options { public View View; public ViewDetailLevel DetailLevel; }
    public class Family : Element { public bool IsEditable => false; }
    public partial class FamilyInstance
    {
        public FamilySymbol Symbol;
        public Structure.StructuralType StructuralType { get; set; }
        public ElementId TestTypeId;
        public GeometryElement TestGeometry = new GeometryElement();
        public int GeometryReads;
        public override GeometryElement get_Geometry(Options o) { GeometryReads++; return TestGeometry; }
        public Transform Placement { get; set; } = Transform.Identity;
        public Transform GetTransform() => Placement;
    }
    public class CurveElement : Element { public Curve GeometryCurve; }
    public partial class Document
    {
        public readonly List<long> TestOwnershipChecks = new List<long>();
        public int TestTransactionStarts, TestRollbacks;
        public Action TestAfterCommit;
        public Action TestAfterAssimilate;
        public Horizun.Revit.Core.ConfirmationStore TestConfirmationStore;
        public ElementId TestSubstituteTitleBlockType;
        public Exception TestTitleBlockReadError;

        public void AddTitleBlock(ViewSheet sheet, ElementId type, bool hidden = false)
        {
            var block = new FamilyInstance
            {
                Id = new ElementId(NextElementId++),
                Document = this,
                TestCategory = BuiltInCategory.OST_TitleBlocks,
                TestOwnerView = sheet.Id.Value,
                TestHiddenInOwnerView = hidden,
                TestTypeId = type
            };
            Elements.Add(block.Id.Value, block);
        }
        public Document EditFamily(Family f) => throw new InvalidOperationException("family unavailable");
        public void Close(bool save) { }
    }
}
namespace Horizun.Revit.Core
{
    internal static partial class HrzOwnership
    {
        public static void Require(Document d, Element e, string kind, bool allow, string argument) { d.TestOwnershipChecks.Add(e.Id.Value); }
        public static bool IsTrustedOwned(Document d, Element e) => !d.TestScheduleHost || e.TestScheduleOwned;
        public static string GateReason(Document d, Element e) => IsTrustedOwned(d, e) ? null : "not owned";
        public static bool CreatedThisSession(Document d, Element e) => false;
        public static bool? MarkPresence(Element e) => e.TestScheduleOwned;
    }
}
