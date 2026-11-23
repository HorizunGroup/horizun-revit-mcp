using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class LargeModelRulesTests
    {
        [Fact]
        public void Threshold_is_inclusive_and_unknown_counts_are_not_small()
        {
            Assert.False(LargeModelRules.IsLarge(99999));
            Assert.True(LargeModelRules.IsLarge(100000));
            Assert.Null(LargeModelRules.IsLarge(null));
            Assert.Equal(100000, LargeModelRules.ElementThreshold);
        }
    }
}
