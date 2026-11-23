// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// horizun_measure_geometry (read-only): solid volume, section profile (width x height
// perpendicular to the axis), bounding box, end points and face normals of explicit
// elements - or of a selector - without a Python script. The arithmetic lives in
// Core/GeometryMeasureRules (tested without Revit); this file only reads the model.
//
// THE CONTRACT: a value that cannot be read is null, the element is listed in `unreadable`
// with the measure and the reason, and coverage_complete is false. It is NEVER 0.
// -----------------------------------------------------------------------------
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
    public sealed class MeasureGeometryCommand : ICommand
    {
        public string Name => "horizun_measure_geometry";
        public string Description => "Read-only geometry measures (volume, section profile, bounding box, end points, face normals) with unreadable elements named, never zero.";

        private const int MaxFacesPerElement = 60;
        private const int MaxEdgesPerElement = 120;

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            JObject request;
            try { request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JsonText.ParseObject(paramsJson); }
            catch (JsonException ex) { return CommandResult.FailCoded(ErrorCodes.EsquemaInvalido, "Parameters must be a JSON object: " + ex.Message); }
            Document doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.FailCoded(ErrorCodes.RevitSinDocumento, "No active Revit document.");
            CommandResult guard = DocumentGate.ReadGuard(doc, request, Name);
            if (guard != null) return guard;
            if (!GeometryMeasureRules.TryParse(request, out MeasureRequest req, out string error))
                return CommandResult.FailCoded(ErrorCodes.EsquemaInvalido, error);

            var unreadable = new JArray();
            var ids = new List<long>(req.Ids);
            bool truncated = false;
            int selectorUnknown = 0;
            var linked = new List<LinkedPick>();
            if (req.Selection)
            {
                // The CURRENT selection, read now - not a selection the caller remembers.
                ICollection<ElementId> picked;
                try { picked = app.ActiveUIDocument.Selection.GetElementIds(); }
                catch (Exception ex) { return CommandResult.FailCoded(ErrorCodes.EsquemaInvalido, "The current selection could not be read: " + ex.Message); }
                // Elements picked INSIDE a link travel as references (Revit 2023+) and are NOT in
                // GetElementIds: a person who selected a linked wall was told "nothing is selected"
                // (live, Revit 2025). They are measured in their link document and carried into
                // host coordinates, like horizun_selection_context does.
                linked = ReadLinkedSelection(app.ActiveUIDocument, doc, unreadable);
                if ((picked == null || picked.Count == 0) && linked.Count == 0)
                    return CommandResult.FailCoded(ErrorCodes.EsquemaInvalido,
                        "selection=true but nothing is selected in '" + doc.Title + "' (active view: '" + SafeViewName(app) +
                        "'). Ask the person to select the elements, or send ids.");
                ids = (picked ?? new List<ElementId>()).Select(Rid.Value).Distinct().ToList();
                if (ids.Count + linked.Count > GeometryMeasureRules.MaxIds)
                {
                    int room = Math.Max(0, GeometryMeasureRules.MaxIds - ids.Count);
                    if (ids.Count > GeometryMeasureRules.MaxIds) ids = ids.Take(GeometryMeasureRules.MaxIds).ToList();
                    linked = linked.Take(room).ToList();
                    truncated = true;
                }
            }
            if (req.Selector != null)
            {
                SelectorResolution found = SelectorResolver.Resolve(doc, req.Selector, GeometryMeasureRules.MaxSelected);
                ids = found.Matched;
                truncated = found.Truncated;
                selectorUnknown = found.Unknown.Count;
                foreach (long id in found.Unknown.Take(50))
                    unreadable.Add(Unread(id, "selector", UnreadableReason.ReadException));
            }

            int total = ids.Count + linked.Count;
            int? nextOffset = null;
            if (req.Measures.Contains(MeasureKinds.Summary))
            {
                ids = ids.OrderBy(x => x).ToList();
                linked = linked.OrderBy(x => Rid.Value(x.Link.Id)).ThenBy(x => Rid.Value(x.Element.Id)).ToList();
                var page = GeometryMeasureRules.Page(total, req.Offset, req.PageSize);
                int hostCount = ids.Count;
                ids = ids.Skip(page.Start).Take(page.Count).ToList();
                linked = linked.Skip(Math.Max(0, page.Start - hostCount)).Take(Math.Max(0, page.Count - ids.Count)).ToList();
                nextOffset = page.Next;
            }
            JArray rows = MeasureAll(doc, ids, req, unreadable);
            foreach (LinkedPick p in linked)
            {
                var local = new JArray();
                foreach (JObject row in MeasureAll(p.Document, new List<long> { Rid.Value(p.Element.Id) }, req, local, p.ToHost).OfType<JObject>())
                {
                    row["source"] = "link";
                    row["link_instance_id"] = Rid.Value(p.Link.Id);
                    row["link_document"] = SafeTitle(p.Document);
                    row["coordinates"] = "host";
                    rows.Add(row);
                }
                foreach (JObject u in local.OfType<JObject>()) { u["link_instance_id"] = Rid.Value(p.Link.Id); unreadable.Add(u); }
            }

            bool complete = unreadable.Count == 0 && !truncated && !nextOffset.HasValue;
            var result = new JObject
            {
                ["target_document"] = doc.Title,
                ["unit"] = req.Unit,
                ["volume_unit"] = "m3",
                ["area_unit"] = "m2",
                ["measures"] = new JArray(req.Measures),
                ["requested"] = ids.Count + linked.Count,
                ["measured"] = rows.Count,
                ["elements"] = rows,
                ["unreadable_total"] = unreadable.Count,
                ["unreadable"] = new JArray(unreadable.Take(200)),
                ["coverage_complete"] = complete
            };
            if (req.Measures.Contains(MeasureKinds.Summary))
            {
                result["page"] = new JObject { ["offset"] = req.Offset, ["page_size"] = req.PageSize, ["total"] = total, ["next_offset"] = nextOffset.HasValue ? new JValue(nextOffset.Value) : JValue.CreateNull() };
                result["summary_units"] = new JObject { ["length"] = "mm", ["area"] = "mm2", ["volume"] = "mm3" };
            }
            if (req.Selection)
            {
                result["source"] = "selection";
                result["selection_truncated"] = truncated;
                result["linked_selected"] = linked.Count;
            }
            if (req.Selector != null)
            {
                result["selector_truncated"] = truncated;
                result["selector_unknown"] = selectorUnknown;
            }
            result["note"] = "A measure that could not be read is null and named in unreadable with its reason; it is never 0. " +
                             "section_profile width runs along axis x vertical (horizontal across a horizontal member), height is perpendicular to both. " +
                             "A row with source=link is an element picked inside a link: element_id is its id IN link_document and every " +
                             "coordinate, normal and box is carried into host coordinates through that link instance's transform.";
            return CommandResult.Ok(result);
        }

        private sealed class LinkedPick { public Element Element; public Document Document; public RevitLinkInstance Link; public Transform ToHost; }

        private static List<LinkedPick> ReadLinkedSelection(UIDocument uidoc, Document doc, JArray unreadable)
        {
            var list = new List<LinkedPick>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            IList<Reference> refs = null;
            try { refs = uidoc.Selection.GetReferences(); }
            catch (Exception ex) { unreadable.Add(new JObject { ["element_id"] = null, ["measure"] = "linked_selection", ["reason"] = UnreadableReason.ReadException, ["exception"] = ex.GetType().Name }); }
            foreach (Reference r in refs ?? new List<Reference>())
            {
                try
                {
                    if (r == null || r.LinkedElementId == ElementId.InvalidElementId) continue;
                    var link = doc.GetElement(r.ElementId) as RevitLinkInstance;
                    Document linkDoc = link?.GetLinkDocument();
                    Element le = linkDoc?.GetElement(r.LinkedElementId);
                    if (le == null)
                    {
                        JObject u = Unread(Rid.Value(r.LinkedElementId), "*", "link_not_loaded_or_element_not_found");
                        u["link_instance_id"] = Rid.Value(r.ElementId);
                        unreadable.Add(u);
                        continue;
                    }
                    if (!seen.Add(Rid.Value(link.Id) + ":" + Rid.Value(le.Id))) continue;
                    list.Add(new LinkedPick { Element = le, Document = linkDoc, Link = link, ToHost = link.GetTotalTransform() });
                }
                catch (Exception ex) { unreadable.Add(new JObject { ["element_id"] = null, ["measure"] = "linked_selection", ["reason"] = UnreadableReason.ReadException, ["exception"] = ex.GetType().Name }); }
            }
            return list;
        }

        private static string SafeTitle(Document d)
        {
            try { return d?.Title; } catch { return null; }
        }

        private static string SafeViewName(UIApplication app)
        {
            try { return app.ActiveUIDocument?.ActiveView?.Name; } catch { return null; }
        }

        /// <summary>One row per id with every requested measure; what cannot be read goes to unreadable.</summary>
        internal static JArray MeasureAll(Document doc, List<long> ids, MeasureRequest req, JArray unreadable, Transform toHost = null)
        {
            // toHost: an element read in a LINK document is measured there and every point,
            // vector and solid is carried into host coordinates (null = already host).
            if (toHost != null && toHost.IsIdentity) toHost = null;
            var rows = new JArray();
            foreach (long id in ids)
            {
                Element e = Rid.CanRepresent(id) ? doc.GetElement(Rid.Make(id)) : null;
                if (e == null)
                {
                    unreadable.Add(Unread(id, "*", UnreadableReason.ElementNotFound));
                    continue;
                }
                var row = new JObject { ["element_id"] = id, ["unique_id"] = e.UniqueId };
                try
                {
                    row["category"] = e.Category?.Name; row["built_in_category"] = CategoryKey.Of(e);
                    Element type = doc.GetElement(e.GetTypeId());
                    row["family"] = (type as ElementType)?.FamilyName;
                    row["type"] = type?.Name;
                }
                catch { }
                List<Solid> solids = null;
                bool solidsRead = false;
                Func<List<Solid>> getSolids = () =>
                {
                    if (!solidsRead) { solids = ToHost(ReadSolids(e), toHost); solidsRead = true; }
                    return solids;
                };
                foreach (string measure in req.Measures)
                {
                    try { row[measure] = Measure(measure, e, req, getSolids, toHost, out string reason); if (reason != null) unreadable.Add(Unread(id, measure, reason)); }
                    catch (Exception ex)
                    {
                        row[measure] = JValue.CreateNull();
                        unreadable.Add(Unread(id, measure, UnreadableReason.ReadException, ex.GetType().Name));
                    }
                }
                rows.Add(row);
            }
            return rows;
        }

        internal static JObject Unread(long id, string measure, string reason, string exception = null)
        {
            var o = new JObject { ["element_id"] = id, ["measure"] = measure, ["reason"] = reason };
            if (exception != null) o["exception"] = exception;
            return o;
        }

        private static JToken Measure(string measure, Element e, MeasureRequest req, Func<List<Solid>> solids, Transform toHost, out string reason)
        {
            reason = null;
            Func<XYZ, XYZ> P = q => toHost == null ? q : toHost.OfPoint(q);
            Func<XYZ, XYZ> V = q => toHost == null ? q : toHost.OfVector(q);
            double k = req.LengthFromFeet;
            switch (measure)
            {
                case MeasureKinds.Summary:
                {
                    var mmRequest = new MeasureRequest { LengthFromFeet = 304.8 };
                    JToken box = Measure(MeasureKinds.BoundingBox, e, mmRequest, solids, toHost, out string boxReason);
                    // Areas and volumes are invariant under Revit link placement, including reflection.
                    // Do not transform solids here: CreateTransformed rejects reflected transforms.
                    List<Solid> ss = ReadSolids(e);
                    JToken endpoints = JValue.CreateNull();
                    if (e.Location is LocationCurve lc && lc.Curve != null && lc.Curve.IsBound)
                        endpoints = Measure(MeasureKinds.EndPoints, e, mmRequest, solids, toHost, out _);
                    if (boxReason != null || ss.Count == 0) reason = boxReason ?? UnreadableReason.NoSolidGeometry;
                    double area = 0; int faceCount = 0;
                    foreach (Solid solid in ss) foreach (Face face in solid.Faces) { area += face.Area; faceCount++; }
                    return new JObject {
                        ["bounding_box"] = box,
                        ["volume_mm3"] = ss.Count == 0 ? JValue.CreateNull() : new JValue(GeometryMeasureRules.CubicMillimetres(ss.Sum(x => x.Volume))),
                        ["face_area_mm2"] = ss.Count == 0 ? JValue.CreateNull() : new JValue(GeometryMeasureRules.SquareMillimetres(area)),
                        ["solid_count"] = ss.Count == 0 ? JValue.CreateNull() : new JValue(ss.Count),
                        ["face_count"] = ss.Count == 0 ? JValue.CreateNull() : new JValue(faceCount),
                        ["end_points"] = endpoints
                    };
                }
                case MeasureKinds.SolidVolume:
                {
                    List<Solid> s = solids();
                    if (s.Count == 0) { reason = UnreadableReason.NoSolidGeometry; return JValue.CreateNull(); }
                    return new JObject
                    {
                        ["volume_m3"] = Math.Round(s.Sum(x => x.Volume) * GeometryMeasureRules.CubicMetresPerCubicFoot, 6),
                        ["solids"] = s.Count
                    };
                }
                case MeasureKinds.BoundingBox:
                {
                    BoundingBoxXYZ bb = e.get_BoundingBox(null);
                    if (bb == null) { reason = UnreadableReason.NoBoundingBox; return JValue.CreateNull(); }
                    double[] min, max;
                    GeometryMeasureRules.HostEnvelope(new[] { bb.Min.X, bb.Min.Y, bb.Min.Z }, new[] { bb.Max.X, bb.Max.Y, bb.Max.Z },
                        c => { XYZ q = P(bb.Transform.OfPoint(new XYZ(c[0], c[1], c[2]))); return new[] { q.X, q.Y, q.Z }; }, out min, out max);
                    return new JObject
                    {
                        ["min"] = Vec(min, k), ["max"] = Vec(max, k),
                        ["size"] = Vec(new[] { max[0] - min[0], max[1] - min[1], max[2] - min[2] }, k)
                    };
                }
                case MeasureKinds.EndPoints:
                {
                    Location loc = e.Location;
                    if (loc is LocationCurve lc && lc.Curve != null && lc.Curve.IsBound)
                    {
                        XYZ p0 = P(lc.Curve.GetEndPoint(0)), p1 = P(lc.Curve.GetEndPoint(1));
                        return new JObject
                        {
                            ["start"] = Vec(new[] { p0.X, p0.Y, p0.Z }, k), ["end"] = Vec(new[] { p1.X, p1.Y, p1.Z }, k),
                            ["length"] = Math.Round(lc.Curve.Length * k, 3)
                        };
                    }
                    if (loc is LocationPoint lp)
                    {
                        XYZ q = P(lp.Point);
                        return new JObject { ["point"] = Vec(new[] { q.X, q.Y, q.Z }, k) };
                    }
                    reason = UnreadableReason.NoLocation; return JValue.CreateNull();
                }
                case MeasureKinds.SectionProfile:
                {
                    double[] axis = Axis(e);
                    if (axis == null) { reason = UnreadableReason.NoAxis; return JValue.CreateNull(); }
                    if (toHost != null) { XYZ ha = V(new XYZ(axis[0], axis[1], axis[2])); axis = new[] { ha.X, ha.Y, ha.Z }; }
                    List<Solid> s = solids();
                    if (s.Count == 0) { reason = UnreadableReason.NoSolidGeometry; return JValue.CreateNull(); }
                    SectionProfileResult p = GeometryMeasureRules.SectionProfile(Vertices(s), axis);
                    if (!p.Ok) { reason = p.Reason; return JValue.CreateNull(); }
                    return new JObject
                    {
                        ["width"] = Math.Round(p.WidthFeet * k, 3), ["height"] = Math.Round(p.HeightFeet * k, 3),
                        ["length"] = Math.Round(p.LengthFeet * k, 3), ["axis"] = Vec(axis, 1),
                        ["method"] = "extent of the solid vertices perpendicular to the axis"
                    };
                }
                case MeasureKinds.FaceNormals:
                {
                    List<Solid> s = solids();
                    if (s.Count == 0) { reason = UnreadableReason.NoSolidGeometry; return JValue.CreateNull(); }
                    var faces = new List<KeyValuePair<double[], double>>();
                    int nonPlanar = 0;
                    foreach (Solid solid in s)
                        foreach (Face f in solid.Faces)
                            if (f is PlanarFace pf) faces.Add(new KeyValuePair<double[], double>(new[] { pf.FaceNormal.X, pf.FaceNormal.Y, pf.FaceNormal.Z }, pf.Area));
                            else nonPlanar++;
                    if (faces.Count == 0) { reason = UnreadableReason.NoPlanarFaces; return JValue.CreateNull(); }
                    var groups = new JArray();
                    foreach (NormalGroup g in GeometryMeasureRules.GroupNormals(faces).Take(24))
                        groups.Add(new JObject
                        {
                            ["normal"] = Vec(g.Normal, 1), ["area_m2"] = Math.Round(g.AreaSquareFeet * GeometryMeasureRules.SquareMetresPerSquareFoot, 6),
                            ["faces"] = g.Faces
                        });
                    return new JObject { ["groups"] = groups, ["non_planar_faces"] = nonPlanar };
                }
                case MeasureKinds.Faces:
                {
                    List<Solid> s = solids();
                    if (s.Count == 0) { reason = UnreadableReason.NoSolidGeometry; return JValue.CreateNull(); }
                    var list = new JArray();
                    int total = 0;
                    foreach (Solid solid in s)
                        foreach (Face f in solid.Faces)
                        {
                            total++;
                            if (list.Count >= MaxFacesPerElement) continue;
                            var row = new JObject
                            {
                                ["index"] = total - 1,
                                ["planar"] = f is PlanarFace,
                                ["area_m2"] = Math.Round(f.Area * GeometryMeasureRules.SquareMetresPerSquareFoot, 6)
                            };
                            try
                            {
                                BoundingBoxUV uv = f.GetBoundingBox();
                                UV mid = new UV((uv.Min.U + uv.Max.U) / 2, (uv.Min.V + uv.Max.V) / 2);
                                PlanarFace planar = f as PlanarFace;
                                XYZ n = planar != null ? planar.FaceNormal : f.ComputeNormal(mid);
                                XYZ p = planar != null ? planar.Origin : f.Evaluate(mid);
                                row["normal"] = Vec(new[] { n.X, n.Y, n.Z }, 1);
                                row["point"] = Vec(new[] { p.X, p.Y, p.Z }, k);
                                row["point_is"] = planar != null ? "plane origin" : "surface at the middle of its UV box";
                            }
                            catch { row["normal"] = JValue.CreateNull(); }
                            try { row["material_id"] = Rid.Value(f.MaterialElementId); } catch { }
                            list.Add(row);
                        }
                    return new JObject { ["count"] = total, ["truncated"] = total > list.Count, ["faces"] = list };
                }
                case MeasureKinds.Edges:
                {
                    List<Solid> s = solids();
                    if (s.Count == 0) { reason = UnreadableReason.NoSolidGeometry; return JValue.CreateNull(); }
                    var list = new JArray();
                    int total = 0;
                    double sum = 0;
                    foreach (Solid solid in s)
                        foreach (Edge edge in solid.Edges)
                        {
                            total++;
                            Curve c = edge.AsCurve();
                            double len = c != null ? c.Length : edge.ApproximateLength;
                            sum += len;
                            if (list.Count >= MaxEdgesPerElement || c == null || !c.IsBound) continue;
                            XYZ a = c.GetEndPoint(0), b = c.GetEndPoint(1);
                            list.Add(new JObject
                            {
                                ["kind"] = c is Line ? "line" : c is Arc ? "arc" : c.GetType().Name.ToLowerInvariant(),
                                ["length"] = Math.Round(len * k, 3),
                                ["start"] = Vec(new[] { a.X, a.Y, a.Z }, k), ["end"] = Vec(new[] { b.X, b.Y, b.Z }, k)
                            });
                        }
                    return new JObject { ["count"] = total, ["total_length"] = Math.Round(sum * k, 3), ["truncated"] = total > list.Count, ["edges"] = list };
                }
                case MeasureKinds.Location:
                {
                    Location loc = e.Location;
                    if (loc is LocationPoint lp)
                    {
                        JToken rot;
                        try
                        {
                            double local = lp.Rotation * 180.0 / Math.PI;
                            rot = toHost == null ? Math.Round(local, 4) : GeometryMeasureRules.HostRotationDegrees(local, toHost.BasisX.X, toHost.BasisX.Y);
                        }
                        catch { rot = JValue.CreateNull(); }
                        XYZ q = P(lp.Point);
                        return new JObject
                        {
                            ["kind"] = "point", ["point"] = Vec(new[] { q.X, q.Y, q.Z }, k), ["rotation_deg"] = rot
                        };
                    }
                    if (loc is LocationCurve lc && lc.Curve != null)
                    {
                        Curve c = lc.Curve;
                        var o = new JObject { ["kind"] = "curve", ["curve"] = c is Line ? "line" : c is Arc ? "arc" : c.GetType().Name.ToLowerInvariant() };
                        if (c.IsBound)
                        {
                            XYZ p0 = P(c.GetEndPoint(0)), p1 = P(c.GetEndPoint(1));
                            o["start"] = Vec(new[] { p0.X, p0.Y, p0.Z }, k);
                            o["end"] = Vec(new[] { p1.X, p1.Y, p1.Z }, k);
                            o["length"] = Math.Round(c.Length * k, 3);
                        }
                        if (c is Line line) { XYZ d = V(line.Direction); o["direction"] = Vec(new[] { d.X, d.Y, d.Z }, 1); }
                        if (c is Arc arc) { XYZ ac = P(arc.Center); o["center"] = Vec(new[] { ac.X, ac.Y, ac.Z }, k); o["radius"] = Math.Round(arc.Radius * k, 3); }
                        return o;
                    }
                    reason = UnreadableReason.NoLocation; return JValue.CreateNull();
                }
            }
            reason = UnreadableReason.ReadException;
            return JValue.CreateNull();
        }

        private static JArray Vec(double[] v, double k) =>
            new JArray(Math.Round(v[0] * k, 4), Math.Round(v[1] * k, 4), Math.Round(v[2] * k, 4));

        private static double[] Axis(Element e)
        {
            if (e.Location is LocationCurve lc && lc.Curve is Line line && line.IsBound)
            {
                XYZ d = line.GetEndPoint(1) - line.GetEndPoint(0);
                return d.GetLength() < 1e-9 ? null : new[] { d.X, d.Y, d.Z };
            }
            if (e.Location is LocationPoint && e is FamilyInstance fi && fi.StructuralType == Autodesk.Revit.DB.Structure.StructuralType.Column)
                return new[] { 0.0, 0.0, 1.0 };
            return null;
        }

        private static List<double[]> Vertices(List<Solid> solids)
        {
            var pts = new List<double[]>();
            foreach (Solid s in solids)
                foreach (Edge edge in s.Edges)
                {
                    Curve c = edge.AsCurve();
                    if (c is Line) { XYZ a = c.GetEndPoint(0), b = c.GetEndPoint(1); pts.Add(new[] { a.X, a.Y, a.Z }); pts.Add(new[] { b.X, b.Y, b.Z }); }
                    else foreach (XYZ p in edge.Tessellate()) pts.Add(new[] { p.X, p.Y, p.Z });
                }
            return pts;
        }

        /// <summary>Solids of a LINKED element in host coordinates (volume is unchanged; faces, edges and normals move).</summary>
        private static List<Solid> ToHost(List<Solid> solids, Transform toHost)
        {
            if (toHost == null) return solids;
            return solids.Select(s => SolidUtils.CreateTransformed(s, toHost)).ToList();
        }

        private static List<Solid> ReadSolids(Element e)
        {
            var found = new List<Solid>();
            var opt = new Options { DetailLevel = ViewDetailLevel.Fine, ComputeReferences = false, IncludeNonVisibleObjects = false };
            GeometryElement ge = e.get_Geometry(opt);
            if (ge != null) Collect(ge, found);
            return found;
        }

        private static void Collect(GeometryElement ge, List<Solid> into)
        {
            foreach (GeometryObject o in ge)
            {
                if (o is Solid s) { if (s.Volume > 1e-9 && s.Faces.Size > 0) into.Add(s); }
                else if (o is GeometryInstance gi) Collect(gi.GetInstanceGeometry(), into);
            }
        }
    }
}
