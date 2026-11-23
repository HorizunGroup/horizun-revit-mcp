// horizun_sheet_layout (FIELD FEEDBACK 2.5): usable area, rows with aligned titles, overlaps, crop anchor - without Revit.
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class SheetLayoutRulesTests
    {
        private const double Mm = 1.0 / 304.8;
        private static double[] R(double a, double b, double c, double d) => new[] { a * Mm, b * Mm, c * Mm, d * Mm };
        private static LayoutItem I(string k, double w, double h) => new LayoutItem { Key = k, Width = w * Mm, Height = h * Mm };

        [Fact]
        public void The_usable_area_is_the_title_block_minus_margins_and_the_notes_strip()
        {
            double[] u = SheetLayoutRules.Usable(R(0, 0, 841, 594), new[] { 20 * Mm, 10 * Mm, 10 * Mm, 10 * Mm }, 180 * Mm);
            Assert.Equal(new[] { 20.0, 10, 651, 584 }, u.Select(v => System.Math.Round(v / Mm, 6)).ToArray());
        }

        [Fact]
        public void A_rectangle_inside_has_no_protrusion_and_one_outside_says_by_how_much_on_each_side()
        {
            double[] area = R(10, 10, 700, 584);
            Assert.Null(SheetLayoutRules.Protrusion(area, R(20, 20, 300, 300), 0.5 * Mm));
            Assert.Null(SheetLayoutRules.Protrusion(area, R(10.2, 10, 700.3, 584), 0.5 * Mm));
            double[] p = SheetLayoutRules.Protrusion(area, R(600, 5, 760, 300), 0.5 * Mm);
            Assert.Equal(new[] { 0.0, 60, 0, 5 }, p.Select(v => System.Math.Round(v / Mm, 6)).ToArray());
            Assert.Null(SheetLayoutRules.Protrusion(null, R(0, 0, 1, 1), 0));
        }

        [Fact]
        public void Margins_that_leave_nothing_are_refused()
            => Assert.Throws<System.ArgumentException>(() => SheetLayoutRules.Usable(R(0, 0, 100, 100), new[] { 60 * Mm, 60 * Mm, 0, 0 }, 0));

        [Fact]
        public void Rows_fill_left_to_right_with_bottoms_aligned_and_no_overlap()
        {
            double[] usable = R(0, 0, 400, 300);
            var items = new List<LayoutItem> { I("a", 150, 100), I("b", 150, 60), I("c", 150, 80) };
            var placed = SheetLayoutRules.Rows(items, usable, 10 * Mm, 0.5 * Mm, out string refusal);
            Assert.Null(refusal);
            // a and b share the first row and its bottom (titles aligned); c wraps to the next row
            Assert.Equal(placed["a"][1], placed["b"][1], 9);
            Assert.Equal(160 * Mm, placed["b"][0], 9);
            Assert.Equal(0, placed["c"][0], 9);
            Assert.Equal(placed["a"][1] - 10 * Mm, placed["c"][3], 9);
            Assert.Equal(300 * Mm, placed["a"][3], 9);
            var rects = placed.Select(kv => new KeyValuePair<string, double[]>(kv.Key, kv.Value)).ToList();
            Assert.Empty(SheetLayoutRules.Overlaps(rects, 0.5 * Mm));
            Assert.All(placed.Values, r => Assert.True(SheetLayoutRules.Inside(usable, r, 0.5 * Mm)));
        }

        [Fact]
        public void A_layout_that_does_not_fit_is_refused_whole_with_the_height_it_needs()
        {
            var placed = SheetLayoutRules.Rows(new List<LayoutItem> { I("a", 300, 200), I("b", 300, 200) }, R(0, 0, 400, 300), 10 * Mm, 0.5 * Mm, out string refusal);
            Assert.Null(placed);
            Assert.Contains("410", refusal);
            Assert.Contains("300", refusal);
        }

        [Fact]
        public void An_item_wider_than_the_area_is_refused()
        {
            Assert.Null(SheetLayoutRules.Rows(new List<LayoutItem> { I("a", 500, 10) }, R(0, 0, 400, 300), 10 * Mm, 0.5 * Mm, out string refusal));
            Assert.Contains("wide", refusal);
        }

        [Fact]
        public void Overlaps_are_named_by_pair_and_touching_is_not_an_overlap()
        {
            var rects = new List<KeyValuePair<string, double[]>>
            {
                new KeyValuePair<string, double[]>("1", R(0, 0, 100, 100)),
                new KeyValuePair<string, double[]>("2", R(100, 0, 200, 100)),
                new KeyValuePair<string, double[]>("3", R(50, 50, 150, 150))
            };
            List<string[]> o = SheetLayoutRules.Overlaps(rects, 0.5 * Mm);
            Assert.Equal(2, o.Count);
            Assert.DoesNotContain(o, p => p.Contains("1") && p.Contains("2"));
        }

        [Theory]
        [InlineData(55, 80)] // B goes from 5 mm of overlap to fully covered.
        [InlineData(50, 80)] // B remains partly visible, but the overlap still grows.
        public void Repair_rejects_growing_an_old_overlap(double otherLeft, double otherRight)
        {
            var before = new Dictionary<string, double[]>
            {
                ["A"] = R(75, 20, 120, 60), ["B"] = R(otherLeft, 20, otherRight, 60)
            };

            Assert.Null(SheetLayoutRules.RepairOutside(before.ToList(), R(0, 0, 100, 100), 0.5 * Mm, out string refusal));
            Assert.Contains("viewport A collides with viewport B", refusal);
        }

        [Theory]
        [InlineData(64, 55, false)] // Equal area, neither viewport covered.
        [InlineData(62, 55, false)] // Smaller area, neither viewport covered.
        [InlineData(63, 56, true)]  // Smaller area, but B is now fully covered.
        public void Repair_comparison_distinguishes_new_containment_from_ordinary_overlap(double right, double left, bool collision)
        {
            var before = new Dictionary<string, double[]>
            {
                ["A"] = R(56, 20, 95, 60), ["B"] = R(55, 20, 64, 60)
            };
            var after = new Dictionary<string, double[]>
            {
                ["B"] = R(left, 20, right, 60), ["A"] = before["A"]
            };

            var pairs = SheetLayoutRules.NewOverlaps(before.ToList(), after.ToList(), 0.5 * Mm);
            if (collision) Assert.Equal(new[] { "B", "A" }, Assert.Single(pairs));
            else Assert.Empty(pairs);
        }

        [Theory]
        [InlineData(0.4, 10, 0.8, 2, true)]
        [InlineData(0.4, 10, 0.6, 5, true)]
        [InlineData(10, 0.4, 2, 0.8, true)]
        [InlineData(0.5, 10, 0.8, 2, true)]
        [InlineData(0.4, 10, 0.5, 5, false)]
        [InlineData(0, 20, 0.4, 20, false)]
        public void Repair_detects_a_new_overlap_at_the_same_threshold_as_the_sheet_check(
            double oldWidth, double oldHeight, double width, double height, bool collision)
        {
            var before = new List<KeyValuePair<string, double[]>>
            {
                new KeyValuePair<string, double[]>("A", R(0, 0, 40, 40)),
                new KeyValuePair<string, double[]>("B", R(-20, -20, oldWidth, oldHeight))
            };
            var after = new List<KeyValuePair<string, double[]>>
            {
                before[0],
                new KeyValuePair<string, double[]>("B", R(-20, -20, width, height))
            };

            const double tolerance = SheetLayoutRules.ToleranceFeet;
            Assert.Empty(SheetLayoutRules.Overlaps(before, tolerance));
            var sheetPairs = SheetLayoutRules.Overlaps(after, tolerance);
            var repairPairs = SheetLayoutRules.NewOverlaps(before, after, tolerance);
            if (collision)
            {
                Assert.Equal(new[] { "A", "B" }, Assert.Single(sheetPairs));
                Assert.Equal(new[] { "A", "B" }, Assert.Single(repairPairs));
            }
            else
            {
                Assert.Empty(sheetPairs);
                Assert.Empty(repairPairs);
            }
        }

        [Theory]
        [InlineData(99.5, false)]
        [InlineData(99.5, true)]
        [InlineData(99.499999999, false)]
        [InlineData(99.499999999, true)]
        public void Repair_handles_overlap_at_the_threshold_after_subtracting_paper_coordinates(double edge, bool transpose)
        {
            double[] PaperRect(double x0, double y0, double x1, double y1)
                => transpose ? R(y0, x0, y1, x1) : R(x0, y0, x1, y1);

            var before = new Dictionary<string, double[]>
            {
                ["A"] = PaperRect(80, -20, 100, 80), ["B"] = PaperRect(edge, 90, 115, 98)
            };
            var after = new Dictionary<string, double[]>
            {
                ["A"] = PaperRect(80, 0, 100, 100), ["B"] = before["B"]
            };
            const double tolerance = SheetLayoutRules.ToleranceFeet;
            var repair = SheetLayoutRules.RepairOutside(before.ToList(), R(0, 0, 120, 120), tolerance, out string refusal);
            if (edge == 99.5)
            {
                Assert.Null(refusal);
                Assert.Single(repair);
                Assert.Empty(SheetLayoutRules.Overlaps(after.ToList(), tolerance));
                Assert.Empty(SheetLayoutRules.NewOverlaps(before.ToList(), after.ToList(), tolerance));
            }
            else
            {
                Assert.Null(repair);
                Assert.Contains("viewport A collides with viewport B", refusal);
                Assert.Equal(new[] { "A", "B" }, Assert.Single(SheetLayoutRules.Overlaps(after.ToList(), tolerance)));
                Assert.Equal(new[] { "A", "B" }, Assert.Single(SheetLayoutRules.NewOverlaps(before.ToList(), after.ToList(), tolerance)));
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Repair_ignores_area_roundoff_but_detects_measurable_growth_in_a_narrow_overlap(bool transpose)
        {
            double[] PaperRect(double x0, double y0, double x1, double y1)
                => transpose ? R(y0, x0, y1, x1) : R(x0, y0, x1, y1);

            var a = new KeyValuePair<string, double[]>("A", PaperRect(11, -20, 45, 80));
            var b = new KeyValuePair<string, double[]>("B", PaperRect(44.995, 50, 60, 60));
            var before = new[] { a, b };
            var shifted = PaperRect(11, 0, 45, 100);
            int rightEdge = transpose ? 3 : 2;
            shifted[rightEdge] = System.Math.BitIncrement(shifted[rightEdge]);
            var after = new[] { new KeyValuePair<string, double[]>("A", shifted), b };
            const double tolerance = SheetLayoutRules.ToleranceFeet;

            Assert.Empty(SheetLayoutRules.NewOverlaps(before, after, tolerance));

            shifted[rightEdge] += 1e-9 * Mm;
            Assert.Equal(new[] { "A", "B" }, Assert.Single(SheetLayoutRules.NewOverlaps(before, after, tolerance)));
        }

        [Fact]
        public void The_crop_anchor_moves_the_crop_centre_not_the_box_centre()
        {
            // the box is taller than the crop by 95 mm of grid bubbles above it (the reported offset)
            double[] crop = R(100, 100, 300, 250);
            double[] delta = SheetLayoutRules.Delta(crop, new[] { 420 * Mm, 297 * Mm }, false);
            Assert.Equal(220 * Mm, delta[0], 9);
            Assert.Equal(122 * Mm, delta[1], 9);
            double[] byCorner = SheetLayoutRules.Delta(crop, new[] { 20 * Mm, 20 * Mm }, true);
            Assert.Equal(-80 * Mm, byCorner[0], 9);
        }

        [Theory]
        [InlineData("{'operation':'get_viewport_box','sheet_id':1}", null)]
        [InlineData("{'operation':'get_viewport_box','viewport_id':1}", null)]
        [InlineData("{'operation':'get_viewport_box','sheet_id':1,'viewport_id':2}", "exactly one")]
        [InlineData("{'operation':'set_title','viewport_id':1,'label_offset':[0,-12]}", null)]
        [InlineData("{'operation':'set_title','viewport_ids':[1,2],'label_offset':[0,-12]}", null)]
        [InlineData("{'operation':'set_title','viewport_id':1,'viewport_ids':[2],'label_offset':[0,-12]}", "exactly one")]
        [InlineData("{'operation':'move_viewport','viewport_id':1,'center':[420,297]}", null)]
        [InlineData("{'operation':'move_viewport','viewport_id':1,'corner':[20,20],'anchor':'box'}", null)]
        [InlineData("{'operation':'move_viewport','viewport_id':1,'center':[1,2],'corner':[1,2]}", "exactly one")]
        [InlineData("{'operation':'move_viewport','viewport_id':1,'center':[1,2],'anchor':'title'}", "anchor")]
        [InlineData("{'operation':'auto_layout','sheet_id':1,'margins':[20,10,10,10],'notes_width':180,'gap':12}", null)]
        [InlineData("{'operation':'auto_layout','sheet_id':1,'margins':[20,10]}", "margins")]
        [InlineData("{'operation':'auto_layout'}", "sheet_id")]
        [InlineData("{'operation':'auto_layout','sheet_id':1,'center':[1,2]}", "not applicable")]
        public void Validate(string json, string expected)
        {
            string got = SheetLayoutRules.Validate(JObject.Parse(json));
            if (expected == null) Assert.Null(got); else Assert.Contains(expected, got);
        }
    }
}
