using System;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests;
public class LinkChainTransformTests
{
    [Fact] public void ReaderDescendsReferencesAndComposesLinkTransforms()
    {
        var dir = new System.IO.DirectoryInfo(System.IO.Directory.GetCurrentDirectory());
        while (dir != null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "AGENTS.md"))) dir = dir.Parent;
        string source = System.IO.File.ReadAllText(System.IO.Path.Combine(dir.FullName, "src", "Horizun.Revit", "Commands", "LeaderTargetReader.cs"));
        Assert.Contains("CreateReferenceInLink()", source);
        Assert.Contains("LinkChainTransform.Compose", source);
        string writer = System.IO.File.ReadAllText(System.IO.Path.Combine(dir.FullName, "src", "Horizun.Revit", "Commands", "PlanimetryOpsCommand.Notes.cs"));
        Assert.Contains("hit.ElementDocument == it.SourceDoc", writer);
    }
    private static double[,] Move(double x, double y) => new double[,] { {1,0,0,x}, {0,1,0,y}, {0,0,1,0}, {0,0,0,1} };
    [Fact] public void OneLevel() => Assert.Equal(5, LinkChainTransform.Compose(new[] { Move(5, 2) })[0,3]);
    [Fact] public void TwoLevels() => Assert.Equal(8, LinkChainTransform.Compose(new[] { Move(5, 2), Move(3, 4) })[0,3]);
    [Fact] public void RotationThenTranslation()
    {
        var rotation = new double[,] { {0,-1,0,0}, {1,0,0,0}, {0,0,1,0}, {0,0,0,1} };
        var composed = LinkChainTransform.Compose(new[] { rotation, Move(3, 4) });
        Assert.Equal(-4, composed[0,3]); Assert.Equal(3, composed[1,3]);
    }
    [Fact] public void NearEqualHitsAreAmbiguous()
    {
        Assert.True(LinkChainTransform.IsTie(2.0, 2.0 + 0.5 / 304.8));
        Assert.False(LinkChainTransform.IsTie(2.0, 2.0 + 2.0 / 304.8));
    }
    [Fact] public void IdenticalChainsHaveIdenticalCoordinates()
    {
        var a = LinkChainTransform.Compose(new[] { Move(2, 3), Move(1, 1) });
        var b = LinkChainTransform.Compose(new[] { Move(3, 4) });
        for (int i = 0; i < 4; i++) for (int j = 0; j < 4; j++) Assert.Equal(a[i,j], b[i,j]);
    }
}
