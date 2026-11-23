// -----------------------------------------------------------------------------
// Horizun Core - original Horizun code. No Revit.
//
// VIEWS ON SHEETS, TYPED (5.2, field report "detalles constructivos", P2). The rules the add-in
// applies around Viewport.Create, Viewport.LabelOffset and the ink measured by pdf_qa:
//
//   detail numbers   a number already used on the sheet is refused BEFORE Viewport.Create (which
//                    otherwise keeps another number without saying so), batch duplicates too.
//   title check      the title (label) outline against the sheet and against the datum heads
//                    (grid / level bubbles) printed on it: outside by how many mm, over which head.
//   ink anchor       pdf_qa's ink relative to the box centre, carried to the box centre now: the
//                    rectangle move_viewport anchor=ink places (GetBoxOutline can differ from the
//                    printed drawing by up to 150 mm; a 3D view prints ~92 mm above its box centre).
//   label watch      Viewport.LabelOffset before and after a datum's 2D extent changes: Revit can
//                    rewrite it, and the title then moves without anybody asking.
// Units: feet on the sheet in, paper mm out, as SheetLayoutRules.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    /// <summary>A datum head (bubble) printed on a sheet: its centre in sheet feet.</summary>
    public sealed class DatumHead
    {
        public long DatumId;
        public string Name;
        public long ViewportId;
        public double X, Y;
    }

    public static class SheetPaperRules
    {
        /// <summary>Radius around a datum head's end point taken as its bubble, paper mm (a typical 8-10 mm bubble, plus air).</summary>
        public const double DatumHeadRadiusMm = 6.0;
        public const double LabelWatchToleranceMm = 0.1;
        public const int MaxDetailNumberLength = 32;

        // ---- detail numbers ------------------------------------------------------------------------

        /// <summary>Null when the detail number is well formed; otherwise why not.</summary>
        public static string ValidateDetailNumber(JToken t)
        {
            if (t == null) return null;
            if (t.Type == JTokenType.Null) return "detail_number is null: omit it to let Revit number the viewport, or send the number as a string.";
            if (t.Type != JTokenType.String || string.IsNullOrWhiteSpace(t.Value<string>()) || t.Value<string>().Trim().Length > MaxDetailNumberLength)
                return "detail_number must be a non-empty string of up to " + MaxDetailNumberLength + " characters.";
            return null;
        }

        /// <summary>The viewport already holding this number on the sheet, or null. Compared without case (not measured whether Revit
        /// tells "a" from "A"; refusing both is the safe side).</summary>
        public static KeyValuePair<long, string>? Holder(string wanted, IEnumerable<KeyValuePair<long, string>> numbersOnSheet)
        {
            if (string.IsNullOrWhiteSpace(wanted)) return null;
            string w = wanted.Trim();
            foreach (KeyValuePair<long, string> kv in numbersOnSheet ?? Enumerable.Empty<KeyValuePair<long, string>>())
                if (string.Equals((kv.Value ?? "").Trim(), w, StringComparison.OrdinalIgnoreCase)) return kv;
            return null;
        }

        /// <summary>A number free on the sheet for a viewport Revit auto-numbered in this batch: one past the largest integer in use.</summary>
        public static string FreeDetailNumber(IEnumerable<string> numbersOnSheet)
        {
            var used = new HashSet<string>((numbersOnSheet ?? Enumerable.Empty<string>()).Where(n => n != null).Select(n => n.Trim()), StringComparer.OrdinalIgnoreCase);
            int max = 0;
            foreach (string n in used)
                if (int.TryParse(n, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int v) && v > max) max = v;
            int next = max + 1;
            while (used.Contains(next.ToString(System.Globalization.CultureInfo.InvariantCulture))) next++;
            return next.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        public static string DetailNumberRefusal(string wanted, string sheet, long holderViewportId, string holderView)
            => "detail_number '" + wanted.Trim() + "' is already used on sheet " + sheet + " by viewport " + holderViewportId +
               (string.IsNullOrEmpty(holderView) ? "" : " ('" + holderView + "')") +
               ". Revit keeps one number per sheet and would not give this viewport that number. Choose a free number or renumber the other viewport first. Nothing was written.";

        // ---- the title on paper --------------------------------------------------------------------

        /// <summary>
        /// The title outline against the sheet area and the datum heads printed on the sheet (all in sheet feet).
        /// outside_mm: how far the title passes each edge (null when inside or the sheet bounds are unreadable); over_datum: the heads whose
        /// bubble (radius DatumHeadRadiusMm of paper) the title covers.
        /// </summary>
        public static JObject LabelCheck(double[] label, double[] sheetArea, IEnumerable<DatumHead> heads, double radiusMm = DatumHeadRadiusMm)
        {
            var o = new JObject();
            if (label == null) { o["state"] = "no_title"; return o; }
            double[] p = sheetArea == null ? null : SheetLayoutRules.Protrusion(sheetArea, label, SheetLayoutRules.ToleranceFeet);
            o["outside_mm"] = p == null ? null : new JObject
            {
                ["left"] = SheetLayoutRules.Mm(p[0]), ["right"] = SheetLayoutRules.Mm(p[1]),
                ["top"] = SheetLayoutRules.Mm(p[2]), ["bottom"] = SheetLayoutRules.Mm(p[3])
            };
            double r = radiusMm * SheetLayoutRules.FeetPerMm;
            var over = new JArray();
            foreach (DatumHead h in heads ?? Enumerable.Empty<DatumHead>())
            {
                double dx = Math.Max(0, Math.Max(label[0] - h.X, h.X - label[2])), dy = Math.Max(0, Math.Max(label[1] - h.Y, h.Y - label[3]));
                if (dx * dx + dy * dy <= r * r)
                    over.Add(new JObject { ["datum_id"] = h.DatumId, ["name"] = h.Name, ["viewport_id"] = h.ViewportId, ["head_mm"] = new JArray(SheetLayoutRules.Mm(h.X), SheetLayoutRules.Mm(h.Y)) });
            }
            o["over_datum"] = over;
            if (sheetArea == null)
            {
                o["state"] = "not_measured";
                o["why"] = "the title block and sheet outline bounds are unreadable";
            }
            else o["state"] = p == null && over.Count == 0 ? "ok" : "warning";
            o["means"] = "outside_mm: the title passes the sheet (title block) by those mm; over_datum: the title covers a grid/level head (bubble radius " +
                         radiusMm.ToString(System.Globalization.CultureInfo.InvariantCulture) + " mm of paper assumed). A warning, nothing was refused.";
            return o;
        }

        /// <summary>A rectangle moved by (dx, dy).</summary>
        public static double[] Shift(double[] r, double dx, double dy) => r == null ? null : new[] { r[0] + dx, r[1] + dy, r[2] + dx, r[3] + dy };

        // ---- the ink anchor -------------------------------------------------------------------------

        /// <summary>Null when ink_box is [x0, y0, x1, y1] paper mm with x1 > x0 and y1 > y0; otherwise why not.</summary>
        public static string ValidateInkBox(JToken t)
        {
            if (!(t is JArray a) || a.Count != 4 || a.Any(v => v.Type != JTokenType.Integer && v.Type != JTokenType.Float))
                return "ink_box must be [x0, y0, x1, y1] in paper mm relative to the viewport's box centre (pdf_qa viewports[].ink_from_box_center_mm).";
            double[] v4 = a.Select(v => v.Value<double>()).ToArray();
            if (v4.Any(v => double.IsNaN(v) || double.IsInfinity(v) || Math.Abs(v) > 5000)) return "ink_box values must be finite paper mm (|v| <= 5000).";
            if (!(v4[2] > v4[0]) || !(v4[3] > v4[1])) return "ink_box must have x1 > x0 and y1 > y0.";
            return null;
        }

        /// <summary>The printed drawing's rectangle on the sheet NOW (feet): the box centre plus the ink measured relative to it.</summary>
        public static double[] InkOnSheet(double[] boxCenterFeet, double[] inkFromCenterMm)
        {
            double f = SheetLayoutRules.FeetPerMm;
            return new[]
            {
                boxCenterFeet[0] + inkFromCenterMm[0] * f, boxCenterFeet[1] + inkFromCenterMm[1] * f,
                boxCenterFeet[0] + inkFromCenterMm[2] * f, boxCenterFeet[1] + inkFromCenterMm[3] * f
            };
        }

        // ---- label offset ---------------------------------------------------------------------------

        /// <summary>Null when label_offset is [x, y] paper mm; otherwise why not.</summary>
        public static string ValidateLabelOffset(JToken t)
        {
            if (t == null) return null;
            if (!(t is JArray a) || a.Count != 2 || a.Any(v => v.Type != JTokenType.Integer && v.Type != JTokenType.Float))
                return "label_offset must be [x, y] in paper mm: Viewport.LabelOffset, from the viewport box's lower-left corner to the title.";
            if (a.Any(v => Math.Abs(v.Value<double>()) > 2000)) return "label_offset values must be within +-2000 paper mm.";
            return null;
        }

        /// <summary>Viewports whose LabelOffset moved between two reads (feet in, paper mm out).</summary>
        public static JArray LabelOffsetChanges(IDictionary<long, double[]> before, IDictionary<long, double[]> after, double toleranceMm = LabelWatchToleranceMm)
        {
            var rows = new JArray();
            double tol = toleranceMm * SheetLayoutRules.FeetPerMm;
            foreach (KeyValuePair<long, double[]> kv in before.OrderBy(k => k.Key))
            {
                if (!after.TryGetValue(kv.Key, out double[] now) || now == null || kv.Value == null) continue;
                if (Math.Abs(now[0] - kv.Value[0]) <= tol && Math.Abs(now[1] - kv.Value[1]) <= tol) continue;
                rows.Add(new JObject
                {
                    ["viewport_id"] = kv.Key,
                    ["label_offset_before_mm"] = new JArray(SheetLayoutRules.Mm(kv.Value[0]), SheetLayoutRules.Mm(kv.Value[1])),
                    ["label_offset_after_mm"] = new JArray(SheetLayoutRules.Mm(now[0]), SheetLayoutRules.Mm(now[1]))
                });
            }
            return rows;
        }

        public const string LabelRewrittenWarning =
            "Changing a datum's 2D extent made Revit rewrite Viewport.LabelOffset of these viewports: their titles moved. " +
            "Put them back with horizun_sheet_layout set_title label_offset (the 'before' values), or check the sheet.";
    }
}
