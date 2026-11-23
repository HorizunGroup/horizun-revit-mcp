using Horizun.Contracts;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Server.Tests
{
    public class ScheduleElementIdContractTests
    {
        [Theory]
        [InlineData("'Level 1'", true)]
        [InlineData("101", true)]
        [InlineData("9007199254740993", true)]
        [InlineData("101.5", false)]
        [InlineData("true", false)]
        [InlineData("null", false)]
        [InlineData("{}", false)]
        public void Schedule_filter_value_schema_accepts_names_or_integral_ids(string value, bool valid)
        {
            var request = JObject.Parse("{target_document:'fixture',actions:[{operation:'set_filters',schedule_id:55," +
                "filters:[{field:'Level',operator:'equal',value:" + value + "}]}]}");
            var schema = Contract.Find("horizun_manage_schedules").InputSchema;
            Assert.Equal(valid, ContractSchemaCheck.Validate(request, schema).Count == 0);
        }

        [Fact]
        public void Numeric_ids_remain_accepted_via_number_value()
        {
            var request = JObject.Parse("{target_document:'fixture',actions:[{operation:'set_filters',schedule_id:55," +
                "filters:[{parameter_id:-1001952,operator:'equal',number_value:101}]}]}");
            Assert.Empty(ContractSchemaCheck.Validate(request, Contract.Find("horizun_manage_schedules").InputSchema));
        }
    }
}
