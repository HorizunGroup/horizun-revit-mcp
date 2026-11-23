using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

public class PythonErrorAdviceTests
{
    [Theory]
    [InlineData("AttributeError: 'ElementId' object has no attribute 'IntegerValue'", "removed in Revit 2026", "horizun.id_value(element.Id)")]
    [InlineData("AttributeError: 'ElementId' object has no attribute 'Value'", "before Revit 2024", "horizun.id_value(element.Id)")]
    [InlineData("AttributeError: 'InternalDefinition' object has no attribute 'ParameterType'", "param_data_type", "GetDataType()")]
    [InlineData("AttributeError: 'FamilyInstance' object has no attribute 'WallType'", "FamilyInstance", "of_class")]
    [InlineData("TypeError: iteration over non-sequence of type NoneType", "None", "iter_safe")]
    [InlineData("InvalidOperationException: A sub-transaction can only be active inside an open Transaction", "Transaction", "horizun.transaction")]
    [InlineData("SystemError: Intento de leer o escribir en la memoria protegida. A menudo, esto indica que hay otra memoria dañada.", "unstable", "horizun.transaction")]
    [InlineData("SystemError: Attempted to read or write protected memory.", "horizun_save_document", "GetElement")]
    [InlineData("Exception: One of the conditions for the inputs was not satisfied. Consult the documentation for requirements for each argument.", "inconsistent", "horizun_api_help")]
    [InlineData("error: Parameter name: reference2", "ComputeReferences", "ComputeReferences = True")]
    [InlineData("InvalidOperationException: New project document failed", "template", "new_project")]
    [InlineData("InvalidObjectException: The referenced object is not valid, possibly because it has been deleted", "UniqueId", "GetElement(unique_id)")]
    [InlineData("NameError: name 'Autodesk' is not defined", "scope", "import Autodesk")]
    [InlineData("ValueError: Invalid \\escape", "backslash", "safe_json_path")]
    public void Field_errors_have_cause_and_example(string error, string cause, string example)
    {
        JObject advice = PythonErrorAdvice.Find(error, 2023, 12);
        Assert.Contains(cause, (string)advice["cause"]);
        Assert.Contains(example, (string)advice["example"]);
    }
    [Fact]
    public void Generic_attribute_names_class_member_and_year()
    {
        var advice = PythonErrorAdvice.Find("AttributeError: 'Wall' object has no attribute 'Missing'", 2026);
        Assert.Contains("Wall.Missing", (string)advice["cause"]);
        Assert.Contains("2026", (string)advice["cause"]);
        Assert.Contains("horizun_api_help", (string)advice["example"]);
        Assert.Contains("dir(obj)", (string)advice["example"]);
    }
    [Fact]
    public void Argument_exception_names_line_and_possible_causes()
    {
        var advice = PythonErrorAdvice.Find("ArgumentException: invalid argument", 2023, 17);
        Assert.Contains("line 17", (string)advice["cause"]);
        Assert.Contains("feet", (string)advice["cause"]);
        Assert.Contains("null", (string)advice["cause"]);
    }
    [Fact]
    public void Attribute_error_with_hint_but_unrecognized_message_still_exposes_advice()
    {
        var summary = ScriptErrorSummary.Build("AttributeError: member unavailable", "pass", null);
        var detail = new JObject { ["script_error"] = summary };
        PythonErrorAdvice.Attach(detail, "AttributeError: member unavailable", 2026);
        Assert.NotNull(detail["script_error"]["hint"]);
        Assert.Contains("member unavailable", (string)detail["advice"]["cause"]);
        Assert.Contains("horizun_api_help", (string)detail["advice"]["example"]);
    }
    [Fact]
    public void Unknown_error_has_no_guessed_advice() => Assert.Null(PythonErrorAdvice.Find("unrelated failure"));
}
