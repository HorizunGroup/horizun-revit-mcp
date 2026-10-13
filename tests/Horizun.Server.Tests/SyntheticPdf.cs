// -----------------------------------------------------------------------------
// Horizun server tests - original Horizun code.
//
// A TINY VECTOR PDF WRITER for the L10 schedule fixtures. It draws the primitives
// of a SyntheticSheet (drawing millimetres, y up) as a PDF page: stroked lines in
// their RGB colour, texts in Helvetica at any angle (a text matrix, so rotated
// callouts are real rotated text), optionally on a page with /Rotate 90 whose
// user space is turned the other way - so the reader has to apply the rotation to
// get back the sheet that was drawn. Written by hand rather than with PdfPig's
// builder because the builder has no rotated text and no page rotation.
// Everything it draws is synthetic.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;

namespace Horizun.Server.Tests
{
    internal static class SyntheticPdf
    {
        private const double PtPerMm = 72.0 / 25.4;

        /// <summary>One page per sheet. rotate is 0 or 90 (the page is displayed turned clockwise).</summary>
        public static byte[] Write(IList<JObject> sheets, int rotate = 0, double widthMm = 420, double heightMm = 297, bool withText = true)
        {
            var objects = new List<string>();
            int pagesObj = 2, fontObj = 3;
            var kids = new List<int>();
            objects.Add("<< /Type /Catalog /Pages 2 0 R >>");
            objects.Add(null);   // pages, filled in below
            objects.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
            double vw = widthMm * PtPerMm, vh = heightMm * PtPerMm;   // the DISPLAYED page, in points
            foreach (JObject sheet in sheets)
            {
                string content = Content(sheet, rotate, vw, vh, withText);
                objects.Add("<< /Length " + Latin1(content).Length + " >>\nstream\n" + content + "\nendstream");
                int contentObj = objects.Count;
                string box = rotate == 90 ? "[0 0 " + N(vh) + " " + N(vw) + "]" : "[0 0 " + N(vw) + " " + N(vh) + "]";
                objects.Add("<< /Type /Page /Parent " + pagesObj + " 0 R /MediaBox " + box + (rotate != 0 ? " /Rotate " + rotate : "") +
                            " /Resources << /Font << /F1 " + fontObj + " 0 R >> >> /Contents " + contentObj + " 0 R >>");
                kids.Add(objects.Count);
            }
            objects[pagesObj - 1] = "<< /Type /Pages /Kids [" + string.Join(" ", kids.Select(k => k + " 0 R")) + "] /Count " + kids.Count + " >>";

            var ms = new MemoryStream();
            Action<string> put = s => { byte[] b = Latin1(s); ms.Write(b, 0, b.Length); };
            put("%PDF-1.4\n");
            var offsets = new List<long>();
            for (int i = 0; i < objects.Count; i++)
            {
                offsets.Add(ms.Position);
                put((i + 1) + " 0 obj\n" + objects[i] + "\nendobj\n");
            }
            long xref = ms.Position;
            var sb = new StringBuilder();
            sb.Append("xref\n0 ").Append(objects.Count + 1).Append("\n0000000000 65535 f \n");
            foreach (long o in offsets) sb.Append(o.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
            sb.Append("trailer\n<< /Size ").Append(objects.Count + 1).Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
            put(sb.ToString());
            return ms.ToArray();
        }

        private static string Content(JObject sheet, int rotate, double vw, double vh, bool withText)
        {
            var sb = new StringBuilder();
            sb.Append("0.25 w\n");
            Func<double, double, string> P = (xmm, ymm) =>
            {
                double x = xmm * PtPerMm, y = ymm * PtPerMm;
                // /Rotate 90 displays user space turned clockwise: displayed (x, y) is user (vh - y, x).
                return rotate == 90 ? N(vh - y) + " " + N(x) : N(x) + " " + N(y);
            };
            foreach (JObject s in sheet["segments"])
                sb.Append(Rgb((string)s["color"])).Append(" RG ").Append(P((double)s["a"][0], (double)s["a"][1])).Append(" m ")
                  .Append(P((double)s["b"][0], (double)s["b"][1])).Append(" l S\n");
            foreach (JObject pl in sheet["polylines"])
            {
                JArray pts = (JArray)pl["points"];
                sb.Append(Rgb((string)pl["color"])).Append(" RG ").Append(P((double)pts[0][0], (double)pts[0][1])).Append(" m");
                for (int i = 1; i < pts.Count; i++) sb.Append(' ').Append(P((double)pts[i][0], (double)pts[i][1])).Append(" l");
                sb.Append(" S\n");
            }
            if (!withText) return sb.ToString();
            foreach (JObject t in sheet["texts"])
            {
                double dx = (double)t["direction"][0], dy = (double)t["direction"][1];
                if (rotate == 90) { double ux = -dy, uy = dx; dx = ux; dy = uy; }   // a displayed direction in user space
                double size = (double)t["height"] * PtPerMm;
                sb.Append("0 0 0 rg BT /F1 1 Tf ")
                  .Append(N(dx * size)).Append(' ').Append(N(dy * size)).Append(' ').Append(N(-dy * size)).Append(' ').Append(N(dx * size)).Append(' ')
                  .Append(P((double)t["at"][0], (double)t["at"][1])).Append(" Tm ");
                // "tj": one text-showing operation with positioning inside it - strings and the
                // kerning numbers between them (thousandths of the size; negative moves right).
                if (t["tj"] is JArray tj)
                    sb.Append('[').Append(string.Join(" ", tj.Select(x => x.Type == JTokenType.String ? "(" + Escape((string)x) + ")" : N((double)x))))
                      .Append("] TJ ET\n");
                else
                {
                    // THE TEXT IS DRAWN AS WIDE AS THE SHEET SAYS IT IS. A sheet's text carries its
                    // advance ("width"); Helvetica's own is another, and the reader takes a label's
                    // centre - an axis position - from the advance it sees. Horizontal scaling (Tz)
                    // makes the drawn advance the declared one, so the PDF and the primitives describe
                    // the same drawing to the hundredth of a millimetre.
                    string text = (string)t["text"];
                    double em = text.Sum(ch => HelveticaWidth(ch)) / 1000.0;
                    if (t["width"] != null && t["width"].Type != JTokenType.Null && em > 0)
                        sb.Append(N(100.0 * (double)t["width"] / ((double)t["height"] * em))).Append(" Tz ");
                    sb.Append('(').Append(Escape(text)).Append(") Tj ET\n");
                }
            }
            return sb.ToString();
        }

        /// <summary>Helvetica's advance widths (AFM, thousandths of an em), WinAnsi printable ASCII and the diameter sign.</summary>
        private static double HelveticaWidth(char c)
        {
            const string ascii = " !\"#$%&'()*+,-./0123456789:;<=>?@ABCDEFGHIJKLMNOPQRSTUVWXYZ[\\]^_`abcdefghijklmnopqrstuvwxyz{|}~";
            int[] w =
            {
                278, 278, 355, 556, 556, 889, 667, 191, 333, 333, 389, 584, 278, 333, 278, 278,
                556, 556, 556, 556, 556, 556, 556, 556, 556, 556, 278, 278, 584, 584, 584, 556, 1015,
                667, 667, 722, 722, 667, 611, 778, 722, 278, 500, 667, 556, 833, 722, 778, 667, 778, 722, 667, 611, 722, 667, 944, 667, 667, 611,
                278, 278, 278, 469, 556, 333,
                556, 556, 500, 556, 556, 278, 556, 556, 222, 222, 500, 222, 833, 556, 556, 556, 556, 333, 500, 278, 556, 500, 722, 500, 500, 500,
                334, 260, 334, 584
            };
            if (c == '\u00d8') return 778;
            int i = ascii.IndexOf(c);
            if (i < 0) throw new ArgumentException("no Helvetica width for '" + c + "' in the synthetic writer");
            return w[i];
        }

        private static string Rgb(string hex)
        {
            hex = (hex ?? "#000000").TrimStart('#');
            Func<int, string> c = i => N(int.Parse(hex.Substring(i, 2), NumberStyles.HexNumber) / 255.0);
            return c(0) + " " + c(2) + " " + c(4);
        }

        private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)");
        private static string N(double v) => v.ToString("0.########", CultureInfo.InvariantCulture);
        private static byte[] Latin1(string s) => Encoding.GetEncoding("ISO-8859-1").GetBytes(s);
    }
}
