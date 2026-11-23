// -----------------------------------------------------------------------------
// Horizun Core tests — original Horizun code.
//
// The PDF print policy. The properties worth proving are the refusals and the
// honesty of the account: an option that would be silently ignored is refused
// by name, an absent option is 'defaulted' and never 'requested', a page that
// contradicts the paper request is a mismatch and never a pass, and nothing
// the page cannot testify to is ever called verified.
// -----------------------------------------------------------------------------
using System;
using System.Globalization;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class PdfPrintPolicyTests
    {
        private static JObject Applied(PdfPrintPolicy p) => p.Canonical();

        [Fact]
        public void AbsentPolicyIsDefaultsWithNothingRequested()
        {
            PdfPrintPolicy p = PdfPrintPolicy.Parse(null, "mm", 2026);
            Assert.Equal("Default", p.PaperFormat);
            Assert.Equal("auto", p.Orientation);
            Assert.Empty(p.Requested);
            Assert.False(p.IsRequested("paper_format"));
        }

        [Fact]
        public void UnknownFieldIsRefusedByName()
        {
            var ex = Assert.Throws<ArgumentException>(() =>
                PdfPrintPolicy.Parse(new JObject { ["paper_size"] = "ISO_A1" }, "mm", 2026));
            Assert.Contains("paper_size", ex.Message);
            Assert.Contains("paper_format", ex.Message);
        }

        [Fact]
        public void PaperFormatEnumMatchesRevitApiNamesInEveryYear()
        {
            // The 23 names measured on RevitAPI.dll 2023..2027 with a metadata reader.
            Assert.Equal(23, PdfPrintPolicy.PaperFormats.Length);
            Assert.Contains("ISO_A1", PdfPrintPolicy.PaperFormats);
            Assert.Contains("ARCH_E3", PdfPrintPolicy.PaperFormats);
            Assert.Throws<ArgumentException>(() =>
                PdfPrintPolicy.Parse(new JObject { ["paper_format"] = "A1" }, "mm", 2026));
        }

        [Fact]
        public void ZoomPercentageWithoutZoomIsRefusedNotIgnored()
        {
            var ex = Assert.Throws<ArgumentException>(() =>
                PdfPrintPolicy.Parse(new JObject { ["zoom"] = "fit_to_page", ["zoom_percentage"] = 50 }, "mm", 2026));
            Assert.Contains("silently ignored", ex.Message);
            Assert.Throws<ArgumentException>(() =>
                PdfPrintPolicy.Parse(new JObject { ["zoom"] = "zoom" }, "mm", 2026));
            PdfPrintPolicy ok = PdfPrintPolicy.Parse(new JObject { ["zoom"] = "zoom", ["zoom_percentage"] = 50 }, "mm", 2026);
            Assert.Equal(50, ok.ZoomPercentage);
        }

        [Fact]
        public void OffsetsRequireLowerLeftAndComeTogether()
        {
            // Revit's Margins placement IS LowerLeft (same enum value, measured on 2026),
            // so the policy does not pretend there is a third placement.
            Assert.Throws<ArgumentException>(() =>
                PdfPrintPolicy.Parse(new JObject { ["placement"] = "margins" }, "mm", 2026));
            Assert.Throws<ArgumentException>(() =>
                PdfPrintPolicy.Parse(new JObject { ["placement"] = "center", ["origin_offset_x"] = 10, ["origin_offset_y"] = 10 }, "mm", 2026));
            Assert.Throws<ArgumentException>(() =>
                PdfPrintPolicy.Parse(new JObject { ["placement"] = "lower_left", ["origin_offset_x"] = 10 }, "mm", 2026));
            PdfPrintPolicy p = PdfPrintPolicy.Parse(new JObject { ["placement"] = "lower_left", ["zoom"] = "zoom", ["zoom_percentage"] = 50, ["origin_offset_x"] = 304.8, ["origin_offset_y"] = 0 }, "mm", 2026);
            Assert.Equal(1.0, p.OriginOffsetXFeet.Value, 9);
            Assert.Equal(0.0, p.OriginOffsetYFeet.Value, 9);
            PdfPrintPolicy plain = PdfPrintPolicy.Parse(new JObject { ["placement"] = "lower_left" }, "mm", 2026);
            Assert.Null(plain.OriginOffsetXFeet);
        }

        [Fact]
        public void ExportInBackgroundIsRefusedEverywhereAndNamesTheYearBefore2025()
        {
            var old = Assert.Throws<ArgumentException>(() =>
                PdfPrintPolicy.Parse(new JObject { ["export_in_background"] = true }, "mm", 2024));
            Assert.Contains("2024", old.Message);
            Assert.Contains("2025", old.Message);
            // Measured: a background export returns before the file exists, and this
            // bridge reports only files it re-read - so true is refused on every year.
            var bg = Assert.Throws<ArgumentException>(() =>
                PdfPrintPolicy.Parse(new JObject { ["export_in_background"] = true }, "mm", 2026));
            Assert.Contains("before the file exists", bg.Message);
            PdfPrintPolicy p = PdfPrintPolicy.Parse(new JObject { ["export_in_background"] = false }, "mm", 2026);
            Assert.False(p.ExportInBackground);
            Assert.True(p.IsRequested("export_in_background"));
        }

        [Fact]
        public void OffsetsWithFitToPageAreRefusedBecauseTheyClipThePrint()
        {
            // Rendered and looked at on 2026 (c8b): lower_left + 10/20 mm offsets with the
            // default fit_to_page lost 20 mm at the top and 6 mm at the right of an A3.
            var ex = Assert.Throws<ArgumentException>(() => PdfPrintPolicy.Parse(
                new JObject { ["placement"] = "lower_left", ["origin_offset_x"] = 10, ["origin_offset_y"] = 20 }, "mm", 2026));
            Assert.Contains("zoom_percentage", ex.Message);
            Assert.Contains("clipped", ex.Message);
            PdfPrintPolicy ok = PdfPrintPolicy.Parse(
                new JObject { ["placement"] = "lower_left", ["zoom"] = "zoom", ["zoom_percentage"] = 35, ["origin_offset_x"] = 10, ["origin_offset_y"] = 20 }, "mm", 2026);
            Assert.Equal(35, ok.ZoomPercentage);
            Assert.True(ok.OriginOffsetXFeet.HasValue && ok.OriginOffsetYFeet.HasValue);
        }

        [Fact]
        public void OffsetsReadBackAsNumbersMatchUnderACommaDecimalCulture()
        {
            // Found live (c7, 2026-09-08, host culture with a comma decimal separator):
            // the offsets read back as numeric tokens, the comparison formatted them
            // with the current culture and the invariant parse failed, so every
            // lower_left export with offsets died as an applied_mismatch.
            CultureInfo was = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = new CultureInfo("es-CO");
                PdfPrintPolicy p = PdfPrintPolicy.Parse(new JObject { ["placement"] = "lower_left", ["zoom"] = "zoom", ["zoom_percentage"] = 35, ["origin_offset_x"] = 10, ["origin_offset_y"] = 20 }, "mm", 2026);
                JObject applied = Applied(p);
                Assert.Contains(",", applied["origin_offset_x"].ToString());   // the mechanism, on record
                JObject report = p.Report(applied, null);
                var rows = ((JArray)report["options"]).Cast<JObject>().ToDictionary(r => r.Value<string>("option"));
                Assert.Equal(PdfPrintPolicy.StatusRequestedUnverifiable, rows["origin_offset_x"].Value<string>("status"));
                Assert.Equal(PdfPrintPolicy.StatusRequestedUnverifiable, rows["origin_offset_y"].Value<string>("status"));
                Assert.True(report.Value<bool>("verifiable_options_held"));
                // A genuinely rewritten offset is still a mismatch and the reason carries both numbers.
                applied["origin_offset_x"] = 0.05;
                JObject bad = p.Report(applied, null);
                JObject row = ((JArray)bad["options"]).Cast<JObject>().First(r => r.Value<string>("option") == "origin_offset_x");
                Assert.Equal(PdfPrintPolicy.StatusAppliedMismatch, row.Value<string>("status"));
                Assert.Contains("0.05", row.Value<string>("reason"));
                Assert.Contains("0.032808399", row.Value<string>("reason"));
                Assert.False(bad.Value<bool>("verifiable_options_held"));
            }
            finally { CultureInfo.CurrentCulture = was; }
        }

        [Fact]
        public void AnOptionTheExporterRewroteIsAnAppliedMismatchThatFailsTheExport()
        {
            PdfPrintPolicy p = PdfPrintPolicy.Parse(new JObject { ["placement"] = "lower_left", ["color_depth"] = "grayscale" }, "mm", 2026);
            JObject applied = Applied(p);
            applied["color_depth"] = "color";   // what a rewriting exporter would read back
            JObject report = p.Report(applied, null);
            JObject row = ((JArray)report["options"]).Cast<JObject>().First(r => r.Value<string>("option") == "color_depth");
            Assert.Equal(PdfPrintPolicy.StatusAppliedMismatch, row.Value<string>("status"));
            Assert.Contains("read back", row.Value<string>("reason"));
            Assert.False(report.Value<bool>("verifiable_options_held"));
            // The faithful read-back stays unverifiable, not a mismatch.
            JObject ok = p.Report(Applied(p), null);
            Assert.Equal(PdfPrintPolicy.StatusRequestedUnverifiable,
                ((JArray)ok["options"]).Cast<JObject>().First(r => r.Value<string>("option") == "placement").Value<string>("status"));
            Assert.True(ok.Value<bool>("verifiable_options_held"));
        }

        [Fact]
        public void DpiMustBeOneRevitOffers()
        {
            Assert.Throws<ArgumentException>(() =>
                PdfPrintPolicy.Parse(new JObject { ["export_quality_dpi"] = 150 }, "mm", 2026));
            Assert.Equal(1200, PdfPrintPolicy.Parse(new JObject { ["export_quality_dpi"] = 1200 }, "mm", 2026).ExportQualityDpi);
        }

        [Fact]
        public void ExpectedSizesAreTheNominalPaperSizes()
        {
            double[] a1 = PdfPrintPolicy.ExpectedPointsPortrait("ISO_A1");
            Assert.Equal(594.0 * 72 / 25.4, a1[0], 6);
            Assert.Equal(841.0 * 72 / 25.4, a1[1], 6);
            double[] archD = PdfPrintPolicy.ExpectedPointsPortrait("ARCH_D");
            Assert.Equal(24 * 72.0, archD[0], 6);
            Assert.Equal(36 * 72.0, archD[1], 6);
            Assert.Null(PdfPrintPolicy.ExpectedPointsPortrait("Default"));
        }

        [Fact]
        public void LandscapeA1PageVerifiesAgainstA1Landscape()
        {
            PdfPrintPolicy p = PdfPrintPolicy.Parse(new JObject { ["paper_format"] = "ISO_A1", ["orientation"] = "landscape" }, "mm", 2026);
            JObject v = p.VerifyPage(PdfPrintPolicy.MmToPoints(841), PdfPrintPolicy.MmToPoints(594), null);
            Assert.Equal(PdfPrintPolicy.StatusVerified, v.Value<string>("paper_format"));
            Assert.Equal(PdfPrintPolicy.StatusVerified, v.Value<string>("orientation"));
            Assert.Equal("landscape", v.Value<string>("page_orientation_observed"));
            Assert.True(v.Value<bool>("page_verified"));
        }

        [Fact]
        public void WrongPaperIsAMismatchNeverAPass()
        {
            PdfPrintPolicy p = PdfPrintPolicy.Parse(new JObject { ["paper_format"] = "ISO_A1" }, "mm", 2026);
            JObject v = p.VerifyPage(PdfPrintPolicy.MmToPoints(420), PdfPrintPolicy.MmToPoints(297), null);
            Assert.Equal(PdfPrintPolicy.StatusVerifiedMismatch, v.Value<string>("paper_format"));
            Assert.Contains("ISO_A1 expects", v.Value<string>("paper_format_reason"));
            Assert.False(v.Value<bool>("page_verified"));
        }

        [Fact]
        public void WrongOrientationIsAMismatch()
        {
            PdfPrintPolicy p = PdfPrintPolicy.Parse(new JObject { ["paper_format"] = "ISO_A3", ["orientation"] = "portrait" }, "mm", 2026);
            JObject v = p.VerifyPage(PdfPrintPolicy.MmToPoints(420), PdfPrintPolicy.MmToPoints(297), null);
            Assert.Equal(PdfPrintPolicy.StatusVerified, v.Value<string>("paper_format"));
            Assert.Equal(PdfPrintPolicy.StatusVerifiedMismatch, v.Value<string>("orientation"));
            Assert.False(v.Value<bool>("page_verified"));
        }

        [Fact]
        public void DefaultPaperIsVerifiedAgainstTheSheetsOwnSize()
        {
            PdfPrintPolicy p = PdfPrintPolicy.Parse(null, "mm", 2026);
            double[] sheet = { PdfPrintPolicy.MmToPoints(841), PdfPrintPolicy.MmToPoints(594) };
            JObject ok = p.VerifyPage(sheet[0], sheet[1], sheet);
            Assert.Equal(PdfPrintPolicy.StatusVerified, ok.Value<string>("paper_format"));
            // Auto orientation is reported, never judged.
            Assert.Equal(PdfPrintPolicy.StatusRequestedUnverifiable, ok.Value<string>("orientation"));
            Assert.True(ok.Value<bool>("page_verified"));

            JObject unknown = p.VerifyPage(sheet[0], sheet[1], null);
            Assert.Equal(PdfPrintPolicy.StatusRequestedUnverifiable, unknown.Value<string>("paper_format"));
            Assert.Contains("not readable", unknown.Value<string>("paper_format_reason"));
        }

        [Fact]
        public void Implicit_fit_reports_proportional_page_size_without_claiming_physical_paper()
        {
            double[] firstSheet = { PdfPrintPolicy.MmToPoints(827.1), PdfPrintPolicy.MmToPoints(580.1) };
            double[] secondSheet = { PdfPrintPolicy.MmToPoints(420), PdfPrintPolicy.MmToPoints(297) };
            double width = PdfPrintPolicy.MmToPoints(909.9), height = PdfPrintPolicy.MmToPoints(638.2);
            var policy = PdfPrintPolicy.Parse(null, "mm", 2025);
            JObject scaled = policy.VerifyPage(width, height, firstSheet, titleblockPoints: firstSheet);
            JObject exact = policy.VerifyPage(secondSheet[0], secondSheet[1], secondSheet);

            Assert.Equal(PdfPrintPolicy.StatusRequestedUnverifiable, scaled.Value<string>("paper_format"));
            Assert.Equal(1.1, scaled.Value<double>("page_to_sheet_dimension_ratio"), 3);
            Assert.Null(scaled.Value<bool?>("page_verified"));
            Assert.Null(scaled.Value<bool?>("page_exceeds_titleblock"));
            Assert.Equal(PdfPrintPolicy.StatusVerified, exact.Value<string>("paper_format"));
            JObject report = policy.Report(Applied(policy), new[] { scaled, exact });
            Assert.True(report.Value<bool>("verifiable_options_held"));
            Assert.Null(report["composition"].Value<bool?>("held"));

            JObject distorted = policy.VerifyPage(width, height + 10, firstSheet);
            JObject failed = policy.Report(Applied(policy), new[] { scaled, distorted });
            Assert.False(failed.Value<bool>("verifiable_options_held"));
            Assert.Equal(distorted.Value<string>("paper_format_reason"),
                failed["options"].Single(row => row.Value<string>("option") == "paper_format").Value<string>("reason"));

            foreach (JObject requested in new[] { new JObject { ["paper_format"] = "Default" },
                                                  new JObject { ["zoom"] = "fit_to_page" } })
            {
                var explicitPolicy = PdfPrintPolicy.Parse(requested, "mm", 2025);
                JObject mismatch = explicitPolicy.VerifyPage(width, height, firstSheet);
                Assert.Equal(PdfPrintPolicy.StatusVerifiedMismatch, mismatch.Value<string>("paper_format"));
                Assert.False(explicitPolicy.Report(Applied(explicitPolicy), new[] { mismatch }).Value<bool>("verifiable_options_held"));
            }
        }

        [Theory]
        [InlineData(0.9)] // The page matches the titleblock.
        [InlineData(0.8)] // The page is smaller than the titleblock.
        public void Implicit_fit_reduction_leaves_composition_unverified(double ratio)
        {
            double[] outline = { 1000, 800 };
            double[] titleblock = { 900, 720 };
            var policy = PdfPrintPolicy.Parse(null, "mm", 2025);
            JObject page = policy.VerifyPage(outline[0] * ratio, outline[1] * ratio, outline,
                titleblockPoints: titleblock);

            Assert.Equal(PdfPrintPolicy.StatusRequestedUnverifiable, page.Value<string>("paper_format"));
            Assert.Equal(ratio, page.Value<double>("page_to_sheet_dimension_ratio"), 4);
            Assert.Equal(JTokenType.Null, page["page_verified"].Type);
            Assert.Equal(JTokenType.Null, page["page_exceeds_titleblock"].Type);
            Assert.Contains("overflow unverified", page.Value<string>("page_exceeds_titleblock_reason"));
            JObject exact = policy.VerifyPage(titleblock[0], titleblock[1], titleblock, titleblockPoints: titleblock);
            JObject report = policy.Report(Applied(policy), new[] { exact, page });
            Assert.True(report.Value<bool>("verifiable_options_held"));
            Assert.Equal(JTokenType.Null, report["composition"]["held"].Type);
            Assert.Empty((JArray)report["composition"]["pages_exceeding_titleblock"]);
        }

        [Fact]
        public void ToleranceIsTwoPointsNotTwoMillimetres()
        {
            PdfPrintPolicy p = PdfPrintPolicy.Parse(new JObject { ["paper_format"] = "ISO_A4" }, "mm", 2026);
            double w = PdfPrintPolicy.MmToPoints(210), h = PdfPrintPolicy.MmToPoints(297);
            Assert.Equal(PdfPrintPolicy.StatusVerified, p.VerifyPage(w + 1.9, h, null).Value<string>("paper_format"));
            Assert.Equal(PdfPrintPolicy.StatusVerifiedMismatch, p.VerifyPage(w + 2.1, h, null).Value<string>("paper_format"));
        }

        [Fact]
        public void ReportSeparatesRequestedDefaultedVerifiedAndUnverifiable()
        {
            PdfPrintPolicy p = PdfPrintPolicy.Parse(new JObject { ["paper_format"] = "ISO_A1", ["orientation"] = "landscape", ["color_depth"] = "grayscale" }, "mm", 2026);
            JObject page = p.VerifyPage(PdfPrintPolicy.MmToPoints(841), PdfPrintPolicy.MmToPoints(594), null);
            JObject report = p.Report(Applied(p), new[] { page });
            var rows = ((JArray)report["options"]).Cast<JObject>().ToDictionary(r => r.Value<string>("option"));

            Assert.Equal(PdfPrintPolicy.StatusRequested, rows["paper_format"].Value<string>("source"));
            Assert.Equal(PdfPrintPolicy.StatusVerified, rows["paper_format"].Value<string>("status"));
            Assert.Equal(PdfPrintPolicy.StatusVerified, rows["orientation"].Value<string>("status"));

            Assert.Equal(PdfPrintPolicy.StatusRequested, rows["color_depth"].Value<string>("source"));
            Assert.Equal(PdfPrintPolicy.StatusRequestedUnverifiable, rows["color_depth"].Value<string>("status"));
            Assert.Equal("grayscale", rows["color_depth"].Value<string>("applied"));

            Assert.Equal(PdfPrintPolicy.StatusDefaulted, rows["raster_quality"].Value<string>("source"));
            Assert.Equal(JTokenType.Null, rows["raster_quality"]["requested"].Type);
            Assert.Equal(PdfPrintPolicy.StatusRequestedUnverifiable, rows["raster_quality"].Value<string>("status"));

            Assert.Equal(PdfPrintPolicy.StatusNotApplicable, rows["zoom_percentage"].Value<string>("status"));
            Assert.Equal(PdfPrintPolicy.StatusNotApplicable, rows["origin_offset_x"].Value<string>("status"));
            Assert.True(report.Value<bool>("verifiable_options_held"));
        }

        [Fact]
        public void ReportFailsTheVerifiableSetWhenAnyPageMismatches()
        {
            PdfPrintPolicy p = PdfPrintPolicy.Parse(new JObject { ["paper_format"] = "ISO_A1" }, "mm", 2026);
            JObject good = p.VerifyPage(PdfPrintPolicy.MmToPoints(841), PdfPrintPolicy.MmToPoints(594), null);
            JObject bad = p.VerifyPage(PdfPrintPolicy.MmToPoints(420), PdfPrintPolicy.MmToPoints(297), null);
            JObject report = p.Report(Applied(p), new[] { good, bad });
            Assert.False(report.Value<bool>("verifiable_options_held"));
            JObject row = ((JArray)report["options"]).Cast<JObject>().First(r => r.Value<string>("option") == "paper_format");
            Assert.Equal(PdfPrintPolicy.StatusVerifiedMismatch, row.Value<string>("status"));
            Assert.Equal(new[] { PdfPrintPolicy.StatusVerified, PdfPrintPolicy.StatusVerifiedMismatch }, row["pages"].Values<string>().ToArray());
        }

        [Fact]
        public void ReportWithoutPagesClaimsNothingVerified()
        {
            PdfPrintPolicy p = PdfPrintPolicy.Parse(new JObject { ["paper_format"] = "ISO_A1" }, "mm", 2026);
            JObject report = p.Report(Applied(p), null);
            JObject row = ((JArray)report["options"]).Cast<JObject>().First(r => r.Value<string>("option") == "paper_format");
            Assert.Equal(PdfPrintPolicy.StatusRequestedUnverifiable, row.Value<string>("status"));
            Assert.DoesNotContain(((JArray)report["options"]).Cast<JObject>(), r => r.Value<string>("status") == PdfPrintPolicy.StatusVerified);
        }

        [Fact]
        public void ExportInBackgroundIsNotApplicableWhenTheHostHasNoSuchOption()
        {
            PdfPrintPolicy p = PdfPrintPolicy.Parse(null, "mm", 2023);
            JObject applied = Applied(p); applied.Remove("export_in_background");   // what a 2023 host reads back
            JObject report = p.Report(applied, null);
            JObject row = ((JArray)report["options"]).Cast<JObject>().First(r => r.Value<string>("option") == "export_in_background");
            Assert.Equal(PdfPrintPolicy.StatusNotApplicable, row.Value<string>("status"));
        }

        [Fact]
        public void CanonicalFormIsStableForThePlanHash()
        {
            var a = PdfPrintPolicy.Parse(new JObject { ["paper_format"] = "ISO_A1", ["orientation"] = "landscape" }, "mm", 2026).Canonical();
            var b = PdfPrintPolicy.Parse(new JObject { ["orientation"] = "landscape", ["paper_format"] = "ISO_A1" }, "mm", 2026).Canonical();
            Assert.True(JToken.DeepEquals(a, b));
            // One canonical property per contract field (the two offsets carry a _feet suffix).
            Assert.Equal(PdfPrintPolicy.Fields.Length, a.Properties().Count());
        }

        [Fact]
        public void APageLargerThanItsTitleblockIsACompositionDefectWithDefaultPaper()
        {
            // Measured live 2026-09-08: an overflowing sheet number made the Default-paper
            // page 70.8 mm wider than its ARCH E1 titleblock (1066.8 x 762 mm).
            PdfPrintPolicy p = PdfPrintPolicy.Parse(null, "mm", 2026);
            double[] titleblock = { PdfPrintPolicy.MmToPoints(1066.8), PdfPrintPolicy.MmToPoints(762) };
            double[] outline = { PdfPrintPolicy.MmToPoints(1137.6), PdfPrintPolicy.MmToPoints(762) };
            JObject v = p.VerifyPage(outline[0], outline[1], outline, 2.0, titleblock);
            // The paper matched the outline - Revit did what it does - and the page is still wrong.
            Assert.Equal(PdfPrintPolicy.StatusVerified, v.Value<string>("paper_format"));
            Assert.True(v.Value<bool>("page_exceeds_titleblock"));
            Assert.Contains("70.8", v.Value<string>("page_exceeds_titleblock_reason"));
            Assert.False(v.Value<bool>("page_verified"));
            JObject report = p.Report(Applied(p), new[] { v });
            Assert.False(report["composition"].Value<bool>("held"));
            Assert.Single((JArray)report["composition"]["pages_exceeding_titleblock"]);
            Assert.False(report.Value<bool>("verifiable_options_held"));
        }

        [Fact]
        public void APageThatMatchesItsTitleblockHoldsComposition()
        {
            PdfPrintPolicy p = PdfPrintPolicy.Parse(null, "mm", 2026);
            double[] titleblock = { PdfPrintPolicy.MmToPoints(1066.8), PdfPrintPolicy.MmToPoints(762) };
            JObject v = p.VerifyPage(titleblock[0] + 1.0, titleblock[1], titleblock, 2.0, titleblock);
            Assert.False(v.Value<bool>("page_exceeds_titleblock"));
            Assert.True(v.Value<bool>("page_verified"));
            Assert.True(p.Report(Applied(p), new[] { v })["composition"].Value<bool>("held"));
        }

        [Fact]
        public void ANamedPaperFormatDoesNotJudgeTheTitleblock()
        {
            // On ISO_A3 the content is fitted to the paper; the page cannot testify
            // about what lay outside the titleblock, so no claim is made.
            PdfPrintPolicy p = PdfPrintPolicy.Parse(new JObject { ["paper_format"] = "ISO_A3" }, "mm", 2026);
            double[] titleblock = { PdfPrintPolicy.MmToPoints(1066.8), PdfPrintPolicy.MmToPoints(762) };
            JObject v = p.VerifyPage(PdfPrintPolicy.MmToPoints(420), PdfPrintPolicy.MmToPoints(297), null, 2.0, titleblock);
            Assert.False(v.Value<bool>("page_exceeds_titleblock"));
            Assert.True(v.Value<bool>("page_verified"));
        }

        [Fact]
        public void UnitsGovernOffsetsOnly()
        {
            PdfPrintPolicy m = PdfPrintPolicy.Parse(new JObject { ["placement"] = "lower_left", ["zoom"] = "zoom", ["zoom_percentage"] = 50, ["origin_offset_x"] = 1, ["origin_offset_y"] = 2 }, "m", 2026);
            Assert.Equal(1000.0 / 304.8, m.OriginOffsetXFeet.Value, 9);
            Assert.Throws<ArgumentException>(() =>
                PdfPrintPolicy.Parse(new JObject { ["placement"] = "lower_left", ["zoom"] = "zoom", ["zoom_percentage"] = 50, ["origin_offset_x"] = 1, ["origin_offset_y"] = 2 }, "inches", 2026));
        }
    
        [Fact]
        public void Default_page_at_the_declared_titleblock_is_verified_when_the_outline_is_larger()
        {
            // Measured live 2026-10-18, Revit 2024, Snowdon cover G000: outline 938 x 1067 mm,
            // titleblock 762 x 1067 mm, Revit printed the page at 762 x 1067 mm.
            const double pt = 72.0 / 25.4;
            PdfPrintPolicy p = PdfPrintPolicy.Parse(null, "mm", 2024);
            JObject v = p.VerifyPage(1066.88 * pt, 762.0 * pt, new[] { 938.3 * pt, 1066.8 * pt }, 2.0, new[] { 762.0 * pt, 1066.8 * pt });
            Assert.Equal("verified", (string)v["paper_format"]);
            Assert.Contains("titleblock", (string)v["paper_format_reason"]);
            Assert.DoesNotContain("was not printed", (string)v["paper_format_reason"]);
            JObject bad = p.VerifyPage(900 * pt, 700 * pt, new[] { 938.3 * pt, 1066.8 * pt }, 2.0, new[] { 762.0 * pt, 1066.8 * pt });
            Assert.NotEqual("verified", (string)bad["paper_format"]);
        }
}
}
