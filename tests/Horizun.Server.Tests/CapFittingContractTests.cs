using System.Linq;
using Horizun.Contracts;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Server.Tests
{
    public class CapFittingContractTests
    {
        [Fact]
        public void Cap_uses_existing_creation_contract_and_accepts_a_single_named_end()
        {
            var contract = Contract.Find("horizun_create_elements");
            var props = contract.InputSchema.SelectToken("properties.elements.items.properties");
            Assert.Contains("cap", props.SelectToken("fitting.enum").Values<string>());
            Assert.Equal(1, (int)props.SelectToken("elements.minItems"));
            Assert.Equal(4, (int)props.SelectToken("elements.maxItems"));
            Assert.Null(ToolInputRules.ValidateCreation(JObject.Parse(@"{'kind':'fitting','fitting':'cap','type_id':10,'elements':[{'element_id':20,'connector':1}]}"), "fitting"));
            Assert.True((bool)contract.InputSchema.SelectToken("properties.dry_run.default"));
            Assert.NotNull(contract.InputSchema.SelectToken("properties.confirmation_token"));
        }
    }
}
