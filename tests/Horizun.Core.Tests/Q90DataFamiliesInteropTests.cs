using System;
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    /// <summary>
    /// Quantities / schedules, third-party families and IFC/DWG: the causes of the failures that
    /// repeated in the field logs, said as causes (and, for a missing datum, as a SUPUESTO).
    /// </summary>
    public sealed class Q90DataFamiliesInteropTests
    {
        // ---- assumptions ----------------------------------------------------------------

        [Fact]
        public void Assumption_is_stamped_with_flag_and_indicator()
        {
            var payload = new JObject { ["ok"] = true };
            AssumptionNotes.Stamp(payload, new[] { AssumptionNotes.Row("a", "b", "c") });
            Assert.True((bool)payload["has_assumptions"]);
            var row = (JObject)((JArray)payload["assumptions"])[0];
            Assert.Equal("SUPUESTO", (string)row["flag"]);
            Assert.Equal("c", (string)row["override"]);
        }

        [Fact]
        public void No_rows_means_no_indicator()
        {
            var payload = new JObject();
            AssumptionNotes.Stamp(payload, new JObject[0]);
            Assert.Null(payload["has_assumptions"]);
            Assert.Null(payload["assumptions"]);
        }

        [Fact]
        public void A_failure_that_carries_a_payload_is_not_stamped_by_StampResult()
        {
            // A failure WITH a JObject Data: only the !Success test keeps StampResult off it (a failure with no
            // Data would be left alone by the "Data is JObject" test alone, and proved nothing).
            var data = new JObject { ["rows"] = 3 };
            var detail = new JObject { ["error_code"] = "x" };
            CommandResult failure = CommandResult.Restore(false, data, "nope", null, null, null, detail);
            CommandResult back = AssumptionNotes.StampResult(failure, new[] { AssumptionNotes.Row("a", "b", null) });
            Assert.Same(failure, back);
            Assert.False(back.Success);
            Assert.Equal("nope", back.Error);
            Assert.Null(data["assumptions"]);
            Assert.Null(data["has_assumptions"]);
            Assert.Null(back.Detail["assumptions"]);
            // The same payload on a SUCCESS is stamped: the difference is the success flag, nothing else.
            var okData = new JObject { ["rows"] = 3 };
            AssumptionNotes.StampResult(CommandResult.Restore(true, okData, null, null, null, null, null), new[] { AssumptionNotes.Row("a", "b", null) });
            Assert.True((bool)okData["has_assumptions"]);
        }

        [Fact]
        public void MarkFailure_keeps_the_code_and_the_detail_and_adds_the_rows()
        {
            var detail = new JObject { ["error_code"] = "entrada_invalida", ["path"] = "quantities[0]" };
            CommandResult failure = CommandResult.FailWithDetail("bad", detail);
            CommandResult marked = AssumptionNotes.MarkFailure(failure, "p: ", new[] { AssumptionNotes.Row("a", "b", null) });
            Assert.Equal("entrada_invalida", (string)marked.Detail["error_code"]);
            Assert.Equal("quantities[0]", (string)marked.Detail["path"]);
            Assert.Single((JArray)marked.Detail["assumptions"]);
            // The original detail is not mutated: the marked failure carries a copy.
            Assert.Null(detail["assumptions"]);
        }

        [Fact]
        public void A_failure_on_an_assumed_value_says_so_and_keeps_its_detail()
        {
            var detail = new JObject { ["error_code"] = "x" };
            CommandResult failure = CommandResult.FailWithDetail("'level' is not read in mode 'takeoff'.", detail);
            CommandResult marked = AssumptionNotes.MarkFailure(failure, "deduced: ", new[] { AssumptionNotes.Row("mode = 'takeoff'", "b", null) });
            Assert.False(marked.Success);
            Assert.Equal("deduced: 'level' is not read in mode 'takeoff'.", marked.Error);
            Assert.Equal("x", (string)marked.Detail["error_code"]);
            Assert.True((bool)marked.Detail["has_assumptions"]);
            Assert.Equal("SUPUESTO", (string)marked.Detail["assumptions"][0]["flag"]);
            // A success is not a failure to mark.
            CommandResult ok = CommandResult.Ok(new JObject { ["rows"] = 1 });
            Assert.Same(ok, AssumptionNotes.MarkFailure(ok, "deduced: ", new[] { AssumptionNotes.Row("a", "b", null) }));
        }

        [Fact]
        public void A_successful_object_result_is_stamped_in_place()
        {
            var data = new JObject { ["rows"] = 3 };
            CommandResult ok = CommandResult.Ok(data);
            AssumptionNotes.StampResult(ok, new[] { AssumptionNotes.Row("mode", "why", null) });
            Assert.True((bool)((JObject)ok.Data)["has_assumptions"]);
        }

        // ---- quantities: mode inference --------------------------------------------------

        private static Func<string, bool> Has(params string[] keys) => k => keys.Contains(k);

        [Fact]
        public void Takeoff_keys_without_mode_run_takeoff_as_an_assumption()
        {
            QuantitiesModeDecision d = QuantitiesModeInference.Decide(null, Has("categories", "top"));
            Assert.Equal("takeoff", d.InferredMode);
            Assert.Equal("categories", d.DecidingKey);
            JObject row = QuantitiesModeInference.AssumptionRow(d);
            Assert.Equal("SUPUESTO", (string)row["flag"]);
            Assert.Contains("takeoff", (string)row["what"]);
        }

        [Fact]
        public void Carbon_keys_without_mode_run_carbon()
        {
            Assert.Equal("carbon", QuantitiesModeInference.Decide("", Has("carbon_factors")).InferredMode);
        }

        [Fact]
        public void An_explicit_mode_is_never_overridden()
        {
            Assert.Null(QuantitiesModeInference.Decide("volume", Has("categories")).InferredMode);
            Assert.Null(QuantitiesModeInference.Decide("takeoff", Has("categories")).InferredMode);
        }

        [Fact]
        public void Keys_of_two_modes_are_refused_naming_the_keys_of_each_mode()
        {
            QuantitiesModeDecision d = QuantitiesModeInference.Decide(null, Has("categories", "carbon_factors"));
            Assert.Null(d.InferredMode);
            Assert.Contains("'categories' is read by takeoff", d.Refusal);
            Assert.Contains("'carbon_factors' is read by carbon", d.Refusal);
        }

        [Theory]
        [InlineData("tolerance_pct", "volume")]
        [InlineData("only_disagreements", "volume")]
        [InlineData("level", "room_finishes")]
        public void A_takeoff_key_beside_a_key_takeoff_does_not_read_is_not_inferred(string foreign, string readBy)
        {
            QuantitiesModeDecision d = QuantitiesModeInference.Decide(null, Has("categories", foreign));
            Assert.Null(d.InferredMode);
            Assert.NotNull(d.Refusal);
            Assert.Contains("'" + foreign + "' is read by " + readBy, d.Refusal);
            Assert.Contains("'categories' is read by takeoff", d.Refusal);
            Assert.Contains("Nothing was measured", d.Refusal);
        }

        [Fact]
        public void A_carbon_key_beside_a_takeoff_only_or_volume_key_is_not_inferred()
        {
            Assert.NotNull(QuantitiesModeInference.Decide(null, Has("carbon_factors", "include_links")).Refusal);
            Assert.NotNull(QuantitiesModeInference.Decide(null, Has("factor_source", "tolerance_pct")).Refusal);
        }

        [Fact]
        public void A_key_sent_as_false_or_null_is_not_present()
        {
            var request = JObject.Parse("{ \"categories\": [\"OST_Walls\"], \"only_disagreements\": false, \"tolerance_pct\": null }");
            QuantitiesModeDecision d = QuantitiesModeInference.Decide(request);
            Assert.Equal("takeoff", d.InferredMode);
            Assert.Null(d.Refusal);
            Assert.False(QuantitiesModeInference.IsSent(request, "only_disagreements"));
            Assert.True(QuantitiesModeInference.IsSent(JObject.Parse("{ \"only_disagreements\": true }"), "only_disagreements"));
            // A written mode is never overridden, even as a JObject.
            Assert.Null(QuantitiesModeInference.Decide(JObject.Parse("{ \"mode\": \"volume\", \"categories\": [] }")).InferredMode);
        }

        [Fact]
        public void A_failure_of_a_deduced_mode_starts_by_saying_it_was_deduced()
        {
            QuantitiesModeDecision d = QuantitiesModeInference.Decide(null, Has("categories"));
            string prefix = QuantitiesModeInference.FailurePrefix(d);
            Assert.Contains("deduced as 'takeoff' from 'categories'", prefix);
            Assert.Contains("SUPUESTO", prefix);
            Assert.Contains("level", QuantitiesModeInference.NotReadByMode("takeoff"));
            Assert.Empty(QuantitiesModeInference.NotReadByMode("room_finishes"));
        }

        [Fact]
        public void Quantities_checks_the_document_before_judging_the_keys()
        {
            string src = Source("src", "Horizun.Revit", "Commands", "QuantitiesCommand.cs");
            int exec = src.IndexOf("public CommandResult Execute(UIApplication app, string paramsJson)", StringComparison.Ordinal);
            int doc = src.IndexOf("DocumentRefusal(app, probe)", exec, StringComparison.Ordinal);
            int decide = src.IndexOf("QuantitiesModeInference.Decide(probe)", exec, StringComparison.Ordinal);
            Assert.True(doc > exec && decide > doc, "the document check must come before the mixed-keys refusal");
        }

        [Fact]
        public void Keys_that_several_modes_read_decide_nothing()
        {
            Assert.Null(QuantitiesModeInference.Decide(null, Has("phase", "level", "group_by", "category")).InferredMode);
            Assert.Null(QuantitiesModeInference.Decide(null, Has()).InferredMode);
        }

        // ---- schedules: unschedulable fields ---------------------------------------------

        [Theory]
        [InlineData("FAMILY_LEVEL_PARAM", "SCHEDULE_LEVEL_PARAM")]
        [InlineData("family level param", "SCHEDULE_LEVEL_PARAM")]
        [InlineData("FamilyAndType", "ELEM_FAMILY_AND_TYPE_PARAM")]
        public void Known_wrong_tokens_have_a_schedulable_twin(string requested, string first)
        {
            Assert.Equal(first, ScheduleFieldRescue.AliasesFor(requested).First());
        }

        [Fact]
        public void Unknown_tokens_have_no_alias()
        {
            Assert.Empty(ScheduleFieldRescue.AliasesFor("HZ_NOT_A_FIELD"));
            Assert.Empty(ScheduleFieldRescue.AliasesFor(null));
        }

        [Fact]
        public void Suggestions_are_the_closest_names_offered()
        {
            var offered = new[] { "Mark", "Level", "Sill Height", "Head Height", "Width", "Height", "Comments" };
            Assert.Equal("Level", ScheduleFieldRescue.Suggest("Levl", offered).First());
            var h = ScheduleFieldRescue.Suggest("Height", offered);
            Assert.Equal("Height", h[0]);
            Assert.Contains("Sill Height", h);
            Assert.Empty(ScheduleFieldRescue.Suggest("zzzzzz", offered));
        }

        [Fact]
        public void Suggestions_ignore_accents_and_case_and_never_repeat()
        {
            var s = ScheduleFieldRescue.Suggest("NIVEL", new[] { "Nivel", "nivel", "Nivel de base" });
            Assert.Equal(2, s.Count);
            Assert.Equal("Nivel", s[0]);
        }

        [Fact]
        public void Nothing_resolved_lists_what_the_category_offers()
        {
            string m = ScheduleFieldRescue.NothingResolved("Doors", new[] { "X1", "X2" }, new[] { "Mark", "Level" });
            Assert.Contains("X1, X2", m);
            Assert.Contains("Level, Mark", m);
            Assert.Contains("Nothing was changed", m);
        }

        // ---- schedules: the field plan the token is bound to ----------------------------------

        private static ScheduleFieldResolution R(string requested, string used, string identity)
            => new ScheduleFieldResolution { Requested = requested, Used = used, Identity = identity };

        [Fact]
        public void The_field_plan_says_what_is_added_substituted_added_once_and_left_out()
        {
            ScheduleFieldPlan plan = ScheduleFieldPlan.Build(new[]
            {
                R("Level", "Level", "Instance:-1001"),
                R("FAMILY_LEVEL_PARAM", "SCHEDULE_LEVEL_PARAM", "Instance:-1001"),   // its twin is the column already requested
                R("Mark", "Mark", "Instance:-1002"),
                R("Levl2", null, null),
                R("TYPE_NAME", "ELEM_TYPE_PARAM", "Instance:-1003")
            });
            Assert.Equal(new[] { "Level", "Mark", "TYPE_NAME" }, plan.ToAdd.Select(e => e.Requested).ToArray());
            Assert.Equal(new[] { "Levl2" }, plan.OmittedNames.ToArray());
            ScheduleFieldPlanEntry dup = Assert.Single(plan.Duplicates);
            Assert.Equal("FAMILY_LEVEL_PARAM", dup.Requested);
            Assert.Equal("Level", dup.DuplicateOf);
            Assert.Equal("TYPE_NAME", Assert.Single(plan.SubstitutedPairs).Key);
            Assert.False(plan.NothingResolved);
            JObject dupRow = ScheduleFieldPlan.DuplicateRow(plan.Duplicates);
            Assert.Equal("SUPUESTO", (string)dupRow["flag"]);
            Assert.Contains("same column as Level", (string)dupRow["what"]);
        }

        [Fact]
        public void A_column_revit_adds_by_itself_is_not_added_again()
        {
            ScheduleFieldPlan plan = ScheduleFieldPlan.Build(new[] { R("Key Name", "Key Name", "Instance:-9") }, new[] { "Instance:-9" });
            Assert.Empty(plan.ToAdd);
            Assert.Equal(ScheduleFieldPlan.AddedByRevit, plan.Duplicates[0].DuplicateOf);
        }

        [Fact]
        public void Nothing_resolved_is_known_before_the_token()
        {
            Assert.True(ScheduleFieldPlan.Build(new[] { R("A", null, null), R("B", null, null) }).NothingResolved);
            Assert.False(ScheduleFieldPlan.Build(new ScheduleFieldResolution[0]).NothingResolved);
        }

        [Fact]
        public void The_fingerprint_changes_when_a_field_resolves_differently()
        {
            string a = ScheduleFieldPlan.Build(new[] { R("Level", "Level", "Instance:-1"), R("X", null, null) }).Fingerprint();
            string b = ScheduleFieldPlan.Build(new[] { R("Level", "Level", "Instance:-1"), R("X", "X", "Instance:-2") }).Fingerprint();
            string c = ScheduleFieldPlan.Build(new[] { R("Level", "Level", "Instance:-7"), R("X", null, null) }).Fingerprint();
            Assert.NotEqual(a, b);
            Assert.NotEqual(a, c);
            Assert.Equal(a, ScheduleFieldPlan.Build(new[] { R("Level", "Level", "Instance:-1"), R("X", null, null) }).Fingerprint());
        }

        [Fact]
        public void Group_by_a_field_left_out_says_revit_does_not_offer_it()
        {
            ScheduleFieldPlan plan = ScheduleFieldPlan.Build(new[]
            {
                R("Level", "Level", "Instance:-1"), R("Zone", null, null), R("FAMILY_LEVEL_PARAM", "SCHEDULE_LEVEL_PARAM", "Instance:-1")
            });
            string omitted = plan.GroupByProblem("Doors", new[] { "zone" });
            Assert.Contains("'zone', which was left out", omitted);
            Assert.Contains("Revit does not offer it as a field for Doors", omitted);
            Assert.Contains("same column as 'Level'", plan.GroupByProblem("Doors", new[] { "FAMILY_LEVEL_PARAM" }));
            Assert.Null(plan.GroupByProblem("Doors", new[] { "Level" }));
            Assert.Null(plan.GroupByProblem("Doors", null));
        }

        [Fact]
        public void A_schedule_with_fields_left_out_is_partial_never_verified_in_full()
        {
            JObject partial = ScheduleFieldPlan.Tally("Committed", requested: 4, omittedCount: 1, postconditionVerified: true);
            Assert.Equal("partial", (string)partial["state"]);
            Assert.False((bool)partial["fully_applied"]);
            Assert.Equal(4, (int)partial["requested"]);
            Assert.Equal(3, (int)partial["verified"]);
            Assert.Equal(1, (int)partial["unresolved"]);
            Assert.NotEqual("verified_applied", (string)ScheduleFieldPlan.Tally("Committed", 2, 1, true)["state"]);
            JObject notChecked = ScheduleFieldPlan.Tally("Committed", 4, 1, postconditionVerified: false);
            Assert.Equal(0, (int)notChecked["verified"]);
        }

        private static ScheduleFieldPlan PlanWithTwoLeftOut() => ScheduleFieldPlan.Build(new[]
        {
            new ScheduleFieldResolution { Requested = "Mark", Used = "Mark", Identity = "Instance:1" },
            new ScheduleFieldResolution { Requested = "Bogus", Used = null, Identity = null },
            new ScheduleFieldResolution { Requested = "Other", Used = null, Identity = null }
        });

        [Fact]
        public void A_rehearsal_with_fields_left_out_is_a_successful_clean_rehearsal_with_supuestos()
        {
            // The rehearsal payload as the command builds it, sent through CommandResult.Ok - the boundary that turns
            // a dry run declaring rejected/partial into a failure with the token hidden in Detail.
            ScheduleFieldPlan plan = PlanWithTwoLeftOut();
            var rehearsal = new JObject { ["dry_run"] = true, ["transaction_status"] = "not_started", ["confirmation_token"] = "tok-1" };
            AssumptionNotes.Stamp(rehearsal, new[] { ScheduleFieldRescue.SkippedRow("Doors", plan.OmittedNames, null) });
            plan.StampRehearsal(rehearsal);

            CommandResult r = CommandResult.Ok(rehearsal);
            Assert.True(r.Success, r.Error);
            var data = (JObject)r.Data;
            Assert.Equal("tok-1", (string)data["confirmation_token"]);
            Assert.True((bool)data["has_assumptions"]);
            Assert.Equal("SUPUESTO", (string)data["assumptions"][0]["flag"]);
            Assert.Contains("remove the field", (string)data["assumptions"][0]["override"]);
            Assert.Equal("rehearsed", (string)data[ApplicationOutcome.Key]["state"]);
            Assert.Equal("not_started", (string)data["transaction_status"]);
            Assert.Equal("apply", (string)data["transaction_status_scope"]);
            Assert.Equal("RolledBack", (string)data["field_probe_transaction_status"]);
            // And it says the apply will be partial.
            Assert.True(PartialApplyNotice.Announced(data));
            Assert.Contains("Bogus, Other", (string)data[PartialApplyNotice.WhyKey]);
        }

        [Fact]
        public void A_rehearsal_with_every_field_resolved_is_clean_and_announces_nothing()
        {
            var plan = ScheduleFieldPlan.Build(new[]
            {
                new ScheduleFieldResolution { Requested = "Mark", Used = "Mark", Identity = "Instance:1" },
                new ScheduleFieldResolution { Requested = "Level", Used = "Level", Identity = "Instance:2" }
            });
            var rehearsal = new JObject { ["dry_run"] = true };
            ApplicationOutcome.Stamp(rehearsal, ScheduleFieldPlan.RehearsalDeclaration());
            plan.AnnouncePartialApply(rehearsal);
            Assert.True(CommandResult.Ok(rehearsal).Success);
            Assert.False(PartialApplyNotice.Announced(rehearsal));
            Assert.True(ApplicationOutcome.IsValidRehearsal(ApplicationOutcome.Read(rehearsal)));
            // A key schedule with no field requested is one clean schedule too.
            var key = new JObject { ["dry_run"] = true };
            ApplicationOutcome.Stamp(key, ScheduleFieldPlan.RehearsalDeclaration());
            ScheduleFieldPlan.Build(new ScheduleFieldResolution[0]).AnnouncePartialApply(key);
            Assert.Equal("rehearsed", (string)key[ApplicationOutcome.Key]["state"]);
            Assert.False(PartialApplyNotice.Announced(key));
        }

        [Fact]
        public void A_plan_step_that_announces_a_partial_apply_is_not_clean_in_the_plan()
        {
            var rehearsal = new JObject { ["dry_run"] = true, ["transaction_status"] = "not_started" };
            ApplicationOutcome.Stamp(rehearsal, ScheduleFieldPlan.RehearsalDeclaration());
            PlanWithTwoLeftOut().AnnouncePartialApply(rehearsal);
            var ledger = new PlanLedger();
            JObject row = ledger.RecordRehearsal(0, "s1", "horizun_create_schedule", true, rehearsal, null);
            Assert.False(ledger.RehearsedCleanly);
            Assert.Contains("partial", (string)row["not_clean_in_plan"]);
            CommandResult refusal = PlanFailure.BeforeGroupResult("pre_apply_recheck", new JArray(row), row,
                PlanFailure.PreApplyRehearsalError("s1", row), CommandResult.Ok(rehearsal));
            Assert.False(refusal.Success);
            Assert.Contains("rehearsed cleanly but announced that its apply will be partial", refusal.Error);
            Assert.Contains("Bogus, Other", refusal.Error);
            Assert.True((bool)refusal.Detail["nothing_ran"]);
            Assert.False((bool)refusal.Detail["transaction_group_started"]);

            var clean = new JObject { ["dry_run"] = true, ["transaction_status"] = "not_started" };
            ApplicationOutcome.Stamp(clean, ScheduleFieldPlan.RehearsalDeclaration());
            ScheduleFieldPlan.Build(new[] { new ScheduleFieldResolution { Requested = "Mark", Used = "Mark", Identity = "Instance:1" } }).AnnouncePartialApply(clean);
            var ledger2 = new PlanLedger();
            ledger2.RecordRehearsal(0, "s1", "horizun_create_schedule", true, clean, null);
            Assert.True(ledger2.RehearsedCleanly);
            Assert.Null(PartialApplyNotice.PlanRefusal("horizun_create_schedule", clean));
            // Only an explicit boolean true counts.
            Assert.False(PartialApplyNotice.Announced(new JObject { [PartialApplyNotice.Key] = "true" }));
        }

        [Theory]
        [InlineData("postcondition", ApplicationState.Partial, true)]
        [InlineData("postcondition", ApplicationState.Uncertain, true)]
        [InlineData("schedule_reread", ApplicationState.Uncertain, false)]
        [InlineData("result_read", ApplicationState.Uncertain, true)]
        public void A_committed_schedule_failure_keeps_its_field_choices_and_verification_evidence(
            string stage, ApplicationState state, bool reread)
        {
            JObject omitted = ScheduleFieldRescue.SkippedRow("Doors", new[] { "Bogus" }, null);
            JObject substituted = ScheduleFieldRescue.SubstitutedRow(new[] { new KeyValuePair<string, string>("Level", "Schedule Level") });
            JObject evidence = reread ? new JObject { ["check"] = "field order" } : null;
            CommandResult failure = ScheduleFieldPlan.WriteFailure("Re-read did not match.", 45, "Committed",
                state, stage, reread, evidence, new[] { omitted, substituted });

            Assert.False(failure.Success);
            Assert.StartsWith("The schedule used assumed field choices (SUPUESTO).", failure.Error);
            Assert.Contains("Re-read did not match.", failure.Error);
            Assert.Equal(45, (long)failure.Detail["schedule_id"]);
            Assert.Equal(stage, (string)failure.Detail["stage"]);
            Assert.Equal(state, ApplicationOutcome.Read(failure.Detail));
            Assert.Equal(reread ? 1 : 0, (int)failure.Detail["application"]["applied"]);
            Assert.True(JToken.DeepEquals(evidence, failure.Detail["evidence"]));
            Assert.True((bool)failure.Detail["has_assumptions"]);
            Assert.True(JToken.DeepEquals(new JArray(omitted.DeepClone(), substituted.DeepClone()), failure.Detail["assumptions"]));
        }

        [Fact]
        public void A_schedule_failure_without_assumed_fields_omits_the_assumptions_block()
        {
            CommandResult failure = ScheduleFieldPlan.WriteFailure("Could not re-read.", 45, "Committed",
                ApplicationState.Uncertain, "schedule_reread", false, null, Array.Empty<JObject>());
            Assert.Equal("Could not re-read.", failure.Error);
            Assert.Null(failure.Detail["has_assumptions"]);
            Assert.Null(failure.Detail["assumptions"]);
        }

        [Theory]
        [InlineData("field_identity", "Pending", false, false)]
        [InlineData("grouping", "Error", true, false)]
        [InlineData("grouping", "RolledBack", false, true)]
        [InlineData("field_identity", "RolledBack", true, false)]
        [InlineData("grouping", "RolledBack", null, false)]
        [InlineData("transaction_apply", "unknown", null, false)]
        public void A_failed_schedule_apply_requires_both_rollback_and_absence_before_claiming_a_clean_model(
            string stage, string status, bool? scheduleStillThere, bool clean)
        {
            CommandResult failure = ScheduleFieldPlan.RollbackFailure("original cause", stage, 45, status,
                scheduleStillThere, Array.Empty<JObject>());
            Assert.False(failure.Success);
            Assert.StartsWith("original cause", failure.Error);
            Assert.Equal(clean, failure.Error.Contains("Nothing was changed."));
            Assert.Equal(!clean, failure.Error.Contains("The model may have changed"));
            Assert.Equal(!clean, PlanFailure.ModelMayHaveChanged(failure.Detail));
            Assert.Equal(status == "RolledBack", (bool)failure.Detail["rollback_confirmed"]);
            Assert.Equal(scheduleStillThere, (bool?)failure.Detail["schedule_still_in_model"]);
            Assert.Equal(45, (long)failure.Detail["schedule_id"]);
            Assert.Equal(stage, (string)failure.Detail["stage"]);
            Assert.Equal(status, (string)failure.Detail["transaction_status"]);
            Assert.Equal(clean ? ApplicationState.RolledBack : ApplicationState.Uncertain, ApplicationOutcome.Read(failure.Detail));
        }

        [Theory]
        [InlineData("dry_run", ScheduleProbeStage.RollbackFailed, true)]
        [InlineData("pre_apply_recheck", ScheduleProbeStage.LeftInModel, true)]
        [InlineData("pre_apply_recheck", ScheduleProbeStage.PresenceUnverified, true)]
        [InlineData("pre_apply_recheck", ScheduleProbeStage.CreatedRolledBack, false)]
        public void A_plan_stopped_before_the_group_preserves_what_the_child_probe_could_have_left(
            string phase, ScheduleProbeStage stage, bool uncertain)
        {
            CommandResult child = ScheduleFieldProbe.FailureResult(stage, "Doors", "field read failed");
            var ledger = new PlanLedger();
            JObject row = ledger.RecordRehearsal(0, "schedule", "horizun_create_schedule", child.Success, child.Data,
                child.Error, child.Detail);
            CommandResult result = PlanFailure.BeforeGroupResult(phase, new JArray(row), ledger.FailedAction, child.Error, child);
            Assert.False(result.Success);
            Assert.Equal(phase, (string)result.Detail["phase"]);
            Assert.False((bool)result.Detail["transaction_group_started"]);
            Assert.Equal(!uncertain, (bool)result.Detail["nothing_ran"]);
            Assert.Equal(uncertain, PlanFailure.ModelMayHaveChanged(result.Detail));
            Assert.Equal(!uncertain, result.Error.Contains("Nothing ran."));
            Assert.Contains(child.Error, result.Error);
            JToken nested = result.Detail["failed_action"]["child_detail"];
            if (child.Detail == null) Assert.Equal(JTokenType.Null, nested.Type);
            else Assert.True(JToken.DeepEquals(child.Detail, nested));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void The_correction_apply_gate_does_not_authorize_or_dispatch_an_incomplete_schedule_step(bool probeFailed)
        {
            var step = new CorrectionStep { Tool = "horizun_create_schedule" };
            var action = new CorrectionAction { State = CorrectionActionState.Rehearsed, Steps = { step } };
            var rehearsal = new JObject { ["dry_run"] = true };
            PlanWithTwoLeftOut().StampRehearsal(rehearsal);
            CommandResult child = probeFailed
                ? ScheduleFieldProbe.FailureResult(ScheduleProbeStage.RollbackFailed, "Doors", "field read failed")
                : CommandResult.Ok(rehearsal);
            Assert.False(CorrectionSelection.RecordRehearsal(action, step, child));

            CommandResult result = CorrectionApplyLoop.ConfirmAndApply("horizun_apply_corrections", new[] { action },
                () => throw new InvalidOperationException("The token must remain unspent."),
                () => throw new InvalidOperationException("The confirmed scope must not open."),
                _ => throw new InvalidOperationException("The typed write must not be dispatched."));

            Assert.False(result.Success);
            Assert.Equal(CorrectionActionState.RehearsalFailed, action.State);
            Assert.Equal(probeFailed, PlanFailure.ModelMayHaveChanged(result.Detail));
            Assert.Equal(!probeFailed, result.Error.Contains("Nothing was changed."));
            JToken nested = result.Detail["actions"][0]["steps"][0]["rehearsal"];
            if (probeFailed)
            {
                Assert.Contains("A child rehearsal may have changed the model", result.Error);
                Assert.Equal("rollback_unconfirmed", (string)nested["data"]["probe_outcome"]);
            }
            else
            {
                Assert.Contains("announced that its apply will be partial", result.Error);
                Assert.Equal("rehearsed", (string)nested["application_state"]);
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void A_clean_correction_runs_the_writer_only_after_confirmation(bool confirmed)
        {
            var step = new CorrectionStep { Tool = "horizun_create_schedule" };
            var action = new CorrectionAction { State = CorrectionActionState.Rehearsed, Steps = { step } };
            var rehearsal = new JObject { ["dry_run"] = true };
            ScheduleFieldPlan.Build(new[]
            {
                new ScheduleFieldResolution { Requested = "Mark", Used = "Mark", Identity = "Instance:1" }
            }).StampRehearsal(rehearsal);
            Assert.True(CorrectionSelection.RecordRehearsal(action, step, CommandResult.Ok(rehearsal)));

            var calls = new List<string>();
            CommandResult refusal = confirmed ? null : CommandResult.Fail("invalid token");
            CommandResult result = CorrectionApplyLoop.ConfirmAndApply("horizun_apply_corrections", new[] { action },
                () => { calls.Add("confirm"); return refusal; },
                () => { calls.Add("enter"); return null; },
                s =>
                {
                    calls.Add("write");
                    Assert.Same(step, s);
                    return new StepExecution { Success = true, State = ApplicationState.VerifiedApplied };
                });
            Assert.Same(refusal, result);
            Assert.Equal(confirmed ? new[] { "confirm", "enter", "write" } : new[] { "confirm" }, calls);
            Assert.Equal(confirmed ? CorrectionActionState.Applied : CorrectionActionState.Rehearsed, action.State);
        }

        [Fact]
        public void A_field_identity_uses_the_name_only_where_the_parameter_id_repeats()
        {
            // A valid id names one column within its field type; the name (a heading can change) is not part of it.
            Assert.Equal("Instance:123", ScheduleFieldPlan.Identity("Instance", 123, "Area"));
            Assert.Equal(ScheduleFieldPlan.Identity("Instance", 123, "Area"), ScheduleFieldPlan.Identity("Instance", 123, "Superficie"));
            // Material takeoff: the element's quantity and the material's differ by type.
            Assert.NotEqual(ScheduleFieldPlan.Identity("Instance", 123, "Area"), ScheduleFieldPlan.Identity("MaterialQuantity", 123, "Material: Area"));
            // The invalid id is shared by custom fields, combined parameters, formulas: the name tells them apart.
            Assert.NotEqual(ScheduleFieldPlan.Identity("CustomField", -1, "Cost A"), ScheduleFieldPlan.Identity("CustomField", -1, "Cost B"));
            Assert.Equal("CombinedParameter:-1:Code", ScheduleFieldPlan.Identity("CombinedParameter", -1, "Code"));
        }

        [Fact]
        public void A_probe_that_cannot_start_says_why_before_trying()
        {
            Assert.Null(ScheduleFieldProbe.Precondition(false, false, "Doors"));
            string ro = ScheduleFieldProbe.Precondition(true, false, "Doors");
            Assert.Contains("read-only", ro);
            Assert.DoesNotContain("rolled back", ro);
            string open = ScheduleFieldProbe.Precondition(false, true, "Doors");
            Assert.Contains("transaction is already open", open);
            Assert.DoesNotContain("rolled back", open);
            // Read-only is said first when both hold.
            Assert.Contains("read-only", ScheduleFieldProbe.Precondition(true, true, "Doors"));
        }

        [Theory]
        [InlineData(ScheduleProbeStage.NotStarted)]
        [InlineData(ScheduleProbeStage.StartedNothingCreated)]
        [InlineData(ScheduleProbeStage.CreatedRolledBack)]
        [InlineData(ScheduleProbeStage.RollbackFailed)]
        [InlineData(ScheduleProbeStage.LeftInModel)]
        [InlineData(ScheduleProbeStage.PresenceUnverified)]
        public void Each_probe_stage_says_nothing_changed_only_when_the_model_is_untouched(ScheduleProbeStage stage)
        {
            string m = ScheduleFieldProbe.Failure(stage, "Doors", "boom");
            Assert.StartsWith("The fields could not be resolved against what Revit offers for Doors: boom", m);
            bool untouched = ScheduleFieldProbe.ModelUntouched(stage);
            Assert.Equal(untouched, m.Contains("Nothing was changed"));
            Assert.Equal(!untouched, m.Contains("may have changed"));
            // "created and rolled back" only for the stage where a schedule was created and the rollback confirmed.
            Assert.Equal(stage == ScheduleProbeStage.CreatedRolledBack, m.Contains("a schedule was created and rolled back to ask"));
        }

        [Fact]
        public void The_stage_after_a_rollback_reads_what_revit_answered()
        {
            foreach (ScheduleProbeStage reached in new[] { ScheduleProbeStage.StartedNothingCreated, ScheduleProbeStage.CreatedRolledBack })
            {
                // Not confirmed (Guard.RollBack answers a status without throwing): the model may have changed.
                Assert.Equal(ScheduleProbeStage.RollbackFailed, ScheduleFieldProbe.AfterRollback(reached, false, false));
                Assert.Equal(ScheduleProbeStage.RollbackFailed, ScheduleFieldProbe.AfterRollback(reached, false, true));
                // Confirmed, yet the provisional schedule is still there.
                Assert.Equal(ScheduleProbeStage.LeftInModel, ScheduleFieldProbe.AfterRollback(reached, true, true));
                Assert.Equal(ScheduleProbeStage.PresenceUnverified, ScheduleFieldProbe.AfterRollback(reached, true, null));
                // Confirmed and gone: the stage reached stands.
                Assert.Equal(reached, ScheduleFieldProbe.AfterRollback(reached, true, false));
            }
            Assert.False(ScheduleFieldProbe.ModelUntouched(ScheduleFieldProbe.AfterRollback(ScheduleProbeStage.CreatedRolledBack, false, false)));
        }

        [Fact]
        public void Before_the_probe_the_document_then_the_token_and_never_on_a_dry_run()
        {
            int calls = 0;
            Func<string> refuse = () => { calls++; return "that confirmation was issued for 'horizun_quantities'"; };
            bool byToken;
            // Read-only and an open transaction refuse without asking about the token.
            Assert.Contains("read-only", ScheduleFieldProbe.BeforeProbe(true, false, "Doors", false, refuse, out byToken));
            Assert.False(byToken);
            Assert.Contains("already open", ScheduleFieldProbe.BeforeProbe(false, true, "Doors", false, refuse, out byToken));
            Assert.Equal(0, calls);
            // A dry run never asks.
            Assert.Null(ScheduleFieldProbe.BeforeProbe(false, false, "Doors", true, refuse, out byToken));
            Assert.Equal(0, calls);
            // An apply asks once, and its refusal says nothing was created.
            string m = ScheduleFieldProbe.BeforeProbe(false, false, "Doors", false, refuse, out byToken);
            Assert.True(byToken);
            Assert.Equal(1, calls);
            Assert.Contains("horizun_quantities", m);
            Assert.Contains("No schedule was created", m);
            Assert.Null(ScheduleFieldProbe.BeforeProbe(false, false, "Doors", false, () => null, out byToken));
            Assert.False(byToken);
        }

        [Fact]
        public void A_token_precheck_spends_nothing_and_refuses_another_command_or_document()
        {
            var store = new ConfirmationStore();
            Confirmation c = store.Issue("horizun_create_schedule", "doc-A", "plan-1");
            DateTime now = DateTime.UtcNow;
            Assert.Null(store.Precheck(c.Token, "horizun_create_schedule", "doc-A", now));
            Assert.Contains("for 'horizun_create_schedule', not for 'horizun_quantities'", store.Precheck(c.Token, "horizun_quantities", "doc-A", now));
            Assert.Contains("different document", store.Precheck(c.Token, "horizun_create_schedule", "doc-B", now));
            Assert.Contains("no such confirmation", store.Precheck("nope", "horizun_create_schedule", "doc-A", now));
            // Nothing above spent it: the real check still accepts it once.
            Assert.True(store.Validate(c.Token, "horizun_create_schedule", "doc-A", "plan-1").Ok);
            Assert.Contains("already used", store.Precheck(c.Token, "horizun_create_schedule", "doc-A", now));
        }

        [Fact]
        public void A_token_precheck_refuses_expiry_without_spending_it()
        {
            DateTime now = new DateTime(2026, 11, 18, 12, 0, 0, DateTimeKind.Utc);
            var store = new ConfirmationStore(() => now);
            Confirmation c = store.Issue("horizun_create_schedule", "doc-A", "plan-1");
            Assert.Null(store.Precheck(c.Token, c.Command, c.DocumentKey, now));
            now = c.ExpiresUtc.AddSeconds(1);
            Assert.Contains("expired", store.Precheck(c.Token, c.Command, c.DocumentKey, now));
            Assert.False(c.Used);
        }

        [Fact]
        public void A_rollback_failure_keeps_the_original_cause()
        {
            var original = new InvalidOperationException("the field is not valid");
            var rollback = new InvalidOperationException("rollback refused");
            Exception kept = ScheduleFieldProbe.KeepOriginal(original, rollback);
            Assert.StartsWith("the field is not valid", kept.Message);
            Assert.Contains("rollback refused", kept.Message);
            Assert.Same(original, kept.InnerException);
            Assert.Same(original, ScheduleFieldProbe.KeepOriginal(original, null));
            Assert.Same(rollback, ScheduleFieldProbe.KeepOriginal(null, rollback));
        }

        [Fact]
        public void Create_schedule_is_wired_to_the_pure_decisions()
        {
            // The decisions themselves are tested above as behaviour; this only pins that the command uses them.
            string src = Source("src", "Horizun.Revit", "Commands", "CreateScheduleCommand.cs");
            Assert.Contains("ScheduleFieldProbe.BeforeProbe(docReadOnly, docModifiable, categoryName, dryRun,", src);
            Assert.Contains("DocumentGate.Confirmations.Precheck(token, Name, gate.Fingerprint, DateTime.UtcNow)", src);
            Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(src, @"ScheduleFieldProbe\.AfterRollback\(").Count);
        }

        [Fact]
        public void Skipped_fields_row_is_a_supuesto_with_the_closest_names()
        {
            var sugg = new Dictionary<string, IReadOnlyList<string>> { ["Levl"] = new[] { "Level" } };
            JObject row = ScheduleFieldRescue.SkippedRow("Doors", new[] { "Levl", "Zed" }, sugg);
            Assert.Equal("SUPUESTO", (string)row["flag"]);
            Assert.Contains("Levl (closest: Level)", (string)row["why"]);
            Assert.Contains("Zed", (string)row["what"]);
        }

        

        

        

        

        

        

        

        // ---- families: oblique junction rules ---------------------------------------------

        private static List<JunctionRuleInfo> Rules(params (string name, string part)[] r)
        {
            var l = new List<JunctionRuleInfo>();
            for (int i = 0; i < r.Length; i++) l.Add(new JunctionRuleInfo(i, r[i].name, r[i].part));
            return l;
        }

        [Fact]
        public void Wye_candidates_are_ordered_part_type_first_then_named_wyes()
        {
            var c = JunctionRuleClassifier.Choose(Rules(
                ("Fabricante_SanitariaYee: Var.", "Tee"),   // wye only by its name
                ("Tee recta: A", "Tee"),
                ("Ramal: B", "LateralTee")));          // wye by part type
            Assert.Equal(2, c.WyeIndex);
            Assert.Equal(new[] { 2, 0 }, c.WyeCandidates.ToArray());
            Assert.Equal(1, c.TeeIndex);
        }

        [Fact]
        public void Switch_refusal_names_every_wye_tried_and_what_to_do()
        {
            string m = JunctionRuleClassifier.SwitchRefusal(new[]
            {
                new KeyValuePair<string, string>("Fabricante: Var.", "Can't make type"),
                new KeyValuePair<string, string>("HZ_Wye: Std", "Can't make type")
            });
            Assert.Contains("[Fabricante: Var.]", m);
            Assert.Contains("[HZ_Wye: Std]", m);
            Assert.Contains("connector layout", m);
            Assert.Contains("Nothing was built", m);
        }

        [Fact]
        public void A_wye_other_than_the_preferred_one_is_returned_as_a_supuesto_with_revits_words()
        {
            var refusals = new[] { new KeyValuePair<string, string>("Fabricante: Var.", "Can't make type") };
            JObject row = JunctionRuleClassifier.WyeSubstitution(3, "Fabricante: Var.", "HZ_Wye: Std", refusals);
            Assert.NotNull(row);
            Assert.Equal("SUPUESTO", (string)row["flag"]);
            Assert.Equal(3, (int)row["item"]);
            Assert.Equal("Fabricante: Var.", (string)row["preferred_wye"]);
            Assert.Equal("HZ_Wye: Std", (string)row["used_wye"]);
            Assert.Equal("Can't make type", (string)row["refused"][0]["revit_said"]);
            Assert.Contains("[Fabricante: Var.] Revit said \"Can't make type\"", (string)row["why"]);
            // The preferred wye used: nothing was assumed.
            Assert.Null(JunctionRuleClassifier.WyeSubstitution(3, "HZ_Wye: Std", "HZ_Wye: Std", new KeyValuePair<string, string>[0]));
        }

        

        private static List<string> Codes(JObject block) => ((JArray)block["findings"]).Select(f => (string)f["code"]).ToList();

        

        

        

        

        

        

        

        

        // ---- get_schedule_data: why schedule_id names no readable schedule ------------------

        [Fact]
        public void Only_an_absent_or_minus_one_schedule_id_is_not_sent()
        {
            Assert.True(ScheduleIdMessages.IsNotSent(null));
            Assert.True(ScheduleIdMessages.IsNotSent(-1));
            Assert.False(ScheduleIdMessages.IsNotSent(-5));
            Assert.False(ScheduleIdMessages.IsNotSent(0));
            Assert.Contains("was not sent", ScheduleIdMessages.Refusal(ScheduleIdHit.NotSent, null, null, null, null));
            Assert.Contains("-5 is not an element id", ScheduleIdMessages.Refusal(ScheduleIdHit.Invalid, -5, null, null, null));
        }

        [Fact]
        public void The_schedule_id_decision_runs_in_its_one_order()
        {
            int calls = 0;
            Func<long, ScheduleIdFacts> never = id => { calls++; return new ScheduleIdFacts(true, true, false); };
            // Not sent and negative are decided without asking the document.
            Assert.Equal(ScheduleIdHit.NotSent, ScheduleIdMessages.Classify(null, never));
            Assert.Equal(ScheduleIdHit.NotSent, ScheduleIdMessages.Classify(-1, never));
            Assert.Equal(ScheduleIdHit.Invalid, ScheduleIdMessages.Classify(-5, never));
            Assert.Equal(0, calls);
            // Then the document: nothing there, another element, a TEMPLATE (a ViewSchedule too), a readable schedule.
            Assert.Equal(ScheduleIdHit.NoElement, ScheduleIdMessages.Classify(4, id => new ScheduleIdFacts(false, false, false)));
            Assert.Equal(ScheduleIdHit.OtherElement, ScheduleIdMessages.Classify(4, id => new ScheduleIdFacts(true, false, false)));
            Assert.Equal(ScheduleIdHit.Template, ScheduleIdMessages.Classify(4, id => new ScheduleIdFacts(true, true, true)));
            Assert.Equal(ScheduleIdHit.Readable, ScheduleIdMessages.Classify(4, id => new ScheduleIdFacts(true, true, false)));
            // Zero is an id, asked of the document.
            long asked = -99;
            ScheduleIdMessages.Classify(0, id => { asked = id; return new ScheduleIdFacts(false, false, false); });
            Assert.Equal(0, asked);
            // A template flag on something that is not a schedule does not make it a template.
            Assert.Equal(ScheduleIdHit.OtherElement, ScheduleIdMessages.Classify(4, id => new ScheduleIdFacts(true, false, true)));
        }

        [Fact]
        public void Get_schedule_data_decides_through_the_classifier()
        {
            string src = Source("src", "Horizun.Revit", "Commands", "ScheduleReadCommands.cs");
            Assert.Contains("ScheduleIdMessages.Classify(sentId,", src);
            Assert.Contains("if (hit != ScheduleIdHit.Readable) return CommandResult.Fail(ScheduleIdRefusal(doc, hit, sentId, found));", src);
        }

        [Fact]
        public void A_schedule_template_is_named_as_a_template_with_no_rows()
        {
            var list = new List<KeyValuePair<long, string>> { new KeyValuePair<long, string>(7, "Puertas"), new KeyValuePair<long, string>(5, "Aparatos") };
            string m = ScheduleIdMessages.Refusal(ScheduleIdHit.Template, 99, "ViewSchedule", "Plantilla puertas", list);
            Assert.Contains("Id 99 is a schedule view TEMPLATE ('Plantilla puertas')", m);
            Assert.Contains("no rows", m);
            Assert.Contains("Schedules here: 5 'Aparatos'; 7 'Puertas'.", m);
            Assert.Contains("Nothing was read", m);
        }

        [Fact]
        public void Another_element_or_a_stale_id_is_said_and_the_schedules_are_listed()
        {
            Assert.Contains("Id 3 is a Wall ('Muro 1'), not a native ViewSchedule",
                ScheduleIdMessages.Refusal(ScheduleIdHit.OtherElement, 3, "Wall", "Muro 1", new List<KeyValuePair<long, string>>()));
            Assert.Contains("No element has id 4", ScheduleIdMessages.Refusal(ScheduleIdHit.NoElement, 4, null, null, null));
            Assert.Contains("This document has no schedules", ScheduleIdMessages.Refusal(ScheduleIdHit.NoElement, 4, null, null, new List<KeyValuePair<long, string>>()));
            var many = Enumerable.Range(1, 20).Select(i => new KeyValuePair<long, string>(i, "T" + i.ToString("00"))).ToList();
            Assert.Contains("(20 in all, horizun_list_schedules lists them)", ScheduleIdMessages.Refusal(ScheduleIdHit.NoElement, 4, null, null, many));
        }

        

        

        

        

        

        // ---- IFC delivery names ------------------------------------------------------------

        private static JObject Container(string revision, string fileName = null)
        {
            var c = JObject.Parse(
                "{ \"fields\": { \"project\": \"HZ01\", \"originator\": \"HRZ\", \"volume\": \"ZZ\", \"level\": \"XX\", " +
                "\"type\": \"M3\", \"role\": \"A\", \"number\": \"0001\" }, " +
                "\"field_order\": [\"project\",\"originator\",\"volume\",\"level\",\"type\",\"role\",\"number\"], " +
                "\"status\": \"S2\" }");
            c["revision"] = revision;
            if (fileName != null) c["file_name"] = fileName;
            return c;
        }

        

        

        

        

        

        

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern int GetACP();

        /// <summary>
        /// The oracle, independent of the code under test: the SYSTEM's ANSI page as Windows reports it (GetACP), not
        /// the thread culture; a UTF-8 system (65001) falls back to 1252, as the reader does.
        /// </summary>
        private static System.Text.Encoding SystemAnsi()
        {
            System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
            int page = OperatingSystem.IsWindows() ? GetACP() : 1252;
            return System.Text.Encoding.GetEncoding(page == 65001 ? 1252 : page);
        }

        

        

        

        

        

        private static string Source(params string[] parts)
        {
            var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !System.IO.Directory.Exists(System.IO.Path.Combine(dir.FullName, "src", "Horizun.Revit")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            return System.IO.File.ReadAllText(System.IO.Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray()));
        }
    }
}
