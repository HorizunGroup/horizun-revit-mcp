// -----------------------------------------------------------------------------
// Horizun Core tests - 4.2-N5 Python ergonomics, the Revit-free halves.
// -----------------------------------------------------------------------------
using System;
using System.IO;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public sealed class N5PythonErgonomicsTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "horizun-n5-" + Guid.NewGuid().ToString("N"));
        private DurableCommandLedger Ledger(int pid = 10) =>
            new DurableCommandLedger(() => _dir, () => new DateTime(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc), () => pid);
        public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

        [Fact]
        public void A_call_refused_before_it_ran_gives_its_key_back()
        {
            DurableCommandDecision claim = Ledger().Claim("k1", "horizun_execute_python", "fp");
            CommandResult refused = CommandResult.Fail("Script is too long. Nothing ran.").AsRejectedBeforeRun();

            Assert.True(Ledger().Release(claim, refused));

            // The same key, the same operation: fresh again, and it can run and complete normally.
            DurableCommandDecision again = Ledger().Claim("k1", "horizun_execute_python", "fp");
            Assert.Equal(DurableCommandOutcome.Fresh, again.Outcome);
            Ledger().Complete(again, CommandResult.Ok(new JObject { ["ok"] = 1 }));
            Assert.Equal(DurableCommandOutcome.Replay, Ledger().Claim("k1", "horizun_execute_python", "fp").Outcome);
        }

        [Fact]
        public void A_released_key_may_carry_a_corrected_call()
        {
            DurableCommandDecision claim = Ledger().Claim("k2", "horizun_execute_python", "fp-wrong");
            Ledger().Release(claim, CommandResult.Fail("syntax").AsRejectedBeforeRun());
            // Before the fix this was a conflict ("already identifies a DIFFERENT operation").
            Assert.Equal(DurableCommandOutcome.Fresh, Ledger().Claim("k2", "horizun_execute_python", "fp-fixed").Outcome);
        }

        [Fact]
        public void A_failure_after_the_run_began_keeps_its_key()
        {
            DurableCommandDecision claim = Ledger().Claim("k3", "horizun_execute_python", "fp");
            CommandResult failed = CommandResult.Fail("script raised");   // not marked: it ran
            Assert.False(Ledger().Release(claim, failed));
            Assert.Equal(DurableCommandOutcome.InDoubt, Ledger().Claim("k3", "horizun_execute_python", "fp").Outcome);
        }

        [Fact]
        public void Only_a_failure_can_be_marked_and_a_completed_key_is_never_released()
        {
            Assert.False(CommandResult.Ok(new JObject()).AsRejectedBeforeRun().RejectedBeforeRun);
            DurableCommandDecision claim = Ledger().Claim("k4", "horizun_execute_python", "fp");
            CommandResult refused = CommandResult.Fail("x").AsRejectedBeforeRun();
            Ledger().Complete(claim, refused);
            Assert.False(Ledger().Release(claim, refused));
            // A completed key stays completed: it replays, it is never handed out again.
            Assert.Equal(DurableCommandOutcome.Replay, Ledger().Claim("k4", "horizun_execute_python", "fp").Outcome);
        }

        [Fact]
        public void The_refusal_tells_the_caller_the_key_was_not_consumed()
        {
            CommandResult refused = CommandResult.Fail("nope").AsRejectedBeforeRun();
            refused.NoteKeyReleased("abc");
            Assert.Equal("released_not_consumed", (string)refused.Detail["idempotency"]["status"]);
        }

        [Theory]
        [InlineData("horizun_execute_python", false, false, false, true)]
        [InlineData("horizun_execute_python", true, false, false, false)]
        [InlineData("horizun_execute_python", false, true, false, false)]
        [InlineData("horizun_execute_python", false, false, true, false)]
        [InlineData("horizun_create_elements", false, false, false, false)]
        public void Only_a_real_python_run_is_journalled(string tool, bool preflight, bool readOnly, bool rehearsal, bool expected)
        {
            var request = new JObject { ["preflight"] = preflight, ["read_only"] = readOnly, ["rehearsal"] = rehearsal };
            Assert.Equal(expected, PythonUndoRules.Applies(tool, request));
        }

        [Fact]
        public void A_read_only_rehearsal_or_preflight_run_is_never_recorded_as_an_unrecorded_write()
        {
            // Field case: a read_only script answered "undoable: false" and blocked undo_last although it rolled back.
            foreach (string flag in new[] { "read_only", "rehearsal", "preflight" })
                Assert.True(PythonUndoRules.RolledBackByDesign(PythonUndoRules.Tool, new JObject { [flag] = true }));
            Assert.False(PythonUndoRules.RolledBackByDesign(PythonUndoRules.Tool, new JObject()));
            Assert.False(PythonUndoRules.RolledBackByDesign("horizun_create_elements", new JObject { ["read_only"] = true }));
            var root = new DirectoryInfo(AppContext.BaseDirectory); while (root != null && !File.Exists(Path.Combine(root.FullName, "AGENTS.md"))) root = root.Parent;
            string src = File.ReadAllText(Path.Combine(root.FullName, "src", "Horizun.Revit", "Commands", "UnrecordedWriteUndo.cs"));
            Assert.Contains("PythonUndoRules.RolledBackByDesign(tool, request)", src);
        }

        [Fact]
        public void A_script_that_only_created_elements_is_undoable()
        {
            PythonUndoPlan plan = PythonUndoRules.Decide(new long[] { 9, 3, 3, 5 }, 0, 0, 1);
            Assert.True(plan.Undoable);
            Assert.Equal(new long[] { 3, 5, 9 }, plan.Created.ToArray());
            Assert.Null(plan.Reason);
        }

        [Fact]
        public void A_script_that_also_modified_or_deleted_is_journalled_but_not_undoable()
        {
            PythonUndoPlan plan = PythonUndoRules.Decide(new long[] { 1 }, 4, 2, 1);
            Assert.False(plan.Undoable);
            Assert.Contains("modified 4", plan.Reason);
            Assert.Contains("deleted 2", plan.Reason);
            Assert.False(PythonUndoRules.Decide(new long[0], 1, 0, 1).Undoable);
        }

        [Fact]
        public void Nothing_changed_means_nothing_to_journal_and_big_or_multi_document_runs_are_refused()
        {
            Assert.Null(PythonUndoRules.Decide(new long[0], 0, 0, 0));
            Assert.False(PythonUndoRules.Decide(new long[] { 1 }, 0, 0, 2).Undoable);
            Assert.False(PythonUndoRules.Decide(Enumerable.Range(1, PythonUndoRules.MaxCreated + 1).Select(i => (long)i), 0, 0, 1).Undoable);
        }

        [Fact]
        public void Undo_refuses_when_change_observation_is_incomplete()
        {
            var plan = PythonUndoRules.Decide(new long[] { 42 }, 0, 0, 1, false);
            Assert.False(plan.Undoable);
            Assert.Contains("observation", plan.Reason);
        }

        [Fact]
        public void Compact_health_drops_the_heavy_blocks_names_them_and_keeps_identity()
        {
            JObject Full() => new JObject
            {
                ["status"] = "healthy", ["horizun_version"] = "4.2", ["addin_assembly"] = new JObject(),
                ["active_document"] = new JObject(), ["open_documents"] = new JArray(),
                ["data_paths"] = new JObject(), ["registry"] = new JObject(), ["jobs"] = new JObject(),
                ["timings"] = new JObject(), ["clients"] = new JArray(), ["horizun_hub"] = new JObject()
            };
            JObject compact = HealthDetailRules.Apply(Full(), true);
            foreach (string k in new[] { "data_paths", "registry", "jobs", "timings", "clients", "horizun_hub" })
                Assert.Null(compact[k]);
            foreach (string k in new[] { "status", "horizun_version", "addin_assembly", "active_document", "open_documents" })
                Assert.NotNull(compact[k]);
            Assert.Equal("compact", (string)compact["detail"]);
            Assert.Contains("registry", compact["omitted_in_compact"].Select(t => (string)t));

            JObject full = HealthDetailRules.Apply(Full(), false);
            Assert.NotNull(full["registry"]);
            Assert.Equal("full", (string)full["detail"]);
            Assert.Null(full["omitted_in_compact"]);
        }
    }
}
