using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    internal static partial class LeaderTargetReader
    {
        // A view can hide individual linked elements. Model collection and a ray in
        // another view cannot see that state. Read bounded strokes with their owning
        // identities from Revit's 2D output without activating or changing the view.
        internal sealed class PlanLinkGraphics
        {
            private readonly ViewPlan _view;
            private bool _regionsRead;
            private long? _regionId;
            private LinkedOutput _output;
            private string _failure;

            public PlanLinkGraphics(ViewPlan view) { _view = view; }

            public long? PlanRegionId()
            {
                if (_regionsRead) return _regionId;
                ElementId primary = _view.GetPrimaryViewId();
                foreach (ElementId owner in new[] { _view.Id, primary })
                {
                    if (owner == ElementId.InvalidElementId) continue;
                    using (var regions = new FilteredElementCollector(_view.Document).OwnedByView(owner)
                        .OfCategory(BuiltInCategory.OST_PlanRegion).WhereElementIsNotElementType())
                    {
                        ElementId id = regions.FirstElementId();
                        if (id == ElementId.InvalidElementId) continue;
                        _regionId = Rid.Value(id);
                        break;
                    }
                }
                _regionsRead = true;
                return _regionId;
            }

            private PlanStrokeIndex<Document> Read()
            {
                if (_output == null && _failure == null)
                {
                    try
                    {
                        var output = new LinkedOutput(_view);
                        using (var exporter = new CustomExporter(_view.Document, output))
                        {
                            exporter.ShouldStopOnError = true;
                            exporter.IncludeGeometricObjects = true;
                            // Model symbols can emit in the annotation pass. Filter by
                            // owner, and keep tessellated graphics from the native view.
                            exporter.Export2DIncludingAnnotationObjects = true;
                            exporter.Export(new[] { _view.Id });
                        }
                        _output = output;
                    }
                    catch (Exception ex) { _failure = ex.Message; }
                }
                if (_failure != null)
                    throw new InvalidOperationException("the view's linked 2D graphics could not be read: " + _failure);
                if (!_output.Measured)
                    throw new InvalidOperationException("the 2D output contained no identifiable model segments (" +
                        _output.Diagnostics + ")");
                return _output.Strokes;
            }

            public double Distance(RevitLinkInstance link, Element element, XYZ point)
                => Read().Distance(_output.LinkedDocument(Rid.Value(link.Id)), Rid.Value(element.Id), Rid.Value(link.Id),
                    new CadPoint(point.X, point.Y, point.Z))
                    ?? throw new InvalidOperationException("a drawn segment within 0.1 mm of the end has no measurable element or link instance identity");

            public IEnumerable<(long ElementId, double Distance)> NearbyElements(RevitLinkInstance link, XYZ point)
                => Read().NearbyElements(_output.LinkedDocument(Rid.Value(link.Id)), Rid.Value(link.Id),
                    new CadPoint(point.X, point.Y, point.Z));

            public (Document Document, long ElementId, long InstanceId)? UniqueLinkedStroke(XYZ point, out bool ambiguous)
                => Read().UniqueLinkedStroke(new CadPoint(point.X, point.Y, point.Z), out ambiguous);

            public IEnumerable<XYZ> AimPoints(RevitLinkInstance link, Element element)
            {
                foreach (CadPoint p in Read().AimPoints(_output.LinkedDocument(Rid.Value(link.Id)), Rid.Value(element.Id), Rid.Value(link.Id)))
                    yield return new XYZ(p.X, p.Y, p.Z);
            }

            private sealed class LinkedOutput : IExportContext2D
            {
                private readonly Document _host;
                private readonly List<(Document Document, ElementId Type, long Id, Transform Local, Transform Total)> _links =
                    new List<(Document, ElementId, long, Transform, Transform)>();
                private readonly Dictionary<long, Document> _linkedDocuments = new Dictionary<long, Document>();
                private readonly Stack<long?> _linkScopes = new Stack<long?>();
                private readonly Stack<(long Element, Document Reported, long Instance)> _elements =
                    new Stack<(long, Document, long)>();
                private readonly Dictionary<(Document Reported, long Instance, long Element), (Document Owner, bool Ignore)> _owners =
                    new Dictionary<(Document, long, long), (Document, bool)>();
                private readonly Stack<Transform> _transforms = new Stack<Transform>();
                private int _elementNodes, _linkNodes, _segments, _unattributed;
                public readonly PlanStrokeIndex<Document> Strokes = new PlanStrokeIndex<Document>();
                public bool Measured => Strokes.Measured;
                public string Diagnostics => $"element nodes: {_elementNodes}, link nodes: {_linkNodes}, " +
                    $"segments: {_segments}, unattributed segments: {_unattributed}";

                public LinkedOutput(ViewPlan view)
                {
                    _host = view.Document;
                    _transforms.Push(Transform.Identity);
                    // Include every placement when matching a scope transform: visibility
                    // of one placement cannot disambiguate coincident instances.
                    using (var links = new FilteredElementCollector(_host).OfClass(typeof(RevitLinkInstance)))
                        foreach (RevitLinkInstance link in links)
                        {
                            Document linked = link.GetLinkDocument();
                            if (linked == null) continue;
                            long id = Rid.Value(link.Id);
                            _linkedDocuments.Add(id, linked);
                            _links.Add((linked, link.GetTypeId(), id, link.GetTransform(), link.GetTotalTransform()));
                        }
                }

                public Document LinkedDocument(long id) =>
                    _linkedDocuments.TryGetValue(id, out Document document) ? document : null;

                private (Document Owner, bool Ignore) Identify(Document reported, long instance, long element)
                {
                    var key = (reported, instance, element);
                    if (_owners.TryGetValue(key, out var result)) return result;
                    Document owner = PlanLeaderRules.ExportedStrokeDocument(_host, reported, instance, element,
                        LinkedDocument, (document, id) => document.GetElement(Rid.Make(id)) != null, OpenGuard.SameDocument);
                    Element model = owner?.GetElement(Rid.Make(element));
                    // The annotation pass can include real model symbols. Exclude
                    // resolved annotation owners; retain unresolved linework as ties.
                    result = (IsModelTarget(model) ? owner : null,
                        model?.Category?.CategoryType == CategoryType.Annotation);
                    _owners.Add(key, result);
                    return result;
                }

                private void Segment(XYZ a, XYZ b)
                {
                    var node = _elements.Count == 0 ? (Element: -1L, Reported: (Document)null, Instance: -1L) : _elements.Peek();
                    long? instance = PlanLeaderRules.ExportedStrokeInstance(node.Instance, _linkScopes.Count,
                        _linkScopes.Count == 0 ? null : _linkScopes.Peek());
                    // A link scope identifies the placement, but only an explicit
                    // instance id can reattribute a host-reported element to it.
                    if (node.Instance < 0 && _linkScopes.Count > 0 &&
                        OpenGuard.SameDocument(node.Reported, _host)) instance = null;
                    var identity = instance.HasValue ? Identify(node.Reported, instance.Value, node.Element) : (Owner: (Document)null, Ignore: false);
                    if (identity.Ignore) return;
                    Document owner = identity.Owner;
                    Transform toHost = _transforms.Peek();
                    XYZ start = toHost.OfPoint(a), end = toHost.OfPoint(b);
                    _segments++;
                    if (owner == null) _unattributed++;
                    Strokes.Add(owner, node.Element, instance ?? -1, owner != null,
                        new CadPoint(start.X, start.Y, start.Z), new CadPoint(end.X, end.Y, end.Z));
                }

                public bool Start() => true;
                public void Finish() { }
                public bool IsCanceled() => false;
                public RenderNodeAction OnViewBegin(ViewNode node) => RenderNodeAction.Proceed;
                public void OnViewEnd(ElementId id) { }
                public RenderNodeAction OnElementBegin(ElementId id) => RenderNodeAction.Proceed;
                public void OnElementEnd(ElementId id) { }
                public RenderNodeAction OnFaceBegin(FaceNode node) => RenderNodeAction.Proceed;
                public void OnFaceEnd(FaceNode node) { }
                public void OnPolymesh(PolymeshTopology mesh) { }
                public RenderNodeAction OnElementBegin2D(ElementNode node)
                {
                    _elementNodes++;
                    long instance = node.LinkInstanceId == null ? -1 : Rid.Value(node.LinkInstanceId);
                    long element = node.ElementId == null ? -1 : Rid.Value(node.ElementId);
                    _elements.Push((element, node.Document, instance));
                    return RenderNodeAction.Proceed;
                }
                public void OnElementEnd2D(ElementNode node) => _elements.Pop();
                public RenderNodeAction OnInstanceBegin(InstanceNode node) => Push(node.GetTransform());
                public void OnInstanceEnd(InstanceNode node) => _transforms.Pop();
                public RenderNodeAction OnLinkBegin(LinkNode node)
                {
                    _linkNodes++;
                    Transform local = node.GetTransform();
                    Document linked = node.GetDocument();
                    ElementId type = node.SymbolId;
                    _linkScopes.Push(_linkScopes.Count != 0 ? null :
                        PlanLeaderRules.UniqueExportedLinkInstance(_links.Where(link =>
                            Rid.Value(link.Type) == Rid.Value(type) && OpenGuard.SameDocument(link.Document, linked) &&
                            (link.Local.AlmostEqual(local) || link.Total.AlmostEqual(local))).Select(link => link.Id)));
                    return Push(local);
                }
                public void OnLinkEnd(LinkNode node)
                {
                    _linkScopes.Pop();
                    _transforms.Pop();
                }
                private RenderNodeAction Push(Transform local)
                {
                    _transforms.Push(_transforms.Peek().Multiply(local));
                    return RenderNodeAction.Proceed;
                }
                public RenderNodeAction OnFaceEdge2D(FaceEdgeNode node) => RenderNodeAction.Proceed;
                public RenderNodeAction OnFaceSilhouette2D(FaceSilhouetteNode node) => RenderNodeAction.Proceed;
                public RenderNodeAction OnCurve(CurveNode node) => RenderNodeAction.Proceed;
                public RenderNodeAction OnPolyline(PolylineNode node) => RenderNodeAction.Proceed;
                public void OnLineSegment(LineSegment segment) => Segment(segment.StartPoint, segment.EndPoint);
                public void OnPolylineSegments(PolylineSegments segments)
                {
                    IList<XYZ> points = segments.GetVertices();
                    for (int i = 1; i < points.Count; i++) Segment(points[i - 1], points[i]);
                }
                public void OnText(TextNode node) { }
                public void OnMaterial(MaterialNode node) { }
                public void OnRPC(RPCNode node) { }
                public void OnLight(LightNode node) { }
            }
        }
    }
}
