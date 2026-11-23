using Horizun.Contracts;
using Xunit;
using Xunit.Abstractions;

namespace Horizun.Server.Tests
{
    public class LargeModelContractTests
    {
        private readonly ITestOutputHelper output;
        public LargeModelContractTests(ITestOutputHelper output) { this.output = output; }
        [Theory]
        [InlineData("horizun_query_model")]
        [InlineData("horizun_list_elements")]
        [InlineData("horizun_model_scan")]
        [InlineData("horizun_audit_model")]
        [InlineData("horizun_quantities")]
        public void Explicit_confirmation_is_declared_as_boolean(string name)
        {
            Assert.Equal("boolean", (string)Contract.Find(name).InputSchema["properties"]["confirm_large_operation"]["type"]);
            output.WriteLine("Contract hash: " + Contract.Hash);
        }
    }
}
