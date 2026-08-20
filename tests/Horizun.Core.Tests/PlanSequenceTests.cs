// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// The P0 is about a SEQUENCE, not about one action: execute_plan assimilated a
// TransactionGroup after a child answered Success over work that was partial,
// reverted or unmeasured - and, worse, ran the NEXT action on top of it, which in
// a real plan is often a delete.
//
// So these drive the ledger the way the command's loop drives it: record, and stop
// on false. That loop is five lines in ExecutePlanCommand and is pinned separately
// by ApplicationDeclarationWiringTests (the command must call RecordExecuted and
// must throw when it answers false); the DECISION exercised here is production.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class PlanSequenceTests
    {
        private static JObject Declared(ApplicationState state)
        {
            // Built through the official helpers wherever the state is reachable from
            // counts, so the sequence is driven by declarations a real command emits.
            var payload = new JObject();
            switch (state)
            {
                case ApplicationState.VerifiedApplied:
                    ApplicationOutcome.StampApplied(payload, "Committed", 3, 3, 3, 0, 0, 0); break;
                case ApplicationState.NoOp:
                    ApplicationOutcome.StampApplied(payload, "not_started", 0, 0, 0, 0, 0, 0); break;
                case ApplicationState.Partial:
                    ApplicationOutcome.StampApplied(payload, "Committed", 5, 3, 3, 0, 2, 0); break;
                case ApplicationState.RolledBack:
                    ApplicationOutcome.StampApplied(payload, "RolledBack", 4, 0, 0, 0, 0, 0); break;
                case ApplicationState.Failed:
                    ApplicationOutcome.StampApplied(payload, "Committed", 3, 0, 0, 0, 0, 0); break;
                case ApplicationState.Rehearsed:
                    ApplicationOutcome.StampRehearsal(payload, 3, 0, 0, 0); break;
                default:
                    ApplicationOutcome.StampApplied(payload, "Committed", 3, 2, 2, 0, 0, 1); break;
            }
            Assert.Equal(state, ApplicationOutcome.Read(payload));   // the fixture is honest
            return payload;
        }

        /// <summary>
        /// The command's loop: record each action, and stop the moment the ledger says the
        /// plan may not build on it. Returns how many actions were REACHED.
        /// </summary>
        private static int Drive(PlanLedger ledger, IEnumerable<JObject> actions, string lastTool = null)
        {
            int reached = 0;
            foreach (JObject data in actions)
            {
                ApplicationState state;
                reached++;
                string tool = lastTool ?? "horizun_write_params_verified";
                if (!ledger.RecordExecuted(reached - 1, "a" + reached, tool, true, data, null, out state))
                    return reached;
            }
            return reached;
        }

        // ---- Sequences the plan must keep --------------------------------------

        [Fact]
        public void Verified_then_verified_assimilates_and_counts_both()
        {
            var ledger = new PlanLedger();
            int reached = Drive(ledger, new[] { Declared(ApplicationState.VerifiedApplied),
                                                Declared(ApplicationState.VerifiedApplied) });

            Assert.Equal(2, reached);
            Assert.Null(ledger.FailedAction);
            Assert.Equal(2, ledger.VerifiedActions);
            Assert.Equal(2, ledger.SuccessPayload("g", new JObject()).Value<int>("actions_verified"));
        }

        [Fact]
        public void A_legitimate_no_op_then_a_verified_action_assimilates()
        {
            var ledger = new PlanLedger();
            Drive(ledger, new[] { Declared(ApplicationState.NoOp), Declared(ApplicationState.VerifiedApplied) });

            Assert.Null(ledger.FailedAction);
            Assert.Equal(2, ledger.VerifiedActions);
            Assert.Equal(1, ledger.SuccessPayload("g", new JObject()).Value<int>("actions_no_op"));
        }

        // ---- Sequences that must stop ------------------------------------------

        [Theory]
        [InlineData(ApplicationState.Partial)]
        [InlineData(ApplicationState.RolledBack)]
        [InlineData(ApplicationState.Failed)]
        [InlineData(ApplicationState.Uncertain)]
        [InlineData(ApplicationState.Rehearsed)]
        public void A_first_action_in_any_non_applied_state_stops_the_plan_before_a_delete_runs(ApplicationState bad)
        {
            var ledger = new PlanLedger();
            int reached = Drive(ledger, new[] { Declared(bad), Declared(ApplicationState.VerifiedApplied) },
                                lastTool: "horizun_delete_verified");

            Assert.Equal(1, reached);                 // the delete was never reached
            Assert.Single(ledger.Executed);
            Assert.Equal(0, ledger.VerifiedActions);
            Assert.Equal("a1", ledger.FailedAction.Value<string>("key"));
        }

        [Fact]
        public void A_child_that_returned_a_failure_stops_the_plan()
        {
            var ledger = new PlanLedger();
            ApplicationState state;

            Assert.False(ledger.RecordExecuted(0, "first", "horizun_create_elements",
                                               false, null, "Revit refused the type", out state));
            Assert.Equal(0, ledger.VerifiedActions);
            Assert.Equal("the command returned a failure", ledger.FailedAction.Value<string>("stopped_because"));
        }

        [Fact]
        public void A_child_that_answered_success_with_no_declaration_stops_the_plan()
        {
            var ledger = new PlanLedger();
            ApplicationState state;

            Assert.False(ledger.RecordExecuted(0, "mystery", "horizun_delete_verified", true,
                                               new JObject { ["transaction_status"] = "Committed" }, null, out state));
            Assert.Equal(ApplicationState.Uncertain, state);
            Assert.False(ledger.FailedAction.Value<bool>("application_declared"));
        }

        [Fact]
        public void A_child_whose_declaration_contradicts_itself_stops_the_plan()
        {
            var ledger = new PlanLedger();
            ApplicationState state;
            var forged = new JObject
            {
                [ApplicationOutcome.Key] = new JObject
                {
                    ["state"] = "verified_applied", ["fully_applied"] = true,
                    ["transaction_status"] = "RolledBack",
                    ["requested"] = 5, ["applied"] = 5, ["verified"] = 5,
                    ["unresolved"] = 0, ["failed"] = 0, ["unknown"] = 0
                }
            };

            Assert.False(ledger.RecordExecuted(0, "forged", "horizun_delete_verified", true, forged, null, out state));
            Assert.Equal(ApplicationState.Uncertain, state);
            // It DID declare something - the block exists - so the diagnosis says which
            // bug this is: a wrong declaration, not a missing one.
            Assert.True(ledger.FailedAction.Value<bool>("application_declared"));
        }

        [Fact]
        public void A_verified_action_followed_by_a_partial_one_keeps_the_first_count_and_stops()
        {
            var ledger = new PlanLedger();
            int reached = Drive(ledger, new[] { Declared(ApplicationState.VerifiedApplied),
                                                Declared(ApplicationState.Partial),
                                                Declared(ApplicationState.VerifiedApplied) });

            Assert.Equal(2, reached);                    // the third never ran
            Assert.Equal(2, ledger.Executed.Count);
            Assert.Equal(1, ledger.VerifiedActions);     // the first really did land
            Assert.Equal("a2", ledger.FailedAction.Value<string>("key"));
            Assert.Equal(1, ledger.FailedAction.Value<int>("index"));
        }

        // ---- actions_verified and the trace ------------------------------------

        [Fact]
        public void Actions_verified_never_includes_the_action_that_stopped_the_plan()
        {
            foreach (ApplicationState bad in new[] { ApplicationState.Partial, ApplicationState.RolledBack,
                                                     ApplicationState.Failed, ApplicationState.Uncertain,
                                                     ApplicationState.Rehearsed })
            {
                var ledger = new PlanLedger();
                Drive(ledger, new[] { Declared(ApplicationState.VerifiedApplied), Declared(bad) });

                Assert.Equal(1, ledger.VerifiedActions);
                Assert.Equal(2, ledger.Executed.Count);
                Assert.False(((JObject)ledger.Executed[1]).Value<bool>("fully_applied"));
            }
        }

        [Fact]
        public void A_successful_reply_never_reports_more_executed_than_verified()
        {
            var ledger = new PlanLedger();
            Drive(ledger, new[] { Declared(ApplicationState.VerifiedApplied), Declared(ApplicationState.NoOp) });

            JObject payload = ledger.SuccessPayload("g", new JObject());
            Assert.Equal(payload.Value<int>("actions_executed"), payload.Value<int>("actions_verified"));
        }

        [Fact]
        public void The_no_op_counter_only_moves_for_a_declaration_that_asked_for_nothing()
        {
            var ledger = new PlanLedger();
            Drive(ledger, new[] { Declared(ApplicationState.VerifiedApplied) });

            Assert.Equal(1, ledger.VerifiedActions);
            Assert.Equal(0, ledger.SuccessPayload("g", new JObject()).Value<int>("actions_no_op"));
        }

        [Fact]
        public void The_trace_keeps_order_key_tool_state_success_and_error()
        {
            var ledger = new PlanLedger();
            ApplicationState state;
            ledger.RecordExecuted(0, "walls", "horizun_create_elements", true,
                                  Declared(ApplicationState.VerifiedApplied), null, out state);
            ledger.RecordExecuted(1, "tags", "horizun_annotate", false, null, "no tag type", out state);

            var first = (JObject)ledger.Executed[0];
            var second = (JObject)ledger.Executed[1];

            Assert.Equal(0, first.Value<int>("index"));
            Assert.Equal("walls", first.Value<string>("key"));
            Assert.Equal("horizun_create_elements", first.Value<string>("tool"));
            Assert.Equal("verified_applied", first.Value<string>("application_state"));
            Assert.True(first.Value<bool>("success"));

            Assert.Equal(1, second.Value<int>("index"));
            Assert.Equal("tags", second.Value<string>("key"));
            Assert.Equal("horizun_annotate", second.Value<string>("tool"));
            Assert.False(second.Value<bool>("success"));
            Assert.Equal("no tag type", second.Value<string>("error"));
        }

        // ---- The property, swept over every state for lengths 1..4 -------------

        [Fact]
        public void A_sequence_is_assimilable_if_and_only_if_every_state_is_verified_or_a_real_no_op()
        {
            ApplicationState[] all =
            {
                ApplicationState.VerifiedApplied, ApplicationState.NoOp, ApplicationState.Partial,
                ApplicationState.RolledBack, ApplicationState.Failed, ApplicationState.Uncertain,
                ApplicationState.Rehearsed
            };

            foreach (ApplicationState[] sequence in Sequences(all, 4))
            {
                var ledger = new PlanLedger();
                int reached = Drive(ledger, sequence.Select(Declared).ToList());

                int validPrefix = 0;
                while (validPrefix < sequence.Length && ApplicationOutcome.IsFullyApplied(sequence[validPrefix]))
                    validPrefix++;

                bool assimilable = validPrefix == sequence.Length;
                string label = string.Join(",", sequence.Select(ApplicationOutcome.Name));

                // 1. Assimilable exactly when every state is one a plan may keep.
                Assert.Equal(assimilable, ledger.FailedAction == null);

                // 2. The first non-assimilable action stops the sequence...
                Assert.Equal(assimilable ? sequence.Length : validPrefix + 1, reached);

                // 3. ...and nothing after it appears as executed.
                Assert.Equal(assimilable ? sequence.Length : validPrefix + 1, ledger.Executed.Count);

                // 4. VerifiedActions is the valid prefix, never the size of the trace.
                Assert.Equal(validPrefix, ledger.VerifiedActions);
                if (!assimilable)
                    Assert.True(ledger.VerifiedActions < ledger.Executed.Count, label);
            }
        }

        private static IEnumerable<ApplicationState[]> Sequences(ApplicationState[] alphabet, int maxLength)
        {
            for (int length = 1; length <= maxLength; length++)
            {
                var indices = new int[length];
                while (true)
                {
                    yield return indices.Select(i => alphabet[i]).ToArray();

                    int position = length - 1;
                    while (position >= 0 && ++indices[position] == alphabet.Length)
                    {
                        indices[position] = 0;
                        position--;
                    }
                    if (position < 0) break;
                }
            }
        }
    }
}
