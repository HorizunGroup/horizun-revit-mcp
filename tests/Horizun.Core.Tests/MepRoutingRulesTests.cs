// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// horizun_mep_routing, the Revit-free half: the rule list set_rules must find
// after the commit (edits applied in order, indexes against the list as the
// previous edit left it), catalog membership at the stated tolerance, and the
// velocity sizing size_by_flow proposes - including the cases where it must say
// "no size" instead of capping at the largest.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using Horizun.Revit.Core;
using Xunit;
using Edit = Horizun.Revit.Core.MepRoutingRules.RuleEdit;

namespace Horizun.Core.Tests
{
    public class MepRoutingRulesTests
    {
        private static readonly string[] Abc = { "a", "b", "c" };

        [Fact]
        public void Add_without_index_appends_and_with_index_inserts()
        {
            var r = MepRoutingRules.Simulate(Abc, new List<Edit>
            {
                new Edit { Action = "add", Added = "z" },
                new Edit { Action = "add", Index = 0, Added = "y" }
            }, out string error);
            Assert.Null(error);
            Assert.Equal(new[] { "y", "a", "b", "c", "z" }, r);
        }

        [Fact]
        public void Indexes_refer_to_the_list_the_previous_edit_left()
        {
            // remove 0 leaves b,c; index 1 is then c, not b.
            var r = MepRoutingRules.Simulate(Abc, new List<Edit>
            {
                new Edit { Action = "remove", Index = 0 },
                new Edit { Action = "remove", Index = 1 }
            }, out string error);
            Assert.Null(error);
            Assert.Equal(new[] { "b" }, r);
        }

        [Fact]
        public void Move_takes_the_rule_out_and_puts_it_at_the_final_position()
        {
            Assert.Equal(new[] { "c", "a", "b" }, MepRoutingRules.Simulate(Abc, new List<Edit> { new Edit { Action = "move", Index = 2, ToIndex = 0 } }, out _));
            Assert.Equal(new[] { "b", "c", "a" }, MepRoutingRules.Simulate(Abc, new List<Edit> { new Edit { Action = "move", Index = 0, ToIndex = 2 } }, out _));
        }

        [Theory]
        [InlineData("remove", 3, null)]
        [InlineData("remove", -1, null)]
        [InlineData("move", 0, 3)]
        [InlineData("move", 1, 1)]
        [InlineData("move", 1, null)]
        [InlineData("rename", 0, null)]
        public void An_edit_that_cannot_apply_is_refused_not_clamped(string action, int index, int? to)
        {
            var r = MepRoutingRules.Simulate(Abc, new List<Edit> { new Edit { Action = action, Index = index, ToIndex = to } }, out string error);
            Assert.Null(r);
            Assert.False(string.IsNullOrEmpty(error));
        }

        [Fact]
        public void The_before_list_is_not_mutated()
        {
            var before = new List<string>(Abc);
            MepRoutingRules.Simulate(before, new List<Edit> { new Edit { Action = "remove", Index = 0 } }, out _);
            Assert.Equal(Abc, before);
        }

        [Fact]
        public void An_add_with_no_rule_and_an_add_past_the_end_are_refused()
        {
            Assert.Null(MepRoutingRules.Simulate(Abc, new List<Edit> { new Edit { Action = "add" } }, out _));
            Assert.Null(MepRoutingRules.Simulate(Abc, new List<Edit> { new Edit { Action = "add", Index = 4, Added = "x" } }, out _));
            Assert.NotNull(MepRoutingRules.Simulate(Abc, new List<Edit> { new Edit { Action = "add", Index = 3, Added = "x" } }, out _));
        }

        [Fact]
        public void Units_are_mm_in_and_feet_only()
        {
            Assert.True(MepRoutingRules.TryUnitScale("mm", out double mm)); Assert.Equal(1 / 304.8, mm, 12);
            Assert.True(MepRoutingRules.TryUnitScale("in", out double inch)); Assert.Equal(1 / 12.0, inch, 12);
            Assert.True(MepRoutingRules.TryUnitScale(null, out double dflt)); Assert.Equal(mm, dflt);
            Assert.False(MepRoutingRules.TryUnitScale("m", out _));
        }

        [Fact]
        public void Catalog_membership_uses_the_stated_tolerance()
        {
            double d100 = 100 / 304.8;
            Assert.True(MepRoutingRules.CatalogHas(new[] { d100 }, d100 + 5e-6));
            Assert.False(MepRoutingRules.CatalogHas(new[] { d100 }, d100 + 5e-5));
            Assert.False(MepRoutingRules.CatalogHas(null, d100));
        }

