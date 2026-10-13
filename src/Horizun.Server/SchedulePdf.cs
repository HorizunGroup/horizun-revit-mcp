// -----------------------------------------------------------------------------
// Horizun MCP server - original Horizun code.
//
// THE PRIMITIVES OF A VECTOR PDF, for the L10 schedule reader. PdfPig (Apache-2.0,
// see THIRD-PARTY-NOTICES.md) opens the file; this turns each page into what
// ScheduleCards reads - stroked line segments with their colour and positioned
// texts with their height and direction - in DRAWING MILLIMETRES with y up, as the
// page is displayed. MEASURED (PdfPig 0.1.13, the server tests' /Rotate 90 page):
// PdfPig already reports letters and paths in the DISPLAYED frame - the same page
// drawn upright and drawn turned under /Rotate 90 gives identical coordinates - so
// no rotation is applied here; applying it again turned every title on its side.
//
// What it does NOT do, by design: no OCR (a page with no text layer is refused by
// the reader, by name), no curve fitting (Bezier pieces - axis bubbles, arcs - are
// counted and left out), no fill recognition (filled paths are counted and left
// out). A count of what was left out comes back with every page.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Horizun.Revit.Core;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Graphics;
using UglyToad.PdfPig.Graphics.Colors;

namespace Horizun.Server
{
    internal static class SchedulePdf
    {
        /// <summary>Millimetres per PDF point.</summary>
        public const double MmPerPoint = 25.4 / 72.0;

        internal sealed class Result
        {
            public int PageCount;
            public List<ScheduleCards.Sheet> Sheets = new List<ScheduleCards.Sheet>();
        }

        /// <summary>The pages asked for (1-based), or every page when pages is null.</summary>
        internal static Result Read(byte[] bytes, string source, IList<int> pages, int maxPages, CancellationToken ct)
        {
            var result = new Result();
            using (PdfDocument doc = PdfDocument.Open(bytes, new ParsingOptions { UseLenientParsing = true, SkipMissingFonts = true }))
            {
                result.PageCount = doc.NumberOfPages;
                IList<int> wanted = pages ?? Enumerable.Range(1, doc.NumberOfPages).ToList();
                foreach (int n in wanted)
                    if (n < 1 || n > doc.NumberOfPages)
                        throw new ToolRefusal("pages names page " + n + ", and the file has " + doc.NumberOfPages + ". Nothing was read.",
                            new Newtonsoft.Json.Linq.JObject { ["code"] = "page_out_of_range" });
                if (wanted.Count > maxPages)
                    throw new ToolRefusal("the file has " + wanted.Count + " pages to read and one call reads at most " + maxPages +
                                          "; name them with pages. Nothing was read.",
                        new Newtonsoft.Json.Linq.JObject { ["code"] = "too_many_pages" });
                foreach (int n in wanted)
                {
                    ct.ThrowIfCancellationRequested();
                    result.Sheets.Add(ReadPage(doc.GetPage(n), source, n));
                }
            }
            return result;
        }

        private static ScheduleCards.Sheet ReadPage(Page page, string source, int number)
        {
            var sheet = new ScheduleCards.Sheet { Source = source, Page = number, TextExtent = "box" };
            var raw = new List<ScheduleCards.Seg>();

            foreach (PdfPath path in page.Paths)
            {
                if (!path.IsStroked) { Count(sheet, path.IsFilled ? "filled_path" : "unpainted_path"); continue; }
                string color = Hex(path.StrokeColor);
                foreach (PdfSubpath sub in path)
                {
                    PdfPoint? start = null, cur = null;
                    foreach (PdfSubpath.IPathCommand cmd in sub.Commands)
                    {
                        if (cmd is PdfSubpath.Move mv) { cur = mv.Location; start = mv.Location; }
                        else if (cmd is PdfSubpath.Line ln)
                        {
                            raw.Add(Segment(ln.From, ln.To, color));
                            cur = ln.To;
                            if (start == null) start = ln.From;
                        }
                        else if (cmd is PdfSubpath.BezierCurve bz) { Count(sheet, "curve"); cur = bz.EndPoint; if (start == null) start = bz.StartPoint; }
                        else if (cmd is PdfSubpath.Close)
                        {
                            if (cur.HasValue && start.HasValue && (cur.Value.X != start.Value.X || cur.Value.Y != start.Value.Y))
                                raw.Add(Segment(cur.Value, start.Value, color));
                            cur = start;
                        }
                    }
                }
            }
            sheet.Segments = raw;   // ScheduleCards.Finish (through FromExtracted) dedupes and numbers them

            foreach (Span s in Spans(page.Letters))
            {
                sheet.Texts.Add(new ScheduleCards.Span
                {
                    Text = s.Text, X = s.StartX * MmPerPoint, Y = s.StartY * MmPerPoint, Dx = s.Dx, Dy = s.Dy,
                    Height = s.Size * MmPerPoint, Width = s.Length * MmPerPoint
                });
            }
            return ScheduleCards.FromExtracted(sheet);
        }

