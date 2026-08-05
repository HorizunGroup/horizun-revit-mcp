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

        /// <summary>
        /// The schema must declare preflight, or a strict client (additionalProperties:
        /// false) could never send it and the validate-without-executing path would be
        /// documented but unreachable.
        /// </summary>
        [Fact]
        public void The_schema_declares_preflight_and_says_what_it_cannot_prove()
        {
            JObject preflight = (JObject)Props()["preflight"];
            Assert.NotNull(preflight);
            Assert.Equal("boolean", (string)preflight["type"]);
            Assert.False((bool)preflight["default"]);

            string d = (string)preflight["description"];
            Assert.Contains("WITHOUT executing", d);
            // The honesty clause: a preflight is not a rehearsal of arbitrary code.
            Assert.Contains("cannot prove", d);
            // And it must not become a manual approval gate that stalls the fallback.
            Assert.Contains("continue to execution", d);
        }

        /// <summary>
        /// Since the default flipped, the description is where a client learns the
        /// fallback policy: typed first, Python when a capability is missing, never
        /// as a retry of a failed typed write, and evidence over prints.
        /// </summary>
        [Fact]
        public void The_description_states_the_fallback_policy_and_the_evidence_contract()
        {
            string d = ExecutePython().Description;

            Assert.Contains("EXECUTION FALLBACK", d);
            Assert.Contains("Enabled by default", d);
            Assert.Contains("instead of answering 'not supported'", d);
            Assert.Contains("second write", d);
            // The four evidence states, in the description a caller actually reads.
            Assert.Contains("self_reported_verified|completed_unverified|partial|failed", d);
            Assert.Contains("downgraded", d);
        }

        /// <summary>
        /// The description must not offer "verified" as something this path returns. It
        /// is the word a client will repeat to a user, and on the Python path there is
        /// nothing behind it.
        /// </summary>
        [Fact]
        public void The_description_never_offers_verified_as_a_python_result_state()
        {
            string d = ExecutePython().Description;

            Assert.Contains("SELF-REPORTED, NOT HOST-VERIFIED", d);
            Assert.Contains("host_verified is always false", d);
            Assert.Contains("script_reported_status", d);
            // The old four-state list, which began with a bare "verified", must be gone.
            Assert.DoesNotContain("evidence_status is one of verified|", d);
        }

        /// <summary>
        /// The fallback must be described as a structured decision, not as a judgement
        /// call about how an error was phrased.
        /// </summary>
        [Fact]
        public void The_description_points_at_the_structured_fallback_block()
        {
            string d = ExecutePython().Description;

            Assert.Contains("fallback.allowed=true", d);
            Assert.Contains("never on the wording of an error", d);
            Assert.Contains("allowed=false, means", d);
        }
    }
}