        private static MepRoutingRules.SizeOption Mm(double nominal, double bore)
            => new MepRoutingRules.SizeOption { Nominal = nominal / 304.8, Bore = bore / 304.8 };

        [Fact]
        public void Round_sizing_picks_the_smallest_bore_that_keeps_velocity_at_or_below_the_limit()
        {
            // 10 L/s at 2 m/s needs 0.005 m2 -> d >= 79.8 mm. 80 mm bore passes, 65 does not.
            double q = 10 * MepRoutingRules.CubicFeetPerSecondPerLitrePerSecond;
            double v = 2 * MepRoutingRules.FeetPerSecondPerMetrePerSecond;
            var r = MepRoutingRules.SmallestRound(q, v, new[] { Mm(100, 102), Mm(80, 80), Mm(65, 65) });
            Assert.True(r.Found);
            Assert.Equal(80 / 304.8, r.Nominal, 9);
            Assert.True(r.VelocityFeetPerSecond <= v);
        }

        [Fact]
        public void Round_sizing_uses_the_bore_not_the_nominal()
        {
            // A 80 nominal with a 70 bore is too small; the next one is chosen.
            double q = 10 * MepRoutingRules.CubicFeetPerSecondPerLitrePerSecond;
            double v = 2 * MepRoutingRules.FeetPerSecondPerMetrePerSecond;
            var r = MepRoutingRules.SmallestRound(q, v, new[] { Mm(80, 70), Mm(100, 95) });
            Assert.Equal(100 / 304.8, r.Nominal, 9);
        }

        [Fact]
        public void Sizing_says_no_size_rather_than_capping_at_the_largest()
        {
            double q = 1000 * MepRoutingRules.CubicFeetPerSecondPerLitrePerSecond;
            var r = MepRoutingRules.SmallestRound(q, 1, new[] { Mm(50, 50), Mm(100, 100) });
            Assert.False(r.Found);
            Assert.Contains("no catalog size", r.Reason);
        }

        [Theory]
        [InlineData(0.0, 1.0)]
        [InlineData(double.NaN, 1.0)]
        [InlineData(1.0, 0.0)]
        [InlineData(1.0, -2.0)]
        public void No_flow_or_no_velocity_limit_is_a_reason_not_a_size(double flow, double v)
        {
            var r = MepRoutingRules.SmallestRound(flow, v, new[] { Mm(50, 50) });
            Assert.False(r.Found);
            Assert.NotNull(r.Reason);
        }

        [Fact]
        public void Rectangle_holds_the_height_and_picks_the_narrowest_width()
        {
            // 500 L/s at 5 m/s = 0.1 m2; at 250 mm height width >= 400 mm.
            double q = 500 * MepRoutingRules.CubicFeetPerSecondPerLitrePerSecond;
            double v = 5 * MepRoutingRules.FeetPerSecondPerMetrePerSecond;
            var widths = new[] { 300 / 304.8, 400 / 304.8, 500 / 304.8 };
            var r = MepRoutingRules.NarrowestRectangle(q, v, 250 / 304.8, widths);
            Assert.True(r.Found);
            Assert.Equal(400 / 304.8, r.Nominal, 9);
            Assert.False(MepRoutingRules.NarrowestRectangle(q, v, 0, widths).Found);
        }

        [Fact]
        public void Omitting_both_bounds_is_all_sizes_and_is_never_validated_as_numbers()
        {
            // Live, Revit 2026, 2026-09-24: an elbow rule sent without min/max was refused with
            // "min_size must be >= 0 and <= max_size" because All()'s sentinel bounds were validated.
            bool all; string error;
            Assert.True(MepRoutingRules.ResolveSizeRange(null, null, out all, out error));
            Assert.True(all);
            Assert.Null(error);
        }

        [Fact]
        public void A_given_range_is_validated_and_one_bound_alone_is_refused()
        {
            bool all; string error;
            Assert.True(MepRoutingRules.ResolveSizeRange(15, 100, out all, out error));
            Assert.False(all);
            Assert.False(MepRoutingRules.ResolveSizeRange(15, null, out all, out error));
            Assert.Contains("or neither for all sizes", error);
            Assert.False(MepRoutingRules.ResolveSizeRange(null, 100, out all, out error));
            Assert.False(MepRoutingRules.ResolveSizeRange(-1, 100, out all, out error));
            Assert.Contains("min_size must be >= 0", error);
            Assert.False(MepRoutingRules.ResolveSizeRange(100, 15, out all, out error));
            Assert.False(MepRoutingRules.ResolveSizeRange(double.NaN, 15, out all, out error));
            Assert.False(MepRoutingRules.ResolveSizeRange(0, double.PositiveInfinity, out all, out error));
        }
    }
}
