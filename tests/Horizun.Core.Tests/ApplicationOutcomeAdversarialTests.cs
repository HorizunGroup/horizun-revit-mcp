// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// ApplicationOutcome.Read is the arbiter: the one function whose answer decides
// whether a confirmed plan keeps a TransactionGroup. So it gets attacked here
// rather than exercised.
//
// WHAT THE AUDIT MEASURED before this was fixed: of 29 malformed or contradictory
// declaration blocks, 15 were accepted as fully applied and let PlanLedger
// continue - among them `verified_applied` beside transaction_status="RolledBack",
// beside unknown=4, beside requested=10 with verified=0, with negative counters,
// with counters of the wrong JSON type, and with no counters at all. Read trusted
// the `state` string and nothing else.
//
// None of those was reachable from the commands in this tree, because every one of
// them computes its state from its own counts. That argument is true today and is
// exactly the argument somebody would have to re-make after every future edit -
// and Declare() takes an explicit state by design, so the door is open by
// construction. The arbiter now corroborates any claim it would assimilate.
// -----------------------------------------------------------------------------
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class ApplicationOutcomeAdversarialTests
    {
        /// <summary>A hand-built block, bypassing the helpers that would keep it coherent.</summary>
        private static JObject Forged(params object[] kv)
        {
            var block = new JObject();
            for (int i = 0; i < kv.Length; i += 2)
                block[(string)kv[i]] = kv[i + 1] == null ? JValue.CreateNull() : JToken.FromObject(kv[i + 1]);
            return new JObject { [ApplicationOutcome.Key] = block };
        }

        /// <summary>A coherent verified_applied block, as a starting point to spoil.</summary>
        private static JObject Coherent(params object[] overrides)
        {
            var kv = new System.Collections.Generic.List<object>
            {
                "state", "verified_applied", "fully_applied", true, "transaction_status", "Committed",
                "requested", 5, "applied", 5, "verified", 5, "unresolved", 0, "failed", 0, "unknown", 0
            };
            JObject payload = Forged(kv.ToArray());
            var block = (JObject)payload[ApplicationOutcome.Key];
            for (int i = 0; i < overrides.Length; i += 2)
                block[(string)overrides[i]] = overrides[i + 1] == null
                    ? JValue.CreateNull() : JToken.FromObject(overrides[i + 1]);
            return payload;
        }

        /// <summary>Read it, and also push it through the ledger the plan actually uses.</summary>
        private static void AssertNotAssimilable(JObject payload, string why)
        {
            ApplicationState state = ApplicationOutcome.Read(payload);
            Assert.False(ApplicationOutcome.IsFullyApplied(state), why + " -> read as " + ApplicationOutcome.Name(state));

            var ledger = new PlanLedger();
            ApplicationState viaLedger;
            bool mayContinue = ledger.RecordExecuted(0, "k", "horizun_delete_verified", true, payload, null, out viaLedger);

            Assert.False(mayContinue, why + " let the plan continue");
            Assert.Equal(0, ledger.VerifiedActions);
        }

        // ---- The shape of the payload itself ------------------------------------

        [Fact]
        public void A_payload_that_is_not_an_object_is_uncertain()
        {
            foreach (object data in new object[] { null, "a string", 42, new JArray(1, 2) })
                Assert.Equal(ApplicationState.Uncertain, ApplicationOutcome.Read(data));
        }

        [Fact]
        public void A_missing_or_malformed_application_block_is_uncertain()
        {
            AssertNotAssimilable(new JObject { ["transaction_status"] = "Committed" }, "no block at all");
            AssertNotAssimilable(new JObject { [ApplicationOutcome.Key] = JValue.CreateNull() }, "block is null");
            AssertNotAssimilable(new JObject { [ApplicationOutcome.Key] = "verified_applied" }, "block is a string");
            AssertNotAssimilable(new JObject { [ApplicationOutcome.Key] = new JArray("verified_applied") }, "block is an array");
        }

        [Fact]
        public void A_state_that_is_missing_null_numeric_unknown_or_miscased_is_uncertain()
        {
            AssertNotAssimilable(Forged("requested", 1), "state missing");
            AssertNotAssimilable(Forged("state", null), "state null");
            AssertNotAssimilable(Forged("state", 1), "state numeric");
            AssertNotAssimilable(Forged("state", "totally_fine"), "state unknown");
            AssertNotAssimilable(Forged("state", "VERIFIED_APPLIED"), "state wrong case");
            AssertNotAssimilable(Forged("state", new JObject()), "state is an object");
        }

        // ---- A claim of full application, contradicted by its own numbers -------

        [Theory]
        [InlineData("RolledBack")]
        [InlineData("Pending")]
        [InlineData("Error")]
        [InlineData("Started")]
        [InlineData("Uninitialized")]
        [InlineData("committed")]
        [InlineData("")]
        public void Verified_applied_over_a_transaction_that_did_not_commit_is_uncertain(string status)
        {
            AssertNotAssimilable(Coherent("transaction_status", status), "verified_applied + " + status);
        }

        [Fact]
        public void Verified_applied_with_no_transaction_status_at_all_is_uncertain()
        {
            JObject payload = Coherent();
            ((JObject)payload[ApplicationOutcome.Key]).Remove("transaction_status");
            AssertNotAssimilable(payload, "verified_applied with no status");
        }

        [Fact]
        public void Verified_applied_over_counters_that_do_not_support_it_is_uncertain()
        {
            AssertNotAssimilable(Coherent("applied", 0, "verified", 0), "requested=5 verified=0");
            AssertNotAssimilable(Coherent("verified", 2), "verified below requested");
            AssertNotAssimilable(Coherent("unresolved", 3), "unresolved>0");
            AssertNotAssimilable(Coherent("failed", 2), "failed>0");
            AssertNotAssimilable(Coherent("unknown", 4), "unknown>0");
        }

        [Fact]
        public void Counters_that_cannot_describe_a_real_batch_are_uncertain()
        {
            AssertNotAssimilable(Coherent("verified", 9), "verified > applied");
            AssertNotAssimilable(Coherent("requested", 2, "applied", 9, "verified", 2), "applied > requested");
            AssertNotAssimilable(Coherent("requested", -1), "negative requested");
            AssertNotAssimilable(Coherent("applied", -2), "negative applied");
            AssertNotAssimilable(Coherent("verified", -3), "negative verified");
            AssertNotAssimilable(Coherent("unresolved", -1), "negative unresolved");
            AssertNotAssimilable(Coherent("failed", -1), "negative failed");
            AssertNotAssimilable(Coherent("unknown", -1), "negative unknown");
        }

        [Fact]
        public void Counters_that_are_absent_or_the_wrong_json_type_are_uncertain()
        {
            JObject bare = Coherent();
            var block = (JObject)bare[ApplicationOutcome.Key];
            block.Remove("requested"); block.Remove("applied"); block.Remove("verified");
            block.Remove("unresolved"); block.Remove("failed"); block.Remove("unknown");
            AssertNotAssimilable(bare, "no counters at all");

            AssertNotAssimilable(Coherent("requested", "five"), "requested is a string");
            AssertNotAssimilable(Coherent("applied", new JArray(1)), "applied is an array");
            AssertNotAssimilable(Coherent("verified", new JObject()), "verified is an object");
            AssertNotAssimilable(Coherent("unknown", 1.5), "unknown is a float");
            AssertNotAssimilable(Coherent("requested", null), "requested is null");
        }

        [Fact]
        public void Counters_beyond_int_range_are_uncertain_rather_than_wrapped()
        {
            AssertNotAssimilable(Coherent("requested", long.MaxValue, "applied", long.MaxValue,
                                          "verified", long.MaxValue), "counters past int range");
            AssertNotAssimilable(Coherent("requested", (long)int.MaxValue + 1, "applied", 5, "verified", 5),
                                 "requested one past int.MaxValue");
        }

        [Fact]
        public void No_op_claimed_over_a_request_that_asked_for_something_is_uncertain()
        {
            AssertNotAssimilable(Forged("state", "no_op", "fully_applied", true,
                                        "transaction_status", "not_started",
                                        "requested", 7, "applied", 0, "verified", 0,
                                        "unresolved", 0, "failed", 0, "unknown", 0),
                                 "no_op with requested=7");
        }

        [Fact]
        public void No_op_with_nonzero_evidence_is_uncertain_even_when_requested_is_zero()
        {
            AssertNotAssimilable(Forged("state", "no_op", "fully_applied", true,
                                        "transaction_status", "not_started",
                                        "requested", 0, "applied", 0, "verified", 0,
                                        "unresolved", 1, "failed", 0, "unknown", 0),
                                 "no_op with unresolved work");
        }

        [Fact]
        public void A_forged_clean_rehearsal_is_corroborated_before_it_can_mint_confirmation()
        {
            JObject forged = Forged("state", "rehearsed", "fully_applied", false,
                                     "transaction_status", "not_started",
                                     "requested", 5, "applied", 1, "verified", 0,
                                     "unresolved", 0, "failed", 0, "unknown", 0);

            Assert.Equal(ApplicationState.Uncertain, ApplicationOutcome.Read(forged));
            Assert.False(ApplicationOutcome.IsValidRehearsal(ApplicationOutcome.Read(forged)));
        }

        [Fact]
        public void A_rehearsal_missing_fully_applied_is_uncertain()
        {
            JObject payload = Forged("state", "rehearsed", "transaction_status", "not_started",
                                     "requested", 1, "applied", 0, "verified", 0,
                                     "unresolved", 0, "failed", 0, "unknown", 0);

            Assert.Equal(ApplicationState.Uncertain, ApplicationOutcome.Read(payload));
        }

        [Fact]
        public void A_fully_applied_flag_that_disagrees_with_its_own_state_is_uncertain()
        {
            AssertNotAssimilable(Coherent("fully_applied", false), "verified_applied with fully_applied=false");
        }

        [Fact]
        public void A_fully_applied_flag_cannot_promote_a_state_that_is_not_assimilable()
        {
            // The mirror image: the flag is not what IsFullyApplied is asked about, so a
            // forged true over `partial` changes nothing.
            JObject payload = Forged("state", "partial", "fully_applied", true, "transaction_status", "Committed",
                                     "requested", 5, "applied", 3, "verified", 3, "unresolved", 0, "failed", 2, "unknown", 0);

            Assert.Equal(ApplicationState.Partial, ApplicationOutcome.Read(payload));
            AssertNotAssimilable(payload, "partial with fully_applied=true");
        }

        // ---- What must NOT have been broken -------------------------------------

        [Fact]
        public void A_command_may_still_declare_a_state_more_cautious_than_its_counters()
        {
            // Two production paths do exactly this: the purge branch that could not look,
            // and WriteTally's contradiction verdict. Both declare Uncertain over counters
            // that would classify as something else, and both must keep their own word -
            // downgrading a diagnosis to "uncertain" is safe, and re-deriving it would
            // throw away what the command knew and the numbers could not say.
            var payload = new JObject();
            ApplicationOutcome.Stamp(payload, ApplicationOutcome.Declare(
                ApplicationState.Uncertain, ApplicationOutcome.NotStarted, 1, 0, 0, 0, 0, 1));

            Assert.Equal(ApplicationState.Uncertain, ApplicationOutcome.Read(payload));
        }

        [Fact]
        public void Every_declaration_the_official_helpers_build_survives_being_read_back()
        {
            // The corroboration must not have made honest commands unreadable. Swept over
            // the shapes the twelve plan tools actually produce.
            for (int requested = 0; requested <= 3; requested++)
            for (int applied = 0; applied <= requested; applied++)
            for (int unresolved = 0; unresolved <= 2; unresolved++)
            for (int failed = 0; failed <= 2; failed++)
            for (int unknown = 0; unknown <= 2; unknown++)
            foreach (string status in new[] { "Committed", "RolledBack", "not_started", "Pending" })
            {
                var payload = new JObject();
                ApplicationOutcome.StampApplied(payload, status, requested, applied, applied,
                                                unresolved, failed, unknown);
                ApplicationState expected = ApplicationOutcome.Applied(status, requested, applied, applied,
                                                                       unresolved, failed, unknown);
                Assert.Equal(expected, ApplicationOutcome.Read(payload));
            }

            for (int requested = 0; requested <= 3; requested++)
            for (int unresolved = 0; unresolved <= 2; unresolved++)
            for (int failed = 0; failed <= 2; failed++)
            for (int unknown = 0; unknown <= 2; unknown++)
            {
                var payload = new JObject();
                ApplicationOutcome.StampRehearsal(payload, requested, unresolved, failed, unknown);
                Assert.Equal(ApplicationOutcome.Rehearsal(unresolved, failed, unknown),
                             ApplicationOutcome.Read(payload));
            }
        }

        [Fact]
        public void A_declaration_survives_a_serialization_round_trip_unchanged()
        {
            // The durable idempotency ledger writes results to disk and rebuilds them, so a
            // declaration that changed meaning across JSON would change what a replay is
            // allowed to do.
            foreach (string status in new[] { "Committed", "RolledBack", "Pending", "not_started" })
            for (int requested = 0; requested <= 3; requested++)
            {
                var payload = new JObject();
                ApplicationOutcome.StampApplied(payload, status, requested, requested, requested, 0, 0, 0);

                ApplicationState before = ApplicationOutcome.Read(payload);
                ApplicationState after = ApplicationOutcome.Read(
                    JObject.Parse(payload.ToString(Newtonsoft.Json.Formatting.None)));

                Assert.Equal(before, after);
            }
        }

        [Fact]
        public void The_arbiter_never_throws_whatever_it_is_handed()
        {
            foreach (object data in new object[]
                     {
                         null, "", "x", 0, -1, 1.5, true, new JArray(), new JObject(),
                         new JObject { [ApplicationOutcome.Key] = new JObject() },
                         new JObject { [ApplicationOutcome.Key] = new JObject { ["state"] = new JArray() } }
                     })
            {
                ApplicationOutcome.Read(data);
                ApplicationOutcome.IsDeclared(data);
            }
        }
    }
}
