using System;
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    // R2Y - the findings of the independent review of R2X: M-1 (current_id pointed at the intruder), M-2 and m-1 (a Save As that failed handed its ledgers to
    // another document; the wrapper is not the document), m-2 (the document key), m-3 (one rule for every ledger on a rename), m-6 (the overflow notice).
    [Collection("SaveAs")]
    public class ElementIdentityGuardR2YTests
    {
        private static readonly DateTime T = new DateTime(2026, 11, 1, 18, 0, 0, DateTimeKind.Utc);

        private static ElementIdentityFacts Note(long id, string uid) => new ElementIdentityFacts
        {
            Id = id, UniqueId = uid, Owned = true, Category = "Text Notes", CategoryBuiltIn = "OST_TextNotes", Type = "Text 3mm", Family = "Text"
        };

        private static JObject Rows(params (long id, string uid)[] rows) =>
            new JObject { ["rows"] = new JArray(rows.Select(r => new JObject { ["element_id"] = r.id, ["unique_id"] = r.uid })) };

        // ------------------------------------------------------------------ M-1

        [Fact]
        public void When_the_unique_id_asked_for_no_longer_exists_current_id_is_null_and_never_the_id_of_the_note_that_took_the_old_one()
        {
            // The review's scenario: the agent holds 5001 -> uidA; a synchronize deleted uidA and 5001 is uidD now.
            var model = new Dictionary<long, ElementIdentityFacts> { [5001] = Note(5001, "uidD") };
            Func<long, ElementIdentityFacts> byId = id => model.TryGetValue(id, out ElementIdentityFacts f) ? f : null;
            Func<string, ElementIdentityFacts> byUid = uid => model.Values.FirstOrDefault(f => f.UniqueId == uid);

            JToken row = ElementIdentityGuard.Validate(JArray.Parse("[{\"id\":5001,\"unique_id\":\"uidA\"}]"), byId, byUid)["results"][0];

            Assert.Equal("cambio", (string)row["status"]);
            Assert.Contains("no longer exists", (string)row["reason"]);
            Assert.NotNull(row["current_id"]);
            Assert.Equal(JTokenType.Null, row["current_id"].Type);                    // before R2Y: 5001, the intruder
            Assert.Null(row["found"]["unique_id"]);
            Assert.Equal("uidD", (string)row["found"]["holder_unique_id"]);
            Assert.False((bool)row["identifies"]);
            Assert.Contains("null when its unique_id no longer exists", (string)row["warning"]);
        }

        [Fact]
        public void When_the_unique_id_asked_for_lives_elsewhere_current_id_is_where_it_lives_and_a_plain_change_of_category_keeps_the_found_id()
        {
            var model = new Dictionary<long, ElementIdentityFacts> { [5001] = Note(5001, "uidD"), [9101] = Note(9101, "uidA") };
            Func<long, ElementIdentityFacts> byId = id => model.TryGetValue(id, out ElementIdentityFacts f) ? f : null;
            Func<string, ElementIdentityFacts> byUid = uid => model.Values.FirstOrDefault(f => f.UniqueId == uid);

            JToken moved = ElementIdentityGuard.Validate(JArray.Parse("[{\"id\":5001,\"unique_id\":\"uidA\"}]"), byId, byUid)["results"][0];
            Assert.Equal(9101, (long)moved["current_id"]);

            JToken sameElementOtherCategory = ElementIdentityGuard.Validate(JArray.Parse("[{\"id\":9101,\"unique_id\":\"uidA\",\"expected_category\":\"Walls\"}]"), byId, byUid)["results"][0];
            Assert.Equal("cambio", (string)sameElementOtherCategory["status"]);
            Assert.Equal(9101, (long)sameElementOtherCategory["current_id"]);          // the element IS the one asked for: its own id
        }

        [Fact]
        public void The_texts_that_promise_current_id_say_that_it_is_null_when_the_element_is_gone()
        {
            Assert.Contains("null when the unique_id no longer exists", ElementIdentityGuard.ValidateIdsNote);
            IdFreshnessRules.ResetForTests();
            IdFreshnessRules.NoteReply("path:c:\\r2y\\text.rvt", T, "horizun_query_model", null, Rows((1, "a")));
            IdFreshnessRules.NoteSynchronized("path:c:\\r2y\\text.rvt", T);
            JObject changed = IdFreshnessRules.NoteReply("path:c:\\r2y\\text.rvt", T, "horizun_query_model", null, Rows((1, "b")));
            Assert.Contains("null when it no longer exists", (string)changed["note"]);
            IdFreshnessRules.ResetForTests();
        }

        // ------------------------------------------------------------------ M-2 and m-1: Save As between two documents

        // A stand-in for a Revit Document: two wrappers of the same document are different objects that are Equal.
        private sealed class FakeDocument
        {
            public readonly int Id;
            public FakeDocument(int id) { Id = id; }
            public override bool Equals(object obj) => obj is FakeDocument d && d.Id == Id;
            public override int GetHashCode() => Id;
        }

        private static void NoLedger(string a, string b) { }

        [Fact]
        public void A_save_as_of_one_document_that_failed_leaves_its_books_and_the_next_save_as_of_another_document_to_the_same_file_inherits_nothing()
        {
            // The review's scenario: A saves as X and fails; later B saves as X.
            string keyA = DocumentLifecycleRules.PathKey(@"C:\r2y\Two-A.rvt"), keyB = DocumentLifecycleRules.PathKey(@"C:\r2y\Two-B.rvt");
            string target = @"C:\r2y\Two-X.rvt", keyX = DocumentLifecycleRules.PathKey(target);
            SessionCreationLedger.Record(keyA, new[] { "uid-of-a" });
            SessionCreationLedger.Record(keyB, new[] { "uid-of-b" });
            SyncLedgerRules.RecordWrite(keyA, new SyncLedgerEntry { Tool = "horizun_execute_python", EvidenceStatus = "completed_unverified", AtUtc = T });
            var docA = new FakeDocument(1);
            var docB = new FakeDocument(2);

            DocumentLifecycleRules.SavingAsBegan(docA, keyA, target);
            Assert.Equal(0, DocumentLifecycleRules.SavedAsCompleted(docA, keyA, @"C:\r2y\Two-A.rvt", NoLedger, succeeded: false));     // the event says it failed

            DocumentLifecycleRules.SavingAsBegan(docB, keyB, target);
            Assert.Equal(1, DocumentLifecycleRules.SavedAsCompleted(docB, keyX, @"C:\r2y\Two-B.rvt", NoLedger));

            Assert.True(SessionCreationLedger.Created(keyX, "uid-of-b"));                // B brought its own
            Assert.False(SessionCreationLedger.Created(keyX, "uid-of-a"));               // and nothing of A
            Assert.True(SessionCreationLedger.Created(keyA, "uid-of-a"));                // A kept its books
            Assert.Single(SyncLedgerRules.Blockers(keyA));                               // and its blocking write: A still cannot synchronize without accepting the findings
            Assert.Empty(SyncLedgerRules.Writes(keyX));                                  // B does not inherit them
        }

        [Fact]
        public void Even_when_the_failed_save_as_never_reported_and_the_next_one_was_not_seen_beginning_the_target_path_does_not_hand_over_the_books_of_another_document()
        {
            string keyA = DocumentLifecycleRules.PathKey(@"C:\r2y\Silent-A.rvt"), keyB = DocumentLifecycleRules.PathKey(@"C:\r2y\Silent-B.rvt");
            string target = @"C:\r2y\Silent-X.rvt", keyX = DocumentLifecycleRules.PathKey(target);
            SessionCreationLedger.Record(keyA, new[] { "uid-silent-a" });
            SessionCreationLedger.Record(keyB, new[] { "uid-silent-b" });

            DocumentLifecycleRules.SavingAsBegan(new FakeDocument(1), keyA, target);      // A's Save As: no SavedAs ever arrives
            // B's SavedAs arrives with no SavingAs seen; its own OriginalPath says where B started from.
            int moved = DocumentLifecycleRules.SavedAsCompleted(new FakeDocument(2), keyX, @"C:\r2y\Silent-B.rvt", NoLedger);

            Assert.Equal(1, moved);
            Assert.True(SessionCreationLedger.Created(keyX, "uid-silent-b"));
            Assert.False(SessionCreationLedger.Created(keyX, "uid-silent-a"));             // before R2Y: A's entry was paired by the target path alone
            Assert.True(SessionCreationLedger.Created(keyA, "uid-silent-a"));
        }

        [Fact]
        public void A_new_save_as_drops_the_entry_of_an_older_one_that_never_ended()
        {
            string keyA = DocumentLifecycleRules.PathKey(@"C:\r2y\Drop-A.rvt"), keyB = DocumentLifecycleRules.PathKey(@"C:\r2y\Drop-B.rvt");
            string target = @"C:\r2y\Drop-X.rvt", keyX = DocumentLifecycleRules.PathKey(target);
            SessionCreationLedger.Record(keyA, new[] { "uid-drop-a" });
            SessionCreationLedger.Record(keyB, new[] { "uid-drop-b" });

            DocumentLifecycleRules.SavingAsBegan(new FakeDocument(1), keyA, target);
            DocumentLifecycleRules.SavingAsBegan(new FakeDocument(2), keyB, target);
            DocumentLifecycleRules.SavedAsCompleted(new FakeDocument(2), keyX, null, NoLedger);   // no OriginalPath: only the pending entries can say where B started

            Assert.True(SessionCreationLedger.Created(keyX, "uid-drop-b"));
            Assert.False(SessionCreationLedger.Created(keyX, "uid-drop-a"));
            Assert.True(SessionCreationLedger.Created(keyA, "uid-drop-a"));
        }

        [Fact]
        public void A_cancelled_or_failed_save_as_event_moves_nothing_and_discards_its_entry()
        {
            string before = DocumentLifecycleRules.PathKey(@"C:\r2y\Cancel-A.rvt"), target = @"C:\r2y\Cancel-X.rvt", keyX = DocumentLifecycleRules.PathKey(target);
            SessionCreationLedger.Record(before, new[] { "uid-cancel" });
            var doc = new FakeDocument(7);

            DocumentLifecycleRules.SavingAsBegan(doc, before, target);
            int moved = DocumentLifecycleRules.SavedAsCompleted(doc, keyX, @"C:\r2y\Cancel-A.rvt", NoLedger, succeeded: false);

            Assert.Equal(0, moved);
            Assert.True(SessionCreationLedger.Created(before, "uid-cancel"));
            Assert.False(SessionCreationLedger.Created(keyX, "uid-cancel"));
            // The entry is gone: a later event with no OriginalPath finds nothing to pair with.
            Assert.Equal(0, DocumentLifecycleRules.SavedAsCompleted(doc, keyX, null, NoLedger));
        }

        [Fact]
        public void Two_wrappers_of_the_same_document_are_the_same_document_even_when_the_target_path_does_not_match()
        {
            // m-1: Revit hands out wrappers; Document.Equals, not the identity of the object, says whether two of them are one document.
            string before = DocumentLifecycleRules.PathKey(@"C:\r2y\Equal-A.rvt"), keyNow = DocumentLifecycleRules.PathKey(@"C:\r2y\Equal-Elsewhere.rvt");
            SessionCreationLedger.Record(before, new[] { "uid-equal" });

            DocumentLifecycleRules.SavingAsBegan(new FakeDocument(9), before, @"C:\r2y\Equal-Planned.rvt");
            int moved = DocumentLifecycleRules.SavedAsCompleted(new FakeDocument(9), keyNow, null, NoLedger);

            Assert.Equal(1, moved);
            Assert.True(SessionCreationLedger.Created(keyNow, "uid-equal"));
        }

        [Fact]
        public void The_document_comparison_is_replaceable_by_the_caller_of_the_pure_rule()
        {
            Func<object, object, bool> original = DocumentLifecycleRules.SameDocument;
            try
            {
                DocumentLifecycleRules.SameDocument = (a, b) => true;
                Assert.True(DocumentLifecycleRules.SameDocument(new object(), new object()));
            }
            finally { DocumentLifecycleRules.SameDocument = original; }
            Assert.False(DocumentLifecycleRules.SameDocument(new object(), new object()));
            Assert.True(DocumentLifecycleRules.SameDocument(new FakeDocument(3), new FakeDocument(3)));
        }

        // ------------------------------------------------------------------ m-2: the document key

        [WindowsFact]
        public void The_document_key_is_one_for_both_separators_case_dots_and_trailing_separators()
        {
            string key = DocumentLifecycleRules.PathKey(@"C:\Models\Sub\A.rvt");
            Assert.Equal(@"path:c:\models\sub\a.rvt", key);
            Assert.Equal(key, DocumentLifecycleRules.PathKey("C:/models/SUB/a.RVT"));
            Assert.Equal(key, DocumentLifecycleRules.PathKey(@"C:\Models\Other\..\Sub\A.rvt"));
            Assert.Equal(key, DocumentLifecycleRules.PathKey(@"C:\Models\.\Sub\A.rvt"));
            Assert.Equal(DocumentLifecycleRules.PathKey(@"C:\Models\Sub"), DocumentLifecycleRules.PathKey("C:/Models/Sub/"));
            Assert.Equal(DocumentLifecycleRules.PathKey(@"C:\Models\Sub"), DocumentLifecycleRules.PathKey(@"C:\Models\Sub\\"));
        }

        [WindowsFact]
        public void The_document_key_of_a_relative_path_is_the_one_of_its_absolute_path()
        {
            string absolute = System.IO.Path.GetFullPath("r2y-relative-model.rvt");
            Assert.Equal(DocumentLifecycleRules.PathKey(absolute), DocumentLifecycleRules.PathKey("r2y-relative-model.rvt"));
            Assert.Equal(DocumentLifecycleRules.PathKey(absolute), DocumentLifecycleRules.PathKey(@".\r2y-relative-model.rvt"));
        }

        [WindowsFact]
        public void The_document_key_of_a_network_path_keeps_its_share_and_does_not_depend_on_the_separator()
        {
            string unc = DocumentLifecycleRules.PathKey(@"\\Server\Share\Projects\A.rvt");
            Assert.Equal(@"path:\\server\share\projects\a.rvt", unc);
            Assert.Equal(unc, DocumentLifecycleRules.PathKey("//SERVER/share/projects/a.rvt"));
            Assert.Equal(unc, DocumentLifecycleRules.PathKey(@"\\Server\Share\Projects\A.rvt\"));
            Assert.NotEqual(unc, DocumentLifecycleRules.PathKey(@"\\Server\Other\Projects\A.rvt"));
        }

        [WindowsFact]
        public void The_root_of_a_drive_keeps_its_separator()
        {
            Assert.Equal(@"path:c:\", DocumentLifecycleRules.PathKey(@"C:\"));
            Assert.Equal(@"path:c:\", DocumentLifecycleRules.PathKey("C:/"));
        }

        [Theory]
        [InlineData("C:\\r2y\\bad\0name.rvt")]
        [InlineData("   ")]
        [InlineData("C:\\r2y\\what?.rvt")]
        [InlineData("C:\\r2y\\a|b*.rvt")]
        [InlineData("\\\\")]
        [InlineData("C:\\" + "x\\" + "very-long-name\\\\\\\\")]
        public void A_path_that_cannot_be_resolved_never_throws_out_of_an_event_handler_and_still_gives_a_stable_key(string path)
        {
            string key = DocumentLifecycleRules.PathKey(path);
            Assert.NotNull(key);
            Assert.StartsWith("path:", key);
            Assert.Equal(key, DocumentLifecycleRules.PathKey(path));
        }

        // ------------------------------------------------------------------ m-3: one rule for every ledger on a rename

        [Fact]
        public void On_a_rename_the_creations_of_the_destination_are_replaced_by_the_ones_of_the_renamed_document()
        {
            string old = "path:c:\\r2y\\ren-creations-old.rvt", dest = "path:c:\\r2y\\ren-creations-dest.rvt";
            SessionCreationLedger.Record(dest, new[] { "left-by-a-closed-document" });
            SessionCreationLedger.Record(old, new[] { "mine" });

            SessionCreationLedger.Rename(old, dest);

            Assert.True(SessionCreationLedger.Created(dest, "mine"));
            Assert.False(SessionCreationLedger.Created(dest, "left-by-a-closed-document"));      // before R2Y: merged, and another model's elements were "ours"
            Assert.False(SessionCreationLedger.Created(old, "mine"));
            SessionCreationLedger.Rename(old, dest);                                              // twice: harmless
            Assert.True(SessionCreationLedger.Created(dest, "mine"));
        }

        [Fact]
        public void On_a_rename_the_pending_writes_and_the_last_sync_of_the_destination_are_replaced_by_the_ones_of_the_renamed_document()
        {
            string old = "path:c:\\r2y\\ren-sync-old.rvt", dest = "path:c:\\r2y\\ren-sync-dest.rvt";
            DateTime closedSync = T.AddDays(-3), mySync = T.AddHours(-1);
            SyncLedgerRules.RecordWrite(dest, new SyncLedgerEntry { Tool = "from_the_closed_document", EvidenceStatus = "completed_unverified", AtUtc = T });
            SyncLedgerRules.MarkSynced("path:c:\\r2y\\ren-sync-other.rvt", T);
            SyncLedgerRules.RecordWrite(old, new SyncLedgerEntry { Tool = "mine", AtUtc = T });

            SyncLedgerRules.Rename(old, dest);

            Assert.Equal(new[] { "mine" }, SyncLedgerRules.Writes(dest).Select(w => w.Tool).ToArray());     // before R2Y: both, and a stranger's blocking write was inherited
            Assert.Empty(SyncLedgerRules.Blockers(dest));

            // Its last sync follows too, and replaces the destination's.
            SyncLedgerRules.MarkSynced(dest, closedSync);
            string old2 = "path:c:\\r2y\\ren-sync-old2.rvt";
            SyncLedgerRules.MarkSynced(old2, mySync);
            SyncLedgerRules.Rename(old2, dest);
            Assert.Equal(mySync, SyncLedgerRules.LastSyncUtc(dest));
            SyncLedgerRules.Rename(old2, dest);                                                     // twice: harmless
            Assert.Equal(mySync, SyncLedgerRules.LastSyncUtc(dest));
        }

        [Fact]
        public void On_a_rename_the_notice_state_of_the_destination_is_replaced_by_the_one_of_the_renamed_document()
        {
            IdFreshnessRules.ResetForTests();
            string old = "path:c:\\r2y\\ren-notice-old.rvt", dest = "path:c:\\r2y\\ren-notice-dest.rvt";
            IdFreshnessRules.NoteReply(dest, T, "horizun_query_model", null, Rows((1, "closed")));
            IdFreshnessRules.NoteSynchronized(dest, T);
            IdFreshnessRules.NoteReply(old, T, "horizun_query_model", null, Rows((2, "mine")));
            Assert.NotNull(IdFreshnessRules.Block(dest));

            IdFreshnessRules.Rename(old, dest);

            Assert.Null(IdFreshnessRules.Block(dest));                  // the renamed document was never synchronized
            Assert.Null(IdFreshnessRules.Block(old));
            IdFreshnessRules.ResetForTests();
        }

        [Fact]
        public void The_rule_that_the_change_history_uses_replaces_the_destination_moves_both_halves_and_is_harmless_twice()
        {
            // The change history (ChangeLedger) is Revit-bound; it moves its two dictionaries with this rule.
            var last = new Dictionary<string, string> { ["dest"] = "stale-last", ["old"] = "last" };
            var history = new Dictionary<string, List<int>> { ["dest"] = new List<int> { 99 }, ["old"] = new List<int> { 1, 2 } };

            Assert.True(LedgerRenameRules.Move(last, history, "old", "dest"));
            Assert.Equal("last", last["dest"]);
            Assert.Equal(new[] { 1, 2 }, history["dest"].ToArray());
            Assert.False(last.ContainsKey("old") || history.ContainsKey("old"));

            Assert.False(LedgerRenameRules.Move(last, history, "old", "dest"));                      // nothing under the old key: nothing is replaced
            Assert.Equal("last", last["dest"]);
            Assert.Equal(new[] { 1, 2 }, history["dest"].ToArray());

            // A document with only one half still replaces the destination's other half: the destination ends up with the renamed document's content only.
            var onlyLast = new Dictionary<string, string> { ["old"] = "last-only", ["dest"] = "stale" };
            var noHistory = new Dictionary<string, List<int>> { ["dest"] = new List<int> { 5 } };
            Assert.True(LedgerRenameRules.Move(onlyLast, noHistory, "old", "dest"));
            Assert.Equal("last-only", onlyLast["dest"]);
            Assert.False(noHistory.ContainsKey("dest"));

            Assert.False(LedgerRenameRules.Move(last, history, "dest", "dest"));
            Assert.False(LedgerRenameRules.Move(last, history, null, "dest"));
            Assert.False(LedgerRenameRules.Move(last, history, "x", ""));
        }

        [Fact]
        public void The_change_history_really_uses_that_rule()
        {
            // Wiring of a Revit-bound class that no test of this assembly can execute.
            string source = System.IO.File.ReadAllText(System.IO.Path.Combine(RepoRoot(), "src", "Horizun.Revit", "Core", "ChangeWatch.cs"));
            Assert.Contains("LedgerRenameRules.Move(Last, History, oldKey, newKey);", source);
        }

        private static string RepoRoot()
        {
            System.IO.DirectoryInfo d = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !System.IO.File.Exists(System.IO.Path.Combine(d.FullName, "AGENTS.md"))) d = d.Parent;
            Assert.NotNull(d);
            return d.FullName;
        }

        // ------------------------------------------------------------------ m-6: the overflow notice

        private const string Doc = "path:c:\\r2y\\overflow.rvt";

        private static void Overflowed()
        {
            IdFreshnessRules.ResetForTests();
            var rows = new JArray();
            for (int i = 1; i <= IdFreshnessRules.MaxTrackedIds + 5; i++) rows.Add(new JObject { ["element_id"] = i, ["unique_id"] = "u" + i });
            IdFreshnessRules.NoteReply(Doc, T, "horizun_model_scan", null, new JObject { ["rows"] = rows });
            IdFreshnessRules.NoteSynchronized(Doc, T);
            IdFreshnessRules.NoteReply(Doc, T, "horizun_model_scan", null, new JObject { ["rows"] = rows });
            Assert.True((bool)IdFreshnessRules.Block(Doc)["tracking_overflow"]);
        }

        [Fact]
        public void Past_the_cap_a_coincide_of_an_id_the_session_never_handed_out_does_not_turn_the_notice_off()
        {
            Overflowed();
            var model = new Dictionary<long, ElementIdentityFacts> { [99999999] = Note(99999999, "never-handed-out") };
            Func<long, ElementIdentityFacts> byId = id => model.TryGetValue(id, out ElementIdentityFacts f) ? f : null;
            Func<string, ElementIdentityFacts> byUid = uid => model.Values.FirstOrDefault(f => f.UniqueId == uid);

            JObject coincide = ElementIdentityGuard.Validate(JArray.Parse("[{\"id\":99999999,\"unique_id\":\"never-handed-out\"}]"), byId, byUid);
            Assert.Equal("coincide", (string)coincide["results"][0]["status"]);
            IdFreshnessRules.NoteReply(Doc, T, "horizun_selection_context", "validate_ids", coincide);
            Assert.NotNull(IdFreshnessRules.Block(Doc));                                     // before R2Y: off

            var cambio = new JObject { ["results"] = new JArray(new JObject
            {
                ["status"] = "cambio", ["expected"] = new JObject { ["id"] = 88888888 }, ["found"] = new JObject { ["id"] = 88888888, ["holder_unique_id"] = "someone" }
            }) };
            IdFreshnessRules.NoteReply(Doc, T, "horizun_selection_context", "validate_ids", cambio);
            Assert.NotNull(IdFreshnessRules.Block(Doc));
            IdFreshnessRules.ResetForTests();
        }

        [Fact]
        public void Past_the_cap_a_validate_ids_on_an_id_that_was_tracked_turns_the_notice_off()
        {
            Overflowed();
            var held = new JObject { ["results"] = new JArray(new JObject
            {
                ["status"] = "coincide", ["expected"] = new JObject { ["id"] = 7 }, ["found"] = new JObject { ["id"] = 7, ["unique_id"] = "u7" }
            }) };
            IdFreshnessRules.NoteReply(Doc, T, "horizun_selection_context", "validate_ids", held);
            Assert.Null(IdFreshnessRules.Block(Doc));
            IdFreshnessRules.ResetForTests();
        }

        [Fact]
        public void Past_the_cap_a_validate_ids_on_an_id_that_was_handed_out_after_the_cap_turns_the_notice_off_too()
        {
            // "The ids you are going to write" are the ones the session handed out; the ones past the cap are remembered (only the id) for exactly this.
            Overflowed();
            long pastTheCap = IdFreshnessRules.MaxTrackedIds + 3;
            var held = new JObject { ["results"] = new JArray(new JObject
            {
                ["status"] = "coincide", ["expected"] = new JObject { ["id"] = pastTheCap }, ["found"] = new JObject { ["id"] = pastTheCap, ["unique_id"] = "u" + pastTheCap }
            }) };
            IdFreshnessRules.NoteReply(Doc, T, "horizun_selection_context", "validate_ids", held);
            Assert.Null(IdFreshnessRules.Block(Doc));
            IdFreshnessRules.ResetForTests();
        }

        [Fact]
        public void A_validate_ids_on_an_unknown_id_does_not_make_the_session_overflow()
        {
            IdFreshnessRules.ResetForTests();
            var rows = new JArray();
            for (int i = 1; i <= IdFreshnessRules.MaxTrackedIds; i++) rows.Add(new JObject { ["element_id"] = i, ["unique_id"] = "u" + i });
            IdFreshnessRules.NoteReply(Doc, T, "horizun_model_scan", null, new JObject { ["rows"] = rows });      // exactly at the cap: nothing overflowed
            IdFreshnessRules.NoteSynchronized(Doc, T);
            Assert.Null(IdFreshnessRules.Block(Doc)["tracking_overflow"]);

            var unknown = new JObject { ["results"] = new JArray(new JObject
            {
                ["status"] = "coincide", ["expected"] = new JObject { ["id"] = 99999999 }, ["found"] = new JObject { ["id"] = 99999999, ["unique_id"] = "x" }
            }) };
            IdFreshnessRules.NoteReply(Doc, T, "horizun_selection_context", "validate_ids", unknown);
            Assert.Null(IdFreshnessRules.Block(Doc)["tracking_overflow"]);
            IdFreshnessRules.ResetForTests();
        }
    }
}
