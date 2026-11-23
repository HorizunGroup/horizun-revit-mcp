using System;
using Horizun.Revit.Core;
using Xunit;

public class NewProjectFailureStageTests
{
    [Theory]
    [InlineData(false, "create")]
    [InlineData(true, "save")]
    public void InjectedExceptionIdentifiesTheFailingStage(bool saving, string expected)
    {
        var failure = new InvalidOperationException("injected Revit failure");
        Assert.Equal(expected, NewProjectRules.FailureStage(saving, failure));
    }
}
