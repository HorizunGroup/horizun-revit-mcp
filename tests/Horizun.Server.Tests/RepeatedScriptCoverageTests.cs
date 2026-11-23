using System.Linq;
using Horizun.Contracts;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Server.Tests
{
    public class RepeatedScriptCoverageTests
    {
        [Fact]
        public void Piece_photographs_keep_typed_snapshot_and_diff()
        {
            var schema = Contract.Find("horizun_selection_context").InputSchema;
            var operations = schema.SelectToken("properties.operation.enum").Values<string>();
            Assert.Contains("snapshot", operations);
            Assert.Contains("diff", operations);
            Assert.NotNull(schema.SelectToken("properties.snapshot_name"));
        }

        [Fact]
        public void View_color_layers_are_read_by_the_existing_graphics_operation()
        {
            string schema = Contract.Find("horizun_manage_views").InputSchema.ToString();
            Assert.Contains("explain_graphics", schema);
            Assert.Contains("element_ids", schema);
            Assert.Contains("view_id", schema);
        }
    }
}
