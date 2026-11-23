using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class TelemetryErrorRegressionTests
    {
        [Theory]
        [InlineData("OperationCanceledException", "cancelado")]
        [InlineData("TaskCanceledException", "cancelado")]
        [InlineData("TimeoutException", "tiempo_agotado")]
        public void Transport_exception_type_wins_over_incidental_prose(string type, string expected)
        {
            var detail = new JObject { ["code"] = "revit_transport_failed", ["exception_type"] = type };
            var result = ErrorCodes.Classify("The operation failed: the connection is no longer available", detail);
            Assert.Equal(expected, result.Code);
            Assert.Equal(type, result.ExceptionType);
        }

        [Theory]
        [InlineData("Operation cancelled by the user.", "cancelado")]
        [InlineData("No active view is available.", "sin_vista")]
        [InlineData("The target document is not the active document.", "documento_no_activo")]
        [InlineData("TypeError: expected ElementId, got int", "python_excepcion")]
        [InlineData("AttributeError: object has no attribute 'Name'", "python_excepcion")]
        [InlineData("NameError: name 'System' is not defined", "python_excepcion")]
        public void Known_causes_have_a_stable_class(string message, string expected)
            => Assert.Equal(expected, ErrorCodes.Classify(message, null).Code);

        [Theory]
        [InlineData("TypeError: expected ICollection[ElementId], got list", "List[ElementId]")]
        [InlineData("TypeError: Multiple targets could match", "horizun_api_help")]
        [InlineData("AttributeError: 'BuiltInCategory' object has no attribute 'OST_walls'", "horizun_api_help")]
        [InlineData("NameError: name 'System' is not defined", "import System")]
        [InlineData("NameError: name 'arguments' is not defined", "json.loads(HORIZUN_ARGS_JSON)")]
        [InlineData("ArgumentException: input geometry is invalid", "horizun_api_help")]
        public void Python_api_errors_offer_actionable_help(string failure, string expected)
        {
            JObject summary = ScriptErrorSummary.Build("Traceback (most recent call last):\n  File \"<string>\", line 1, in <module>\n" + failure, "pass", null);
            Assert.Contains(expected, (string)summary["hint"]);
        }

        [Fact]
        public void None_checks_stay_more_specific_than_the_Name_workaround()
        {
            JObject summary = ScriptErrorSummary.Build("AttributeError: 'NoneType' object has no attribute 'Name'", "pass", null);
            Assert.Contains("returned None", (string)summary["hint"]);
            Assert.DoesNotContain("Element.Name.GetValue", (string)summary["hint"]);
        }
    }
}
