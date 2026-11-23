// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// What does a leader point at? One ray from the leader end, along the view's own
// looking direction, through the host AND the Revit links (nested links reported
// as such, never guessed), and what the first element it meets is: category, type,
// the layer of its compound structure the ray reaches, and who changed it last.
//
// Shared by horizun_query_planimetry (leader_targets, annotation_staleness) and by
// horizun_planimetry_ops (note_from_model refuses an end that points elsewhere).
//
// Outside plan leader reads, a Revit ray needs a 3D view. This reader never creates one (a read opens no
// transaction): it uses an existing non-template, non-perspective 3D view and, when
// there is none, answers "no_3d_view" for every leader instead of a clean-looking
// empty result.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    internal static partial class LeaderTargetReader
    {
        public static View3D FindView3D(Document doc)
        {
            return new FilteredElementCollector(doc).OfClass(typeof(View3D)).Cast<View3D>()
                .Where(v => !v.IsTemplate && !v.IsPerspective)
                .OrderBy(v => v.IsSectionBoxActive).ThenBy(v => v.Name, StringComparer.Ordinal).FirstOrDefault();
        }

        /// <summary>How far back toward the viewer an orthographic 3D ray starts, in feet (1 km).</summary>
        public const double BackOff3DFeet = 3280.84;

        /// <summary>
        /// The point the ray starts from: the leader end, lifted to the cut plane in a plan. In an orthographic 3D view
        /// (an isometric) the leader end is only a position on the screen: the ray starts back toward the viewer, at the
        /// entry face of the active section box when there is one (ReferenceIntersector does not clip to it), so the
        /// first element it meets is the first one the view shows at that spot. Only when <paramref name="backOff3D"/> is
        /// set for actual leader ends. The general view_ray mode starts at the supplied point.
        /// </summary>
        private static XYZ Origin(Document doc, View view, XYZ end, bool backOff3D = false)
        {
            try
            {
                if (backOff3D && view is View3D v3 && !v3.IsPerspective)
                {
                    XYZ back = end + v3.ViewDirection * BackOff3DFeet;
                    if (!v3.IsSectionBoxActive) return back;
                    BoundingBoxXYZ box = v3.GetSectionBox();
                    Transform inv = box.Transform.Inverse;
                    XYZ o = inv.OfPoint(back), d = inv.OfVector(v3.ViewDirection.Negate());
                    double? t = TextNoteLayoutRules.RayBoxEntry(new[] { o.X, o.Y, o.Z }, new[] { d.X, d.Y, d.Z },
                        new[] { box.Min.X, box.Min.Y, box.Min.Z }, new[] { box.Max.X, box.Max.Y, box.Max.Z });
                    return t.HasValue && t.Value > 0 ? back + v3.ViewDirection.Negate() * t.Value : back;
                }
                if (view is ViewPlan plan && TryCutPlanePoint(doc, plan, end, out XYZ point, allowAssociatedLevelFallback: true)) return point;
            }
            catch { }
            return end;
        }

        public static LeaderHit Cast(Document doc, View view, View3D v3, XYZ end, int maxAlso = 3)
            => CastRay(doc, view, v3, Origin(doc, view, end), maxAlso);

        private static LeaderHit CastRay(Document doc, View view, View3D v3, XYZ origin, int maxAlso = 3)
        {
            var hit = new LeaderHit();
            if (v3 == null) { hit.Coverage = "no_3d_view"; hit.Uncertainty = "the model has no usable 3D view, and a read does not create one"; return hit; }
            hit.Cast = true; hit.Coverage = "complete";
            try
            {
                var ri = new ReferenceIntersector(new ElementIsElementTypeFilter(true),
                    FindReferenceTarget.Element, v3);
                ri.FindReferencesInRevitLinks = true;
                XYZ dir = view.ViewDirection.Negate();
                IList<ReferenceWithContext> refs = ri.Find(origin, dir);
                var seen = new List<(ReferenceWithContext Reference, Element Element, Document Document,
                    RevitLinkInstance OuterLink, Transform ToHost, List<string> LinkPath)>();
                foreach (ReferenceWithContext rc in refs.OrderBy(x => x.Proximity))
                {
                    Reference r = rc.GetReference();
                    Element el; Document ed = doc; RevitLinkInstance inst = null;
                    var transforms = new List<double[,]>();
                    var path = new List<string>();
                    Reference current = r;
                    try
                    {
                        while (current.LinkedElementId != null && current.LinkedElementId != ElementId.InvalidElementId)
                        {
                            var level = ed.GetElement(current.ElementId) as RevitLinkInstance;
                            if (level == null) throw new InvalidOperationException("reference does not identify a link instance");
                            if (inst == null) inst = level;
                            path.Add(level.UniqueId);
                            transforms.Add(Matrix(level.GetTotalTransform()));
                            ed = level.GetLinkDocument();
                            if (ed == null) throw new InvalidOperationException("link is unloaded");
                            current = current.CreateReferenceInLink();
                            if (current == null) throw new InvalidOperationException("link reference is unavailable");
                            if (transforms.Count > 32) throw new InvalidOperationException("link chain exceeds 32 levels");
                        }
                        el = ed.GetElement(current.ElementId);
                        if (el == null || el is RevitLinkInstance) throw new InvalidOperationException("linked target or nested reference is unavailable");
                    }
                    catch (Exception ex)
                    {
                        hit.Coverage = "partial";
                        hit.Uncertainty = "nested reference cannot be resolved: " + ex.Message;
                        return hit; // Do not substitute a farther hit
                    }
                    Transform composed = FromMatrix(LinkChainTransform.Compose(transforms));
                    if (el == null || el is View || el is RevitLinkInstance) continue;
                    if (seen.Any(s => s.Element.Id == el.Id && s.Document == ed && s.LinkPath.SequenceEqual(path))) continue;
                    seen.Add((rc, el, ed, inst, composed, path));
                    if (seen.Count >= 1 + maxAlso) break;
                }
                if (seen.Count == 0)
                {
                    hit.Uncertainty = "the ray met nothing";
                    return hit;
                }
                var first = seen[0];
                Fill(hit, first.Reference, first.Element, first.Document, first.OuterLink, first.ToHost);
                hit.LinkDepth = first.LinkPath.Count;
                hit.LinkPath = first.LinkPath;
                foreach (var s in seen.Skip(1))
                    hit.AlsoHit.Add(s.Element.Category?.Name + " " + Rid.Value(s.Element.Id).ToString(CultureInfo.InvariantCulture) + (s.OuterLink != null ? " (link " + Rid.Value(s.OuterLink.Id) + ")" : ""));
                if (seen.Count > 1 && LinkChainTransform.IsTie(seen[1].Reference.Proximity, first.Reference.Proximity))
                {
                    hit.AmbiguousRay = true;
                    hit.Uncertainty = (hit.Uncertainty == null ? "" : hit.Uncertainty + "; ") + "a second element is within 1 mm along the ray: the target is ambiguous";
                }
            }
            catch (Exception ex)
            {
                hit.Source = null; hit.Coverage = "not_cast"; hit.Uncertainty = "the ray failed: " + ex.Message;
            }
            return hit;
        }

        private const double CutProbeFeet = .01;

        /// <summary>The origin of the ray in a plan, measured from the cut plane's level and offset.</summary>
        private static bool TryCutPlanePoint(Document doc, ViewPlan plan, XYZ end, out XYZ point, bool allowAssociatedLevelFallback = false)
        {
            point = null;
            try
            {
                using (PlanViewRange range = plan.GetViewRange())
                {
                    ElementId levelId = range.GetLevelId(PlanViewPlane.CutPlane);
                    if (levelId == PlanViewRange.Unlimited) return false;
                    Level level = null;
                    if (levelId == PlanViewRange.Current) level = plan.GenLevel;
                    else if (levelId != null && levelId != ElementId.InvalidElementId) level = doc.GetElement(levelId) as Level;
                    else if (allowAssociatedLevelFallback) level = plan.GenLevel;
                    if (level == null) return false;
                    double elevation = level.ProjectElevation + range.GetOffset(PlanViewPlane.CutPlane);
                    if (double.IsNaN(elevation) || double.IsInfinity(elevation)) return false;
                    point = new XYZ(end.X, end.Y, elevation);
                    return true;
                }
            }
            catch { return false; }
        }

        private static IEnumerable<GeometryObject> GeometryParts(GeometryElement geometry)
        {
            foreach (GeometryObject item in geometry)
            {
                if (item is GeometryInstance instance)
                {
                    GeometryElement inner = instance.GetInstanceGeometry();
                    if (inner == null) throw new InvalidOperationException("instance geometry is unavailable");
                    foreach (GeometryObject nested in GeometryParts(inner)) yield return nested;
                }
                else yield return item;
            }
        }

        private static IEnumerable<Solid> Solids(GeometryElement geometry)
            => GeometryParts(geometry).OfType<Solid>().Where(solid => solid.Faces.Size > 0);

        // A face touch does not prove containment. Report an unreadable solid separately from a measured miss.
        private static bool? SolidContains(GeometryElement geometry, Transform fromHost, XYZ point)
        {
            var ends = TextNoteLayoutRules.ProbeInElementSpace(point + XYZ.BasisZ * CutProbeFeet,
                point - XYZ.BasisZ * CutProbeFeet, fromHost.OfPoint);
            XYZ localPoint = fromHost.OfPoint(point);
            Line probe = Line.CreateBound(ends.Start, ends.End);
            using (var inside = new SolidCurveIntersectionOptions { ResultType = SolidCurveIntersectionMode.CurveSegmentsInside })
            {
                bool unknown = false;
                foreach (Solid solid in Solids(geometry))
                {
                    try
                    {
                        if (solid.Volume <= 0) continue;
                        using (SolidCurveIntersection result = solid.IntersectWithCurve(probe, inside))
                        {
                            if (result == null) { unknown = true; continue; }
                            for (int i = 0; i < result.SegmentCount; i++)
                            {
                                Curve segment = result.GetCurveSegment(i);
                                if (segment == null) { unknown = true; continue; }
                                if (!TextNoteLayoutRules.SegmentProvesInside(segment.Length, probe.Length, segment.Distance(localPoint))) continue;
                                // The API also returns segments that run along a solid face.
                                if (!solid.Faces.Cast<Face>().Any(face =>
                                    face.Project(localPoint)?.Distance <= TextNoteLayoutRules.PlanStrokeToleranceFeet)) return true;
                            }
                        }
                    }
                    catch { unknown = true; }
                }
                return unknown ? (bool?)null : false;
            }
        }

        // Each sample is in the owning document's coordinates. The caller projects it onto the host view.
        internal static IEnumerable<XYZ> GeometryAimPoints(Element element, View view, bool linked)
        {
            GeometryElement geometry = element.get_Geometry(PlanGeometryOptions(view, linked));
            if (geometry == null) yield break;
            if (!linked && view is ViewPlan && IsDoor(element))
                foreach (Curve curve in DoorStrokes(element, view, geometry))
                    foreach (double fraction in new[] { .5, .25, .75, .1, .9 })
                        yield return curve.Evaluate(fraction, true);
            foreach (Solid solid in Solids(geometry))
                foreach (Face face in solid.Faces)
                {
                    Mesh mesh = face.Triangulate();
                    for (int i = 0; i < mesh.NumTriangles; i++)
                    {
                        MeshTriangle triangle = mesh.get_Triangle(i);
                        yield return (triangle.get_Vertex(0) + triangle.get_Vertex(1) + triangle.get_Vertex(2)) / 3;
                    }
                }
        }

        private static LeaderHit PlanHit(PlanCandidate candidate, XYZ point)
        {
            Element el = candidate.Element;
            LeaderHit hit = PlanHitIdentity(el, candidate.Link, candidate.Proof, candidate.Distance, depthMeasured: true, out ElementType type);
            ResolveLayer(hit, el, hit.ElementDocument, type as HostObjAttributes, candidate.FromHost.OfPoint(point));
            return hit;
        }

        private static double[,] Matrix(Transform t) => new double[,] {
            { t.BasisX.X, t.BasisY.X, t.BasisZ.X, t.Origin.X },
            { t.BasisX.Y, t.BasisY.Y, t.BasisZ.Y, t.Origin.Y },
            { t.BasisX.Z, t.BasisY.Z, t.BasisZ.Z, t.Origin.Z },
            { 0, 0, 0, 1 } };

        private static Transform FromMatrix(double[,] m)
        {
            Transform t = Transform.Identity;
            t.BasisX = new XYZ(m[0,0], m[1,0], m[2,0]);
            t.BasisY = new XYZ(m[0,1], m[1,1], m[2,1]);
            t.BasisZ = new XYZ(m[0,2], m[1,2], m[2,2]);
            t.Origin = new XYZ(m[0,3], m[1,3], m[2,3]);
            return t;
        }

        private static void Fill(LeaderHit hit, ReferenceWithContext rc, Element el, Document ed, RevitLinkInstance inst, Transform toHost)
        {
            hit.Source = inst == null ? "host" : "link";
            hit.Element = el; hit.ElementDocument = ed;
            hit.ElementId = Rid.Value(el.Id);
            try { hit.UniqueId = el.UniqueId; } catch { }
            if (inst != null) { hit.LinkInstanceId = Rid.Value(inst.Id); try { hit.LinkInstanceUniqueId = inst.UniqueId; } catch { } }
            hit.Category = el.Category?.Name;
            hit.DistanceFeet = rc.Proximity;
            ElementType type = ed.GetElement(el.GetTypeId()) as ElementType;
            hit.TypeName = type == null ? null : (type.FamilyName + " : " + type.Name);
            try
            {
                if (ed.IsWorkshared) hit.LastChangedBy = WorksharingUtils.GetWorksharingTooltipInfo(ed, el.Id)?.LastChangedBy;
            }
            catch { }
            XYZ point = rc.GetReference().GlobalPoint;
            if (inst != null) point = toHost.Inverse.OfPoint(point);
            ResolveLayer(hit, el, ed, type as HostObjAttributes, point);
        }

        public static IList<NoteLeaderRules.Layer> Layers(Document ed, HostObjAttributes type)
        {
            var list = new List<NoteLeaderRules.Layer>();
            CompoundStructure cs = type?.GetCompoundStructure();
            if (cs == null) return list;
            foreach (CompoundStructureLayer l in cs.GetLayers())
            {
                var m = l.MaterialId != ElementId.InvalidElementId ? ed.GetElement(l.MaterialId) as Material : null;
                list.Add(new NoteLeaderRules.Layer { Material = m?.Name, ThicknessMm = l.Width * 304.8, Function = l.Function.ToString() });
            }
            return list;
        }

        /// <summary>Which layer of the compound structure the point is in, measured from the first (outside / top) face.</summary>
        public static void ResolveLayer(LeaderHit hit, Element el, Document ed, HostObjAttributes type, XYZ localPoint)
        {
            IList<NoteLeaderRules.Layer> layers = Layers(ed, type);
            if (layers.Count == 0) { hit.LayerResolution = "no_compound_structure"; return; }
            try
            {
                Face face = null;
                if (el is Wall wall)
                {
                    IList<Reference> faces = HostObjectUtils.GetSideFaces(wall, ShellLayerType.Exterior);
                    if (faces != null && faces.Count > 0) face = wall.GetGeometryObjectFromReference(faces[0]) as Face;
                }
                else if (el is Floor floor)
                {
                    IList<Reference> faces = HostObjectUtils.GetTopFaces(floor);
                    if (faces != null && faces.Count > 0) face = floor.GetGeometryObjectFromReference(faces[0]) as Face;
                }
                else if (el is RoofBase roof)
                {
                    IList<Reference> faces = HostObjectUtils.GetTopFaces(roof);
                    if (faces != null && faces.Count > 0) face = roof.GetGeometryObjectFromReference(faces[0]) as Face;
                }
                if (face == null) { hit.LayerResolution = "core_default"; PickCore(hit, layers); return; }
                IntersectionResult proj = face.Project(localPoint);
                if (proj == null) { hit.LayerResolution = "core_default"; PickCore(hit, layers); return; }
                int idx = NoteLeaderRules.LayerAtDepth(layers.Select(l => l.ThicknessMm).ToList(), proj.Distance * 304.8);
                if (idx < 0) { hit.LayerResolution = "outside_stack"; hit.Uncertainty = (hit.Uncertainty == null ? "" : hit.Uncertainty + "; ") + "the point is farther from the first face than the stack is thick"; PickCore(hit, layers); return; }
                hit.LayerIndex = idx; hit.LayerMaterial = layers[idx].Material; hit.LayerResolution = "measured_from_face";
            }
            catch (Exception ex) { hit.LayerResolution = "unresolved: " + ex.Message; PickCore(hit, layers); }
        }

        private static void PickCore(LeaderHit hit, IList<NoteLeaderRules.Layer> layers)
        {
            int core = -1; double thickest = -1;
            for (int i = 0; i < layers.Count; i++)
                if (layers[i].Function == "Structure" && layers[i].ThicknessMm > thickest) { core = i; thickest = layers[i].ThicknessMm; }
            if (core < 0) { core = 0; }
            hit.LayerIndex = core; hit.LayerMaterial = layers[core].Material;
        }
    }

    /// <summary>The origin a note generated from the model carries: the element it was written from, and what it said.</summary>
    internal static class NoteOrigin
    {
        public static readonly Guid SchemaGuid = new Guid("3b7f4a52-91c6-4d28-8e0a-6c5d1f2a9b47");
        public const string SchemaName = "HorizunNoteOriginV1";
        private const string FUid = "SourceUniqueId", FLink = "LinkInstanceUniqueId", FSource = "Source", FSig = "Signature", FVersion = "SchemaVersion", FTypeName = "SourceTypeName";
        private static Schema _cached;

        private static Schema GetOrCreate()
        {
            if (_cached != null && _cached.IsValidObject) return _cached;
            Schema existing = Schema.Lookup(SchemaGuid);
            if (existing != null) { _cached = existing; return _cached; }
            var b = new SchemaBuilder(SchemaGuid);
            b.SetSchemaName(SchemaName);
            b.SetReadAccessLevel(AccessLevel.Public);
            b.SetWriteAccessLevel(AccessLevel.Vendor);
            b.SetVendorId(HorizunVendor.Id);
            b.SetDocumentation("Horizun note generated from the model: the element it was written from and a signature of its text, to detect a stale note.");
            b.AddSimpleField(FVersion, typeof(int));
            foreach (string f in new[] { FUid, FLink, FSource, FSig, FTypeName }) b.AddSimpleField(f, typeof(string));
            _cached = b.Finish();
            return _cached;
        }

        public static void Write(Element note, string sourceUniqueId, string linkUniqueId, string source, string signature, string typeName)
        {
            var e = new Entity(GetOrCreate());
            e.Set(FVersion, 1);
            e.Set(FUid, sourceUniqueId ?? "");
            e.Set(FLink, linkUniqueId ?? "");
            e.Set(FSource, source ?? "");
            e.Set(FSig, signature ?? "");
            e.Set(FTypeName, typeName ?? "");
            note.SetEntity(e);
        }

        public sealed class Record { public string SourceUniqueId, LinkInstanceUniqueId, Source, Signature, SourceTypeName; }

        public static Record Read(Element note)
        {
            try
            {
                Schema s = Schema.Lookup(SchemaGuid);
                if (s == null || note == null) return null;
                Entity en = note.GetEntity(s);
                if (en == null || !en.IsValid()) return null;
                return new Record
                {
                    SourceUniqueId = Nz(en.Get<string>(FUid)), LinkInstanceUniqueId = Nz(en.Get<string>(FLink)),
                    Source = Nz(en.Get<string>(FSource)), Signature = Nz(en.Get<string>(FSig)), SourceTypeName = Nz(en.Get<string>(FTypeName))
                };
            }
            catch { return null; }
        }

        private static string Nz(string s) => string.IsNullOrEmpty(s) ? null : s;
    }
}
