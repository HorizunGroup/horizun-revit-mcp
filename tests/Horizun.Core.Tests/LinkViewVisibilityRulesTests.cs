using Horizun.Revit.Core;
using Xunit;

public sealed class LinkViewVisibilityRulesTests
{
    [Fact]
    public void LinkedElementMustIntersectMeasuredViewZBand()
    {
        Assert.True(LinkViewVisibilityRules.OverlapsZ(10, 12, 9, 11));
        Assert.False(LinkViewVisibilityRules.OverlapsZ(12, 14, 9, 11));
        Assert.False(LinkViewVisibilityRules.OverlapsZ(double.NaN, 14, 9, 11));
    }
}
