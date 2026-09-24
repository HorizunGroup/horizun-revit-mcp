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
    public sealed class SlabShapeCommand : ICommand
    {
        public string Name => "horizun_slab_shape";
        public string Description => "Read and edit a floor or roof's slab shape (points, split lines, vertex/crease offsets, reset), re-reading every vertex elevation.";

        private const double Tol = ArchitecturalEditRules.PositionToleranceFeet;
        private const double XyTol = 1.0 / 304.8;

        public CommandResult Execute(UIApplication app, string paramsJson) =>
            ModelEditRunner.Run(app, paramsJson, Name, "Horizun: slab shape", ArchitecturalEditRules.ValidateSlabShape, Plan);

        // ---- the per-year API ----------------------------------------------------------------

        private static SlabShapeEditor Editor(Element e)
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

        private static void AddPoint(SlabShapeEditor ed, XYZ p)
        {
#if REVIT2023 || REVIT2024
            ed.DrawPoint(p);
#else
            ed.AddPoint(p);
#endif
        }

        private static void AddSplitLine(SlabShapeEditor ed, SlabShapeVertex a, SlabShapeVertex b)
        {
#if REVIT2023 || REVIT2024
            ed.DrawSplitLine(a, b);
#else
            ed.AddSplitLine(a, b);
#endif
        }

        // ---- planning --------------------------------------------------------------------------

        private static ArchModelEdit Plan(Document doc, JObject r, double scale)
        {
            string op = r.Value<string>("operation").ToLowerInvariant();
            Element slab = ModelEditRunner.Need<Element>(doc, r, "element_id");
            if (Editor(slab) == null)
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
                        for (int i = 0; i < pts.Count; i++)
                        {
                            var p = pts[i]; string key = "point[" + i + "]";
                            SlabShapeVertex v = ed.IsEnabled ? VertexAt(ed, p.X, p.Y) : null;
                            if (v == null) { check.Unreadable(key, p.Offset * 304.8, "no vertex found at that x,y after the commit"); conventions.Add(null); continue; }
                            string how = ArchitecturalEditRules.SlabVertexConvention(v.Position.Z, top, p.Offset, Tol);
                            conventions.Add(how);
                            double found = how == "absolute" ? v.Position.Z - top : v.Position.Z;
                            check.Measure(key, p.Offset * 304.8, found * 304.8, 0.5, "mm", "vertex offset re-read (Position.Z read as " + (how ?? "neither reading") + ")");
                        }
                        edit.Evidence["z_convention"] = conventions;
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
                    double offset = split ? 0 : r.Value<double>("offset") * scale;
                    edit.Summary["start"] = ModelEditRunner.Arr(a, scale); edit.Summary["end"] = ModelEditRunner.Arr(b, scale);
                    edit.Apply = d =>
                    {
                        SlabShapeEditor ed = Editor(Slab(d));
                        if (!ed.IsEnabled) { ed.Enable(); d.Regenerate(); ed = Editor(Slab(d)); }
                        if (split)
                        {
                            bool added = false;
                            foreach (XYZ p in new[] { a, b })
                                if (VertexAt(ed, p.X, p.Y) == null) { AddPoint(ed, new XYZ(p.X, p.Y, top)); added = true; }
                            if (added) { d.Regenerate(); ed = Editor(Slab(d)); }
                            SlabShapeVertex va = VertexAt(ed, a.X, a.Y), vb = VertexAt(ed, b.X, b.Y);
                            if (va == null || vb == null) throw new InvalidOperationException("an end of the split line is neither an existing vertex nor a point inside the boundary.");
                            AddSplitLine(ed, va, vb);
                        }
                        else
                        {
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
                            var check = new PostconditionCheck("split_line_coverage");
                            if (!ed.IsEnabled) { check.Unreadable("split_line_coverage", a.DistanceTo(b) * 304.8, "shape editing is not enabled after the commit"); return check; }
                            XYZ dir = (b - a).Normalize(); double len = a.DistanceTo(b);
                            var spans = new List<double[]>();
                            foreach (SlabShapeCrease c in ed.SlabShapeCreases)
                            {
                                XYZ p0 = Flat(c.Curve.GetEndPoint(0)), p1 = Flat(c.Curve.GetEndPoint(1));
                                if (OffLine(p0, a, dir) > XyTol || OffLine(p1, a, dir) > XyTol) continue;
                                spans.Add(new[] { (p0 - a).DotProduct(dir), (p1 - a).DotProduct(dir) });
                            }
                            double covered = ArchitecturalEditRules.CoveredLength(spans, 0, len);
                            check.Measure("split_line_coverage", len * 304.8, covered * 304.8, 1.0, "mm", "length of the requested line covered by committed creases");
                            return check;
                        }
                        else
                        {
                            var check = new PostconditionCheck("crease_start", "crease_end");
                            foreach (var end in new[] { new { K = "crease_start", P = a }, new { K = "crease_end", P = b } })
                            {
                                SlabShapeVertex v = ed.IsEnabled ? VertexAt(ed, end.P.X, end.P.Y) : null;
                                if (v == null) { check.Unreadable(end.K, offset * 304.8, "no vertex at that end after the commit"); continue; }
                                string how = ArchitecturalEditRules.SlabVertexConvention(v.Position.Z, top, offset, Tol);
                                double found = how == "absolute" ? v.Position.Z - top : v.Position.Z;
                                check.Measure(end.K, offset * 304.8, found * 304.8, 0.5, "mm", "crease end offset re-read (Position.Z read as " + (how ?? "neither reading") + ")");
                            }
                            return check;
                        }
                    };
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
            var result = new JObject { ["element_id"] = Rid.Value(slab.Id), ["enabled"] = ed.IsEnabled, ["top_elevation"] = Math.Round(top / scale, 4),
                ["z_note"] = "vertex z is Revit's SlabShapeVertex.Position.Z as returned; compare with top_elevation to read it" };
            if (!ed.IsEnabled) { result["note"] = "shape editing is not enabled: the slab is flat and has no editable vertices yet."; return result; }
            result["vertices"] = Vertices(ed, scale);
            var creases = new JArray();
            foreach (SlabShapeCrease c in ed.SlabShapeCreases)
                creases.Add(new JObject { ["start"] = ModelEditRunner.Arr(c.Curve.GetEndPoint(0), scale), ["end"] = ModelEditRunner.Arr(c.Curve.GetEndPoint(1), scale), ["type"] = c.CreaseType.ToString() });
            result["creases"] = creases;
            return result;
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
        private static double Top(Element slab)
        {
            double level = 0;
            try { if (slab.Document.GetElement(slab.LevelId) is Level l) level = l.Elevation; } catch { }
            Parameter off = slab.get_Parameter(BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM)
                            ?? slab.get_Parameter(BuiltInParameter.ROOF_LEVEL_OFFSET_PARAM);
            return level + (off?.AsDouble() ?? 0);
        }

        private static SlabShapeVertex VertexAt(SlabShapeEditor ed, double x, double y)
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
            foreach (SlabShapeCrease c in ed.SlabShapeCreases)
            {
                XYZ p0 = Flat(c.Curve.GetEndPoint(0)), p1 = Flat(c.Curve.GetEndPoint(1));
                if ((p0.DistanceTo(a) <= XyTol && p1.DistanceTo(b) <= XyTol) || (p0.DistanceTo(b) <= XyTol && p1.DistanceTo(a) <= XyTol)) return c;
            }
            return null;
        }

        private static XYZ Flat(XYZ p) => new XYZ(p.X, p.Y, 0);
        private static double OffLine(XYZ p, XYZ origin, XYZ dir) => (p - origin).CrossProduct(dir).GetLength();
    }
}