        private static ScheduleCards.Seg Segment(PdfPoint a, PdfPoint b, string color) =>
            new ScheduleCards.Seg { X0 = a.X * MmPerPoint, Y0 = a.Y * MmPerPoint, X1 = b.X * MmPerPoint, Y1 = b.Y * MmPerPoint, Color = color };

        private static void Count(ScheduleCards.Sheet s, string k) => s.IgnoredByKind[k] = s.IgnoredByKind.TryGetValue(k, out int n) ? n + 1 : 1;

        private static string Hex(IColor c)
        {
            if (c == null) return null;
            try
            {
                (double r, double g, double b) = c.ToRGBValues();
                return "#" + Byte(r) + Byte(g) + Byte(b);
            }
            catch (Exception) { return null; }
        }

        private static string Byte(double v) => ((int)Math.Round(Math.Max(0, Math.Min(1, v)) * 255)).ToString("x2");

        private sealed class Span
        {
            public string Text;
            public double StartX, StartY, EndX, EndY, Dx, Dy, Size;
            public double Length => Math.Sqrt((EndX - StartX) * (EndX - StartX) + (EndY - StartY) * (EndY - StartY));
        }

        /// <summary>
        /// Letters joined into texts in the order the page draws them: same size and
        /// direction, each one starting where the last one ended (a space's worth of gap
        /// at most, about 0.8 of the size, and on the same baseline).
        /// </summary>
        private static IEnumerable<Span> Spans(IReadOnlyList<Letter> letters)
        {
            Span cur = null;
            foreach (Letter l in letters)
            {
                if (string.IsNullOrEmpty(l.Value)) continue;
                double sx = l.StartBaseLine.X, sy = l.StartBaseLine.Y, ex = l.EndBaseLine.X, ey = l.EndBaseLine.Y;
                double dx = ex - sx, dy = ey - sy, n = Math.Sqrt(dx * dx + dy * dy);
                if (n < 1e-9)
                {
                    if (cur != null) { dx = cur.Dx; dy = cur.Dy; }
                    else { dx = 1; dy = 0; }
                }
                else { dx /= n; dy /= n; }
                double size = l.PointSize > 0 ? l.PointSize : l.FontSize;
                bool joins = cur != null && Math.Abs(size - cur.Size) <= 0.05 * cur.Size && Math.Abs(dx * cur.Dy - dy * cur.Dx) <= 0.02 &&
                             dx * cur.Dx + dy * cur.Dy > 0;
                if (joins)
                {
                    double gx = sx - cur.EndX, gy = sy - cur.EndY;
                    double along = gx * cur.Dx + gy * cur.Dy, across = Math.Abs(-gx * cur.Dy + gy * cur.Dx);
                    joins = along >= -0.3 * size && along <= 0.8 * size && across <= 0.2 * size;
                }
                if (joins)
                {
                    // A gap of a space's width with no space character in it is a word break the
                    // drawing left out (measured on real sheets: two zone callouts written side by
                    // side, "6.#3@.06" "2.#3@.25", reached PdfPig as one run of letters with a gap
                    // between them). Within a word the letters abut (|gap| under 0.05 of the size).
                    double gap = (sx - cur.EndX) * cur.Dx + (sy - cur.EndY) * cur.Dy;
                    if (gap > WordGap * size && l.Value != " " && !cur.Text.EndsWith(" ", StringComparison.Ordinal)) cur.Text += " ";
                    cur.Text += l.Value;
                    if (n >= 1e-9) { cur.EndX = ex; cur.EndY = ey; }
                    continue;
                }
                if (cur != null && cur.Text.Trim().Length > 0) yield return Trimmed(cur);
                cur = new Span { Text = l.Value, StartX = sx, StartY = sy, EndX = n >= 1e-9 ? ex : sx, EndY = n >= 1e-9 ? ey : sy, Dx = dx, Dy = dy, Size = size };
            }
            if (cur != null && cur.Text.Trim().Length > 0) yield return Trimmed(cur);
        }

        /// <summary>A gap between two letters wider than this share of the size, with no space drawn, is a space.</summary>
        public const double WordGap = 0.15;

        /// <summary>A text keeps its drawn start; spaces at its ends are dropped from the string only.</summary>
        private static Span Trimmed(Span s)
        {
            s.Text = s.Text.Trim();
            return s;
        }
    }
}
