using System;
using System.Collections.Generic;

namespace Autodesk.Revit.DB
{
    public partial class Document
    {
        internal Action<IExportContext2D, ElementId> TestExport2D;
    }

    public partial class Transform
    {
        public bool AlmostEqual(Transform other) =>
            OfPoint(XYZ.Zero).DistanceTo(other.OfPoint(XYZ.Zero)) < 1e-9 &&
            OfPoint(BasisX).DistanceTo(other.OfPoint(BasisX)) < 1e-9 &&
            OfPoint(BasisY).DistanceTo(other.OfPoint(BasisY)) < 1e-9;
    }

    internal static class WorksharingUtils
    {
        public static WorksharingTooltipInfo GetWorksharingTooltipInfo(Document doc, ElementId id) =>
            throw new NotSupportedException();
    }

    internal sealed class WorksharingTooltipInfo { public string LastChangedBy => throw new NotSupportedException(); }
    public class SpatialElement : Element { }
    internal enum RenderNodeAction { Proceed }
    internal sealed record ElementNode(Document Document, ElementId ElementId, ElementId LinkInstanceId);
    internal sealed record LineSegment(XYZ StartPoint, XYZ EndPoint);
    internal sealed record LinkNode(Document Document, ElementId SymbolId, Transform Placement)
    {
        public Transform GetTransform() => Placement;
        public Document GetDocument() => Document;
    }

    internal sealed class ViewNode { }
    internal sealed class FaceNode { }
    internal sealed class PolymeshTopology { }
    internal sealed class InstanceNode { public Transform GetTransform() => throw new NotSupportedException(); }
    internal sealed class FaceEdgeNode { }
    internal sealed class FaceSilhouetteNode { }
    internal sealed class CurveNode { }
    internal sealed class PolylineNode { }
    internal sealed record PolylineSegments(IList<XYZ> Vertices)
    {
        public IList<XYZ> GetVertices() => Vertices;
    }
    internal sealed class TextNode { }
    internal sealed class MaterialNode { }
    internal sealed class RPCNode { }
    internal sealed class LightNode { }

    internal interface IExportContext2D
    {
        bool Start();
        void Finish();
        bool IsCanceled();
        RenderNodeAction OnViewBegin(ViewNode node);
        void OnViewEnd(ElementId id);
        RenderNodeAction OnElementBegin(ElementId id);
        void OnElementEnd(ElementId id);
        RenderNodeAction OnFaceBegin(FaceNode node);
        void OnFaceEnd(FaceNode node);
        void OnPolymesh(PolymeshTopology mesh);
        RenderNodeAction OnElementBegin2D(ElementNode node);
        void OnElementEnd2D(ElementNode node);
        RenderNodeAction OnInstanceBegin(InstanceNode node);
        void OnInstanceEnd(InstanceNode node);
        RenderNodeAction OnLinkBegin(LinkNode node);
        void OnLinkEnd(LinkNode node);
        RenderNodeAction OnFaceEdge2D(FaceEdgeNode node);
        RenderNodeAction OnFaceSilhouette2D(FaceSilhouetteNode node);
        RenderNodeAction OnCurve(CurveNode node);
        RenderNodeAction OnPolyline(PolylineNode node);
        void OnLineSegment(LineSegment segment);
        void OnPolylineSegments(PolylineSegments segments);
        void OnText(TextNode node);
        void OnMaterial(MaterialNode node);
        void OnRPC(RPCNode node);
        void OnLight(LightNode node);
    }

    internal sealed class CustomExporter : IDisposable
    {
        private readonly Document document;
        private readonly IExportContext2D context;
        public CustomExporter(Document document, IExportContext2D context)
        {
            this.document = document;
            this.context = context;
        }

        public bool ShouldStopOnError { get; set; }
        public bool IncludeGeometricObjects { get; set; }
        public bool Export2DIncludingAnnotationObjects { get; set; }
        public void Export(IList<ElementId> ids)
        {
            context.Start();
            foreach (ElementId id in ids)
            {
                context.OnViewBegin(new ViewNode());
                document.TestExport2D?.Invoke(context, id);
                context.OnViewEnd(id);
            }
            context.Finish();
        }
        public void Dispose() { }
    }
}
