using Horizun.Contracts;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Server.Tests
{
    public class WallSlabContractTests
    {
        private static JObject Schema => Contract.Find("horizun_audit_model").InputSchema;

        [Theory]
        [InlineData("level_id", "12")]
        [InlineData("wall_ids", "[14]")]
        [InlineData("tolerance_mm", "3")]
        public void Each_wall_slab_filter_requires_the_wall_slab_operation(string filter, string value)
        {
            var request = new JObject { ["target_document"] = "M", [filter] = JToken.Parse(value) };
            Assert.NotEmpty(ContractSchemaCheck.Validate(request, Schema));
            request["operation"] = "warnings";
            Assert.NotEmpty(ContractSchemaCheck.Validate(request, Schema));
            request["operation"] = "wall_slab";
            Assert.Empty(ContractSchemaCheck.Validate(request, Schema));
        }

        [Theory]
        [InlineData("{target_document:'M',top:20}")]
        [InlineData("{target_document:'M',operation:'warnings',page_size:1}")]
        [InlineData("{target_document:'M',operation:'wall_slab',page_size:1}")]
        public void Unfiltered_audit_requests_keep_their_existing_shape(string request)
        {
            Assert.Empty(ContractSchemaCheck.Validate(JObject.Parse(request), Schema));
        }
    }
}
