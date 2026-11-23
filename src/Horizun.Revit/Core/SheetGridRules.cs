// -----------------------------------------------------------------------------
// Horizun Core - original Horizun code. Revit-free.
//
// THE PAPER ARITHMETIC OF THE 4.2-N4 SHEET OPERATIONS of horizun_sheet_layout
// (CT-P1-4a, CT-P1-4b, LP-P3-11). Everything is paper feet, rectangles
// [minX, minY, maxX, maxY] as in SheetLayoutRules.
//
//   layout_grid     a fixed number of COLUMNS, row-major. Every cell is as wide as the widest
//                   crop-or-title in its column and as tall as the tallest crop + title_gap +
//                   title in its row. Each crop sits at its cell's top-left corner and its
//                   TITLE is put title_gap below the crop, left-aligned with it, whatever the
//                   viewport box did (grids and levels outside the crop widen the box, which
//                   is what made hand-placed titles drift). A grid that does not fit is refused
//                   whole, with the height it needed.
//   set_view_scale  the crop's centre is the thing that must not move.
//   rename          a title is measured on paper: it must still fit under its viewport.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public sealed class GridItem
    {
        public string Key;
        /// <summary>The view's crop on paper.</summary>
        public double[] Crop;
        /// <summary>The viewport title (label) on paper; null when the viewport shows no title.</summary>
        public double[] Label;
    }

    public sealed class GridPlacement
    {
        public string Key;
        public int Row, Column;
        /// <summary>The paper move that puts the crop on its target.</summary>
        public double[] Move;
        /// <summary>The crop where it will be.</summary>
        public double[] Crop;
        /// <summary>Added to Viewport.LabelOffset AFTER the move, so the title lands title_gap under the crop (null: no title).</summary>
        public double[] LabelDelta;
        /// <summary>The title where it will be (null: no title).</summary>
        public double[] Label;
    }

    public static class SheetGridRules
    {
        public const int MaxColumns = 12;
        public const double DefaultTitleGapMm = 5;
        public const int MaxRenames = 200;

        public static Dictionary<string, GridPlacement> Grid(IList<GridItem> items, double[] usable, int columns, double gapX, double gapY,
                                                              double titleGap, double tolerance, out string refusal)
        {
            refusal = null;
            if (items == null || items.Count == 0) { refusal = "the sheet has no viewport to lay out."; return null; }
            if (columns < 1 || columns > MaxColumns) { refusal = "columns must be 1.." + MaxColumns + "."; return null; }
            foreach (GridItem it in items)
                if (it.Crop == null || !(it.Crop[2] - it.Crop[0] > 0) || !(it.Crop[3] - it.Crop[1] > 0))
                { refusal = "viewport " + it.Key + " has no readable crop on paper (a grid is laid out by the crop). Nothing was moved."; return null; }

            int rows = (items.Count + columns - 1) / columns;
            var colWidth = new double[columns];
            var rowHeight = new double[rows];
            for (int i = 0; i < items.Count; i++)
            {
                GridItem it = items[i];
                double cw = it.Crop[2] - it.Crop[0];
                double lw = it.Label == null ? 0 : it.Label[2] - it.Label[0];
                double lh = it.Label == null ? 0 : it.Label[3] - it.Label[1];
                double h = (it.Crop[3] - it.Crop[1]) + (it.Label == null ? 0 : titleGap + lh);
                colWidth[i % columns] = Math.Max(colWidth[i % columns], Math.Max(cw, lw));
                rowHeight[i / columns] = Math.Max(rowHeight[i / columns], h);
            }
            double needW = colWidth.Sum() + gapX * (columns - 1);
            double needH = rowHeight.Sum() + gapY * (rows - 1);
            double haveW = usable[2] - usable[0], haveH = usable[3] - usable[1];
            if (needW > haveW + tolerance)
            { refusal = columns + " column(s) need " + SheetLayoutRules.Mm(needW) + " mm of width; the usable width is " + SheetLayoutRules.Mm(haveW) + " mm. Nothing was moved: fewer columns, a smaller gap, or change a scale first."; return null; }
            if (needH > haveH + tolerance)
            { refusal = rows + " row(s) need " + SheetLayoutRules.Mm(needH) + " mm of height; the usable height is " + SheetLayoutRules.Mm(haveH) + " mm. Nothing was moved: more columns, a smaller gap, or split the sheet."; return null; }

            var colLeft = new double[columns];
            double x = usable[0];
            for (int c = 0; c < columns; c++) { colLeft[c] = x; x += colWidth[c] + gapX; }
            var rowTop = new double[rows];
            double y = usable[3];
            for (int r = 0; r < rows; r++) { rowTop[r] = y; y -= rowHeight[r] + gapY; }

            var placed = new Dictionary<string, GridPlacement>(StringComparer.Ordinal);
            for (int i = 0; i < items.Count; i++)
            {
                GridItem it = items[i];
                int r = i / columns, c = i % columns;
                double cw = it.Crop[2] - it.Crop[0], ch = it.Crop[3] - it.Crop[1];
                double[] crop = { colLeft[c], rowTop[r] - ch, colLeft[c] + cw, rowTop[r] };
                var p = new GridPlacement { Key = it.Key, Row = r, Column = c, Crop = crop, Move = new[] { crop[0] - it.Crop[0], crop[1] - it.Crop[1] } };
                if (it.Label != null)
                {
                    double lw = it.Label[2] - it.Label[0], lh = it.Label[3] - it.Label[1];
                    double[] label = { crop[0], crop[1] - titleGap - lh, crop[0] + lw, crop[1] - titleGap };
                    p.Label = label;
                    // the title moves WITH the viewport; the offset only makes up what is left
                    p.LabelDelta = new[] { label[0] - (it.Label[0] + p.Move[0]), label[1] - (it.Label[1] + p.Move[1]) };
                }
                placed[it.Key] = p;
            }
            return placed;
        }

        /// <summary>The distance from the crop's bottom edge to the title's top edge (positive: the title is below).</summary>
        public static double TitleDistance(double[] crop, double[] label) => crop[1] - label[3];

        /// <summary>set_view_scale: the shift that gives the crop's centre back to where it was.</summary>
        public static double[] KeepCentre(double[] cropBefore, double[] cropAfter)
        {
            double[] a = SheetLayoutRules.Center(cropBefore), b = SheetLayoutRules.Center(cropAfter);
            return new[] { a[0] - b[0], a[1] - b[1] };
        }

        public static bool ValidScale(long scale) => scale >= 1 && scale <= 24000;

        /// <summary>rename: does the title still sit under its viewport (not wider than the box on either side)?</summary>
        public static bool TitleFits(double[] box, double[] label, double tolerance)
            => box != null && label != null && label[2] <= box[2] + tolerance && label[0] >= box[0] - tolerance;

        /// <summary>
        /// rename: null when the new names are usable. Case-insensitive duplicates inside the request and against
        /// the names that stay (Revit refuses a duplicate view name; a sheet number must be unique too).
        /// </summary>
        public static string RenameConflict(IList<KeyValuePair<string, string>> wanted, IEnumerable<string> namesThatStay, string kind)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var stay = new HashSet<string>(namesThatStay ?? new string[0], StringComparer.OrdinalIgnoreCase);
            foreach (var kv in wanted)
            {
                if (string.IsNullOrWhiteSpace(kv.Value)) return kind + " " + kv.Key + ": the new name is empty.";
                if (kind == "view" && kv.Value.IndexOfAny(new[] { '{', '}', '[', ']', '|', ';', '<', '>', '?', '`', '~' }) >= 0)
                    return kind + " " + kv.Key + ": the name '" + kv.Value + "' has characters Revit does not accept in a view name ({ } [ ] | ; < > ? ` ~).";
                if (!seen.Add(kv.Value.Trim())) return kind + " name '" + kv.Value.Trim() + "' appears twice in the request.";
                if (stay.Contains(kv.Value.Trim())) return kind + " name '" + kv.Value.Trim() + "' already exists on a " + kind + " this call does not rename.";
            }
            return null;
        }

        public static string ValidateGrid(JObject r)
        {
            if (r["columns"] == null || r["columns"].Type != JTokenType.Integer || r.Value<long>("columns") < 1 || r.Value<long>("columns") > MaxColumns)
                return "layout_grid needs columns, an integer 1.." + MaxColumns + ".";
            if (r["sheet_id"]?.Type != JTokenType.Integer) return "sheet_id is required.";
            foreach (string n in new[] { "gap_x", "gap_y", "title_gap", "notes_width" })
                if (r[n] != null && (!ViewCropGeometry.Number(r[n]) || r.Value<double>(n) < 0)) return n + " must be a non-negative number of paper mm.";
            if (r["margins"] != null && (!(r["margins"] is JArray m) || m.Count != 4 || m.Any(t => !ViewCropGeometry.Number(t) || t.Value<double>() < 0)))
                return "margins must be [left, right, top, bottom], each >= 0 paper mm.";
            if (r["viewport_ids"] != null && (!(r["viewport_ids"] is JArray a) || a.Count == 0 || a.Any(t => t.Type != JTokenType.Integer)))
                return "viewport_ids must be a non-empty list of viewport ids (the grid's order; default: the sheet's viewports by id).";
            return null;
        }

        /// <summary>set_title takes one viewport or an explicit list, and dimensions in paper mm.</summary>
        public static string ValidateSetTitle(JObject r)
        {
            if ((r["viewport_id"] != null) == (r["viewport_ids"] != null) ||
                (r["viewport_id"] != null && r["viewport_id"].Type != JTokenType.Integer) ||
                (r["viewport_ids"] != null && (!(r["viewport_ids"] is JArray a) || a.Count == 0 || a.Count > 200 ||
                                             a.Any(t => t.Type != JTokenType.Integer))))
                return "set_title takes exactly one of viewport_id (integer) or viewport_ids (1..200 integers).";
            if (r["title_gap"] == null && r["line_length"] == null && r["label_offset"] == null) return "set_title needs title_gap, label_offset and/or line_length (paper mm).";
            if (r["title_gap"] != null && r["label_offset"] != null) return "set_title takes title_gap OR label_offset: both place the title.";
            string lo = SheetPaperRules.ValidateLabelOffset(r["label_offset"]);
            if (lo != null) return lo;
            foreach (string n in new[] { "title_gap", "line_length" })
                if (r[n] != null && (!ViewCropGeometry.Number(r[n]) || r.Value<double>(n) < 0)) return n + " must be a non-negative number of paper mm.";
            return null;
        }

        public static string ValidateScale(JObject r)
        {
            if (r["viewport_id"]?.Type != JTokenType.Integer) return "set_view_scale needs viewport_id.";
            if (r["scale"] != null && (r["scale"].Type != JTokenType.Integer || !ValidScale(r.Value<long>("scale"))))
                return "set_view_scale invalid scale, an integer 1..24000 (the N of 1:N).";
            if (r["keep_viewport_center"] != null && r["keep_viewport_center"].Type != JTokenType.Boolean) return "keep_viewport_center must be a boolean.";
            return null;
        }

        public static string ValidateRename(JObject r)
        {
            if (!(r["items"] is JArray items) || items.Count == 0 || items.Count > MaxRenames)
                return "rename needs items, 1.." + MaxRenames + " of { view_id | sheet_id, name } (sheet_id may also carry sheet_number).";
            foreach (JToken t in items)
            {
                if (!(t is JObject o)) return "every item of rename must be an object.";
                foreach (JProperty p in o.Properties())
                    if (p.Name != "view_id" && p.Name != "sheet_id" && p.Name != "name" && p.Name != "sheet_number")
                        return "item field '" + p.Name + "' is not applicable to rename (view_id | sheet_id, name, sheet_number).";
                bool v = o["view_id"]?.Type == JTokenType.Integer, s = o["sheet_id"]?.Type == JTokenType.Integer;
                if (v == s) return "every rename item takes exactly one of view_id or sheet_id.";
                if (o["name"] == null && o["sheet_number"] == null) return "every rename item needs name (and/or sheet_number for a sheet).";
                if (o["sheet_number"] != null && !s) return "sheet_number belongs to a sheet_id item.";
                foreach (string f in new[] { "name", "sheet_number" })
                    if (o[f] != null && (o[f].Type != JTokenType.String || string.IsNullOrWhiteSpace(o.Value<string>(f)))) return f + " must be a non-empty string.";
            }
            if (r["require_title_fit"] != null && r["require_title_fit"].Type != JTokenType.Boolean) return "require_title_fit must be a boolean.";
            return null;
        }
    }
}
