using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class PythonErrorHelpDataTests
    {
        [Theory]
        [InlineData("NameError", "name 'ElementId' is not defined", "from Autodesk.Revit.DB import ElementId")]
        [InlineData("AttributeError", "'Wall' object has no attribute 'Foo'", "Wall; member: Foo")]
        [InlineData("InvalidOperationException", "modification outside of a transaction", "Transaction")]
        [InlineData("TypeError", "expected ICollection[ElementId]", "List[ElementId]")]
        public void Gives_line_and_specific_hint(string type, string message, string expected)
        {
            var help = PythonErrorHelpData.Build(new JObject { ["type"] = type, ["message"] = message, ["line"] = 2 }, "first\nsecond\nthird");
            Assert.Equal("second", (string)help["code"]);
            Assert.Equal(type, (string)help["type"]);
            Assert.Equal(message, (string)help["message"]);
            Assert.Contains(expected, (string)help["hint"]);
        }
        [Fact]
        public void Unknown_name_is_not_guessed_as_an_api_import()
        {
            var help = PythonErrorHelpData.Build(new JObject { ["type"] = "NameError", ["message"] = "name 'foo' is not defined", ["line"] = 9 }, "x");
            Assert.Equal(JTokenType.Null, help["hint"].Type);
            Assert.Equal(JTokenType.Null, help["code"].Type);
        }
    }
}
