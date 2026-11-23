using Horizun.Contracts;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Server.Tests
{
    public class R9AQueryFieldsTests
    {
        [Fact]
        public void ElementIdProjectionPassesThePublishedContract()
        {
            var args = JObject.Parse("{return_fields:['element_id']}");
            Assert.Empty(ContractSchemaCheck.Validate(args, Contract.Find("horizun_query_model").InputSchema));
        }

        [Fact]
        public void UnknownProjectionOffersAValidCallWithoutDiscardingOtherArguments()
        {
            var args = JObject.Parse("{categories:['OST_Walls'],return_fields:['name','bogus']}");
            JObject result = UndeclaredArgumentsTests.CallOverStdio("horizun_query_model", args);
            Assert.Equal("esquema_invalido", (string)result["structuredContent"]["error_code"]);
            var suggestion = result["structuredContent"]["suggested_call"];
            Assert.Equal("horizun_query_model", (string)suggestion["tool"]);
            Assert.Equal("OST_Walls", (string)suggestion["arguments"]["categories"][0]);
            Assert.Equal("name", (string)suggestion["arguments"]["return_fields"][0]);
            Assert.Single((JArray)suggestion["arguments"]["return_fields"]);
        }
    }
}
