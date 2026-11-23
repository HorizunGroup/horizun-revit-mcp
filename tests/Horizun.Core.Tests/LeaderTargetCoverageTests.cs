using System.IO;
using Xunit;

namespace Horizun.Core.Tests;

public class LeaderTargetCoverageTests
{
    [Fact]
    public void NestedReferenceCannotBeSkippedInFavourOfAFartherHit()
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "AGENTS.md"))) dir = dir.Parent;
        string source = File.ReadAllText(Path.Combine(dir.FullName, "src", "Horizun.Revit", "Commands", "LeaderTargetReader.cs"));
        Assert.Contains("nested reference cannot be resolved", source);
        Assert.Contains("hit.Coverage = \"partial\"", source);
        Assert.Contains("return hit; // Do not substitute a farther hit", source);
    }
}
