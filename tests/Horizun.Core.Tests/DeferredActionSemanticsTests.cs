// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// CHARACTERISATION, NOT ENDORSEMENT.
//
// A deferred action is one whose arguments contain ${key.path} pointing at
// something an earlier action has not created yet. There is nothing to rehearse it
// against during a dry run, so it is not rehearsed - and today it does NOT dirty
// RehearsedCleanly, which means the outer executable confirmation is still issued
// over a graph containing an action nobody previewed.
//
// That is the historical create-then-use behaviour the tool documents, and it sits
// against the rule the rest of this work enforces: a dry run that could not
// resolve what it was given must not produce an executable confirmation. Which of
// the two wins is a decision about what a user authorises, so it is NOT decided
// here. These tests pin what the code does today so the decision is taken against
// a measurement, and so that changing it is a deliberate act that breaks a test
// rather than a quiet drift.
//
// The audit report accompanying this file lists the options.
// -----------------------------------------------------------------------------
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class DeferredActionSemanticsTests
    {
        private static JObject CleanRehearsal()
        {
            var payload = new JObject();
            ApplicationOutcome.StampRehearsal(payload, 3, 0, 0, 0);
            return payload;
        }

        [Fact]
        public void A_deferred_row_claims_nothing_about_itself()
        {
            JObject row = PlanLedger.Deferred(2, "tags", "horizun_annotate",
                                              "${walls.rows.0.element_id} is not known yet");

            Assert.Equal("deferred_until_execution", row.Value<string>("status"));
            Assert.Equal("uncertain", row.Value<string>("application_state"));
            Assert.False(row.Value<bool>("fully_applied"));
            Assert.False(row.Value<bool>("application_declared"));
            Assert.Contains("walls", row.Value<string>("reason"));
        }

        [Fact]
        public void A_deferred_action_dirties_the_rehearsal_and_withholds_confirmation()
        {
            var ledger = new PlanLedger();
            ledger.RecordRehearsal(0, "walls", "horizun_create_elements", true, CleanRehearsal(), null);
            JObject deferred = ledger.RecordDeferred(1, "tags", "horizun_annotate",
                                                      "created ids do not exist during rehearsal");

            Assert.False(ledger.RehearsedCleanly);
            Assert.Same(deferred, ledger.FailedAction);
        }

        [Fact]
        public void A_deferred_action_is_still_gated_at_apply_time()
        {
            // What replaces the preview. It is not nothing, and it is not a preview: the
            // action must come back fully applied and verified or the group rolls back,
            // and nothing after it runs.
            var ledger = new PlanLedger();
            ApplicationState state;

            var partial = new JObject();
            ApplicationOutcome.StampApplied(partial, "Committed", 5, 3, 3, 0, 2, 0);

            Assert.False(ledger.RecordExecuted(0, "deferred_delete", "horizun_delete_verified",
                                               true, partial, null, out state));
            Assert.Equal(ApplicationState.Partial, state);
            Assert.Equal(0, ledger.VerifiedActions);
        }

        [Fact]
        public void A_deferred_reference_can_resolve_later_but_never_receives_a_token()
        {
            // The gap, stated as a test so it cannot be forgotten: a deferred action's
            // arguments are resolved from a PREVIOUS action's real output at apply time.
            // Nothing in the dry run saw those values, so nothing in the token binds them.
            var results = new System.Collections.Generic.Dictionary<string, JToken>(System.StringComparer.Ordinal);

            // At dry-run time the previous action has no created ids: the reference cannot
            // resolve, which is exactly why the row is deferred.
            string error;
            PlanReferences.Resolve(JToken.Parse("\"${walls.rows.0.element_id}\""), results, out error);
            Assert.NotNull(error);

            var ledger = new PlanLedger();
            ledger.RecordDeferred(1, "cleanup", "horizun_delete_verified", error);
            Assert.False(ledger.RehearsedCleanly);

            // It could resolve at apply time, but the outer apply cannot be reached because
            // no executable confirmation was minted over the deferred rehearsal.
            results["walls"] = JToken.Parse("{\"rows\":[{\"element_id\":987654}]}");
            JToken resolved = PlanReferences.Resolve(JToken.Parse("\"${walls.rows.0.element_id}\""), results, out error);

            Assert.Null(error);
            Assert.Equal(987654, (long)resolved);
        }

        [Fact]
        public void A_reference_to_a_key_that_does_not_exist_never_resolves()
        {
            var results = new System.Collections.Generic.Dictionary<string, JToken>(System.StringComparer.Ordinal);
            string error;

            PlanReferences.Resolve(JToken.Parse("\"${nobody.rows.0.element_id}\""), results, out error);

            Assert.NotNull(error);
            Assert.Contains("nobody", error);
        }

        [Fact]
        public void A_reference_to_a_path_that_does_not_exist_on_a_real_result_never_resolves()
        {
            var results = new System.Collections.Generic.Dictionary<string, JToken>(System.StringComparer.Ordinal)
            {
                { "walls", JToken.Parse("{\"rows\":[{\"element_id\":1}]}") }
            };
            string error;

            PlanReferences.Resolve(JToken.Parse("\"${walls.rows.9.element_id}\""), results, out error);

            Assert.NotNull(error);
        }
    }
}
