// 4.2-N4 (sheets and PDF): the Revit-free halves, without Revit.
//   layout_grid / set_view_scale / rename   SheetGridRules + SheetLayoutRules.Validate
//   PDF QA                                  PdfQa (fonts, blank pages, collisions, frame) on synthetic facts and on a real file
//   delivery                                ExportDeliveryRules (folder output, versions, archive, parts, token life)
//   pattern blockers                        PatternBlockerRules
//   family/type selectors                   ViewVisibilityRules
//   room isometric                          RoomIsometricRules
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using Xunit;

namespace Horizun.Core.Tests
{
    public class SheetGridRulesTests
    {
        private const double Mm = 1.0 / 304.8;
        private static double[] R(double a, double b, double c, double d) => new[] { a * Mm, b * Mm, c * Mm, d * Mm };
        private static GridItem I(string k, double[] crop, double[] label) => new GridItem { Key = k, Crop = crop, Label = label };

        [Fact]
        public void A_grid_puts_each_crop_at_its_cell_and_each_title_a_fixed_distance_below_it()
        {
            // two columns; the crops sit anywhere on the sheet, their titles anywhere near them
            var items = new List<GridItem>
            {
                I("a", R(500, 500, 600, 580), R(450, 480, 560, 495)),
                I("b", R(10, 10, 130, 90), R(10, 0, 100, 9)),
                I("c", R(300, 300, 400, 360), R(300, 290, 380, 299))
            };
            double[] usable = R(0, 0, 400, 300);
            var placed = SheetGridRules.Grid(items, usable, 2, 10 * Mm, 20 * Mm, 5 * Mm, 0.5 * Mm, out string refusal);
            Assert.Null(refusal);
            foreach (GridPlacement p in placed.Values)
                Assert.Equal(5 * Mm, SheetGridRules.TitleDistance(p.Crop, p.Label), 9);     // fixed distance, whatever the box did
            Assert.Equal(placed["a"].Crop[3], placed["b"].Crop[3], 9);                        // same row: same top
            Assert.Equal(placed["a"].Crop[0], placed["c"].Crop[0], 9);                        // same column: same left
            Assert.True(placed["c"].Crop[3] < placed["a"].Crop[1]);                          // next row is below the first
            Assert.True(placed["b"].Crop[0] > placed["a"].Crop[2]);                          // next column is right of the first
            Assert.All(placed.Values, p => Assert.True(SheetLayoutRules.Inside(usable, SheetLayoutRules.Union(p.Crop, p.Label), 0.5 * Mm)));
            var rects = placed.Select(kv => new KeyValuePair<string, double[]>(kv.Key, SheetLayoutRules.Union(kv.Value.Crop, kv.Value.Label))).ToList();
            Assert.Empty(SheetLayoutRules.Overlaps(rects, 0.5 * Mm));
        }

        [Fact]
        public void The_label_delta_makes_up_only_what_the_move_does_not_carry()
        {
            var items = new List<GridItem> { I("a", R(100, 100, 200, 180), R(100, 80, 190, 95)) };
            var p = SheetGridRules.Grid(items, R(0, 0, 400, 300), 1, 0, 0, 5 * Mm, 0.5 * Mm, out _)["a"];
            // after the viewport moves by p.Move the title is at label + move; the delta closes the rest
            double afterMoveLeft = items[0].Label[0] + p.Move[0] + p.LabelDelta[0];
            double afterMoveBottom = items[0].Label[1] + p.Move[1] + p.LabelDelta[1];
            Assert.Equal(p.Label[0], afterMoveLeft, 9);
            Assert.Equal(p.Label[1], afterMoveBottom, 9);
        }

        [Fact]
        public void A_viewport_without_a_title_is_placed_by_its_crop_alone()
        {
            var p = SheetGridRules.Grid(new List<GridItem> { I("a", R(0, 0, 50, 50), null) }, R(0, 0, 100, 100), 1, 0, 0, 5 * Mm, 0.5 * Mm, out _)["a"];
            Assert.Null(p.Label);
            Assert.Null(p.LabelDelta);
        }

