// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// horizun_query_planimetry, four more READ modes (4.2 front N2). No transaction is
// opened by any of them; the arithmetic is Core/NoteLeaderRules.
//
//   leader_targets        what each text-note leader end points at: the element the view's
//                         own looking direction meets first, in the host or in a link,
//                         with category, type, the layer of its compound structure, who
//                         changed it last and the distance.
//   annotation_staleness  notes whose element changed or was deleted: the changed /
//                         deleted UniqueIds the caller got from a diff are crossed with
//                         what each leader points at AND with the origin a note made by
//                         note_from_model carries. A note with neither is "unknown", and
//                         a clean answer needs zero unknown.
//   sheet_conflicts       overlaps in SHEET coordinates, from real geometry: text boxes,
//                         leader and dimension LINES (not the box of the whole run), tag
//                         boxes, and - on request - the symbols of linked families,
//                         approximated by the bounding box of each linked instance.
//   free_spot             the nearest free place for a w x h box around a point of the
//                         sheet, computed from that same geometry. Nothing is regenerated.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public sealed partial class QueryPlanimetryCommand
    {
        private const double FtPerMm = 1.0 / 304.8;

        private static CommandResult NotesMode(Document doc, JObject request, string mode, int maxRows, JArray rows)
        {
            string invalid = NoteLeaderRules.ValidateRead(mode, request);
            if (invalid != null) return CommandResult.FailCoded(ErrorCodes.EsquemaInvalido, invalid);
            foreach (JProperty p in request.Properties())
                if (p.Name != "time_budget_s" && !NoteLeaderRules.NoteReadFieldAllowed(mode, p.Name))
                    return CommandResult.Fail(p.Name + " is not applicable to mode=" + mode + ". Nothing ran.");
            try
            {
                switch (mode)
                {
                    case "leader_targets": return LeaderTargets(doc, request, maxRows, rows);
                    case "annotation_staleness": return Staleness(doc, request, maxRows, rows);
                    case "sheet_conflicts": return SheetConflicts(doc, request, maxRows);
                    case "view_ray": return ViewRay(doc, request, rows);
                    case "section_mark_diagnosis": return SectionMarkDiagnosis(doc, request, maxRows);
                    default: return FreeSpot(doc, request);
                }
            }
            catch (ArgumentException ex) { return CommandResult.Fail(ex.Message); }
        }

        private static JArray Mm3(XYZ p) => new JArray(Math.Round(p.X / FtPerMm, 2), Math.Round(p.Y / FtPerMm, 2), Math.Round(p.Z / FtPerMm, 2));

        private static T NeedAs<T>(Document doc, long id, string what) where T : Element =>
            (Rid.CanRepresent(id) ? doc.GetElement(Rid.Make(id)) : null) as T ?? throw new ArgumentException(what + " " + id + " is not a " + typeof(T).Name + " of this document.");

        // ---- sheet geometry: conflicts and free places ------------------------------------------------

        private sealed class Obstacle
        {
            public string Kind, Source, Label; public long Id; public long ViewId;
            public double[] Box;           // sheet AABB (feet)
            public double[] P, Q;          // a segment (feet), or null
            public bool Fixed;             // viewport frames and linked symbols: reported, never "the culprit" to move
        }

        private static double[] SheetBox(Transform toSheet, BoundingBoxXYZ b, Transform boxToModel)
        {
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            foreach (double x in new[] { b.Min.X, b.Max.X }) foreach (double y in new[] { b.Min.Y, b.Max.Y }) foreach (double z in new[] { b.Min.Z, b.Max.Z })
                    {
                        XYZ p = boxToModel != null ? boxToModel.OfPoint(new XYZ(x, y, z)) : new XYZ(x, y, z);
                        XYZ s = toSheet.OfPoint(p);
                        minX = Math.Min(minX, s.X); maxX = Math.Max(maxX, s.X); minY = Math.Min(minY, s.Y); maxY = Math.Max(maxY, s.Y);
                    }
            return new[] { minX, minY, maxX, maxY };
        }

        private static List<Obstacle> Obstacles(Document doc, ViewSheet sheet, bool linked, JArray notMeasured, out int viewsRead)
        {
            var list = new List<Obstacle>();
            viewsRead = 0;
            foreach (Viewport vp in PlanimetryReadBudget.CheckEach(new FilteredElementCollector(doc, sheet.Id).OfClass(typeof(Viewport)).Cast<Viewport>()).OrderBy(x => Rid.Value(x.Id)))
            {
                var view = doc.GetElement(vp.ViewId) as View;
                if (view == null) continue;
                Transform toSheet;
                try { toSheet = vp.GetProjectionToSheetTransform(); }
                catch (Exception ex) { notMeasured.Add(new JObject { ["viewport_id"] = Rid.Value(vp.Id), ["why"] = "no projection-to-sheet transform: " + ex.Message }); continue; }
                viewsRead++;
                Outline frame = vp.GetBoxOutline();
                list.Add(new Obstacle { Kind = "viewport_frame", Source = "host", Id = Rid.Value(vp.Id), ViewId = Rid.Value(view.Id), Fixed = true, Label = view.Name,
                    Box = new[] { frame.MinimumPoint.X, frame.MinimumPoint.Y, frame.MaximumPoint.X, frame.MaximumPoint.Y } });
                foreach (Element e in new FilteredElementCollector(doc, view.Id).WhereElementIsNotElementType())
                {
                    PlanimetryReadBudget.Checkpoint();
                    try
                    {
                        if (e is TextNote tn)
                        {
                            BoundingBoxXYZ b = AnnotationBoxReader.Fixed(tn.get_BoundingBox(view), tn, view);
                            if (b == null) { notMeasured.Add(new JObject { ["element_id"] = Rid.Value(e.Id), ["why"] = "text note without a view box" }); continue; }
                            list.Add(new Obstacle { Kind = "text_note", Source = "host", Id = Rid.Value(e.Id), ViewId = Rid.Value(view.Id), Box = SheetBox(toSheet, b, b.Transform), Label = Trim(tn.Text) });
                            foreach (Leader l in tn.GetLeaders())
                            {
                                XYZ a = toSheet.OfPoint(l.Anchor), el = toSheet.OfPoint(l.Elbow), en = toSheet.OfPoint(l.End);
                                bool straight = l.LeaderShape == LeaderShape.Straight;
                                if (!straight) list.Add(new Obstacle { Kind = "leader_line", Source = "host", Id = Rid.Value(e.Id), ViewId = Rid.Value(view.Id), P = new[] { a.X, a.Y }, Q = new[] { el.X, el.Y } });
                                list.Add(new Obstacle { Kind = "leader_line", Source = "host", Id = Rid.Value(e.Id), ViewId = Rid.Value(view.Id), P = straight ? new[] { a.X, a.Y } : new[] { el.X, el.Y }, Q = new[] { en.X, en.Y } });
                            }
                        }
                        else if (e is IndependentTag tag)
                        {
                            BoundingBoxXYZ b = AnnotationBoxReader.Fixed(tag.get_BoundingBox(view), tag, view);
                            if (b == null) { notMeasured.Add(new JObject { ["element_id"] = Rid.Value(e.Id), ["why"] = "tag without a view box" }); continue; }
                            list.Add(new Obstacle { Kind = "tag", Source = "host", Id = Rid.Value(e.Id), ViewId = Rid.Value(view.Id), Box = SheetBox(toSheet, b, b.Transform) });
                        }
                        else if (e is Dimension dim)
                        {
                            if (dim.Curve is Line line)
                            {
                                XYZ s0 = toSheet.OfPoint(line.GetEndPoint(0)), s1 = toSheet.OfPoint(line.GetEndPoint(1));
                                list.Add(new Obstacle { Kind = "dimension_line", Source = "host", Id = Rid.Value(e.Id), ViewId = Rid.Value(view.Id), P = new[] { s0.X, s0.Y }, Q = new[] { s1.X, s1.Y } });
                            }
                            else notMeasured.Add(new JObject { ["element_id"] = Rid.Value(e.Id), ["why"] = "dimension without a straight line" });
                            XYZ tp = dim.TextPosition;
                            if (tp != null)
                            {
                                XYZ s = toSheet.OfPoint(tp);
                                double halfW = Math.Max(2.0, (dim.ValueString ?? "").Length * 1.1) * FtPerMm, halfH = 1.6 * FtPerMm;
                                list.Add(new Obstacle { Kind = "dimension_text", Source = "host", Id = Rid.Value(e.Id), ViewId = Rid.Value(view.Id), Box = new[] { s.X - halfW, s.Y - halfH, s.X + halfW, s.Y + halfH }, Label = dim.ValueString });
                            }
                        }
                    }
                    catch (Exception ex) { notMeasured.Add(new JObject { ["element_id"] = Rid.Value(e.Id), ["why"] = ex.Message }); }
                }
                if (linked) AddLinkedSymbols(doc, view, toSheet, vp, list, notMeasured);
            }
            return list;
        }

        private static readonly BuiltInCategory[] SymbolCategories =
        {
            BuiltInCategory.OST_LightingFixtures, BuiltInCategory.OST_LightingDevices, BuiltInCategory.OST_ElectricalFixtures,
            BuiltInCategory.OST_ElectricalEquipment, BuiltInCategory.OST_FireAlarmDevices, BuiltInCategory.OST_DataDevices,
            BuiltInCategory.OST_CommunicationDevices, BuiltInCategory.OST_SecurityDevices, BuiltInCategory.OST_Sprinklers, BuiltInCategory.OST_MechanicalEquipment
        };

        private static void AddLinkedSymbols(Document doc, View view, Transform toSheet, Viewport vp, List<Obstacle> list, JArray notMeasured)
        {
            if (!view.CropBoxActive) { notMeasured.Add(new JObject { ["view_id"] = Rid.Value(view.Id), ["why"] = "linked symbols need an active crop to bound the search" }); return; }
            BoundingBoxXYZ crop = view.CropBox;
            foreach (RevitLinkInstance inst in new FilteredElementCollector(doc, view.Id).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>())
            {
                PlanimetryReadBudget.Checkpoint();
                Document ld = inst.GetLinkDocument();
                if (ld == null) { notMeasured.Add(new JObject { ["link_instance_id"] = Rid.Value(inst.Id), ["why"] = "link not loaded" }); continue; }
                Transform toHost = inst.GetTotalTransform(), toLink = toHost.Inverse;
                double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
                foreach (double x in new[] { crop.Min.X, crop.Max.X }) foreach (double y in new[] { crop.Min.Y, crop.Max.Y }) foreach (double z in new[] { -1000.0, 1000.0 })
                        {
                            XYZ p = toLink.OfPoint(crop.Transform.OfPoint(new XYZ(x, y, z)));
                            minX = Math.Min(minX, p.X); maxX = Math.Max(maxX, p.X); minY = Math.Min(minY, p.Y); maxY = Math.Max(maxY, p.Y); minZ = Math.Min(minZ, p.Z); maxZ = Math.Max(maxZ, p.Z);
                        }
                var outline = new Outline(new XYZ(minX, minY, minZ), new XYZ(maxX, maxY, maxZ));
                foreach (BuiltInCategory bic in SymbolCategories)
                    foreach (FamilyInstance fi in new FilteredElementCollector(ld).OfCategory(bic).WhereElementIsNotElementType().WherePasses(new BoundingBoxIntersectsFilter(outline)).OfType<FamilyInstance>())
                    {
                        PlanimetryReadBudget.Checkpoint();
                        BoundingBoxXYZ b = fi.get_BoundingBox(null);
                        if (b == null) { notMeasured.Add(new JObject { ["link_instance_id"] = Rid.Value(inst.Id), ["element_id"] = Rid.Value(fi.Id), ["why"] = "linked family without a bounding box" }); continue; }
                        list.Add(new Obstacle
                        {
                            Kind = "linked_symbol", Source = "link:" + Rid.Value(inst.Id).ToString(CultureInfo.InvariantCulture), Id = Rid.Value(fi.Id), ViewId = Rid.Value(view.Id), Fixed = true,
                            Label = fi.Category?.Name, Box = SheetBox(toSheet, b, toHost.Multiply(b.Transform))
                        });
                    }
            }
        }

        private static string Trim(string s) { s = (s ?? "").Replace("\r", " ").Replace("\n", " "); return s.Length > 40 ? s.Substring(0, 40) + "..." : s; }

        private static JArray Rect(double[] b) => new JArray(Math.Round(b[0] / FtPerMm, 1), Math.Round(b[1] / FtPerMm, 1), Math.Round(b[2] / FtPerMm, 1), Math.Round(b[3] / FtPerMm, 1));

        private static JObject Coverage(JArray notMeasured, bool linked, int viewsRead) => new JObject
        {
            ["frame"] = "sheet, paper millimetres [minX, minY, maxX, maxY]",
            ["views_read"] = viewsRead,
            ["geometry"] = "text and tag boxes from the view bounding box; leader and dimension LINES as segments; dimension text boxes estimated from the value string; viewport frames from the box outline",
            ["linked_symbols"] = linked ? "approximated by the bounding box of each linked family instance of the lighting/electrical/fire/data/security/sprinkler/mechanical categories" : "not read (include_linked_symbols=false)",
            ["approximation_limits"] = "model boxes and estimated dimension text can produce false positives and false negatives; linked symbol visibility and nested links are not verified",
            ["not_measured"] = notMeasured, ["not_measured_count"] = notMeasured.Count,
            ["clean_means"] = "no conflict among the geometry above; it says nothing about what was not measured"
        };

        private static CommandResult SheetConflicts(Document doc, JObject request, int maxRows)
        {
            ViewSheet sheet = NeedAs<ViewSheet>(doc, request.Value<long>("sheet_id"), "sheet");
            bool linked = request.Value<bool?>("include_linked_symbols") ?? false;
            double gap = (request.Value<double?>("gap_mm") ?? 0.5) * FtPerMm;
            var nm = new JArray(); int viewsRead;
            List<Obstacle> all = Obstacles(doc, sheet, linked, nm, out viewsRead);
            var boxes = all.Where(o => o.Box != null && o.Kind != "viewport_frame").ToList();
            var segs = all.Where(o => o.P != null).ToList();
            var rows = new JArray(); int total = 0;
            Action<Obstacle, Obstacle, string> add = (a, b, how) =>
            {
                total++;
                if (rows.Count >= maxRows) return;
                rows.Add(new JObject
                {
                    ["entity_kind"] = "sheet_conflict", ["how"] = how,
                    ["approximate"] = a.Kind == "linked_symbol" || b.Kind == "linked_symbol", ["gap_mm"] = gap / FtPerMm,
                    ["a"] = new JObject { ["kind"] = a.Kind, ["id"] = a.Id, ["source"] = a.Source, ["view_id"] = a.ViewId, ["label"] = a.Label, ["box_mm"] = a.Box != null ? Rect(a.Box) : null },
                    ["b"] = new JObject { ["kind"] = b.Kind, ["id"] = b.Id, ["source"] = b.Source, ["view_id"] = b.ViewId, ["label"] = b.Label, ["box_mm"] = b.Box != null ? Rect(b.Box) : null }
                });
            };
            for (int i = 0; i < boxes.Count; i++)
                for (int j = i + 1; j < boxes.Count; j++)
                {
                    if (boxes[i].Id == boxes[j].Id && boxes[i].Source == boxes[j].Source) continue;
                    if (boxes[i].Fixed && boxes[j].Fixed) continue;      // two fixed symbols are not a documentation fault of ours
                    if (NoteLeaderRules.Overlap(boxes[i].Box, boxes[j].Box, gap)) add(boxes[i], boxes[j], "box_box");
                }
            foreach (Obstacle s in segs)
                foreach (Obstacle b in boxes)
                {
                    PlanimetryReadBudget.Checkpoint();
                    if (s.Id == b.Id && b.Source == "host") continue;       // a note's own leader touches its own box by construction
                    if (NoteLeaderRules.SegmentHitsBox(s.P, s.Q, b.Box, gap)) add(s, b, "line_box");
                }
            return CommandResult.Ok(new JObject
            {
                ["mode"] = "sheet_conflicts", ["sheet_id"] = Rid.Value(sheet.Id), ["sheet_number"] = sheet.SheetNumber,
                ["obstacles"] = all.Count, ["conflicts_total"] = total, ["truncated"] = total > rows.Count, ["rows"] = rows,
                ["coverage"] = Coverage(nm, linked, viewsRead)
            });
        }

        private static CommandResult FreeSpot(Document doc, JObject request)
        {
            View view = NeedAs<View>(doc, request.Value<long>("view_id"), "view");
            Viewport vp = new FilteredElementCollector(doc).OfClass(typeof(Viewport)).Cast<Viewport>().FirstOrDefault(x => x.ViewId == view.Id);
            if (vp == null) throw new ArgumentException("free_spot works in sheet coordinates: view " + Rid.Value(view.Id) + " is not placed on a sheet.");
            var sheet = (ViewSheet)doc.GetElement(vp.SheetId);
            bool linked = request.Value<bool?>("include_linked_symbols") ?? false;
            double gap = (request.Value<double?>("gap_mm") ?? 1.0) * FtPerMm, step = (request.Value<double?>("step_mm") ?? 2.0) * FtPerMm;
            double maxRadius = (request.Value<double?>("max_radius_mm") ?? 150.0) * FtPerMm;
            double w = request["size_mm"][0].Value<double>() * FtPerMm, h = request["size_mm"][1].Value<double>() * FtPerMm;
            double[] near = { request["near_mm"][0].Value<double>() * FtPerMm, request["near_mm"][1].Value<double>() * FtPerMm };
            var nm = new JArray(); int viewsRead;
            List<Obstacle> all = Obstacles(doc, sheet, linked, nm, out viewsRead);
            // Own frame is the BOUNDS; other viewports' frames and every annotation are obstacles. Line obstacles become thin boxes.
            Obstacle own = all.First(o => o.Kind == "viewport_frame" && o.Id == Rid.Value(vp.Id));
            var exact = all.Where(o => o.P != null).ToList();          // lines are tested exactly, not by their bounding box
            var rejected = new List<double[]>();
            double[] spot = null;
            for (int attempt = 0; attempt < 400 && spot == null; attempt++)
            {
                var blocks = all.Where(o => o != own && o.Box != null).Select(o => o.Box).Concat(rejected).ToList();
                double[] best = NoteLeaderRules.FreeSpot(blocks, w, h, near, own.Box, gap, step, maxRadius);
                if (best == null) break;
                double[] cand = { best[0] - w / 2, best[1] - h / 2, best[0] + w / 2, best[1] + h / 2 };
                if (exact.Any(l => NoteLeaderRules.SegmentHitsBox(l.P, l.Q, cand, gap)))
                    rejected.Add(new[] { best[0] - step / 2, best[1] - step / 2, best[0] + step / 2, best[1] + step / 2 });
                else spot = best;
            }
            var res = new JObject
            {
                ["mode"] = "free_spot", ["view_id"] = Rid.Value(view.Id), ["viewport_id"] = Rid.Value(vp.Id), ["sheet_id"] = Rid.Value(sheet.Id),
                ["found"] = spot != null, ["regenerated"] = false, ["obstacles_considered"] = all.Count - 1,
                ["search"] = new JObject { ["step_mm"] = Math.Round(step / FtPerMm, 2), ["max_radius_mm"] = Math.Round(maxRadius / FtPerMm, 1), ["gap_mm"] = Math.Round(gap / FtPerMm, 2) },
                ["coverage"] = Coverage(nm, linked, viewsRead)
            };
            if (spot != null)
            {
                Transform toSheet = vp.GetProjectionToSheetTransform();
                XYZ model = toSheet.Inverse.OfPoint(new XYZ(spot[0], spot[1], 0));
                res["center_sheet_mm"] = new JArray(Math.Round(spot[0] / FtPerMm, 2), Math.Round(spot[1] / FtPerMm, 2));
                res["center_model_mm"] = Mm3(model);
                res["distance_mm"] = Math.Round(Math.Sqrt(Math.Pow(spot[0] - near[0], 2) + Math.Pow(spot[1] - near[1], 2)) / FtPerMm, 2);
            }
            else res["why_not"] = "no place of that size inside the viewport frame within the search radius that is clear of every measured annotation, frame and symbol";
            return CommandResult.Ok(res);
        }
    }
}
