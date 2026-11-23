using System;
using Horizun.Contracts;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Server.Tests
{
    public class FamilyBatchContractTests
    {

        [Fact]
        public void Classification_table_is_a_guarded_typed_write()
        {
            CommandContract contract = Contract.Find("horizun_manage_classification_table");
            Assert.NotNull(contract);
            Assert.Equal(ToolEffect.MutatingUnlessDryRun, contract.Effect);
            Assert.Equal("horizun_manage_classification_table", contract.Command);
            JObject properties = (JObject)contract.InputSchema["properties"];
            Assert.Contains("reload", properties["operation"]["enum"].Values<string>());
            Assert.Contains("assembly_code", properties["table"]["enum"].Values<string>());
            Assert.NotNull(properties["path"]);
            Assert.NotNull(properties["confirmation_token"]);
        }

    }
}
