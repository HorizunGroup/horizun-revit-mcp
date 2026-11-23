// -----------------------------------------------------------------------------
// Horizun Core - original Horizun code. No Revit.
//
// THE QA OF A PRODUCED PDF (4.2-N4: CT-P1-4d, CT-S0-9, LP-P3-13c).
//
// A sheet set went out with Arial Narrow quietly replaced by ArialMT, with notes printed over a
// dimension and with a page that was white. The exporter reported success each time, because
// producing a file is not the same as the file being right. This reads the file back with
// PdfPig and answers four questions from what is in it:
//
//   fonts            which fonts the text really uses, per page; against expected_fonts (families)
//                    a missing family with a different one in its place is SUBSTITUTED.
//   blank pages      no text, no image, no drawn path.
//   text overlaps    two words whose rectangles cross by more than a hair (the "choque de texto").
//   text outside     a word beyond the page, or inside the frame margin asked for.
//
// 5.2 (P1, PdfQaPaper): ink per viewport against its reported box, printed gray in a box (a coarse
// software raster of the vector drawing), printed lines per note, a before/after comparison and,
// for a missing font installed for one Windows user only, the advice to install it for all users.
//
// WHAT THIS IS NOT. It does not render a picture. A rasterised comparison (a white square where a view
// should be, a hatch that did not print) needs a renderer this layer does not carry: the report
// says render_checked=false so nobody reads a clean text report as a clean picture. Embedding is
// not judged either: PdfPig names the fonts the content stream selects, not whether the program
// is embedded.
//
// Read() is the only part that touches PdfPig; Judge() is arithmetic over PageFacts.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using UglyToad.PdfPig;

namespace Horizun.Revit.Core
{
    public sealed class PdfWordFact
    {
        public string Text;
        public double Left, Bottom, Right, Top;
        /// <summary>Font name and point size of the word's first letter (null / 0 when unknown).</summary>
        public string Font;
        public double Size;
    }

    public sealed class PdfPageFacts
    {
        public int Page;
        public double Width, Height;
        /// <summary>Lower-left corner of the page box (media box origin), points; ink coordinates are in the same frame.</summary>
        public double Left, Bottom;
        public List<PdfWordFact> Words = new List<PdfWordFact>();
        /// <summary>Font name -> letters set in it on this page.</summary>
        public Dictionary<string, int> Fonts = new Dictionary<string, int>(StringComparer.Ordinal);
        public int Images, Paths;
        /// <summary>Path segments (lines and curves) of the painted paths: the "strokes" a before/after comparison counts.</summary>
        public int Segments;
        /// <summary>Every drawn thing (word, painted path, image) as [left, bottom, right, top] in points: the page's INK.</summary>
        public List<double[]> Ink = new List<double[]>();
        /// <summary>Text, path and image boxes for viewport attribution, with multi-part strokes separated.</summary>
        public List<PdfMark> Marks = new List<PdfMark>();
        public bool MarksTruncated;
        internal bool TextOrImageMarksOmitted;
        /// <summary>The painted paths, flattened, in painting order; null unless the read was asked to capture them.</summary>
        public List<PdfDrawOp> Drawing;
        public bool DrawingTruncated;
    }

    public sealed class PdfMark
    {
        public string Kind;
        public double[] Rect;
    }

    public sealed class PdfRegion
    {
        public string Name;
        /// <summary>[x0, y0, x1, y1] in points from the page's lower-left corner; null when the region is a viewport's or an element's.</summary>
        public double[] Rect;
        /// <summary>Only this page (1-based); null: every page.</summary>
        public int? Page;
        /// <summary>The viewport whose box is the region (or through which ElementId is seen).</summary>
        public long? ViewportId, ElementId;
        /// <summary>The element's box on the sheet, sheet mm, filled by the add-in.</summary>
        public double[] SheetRectMm;
        /// <summary>Why the add-in could not resolve the region.</summary>
        public string Unresolved;
    }

    public sealed class PdfQaOptions
    {
        /// <summary>Font FAMILIES the set must be typeset in ("Arial Narrow"). Empty: fonts are reported, not judged.</summary>
        public List<string> ExpectedFonts = new List<string>();
        /// <summary>Text must stay this far inside the page edge, in points. 0: only "beyond the page" counts.</summary>
        public double FrameMarginPoints;
        /// <summary>Overlaps smaller than this on either axis are touching, not colliding (points).</summary>
        public double OverlapMinPoints = 0.75;
        /// <summary>...and smaller than this fraction of the shorter word's height.</summary>
        public double OverlapMinFraction = 0.30;
        public int MaxFindings = 200;
        /// <summary>Named windows of the page (a viewport's box on paper): the ink inside each is measured (F93).</summary>
        public List<PdfRegion> Regions = new List<PdfRegion>();
        /// <summary>5.2: measure each viewport's printed ink (needs SheetMaps).</summary>
        public bool Viewports;
        /// <summary>Which sheet each page prints, filled by the add-in from the exported sheets.</summary>
        public List<PdfSheetMap> SheetMaps = new List<PdfSheetMap>();
        public List<PdfNoteRequest> Notes = new List<PdfNoteRequest>();
        /// <summary>A previous PDF of the same set: the report compares this one against it.</summary>
        public string CompareTo;
        /// <summary>Family key -> user | machine | both, read from the Windows font registry by the add-in; null: unknown.</summary>
        public Dictionary<string, string> FontInventory;
        public bool NeedsDrawing => Regions.Count > 0 || Viewports;
        public const double PointsPerMm = 72.0 / 25.4;
    }

