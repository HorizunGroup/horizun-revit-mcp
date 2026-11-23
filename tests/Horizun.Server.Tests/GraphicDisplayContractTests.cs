using Horizun.Contracts;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Server.Tests
{
    public class GraphicDisplayContractTests
    {
        [Fact]
        public void ManageViewsAdvertisesGraphicDisplayAndItsPayload()
        {
            var p = (JObject)Contract.Find("horizun_manage_views").InputSchema["properties"]["actions"]["items"]["properties"];
            Assert.Contains(p["operation"]["enum"].Values<string>(), x => x == "set_graphic_display");
            Assert.Equal("object", (string)p["display"]["type"]);
        }
    }
}
