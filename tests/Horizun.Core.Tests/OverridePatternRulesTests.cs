using System.Collections.Generic;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class OverridePatternRulesTests
    {
        private static List<PatternCandidate> Doc() => new List<PatternCandidate>
        {
            new PatternCandidate { Id = 30, Name = "Diagonal crosshatch", IsSolid = false, IsDrafting = true },
            new PatternCandidate { Id = 20, Name = "Solid model", IsSolid = true, IsDrafting = false },
            new PatternCandidate { Id = 25, Name = "Solid black", IsSolid = true, IsDrafting = true },
            new PatternCandidate { Id = 22, Name = "Solid fill", IsSolid = true, IsDrafting = true },
        };

        [Theory]
        [InlineData(null, "solid")]
        [InlineData("", "solid")]
        [InlineData("Solid", "solid")]
        [InlineData(" solid fill ", "solid")]
        [InlineData("1234", "id")]
        [InlineData("Diagonal crosshatch", "name")]
        public void A_pattern_spec_is_solid_an_id_or_a_name(string raw, string kind)
        {
            OverridePatternRules.Parse(raw, out string k, out long id, out string name);
            Assert.Equal(kind, k);
            if (kind == "id") Assert.Equal(1234, id);
            if (kind == "name") Assert.Equal("Diagonal crosshatch", name);
        }

        [Fact]
        public void The_solid_pattern_is_the_drafting_one_named_solid_fill_first()
        {
            Assert.Equal(22, OverridePatternRules.PickSolid(Doc()).Id);
        }

        [Fact]
        public void Without_a_solid_fill_named_so_the_lowest_drafting_solid_wins_every_time()
        {
            var docs = Doc(); docs.RemoveAll(c => c.Id == 22);
            Assert.Equal(25, OverridePatternRules.PickSolid(docs).Id);
            docs.Reverse();
            Assert.Equal(25, OverridePatternRules.PickSolid(docs).Id);
        }

        [Fact]
        public void A_model_solid_is_not_a_drafting_solid_and_no_solid_at_all_is_null()
        {
            var onlyModel = new List<PatternCandidate> { new PatternCandidate { Id = 20, Name = "Solid model", IsSolid = true, IsDrafting = false } };
            Assert.Null(OverridePatternRules.PickSolid(onlyModel));
            Assert.Null(OverridePatternRules.PickSolid(new List<PatternCandidate>()));
            Assert.Null(OverridePatternRules.PickSolid(null));
        }

        [Fact]
        public void A_named_or_numbered_pattern_is_found_and_a_missing_one_is_null()
        {
            Assert.Equal(30, OverridePatternRules.Pick(Doc(), "name", 0, "diagonal CROSSHATCH").Id);
            Assert.Equal(20, OverridePatternRules.Pick(Doc(), "id", 20, null).Id);
            Assert.Null(OverridePatternRules.Pick(Doc(), "name", 0, "Brick"));
            Assert.Null(OverridePatternRules.Pick(Doc(), "id", 999, null));
        }

        [Fact]
        public void The_refusal_for_no_solid_says_a_colour_alone_paints_nothing()
        {
            string solid = OverridePatternRules.NotFound("surface_pattern", null);
            Assert.Contains("paint nothing", solid);
            Assert.Contains("no solid DRAFTING fill", solid);
            string named = OverridePatternRules.NotFound("cut_pattern", "Brick");
            Assert.Contains("'Brick'", named);
            Assert.Contains("exact name", named);
        }

        [Fact]
        public void A_colour_counts_only_on_the_pattern_asked_for_and_switched_on()
        {
            Assert.True(OverridePatternRules.PatternHolds(22, 22, true));
            Assert.False(OverridePatternRules.PatternHolds(22, 22, false));
            Assert.False(OverridePatternRules.PatternHolds(22, -1, true));
        }
    }
}
