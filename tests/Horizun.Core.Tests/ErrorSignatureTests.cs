using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

public class ErrorSignatureTests
{
    // The stack exactly as revit-2025.log recorded it on 2026-11-09 (Spanish UI: "en" instead of "at").
    private const string SpanishStack =
        "   en Newtonsoft.Json.Linq.Extensions.Convert[T,U](T token)\n" +
        "   en Newtonsoft.Json.Linq.JToken.Value[T](Object key)\n" +
        "   en Horizun.Revit.Commands.ManageMaterialsCommand.CheckColours(JObject a)\n" +
        "   en Horizun.Revit.Commands.ManageMaterialsCommand.PlanAll(Document doc, JArray raw, String& error)\n" +
        "   en Horizun.Revit.Commands.ManageMaterialsCommand.Execute(UIApplication app, String paramsJson)\n" +
        "   en Horizun.Revit.Core.LargeModelDispatch.Execute(UIApplication app, ICommand command, String json)\n" +
        "   en Horizun.Revit.Core.Dispatcher.Execute(UIApplication app)";

    [Fact]
    public void Names_the_type_and_the_innermost_horizun_methods_only()
    {
        string s = ErrorSignature.From("InvalidCastException", SpanishStack);
        Assert.Equal("InvalidCastException \uFF20 ManageMaterials.CheckColours < ManageMaterials.PlanAll < " +
                     "ManageMaterials.Execute < LargeModelDispatch.Execute", s);
        Assert.DoesNotContain("Newtonsoft", s);
        Assert.DoesNotContain("paramsJson", s);
    }

    [Fact]
    public void English_stack_and_compiler_generated_names_stay_identifiers()
    {
        string stack = "   at Horizun.Revit.Commands.TransformElementsCommand.<Execute>b__12_0(Element e) in C:\\Users\\someone\\src\\x.cs:line 4\n";
        string s = ErrorSignature.From("NullReferenceException", stack);
        Assert.StartsWith("NullReferenceException \uFF20 TransformElements.", s);
        Assert.DoesNotContain("Users", s);
        Assert.DoesNotContain(":", s.Substring(s.IndexOf('\uFF20')));
    }

    [Fact]
    public void No_word_reaches_forty_characters_so_the_receiver_keeps_the_message()
    {
        string stack = "   at Horizun.Revit.Commands.AVeryLongCommandNameThatKeepsGoingAndGoingCommand.AMethodNameThatIsFarTooLongToKeep(Object o)";
        string s = ErrorSignature.From("SomeExceptionTypeWhoseNameIsLongerThanFortyCharactersException", stack);
        Assert.DoesNotMatch(@"\S{40,}", s);
        Assert.DoesNotMatch(@"\S{40,}", "error_signature: " + s);
    }

    // The downstream reader 0.4.81 keeps the line whole only when it matches this exactly; anything else is scrubbed like the message.
    private const string ReceiverRule = @"^error_signature: [A-Za-z]+( \uFF20 [A-Za-z0-9_.]+( < [A-Za-z0-9_.]+){0,3})?$";

    [Theory]
    [InlineData("InvalidCastException", SpanishStack)]
    [InlineData("Win32Exception", "   at Horizun.Revit.Commands.ExportCommand.<Run>b__3_0(Object o)\n   at Horizun.Revit.Core.Dispatcher..ctor()")]
    [InlineData("KeyNotFoundException", "   at System.Collections.Generic.Dictionary`2.get_Item(TKey key)")]
    [InlineData("SomeExceptionTypeWhoseNameIsLongerThanFortyCharactersException",
                "   at Horizun.Revit.Commands.AVeryLongCommandNameThatKeepsGoingAndGoingCommand.AMethodNameThatIsFarTooLongToKeep(Object o)")]
    public void The_line_matches_the_receiver_rule_exactly(string type, string stack)
    {
        string line = "error_signature: " + ErrorSignature.From(type, stack);
        Assert.Matches(ReceiverRule, line);
        Assert.DoesNotMatch(@"[A-Za-z0-9_-]{40,}", line);
    }

    [Fact]
    public void Without_horizun_frames_only_the_type_is_named()
        => Assert.Equal("KeyNotFoundException", ErrorSignature.From("KeyNotFoundException", "   at System.Collections.Generic.Dictionary`2.get_Item(TKey key)"));

    [Fact]
    public void The_error_code_line_carries_the_signature_on_its_own_line()
    {
        var d = new JObject { ["error_code"] = "error_interno", ["exception_type"] = "InvalidCastException",
                              ["error_signature"] = "InvalidCastException \uFF20 A.B" };
        Assert.Equal("error_code: error_interno (InvalidCastException)\nerror_signature: InvalidCastException \uFF20 A.B", ErrorCodes.Line(d));
    }
}
