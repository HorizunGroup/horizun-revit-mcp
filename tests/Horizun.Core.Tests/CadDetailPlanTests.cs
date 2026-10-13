using System;
using System.Collections.Generic;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests;

public sealed class CadDetailPlanTests
{
    private static readonly CadPoint O = new CadPoint(100, 200, 0);
    private static readonly CadPoint X = new CadPoint(1, 0, 0);
    private static readonly CadPoint Y = new CadPoint(0, 1, 0);
    private static readonly CadPoint Z = new CadPoint(0, 0, 1);

    [Fact]
    public void NestedLineAndArcBecomeNativeActionsWithMeasuredCoverage()
    {
        var line = new CadIrEntity { Id = "a", Kind = CadEntityKind.Line, Layer = "L1",
            BlockPath = new List<string> { "outer", "inner" } };
        line.Points.Add(new CadPoint(100, 200)); line.Points.Add(new CadPoint(150, 200));
        var arc = new CadIrEntity { Id = "b", Kind = CadEntityKind.Arc, Layer = "L2",
            Arc = new CadArcFact("arc", new CadPoint(200, 200), 10,
                new CadPoint(210, 200), new CadPoint(200, 210), new CadPoint(207, 207), "L2", 4, 1) };
        var hatch = new CadIrEntity { Id = "c", Kind = CadEntityKind.Hatch, Layer = "L3" };
        var result = CadDetailPlanRules.Build(new[] { line, arc, hatch }, 42, O, X, Y, Z, 0, 100,
            new Dictionary<string, long> { ["L1"] = 10, ["L2"] = 11 });
        Assert.Equal(3, result.SourceEntities);
        Assert.Equal(2, result.PlannedEntities);
        Assert.Single(result.Gaps);
        Assert.Equal("unconverted_hatch", result.Gaps[0].Reason);
        Assert.Equal("create_detail_line", (string)result.Actions[0]["operation"]);
        Assert.Equal("L1", result.ActionSources[0].Layer);
        Assert.Equal(10, (long)result.Actions[0]["line_style_id"]);
        Assert.Equal(2, result.ActionSources[0].BlockPath.Count);
        Assert.Equal("create_detail_arc", (string)result.Actions[1]["operation"]);
        Assert.Equal(0, (double)result.Actions[0]["start"][0]);
    }

    [Fact]
    public void OutOfPlaneAndPageTruncationNeverClaimComplete()
    {
        var a = new CadIrEntity { Id = "a", Kind = CadEntityKind.Line };
        a.Points.Add(new CadPoint(0, 0, 20)); a.Points.Add(new CadPoint(1, 0, 20));
        var b = new CadIrEntity { Id = "b", Kind = CadEntityKind.Line };
        b.Points.Add(new CadPoint(0, 0)); b.Points.Add(new CadPoint(1, 0));
        var result = CadDetailPlanRules.Build(new[] { a, b }, 42,
            new CadPoint(0, 0), X, Y, Z, 0, 1);
        Assert.False(result.Complete);
        Assert.Equal(0, result.PlannedEntities);
        Assert.Equal("out_of_view_plane", result.Gaps[0].Reason);
    }
}
