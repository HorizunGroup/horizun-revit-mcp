using Horizun.Revit.Core;
using Xunit;
using Rect = Horizun.Revit.Core.TagOverlapRules.Rect;

namespace Horizun.Core.Tests
{
    public class TagOverlapRulesTests
    {
        [Fact]
        public void Two_rectangles_stacked_on_top_of_each_other_overlap()
        {
            var a = Rect.Normalized(0, 0, 2, 1);
            var b = Rect.Normalized(0.5, 0, 2.5, 1);
            Assert.True(TagOverlapRules.Overlaps(a, b));
            Assert.True(TagOverlapRules.OverlapArea(a, b) > 0);
        }

        [Fact]
        public void Touching_edges_are_not_an_overlap()
        {
            var a = Rect.Normalized(0, 0, 1, 1);
            var b = Rect.Normalized(1, 0, 2, 1);
            Assert.False(TagOverlapRules.Overlaps(a, b));
            Assert.Equal(0, TagOverlapRules.OverlapArea(a, b));
        }

        [Fact]
        public void Far_apart_rectangles_do_not_overlap()
        {
            var a = Rect.Normalized(0, 0, 1, 1);
            var b = Rect.Normalized(10, 10, 11, 11);
            Assert.False(TagOverlapRules.Overlaps(a, b));
        }

        [Fact]
        public void Normalized_does_not_care_which_corner_came_first()
        {
            var a = Rect.Normalized(2, 2, 0, 0);
            Assert.Equal(0, a.MinX);
            Assert.Equal(0, a.MinY);
            Assert.Equal(2, a.MaxX);
            Assert.Equal(2, a.MaxY);
        }

        [Fact]
        public void One_rectangle_fully_inside_another_overlaps_by_the_smaller_area()
        {
            var outer = Rect.Normalized(0, 0, 10, 10);
            var inner = Rect.Normalized(2, 2, 4, 4);
            Assert.Equal(4, TagOverlapRules.OverlapArea(outer, inner));
        }

        // Backlog #12: the boxes were compared by their MODEL X/Y. In a section or an
        // elevation the view's own plane is X/Z (or any vertical plane), every tag box is flat
        // in model Y, and the overlap was always 0 - "clean" by construction. In a rotated plan
        // two tags side by side on screen could read as overlapping. Boxes are projected onto
        // the view's right and up directions now.
        [Fact]
        public void In_a_section_two_tags_stacked_on_screen_overlap()
        {
            double[] right = { 1, 0, 0 }, up = { 0, 0, 1 };
            // Two tags at the same height band, one drawn over the other in the section, both
            // flat in model Y (the section's depth).
            var a = TagOverlapRules.ViewRect(new[] { 0.0, 5, 1000 }, new[] { 2.0, 5, 1000.5 }, null, right, up);
            var b = TagOverlapRules.ViewRect(new[] { 1.0, 5, 1000.2 }, new[] { 3.0, 5, 1000.7 }, null, right, up);
            Assert.True(TagOverlapRules.Overlaps(a, b));
        }

        [Fact]
        public void In_a_section_two_tags_apart_vertically_do_not()
        {
            double[] right = { 1, 0, 0 }, up = { 0, 0, 1 };
            var a = TagOverlapRules.ViewRect(new[] { 0.0, 5, 1000 }, new[] { 2.0, 5, 1000.5 }, null, right, up);
            var b = TagOverlapRules.ViewRect(new[] { 0.0, 5, 1001 }, new[] { 2.0, 5, 1001.5 }, null, right, up);
            Assert.False(TagOverlapRules.Overlaps(a, b));
        }

        [Fact]
        public void A_box_with_a_transform_is_moved_before_it_is_projected()
        {
            double[] right = { 1, 0, 0 }, up = { 0, 1, 0 };
            // A transform that rotates 90 degrees about Z and moves 10 along X: the local box
            // 0..2 x 0..1 lands at X 9..10, Y 0..2.
            var t = new TagOverlapRules.BoxTransform
            {
                Origin = new[] { 10.0, 0, 0 }, BasisX = new[] { 0.0, 1, 0 }, BasisY = new[] { -1.0, 0, 0 }, BasisZ = new[] { 0.0, 0, 1 }
            };
            Rect r = TagOverlapRules.ViewRect(new[] { 0.0, 0, 0 }, new[] { 2.0, 1, 0 }, t, right, up);
            Assert.Equal(9, r.MinX, 9);
            Assert.Equal(10, r.MaxX, 9);
            Assert.Equal(0, r.MinY, 9);
            Assert.Equal(2, r.MaxY, 9);
        }

        [Fact]
        public void The_check_projects_on_the_view_directions()
        {
            var d = new System.IO.DirectoryInfo(System.AppContext.BaseDirectory);
            while (d != null && !System.IO.File.Exists(System.IO.Path.Combine(d.FullName, "AGENTS.md"))) d = d.Parent;
            string src = System.IO.File.ReadAllText(System.IO.Path.Combine(d.FullName, "src", "Horizun.Revit", "Core", "TagOverlapCheck.cs"));
            Assert.Contains("TagOverlapRules.ViewRect(", src);
            Assert.Contains("view.RightDirection", src);
            Assert.Contains("view.UpDirection", src);
            Assert.DoesNotContain("Rect.Normalized(b.Min.X, b.Min.Y, b.Max.X, b.Max.Y)", src);
        }

        // Remaining #12 limitation: model-axis boxes cannot reconstruct a label's
        // exact screen rectangle. Projection fixes the plane, not the inflated box.
        [Fact]
        public void Model_axis_boxes_remain_conservative_in_a_rotated_plan()
        {
            double q = System.Math.Sqrt(0.5);
            double[] right = { q, q, 0 }, up = { -q, q, 0 };
            // Actual screen rectangles: width 100, height 20, centres 110 apart.
            Assert.False(TagOverlapRules.Overlaps(Rect.Normalized(-50, -10, 50, 10), Rect.Normalized(60, -10, 160, 10)));
            double half = 60 * q, shift = 110 * q;
            var a = TagOverlapRules.ViewRect(new[] { -half, -half, 0 }, new[] { half, half, 0 }, null, right, up);
            var b = TagOverlapRules.ViewRect(new[] { shift - half, shift - half, 0 }, new[] { shift + half, shift + half, 0 }, null, right, up);
            Assert.True(TagOverlapRules.Overlaps(a, b));
        }
    }
}
