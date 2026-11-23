// -----------------------------------------------------------------------------
// Horizun Revit MCP - slab shape editing on floors and roofs.
//
// THE API MOVED TWICE, measured from each year's RevitAPI.xml:
//   2023       Floor/RoofBase.SlabShapeEditor (property); DrawPoint, DrawSplitLine.
//   2024       GetSlabShapeEditor() added beside the property; DrawPoint, DrawSplitLine.
//   2025       AddPoint, AddSplitLine added; DrawPoint/DrawSplitLine kept (obsolete).
//   2026-2027  the property and DrawPoint/DrawSplitLine are gone.
// So the editor and the two creation calls are guarded per year below; everything
// else (Enable, ModifySubElement, ResetSlabShape, the vertex and crease arrays) is
// the same in all five.
//
// SlabShapeVertex.Position.Z is not documented as absolute or relative to the
// slab top. The verification measures both readings and reports which one held
// (ArchitecturalEditRules.SlabVertexConvention) instead of assuming one.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public sealed partial class SlabShapeCommand : ICommand
    {
        public string Name => "horizun_slab_shape";
        public string Description => "Read and edit a floor or roof's slab shape (points, split lines, vertex/crease offsets, reset), re-reading every vertex elevation.";

        private const double XyTol = 1.0 / 304.8;

        public CommandResult Execute(UIApplication app, string paramsJson) =>
            ModelEditRunner.Run(app, paramsJson, Name, "Horizun: slab shape", ArchitecturalEditRules.ValidateSlabShape, Plan);

        // ---- the per-year API ----------------------------------------------------------------

        internal static SlabShapeEditor Editor(Element e)
        {
#if REVIT2023
            if (e is Floor f) return f.SlabShapeEditor;
            if (e is RoofBase r) return r.SlabShapeEditor;
#else
            if (e is Floor f) return f.GetSlabShapeEditor();
            if (e is RoofBase r) return r.GetSlabShapeEditor();
#endif
            return null;
        }

        internal static void AddPoint(SlabShapeEditor ed, XYZ p)
        {
#if REVIT2023 || REVIT2024
            ed.DrawPoint(p);
#else
            ed.AddPoint(p);
#endif
        }

        private static void AddSplitLine(SlabShapeEditor ed, SlabShapeVertex a, SlabShapeVertex b, bool useLegacyBoundaryDraw)
        {
#if REVIT2023 || REVIT2024
            SlabCall("SlabShapeEditor.DrawSplitLine", () => { ed.DrawSplitLine(a, b); });
#else
#if REVIT2025
            if (useLegacyBoundaryDraw)
            {
#pragma warning disable CS0618 // Keep the DrawPoint/DrawSplitLine pair for inserted boundary vertices.
                SlabCall("SlabShapeEditor.DrawSplitLine", () => { ed.DrawSplitLine(a, b); });
#pragma warning restore CS0618
                return;
            }
#endif
            SlabCall("SlabShapeEditor.AddSplitLine", () => { ed.AddSplitLine(a, b); });
#endif
        }

        // DrawPoint documents insertion on a boundary crease. AddPoint requires an
        // interior XY, and Revit removed DrawPoint in 2026.
#if REVIT2023 || REVIT2024 || REVIT2025
        private const bool CanAddBoundaryVertex = true;
#else
        private const bool CanAddBoundaryVertex = false;
#endif

        private static void AddSplitVertex(SlabShapeEditor editor, XYZ surface, bool boundary)
        {
#if REVIT2023 || REVIT2024 || REVIT2025
            if (boundary)
            {
#pragma warning disable CS0618 // The 2025 replacement does not accept boundary points.
                if (SlabCall("SlabShapeEditor.DrawPoint (boundary vertex)", () => editor.DrawPoint(surface)) == null)
                    throw new ArgumentException("SlabShapeEditor.DrawPoint returned no vertex on the slab's boundary crease.");
#pragma warning restore CS0618
                return;
            }
#endif
#if REVIT2023 || REVIT2024
            SlabCall("SlabShapeEditor.DrawPoint", () => AddPoint(editor, surface));
#else
            SlabCall("SlabShapeEditor.AddPoint", () => AddPoint(editor, surface));
#endif
        }

        // ---- planning --------------------------------------------------------------------------

        private static ArchModelEdit Plan(Document doc, JObject r, double scale)
        {
            string op = r.Value<string>("operation").ToLowerInvariant();
            Element slab = ModelEditRunner.Need<Element>(doc, r, "element_id");
            // The split opens its own transaction, where a new slab can regenerate
            // before the first editor lookup. Planning only needs the host class.
            if (!(slab is Floor || slab is RoofBase) || (op != "add_split_line" && Editor(slab) == null))
                throw new UnsupportedCapability("element_id " + Rid.Value(slab.Id) + " is a " + slab.GetType().Name +
                    "; horizun_slab_shape edits floors and roofs.", FallbackSignal.ReasonUnsupportedKind);
            long id = Rid.Value(slab.Id);
            double top = Top(slab);
            var edit = new ArchModelEdit();
            if (op == "read") { edit.ReadResult = Read(slab, top, scale); return edit; }
            edit.Planned.Add(ModelEditRunner.Planned(slab, PlannedAction.Modify, r));
            edit.Summary["operation"] = op; edit.Summary["element_id"] = id;
            edit.Evidence["top_elevation"] = Math.Round(top / scale, 4);
            Element Slab(Document d) => d.GetElement(Rid.Make(id));

            switch (op)
            {
                case "add_point":
                case "modify_subelement" when r["points"] != null:
                {
                    bool adding = op == "add_point";
                    var pts = ((JArray)r["points"]).Select((t, i) =>
                    {
                        XYZ p = ModelEditRunner.Point(t, scale, "points[" + i + "]");
                        return new { X = p.X, Y = p.Y, Offset = p.Z };
                    }).ToList();
                    edit.Summary["points"] = pts.Count;
                    edit.Apply = d =>
                    {
                        SlabShapeEditor ed = Editor(Slab(d));
                        if (!ed.IsEnabled) { ed.Enable(); d.Regenerate(); }
                        if (adding)
                        {
                            foreach (var p in pts) AddPoint(ed, new XYZ(p.X, p.Y, top));
                            d.Regenerate(); ed = Editor(Slab(d));
                        }
                        foreach (var p in pts)
                        {
                            SlabShapeVertex v = VertexAt(ed, p.X, p.Y);
                            if (v == null) throw new InvalidOperationException("no slab-shape vertex at (" + Math.Round(p.X * 304.8) + ", " + Math.Round(p.Y * 304.8) + ") mm" +
                                (adding ? " after adding it - the point must lie inside the boundary." : "; name an existing vertex (read lists them)."));
                            ed.ModifySubElement(v, p.Offset);
                        }
                    };
                    edit.Verify = d =>
                    {
                        var check = new PostconditionCheck(pts.Select((p, i) => "point[" + i + "]").ToArray());
                        SlabShapeEditor ed = Editor(Slab(d));
                        var conventions = new JArray();
                        var vertices = pts.Select(p => ed.IsEnabled ? VertexAt(ed, p.X, p.Y) : null).ToList();
                        // ONE ABSOLUTE READING (backlog #20). Choosing a convention by its fit
                        // to the requested result could accept even a single unchanged vertex.
                        string convention = ArchitecturalEditRules.SlabEditConvention(
                            vertices.Select(v => v == null ? (double?)null : v.Position.Z).ToList(), top, pts.Select(p => p.Offset).ToList(), Tol);
                        for (int i = 0; i < pts.Count; i++)
                        {
                            var p = pts[i]; string key = "point[" + i + "]";
                            SlabShapeVertex v = vertices[i];
                            if (v == null) { check.Unreadable(key, p.Offset * 304.8, "no vertex found at that x,y after the commit"); conventions.Add(null); continue; }
                            string how = ArchitecturalEditRules.SlabVertexConvention(v.Position.Z, top, p.Offset, Tol);
                            conventions.Add(how);
                            double found = ArchitecturalEditRules.SlabOffsetRead(v.Position.Z, top, convention);
                            check.Measure(key, p.Offset * 304.8, found * 304.8, 0.5, "mm",
                                          "vertex offset re-read (Position.Z read as " + (convention ?? "absolute, no reading held") + " for the whole edit)");
                        }
                        edit.Evidence["z_convention"] = conventions;
                        edit.Evidence["z_convention_of_the_edit"] = convention;
                        edit.Evidence["vertices"] = Vertices(ed, scale);
                        return check;
                    };
                    break;
                }
                case "add_split_line":
                case "modify_subelement":
                {
                    XYZ a = ModelEditRunner.Point(r["start"], scale, "start", 2), b = ModelEditRunner.Point(r["end"], scale, "end", 2);
                    if (a.DistanceTo(b) < XyTol) throw new ArgumentException("start and end must differ.");
                    bool split = op == "add_split_line";
                    double? supportTop = null;
                    double[][] beforeDraw = null, afterDraw = null;
                    int priorSplitCount = 0;
                    XYZ[] priorVertices = null;
                    double offset = split ? 0 : r.Value<double>("offset") * scale;
                    edit.Summary["start"] = ModelEditRunner.Arr(a, scale); edit.Summary["end"] = ModelEditRunner.Arr(b, scale);
                    edit.Apply = d =>
                    {
                        if (split)
                        {
                            supportTop = null;
                            priorVertices = null;
                            bool? wasEnabled = null;
#if REVIT2025
                            bool hadInteriorCreases = false;
#endif
                            SlabShapeEditor Refresh()
                            {
                                SlabShapeEditor editor = ArchitecturalEditRules.RefreshSlabEditor(
                                    () => SlabCall("Document.Regenerate (slab editor)", d.Regenerate),
                                    () => SlabCall("Floor/RoofBase slab shape editor", () => Editor(Slab(d))) ??
                                        throw new InvalidOperationException("Revit did not expose a slab shape editor after regenerating the floor or roof."),
                                    e => SlabCall("SlabShapeEditor.IsEnabled", () => e.IsEnabled),
                                    e => SlabCall("SlabShapeEditor.Enable", e.Enable), out bool enabled);
                                if (!wasEnabled.HasValue)
                                {
                                    wasEnabled = enabled;
                                    var types = ReadCreases(editor).Select(CreaseType).ToArray();
                                    priorSplitCount = types.Count(type => type == SlabShapeCreaseType.UserDrawn);
#if REVIT2025
                                    hadInteriorCreases = enabled && types.Any(type => type != SlabShapeCreaseType.Boundary);
#endif
                                    if (enabled)
                                        priorVertices = SlabCall("SlabShapeEditor.SlabShapeVertices / SlabShapeVertex.Position",
                                            () => editor.SlabShapeVertices.Cast<SlabShapeVertex>().Select(v => v.Position).ToArray());
                                }
                                return editor;
                            }
                            var endpoints = new Dictionary<XYZ, (XYZ Surface, ArchitecturalEditRules.SlabEndpoint Location)>();
                            ArchitecturalEditRules.SlabEndpoint Locate(XYZ point)
                            {
                                var location = LocateSplitEndpoint(TopFaces((HostObject)Slab(d)), point, top, out XYZ surface);
                                if (location == ArchitecturalEditRules.SlabEndpoint.Boundary)
                                {
                                    // A trim shared by two top facets is not the floor's perimeter.
                                    var perimeter = ReadCreases(Editor(Slab(d)))
                                        .Where(crease => CreaseType(crease) == SlabShapeCreaseType.Boundary).ToArray();
                                    if (perimeter.Length > 0 && !perimeter.Any(crease => EdgePoints(SlabCall("SlabShapeCrease.Curve", () => crease.Curve), surface)
                                        .Any(p => p != null && Flat(p).DistanceTo(point) <= Tol)))
                                        location = ArchitecturalEditRules.SlabEndpoint.Interior;
                                }
                                endpoints[point] = (surface, location);
                                return location;
                            }
                            string stage = "reading the slab shape editor and classifying the endpoints";
                            try
                            {
                                ArchitecturalEditRules.InsertSlabSplit(a, b, Refresh,
                                    (editor, point) => VertexAt(editor, point.X, point.Y),
                                    Locate,
                                    (editor, point) =>
                                    {
                                        // Inserting the other vertex can retriangulate the top face.
                                        if (Locate(point) == ArchitecturalEditRules.SlabEndpoint.Outside)
                                            throw new ArgumentException("A split endpoint is outside the regenerated slab top face.");
                                        var endpoint = endpoints[point];
                                        bool boundary = endpoint.Location == ArchitecturalEditRules.SlabEndpoint.Boundary;
                                        stage = FormattableString.Invariant($"inserting a {endpoint.Location} vertex at ({point.X * 304.8:0.###}, {point.Y * 304.8:0.###}, {endpoint.Surface.Z * 304.8:0.###}) mm");
                                        AddSplitVertex(editor, endpoint.Surface, boundary);
                                        if (boundary) edit.Evidence["split_method"] = "boundary_vertex";
                                    },
                                    (editor, startVertex, endVertex, insertedBoundaryVertex) =>
                                    {
                                        stage = $"drawing between {endpoints[a].Location} and {endpoints[b].Location} vertices";
                                        bool useLegacyBoundaryDraw = insertedBoundaryVertex;
#if REVIT2025
                                        // AddSplitLine subdivides the chord at existing folds. Keep
                                        // DrawSplitLine for boundary insertions on a flat floor.
                                        if (slab is Floor && hadInteriorCreases &&
                                            endpoints[a].Location == ArchitecturalEditRules.SlabEndpoint.Boundary &&
                                            endpoints[b].Location == ArchitecturalEditRules.SlabEndpoint.Boundary)
                                        {
                                            useLegacyBoundaryDraw = false;
                                            edit.Evidence["split_method"] = "native_chord";
                                        }
#endif
                                        AddSplitLine(editor, startVertex, endVertex, useLegacyBoundaryDraw);
                                    },
                                    editor =>
                                    {
                                        stage = "creating the measured edge-to-edge support line";
                                        supportTop = SplitFlatFloorAtBoundary((HostObject)Slab(d), editor,
                                            endpoints[a].Surface, endpoints[b].Surface, wasEnabled != false);
                                        edit.Evidence["split_method"] = "pick_support";
                                    }, CanAddBoundaryVertex,
                                    editor => beforeDraw = UserSplitSpans(editor, a, b),
                                    editor =>
                                    {
                                        stage = "re-reading the new split creases";
                                        afterDraw = UserSplitSpans(editor, a, b);
                                    });
                            }
                            catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException ||
                                ex is ArgumentException || ex is InvalidOperationException)
                            {
                                throw new InvalidOperationException(FormattableString.Invariant(
                                    $"Revit refused the slab split from ({a.X * 304.8:0.###}, {a.Y * 304.8:0.###}) to ({b.X * 304.8:0.###}, {b.Y * 304.8:0.###}) mm while {stage}. {ex.Message}").TrimEnd(), ex);
                            }
                        }
                        else
                        {
                            SlabShapeEditor ed = Editor(Slab(d));
                            if (!ed.IsEnabled) { ed.Enable(); d.Regenerate(); ed = Editor(Slab(d)); }
                            SlabShapeCrease c = CreaseBetween(ed, a, b);
                            if (c == null) throw new InvalidOperationException("no crease runs between start and end; read lists the creases.");
                            ed.ModifySubElement(c, offset);
                        }
                    };
                    edit.Verify = d =>
                    {
                        SlabShapeEditor ed = Editor(Slab(d));
                        if (split)
                        {
                            var required = new List<string> { "split_line_coverage", "new_split_creases" };
                            if (supportTop.HasValue) required.Add("top_elevation_preserved");
                            if (priorVertices != null) required.Add("existing_vertices_preserved");
                            var check = new PostconditionCheck(required.ToArray());
                            if (ed == null || !ed.IsEnabled) { check.Unreadable("split_line_coverage", a.DistanceTo(b) * 304.8, "shape editing is not enabled after the commit"); return check; }
                            double len = a.DistanceTo(b);
                            double[][] spans = UserSplitSpans(ed, a, b);
                            double covered = ArchitecturalEditRules.CoveredLength(spans, 0, len);
                            check.Measure("split_line_coverage", len * 304.8, covered * 304.8, 1.0, "mm", "length covered in XY by committed user-drawn creases");
                            // Measure new coverage against the state immediately before
                            // the native draw, and check that it survived commit.
                            double before = ArchitecturalEditRules.CoveredLength(beforeDraw, 0, len);
                            double added = ArchitecturalEditRules.NewCommittedSplitCoverage(beforeDraw, afterDraw, spans, 0, len);
                            var traceEvidence = new JArray(new JObject
                            {
                                ["start_mm"] = 0, ["end_mm"] = len * 304.8,
                                ["before_draw_mm"] = before * 304.8,
                                ["after_draw_mm"] = ArchitecturalEditRules.CoveredLength(afterDraw, 0, len) * 304.8,
                                ["committed_mm"] = covered * 304.8
                            });
                            check.Compare("new_split_creases", true, added > XyTol);
                            edit.Evidence["split_creases_before"] = priorSplitCount;
                            edit.Evidence["split_creases_after"] = UserSplitCount(ed);
                            edit.Evidence["split_coverage_before_mm"] = before * 304.8;
                            edit.Evidence["split_coverage_added_mm"] = added * 304.8;
                            edit.Evidence["split_coverage_trace"] = traceEvidence;
                            if (supportTop.HasValue)
                            {
                                bool flat = ArchitecturalEditRules.FlatSlabAt(FaceElevations(TopFaces((HostObject)Slab(d))),
                                    supportTop.Value, Tol);
                                check.Compare("top_elevation_preserved", true, flat);
                            }
                            if (priorVertices != null)
                                check.Compare("existing_vertices_preserved", true, priorVertices.All(p =>
                                {
                                    SlabShapeVertex v = VertexAt(ed, p.X, p.Y);
                                    return v != null && Math.Abs(v.Position.Z - p.Z) <= Tol;
                                }));
                            edit.Evidence["vertices"] = SlabCall("SlabShapeEditor.SlabShapeVertices / SlabShapeVertex.Position",
                                () => Vertices(ed, scale));
                            edit.Evidence["creases"] = Creases(ed, scale);
                            return check;
                        }
                        else
                        {
                            var check = new PostconditionCheck("crease_start", "crease_end");
                            var ends = new[] { new { K = "crease_start", P = a }, new { K = "crease_end", P = b } };
                            var endVertices = ends.Select(end => ed.IsEnabled ? VertexAt(ed, end.P.X, end.P.Y) : null).ToList();
                            // One convention for both ends, as for points (backlog #20).
                            string convention = ArchitecturalEditRules.SlabEditConvention(
                                endVertices.Select(v => v == null ? (double?)null : v.Position.Z).ToList(), top, new[] { offset, offset }, Tol);
                            for (int i = 0; i < ends.Length; i++)
                            {
                                SlabShapeVertex v = endVertices[i];
                                if (v == null) { check.Unreadable(ends[i].K, offset * 304.8, "no vertex at that end after the commit"); continue; }
                                double found = ArchitecturalEditRules.SlabOffsetRead(v.Position.Z, top, convention);
                                check.Measure(ends[i].K, offset * 304.8, found * 304.8, 0.5, "mm",
                                              "crease end offset re-read (Position.Z read as " + (convention ?? "absolute, no reading held") + " for both ends)");
                            }
                            return check;
                        }
                    };
                    if (split) edit.NativeRehearsalSubject = $"Slab {id}";
                    break;
                }
                case "reset_shape":
                {
                    edit.Apply = d => Editor(Slab(d)).ResetSlabShape();
                    edit.Verify = d =>
                    {
                        var check = new PostconditionCheck("interior_vertices", "vertices_at_zero");
                        SlabShapeEditor ed = Editor(Slab(d));
                        if (!ed.IsEnabled) { check.Compare("interior_vertices", 0, 0); check.Compare("vertices_at_zero", true, true); edit.Evidence["enabled"] = false; return check; }
                        var all = ed.SlabShapeVertices.Cast<SlabShapeVertex>().ToList();
                        check.Compare("interior_vertices", 0, all.Count(v => v.VertexType == SlabShapeVertexType.Interior));
                        check.Compare("vertices_at_zero", true, all.All(v => ArchitecturalEditRules.SlabVertexConvention(v.Position.Z, top, 0, Tol) != null));
                        edit.Evidence["enabled"] = true;
                        return check;
                    };
                    break;
                }
            }
            return edit;
        }

        private static JObject Read(Element slab, double top, double scale)
        {
            SlabShapeEditor ed = Editor(slab);
            var result = new JObject { ["element_id"] = Rid.Value(slab.Id), ["top_elevation"] = Math.Round(top / scale, 4) };
            if (!ed.IsEnabled)
            {
                JObject contour = ArchitecturalEditRules.FlatSlabContour(TopFaces((HostObject)slab)
                    .SelectMany(face => face.GetEdgesAsCurveLoops())
                    .Select(loop => LoopVertices(loop).Select(p => new[] { p.X, p.Y, p.Z })), scale);
                result.Merge(contour);
                if (result.Value<bool>("boundary_read_complete") != true)
                    result["note"] = "Revit did not return a complete readable top-face boundary.";
                return result;
            }
            result["enabled"] = true;
            result["vertex_source"] = "shape_editor";
            result["z_note"] = "vertex and crease endpoint z are Revit's SlabShapeVertex.Position.Z as returned; compare with top_elevation to read them";
            result["vertices"] = Vertices(ed, scale);
            result["creases"] = Creases(ed, scale);
            return result;
        }

        private static JArray Creases(SlabShapeEditor ed, double scale)
        {
            var creases = new JArray();
            foreach (SlabShapeCrease c in ReadCreases(ed))
            {
                var ends = CreaseEnds(c);
                var type = CreaseType(c);
                creases.Add(new JObject { ["start"] = ModelEditRunner.Arr(ends.Start, scale),
                    ["end"] = ModelEditRunner.Arr(ends.End, scale), ["type"] = type.ToString() });
            }
            return creases;
        }

        private static (XYZ Start, XYZ End) CreaseEnds(SlabShapeCrease crease)
        {
            // The vertices bound the actual crease even when Curve has no readable endpoints.
            var vertices = SlabCall("SlabShapeCrease.EndPoints",
                () => crease.EndPoints?.Cast<SlabShapeVertex>().ToArray() ?? Array.Empty<SlabShapeVertex>());
            if (vertices.Length != 2)
                throw new InvalidOperationException("SlabShapeCrease.EndPoints did not return two vertices.");
            return SlabCall("SlabShapeVertex.Position (crease endpoints)",
                () => (vertices[0].Position, vertices[1].Position));
        }

        private static T SlabCall<T>(string call, Func<T> read)
        {
            try { return read(); }
            catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException || ex is InvalidOperationException)
            {
                throw new InvalidOperationException($"{call} failed ({ex.GetType().Name}). {ex.Message}".TrimEnd(), ex);
            }
        }

        private static void SlabCall(string call, Action action) =>
            SlabCall(call, () => { action(); return true; });

        private static IEnumerable<Face> TopFaces(HostObject slab) =>
            HostObjectUtils.GetTopFaces(slab).Select(r => slab.GetGeometryObjectFromReference(r)).OfType<Face>();

        private static IEnumerable<XYZ> LoopVertices(CurveLoop loop)
        {
            foreach (Curve curve in loop)
            {
                IList<XYZ> points = curve.Tessellate();
                for (int i = 0; i < points.Count - 1; i++) yield return points[i];
            }
        }

        private static IEnumerable<double> FaceElevations(IEnumerable<Face> faces) =>
            faces.Select(face => face is PlanarFace plane && Math.Abs(plane.FaceNormal.Z) > 1 - 1e-9
                ? plane.Origin.Z : double.NaN);

        private static int UserSplitCount(SlabShapeEditor editor) =>
            UserSplits(editor).Count();

        private static SlabShapeCrease[] ReadCreases(SlabShapeEditor editor) =>
            SlabCall("SlabShapeEditor.SlabShapeCreases", () => editor.SlabShapeCreases.Cast<SlabShapeCrease>().ToArray());

        private static SlabShapeCreaseType CreaseType(SlabShapeCrease crease) =>
            SlabCall("SlabShapeCrease.CreaseType", () => crease.CreaseType);

        private static IEnumerable<SlabShapeCrease> UserSplits(SlabShapeEditor editor) =>
            ReadCreases(editor).Where(crease => CreaseType(crease) == SlabShapeCreaseType.UserDrawn);

        private static double[][] UserSplitSpans(SlabShapeEditor editor, XYZ start, XYZ end)
        {
            XYZ direction = (end - start).Normalize();
            var spans = new List<double[]>();
            foreach (SlabShapeCrease crease in UserSplits(editor))
            {
                var ends = CreaseEnds(crease);
                XYZ a = Flat(ends.Start), b = Flat(ends.End);
                if (OffLine(a, start, direction) > XyTol || OffLine(b, start, direction) > XyTol) continue;
                spans.Add(new[] { (a - start).DotProduct(direction), (b - start).DotProduct(direction) });
            }
            return spans.ToArray();
        }

        private static (double? Support, double Top) FlatFloorSupport(HostObject slab)
        {
            double[] tops = FaceElevations(TopFaces(slab)).ToArray();
            double[] bottoms = FaceElevations(HostObjectUtils.GetBottomFaces(slab)
                .Select(r => slab.GetGeometryObjectFromReference(r)).OfType<Face>()).ToArray();
            return (ArchitecturalEditRules.FlatFloorSupportElevation(tops, bottoms, Tol),
                tops.Length > 0 ? tops[0] : double.NaN);
        }

        private static double SplitFlatFloorAtBoundary(HostObject slab, SlabShapeEditor editor, XYZ start, XYZ end, bool wasEnabled)
        {
            var (support, top) = FlatFloorSupport(slab);
            if (!(slab is Floor) || wasEnabled || !support.HasValue)
                throw new ArgumentException("A split with new edge endpoints needs a flat floor with shape editing initially disabled and readable parallel faces.");
            if (start.DistanceTo(end) <= slab.Document.Application.ShortCurveTolerance)
                throw new ArgumentException("The edge-to-edge split is shorter than Revit's curve tolerance.");

            // Revit adds the slab thickness to the support elevation. Use the
            // measured underside so the split does not raise the top face.
            editor.PickSupport(Line.CreateBound(new XYZ(start.X, start.Y, support.Value), new XYZ(end.X, end.Y, support.Value)));
            return top;
        }

        private static JArray Vertices(SlabShapeEditor ed, double scale)
        {
            var a = new JArray();
            if (!ed.IsEnabled) return a;
            foreach (SlabShapeVertex v in ed.SlabShapeVertices)
            {
                JArray p = ModelEditRunner.Arr(v.Position, scale); p.Add(v.VertexType.ToString()); a.Add(p);
            }
            return a;
        }

        /// <summary>The slab top the unmodified shape sits at: level elevation plus the height offset.</summary>
        internal static double Top(Element slab)
        {
            double level = 0;
            try { if (slab.Document.GetElement(slab.LevelId) is Level l) level = l.ProjectElevation; } catch { }
            bool isRoof = slab is RoofBase;
            Parameter off = isRoof ? slab.get_Parameter(BuiltInParameter.ROOF_LEVEL_OFFSET_PARAM)
                                   : slab.get_Parameter(BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM);
            double? thickness = null;
            if (isRoof)
            {
                try
                {
                    Parameter t = slab.get_Parameter(BuiltInParameter.ROOF_ATTR_THICKNESS_PARAM);
                    if (t != null && t.HasValue && t.AsDouble() > 0) thickness = t.AsDouble();
                    else if (slab.Document.GetElement(slab.GetTypeId()) is HostObjAttributes ht)
                        thickness = ht.GetCompoundStructure()?.GetWidth();
                }
                catch { }
            }
            return ArchitecturalEditRules.SlabShapeTop(level, off?.AsDouble() ?? 0, isRoof, thickness);
        }

        internal static SlabShapeVertex VertexAt(SlabShapeEditor ed, double x, double y)
        {
            SlabShapeVertex best = null; double bestD = XyTol;
            foreach (SlabShapeVertex v in ed.SlabShapeVertices)
            {
                double dd = Math.Sqrt(Math.Pow(v.Position.X - x, 2) + Math.Pow(v.Position.Y - y, 2));
                if (dd <= bestD) { bestD = dd; best = v; }
            }
            return best;
        }

        private static SlabShapeCrease CreaseBetween(SlabShapeEditor ed, XYZ a, XYZ b)
        {
            foreach (SlabShapeCrease c in ReadCreases(ed))
            {
                var ends = CreaseEnds(c);
                XYZ p0 = Flat(ends.Start), p1 = Flat(ends.End);
                if ((p0.DistanceTo(a) <= XyTol && p1.DistanceTo(b) <= XyTol) || (p0.DistanceTo(b) <= XyTol && p1.DistanceTo(a) <= XyTol)) return c;
            }
            return null;
        }

        private static double OffLine(XYZ p, XYZ origin, XYZ dir) => (p - origin).CrossProduct(dir).GetLength();
    }
}
