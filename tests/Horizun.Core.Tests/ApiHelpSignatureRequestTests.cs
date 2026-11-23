using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class ApiHelpSignatureRequestTests
    {
        [Theory]
        [InlineData("{}")]
        [InlineData("{ 'operation':'describe', 'member_filter':'Property' }")]
        [InlineData("{ 'operation':'check_signature', 'member_filter':'.ctor', 'argument_types':[] }")]
        public void Legacy_describe_and_explicit_zero_argument_check_are_accepted(string json)
            => Assert.Null(ApiHelpRules.ValidateSignatureRequest(JObject.Parse(json)));

        [Theory]
        [InlineData("{ 'operation':null }")]
        [InlineData("{ 'operation':'invoke' }")]
        [InlineData("{ 'operation':1 }")]
        [InlineData("{ 'operation':'check_signature', 'argument_types':[] }")]
        [InlineData("{ 'operation':'check_signature', 'member_filter':'Del', 'argument_types':null }")]
        [InlineData("{ 'operation':'check_signature', 'member_filter':'Delete()', 'argument_types':[] }")]
        [InlineData("{ 'operation':'check_signature', 'member_filter':'Delete', 'argument_types':[null] }")]
        [InlineData("{ 'operation':'check_signature', 'member_filter':'Delete', 'argument_types':[1] }")]
        [InlineData("{ 'operation':'check_signature', 'member_filter':'Delete', 'argument_types':[' '] }")]
        public void Direct_bridge_calls_cannot_smuggle_values_or_implicit_null(string json)
            => Assert.NotNull(ApiHelpRules.ValidateSignatureRequest(JObject.Parse(json)));

        [Fact]
        public void Argument_count_limit_is_enforced_without_truncation()
        {
            var request = new JObject { ["operation"] = "check_signature", ["member_filter"] = ".ctor", ["argument_types"] = new JArray() };
            var types = (JArray)request["argument_types"];
            for (int i = 0; i < 32; i++) types.Add("System.Int32");
            Assert.Null(ApiHelpRules.ValidateSignatureRequest(request));
            types.Add("System.Int32");
            Assert.NotNull(ApiHelpRules.ValidateSignatureRequest(request));
        }
    }
}
