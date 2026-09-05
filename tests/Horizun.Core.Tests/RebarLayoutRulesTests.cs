// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// WHAT THESE ARE PROTECTING.
//
// A rebar set is the one place where "Revit accepted it" is nearly worthless as
// evidence. Revit will take a layout, place the bars, and report a healthy
// element with half the set standing outside the beam. So the plan computes
// every bar position BEFORE anything is written, and the apply compares Revit's
// own answer to it.
//
// Which means: if this arithmetic is wrong, the plan and the verification agree
// with each other, and both are wrong. There is no live test that catches that,
// because the live test asks the same arithmetic what it expected. It has to be
// caught here.
//
// The two properties below are the ones a rounding slip breaks silently:
// a MAXIMUM spacing that comes out larger than the maximum, and a MINIMUM clear
// spacing that comes out smaller than the minimum. Both would look fine in a
// reply and be wrong in a building.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class RebarLayoutRulesTests
    {
        private static RebarLayoutPlan R(string layout, int? number = null, double? spacing = null,
                                         double? array = null, bool first = true, bool last = true,
                                         double? diameter = null)
        {
            return RebarLayoutRules.Resolve(new RebarLayoutRequest
            {
                Layout = layout,
                Number = number,
                SpacingMm = spacing,
                ArrayLengthMm = array,
                IncludeFirstBar = first,
                IncludeLastBar = last,
                BarDiameterMm = diameter
            });
        }

        // ------------------------------------------------------------- single

        [Fact]
        public void A_single_bar_is_one_bar_at_one_position()
        {
            RebarLayoutPlan p = R(RebarLayout.Single);
            Assert.True(p.Ok);
            Assert.Equal(1, p.NumberOfBarPositions);
            Assert.Equal(1, p.Quantity);
            Assert.Equal(new[] { 0.0 }, p.PositionsMm);
            Assert.Null(p.ResultingSpacingMm);
        }

        [Fact]
        public void A_spacing_declared_beside_SINGLE_is_refused_rather_than_ignored()
        {
            // Somebody who wrote a spacing meant something by it. Building one bar
            // and staying quiet answers a question they did not ask.
            RebarLayoutPlan p = R(RebarLayout.Single, spacing: 150);
            Assert.False(p.Ok);
            Assert.Equal(RebarLayoutRules.CodeStatedNotUsed, p.Code);
        }

        // ------------------------------------------------------- fixed number

        [Fact]
        public void Fixed_number_spreads_the_positions_evenly_across_the_array()
        {
            RebarLayoutPlan p = R(RebarLayout.FixedNumber, number: 4, array: 900);
            Assert.True(p.Ok);
            Assert.Equal(4, p.NumberOfBarPositions);
            Assert.Equal(4, p.Quantity);
            Assert.Equal(300.0, p.ResultingSpacingMm.Value, 6);
            Assert.Equal(new[] { 0.0, 300.0, 600.0, 900.0 }, p.PositionsMm.Select(x => Math.Round(x, 6)));
        }

        [Fact]
        public void Fixed_number_with_a_spacing_ALSO_declared_is_refused()
        {
            // The two can disagree, and silently preferring one of them is how a set
            // ends up at a pitch nobody asked for.
            RebarLayoutPlan p = R(RebarLayout.FixedNumber, number: 4, array: 900, spacing: 200);
            Assert.False(p.Ok);
            Assert.Equal(RebarLayoutRules.CodeStatedNotUsed, p.Code);
        }

        [Fact]
        public void One_bar_declared_as_fixed_number_is_refused_and_named_single()
        {
            RebarLayoutPlan p = R(RebarLayout.FixedNumber, number: 1, array: 900);
            Assert.False(p.Ok);
            Assert.Equal(RebarLayoutRules.CodeNumberTooSmall, p.Code);
            Assert.Contains("single", p.Error);
        }

        // ------------------------------------------------- number with spacing

        [Fact]
        public void Number_with_spacing_derives_the_array_length()
        {
            RebarLayoutPlan p = R(RebarLayout.NumberWithSpacing, number: 5, spacing: 150);
            Assert.True(p.Ok);
            Assert.Equal(600.0, p.ArrayLengthMm, 6);
            Assert.Equal(5, p.NumberOfBarPositions);
        }

        [Fact]
        public void A_declared_array_length_that_CONTRADICTS_the_derived_one_is_refused()
        {
            RebarLayoutPlan p = R(RebarLayout.NumberWithSpacing, number: 5, spacing: 150, array: 800);
            Assert.False(p.Ok);
            Assert.Equal(RebarLayoutRules.CodeStatedNotUsed, p.Code);
        }

        [Fact]
        public void A_declared_array_length_that_AGREES_is_accepted()
        {
            RebarLayoutPlan p = R(RebarLayout.NumberWithSpacing, number: 5, spacing: 150, array: 600);
            Assert.True(p.Ok);
        }

        // ----------------------------------------------------- maximum spacing

        [Fact]
        public void Maximum_spacing_rounds_the_count_UP_so_no_gap_exceeds_the_maximum()
        {
            // 1000 / 300 = 3.33 gaps. Rounding to NEAREST gives 3 gaps at 333 mm -
            // wider than the maximum that was declared, in a set that reports success.
            RebarLayoutPlan p = R(RebarLayout.MaximumSpacing, spacing: 300, array: 1000);
            Assert.True(p.Ok);
            Assert.Equal(5, p.NumberOfBarPositions);
            Assert.Equal(250.0, p.ResultingSpacingMm.Value, 6);
        }

        [Fact]
        public void Maximum_spacing_that_divides_exactly_does_not_add_a_spurious_bar()
        {
            // The floating-point epsilon in the ceiling is there for this case: 900/300
            // is 3.0000000000000004 on some paths, and a naive ceiling makes it 4 gaps.
            RebarLayoutPlan p = R(RebarLayout.MaximumSpacing, spacing: 300, array: 900);
            Assert.True(p.Ok);
            Assert.Equal(4, p.NumberOfBarPositions);
            Assert.Equal(300.0, p.ResultingSpacingMm.Value, 6);
        }

        [Fact]
        public void THE_PROPERTY_a_maximum_is_never_exceeded()
        {
            // Over a wide sweep, not one example. This is the whole meaning of the
            // word maximum, and it is one rounding character away from being false.
            for (int arrayMm = 50; arrayMm <= 6000; arrayMm += 37)
            {
                for (int maxMm = 40; maxMm <= 400; maxMm += 13)
                {
                    RebarLayoutPlan p = R(RebarLayout.MaximumSpacing, spacing: maxMm, array: arrayMm);
                    Assert.True(p.Ok, "refused " + arrayMm + " / " + maxMm + ": " + p.Error);
                    Assert.True(p.ResultingSpacingMm.Value <= maxMm + 1e-9,
                        "array " + arrayMm + " at max " + maxMm + " produced " + p.ResultingSpacingMm.Value);
                    Assert.True(p.NumberOfBarPositions >= 2);
                }
            }
        }

        // ----------------------------------------------- minimum clear spacing

        [Fact]
        public void Minimum_clear_spacing_measures_between_bar_SURFACES()
        {
            // 100 mm clear on a 20 mm bar is 120 mm centre to centre. Treating the
            // declared number as centre-to-centre puts one extra bar in every
            // 120 mm of array - and every bar closer together than allowed.
            RebarLayoutPlan p = R(RebarLayout.MinimumClearSpacing, spacing: 100, array: 1000, diameter: 20);
            Assert.True(p.Ok);
            Assert.Equal(9, p.NumberOfBarPositions);
            Assert.Equal(125.0, p.ResultingSpacingMm.Value, 6);
        }

        [Fact]
        public void Minimum_clear_spacing_without_a_DIAMETER_is_refused_rather_than_assumed()
        {
            RebarLayoutPlan p = R(RebarLayout.MinimumClearSpacing, spacing: 100, array: 1000);
            Assert.False(p.Ok);
            Assert.Equal(RebarLayoutRules.CodeMissingDiameter, p.Code);
        }

        [Fact]
        public void A_clear_spacing_too_wide_for_the_array_is_refused_with_the_arithmetic()
        {
            RebarLayoutPlan p = R(RebarLayout.MinimumClearSpacing, spacing: 500, array: 300, diameter: 25);
            Assert.False(p.Ok);
            Assert.Equal(RebarLayoutRules.CodeSpacingExceedsArray, p.Code);
            Assert.Contains("525", p.Error);   // 500 clear + 25 diameter
        }

        [Fact]
        public void THE_PROPERTY_a_minimum_clear_distance_is_never_undercut()
        {
            for (int arrayMm = 200; arrayMm <= 6000; arrayMm += 41)
            {
                foreach (int dia in new[] { 8, 12, 16, 20, 25, 32 })
                {
                    for (int clearMm = 25; clearMm <= 250; clearMm += 17)
                    {
                        RebarLayoutPlan p = R(RebarLayout.MinimumClearSpacing,
                                              spacing: clearMm, array: arrayMm, diameter: dia);
                        if (!p.Ok)
                        {
                            // The only legitimate refusal is that two bars do not fit.
                            Assert.Equal(RebarLayoutRules.CodeSpacingExceedsArray, p.Code);
                            Assert.True(clearMm + dia > arrayMm + 1e-9);
                            continue;
                        }
                        double clear = p.ResultingSpacingMm.Value - dia;
                        Assert.True(clear >= clearMm - 1e-9,
                            "array " + arrayMm + " dia " + dia + " min clear " + clearMm +
                            " produced clear " + clear);
                    }
                }
            }
        }

        // ---------------------------------------------- suppressed end bars

        [Fact]
        public void Excluding_both_ends_is_refused_because_Revit_keeps_one_of_them()
        {
            // MEASURED 2026-09-03 on Revit 2026 (26.4.0.32): a 16-position array
            // declared with both ends off came back with IncludeFirstBar true and
            // one more bar than the plan. The pair is refused, never modelled.
            RebarLayoutPlan p = R(RebarLayout.FixedNumber, number: 5, array: 800, first: false, last: false);
            Assert.False(p.Ok);
            Assert.Equal(RebarLayoutRules.CodeBothEndsSuppressed, p.Code);
            Assert.Contains("one pitch shorter", p.Error);
        }

        [Fact]
        public void A_suppressed_first_bar_on_maximum_spacing_is_refused_as_measured()
        {
            var req = new RebarLayoutRequest { Layout = RebarLayout.MaximumSpacing, SpacingMm = 100, ArrayLengthMm = 1000, IncludeFirstBar = false };
            RebarLayoutPlan p = RebarLayoutRules.Resolve(req);
            Assert.False(p.Ok);
            Assert.Equal(RebarLayoutRules.CodeFirstBarNotSuppressible, p.Code);
            // the last bar alone is honoured (measured in the same run)
            var ok = new RebarLayoutRequest { Layout = RebarLayout.MaximumSpacing, SpacingMm = 100, ArrayLengthMm = 1000, IncludeLastBar = false };
            Assert.True(RebarLayoutRules.Resolve(ok).Ok);
        }

        [Fact]
        public void Excluding_ONE_end_still_removes_that_bar_and_keeps_the_positions()
        {
            RebarLayoutPlan p = R(RebarLayout.FixedNumber, number: 5, array: 800, first: false, last: true);
            Assert.True(p.Ok, p.Error);
            Assert.Equal(5, p.NumberOfBarPositions);   // the ARRAY is still five long
            Assert.Equal(4, p.Quantity);               // four bars actually stand
            Assert.Equal(5, p.PositionsMm.Count);
        }

        [Fact]
        public void Excluding_both_ends_of_a_TWO_bar_set_is_refused_the_same_way()
        {
            RebarLayoutPlan p = R(RebarLayout.FixedNumber, number: 2, array: 300, first: false, last: false);
            Assert.False(p.Ok);
            Assert.Equal(RebarLayoutRules.CodeBothEndsSuppressed, p.Code);
        }

        // ------------------------------------------------------------- drift

        [Fact]
        public void The_LAST_position_lands_exactly_on_the_array_length()
        {
            // Accumulating a spacing 100 times drifts, and the drift lands on the
            // last bar - which is precisely the one a fit check is about. The
            // positions are multiplied, not added, so this is exact.
            RebarLayoutPlan p = R(RebarLayout.FixedNumber, number: 101, array: 1000);
            Assert.True(p.Ok);
            Assert.Equal(1000.0, p.PositionsMm[100], 9);
            Assert.Equal(0.0, p.PositionsMm[0], 9);
        }

        [Fact]
        public void Positions_are_ascending_and_unique_for_every_layout()
        {
            var plans = new List<RebarLayoutPlan>
            {
                R(RebarLayout.Single),
                R(RebarLayout.FixedNumber, number: 7, array: 1234.5),
                R(RebarLayout.NumberWithSpacing, number: 6, spacing: 175),
                R(RebarLayout.MaximumSpacing, spacing: 250, array: 1700),
                R(RebarLayout.MinimumClearSpacing, spacing: 60, array: 1700, diameter: 16)
            };
            foreach (RebarLayoutPlan p in plans)
            {
                Assert.True(p.Ok, p.Error);
                for (int i = 1; i < p.PositionsMm.Count; i++)
                    Assert.True(p.PositionsMm[i] > p.PositionsMm[i - 1]);
            }
        }

        // -------------------------------------------------------------- fit

        [Fact]
        public void A_set_that_runs_past_the_host_names_WHICH_positions_are_outside()
        {
            RebarLayoutPlan p = R(RebarLayout.FixedNumber, number: 5, array: 1000);
            List<int> bad = RebarLayoutRules.PositionsOutside(p.PositionsMm, 0, 600, 1.0);

            // 750 and 1000 are past the end; 0, 250 and 500 are not.
            Assert.Equal(new[] { 3, 4 }, bad);
        }

        [Fact]
        public void A_set_that_fits_reports_nothing_outside()
        {
            RebarLayoutPlan p = R(RebarLayout.FixedNumber, number: 5, array: 600);
            Assert.Empty(RebarLayoutRules.PositionsOutside(p.PositionsMm, 0, 600, 1.0));
        }

        [Fact]
        public void The_tolerance_is_applied_at_BOTH_ends()
        {
            RebarLayoutPlan p = R(RebarLayout.FixedNumber, number: 3, array: 600);
            // A bar exactly on the boundary is inside; the tolerance decides the rest.
            Assert.Empty(RebarLayoutRules.PositionsOutside(p.PositionsMm, 0.5, 599.5, 1.0));
            Assert.NotEmpty(RebarLayoutRules.PositionsOutside(p.PositionsMm, 2.0, 598.0, 1.0));
        }

        // ---------------------------------------------------------- vocabulary

        [Fact]
        public void The_layout_vocabulary_is_closed()
        {
            Assert.False(RebarLayout.IsKnown("maximum-spacing"));
            Assert.False(RebarLayout.IsKnown("Maximum_Spacing"));
            Assert.False(RebarLayout.IsKnown(""));
            Assert.False(RebarLayout.IsKnown(null));
            Assert.Equal(5, RebarLayout.All.Length);
            foreach (string s in RebarLayout.All) Assert.True(RebarLayout.IsKnown(s));
        }

        [Fact]
        public void An_unknown_layout_lists_the_five_that_exist()
        {
            RebarLayoutPlan p = R("every_300");
            Assert.False(p.Ok);
            Assert.Equal(RebarLayoutRules.CodeUnknownLayout, p.Code);
            foreach (string s in RebarLayout.All) Assert.Contains(s, p.Error);
        }

        [Fact]
        public void A_refused_layout_carries_NO_positions_and_no_quantity()
        {
            // A caller that reads positions without checking Ok must not find a
            // plausible-looking half answer there.
            RebarLayoutPlan p = R(RebarLayout.FixedNumber, number: 4);   // no array length
            Assert.False(p.Ok);
            Assert.Empty(p.PositionsMm);
            Assert.Equal(0, p.Quantity);
            Assert.Equal(0, p.NumberOfBarPositions);
        }
    }
}