    public static class PdfQa
    {
        public const int MaxWordsPerPage = 6000;
        internal const int MaxMarksPerPage = 20000;

        /// <summary>Family key: subset tag, style suffix, spaces and the foundry "MT" removed, lower case. "ABCDEF+Arial-BoldMT" -> "arial".</summary>
        public static string FontKey(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "";
            string s = Regex.Replace(raw.Trim(), "^[A-Z]{6}\\+", "");
            s = Regex.Replace(s, "[-,](bold|italic|oblique|regular)(italic|oblique)?(mt)?$", "", RegexOptions.IgnoreCase);
            s = s.Replace(" ", "").Replace("_", "").Replace("-", "").ToLowerInvariant();
            if (s.EndsWith("mt", StringComparison.Ordinal) && s.Length > 4) s = s.Substring(0, s.Length - 2);
            foreach (string style in new[] { "bold", "italic", "oblique", "regular" })
                if (s.EndsWith(style, StringComparison.Ordinal) && s.Length > style.Length + 2) s = s.Substring(0, s.Length - style.Length);
            return s;
        }

        private static readonly string[] Fields = { "expected_fonts", "frame_margin_mm", "regions", "viewports", "notes", "compare_to" };

        private static bool IsNumber(JToken t) => t != null && (t.Type == JTokenType.Integer || t.Type == JTokenType.Float);
        private static bool IsId(JToken t) => t != null && t.Type == JTokenType.Integer && t.Value<long>() > 0;

