using System.Collections.Generic;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests;

public sealed class GroupDuplicateRulesTests
{
    [Fact]
    public void SameTypeAndPositionPairEvenAcrossQuantizationBoundary()
    {
        var rows = new[] {
            new GroupDuplicateFact { Id=1, Kind="group", TypeId=10, X=0.99, Y=0, Z=0 },
            new GroupDuplicateFact { Id=2, Kind="group", TypeId=10, X=1.01, Y=0, Z=0 },
            new GroupDuplicateFact { Id=3, Kind="family", TypeId=10, X=1, Y=0, Z=0 },
            new GroupDuplicateFact { Id=4, Kind="group", TypeId=11, X=1, Y=0, Z=0 }
        };
        var pairs = GroupDuplicateRules.Find(rows, 0.1);
        Assert.Single(pairs);
        Assert.Equal(1, pairs[0].FirstId);
        Assert.Equal(2, pairs[0].SecondId);
    }

    [Fact]
    public void GeometryLevelIsUnknownWhenNoLevelIsNear()
    {
        var levels = new[] { new GroupLevelFact { Id=5, ElevationMm=0 },
            new GroupLevelFact { Id=6, ElevationMm=3000 } };
        Assert.Equal(6, GroupDuplicateRules.InferLevel(3010, levels, 300).LevelId);
        Assert.Null(GroupDuplicateRules.InferLevel(2200, levels, 300).LevelId);
    }
}
