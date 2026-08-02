// -----------------------------------------------------------------------------
// Horizun Server tests - original Horizun code.
//
// The schema has to ADMIT the arguments the handler demands.
//
// This pairing is where the family_apply defect lived: the command and its
// schema each read plausibly, and only crossing them showed that the three
// fields saying WHAT TO WRITE were absent from the approval. The same crossing
// applies here in the other direction - the handler now requires
// target_document, and refuses run_async without idempotency_key. The schema
// carries "additionalProperties": false, so a key the schema does not declare is
// a key a strict client will not send, and every run_async call would fail on a
// requirement it had no way to satisfy.
// -----------------------------------------------------------------------------
using System.Linq;
using Horizun.Contracts;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Server.Tests
{
    public class ExecutePythonContractTests
    {
        private static CommandContract ExecutePython() => Contract.Find("horizun_execute_python");

        private static JObject Props() => (JObject)ExecutePython().InputSchema["properties"];

        [Fact]
        public void The_schema_requires_code_and_target_document()
        {
            var required = ((JArray)ExecutePython().InputSchema["required"]).Select(t => (string)t).ToList();

            Assert.Contains("code", required);
            // The handler refuses without it. A schema that did not require it would
            // advertise a call that always fails.
            Assert.Contains("target_document", required);
        }

        [Fact]
        public void The_schema_declares_the_two_arguments_the_handler_demands()
        {
            JObject props = Props();

            Assert.NotNull(props["target_document"]);
            Assert.NotNull(props["idempotency_key"]);
            // additionalProperties:false means an undeclared field is rejected before it
            // ever reaches the handler - so declaring them is not documentation, it is
            // the difference between run_async working and being unreachable.
            Assert.False((bool)ExecutePython().InputSchema["additionalProperties"]);
        }

        [Fact]
        public void The_key_is_not_required_at_the_schema_level_because_it_depends_on_run_async()
        {
            var required = ((JArray)ExecutePython().InputSchema["required"]).Select(t => (string)t).ToList();

            // Required only when run_async is true, which JSON Schema draft-7 could
            // express and this schema deliberately does not: the handler is the gate,
            // it says exactly why, and a schema-level requirement would break every
            // synchronous call - where the key is refused, not demanded.
            Assert.DoesNotContain("idempotency_key", required);
        }

        [Fact]
        public void The_description_warns_it_is_still_a_privileged_bypass()
        {
            string d = ExecutePython().Description;

            Assert.Contains("target_document", d);
            Assert.Contains("idempotency_key", d);
            // The surface must stop implying one uniform mutation policy. This command
            // is document-scoped now and still has no dry run, no plan and no token.
            Assert.Contains("PRIVILEGED BYPASS", d);
        }

        [Fact]
        public void Run_async_says_it_needs_a_key()
        {
            string runAsync = (string)Props()["run_async"]["description"];
            Assert.Contains("idempotency_key", runAsync);
        }
    }
}
