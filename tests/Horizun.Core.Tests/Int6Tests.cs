// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// INT-6: the findings of the independent review of int/4.2 @ e7af5680 (M-1..M-4, m-1..m-12) that a Revit-free test can hold.
// Each test was written and run against e7af5680 first (the red logs); what could not compile there is API that did not exist.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Horizun.Revit.Core;
using Horizun.Revit.Transport;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    // ---- M-1: sync_sheet_references rewrites by position, in one pass, whole words ------------------------------------

    public class SheetReferenceRewriteTests
    {
        private static readonly Regex Default = new Regex(
            @"(?i)\b(?:PLANCHA|L[ÃA]MINA|HOJA|SHEET)\s+([A-Z]{1,4}[-.]?\d{1,4}(?:[.-]\d{1,3})?)", RegexOptions.None);

        private static Dictionary<string, string> Map(params string[] pairs)
        {
            var m = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < pairs.Length; i += 2) m[pairs[i]] = pairs[i + 1];
            return m;
        }

        [Fact]
        public void A_shift_A1_to_A2_and_A2_to_A3_reads_A2_and_A3_not_A3_twice()
        {
            const string plain = "Ver Hoja A1 y Hoja A2";
            var map = Map("A1", "A2", "A2", "A3");
            var edits = NoteLeaderRules.PlanSheetReferenceEdits(plain, Default, map);
            Assert.Equal(2, edits.Count);
            string expected = NoteLeaderRules.ExpectedAfterSheetReferences(plain, Default, map);
            Assert.Equal("Ver Hoja A2 y Hoja A3", expected);
            Assert.Equal(expected, NoteLeaderRules.ApplySheetReferenceEdits(plain, edits));
            // The code of e7af5680 (copied literally in SheetReferencePrefixTests) gave "A3 y A3": a re-read compared against ITS OWN expected text could not tell.
            Assert.Equal("Ver Hoja A3 y Hoja A3", SheetReferencePrefixTests.E7af5680Rewrite(plain, Default, map));
            Assert.NotEqual(expected, SheetReferencePrefixTests.E7af5680Rewrite(plain, Default, map));
        }

        [Fact]
        public void A1_never_reaches_inside_A10()
        {
            const string plain = "Hoja A1, Hoja A10 y Hoja A1.";
            var map = Map("A1", "B1");
            var edits = NoteLeaderRules.PlanSheetReferenceEdits(plain, Default, map);
            Assert.Equal(2, edits.Count);
            Assert.Equal("Hoja B1, Hoja A10 y Hoja B1.", NoteLeaderRules.ApplySheetReferenceEdits(plain, edits));
            Assert.Equal("Hoja B1, Hoja A10 y Hoja B1.", NoteLeaderRules.ExpectedAfterSheetReferences(plain, Default, map));
        }

        [Fact]
        public void The_rehearsal_row_of_A10_changes_only_A10()
        {
            const string plain = "Hoja A1 y Hoja A10";
            var map = Map("A10", "A11");
            var edits = NoteLeaderRules.PlanSheetReferenceEdits(plain, Default, map);
            Assert.Single(edits);
            Assert.Equal("A10", edits[0].Number);
            Assert.Equal("A11", edits[0].NewNumber);
            Assert.Equal("Hoja A1 y Hoja A11", NoteLeaderRules.ApplySheetReferenceEdits(plain, edits));
            Assert.Equal("Hoja A1 y Hoja A11", NoteLeaderRules.ExpectedAfterSheetReferences(plain, Default, map));
        }

        [Fact]
        public void A_pattern_without_word_limits_still_replaces_whole_numbers_only()
        {
            var loose = new Regex(@"(A1)");
            var map = Map("A1", "B1");
            Assert.Empty(NoteLeaderRules.PlanSheetReferenceEdits("plano A10", loose, map));
            Assert.Equal("plano A10", NoteLeaderRules.ExpectedAfterSheetReferences("plano A10", loose, map));
            Assert.Single(NoteLeaderRules.PlanSheetReferenceEdits("plano A1 fin", loose, map));
        }

        [Fact]
        public void Edits_apply_from_the_last_so_a_longer_replacement_moves_nothing_before_it()
        {
            const string plain = "Hoja A1 / Hoja A2 / Hoja A3";
            var map = Map("A1", "A100", "A3", "A");
            var edits = NoteLeaderRules.PlanSheetReferenceEdits(plain, Default, map);
            Assert.Equal("Hoja A100 / Hoja A2 / Hoja A", NoteLeaderRules.ApplySheetReferenceEdits(plain, edits));
        }

        [Fact]
        public void The_expected_text_does_not_come_from_the_edits_so_a_wrong_edit_list_is_caught()
        {
            const string plain = "Hoja A1 y Hoja A2";
            var map = Map("A1", "A2", "A2", "A3");
            string expected = NoteLeaderRules.ExpectedAfterSheetReferences(plain, Default, map);
            var wrong = NoteLeaderRules.PlanSheetReferenceEdits(plain, Default, map);
            wrong[0].NewNumber = "A9";   // an edit list that disagrees with the map
            Assert.NotEqual(expected, NoteLeaderRules.ApplySheetReferenceEdits(plain, wrong));
        }
    }

    // ---- M-2, M-3, m-5: MZ-13 --------------------------------------------------------------------------------------

    public class Mz13Int6Tests
    {
        

        private static IList<KeyValuePair<string, JObject>> NoSaved() => new List<KeyValuePair<string, JObject>>();

        [Fact]
        public void The_exporter_keeps_setups_under_three_schemas_and_this_tool_names_the_other_two()
        {
            var all = new[] { IfcSetupRules.SchemaGuid }.Concat(IfcSetupRules.OtherSetupSchemaGuids).Select(g => g.ToUpperInvariant()).ToArray();
            Assert.Equal(3, all.Distinct().Count());
            foreach (string g in all) Assert.True(Guid.TryParse(g, out _), g);
        }

        

        

        

        

        

        

        

        

        

        private static string RepoRoot()
        {
            string dir = AppContext.BaseDirectory;
            while (dir != null && !File.Exists(Path.Combine(dir, "AGENTS.md"))) dir = Path.GetDirectoryName(dir);
            return dir ?? throw new InvalidOperationException("repository root not found");
        }
    }

    // ---- m-3 and m-1 -----------------------------------------------------------------------------------------------

    public class SyncReminderInt6Tests
    {
        [Fact]
        public void Under_safe_write_with_the_sync_only_option_the_reminder_names_no_tool_the_profile_hides()
        {
            var caps = new ReminderCapabilities { Profile = "safe_write", SyncOnly = true, SyncAvailable = true, VerifyAvailable = false };
            string text = ReminderCapabilities.Reminder(2, caps);
            Assert.DoesNotContain("horizun_verify_changes", text);
            Assert.Contains("owner", text);
            Assert.Contains("full_write", text);
            Assert.Contains("accept_findings", text);
        }

        [Fact]
        public void When_the_tool_is_listed_the_reminder_names_it_and_still_says_findings_are_not_accepted()
        {
            var caps = new ReminderCapabilities { Profile = "full_write", SyncOnly = true, SyncAvailable = true, VerifyAvailable = true };
            string text = ReminderCapabilities.Reminder(1, caps);
            Assert.Contains("re-check them with horizun_verify_changes", text);
            Assert.Contains("does not accept findings", text);
        }

        [Fact]
        public void Without_the_option_the_two_earlier_sentences_are_unchanged()
        {
            var hidden = new ReminderCapabilities { Profile = "safe_write", SyncOnly = false, SyncAvailable = false, VerifyAvailable = false };
            Assert.Contains("horizun_verify_changes is not available in permission_profile=safe_write", ReminderCapabilities.Reminder(1, hidden));
            var listed = new ReminderCapabilities { Profile = "full_write", SyncOnly = false, SyncAvailable = true, VerifyAvailable = true };
            Assert.Contains("re-check them with horizun_verify_changes", ReminderCapabilities.Reminder(1, listed));
        }

        [Theory]
        [InlineData("safe_write", true, false)]
        [InlineData("safe_write", false, true)]
        [InlineData("full_write", true, true)]
        [InlineData("read_only", true, true)]
        [InlineData("documentation", false, true)]
        public void The_decision_from_one_settings_snapshot_is_the_decision_from_its_three_values(string profile, bool grant, bool flag)
        {
            var snap = new Settings.SyncSettingsSnapshot { Profile = profile, OwnerSyncGrant = grant, SyncOnlyFlag = flag };
            SyncOnlyGrantRules.SyncDecision a = SyncOnlyGrantRules.Decide(snap);
            SyncOnlyGrantRules.SyncDecision b = SyncOnlyGrantRules.Decide(profile, grant, flag);
            Assert.Equal(b.Strict, a.Strict);
            Assert.Equal(b.Authorised, a.Authorised);
            Assert.Equal(b.Allowed, a.Allowed);
        }

        [Fact]
        public void The_sync_settings_come_from_one_read_of_the_file_and_fall_closed_when_it_is_unreadable()
        {
            using (new EnvGuard())
            {
                string temp = Path.Combine(Path.GetTempPath(), "hz-int6-" + Guid.NewGuid().ToString("N"));
                EnvGuard.Set(HorizunPaths.RootOverrideVariable, temp);
                try
                {
                    Directory.CreateDirectory(temp);
                    File.WriteAllText(HorizunPaths.SettingsPath(), "{\"permission_profile\":\"full_write\",\"sync_with_central_owner_granted\":true,\"allow_sync_only_grant\":true}");
                    Settings.SyncSettingsSnapshot s = Settings.ReadSyncSettings();
                    Assert.Equal("full_write", s.Profile);
                    Assert.True(s.OwnerSyncGrant);
                    Assert.True(s.SyncOnlyFlag);
                    File.WriteAllText(HorizunPaths.SettingsPath(), "{ not json");
                    Settings.SyncSettingsSnapshot closed = Settings.ReadSyncSettings();
                    Assert.Equal("read_only", closed.Profile);
                    Assert.False(closed.OwnerSyncGrant);
                    Assert.False(closed.SyncOnlyFlag);
                }
                finally { try { Directory.Delete(temp, true); } catch { } }
            }
        }

        [Fact]
        public void The_docs_say_what_the_preview_block_really_holds()
        {
            string doc = File.ReadAllText(Path.Combine(RepoRoot(), "docs", "TOOLS-EXTENDED.md"));
            Assert.DoesNotContain("ok_to_apply", doc);
            JObject block = SyncOnlyGrantRules.PreviewBlock(SyncRelinquish.All, new long[] { 1 }, new[] { "u" }, new long[] { 2 }, new[] { "w" }, new[] { 3 }, 4);
            foreach (string field in new[] { "active", "relinquish", "borrowed", "owned_elements", "owned_worksets", "held_by_others", "token_binds" })
            {
                Assert.NotNull(block[field]);
                Assert.Contains("`" + field + "`", doc);
            }
            Assert.Null(block["ok_to_apply"]);
        }

        private static string RepoRoot()
        {
            string dir = AppContext.BaseDirectory;
            while (dir != null && !File.Exists(Path.Combine(dir, "AGENTS.md"))) dir = Path.GetDirectoryName(dir);
            return dir ?? throw new InvalidOperationException("repository root not found");
        }
    }

    // ---- m-6: a cancel by job id is not lost -----------------------------------------------------------------------

    [Collection("AsyncQueue")]
    public class PlanCancellationInt6Tests
    {
        private static void Drain() { while (AsyncQueue.Take() != null) { } }

        private static AsyncWork Plan(string id) => new AsyncWork
        {
            JobId = id, Command = "horizun_execute_plan", ParamsJson = "{}", QueuedUtc = DateTime.UtcNow
        };

        [Fact]
        public void A_cancel_that_arrives_after_the_plan_is_taken_and_before_its_child_registers_is_kept()
        {
            Drain();
            AsyncQueue.TryAdd(Plan("int6-plan-a"), out _);
            Assert.Equal("not_running", PythonRunRegistry.Cancel("int6-plan-a", "while queued"));   // nothing claimed yet
            AsyncWork taken = AsyncQueue.Take();
            Assert.Equal("int6-plan-a", taken.JobId);
            Assert.Equal("interrupt_requested", PythonRunRegistry.Cancel("int6-plan-a", "between take and child"));
            Assert.True(PythonRunRegistry.IsCancelRequested("int6-plan-a"));
            var child = new PythonRunControl(null);
            PythonRunRegistry.Register("int6-plan-a", child);
            Assert.Equal(PythonRunControl.Cancelled, child.Poll());
            PythonRunRegistry.Unregister("int6-plan-a");
            Assert.False(PythonRunRegistry.IsCancelRequested("int6-plan-a"));
        }

        [Fact]
        public void A_cancel_between_two_steps_survives_the_child_that_ended_and_the_plan_reads_it_before_the_next_step()
        {
            Drain();
            AsyncQueue.TryAdd(Plan("int6-plan-b"), out _);
            AsyncQueue.Take();
            var first = new PythonRunControl(null);
            PythonRunRegistry.Register("int6-plan-b", first);
            Assert.False(PythonRunRegistry.IsCancelRequested("int6-plan-b"));
            PythonRunRegistry.ChildEnded("int6-plan-b");                 // the first script is over; the plan is not
            Assert.False(PythonRunRegistry.IsCancelRequested("int6-plan-b"));
            Assert.Equal("interrupt_requested", PythonRunRegistry.Cancel("int6-plan-b", "between steps"));
            Assert.True(PythonRunRegistry.IsCancelRequested("int6-plan-b"));    // what the plan checks before action 2
            var second = new PythonRunControl(null);
            PythonRunRegistry.Register("int6-plan-b", second);          // a script that does start is stopped at once
            Assert.Equal(PythonRunControl.Cancelled, second.Poll());
            PythonRunRegistry.Unregister("int6-plan-b");
        }

        [Fact]
        public void Taking_a_queued_plan_claims_its_job_and_taking_a_read_does_not()
        {
            Drain();
            Assert.True(PythonRunRegistry.ClaimsAtTake("horizun_execute_python"));
            Assert.True(PythonRunRegistry.ClaimsAtTake("horizun_execute_plan"));
            Assert.False(PythonRunRegistry.ClaimsAtTake("horizun_model_scan"));
            AsyncQueue.TryAdd(new AsyncWork { JobId = "int6-read", Command = "horizun_model_scan", ParamsJson = "{}", QueuedUtc = DateTime.UtcNow }, out _);
            AsyncQueue.Take();
            Assert.Equal("not_running", PythonRunRegistry.Cancel("int6-read", "x"));
        }

        [Fact]
        public void A_plan_that_was_cancelled_while_waiting_is_removed_as_before()
        {
            Drain();
            AsyncQueue.TryAdd(Plan("int6-plan-c"), out _);
            AsyncQueue.Cancellation c = AsyncQueue.CancelWaiting("int6-plan-c");
            Assert.True(c.Cancelled);
            Assert.True(c.BeforeStart);
            Assert.Null(AsyncQueue.Take());
        }
    }

    // ---- m-7 ------------------------------------------------------------------------------------------------------

    public class ChangesAppliedScopeTests
    {
        [Fact]
        public void A_false_changes_applied_carries_the_scope_and_the_categories_at_its_own_level()
        {
            var block = new JObject { ["changes_applied"] = false };
            PythonRunControl.StampChangesAppliedScope(block);
            Assert.Contains("transactions", (string)block["changes_applied_scope"]);
            Assert.Contains("not_reverted_categories", (string)block["changes_applied_scope"]);
            Assert.Equal(PythonRunControl.NotRevertedCategories, block["not_reverted_categories"].Select(t => (string)t).ToArray());
        }

        [Theory]
        [InlineData(null)]
        [InlineData(true)]
        public void A_changes_applied_that_is_not_false_gets_nothing_beside_it(object value)
        {
            var block = new JObject();
            if (value != null) block["changes_applied"] = (bool)value; else block["changes_applied"] = JValue.CreateNull();
            PythonRunControl.StampChangesAppliedScope(block);
            Assert.Null(block["changes_applied_scope"]);
            Assert.Null(block["not_reverted_categories"]);
            PythonRunControl.StampChangesAppliedScope(new JObject());
            PythonRunControl.StampChangesAppliedScope(null);
        }

        [Fact]
        public void Both_places_that_set_it_false_after_a_script_stamp_it()
        {
            string py = File.ReadAllText(Path.Combine(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..")),
                "src", "Horizun.Revit", "Commands", "ExecutePythonCommand.cs"));
            Assert.Equal(2, Regex.Matches(py, Regex.Escape("PythonRunControl.StampChangesAppliedScope(raised)")).Count);
        }
    }

    // ---- m-8 ------------------------------------------------------------------------------------------------------

    public class InternalDependentOwnerTests
    {
        private static HashSet<long> Set(params long[] ids) => new HashSet<long>(ids);

        [Fact]
        public void A_sketch_is_internal_only_when_its_owner_is_a_target()
        {
            Assert.Equal(InternalDependentRules.SketchPart,
                InternalDependentRules.Classify("Sketch", null, false, null, Set(100), null, ownerId: 100));
            Assert.Null(InternalDependentRules.Classify("Sketch", null, false, null, Set(100), null, ownerId: 999));
            Assert.Null(InternalDependentRules.Classify("Sketch", null, false, null, Set(100), null, ownerId: null));
            Assert.Null(InternalDependentRules.Classify("Sketch", null, false, null, Set(100), null));
        }

        [Fact]
        public void A_sketch_line_is_internal_only_through_an_internal_sketch_or_a_target()
        {
            Assert.Equal(InternalDependentRules.SketchPart,
                InternalDependentRules.Classify("ModelCurve", "OST_SketchLines", false, null, Set(100), Set(201), ownerId: 201));
            Assert.Null(InternalDependentRules.Classify("ModelCurve", "OST_SketchLines", false, null, Set(100), Set(201), ownerId: 777));
            Assert.Null(InternalDependentRules.Classify("ModelCurve", "OST_SketchLines", false, null, Set(100), Set(201), ownerId: null));
        }

        [Fact]
        public void Something_that_is_not_a_sketch_part_is_never_internal_whoever_owns_it()
        {
            Assert.Null(InternalDependentRules.Classify("Wall", "OST_Walls", false, null, Set(100), null, ownerId: 100));
        }

        [Fact]
        public void The_dimension_rule_still_reads_the_sketch_parts_found_by_ownership()
        {
            Assert.Equal(InternalDependentRules.ConstrainingDimension,
                InternalDependentRules.Classify("Dimension", "OST_Dimensions", true, new long[] { 201, 100 }, Set(100), Set(201)));
        }

        [Fact]
        public void The_delete_command_resolves_owners_before_it_classifies_and_closes_over_sketch_lines()
        {
            string del = File.ReadAllText(Path.Combine(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..")),
                "src", "Horizun.Revit", "Commands", "DeleteCommand.cs"));
            Assert.Contains("SketchOwner(doc, sk, targetIds)", del);
            Assert.Contains("sketchParts, owner, provenCurves.Contains(c.RawId.Value))", del);
        }
    }
}
