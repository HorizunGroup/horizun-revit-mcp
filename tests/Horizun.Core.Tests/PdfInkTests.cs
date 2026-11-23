// 4.2-N4 round 2 (F93): the ink of a PDF page and of named windows on it, without Revit.
using System;
using System.IO;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using Xunit;

namespace Horizun.Core.Tests
{
    public class PdfInkTests
    {
        private const double Pt = 72.0 / 25.4;

        [Fact]
        public void Hyphenated_family_is_not_mistaken_for_its_base_font()
        {
            Assert.Equal("arialnarrow", PdfQa.FontKey("ABCDEF+Arial-Narrow-BoldMT"));
            var page = new PdfPageFacts { Page = 1, Width = 100, Height = 100 };
            page.Fonts["ABCDEF+Arial-Narrow-BoldMT"] = 5;
            page.Words.Add(new PdfWordFact { Text = "note", Left = 10, Bottom = 10, Right = 30, Top = 20 });
            Assert.Equal("clean", PdfQa.Judge(new[] { page }, new PdfQaOptions { ExpectedFonts = { "Arial Narrow" } }).Value<string>("verdict"));
        }

        [Fact]
        public void The_ink_box_of_a_region_is_the_union_of_what_is_drawn_inside_it_and_says_how_far_it_sticks_out()
        {
            var page = new PdfPageFacts { Page = 1, Width = 842, Height = 595 };
            page.Ink.Add(new[] { 100.0, 100, 200, 150 });
            page.Ink.Add(new[] { 150.0, 120, 260, 190 });      // centre inside the region, extent beyond its right edge
            page.Ink.Add(new[] { 600.0, 400, 650, 420 });      // elsewhere
            var o = new PdfQaOptions();
            o.Regions.Add(new PdfRegion { Name = "vp-1", Rect = new[] { 90.0, 90, 250, 200 } });
            JObject ink = (JObject)PdfQa.Ink(new[] { page }, o)[0];
            Assert.Equal(3, ink.Value<int>("items"));
            JObject r = (JObject)ink["regions"][0];
            Assert.Equal(2, r.Value<int>("items"));
            Assert.Equal(100.0 / Pt, r["box_mm"][0].Value<double>(), 1);
            Assert.True(r["sticks_out_mm"][2].Value<double>() > 0);   // 260 pt vs 250 pt on the right
            Assert.Equal(0.0, r["sticks_out_mm"][0].Value<double>());
        }

        [Fact]
        public void A_region_with_no_ink_reports_nothing_rather_than_a_zero_box()
        {
            var page = new PdfPageFacts { Page = 1, Width = 842, Height = 595 };
            var o = new PdfQaOptions();
            o.Regions.Add(new PdfRegion { Name = "empty", Rect = new[] { 0.0, 0, 10, 10 } });
            JObject r = (JObject)PdfQa.Ink(new[] { page }, o)[0]["regions"][0];
            Assert.Equal(JTokenType.Null, r["box_mm"].Type);
            Assert.Equal(0, r.Value<int>("items"));
        }

        [Fact]
        public void The_regions_option_is_closed_and_checked()
        {
            PdfQaOptions ok = PdfQa.Parse(JObject.Parse("{regions:[{name:'a',rect_mm:[0,0,100,50]}]}"), out string e1);
            Assert.Null(e1);
            Assert.Single(ok.Regions);
            Assert.Equal(100 * Pt, ok.Regions[0].Rect[2], 6);
            Assert.Null(PdfQa.Parse(JObject.Parse("{regions:[{name:'a',rect_mm:[10,0,5,50]}]}"), out string e2));
            Assert.NotNull(e2);
            Assert.Null(PdfQa.Parse(JObject.Parse("{regions:[{rect_mm:[0,0,5,5]}]}"), out string e3));
            Assert.NotNull(e3);
        }

        [Fact]
        public void A_real_pdf_gives_a_page_ink_box_and_the_region_that_holds_its_text()
        {
            var builder = new PdfDocumentBuilder();
            var font = builder.AddStandard14Font(Standard14Font.Helvetica);
            builder.AddPage(595, 842).AddText("Detalle D-01", 12, new PdfPoint(100, 500), font);
            string path = Path.Combine(Path.GetTempPath(), "hz-n4-ink-" + Guid.NewGuid().ToString("N") + ".pdf");
            try
            {
                File.WriteAllBytes(path, builder.Build());
                var o = new PdfQaOptions();
                o.Regions.Add(new PdfRegion { Name = "text", Rect = new[] { 90.0, 490, 300, 520 } });
                JObject report = PdfQa.Inspect(path, o);
                JObject ink = (JObject)report["ink"][0];
                Assert.True(ink.Value<int>("items") >= 1);
                Assert.NotEqual(JTokenType.Null, ink["box_mm"].Type);
                Assert.True(ink["regions"][0].Value<int>("items") >= 1);
                Assert.Contains("rasteriser", report.Value<string>("render_limit"));
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        }
    }
}
