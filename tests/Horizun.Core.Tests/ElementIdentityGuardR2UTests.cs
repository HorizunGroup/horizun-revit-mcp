using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Horizun.Contracts;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    // R2U - the findings of the independent review of R2T. M-A (the sync notice lost to any read; the ledger kept ElementIds), M-B (another
    // user's mark believed), the collaborative rule (a category / type alone is refused in a workshared model, always), m-1 (every delete
    // call classified), m-2 (the whole coverage in the hash; unknown fails closed), m-3 (read-only tables), m-4 (Reload Latest),
    // m-5 (behaviour instead of text), m-6 (fewer refusals than necessary) and the wording of the refusal tied to the documents.
    public class ElementIdentityGuardR2UTests
    {
        // 1..99 are ours (Horizun mark); 100..199 carry no mark. Every element N has the unique_id "uN".
        private static ElementIdentityFacts Facts(long id)
        {
            if (id < 1 || id > 999) return null;
            return new ElementIdentityFacts
            {
                Id = id, UniqueId = "u" + id, Owned = !(id >= 100 && id < 200),
                Category = "Walls", CategoryBuiltIn = "OST_Walls", Type = "Generic - 200mm", Family = "Basic Wall"
            };
        }

        private static ElementIdentityFacts ByUid(string uid) =>
            uid != null && uid.StartsWith("u", StringComparison.Ordinal) && long.TryParse(uid.Substring(1), out long id) ? Facts(id) : null;

        private static JObject Req(string json) => JObject.Parse(json);

        private static IdentityLookups Lookups(Func<bool> collaborative) =>
            new IdentityLookups { ById = Facts, ByUniqueId = ByUid, IsCollaborative = collaborative };

        private static CommandResult Early(string json, string tool, Func<bool> collaborative)
        {
            IdentityBinding.Reset();
            return IdentityPrepassCore.Early(Req(json), tool, () => Lookups(collaborative));
        }

        private static string RepoRoot()
        {
            DirectoryInfo d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, "AGENTS.md"))) d = d.Parent;
            Assert.NotNull(d);
            return d.FullName;
        }

        private const string Weak = "{\"target_id\":7,\"expected_category\":\"Walls\"}";
        private const string WeakForeign = "{\"target_id\":150,\"expected_category\":\"Walls\"}";
        private const string Tool = "horizun_write_params_verified";

        // ------------------------------------------------------------------ the collaborative rule

        [Theory]
        [InlineData(Weak)]          // our own element
        [InlineData(WeakForeign)]   // somebody else's
        public void A_category_only_identity_is_refused_in_a_collaborative_document_whoever_made_the_element(string request)
        {
            CommandResult refused = Early(request, Tool, () => true);
            Assert.NotNull(refused);
            Assert.False(refused.Success);
            Assert.Contains("entrada_invalida", refused.Detail.ToString());
            Assert.Contains("collaborative", refused.Error);
            Assert.Contains("unique_id", refused.Error);
            Assert.Contains("Nothing was written", refused.Error);
            Assert.False((bool)refused.Detail["write_started"]);
        }

        [Fact]
        public void A_unique_id_proceeds_in_a_collaborative_document()
        {
            Assert.Null(Early("{\"target_id\":7,\"unique_id\":\"u7\",\"expected_category\":\"Walls\"}", Tool, () => true));
            Assert.Equal("unique_id", (string)IdentityBinding.Notice()["identity_strength"]);
            IdentityBinding.Reset();
        }

        [Fact]
        public void In_a_non_collaborative_document_our_element_proceeds_with_the_warning_and_the_others_do_not()
        {
            Assert.Null(Early(Weak, Tool, () => false));
            Assert.Equal("categoria", (string)IdentityBinding.Notice()["identity_strength"]);
            IdentityBinding.Reset();
            CommandResult foreign = Early(WeakForeign, Tool, () => false);
            Assert.NotNull(foreign);
            Assert.Contains("ownership mark", foreign.Error);
            CommandResult deleting = Early("{\"ids\":[7],\"expected_category\":\"Walls\"}", "horizun_delete_verified", () => false);
            Assert.NotNull(deleting);
            Assert.Contains("deletes", deleting.Error);
        }

        [Fact]
        public void When_it_cannot_be_read_whether_the_document_is_collaborative_it_counts_as_collaborative()
        {
            Assert.NotNull(Early(Weak, Tool, null));                                                  // not supplied
            Assert.NotNull(Early(Weak, Tool, () => throw new InvalidOperationException("IsWorkshared failed")));
            Assert.Null(Early("{\"target_id\":7,\"unique_id\":\"u7\"}", Tool, null));                  // a unique_id never needs to know
            IdentityBinding.Reset();
        }

        [Fact]
        public void The_state_of_the_document_is_asked_once_and_only_when_a_category_only_identity_needs_it()
        {
            int asked = 0;
            Func<bool> probe = () => { asked++; return false; };
            Assert.Null(Early("{\"target_id\":7,\"unique_id\":\"u7\"}", Tool, probe));
            Assert.Equal(0, asked);
            Assert.Null(Early("{\"writes\":[{\"target_id\":7,\"expected_category\":\"Walls\"},{\"target_id\":8,\"expected_category\":\"Walls\"}]}", Tool, probe));
            Assert.Equal(1, asked);
            IdentityBinding.Reset();
        }

        [Fact]
        public void The_collaborative_flag_is_read_by_a_rule_that_fails_closed()
        {
            Assert.False(IdentityPrepassCore.CollaborativeOrUnknown(() => false));
            Assert.True(IdentityPrepassCore.CollaborativeOrUnknown(() => true));
            Assert.True(IdentityPrepassCore.CollaborativeOrUnknown(() => throw new InvalidOperationException()));
            Assert.True(IdentityPrepassCore.CollaborativeOrUnknown(null));
        }

        // ------------------------------------------------------------------ the wording of the refusal, tied to the documents

        [Fact]
        public void The_refusal_is_the_template_filled_in_and_the_documents_repeat_it_word_for_word()
        {
            string text = Early(WeakForeign, Tool, () => false).Error;
            string expected = ElementIdentityGuard.WeakRefusalTemplate
                .Replace("{why}", ElementIdentityGuard.WhyNotOurs.Replace("{id}", "150")).Replace("{id_key}", "target_id");
            Assert.Contains(expected, text);
            string collab = Early(Weak, Tool, () => true).Error;
            Assert.Contains(ElementIdentityGuard.WeakRefusalTemplate.Replace("{why}", ElementIdentityGuard.WhyCollaborative).Replace("{id_key}", "target_id"), collab);
            string deleting = Early("{\"ids\":[7],\"expected_category\":\"Walls\"}", "horizun_delete_verified", () => false).Error;
            Assert.Contains(ElementIdentityGuard.WeakRefusalTemplate.Replace("{why}", ElementIdentityGuard.WhyDeletes).Replace("{id_key}", "ids"), deleting);

            string agents = File.ReadAllText(Path.Combine(RepoRoot(), "AGENTS.md"));
            string guard = File.ReadAllText(Path.Combine(RepoRoot(), "docs", "IDENTITY-GUARD.md"));
            foreach (string piece in new[] { ElementIdentityGuard.WeakRefusalTemplate, ElementIdentityGuard.WhyDeletes, ElementIdentityGuard.WhyCollaborative, ElementIdentityGuard.WhyNotOurs })
            {
                Assert.Equal(2, Regex.Matches(agents, Regex.Escape(piece)).Count);   // the English section and the Spanish one
                Assert.Contains(piece, guard);
            }
            foreach (string where in new[] { "horizun_selection_context operation=validate_ids", "horizun_query_model", "horizun_list_elements" })
                Assert.Contains(where, ElementIdentityGuard.WeakRefusalTemplate);
        }

        // ------------------------------------------------------------------ M-B: the mark carries the user

        [Theory]
        // marked, markSession, currentSession, createdThisSession, markUser, currentUser, trusted
        [InlineData(false, "s0", "s1", false, "user-a", "user-a", false)]   // no mark
        [InlineData(true, "s0", "s1", false, "user-a", "user-a", true)]     // an earlier session of the same user
        [InlineData(true, "s0", "s1", false, "user-b", "user-a", false)]   // another user's mark: somebody else's
        [InlineData(true, "s0", "s1", false, "USER-A", "user-a", true)]     // user names compare without case
        [InlineData(true, "s0", "s1", false, null, "user-a", false)]     // an old mark without the field: fails closed
        [InlineData(true, "s0", "s1", false, "", "user-a", false)]
        [InlineData(true, "s0", "s1", false, "user-a", null, false)]     // this user cannot be established: nothing is believed
        [InlineData(true, "s1", "s1", true, "user-a", "user-a", true)]      // made by a write of this session the ledger recorded
        [InlineData(true, "s1", "s1", false, "user-a", "user-a", false)]    // this session's id but no write of this session made it: a copy
        [InlineData(true, null, "s1", false, "user-a", "user-a", true)]     // session field unreadable but the user is ours
        [InlineData(true, "s1", "s1", true, "user-b", "user-a", false)]    // the ledger does not rescue another user's mark
        public void The_mark_is_believed_only_when_it_is_ours(bool marked, string markSession, string current, bool created, string markUser, string currentUser, bool trusted)
        {
            Assert.Equal(trusted, HrzOwnershipRules.TrustMark(marked, markSession, current, created, markUser, currentUser));
        }

        [Theory]
        [InlineData("user-a-account", "winuser", "user-a-account")]
        [InlineData("  ", "winuser", "os:winuser")]
        [InlineData(null, "winuser", "os:winuser")]
        [InlineData(null, "", null)]
        public void The_user_of_a_mark_is_the_revit_user_or_else_the_windows_user(string revit, string os, string expected)
        {
            Assert.Equal(expected, HrzOwnershipRules.UserIdentity(revit, os));
        }

        // ------------------------------------------------------------------ M-A: the ledger of creations keeps UniqueIds

        [Fact]
        public void The_ledger_knows_what_a_write_of_this_session_created_by_unique_id_and_per_document()
        {
            SessionCreationLedger.ResetForTests();
            SessionCreationLedger.Record("path:a", new[] { "uid-1", "uid-2" });
            Assert.True(SessionCreationLedger.Created("path:a", "uid-1"));
            Assert.True(SessionCreationLedger.Created("path:a", "uid-2"));
            Assert.False(SessionCreationLedger.Created("path:a", "uid-3"));
            Assert.False(SessionCreationLedger.Created("path:b", "uid-1"));
            Assert.False(SessionCreationLedger.Created("path:a", null));
            SessionCreationLedger.ResetForTests();
        }

        [Fact]
        public void The_ledger_keeps_the_last_500_writes_and_the_older_ones_fail_closed()
        {
            SessionCreationLedger.ResetForTests();
            Assert.Equal(500, SessionCreationLedger.WriteCap);
            for (int i = 0; i < 501; i++) SessionCreationLedger.Record("path:a", new[] { "uid-" + i });
            Assert.False(SessionCreationLedger.Created("path:a", "uid-0"));     // beyond the cap: refused, the caller sends the unique_id
            Assert.True(SessionCreationLedger.Created("path:a", "uid-1"));
            Assert.True(SessionCreationLedger.Created("path:a", "uid-500"));
            SessionCreationLedger.ResetForTests();
        }

        [Fact]
        public void Save_as_and_the_first_save_do_not_turn_what_is_ours_into_somebody_elses()
        {
            SessionCreationLedger.ResetForTests();
            const string unsaved = "title:Project1#12345";
            SessionCreationLedger.Record(unsaved, new[] { "uid-9" });
            SessionCreationLedger.Rename(unsaved, "path:c:/models/first.rvt");                // the first save gives the document a path
            Assert.True(SessionCreationLedger.Created("path:c:/models/first.rvt", "uid-9"));
            Assert.False(SessionCreationLedger.Created(unsaved, "uid-9"));
            SessionCreationLedger.Record("path:c:/models/first.rvt", new[] { "uid-10" });
            SessionCreationLedger.Rename("path:c:/models/first.rvt", "path:c:/models/second.rvt");   // Save As
            Assert.True(SessionCreationLedger.Created("path:c:/models/second.rvt", "uid-9"));
            Assert.True(SessionCreationLedger.Created("path:c:/models/second.rvt", "uid-10"));
            // The mark composed with the ledger: still ours after the rename.
            Assert.True(HrzOwnershipRules.Owned("path:c:/models/second.rvt", "uid-9", true, "s1", "user-a", "s1", "user-a"));
            SessionCreationLedger.ResetForTests();
        }

        [Fact]
        public void An_element_that_took_over_an_id_is_ours_only_if_its_unique_id_was_created_by_us()
        {
            SessionCreationLedger.ResetForTests();
            SessionCreationLedger.Record("path:m", new[] { "uid-A" });
            // uid-A was created by us. A hand-made copy of it carries our mark of THIS session under another unique_id.
            Assert.True(HrzOwnershipRules.Owned("path:m", "uid-A", true, "s1", "user-a", "s1", "user-a"));
            Assert.False(HrzOwnershipRules.Owned("path:m", "uid-copy", true, "s1", "user-a", "s1", "user-a"));
            Assert.False(HrzOwnershipRules.Owned("path:m", "uid-A", true, "s1", "user-b", "s1", "user-a"));     // another user's mark
            Assert.False(HrzOwnershipRules.Owned("path:m", "uid-A", true, "s0", null, "s1", "user-a"));       // a mark without the user (older schema)
            SessionCreationLedger.ResetForTests();
        }

        // ------------------------------------------------------------------ M-A: the "ids may have changed" notice

        private const string Doc = "path:c:/models/colon.rvt";
        private static readonly DateTime T = new DateTime(2026, 11, 1, 10, 0, 0, DateTimeKind.Utc);

        private static JObject Rows(params (long id, string uid)[] rows) =>
            new JObject { ["rows"] = new JArray(rows.Select(r => new JObject { ["element_id"] = r.id, ["unique_id"] = r.uid })) };

        private static JObject Validated(string status, long id, string uid, string foundUid = null) =>
            new JObject
            {
                ["results"] = new JArray(new JObject
                {
                    ["status"] = status,
                    ["expected"] = new JObject { ["id"] = id, ["unique_id"] = uid },
                    ["found"] = foundUid == null ? null : new JObject { ["id"] = id, ["unique_id"] = foundUid }
                })
            };

        [Fact]
        public void A_read_of_something_else_does_not_clear_the_notice()
        {
            IdFreshnessRules.ResetForTests();
            IdFreshnessRules.NoteReply(Doc, T, "horizun_query_model", null, Rows((5001, "uidA"), (5002, "uidB"), (5003, "uidC")));
            IdFreshnessRules.NoteSynchronized(Doc, T);
            Assert.NotNull(IdFreshnessRules.Block(Doc));
            IdFreshnessRules.NoteReply(Doc, T, "horizun_query_model", null, Rows((30, "uidL1"), (31, "uidL2")));   // the levels
            Assert.NotNull(IdFreshnessRules.Block(Doc));
            IdFreshnessRules.ResetForTests();
        }

        [Fact]
        public void A_read_that_returns_the_same_ids_with_their_unique_ids_clears_it_only_when_all_of_them_are_back()
        {
            IdFreshnessRules.ResetForTests();
            IdFreshnessRules.NoteReply(Doc, T, "horizun_list_elements", null, Rows((5001, "uidA"), (5002, "uidB"), (5003, "uidC")));
            IdFreshnessRules.NoteSynchronized(Doc, T);
            IdFreshnessRules.NoteReply(Doc, T, "horizun_list_elements", null, Rows((5001, "uidA")));
            Assert.NotNull(IdFreshnessRules.Block(Doc));                                   // two of three are still unverified
            IdFreshnessRules.NoteReply(Doc, T, "horizun_list_elements", null, Rows((5002, "uidB"), (5003, "uidC"), (9, "x")));
            Assert.Null(IdFreshnessRules.Block(Doc));
            IdFreshnessRules.ResetForTests();
        }

        [Fact]
        public void A_validation_of_the_ids_that_were_held_clears_it_and_one_that_proves_nothing_does_not()
        {
            IdFreshnessRules.ResetForTests();
            IdFreshnessRules.NoteReply(Doc, T, "horizun_query_model", null, Rows((5001, "uidA"), (5002, "uidB")));
            IdFreshnessRules.NoteSynchronized(Doc, T);
            IdFreshnessRules.NoteReply(Doc, T, "horizun_selection_context", "validate_ids", Validated("sin_expectativa", 5001, null));
            Assert.NotNull(IdFreshnessRules.Block(Doc));
            IdFreshnessRules.NoteReply(Doc, T, "horizun_selection_context", "validate_ids", Validated("coincide", 5001, "uidA", "uidA"));
            Assert.NotNull(IdFreshnessRules.Block(Doc));                                   // 5002 not yet
            IdFreshnessRules.NoteReply(Doc, T, "horizun_selection_context", "validate_ids", Validated("cambio", 5002, "uidB", "uidZ"));   // the caller now knows it moved
            Assert.Null(IdFreshnessRules.Block(Doc));
            IdFreshnessRules.ResetForTests();
        }

        [Fact]
        public void When_no_ids_were_tracked_one_read_that_returns_ids_is_still_needed()
        {
            IdFreshnessRules.ResetForTests();
            IdFreshnessRules.NoteSynchronized(Doc, T);
            Assert.NotNull(IdFreshnessRules.Block(Doc));
            IdFreshnessRules.NoteReply(Doc, T, "horizun_query_model", null, new JObject { ["rows"] = new JArray() });
            Assert.NotNull(IdFreshnessRules.Block(Doc));
            IdFreshnessRules.NoteReply(Doc, T, "horizun_query_model", null, Rows((30, "uidL1")));
            Assert.Null(IdFreshnessRules.Block(Doc));
            IdFreshnessRules.ResetForTests();
        }

        [Fact]
        public void A_second_synchronize_raises_it_again_for_everything_held_and_the_state_is_per_document()
        {
            IdFreshnessRules.ResetForTests();
            IdFreshnessRules.NoteReply(Doc, T, "horizun_query_model", null, Rows((1, "a")));
            IdFreshnessRules.NoteSynchronized(Doc, T);
            IdFreshnessRules.NoteReply(Doc, T, "horizun_query_model", null, Rows((1, "a")));
            Assert.Null(IdFreshnessRules.Block(Doc));
            IdFreshnessRules.NoteSynchronized(Doc, T);
            Assert.NotNull(IdFreshnessRules.Block(Doc));
            Assert.Null(IdFreshnessRules.Block("path:c:/models/other.rvt"));
            IdFreshnessRules.ResetForTests();
        }

        [Fact]
        public void Reload_Latest_raises_the_notice_like_a_synchronize_and_the_notice_follows_a_save_as()
        {
            IdFreshnessRules.ResetForTests();
            IdFreshnessRules.NoteReply(Doc, T, "horizun_query_model", null, Rows((1, "a")));
            IdFreshnessRules.NoteReloaded(Doc, T);
            Assert.NotNull(IdFreshnessRules.Block(Doc));
            IdFreshnessRules.Rename(Doc, "path:c:/models/renamed.rvt");
            Assert.Null(IdFreshnessRules.Block(Doc));
            Assert.NotNull(IdFreshnessRules.Block("path:c:/models/renamed.rvt"));
            IdFreshnessRules.ResetForTests();
        }

        // ------------------------------------------------------------------ the field case of Colon T3, end to end without Revit

        // Three text notes made by us (marked). A synchronize renumbers them (5001..5003 -> 9101..9103). The old ids now name OTHER notes.
        // A read of something else follows, and then a write with the old id and only expected_category "Text Notes".
        private static ElementIdentityFacts Note(long id, string uid, bool owned) => new ElementIdentityFacts
        {
            Id = id, UniqueId = uid, Owned = owned, Category = "Text Notes", CategoryBuiltIn = "OST_TextNotes", Type = "Text 3mm", Family = "Text"
        };

        private static IdentityLookups ColonModel(bool collaborative, Func<string, bool> ownedUid)
        {
            // After the synchronize: uidA..C live at 9101..9103; ids 5001..5003 name other notes uidD..F (a person's copies of ours: the mark came with them).
            var byId = new Dictionary<long, ElementIdentityFacts>
            {
                [9101] = Note(9101, "uidA", ownedUid("uidA")), [9102] = Note(9102, "uidB", ownedUid("uidB")), [9103] = Note(9103, "uidC", ownedUid("uidC")),
                [5001] = Note(5001, "uidD", ownedUid("uidD")), [5002] = Note(5002, "uidE", ownedUid("uidE")), [5003] = Note(5003, "uidF", ownedUid("uidF"))
            };
            return new IdentityLookups
            {
                ById = id => byId.TryGetValue(id, out ElementIdentityFacts f) ? f : null,
                ByUniqueId = uid => byId.Values.FirstOrDefault(f => f.UniqueId == uid),
                IsCollaborative = () => collaborative
            };
        }

        private static void ReadNotesSynchronizeAndReadLevels()
        {
            IdFreshnessRules.ResetForTests();
            IdFreshnessRules.NoteReply(Doc, T, "horizun_query_model", null, Rows((5001, "uidA"), (5002, "uidB"), (5003, "uidC")));
            IdFreshnessRules.NoteSynchronized(Doc, T);
            IdFreshnessRules.NoteReply(Doc, T, "horizun_query_model", null, Rows((30, "uidL1"), (31, "uidL2")));   // "a read of something else"
        }

        [Fact]
        public void Colon_T3_collaborative_the_write_with_the_old_id_and_only_the_category_is_refused_and_the_notice_stays()
        {
            ReadNotesSynchronizeAndReadLevels();
            Assert.NotNull(IdFreshnessRules.Block(Doc));                                   // the levels did not revalidate the notes
            IdentityLookups model = ColonModel(true, uid => true);                         // worst case: every note carries our mark
            IdentityBinding.Reset();
            CommandResult refused = IdentityPrepassCore.Early(Req("{\"target_id\":5001,\"expected_category\":\"Text Notes\"}"), Tool, () => model);
            Assert.NotNull(refused);                                                       // on ff8cc79e this was written, on the wrong note
            Assert.False(refused.Success);
            Assert.Contains("unique_id", refused.Error);
            // With the unique_id it is the same call that is refused for the right reason: the id no longer names that unique_id.
            CommandResult wrongNote = IdentityPrepassCore.Early(Req("{\"target_id\":5001,\"unique_id\":\"uidA\",\"expected_category\":\"Text Notes\"}"), Tool, () => model);
            Assert.NotNull(wrongNote);
            Assert.Contains("elemento_no_coincide", wrongNote.Error);
            IdentityBinding.Reset();
            IdFreshnessRules.ResetForTests();
        }

        [Fact]
        public void Colon_T3_non_collaborative_with_a_reassigned_id_the_copy_of_our_mark_is_not_ours_and_the_real_note_is()
        {
            ReadNotesSynchronizeAndReadLevels();
            SessionCreationLedger.ResetForTests();
            SessionCreationLedger.Record(Doc, new[] { "uidA", "uidB", "uidC" });
            // uidD..F are copies that carry the mark of THIS session; only uidA..C were created by a write of this session.
            Func<string, bool> owned = uid => HrzOwnershipRules.Owned(Doc, uid, true, "s1", "user-a", "s1", "user-a");
            IdentityLookups model = ColonModel(false, owned);
            IdentityBinding.Reset();
            CommandResult copy = IdentityPrepassCore.Early(Req("{\"target_id\":5001,\"expected_category\":\"Text Notes\"}"), Tool, () => model);
            Assert.NotNull(copy);
            Assert.Contains("ownership mark", copy.Error);
            IdentityBinding.Reset();
            CommandResult real = IdentityPrepassCore.Early(Req("{\"target_id\":9101,\"expected_category\":\"Text Notes\"}"), Tool, () => model);
            Assert.Null(real);                                                              // ours by unique_id, no collaboration: proceeds with the warning
            Assert.Equal("categoria", (string)IdentityBinding.Notice()["identity_strength"]);
            IdentityBinding.Reset();
            // The same note under another user's mark (a colleague's add-in, or a model passed on): not ours.
            Func<string, bool> others = uid => HrzOwnershipRules.Owned(Doc, uid, true, "s0", "user-b", "s1", "user-a");
            CommandResult theirs = IdentityPrepassCore.Early(Req("{\"target_id\":9101,\"expected_category\":\"Text Notes\"}"), Tool, () => ColonModel(false, others));
            Assert.NotNull(theirs);
            IdentityBinding.Reset();
            SessionCreationLedger.ResetForTests();
            IdFreshnessRules.ResetForTests();
        }

        // ------------------------------------------------------------------ m-6: fewer refusals than necessary

        [Fact]
        public void Adding_members_to_a_group_deletes_the_group_and_its_type_not_the_members()
        {
            // identity: the group (group_ids) is ungrouped and redefined, the members (element_ids) are only added.
            string members = "{\"operation\":\"add_members\",\"group_ids\":[7],\"element_ids\":[8],\"identity\":{\"group_ids\":[\"u7\"],\"element_ids\":[{\"expected_category\":\"Walls\"}]}}";
            IdentityBinding.Reset();
            Assert.Null(ElementIdentityGuard.PreCheck(Req(members), "horizun_manage_groups", Facts, ByUid, () => false));
            IdentityBinding.Reset();
            string group = "{\"operation\":\"add_members\",\"group_ids\":[7],\"element_ids\":[8],\"identity\":{\"group_ids\":[{\"expected_category\":\"Walls\"}],\"element_ids\":[\"u8\"]}}";
            JObject error = ElementIdentityGuard.PreCheck(Req(group), "horizun_manage_groups", Facts, ByUid, () => false);
            Assert.NotNull(error);
            Assert.Contains("deletes", (string)error["detail"]);
            Assert.Contains("group_ids", (string)error["detail"]);
            // The other operation of the table still deletes whatever it names.
            JObject type = ElementIdentityGuard.PreCheck(Req("{\"operation\":\"edit_type_members\",\"type_id\":7,\"element_ids\":[8],\"identity\":{\"element_ids\":[{\"expected_category\":\"Walls\"}]}}"),
                "horizun_manage_groups", Facts, ByUid, () => false);
            Assert.NotNull(type);
        }

        [Theory]
        [InlineData("horizun_manage_schedules", "remove_fields", "\"schedule_id\":7")]
        [InlineData("horizun_manage_parameters", "remove_binding", "\"element_ids\":[7]")]
        public void Operations_called_remove_that_do_not_delete_the_element_they_name_do_not_inherit_the_deletion(string tool, string operation, string ids)
        {
            JObject r = Req("{\"operation\":\"" + operation + "\"," + ids + ",\"expected_category\":\"Walls\"}");
            Assert.False(ElementIdentityGuard.IsDeleting(tool, r));
            IdentityBinding.Reset();
            Assert.Null(ElementIdentityGuard.PreCheck(r, tool, Facts, ByUid, () => false));
            IdentityBinding.Reset();
            // ... and a child of such a node does not inherit a deletion either.
            JObject child = Req("{\"operation\":\"" + operation + "\",\"targets\":[{" + ids + ",\"expected_category\":\"Walls\"}]}");
            Assert.Null(ElementIdentityGuard.PreCheck(child, tool, Facts, ByUid, () => false));
            IdentityBinding.Reset();
        }

        [Fact]
        public void Removing_that_does_delete_still_does()
        {
            Assert.True(ElementIdentityGuard.IsDeleting("horizun_manage_groups", Req("{\"operation\":\"remove_members\"}")));
            Assert.True(ElementIdentityGuard.IsDeleting("horizun_manage_parameters", Req("{\"operation\":\"global_delete\"}")));
            Assert.True(ElementIdentityGuard.IsDeleting("horizun_manage_curtain", Req("{\"operation\":\"remove_grid_line\"}")));
        }

        [Fact]
        public void Framing_with_the_curtain_method_stays_a_deletion_because_whether_the_curtain_covers_the_wall_is_decided_by_geometry_after_the_rehearsal()
        {
            // Documented limit: the carrier action comes from the plan (CurtainFramingRoles.CarrierDelete), not from an argument.
            Assert.True(ElementIdentityGuard.IsDeleting("horizun_framing", Req("{\"operation\":\"wall\",\"spec\":{\"wall\":{\"method\":\"curtain\"}}}")));
            Assert.Contains("carrier", File.ReadAllText(Path.Combine(RepoRoot(), "docs", "IDENTITY-GUARD.md")));
        }

        // ------------------------------------------------------------------ m-2 / m-3: the whole coverage in the hash, read-only tables

        [Fact]
        public void Every_table_the_guard_reads_is_in_the_hash_summary()
        {
            string summary = IdentityCoverage.Summary();
            foreach (string label in new[] { "no_subject=", "near_names=", "own_identity_text=", "default_keys=", "deleting_keys=", "not_deleting=" })
                Assert.Contains(label, summary);
            Assert.Contains("uniqueid", summary);
            Assert.Contains("horizun_plan_views", summary);
            Assert.Contains("horizun_manage_groups:add_members", summary.Replace("=", ":"));
        }

        [Fact]
        public void The_default_keys_in_the_hash_are_the_ones_the_guard_fills_in()
        {
            var table = IdentityCoverage.DocumentedDefaultKeys.ToDictionary(k => k.Key, k => k.Value, StringComparer.Ordinal);
            var problems = new List<string>();
            foreach (string tool in ElementIdentityGuard.CoveredTools)
            {
                string expected = "s:" + string.Join(",", ElementIdentityGuard.DocumentedDefaults(tool, false)) + ";l:" + string.Join(",", ElementIdentityGuard.DocumentedDefaults(tool, true));
                string actual = table.TryGetValue(tool, out string row) ? row : "s:;l:";   // a tool with no default is not listed
                if (actual != expected) problems.Add(tool + ": " + actual + " != " + expected);
            }
            foreach (string tool in table.Keys) if (!ElementIdentityGuard.Covers(tool)) problems.Add(tool + ": not a covered tool");
            Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
        }

        [Fact]
        public void The_public_tables_of_the_guard_are_read_only_views()
        {
            foreach (FieldInfo f in typeof(ElementIdentityGuard).GetFields(BindingFlags.Public | BindingFlags.Static).Where(x => !x.IsLiteral))
            {
                Type t = f.FieldType;
                Assert.False(t.IsArray || (t.IsGenericType && (t.GetGenericTypeDefinition() == typeof(HashSet<>) || t.GetGenericTypeDefinition() == typeof(List<>) || t.GetGenericTypeDefinition() == typeof(Dictionary<,>))),
                    f.Name + " is a modifiable " + t.Name);
            }
            foreach (PropertyInfo p in typeof(ElementIdentityGuard).GetProperties(BindingFlags.Public | BindingFlags.Static))
                Assert.False(p.PropertyType.IsArray, p.Name + " returns a modifiable array");
            Assert.Null(typeof(ElementIdentityGuard).GetField("IdentityFields", BindingFlags.Public | BindingFlags.Static));
        }

        // ------------------------------------------------------------------ m-1: every call that deletes, classified one by one

        private static readonly Regex DeleteCall = new Regex(@"\.Delete\(|UngroupMembers\(|\.Disassemble\(", RegexOptions.Compiled);

        /// <summary>The code lines (comments skipped) that delete, ungroup or disassemble, other than File / Directory / FileInfo deletions.</summary>
        internal static List<string> DeleteLines(string source)
        {
            var found = new List<string>();
            foreach (string raw in source.Split('\n'))
            {
                string line = raw.Trim();
                if (line.StartsWith("//", StringComparison.Ordinal) || line.StartsWith("*", StringComparison.Ordinal) || line.StartsWith("///", StringComparison.Ordinal)) continue;
                int slashes = line.IndexOf("//", StringComparison.Ordinal);
                string code = slashes >= 0 ? line.Substring(0, slashes) : line;
                if (!DeleteCall.IsMatch(code)) continue;
                if (code.Contains("File.Delete(") || code.Contains("Directory.Delete(") || code.Contains("backups[i].Delete(")) continue;
                if (code.Contains("Discovery.Delete(")) continue;   // the add-in's own pipe discovery file, App.OnShutdown
                found.Add(line);
            }
            return found;
        }

        [Fact]
        public void The_scanner_sees_a_variable_called_anything_and_ungrouping_and_disassembling()
        {
            Assert.Single(DeleteLines("  if (!vss.Delete()) throw;"));
            Assert.Single(DeleteLines("p.Groups[0].UngroupMembers();"));
            Assert.Single(DeleteLines("((AssemblyInstance)x).Disassemble();"));
            Assert.Empty(DeleteLines("// doc.Delete(id) in a comment"));
            Assert.Empty(DeleteLines("File.Delete(path);"));
        }

        // file -> one entry per call, in order: (a fragment of the line, "tool|node" when it deletes an element the caller NAMED, or "own|why" otherwise)
        private static KeyValuePair<string, string> Call(string fragment, string tool, string node) => new KeyValuePair<string, string>(fragment, tool + "|" + node);
        private static KeyValuePair<string, string> Own(string fragment, string why, string operation = null) => new KeyValuePair<string, string>(fragment, "own|" + why + (operation == null ? "" : "|" + operation));

        internal static readonly Dictionary<string, KeyValuePair<string, string>[]> Sweep = new Dictionary<string, KeyValuePair<string, string>[]>(StringComparer.Ordinal)
        {
            ["Commands/AnnotateCommand.Apply.cs"] = new[] { Own("doc.Delete(p.Created)", "a row that did not verify, before the commit") },
            ["Commands/CreateElementsCommand.cs"] = new[]
            {
                Own("doc.Delete(placed.Id)", "the temporary reflected copy"), Own("doc.Delete(placed.Id)", "the temporary reflected copy"),
                Own("doc.Delete(placed.Id)", "the temporary reflected copy"), Own("doc.Delete(middle.Id)", "the removable middle piece of an inline break; the named pipe stays"),
                Own("doc.Delete(temporary.Id)", "a temporary")
            },
            ["Commands/DeleteCommand.cs"] = new[] { Call("doc.Delete(t.Id)", "horizun_delete_verified", "{}") },
            ["Commands/EnergyModelBuild.cs"] = new[] { Own("doc.Delete(current.Id)", "the energy model the command itself created; not a covered tool") },
            ["Commands/FixPlanimetryCommand.cs"] = new[] { Own("doc.Delete(newDimensionIds)", "dimensions Revit added as a side effect") },
            ["Commands/FramingApply.cs"] = new[]
            {
                Call("doc.Delete(toRemove", "horizun_framing", "{\"operation\":\"remove\"}"), Call("doc.Delete(toRemove", "horizun_framing", "{\"operation\":\"remove\"}")
            },
            ["Commands/FramingCurtain.cs"] = new[] { Call("doc.Delete(carrier.Id)", "horizun_framing", "{\"operation\":\"wall\",\"spec\":{\"wall\":{\"method\":\"curtain\"}}}") },
            ["Commands/FramingCurtainRemove.cs"] = new[] { Call("doc.Delete(carrier.Id)", "horizun_framing", "{\"operation\":\"remove\"}") },
            ["Commands/ManageAssembliesPartsCommand.cs"] = new[]
            {
                Call("d.Delete(makers)", "horizun_manage_assemblies_parts", "{\"operation\":\"dissolve_parts\"}"),
                Call(".Disassemble()", "horizun_manage_assemblies_parts", "{\"operation\":\"disassemble\"}")
            },
            ["Commands/ManageCurtainCommand.cs"] = new[]
            {
                Call("d.Delete(Rid.Make(lineId))", "horizun_manage_curtain", "{\"operation\":\"remove_grid_line\"}"),
                Call("d.Delete(removed", "horizun_manage_curtain", "{\"operation\":\"set_mullions\",\"mode\":\"remove\"}")
            },
            ["Commands/ManageGroupsCommand.cs"] = new[]
            {
                Call("p.Groups[0].UngroupMembers()", "horizun_manage_groups", "{\"operation\":\"add_members\"}"),
                Call("doc.Delete(oldType)", "horizun_manage_groups", "{\"operation\":\"add_members\"}"),
                Own("doc.Delete(tag.TagId)", "the member tags the command recreates after retyping the group; the caller named the group, not the tags", "change_group_type"),
                Call("UngroupMembers()", "horizun_manage_groups", "{\"operation\":\"ungroup\"}")
            },
            ["Commands/ManageGroupsCommand.EditType.cs"] = new[]
            {
                Call("UngroupMembers()", "horizun_manage_groups", "{\"operation\":\"edit_type_members\"}"),
                Call("doc.Delete(p.TypeId)", "horizun_manage_groups", "{\"operation\":\"edit_type_members\"}")
            },
            ["Commands/ManageParametersCommand.Globals.cs"] = new[] { Call("d.Delete(id)", "horizun_manage_parameters", "{\"operation\":\"global_delete\"}") },
            ["Commands/ManageViewsControl.cs"] = new[] { Call("vss.Delete()", "horizun_manage_views", "{\"operation\":\"sheet_set_delete\"}") },
            ["Commands/PackSheetsCommand.cs"] = new[] { Own("doc.Delete(placement.Id)", "the provisional placement used to measure") },
            ["Commands/PlanimetryOpsCommand.cs"] = new[] { Own("d.Delete(inLegend", "the contents of the legend the command duplicated") },
            ["Commands/SplitByLevelCommand.Apply.cs"] = new[]
            {
                Call("doc.Delete(source.Id)", "horizun_split_by_level", "{}"), Call("doc.Delete(source.Id)", "horizun_split_by_level", "{}"), Call("doc.Delete(source.Id)", "horizun_split_by_level", "{}")
            },
            ["Commands/SplitByLevelCommand.Openings.cs"] = new[] { Call("doc.Delete(Rid.Make(f.Id))", "horizun_split_by_level", "{}"), Call("doc.Delete(id)", "horizun_split_by_level", "{}") },
            ["Commands/TransformElementsCommand.EditSketch.cs"] = new[] { Own("doc.Delete(byStep", "the sketch curves of the element being redrawn; the element stays") },
            ["Commands/UndoCapture.cs"] = new[] { Own("doc.Delete(ids)", "horizun_undo, not a covered tool") },
            ["Commands/VerifyChangesWhatIf.cs"] = new[] { Own("doc.Delete(targets)", "a what-if deletion inside a rolled-back transaction") },
            ["Commands/ViewVisibilityLinks.cs"] = new[] { Own("d.Delete(tempId)", "a temporary override element") },
            ["Commands/ViewVisibilityCopyGraphics.cs"] = new[] { Own("d.Delete(template.Id)", "the temporary view template created in this transaction, never a caller-supplied template") },
            ["Commands/WallFinishCommand.cs"] = new[] { Call("d.Delete(ids.Select(Rid.Make).ToList())", "horizun_wall_finish", "{\"original_wall\":\"delete_if_unhosted\"}") },
            ["Commands/WallSplitTypes.cs"] = new[] { Own("doc.Delete(duplicate.Id)", "a duplicate type inside a rolled-back SubTransaction") },
        };
    }
}
