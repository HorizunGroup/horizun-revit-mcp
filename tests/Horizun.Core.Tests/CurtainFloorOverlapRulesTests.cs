using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class CurtainFloorOverlapRulesTests
    {
        [Fact]
        public void Curtain_wall_supporting_floor_with_only_contact_passes() =>
            Assert.Null(CurtainFloorOverlapRules.Problem(0, 1e-6));

        [Fact]
        public void Curtain_wall_crossed_by_floor_is_reported_as_overlap() =>
            Assert.Equal("floor_wall_overlap_increased", CurtainFloorOverlapRules.Problem(0.01, 1e-6));

        [Fact]
        public void Approximate_overlap_is_a_warning_even_when_it_exceeds_tolerance()
        {
            const double overlapM3 = 0.12316968;
            Assert.Equal("floor_wall_overlap_approximate", CurtainFloorOverlapRules.Problem(
                overlapM3, 1e-6, CurtainFloorOverlapRules.OrientedPrismMethod));
            Assert.True(CurtainFloorOverlapRules.IsApproximateMethod(CurtainFloorOverlapRules.OrientedPrismMethod));
            Assert.False(CurtainFloorOverlapRules.IsApproximateMethod(CurtainFloorOverlapRules.SolidIntersectionMethod));
        }

        [Fact]
        public void Five_millimetre_floor_offset_from_a_150_millimetre_mullion_is_only_a_warning()
        {
            var footprint = CurtainFloorOverlapRules.OrientedFootprint(0, 0, 9, 0, 0.15);
            double overlapArea = CurtainFloorOverlapRules.OrientedFootprintIntersectionArea(footprint, 0, 0.07, 9, 0.2);

            Assert.True(overlapArea > 0);
            Assert.Equal("floor_wall_overlap_approximate", CurtainFloorOverlapRules.Problem(
                0.12316968, 1e-6, CurtainFloorOverlapRules.OrientedPrismMethod));
            Assert.Contains("not an exact solid intersection", CurtainFloorOverlapRules.Guidance(
                CurtainFloorOverlapRules.OrientedPrismMethod, 42));
        }

        [Fact]
        public void Exact_solid_intersection_above_tolerance_still_blocks()
        {
            Assert.Equal("floor_wall_overlap_increased", CurtainFloorOverlapRules.Problem(
                0.01, 1e-6, CurtainFloorOverlapRules.SolidIntersectionMethod));
        }

        [Fact]
        public void Oriented_diagonal_prism_avoids_the_axis_aligned_wall_box_false_positive()
        {
            var footprint = CurtainFloorOverlapRules.OrientedFootprint(0, 0, 10, 10, 0.15);

            // This floor region is inside the wall's global AABB, but far from the diagonal wall.
            Assert.Equal(0, CurtainFloorOverlapRules.OrientedFootprintIntersectionArea(footprint, 0.1, 8.5, 1, 9.5), 8);
            // A narrow band around the actual line still intersects the oriented prism.
            Assert.True(CurtainFloorOverlapRules.OrientedFootprintIntersectionArea(footprint, 4, 4, 6, 6) > 0);
        }

        [Fact]
        public void Unmeasurable_curtain_wall_is_a_warning_with_concrete_guidance()
        {
            Assert.Equal("floor_wall_overlap_unmeasured", CurtainFloorOverlapRules.Problem(null, 1e-6));
            string guidance = CurtainFloorOverlapRules.Guidance(CurtainFloorOverlapRules.WarningMethod, 42);
            Assert.Contains("42", guidance);
            Assert.Contains("creation continued with a warning", guidance);
            Assert.Contains("interior face", guidance);
            Assert.Contains("roof", guidance);
            Assert.Contains("explicit overlap tolerance", guidance);
        }
    }
}
