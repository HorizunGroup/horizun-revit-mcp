// -----------------------------------------------------------------------------
// Horizun Revit MCP - horizun_sheet_layout: viewports placed on paper, verified.
// Original Horizun code.
//
// FIELD FEEDBACK 2.5 and E9. Through ModelEditRunner (rehearse,
// single-use token, re-read after commit, roll back on disagreement):
//
//   get_viewport_box  per viewport: box (GetBoxOutline), title (GetLabelOutline),
//                     centre, and the view's CROP carried to paper through
//                     GetModelToProjectionTransforms + GetProjectionToSheetTransform.
//   move_viewport     the crop's (default) or the box's centre/corner to a paper
//                     point. Placing by the box was what put views 90-100 mm off in
//                     Y: grids and levels outside the crop widen the box.
//   auto_layout       rows inside the drawing frame inset by margins and a notes strip,
//                     or minimum translations of the viewports outside that area.
//
// After commit every moved viewport is re-read; a moved extent outside the usable
// area (the title block for move_viewport), an overlap between viewports or a
// position off by more than 0.5 mm rolls the edit back. Only viewports with the
// HRZ mark (or created this session) are moved unless allow_unmarked=true.
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
    public sealed partial class SheetLayoutCommand : ICommand
    {
        public string Name => "horizun_sheet_layout";
        public string Description => "Viewport boxes and crops on paper, move a viewport by its crop, and lay out a sheet's viewports in rows, re-read after commit.";

        public CommandResult Execute(UIApplication app, string paramsJson) =>
            ModelEditRunner.Run(app, paramsJson, Name, "Horizun: sheet layout", SheetLayoutRules.Validate, Plan, "get_viewport_box");

        // ---- paper readers ------------------------------------------------------------------

        private static double[] Box(Viewport vp) => Box(vp, out _);

        private static double[] Box(Viewport vp, out string why)
        {
            why = null;
            try { Outline o = vp.GetBoxOutline(); return SheetLayoutRules.Rect(o.MinimumPoint.X, o.MinimumPoint.Y, o.MaximumPoint.X, o.MaximumPoint.Y); }
            catch (Exception ex) { why = "viewport box is unreadable: " + ex.Message; return null; }
        }

        private static double[] Label(Viewport vp)
        {
            try
            {
                Outline o = vp.GetLabelOutline();
                if (o == null || o.IsEmpty) return null;
                return SheetLayoutRules.Rect(o.MinimumPoint.X, o.MinimumPoint.Y, o.MaximumPoint.X, o.MaximumPoint.Y);
            }
            catch { return null; }
        }

        /// <summary>Box plus title: what takes room on paper.</summary>
        private static double[] Extent(Viewport vp) => SheetLayoutRules.Union(Box(vp), Label(vp));

        private static (double[] Box, double[] Label, double[] Extent, string Warning) ReadDrawingFootprint(Viewport vp)
        {
            double[] box = Box(vp, out string why);
            if (box == null) return (null, null, null, why);
            if (box.Any(v => double.IsNaN(v) || double.IsInfinity(v)))
                return (null, null, null, "viewport box has non-finite bounds");
            try
            {
                Outline label = vp.GetLabelOutline();
                if (label == null || label.IsEmpty) return (box, null, box, null);
                double[] bounds = SheetLayoutRules.Rect(label.MinimumPoint.X, label.MinimumPoint.Y, label.MaximumPoint.X, label.MaximumPoint.Y);
                if (bounds.Any(v => double.IsNaN(v) || double.IsInfinity(v)))
                    return (box, null, null, "viewport title has non-finite bounds");
                return (box, bounds, SheetLayoutRules.Union(box, bounds), null);
            }
            catch (Exception ex) { return (box, null, null, "viewport title is unreadable: " + ex.Message); }
        }

        /// <summary>The view's crop on paper, or null with the reason.</summary>
        internal static double[] CropOnPaper(Document doc, Viewport vp, out string why)
        {
            why = null;
            try
            {
                View v = doc.GetElement(vp.ViewId) as View;
                if (v == null) { why = "the viewport's view is unreadable"; return null; }
                bool active;
                try { active = v.CropBoxActive; } catch { active = false; }
                if (!active) { why = "the view's crop is not active: it has no crop rectangle to anchor on"; return null; }
                IList<TransformWithBoundary> tw = v.GetModelToProjectionTransforms();
                if (tw == null || tw.Count != 1) { why = "the view's crop is split into regions; anchor=box is the only reading"; return null; }
                Transform m2p = tw[0].GetModelToProjectionTransform();
                Transform p2s = vp.GetProjectionToSheetTransform();
                var pts = new List<XYZ>();
                try
                {
                    ViewCropRegionShapeManager m = v.GetCropRegionShapeManager();
                    foreach (CurveLoop loop in m.GetCropShape())
                        foreach (Curve c in loop) pts.AddRange(c.Tessellate());
                }
                catch { }
                if (pts.Count == 0)
                {
                    BoundingBoxXYZ c = v.CropBox;
                    foreach (double x in new[] { c.Min.X, c.Max.X })
                        foreach (double y in new[] { c.Min.Y, c.Max.Y })
                            pts.Add(c.Transform.OfPoint(new XYZ(x, y, c.Max.Z)));
                }
                List<XYZ> paper = pts.Select(p => p2s.OfPoint(m2p.OfPoint(p))).ToList();
                return SheetLayoutRules.Rect(paper.Min(p => p.X), paper.Min(p => p.Y), paper.Max(p => p.X), paper.Max(p => p.Y));
            }
            catch (Exception ex) { why = "the crop could not be carried to paper: " + ex.Message; return null; }
        }

        private static double[] TitleBlock(Document doc, ViewSheet sheet)
        {
            double[] r = null;
            try
            {
                foreach (Element tb in new FilteredElementCollector(doc, sheet.Id).OfCategory(BuiltInCategory.OST_TitleBlocks).WhereElementIsNotElementType())
                {
                    BoundingBoxXYZ b = tb.get_BoundingBox(sheet);
                    if (b != null) r = SheetLayoutRules.Union(r, SheetLayoutRules.Rect(b.Min.X, b.Min.Y, b.Max.X, b.Max.Y));
                }
            }
            catch { }
            if (r != null) return r;
            try { BoundingBoxUV o = sheet.Outline; return o == null ? null : SheetLayoutRules.Rect(o.Min.U, o.Min.V, o.Max.U, o.Max.V); }
            catch { return null; }
        }

        /// <summary>
        /// The title block's straight lines on the sheet ([x0, y0, x1, y1] in feet) and where they were read.
        /// Revit gives no geometry for an annotation family placed on a sheet (measured on Revit 2025: the
        /// title block instance's get_Geometry is empty with or without a view), so the lines come from the
        /// family itself, opened in memory, carried through the instance's transform, and closed without
        /// saving. Lines switched off by a visibility parameter are still read (stated limit).
        /// </summary>
        private static List<double[]> TitleBlockLines(Document doc, ViewSheet sheet, out string source)
        {
            var lines = new List<double[]>();
            source = "none";
            var blocks = new List<FamilyInstance>();
            var why = new List<string>();
            try
            {
                foreach (Element tb in new FilteredElementCollector(doc, sheet.Id).OfCategory(BuiltInCategory.OST_TitleBlocks).WhereElementIsNotElementType())
                    if (tb is FamilyInstance fi) blocks.Add(fi);
                var opt = new Options { View = sheet };
                foreach (FamilyInstance tb in blocks) CollectLines(tb.get_Geometry(opt), lines, 0);
                if (lines.Count >= 4) { source = "title_block_instance_geometry"; return lines; }
            }
            catch (Exception ex) { why.Add("instance geometry: " + ex.Message); }
            foreach (FamilyInstance tb in blocks)
            {
                Family fam = tb.Symbol?.Family;
                if (fam == null || !fam.IsEditable) { why.Add("family not editable"); continue; }
                if (doc.IsModifiable) { why.Add("a transaction is open"); continue; }
                Document fdoc = null;
                try
                {
                    fdoc = doc.EditFamily(fam);
                    Transform tr = tb.GetTransform();
                    foreach (CurveElement ce in new FilteredElementCollector(fdoc).OfClass(typeof(CurveElement)).Cast<CurveElement>())
                        if (ce.GeometryCurve is Line l && l.IsBound)
                        {
                            XYZ a = tr.OfPoint(l.GetEndPoint(0)), b = tr.OfPoint(l.GetEndPoint(1));
                            lines.Add(new[] { a.X, a.Y, b.X, b.Y });
                        }
                }
                catch (Exception ex) { why.Add(ex.Message); }
                finally { try { fdoc?.Close(false); } catch { } }
            }
            if (blocks.Count == 0) why.Add("no title block instance found");
            else if (lines.Count == 0) why.Add("no straight title block lines could be read");
            source = lines.Count > 0 ? "title_block_family (opened in memory, closed without saving)"
                   : "none";
            if (why.Count > 0) source += "; warnings: " + string.Join("; ", why.Distinct());
            return lines;
        }

        private static void CollectLines(GeometryElement ge, List<double[]> lines, int depth)
        {
            if (ge == null || depth > 6) return;
            foreach (GeometryObject g in ge)
            {
                if (g is GeometryInstance gi) CollectLines(gi.GetInstanceGeometry(), lines, depth + 1);
                else if (g is Line l && l.IsBound)
                {
                    XYZ a = l.GetEndPoint(0), b = l.GetEndPoint(1);
                    lines.Add(new[] { a.X, a.Y, b.X, b.Y });
                }
                else if (g is PolyLine pl)
                {
                    IList<XYZ> pts = pl.GetCoordinates();
                    for (int i = 1; i < pts.Count; i++) lines.Add(new[] { pts[i - 1].X, pts[i - 1].Y, pts[i].X, pts[i].Y });
                }
            }
        }

        private static List<Viewport> ViewportsOf(Document doc, ViewSheet sheet)
            => sheet.GetAllViewports().Select(id => doc.GetElement(id) as Viewport).Where(v => v != null).OrderBy(v => Rid.Value(v.Id)).ToList();

        private static JObject Row(Document doc, Viewport vp) => Row(doc, vp, Box(vp), Label(vp));

        private static JObject Row(Document doc, Viewport vp, double[] box, double[] label)
        {
            View v = doc.GetElement(vp.ViewId) as View;
            double[] crop = CropOnPaper(doc, vp, out string why);
            var o = new JObject
            {
                ["viewport_id"] = Rid.Value(vp.Id), ["unique_id"] = vp.UniqueId, ["view_id"] = Rid.Value(vp.ViewId), ["view"] = v?.Name,
                ["box"] = SheetLayoutRules.RectMm(box), ["label"] = SheetLayoutRules.RectMm(label),
                ["crop"] = SheetLayoutRules.RectMm(crop), ["hrz"] = HrzOwnership.IsTrustedOwned(vp.Document, vp)
            };
            try
            {
                XYZ offset = vp.LabelOffset;
                var offsetMm = new JArray(SheetLayoutRules.Mm(offset.X), SheetLayoutRules.Mm(offset.Y));
                o["label_offset"] = offsetMm;
                o["label_offset_mm"] = offsetMm.DeepClone();
            }
            catch (Exception ex)
            {
                o["label_offset_warning"] = "Viewport.LabelOffset could not be read: " + ex.Message;
            }
            try { o["scale"] = v?.Scale; } catch { }
            if (why != null) o["crop_note"] = why;
            return o;
        }

        // ---- operations -------------------------------------------------------------------------

        private static ArchModelEdit Plan(Document doc, JObject r, double scale)
        {
            string op = r.Value<string>("operation").Trim().ToLowerInvariant();
            if (op == "get_viewport_box")
            {
                if (r["viewport_id"] != null)
                {
                    Viewport one = ModelEditRunner.Need<Viewport>(doc, r, "viewport_id");
                    var single = new JObject { ["read_only"] = true, ["sheet_id"] = Rid.Value(one.SheetId),
                        ["units"] = "paper mm", ["viewports"] = new JArray(Row(doc, one)) };
                    if (doc.GetElement(one.SheetId) is ViewSheet os)
                    {
                        DrawingFrame frame = ReadDrawingFrame(doc, os);
                        single["title_block"] = SheetLayoutRules.RectMm(frame.Box);
                        single["drawing_area_check"] = DrawingAreaCheck(doc, ReadDrawingExtents(new[] { one }), frame, frame.Area);
                    }
                    return new ArchModelEdit { ReadResult = single };
                }
                ViewSheet s = ModelEditRunner.Need<ViewSheet>(doc, r, "sheet_id");
                List<Viewport> onSheet = ViewportsOf(doc, s);
                DrawingFrame sheetFrame = ReadDrawingFrame(doc, s);
                var read = new JObject
                {
                    ["read_only"] = true, ["sheet_id"] = Rid.Value(s.Id), ["sheet"] = s.SheetNumber + " - " + s.Name,
                    ["title_block"] = SheetLayoutRules.RectMm(sheetFrame.Box), ["units"] = "paper mm",
                    ["viewports"] = new JArray(onSheet.Select(vp => (JToken)Row(doc, vp)))
                };
                read["drawing_area_check"] = DrawingAreaCheck(doc, ReadDrawingExtents(onSheet), sheetFrame, sheetFrame.Area);
                return new ArchModelEdit { ReadResult = read };
            }
            bool allow = r.Value<bool?>("allow_unmarked") == true;
            if (op == "layout_grid") return Grid(doc, r, allow);
            if (op == "set_view_scale") return SetScale(doc, r, allow);
            if (op == "rename") return Rename(doc, r, allow);
            if (op == "set_title") return SetTitle(doc, r, allow);
            return op == "move_viewport" ? Move(doc, r, allow) : AutoLayout(doc, r, allow);
        }

        /// <summary>
        /// 5.0.4 (B4b): which viewports (box + title) leave the drawing area - the frame read from the title
        /// block's lines, minus a title box in a corner - and by how many mm on each side. Read only.
        /// </summary>
        private sealed class DrawingFrame
        {
            private static readonly (string Name, int OutwardSign)[] Edges =
                { ("left", -1), ("right", 1), ("top", 1), ("bottom", -1) };

            public double[] Box, Area;
            public string Source, CornerCut;
            public string[] Sides;
            public double?[] BoundaryLines;
            public List<double[]> CornerBoxes;
            private bool HasBoundaryLine => BoundaryLines.Any(x => x.HasValue);
            public bool Framed => Sides.Any(x => x == "frame_line") || CornerCut != null;
            public string MeasuredFrom => Framed ? "title_block_frame"
                : HasBoundaryLine ? "title_block_box" : "title_block_box (no frame line found)";
            public JObject BoundaryLinesMm() => new JObject(Edges.Select((edge, i) =>
                new JProperty(edge.Name, BoundaryLines[i] is double position ? SheetLayoutRules.Mm(position) : (double?)null)));
            public string Warning => Box == null ? "title block and sheet outline bounds are unreadable; " + Source
                : Source.StartsWith("none", StringComparison.Ordinal) || Source.Contains("; warnings:") ? Source
                : !HasBoundaryLine && CornerCut == null ? "no drawing frame boundary was found in the title block lines" : null;

            public string RereadWarning(DrawingFrame before)
            {
                if (Warning is string warning) return warning;
                const double tolerance = SheetLayoutRules.ToleranceFeet;
                var lost = new List<string>();
                // An inward line still constrains the area. An outward replacement can hide a lost limit.
                for (int i = 0; i < Edges.Length; i++)
                    if (before.BoundaryLines[i] is double previous &&
                        (BoundaryLines[i] is not double current || (current - previous) * Edges[i].OutwardSign > tolerance))
                        lost.Add(Edges[i].Name + " edge");
                if (!SheetLayoutRules.HasDistinctCornerBoxMatches(before.CornerBoxes, CornerBoxes, tolerance))
                    lost.Add("corner title box");
                return lost.Count == 0 ? null : "drawing frame geometry could not be remeasured: " + string.Join(", ", lost);
            }
        }

        private static DrawingFrame ReadDrawingFrame(Document doc, ViewSheet sheet)
        {
            var frame = new DrawingFrame { Box = TitleBlock(doc, sheet) };
            List<double[]> lines = TitleBlockLines(doc, sheet, out frame.Source);
            frame.Area = SheetLayoutRules.AvoidCornerBoxes(lines,
                SheetLayoutRules.InnerFrame(lines, frame.Box, 0.6, SheetLayoutRules.ToleranceFeet, out frame.Sides, out frame.BoundaryLines),
                30 * SheetLayoutRules.FeetPerMm, SheetLayoutRules.ToleranceFeet, out frame.CornerBoxes, out frame.CornerCut);
            return frame;
        }

        private static IEnumerable<(Viewport Viewport, double[] Extent, string Warning)> ReadDrawingExtents(IEnumerable<Viewport> viewports)
        {
            foreach (Viewport viewport in viewports)
            {
                var footprint = ReadDrawingFootprint(viewport);
                yield return (viewport, footprint.Extent, footprint.Warning);
            }
        }

        private static JObject DrawingAreaCheck(Document doc,
            IEnumerable<(Viewport Viewport, double[] Extent, string Warning)> extents, DrawingFrame frame, double[] area)
        {
            if (area == null)
                return new JObject { ["state"] = "not_measured", ["why"] = frame.Warning };
            var outside = new JArray();
            var unreadable = new JArray();
            foreach (var (vp, e, why) in extents)
            {
                if (e == null)
                {
                    unreadable.Add(new JObject { ["viewport_id"] = Rid.Value(vp.Id), ["unique_id"] = vp.UniqueId, ["why"] = why });
                    continue;
                }
                double[] p = SheetLayoutRules.Protrusion(area, e, SheetLayoutRules.ToleranceFeet);
                if (p == null) continue;
                outside.Add(new JObject
                {
                    ["viewport_id"] = Rid.Value(vp.Id), ["unique_id"] = vp.UniqueId, ["view"] = (doc.GetElement(vp.ViewId) as View)?.Name,
                    ["outside_mm"] = new JObject
                    {
                        ["left"] = SheetLayoutRules.Mm(p[0]), ["right"] = SheetLayoutRules.Mm(p[1]),
                        ["top"] = SheetLayoutRules.Mm(p[2]), ["bottom"] = SheetLayoutRules.Mm(p[3])
                    },
                    ["over_title_box"] = frame.CornerBoxes.Any(b => SheetLayoutRules.Overlaps(
                        new List<KeyValuePair<string, double[]>> { new KeyValuePair<string, double[]>("v", e), new KeyValuePair<string, double[]>("t", b) },
                        SheetLayoutRules.ToleranceFeet).Count > 0)
                });
            }
            return new JObject
            {
                ["state"] = outside.Count > 0 ? "viewports_outside" : unreadable.Count > 0 ? "partial" : !frame.Framed ? "approximate" : "ok",
                ["drawing_area"] = SheetLayoutRules.RectMm(area),
                ["measured_from"] = frame.MeasuredFrom,
                ["frame_lines_read_from"] = frame.Source,
                ["frame_read_warning"] = frame.Warning,
                ["boundary_lines_mm"] = frame.BoundaryLinesMm(),
                ["title_box_in_corner"] = frame.CornerBoxes.Count == 0 ? null : new JArray(frame.CornerBoxes.Select(SheetLayoutRules.RectMm)),
                ["outside"] = outside,
                ["unreadable_viewports"] = unreadable.Count,
                ["unreadable"] = unreadable,
                ["means"] = "A viewport listed in outside has its box or title past the drawing area by those mm. approximate means no inner frame or corner title box cut was detected. A measured line within 0.5 mm of the title block box edge can still set a limit. boundary_lines_mm gives the measured line per side, or null for a box edge."
            };
        }

        private static ArchModelEdit Move(Document doc, JObject r, bool allow)
        {
            Viewport vp = ModelEditRunner.Need<Viewport>(doc, r, "viewport_id");
            HrzOwnership.Require(doc, vp, "viewport", allow, "allow_unmarked");
            if (vp.Pinned) throw new ArgumentException("viewport " + Rid.Value(vp.Id) + " is pinned; unpin it deliberately first.");
            ViewSheet sheet = doc.GetElement(vp.SheetId) as ViewSheet;
            bool corner = r["corner"] != null;
            JArray pt = (JArray)r[corner ? "corner" : "center"];
            double[] point = { pt[0].Value<double>() * SheetLayoutRules.FeetPerMm, pt[1].Value<double>() * SheetLayoutRules.FeetPerMm };
            string anchorKind = r.Value<string>("anchor") ?? "crop";
            bool byCrop = anchorKind == "crop", byInk = anchorKind == "ink";
            double[] inkBox = byInk ? ((JArray)r["ink_box"]).Select(t => t.Value<double>()).ToArray() : null;
            string why = null;
            double[] anchor = byCrop ? CropOnPaper(doc, vp, out why) : byInk ? InkNow(Box(vp), inkBox) : Box(vp);
            var assumptions = new List<JObject>();
            bool assumedBox = DocumentationDefaults.BoxAnchor(r, anchor != null, why, assumptions);
            if (assumedBox) { anchorKind = "box"; byCrop = false; anchor = Box(vp); }
            if (anchor == null)
                throw new ArgumentException(byCrop ? "anchor=crop cannot be used: " + why + ". Send anchor=box to place by the viewport box." : "the viewport box is unreadable.");
            double[] delta = SheetLayoutRules.Delta(anchor, point, corner);
            double[] area = sheet == null ? null : TitleBlock(doc, sheet);
            double[] extent = byInk ? SheetLayoutRules.Union(Extent(vp), anchor) : Extent(vp);
            double[] after = extent == null ? null : new[] { extent[0] + delta[0], extent[1] + delta[1], extent[2] + delta[0], extent[3] + delta[1] };
            if (area != null && after != null && !SheetLayoutRules.Inside(area, after, SheetLayoutRules.ToleranceFeet))
                throw new ArgumentException("at that point the viewport " + (byInk ? "(box, title and projected ink) " : "(box and title) ") +
                    SheetLayoutRules.RectMm(after).ToString(Newtonsoft.Json.Formatting.None) +
                    " would fall outside the title block " + SheetLayoutRules.RectMm(area).ToString(Newtonsoft.Json.Formatting.None) + ". Nothing was moved.");

            var edit = new ArchModelEdit();
            var planned = ModelEditRunner.Planned(vp, PlannedAction.Modify, r);
            // The same viewport box can resolve to a different crop or fall back to the box.
            // Bind the resolved move at full precision, independently of the rounded preview in mm.
            planned.BeforeValues["resolved_anchor"] = anchorKind;
            planned.BeforeValues["move_feet"] = new JArray(delta[0], delta[1]).ToString(Newtonsoft.Json.Formatting.None);
            edit.Planned.Add(planned);
            edit.Summary["operation"] = "move_viewport";
            edit.Summary["anchor"] = anchorKind;
            AssumptionNotes.Stamp(edit.Summary, assumptions);
            edit.Summary["before"] = Row(doc, vp);
            if (byInk)
            {
                edit.Summary["ink_now"] = SheetLayoutRules.RectMm(anchor);
                edit.Summary["ink_means"] = "the printed drawing measured by pdf_qa, carried with the box centre; valid while the view's crop, scale and annotations are as they were when the PDF was printed";
            }
            edit.Summary["move_mm"] = new JArray(SheetLayoutRules.Mm(delta[0]), SheetLayoutRules.Mm(delta[1]));
            ElementId id = vp.Id;
            edit.Apply = d =>
            {
                Viewport v = (Viewport)d.GetElement(id);
                XYZ c = v.GetBoxCenter();
                v.SetBoxCenter(new XYZ(c.X + delta[0], c.Y + delta[1], c.Z));
            };
            edit.Verify = d =>
            {
                var check = new PostconditionCheck("position", "inside_title_block", "no_overlap");
                Viewport v = (Viewport)d.GetElement(id);
                var footprint = ReadDrawingFootprint(v);
                double[] a2 = byCrop ? CropOnPaper(d, v, out _) : byInk ? InkNow(footprint.Box, inkBox) : footprint.Box;
                if (a2 == null) check.Unreadable("position", new JArray(pt), "the anchor could not be re-read");
                else
                {
                    double[] got = corner ? new[] { a2[0], a2[1] } : SheetLayoutRules.Center(a2);
                    double err = Math.Max(Math.Abs(got[0] - point[0]), Math.Abs(got[1] - point[1]));
                    check.Record("position", new JArray(pt), new JArray(SheetLayoutRules.Mm(got[0]), SheetLayoutRules.Mm(got[1])), err <= SheetLayoutRules.ToleranceFeet);
                }
                var sheetAfter = d.GetElement(v.SheetId) as ViewSheet;
                double[] areaAfter = sheetAfter == null ? null : TitleBlock(d, sheetAfter);
                double[] boundaryExtent = byInk && footprint.Extent != null
                    ? SheetLayoutRules.Union(footprint.Extent, a2) : footprint.Extent;
                if (footprint.Extent == null || areaAfter == null)
                    check.Unreadable("inside_title_block", true, footprint.Warning ?? "the title block and sheet outline bounds are unreadable");
                else
                    check.Compare("inside_title_block", true, SheetLayoutRules.Inside(areaAfter, boundaryExtent, SheetLayoutRules.ToleranceFeet));
                VerifyMoveOverlaps(d, sheetAfter, v, footprint.Extent, footprint.Warning, check, edit.Evidence);
                edit.Evidence["viewport"] = Row(d, v, footprint.Box, footprint.Label);
                if (byInk && a2 != null)
                {
                    edit.Evidence["ink_after"] = SheetLayoutRules.RectMm(a2);
                    edit.Evidence["ink_remeasured"] = false;
                    edit.Evidence["ink_note"] = "ink_after is the pdf_qa measurement carried with the box, not a new measurement: export the sheet again and run pdf_qa (viewports: true) to confirm where the drawing printed";
                }
                edit.Evidence["title_check"] = footprint.Warning == null
                    ? SheetPaperRules.LabelCheck(footprint.Label, areaAfter, DatumHeadsOn(d, sheetAfter))
                    : new JObject { ["state"] = "not_measured", ["why"] = footprint.Warning };
                return check;
            };
            return edit;
        }

        /// <summary>The printed drawing's rectangle now: the box centre plus pdf_qa's ink_from_box_center_mm (sheet feet).</summary>
        private static double[] InkNow(double[] box, double[] inkFromCenterMm)
            => box == null ? null : SheetPaperRules.InkOnSheet(SheetLayoutRules.Center(box), inkFromCenterMm);

        private static void VerifyMoveOverlaps(Document doc, ViewSheet sheet, Viewport moved, double[] movedExtent,
            string movedWarning, PostconditionCheck check, JObject evidence)
        {
            if (sheet == null)
            {
                check.Unreadable("no_overlap", 0, "the viewport's sheet is unreadable");
                return;
            }

            string movedKey = Key(moved);
            var rects = new List<KeyValuePair<string, double[]>>();
            var unreadable = new JArray();
            void AddFootprint(ElementId id, Viewport viewport, double[] extent, string warning)
            {
                if (extent != null)
                    rects.Add(new KeyValuePair<string, double[]>(Key(viewport), extent));
                else
                    unreadable.Add(new JObject { ["viewport_id"] = Rid.Value(id), ["unique_id"] = viewport?.UniqueId, ["why"] = warning });
            }

            foreach (ElementId id in sheet.GetAllViewports().Concat(new[] { moved.Id }).Distinct().OrderBy(id => Rid.Value(id)))
            {
                if (id == moved.Id)
                    AddFootprint(id, moved, movedExtent, movedWarning);
                else if (doc.GetElement(id) is Viewport other)
                {
                    var footprint = ReadDrawingFootprint(other);
                    AddFootprint(id, other, footprint.Extent, footprint.Warning);
                }
                else
                    AddFootprint(id, null, null, "viewport could not be read from the sheet");
            }

            var overlaps = SheetLayoutRules.Overlaps(rects, SheetLayoutRules.ToleranceFeet).Where(p => p.Contains(movedKey)).ToList();
            if (overlaps.Count > 0) evidence["overlaps"] = new JArray(overlaps.Select(p => new JArray(p)));
            if (unreadable.Count > 0)
            {
                evidence["unreadable_viewports"] = unreadable;
                check.Unreadable("no_overlap", 0, string.Join("; ", unreadable.Select(row =>
                    "viewport " + (string)row["viewport_id"] + ": " + (string)row["why"])));
            }
            else check.Compare("no_overlap", 0, overlaps.Count);
        }

        /// <summary>The datum heads (grid/level bubbles shown) every viewport of the sheet prints, in sheet feet.</summary>
        internal static List<DatumHead> DatumHeadsOn(Document doc, ViewSheet sheet)
        {
            var heads = new List<DatumHead>();
            if (sheet == null) return heads;
            foreach (Viewport vp in ViewportsOf(doc, sheet))
            {
                try
                {
                    if (!(doc.GetElement(vp.ViewId) is View view) || view is View3D) continue;
                    IList<TransformWithBoundary> tw = view.GetModelToProjectionTransforms();
                    if (tw == null || tw.Count != 1) continue;
                    Transform m2p = tw[0].GetModelToProjectionTransform(), p2s = vp.GetProjectionToSheetTransform();
                    foreach (DatumPlane d in new FilteredElementCollector(doc, view.Id).WhereElementIsNotElementType().OfType<DatumPlane>().Where(x => x is Grid || x is Level))
                    {
                        if (heads.Count >= 500) return heads;
                        Curve c;
                        try { c = d.GetCurvesInView(DatumExtentType.ViewSpecific, view).FirstOrDefault(); } catch { continue; }
                        if (c == null) continue;
                        foreach (DatumEnds end in new[] { DatumEnds.End0, DatumEnds.End1 })
                        {
                            bool shown;
                            try { shown = d.IsBubbleVisibleInView(end, view); } catch { shown = false; }
                            if (!shown) continue;
                            XYZ s = p2s.OfPoint(m2p.OfPoint(c.GetEndPoint(end == DatumEnds.End0 ? 0 : 1)));
                            heads.Add(new DatumHead { DatumId = Rid.Value(d.Id), Name = d.Name, ViewportId = Rid.Value(vp.Id), X = s.X, Y = s.Y });
                        }
                    }
                }
                catch { }
            }
            return heads;
        }

        /// <summary>5.2 (P2): the title against the sheet and the datum heads printed on it. A warning, never a refusal.</summary>
        internal static JObject TitleCheck(Document doc, ViewSheet sheet, double[] label)
            => SheetPaperRules.LabelCheck(label, sheet == null ? null : TitleBlock(doc, sheet), DatumHeadsOn(doc, sheet));

        /// <summary>Overlaps between the viewport and the others on its sheet (box + title), by viewport id.</summary>
        private static List<string[]> OverlapsOn(Document d, ViewSheet sheet, Viewport moved)
        {
            if (sheet == null) return new List<string[]>();
            var rects = ViewportsOf(d, sheet).Select(v => new KeyValuePair<string, double[]>(Rid.Value(v.Id).ToString(System.Globalization.CultureInfo.InvariantCulture), Extent(v))).ToList();
            string key = Rid.Value(moved.Id).ToString(System.Globalization.CultureInfo.InvariantCulture);
            return SheetLayoutRules.Overlaps(rects, SheetLayoutRules.ToleranceFeet).Where(p => p.Contains(key)).ToList();
        }

        private static ArchModelEdit AutoLayout(Document doc, JObject r, bool allow)
        {
            ViewSheet sheet = ModelEditRunner.Need<ViewSheet>(doc, r, "sheet_id");
            List<Viewport> vps = ViewportsOf(doc, sheet);
            bool repair = r.Value<bool?>("repair_outside") == true;
            if (vps.Count == 0)
            {
                if (repair) return NoRepairNeeded();
                throw new ArgumentException("sheet " + sheet.SheetNumber + " has no viewport to lay out.");
            }
            double[] margins = r["margins"] is JArray m ? m.Select(t => t.Value<double>() * SheetLayoutRules.FeetPerMm).ToArray()
                                                        : repair ? new double[4] : SheetLayoutRules.DefaultMarginsMm.Select(v => v * SheetLayoutRules.FeetPerMm).ToArray();
            double notes = (r.Value<double?>("notes_width") ?? 0) * SheetLayoutRules.FeetPerMm;
            double gap = (r.Value<double?>("gap") ?? SheetLayoutRules.DefaultGapMm) * SheetLayoutRules.FeetPerMm;
            // Explicit margins inset the measured drawing frame. Repair has no default inset,
            // so a viewport accepted by drawing_area_check is left alone.
            DrawingFrame frame = ReadDrawingFrame(doc, sheet);
            if (repair && frame.Warning != null)
                throw new ArgumentException("drawing frame could not be read for sheet " + sheet.SheetNumber + ": " + frame.Warning + ". Nothing was moved.");
            double[] usable = SheetLayoutRules.Usable(frame.Area, margins, notes);
            JObject AreaBasis(DrawingFrame measured) => new JObject
            {
                ["measured_from"] = measured.MeasuredFrom,
                ["title_box_in_corner"] = measured.CornerBoxes.Count == 0 ? null : new JArray(measured.CornerBoxes.Select(SheetLayoutRules.RectMm)),
                ["title_box_cut"] = measured.CornerCut,
                ["title_block_box"] = SheetLayoutRules.RectMm(measured.Box),
                ["frame"] = SheetLayoutRules.RectMm(measured.Area),
                ["frame_lines_read_from"] = measured.Source,
                ["frame_read_warning"] = measured.Warning,
                ["boundary_lines_mm"] = measured.BoundaryLinesMm(),
                ["sides"] = new JObject { ["left"] = measured.Sides[0], ["right"] = measured.Sides[1], ["top"] = measured.Sides[2], ["bottom"] = measured.Sides[3] },
                ["margins_mm"] = new JArray(margins.Select(SheetLayoutRules.Mm)),
                ["notes_width_mm"] = SheetLayoutRules.Mm(notes),
                ["means"] = "usable_area = drawing frame minus margins [left, right, top, bottom] and notes_width. Repair uses zero default margins, matching drawing_area_check; rows use 10 mm. Explicit margins always inset the frame."
            };
            var extents = new Dictionary<string, double[]>(StringComparer.Ordinal);
            var items = new List<LayoutItem>();
            var before = ReadDrawingExtents(vps).ToList();
            foreach (var (vp, e, why) in before)
            {
                string key = Key(vp);
                if (e == null) throw new ArgumentException("viewport " + key + ": " + why + ". Nothing was moved.");
                extents[key] = e;
                items.Add(new LayoutItem { Key = key, Width = e[2] - e[0], Height = e[3] - e[1] });
            }
            string refusal;
            Dictionary<string, double[]> targets = repair
                ? SheetLayoutRules.RepairOutside(extents.ToList(), usable, SheetLayoutRules.ToleranceFeet, out refusal)
                : SheetLayoutRules.Rows(items, usable, gap, SheetLayoutRules.ToleranceFeet, out refusal);
            if (targets == null) throw new ArgumentException(refusal + ". Nothing was moved.");
            if (targets.Count == 0) return NoRepairNeeded(DrawingAreaCheck(doc, before, frame, usable));
            foreach (Viewport vp in vps.Where(v => targets.ContainsKey(Key(v))))
            {
                HrzOwnership.Require(doc, vp, "viewport", allow, "allow_unmarked");
                if (vp.Pinned) throw new ArgumentException("viewport " + Key(vp) + " is pinned. Nothing was moved.");
            }

            var edit = new ArchModelEdit();
            foreach (Viewport vp in vps.Where(v => targets.ContainsKey(Key(v))))
            {
                var planned = ModelEditRunner.Planned(vp, PlannedAction.Modify, r);
                string key = Key(vp);
                planned.BeforeValues["paper_extent"] = new JArray(extents[key]).ToString(Newtonsoft.Json.Formatting.None);
                planned.ProposedValues["paper_extent"] = new JArray(targets[key]).ToString(Newtonsoft.Json.Formatting.None);
                planned.ProposedValues["usable_area"] = new JArray(usable).ToString(Newtonsoft.Json.Formatting.None);
                edit.Planned.Add(planned);
            }
            edit.Summary["operation"] = "auto_layout";
            edit.Summary["strategy"] = repair ? "minimum_translation" : "rows";
            edit.Summary["drawing_area_before"] = DrawingAreaCheck(doc, before, frame, usable);
            edit.Summary["sheet"] = sheet.SheetNumber + " - " + sheet.Name;
            edit.Summary["usable_area"] = SheetLayoutRules.RectMm(usable);
            edit.Summary["usable_area_basis"] = AreaBasis(frame);
            edit.Summary["placements"] = new JArray(targets.Select(kv => new JObject { ["viewport_id"] = long.Parse(kv.Key, System.Globalization.CultureInfo.InvariantCulture), ["extent"] = SheetLayoutRules.RectMm(kv.Value) }));
            edit.Apply = d =>
            {
                foreach (KeyValuePair<string, double[]> kv in targets)
                {
                    var v = (Viewport)d.GetElement(Rid.Make(long.Parse(kv.Key, System.Globalization.CultureInfo.InvariantCulture)));
                    double[] from = extents[kv.Key];
                    XYZ c = v.GetBoxCenter();
                    double dx = kv.Value[0] - from[0], dy = kv.Value[1] - from[1];
                    v.SetBoxCenter(new XYZ(c.X + dx, c.Y + dy, c.Z));
                }
            };
            edit.Verify = d =>
            {
                var check = new PostconditionCheck("positions", "inside_usable_area", "no_overlap", "drawing_area_after");
                var rows = new JArray();
                DrawingFrame frameAfter = repair ? ReadDrawingFrame(d, sheet) : frame;
                string areaWarning = repair ? frameAfter.RereadWarning(frame) : null;
                double[] usableAfter = usable;
                if (repair)
                {
                    usableAfter = null;
                    if (areaWarning == null)
                    {
                        try { usableAfter = SheetLayoutRules.Usable(frameAfter.Area, margins, notes); }
                        catch (ArgumentException ex) { areaWarning = ex.Message; }
                    }
                }
                // Both area checks use the same frame and the same footprints from this pass.
                var after = ReadDrawingExtents(ViewportsOf(d, sheet)).ToList();
                var allRects = after.ToDictionary(row => Key(row.Viewport), row => row.Extent, StringComparer.Ordinal);
                int placedOk = 0, inside = 0;
                foreach (KeyValuePair<string, double[]> kv in targets)
                {
                    allRects.TryGetValue(kv.Key, out double[] e);
                    bool at = e != null && ViewCropGeometry.Matches(kv.Value, e, SheetLayoutRules.ToleranceFeet);
                    bool? inArea = usableAfter == null || e == null ? (bool?)null
                        : SheetLayoutRules.Inside(usableAfter, e, SheetLayoutRules.ToleranceFeet);
                    if (at) placedOk++;
                    if (inArea == true) inside++;
                    rows.Add(new JObject { ["viewport_id"] = long.Parse(kv.Key, System.Globalization.CultureInfo.InvariantCulture), ["extent"] = SheetLayoutRules.RectMm(e), ["at_target"] = at, ["inside"] = inArea });
                }
                List<string[]> overlaps = repair
                    ? SheetLayoutRules.NewOverlaps(extents.ToList(), allRects.ToList(), SheetLayoutRules.ToleranceFeet)
                    : SheetLayoutRules.Overlaps(allRects.ToList(), SheetLayoutRules.ToleranceFeet);
                check.Compare("positions", targets.Count, placedOk);
                JObject areaAfter;
                if (areaWarning == null)
                {
                    check.Compare("inside_usable_area", targets.Count, inside);
                    areaAfter = DrawingAreaCheck(d, after, frameAfter, usableAfter);
                    bool areaOk = areaAfter["outside"] is JArray outside && outside.Count == 0 && areaAfter.Value<int?>("unreadable_viewports") == 0;
                    check.Record("drawing_area_after", "all readable and inside", areaAfter, areaOk);
                }
                else
                {
                    areaAfter = new JObject { ["state"] = "not_measured", ["why"] = areaWarning };
                    check.Unreadable("inside_usable_area", targets.Count, areaWarning);
                    check.Unreadable("drawing_area_after", "all readable and inside", areaWarning);
                }
                edit.Evidence["drawing_area_after"] = areaAfter;
                var unreadable = after.Where(row => row.Extent == null).ToList();
                if (unreadable.Count > 0)
                    check.Unreadable("no_overlap", 0, string.Join("; ", unreadable.Select(row =>
                        "viewport " + Key(row.Viewport) + ": " + row.Warning)));
                else check.Compare("no_overlap", 0, overlaps.Count);
                edit.Evidence["viewports"] = rows;
                edit.Evidence["usable_area"] = SheetLayoutRules.RectMm(usableAfter);
                edit.Evidence["usable_area_basis"] = AreaBasis(frameAfter);
                if (overlaps.Count > 0) edit.Evidence["overlaps"] = new JArray(overlaps.Select(p => new JArray(p)));
                else edit.Evidence.Remove("overlaps");
                return check;
            };
            return edit;
        }

        private static ArchModelEdit NoRepairNeeded(JObject areaCheck = null)
        {
            var result = new JObject {
                ["operation"] = "auto_layout", ["message"] = "nada que reparar", ["transaction_status"] = "not_started",
                ["changes_applied"] = false
            };
            if (areaCheck != null) result["drawing_area_check"] = areaCheck;
            return new ArchModelEdit { ReadResult = result };
        }
    }
}