        public static PdfQaOptions Parse(JToken token, out string error)
        {
            error = null;
            var o = new PdfQaOptions();
            if (token == null || token.Type == JTokenType.Null) return o;
            if (!(token is JObject j)) { error = "pdf_qa must be an object { " + string.Join(", ", Fields) + " }."; return null; }
            foreach (JProperty p in j.Properties())
                if (Array.IndexOf(Fields, p.Name) < 0) { error = "pdf_qa field '" + p.Name + "' is not applicable (" + string.Join(", ", Fields) + ")."; return null; }
            if (j["regions"] != null)
            {
                if (!(j["regions"] is JArray ra) || ra.Count == 0 || ra.Count > 50)
                { error = "pdf_qa.regions must be 1..50 of { name, rect_mm: [x0, y0, x1, y1] (page mm from the lower-left corner) | viewport_id [, element_id], page }."; return null; }
                var regionNames = new HashSet<string>(StringComparer.Ordinal);
                foreach (JToken t in ra)
                {
                    var rr = t as JObject;
                    if (rr == null || rr["name"]?.Type != JTokenType.String)
                    { error = "every pdf_qa.regions item needs name, and rect_mm [x0, y0, x1, y1] or viewport_id."; return null; }
                    if (!regionNames.Add(rr.Value<string>("name")))
                    { error = "pdf_qa.regions name '" + rr.Value<string>("name") + "' is used twice; every region needs its own name (the reply and its reasons are keyed by it)."; return null; }
                    foreach (JProperty p in rr.Properties())
                        if (p.Name != "name" && p.Name != "rect_mm" && p.Name != "page" && p.Name != "viewport_id" && p.Name != "element_id")
                        { error = "pdf_qa.regions field '" + p.Name + "' is not applicable (name, rect_mm, page, viewport_id, element_id)."; return null; }
                    var region = new PdfRegion { Name = rr.Value<string>("name") };
                    if (rr["page"] != null) { if (!IsId(rr["page"])) { error = "pdf_qa.regions page must be a page number from 1."; return null; } region.Page = rr.Value<int>("page"); }
                    if (rr["viewport_id"] != null) { if (!IsId(rr["viewport_id"])) { error = "pdf_qa.regions viewport_id must be a viewport id."; return null; } region.ViewportId = rr.Value<long>("viewport_id"); }
                    if (rr["element_id"] != null) { if (!IsId(rr["element_id"])) { error = "pdf_qa.regions element_id must be an element id."; return null; } region.ElementId = rr.Value<long>("element_id"); }
                    bool byRect = rr["rect_mm"] != null;
                    if (byRect == region.ViewportId.HasValue)
                    { error = "every pdf_qa.regions item takes exactly one of rect_mm or viewport_id (with element_id: that element as seen through that viewport)."; return null; }
                    if (region.ElementId.HasValue && !region.ViewportId.HasValue) { error = "pdf_qa.regions element_id needs the viewport_id it is seen through."; return null; }
                    if (byRect)
                    {
                        var rect = rr["rect_mm"] as JArray;
                        if (rect == null || rect.Count != 4 || rect.Any(v => !IsNumber(v))) { error = "pdf_qa.regions rect_mm must be [x0, y0, x1, y1] in page mm."; return null; }
                        double[] mm = rect.Select(v => v.Value<double>()).ToArray();
                        if (!(mm[2] > mm[0]) || !(mm[3] > mm[1])) { error = "pdf_qa.regions rect_mm must have x1 > x0 and y1 > y0."; return null; }
                        region.Rect = mm.Select(v => v * PdfQaOptions.PointsPerMm).ToArray();
                    }
                    o.Regions.Add(region);
                }
            }
            if (j["viewports"] != null)
            {
                if (j["viewports"].Type != JTokenType.Boolean) { error = "pdf_qa.viewports must be a boolean."; return null; }
                o.Viewports = j.Value<bool>("viewports");
            }
            if (j["notes"] != null)
            {
                if (!(j["notes"] is JArray na) || na.Count == 0 || na.Count > 50)
                { error = "pdf_qa.notes must be 1..50 of { text | note_id, page, expected_lines }."; return null; }
                foreach (JToken t in na)
                {
                    var no = t as JObject;
                    if (no == null) { error = "every pdf_qa.notes item must be an object."; return null; }
                    foreach (JProperty p in no.Properties())
                        if (p.Name != "text" && p.Name != "note_id" && p.Name != "page" && p.Name != "expected_lines")
                        { error = "pdf_qa.notes field '" + p.Name + "' is not applicable (text, note_id, page, expected_lines)."; return null; }
                    bool hasText = no["text"] != null, hasId = no["note_id"] != null;
                    if (hasText == hasId) { error = "every pdf_qa.notes item takes exactly one of text or note_id."; return null; }
                    var note = new PdfNoteRequest();
                    if (hasText)
                    {
                        if (no["text"].Type != JTokenType.String || PdfQaPaper.Normalize(no.Value<string>("text")).Length == 0) { error = "pdf_qa.notes text must hold letters or digits."; return null; }
                        note.Text = no.Value<string>("text");
                    }
                    else { if (!IsId(no["note_id"])) { error = "pdf_qa.notes note_id must be a text note id."; return null; } note.NoteId = no.Value<long>("note_id"); }
                    if (no["page"] != null) { if (!IsId(no["page"])) { error = "pdf_qa.notes page must be a page number from 1."; return null; } note.Page = no.Value<int>("page"); }
                    if (no["expected_lines"] != null)
                    {
                        if (no["expected_lines"].Type != JTokenType.Integer || no.Value<int>("expected_lines") < 1 || no.Value<int>("expected_lines") > 100)
                        { error = "pdf_qa.notes expected_lines must be an integer 1..100."; return null; }
                        note.ExpectedLines = no.Value<int>("expected_lines");
                    }
                    o.Notes.Add(note);
                }
            }
            if (j["compare_to"] != null)
            {
                string c = j["compare_to"].Type == JTokenType.String ? j.Value<string>("compare_to") : null;
                if (string.IsNullOrWhiteSpace(c) || !System.IO.Path.IsPathRooted(c) || !c.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                { error = "pdf_qa.compare_to must be the absolute path of the earlier .pdf."; return null; }
                o.CompareTo = c;
            }
            if (j["expected_fonts"] != null)
            {
                if (!(j["expected_fonts"] is JArray a) || a.Count > 20 || a.Any(t => t.Type != JTokenType.String || string.IsNullOrWhiteSpace(t.Value<string>())))
                { error = "pdf_qa.expected_fonts must be a list of up to 20 font family names."; return null; }
                o.ExpectedFonts = a.Select(t => t.Value<string>().Trim()).ToList();
            }
            if (j["frame_margin_mm"] != null)
            {
                if (!IsNumber(j["frame_margin_mm"]) || j.Value<double>("frame_margin_mm") < 0 || j.Value<double>("frame_margin_mm") > 100)
                { error = "pdf_qa.frame_margin_mm must be a number 0..100."; return null; }
                o.FrameMarginPoints = j.Value<double>("frame_margin_mm") * PdfQaOptions.PointsPerMm;
            }
            return o;
        }

        // ---- reading ----------------------------------------------------------------------

        public static List<PdfPageFacts> Read(string path) => Read(path, false);

        /// <summary>The page facts; with captureDrawing the painted paths are kept, flattened, for the gray raster.</summary>
        public static List<PdfPageFacts> Read(string path, bool captureDrawing)
        {
            var pages = new List<PdfPageFacts>();
            using (PdfDocument pdf = PdfDocument.Open(path))
            {
                foreach (var page in pdf.GetPages())
                {
                    var f = new PdfPageFacts { Page = page.Number, Width = page.Width, Height = page.Height, Left = page.MediaBox.Bounds.Left, Bottom = page.MediaBox.Bounds.Bottom };
                    if (captureDrawing) f.Drawing = new List<PdfDrawOp>();
                    foreach (var letter in page.Letters)
                    {
                        string name = letter.FontName ?? "(unnamed)";
                        f.Fonts[name] = f.Fonts.TryGetValue(name, out int n) ? n + 1 : 1;
                    }
                    try
                    {
                        foreach (var drawn in page.Paths)
                        {
                            // A clipping path is a window, not ink: counting it put the viewport's crop in the ink box.
                            if (drawn.IsClipping || (!drawn.IsFilled && !drawn.IsStroked)) continue;
                            f.Paths++;
                            PdfDrawOp op = Flatten(drawn, out int segments);
                            f.Segments += segments;
                            if (op.Box == null) continue;
                            // A white fill with no stroke masks what is under it; it is not ink.
                            bool visible = op.Stroke || op.FillDark > 0.02;
                            if (visible) AddPathInk(f, op);
                            if (f.Drawing != null)
                            {
                                if (f.Drawing.Count < 100000) f.Drawing.Add(op);
                                else f.DrawingTruncated = true;
                            }
                        }
                    }
                    catch { }
                    try
                    {
                        foreach (var image in page.GetImages())
                        {
                            f.Images++;
                            var b = image.Bounds;
                            Add(f, "image", new[] { b.Left, b.Bottom, b.Right, b.Top });
                        }
                    }
                    catch { }
                    int taken = 0;
                    foreach (var w in page.GetWords())
                    {
                        if (taken++ >= MaxWordsPerPage) break;
                        var b = w.BoundingBox;
                        var first = w.Letters.Count > 0 ? w.Letters[0] : null;
                        f.Words.Add(new PdfWordFact { Text = w.Text, Left = b.Left, Bottom = b.Bottom, Right = b.Right, Top = b.Top,
                            Font = first?.FontName, Size = first?.PointSize ?? 0 });
                        Add(f, "text", new[] { b.Left, b.Bottom, b.Right, b.Top });
                    }
                    pages.Add(f);
                }
            }
            return pages;
        }

        private static void AddPathInk(PdfPageFacts page, PdfDrawOp op)
        {
            if (page.Ink.Count < MaxMarksPerPage) page.Ink.Add(op.Box);
            if (!op.Stroke || op.Rings.Count < 2)
            {
                AddMark(page, "path", op.Box);
                return;
            }

            // A fill-and-stroke operator can also paint disjoint subpaths. Keep the
            // compound path intact for gray measurement and assign its boxes separately.
            foreach (var ring in op.Rings)
            {
                if (!HasMarkSpace(page)) break;
                double[] box = { ring.Min(p => p[0]), ring.Min(p => p[1]), ring.Max(p => p[0]), ring.Max(p => p[1]) };
                page.Marks.Add(new PdfMark { Kind = "path", Rect = box });
            }
        }

        private static void Add(PdfPageFacts f, string kind, double[] rect)
        {
            f.Ink.Add(rect);
            if (HasMarkSpace(f)) f.Marks.Add(new PdfMark { Kind = kind, Rect = rect });
            else f.TextOrImageMarksOmitted = true;
        }

        private static void AddMark(PdfPageFacts page, string kind, double[] rect)
        {
            if (HasMarkSpace(page)) page.Marks.Add(new PdfMark { Kind = kind, Rect = rect });
        }

        private static bool HasMarkSpace(PdfPageFacts page)
        {
            if (page.Marks.Count < MaxMarksPerPage) return true;
            page.MarksTruncated = true;
            return false;
        }

        private static double Dark(UglyToad.PdfPig.Graphics.Colors.IColor c)
        {
            if (c == null) return 1;   // the PDF default colour is black
            try { var rgb = c.ToRGBValues(); return PdfQaPaper.Darkness(rgb.r, rgb.g, rgb.b); }
            catch { return 1; }
        }

        private static PdfDrawOp Flatten(UglyToad.PdfPig.Graphics.PdfPath drawn, out int segments)
        {
            segments = 0;
            var op = new PdfDrawOp
            {
                Fill = drawn.IsFilled, Stroke = drawn.IsStroked,
                EvenOdd = drawn.FillingRule == UglyToad.PdfPig.Core.FillingRule.EvenOdd,
                FillDark = Dark(drawn.FillColor), StrokeDark = Dark(drawn.StrokeColor),
                Width = Convert.ToDouble(drawn.LineWidth, CultureInfo.InvariantCulture)
            };
            double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
            foreach (UglyToad.PdfPig.Core.PdfSubpath sub in drawn)
            {
                List<double[]> ring = null; bool closed = false;
                Action<double, double> point = (x, y) =>
                {
                    if (ring == null) ring = new List<double[]>();
                    ring.Add(new[] { x, y });
                    x0 = Math.Min(x0, x); y0 = Math.Min(y0, y); x1 = Math.Max(x1, x); y1 = Math.Max(y1, y);
                };
                Action flush = () =>
                {
                    if (ring != null && ring.Count >= 2) { op.Rings.Add(ring); op.Closed.Add(closed); }
                    ring = null; closed = false;
                };
                foreach (var cmd in sub.Commands)
                {
                    if (cmd is UglyToad.PdfPig.Core.PdfSubpath.Move mv) { flush(); point(mv.Location.X, mv.Location.Y); }
                    else if (cmd is UglyToad.PdfPig.Core.PdfSubpath.Line ln)
                    {
                        if (ring == null) point(ln.From.X, ln.From.Y);
                        point(ln.To.X, ln.To.Y); segments++;
                    }
                    else if (cmd is UglyToad.PdfPig.Core.PdfSubpath.BezierCurve bz)
                    {
                        if (ring == null) point(bz.StartPoint.X, bz.StartPoint.Y);
                        foreach (var part in bz.ToLines(8)) point(part.To.X, part.To.Y);
                        segments++;
                    }
                    else if (cmd is UglyToad.PdfPig.Core.PdfSubpath.Close) { closed = true; segments++; }
                }
                flush();
            }
            if (x0 <= x1) op.Box = new[] { x0, y0, x1, y1 };
            return op;
        }

        // ---- judging ----------------------------------------------------------------------

        public static JObject Judge(IList<PdfPageFacts> pages, PdfQaOptions o)
        {
            o = o ?? new PdfQaOptions();
            var usedByKey = new SortedDictionary<string, JObject>(StringComparer.Ordinal);
            var blank = new JArray();
            var overlaps = new JArray();
            var outside = new JArray();
            int overlapTotal = 0, outsideTotal = 0;
            bool truncated = false;
            foreach (PdfPageFacts p in pages)
            {
                foreach (KeyValuePair<string, int> kv in p.Fonts)
                {
                    string key = FontKey(kv.Key);
                    if (!usedByKey.TryGetValue(key, out JObject row))
                        usedByKey[key] = row = new JObject { ["family_key"] = key, ["names"] = new JArray(), ["letters"] = 0, ["pages"] = new JArray() };
                    if (!((JArray)row["names"]).Any(t => t.Value<string>() == kv.Key)) ((JArray)row["names"]).Add(kv.Key);
                    row["letters"] = row.Value<int>("letters") + kv.Value;
                    if (!((JArray)row["pages"]).Any(t => t.Value<int>() == p.Page)) ((JArray)row["pages"]).Add(p.Page);
                }
                if (p.Words.Count == 0 && p.Images == 0 && p.Paths == 0) blank.Add(p.Page);

                foreach (PdfWordFact w in OutsideFrame(p, o))
                {
                    outsideTotal++;
                    if (outside.Count < o.MaxFindings) outside.Add(new JObject { ["page"] = p.Page, ["text"] = w.Text, ["rect_pt"] = new JArray(R(w.Left), R(w.Bottom), R(w.Right), R(w.Top)) });
                    else truncated = true;
                }
                foreach (Tuple<PdfWordFact, PdfWordFact, double, double> hit in Overlaps(p, o))
                {
                    overlapTotal++;
                    if (overlaps.Count < o.MaxFindings)
                        overlaps.Add(new JObject { ["page"] = p.Page, ["a"] = hit.Item1.Text, ["b"] = hit.Item2.Text, ["overlap_mm"] = new JArray(Mm(hit.Item3), Mm(hit.Item4)) });
                    else truncated = true;
                }
            }

            var expectedKeys = o.ExpectedFonts.Select(FontKey).Distinct().ToList();
            var missing = new JArray();
            var substituted = new JArray();
            var unexpected = new JArray();
            var advices = new JArray();
            bool judged = expectedKeys.Count > 0;
            if (judged)
            {
                var strangers = usedByKey.Values.Where(r => !expectedKeys.Contains(r.Value<string>("family_key"))).ToList();
                foreach (string fam in o.ExpectedFonts)
                {
                    string key = FontKey(fam);
                    if (usedByKey.ContainsKey(key)) continue;
                    missing.Add(fam);
                    string installed = PdfQaPaper.Installed(fam, o.FontInventory);
                    string advice = PdfQaPaper.Advice(fam, installed);
                    if (installed != "unknown") advices.Add(new JObject { ["family"] = fam, ["installed"] = installed, ["advice"] = advice });
                    if (strangers.Count > 0)
                        substituted.Add(new JObject { ["expected"] = fam, ["replaced_by"] = new JArray(strangers.SelectMany(s => s["names"].Select(n => n.Value<string>()))),
                            ["installed"] = installed, ["advice"] = advice });
                }
                foreach (JObject s in strangers) unexpected.Add(new JObject { ["names"] = s["names"].DeepClone(), ["letters"] = s["letters"], ["pages"] = s["pages"].DeepClone() });
            }

            // B1: what was asked and could not be measured is never "clean": it makes the verdict partial, with the reasons.
            var incomplete = new List<string>();
            JObject paper = Paper(pages, o, incomplete, out bool paperFindings);
            JArray ink = Ink(pages, o, incomplete);
            bool clean = !paperFindings && blank.Count == 0 && overlapTotal == 0 && outsideTotal == 0 && (!judged || (substituted.Count == 0 && missing.Count == 0 && unexpected.Count == 0));
            var report = new JObject
            {
                ["verdict"] = !clean ? "findings" : incomplete.Count > 0 ? "partial" : "clean",
                ["incomplete"] = new JArray(incomplete.Distinct().Take(Math.Max(1, o.MaxFindings)).Select(x => (JToken)x)),
                ["pages"] = pages.Count,
                ["fonts"] = new JObject
                {
                    ["used"] = new JArray(usedByKey.Values.Select(v => (JToken)v)),
                    ["judged_against_expected"] = judged,
                    ["expected"] = new JArray(o.ExpectedFonts),
                    ["missing_expected"] = missing,
                    ["substituted"] = substituted,
                    ["unexpected"] = unexpected,
                    ["install_advice"] = advices,
                    ["embedding_judged"] = false
                },
                ["blank_pages"] = blank,
                ["text_overlaps"] = new JObject { ["count"] = overlapTotal, ["items"] = overlaps },
                ["text_outside_frame"] = new JObject { ["count"] = outsideTotal, ["frame_margin_mm"] = Math.Round(o.FrameMarginPoints / PdfQaOptions.PointsPerMm, 2), ["items"] = outside },
                ["ink"] = ink,
                ["findings_truncated"] = truncated,
                ["render_checked"] = false,
                ["render_limit"] = "no picture rasteriser in this process: PdfPig reads, it does not draw. For a picture use horizun_capture_view on the sheet; ink boxes say where the drawing is, and gray (regions, viewports) is a coarse raster of the vector drawing only",
                ["means"] = "read back from the file's text, fonts and drawing operators; a picture-level comparison was not made"
            };
            foreach (JProperty extra in paper.Properties()) report[extra.Name] = extra.Value;
            return report;
        }

        /// <summary>
        /// 5.2 (P1): viewport ink and notes, when asked. paperFindings: a note not found, or on a different number of
        /// lines than expected. incomplete (B1): an unmapped sheet, an approximate mapping, an unverified page order,
        /// a viewport with no ink, a page exceeding the ink mark limit, an ambiguous note - each with its reason.
        /// </summary>
        private static JObject Paper(IList<PdfPageFacts> pages, PdfQaOptions o, List<string> incomplete, out bool paperFindings)
        {
            paperFindings = false;
            var result = new JObject();
            if (o.Viewports)
            {
                var rows = new JArray();
                var unmapped = new JArray();
                var mappings = new JArray();
                if (o.SheetMaps.Count == 0) incomplete.Add("viewports: no sheet is known for any page (format=pdf_qa needs view_ids: the sheets, in page order)");
                foreach (PdfSheetMap map in o.SheetMaps)
                {
                    PdfPageFacts page = pages.FirstOrDefault(p => p.Page == map.Page);
                    if (page == null)
                    {
                        unmapped.Add(new JObject { ["sheet_id"] = map.SheetId, ["page"] = map.Page, ["why"] = "the PDF has no such page" });
                        incomplete.Add("sheet " + map.SheetId + ": the PDF has no page " + map.Page);
                        continue;
                    }
                    JObject mapping = PdfQaPaper.Mapping(page, map, out double[] m, out bool exact);
                    mappings.Add(mapping);
                    if (m == null)
                    {
                        unmapped.Add(new JObject { ["sheet_id"] = map.SheetId, ["page"] = map.Page, ["why"] = mapping["why"] });
                        incomplete.Add("sheet " + map.SheetId + " (page " + map.Page + ") not mapped: " + mapping.Value<string>("why"));
                        continue;
                    }
                    if (!exact) incomplete.Add("page " + map.Page + ": approximate mapping onto sheet " + map.SheetId + " (" + mapping.Value<string>("why") + ")");
                    if (page.MarksTruncated && map.Viewports.Count > 0)
                        incomplete.Add("page " + map.Page + ": viewport ink exceeded the limit of " + MaxMarksPerPage + " measured items");
                    if (mapping.Value<bool>("page_order_unverified"))
                        incomplete.Add("page " + map.Page + ": sheet number " + (map.SheetNumber ?? "(none)") + " not found among its words, page order unverified");
                    foreach (JObject row in PdfQaPaper.ViewportInk(page, map, m, exact).OfType<JObject>())
                    {
                        if (row.Value<bool?>("ink_found") == false)
                            incomplete.Add("viewport " + row.Value<long>("viewport_id") + " (page " + map.Page + "): " + row.Value<string>("why"));
                        if (row["gray"] is JObject g && (g.Value<bool?>("measured") != true || g.Value<bool?>("truncated") == true))
                            incomplete.Add("viewport " + row.Value<long>("viewport_id") + ": gray " + (g.Value<bool?>("measured") != true ? "not measured" : "truncated"));
                        rows.Add(row);
                    }
                }
                result["viewports"] = new JObject
                {
                    ["sheets"] = o.SheetMaps.Count, ["items"] = rows, ["flagged"] = new JArray(rows.OfType<JObject>().Where(r => r["flags"] is JArray f && f.Count > 0).Select(r => new JObject { ["viewport_id"] = r["viewport_id"], ["page"] = r["page"], ["flags"] = r["flags"], ["flags_basis"] = r["flags_basis"], ["why"] = r["why"] })),
                    ["unmapped"] = unmapped, ["pages"] = mappings,
                    ["mapping"] = "sheet outline fitted to the page, centred, one scale; per page: exact only when the scale is 72/25.4 pt per mm and the aspect ratio the sheet's (within 0.5 %), else approximate; sheet mm = the frame of horizun_sheet_layout",
                    ["search_margin_mm"] = PdfQaPaper.ViewportSearchMarginMm,
                    ["means"] = "ink_mm is what was drawn for the viewport (centre within its box + search margin, no larger than it, title excluded); " +
                                "ink_from_box_center_mm goes to horizun_sheet_layout move_viewport anchor=ink; flags (empty, off_box: ink centre 25 mm or more from the box centre, overflows_box: ink 5 mm or more past a side, touches_page_edge: ink within 0.5 mm of the page box edge; off_box and overflows_box are not computed when the page mapping is approximate, flags_basis says so; no_box: Revit reported no box) are leads for a person, not a verdict"
                };
            }
            if (o.Notes.Count > 0)
            {
                var rows = new JArray();
                foreach (PdfNoteRequest note in o.Notes)
                {
                    JObject row = PdfQaPaper.NoteLines(pages, note);
                    if (row.Value<bool?>("ambiguous") == true || row.Value<bool?>("not_measured") == true)
                        incomplete.Add("note " + (note.NoteId.HasValue ? note.NoteId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) : "'" + row.Value<string>("text") + "'") + ": " + row.Value<string>("why"));
                    else if (row.Value<bool?>("found") != true || row.Value<bool?>("matches_expected") == false) paperFindings = true;
                    rows.Add(row);
                }
                result["notes"] = rows;
            }
            return result;
        }