        [Fact]
        public void A_grid_that_does_not_fit_is_refused_whole_with_the_size_it_needed()
        {
            var items = Enumerable.Range(0, 4).Select(i => I("v" + i, R(0, 0, 200, 150), R(0, 0, 100, 10))).ToList();
            Assert.Null(SheetGridRules.Grid(items, R(0, 0, 300, 400), 2, 10 * Mm, 10 * Mm, 5 * Mm, 0.5 * Mm, out string wide));
            Assert.Contains("column(s) need", wide);
            Assert.Null(SheetGridRules.Grid(items, R(0, 0, 450, 300), 2, 10 * Mm, 10 * Mm, 5 * Mm, 0.5 * Mm, out string tall));
            Assert.Contains("row(s) need", tall);
            Assert.Null(SheetGridRules.Grid(items, R(0, 0, 450, 400), 13, 0, 0, 0, 0.5 * Mm, out string cols));
            Assert.Contains("columns must be", cols);
        }

        [Fact]
        public void A_viewport_with_no_readable_crop_is_refused_not_guessed()
        {
            Assert.Null(SheetGridRules.Grid(new List<GridItem> { I("x", null, null) }, R(0, 0, 100, 100), 1, 0, 0, 0, 0.5 * Mm, out string why));
            Assert.Contains("no readable crop", why);
        }

        [Fact]
        public void Changing_the_scale_gives_the_crop_centre_back()
        {
            double[] shift = SheetGridRules.KeepCentre(R(100, 100, 200, 160), R(80, 90, 220, 170));
            Assert.Equal(0, 100 * Mm + 0 - (80 * Mm + 140 * Mm / 2 - 50 * Mm) - 0 + shift[0] * 0, 9); // sanity: arithmetic below is the assertion
            double cx = (80 * Mm + 220 * Mm) / 2 + shift[0], cy = (90 * Mm + 170 * Mm) / 2 + shift[1];
            Assert.Equal(150 * Mm, cx, 9);
            Assert.Equal(130 * Mm, cy, 9);
        }

        [Theory]
        [InlineData  (0)]
        [InlineData (90)]
        [InlineData(180)]
        [InlineData(270)]
        public void Synthetic_rotated_crops_keep_the_paper_centre_and_title_gap(int degrees)
        {
            double angle = degrees * Math.PI / 180.0;
            double c = Math.Cos(angle), s = Math.Sin(angle);
            double[] Bounds(double width, double height, double x, double y)
            {
                var points = new[] { (0.0, 0.0), (width, 0.0), (width, height), (0.0, height) }
                    .Select(p => (X: x + c * p.Item1 - s * p.Item2, Y: y + s * p.Item1 + c * p.Item2)).ToArray();
                return R(points.Min(p => p.X), points.Min(p => p.Y), points.Max(p => p.X), points.Max(p => p.Y));
            }
            var before = Bounds(100, 60, 200, 150);
            var after = Bounds(50, 30, 230, 170);
            var shift = SheetGridRules.KeepCentre(before, after);
            var a = SheetLayoutRules.Center(before);
            var b = SheetLayoutRules.Center(after);
            Assert.True(Math.Abs(a[0] - b[0] - shift[0]) <= SheetLayoutRules.ToleranceFeet);
            Assert.True(Math.Abs(a[1] - b[1] - shift[1]) <= SheetLayoutRules.ToleranceFeet);
            var placement = SheetGridRules.Grid(new[] { I("v", before, R(0, 0, 40, 8)) }, R(-500, -500, 500, 500), 1, 0, 0, 7 * Mm, SheetLayoutRules.ToleranceFeet, out var refusal)["v"];
            Assert.Null(refusal);
            Assert.Equal(7 * Mm, SheetGridRules.TitleDistance(placement.Crop, placement.Label), 9);
        }

        [Theory]
        [InlineData(0, false)] [InlineData(1, true)] [InlineData(24000, true)] [InlineData(24001, false)]
        public void A_scale_is_one_to_n_within_revits_range(long n, bool ok) => Assert.Equal(ok, SheetGridRules.ValidScale(n));

