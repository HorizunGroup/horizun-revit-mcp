using System;
using System.IO;
using Xunit;

public sealed class GroupPatternPlacementSourceTests
{
    [Fact]
    public void TrialsStartAtTheTargetSoTheyDoNotCollideWithThePattern()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !Directory.Exists(Path.Combine(root.FullName, "src"))) root = root.Parent;
        Assert.NotNull(root);
        string source = File.ReadAllText(Path.Combine(root.FullName, "src", "Horizun.Revit", "Commands", "ManageGroupsCommand.cs"));
        Assert.Contains("doc.Create.PlaceGroup(p.TargetPoint, p.Type)", source);
        Assert.DoesNotContain("ElementTransformUtils.MoveElement(doc, placed.Id, p.TargetPoint - p.PositionBefore)", source);
        Assert.Contains("ElementTransformUtils.MirrorElements(doc, new[] { candidate.Id }, plane, false)", source);
        Assert.DoesNotContain("ElementTransformUtils.MirrorElement(doc, candidate.Id", source);
    }
}