        /// <summary>A region's rectangle on a page (points), or null when it does not apply there; why says what is missing.</summary>
        private static double[] RegionOnPage(PdfRegion rg, PdfPageFacts p, PdfQaOptions o, out string why, out string mappingNote)
        {
            why = null; mappingNote = null;
            if (rg.Page.HasValue && rg.Page.Value != p.Page) return null;
            if (rg.Rect != null) return rg.Rect;
            if (rg.Unresolved != null) { why = rg.Unresolved; return null; }
            PdfSheetMap map = o.SheetMaps.FirstOrDefault(s => s.Page == p.Page && s.Viewports.Any(v => v.ViewportId == rg.ViewportId));
            if (map == null)
            {
                if (!o.SheetMaps.Any(s => s.Viewports.Any(v => v.ViewportId == rg.ViewportId)))
                    why = "viewport " + rg.ViewportId + " is not on a sheet this PDF prints (pass view_ids: the sheets in page order)";
                return null;
            }
            PdfQaPaper.Mapping(p, map, out double[] m, out bool exact);
            if (m == null) { why = "the sheet of page " + p.Page + " could not be mapped onto it: " + (map.OutlineWhy ?? "the sheet's outline could not be read"); return null; }
            mappingNote = exact ? "exact" : "approximate";
            double[] sheetRect = rg.ElementId.HasValue ? rg.SheetRectMm : map.Viewports.First(v => v.ViewportId == rg.ViewportId).BoxMm;
            if (sheetRect == null) { why = rg.ElementId.HasValue ? "the element's box on the sheet could not be read" : "Revit reported no box for viewport " + rg.ViewportId; return null; }
            return PdfQaPaper.ToPage(m, sheetRect);
        }

