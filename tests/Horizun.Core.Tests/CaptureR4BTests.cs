using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class CaptureR4BTests
    {
        [Fact]
        public void Rejects_template_2d_orientation_and_out_of_range_pixels()
        {
            Assert.Contains("template", CaptureFrameRules.ValidateCapture(true, false, false, 1600));
            Assert.Contains("orthographic 3D", CaptureFrameRules.ValidateCapture(false, true, false, 1600));
            Assert.Contains("8192", CaptureFrameRules.ValidateCapture(false, false, false, 8193));
            Assert.Null(CaptureFrameRules.ValidateCapture(false, true, true, 8192));
        }

        [Fact]
        public void Missing_element_and_empty_geometry_have_explanations()
        {
            Assert.Contains("does not exist: 42", CaptureFrameRules.ElementProblem(42, false, false));
            Assert.Contains("nothing can be framed", CaptureFrameRules.ElementProblem(42, true, false));
            Assert.Null(CaptureFrameRules.ElementProblem(42, true, true));
            Assert.Contains("no candidate elements", CaptureFrameRules.EmptyViewProblem(true, 0));
            Assert.Null(CaptureFrameRules.EmptyViewProblem(false, 0));
            Assert.Null(CaptureFrameRules.EmptyViewProblem(true, 1));
        }
    }
}