        [Fact]
        public void A_title_that_sticks_out_of_its_viewport_does_not_fit()
        {
            Assert.True(SheetGridRules.TitleFits(R(0, 0, 100, 80), R(0, -10, 90, -2), 0.5 * Mm));
            Assert.False(SheetGridRules.TitleFits(R(0, 0, 100, 80), R(0, -10, 140, -2), 0.5 * Mm));
            Assert.False(SheetGridRules.TitleFits(R(0, 0, 100, 80), null, 0.5 * Mm));
        }

        [Fact]
        public void Renames_that_collide_are_refused_before_revit_is_touched()
        {
            var wanted = new List<KeyValuePair<string, string>> { new KeyValuePair<string, string>("1", "Planta 1"), new KeyValuePair<string, string>("2", "planta 1") };
            Assert.Contains("twice", SheetGridRules.RenameConflict(wanted, new string[0], "view"));
            var one = new List<KeyValuePair<string, string>> { new KeyValuePair<string, string>("1", "Detalle A") };
            Assert.Contains("already exists", SheetGridRules.RenameConflict(one, new[] { "detalle a" }, "view"));
            Assert.Null(SheetGridRules.RenameConflict(one, new[] { "Detalle B" }, "view"));
            Assert.Contains("characters", SheetGridRules.RenameConflict(new List<KeyValuePair<string, string>> { new KeyValuePair<string, string>("1", "A{1}") }, new string[0], "view"));
            Assert.Contains("empty", SheetGridRules.RenameConflict(new List<KeyValuePair<string, string>> { new KeyValuePair<string, string>("1", " ") }, new string[0], "view"));
        }

        [Fact]
        public void The_new_sheet_layout_operations_validate_their_own_arguments()
        {
            Assert.Null(SheetLayoutRules.Validate(JObject.Parse("{operation:'layout_grid',sheet_id:5,columns:3,title_gap:5}")));
            Assert.Contains("columns", SheetLayoutRules.Validate(JObject.Parse("{operation:'layout_grid',sheet_id:5}")));
            Assert.Contains("columns", SheetLayoutRules.Validate(JObject.Parse("{operation:'layout_grid',sheet_id:5,columns:13}")));
            Assert.Contains("not applicable", SheetLayoutRules.Validate(JObject.Parse("{operation:'layout_grid',sheet_id:5,columns:2,scale:50}")));
            Assert.Null(SheetLayoutRules.Validate(JObject.Parse("{operation:'set_view_scale',viewport_id:9,scale:50}")));
            Assert.Null(SheetLayoutRules.Validate(JObject.Parse("{operation:'set_view_scale',viewport_id:9}")));
            Assert.Contains("scale", SheetLayoutRules.Validate(JObject.Parse("{operation:'set_view_scale',viewport_id:9,scale:0}")));
            Assert.Contains("boolean", SheetLayoutRules.Validate(JObject.Parse("{operation:'set_view_scale',viewport_id:9,scale:50,keep_viewport_center:'yes'}")));
            Assert.Null(SheetLayoutRules.Validate(JObject.Parse("{operation:'rename',items:[{view_id:1,name:'A'},{sheet_id:2,name:'B',sheet_number:'A-101'}]}")));
            Assert.Contains("exactly one", SheetLayoutRules.Validate(JObject.Parse("{operation:'rename',items:[{view_id:1,sheet_id:2,name:'A'}]}")));
            Assert.Contains("sheet_number belongs", SheetLayoutRules.Validate(JObject.Parse("{operation:'rename',items:[{view_id:1,name:'A',sheet_number:'X'}]}")));
            Assert.Contains("rename needs items", SheetLayoutRules.Validate(JObject.Parse("{operation:'rename'}")));
        }
    }

    public class PdfQaTests
    {
        private static PdfWordFact W(string t, double l, double b, double r, double top) => new PdfWordFact { Text = t, Left = l, Bottom = b, Right = r, Top = top };
        private static PdfPageFacts Page(int n, params PdfWordFact[] words)
        {
            var p = new PdfPageFacts { Page = n, Width = 842, Height = 595, Paths = 10 };
            p.Words.AddRange(words);
            return p;
        }

