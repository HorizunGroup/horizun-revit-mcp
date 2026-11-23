using System.Collections.Generic;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;
public class R4ALargeScopeTests
{
    [Fact]
    public void RejectionSuggestsSameCallAndDoesNotRun()
    {
        bool ran = false;
        var args = JObject.Parse("{\"categories\":[\"OST_Walls\"],\"include_links\":false}");
        var verdict = LargeModelToolGate.Run("horizun_query_model", null, new Dictionary<string, long> { ["unknown_scope_guard"] = LargeModelToolGate.ScopeThreshold + 1 }, false, () => { ran = true; return true; });
        Assert.False(verdict.Executed);
        Assert.False(ran);
        var suggested = LargeModelToolGate.SuggestedCall("horizun_query_model", args);
        Assert.Equal("horizun_query_model", (string)suggested["tool"]);
        var expected = (JObject)args.DeepClone(); expected["confirm_large_operation"] = true;
        Assert.True(JToken.DeepEquals(expected, suggested["arguments"]));
        Assert.Null(args["confirm_large_operation"]);
    }
}
