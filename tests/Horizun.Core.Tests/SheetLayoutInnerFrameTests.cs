// horizun_sheet_layout: the drawing frame read from a title block's lines (auto_layout without margins).
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class SheetLayoutInnerFrameTests
    {
        private const double Mm = 1.0 / 304.8;
        private const double Tol = 0.5 * Mm;
        private static double[] S(double a, double b, double c, double d) => new[] { a * Mm, b * Mm, c * Mm, d * Mm };
        private static double[] Round(double[] r) => r.Select(v => System.Math.Round(v / Mm, 3)).ToArray();

        private static List<double[]> Rectangle(double x0, double y0, double x1, double y1)
            => new List<double[]> { S(x0, y0, x1, y0), S(x1, y0, x1, y1), S(x1, y1, x0, y1), S(x0, y1, x0, y0) };

        [Fact]
        public void A_frame_in_from_the_paper_edge_and_a_title_panel_on_the_right_bound_the_drawing_area()
        {
            // 1066.8 x 762 sheet (30 x 42 in), frame 38 / 19 mm in, title panel from x = 914 to the frame.
            var lines = Rectangle(0, 0, 1066.8, 762);
            lines.AddRange(Rectangle(38, 19, 1048, 743));
            lines.Add(S(914, 19, 914, 743));
            // panel boxes: short lines that must not count
            lines.Add(S(917, 300, 1045, 300)); lines.Add(S(917, 400, 1045, 400)); lines.Add(S(980, 20, 980, 200));
            double[] f = SheetLayoutRules.InnerFrame(lines, S(0, 0, 1066.8, 762), 0.6, Tol, out string[] sides);
            Assert.Equal(new[] { 38.0, 19, 914, 743 }, Round(f));
            Assert.All(sides, s => Assert.Equal("frame_line", s));
        }

        [Fact]
        public void A_frame_drawn_in_pieces_still_counts()
        {
            var lines = new List<double[]> { S(30, 20, 30, 300), S(30, 300, 30, 580), S(30, 20, 800, 20), S(30, 580, 800, 580), S(800, 20, 800, 580) };
            double[] f = SheetLayoutRules.InnerFrame(lines, S(0, 0, 841, 594), 0.6, Tol, out _);
            Assert.Equal(new[] { 30.0, 20, 800, 580 }, Round(f));
        }

        [Fact]
        public void A_title_strip_along_the_bottom_raises_the_bottom_edge()
        {
            var lines = Rectangle(10, 10, 831, 584);
            lines.Add(S(10, 80, 831, 80));
            double[] f = SheetLayoutRules.InnerFrame(lines, S(0, 0, 841, 594), 0.6, Tol, out string[] sides);
            Assert.Equal(new[] { 10.0, 80, 831, 584 }, Round(f));
            Assert.Equal("frame_line", sides[3]);
        }

        [Fact]
        public void No_lines_keeps_the_box_and_says_so()
        {
            double[] f = SheetLayoutRules.InnerFrame(new List<double[]>(), S(0, 0, 841, 594), 0.6, Tol, out string[] sides);
            Assert.Equal(new[] { 0.0, 0, 841, 594 }, Round(f));
            Assert.All(sides, s => Assert.Equal("title_block_box", s));
        }

        [Fact]
        public void A_frame_too_small_to_be_believed_falls_back_to_the_box()
        {
            // two long verticals close to the centre would leave a sliver: not a drawing frame
            var lines = new List<double[]> { S(400, 0, 400, 594), S(440, 0, 440, 594) };
            double[] f = SheetLayoutRules.InnerFrame(lines, S(0, 0, 841, 594), 0.6, Tol, out string[] sides);
            Assert.Equal(new[] { 0.0, 0, 841, 594 }, Round(f));
            Assert.All(sides, s => Assert.Equal("title_block_box", s));
        }

        [Fact]
        public void The_usable_area_is_the_frame_minus_the_default_margins()
        {
            var lines = Rectangle(38, 19, 1048, 743); lines.Add(S(914, 19, 914, 743));
            double[] f = SheetLayoutRules.InnerFrame(lines, S(0, 0, 1066.8, 762), 0.6, Tol, out _);
            double[] u = SheetLayoutRules.Usable(f, SheetLayoutRules.DefaultMarginsMm.Select(v => v * Mm).ToArray(), 0);
            Assert.Equal(new[] { 48.0, 29, 904, 733 }, Round(u));
        }
        [Fact]
        public void A_title_box_in_the_lower_right_corner_takes_the_strip_beside_it_when_that_leaves_more()
        {
            // the Horizun A1 title block: its only long lines are the paper edge; the title data is a box 671-831 x 10-285
            var lines = Rectangle(0.5, -1, 841.5, 593);
            lines.AddRange(Rectangle(671, 10, 831, 285));
            double[] frame = SheetLayoutRules.InnerFrame(lines, S(0.5, -1, 841.5, 593), 0.6, Tol, out _);
            double[] f = SheetLayoutRules.AvoidCornerBoxes(lines, frame, 30 * Mm, Tol, out List<double[]> boxes, out string cut);
            Assert.Single(boxes);
            Assert.Equal(new[] { 0.5, -1, 671, 593 }, Round(f));
            Assert.Contains("right", cut);
        }

        [Fact]
        public void A_wide_low_title_box_takes_the_strip_above_it()
        {
            var lines = Rectangle(0, 0, 841, 594);
            lines.AddRange(Rectangle(300, 10, 831, 60));
            double[] f = SheetLayoutRules.AvoidCornerBoxes(lines, S(0, 0, 841, 594), 30 * Mm, Tol, out _, out string cut);
            Assert.Equal(new[] { 0.0, 60, 841, 594 }, Round(f));
            Assert.Contains("bottom", cut);
        }

        [Fact]
        public void A_box_in_the_middle_of_the_sheet_is_not_a_title_box()
        {
            var lines = Rectangle(0, 0, 841, 594);
            lines.AddRange(Rectangle(300, 200, 400, 300));
            double[] f = SheetLayoutRules.AvoidCornerBoxes(lines, S(0, 0, 841, 594), 30 * Mm, Tol, out List<double[]> boxes, out string cut);
            Assert.Empty(boxes);
            Assert.Null(cut);
            Assert.Equal(new[] { 0.0, 0, 841, 594 }, Round(f));
        }

        [Fact]
        public void The_frame_itself_is_not_taken_for_a_title_box()
        {
            var lines = Rectangle(38, 19, 1048, 743);
            lines.Add(S(914, 19, 914, 743));
            double[] frame = SheetLayoutRules.InnerFrame(lines, S(0, 0, 1066.8, 762), 0.6, Tol, out _);
            double[] f = SheetLayoutRules.AvoidCornerBoxes(lines, frame, 30 * Mm, Tol, out List<double[]> boxes, out _);
            Assert.Empty(boxes);
            Assert.Equal(new[] { 38.0, 19, 914, 743 }, Round(f));
        }
    }
}