        [Theory]
        [InlineData("ABCDEF+Arial-BoldMT", "arial")]
        [InlineData("ArialMT", "arial")]
        [InlineData("ArialNarrow", "arialnarrow")]
        [InlineData("Arial Narrow", "arialnarrow")]
        [InlineData("Helvetica", "helvetica")]
        [InlineData("Calibri,Italic", "calibri")]
        public void A_font_is_compared_by_family_not_by_subset_tag_or_style(string raw, string key) => Assert.Equal(key, PdfQa.FontKey(raw));

        [Fact]
        public void A_missing_expected_family_with_another_in_its_place_is_a_substitution()
        {
            var page = Page(1, W("A101", 10, 10, 40, 20));
            page.Fonts["ArialMT"] = 400;
            JObject r = PdfQa.Judge(new[] { page }, new PdfQaOptions { ExpectedFonts = { "Arial Narrow" } });
            Assert.Equal("findings", r.Value<string>("verdict"));
            Assert.Equal("Arial Narrow", r["fonts"]["substituted"][0].Value<string>("expected"));
            Assert.Equal("ArialMT", r["fonts"]["substituted"][0]["replaced_by"][0].Value<string>());
            Assert.True(r["fonts"].Value<bool>("judged_against_expected"));
        }

        [Fact]
        public void Without_expected_fonts_the_fonts_are_reported_and_not_judged()
        {
            var page = Page(1, W("A", 10, 10, 20, 20));
            page.Fonts["ArialMT"] = 5;
            JObject r = PdfQa.Judge(new[] { page }, new PdfQaOptions());
            Assert.Equal("clean", r.Value<string>("verdict"));
            Assert.False(r["fonts"].Value<bool>("judged_against_expected"));
            Assert.Single(r["fonts"]["used"]);
        }

        [Fact]
        public void The_expected_family_alone_is_clean()
        {
            var page = Page(1, W("A", 10, 10, 20, 20));
            page.Fonts["ABCDEF+ArialNarrow"] = 5;
            Assert.Equal("clean", PdfQa.Judge(new[] { page }, new PdfQaOptions { ExpectedFonts = { "Arial Narrow" } }).Value<string>("verdict"));
        }

        [Fact]
        public void A_page_with_no_text_no_image_and_no_drawing_is_blank()
        {
            var blank = new PdfPageFacts { Page = 2, Width = 842, Height = 595 };
            JObject r = PdfQa.Judge(new[] { Page(1, W("A", 10, 10, 20, 20)), blank }, new PdfQaOptions());
            Assert.Equal(2, r["blank_pages"][0].Value<int>());
            Assert.Equal("findings", r.Value<string>("verdict"));
        }

        [Fact]
        public void Two_words_that_cross_collide_and_two_that_only_touch_do_not()
        {
            var crossing = Page(1, W("N-1", 100, 100, 140, 110), W("12.5", 120, 102, 150, 112));
            var touching = Page(1, W("N-1", 100, 100, 140, 110), W("12.5", 140, 100, 170, 110));
            var stacked = Page(1, W("N-1", 100, 100, 140, 110), W("12.5", 100, 110, 140, 120));
            Assert.Single(PdfQa.Overlaps(crossing, new PdfQaOptions()));
            Assert.Empty(PdfQa.Overlaps(touching, new PdfQaOptions()));
            Assert.Empty(PdfQa.Overlaps(stacked, new PdfQaOptions()));
            JObject r = PdfQa.Judge(new[] { crossing }, new PdfQaOptions());
            Assert.Equal(1, r["text_overlaps"].Value<int>("count"));
            Assert.Equal("findings", r.Value<string>("verdict"));
        }

