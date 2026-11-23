using System.Collections.Generic;
using Horizun.Revit.Core;
using Xunit;

public class RoomBoundaryRulesTests
{
    private static BoundaryEdge Edge(double x0, double y0, double x1, double y1) =>
        new BoundaryEdge(new[] { x0, y0 }, new[] { (x0 + x1) / 2, (y0 + y1) / 2 }, new[] { x1, y1 });

    [Fact]
    public void ClosedRectangularLoopHasNoGapsOrDuplicates()
    {
        var loop = new List<BoundaryEdge> { Edge(0, 0, 4, 0), Edge(4, 0, 4, 3),
            Edge(4, 3, 0, 3), Edge(0, 3, 0, 0) };
        RoomBoundaryAssessment result = RoomBoundaryRules.Assess(new[] { loop }, 0.001);
        Assert.True(result.Closed);
        Assert.Equal(0, result.GapCount);
        Assert.Equal(0, result.DuplicatePairs);
    }

    [Fact]
    public void GapAndCoincidentReverseSegmentAreDistinctFindings()
    {
        var loop = new List<BoundaryEdge> { Edge(0, 0, 4, 0), Edge(4, 0, 4, 3),
            Edge(4, 3, 0, 3), Edge(0, 3, 0, 0.1), Edge(4, 0, 0, 0) };
        RoomBoundaryAssessment result = RoomBoundaryRules.Assess(new[] { loop }, 0.001);
        Assert.False(result.Closed);
        Assert.True(result.GapCount > 0);
        Assert.Equal(1, result.DuplicatePairs);
    }

    [Fact]
    public void SameEndpointsWithDifferentArcBulgeAreNotDuplicates()
    {
        var first = new BoundaryEdge(new[] { 0.0, 0.0 }, new[] { 1.0, 1.0 }, new[] { 2.0, 0.0 });
        var second = new BoundaryEdge(new[] { 2.0, 0.0 }, new[] { 1.0, -1.0 }, new[] { 0.0, 0.0 });
        Assert.Equal(0, RoomBoundaryRules.Assess(new[] { new List<BoundaryEdge> { first, second } }, 0.001).DuplicatePairs);
    }
}
