// color_by_value reads a type-bound parameter and colours the empty value apart; list_elements
// can return parameters per row. The schema must declare both, or the bridge refuses the argument.
using System.Linq;
using Horizun.Contracts;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Server.Tests
{
    public class ColourByValueContractTests
    {
        [Fact]
        public void Manage_views_declares_empty_color_and_names_the_grey_default()
        {
            JObject actionProps = (JObject)Contract.Find("horizun_manage_views").InputSchema["properties"]["actions"]["items"]["properties"];
            JToken emptyColor = actionProps["empty_color"];
            Assert.NotNull(emptyColor);
            Assert.Equal("string", (string)emptyColor["type"]);
            Assert.Contains("#AAAAAA", (string)emptyColor["description"]);
        }

        [Fact]
        public void List_elements_declares_return_parameters_as_an_array_of_names()
        {
            JToken p = Contract.Find("horizun_list_elements").InputSchema["properties"]["return_parameters"];
            Assert.NotNull(p);
            Assert.Equal("array", (string)p["type"]);
            Assert.Equal("string", (string)p["items"]["type"]);
            Assert.True((int)p["maxItems"] >= 1);
        }
    }
}
