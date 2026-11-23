using System;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

public class ArgumentTypeMismatchTests
{
    [Fact]
    public void Array_read_as_a_string_is_a_wrong_argument_type()
    {
        // Field 2026-11-09: manage_materials with a colour sent as [r, g, b].
        var a = JObject.Parse("{\"color\":[176,176,176]}");
        Exception ex = Record.Exception(() => a.Value<string>("color"));
        Assert.NotNull(ex);
        string text = ArgumentTypeMismatch.Explain(ex, "horizun_manage_materials");
        Assert.NotNull(text);
        Assert.Contains("wrong JSON type", text);
        Assert.Contains("horizun://contract/tools/horizun_manage_materials", text);
    }

    [Fact]
    public void Explicit_string_cast_of_an_object_is_a_wrong_argument_type()
    {
        var a = JObject.Parse("{\"name\":{\"x\":1}}");
        Exception ex = Record.Exception(() => { string s = (string)a["name"]; });
        Assert.True(ArgumentTypeMismatch.Is(ex));
    }

    [Fact]
    public void Value_cast_to_object_is_a_wrong_argument_type()
    {
        var a = JObject.Parse("{\"spec\":\"text\"}");
        Exception ex = Record.Exception(() => { JObject o = (JObject)a["spec"]; });
        Assert.True(ArgumentTypeMismatch.Is(ex));
    }

    [Fact]
    public void Other_exceptions_are_not_relabelled()
    {
        Assert.Null(ArgumentTypeMismatch.Explain(new NullReferenceException("x"), "t"));
        Assert.Null(ArgumentTypeMismatch.Explain(new InvalidCastException("Unable to cast object of type 'Wall' to type 'Floor'."), "t"));
        Assert.Null(ArgumentTypeMismatch.Explain(new ArgumentException("Can not convert Array to String."), "t")); // not from Newtonsoft
    }
}
