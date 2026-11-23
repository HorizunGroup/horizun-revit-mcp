using System;
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class DocumentationRepairTests
    {
        [Theory]
        [InlineData(-20, 30, 20, 70, 0, 30)]
        [InlineData(80, 30, 120, 70, 60, 30)]
        [InlineData(30, -20, 70, 20, 30, 0)]
        [InlineData(30, 80, 70, 120, 30, 60)]
        [InlineData(-20, 80, 20, 120, 0, 60)]
        public void Repair_translates_each_side_and_corner_without_resizing(double x0, double y0, double x1, double y1, double x, double y)
        {
            var original = new[] { x0, y0, x1, y1 };
            var targets = SheetLayoutRules.RepairOutside(new List<KeyValuePair<string, double[]>> { new KeyValuePair<string, double[]>("v", original) }, new double[] { 0, 0, 100, 100 }, 0);
            Assert.Equal(new[] { x, y, x + x1 - x0, y + y1 - y0 }, targets["v"]);
            Assert.Equal(new[] { x0, y0, x1, y1 }, original);
        }

        [Fact]
        public void Repair_keeps_inside_viewports_and_includes_the_title_extent()
        {
            var extent = SheetLayoutRules.Union(new double[] { 80, 20, 110, 70 }, new double[] { 75, 5, 120, 15 });
            var before = new Dictionary<string, double[]> { ["inside"] = new double[] { 0, 40, 30, 60 }, ["outside"] = extent };
            var repaired = SheetLayoutRules.RepairOutside(before.ToList(), new double[] { 0, 0, 100, 100 }, 0);
            Assert.False(repaired.ContainsKey("inside"));
            Assert.Equal(new double[] { 55, 5, 100, 70 }, repaired["outside"]);
            Assert.All(repaired.Values, r => Assert.True(SheetLayoutRules.Inside(new double[] { 0, 0, 100, 100 }, r, 0)));
        }

        [Fact]
        public void Collision_or_oversized_or_unreadable_extent_refuses_with_the_viewport_and_cause()
        {
            var area = new double[] { 0, 0, 100, 100 };
            foreach (double[] bad in new[] { new double[] { 80, 0, 120, 40 }, new double[] { 0, 0, 101, 50 }, null, new double[] { 0, 0, double.NaN, 20 } })
            {
                var input = new Dictionary<string, double[]> { ["fixed"] = new double[] { 60, 0, 75, 40 }, ["bad"] = bad };
                Assert.Null(SheetLayoutRules.RepairOutside(input.ToList(), area, 0.1, out string refusal));
                Assert.Contains("bad", refusal);
                Assert.True(refusal.Contains("collides") || refusal.Contains("large") || refusal.Contains("unreadable"));
            }
        }

        [Fact]
        public void Previous_overlap_does_not_move_fixed_viewports_or_prevent_repair_of_C()
        {
            var input = new Dictionary<string, double[]> {
                ["A"] = new double[] { 0, 0, 20, 20 }, ["B"] = new double[] { 10, 10, 30, 30 },
                ["C"] = new double[] { 90, 60, 110, 80 } };
            var moved = SheetLayoutRules.RepairOutside(input.ToList(), new double[] { 0, 0, 100, 100 }, 0.5, out string refusal);
            Assert.Null(refusal);
            Assert.Equal("C", Assert.Single(moved).Key);
            Assert.Equal(new double[] { 80, 60, 100, 80 }, moved["C"]);
        }

        [Fact]
        public void Inside_with_nonzero_tolerance_is_nothing_to_repair()
        {
            var input = new Dictionary<string, double[]> { ["A"] = new double[] { -0.4, 0, 100.4, 20 } };
            Assert.Empty(SheetLayoutRules.RepairOutside(input.ToList(), new double[] { 0, 0, 100, 100 }, 0.5, out string refusal));
            Assert.Null(refusal);
        }

        [Fact]
        public void Repair_does_not_shift_an_axis_already_inside_the_tolerance()
        {
            var input = new Dictionary<string, double[]> { ["A"] = new[] { -0.4, 90, 20, 110.0 } };
            var repaired = SheetLayoutRules.RepairOutside(input.ToList(), new double[] { 0, 0, 100, 100 }, 0.5, out string refusal);
            Assert.Null(refusal);
            Assert.Equal(new[] { -0.4, 80, 20, 100.0 }, repaired["A"]);
        }

        [Theory]
        [InlineData("{operation:'auto_layout',sheet_id:1,repair_outside:true}", true)]
        [InlineData("{operation:'auto_layout',sheet_id:1,repair_outside:'yes'}", false)]
        [InlineData("{operation:'move_viewport',viewport_id:1,center:[0,0],repair_outside:true}", false)]
        [InlineData("{operation:'set_view_scale',viewport_id:1}", true)]
        [InlineData("{operation:'set_view_scale',viewport_id:1,scale:0}", false)]
        [InlineData("{operation:'set_view_scale',viewport_id:1,scale:1.5}", false)]
        public void Optional_values_do_not_weaken_validation_of_explicit_values(string json, bool valid)
            => Assert.Equal(valid, SheetLayoutRules.Validate(JObject.Parse(json)) == null);
    }
}
