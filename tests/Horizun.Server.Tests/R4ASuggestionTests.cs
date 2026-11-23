using System.Collections.Generic;
using Horizun.Server;
using Xunit;
using Newtonsoft.Json.Linq;
public class R4ASuggestionTests
{
    [Fact]
    public void UnknownTimeoutReturnsCorrectedCallPreservingOtherArguments()
    {
        var args = JObject.Parse("{target_document:'Example',timeout_ms:123}");
        var detail = UndeclaredArguments.Refusal("horizun_query_model", new List<string> { "timeout_ms" }, args)["detail"];
        Assert.Equal("Example", (string)detail["suggested_call"]["arguments"]["target_document"]);
        Assert.Null(detail["suggested_call"]["arguments"]["timeout_ms"]);
    }
    [Theory]
    [InlineData("horizun_open_document", "dry_run", "horizun_document_session", "dry_run")]
    [InlineData("horizun_open_document", "operation", "horizun_document_session", "operation")]
    [InlineData("horizun_query_model", "category", "horizun_query_model", "categories")]
    [InlineData("horizun_execute_python", "timeout_ms", "horizun_execute_python", "timeout_s")]
    public void RefusalNamesContractDestination(string tool, string wrong, string expectedTool, string expectedArgument)
    {
        var detail = UndeclaredArguments.Refusal(tool, new List<string> { wrong })["detail"];
        Assert.Equal(expectedTool, (string)detail["suggested_call"]["tool"]);
        Assert.Equal(expectedArgument, (string)detail["suggested_call"]["argument"]);
        Assert.False((bool)detail["write_started"]);
    }
}
