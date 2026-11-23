using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    // R2X - live finding of rc3 (step 18, F-2, F-3, F-4) and the defect that the first compilation of R2W found in validate_ids.
    [Collection("SaveAs")]       // the Save As bookkeeping is one static list: classes that drive it must not run at the same time
    public class ElementIdentityGuardR2XTests
    {
        private static string RepoRoot()
        {
            DirectoryInfo d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, "AGENTS.md"))) d = d.Parent;
            Assert.NotNull(d);
            return d.FullName;
        }

        private static string Source(params string[] parts) => File.ReadAllText(Path.Combine(new[] { RepoRoot() }.Concat(parts).ToArray()));

        // ------------------------------------------------------------------ step 18: Save As keeps what is ours ours

        // A stand-in for a Revit Document wrapper: the pairing must not depend on it being the SAME object in both events.
        private sealed class FakeDocument { }

        private static void Renamer(string a, string b) { }

        [Fact]
        public void A_note_the_session_created_is_still_ours_after_save_as_when_the_events_hand_the_same_document()
        {
            string before = "path:c:/r2x/same-object-a.rvt", target = @"C:\r2x\Same-Object-B.rvt", now = DocumentLifecycleRules.PathKey(target);
            var doc = new FakeDocument();
            SessionCreationLedger.Record(before, new[] { "uid-same" });

            DocumentLifecycleRules.SavingAsBegan(doc, before, target);
            DocumentLifecycleRules.SavedAsCompleted(doc, now, @"C:\r2x\Same-Object-A.rvt", Renamer);

            Assert.True(SessionCreationLedger.Created(now, "uid-same"));
            Assert.False(SessionCreationLedger.Created(before, "uid-same"));
        }

        [Fact]
        public void The_pair_is_found_when_revit_hands_a_different_document_wrapper_to_the_saved_event_by_the_target_path()
        {
            // Pairing by the identity of the wrapper alone lost the pair in the field (step 18).
            string before = "path:c:/r2x/wrapper-a.rvt", target = @"C:\r2x\Wrapper-B.rvt", now = DocumentLifecycleRules.PathKey(target);
            SessionCreationLedger.Record(before, new[] { "uid-wrapper" });

            DocumentLifecycleRules.SavingAsBegan(new FakeDocument(), before, target);
            DocumentLifecycleRules.SavedAsCompleted(new FakeDocument(), now, null, Renamer);

            Assert.True(SessionCreationLedger.Created(now, "uid-wrapper"));
            Assert.False(SessionCreationLedger.Created(before, "uid-wrapper"));
        }

        [Fact]
        public void The_pair_is_found_from_the_original_path_of_the_saved_event_when_the_saving_event_was_never_seen()
        {
            string before = DocumentLifecycleRules.PathKey(@"C:\r2x\Original-A.rvt"), now = DocumentLifecycleRules.PathKey(@"C:\r2x\Original-B.rvt");
            SessionCreationLedger.Record(before, new[] { "uid-original" });

            int moved = DocumentLifecycleRules.SavedAsCompleted(new FakeDocument(), now, @"C:\r2x\original-a.RVT", Renamer);

            Assert.Equal(1, moved);
            Assert.True(SessionCreationLedger.Created(now, "uid-original"));
        }

        [Fact]
        public void The_first_save_of_a_document_without_a_file_moves_what_it_remembered_from_its_title_key()
        {
            string before = "title:Project1#4242", target = @"C:\r2x\First-Save.rvt", now = DocumentLifecycleRules.PathKey(target);
            var doc = new FakeDocument();
            SessionCreationLedger.Record(before, new[] { "uid-first" });

            DocumentLifecycleRules.SavingAsBegan(doc, before, target);
            DocumentLifecycleRules.SavedAsCompleted(doc, now, "", Renamer);                 // no OriginalPath: the document had no file

            Assert.True(SessionCreationLedger.Created(now, "uid-first"));
            Assert.False(SessionCreationLedger.Created(before, "uid-first"));
        }

        [Fact]
        public void Renaming_twice_is_harmless_the_tool_and_the_events_both_run_the_same_rule()
        {
            string before = "path:c:/r2x/twice-a.rvt", target = @"C:\r2x\Twice-B.rvt", now = DocumentLifecycleRules.PathKey(target);
            SessionCreationLedger.Record(before, new[] { "uid-twice" });
            DocumentLifecycleRules.SavedAs(before, now, Renamer);                            // the tool, right after doc.SaveAs
            DocumentLifecycleRules.SavingAsBegan(new FakeDocument(), before, target);
            DocumentLifecycleRules.SavedAsCompleted(new FakeDocument(), now, @"C:\r2x\Twice-A.rvt", Renamer);   // the events, afterwards

            Assert.True(SessionCreationLedger.Created(now, "uid-twice"));
            Assert.False(SessionCreationLedger.Created(before, "uid-twice"));
        }

        [Fact]
        public void A_save_as_that_never_completed_loses_nothing()
        {
            string before = "path:c:/r2x/cancelled-a.rvt";
            SessionCreationLedger.Record(before, new[] { "uid-cancelled" });
            DocumentLifecycleRules.SavingAsBegan(new FakeDocument(), before, @"C:\r2x\Cancelled-B.rvt");
            // no SavedAs: the person cancelled
            Assert.True(SessionCreationLedger.Created(before, "uid-cancelled"));
        }

        [Fact]
        public void A_save_as_over_the_same_path_moves_nothing()
        {
            string key = DocumentLifecycleRules.PathKey(@"C:\r2x\Same-Path.rvt");
            SessionCreationLedger.Record(key, new[] { "uid-samepath" });
            DocumentLifecycleRules.SavingAsBegan(new FakeDocument(), key, @"C:\r2x\same-path.rvt");
            int moved = DocumentLifecycleRules.SavedAsCompleted(new FakeDocument(), key, @"C:\r2x\Same-Path.rvt", Renamer);
            Assert.Equal(0, moved);
            Assert.True(SessionCreationLedger.Created(key, "uid-samepath"));
        }

        [Fact]
        public void The_history_of_writes_and_the_pending_sync_writes_follow_the_document_too()
        {
            string before = "path:c:/r2x/sync-a.rvt", now = "path:c:/r2x/sync-b.rvt";
            var renamed = new List<string>();
            SyncLedgerRules.RecordWrite(before, new SyncLedgerEntry { Tool = "horizun_annotate", AtUtc = DateTime.UtcNow });
            SyncLedgerRules.MarkSynced("path:c:/r2x/sync-other.rvt", DateTime.UtcNow);

            DocumentLifecycleRules.SavedAs(before, now, (a, b) => renamed.Add(a + ">" + b));

            Assert.Single(SyncLedgerRules.Writes(now));
            Assert.Empty(SyncLedgerRules.Writes(before));
            Assert.Equal(new[] { before + ">" + now }, renamed);                              // the Revit-bound history is told as well
        }

        [WindowsFact]
        public void The_document_key_of_a_file_is_built_in_one_place_and_the_tool_and_the_events_use_it()
        {
            Assert.Equal(@"path:c:\models\a.rvt", DocumentLifecycleRules.PathKey(@"C:/Models/A.rvt"));        // R2Y m-2: one spelling for both separators
            Assert.Null(DocumentLifecycleRules.PathKey(""));
            Assert.Contains("DocumentLifecycleRules.PathKey(path)", Source("src", "Horizun.Revit", "Core", "ChangeWatch.cs"));

            string lifecycle = Source("src", "Horizun.Revit", "Core", "QueryCacheLifecycle.cs");
            Assert.Contains("SavingAsBegan(e.Document, ChangeWatch.Key(e.Document), e.PathName)", lifecycle);
            Assert.Contains("SavedAsCompleted(e.Document, ChangeWatch.Key(e.Document), e.OriginalPath, ChangeLedger.Rename, e.Status == Autodesk.Revit.DB.Events.RevitAPIEventStatus.Succeeded)", lifecycle);
        }

        [Fact]
        public void The_save_as_tool_moves_the_ledgers_itself_right_after_the_save()
        {
            string command = Source("src", "Horizun.Revit", "Commands", "DocumentSessionCommand.cs");
            int key = command.IndexOf("string keyBeforeSaveAs = saveAs ? ChangeWatch.Key(doc) : null;", StringComparison.Ordinal);
            int save = command.IndexOf("doc.SaveAs(ModelPathUtils.ConvertUserVisiblePathToModelPath(targetPath), opts);", StringComparison.Ordinal);
            int rename = command.IndexOf("DocumentLifecycleRules.SavedAs(keyBeforeSaveAs, ChangeWatch.Key(doc), ChangeLedger.Rename);", StringComparison.Ordinal);
            Assert.True(key > 0 && save > key, "the key is read BEFORE doc.SaveAs");
            Assert.True(rename > save, "the ledgers follow the document AFTER doc.SaveAs");
        }

        // ------------------------------------------------------------------ F-3: accept_findings

        [Fact]
        public void The_refusal_for_unverified_writes_names_the_tool_that_declares_accept_findings()
        {
            string message = SyncLedgerRules.BlockedMessage("Model", new List<SyncLedgerEntry>
            {
                new SyncLedgerEntry { Tool = "horizun_execute_python", EvidenceStatus = "completed_unverified" }
            });
            Assert.Contains("horizun_save_document operation=sync_with_central", message);
            Assert.Contains("accept_findings=true", message);
            Assert.Contains("horizun_document_session does not take accept_findings", message);
            Assert.Contains("Nothing was synchronized", message);
        }

        [Fact]
        public void The_tool_named_by_the_refusal_really_declares_accept_findings_and_the_other_one_does_not()
        {
            // The message is only right while the schemas say what it says.
            string contract = Source("src", "Horizun.Contracts", "Contract.cs");
            int saveStart = contract.IndexOf("Name = \"horizun_save_document\"", StringComparison.Ordinal);
            int sessionStart = contract.IndexOf("Name = \"horizun_document_session\"", StringComparison.Ordinal);
            int openStart = contract.IndexOf("Name = \"horizun_open_document\"", StringComparison.Ordinal);
            Assert.True(saveStart > 0 && openStart > saveStart && sessionStart > openStart);
            Assert.Contains("\"\"accept_findings\"\"", contract.Substring(saveStart, openStart - saveStart));
            int sessionEnd = contract.IndexOf("new CommandContract", sessionStart, StringComparison.Ordinal);
            Assert.DoesNotContain("accept_findings", contract.Substring(sessionStart, sessionEnd - sessionStart));
            Assert.Equal(SyncLedgerRules.AcceptFindingsTool, "horizun_save_document");
            Assert.Equal(SyncLedgerRules.AcceptFindingsArgument, "accept_findings");
        }

        [Fact]
        public void The_sync_command_builds_its_refusal_with_that_text_and_tells_the_caller_where_to_accept()
        {
            string sync = Source("src", "Horizun.Revit", "Commands", "DocumentSessionSync.cs");
            Assert.Contains("SyncLedgerRules.BlockedMessage(title, blockers, wayOut)", sync);
            Assert.Contains("[\"accept_findings_with\"]", sync);
            Assert.DoesNotContain("repeat with accept_findings=true", sync);
        }

        // ------------------------------------------------------------------ F-4: a local that Revit still reports as a central

        [Fact]
        public void The_refusal_for_a_never_opened_local_says_that_open_central_is_the_right_flag_for_it()
        {
            var facts = new OpenFacts { Path = @"C:\scratch\Tower_local.rvt", CentralPath = @"C:\scratch\Tower.rvt", IsCentral = true, FileVersion = "2026", HostVersion = "2026" };
            OpenVerdict v = OpenDecision.Decide(facts, new OpenIntent { ExpectedVersion = "2026" });
            Assert.False(v.Ok);
            Assert.Contains("CreateNewLocal", v.Refusal);
            Assert.Contains("open_central names what Revit reports about the file", v.Refusal);
            Assert.Contains("does not open the central model itself", v.Refusal);
            Assert.True(OpenDecision.Decide(facts, new OpenIntent { ExpectedVersion = "2026", OpenCentral = true }).Ok);
        }

        // ------------------------------------------------------------------ compile finding: a JSON null is not a C# null

        [Fact]
        public void A_validate_ids_row_whose_found_is_a_json_null_is_read_without_throwing()
        {
            IdFreshnessRules.ResetForTests();
            string doc = "path:c:/r2x/json-null.rvt";
            DateTime t = DateTime.UtcNow;
            IdFreshnessRules.NoteReply(doc, t, "horizun_query_model", null, new JObject { ["rows"] = new JArray(new JObject { ["element_id"] = 7, ["unique_id"] = "u7" }) });
            IdFreshnessRules.NoteSynchronized(doc, t);
            Assert.Equal(1, (int)IdFreshnessRules.Block(doc)["ids_to_revalidate"]);          // the notice waits for id 7

            var reply = new JObject
            {
                ["results"] = new JArray(
                    new JObject { ["status"] = "no_existe", ["expected"] = new JObject { ["id"] = 7 }, ["found"] = JValue.CreateNull() },
                    new JObject { ["status"] = "no_existe", ["expected"] = JValue.CreateNull(), ["found"] = JValue.CreateNull() })
            };
            Assert.Null(IdFreshnessRules.NoteReply(doc, t, "horizun_selection_context", "validate_ids", reply));    // a JSON null is read as "nothing there", not thrown at

            // The state followed the row: id 7 was proven gone (the row with expected null proves nothing and is skipped), so nothing is left to revalidate.
            Assert.Null(IdFreshnessRules.Block(doc));
            IdFreshnessRules.ResetForTests();
        }
    }
}
