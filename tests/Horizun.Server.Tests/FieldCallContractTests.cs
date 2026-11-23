using System.Linq;
using Horizun.Contracts;
using Horizun.Server;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Server.Tests
{
    public class FieldCallContractTests
    {
        private static void Valid(string name, JObject arguments)
        {
            var schema = Contract.All.Single(c => c.Name == name).InputSchema;
            Assert.Empty(ContractSchemaCheck.Validate(arguments, schema));
        }

        [Fact]
        public void Compact_is_accepted_for_local_save_and_rejected_for_relinquish()
        {
            var schema = Contract.All.Single(c => c.Name == "horizun_save_document").InputSchema;
            Assert.Empty(ContractSchemaCheck.Validate(new JObject { ["operation"] = "save_local", ["compact"] = true }, schema));
            Assert.NotEmpty(ContractSchemaCheck.Validate(new JObject { ["operation"] = "relinquish_own", ["compact"] = true }, schema));
        }

        [Fact]
        public void Field_calls_are_admitted_by_the_published_schema()
        {
            Valid("horizun_open_document", new JObject {
                ["cloud_project_guid"] = "11111111-1111-1111-1111-111111111111",
                ["cloud_model_guid"] = "22222222-2222-2222-2222-222222222222",
                ["cloud_region"] = "US", ["on_open_dialog"] = "cancel",
                ["idempotency_key"] = "33333333-3333-3333-3333-333333333333" });
            foreach (string target in new[] { new string('a', 32), @"C:\Models\model.rvt" })
                Valid("horizun_document_session", new JObject { ["operation"] = "activate", ["target_document"] = target });
            Valid("horizun_save_document", new JObject { ["target_document"] = "model" });
            Valid("horizun_save_document", new JObject { ["compact"] = true });
            Valid("horizun_capture_view", new JObject { ["view_id"] = 42 });
        }
    }
}
