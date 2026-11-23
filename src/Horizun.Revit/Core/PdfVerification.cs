using System;
using System.Collections.Generic;
using System.IO;
using System.Globalization;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public static class PdfVerification
    {
        public const string Advice = "for long batches use horizun_submit_job, or split view_ids into smaller batches";
        /// <summary>
        /// A produced PDF that contradicts the print policy. Not a time or batch-size failure, so the caller does NOT
        /// append <see cref="Advice"/> (which is about long batches) to it.
        /// </summary>
        public sealed class PolicyContradiction : InvalidOperationException
        {
            public PolicyContradiction(string message) : base(message) { }
        }

        /// <summary>The failure text of a package verification: the long-batch advice only where it fits (not a print-policy contradiction).</summary>
        public static string FailureText(Exception ex)
            => "PDF package verification failed: " + ex.Message + (ex is PolicyContradiction ? "" : ". " + Advice);

        private static string N1(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);
        private static string N2(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

        /// <summary>
        /// Says WHICH page (and file) and sheet a paper_format mismatch is. Only with Default paper and a page LARGER than the
        /// sheet's does it go on to compare the viewport boxes (sheet mm, from <paramref name="map"/>) with the frame; with a fixed
        /// paper, or a smaller page, the reason is the policy's own, with the format named. It states what was measured and nothing
        /// else: the box of a viewport is compared, not its drawing, and what else lies outside the frame is not measured.
        /// </summary>
        public static string PaperMismatchDetail(JObject verdict, string sheetLabel, PdfSheetMap map, string paperFormat = "Default",
            string fileName = null, double toleranceMm = 2.0)
        {
            int page = verdict.Value<int?>("page") ?? 0;
            double? pw = verdict.Value<double?>("page_width_mm"), ph = verdict.Value<double?>("page_height_mm");
            double? ew = verdict.Value<double?>("expected_width_points"), eh = verdict.Value<double?>("expected_height_points");
            var sb = new StringBuilder();
            sb.Append("page ").Append(page);
            if (!string.IsNullOrEmpty(fileName)) sb.Append(" of ").Append(fileName);
            string label = !string.IsNullOrEmpty(sheetLabel) ? sheetLabel : map?.Sheet;
            long? sid = verdict.Value<long?>("source_view_id") ?? (map != null && map.SheetId != 0 ? (long?)map.SheetId : null);
            if (!string.IsNullOrEmpty(label) || sid.HasValue)
                sb.Append(" (sheet ").Append(label ?? "").Append(sid.HasValue ? (string.IsNullOrEmpty(label) ? "" : ", ") + "id " + sid.Value : "").Append(")");
            bool haveSizes = pw.HasValue && ph.HasValue && ew.HasValue && eh.HasValue;
            double a = 0, b = 0, xw = 0, xh = 0;
            if (haveSizes)
            {
                a = Math.Min(pw.Value, ph.Value); b = Math.Max(pw.Value, ph.Value);
                xw = Math.Min(ew.Value, eh.Value) / 72.0 * 25.4; xh = Math.Max(ew.Value, eh.Value) / 72.0 * 25.4;
            }
            // Larger = no side shrank and at least one grew: content past one edge grows the page along that axis only.
            bool larger = haveSizes && (b - xh > toleranceMm || a - xw > toleranceMm) && b - xh >= -toleranceMm && a - xw >= -toleranceMm;
            if (paperFormat != "Default" || !larger)
            {
                sb.Append(": ").Append(verdict.Value<string>("paper_format_reason") ?? (haveSizes
                    ? "the produced page is " + N1(b) + "x" + N1(a) + " mm; " + paperFormat + " expects " + N1(xh) + "x" + N1(xw) + " mm" : "the page size contradicts the paper requested"));
                if (paperFormat != "Default") sb.Append(" (paper_format ").Append(paperFormat).Append(")");
                return sb.ToString();
            }
            double growW = b - xh, growH = a - xw;
            sb.Append(": the page came out ").Append(N1(b)).Append(" x ").Append(N1(a)).Append(" mm against ").Append(N1(xh)).Append(" x ").Append(N1(xw))
              .Append(" mm for the sheet, ").Append(N1(growW)).Append(" x ").Append(N1(growH)).Append(" mm larger.");
            double fw = b / xh, fh = a / xw;
            bool uniform = growW > toleranceMm && growH > toleranceMm && Math.Abs(fw - fh) <= 0.005;
            if (uniform)
                sb.Append(" It grew by the same factor in width and height (").Append(N2(fw)).Append("), which looks like a scaling (zoom or fit to page) more than content outside the frame.");
            else
                sb.Append(" Revit may enlarge the page when something on the sheet lies outside the frame.");
            double[] frame; string frameName;
            if (map?.TitleBlockMm != null) { frame = map.TitleBlockMm; frameName = "title block box"; }
            else if (map?.OutlineMm != null) { frame = map.OutlineMm; frameName = "sheet outline (this sheet has no title block box)"; }
            else { frame = null; frameName = null; }
            if (frame == null)
            {
                sb.Append(" The frame could not be read (").Append(map?.OutlineWhy ?? "no sheet outline and no title block box").Append("), so no viewport was compared with it.");
                return sb.ToString();
            }
            var outside = new List<string>();
            int unmeasured = 0; double worstOver = 0;
            foreach (PdfViewportBox vp in map.Viewports)
            {
                double[] box = vp.BoxMm;
                if (box == null || box.Length < 4) { unmeasured++; continue; }
                double left = frame[0] - box[0], bottom = frame[1] - box[1], right = box[2] - frame[2], top = box[3] - frame[3];
                double worst = Math.Max(Math.Max(left, bottom), Math.Max(right, top));
                if (worst <= toleranceMm) continue;
                worstOver = Math.Max(worstOver, worst);
                var sides = new List<string>();
                if (right > toleranceMm) sides.Add("right edge at " + N1(box[2]) + " mm, " + N1(right) + " mm beyond " + N1(frame[2]));
                if (left > toleranceMm) sides.Add("left edge at " + N1(box[0]) + " mm, " + N1(left) + " mm beyond " + N1(frame[0]));
                if (top > toleranceMm) sides.Add("top edge at " + N1(box[3]) + " mm, " + N1(top) + " mm beyond " + N1(frame[3]));
                if (bottom > toleranceMm) sides.Add("bottom edge at " + N1(box[1]) + " mm, " + N1(bottom) + " mm beyond " + N1(frame[1]));
                outside.Add("viewport " + vp.ViewportId + (string.IsNullOrEmpty(vp.View) ? "" : " '" + vp.View + "'") + " (" + string.Join("; ", sides) + ")");
            }
            sb.Append(" Compared against the ").Append(frameName).Append(": the BOX of each viewport, not its drawing (a box can reach past what is drawn in it).");
            if (outside.Count > 0)
            {
                sb.Append(" Boxes outside: ").Append(string.Join(", ", outside)).Append(".");
                bool unexplained = worstOver < Math.Max(growW, growH) / 2;
                if (unexplained)
                    sb.Append(" That overshoot (").Append(N1(worstOver)).Append(" mm) does not explain a growth of ").Append(N1(Math.Max(growW, growH))).Append(" mm.");
                if (uniform || unexplained)
                    sb.Append(" Moving those boxes inside the frame fixes the overshoot, not the page size: check the print setup's zoom / fit to page first.");
                else
                    sb.Append(" Move them inside the frame, or use a larger paper.");
            }
            else
                sb.Append(" No viewport box lies outside it; what other element (text, schedule, legend, label) lies outside was not measured.");
            if (unmeasured > 0) sb.Append(" ").Append(unmeasured).Append(" viewport(s) have no measurable box and were not compared.");
            return sb.ToString();
        }

        /// <summary>
        /// The refusal text for a print report that does not hold: every option the file contradicts, with the detail of a
        /// paper_format mismatch per page (<paramref name="paperDetail"/>, by the page's position in the report, 0-based) in
        /// place of the bare size line.
        /// </summary>
        public static string PolicyFailureMessage(JObject printReport, Func<int, string> paperDetail)
        {
            var parts = new List<string>();
            foreach (JObject r in ((JArray)printReport["options"]).OfType<JObject>())
            {
                string status = r.Value<string>("status");
                if (status != PdfPrintPolicy.StatusVerifiedMismatch && status != PdfPrintPolicy.StatusAppliedMismatch) continue;
                string option = r.Value<string>("option");
                string reason = r.Value<string>("reason");
                if (option == "paper_format" && status == PdfPrintPolicy.StatusVerifiedMismatch && paperDetail != null && r["pages"] is JArray pages)
                {
                    var perPage = new List<string>();
                    for (int i = 0; i < pages.Count; i++)
                    {
                        if (pages[i].Value<string>() != PdfPrintPolicy.StatusVerifiedMismatch) continue;
                        string d = paperDetail(i);
                        if (!string.IsNullOrEmpty(d)) perPage.Add(d);
                    }
                    if (perPage.Count > 0) reason = string.Join("; ", perPage);
                }
                parts.Add(option + " (" + status + ") - " + reason);
            }
            foreach (JObject c in ((JArray)printReport["composition"]["pages_exceeding_titleblock"]).OfType<JObject>())
                parts.Add("page " + c["page"] + " (sheet " + c["source_view_id"] + ") - " + c.Value<string>("reason"));
            return "The produced PDF contradicts the print policy: " + string.Join("; ", parts);
        }

        public static JObject Compare(IEnumerable<string> expected, IEnumerable<KeyValuePair<string, long>> produced, int? pagesExpected = null, int? pagesFound = null, string rule = null)
        {
            var e = expected.Select(Path.GetFileName).ToArray();
            var p = produced.Select(x => new KeyValuePair<string, long>(Path.GetFileName(x.Key), x.Value)).ToArray();
            var missing = e.Except(p.Select(x => x.Key), StringComparer.OrdinalIgnoreCase).ToArray();
            var unexpected = p.Select(x => x.Key).Except(e, StringComparer.OrdinalIgnoreCase).ToArray();
            string failed = rule ?? (missing.Length > 0 ? "missing_files" : unexpected.Length > 0 ? "unexpected_files" : pagesExpected.HasValue && pagesFound.HasValue && pagesExpected != pagesFound ? "page_count" : null);
            var result = new JObject { ["expected_files"] = new JArray(e), ["produced_files"] = new JArray(p.Select(x => new JObject { ["name"] = x.Key, ["size"] = x.Value })), ["missing"] = new JArray(missing), ["unexpected"] = new JArray(unexpected), ["rule"] = failed };
            if (pagesExpected.HasValue) result["pages_expected"] = pagesExpected.Value;
            if (pagesFound.HasValue) result["pages_found"] = pagesFound.Value;
            return result;
        }
    }
}
