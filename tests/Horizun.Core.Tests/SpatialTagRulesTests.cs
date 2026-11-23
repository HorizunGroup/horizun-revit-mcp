// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// Area tags and MEP space tags (horizun_annotate operation=tag). An Area is tagged only in an
// area plan of its own scheme and level, a Space in a plan at its level; the options a spatial
// tag cannot honour are refused rather than dropped, and a type of the wrong tag category names
// the category it needed.
// -----------------------------------------------------------------------------
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class SpatialTagRulesTests
    {
        [Fact]
        public void Each_kind_names_its_tag_category_and_native_class()
        {
            Assert.Equal("OST_AreaTags", SpatialTagRules.TagCategory(SpatialTagRules.Area));
            Assert.Equal("OST_MEPSpaceTags", SpatialTagRules.TagCategory(SpatialTagRules.Space));
            Assert.Equal("AreaTag", SpatialTagRules.NativeName(SpatialTagRules.Area));
            Assert.Equal("SpaceTag", SpatialTagRules.NativeName(SpatialTagRules.Space));
        }

        [Fact]
        public void An_area_is_tagged_only_in_an_area_plan_of_its_scheme_and_level()
        {
            Assert.Null(SpatialTagRules.ViewRefusal(SpatialTagRules.Area, "AreaPlan", true, true));
            Assert.Contains("AREA PLAN", SpatialTagRules.ViewRefusal(SpatialTagRules.Area, "FloorPlan", true, true));
            Assert.Contains("another area scheme", SpatialTagRules.ViewRefusal(SpatialTagRules.Area, "AreaPlan", false, true));
            Assert.Contains("another level", SpatialTagRules.ViewRefusal(SpatialTagRules.Area, "AreaPlan", true, false));
        }

        [Theory]
        [InlineData("FloorPlan")] [InlineData("CeilingPlan")] [InlineData("EngineeringPlan")]
        public void A_space_is_tagged_in_a_plan_at_its_level(string viewType)
        {
            Assert.Null(SpatialTagRules.ViewRefusal(SpatialTagRules.Space, viewType, true, true));
            Assert.Contains("another level", SpatialTagRules.ViewRefusal(SpatialTagRules.Space, viewType, true, false));
        }

        [Theory]
        [InlineData("ThreeD")] [InlineData("Section")] [InlineData("AreaPlan")] [InlineData("Schedule")]
        public void A_space_is_not_tagged_outside_a_plan(string viewType)
            => Assert.Contains("floor, ceiling or structural plan", SpatialTagRules.ViewRefusal(SpatialTagRules.Space, viewType, true, true));

        [Fact]
        public void Options_a_spatial_tag_cannot_honour_are_refused_by_name()
        {
            Assert.Null(SpatialTagRules.OptionRefusal(SpatialTagRules.Area, null, false, false, false));
            Assert.Null(SpatialTagRules.OptionRefusal(SpatialTagRules.Space, "by_category", false, false, false));
            Assert.Contains("collision", SpatialTagRules.OptionRefusal(SpatialTagRules.Area, null, true, false, false));
            Assert.Contains("by_category", SpatialTagRules.OptionRefusal(SpatialTagRules.Space, "material", false, false, false));
            Assert.Contains("tag_type_id", SpatialTagRules.OptionRefusal(SpatialTagRules.Area, null, false, true, false));
            Assert.Contains("SpaceTag", SpatialTagRules.OptionRefusal(SpatialTagRules.Space, null, false, false, true));
        }

        [Fact]
        public void A_type_of_another_tag_category_names_the_category_it_needed()
        {
            Assert.Null(SpatialTagRules.TypeCategoryRefusal(SpatialTagRules.Area, "OST_AreaTags"));
            Assert.Contains("OST_MEPSpaceTags", SpatialTagRules.TypeCategoryRefusal(SpatialTagRules.Space, "OST_RoomTags"));
            Assert.Contains("OST_AreaTags", SpatialTagRules.TypeCategoryRefusal(SpatialTagRules.Area, null));
        }

        [Fact]
        public void Zero_or_several_loaded_types_need_a_choice_and_exactly_one_is_the_default()
        {
            Assert.Null(SpatialTagRules.TypeChoiceError(SpatialTagRules.Area, 1));
            Assert.Contains("No AreaTag type is loaded", SpatialTagRules.TypeChoiceError(SpatialTagRules.Area, 0));
            Assert.Contains("3 are loaded", SpatialTagRules.TypeChoiceError(SpatialTagRules.Space, 3));
        }
    }
}
