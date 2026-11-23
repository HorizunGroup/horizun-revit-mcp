using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests;

public sealed class AnnotationDependencyRulesTests
{
    [Fact]
    public void TableCoverageRequiresExplicitScopeAndEveryScheduleReadable()
    {
        Assert.True(AnnotationDependencyRules.TablesCovered(true, new[] { true, true }));
        Assert.True(AnnotationDependencyRules.TablesCovered(true, new bool[0]));
        Assert.False(AnnotationDependencyRules.TablesCovered(false, new[] { true }));
        Assert.False(AnnotationDependencyRules.TablesCovered(true, new[] { true, false }));
    }
    [Fact]
    public void ScheduleCoverageReadsEmptyAndLargeBodiesWithoutNoteCreationLimits()
    {
        Assert.True(AnnotationDependencyRules.ScheduleBodyAllowed(0, 0, 0, true));
        Assert.True(AnnotationDependencyRules.ScheduleBodyAllowed(1001, 101, 30001, true));
        Assert.False(AnnotationDependencyRules.ScheduleBodyAllowed(1001, 101, 30001, false));
    }
    [Theory]
    [InlineData(null, true, "new", "unknown")]
    [InlineData("old", false, null, "source_deleted")]
    [InlineData("old", true, "old", "current")]
    [InlineData("old", true, "new", "stale")]
    [InlineData("old", null, null, "unknown")]
    public void ScheduleAssociationNeedsEvidence(string storedHash, bool? sourceExists, string currentHash, string expected)
    {
        Assert.Equal(expected, AnnotationDependencyRules.ScheduleVerdict(storedHash, sourceExists, currentHash));
    }
    [Fact]
    public void IncompleteTableCoverageCannotBeClean()
    {
        Assert.False(AnnotationDependencyRules.IsClean(0, 0, true, false));
        Assert.True(AnnotationDependencyRules.IsClean(0, 0, true, true));
        Assert.False(AnnotationDependencyRules.IsClean(1, 0, true, true));
        Assert.False(AnnotationDependencyRules.IsClean(0, 1, true, true));
    }
    [Fact]
    public void UnloadedOriginCannotBeDeclaredCurrent()
    {
        string verdict;
        NoteLeaderRules.StaleReasons("source", null, "old", null, null, false,
            new HashSet<string>(), new HashSet<string>(), out verdict);
        Assert.StartsWith("unknown", verdict);
    }
    [Fact]
    public void DependencyIsReportedInItsOwnViewAndDeduplicated()
    {
        var items = new[] {
            new AnnotationDependencyFact { Id=10, ViewId=100, Kind="tag", References=new List<long>{1,1,2} },
            new AnnotationDependencyFact { Id=11, ViewId=200, Kind="dimension", References=new List<long>{2} }
        };
        var report = AnnotationDependencyRules.ForTargets(items, new HashSet<long>{1,2});
        Assert.Equal(2, report.Count);
        Assert.Equal(100, report[0].ViewId);
        Assert.Equal(2, report[0].Items[0].References.Count);
        Assert.Equal(200, report[1].ViewId);
    }

    [Theory]
    [InlineData("type")]
    [InlineData("all_instances")]
    public void Annotations_on_a_second_copy_cannot_be_lost_or_retargeted(string requestedScope)
    {
        var reference = new long[] { 1, 10 };
        var copy = new long[] { 2, 20 };
        string scope = GroupRedefinitionRules.ResolveScope(requestedScope, 1, out _, "add_members");
        var affected = GroupRedefinitionRules.AffectedAddInstances(scope, reference, new[] { copy }).SelectMany(ids => ids).ToHashSet();
        var annotations = new[]
        {
            new AnnotationDependencyFact { Id = 30, ViewId = 100, Kind = "tag", References = new List<long> { 10 } },
            new AnnotationDependencyFact { Id = 31, ViewId = 200, Kind = "dimension", References = new List<long> { 20, 99 } }
        };
        var views = AnnotationDependencyRules.ForTargets(annotations, affected);
        Assert.Equal(new long[] { 100, 200 }, views.Select(view => view.ViewId));
        Assert.Equal(31, Assert.Single(views[1].Items).Id);

        var dim = new JObject { ["id"] = 31, ["unique_id"] = "dim-uid",
            ["stable_references"] = new JArray("copy-wall:face-1", "outside-wall:face-3") };
        var before = new JObject { ["coverage_complete"] = true,
            ["by_view"] = new JArray(new JObject { ["view_id"] = 200, ["dimensions"] = new JArray(dim) }) };
        JObject After() => (JObject)before.DeepClone();
        Assert.True(AnnotationDependencyRules.ReferencesPreserved(before, After()));
        JObject deleted = After();
        ((JArray)deleted["by_view"]).Clear();
        Assert.False(AnnotationDependencyRules.ReferencesPreserved(before, deleted));
        JObject orphaned = After();
        orphaned["coverage_complete"] = false;
        Assert.False(AnnotationDependencyRules.ReferencesPreserved(before, orphaned));
        JObject retargeted = After();
        retargeted["by_view"][0]["dimensions"][0]["stable_references"][0] = "copy-wall:face-2";
        Assert.False(AnnotationDependencyRules.ReferencesPreserved(before, retargeted));
    }
}
