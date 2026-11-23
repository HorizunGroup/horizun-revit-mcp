// horizun_capture_view framing and image size, without Revit.
// INFORME_P6 §6.4: element_ids with Structural Rebar framed a +-100 ft box and came back as a
// 900 x 9000 px image; the coupler alone (no bars) framed correctly.
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class CaptureFrameRulesTests
    {
        [Fact]
        public void A_bar_whose_model_box_is_far_larger_than_its_geometry_is_framed_by_its_geometry()
        {
            // a 1.2 m bar reported with a +-100 ft model box
            double[] model = { -100, -100, -100, 100, 100, 100 };
            double[] geometry = { 10, 20, 3, 13.9, 20.05, 3.05 };
            double[] used = CaptureFrameRules.ChooseBounds(model, geometry, out string basis);
            Assert.Equal(geometry, used);
            Assert.Equal("geometry", basis);
        }

        [Fact]
        public void An_ordinary_element_keeps_its_model_box()
        {
            double[] model = { 0, 0, 0, 10, 1, 9 };
            double[] geometry = { 0.01, 0.0, 0.0, 9.99, 1.0, 9.0 };
            Assert.Equal(model, CaptureFrameRules.ChooseBounds(model, geometry, out string basis));
            Assert.Equal("model_box", basis);
        }

        [Fact]
        public void No_geometry_keeps_the_model_box_and_no_model_box_takes_the_geometry()
        {
            double[] model = { 0, 0, 0, 1, 1, 1 };
            Assert.Equal(model, CaptureFrameRules.ChooseBounds(model, null, out _));
            double[] geometry = { 0, 0, 0, 2, 2, 2 };
            Assert.Equal(geometry, CaptureFrameRules.ChooseBounds(null, geometry, out string basis));
            Assert.Equal("geometry", basis);
            Assert.Null(CaptureFrameRules.ChooseBounds(null, null, out basis));
        }

        [Theory]
        [InlineData(900, 9000, 900, true)]    // the reported image: fitted by width, ten times taller
        [InlineData(1600, 1601, 1600, true)]
        [InlineData(1600, 900, 1600, false)]  // landscape fits by width already
        [InlineData(1600, 1600, 1600, false)]
        [InlineData(0, 0, 1600, false)]       // unreadable header: nothing to judge
        public void A_portrait_export_taller_than_pixel_size_is_refitted_by_height(int w, int h, int size, bool refit)
            => Assert.Equal(refit, CaptureFrameRules.NeedsHeightFit(w, h, size));

        [Theory]
        [InlineData(900, 9000, 900, false)]
        [InlineData(90, 900, 900, true)]
        [InlineData(1600, 900, 1600, true)]
        public void The_longest_side_never_exceeds_pixel_size(int w, int h, int size, bool ok)
            => Assert.Equal(ok, CaptureFrameRules.WithinPixelSize(w, h, size));
    }
}
