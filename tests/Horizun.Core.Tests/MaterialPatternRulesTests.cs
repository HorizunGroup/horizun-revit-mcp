using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class MaterialPatternRulesTests
    {
        private static readonly MaterialPatternCandidate[] Patterns =
        {
            new MaterialPatternCandidate(10, "<Solid fill>", isDrafting: true, isSolid: true),
            new MaterialPatternCandidate(11, "Diagonal up", isDrafting: true, isSolid: false),
            new MaterialPatternCandidate(20, "Masonry - Brick 75x225mm", isDrafting: false, isSolid: false),
            new MaterialPatternCandidate(21, "Diagonal up", isDrafting: false, isSolid: false),
            new MaterialPatternCandidate(22, "<Solid fill>", isDrafting: false, isSolid: true),
        };

        [Fact]
        public void A_model_pattern_by_its_exact_name_is_accepted()
            => Assert.Equal(20, MaterialPatternRules.Pick(Patterns, "masonry - brick 75x225MM"));

        [Fact]
        public void A_name_in_both_keeps_the_drafting_pattern_as_before()
            => Assert.Equal(11, MaterialPatternRules.Pick(Patterns, "Diagonal up"));

        [Fact]
        public void Solid_is_still_the_drafting_solid_fill()
            => Assert.Equal(10, MaterialPatternRules.Pick(Patterns, "solid"));

        [Fact]
        public void A_pattern_element_id_is_accepted()
        {
            Assert.Equal(21, MaterialPatternRules.Pick(Patterns, "21"));
            Assert.Equal(22, MaterialPatternRules.Pick(Patterns, " 22 "));
        }

        [Fact]
        public void An_unknown_name_or_id_is_not_picked()
        {
            Assert.Null(MaterialPatternRules.Pick(Patterns, "Hatch 45"));
            Assert.Null(MaterialPatternRules.Pick(Patterns, "999"));
            Assert.Null(MaterialPatternRules.Pick(Patterns, "  "));
            Assert.Contains("drafting or model", MaterialPatternRules.NotFound("Hatch 45"));
        }


        [Fact]
        public void A_cut_pattern_takes_drafting_patterns_only()
        {
            Assert.Null(MaterialPatternRules.Pick(Patterns, "Masonry - Brick 75x225mm", allowModel: false));
            Assert.Null(MaterialPatternRules.Pick(Patterns, "21", allowModel: false));
            Assert.Equal(11, MaterialPatternRules.Pick(Patterns, "11", allowModel: false));
            Assert.Equal(11, MaterialPatternRules.Pick(Patterns, "Diagonal up", allowModel: false));
            Assert.Contains("drafting patterns only", MaterialPatternRules.NotFound("x", allowModel: false));
        }
    }
}
