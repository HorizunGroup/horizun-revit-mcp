using System.Collections.Generic;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class LargeModelEstimateTests
    {
        [Fact]
        public void Large_unconfirmed_scope_over_threshold_requires_confirmation()
        {
            var estimate = LargeModelEstimate.Create(120000, new Dictionary<string, long> { ["Walls"] = 80000, ["Doors"] = 30000 }, 50000, false, 1000);
            Assert.True(estimate.RequiresConfirmation);
            Assert.Equal(110000, estimate.ScopedElements);
            Assert.Equal(110, estimate.EstimatedSeconds);
            Assert.Equal(80000, estimate.CategoryCounts["Walls"]);
        }

        [Fact]
        public void Confirmation_and_bounded_scope_do_not_refuse()
        {
            var counts = new Dictionary<string, long> { ["Walls"] = 100000 };
            Assert.False(LargeModelEstimate.Create(100000, counts, 50000, true, 1000).RequiresConfirmation);
            Assert.False(LargeModelEstimate.Create(99999, counts, 50000, false, 1000).RequiresConfirmation);
            Assert.False(LargeModelEstimate.Create(100000, counts, 100000, false, 1000).RequiresConfirmation);
        }

        [Fact]
        public void Unknown_model_and_uncalibrated_rate_are_not_fabricated()
        {
            var estimate = LargeModelEstimate.Create(null, new Dictionary<string, long> { ["Walls"] = 100000 }, 50000, false, null);
            Assert.Null(estimate.LargeModel);
            Assert.Null(estimate.EstimatedSeconds);
            Assert.True(estimate.RequiresConfirmation);
            Assert.False(LargeModelEstimate.Create(null, new Dictionary<string, long> { ["Walls"] = 100000 }, 50000, true, null).RequiresConfirmation);
            Assert.False(LargeModelEstimate.Create(null, new Dictionary<string, long> { ["Walls"] = 50000 }, 50000, false, null).RequiresConfirmation);
        }
    }
}
