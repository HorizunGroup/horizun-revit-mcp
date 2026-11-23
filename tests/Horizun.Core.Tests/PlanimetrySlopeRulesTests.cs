using System;
using Horizun.Revit.Core;
using Xunit;

public class PlanimetrySlopeRulesTests
{
    [Fact]
    public void TwoElevationsDetermineDirectedSlope()
    {
        Assert.Equal(0.5, PlanimetrySlopeRules.RatioFromHeights(0, 4, 2, 4), 9);
        Assert.Equal(-0.5, PlanimetrySlopeRules.RatioFromHeights(0, 4, 4, 2), 9);
        Assert.Throws<ArgumentException>(() => PlanimetrySlopeRules.RatioFromHeights(0, 0, 2, 4));
    }

    [Fact]
    public void PlaneElevationsProveBothEndsAndDirection()
    {
        // z = 2 + 0.5*x; the upward face normal points toward decreasing x.
        double[] a = PlanimetrySlopeRules.ElevationsOnPlane(0, 0, 4, 0, 0, 2, -0.4472135955, 0, 0.894427191);
        Assert.Equal(2, a[0], 8);
        Assert.Equal(4, a[1], 8);
        Assert.True(PlanimetrySlopeRules.Matches(a[0], a[1], 2, 4, 0.001));
        Assert.False(PlanimetrySlopeRules.Matches(a[0], a[1], 4, 2, 0.001));
    }
}