        [Fact]
        public void Text_beyond_the_page_or_inside_the_margin_is_outside_the_frame()
        {
            var p = Page(1, W("ok", 100, 100, 130, 110), W("edge", 5, 100, 30, 110), W("off", 830, 100, 860, 110));
            Assert.Single(PdfQa.OutsideFrame(p, new PdfQaOptions { FrameMarginPoints = 0 }));
            Assert.Equal(2, PdfQa.OutsideFrame(p, new PdfQaOptions { FrameMarginPoints = 10 }).Count);
        }

        [Fact]
        public void The_options_are_closed_and_checked()
        {
            Assert.NotNull(PdfQa.Parse(JObject.Parse("{expected_fonts:['Arial Narrow'],frame_margin_mm:5}"), out string e1));
            Assert.Null(e1);
            Assert.Null(PdfQa.Parse(JObject.Parse("{fonts:['x']}"), out string e2));
            Assert.Contains("not applicable", e2);
            Assert.Null(PdfQa.Parse(JObject.Parse("{expected_fonts:[1]}"), out string e3));
            Assert.Null(PdfQa.Parse(JObject.Parse("{frame_margin_mm:-1}"), out string e4));
            Assert.NotNull(e4);
        }

        [Fact]
        public void A_real_pdf_is_read_back_for_its_fonts_blank_pages_and_text()
        {
            var builder = new PdfDocumentBuilder();
            PdfDocumentBuilder.AddedFont font = builder.AddStandard14Font(Standard14Font.Helvetica);
            PdfPageBuilder page1 = builder.AddPage(595, 842);
            page1.AddText("Plano A-101", 12, new PdfPoint(50, 700), font);
            page1.AddText("OVERLAP", 12, new PdfPoint(52, 701), font);
            builder.AddPage(595, 842);                                   // nothing on it
            string path = Path.Combine(Path.GetTempPath(), "hz-n4-qa-" + Guid.NewGuid().ToString("N") + ".pdf");
            try
            {
                File.WriteAllBytes(path, builder.Build());
                JObject r = PdfQa.Inspect(path, new PdfQaOptions { ExpectedFonts = { "Arial Narrow" } });
                Assert.Equal(2, r.Value<int>("pages"));
                Assert.Equal(2, r["blank_pages"][0].Value<int>());
                Assert.True(r["text_overlaps"].Value<int>("count") >= 1);
                Assert.Equal("Arial Narrow", r["fonts"]["missing_expected"][0].Value<string>());
                Assert.False(r.Value<bool>("render_checked"));
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        }
    }

