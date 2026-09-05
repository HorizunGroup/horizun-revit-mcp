// -----------------------------------------------------------------------------
// Horizun Server tests - original Horizun code.
//
// The published contract of the correction cycle and the gated save/export:
// what a client is told, held to what the add-in does.
// -----------------------------------------------------------------------------
using System.Linq;
using Horizun.Contracts;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Server.Tests
{
    public class CorrectionCycleContractTests
    {
        private static CommandContract Apply() => Contract.Find("horizun_apply_corrections");

        [Fact]
        public void Apply_corrections_is_a_dry_run_gated_destructive_write_with_the_shared_key()
        {
            CommandContract c = Apply();
            Assert.NotNull(c);
            Assert.Equal(ToolEffect.MutatingUnlessDryRun, c.Effect);
            // It can drive horizun_delete_verified, so a client must ask a person first.
            Assert.True(c.Destructive);
            JObject props = (JObject)c.InputSchema["properties"];
            Assert.NotNull(props["idempotency_key"]);
            Assert.NotNull(props["confirmation_token"]);
            Assert.False((bool)c.InputSchema["additionalProperties"]);
            var required = c.InputSchema["required"].Select(t => (string)t).ToList();
            Assert.Contains("target_document", required);
            Assert.Contains("finding_set_fingerprint", required);
            Assert.Contains("actions", required);
            Assert.Equal(1, (int)props["actions"]["minItems"]);
        }

        /// <summary>
        /// THE SHARED SENTENCE IS NOT TRUE OF THIS COMMAND, so this command does not
        /// publish it. Every mutating contract gets an idempotency_key injected with
        /// "a retry with the same key returns the recorded result without executing
        /// twice"; there is no such record here, and no typed model write this drives
        /// keeps one - the durable ledger belongs to the host-side tools that append to
        /// a workbook or push a dataset. What makes a retry safe is the single-use
        /// token plus the pre-apply re-check, and that is what the schema now says.
        /// </summary>
        [Fact]
        public void The_idempotency_key_says_what_it_does_and_what_it_does_is_a_replay()
        {
            // THIS TEST ASSERTED THE OPPOSITE, AND THE OPPOSITE WAS FALSE. It pinned a
            // description claiming "NOTHING here records a reply against it". Measured
            // 2026-09-03 on Revit 2026: re-sending an applied call under the same key
            // came back idempotency.status = replayed, command_executed_in_this_call =
            // false, and nothing ran - the DISPATCHER's durable ledger records every
            // mutating call, this one included. The description now says that, and
            // still names the stronger guarantee: a NEW key over the same actions is
            // refused, because the token is single use and the checks are re-run.
            var key = (JObject)Apply().InputSchema["properties"]["idempotency_key"];
            string described = (string)key["description"];
            Assert.Contains("returns the recorded reply and runs nothing", described);
            Assert.DoesNotContain("NOTHING here records a reply against it", described);
            Assert.Contains("single use", described);
            Assert.Contains("stale plan", described);
        }

        /// <summary>
        /// A DELETE NAMES ITS ELEMENTS, and the published description says so. The
        /// registry's destructive_means promised it while the selection did not
        /// enforce it; CorrectionSelectionSafetyTests holds the behaviour, this holds
        /// what a client is told about it before it calls.
        /// </summary>
        [Fact]
        public void The_description_says_a_destructive_action_must_list_its_element_ids()
        {
            string d = Apply().Description;
            Assert.Contains("must LIST element_ids", d);
            Assert.Contains("rather than read as every element the finding named", d);
            var items = (JObject)Apply().InputSchema["properties"]["actions"]["items"];
            Assert.NotNull(items["properties"]["element_ids"]);
        }

        [Fact]
        public void Apply_corrections_rides_in_the_audit_pack_beside_the_audit()
        {
            Assert.Contains("horizun_apply_corrections", ToolPacks.MembersOf("audit"));
            Assert.Contains("horizun_audit_model", ToolPacks.MembersOf("audit"));
        }

        [Fact]
        public void The_description_states_the_rollback_scope_and_the_stale_refusal()
        {
            string d = Apply().Description;
            Assert.Contains("PER ACTION", d);
            Assert.Contains("stale_plan", d);
            Assert.Contains("skipped", d);
            Assert.Contains("horizun_execute_python is not reachable", d);
        }

        [Fact]
        public void Save_and_export_publish_one_identical_require_gate_grammar()
        {
            JObject save = (JObject)Contract.Find("horizun_save_document").InputSchema["properties"]["require_gate"];
            JObject export = (JObject)Contract.Find("horizun_export").InputSchema["properties"]["require_gate"];
            Assert.NotNull(save);
            Assert.True(JToken.DeepEquals(save, export), "one decision, two grammars");
            Assert.False((bool)save["additionalProperties"]);
            Assert.Equal(new[] { "profile" }, save["required"].Select(t => (string)t).ToArray());
            JObject profile = (JObject)save["properties"]["profile"];
            Assert.Equal(new[] { "name", "version", "requirements" }, profile["required"].Select(t => (string)t).ToArray());
            Assert.Contains("not_interceptable", (string)save["description"]);
        }

        [Fact]
        public void Require_gate_is_optional_so_both_calls_keep_their_old_shape()
        {
            foreach (string tool in new[] { "horizun_save_document", "horizun_export" })
            {
                JToken required = Contract.Find(tool).InputSchema["required"];
                if (required != null) Assert.DoesNotContain("require_gate", required.Select(t => (string)t));
            }
        }

        [Fact]
        public void Manage_links_is_now_composable_in_an_atomic_plan()
        {
            JToken tools = Contract.Find("horizun_execute_plan").InputSchema["properties"]["actions"]["items"]["properties"]["tool"]["enum"];
            Assert.Contains("horizun_manage_links", tools.Select(t => (string)t));
        }
    }
}
