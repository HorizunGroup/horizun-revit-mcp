using Horizun.Revit.Core;
using Xunit;

public sealed class GroupAnnotationSessionTests
{
    [Fact]
    public void ReportsOnlyTheActiveDocumentAndCountsLossesSeparately()
    {
        var ledger = new GroupAnnotationSession();
        ledger.Record("A", "change_group_type", 2, 1, 1);
        ledger.Record("B", "change_group_type", 9, 9, 9);
        ledger.Record("A", "set_group_level", 0, 0, 0);
        var report = ledger.Read("A");
        Assert.Equal(2, report.Operations);
        Assert.Equal(2, report.TagsAtRisk);
        Assert.Equal(1, report.TagsRecreated);
        Assert.Equal(1, report.DimensionsLost);
    }
}