        /// <summary>F93: the ink box of each page (mm, from the lower-left corner) and, per region, the ink whose centre falls inside it.</summary>
        public static JArray Ink(IList<PdfPageFacts> pages, PdfQaOptions o) => Ink(pages, o, new List<string>());

        /// <summary>The same; a region that could not be measured (or whose gray could not) adds its reason to incomplete (B1).</summary>
        public static JArray Ink(IList<PdfPageFacts> pages, PdfQaOptions o, List<string> incomplete)
        {
            var result = new JArray();
            var measuredOnce = new HashSet<string>(StringComparer.Ordinal);
            var whyNot = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (PdfPageFacts p in pages)
            {
                var row = new JObject { ["page"] = p.Page, ["items"] = p.Ink.Count, ["box_mm"] = BoxMm(Union(p.Ink)) };
                if (o.Regions.Count > 0)
                {
                    var regions = new JArray();
                    foreach (PdfRegion rg in o.Regions)
                    {
                        double[] rect = RegionOnPage(rg, p, o, out string why, out string mappingNote);
                        if (rect == null)
                        {
                            if (why != null) { regions.Add(new JObject { ["name"] = rg.Name, ["measured"] = false, ["why"] = why }); whyNot[rg.Name] = why; }
                            continue;
                        }
                        measuredOnce.Add(rg.Name);
                        List<double[]> inside = p.Ink.Where(r => (r[0] + r[2]) / 2 >= rect[0] && (r[0] + r[2]) / 2 <= rect[2] && (r[1] + r[3]) / 2 >= rect[1] && (r[1] + r[3]) / 2 <= rect[3]).ToList();
                        double[] box = Union(inside);
                        var rrow = new JObject { ["name"] = rg.Name, ["rect_mm"] = BoxMm(rect), ["items"] = inside.Count, ["box_mm"] = BoxMm(box),
                            ["sticks_out_mm"] = box == null ? (JToken)JValue.CreateNull() : new JArray(Mm(Math.Max(0, rect[0] - box[0])), Mm(Math.Max(0, rect[1] - box[1])), Mm(Math.Max(0, box[2] - rect[2])), Mm(Math.Max(0, box[3] - rect[3]))) };
                        if (mappingNote != null) rrow["mapping"] = mappingNote;
                        if (mappingNote == "approximate") incomplete.Add("region '" + rg.Name + "' (page " + p.Page + "): approximate mapping of its sheet onto the page");
                        if (p.Drawing != null)
                        {
                            JObject g = PdfQaPaper.Gray(p, rect);
                            rrow["gray"] = g;
                            if (g.Value<bool?>("measured") != true) incomplete.Add("region '" + rg.Name + "' (page " + p.Page + "): gray not measured (" + (g.Value<string>("why") ?? "no cell outside text and images") + ")");
                            else if (g.Value<bool?>("truncated") == true) incomplete.Add("region '" + rg.Name + "' (page " + p.Page + "): gray truncated");
                        }
                        regions.Add(rrow);
                    }
                    row["regions"] = regions;
                }
                result.Add(row);
            }
            foreach (PdfRegion rg in o.Regions)
                if (!measuredOnce.Contains(rg.Name))
                    incomplete.Add("region '" + rg.Name + "' not measured: " + (whyNot.TryGetValue(rg.Name, out string w) ? w : rg.Page.HasValue ? "the PDF has no page " + rg.Page.Value : "it applies to no page of this PDF"));
            return result;
        }

