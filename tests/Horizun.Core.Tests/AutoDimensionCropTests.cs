// Measured live 2026-10-18: an auto-dimension chain on a cropped plan landed outside the crop.
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class AutoDimensionCropTests
    {
        [Fact]
        public void A_vertical_chain_past_the_crop_moves_inside_and_is_clipped()
        {
            var chain = new AutoDimensionChain { Axis = "vertical", StartX = 10.5, EndX = 10.5, StartY = -5, EndY = 50 };
            Assert.True(AutoDimensionRules.KeepInsideCrop(chain, new[] { 0.0, 0.0, 10.0, 40.0 }, 0.5));
            Assert.Equal(9.5, chain.StartX, 6); Assert.Equal(9.5, chain.EndX, 6);
            Assert.Equal(0.0, chain.StartY, 6); Assert.Equal(40.0, chain.EndY, 6);
        }

        [Fact]
        public void A_chain_inside_or_without_crop_is_left_alone()
        {
            var chain = new AutoDimensionChain { Axis = "horizontal", StartX = 1, EndX = 9, StartY = 5, EndY = 5 };
            Assert.False(AutoDimensionRules.KeepInsideCrop(chain, new[] { 0.0, 0.0, 10.0, 10.0 }, 0.5));
            Assert.False(AutoDimensionRules.KeepInsideCrop(chain, null, 0.5));
            Assert.Equal(5.0, chain.StartY, 6);
        }
    }
}
