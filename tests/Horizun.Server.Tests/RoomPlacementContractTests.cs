using Horizun.Contracts;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Server.Tests
{
    public class RoomPlacementContractTests
    {
        private static bool Valid(JObject row)
        {
            var request = new JObject { ["target_document"] = "Synthetic", ["elements"] = new JArray(row) };
            return ContractSchemaCheck.Validate(request, Contract.Find("horizun_create_elements").InputSchema).Count == 0;
        }

        [Fact]
        public void Point_rooms_omit_phase_and_topology_rooms_require_it()
        {
            var point = JObject.Parse(@"{'kind':'room','level_id':1,'point':[0,0]}");
            Assert.True(Valid(point));
            point["phase_id"] = 2;
            Assert.False(Valid(point));
            var topology = JObject.Parse(@"{'kind':'room','level_id':1,'placement':'all_enclosed'}");
            Assert.False(Valid(topology));
            topology["phase_id"] = 2;
            Assert.True(Valid(topology));
            topology["point"] = new JArray(0, 0);
            Assert.False(Valid(topology));
        }

        [Fact]
        public void Closed_separator_accepts_a_triangle_and_open_separator_accepts_a_line()
        {
            var row = JObject.Parse(@"{'kind':'room_separator','level_id':1,'view_id':2,'profile':[[[0,0,0],[3,0,0]]]}");
            Assert.True(Valid(row));
            row["closed"] = true;
            Assert.False(Valid(row));
            ((JArray)row["profile"][0]).Add(new JArray(3, 2, 0));
            Assert.True(Valid(row));
            Assert.Null(ToolInputRules.ValidateCreation(row, "room_separator"));
        }

        [Fact]
        public void Room_finish_gap_tolerance_is_published_and_rejects_zero()
        {
            var schema = Contract.Find("horizun_room_finishes").InputSchema;
            var request = JObject.Parse(@"{'target_document':'Synthetic','operation':'floors','room_ids':[1],'default_type_id':2,'boundary_gap_tolerance_mm':0.5}");
            Assert.Empty(ContractSchemaCheck.Validate(request, schema));
            request["boundary_gap_tolerance_mm"] = 0;
            Assert.NotEmpty(ContractSchemaCheck.Validate(request, schema));
        }
    }
}
