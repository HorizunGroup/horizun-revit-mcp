using Horizun.Revit.Core;
using Xunit;

public sealed class PythonCollectorHazardTests
{
    [Fact]
    public void ThreeArgumentViewLinkCollectorIsFlagged()
    {
        Assert.True(PythonCollectorHazard.HasViewLinkOverload(
            "FilteredElementCollector(doc, viewId, linkId)"));
        Assert.False(PythonCollectorHazard.HasViewLinkOverload(
            "FilteredElementCollector(doc, viewId)"));
        Assert.False(PythonCollectorHazard.HasViewLinkOverload(
            "# FilteredElementCollector(doc, viewId, linkId)\nprint('safe')"));
    }
}
