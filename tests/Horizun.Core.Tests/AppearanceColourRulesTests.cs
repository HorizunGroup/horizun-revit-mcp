using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class AppearanceColourRulesTests
    {
        [Theory]
        [InlineData("Generic", "generic_diffuse")]
        [InlineData("CeramicSchema", "ceramic_color")]
        [InlineData("Concrete", "concrete_color")]
        [InlineData("Metal", "metal_color")]
        [InlineData("MetallicPaint", "metallicpaint_base_color")]
        [InlineData("WallPaint", "wallpaint_color")]
        [InlineData("Plastic", "plasticvinyl_color")]
        [InlineData("Masonry", "masonrycmu_color")]
        [InlineData("Stone", "stone_color")]
        [InlineData("Glazing", "glazing_transmittance_map")]
        [InlineData("SolidGlass", "solidglass_transmittance_custom_color")]
        public void KnownSchemas(string schema, string expected) => Assert.Equal(expected, AppearanceColourRules.Candidates(schema)[0]);

        [Theory]
        [InlineData("Wood")]
        [InlineData("Hardwood")]
        [InlineData("unknown")]
        public void NoGuessedColour(string schema) => Assert.Empty(AppearanceColourRules.Candidates(schema));

        [Fact]
        public void GlazingHasBothKnownNames() => Assert.Equal(new[] { "glazing_transmittance_map", "glazing_transmittance_color" }, AppearanceColourRules.Candidates("glazing"));
    }
}
