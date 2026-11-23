using System.Linq;
using Horizun.Contracts;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Server.Tests
{
    public sealed class WallFoundationDimensionContractTests
    {
        [Fact]
        public void Wall_foundation_exposes_absolute_top_and_physical_dimensions()
        {
            var items = Contract.Find("horizun_create_elements").InputSchema["properties"]["elements"]["items"];
            var footing = ((JArray)items["oneOf"]).Single(v =>
                (string)v["properties"]["kind"]["const"] == "wall_foundation");
            foreach (string field in new[] { "top_elevation", "width", "thickness" })
            {
                Assert.Equal("number", (string)footing["properties"][field]["type"]);
                Assert.Null(ToolInputRules.ValidateCreation(new JObject
                {
                    ["kind"] = "wall_foundation", ["wall_id"] = 1, ["type_id"] = 2,
                    [field] = 1.0
                }, "wall_foundation"));
            }
            Assert.Contains("internal origin", (string)footing["properties"]["top_elevation"]["description"]);
        }
    }
}
