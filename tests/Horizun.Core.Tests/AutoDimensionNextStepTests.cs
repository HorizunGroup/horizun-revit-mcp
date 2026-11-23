using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class AutoDimensionNextStepTests
    {
        [Fact]
        public void One_level_points_to_a_level_and_face_pair()
            => Assert.Contains("top_face", AutoDimensionRules.NextStep(
                AutoDimensionRules.OpLevels, AutoDimensionRules.CodeGroupTooSmall, 1, 0));

        [Fact]
        public void Missing_opening_references_name_the_available_selectors()
            => Assert.Contains("opening_right", AutoDimensionRules.NextStep(
                AutoDimensionRules.OpOpenings, AutoDimensionRules.CodeNoReference, 1, 0));

        [Fact]
        public void Already_dimensioned_suggests_an_explicit_replan()
            => Assert.Contains("skip_existing=false", AutoDimensionRules.NextStep(
                AutoDimensionRules.OpGrids, AutoDimensionRules.CodeAlreadyDimensioned, 2, 0));

        [Fact]
        public void Empty_view_asks_for_visible_targets()
            => Assert.Contains("view_id", AutoDimensionRules.NextStep(
                AutoDimensionRules.OpGrids, AutoDimensionRules.CodeNothingToDimension, 0, 0));
    }
}
