// E6: a capture after a model change that returns the same bytes is retried once, then flagged.
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class CaptureFreshnessTests
    {
        [Fact]
        public void First_capture_and_changed_bytes_are_fresh()
        {
            Assert.Equal(CaptureFreshness.FirstCapture, CaptureFreshness.Decide(null, null, "a", 3, false));
            Assert.Equal(CaptureFreshness.Fresh, CaptureFreshness.Decide("a", 1, "b", 3, false));
        }

        [Fact]
        public void Same_bytes_with_no_change_is_simply_unchanged()
            => Assert.Equal(CaptureFreshness.UnchangedModel, CaptureFreshness.Decide("a", 3, "a", 3, false));

        [Fact]
        public void Same_bytes_after_a_change_is_retried_then_flagged()
        {
            Assert.Equal(CaptureFreshness.RetryNeeded, CaptureFreshness.Decide("a", 1, "a", 3, false));
            Assert.Equal(CaptureFreshness.PossiblyStale, CaptureFreshness.Decide("a", 1, "a", 3, true));
        }
    }
}