        public static double[] Union(IEnumerable<double[]> rects)
        {
            double[] u = null;
            foreach (double[] r in rects)
                u = u == null ? (double[])r.Clone() : new[] { Math.Min(u[0], r[0]), Math.Min(u[1], r[1]), Math.Max(u[2], r[2]), Math.Max(u[3], r[3]) };
            return u;
        }

        private static JToken BoxMm(double[] b) => b == null ? (JToken)JValue.CreateNull() : new JArray(Mm(b[0]), Mm(b[1]), Mm(b[2]), Mm(b[3]));

        private static double R(double v) => Math.Round(v, 1);
        private static double Mm(double points) => Math.Round(points / PdfQaOptions.PointsPerMm, 2);

        /// <summary>Words beyond the page edge, or inside the margin asked for.</summary>
        public static List<PdfWordFact> OutsideFrame(PdfPageFacts p, PdfQaOptions o)
        {
            double m = o.FrameMarginPoints;
            const double eps = 0.01;
            return p.Words.Where(w => w.Left < m - eps || w.Bottom < m - eps || w.Right > p.Width - m + eps || w.Top > p.Height - m + eps).ToList();
        }

        /// <summary>Pairs of words that really collide: both axes cross by more than the floor, sweep over Left.</summary>
        public static List<Tuple<PdfWordFact, PdfWordFact, double, double>> Overlaps(PdfPageFacts p, PdfQaOptions o)
        {
            var hits = new List<Tuple<PdfWordFact, PdfWordFact, double, double>>();
            List<PdfWordFact> words = p.Words.Where(w => w.Right > w.Left && w.Top > w.Bottom).OrderBy(w => w.Left).ToList();
            for (int i = 0; i < words.Count; i++)
                for (int j = i + 1; j < words.Count && words[j].Left < words[i].Right; j++)
                {
                    PdfWordFact a = words[i], b = words[j];
                    double ox = Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left);
                    double oy = Math.Min(a.Top, b.Top) - Math.Max(a.Bottom, b.Bottom);
                    if (ox <= o.OverlapMinPoints || oy <= o.OverlapMinPoints) continue;
                    double shorter = Math.Min(a.Top - a.Bottom, b.Top - b.Bottom);
                    if (oy < shorter * o.OverlapMinFraction) continue;
                    hits.Add(Tuple.Create(a, b, ox, oy));
                }
            return hits;
        }

        /// <summary>One-call convenience: read the file and judge it.</summary>
        public static JObject Inspect(string path, PdfQaOptions o)
        {
            o = o ?? new PdfQaOptions();
            List<PdfPageFacts> pages = Read(path, o.NeedsDrawing);
            JObject report = Judge(pages, o);
            report["path"] = path;
            if (o.CompareTo != null)
            {
                try
                {
                    if (!System.IO.File.Exists(o.CompareTo)) report["comparison"] = new JObject { ["verdict"] = "not_compared", ["why"] = "compare_to does not exist: " + o.CompareTo };
                    else
                    {
                        JObject cmp = PdfQaPaper.Compare(Read(o.CompareTo, false), pages);
                        cmp["before"] = o.CompareTo;
                        report["comparison"] = cmp;
                    }
                }
                catch (Exception ex) { report["comparison"] = new JObject { ["verdict"] = "not_compared", ["why"] = "compare_to could not be read: " + ex.Message }; }
                if (report["comparison"].Value<string>("verdict") == "not_compared")
                {
                    ((JArray)report["incomplete"]).Add(report["comparison"].Value<string>("why"));
                    if (report.Value<string>("verdict") == "clean") report["verdict"] = "partial";
                }
            }
            return report;
        }
    }
}