    public class ExportDeliveryRulesTests
    {
        [Fact]
        public void A_path_without_pdf_extension_or_with_a_trailing_separator_is_a_folder()
        {
            Assert.True(ExportDeliveryRules.LooksLikeFolder(@"C:\out\set", null));
            Assert.True(ExportDeliveryRules.LooksLikeFolder(@"C:\out\set\", null));
            Assert.False(ExportDeliveryRules.LooksLikeFolder(@"C:\out\set.pdf", null));
            Assert.True(ExportDeliveryRules.LooksLikeFolder(@"C:\out\set.v2", p => true));
            Assert.False(ExportDeliveryRules.LooksLikeFolder(@"C:\out\set.v2", p => false));
        }

        [WindowsFact]
        public void A_folder_output_takes_the_folders_name_as_stem()
            => Assert.Equal(@"C:\out\emision\emision.pdf", ExportDeliveryRules.FolderStem(@"C:\out\emision\"));

        [Fact]
        public void Auto_version_is_one_past_the_highest_found_under_the_same_pattern()
        {
            string pattern = "{sheet_number}_{date}_v{version}";
            var files = new[] { @"C:\o\A101_20261020_v1.pdf", @"C:\o\A101_20261025_v2.pdf", @"C:\o\A102_20261025_v2.pdf", @"C:\o\otro.pdf", @"C:\o\A101_20261025_v10.pdf" };
            Assert.Equal(11, ExportDeliveryRules.NextVersion(pattern, files));
            Assert.Equal(1, ExportDeliveryRules.NextVersion(pattern, new[] { @"C:\o\otro.pdf" }));
            Assert.Throws<ArgumentException>(() => ExportDeliveryRules.NextVersion("{sheet_number}", files));
        }

        [Fact]
        public void The_older_versions_are_the_ones_below_the_new_one_and_nothing_else()
        {
            string pattern = "{sheet_number}_v{version}.pdf";
            var files = new[] { @"C:\o\A1_v1.pdf", @"C:\o\A1_v2.pdf", @"C:\o\A1_v3.pdf", @"C:\o\notes.pdf" };
            var older = ExportDeliveryRules.PreviousVersions(pattern, files, 3);
            Assert.Equal(new[] { @"C:\o\A1_v1.pdf", @"C:\o\A1_v2.pdf" }, older);
        }

        [Fact]
        public void The_archive_folder_is_a_plain_name()
        {
            Assert.Null(ExportDeliveryRules.ArchiveFolderError("_anteriores"));
            Assert.NotNull(ExportDeliveryRules.ArchiveFolderError(@"..\x"));
            Assert.NotNull(ExportDeliveryRules.ArchiveFolderError(".."));
        }

        [Fact]
        public void Parts_are_contiguous_and_balanced()
        {
            int[][] parts = ExportDeliveryRules.Split(10, 3);
            Assert.Equal(new[] { 4, 3, 3 }, parts.Select(p => p.Length).ToArray());
            Assert.Equal(Enumerable.Range(0, 10), parts.SelectMany(p => p));
        }

        [Fact]
        public void The_jobs_say_which_file_holds_which_sheets()
        {
            string[] perSheet = { @"C:\o\s-001-1.pdf", @"C:\o\s-002-2.pdf", @"C:\o\s-003-3.pdf" };
            PdfJobs combined = ExportDeliveryRules.Jobs(@"C:\o\s.pdf", perSheet, 3, true, 0, false);
            Assert.Equal(new[] { @"C:\o\s.pdf" }, combined.Paths);
            Assert.Equal(new[] { 0, 1, 2 }, combined.Groups[0]);

            PdfJobs separate = ExportDeliveryRules.Jobs(@"C:\o\s.pdf", perSheet, 3, false, 0, false);
            Assert.Equal(perSheet, separate.Paths);
            Assert.Equal(new[] { 1 }, separate.Groups[1]);

            PdfJobs both = ExportDeliveryRules.Jobs(@"C:\o\s.pdf", perSheet, 3, true, 0, true);
            Assert.Equal(4, both.Paths.Length);
            Assert.Equal(@"C:\o\s-set.pdf", both.Paths[0]);
            Assert.Equal(new[] { 0, 1, 2 }, both.Groups[0]);

            PdfJobs split = ExportDeliveryRules.Jobs(@"C:\o\s.pdf", perSheet, 3, true, 2, false);
            Assert.Equal(new[] { @"C:\o\s-parte-1-de-2.pdf", @"C:\o\s-parte-2-de-2.pdf" }, split.Paths);
            Assert.Equal(new[] { 2 }, split.Groups[1].Length == 1 ? split.Groups[1] : new[] { -1 });
        }

        [Fact]
        public void Incompatible_delivery_options_are_refused_before_anything_is_exported()
        {
            Assert.Contains("pdf_parts", ExportDeliveryRules.ValidateJobOptions(false, 2, false, 5));
            Assert.Contains("pdf_parts", ExportDeliveryRules.ValidateJobOptions(true, 1, false, 5));
            Assert.Contains("pdf_parts", ExportDeliveryRules.ValidateJobOptions(true, 6, false, 5));
            Assert.Contains("pdf_also_per_sheet", ExportDeliveryRules.ValidateJobOptions(false, 0, true, 5));
            Assert.Contains("not both", ExportDeliveryRules.ValidateJobOptions(true, 2, true, 5));
            Assert.Null(ExportDeliveryRules.ValidateJobOptions(true, 0, false, 5));
        }

        [Fact]
        public void Two_jobs_writing_the_same_file_are_refused()
        {
            Assert.Throws<ArgumentException>(() => ExportDeliveryRules.Jobs(@"C:\o\s.pdf", new[] { @"C:\o\x.pdf", @"C:\o\X.pdf" }, 2, false, 0, false));
        }

        [Fact]
        public void A_big_set_gets_a_longer_approval_up_to_an_hour()
        {
            Assert.Equal(TimeSpan.FromMinutes(10), ExportDeliveryRules.TokenTtl(0));
            Assert.Equal(TimeSpan.FromMinutes(25), ExportDeliveryRules.TokenTtl(30));
            Assert.Equal(TimeSpan.FromMinutes(60), ExportDeliveryRules.TokenTtl(5000));
        }
    }

    public class PatternBlockerRulesTests
    {
        [Fact]
        public void A_pattern_set_but_not_visible_is_named_with_the_setting_to_open()
        {
            var reasons = PatternBlockerRules.Explain(new OverrideFacts { SurfaceForegroundPatternSet = true, SurfaceForegroundVisible = false });
            Assert.Equal("surface_foreground_off", reasons.Single().Value<string>("code"));
            Assert.Contains("Foreground", reasons.Single().Value<string>("setting"));
        }

        [Fact]
        public void A_colour_on_no_pattern_halftone_and_transparency_each_weaken_it()
        {
            var codes = PatternBlockerRules.Explain(new OverrideFacts { SurfaceForegroundColorSet = true, Halftone = true, Transparency = 40 }).Select(r => r.Value<string>("code")).ToList();
            Assert.Contains("colour_on_no_pattern", codes);
            Assert.Contains("halftone", codes);
            Assert.Contains("transparency", codes);
            Assert.Equal("transparency_full", PatternBlockerRules.Explain(new OverrideFacts { Transparency = 100 }).Single().Value<string>("code"));
        }

        [Fact]
        public void An_untouched_override_has_no_reason() => Assert.Empty(PatternBlockerRules.Explain(new OverrideFacts()));

        [Fact]
        public void The_view_can_be_the_reason()
        {
            Assert.Equal("view_wireframe", PatternBlockerRules.ExplainView(new ViewPatternFacts { DisplayStyle = "Wireframe" }).Single().Value<string>("code"));
            Assert.Equal("view_template_controls_graphics",
                PatternBlockerRules.ExplainView(new ViewPatternFacts { DisplayStyle = "Shading", OverridesAllowed = false, TemplateName = "PLANTA", TemplateControlsGraphics = true })
                    .Single(r => r.Value<string>("code").StartsWith("view_template")).Value<string>("code"));
            Assert.Empty(PatternBlockerRules.ExplainView(new ViewPatternFacts { DisplayStyle = "Shading" }));
        }
    }

    public class ViewVisibilitySelectorTests
    {
        private static readonly string[] Door = { "M_Door-Single" };

        [Fact]
        public void A_family_selects_all_its_types_and_a_type_name_narrows_it()
        {
            Assert.True(ViewVisibilityRules.SelectsType("M_Door-Single", "0915 x 2134", Door, null));
            Assert.False(ViewVisibilityRules.SelectsType("Basic Wall", "Generic - 200", Door, null));
            Assert.True(ViewVisibilityRules.SelectsType("m_door-single", "0915 x 2134", Door, new[] { "0915 x 2134" }));
            Assert.False(ViewVisibilityRules.SelectsType("M_Door-Single", "1000 x 2134", Door, new[] { "0915 x 2134" }));
            Assert.True(ViewVisibilityRules.SelectsType("Basic Wall", "Generic - 200", null, new[] { "generic - 200" }));
        }

        [Fact]
        public void Nothing_named_selects_nothing() => Assert.False(ViewVisibilityRules.SelectsType("Basic Wall", "x", null, null));

        [Fact]
        public void Batches_by_family_or_type_validate_and_explain_overrides_is_read_only()
        {
            Assert.Null(ViewVisibilityRules.Validate(JObject.Parse("{operation:'set_overrides',view_id:5,family_names:['Basic Wall'],overrides:{halftone:true}}")));
            Assert.Null(ViewVisibilityRules.Validate(JObject.Parse("{operation:'hide',view_id:5,type_names:['Generic - 200']}")));
            Assert.Contains("needs element_ids", ViewVisibilityRules.Validate(JObject.Parse("{operation:'hide',view_id:5}")));
            Assert.Contains("family_names", ViewVisibilityRules.Validate(JObject.Parse("{operation:'hide',view_id:5,family_names:[]}")));
            Assert.Contains("ONE family", ViewVisibilityRules.Validate(JObject.Parse("{operation:'hide',view_id:5,family_names:['a','b'],type_names:['t']}")));
            Assert.Null(ViewVisibilityRules.Validate(JObject.Parse("{operation:'explain_overrides',view_id:5,categories:['OST_Walls']}")));
            Assert.Contains("explain_overrides needs", ViewVisibilityRules.Validate(JObject.Parse("{operation:'explain_overrides',view_id:5}")));
            Assert.Contains("only reads", ViewVisibilityRules.Validate(JObject.Parse("{operation:'explain_overrides',view_id:5,categories:['OST_Walls'],overrides:{halftone:true}}")));
        }
    }

    public class RoomIsometricRulesTests
    {
        [Theory]
        [InlineData("ne", -1, -1)] [InlineData("nw", 1, -1)] [InlineData("se", -1, 1)] [InlineData("sw", 1, 1)]
        public void The_viewer_looks_from_the_corner_toward_the_room_and_down(string dir, int sx, int sy)
        {
            double[] f = RoomIsometricRules.Forward(dir), u = RoomIsometricRules.Up(dir);
            Assert.Equal(sx, Math.Sign(f[0]));
            Assert.Equal(sy, Math.Sign(f[1]));
            Assert.True(f[2] < 0);
            Assert.Equal(1.0, Math.Sqrt(f.Sum(v => v * v)), 9);
            Assert.Equal(1.0, Math.Sqrt(u.Sum(v => v * v)), 9);
            Assert.Equal(0.0, f[0] * u[0] + f[1] * u[1] + f[2] * u[2], 9);      // up is perpendicular to the sight line
            Assert.True(u[2] > 0);
        }

        [Fact]
        public void The_box_is_the_room_grown_by_the_margin_on_every_side()
        {
            double[][] box = RoomIsometricRules.Box(new[] { 0.0, 0, 0 }, new[] { 4.0, 3, 2.5 }, 1);
            Assert.Equal(new[] { -1.0, -1, -1 }, box[0]);
            Assert.Equal(new[] { 5.0, 4, 3.5 }, box[1]);
            Assert.True(RoomIsometricRules.Contains(box, new[] { new[] { 0.0, 0, 0 }, new[] { 4.0, 3, 2.5 } }, 0));
            Assert.False(RoomIsometricRules.Contains(new[] { new[] { 0.0, 0, 0 }, new[] { 4.0, 3, 2.0 } }, new[] { new[] { 0.0, 0, 0 }, new[] { 4.0, 3, 2.5 } }, 0.001));
        }

        [Fact]
        public void A_room_without_extent_is_refused()
        {
            Assert.Throws<ArgumentException>(() => RoomIsometricRules.Box(new[] { 0.0, 0, 0 }, new[] { 4.0, 3, 0 }, 1));
            Assert.Throws<ArgumentException>(() => RoomIsometricRules.Box(null, null, 1));
            Assert.Throws<ArgumentException>(() => RoomIsometricRules.Forward("up"));
        }

        [Fact]
        public void The_name_drops_what_revit_refuses_and_the_action_is_checked()
        {
            Assert.Equal("ISO 101 Sala-Comedor", RoomIsometricRules.DefaultName("101", "Sala[Comedor"));
            Assert.Null(RoomIsometricRules.Validate(JObject.Parse("{room_id:7,direction:'NE',margin:300}")));
            Assert.Contains("room_id", RoomIsometricRules.Validate(JObject.Parse("{direction:'ne'}")));
            Assert.Contains("direction", RoomIsometricRules.Validate(JObject.Parse("{room_id:7,direction:'north'}")));
            Assert.Contains("camera", RoomIsometricRules.Validate(JObject.Parse("{room_id:7,start:[0,0,0]}")));
        }
    }
}
