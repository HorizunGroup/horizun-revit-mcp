// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// A failed change says "rolled back" only when the rollback was confirmed
// (audit 2026-10-08, 6.18).
// -----------------------------------------------------------------------------
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class FailedChangeWordingTests
    {
        [Fact]
        public void Rolled_back_is_said_only_for_a_confirmed_rollback()
        {
            Assert.Contains("was rolled back", PlanFailure.FailedChange("route", "rolled_back", "x"));
            string uncertain = PlanFailure.FailedChange("route", "uncertain", "x");
            Assert.DoesNotContain("was rolled back", uncertain);
            Assert.Contains("could NOT be confirmed", uncertain);
            Assert.Contains("uncertain", uncertain);
        }
    }
}
