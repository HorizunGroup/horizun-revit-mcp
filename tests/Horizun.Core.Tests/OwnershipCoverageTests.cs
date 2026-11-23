using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class OwnershipCoverageTests
    {
        [Fact]
        public void Incomplete_stamping_is_never_reported_as_complete()
        {
            Assert.True((bool)HrzOwnershipRules.Report(3, 0, null)["coverage_complete"]);
            Assert.False((bool)HrzOwnershipRules.Report(3, 1, null)["coverage_complete"]);
            Assert.False((bool)HrzOwnershipRules.Report(0, 0, "failed")["coverage_complete"]);
        }
    }
}
