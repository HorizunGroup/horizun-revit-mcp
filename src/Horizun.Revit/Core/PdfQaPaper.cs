// -----------------------------------------------------------------------------
// Horizun Core - original Horizun code. No Revit.
//
// VERIFY ON PAPER (5.2, field report "detalles constructivos", P1). The API re-reads what it
// wrote and is satisfied; the printed sheet shows something else. These are the measurements
// pdf_qa makes on the produced PDF itself, all arithmetic over what PdfQa.Read took from it:
//
//   viewport ink   per viewport of a sheet, the box of what was really drawn for it on the
//                  page, against the box Revit reports (Viewport.GetBoxOutline) - in sheet mm,
//                  the frame horizun_sheet_layout uses, plus the ink relative to the box centre,
//                  which survives a later move of the viewport (move_viewport anchor=ink).
//   printed gray   a coarse software raster of the vector drawing inside a box: mean darkness,
//                  inked fraction and a 10-bin histogram. Painter's order, opaque fills,
//                  strokes by their width. LIMITS, stated in every reply: text is excluded
//                  (counted apart), images are not decoded (their area is excluded and
//                  counted), shadings, soft masks and blend modes are not drawn, line widths are
//                  taken as written in the content stream (no CTM scaling).
//   note lines     a note's text found again among the page's words, and on how many baselines
//                  it was printed (did Revit wrap it?).
//   comparison     two PDFs of the same set, page by page: words, font families, painted paths,
//                  segments and images, and the shift of the page's ink box.
//   font advice    a missing expected family that is installed for this Windows user only.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    /// <summary>One painted path, flattened, in page points.</summary>
    public sealed class PdfDrawOp
    {
        public bool Fill, Stroke, EvenOdd;
        /// <summary>0 = white, 1 = black (1 - luminance of the colour).</summary>
        public double FillDark = 1, StrokeDark = 1;
        /// <summary>Stroke width in content-stream units (points when the CTM does not scale).</summary>
        public double Width;
        public List<List<double[]>> Rings = new List<List<double[]>>();
        public List<bool> Closed = new List<bool>();
        public double[] Box;
    }

    /// <summary>A viewport of a sheet as Revit reports it, in SHEET mm (feet x 304.8, the sheet's own frame).</summary>
    public sealed class PdfViewportBox
    {
        public long ViewportId, ViewId;
        public string View;
        public double[] BoxMm, LabelMm;
    }

    /// <summary>Which sheet a page prints, and what is on it.</summary>
    public sealed class PdfSheetMap
    {
        public int Page;
        public long SheetId;
        public string Sheet;
        /// <summary>The sheet number, looked for among the page's words to confirm the page order.</summary>
        public string SheetNumber;
        /// <summary>ViewSheet.Outline in sheet mm [x0, y0, x1, y1]: the paper the page is fitted from.</summary>
        public double[] OutlineMm;
        /// <summary>Why OutlineMm could not be read (null when it was).</summary>
        public string OutlineWhy;
        /// <summary>The title block's box on the sheet (sheet mm), when the sheet has one: where the sheet number is expected.</summary>
        public double[] TitleBlockMm;
        public List<PdfViewportBox> Viewports = new List<PdfViewportBox>();
    }

    public sealed class PdfNoteRequest
    {
        public string Text;
        public long? NoteId;
        public int? Page;
        public int? ExpectedLines;
        /// <summary>Why the note's text could not be resolved (note_id not a text note...).</summary>
        public string Unresolved;
        /// <summary>note_id: the view that owns the note (a sheet, or a view placed on one).</summary>
        public long? OwnerViewId;
        /// <summary>note_id: the note's box carried to its sheet (sheet mm) and that sheet's outline; the search is limited to it.</summary>
        public double[] SheetRectMm, OutlineMm;
        /// <summary>Unresolved because of what this file shows (the note's view is not in it, or on several of its pages), not because the input is wrong: partial, not a finding.</summary>
        public bool NotInThisFile;

        public PdfNoteRequest Clone() => (PdfNoteRequest)MemberwiseClone();
    }

    public static class PdfQaPaper
    {
        public const double PtPerMm = 72.0 / 25.4;
        /// <summary>How far outside its reported box a viewport's ink is still searched (paper mm).</summary>
        public const double ViewportSearchMarginMm = 10.0;
        public const int MaxCells = 250000;
        public const long MaxRasterVisits = 30000000;
        public const double HairlinePt = 0.25;
        /// <summary>A page maps EXACTLY onto its sheet when its scale is 72/25.4 pt per mm and its aspect ratio the sheet's, both within this.</summary>
        public const double MappingTolerance = 0.005;
        /// <summary>A note's box on the sheet is grown by this before its words are searched (the box Revit reports is the note's, not the printed glyphs').</summary>
        public const double NoteSearchMarginMm = 2.0;

        public const string GrayLimit = "vector drawing only: cells under text and images are left out of the tone (text_fraction, image_fraction), " +
            "shadings/soft masks/blend modes not drawn, line widths as written in the content stream (no CTM scaling)";

        // ---- sheet <-> page ---------------------------------------------------------------------

        /// <summary>
        /// The sheet outline fitted to the page, centred, one uniform scale: {s (pt per sheet mm), offX, offY}.
        /// With paper_format Default the page IS the sheet (s = 72/25.4). A PDF printed with a zoom or an
        /// offset is not this mapping; the reply says which mapping was assumed.
        /// </summary>
        public static double[] SheetToPage(double[] outlineMm, double pageWidthPt, double pageHeightPt)
        {
            if (outlineMm == null || outlineMm.Length != 4) return null;
            double ow = outlineMm[2] - outlineMm[0], oh = outlineMm[3] - outlineMm[1];
            if (!(ow > 0) || !(oh > 0) || !(pageWidthPt > 0) || !(pageHeightPt > 0)) return null;
            double s = Math.Min(pageWidthPt / ow, pageHeightPt / oh);
            return new[] { s, (pageWidthPt - ow * s) / 2 - outlineMm[0] * s, (pageHeightPt - oh * s) / 2 - outlineMm[1] * s };
        }

        /// <summary>
        /// B2: how a page maps onto its sheet. exact = scale within 0.5 % of 72/25.4 pt per mm AND the same aspect
        /// ratio; anything else is approximate (a zoom, another paper size, a rotation), and then nothing that needs
        /// the exact paper position (ink_from_box_center_mm for anchor=ink) is offered. The sheet number is looked for
        /// among the page's words: not found, the page order is unverified.
        /// </summary>
        public static JObject Mapping(PdfPageFacts page, PdfSheetMap map, out double[] m, out bool exact)
        {
            exact = false;
            m = SheetToPage(map.OutlineMm, page.Width, page.Height);
            var row = new JObject { ["page"] = page.Page, ["sheet_id"] = map.SheetId, ["sheet"] = map.Sheet };
            if (m == null)
            {
                row["mapping"] = "unmapped";
                row["why"] = map.OutlineWhy ?? (map.OutlineMm == null ? "the sheet's outline could not be read" : "the sheet's outline or the page has no area");
                return row;
            }
            double ow = map.OutlineMm[2] - map.OutlineMm[0], oh = map.OutlineMm[3] - map.OutlineMm[1];
            double aspectSheet = ow / oh, aspectPage = page.Width / page.Height;
            bool scaleOk = Math.Abs(m[0] / PtPerMm - 1) <= MappingTolerance;
            bool aspectOk = Math.Abs(aspectPage / aspectSheet - 1) <= MappingTolerance;
            exact = scaleOk && aspectOk;
            row["scale_pt_per_mm"] = Math.Round(m[0], 4);
            row["scale_expected_pt_per_mm"] = Math.Round(PtPerMm, 4);
            row["aspect_sheet"] = Math.Round(aspectSheet, 4);
            row["aspect_page"] = Math.Round(aspectPage, 4);
            row["mapping"] = exact ? "exact" : "approximate";
            if (!exact)
                row["why"] = (scaleOk ? "" : "the page is printed at " + Math.Round(m[0] / PtPerMm * 100, 1) + " % of the sheet's size") +
                             (scaleOk || aspectOk ? "" : "; ") + (aspectOk ? "" : "the page's aspect ratio is not the sheet's (another paper size or a rotation)") +
                             ": sheet and page were matched by centring and one scale, an assumption";
            bool? onPage = SheetNumberOnPage(page, map.SheetNumber, ToPage(m, map.TitleBlockMm));
            row["sheet_number"] = map.SheetNumber;
            row["sheet_number_on_page"] = onPage.HasValue ? (JToken)onPage.Value : JValue.CreateNull();
            row["page_order_unverified"] = onPage != true;
            return row;
        }

        /// <summary>
        /// True when the sheet number is printed on the page (one to three consecutive words, punctuation ignored)
        /// inside the title block's box, or - with no title block box, or none of them inside it - exactly once on the
        /// page: a number printed several times outside the title block (a "1" that is also a detail number) does not
        /// prove which sheet the page is. Null when there is no number to look for.
        /// </summary>
        public static bool? SheetNumberOnPage(PdfPageFacts page, string sheetNumber, double[] titleBlockPt = null)
        {
            string target = Normalize(sheetNumber);
            if (target.Length == 0) return null;
            List<string> words = page.Words.Select(w => Normalize(w.Text)).ToList();
            int found = 0;
            for (int i = 0; i < words.Count; i++)
            {
                string acc = "";
                for (int j = i; j < words.Count && j < i + 3; j++)
                {
                    acc += words[j];
                    if (acc == target)
                    {
                        found++;
                        PdfWordFact w = page.Words[i];
                        if (titleBlockPt != null && Contains(titleBlockPt, (w.Left + w.Right) / 2, (w.Bottom + w.Top) / 2)) return true;
                        break;
                    }
                    if (acc.Length >= target.Length) break;
                }
            }
            return found == 1;
        }

        public static double[] ToPage(double[] m, double[] rectMm)
            => m == null || rectMm == null ? null : new[] { m[1] + rectMm[0] * m[0], m[2] + rectMm[1] * m[0], m[1] + rectMm[2] * m[0], m[2] + rectMm[3] * m[0] };

        public static double[] ToSheet(double[] m, double[] rectPt)
            => m == null || rectPt == null ? null : new[] { (rectPt[0] - m[1]) / m[0], (rectPt[1] - m[2]) / m[0], (rectPt[2] - m[1]) / m[0], (rectPt[3] - m[2]) / m[0] };

        // ---- viewport ink -----------------------------------------------------------------------

        private static bool Contains(double[] r, double x, double y) => x >= r[0] && x <= r[2] && y >= r[1] && y <= r[3];
        private static double[] Grow(double[] r, double d) => new[] { r[0] - d, r[1] - d, r[2] + d, r[3] + d };
        private static double DistanceTo(double[] r, double x, double y)
        {
            double dx = Math.Max(0, Math.Max(r[0] - x, x - r[2])), dy = Math.Max(0, Math.Max(r[1] - y, y - r[3]));
            return Math.Sqrt(dx * dx + dy * dy);
        }

        /// <summary>
        /// Which viewport each drawn item belongs to. An item goes to the title of a viewport when its centre
        /// lies in that title's outline; otherwise to the viewport whose box (grown by the search margin)
        /// holds its centre and is no smaller than the item - the nearest box when several do. Items wider or
        /// taller than every candidate (a title block frame line) stay unassigned. Index -1: unassigned;
        /// index n + k: the title of viewport k.
        /// </summary>
        public static int[] Assign(IList<double[]> items, IList<double[]> boxesPt, IList<double[]> labelsPt, double marginPt)
        {
            int n = boxesPt.Count;
            var owner = new int[items.Count];
            for (int i = 0; i < items.Count; i++)
            {
                double[] it = items[i];
                double cx = (it[0] + it[2]) / 2, cy = (it[1] + it[3]) / 2;
                owner[i] = -1;
                for (int k = 0; k < n; k++)
                    if (labelsPt[k] != null && Contains(Grow(labelsPt[k], 0.5 * PtPerMm), cx, cy)) { owner[i] = n + k; break; }
                if (owner[i] >= 0) continue;
                double best = double.MaxValue, bestArea = double.MaxValue;
                for (int k = 0; k < n; k++)
                {
                    if (boxesPt[k] == null) continue;
                    double[] g = Grow(boxesPt[k], marginPt);
                    if (!Contains(g, cx, cy)) continue;
                    if (it[2] - it[0] > g[2] - g[0] + 1e-6 || it[3] - it[1] > g[3] - g[1] + 1e-6) continue;
                    double d = DistanceTo(boxesPt[k], cx, cy);
                    double area = (boxesPt[k][2] - boxesPt[k][0]) * (boxesPt[k][3] - boxesPt[k][1]);
                    if (d < best - 1e-9 || (Math.Abs(d - best) <= 1e-9 && area < bestArea)) { best = d; bestArea = area; owner[i] = k; }
                }
            }
            return owner;
        }

        /// <summary>The printed ink of every viewport of a sheet against its reported box, in sheet mm.</summary>
        public static JArray ViewportInk(PdfPageFacts page, PdfSheetMap map, double marginMm = ViewportSearchMarginMm)
        {
            Mapping(page, map, out double[] m, out bool exact);
            return ViewportInk(page, map, m, exact, marginMm);
        }

        /// <summary>The same, with the mapping already judged: approximate withholds ink_from_box_center_mm (anchor=ink needs the exact paper).</summary>
        public static JArray ViewportInk(PdfPageFacts page, PdfSheetMap map, double[] m, bool exact, double marginMm = ViewportSearchMarginMm)
        {
            var rows = new JArray();
            if (m == null) return rows;
            var boxes = map.Viewports.Select(v => ToPage(m, v.BoxMm)).ToList();
            var labels = map.Viewports.Select(v => ToPage(m, v.LabelMm)).ToList();
            List<double[]> items = page.Marks.Select(x => x.Rect).ToList();
            int[] owner = Assign(items, boxes, labels, marginMm * m[0]);
            int n = map.Viewports.Count;
            for (int k = 0; k < n; k++)
            {
                PdfViewportBox v = map.Viewports[k];
                var mine = new List<double[]>(); var title = new List<double[]>();
                for (int i = 0; i < items.Count; i++)
                {
                    if (owner[i] == k) mine.Add(items[i]);
                    else if (owner[i] == n + k) title.Add(items[i]);
                }
                double[] inkPt = PdfQa.Union(mine);
                double[] ink = ToSheet(m, inkPt);
                var row = new JObject
                {
                    ["page"] = page.Page, ["sheet_id"] = map.SheetId, ["viewport_id"] = v.ViewportId, ["view_id"] = v.ViewId, ["view"] = v.View,
                    ["box_mm"] = Rect(v.BoxMm), ["ink_mm"] = Rect(ink), ["ink_items"] = mine.Count,
                    ["label_mm"] = Rect(v.LabelMm), ["label_ink_mm"] = Rect(ToSheet(m, PdfQa.Union(title))), ["label_ink_items"] = title.Count
                };
                if (ink != null && v.BoxMm != null)
                {
                    double bcx = (v.BoxMm[0] + v.BoxMm[2]) / 2, bcy = (v.BoxMm[1] + v.BoxMm[3]) / 2;
                    row["ink_minus_box_mm"] = new JObject
                    {
                        ["left"] = R(ink[0] - v.BoxMm[0]), ["bottom"] = R(ink[1] - v.BoxMm[1]),
                        ["right"] = R(ink[2] - v.BoxMm[2]), ["top"] = R(ink[3] - v.BoxMm[3])
                    };
                    row["center_offset_mm"] = new JArray(R((ink[0] + ink[2]) / 2 - bcx), R((ink[1] + ink[3]) / 2 - bcy));
                    if (exact && !page.MarksTruncated) row["ink_from_box_center_mm"] = new JArray(R(ink[0] - bcx), R(ink[1] - bcy), R(ink[2] - bcx), R(ink[3] - bcy));
                    else
                    {
                        row["ink_from_box_center_mm"] = JValue.CreateNull();
                        row["ink_anchor"] = page.MarksTruncated
                            ? "not offered: the page exceeded the limit of measured ink items"
                            : "not offered: this page's mapping onto its sheet is approximate, so the ink's position on the sheet is not exact enough for anchor=ink";
                    }
                    row["max_difference_mm"] = R(new[] { ink[0] - v.BoxMm[0], ink[1] - v.BoxMm[1], ink[2] - v.BoxMm[2], ink[3] - v.BoxMm[3] }.Max(Math.Abs));
                }
                if (ink == null)
                {
                    row["ink_found"] = false;
                    row["why"] = v.BoxMm == null ? "Revit reported no box for this viewport"
                        : page.MarksTruncated ? "no viewport ink found before the page's ink measurement limit"
                        : "no printed item was assigned to this viewport (an empty view, a view printed elsewhere, or a mapping that missed it)";
                }
                row["flags"] = InkFlags(row, inkPt, page, exact, v.BoxMm == null);
                if (page.MarksTruncated) row["flags_basis"] = "partial";
                else if (!exact) row["flags_basis"] = "approximate";
                if (page.Drawing != null)
                {
                    if (page.MarksTruncated)
                        row["gray"] = new JObject { ["measured"] = false, ["why"] = "the page's ink measurement limit left the viewport's printed bounds incomplete" };
                    else if (inkPt != null)
                        row["gray"] = Gray(page, inkPt);
                }
                rows.Add(row);
            }
            return rows;
        }

        /// <summary>Ink further than this from its box's centre is flagged off_box (sheet mm). A heuristic, not a verdict: a drawing may sit off-centre in its crop.</summary>
        public const double OffBoxCenterMm = 25;
        /// <summary>Ink this far past its box on any side is flagged overflows_box (sheet mm).</summary>
        public const double OverflowBoxMm = 5;
        /// <summary>Ink within this of the page box edge (PDF mm) is flagged touches_page_edge.</summary>
        public const double PageEdgeToleranceMm = 0.5;

        /// <summary>Informational flags for one viewport. Truncated pages cannot confirm that a viewport is empty.</summary>
        private static JArray InkFlags(JObject row, double[] inkPt, PdfPageFacts page, bool exact, bool noBox)
        {
            var flags = new JArray();
            if (page.MarksTruncated) flags.Add("ink_items_truncated");
            if (row.Value<bool?>("ink_found") == false)
            {
                if (noBox) flags.Add("no_box");
                else if (!page.MarksTruncated) flags.Add("empty");
                return flags;
            }
            if (exact && row["center_offset_mm"] is JArray c && Math.Sqrt(Math.Pow(c[0].Value<double>(), 2) + Math.Pow(c[1].Value<double>(), 2)) >= OffBoxCenterMm) flags.Add("off_box");
            if (exact && row["ink_minus_box_mm"] is JObject d
                && (d.Value<double>("left") <= -OverflowBoxMm || d.Value<double>("bottom") <= -OverflowBoxMm || d.Value<double>("right") >= OverflowBoxMm || d.Value<double>("top") >= OverflowBoxMm))
                flags.Add("overflows_box");
            double edge = PageEdgeToleranceMm * PtPerMm;
            if (inkPt != null && (inkPt[0] <= page.Left + edge || inkPt[1] <= page.Bottom + edge || inkPt[2] >= page.Left + page.Width - edge || inkPt[3] >= page.Bottom + page.Height - edge)) flags.Add("touches_page_edge");
            return flags;
        }

        public static JToken Rect(double[] r)
            => r == null ? (JToken)JValue.CreateNull() : new JArray(R(r[0]), R(r[1]), R(r[2]), R(r[3]));

        private static double R(double v) => Math.Round(v, 2);

        // ---- printed gray -----------------------------------------------------------------------

        /// <summary>Darkness of an RGB colour in 0..1 (0 white).</summary>
        public static double Darkness(double r, double g, double b)
        {
            double lum = 0.299 * r + 0.587 * g + 0.114 * b;
            return Math.Max(0, Math.Min(1, 1 - lum));
        }

        /// <summary>The printed gray inside a box of the page (points).</summary>
        public static JObject Gray(PdfPageFacts page, double[] regionPt, double cellMm = 0.1)
        {
            if (page.Drawing == null)
                return new JObject { ["measured"] = false, ["why"] = "the drawing was not captured for this page" };
            if (page.TextOrImageMarksOmitted)
                return new JObject { ["measured"] = false, ["why"] = "the page's ink measurement limit omitted text or image bounds" };
            return Gray(page.Drawing, page.Marks.Where(x => x.Kind == "image").Select(x => x.Rect).ToList(),
                page.Marks.Where(x => x.Kind == "text").Select(x => x.Rect).ToList(), regionPt, cellMm, page.DrawingTruncated);
        }

        public static JObject Gray(IList<PdfDrawOp> ops, IList<double[]> images, IList<double[]> texts, double[] region, double cellMm, bool opsTruncated = false)
        {
            double w = region[2] - region[0], h = region[3] - region[1];
            if (!(w > 0) || !(h > 0)) return new JObject { ["measured"] = false, ["why"] = "the region has no area" };
            double cell = Math.Max(0.01, cellMm) * PtPerMm;
            if ((w / cell) * (h / cell) > MaxCells) cell = Math.Sqrt(w * h / MaxCells);
            int nx = Math.Max(1, (int)Math.Round(w / cell)), ny = Math.Max(1, (int)Math.Round(h / cell));
            var g = new Grid { X0 = region[0], Y0 = region[1], Cx = w / nx, Cy = h / ny, Nx = nx, Ny = ny };
            cell = Math.Sqrt(g.Cx * g.Cy);
            var dark = new double[nx * ny];
            long visits = 0; bool truncated = opsTruncated;
            foreach (PdfDrawOp op in ops)
            {
                if (visits > MaxRasterVisits) { truncated = true; break; }
                double reach = op.Stroke ? Math.Max(op.Width, HairlinePt) : 0;
                if (op.Box == null || op.Box[2] + reach < region[0] || op.Box[0] - reach > region[2] || op.Box[3] + reach < region[1] || op.Box[1] - reach > region[3]) continue;
                if (op.Fill) visits += FillOp(op, dark, g);
                if (op.Stroke) visits += StrokeOp(op, dark, g);
            }
            bool[] excluded = new bool[nx * ny];
            int imageCells = Mark(images, excluded, g);
            var textMask = new bool[nx * ny];
            int textCells = Mark(texts, textMask, g);
            var bins = new int[10];
            double sum = 0; int measured = 0, inked = 0;
            for (int i = 0; i < dark.Length; i++)
            {
                if (excluded[i] || textMask[i]) continue;
                measured++;
                sum += dark[i];
                if (dark[i] > 0.05) inked++;
                bins[Math.Min(9, (int)(dark[i] * 10))]++;
            }
            double mean = measured == 0 ? 0 : sum / measured;
            return new JObject
            {
                ["measured"] = measured > 0,
                ["mean_darkness"] = Math.Round(mean, 4),
                ["mean_gray_255"] = (int)Math.Round(255 * (1 - mean)),
                ["inked_fraction"] = measured == 0 ? 0 : Math.Round((double)inked / measured, 4),
                ["histogram"] = new JArray(bins.Select(b => (JToken)(measured == 0 ? 0 : Math.Round((double)b / measured, 4)))),
                ["histogram_means"] = "fraction of the region's cells per darkness tenth: [0] white..0.1, [9] 0.9..black",
                ["image_fraction"] = Math.Round((double)imageCells / dark.Length, 4),
                ["text_fraction"] = Math.Round((double)textCells / dark.Length, 4),
                ["cell_mm"] = Math.Round(cell / PtPerMm, 3),
                ["truncated"] = truncated,
                ["limits"] = GrayLimit
            };
        }

        private sealed class Grid
        {
            public double X0, Y0, Cx, Cy;
            public int Nx, Ny;
            public int Col(double x, bool up) => up ? (int)Math.Ceiling((x - X0) / Cx - 0.5) : (int)Math.Floor((x - X0) / Cx - 0.5);
            public int Row(double y, bool up) => up ? (int)Math.Ceiling((y - Y0) / Cy - 0.5) : (int)Math.Floor((y - Y0) / Cy - 0.5);
            public double X(int c) => X0 + (c + 0.5) * Cx;
            public double Y(int r) => Y0 + (r + 0.5) * Cy;
        }

        private static int Mark(IList<double[]> rects, bool[] mask, Grid g)
        {
            int count = 0;
            foreach (double[] r in rects ?? new List<double[]>())
            {
                int c0 = Math.Max(0, g.Col(r[0], true)), c1 = Math.Min(g.Nx - 1, g.Col(r[2], false));
                int r0 = Math.Max(0, g.Row(r[1], true)), r1 = Math.Min(g.Ny - 1, g.Row(r[3], false));
                for (int y = r0; y <= r1; y++)
                    for (int x = c0; x <= c1; x++)
                        if (!mask[y * g.Nx + x]) { mask[y * g.Nx + x] = true; count++; }
            }
            return count;
        }

        private static long FillOp(PdfDrawOp op, double[] dark, Grid g)
        {
            long visits = 0;
            int r0 = Math.Max(0, g.Row(op.Box[1], false)), r1 = Math.Min(g.Ny - 1, g.Row(op.Box[3], true));
            var xs = new List<KeyValuePair<double, int>>();
            for (int row = r0; row <= r1; row++)
            {
                double y = g.Y(row);
                xs.Clear();
                foreach (List<double[]> ring in op.Rings)
                    for (int i = 0; i < ring.Count; i++)
                    {
                        double[] p = ring[i], q = ring[(i + 1) % ring.Count];
                        if (p[1] == q[1]) continue;
                        bool up = q[1] > p[1];
                        if (up ? (y < p[1] || y >= q[1]) : (y < q[1] || y >= p[1])) continue;
                        xs.Add(new KeyValuePair<double, int>(p[0] + (y - p[1]) * (q[0] - p[0]) / (q[1] - p[1]), up ? 1 : -1));
                    }
                if (xs.Count < 2) continue;
                xs.Sort((a, b) => a.Key.CompareTo(b.Key));
                int wind = 0;
                for (int k = 0; k < xs.Count - 1; k++)
                {
                    wind += op.EvenOdd ? 1 : xs[k].Value;
                    bool inside = op.EvenOdd ? (wind % 2) == 1 : wind != 0;
                    if (!inside) continue;
                    int c0 = Math.Max(0, g.Col(xs[k].Key, true)), c1 = Math.Min(g.Nx - 1, g.Col(xs[k + 1].Key, false));
                    for (int c = c0; c <= c1; c++) { dark[row * g.Nx + c] = op.FillDark; visits++; }
                }
            }
            return visits;
        }

        private static long StrokeOp(PdfDrawOp op, double[] dark, Grid g)
        {
            long visits = 0;
            double cell = Math.Sqrt(g.Cx * g.Cy);
            double width = op.Width > 0 ? op.Width : HairlinePt;
            double half = width / 2, reach = Math.Max(half, cell / 2), cov = Math.Min(1, width / cell);
            for (int ri = 0; ri < op.Rings.Count; ri++)
            {
                List<double[]> ring = op.Rings[ri];
                int segs = op.Closed[ri] ? ring.Count : ring.Count - 1;
                for (int i = 0; i < segs; i++)
                {
                    double[] a = ring[i], b = ring[(i + 1) % ring.Count];
                    int c0 = Math.Max(0, g.Col(Math.Min(a[0], b[0]) - reach, false)), c1 = Math.Min(g.Nx - 1, g.Col(Math.Max(a[0], b[0]) + reach, true));
                    int r0 = Math.Max(0, g.Row(Math.Min(a[1], b[1]) - reach, false)), r1 = Math.Min(g.Ny - 1, g.Row(Math.Max(a[1], b[1]) + reach, true));
                    double dx = b[0] - a[0], dy = b[1] - a[1], len2 = dx * dx + dy * dy;
                    for (int row = r0; row <= r1; row++)
                    {
                        double y = g.Y(row);
                        for (int c = c0; c <= c1; c++)
                        {
                            visits++;
                            double x = g.X(c);
                            double t = len2 <= 0 ? 0 : Math.Max(0, Math.Min(1, ((x - a[0]) * dx + (y - a[1]) * dy) / len2));
                            double ex = a[0] + t * dx - x, ey = a[1] + t * dy - y;
                            if (ex * ex + ey * ey > reach * reach) continue;
                            int idx = row * g.Nx + c;
                            dark[idx] += (op.StrokeDark - dark[idx]) * cov;
                        }
                    }
                }
            }
            return visits;
        }

        // ---- printed lines of a note -------------------------------------------------------------

        public static string Normalize(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length);
            foreach (char ch in s.Normalize(NormalizationForm.FormC))
                if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
            return sb.ToString();
        }

        /// <summary>
        /// The note's text found again among the page's words (content order, punctuation and spaces
        /// ignored, up to 3 foreign words skipped inside it) and the baselines it sits on. Null when the
        /// text is not on the page or is there more than once (use FindAll to tell those apart).
        /// </summary>
        public static List<PdfWordFact> FindText(PdfPageFacts page, string text)
        {
            List<List<PdfWordFact>> all = FindAll(page.Words, text);
            return all.Count == 1 ? all[0] : null;
        }

        /// <summary>
        /// Every place the text is printed among these words. A match the same line goes on after - the next word
        /// of the SAME NOTE (same font and size as the match's last word) on the same baseline, no farther than two
        /// letter heights - is part of a longer text and is not counted. A dimension or a neighbouring label in another
        /// font or size does not void the match. With note_id the words are already limited to the note's box.
        /// </summary>
        public static List<List<PdfWordFact>> FindAll(IList<PdfWordFact> words, string text)
        {
            var found = new List<List<PdfWordFact>>();
            string target = Normalize(text);
            if (target.Length == 0) return found;
            var used = new HashSet<PdfWordFact>();
            for (int start = 0; start < words.Count; start++)
            {
                if (used.Contains(words[start])) continue;
                string first = Normalize(words[start].Text);
                if (first.Length == 0 || !target.StartsWith(first, StringComparison.Ordinal)) continue;
                var got = new List<PdfWordFact> { words[start] };
                string acc = first; int skips = 0, last = start;
                for (int j = start + 1; j < words.Count && acc.Length < target.Length; j++)
                {
                    string w = Normalize(words[j].Text);
                    if (w.Length == 0) continue;
                    if (target.Length >= acc.Length + w.Length && string.CompareOrdinal(target, acc.Length, w, 0, w.Length) == 0) { acc += w; got.Add(words[j]); last = j; }
                    else if (++skips > 3) break;
                }
                if (acc != target || ContinuesOnItsLine(words, last)) continue;
                found.Add(got);
                foreach (PdfWordFact w in got) used.Add(w);
            }
            return found;
        }

        private static bool ContinuesOnItsLine(IList<PdfWordFact> words, int last)
        {
            PdfWordFact end = words[last];
            double h = Math.Max(0.1, end.Top - end.Bottom);
            for (int j = last + 1; j < words.Count; j++)
            {
                PdfWordFact next = words[j];
                if (Normalize(next.Text).Length == 0 || !SameNote(end, next)) continue;
                return Math.Abs(next.Bottom - end.Bottom) <= 0.5 * h && next.Left >= end.Left && next.Left - end.Right <= 2 * h;
            }
            return false;
        }

        /// <summary>Same font and size (within 5 %); an unknown font or size on either word is not told apart.</summary>
        public static bool SameNote(PdfWordFact a, PdfWordFact b)
        {
            if (a.Font != null && b.Font != null && !string.Equals(a.Font, b.Font, StringComparison.Ordinal)) return false;
            if (a.Size > 0 && b.Size > 0 && Math.Abs(a.Size - b.Size) > 0.05 * Math.Max(a.Size, b.Size)) return false;
            return true;
        }

        /// <summary>
        /// Which page a note by id is searched on: the pages whose sheet shows the note's view (a legend can be on
        /// several). The page asked when it is one of them; the only one when none was asked; otherwise null with why.
        /// </summary>
        public static int? NotePage(IList<int> pagesShowingIt, int? asked, out string why)
        {
            why = null;
            var list = (pagesShowingIt ?? new int[0]).Distinct().OrderBy(x => x).ToList();
            string where = string.Join(", ", list);
            if (list.Count == 0) { why = "it is in a view that no sheet of this PDF shows"; return null; }
            if (asked.HasValue)
            {
                if (list.Contains(asked.Value)) return asked.Value;
                why = "it is shown on page(s) " + where + ", not on the page asked (" + asked.Value + ")";
                return null;
            }
            if (list.Count == 1) return list[0];
            why = "its view is shown on pages " + where + "; send page to say which one";
            return null;
        }

        /// <summary>The evidence of a viewport auto-numbered in the batch and moved to a free number for a later place_view.</summary>
        public static JObject DetailNumberMoved(long viewportId, string before, string after, string wanted)
            => new JObject
            {
                ["viewport_id"] = viewportId, ["number_before"] = before, ["number_after"] = after,
                ["why"] = "Revit auto-numbered it with '" + wanted + "', which a later place_view of this batch asked for"
            };

        /// <summary>Words grouped into printed lines by baseline (bottom within half the shorter height), top line first.</summary>
        public static List<List<PdfWordFact>> Lines(IEnumerable<PdfWordFact> words)
        {
            var lines = new List<List<PdfWordFact>>();
            foreach (PdfWordFact w in words.OrderByDescending(x => x.Bottom))
            {
                double h = Math.Max(0.1, w.Top - w.Bottom);
                List<PdfWordFact> line = lines.FirstOrDefault(l => Math.Abs(l[0].Bottom - w.Bottom) <= 0.5 * Math.Min(h, Math.Max(0.1, l[0].Top - l[0].Bottom)));
                if (line == null) lines.Add(new List<PdfWordFact> { w }); else line.Add(w);
            }
            return lines;
        }

        /// <summary>
        /// B3: the printed lines of one note. With note_id the search is limited to the page of the note's sheet and
        /// to the note's box carried to that page (grown by NoteSearchMarginMm); by text alone it covers the page asked
        /// or every page. Two or more places with that text answer ambiguous, with no line count.
        /// </summary>
        public static JObject NoteLines(IList<PdfPageFacts> pages, PdfNoteRequest note)
        {
            string shown = note.Text == null ? null : (note.Text.Length > 80 ? note.Text.Substring(0, 80) + "…" : note.Text);
            var row = new JObject { ["text"] = shown };
            if (note.NoteId.HasValue) row["note_id"] = note.NoteId.Value;
            if (note.Unresolved != null)
            {
                row["found"] = false; row["why"] = note.Unresolved;
                if (note.NotInThisFile) row["not_measured"] = true;
                return row;
            }
            bool boxed = note.SheetRectMm != null && note.OutlineMm != null && note.Page.HasValue;
            row["scope"] = boxed ? "note_box" : note.Page.HasValue ? "page" : "all_pages";
            var hits = new List<Tuple<PdfPageFacts, List<PdfWordFact>>>();
            foreach (PdfPageFacts p in pages)
            {
                if (note.Page.HasValue && p.Page != note.Page.Value) continue;
                IList<PdfWordFact> words = p.Words;
                if (boxed)
                {
                    double[] m = SheetToPage(note.OutlineMm, p.Width, p.Height);
                    double[] r = ToPage(m, new[] { note.SheetRectMm[0] - NoteSearchMarginMm, note.SheetRectMm[1] - NoteSearchMarginMm,
                                                    note.SheetRectMm[2] + NoteSearchMarginMm, note.SheetRectMm[3] + NoteSearchMarginMm });
                    if (r == null) { row["found"] = false; row["why"] = "the note's sheet could not be mapped onto page " + p.Page; return row; }
                    row["search_box_page_mm"] = Rect(r.Select(v => v / PtPerMm).ToArray());
                    words = p.Words.Where(w => Contains(r, (w.Left + w.Right) / 2, (w.Bottom + w.Top) / 2)).ToList();
                }
                foreach (List<PdfWordFact> hit in FindAll(words, note.Text)) hits.Add(Tuple.Create(p, hit));
            }
            if (hits.Count > 1)
            {
                row["found"] = true;
                row["ambiguous"] = true;
                row["candidates"] = hits.Count;
                row["candidate_pages"] = new JArray(hits.Select(h => h.Item1.Page).Distinct());
                row["why"] = "the text is printed " + hits.Count + " times in the searched area; the lines of THIS note cannot be told apart" +
                             (boxed ? "" : " (send note_id with view_ids, the sheets, to search only the note's own box)");
                return row;
            }
            if (hits.Count == 1)
            {
                PdfPageFacts p = hits[0].Item1; List<PdfWordFact> hit = hits[0].Item2;
                List<List<PdfWordFact>> lines = Lines(hit);
                double[] box = PdfQa.Union(hit.Select(w => new[] { w.Left, w.Bottom, w.Right, w.Top }));
                row["found"] = true;
                row["ambiguous"] = false;
                row["page"] = p.Page;
                row["lines"] = lines.Count;
                row["line_texts"] = new JArray(lines.Select(l => (JToken)string.Join(" ", l.OrderBy(w => w.Left).Select(w => w.Text))));
                row["box_page_mm"] = Rect(box.Select(v => v / PtPerMm).ToArray());
                if (note.ExpectedLines.HasValue)
                {
                    row["expected_lines"] = note.ExpectedLines.Value;
                    row["matches_expected"] = lines.Count == note.ExpectedLines.Value;
                }
                return row;
            }
            row["found"] = false;
            row["why"] = "the text was not found " + (boxed ? "inside the note's box on page " + note.Page.Value : "among the words of " + (note.Page.HasValue ? "page " + note.Page.Value : "any page")) +
                         " (a note drawn as outlines, a font without a text layer, different text, or text that goes on past it on the same line)";
            return row;
        }

        // ---- before / after -----------------------------------------------------------------------

        public static JObject Compare(IList<PdfPageFacts> before, IList<PdfPageFacts> after, int maxWords = 40)
        {
            var pages = new JArray();
            int changed = 0;
            int n = Math.Max(before.Count, after.Count);
            for (int i = 0; i < n; i++)
            {
                PdfPageFacts a = i < before.Count ? before[i] : null, b = i < after.Count ? after[i] : null;
                if (a == null || b == null)
                {
                    changed++;
                    pages.Add(new JObject { ["page"] = i + 1, ["state"] = a == null ? "added" : "removed" });
                    continue;
                }
                var wa = Count(a.Words.Select(w => w.Text)); var wb = Count(b.Words.Select(w => w.Text));
                var removed = Diff(wa, wb); var added = Diff(wb, wa);
                int la = a.Fonts.Values.Sum(), lb = b.Fonts.Values.Sum();
                var fa = new SortedSet<string>(a.Fonts.Keys.Select(PdfQa.FontKey), StringComparer.Ordinal);
                var fb = new SortedSet<string>(b.Fonts.Keys.Select(PdfQa.FontKey), StringComparer.Ordinal);
                double[] ia = PdfQa.Union(a.Ink), ib = PdfQa.Union(b.Ink);
                bool same = removed.Count == 0 && added.Count == 0 && la == lb && fa.SetEquals(fb) && a.Paths == b.Paths && a.Segments == b.Segments && a.Images == b.Images
                            && Math.Abs(a.Width - b.Width) < 0.5 && Math.Abs(a.Height - b.Height) < 0.5;
                if (!same) changed++;
                var row = new JObject
                {
                    ["page"] = i + 1, ["state"] = same ? "same" : "changed",
                    ["words"] = new JObject { ["before"] = a.Words.Count, ["after"] = b.Words.Count,
                        ["removed"] = new JArray(removed.Take(maxWords)), ["added"] = new JArray(added.Take(maxWords)),
                        ["truncated"] = removed.Count > maxWords || added.Count > maxWords },
                    ["fonts"] = new JObject { ["removed"] = new JArray(fa.Except(fb)), ["added"] = new JArray(fb.Except(fa)) },
                    ["letters"] = new JArray(la, lb), ["letters_delta"] = lb - la,
                    ["paths_delta"] = b.Paths - a.Paths, ["segments_delta"] = b.Segments - a.Segments,
                    ["paths"] = new JArray(a.Paths, b.Paths), ["segments"] = new JArray(a.Segments, b.Segments), ["images"] = new JArray(a.Images, b.Images),
                    ["ink_box_mm"] = new JObject { ["before"] = Rect(Mm(ia)), ["after"] = Rect(Mm(ib)) }
                };
                if (ia != null && ib != null)
                    row["ink_box_shift_mm"] = new JArray(R((ib[0] - ia[0]) / PtPerMm), R((ib[1] - ia[1]) / PtPerMm), R((ib[2] - ia[2]) / PtPerMm), R((ib[3] - ia[3]) / PtPerMm));
                pages.Add(row);
            }
            return new JObject
            {
                ["verdict"] = changed == 0 ? "same" : "changed",
                ["pages_before"] = before.Count, ["pages_after"] = after.Count, ["pages_changed"] = changed,
                ["pages"] = pages,
                ["means"] = "page i against page i: words as a multiset (moved text is not a change), font families, painted paths, path segments, images; a picture-level difference is not measured"
            };
        }

        private static double[] Mm(double[] pt) => pt == null ? null : pt.Select(v => v / PtPerMm).ToArray();

        private static Dictionary<string, int> Count(IEnumerable<string> words)
        {
            var d = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (string w in words) d[w] = d.TryGetValue(w, out int c) ? c + 1 : 1;
            return d;
        }

        private static List<string> Diff(Dictionary<string, int> a, Dictionary<string, int> b)
        {
            var list = new List<string>();
            foreach (KeyValuePair<string, int> kv in a.OrderBy(x => x.Key, StringComparer.Ordinal))
            {
                int extra = kv.Value - (b.TryGetValue(kv.Key, out int c) ? c : 0);
                for (int i = 0; i < extra; i++) list.Add(kv.Key);
            }
            return list;
        }

        // ---- fonts installed for one user only ----------------------------------------------------

        /// <summary>Family key of a Windows font registry value name: "Arial Narrow Bold Italic (TrueType)" -> "arialnarrow".</summary>
        public static string RegistryFontKey(string valueName)
        {
            if (string.IsNullOrWhiteSpace(valueName)) return "";
            string s = Regex.Replace(valueName, "\\s*\\((truetype|opentype|all res|type 1)[^)]*\\)\\s*$", "", RegexOptions.IgnoreCase).Trim();
            s = s.Split('&')[0].Trim();
            string prev;
            do
            {
                prev = s;
                s = Regex.Replace(s, "\\s+(bold|italic|oblique|regular|light|semibold|black|condensed)$", "", RegexOptions.IgnoreCase).Trim();
            } while (s != prev);
            return PdfQa.FontKey(s);
        }

        /// <summary>Where an expected family is installed: user, machine, both or none (null inventory: unknown).</summary>
        public static string Installed(string family, IDictionary<string, string> inventory)
        {
            if (inventory == null) return "unknown";
            return inventory.TryGetValue(PdfQa.FontKey(family), out string where) ? where : "none";
        }

        public static string Advice(string family, string installed)
        {
            switch (installed)
            {
                case "user":
                    return "'" + family + "' is installed for this Windows user only (per-user fonts folder). Revit's PDF export can miss per-user fonts: " +
                           "install it for all users (right-click the font file > Install for all users, needs administrator rights) and export again.";
                case "none":
                    return "'" + family + "' is not installed on this machine: install it for all users and export again, or change the text types to a font that is.";
                case "machine":
                case "both":
                    return "'" + family + "' is installed for all users: the substitution comes from elsewhere (the font name in the text or family type, or a missing bold/italic style).";
                default:
                    return null;
            }
        }
    }
}
