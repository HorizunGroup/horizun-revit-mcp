using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    /// <summary>
    /// Field case 2026-11-09, measured on Revit 2025: a wall's line lies at its base level (z 0 here) and the base offset
    /// (3.0 ft) is added on top. A line sent at z=3.0 floated the wall to 6.0 and was reported verified_applied.
    /// </summary>
    public class WallCurveElevationRulesTests
    {
        private const double Tol = 0.5 / 304.8;

        [Fact]
        public void A_line_at_the_base_offset_height_is_refused_before_anything_is_written()
        {
            string refusal = WallCurveElevationRules.Refusal(1235365, currentZ: 0, wantZ0: 3.0, wantZ1: 3.0, baseOffset: 3.0, tolerance: Tol);
            Assert.NotNull(refusal);
            Assert.Contains("Nothing was written", refusal);
            Assert.Contains("WALL_BASE_OFFSET", refusal);
            Assert.Contains("z 0 mm", refusal);
            Assert.Contains(WallCurveElevationRules.Code, refusal);
        }

        [Fact]
        public void A_line_at_the_walls_own_z_moves_it_in_plan()
        {
            Assert.Null(WallCurveElevationRules.Refusal(1, currentZ: 0, wantZ0: 0, wantZ1: 0.0001 / 304.8, baseOffset: 3.0, tolerance: Tol));
            Assert.Null(WallCurveElevationRules.Refusal(1, currentZ: 10, wantZ0: 10, wantZ1: 10, baseOffset: double.NaN, tolerance: Tol));
        }

        [Fact]
        public void A_sloped_request_is_refused_even_when_one_end_keeps_the_z()
        {
            Assert.NotNull(WallCurveElevationRules.Refusal(1, currentZ: 0, wantZ0: 0, wantZ1: 1.0, baseOffset: 0, tolerance: Tol));
        }

        [Fact]
        public void The_field_case_does_not_verify_when_the_solid_rose()
        {
            // before: line z 0 + offset 3 = 3; after: line z 3 + offset 3 = 6.
            string verdict = WallCurveElevationRules.Judge(baseBefore: 3.0, baseAfter: 6.0, tolerance: Tol);
            Assert.NotNull(verdict);
            Assert.Contains("914.4", verdict);
            Assert.Contains("1828.8", verdict);
        }

        [Fact]
        public void A_base_that_stayed_verifies_and_an_unreadable_one_does_not()
        {
            Assert.Null(WallCurveElevationRules.Judge(3.0, 3.0 + 0.2 / 304.8, Tol));
            Assert.NotNull(WallCurveElevationRules.Judge(double.NaN, 3.0, Tol));
            Assert.NotNull(WallCurveElevationRules.Judge(3.0, double.NaN, Tol));
        }
    }
}
