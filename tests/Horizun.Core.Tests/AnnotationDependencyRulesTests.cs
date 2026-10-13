using System.Collections.Generic;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests;

public sealed class AnnotationDependencyRulesTests
{
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
}
