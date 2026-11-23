using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class QueryCoverageTests
    {
        [Theory]
        [InlineData("budget")]
        [InlineData("rows")]
        [InlineData("unreadable")]
        [InlineData("links")]
        [InlineData("geometry")]
        public void Missing_coverage_names_the_gap_and_action(string kind)
        {
            var result = new JObject { ["coverage_complete"] = true, ["matched_total"] = 20 };
            if (kind == "budget") { result["coverage_complete"] = false; result["collection_complete"] = false; }
            if (kind == "rows") { result["returned"] = 5; result["offset"] = 0; result["next_cursor"] = "next"; }
            if (kind == "unreadable" || kind == "geometry") { result["coverage_complete"] = false; result["unreadable_total"] = 1; result["unreadable"] = new JArray(new JObject { ["reason"] = kind == "geometry" ? "bounding box is unavailable" : "category could not be read" }); }
            if (kind == "links") { result["coverage_complete"] = false; result["federated_coverage"] = new JObject { ["coverage_complete"] = false, ["link_instances_unavailable"] = 1 }; }
            // On the old implementation no coverage description existed: assert the
            // actual missing response rather than making the regression fail to compile.
            typeof(QueryResponseOptions).GetMethod("DescribeCoverage")?.Invoke(null, new object[] { result, new JObject { ["categories"] = new JArray("OST_Walls") } });
            Assert.NotEmpty((JArray)result["missing"]);
            Assert.NotNull(result["missing"][0]["count"]);
            Assert.NotNull(result["missing"][0]["reason"]);
            Assert.NotNull(result["how_to_complete"][0]["suggested_call"]["arguments"]);
        }
    }
}
