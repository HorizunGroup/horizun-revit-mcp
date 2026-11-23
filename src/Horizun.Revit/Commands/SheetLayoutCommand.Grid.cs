// -----------------------------------------------------------------------------
// Horizun Revit MCP - horizun_sheet_layout, 4.2-N4 operations. Original Horizun code.
//
//   layout_grid     (CT-P1-4a) the sheet's viewports in a fixed number of columns, row-major,
//                   each crop at its cell's corner and each TITLE a fixed distance under its
//                   crop. SheetGridRules does the arithmetic.
//   set_view_scale  (CT-P1-4b) changes a placed view's scale and, by default, gives the crop's
//                   centre back to where it was on paper.
//   rename          (LP-P3-11) renames views and sheets and MEASURES the title afterwards: a
//                   longer name must still fit under its viewport.
//
// All three go through ModelEditRunner: rehearse, single-use token, re-read after commit, roll
// back on disagreement. HRZ-marked viewports/views only unless allow_unmarked.
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
    public sealed partial class SheetLayoutCommand
    {
        private static string Key(Viewport v) => Rid.Value(v.Id).ToString(CultureInfo.InvariantCulture);

        /// <summary>What a grid cell occupies: the crop and the title (the box is NOT used: it is widened by grids and levels).</summary>
        private static double[] CropAndTitle(Document d, Viewport vp)
        {
            double[] crop = CropOnPaper(d, vp, out _);
            return crop == null ? Extent(vp) : SheetLayoutRules.Union(crop, Label(vp));
        }

        // ---- layout_grid ---------------------------------------------------------------------

        private static ArchModelEdit Grid(Document doc, JObject r, bool allow)
        {
            ViewSheet sheet = ModelEditRunner.Need<ViewSheet>(doc, r, "sheet_id");
            List<Viewport> all = ViewportsOf(doc, sheet);
            List<Viewport> vps = all;
            if (r["viewport_ids"] is JArray wanted)
            {
                var ids = wanted.Select(t => t.Value<long>()).ToList();
                if (ids.Distinct().Count() != ids.Count) throw new ArgumentException("viewport_ids repeats a viewport. Nothing was moved.");
                vps = ids.Select(id => all.FirstOrDefault(v => Rid.Value(v.Id) == id)
                    ?? throw new ArgumentException("viewport " + id + " is not on sheet " + sheet.SheetNumber + ". Nothing was moved.")).ToList();
            }
            if (vps.Count == 0) throw new ArgumentException("sheet " + sheet.SheetNumber + " has no viewport to lay out.");
            foreach (Viewport vp in vps)
            {
                HrzOwnership.Require(doc, vp, "viewport", allow, "allow_unmarked");
                if (vp.Pinned) throw new ArgumentException("viewport " + Rid.Value(vp.Id) + " is pinned; unpin it deliberately first. Nothing was moved.");
            }
            double mm = SheetLayoutRules.FeetPerMm;
            double[] margins = r["margins"] is JArray m ? m.Select(t => t.Value<double>() * mm).ToArray() : SheetLayoutRules.DefaultMarginsMm.Select(v => v * mm).ToArray();
            double notes = (r.Value<double?>("notes_width") ?? 0) * mm;
            double gapX = (r.Value<double?>("gap_x") ?? SheetLayoutRules.DefaultGapMm) * mm;
            double gapY = (r.Value<double?>("gap_y") ?? SheetLayoutRules.DefaultGapMm) * mm;
            double titleGap = (r.Value<double?>("title_gap") ?? SheetGridRules.DefaultTitleGapMm) * mm;
            int columns = r.Value<int>("columns");
            double[] usable = SheetLayoutRules.Usable(TitleBlock(doc, sheet), margins, notes);

            var items = new List<GridItem>();
            foreach (Viewport vp in vps)
            {
                double[] crop = CropOnPaper(doc, vp, out string why);
                if (crop == null) throw new ArgumentException("viewport " + Key(vp) + ": " + why + ". A grid is laid out by the crop. Nothing was moved.");
                items.Add(new GridItem { Key = Key(vp), Crop = crop, Label = Label(vp) });
            }
            Dictionary<string, GridPlacement> targets = SheetGridRules.Grid(items, usable, columns, gapX, gapY, titleGap, SheetLayoutRules.ToleranceFeet, out string refusal);
            if (targets == null) throw new ArgumentException(refusal);

            var edit = new ArchModelEdit();
            foreach (Viewport vp in vps) edit.Planned.Add(ModelEditRunner.Planned(vp, PlannedAction.Modify, r));
            edit.Summary["operation"] = "layout_grid";
            edit.Summary["sheet"] = sheet.SheetNumber + " - " + sheet.Name;
            edit.Summary["columns"] = columns;
            edit.Summary["title_gap_mm"] = SheetLayoutRules.Mm(titleGap);
            edit.Summary["usable_area"] = SheetLayoutRules.RectMm(usable);
            edit.Summary["placements"] = new JArray(vps.Select(vp =>
            {
                GridPlacement p = targets[Key(vp)];
                return (JToken)new JObject
                {
                    ["viewport_id"] = Rid.Value(vp.Id), ["row"] = p.Row + 1, ["column"] = p.Column + 1,
                    ["crop"] = SheetLayoutRules.RectMm(p.Crop), ["title"] = SheetLayoutRules.RectMm(p.Label)
                };
            }));
            var others = all.Where(v => !vps.Any(x => x.Id == v.Id)).ToList();
            edit.Apply = d =>
            {
                foreach (KeyValuePair<string, GridPlacement> kv in targets)
                {
                    var v = (Viewport)d.GetElement(Rid.Make(long.Parse(kv.Key, CultureInfo.InvariantCulture)));
                    XYZ c = v.GetBoxCenter();
                    v.SetBoxCenter(new XYZ(c.X + kv.Value.Move[0], c.Y + kv.Value.Move[1], c.Z));
                    if (kv.Value.LabelDelta != null)
                    {
                        XYZ o = v.LabelOffset;
                        v.LabelOffset = new XYZ(o.X + kv.Value.LabelDelta[0], o.Y + kv.Value.LabelDelta[1], o.Z);
                    }
                }
            };
            edit.Verify = d =>
            {
                var check = new PostconditionCheck("crop_positions", "title_distance", "inside_usable_area", "no_overlap");
                var rows = new JArray();
                var rects = new List<KeyValuePair<string, double[]>>();
                int atTarget = 0, titleOk = 0, titled = 0, inside = 0;
                foreach (KeyValuePair<string, GridPlacement> kv in targets)
                {
                    var v = d.GetElement(Rid.Make(long.Parse(kv.Key, CultureInfo.InvariantCulture))) as Viewport;
                    double[] crop = v == null ? null : CropOnPaper(d, v, out _);
                    double[] label = v == null ? null : Label(v);
                    double[] cell = crop == null ? null : SheetLayoutRules.Union(crop, label);
                    rects.Add(new KeyValuePair<string, double[]>(kv.Key, cell));
                    bool at = crop != null && ViewCropGeometry.Matches(kv.Value.Crop, crop, SheetLayoutRules.ToleranceFeet);
                    bool inArea = SheetLayoutRules.Inside(usable, cell, SheetLayoutRules.ToleranceFeet);
                    double? dist = null; bool tOk = true;
                    if (kv.Value.Label != null)
                    {
                        titled++;
                        tOk = crop != null && label != null &&
                              Math.Abs(SheetGridRules.TitleDistance(crop, label) - titleGap) <= SheetLayoutRules.ToleranceFeet &&
                              Math.Abs(label[0] - crop[0]) <= SheetLayoutRules.ToleranceFeet;
                        if (crop != null && label != null) dist = SheetLayoutRules.Mm(SheetGridRules.TitleDistance(crop, label));
                        if (tOk) titleOk++;
                    }
                    if (at) atTarget++;
                    if (inArea) inside++;
                    rows.Add(new JObject { ["viewport_id"] = long.Parse(kv.Key, CultureInfo.InvariantCulture), ["crop"] = SheetLayoutRules.RectMm(crop),
                        ["title_distance_mm"] = dist, ["at_target"] = at, ["title_ok"] = tOk, ["inside"] = inArea });
                }
                foreach (Viewport o in others) rects.Add(new KeyValuePair<string, double[]>(Key(o), Extent((Viewport)d.GetElement(o.Id))));
                List<string[]> overlaps = SheetLayoutRules.Overlaps(rects, SheetLayoutRules.ToleranceFeet);
                check.Compare("crop_positions", targets.Count, atTarget);
                check.Compare("title_distance", titled, titleOk);
                check.Compare("inside_usable_area", targets.Count, inside);
                check.Compare("no_overlap", 0, overlaps.Count);
                edit.Evidence["viewports"] = rows;
                if (overlaps.Count > 0) edit.Evidence["overlaps"] = new JArray(overlaps.Select(p => new JArray(p)));
                return check;
            };
            return edit;
        }

        // ---- set_view_scale ------------------------------------------------------------------

        private static ArchModelEdit SetScale(Document doc, JObject r, bool allow)
        {
            Viewport vp = ModelEditRunner.Need<Viewport>(doc, r, "viewport_id");
            HrzOwnership.Require(doc, vp, "viewport", allow, "allow_unmarked");
            if (vp.Pinned) throw new ArgumentException("viewport " + Rid.Value(vp.Id) + " is pinned; unpin it deliberately first.");
            View view = doc.GetElement(vp.ViewId) as View;
            if (view == null) throw new ArgumentException("the viewport's view is unreadable.");
            HrzOwnership.Require(doc, view, "view", allow, "allow_unmarked");
            var assumptions = new List<JObject>();
            int oldScale = view.Scale;
            int scale = DocumentationDefaults.Scale(r, oldScale, assumptions);
            bool keep = r.Value<bool?>("keep_viewport_center") ?? true;
            if (r["scale"] != null && view.ViewTemplateId != ElementId.InvalidElementId && doc.GetElement(view.ViewTemplateId) is View template)
            {
                var free = template.GetNonControlledTemplateParameterIds();
                if (!free.Contains(new ElementId(BuiltInParameter.VIEW_SCALE)))
                    throw new ArgumentException("view '" + view.Name + "' takes its scale from the template '" + template.Name + "'. Change the template, or detach it deliberately. Nothing was written.");
            }
            ViewSheet sheet = doc.GetElement(vp.SheetId) as ViewSheet;
            double[] before = CropOnPaper(doc, vp, out string why);
            bool boxAnchor = keep && before == null && r["keep_viewport_center"] == null;
            if (boxAnchor)
            {
                before = Box(vp);
                if (before == null) why += "; the viewport box is also unreadable";
                else assumptions.Add(AssumptionNotes.Row("center_anchor=box", why, "send keep_viewport_center=false"));
            }
            if (keep && before == null) throw new ArgumentException("keep_viewport_center cannot be honoured: " + why + ". Send keep_viewport_center=false to accept Revit's own placement.");
            double[] area = sheet == null ? null : TitleBlock(doc, sheet);

            var edit = new ArchModelEdit();
            var planned = ModelEditRunner.Planned(vp, PlannedAction.Modify, r);
            planned.BeforeValues["scale"] = oldScale.ToString(System.Globalization.CultureInfo.InvariantCulture);
            planned.BeforeValues["paper_anchor"] = before == null ? "unavailable" : new JArray(before).ToString(Newtonsoft.Json.Formatting.None);
            planned.ProposedValues["scale"] = scale.ToString(System.Globalization.CultureInfo.InvariantCulture);
            planned.ProposedValues["center_anchor"] = boxAnchor ? "box" : "crop";
            edit.Planned.Add(planned);
            edit.Summary["operation"] = "set_view_scale";
            edit.Summary["view"] = view.Name;
            edit.Summary["scale_before"] = oldScale;
            edit.Summary["scale_after"] = scale;
            edit.Summary["keep_viewport_center"] = keep;
            AssumptionNotes.Stamp(edit.Summary, assumptions);
            edit.Summary["anchor_kind"] = boxAnchor ? "box" : "crop";
            edit.Evidence["anchor_kind"] = boxAnchor ? "box" : "crop";
            edit.Summary["crop_before"] = boxAnchor ? null : SheetLayoutRules.RectMm(before);
            edit.Summary["anchor_before"] = SheetLayoutRules.RectMm(before);
            ElementId vpId = vp.Id, viewId = view.Id;
            edit.Apply = d =>
            {
                if (r["scale"] == null) return;
                var v = (View)d.GetElement(viewId);
                v.Scale = scale;
                d.Regenerate();
                if (!keep) return;
                var p = (Viewport)d.GetElement(vpId);
                double[] after = boxAnchor ? Box(p) : CropOnPaper(d, p, out _);
                if (after == null) throw new InvalidOperationException("the paper anchor could not be read after the scale change.");
                double[] shift = SheetGridRules.KeepCentre(before, after);
                XYZ c = p.GetBoxCenter();
                p.SetBoxCenter(new XYZ(c.X + shift[0], c.Y + shift[1], c.Z));
            };
            edit.Verify = d =>
            {
                string centreCheck = boxAnchor ? "box_centre" : "crop_centre";
                var check = new PostconditionCheck("scale", centreCheck, "inside_title_block", "no_overlap");
                var v = (View)d.GetElement(viewId);
                var p = (Viewport)d.GetElement(vpId);
                check.Compare("scale", scale, v.Scale);
                string w3 = boxAnchor ? "the viewport box could not be read after the scale change" : null;
                double[] now = boxAnchor ? Box(p) : CropOnPaper(d, p, out w3);
                if (!keep) check.Record(centreCheck, "not requested", "not requested", true);
                else if (now == null) check.Unreadable(centreCheck, new JArray(SheetLayoutRules.Mm(SheetLayoutRules.Center(before)[0]), SheetLayoutRules.Mm(SheetLayoutRules.Center(before)[1])), w3);
                else
                {
                    double[] a = SheetLayoutRules.Center(before), b = SheetLayoutRules.Center(now);
                    double err = Math.Max(Math.Abs(a[0] - b[0]), Math.Abs(a[1] - b[1]));
                    check.Record(centreCheck, new JArray(SheetLayoutRules.Mm(a[0]), SheetLayoutRules.Mm(a[1])), new JArray(SheetLayoutRules.Mm(b[0]), SheetLayoutRules.Mm(b[1])), err <= SheetLayoutRules.ToleranceFeet);
                }
                double[] ext = Extent(p);
                // An unreadable title block is not a satisfied property (review 2026-11-20, M3): it is reported
                // unread, the way move_viewport does.
                if (area == null)
                    check.Unreadable("inside_title_block", true, "the title block area of the sheet could not be read");
                else
                {
                    bool in1 = SheetLayoutRules.Inside(area, ext, SheetLayoutRules.ToleranceFeet);
                    check.Record("inside_title_block", true, in1, in1);
                }
                List<string[]> overlaps = OverlapsOn(d, d.GetElement(p.SheetId) as ViewSheet, p);
                check.Record("no_overlap", 0, overlaps.Count, overlaps.Count == 0);
                edit.Evidence["viewport"] = Row(d, p);
                if (overlaps.Count > 0) edit.Evidence["overlaps"] = new JArray(overlaps.Select(q => new JArray(q)));
                return check;
            };
            return edit;
        }

        // ---- set_title (F52, vplabel/vptitle/isolabel) ------------------------------------------

        private static ArchModelEdit SetTitle(Document doc, JObject r, bool allow)
        {
            var vps = r["viewport_ids"] is JArray ids
                ? ids.Select(t => ModelEditRunner.Need<Viewport>(doc, new JObject { ["viewport_id"] = t }, "viewport_id")).ToList()
                : new List<Viewport> { ModelEditRunner.Need<Viewport>(doc, r, "viewport_id") };
            double mm = SheetLayoutRules.FeetPerMm;
            double? gap = r.Value<double?>("title_gap") * mm, line = r.Value<double?>("line_length") * mm;
            // 5.2 (P2): label_offset sets Viewport.LabelOffset explicitly; the title (and the detail-number bubble in it) moves with it.
            double[] fixedOffset = r["label_offset"] is JArray lo ? new[] { lo[0].Value<double>() * mm, lo[1].Value<double>() * mm } : null;
            var plan = new Dictionary<string, double[]>(StringComparer.Ordinal);   // key -> label delta
            var labelsBefore = new Dictionary<string, double[]>(StringComparer.Ordinal);
            foreach (Viewport vp in vps)
            {
                HrzOwnership.Require(doc, vp, "viewport", allow, "allow_unmarked");
                if (vp.Pinned) throw new ArgumentException("viewport " + Key(vp) + " is pinned; unpin it deliberately first.");
                if (gap.HasValue)
                {
                    double[] crop = CropOnPaper(doc, vp, out string why), label = Label(vp);
                    if (crop == null) throw new ArgumentException("viewport " + Key(vp) + ": " + why + ". title_gap is measured from the crop.");
                    if (label == null) throw new ArgumentException("viewport " + Key(vp) + " shows no title.");
                    plan[Key(vp)] = new[] { crop[0] - label[0], (crop[1] - gap.Value - (label[3] - label[1])) - label[1] };
                }
                XYZ o0 = vp.LabelOffset;
                labelsBefore[Key(vp)] = Label(vp);
                if (fixedOffset != null) plan[Key(vp)] = new[] { fixedOffset[0] - o0.X, fixedOffset[1] - o0.Y };
            }
            var edit = new ArchModelEdit();
            foreach (Viewport vp in vps) edit.Planned.Add(ModelEditRunner.Planned(vp, PlannedAction.Modify, r));
            edit.Summary["operation"] = "set_title";
            edit.Summary["viewports"] = vps.Count;
            edit.Summary["title_gap_mm"] = r["title_gap"]; edit.Summary["line_length_mm"] = r["line_length"];
            edit.Summary["label_offset_mm"] = r["label_offset"];
            var predicted = new JArray();
            foreach (Viewport vp in vps)
            {
                if (!plan.TryGetValue(Key(vp), out double[] dl)) continue;
                double[] after = SheetPaperRules.Shift(labelsBefore[Key(vp)], dl[0], dl[1]);
                predicted.Add(new JObject { ["viewport_id"] = Rid.Value(vp.Id), ["label_after"] = SheetLayoutRules.RectMm(after),
                    ["title_check"] = TitleCheck(doc, doc.GetElement(vp.SheetId) as ViewSheet, after) });
            }
            if (predicted.Count > 0) edit.Summary["predicted"] = predicted;
            edit.Apply = d =>
            {
                foreach (Viewport vp in vps)
                {
                    var v = (Viewport)d.GetElement(vp.Id);
                    if (fixedOffset != null) { XYZ o = v.LabelOffset; v.LabelOffset = new XYZ(fixedOffset[0], fixedOffset[1], o.Z); }
                    else if (plan.TryGetValue(Key(vp), out double[] dl)) { XYZ o = v.LabelOffset; v.LabelOffset = new XYZ(o.X + dl[0], o.Y + dl[1], o.Z); }
                    if (line.HasValue) v.LabelLineLength = line.Value;
                }
            };
            edit.Verify = d =>
            {
                // title_moved_with_offset is compared only where the title is shown before and after (a hidden title has no
                // outline), and only by the outline's lower-left corner: line_length changes the width, not the origin.
                int ok1 = 0, ok2 = 0, ok3 = 0, ok4 = 0, measured4 = 0; var rows = new JArray();
                foreach (Viewport vp in vps)
                {
                    var v = (Viewport)d.GetElement(vp.Id);
                    bool a = true, b = true;
                    if (gap.HasValue)
                    {
                        double[] crop = CropOnPaper(d, v, out _), label = Label(v);
                        a = crop != null && label != null && Math.Abs(SheetGridRules.TitleDistance(crop, label) - gap.Value) <= SheetLayoutRules.ToleranceFeet;
                    }
                    if (line.HasValue) b = Math.Abs(v.LabelLineLength - line.Value) <= SheetLayoutRules.ToleranceFeet;
                    if (a) ok1++; if (b) ok2++;
                    var row = new JObject { ["viewport_id"] = Rid.Value(v.Id), ["title_ok"] = a, ["line_ok"] = b };
                    double[] labelNow = Label(v);
                    if (fixedOffset != null)
                    {
                        XYZ o = v.LabelOffset;
                        bool offOk = Math.Abs(o.X - fixedOffset[0]) <= SheetLayoutRules.ToleranceFeet && Math.Abs(o.Y - fixedOffset[1]) <= SheetLayoutRules.ToleranceFeet;
                        double[] was = labelsBefore[Key(vp)], dl = plan[Key(vp)];
                        if (offOk) ok3++;
                        row["label_offset_mm"] = new JArray(SheetLayoutRules.Mm(o.X), SheetLayoutRules.Mm(o.Y));
                        if (was != null && labelNow != null)
                        {
                            measured4++;
                            bool moved = Math.Abs(labelNow[0] - (was[0] + dl[0])) <= SheetLayoutRules.ToleranceFeet &&
                                         Math.Abs(labelNow[1] - (was[1] + dl[1])) <= SheetLayoutRules.ToleranceFeet;
                            if (moved) ok4++;
                            row["title_moved_with_offset"] = moved;
                        }
                        else
                        {
                            row["title_moved_with_offset"] = JValue.CreateNull();
                            row["title_moved_why"] = "the title is not shown (no outline before or after): its movement was not measured";
                        }
                    }
                    row["label"] = SheetLayoutRules.RectMm(labelNow);
                    row["title_check"] = TitleCheck(d, d.GetElement(v.SheetId) as ViewSheet, labelNow);
                    rows.Add(row);
                }
                // Only what was asked for is a postcondition: a title_gap or line_length nobody requested was
                // "compared" against vacuous trues (review 2026-11-20, M3).
                var required = new List<string>();
                if (gap.HasValue) required.Add("title_distance");
                if (line.HasValue) required.Add("line_length");
                if (fixedOffset != null) required.Add("label_offset");
                if (measured4 > 0) required.Add("title_moved_with_offset");
                var check = new PostconditionCheck(required.ToArray());
                if (gap.HasValue) check.Compare("title_distance", vps.Count, ok1);
                if (line.HasValue) check.Compare("line_length", vps.Count, ok2);
                if (fixedOffset != null)
                {
                    check.Compare("label_offset", vps.Count, ok3);
                    if (measured4 > 0) check.Compare("title_moved_with_offset", measured4, ok4);
                    edit.Evidence["title_moved_measured"] = measured4;
                }
                edit.Evidence["viewports"] = rows;
                return check;
            };
            return edit;
        }

        // ---- rename ---------------------------------------------------------------------------

        private sealed class RenameRow
        {
            public Element Target; public bool IsSheet; public string NewName, NewNumber;
            public string OldName, OldNumber; public Viewport Vp; public bool FitBefore;
        }

        private static ArchModelEdit Rename(Document doc, JObject r, bool allow)
        {
            bool requireFit = r.Value<bool?>("require_title_fit") ?? true;
            var rows = new List<RenameRow>();
            var viewports = new FilteredElementCollector(doc).OfClass(typeof(Viewport)).Cast<Viewport>().GroupBy(v => Rid.Value(v.ViewId)).ToDictionary(g => g.Key, g => g.First());
            foreach (JObject item in ((JArray)r["items"]).OfType<JObject>())
            {
                bool sheetItem = item["sheet_id"] != null;
                Element e = sheetItem ? (Element)ModelEditRunner.Need<ViewSheet>(doc, item, "sheet_id") : ModelEditRunner.Need<View>(doc, item, "view_id");
                if (!sheetItem && ((View)e).IsTemplate) throw new ArgumentException("view " + Rid.Value(e.Id) + " is a view template; rename it with horizun_manage_views.");
                HrzOwnership.Require(doc, e, sheetItem ? "sheet" : "view", allow, "allow_unmarked");
                var row = new RenameRow { Target = e, IsSheet = sheetItem, NewName = item.Value<string>("name")?.Trim(), NewNumber = item.Value<string>("sheet_number")?.Trim() };
                row.OldName = e.Name;
                if (sheetItem) row.OldNumber = ((ViewSheet)e).SheetNumber;
                else if (viewports.TryGetValue(Rid.Value(e.Id), out Viewport vp))
                {
                    row.Vp = vp;
                    row.FitBefore = SheetGridRules.TitleFits(Box(vp), Label(vp), SheetLayoutRules.ToleranceFeet);
                }
                rows.Add(row);
            }
            if (rows.Select(x => Rid.Value(x.Target.Id)).Distinct().Count() != rows.Count) throw new ArgumentException("rename lists an element twice. Nothing was written.");
            var viewRows = rows.Where(x => !x.IsSheet && x.NewName != null).ToList();
            var movingViews = new HashSet<long>(viewRows.Select(x => Rid.Value(x.Target.Id)));
            string conflict = SheetGridRules.RenameConflict(viewRows.Select(x => new KeyValuePair<string, string>(Rid.Value(x.Target.Id).ToString(CultureInfo.InvariantCulture), x.NewName)).ToList(),
                new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().Where(v => !v.IsTemplate && !movingViews.Contains(Rid.Value(v.Id))).Select(v => v.Name), "view");
            if (conflict == null)
            {
                var sheetRows = rows.Where(x => x.IsSheet && x.NewNumber != null).ToList();
                var movingSheets = new HashSet<long>(sheetRows.Select(x => Rid.Value(x.Target.Id)));
                conflict = SheetGridRules.RenameConflict(sheetRows.Select(x => new KeyValuePair<string, string>(Rid.Value(x.Target.Id).ToString(CultureInfo.InvariantCulture), x.NewNumber)).ToList(),
                    new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>().Where(s => !movingSheets.Contains(Rid.Value(s.Id))).Select(s => s.SheetNumber), "sheet number");
            }
            if (conflict != null) throw new ArgumentException(conflict + " Nothing was written.");

            var edit = new ArchModelEdit();
            foreach (RenameRow x in rows) edit.Planned.Add(ModelEditRunner.Planned(x.Target, PlannedAction.Modify, r));
            edit.Summary["operation"] = "rename";
            edit.Summary["count"] = rows.Count;
            edit.Summary["require_title_fit"] = requireFit;
            edit.Summary["renames"] = new JArray(rows.Select(x => (JToken)new JObject
            {
                [x.IsSheet ? "sheet_id" : "view_id"] = Rid.Value(x.Target.Id), ["from"] = x.OldName, ["to"] = x.NewName,
                ["number_from"] = x.OldNumber, ["number_to"] = x.NewNumber, ["on_sheet"] = x.Vp != null
            }));
            edit.Apply = d =>
            {
                foreach (RenameRow x in rows)
                {
                    Element e = d.GetElement(x.Target.Id);
                    if (e is ViewSheet s)
                    {
                        if (x.NewNumber != null) s.SheetNumber = x.NewNumber;
                        if (x.NewName != null) s.Name = x.NewName;
                    }
                    else if (x.NewName != null) e.Name = x.NewName;
                }
                d.Regenerate();
            };
            edit.Verify = d =>
            {
                var check = new PostconditionCheck("names", "title_fit");
                var evidence = new JArray();
                int named = 0, fits = 0, measured = 0;
                foreach (RenameRow x in rows)
                {
                    Element e = d.GetElement(x.Target.Id);
                    bool ok = e != null && (x.NewName == null || string.Equals(e.Name, x.NewName, StringComparison.Ordinal)) &&
                              (x.NewNumber == null || (e is ViewSheet s && string.Equals(s.SheetNumber, x.NewNumber, StringComparison.Ordinal)));
                    if (ok) named++;
                    var ev = new JObject { [x.IsSheet ? "sheet_id" : "view_id"] = Rid.Value(x.Target.Id), ["name_ok"] = ok };
                    if (x.Vp != null && d.GetElement(x.Vp.Id) is Viewport p)
                    {
                        measured++;
                        double[] box = Box(p), label = Label(p);
                        bool fit = SheetGridRules.TitleFits(box, label, SheetLayoutRules.ToleranceFeet);
                        // a title that did not fit before the rename is not blamed on it
                        bool pass = !requireFit || fit || !x.FitBefore;
                        if (pass) fits++;
                        ev["title_fits"] = fit; ev["fit_before"] = x.FitBefore; ev["title_mm"] = SheetLayoutRules.RectMm(label); ev["box_mm"] = SheetLayoutRules.RectMm(box);
                    }
                    evidence.Add(ev);
                }
                check.Compare("names", rows.Count, named);
                check.Compare("title_fit", measured, fits);
                edit.Evidence["items"] = evidence;
                return check;
            };
            return edit;
        }
    }
}
