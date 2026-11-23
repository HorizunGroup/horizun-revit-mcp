using System;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

public class PythonInternalTraceTests
{
    [Fact]
    public void WindowsPathKeepsOnlyFileName()
    {
        Assert.Equal("at X() in Bridge.cs:line 42", PythonInternalTrace.RedactPaths(@"at X() in D:\build\agent\src\Bridge.cs:line 42"));
    }

    [Fact]
    public void UncPathKeepsOnlyFileName()
    {
        Assert.Equal("at X() in File.cs:line 42", PythonInternalTrace.RedactPaths(@"at X() in \\server\share\dir\File.cs:line 42"));
    }

    [Fact]
    public void NonArraysAreSafe()
    {
        Assert.Empty(PythonInternalTrace.Advisories(new JObject()));
        Assert.Empty(PythonInternalTrace.Advisories(null));
        Assert.Single(PythonInternalTrace.Advisories(new JArray("warning")));
    }

    [Fact]
    public void TraceDoesNotExposeExceptionMessage()
    {
        var trace = PythonInternalTrace.FromException(new InvalidCastException("model secret"));
        Assert.DoesNotContain("model secret", trace.ToString());
        Assert.Equal("System.InvalidCastException", (string)trace["type"]);
    }

    [Fact]
    public void ReportedJArrayCastKeepsSafeDiagnostic()
    {
        var trace = PythonInternalTrace.FromException(new InvalidCastException("Unable to cast JArray to JToken"));
        Assert.Equal("Invalid cast: JArray to JToken.", (string)trace["message"]);
    }
}
