using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    /// <summary>
    /// Review of int/4.2, findings M1-M3, M5, M6 and the small ones that live in Revit-free rules (m2-m6, m9, m11).
    /// The Revit halves are wired in Dispatcher/ExecutePythonCommand/PipeServer; what can be decided at a desk is decided here.
    /// </summary>
    public class IntegrationReviewCancellationTests
    {




        // ------------------------------------------------------------------ M2 / M3

        [Fact]
        public void The_wire_cancel_of_a_running_synchronous_script_reaches_its_stop_switch()
        {
            var gate = new RequestGate();
            var request = gate.Begin("w-py", "horizun_execute_python", "{}", false, out _);
            Assert.Same(request, gate.Take());
            var control = new PythonRunControl(null);
            PythonRunRegistry.Register(PythonRunRegistry.WireKey("w-py"), control);

            Assert.False(gate.CancelQueued("w-py", out string state));
            Assert.Equal("cancel_requested", state);
            Assert.Equal(PythonRunControl.Cancelled, control.Poll());   // checkpoint() would now raise HorizunInterrupted
            gate.Complete(request);
        }

        [Fact]
        public void A_wire_cancel_that_arrives_before_the_script_registers_is_applied_when_it_does()
        {
            var gate = new RequestGate();
            var request = gate.Begin("w-early-py", "horizun_execute_python", "{}", false, out _);
            Assert.Same(request, gate.Take());
            Assert.False(gate.CancelQueued("w-early-py", out string state));   // between Take and Register
            Assert.Equal("cancel_requested", state);

            var control = new PythonRunControl(null);
            PythonRunRegistry.Register(PythonRunRegistry.WireKey("w-early-py"), control);
            Assert.Equal(PythonRunControl.Cancelled, control.Poll());
            gate.Complete(request);
        }

        [Fact]
        public void A_finished_request_leaves_nothing_behind_for_a_late_cancel()
        {
            var gate = new RequestGate();
            var request = gate.Begin("w-done", "horizun_execute_python", "{}", false, out _);
            gate.Take();
            gate.CancelQueued("w-done", out _);
            gate.Complete(request);
            Assert.Equal("not_running", PythonRunRegistry.Cancel(PythonRunRegistry.WireKey("w-done"), "late"));
        }

        [Fact]
        public void An_async_job_cancelled_between_take_and_register_is_not_lost()
        {
            PythonRunRegistry.Claim("job-m3");
            Assert.Equal("interrupt_requested", PythonRunRegistry.Cancel("job-m3", "cancel before the script registered"));
            Assert.Equal("already_interrupted", PythonRunRegistry.Cancel("job-m3", "again"));
            var control = new PythonRunControl(null);
            PythonRunRegistry.Register("job-m3", control);
            Assert.Equal(PythonRunControl.Cancelled, control.Poll());
            Assert.Contains("before the script registered", control.Reason);
            PythonRunRegistry.Unregister("job-m3");
            Assert.Equal("not_running", PythonRunRegistry.Cancel("job-m3", "after it finished"));
        }

        [Fact]
        public void A_cancel_for_a_job_nobody_claimed_is_not_kept()
        {
            Assert.Equal("not_running", PythonRunRegistry.Cancel("job-never-claimed", "x"));
            var control = new PythonRunControl(null);
            PythonRunRegistry.Register("job-never-claimed", control);
            Assert.Null(control.Poll());
            PythonRunRegistry.Unregister("job-never-claimed");
        }

        [Fact]
        public void A_running_registered_script_is_asked_once_and_then_reported_already_interrupted()
        {
            var control = new PythonRunControl(null);
            PythonRunRegistry.Register("job-run", control);
            Assert.Equal("interrupt_requested", PythonRunRegistry.Cancel("job-run", "first"));
            Assert.Equal("already_interrupted", PythonRunRegistry.Cancel("job-run", "second"));
            PythonRunRegistry.Unregister("job-run");
        }

        // ------------------------------------------------------------------ M5

        [Fact]
        public void The_callers_timeout_does_not_cancel_a_running_cooperative_job()
        {
            var gate = new RequestGate();
            var request = gate.Begin("w-timeout", "horizun_clash", "{\"cooperative\":true}", true, out _);
            Assert.Same(request, gate.Take());
            Assert.False(gate.CancelQueued("w-timeout", out string state, callerTimedOut: true));
            Assert.Equal(RequestGate.JobContinues, state);
            Assert.False(request.CancellationPending);   // nothing was asked of the job

            Assert.False(gate.CancelQueued("w-timeout", out string cancelState));   // an actual cancel still does
            Assert.Equal("cancel_requested", cancelState);
            Assert.True(request.CancellationPending);
            gate.Complete(request);
        }

        [Fact]
        public void The_callers_timeout_leaves_a_detached_cooperative_job_running_but_a_cancel_stops_it()
        {
            var gate = new RequestGate();
            var request = gate.Begin("w-detached", "horizun_clash", "{\"cooperative\":true}", true, out _);
            gate.Take();
            int cancelled = 0;
            Assert.True(gate.Detach(request, () => cancelled++));

            Assert.False(gate.CancelQueued("w-detached", out string state, callerTimedOut: true));
            Assert.Equal(RequestGate.JobContinues, state);
            Assert.Equal(0, cancelled);

            Assert.False(gate.CancelQueued("w-detached", out string cancelState));
            Assert.Equal("cancel_requested", cancelState);
            Assert.Equal(1, cancelled);
            gate.Complete(request);
        }

        [Fact]
        public void The_callers_timeout_still_removes_work_that_never_started()
        {
            var gate = new RequestGate();
            var running = gate.Begin("w-run", "horizun_health", "{}", out _);
            gate.Take();
            var waiting = gate.Begin("w-wait", "horizun_clash", "{\"cooperative\":true}", true, out _);
            Assert.True(gate.CancelQueued("w-wait", out string state, callerTimedOut: true));
            Assert.Equal("cancelled_before_start", state);
            Assert.True(waiting.CancelledBeforeStart);
            gate.Complete(running);
        }

        [Fact]
        public void The_callers_timeout_does_not_stop_a_running_script()
        {
            var gate = new RequestGate();
            var request = gate.Begin("w-py-timeout", "horizun_execute_python", "{}", false, out _);
            gate.Take();
            var control = new PythonRunControl(null);
            PythonRunRegistry.Register(PythonRunRegistry.WireKey("w-py-timeout"), control);
            Assert.False(gate.CancelQueued("w-py-timeout", out string state, callerTimedOut: true));
            Assert.Equal("already_running", state);
            Assert.Null(control.Poll());
            gate.Complete(request);
        }

        // ------------------------------------------------------------------ M6

        [Fact]
        public void The_not_undone_text_says_the_opposite_for_a_group_that_is_rolled_back_as_a_whole()
        {
            string normal = PythonRunControl.NotUndoneText("normal", null);
            Assert.Contains("stays committed", normal);

            string ro = PythonRunControl.NotUndoneText("read_only", true);
            Assert.DoesNotContain("stays committed", ro);
            Assert.DoesNotContain("Nothing is kept", ro);
            Assert.Contains("RollbackProven", ro);
            // M-3: the text promises only what is measured - model changes inside transactions - and names what is not.
            foreach (string outside in new[] { "checkouts", "files written", "other documents", "link reloads", "active view", "selection" })
                Assert.Contains(outside, ro);
            Assert.Contains("neither measured nor reverted", ro);

            string doc = PythonRunControl.NotUndoneText("documentation_only", true);
            Assert.DoesNotContain("stays committed", doc);
            Assert.Contains("WHOLE documentation_only group", doc);

            Assert.Contains("NOT confirmed", PythonRunControl.NotUndoneText("documentation_only", false));
            Assert.Contains("NOT confirmed", PythonRunControl.NotUndoneText("read_only", null));
        }

        [Fact]
        public void The_interruption_block_carries_the_scope_and_its_own_not_undone_sentence()
        {
            var control = new PythonRunControl(null);
            control.Cancel("test");
            JObject plain = control.Describe("cp", "none_open_at_stop", 0);
            Assert.Equal("normal", (string)plain["scope"]);
            Assert.Contains("stays committed", (string)plain["not_undone"]);
            JObject doc = control.Describe("cp", "rolled_back", 1, "documentation_only", true);
            Assert.Equal("documentation_only", (string)doc["scope"]);
            Assert.DoesNotContain("stays committed", (string)doc["not_undone"]);
            Assert.DoesNotContain("Nothing is kept", (string)doc["not_undone"]);
            var cats = ((JArray)doc["not_reverted_categories"]).Select(t => (string)t).ToArray();
            Assert.Equal(new[] { "explicit_checkouts", "files_written_or_exported", "other_documents_opened_or_edited",
                                 "link_reloads", "active_view", "selection" }, cats);
        }

        [Theory]
        [InlineData(false, false, true, DocumentationOnlyRules.GroupFate.Assimilate)]
        [InlineData(false, false, false, DocumentationOnlyRules.GroupFate.RollBackJudged)]
        [InlineData(false, true, true, DocumentationOnlyRules.GroupFate.RollBackInterrupted)]     // a clean diff does not save an interrupted run
        [InlineData(false, true, false, DocumentationOnlyRules.GroupFate.RollBackInterrupted)]
        [InlineData(true, false, true, DocumentationOnlyRules.GroupFate.RollBackAfterOpenTransaction)]   // m8: never left open
        [InlineData(true, true, true, DocumentationOnlyRules.GroupFate.RollBackAfterOpenTransaction)]
        public void An_interrupted_documentation_only_run_rolls_back_the_whole_group(bool leftModifiable, bool interrupted, bool verdictOk, DocumentationOnlyRules.GroupFate expected)
            => Assert.Equal(expected, DocumentationOnlyRules.Decide(leftModifiable, interrupted, verdictOk));

        [Fact]
        public void The_interruption_notes_state_the_rollback_or_its_absence()
        {
            Assert.Contains("WHOLE group was rolled back", DocumentationOnlyRules.InterruptedNote(true));
            Assert.Contains("COULD NOT BE CONFIRMED", DocumentationOnlyRules.InterruptedNote(false));
            Assert.Contains("group was rolled back", DocumentationOnlyRules.OpenTransactionNote(true));
            Assert.Contains("could NOT be closed", DocumentationOnlyRules.OpenTransactionNote(false));
        }

        [Fact]
        public void The_documentation_only_report_names_the_new_outcomes()
        {
            var verdict = new DocumentationOnlyVerdict { Ok = true };
            JObject report = DocumentationOnlyRules.Report(verdict, DocumentationOnlyRules.OutcomeRolledBackInterrupted, true, true, 0);
            Assert.Equal("rolled_back_interrupted", (string)report["outcome"]);
            Assert.True((bool)report["rollback_verified"]);
        }

        // ------------------------------------------------------------------ m2

        [Fact]
        public void The_new_planimetry_inventories_refuse_arguments_they_do_not_read()
        {
            Assert.Null(PlanimetryInventoryArgs.Refusal(JObject.Parse("{\"mode\":\"iso_inventory\",\"sheet_ids\":[1],\"view_ids\":[2],\"units\":\"mm\",\"max_rows\":10,\"cursor\":\"x\"}"), "iso_inventory"));
            string refused = PlanimetryInventoryArgs.Refusal(JObject.Parse("{\"mode\":\"rasters\",\"note_ids\":[1],\"cooperative\":true,\"element_ids\":[3]}"), "rasters");
            Assert.NotNull(refused);
            Assert.Contains("cooperative", refused);
            Assert.Contains("element_ids", refused);
            Assert.Contains("note_ids", refused);
            Assert.Contains("Nothing was read", refused);
        }

        // ------------------------------------------------------------------ m3

        [Theory]
        [InlineData("datum_cleanup")]
        [InlineData("set_scale")]
        [InlineData("marker_scale")]
        [InlineData("copy_link_overrides")]
        [InlineData("read")]
        public void family_names_and_type_names_are_refused_where_they_would_be_ignored(string operation)
        {
            string json = "{\"operation\":\"" + operation + "\",\"view_id\":5,\"view_ids\":[6],\"scale\":50,\"coarser_than\":100,\"family_names\":[\"Basic Wall\"]}";
            string why = ViewVisibilityRules.Validate(JObject.Parse(json));
            Assert.NotNull(why);
            Assert.Contains("family_names / type_names do not apply to " + operation, why);
        }

        [Fact]
        public void family_names_still_select_for_hide_and_explain()
        {
            Assert.Null(ViewVisibilityRules.Validate(JObject.Parse("{\"operation\":\"hide\",\"view_id\":5,\"family_names\":[\"Basic Wall\"]}")));
            Assert.Null(ViewVisibilityRules.Validate(JObject.Parse("{\"operation\":\"explain_overrides\",\"view_id\":5,\"type_names\":[\"Generic - 200mm\"]}")));
        }

        // ------------------------------------------------------------------ m4

        [Theory]
        [InlineData("pdf", false, true)]
        [InlineData("ifc", false, true)]
        [InlineData("dwg", false, true)]
        [InlineData("dwg", true, false)]
        [InlineData("pdf_qa", false, false)]
        [InlineData("cobie", false, false)]
        [InlineData("rfa", false, false)]
        public void dialog_policy_applies_only_to_single_file_exports(string format, bool dwgSet, bool applies)
            => Assert.Equal(applies, ExportDialogPolicy.AppliesTo(format, dwgSet));

        // ------------------------------------------------------------------ m5

        [Fact]
        public void A_synchronous_cooperative_job_without_a_durable_record_is_not_reported_as_a_lost_record()
        {
            var sync = new AsyncWork { JobId = "sync-1", SyncRequest = new RequestGate().Begin("w", "horizun_clash", "{}", true, out _) };
            Assert.Null(sync.RecordPersisted());
            var async = new AsyncWork { JobId = "async-1" };
            Assert.False(async.RecordPersisted().Value);
        }

        // ------------------------------------------------------------------ m6

        [Theory]
        [InlineData(false, false, true, false, CooperativeInterruption.Terminal.Publish)]
        [InlineData(false, true, true, false, CooperativeInterruption.Terminal.PublishDespiteLateCancel)]   // late cancel, complete result
        [InlineData(false, true, false, true, CooperativeInterruption.Terminal.Cancelled)]                  // cancel mid-run
        [InlineData(false, true, false, false, CooperativeInterruption.Terminal.Cancelled)]
        [InlineData(true, true, true, false, CooperativeInterruption.Terminal.Failed)]                      // a failure still wins
        [InlineData(false, false, false, false, CooperativeInterruption.Terminal.NoResult)]
        public void A_late_cancel_does_not_discard_a_complete_result(bool failed, bool cancelled, bool hasResult, bool more, CooperativeInterruption.Terminal expected)
            => Assert.Equal(expected, CooperativeInterruption.Decide(failed, cancelled, hasResult, more));

        // ------------------------------------------------------------------ m9

        [Fact]
        public void A_view_on_several_sheets_is_ambiguous_unless_every_viewport_agrees()
        {
            Assert.Equal("None", ViewportRotationChoice.Resolve(new[] { "None", "None" }, "7", out string e1));
            Assert.Null(e1);
            Assert.Null(ViewportRotationChoice.Resolve(new[] { "None", "Clockwise" }, "7", out string e2));
            Assert.Contains("ambiguous", e2);
            Assert.Null(ViewportRotationChoice.Resolve(new string[0], "7", out string e3));
            Assert.Contains("placed on a sheet", e3);
        }

        // ------------------------------------------------------------------ source wiring (the Revit halves cannot run here)

        private static string Source(params string[] parts)
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !Directory.Exists(Path.Combine(d.FullName, "src"))) d = d.Parent;
            Assert.NotNull(d);
            return File.ReadAllText(Path.Combine(new[] { d.FullName }.Concat(parts).ToArray()));
        }

        [Fact]
        public void The_pipe_server_separates_a_timeout_from_a_cancel_and_publishes_already_interrupted_apart()
        {
            string server = Source("src", "Horizun.Revit", "Transport", "PipeServer.cs");
            Assert.Contains("[\"intent\"]", Source("src", "Horizun.Server", "PipeClient.cs"));
            Assert.Contains("\"timeout\"", server);
            Assert.Contains("_dispatcher.CancelQueued(target, out detail, timedOut)", server);
            Assert.Contains("[\"interrupt_requested\"] = interrupt == \"interrupt_requested\",", server);
            Assert.Contains("[\"already_interrupted\"] = interrupt == \"already_interrupted\"", server);
            Assert.Contains("RecordPersisted()", server);
            Assert.Contains("Dispatcher.ScheduleJobRetention()", server);   // m7
        }

        [Fact]
        public void The_client_sends_the_timeout_intent_when_its_wait_expires_and_the_cancel_intent_when_the_client_cancels()
        {
            string client = Source("src", "Horizun.Server", "PipeClient.cs");
            Assert.Contains("TryCancelQueued(d, wireId, CancelIntent.Timeout)", client);
            Assert.Contains("TryCancelQueued(d, wireId, CancelIntent.Cancel)", client);
            Assert.Contains("job_continues", client);
        }

        [Fact]
        public void execute_python_registers_the_wire_key_and_the_documentation_only_group_is_never_left_open()
        {
            string py = Source("src", "Horizun.Revit", "Commands", "ExecutePythonCommand.cs");
            Assert.Contains("PythonRunRegistry.WireKey(RequestGate.CurrentWireId)", py);
            Assert.Contains("DocumentationOnlyRules.Decide(leftModifiable, interrupted, verdict.Ok)", py);
            Assert.Contains("RehearsalRules.CancellationReport(observedRollback", py);
            Assert.Contains("raised[\"cancelled_at_checkpoint\"][\"rolled_back_verified\"]", py);
        }
    }
}
