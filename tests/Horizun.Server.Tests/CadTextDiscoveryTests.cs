using Horizun.Contracts;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Server.Tests
{
    public class CadTextDiscoveryTests
    {
        [Fact]
        public void Cad_extract_advertises_the_file_reader_that_can_read_TEXT_and_MTEXT()
        {
            var tool = Contract.Find("horizun_cad_extract");
            var props = (JObject)tool.InputSchema["properties"];
            Assert.NotNull(props["read_dwg_text"]);
            Assert.NotNull(props["dwg_path"]);
            Assert.Contains("TEXT", tool.Description);
            Assert.Contains("MTEXT", tool.Description);
            Assert.Contains("AutoCAD", tool.Description);
        }

        [Fact]
        public void Query_cad_directs_callers_to_the_text_reader()
        {
            var tool = Contract.Find("horizun_query_cad");
            Assert.Contains("horizun_cad_extract", tool.Description);
        }
    }
}
