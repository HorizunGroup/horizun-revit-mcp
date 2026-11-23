using System.Collections.Generic;
using Horizun.Revit.Core;
using Xunit;

public class WallAttachmentRulesTests
{
    [Fact]
    public void EndElevationsAreSelectedAtEachEndOfSlopedProfile()
    {
        var vertices = new List<double[]>
        {
            new[] {0.0, -0.1, 0.0}, new[] {0.0, -0.1, 2.0},
            new[] {0.0, 0.1, 0.0}, new[] {0.0, 0.1, 2.0},
            new[] {4.0, -0.1, 0.0}, new[] {4.0, -0.1, 4.0},
            new[] {4.0, 0.1, 0.0}, new[] {4.0, 0.1, 4.0},
        };
        double?[] top = WallAttachmentRules.EndElevations(vertices, new[] {0.0, 0.0}, new[] {4.0, 0.0}, 0.2, true);
        Assert.Equal(2, top[0]);
        Assert.Equal(4, top[1]);
        double?[] baseLevels = WallAttachmentRules.EndElevations(vertices, new[] {0.0, 0.0}, new[] {4.0, 0.0}, 0.2, false);
        Assert.Equal(0, baseLevels[0]);
        Assert.Equal(0, baseLevels[1]);
    }

    [Fact]
    public void MissingEndGeometryIsUnmeasured()
    {
        double?[] heights = WallAttachmentRules.EndElevations(new List<double[]> {new[] {0.0, 0.0, 2.0}},
            new[] {0.0, 0.0}, new[] {4.0, 0.0}, 0.2, true);
        Assert.Equal(2, heights[0]);
        Assert.Null(heights[1]);
    }
}
