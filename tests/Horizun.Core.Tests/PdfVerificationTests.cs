using System.Collections.Generic;
using Horizun.Revit.Core;
using Xunit;

public class PdfVerificationTests
{
    [Theory]
    [InlineData("", 2, 2, "missing_files")]
    [InlineData("other.pdf", 2, 2, "missing_files")]
    [InlineData("a.pdf", 2, 1, "page_count")]
    [InlineData("a.pdf", 2, 2, null)]
    public void ReportsPackageEvidence(string name, int expected, int found, string rule)
    {
        var files = new List<KeyValuePair<string,long>>();
        if (name != "") files.Add(new KeyValuePair<string,long>(name, 42));
        var result = PdfVerification.Compare(new[] { "a.pdf" }, files, expected, found);
        Assert.Equal(rule, (string)result["rule"]);
        Assert.Equal(name == "other.pdf" ? 1 : 0, ((Newtonsoft.Json.Linq.JArray)result["unexpected"]).Count);
        Assert.Equal(expected, (int)result["pages_expected"]);
    }
}
