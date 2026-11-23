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
    // R2V - the findings of the independent review of R2U. B-1 (the refusal message led to a unique_id of the wrong element), M-1 (Python and
    // copies are not ours), M-2 (every ownership gate asks the same question as the guard), M-3 (writes are tracked by the sync notice),
    // M-4 (who a user is), m-a..m-h.
    public class ElementIdentityGuardR2VTests
    {
        private static string RepoRoot()
        {
            DirectoryInfo d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, "AGENTS.md"))) d = d.Parent;
            Assert.NotNull(d);
            return d.FullName;
        }

        private const string Doc = "path:c:/models/colon-v.rvt";
        private static readonly DateTime T = new DateTime(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);
        private const string Tool = "horizun_write_params_verified";

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

        // ------------------------------------------------------------------ B-1: the refusal must never lead to a unique_id of another element

        private static ElementIdentityFacts Note(long id, string uid) => new ElementIdentityFacts
        {
            Id = id, UniqueId = uid, Owned = true, Category = "Text Notes", CategoryBuiltIn = "OST_TextNotes", Type = "Text 3mm", Family = "Text"
        };

        // After the synchronize: uidA..C live at 9101..9103; the ids 5001..5003 the agent holds now name OTHER notes uidD..F.
        private static Dictionary<long, ElementIdentityFacts> ColonAfterSync() => new Dictionary<long, ElementIdentityFacts>
        {
            [9101] = Note(9101, "uidA"), [9102] = Note(9102, "uidB"), [9103] = Note(9103, "uidC"),
            [5001] = Note(5001, "uidD"), [5002] = Note(5002, "uidE"), [5003] = Note(5003, "uidF")
        };

        [Theory]
        [InlineData("horizun_write_params_verified", "{\"target_id\":5001,\"unique_id\":\"uidA\"}")]
        [InlineData("horizun_set_keynote", "{\"element_ids\":[5001],\"unique_ids\":[\"uidA\"]}")]
        [InlineData("horizun_execute_plan", "{\"actions\":[{\"key\":\"a\",\"tool\":\"horizun_write_params_verified\",\"arguments\":{\"target_id\":5001,\"unique_id\":\"uidA\"}}]}")]
        [InlineData("horizun_execute_plan", "{\"actions\":[{\"key\":\"a\",\"tool\":\"horizun_execute_plan\",\"arguments\":{\"actions\":[{\"key\":\"b\",\"tool\":\"horizun_set_keynote\",\"arguments\":{\"element_ids\":[5001],\"unique_ids\":[\"uidA\"]}}]}}]}")]
        public void Reused_id_is_refused_before_the_command_for_direct_collections_and_plan_children(string tool, string json)
        {
            var model = ColonAfterSync();
            var lookups = new IdentityLookups { ById = id => model.TryGetValue(id, out var f) ? f : null,
                ByUniqueId = uid => model.Values.FirstOrDefault(f => f.UniqueId == uid), IsCollaborative = () => true };
            int writes = 0;
            var result = IdentityPrepassCore.Execute(tool, json, r => IdentityPrepassCore.Early(r, tool, () => lookups),
                _ => { writes++; return CommandResult.Ok(new JObject()); });
            Assert.False(result.Success);
            Assert.Equal(0, writes);
        }

        [Fact]
        public void Linked_element_array_entry_is_not_checked_against_the_host()
        {
            var request = JObject.Parse("{\"link_instance_id\":9101,\"element_ids\":[5001],\"identity\":{\"element_ids[0]\":\"uidD\"}}");
            var model = ColonAfterSync();
            int writes = 0;
            var result = IdentityPrepassCore.Execute(Tool, request.ToString(),
                r => IdentityPrepassCore.Early(r, Tool, () => new IdentityLookups {
                    ById = id => model.TryGetValue(id, out var f) ? f : null,
                    ByUniqueId = uid => model.Values.FirstOrDefault(f => f.UniqueId == uid), IsCollaborative = () => true }),
                text => { writes++; return CommandResult.Ok(new JObject()); });
            Assert.False(result.Success);
            Assert.Equal(0, writes);
        }

        [Fact]
        public void Following_the_refusal_message_literally_never_writes_on_the_wrong_note()
        {
            // The agent holds (5001 -> uidA). The refusal asks it for a unique_id; the old message said to ask validate_ids and read found.unique_id.
            Dictionary<long, ElementIdentityFacts> model = ColonAfterSync();
            Func<long, ElementIdentityFacts> byId = id => model.TryGetValue(id, out ElementIdentityFacts f) ? f : null;
            Func<string, ElementIdentityFacts> byUid = uid => model.Values.FirstOrDefault(f => f.UniqueId == uid);
            IdentityLookups lookups = new IdentityLookups { ById = byId, ByUniqueId = byUid, IsCollaborative = () => true };

            IdentityBinding.Reset();
            CommandResult refused = IdentityPrepassCore.Early(JObject.Parse("{\"target_id\":5001,\"expected_category\":\"Text Notes\"}"), Tool, () => lookups);
            Assert.NotNull(refused);

            // Step 2, as the message says: validate_ids with {id, expected_category}.
            JObject validation = ElementIdentityGuard.Validate(JArray.Parse("[{\"id\":5001,\"expected_category\":\"Text Notes\"}]"), byId, byUid);
            JToken row = validation["results"][0];

            // Step 3: take what the answer calls the unique_id and send it back.
            string taken = (string)row["found"]?["unique_id"];
            var resend = new JObject { ["target_id"] = 5001, ["expected_category"] = "Text Notes" };
            if (taken != null) resend["unique_id"] = taken;
            IdentityBinding.Reset();
            CommandResult second = IdentityPrepassCore.Early(resend, Tool, () => lookups);

            // The final result must not be a write on uidD under the strength "unique_id".
            Assert.True(second != null, "the resend with the unique_id the validation handed out passed the guard: it would write on the note " + taken + " while the agent meant uidA");
            IdentityBinding.Reset();
        }

        [Fact]
        public void A_validation_of_an_id_sent_without_its_unique_id_does_not_hand_out_an_identity_and_says_so()
        {
            Dictionary<long, ElementIdentityFacts> model = ColonAfterSync();
            Func<long, ElementIdentityFacts> byId = id => model.TryGetValue(id, out ElementIdentityFacts f) ? f : null;
            Func<string, ElementIdentityFacts> byUid = uid => model.Values.FirstOrDefault(f => f.UniqueId == uid);

            foreach (string check in new[]
            {
                "{\"id\":5001,\"expected_category\":\"Text Notes\"}",                       // coincide_categoria
                "{\"id\":5001}",                                                            // sin_expectativa
                "{\"id\":5001,\"expected_category\":\"Walls\"}"                             // cambio (category moved)
            })
            {
                JToken row = ElementIdentityGuard.Validate(JArray.Parse("[" + check + "]"), byId, byUid)["results"][0];
                Assert.Null(row["found"]["unique_id"]);                                      // never offered as "the" identity
                Assert.Equal("uidD", (string)row["found"]["holder_unique_id"]);              // named for what it is: the element that holds the id today
                Assert.False((bool)row["identifies"]);
                Assert.Contains("does not prove", (string)row["warning"]);
            }
            // With the unique_id the caller read, the answer is an identity.
            JToken sure = ElementIdentityGuard.Validate(JArray.Parse("[{\"id\":9101,\"unique_id\":\"uidA\"}]"), byId, byUid)["results"][0];
            Assert.Equal("coincide", (string)sure["status"]);
            Assert.Equal("uidA", (string)sure["found"]["unique_id"]);
            JToken moved = ElementIdentityGuard.Validate(JArray.Parse("[{\"id\":5001,\"unique_id\":\"uidA\"}]"), byId, byUid)["results"][0];
            Assert.Equal("cambio", (string)moved["status"]);
            Assert.Equal(9101, (long)moved["current_id"]);                                  // where uidA really lives
            // R2W B-2: the occupant of 5001 today is uidD. Its unique_id must not be offered as "found.unique_id" (an identity).
            Assert.Null(moved["found"]["unique_id"]);
            Assert.Equal("uidD", (string)moved["found"]["holder_unique_id"]);
            Assert.False((bool)moved["identifies"]);
        }

        [Fact]
        public void The_refusal_names_where_a_unique_id_really_comes_from_and_not_the_lookup_by_an_old_id()
        {
            string template = ElementIdentityGuard.WeakRefusalTemplate;
            Assert.DoesNotContain("all three return", template);
            Assert.Contains("response_mode full or compact, never summary", template);
            Assert.Contains("horizun_list_elements", template);
            Assert.Contains("before a synchronize", template);
            Assert.Contains("holder_unique_id", template);
            Assert.Contains("the read that gave you that id", template);
        }

        [Fact]
        public void The_validate_ids_note_says_the_same_as_the_refusal()
        {
            // R2W: from text to behavior. The note is a constant of the guard; it is held against what Validate really answers and against the refusal.
            string note = ElementIdentityGuard.ValidateIdsNote;
            Dictionary<long, ElementIdentityFacts> model = ColonAfterSync();
            Func<long, ElementIdentityFacts> byId = id => model.TryGetValue(id, out ElementIdentityFacts f) ? f : null;
            Func<string, ElementIdentityFacts> byUid = uid => model.Values.FirstOrDefault(f => f.UniqueId == uid);
            JToken idOnly = ElementIdentityGuard.Validate(JArray.Parse("[{\"id\":5001,\"expected_category\":\"Text Notes\"}]"), byId, byUid)["results"][0];
            JToken reassigned = ElementIdentityGuard.Validate(JArray.Parse("[{\"id\":5001,\"unique_id\":\"uidA\"}]"), byId, byUid)["results"][0];

            // Every field the note names as part of an answer is in the answers it describes (both the id-only and the reassigned one).
            foreach (JToken row in new[] { idOnly, reassigned })
            {
                Assert.Contains("holder_unique_id", note);
                Assert.NotNull(row["found"]["holder_unique_id"]);
                Assert.Contains("identifies", note);
                Assert.False((bool)row["identifies"]);
                Assert.Contains("warning", note);
                Assert.NotNull(row["warning"]);
                Assert.Null(row["found"]["unique_id"]);
            }
            Assert.Contains("id_reasignado", note);
            Assert.StartsWith("id_reasignado", (string)reassigned["reason"]);
            Assert.Contains("never holder_unique_id", note);

            // And it agrees with the refusal about where a unique_id comes from.
            string refusal = ElementIdentityGuard.WeakRefusalTemplate;
            Assert.Contains("holder_unique_id", refusal);
            Assert.Contains("horizun_query_model", refusal);
            Assert.Contains("list_elements", refusal);
            Assert.Contains("query_model", note);
            Assert.Contains("list_elements", note);
        }

        [Fact]
        public void The_select_context_command_hands_out_the_note_of_the_guard_and_keeps_no_copy()
        {
            // The only thing left as text: the command must use the constant that the test above holds against the behavior.
            string source = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Horizun.Revit", "Commands", "SelectionContextCommand.cs"));
            Assert.Contains("result[\"note\"] = ElementIdentityGuard.ValidateIdsNote;", source);
            Assert.DoesNotContain("holder_unique_id", source);
        }

        // ------------------------------------------------------------------ M-1: what Python creates, and what is copied, is not ours

        [Theory]
        [InlineData("horizun_execute_python", false)]
        [InlineData("horizun_document_session", false)]
        [InlineData("horizun_undo", false)]
        [InlineData("horizun_create_elements", true)]
        [InlineData("horizun_manage_views", true)]
        public void Only_the_typed_writers_that_make_their_own_elements_create_ours(string tool, bool ours)
        {
            Assert.Equal(ours, HrzOwnershipRules.CreationsAreOurs(tool));
            Assert.Equal(ours, HrzOwnershipRules.ShouldStamp(tool, true, true, false));
        }

        // ------------------------------------------------------------------ M-2: one rule for every ownership gate

        private static HrzOwnershipRules.UserRef Me => new HrzOwnershipRules.UserRef { User = "user-a" };

        [Fact]
        public void Every_gate_asks_the_same_question_another_users_mark_and_an_old_mark_are_somebody_elses()
        {
            SessionCreationLedger.ResetForTests();
            var ours = new HrzOwnershipRules.MarkFacts { Present = true, Session = "s0", User = new HrzOwnershipRules.UserRef { User = "user-a" } };
            var theirs = new HrzOwnershipRules.MarkFacts { Present = true, Session = "s0", User = new HrzOwnershipRules.UserRef { User = "user-b" } };
            var old = new HrzOwnershipRules.MarkFacts { Present = true, Session = "s0", User = null };
            var none = new HrzOwnershipRules.MarkFacts { Present = false };

            // The composition the delete cascade and view_visibility use (OwnershipRules.Decide fed by the believed mark) in a workshared model.
            Assert.True(HrzOwnershipRules.Verdict(true, ours, "s1", Me, false, null, null).Own);
            Assert.False(HrzOwnershipRules.Verdict(true, theirs, "s1", Me, false, null, null).Own);
            Assert.False(HrzOwnershipRules.Verdict(true, old, "s1", Me, false, null, null).Own);
            Assert.False(HrzOwnershipRules.Verdict(true, none, "s1", Me, false, null, null).Own);
            Assert.Equal(OwnershipRules.Mark, HrzOwnershipRules.Verdict(true, ours, "s1", Me, false, null, null).Evidence);
            // The creator evidence still rescues an element somebody else's mark was copied onto when Revit says we made it.
            Assert.True(HrzOwnershipRules.Verdict(true, theirs, "s1", Me, false, true, "me").Own);
            Assert.False(HrzOwnershipRules.Verdict(true, theirs, "s1", Me, false, false, "ana").Own);
            // A file nobody else works in keeps its rule.
            Assert.True(HrzOwnershipRules.Verdict(false, theirs, "s1", Me, false, null, null).Own);

            // The gate of view_crop / sheet_layout.
            Assert.Null(HrzOwnershipRules.RequireRefusal("view", 5, ours, "s1", Me, false, false, "allow_unmarked_view"));
            Assert.NotNull(HrzOwnershipRules.RequireRefusal("view", 5, theirs, "s1", Me, false, false, "allow_unmarked_view"));
            Assert.NotNull(HrzOwnershipRules.RequireRefusal("view", 5, old, "s1", Me, false, false, "allow_unmarked_view"));
            Assert.Null(HrzOwnershipRules.RequireRefusal("view", 5, theirs, "s1", Me, false, true, "allow_unmarked_view"));      // the explicit override
            Assert.Null(HrzOwnershipRules.RequireRefusal("view", 5, none, "s1", Me, true, false, "allow_unmarked_view"));        // created by a write of this session
            SessionCreationLedger.ResetForTests();
        }

        [Theory]
        // present, markSession, currentSession, created, user in the mark, trusted
        [InlineData(true, "s0", "s1", false, "user-a", true)]
        [InlineData(true, "s0", "s1", false, "user-b", false)]
        [InlineData(true, "s0", "s1", false, null, false)]
        [InlineData(true, "s1", "s1", false, "user-a", false)]
        [InlineData(true, "s1", "s1", true, "user-a", true)]
        [InlineData(false, "s0", "s1", false, "user-a", false)]
        public void A_mark_is_believed_by_the_one_rule_IsTrusted(bool present, string session, string current, bool created, string user, bool trusted)
        {
            var mark = new HrzOwnershipRules.MarkFacts { Present = present, Session = session, User = user == null ? null : new HrzOwnershipRules.UserRef { User = user } };
            Assert.Equal(trusted, HrzOwnershipRules.IsTrusted(mark, current, Me, created));
        }

        // ------------------------------------------------------------------ M-3: the notice tracks what the writes hand out

        [Fact]
        public void The_three_notes_a_write_created_keep_the_notice_alive_after_a_synchronize_until_they_are_revalidated()
        {
            IdFreshnessRules.ResetForTests();
            IdFreshnessRules.NoteReply(Doc, T, "horizun_create_elements", null, Rows((5001, "uidA"), (5002, "uidB"), (5003, "uidC")), isWrite: true);
            IdFreshnessRules.NoteSynchronized(Doc, T);
            IdFreshnessRules.NoteReply(Doc, T, "horizun_query_model", null, Rows((30, "uidL1"), (31, "uidL2")));            // the levels
            Assert.NotNull(IdFreshnessRules.Block(Doc));                                                                      // not cleared by that
            Assert.Equal(3, (int)IdFreshnessRules.Block(Doc)["ids_to_revalidate"]);
            IdFreshnessRules.NoteReply(Doc, T, "horizun_query_model", null, Rows((5001, "uidA"), (5002, "uidB")));
            Assert.NotNull(IdFreshnessRules.Block(Doc));
            IdFreshnessRules.NoteReply(Doc, T, "horizun_set_keynote", null, Rows((5003, "uidC")), isWrite: true);             // a write that hands the id back with its unique_id
            Assert.Null(IdFreshnessRules.Block(Doc));
            IdFreshnessRules.ResetForTests();
        }

        [Fact]
        public void An_element_a_write_deleted_is_no_longer_waiting_to_be_revalidated()
        {
            IdFreshnessRules.ResetForTests();
            IdFreshnessRules.NoteReply(Doc, T, "horizun_create_elements", null, Rows((5001, "uidA"), (5002, "uidB"), (5003, "uidC")), isWrite: true);
            var deleted = new JObject { ["rows"] = new JArray(new JObject { ["id"] = 5002, ["unique_id"] = "uidB", ["verdict"] = "deleted" }) };
            IdFreshnessRules.NoteReply(Doc, T, "horizun_delete_verified", null, deleted, isWrite: true);
            IdFreshnessRules.NoteSynchronized(Doc, T);
            Assert.Equal(2, (int)IdFreshnessRules.Block(Doc)["ids_to_revalidate"]);
            IdFreshnessRules.ResetForTests();
        }

        [Fact]
        public void The_old_test_that_any_unrelated_read_clears_the_notice_after_a_write_is_gone()
        {
            // Before R2V only the id-returning READS were harvested: write, synchronize, read levels cleared the notice (field Colon T3, step 2).
            IdFreshnessRules.ResetForTests();
            IdFreshnessRules.NoteReply(Doc, T, "horizun_create_elements", null, Rows((5001, "uidA")), isWrite: true);
            IdFreshnessRules.NoteSynchronized(Doc, T);
            IdFreshnessRules.NoteReply(Doc, T, "horizun_list_elements", null, Rows((30, "uidL1")));
            Assert.NotNull(IdFreshnessRules.Block(Doc));
            IdFreshnessRules.ResetForTests();
        }

        // ------------------------------------------------------------------ m-a, m-b, m-c: the notice

        [Fact]
        public void The_same_id_coming_back_with_another_unique_id_is_reported_as_ids_changed_and_not_cleared_in_silence()
        {
            IdFreshnessRules.ResetForTests();
            IdFreshnessRules.NoteReply(Doc, T, "horizun_query_model", null, Rows((5001, "uidA")));
            IdFreshnessRules.NoteSynchronized(Doc, T);
            JObject changed = IdFreshnessRules.NoteReply(Doc, T, "horizun_query_model", null, Rows((5001, "uidZ")));
            Assert.NotNull(changed);
            Assert.Equal(1, (int)changed["count"]);
            Assert.Equal(5001, (long)changed["ids"][0]["id"]);
            Assert.Equal("uidA", (string)changed["ids"][0]["was_unique_id"]);
            Assert.Equal("uidZ", (string)changed["ids"][0]["now_unique_id"]);
            // The same id with the same unique_id reports nothing.
            IdFreshnessRules.NoteSynchronized(Doc, T);
            Assert.Null(IdFreshnessRules.NoteReply(Doc, T, "horizun_query_model", null, Rows((5001, "uidZ"))));
            IdFreshnessRules.ResetForTests();
        }

        [Fact]
        public void A_row_of_a_linked_model_never_revalidates_an_id_of_the_host()
        {
            IdFreshnessRules.ResetForTests();
            IdFreshnessRules.NoteReply(Doc, T, "horizun_query_model", null, Rows((5001, "uidA")));
            IdFreshnessRules.NoteSynchronized(Doc, T);
            var link = new JObject { ["rows"] = new JArray(
                new JObject { ["element_id"] = 5001, ["unique_id"] = "uid-in-the-link", ["link_instance_id"] = 77 },
                new JObject { ["element_id"] = 5001, ["unique_id"] = "uid-in-the-link-2", ["source_kind"] = "link" }) };
            Assert.Null(IdFreshnessRules.NoteReply(Doc, T, "horizun_query_model", null, link));                              // not reported as a change either
            Assert.NotNull(IdFreshnessRules.Block(Doc));
            IdFreshnessRules.NoteReply(Doc, T, "horizun_query_model", null, Rows((5001, "uidA")));
            Assert.Null(IdFreshnessRules.Block(Doc));
            IdFreshnessRules.ResetForTests();
        }

        [Fact]
        public void Past_the_tracking_cap_the_notice_says_how_to_turn_itself_off_and_one_validation_does()
        {
            IdFreshnessRules.ResetForTests();
            Assert.True(IdFreshnessRules.MaxTrackedIds <= 50000);
            var rows = new JArray();
            for (int i = 1; i <= IdFreshnessRules.MaxTrackedIds + 5; i++) rows.Add(new JObject { ["element_id"] = i, ["unique_id"] = "u" + i });
            IdFreshnessRules.NoteReply(Doc, T, "horizun_model_scan", null, new JObject { ["rows"] = rows });
            IdFreshnessRules.NoteSynchronized(Doc, T);
            IdFreshnessRules.NoteReply(Doc, T, "horizun_model_scan", null, new JObject { ["rows"] = rows });                // a model_scan of everything again
            JObject block = IdFreshnessRules.Block(Doc);
            Assert.NotNull(block);                                                                                           // a re-read cannot prove what was not tracked
            Assert.True((bool)block["tracking_overflow"]);
            Assert.Contains("validate_ids", (string)block["note"]);
            Assert.Contains("only the ids you are going to write", (string)block["note"]);
            IdFreshnessRules.NoteReply(Doc, T, "horizun_selection_context", "validate_ids", Validated("coincide", 7, "u7", "u7"));
            Assert.Null(IdFreshnessRules.Block(Doc));
            IdFreshnessRules.ResetForTests();
        }

        [Fact]
        public void The_state_of_a_document_is_forgotten_when_it_closes_and_the_handlers_do_the_bookkeeping()
        {
            IdFreshnessRules.ResetForTests();
            IdFreshnessRules.NoteReply(Doc, T, "horizun_query_model", null, Rows((1, "a")));
            DocumentLifecycleRules.Reloaded(Doc, T);                                                                        // "Reload Latest"
            Assert.NotNull(IdFreshnessRules.Block(Doc));
            DocumentLifecycleRules.Synchronized(Doc, T);
            Assert.Equal(2, (int)IdFreshnessRules.Block(Doc)["syncs_since_last_read"]);

            string renamedFrom = null, renamedTo = null;
            DocumentLifecycleRules.SavedAs(Doc, "path:c:/models/saved-as.rvt", (a, b) => { renamedFrom = a; renamedTo = b; });
            Assert.Equal(Doc, renamedFrom);
            Assert.Equal("path:c:/models/saved-as.rvt", renamedTo);
            Assert.Null(IdFreshnessRules.Block(Doc));
            Assert.NotNull(IdFreshnessRules.Block("path:c:/models/saved-as.rvt"));

            DocumentLifecycleRules.Closing("path:c:/models/saved-as.rvt");
            Assert.Null(IdFreshnessRules.Block("path:c:/models/saved-as.rvt"));
            Assert.Equal(0, IdFreshnessRules.TrackedDocuments);
            IdFreshnessRules.ResetForTests();
        }

        // R2W: from "the file contains a call" to "the handler of THAT event, subscribed and unsubscribed, contains THAT call". The rules themselves are
        // tested by behavior (here and in ElementIdentityGuardR2WTests); this ties each Revit event to its rule through the document of the handlers.
        [Theory]
        [InlineData("DocumentReloadedLatest", "ReloadedLatest", "DocumentLifecycleRules.Reloaded(")]
        [InlineData("DocumentSynchronizedWithCentral", "Synchronized", "DocumentLifecycleRules.Synchronized(")]
        [InlineData("DocumentSavedAs", "SavedAs", "DocumentLifecycleRules.SavedAsCompleted(")]
        [InlineData("DocumentSavingAs", "SavingAs", "DocumentLifecycleRules.SavingAsBegan(")]
        [InlineData("DocumentClosing", "Closing", "DocumentLifecycleRules.ClosingBegan(")]
        [InlineData("DocumentClosed", "Closed", "DocumentLifecycleRules.Closed(")]
        public void The_revit_handlers_hand_each_event_to_the_lifecycle_rules(string revitEvent, string handler, string call)
        {
            string text = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Horizun.Revit", "Core", "QueryCacheLifecycle.cs"));
            Match on = Regex.Match(text, @"ControlledApplication\." + revitEvent + @"\s*\+=\s*(\w+);");
            Match off = Regex.Match(text, @"ControlledApplication\." + revitEvent + @"\s*-=\s*(\w+);");
            Assert.True(on.Success && off.Success, revitEvent + " must be subscribed in Attach and unsubscribed in Detach");
            Assert.Equal(handler, on.Groups[1].Value);
            Assert.Equal(handler, off.Groups[1].Value);

            int start = text.IndexOf("static void " + handler + "(", StringComparison.Ordinal);
            Assert.True(start >= 0, "handler " + handler + " not found");
            int open = text.IndexOf('{', start);
            int depth = 0, end = open;
            for (; end < text.Length; end++)
            {
                if (text[end] == '{') depth++;
                else if (text[end] == '}' && --depth == 0) break;
            }
            string body = text.Substring(open, end - open + 1);
            Assert.Contains(call, body);
            Assert.Contains(handler == "Closed" ? "e.DocumentId" : "e.Document", body);                    // the event's own document, not the active one
            if (handler == "Closed") Assert.Contains("RevitAPIEventStatus.Succeeded", body);   // a cancelled close loses nothing
            if (handler == "Closing") Assert.DoesNotContain("Closed(", body);               // the closing can still be cancelled: it only remembers
        }

        // ------------------------------------------------------------------ M-4: who a user is

        private static HrzOwnershipRules.UserRef U(string user, string windows = null, string sid = null) =>
            new HrzOwnershipRules.UserRef { User = user, Windows = windows, Sid = sid };

        [Theory]
        [InlineData("alice", "alice", true)]
        [InlineData("DOM\\alice", "alice", true)]               // the domain is not part of who the person is
        [InlineData("alice", "DOM\\alice", true)]
        [InlineData("os:alice", "alice", true)]                 // a session where Revit had no user name, against one where it had
        [InlineData("os:DOM\\alice", "ALICE", true)]
        [InlineData("alice", "bob", false)]
        [InlineData("os:", "alice", false)]
        [InlineData("", "", false)]
        [InlineData(null, "alice", false)]
        public void Users_are_compared_without_domain_prefix_or_case(string a, string b, bool same)
        {
            Assert.Equal(same, HrzOwnershipRules.SameUser(U(a), U(b)));
        }

        [Fact]
        public void The_windows_sid_decides_when_both_sides_have_it()
        {
            // Same Revit account on two machines of two people (a generic account): the SIDs tell them apart.
            Assert.False(HrzOwnershipRules.SameUser(U("studio", "DOM\\ana", "S-1-5-21-1"), U("studio", "DOM\\luis", "S-1-5-21-2")));
            // The same person signed in with another Revit account: the SID says it is the same person.
            Assert.True(HrzOwnershipRules.SameUser(U("ana-account", "DOM\\ana", "S-1-5-21-1"), U("os:ana", "DOM\\ana", "s-1-5-21-1")));
            // Only one side has a SID (an older mark): the names decide, which is the documented limit of a shared account.
            Assert.True(HrzOwnershipRules.SameUser(U("studio"), U("studio", "DOM\\luis", "S-1-5-21-2")));
            // Different Revit accounts but the same Windows user, no SIDs.
            Assert.True(HrzOwnershipRules.SameUser(U("ana-account", "DOM\\ana"), U("os:ana", "ana")));
            Assert.False(HrzOwnershipRules.SameUser(null, U("alice")));
        }

        [Fact]
        public void The_new_mark_user_rule_agrees_with_the_old_string_overload()
        {
            Assert.True(HrzOwnershipRules.TrustMark(true, "s0", "s1", false, "DOM\\alice", "alice"));
            Assert.True(HrzOwnershipRules.TrustMark(true, "s0", "s1", false, "os:alice", "alice"));
            Assert.False(HrzOwnershipRules.TrustMark(true, "s0", "s1", false, "bob", "alice"));
        }

        // ------------------------------------------------------------------ m-g: a stamp is both versions or it is not a stamp

        [Theory]
        [InlineData(true, true, false, HrzOwnershipRules.StampOutcome.Stamped)]
        [InlineData(true, false, true, HrzOwnershipRules.StampOutcome.SkippedV1RolledBack)]
        [InlineData(true, false, false, HrzOwnershipRules.StampOutcome.SkippedV1Left)]
        [InlineData(false, false, false, HrzOwnershipRules.StampOutcome.Skipped)]
        public void The_element_counts_as_stamped_only_when_both_versions_were_written(bool v1, bool v2, bool rolledBack, HrzOwnershipRules.StampOutcome expected)
        {
            Assert.Equal(expected, HrzOwnershipRules.StampResult(v1, v2, rolledBack));
        }

        // ------------------------------------------------------------------ m-e / m-f: the hash and the read-only tables

        [Fact]
        public void The_tools_that_take_their_identity_per_action_are_part_of_the_hash()
        {
            Assert.Contains("per_action=", IdentityCoverage.Summary());
            Assert.Contains("horizun_annotate", IdentityCoverage.PerActionTools);
            Assert.Contains("horizun_edit_dimensions", IdentityCoverage.PerActionTools);
        }

        // Property of IdentityCoverage -> the label of the section of the hash summary that carries it. A new table with no entry here fails the test.
        private static readonly Dictionary<string, string> SummarySection = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["VerifiedTools"] = "verified", ["ReadOperations"] = "writes_nothing", ["CaptureTemporaryFields"] = "capture",
            ["AlwaysDeletingTools"] = "deleting", ["DeleteWords"] = "deleting", ["OperationKeys"] = "deleting", ["DeleteKeys"] = "deleting",
            ["DeletingOperations"] = "deleting_operations", ["DeletingConditions"] = "deleting_conditions", ["NotDeletingOperations"] = "not_deleting",
            ["DeletingKeys"] = "deleting_keys", ["NoSubjectTools"] = "no_subject", ["PerActionTools"] = "per_action", ["NearNames"] = "near_names",
            ["OwnIdentityTextPaths"] = "own_identity_text", ["DocumentedDefaultKeys"] = "default_keys", ["AcceptedNames"] = "accepted", ["DocumentedIdKeys"] = "keys"
        };

        [Fact]
        public void Every_value_of_every_public_table_of_the_coverage_is_in_its_own_section_of_the_hash_summary()
        {
            string[] sections = IdentityCoverage.Summary().Split((char)30);
            var problems = new List<string>();
            foreach (PropertyInfo p in typeof(IdentityCoverage).GetProperties(BindingFlags.Public | BindingFlags.Static))
            {
                if (!SummarySection.TryGetValue(p.Name, out string label)) { problems.Add(p.Name + ": a public table with no section in this test (and probably none in the hash)"); continue; }
                string section = sections.FirstOrDefault(x => x.StartsWith(label + "=", StringComparison.Ordinal));
                if (section == null) { problems.Add(p.Name + ": the hash summary has no section '" + label + "='"); continue; }
                object value = p.GetValue(null);
                var strings = new List<string>();
                if (value is IEnumerable<string> list) strings.AddRange(list);
                else if (value is IEnumerable<KeyValuePair<string, string>> pairs) foreach (var kv in pairs) { strings.Add(kv.Key); strings.Add(kv.Value); }
                else if (value is IEnumerable<KeyValuePair<string, IReadOnlyList<string>>> rows) foreach (var kv in rows) { strings.Add(kv.Key); strings.AddRange(kv.Value); }
                else { problems.Add(p.Name + ": a type this test cannot read (" + p.PropertyType + ")"); continue; }
                foreach (string v in strings.Where(x => !string.IsNullOrEmpty(x)))
                    if (!section.Contains(v)) problems.Add(p.Name + ": '" + v + "' is not in the '" + label + "=' section");
            }
            Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
        }

        [Fact]
        public void No_public_table_of_the_guard_or_of_the_ownership_rules_is_a_mutable_collection()
        {
            var bad = new List<string>();
            foreach (Type type in new[] { typeof(ElementIdentityGuard), typeof(IdentityCoverage), typeof(HrzOwnershipRules), typeof(IdFreshnessRules), typeof(SessionCreationLedger), typeof(OwnershipRules) })
            {
                var members = new List<KeyValuePair<string, Type>>();
                foreach (FieldInfo f in type.GetFields(BindingFlags.Public | BindingFlags.Static))
                    if (!f.IsLiteral) members.Add(new KeyValuePair<string, Type>(type.Name + "." + f.Name, f.FieldType));
                foreach (PropertyInfo p in type.GetProperties(BindingFlags.Public | BindingFlags.Static)) members.Add(new KeyValuePair<string, Type>(type.Name + "." + p.Name, p.PropertyType));
                foreach (KeyValuePair<string, Type> m in members)
                    if (IsMutable(m.Value)) bad.Add(m.Key + " : " + m.Value);
            }
            Assert.True(bad.Count == 0, string.Join(Environment.NewLine, bad));
        }

        private static bool IsMutable(Type t)
        {
            if (t.IsArray) return true;
            if (t.IsGenericType)
            {
                Type d = t.GetGenericTypeDefinition();
                if (d == typeof(List<>) || d == typeof(HashSet<>) || d == typeof(Dictionary<,>) || d == typeof(SortedSet<>) || d == typeof(Queue<>) || d == typeof(Stack<>) ||
                    d == typeof(ICollection<>) || d == typeof(IList<>) || d == typeof(IDictionary<,>) || d == typeof(ISet<>)) return true;
                return t.GetGenericArguments().Any(IsMutable);
            }
            return false;
        }

        // ------------------------------------------------------------------ m-f: pure rules extracted from the Revit code

        [Fact]
        public void An_ownership_reader_that_fails_counts_as_not_ours()
        {
            Assert.True(ElementIdentityGuard.SafeOwned(() => true));
            Assert.False(ElementIdentityGuard.SafeOwned(() => false));
            Assert.False(ElementIdentityGuard.SafeOwned(() => throw new InvalidOperationException("the schema is unreadable")));
            Assert.False(ElementIdentityGuard.SafeOwned(null));
        }

        [Fact]
        public void A_text_note_type_has_a_built_in_category_even_though_revit_gives_it_none()
        {
            Assert.Equal("OST_Walls", ElementIdentityGuard.BuiltInNameFor(() => "OST_Walls", false));
            Assert.Equal("OST_TextNotes", ElementIdentityGuard.BuiltInNameFor(() => null, true));
            Assert.Equal("OST_TextNotes", ElementIdentityGuard.BuiltInNameFor(() => throw new InvalidOperationException(), true));
            Assert.Null(ElementIdentityGuard.BuiltInNameFor(() => null, false));
            Assert.Null(ElementIdentityGuard.BuiltInNameFor(() => throw new InvalidOperationException(), false));
        }

        // ------------------------------------------------------------------ m-d: the sweep of deleting calls reaches every folder and the helpers

        private static readonly Regex Call = new Regex(@"\.Delete\(|UngroupMembers\(|\.Disassemble\(", RegexOptions.Compiled);

        private static IEnumerable<string> ProductFiles()
        {
            string src = Path.Combine(RepoRoot(), "src", "Horizun.Revit");
            foreach (string path in Directory.GetFiles(src, "*.cs", SearchOption.AllDirectories))
            {
                string rel = path.Substring(src.Length + 1).Replace('\\', '/');
                if (rel.StartsWith("bin/") || rel.StartsWith("obj/")) continue;
                yield return rel;
            }
        }

        [Fact]
        public void The_label_of_every_classified_call_is_the_operation_of_the_case_the_call_lives_in()
        {
            // R2U labelled the call that recreates a retyped group's member tags "add_members". The label is checked against the code: the nearest
            // `case "x":` block above the call (when the call sits inside a switch and not in a helper below it) must contain the labelled operation.
            string src = Path.Combine(RepoRoot(), "src", "Horizun.Revit");
            var caseRx = new Regex(@"^\s*case ""([a-z0-9_]+)"":", RegexOptions.Compiled);
            var methodRx = new Regex(@"^\s{8}(private|internal|public|protected)\s[^=;]*\(", RegexOptions.Compiled);
            var problems = new List<string>();
            foreach (KeyValuePair<string, KeyValuePair<string, string>[]> file in ElementIdentityGuardR2UTests.Sweep)
            {
                string[] lines = File.ReadAllLines(Path.Combine(src, file.Key.Replace('/', Path.DirectorySeparatorChar)));
                var callAt = new List<int>();
                for (int n = 0; n < lines.Length; n++)
                {
                    string code = lines[n].Trim();
                    if (code.StartsWith("//") || !Regex.IsMatch(code, @"\.Delete\(|UngroupMembers\(|\.Disassemble\(")) continue;
                    if (code.Contains("File.Delete(") || code.Contains("Directory.Delete(") || code.Contains("backups[i].Delete(") || code.Contains("Discovery.Delete(")) continue;
                    callAt.Add(n);
                }
                for (int i = 0; i < callAt.Count && i < file.Value.Length; i++)
                {
                    string[] kind = file.Value[i].Value.Split(new[] { '|' }, 3);
                    string operation = kind[0] == "own" ? (kind.Length > 2 ? kind[2] : null) : (string)JObject.Parse(kind[1])["operation"];
                    if (operation == null) continue;
                    int caseLine = -1, methodLine = -1;
                    for (int n = callAt[i]; n >= 0; n--)
                    {
                        if (caseLine < 0 && caseRx.IsMatch(lines[n])) caseLine = n;
                        if (methodLine < 0 && methodRx.IsMatch(lines[n])) methodLine = n;
                        if (caseLine >= 0 && methodLine >= 0) break;
                    }
                    if (caseLine < 0 || caseLine < methodLine) continue;      // not inside a switch of its own method
                    var stack = new List<string>();
                    for (int n = caseLine; n >= 0 && (caseRx.IsMatch(lines[n]) || lines[n].Trim().Length == 0); n--)
                    {
                        Match m = caseRx.Match(lines[n]);
                        if (m.Success) stack.Add(m.Groups[1].Value);
                    }
                    if (!stack.Contains(operation)) problems.Add(file.Key + " call " + (i + 1) + ": labelled '" + operation + "' but it lives in case " + string.Join("/", stack));
                }
            }
            Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
        }

        [Fact]
        public void A_helper_that_deletes_and_is_not_private_is_called_only_from_files_that_were_examined()
        {
            // A delete hidden behind an internal / public method is not "a delete call in the command", and the line scanner alone would not
            // relate the command that calls it. Each such helper is listed with the files that call it; a new helper or a new caller fails.
            string src = Path.Combine(RepoRoot(), "src", "Horizun.Revit");
            var methodRx = new Regex(@"^\s*(public|internal|private|protected)\s+((?:static|override|sealed|virtual|async)\s+)*[\w<>\[\],\.\? ]+?\s+(\w+)\s*\(", RegexOptions.Compiled);
            var typeRx = new Regex(@"\b(?:partial\s+)?(?:static\s+|sealed\s+)*(?:partial\s+)?class\s+(\w+)", RegexOptions.Compiled);
            var found = new Dictionary<string, string>(StringComparer.Ordinal);   // "file::method" -> class
            foreach (string rel in ProductFiles())
            {
                string[] lines = File.ReadAllLines(Path.Combine(src, rel.Replace('/', Path.DirectorySeparatorChar)));
                string access = null, method = null, type = null;
                foreach (string raw in lines)
                {
                    Match t = typeRx.Match(raw);
                    if (t.Success && !raw.TrimStart().StartsWith("//")) type = t.Groups[1].Value;
                    Match m = methodRx.Match(raw);
                    if (m.Success && !raw.Contains(" class ")) { access = m.Groups[1].Value; method = m.Groups[3].Value; }
                    string code = raw.Trim();
                    if (code.StartsWith("//") || !Call.IsMatch(code) || code.Contains("File.Delete(") || code.Contains("Directory.Delete(") || code.Contains("Discovery.Delete(")) continue;
                    if (access == null || access == "private" || method == "Execute" || method == "OnShutdown") continue;   // the command itself / the add-in's own shutdown
                    found[rel + "::" + method] = type;
                }
            }
            var actual = new Dictionary<string, string[]>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, string> h in found)
            {
                string definer = h.Key.Substring(0, h.Key.IndexOf("::", StringComparison.Ordinal));
                string method = h.Key.Substring(h.Key.IndexOf("::", StringComparison.Ordinal) + 2);
                var qualified = new Regex(@"\b" + Regex.Escape(h.Value) + @"\." + Regex.Escape(method) + @"\s*\(");
                actual[h.Key] = ProductFiles().Where(f => f != definer && qualified.IsMatch(File.ReadAllText(Path.Combine(src, f.Replace('/', Path.DirectorySeparatorChar)))))
                                              .Concat(ProductFiles().Where(f => f != definer && ExposedDeletingHelpers.SamePartial.Contains(h.Key) && File.ReadAllText(Path.Combine(src, f.Replace('/', Path.DirectorySeparatorChar))).Contains(" " + method + "(")))
                                              .Distinct().OrderBy(x => x, StringComparer.Ordinal).ToArray();
            }
            string report = string.Join(Environment.NewLine, actual.OrderBy(k => k.Key).Select(k => "[\"" + k.Key + "\"] = " + string.Join(", ", k.Value)));
            Assert.True(ExposedDeletingHelpers.Matches(actual), "exposed deleting helpers and their callers changed; examine them and update ExposedDeletingHelpers:" + Environment.NewLine + report);
        }

        [Fact]
        public void The_ledger_of_creations_does_not_take_in_what_a_script_or_a_copy_added()
        {
            SessionCreationLedger.ResetForTests();
            SessionCreationLedger.RecordFor("horizun_execute_python", "path:m", new[] { "py-1" });
            SessionCreationLedger.RecordFor("horizun_create_elements", "path:m", new[] { "typed-1" });
            Assert.False(SessionCreationLedger.Created("path:m", "py-1"));
            Assert.True(SessionCreationLedger.Created("path:m", "typed-1"));
            Assert.Equal(new[] { "typed-1" }, SessionCreationLedger.All("path:m").ToArray());
            // A mark of this session on a script's element is therefore a copy: not ours.
            Assert.False(HrzOwnershipRules.Owned("path:m", "py-1", true, "s1", "user-a", "s1", "user-a"));
            SessionCreationLedger.ResetForTests();
        }

        [Fact]
        public void The_sweep_reaches_every_folder_of_the_add_in_and_every_call_in_it_is_classified()
        {
            string src = Path.Combine(RepoRoot(), "src", "Horizun.Revit");
            Assert.Contains(ProductFiles(), f => f == "App.cs");
            var problems = new List<string>();
            foreach (string rel in ProductFiles())
            {
                string path = Path.Combine(src, rel.Replace('/', Path.DirectorySeparatorChar));
                List<string> lines = ElementIdentityGuardR2UTests.DeleteLines(File.ReadAllText(path));
                if (lines.Count == 0) { if (ElementIdentityGuardR2UTests.Sweep.ContainsKey(rel)) problems.Add(rel + ": classified but it no longer deletes"); continue; }
                if (!ElementIdentityGuardR2UTests.Sweep.TryGetValue(rel, out KeyValuePair<string, string>[] entries)) { problems.Add(rel + ": " + lines.Count + " call(s) not classified: " + string.Join(" || ", lines)); continue; }
                if (entries.Length != lines.Count) { problems.Add(rel + ": " + lines.Count + " call(s) in the file, " + entries.Length + " classified: " + string.Join(" || ", lines)); continue; }
                for (int i = 0; i < lines.Count; i++)
                {
                    if (!lines[i].Contains(entries[i].Key)) problems.Add(rel + " call " + (i + 1) + ": the line '" + lines[i] + "' is not the one classified ('" + entries[i].Key + "')");
                    string[] kind = entries[i].Value.Split(new[] { '|' }, 3);
                    if (kind[0] == "own") continue;
                    if (!ElementIdentityGuard.IsDeleting(kind[0], JObject.Parse(kind[1]))) problems.Add(rel + " call " + (i + 1) + ": IsDeleting(" + kind[0] + ", " + kind[1] + ") is false");
                }
            }
            foreach (string rel in ElementIdentityGuardR2UTests.Sweep.Keys)
                if (!File.Exists(Path.Combine(src, rel.Replace('/', Path.DirectorySeparatorChar)))) problems.Add(rel + ": classified but the file does not exist");
            Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
        }
    }

    /// <summary>The non-private methods that contain a model delete, with the files that call them, examined one by one (R2V m-d).</summary>
    internal static class ExposedDeletingHelpers
    {
        // "file::method" -> the files that call it, and why that is safe.
        private static readonly Dictionary<string, string[]> Examined = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            // deletes the energy model the command itself created (not a covered tool): code_check and the energy export.
            ["Commands/EnergyModelBuild.cs::CreateSpatial"] = new[] { "Commands/CodeCheckEnergy.cs", "Commands/ExportSets.cs" },
            // The crop API creates sketch dimensions. The helper removes only dimensions added by that call,
            // with both crop commands verifying the resulting boundary before they keep the transaction.
            ["Commands/FixPlanimetryCommand.cs::SetCropShapeWithoutGeneratedDimensions"] = new[] { "Commands/ViewCropCommand.cs" },
            // the sheet-set deletion of manage_views (covered: sheet_set_delete is a deleting operation); called by the same command.
            ["Commands/ManageViewsControl.cs::ApplyControl"] = new[] { "Commands/ManageViewsCommand.cs" },
            // horizun_undo, not a covered tool.
            ["Commands/UndoCapture.cs::ApplyInverse"] = new[] { "Commands/UndoCommand.cs" }
        };

        /// <summary>Helpers of a partial class: its other files call them without a qualifier.</summary>
        internal static readonly HashSet<string> SamePartial = new HashSet<string>(StringComparer.Ordinal) { "Commands/ManageViewsControl.cs::ApplyControl" };

        internal static bool Matches(Dictionary<string, string[]> actual)
        {
            if (actual.Count != Examined.Count) return false;
            foreach (var kv in actual)
                if (!Examined.TryGetValue(kv.Key, out string[] callers) || !callers.SequenceEqual(kv.Value)) return false;
            return true;
        }
    }
}
