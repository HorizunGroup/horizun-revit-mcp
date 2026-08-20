// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// CHARACTERISATION, NOT ENDORSEMENT. Measured during the second adversarial pass.
//
// A ${key.path} reference is resolved twice against two DIFFERENT sources:
//
//   dry run / recheck   known[key]   = the referenced action's DRY-RUN payload
//   apply               results[key] = the referenced action's APPLY payload
//
// When the path exists in both but holds different values, the action is
// rehearsed - it does not become a deferred row, RehearsedCleanly stays true, an
// executable token is issued, and the value the approver previewed is not the
// value that gets written. Nothing in the recheck catches it either, because the
// recheck resolves against dry-run data too.
//
// This is a sharper instance of the same contract question as deferred actions,
// and it is worse in one way: a deferred action at least announces itself in
// not_rehearsed, and this one announces nothing.
//
// It cannot cause a wrong assimilation: the child still has to come back fully
// applied and verified. What it can do is have the plan report a verified
// application of something the user did not approve. Fixing it changes what the
// token binds, so it is NOT decided here - these tests pin the behaviour so the
// decision is taken against a measurement and so changing it breaks a test.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class PlanReferenceDriftTests
    {
        private static Dictionary<string, JToken> Known(string json)
            => new Dictionary<string, JToken>(StringComparer.Ordinal) { { "walls", JToken.Parse(json) } };

        /// <summary>create_elements' real dry-run payload, trimmed to the keys that matter.</summary>
        private const string CreateElementsDryRun =
            "{ 'dry_run':true, 'transaction_status':'not_started', 'requested':2, 'plan':[{'index':0},{'index':1}] }";

        /// <summary>...and its real apply payload.</summary>
        private const string CreateElementsApply =
            "{ 'dry_run':false, 'transaction_status':'Committed', 'requested':2, 'created_verified':2, " +
            "  'rows':[{'element_id':101},{'element_id':102}] }";

        [Fact]
        public void A_reference_value_change_is_detected_before_the_consumer_runs()
        {
            // THE GAP. Same reference text, rehearsed as "not_started", applied as
            // "Committed". A caller writing this into a parameter previews one string and
            // writes another, with a clean token and rehearsed_cleanly true.
            const string reference = "\"${walls.transaction_status}\"";
            string error;

            JToken rehearsed = PlanReferences.Resolve(JToken.Parse(reference), Known(CreateElementsDryRun), out error);
            Assert.Null(error);

            JToken applied = PlanReferences.Resolve(JToken.Parse(reference), Known(CreateElementsApply), out error);
            Assert.Null(error);

            Assert.Equal("not_started", (string)rehearsed);
            Assert.Equal("Committed", (string)applied);
            Assert.NotEqual((string)rehearsed, (string)applied);

            JObject expected = PlanReferences.DescribeBinding(JToken.Parse(reference), rehearsed);
            JObject comparison = PlanReferences.CompareBinding(expected, JToken.Parse(reference), applied);
            Assert.False(comparison.Value<bool>("matches"));
            Assert.Equal("reference_binding_changed", comparison.Value<string>("code"));
        }

        [Fact]
        public void A_reference_cardinality_change_changes_the_binding()
        {
            // Case G. The dry run has no created rows at all, so a reference to them defers
            // - but a reference to a collection that EXISTS in both resolves to a different
            // cardinality, and nothing binds the count.
            const string reference = "\"${walls.plan}\"";
            string error;

            JToken rehearsed = PlanReferences.Resolve(JToken.Parse(reference), Known(CreateElementsDryRun), out error);
            JToken applied = PlanReferences.Resolve(JToken.Parse(reference),
                Known("{ 'plan':[{'index':0},{'index':1},{'index':2},{'index':3}] }"), out error);

            Assert.Equal(2, ((JArray)rehearsed).Count);
            Assert.Equal(4, ((JArray)applied).Count);
            JObject expected = PlanReferences.DescribeBinding(JToken.Parse(reference), rehearsed);
            Assert.False(PlanReferences.CompareBinding(expected, JToken.Parse(reference), applied)
                                       .Value<bool>("matches"));
        }

        [Fact]
        public void Canonical_binding_ignores_object_property_order_but_not_array_order_or_scalar_type()
        {
            JObject original = JObject.Parse("{'value':'${walls.plan}'}");
            JObject resolved = JObject.Parse("{'b':2,'a':1,'items':[1,2]}");
            JObject expected = PlanReferences.DescribeBinding(original, resolved);

            Assert.True(PlanReferences.CompareBinding(expected, original,
                JObject.Parse("{'items':[1,2],'a':1,'b':2}")).Value<bool>("matches"));
            Assert.False(PlanReferences.CompareBinding(expected, original,
                JObject.Parse("{'items':[2,1],'a':1,'b':2}")).Value<bool>("matches"));
            Assert.False(PlanReferences.CompareBinding(expected, original,
                JObject.Parse("{'items':[1,2],'a':'1','b':2}")).Value<bool>("matches"));
        }

        [Fact]
        public void A_reference_is_resolved_without_any_check_of_the_type_the_field_expects()
        {
            // Case E. An array, an object, a string and a null all resolve into a slot that
            // wants an element id. PlanReferences is a substitution, not a schema: the CHILD
            // is what refuses, at apply time, which the plan then turns into a rollback.
            // Safe, and it means a type error surfaces after the token was spent rather than
            // during the rehearsal that was supposed to preview it.
            string error;

            JToken asArray = PlanReferences.Resolve(JToken.Parse("\"${walls.plan}\""), Known(CreateElementsDryRun), out error);
            Assert.Null(error);
            Assert.Equal(JTokenType.Array, asArray.Type);

            JToken asObject = PlanReferences.Resolve(JToken.Parse("\"${walls.plan.0}\""), Known(CreateElementsDryRun), out error);
            Assert.Null(error);
            Assert.Equal(JTokenType.Object, asObject.Type);

            JToken asNull = PlanReferences.Resolve(JToken.Parse("\"${walls.created}\""), Known("{'created':null}"), out error);
            Assert.Null(error);
            Assert.Equal(JTokenType.Null, asNull.Type);
        }

        [Fact]
        public void A_reference_resolving_to_an_id_that_names_nothing_here_is_not_the_reference_layer_s_business()
        {
            // Case F. Substitution cannot know which document an id belongs to. The child's
            // own resolution is what fails, and the plan rolls the group back - so it is
            // safe, and it is again a failure that arrives after approval rather than
            // during it.
            string error;
            JToken foreign = PlanReferences.Resolve(JToken.Parse("\"${walls.rows.0.element_id}\""),
                Known("{'rows':[{'element_id':99999999}]}"), out error);

            Assert.Null(error);
            Assert.Equal(99999999, (long)foreign);
        }

        [Fact]
        public void A_reference_substitutes_into_a_destructive_argument_exactly_like_any_other()
        {
            // Nothing marks element_ids as special, which is why the contract decision on
            // deferred actions is about deletes above all.
            string error;
            JToken resolved = PlanReferences.Resolve(
                JToken.Parse("{'element_ids':[\"${walls.rows.0.element_id}\"],'dry_run':false}"),
                Known("{'rows':[{'element_id':7}]}"), out error);

            Assert.Null(error);
            Assert.Equal(7, (long)resolved["element_ids"][0]);
            Assert.True(PlanReferences.HasReference(
                JToken.Parse("{'element_ids':[\"${walls.rows.0.element_id}\"]}")));
        }

        // ---- What IS bound, and still holds -------------------------------------

        [Fact]
        public void A_reference_to_a_key_or_path_that_does_not_exist_still_refuses()
        {
            string error;

            PlanReferences.Resolve(JToken.Parse("\"${nobody.rows.0}\""), Known("{}"), out error);
            Assert.NotNull(error);

            PlanReferences.Resolve(JToken.Parse("\"${walls.rows.9.element_id}\""),
                                   Known("{'rows':[{'element_id':1}]}"), out error);
            Assert.NotNull(error);
        }

        [Fact]
        public void Whatever_a_reference_resolves_to_the_action_still_has_to_come_back_verified()
        {
            // The guarantee that survives all of the above, and the reason none of it can
            // cause a wrong assimilation: the drift changes WHAT is written, never whether
            // the plan believes it was written.
            var ledger = new PlanLedger();
            var partial = new JObject();
            ApplicationOutcome.StampApplied(partial, "Committed", 3, 1, 1, 0, 2, 0);

            ApplicationState state;
            Assert.False(ledger.RecordExecuted(0, "drifted", "horizun_delete_verified",
                                               true, partial, null, out state));
            Assert.Equal(0, ledger.VerifiedActions);
        }
    }
}
