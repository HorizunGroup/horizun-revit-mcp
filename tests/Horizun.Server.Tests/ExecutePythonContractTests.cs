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
        public void The_schema_requires_target_document_and_leaves_the_source_to_the_handler()
        {
            var required = ((JArray)ExecutePython().InputSchema["required"]).Select(t => (string)t).ToList();

            // The handler refuses without it. A schema that did not require it would
            // advertise a call that always fails.
            Assert.Contains("target_document", required);

            // `code` is NO LONGER schema-required, because since 5.27 the rule is
            // "exactly one of code / code_path" - which draft-7 can express and this
            // schema deliberately does not, for the same reason it does not express
            // run_async's dependency on idempotency_key: oneOf is enforced
            // inconsistently across MCP clients, and a schema that demanded `code`
            // would make code_path unreachable for every strict one.
            Assert.DoesNotContain("code", required);
            Assert.DoesNotContain("code_path", required);
        }

        /// <summary>
        /// A 26 KB driver could not be sent inline, so it arrived as a stub that read
        /// and compiled its own file - three attempts, two of them lost to IronPython
        /// decoding errors. code_path is the supported path, and with
        /// additionalProperties:false an undeclared field is unreachable, so declaring
        /// it IS the feature.
        /// </summary>
        [Fact]
        public void The_schema_declares_code_path_and_says_it_evades_nothing()
        {
            JObject codePath = (JObject)Props()["code_path"];

            Assert.NotNull(codePath);
            Assert.Equal("string", (string)codePath["type"]);

            string d = (string)codePath["description"];
            Assert.Contains("MACHINE RUNNING REVIT", d);
            Assert.Contains("Exactly one", d);
            // The limits it must not be read as a way around.
            Assert.Contains("submitted_source_sha256", d);
            // And the one that is easy to over-promise: the durable claim is taken over
            // the REQUEST, which names the path - so an edited file under the same key
            // replays rather than running. Claiming it would be "refused as a different
            // request" would be a guarantee no code here provides.
            Assert.Contains("NAMES THE PATH, NOT THE BYTES", d);
            Assert.Contains("replays the original answer", d);
            // And what it buys, which is why a caller would choose it.
            Assert.Contains("<string>", d);
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
        /// The description is where a client learns the safe default and the
        /// fallback policy: typed first, Python when a capability is missing, never
        /// as a retry of a failed typed write, and evidence over prints.
        /// </summary>
        [Fact]
        public void The_description_states_the_fallback_policy_and_the_evidence_contract()
        {
            string d = ExecutePython().Description;

            Assert.Contains("EXECUTION FALLBACK", d);
            Assert.Contains("Disabled by default", d);
            Assert.Contains("explicitly grant", d);
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
        /// The dialog record is only useful to a caller who knows it exists. Three models
        /// were reported unauditable with no cause, twice a month apart, because the
        /// script saw nothing but Revit's own "Opening was canceled" - so the description
        /// has to name the field, the reason, and the way past it.
        /// </summary>
        [Fact]
        public void The_description_says_where_a_cancelled_open_is_explained()
        {
            string d = ExecutePython().Description;

            Assert.Contains("'dialogs' and 'failures'", d);
            Assert.Contains("Opening was canceled", d);
            // Read DURING the run, which is the only way a batch can attribute one.
            Assert.Contains("revit_raised(since)", d);
            Assert.Contains("dialog_answer('dismiss')", d);
        }

        /// <summary>
        /// `app` was always injected and `__revit__` never was, so the first thing anybody
        /// arriving from pyRevit types answered "NameError" - which reads like the bridge
        /// has no application object. Both names, and what each one IS, belong here.
        /// </summary>
        [Fact]
        public void The_description_names_what_is_injected_including_the_pyrevit_alias()
        {
            string d = ExecutePython().Description;

            Assert.Contains("__revit__", d);
            Assert.Contains("UIApplication", d);
            Assert.Contains("checkpoint()", d);
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
