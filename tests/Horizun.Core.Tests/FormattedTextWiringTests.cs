using System;
using System.IO;
using Xunit;

public class FormattedTextWiringTests
{
    private static string Source(string name)
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null)
        {
            string p = Path.Combine(d.FullName, "src", "Horizun.Revit", "Commands", name);
            if (File.Exists(p)) return File.ReadAllText(p);
            d = d.Parent;
        }
        throw new InvalidOperationException("repository root not found");
    }
    [Fact] public void ReplacementPlansBeforeApplyingAndChecksFormatBeforeRollback()
    {
        string src = Source("PlanimetryOpsCommand.Notes.cs");
        Assert.True(src.IndexOf("FormattedRunEdit.Create", StringComparison.Ordinal) < src.IndexOf("edit.Apply =", StringComparison.Ordinal));
        Assert.Contains("ApplyRunFormat(ft", src);
        Assert.Contains("FormattedRunEdit.Compare", src);
        Assert.Contains("check.Compare(\"formatting\"", src);
        Assert.Contains("VerificationFailureCode = \"verificacion_fallida\"", src);
        string runner = Source("ModelEditRunner.cs");
        Assert.True(runner.IndexOf("if (!check.AllVerified)", StringComparison.Ordinal) < runner.IndexOf("Guard.RollBack(group)", StringComparison.Ordinal));
    }
}
