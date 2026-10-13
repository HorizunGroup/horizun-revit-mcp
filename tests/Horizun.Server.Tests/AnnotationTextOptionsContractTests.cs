using Horizun.Contracts;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Server.Tests
{
    public class AnnotationTextOptionsContractTests
    {
        [Fact]
        public void Text_note_contract_can_bind_height_justification_and_rotation()
        {
            var props = (JObject)Contract.Find("horizun_annotate").InputSchema["properties"]["actions"]["items"]["properties"];
            Assert.NotNull(props["expected_height_mm"]);
            Assert.NotNull(props["horizontal_alignment"]);
            Assert.NotNull(props["vertical_alignment"]);
            Assert.NotNull(props["rotation_degrees"]);
        }
    }
}
