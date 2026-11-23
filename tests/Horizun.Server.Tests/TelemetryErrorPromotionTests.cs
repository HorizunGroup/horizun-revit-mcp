using Horizun.Server;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Server.Tests
{
    public class TelemetryErrorPromotionTests
    {
        [Fact]
        public void Generic_old_code_can_be_promoted_by_new_structured_transport_evidence()
        {
            var result = McpResult.Error("Operation aborted", null, null,
                new JObject { ["error_code"] = "error_no_clasificado" });
            var detail = (JObject)result["structuredContent"];
            detail["code"] = "revit_transport_failed";
            detail["exception_type"] = "OperationCanceledException";
            McpResult.StampError(result);
            Assert.Equal("cancelado", (string)detail["error_code"]);
            Assert.DoesNotContain("error_no_clasificado", (string)result["content"][0]["text"]);
            string once = result.ToString();
            McpResult.StampError(result);
            Assert.Equal(once, result.ToString());
        }

        [Fact]
        public void Promotion_keeps_the_exception_type_discovered_in_a_legacy_traceback()
        {
            var result = McpResult.Error("Unrecognised failure", null, null,
                new JObject { ["error_code"] = "error_no_clasificado" });
            result["content"][0]["text"] = "TypeError: expected a CLR collection\n\nerror_code: error_no_clasificado";
            McpResult.StampError(result);
            Assert.Equal("python_excepcion", (string)result["structuredContent"]["error_code"]);
            Assert.Equal("TypeError", (string)result["structuredContent"]["exception_type"]);
            Assert.Contains("error_code: python_excepcion (TypeError)", (string)result["content"][0]["text"]);
            string once = result.ToString();
            McpResult.StampError(result);
            Assert.Equal(once, result.ToString());
        }
    }
}
