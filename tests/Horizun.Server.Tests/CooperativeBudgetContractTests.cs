using System.Linq;
using Horizun.Contracts;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Server.Tests
{
    /// <summary>
    /// The opt-in budget, as the contract publishes it, for the five heavy readers. A request
    /// WITHOUT the argument must validate exactly as before; one WITH it must validate; a typo
    /// inside it must not.
    /// </summary>
    public class CooperativeBudgetContractTests
    {
        public static readonly object[][] Tools =
        {
            new object[] { "horizun_model_scan", "{target_document_title:'m'}" },
            new object[] { "horizun_query_model", "{categories:['OST_Walls']}" },
            new object[] { "horizun_audit_model", "{target_document:'m'}" },
            new object[] { "horizun_quantities", "{category:'OST_Walls'}" },
            new object[] { "horizun_query_planimetry", "{mode:'sheets'}" },
        };

        [Theory]
        [MemberData(nameof(Tools))]
        public void The_tool_declares_the_three_arguments_of_the_budget(string tool, string baseRequest)
        {
            // The base request of the tool carries no budget of its own: the argument is optional.
            Assert.Null(JObject.Parse(baseRequest)["cooperative"]);
            JObject schema = Contract.Find(tool).InputSchema;
            JObject cooperative = (JObject)schema["properties"]["cooperative"];
            Assert.NotNull(cooperative);
            Assert.False((bool)cooperative["additionalProperties"]);
            var names = ((JObject)cooperative["properties"]).Properties().Select(p => p.Name).OrderBy(n => n).ToArray();
            Assert.Equal(new[] { "cursor", "max_units", "ui_budget_ms" }, names);
            Assert.Equal(tool == "horizun_model_scan" ? 0 : 1000, (int)cooperative["properties"]["ui_budget_ms"]["minimum"]);
            Assert.Equal(600000, (int)cooperative["properties"]["ui_budget_ms"]["maximum"]);
            // Optional: nothing about the budget is required.
            Assert.DoesNotContain("cooperative", (schema["required"] as JArray ?? new JArray()).Select(t => (string)t));
        }

        [Theory]
        [MemberData(nameof(Tools))]
        public void Without_the_argument_the_request_validates_exactly_as_before(string tool, string baseRequest)
        {
            Assert.Empty(ContractSchemaCheck.Validate(JObject.Parse(baseRequest), Contract.Find(tool).InputSchema));
        }

        [Theory]
        [MemberData(nameof(Tools))]
        public void With_the_argument_the_request_validates(string tool, string baseRequest)
        {
            JObject request = JObject.Parse(baseRequest);
            request["cooperative"] = JObject.Parse("{ui_budget_ms:5000,max_units:100}");
            Assert.Empty(ContractSchemaCheck.Validate(request, Contract.Find(tool).InputSchema));
        }

        [Theory]
        [MemberData(nameof(Tools))]
        public void A_misspelt_or_out_of_range_budget_is_refused(string tool, string baseRequest)
        {
            JObject schema = Contract.Find(tool).InputSchema;
            JObject typo = JObject.Parse(baseRequest);
            typo["cooperative"] = JObject.Parse("{budget_ms:5000}");
            Assert.NotEmpty(ContractSchemaCheck.Validate(typo, schema));

            JObject tooSmall = JObject.Parse(baseRequest);
            tooSmall["cooperative"] = JObject.Parse(tool == "horizun_model_scan" ? "{ui_budget_ms:-1}" : "{ui_budget_ms:10}");
            Assert.NotEmpty(ContractSchemaCheck.Validate(tooSmall, schema));

            JObject tooLarge = JObject.Parse(baseRequest);
            tooLarge["cooperative"] = JObject.Parse("{ui_budget_ms:600001}");
            Assert.NotEmpty(ContractSchemaCheck.Validate(tooLarge, schema));

            if (tool == "horizun_model_scan")
            {
                foreach (int budget in new[] { 0, 10, 1000, 600000 })
                {
                    JObject valid = JObject.Parse(baseRequest);
                    valid["cooperative"] = new JObject { ["ui_budget_ms"] = budget };
                    Assert.Empty(ContractSchemaCheck.Validate(valid, schema));
                }
            }
        }
    }
}
