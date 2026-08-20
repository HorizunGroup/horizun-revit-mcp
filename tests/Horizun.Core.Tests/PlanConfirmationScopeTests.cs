// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// WHAT THE OUTER TOKEN OF execute_plan IS ACTUALLY BOUND TO.
//
// The previous audit round proved the plan WITHHOLDS its confirmation when a
// rehearsal did not resolve, but never checked what the token covers once it is
// issued. That is the other half of the same guarantee: a token that survives a
// change to the graph it approved is worth as little as one issued over an
// unrehearsed graph.
//
// execute_plan mints its token with PlanHash(request, "actions") plus the
// document key, so these exercise ConfirmationStore.PlanHash - production code -
// with the plan's own scope field, and ConfirmationStore.Validate for the parts
// the hash deliberately does not cover.
// -----------------------------------------------------------------------------
using System;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class PlanConfirmationScopeTests
    {
        /// <summary>The scope field execute_plan passes. Read from one place so it cannot drift.</summary>
        private const string Scope = "actions";

        private static JObject Graph(string actionsJson, string transactionName = null)
        {
            var request = new JObject { ["target_document"] = "C:/models/tower.rvt" };
            request[Scope] = JArray.Parse(actionsJson);
            if (transactionName != null) request["transaction_name"] = transactionName;
            return request;
        }

        private const string TwoActions = @"[
            { 'key':'walls', 'tool':'horizun_create_elements', 'arguments':{ 'items':[{'kind':'wall','level':'L1'}] } },
            { 'key':'codes', 'tool':'horizun_write_params_verified', 'arguments':{ 'rows':[{'element_id':1,'parameter':'Comments','value':'A'}] } }
        ]";

        private static string Hash(JObject request) => ConfirmationStore.PlanHash(request, Scope);

        // ---- What the token MUST be bound to ------------------------------------

        [Fact]
        public void The_same_graph_hashes_the_same_so_an_honest_apply_is_accepted()
        {
            Assert.Equal(Hash(Graph(TwoActions)), Hash(Graph(TwoActions)));
        }

        [Fact]
        public void Adding_an_action_to_the_approved_graph_changes_the_hash()
        {
            string withDelete = TwoActions.TrimEnd().TrimEnd(']') +
                @", { 'key':'purge', 'tool':'horizun_delete_verified', 'arguments':{ 'element_ids':[42] } } ]";

            Assert.NotEqual(Hash(Graph(TwoActions)), Hash(Graph(withDelete)));
        }

        [Fact]
        public void Removing_an_action_changes_the_hash()
        {
            string onlyFirst = @"[
                { 'key':'walls', 'tool':'horizun_create_elements', 'arguments':{ 'items':[{'kind':'wall','level':'L1'}] } }
            ]";

            Assert.NotEqual(Hash(Graph(TwoActions)), Hash(Graph(onlyFirst)));
        }

        [Fact]
        public void Reordering_the_graph_changes_the_hash_even_with_identical_actions()
        {
            // Order is the whole meaning of a graph: create-then-write and write-then-create
            // are different operations over the same two actions.
            string reversed = @"[
                { 'key':'codes', 'tool':'horizun_write_params_verified', 'arguments':{ 'rows':[{'element_id':1,'parameter':'Comments','value':'A'}] } },
                { 'key':'walls', 'tool':'horizun_create_elements', 'arguments':{ 'items':[{'kind':'wall','level':'L1'}] } }
            ]";

            Assert.NotEqual(Hash(Graph(TwoActions)), Hash(Graph(reversed)));
        }

        [Fact]
        public void Changing_one_argument_deep_inside_an_action_changes_the_hash()
        {
            string differentValue = TwoActions.Replace("'value':'A'", "'value':'B'");

            Assert.NotEqual(Hash(Graph(TwoActions)), Hash(Graph(differentValue)));
        }

        [Fact]
        public void Changing_the_tool_of_an_action_changes_the_hash()
        {
            string swapped = TwoActions.Replace("horizun_write_params_verified", "horizun_delete_verified");

            Assert.NotEqual(Hash(Graph(TwoActions)), Hash(Graph(swapped)));
        }

        [Fact]
        public void Renaming_a_key_changes_the_hash_because_later_references_point_at_it()
        {
            string renamed = TwoActions.Replace("'key':'walls'", "'key':'muros'");

            Assert.NotEqual(Hash(Graph(TwoActions)), Hash(Graph(renamed)));
        }

        [Fact]
        public void Two_graphs_cannot_be_run_together_into_one_that_hashes_the_same()
        {
            // The separator attack the index-prefixed encoding exists to stop: a graph of
            // two actions and a graph of one action whose text spans both must not collide.
            string oneFatAction = @"[
                { 'key':'walls', 'tool':'horizun_create_elements', 'arguments':{ 'items':[{'kind':'wall','level':'L1'}],
                  'smuggled':""},{'key':'codes','tool':'horizun_write_params_verified','arguments':{"" } }
            ]";

            Assert.NotEqual(Hash(Graph(TwoActions)), Hash(Graph(oneFatAction)));
        }

        [Fact]
        public void An_empty_graph_and_an_absent_one_do_not_hash_the_same()
        {
            var absent = new JObject { ["target_document"] = "C:/models/tower.rvt" };

            Assert.NotEqual(Hash(Graph("[]")), Hash(absent));
        }

        // ---- What the hash deliberately does NOT cover --------------------------

        [Fact]
        public void A_cosmetic_field_outside_the_scope_does_not_invalidate_the_confirmation()
        {
            // transaction_name only labels the undo step. A guard that fired on it is one
            // callers learn to work around, which is the measured reason scope fields exist.
            Assert.Equal(Hash(Graph(TwoActions, "Horizun: atomic plan")),
                         Hash(Graph(TwoActions, "Coordinación jueves")));
        }

        [Fact]
        public void The_document_is_covered_by_its_own_check_and_not_by_the_hash()
        {
            // target_document is NOT a scope field - the hash is identical - so if the
            // document were not checked separately, a token would carry across models.
            JObject here = Graph(TwoActions);
            JObject elsewhere = Graph(TwoActions);
            elsewhere["target_document"] = "C:/models/DIFFERENT.rvt";
            Assert.Equal(Hash(here), Hash(elsewhere));

            // And the separate check is what refuses it.
            var store = new ConfirmationStore();
            Confirmation issued = store.Issue("horizun_execute_plan", "doc-A", Hash(here), null, null);
            ConfirmationCheck check = store.Validate(issued.Token, "horizun_execute_plan", "doc-B", Hash(elsewhere));

            Assert.False(check.Ok);
            Assert.Equal(ConfirmationState.DocumentChanged, check.State);
        }

        // ---- The token itself ---------------------------------------------------

        [Fact]
        public void A_plan_token_cannot_be_spent_on_a_different_command()
        {
            // The inverse of "a child's inner token does not substitute for the plan's":
            // the plan's token is bound to horizun_execute_plan by name.
            var store = new ConfirmationStore();
            Confirmation issued = store.Issue("horizun_execute_plan", "doc-A", Hash(Graph(TwoActions)), null, null);

            ConfirmationCheck check = store.Validate(issued.Token, "horizun_write_params_verified", "doc-A",
                                                     Hash(Graph(TwoActions)));

            Assert.False(check.Ok);
            Assert.Equal(ConfirmationState.WrongCommand, check.State);
        }

        [Fact]
        public void A_plan_token_authorises_one_execution_and_not_a_session()
        {
            var store = new ConfirmationStore();
            string hash = Hash(Graph(TwoActions));
            Confirmation issued = store.Issue("horizun_execute_plan", "doc-A", hash, null, null);

            Assert.True(store.Validate(issued.Token, "horizun_execute_plan", "doc-A", hash).Ok);

            ConfirmationCheck second = store.Validate(issued.Token, "horizun_execute_plan", "doc-A", hash);
            Assert.False(second.Ok);
            Assert.Equal(ConfirmationState.AlreadyUsed, second.State);
        }

        [Fact]
        public void A_graph_changed_after_approval_is_refused_as_a_changed_plan()
        {
            var store = new ConfirmationStore();
            Confirmation issued = store.Issue("horizun_execute_plan", "doc-A", Hash(Graph(TwoActions)), null, null);

            string withDelete = TwoActions.TrimEnd().TrimEnd(']') +
                @", { 'key':'purge', 'tool':'horizun_delete_verified', 'arguments':{ 'element_ids':[42] } } ]";

            ConfirmationCheck check = store.Validate(issued.Token, "horizun_execute_plan", "doc-A",
                                                     Hash(Graph(withDelete)));

            Assert.False(check.Ok);
            Assert.Equal(ConfirmationState.PlanChanged, check.State);
        }

        [Fact]
        public void No_token_at_all_is_refused_rather_than_treated_as_absent_approval()
        {
            var store = new ConfirmationStore();

            foreach (string token in new[] { null, "", "   ", "not-a-token" })
                Assert.False(store.Validate(token, "horizun_execute_plan", "doc-A", "hash").Ok);
        }
    }
}
