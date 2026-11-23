using System.IO;
using Xunit;
namespace Horizun.Core.Tests;
public sealed class ScheduleAssociationWiringTests
{
    [Fact]
    public void ScheduleNotesAreStoredAndStalenessCallsVerdict()
    {
        string root = FindRoot();
        string write = File.ReadAllText(Path.Combine(root, "src/Horizun.Revit/Commands/PlanimetryOpsCommand.Notes.cs"));
        string read = File.ReadAllText(Path.Combine(root, "src/Horizun.Revit/Commands/QueryPlanimetryCommand.LeaderReads.cs"));
        Assert.Contains("ScheduleAssociationStorage.Write", write);
        Assert.Contains("ScheduleAssociationStorage.Read", read);
        Assert.Contains("AnnotationDependencyRules.ScheduleVerdict", read);
        Assert.Contains("ScheduleSource.Read", read);
    }
    private static string FindRoot()
    {
        var d = new DirectoryInfo(System.AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "global.json"))) d = d.Parent;
        return d.FullName;
    }
}
