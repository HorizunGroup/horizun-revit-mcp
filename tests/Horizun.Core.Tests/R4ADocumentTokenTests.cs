using Horizun.Revit.Core;
using Xunit;
public class R4ADocumentTokenTests
{
    [Theory]
    [InlineData("open")]
    [InlineData("save")]
    [InlineData("save_as")]
    [InlineData("close")]
    [InlineData("new_project")]
    public void ErrorGuidanceExplainsProtocols(string variant)
    {
        string guidance = DocumentTokenGuidance.For(variant);
        Assert.Contains("save_as do not require a token", guidance);
        Assert.Contains("dry_run=true", guidance);
        Assert.Contains("new_project", guidance);
        Assert.Contains("close", guidance);
    }
}
