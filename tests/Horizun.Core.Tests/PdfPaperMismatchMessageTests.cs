// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// A PDF whose page came out larger than the sheet's paper is refused; the refusal must say which
// page (and file) and sheet, what was measured, and nothing that was not measured. The long-batch
// advice must not ride on a print-policy contradiction.
// -----------------------------------------------------------------------------
using System;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class PdfPaperMismatchMessageTests
    {
        // The field case: page 909.87 x 638.17 mm against 827.1 x 580.1 mm (exactly 110 %).
        private static JObject Verdict(int page = 7, long sheet = 985289, double w = 909.87, double h = 638.17,
            double ewPt = 1644.367, double ehPt = 2344.524, string reason = null) => new JObject
        {
            ["page"] = page, ["source_view_id"] = sheet, ["page_width_mm"] = w, ["page_height_mm"] = h,
            ["expected_width_points"] = ewPt, ["expected_height_points"] = ehPt, ["paper_format"] = "verified_mismatch",
            ["paper_format_reason"] = reason ?? "the produced page is 638.2x909.9 mm; Default expects 580.1x827.1 mm"
        };

        private static PdfSheetMap Map(double[] outline, double[] titleBlock, params PdfViewportBox[] vps)
        {
            var m = new PdfSheetMap { Page = 7, SheetId = 985289, Sheet = "HZ-000 - INICIO", OutlineMm = outline, TitleBlockMm = titleBlock };
            m.Viewports.AddRange(vps);
            return m;
        }

        private static readonly double[] Outline = { 0.0, 0.0, 827.1, 580.1 };

        [Fact]
        public void FieldCaseNamesPageFileSheetAndSaysScalingNotAnExplainedCause()
        {
            // The real boxes: the viewport box 6.95..834.05 passes the frame, its drawing (47.3..793.7) does not and is not measured.
            var map = Map(Outline, null, new PdfViewportBox { ViewportId = 985294, View = "Portada", BoxMm = new[] { 6.95, 20.0, 834.05, 500.0 } });
            string t = PdfVerification.PaperMismatchDetail(Verdict(), "HZ-000 - INICIO", map, "Default", "casa.pdf");
            Assert.Contains("page 7 of casa.pdf", t);
            Assert.Contains("HZ-000 - INICIO", t);
            Assert.Contains("985289", t);
            Assert.Contains("909.9 x 638.2 mm", t);
            Assert.Contains("827.1 x 580.1 mm", t);
            Assert.Contains("same factor in width and height (1.1)", t);
            Assert.Contains("scaling", t);
            Assert.Contains("this sheet has no title block box", t);
            Assert.Contains("BOX of each viewport, not its drawing", t);
            Assert.Contains("viewport 985294", t);
            Assert.Contains("does not explain a growth", t);
            Assert.Contains("check the print setup's zoom / fit to page first", t);
            Assert.DoesNotContain("use a larger paper", t);
            Assert.DoesNotContain("so the cause is", t);
            Assert.DoesNotContain("-", t.Substring(t.IndexOf("mm for the sheet", StringComparison.Ordinal), 40));
            Assert.DoesNotContain("horizun_submit_job", t);
        }

        [Fact]
        public void NoViewportOutsideSaysTheOtherElementWasNotMeasured()
        {
            var map = Map(Outline, null, new PdfViewportBox { ViewportId = 5, BoxMm = new[] { 0.0, 0.0, 800.0, 500.0 } });
            string t = PdfVerification.PaperMismatchDetail(Verdict(w: 950, h: 610.1), "S", map);
            Assert.Contains("No viewport box lies outside it", t);
            Assert.Contains("was not measured", t);
            Assert.DoesNotContain("so the cause", t);
            Assert.Contains("may enlarge", t);   // a non-uniform growth: a possibility, not a fact
        }

        [Fact]
        public void CountsViewportsWithoutAMeasurableBoxAndUsesTheTitleBlockWhenThereIsOne()
        {
            var map = Map(Outline, new[] { 0.0, 0.0, 800.0, 560.0 },
                new PdfViewportBox { ViewportId = 1 }, new PdfViewportBox { ViewportId = 2, BoxMm = new[] { 0.0, 0.0, 700.0, 100.0 } });
            string t = PdfVerification.PaperMismatchDetail(Verdict(), "S", map);
            Assert.Contains("1 viewport(s) have no measurable box", t);
            Assert.Contains("title block box", t);
            Assert.DoesNotContain("no title block box)", t);
        }

        [Fact]
        public void UnreadableFrameSaysWhy()
        {
            var map = new PdfSheetMap { Page = 1, OutlineWhy = "Revit returned no outline for sheet X" };
            string t = PdfVerification.PaperMismatchDetail(Verdict(), "S", map);
            Assert.Contains("frame could not be read (Revit returned no outline for sheet X)", t);
            Assert.Contains("frame could not be read", PdfVerification.PaperMismatchDetail(Verdict(), "S", null));
        }

        [Fact]
        public void FixedPaperKeepsTheReasonAndNamesTheFormat()
        {
            var v = Verdict(w: 594, h: 841, ewPt: 1683.78, ehPt: 2383.937, reason: "the produced page is 594x841 mm; A1 expects 594x841 mm");
            string t = PdfVerification.PaperMismatchDetail(v, "S", Map(Outline, null), "A1", "f.pdf");
            Assert.Contains("page 7 of f.pdf", t);
            Assert.Contains("A1 expects", t);
            Assert.Contains("(paper_format A1)", t);
            Assert.DoesNotContain("larger", t);
            Assert.DoesNotContain("frame", t);
        }

        [Fact]
        public void FixedPaperWithALargerPageStillDoesNotTellTheFrameStory()
        {
            string t = PdfVerification.PaperMismatchDetail(Verdict(), "S", Map(Outline, null), "A3");
            Assert.Contains("(paper_format A3)", t);
            Assert.DoesNotContain("larger", t);
            Assert.DoesNotContain("Boxes outside", t);
        }

        [Fact]
        public void SmallerPageUnderDefaultKeepsTheReasonAndNeverSaysLarger()
        {
            var v = Verdict(w: 700, h: 500, reason: "the produced page is 500x700 mm; Default expects 580.1x827.1 mm");
            string t = PdfVerification.PaperMismatchDetail(v, "S", Map(Outline, null));
            Assert.Contains("Default expects 580.1x827.1 mm", t);
            Assert.DoesNotContain("larger", t);
        }

        [Fact]
        public void PolicyFailureUsesTheGlobalPageIndexNotThePageNumber()
        {
            // Two files whose pages both carry the number 5: the first row failed. Passing the page number (old bug) would
            // give INDEX-5; the position gives INDEX-0.
            var report = new JObject
            {
                ["options"] = new JArray(new JObject { ["option"] = "paper_format", ["status"] = "verified_mismatch", ["reason"] = "bare",
                    ["pages"] = new JArray("verified_mismatch", "verified") }),
                ["pages"] = new JArray(new JObject { ["page"] = 5 }, new JObject { ["page"] = 5 }),
                ["composition"] = new JObject { ["pages_exceeding_titleblock"] = new JArray() }
            };
            string t = PdfVerification.PolicyFailureMessage(report, i => "INDEX-" + i);
            Assert.Contains("paper_format (verified_mismatch) - INDEX-0", t);
            Assert.DoesNotContain("INDEX-5", t);
            Assert.DoesNotContain("INDEX-1", t);
        }

        [Fact]
        public void ExportCommandLooksUpTheVerdictAndFileByPosition()
        {
            var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !System.IO.Directory.Exists(System.IO.Path.Combine(dir.FullName, "src", "Horizun.Revit", "Commands"))) dir = dir.Parent;
            Assert.NotNull(dir);
            string src = System.IO.File.ReadAllText(System.IO.Path.Combine(dir.FullName, "src", "Horizun.Revit", "Commands", "ExportCommand.cs"));
            Assert.Contains("pageVerdicts[idx]", src);
            Assert.Contains("pageFiles[idx]", src);
        }

        [Fact]
        public void GrowthAlongOneSideStillGetsTheFrameDetail()
        {
            // Content past the right edge widens the page only (897.9 x 580.2 against 827.1 x 580.1).
            var map = Map(Outline, null, new PdfViewportBox { ViewportId = 9, BoxMm = new[] { 10.0, 10.0, 897.0, 570.0 } });
            string t = PdfVerification.PaperMismatchDetail(Verdict(w: 897.9, h: 580.2), "S", map);
            Assert.Contains("70.8 x 0.1 mm larger", t);
            Assert.Contains("viewport 9", t);
            Assert.DoesNotContain("same factor", t);
            Assert.DoesNotContain("does not explain", t);
            Assert.Contains("Move them inside the frame, or use a larger paper.", t);
        }

        [Fact]
        public void PolicyFailureKeepsOtherReasonsAndTheBareReasonWithoutDetail()
        {
            var report = new JObject
            {
                ["options"] = new JArray(
                    new JObject { ["option"] = "paper_format", ["status"] = "verified_mismatch", ["reason"] = "bare", ["pages"] = new JArray("verified_mismatch") },
                    new JObject { ["option"] = "zoom", ["status"] = "applied_mismatch", ["reason"] = "rewritten" },
                    new JObject { ["option"] = "other", ["status"] = "verified" }),
                ["pages"] = new JArray(new JObject { ["page"] = 1 }),
                ["composition"] = new JObject { ["pages_exceeding_titleblock"] = new JArray() }
            };
            string t = PdfVerification.PolicyFailureMessage(report, i => "DETAIL");
            Assert.StartsWith("The produced PDF contradicts the print policy: paper_format (verified_mismatch) - DETAIL", t);
            Assert.Contains("zoom (applied_mismatch) - rewritten", t);
            Assert.DoesNotContain("other", t);
            Assert.Contains("- bare", PdfVerification.PolicyFailureMessage(report, i => null));
        }

        [Fact]
        public void PolicyFailureListsPagesExceedingTheTitleblock()
        {
            var report = new JObject
            {
                ["options"] = new JArray(),
                ["pages"] = new JArray(),
                ["composition"] = new JObject { ["pages_exceeding_titleblock"] = new JArray(
                    new JObject { ["page"] = 3, ["source_view_id"] = 77, ["reason"] = "the page is 10 x 10 mm larger than the titleblock" }) }
            };
            string t = PdfVerification.PolicyFailureMessage(report, i => null);
            Assert.Contains("page 3 (sheet 77) - the page is 10 x 10 mm larger than the titleblock", t);
        }

        [Fact]
        public void TheLongBatchAdviceIsNotAppendedToAPolicyContradiction()
        {
            string contradiction = PdfVerification.FailureText(new PdfVerification.PolicyContradiction("boom"));
            Assert.Equal("PDF package verification failed: boom", contradiction);
            Assert.DoesNotContain("horizun_submit_job", contradiction);
            Assert.Contains("horizun_submit_job", PdfVerification.FailureText(new InvalidOperationException("PDF page count mismatch.")));
        }
    }
}
