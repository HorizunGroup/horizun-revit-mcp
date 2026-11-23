using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Horizun.Contracts;
using Horizun.Revit.Commands;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    // R2W - the findings of the independent review of R2V. B-2 (validate_ids handed out the unique_id of another element), M-a (a shared Windows account),
    // M-b (view / sheet gates in a document that is not collaborative), M-c (query_model by id), m-1..m-6 and the weak tests of the review.
    public class ElementIdentityGuardR2WTests
    {
        private static string RepoRoot()
        {
            DirectoryInfo d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, "AGENTS.md"))) d = d.Parent;
            Assert.NotNull(d);
            return d.FullName;
        }

        private static string Source(params string[] parts) => File.ReadAllText(Path.Combine(new[] { RepoRoot() }.Concat(parts).ToArray()));

        private const string Doc = "path:c:/models/colon-w.rvt";
        private static readonly DateTime T = new DateTime(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);

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

        private static JObject Rows(params (long id, string uid)[] rows) =>
            new JObject { ["rows"] = new JArray(rows.Select(r => new JObject { ["element_id"] = r.id, ["unique_id"] = r.uid })) };

        // ------------------------------------------------------------------ B-2

        [Fact]
        public void A_reassigned_id_never_hands_out_the_unique_id_of_the_note_that_took_it_and_a_resend_of_found_cannot_write_on_it()
        {
            Dictionary<long, ElementIdentityFacts> model = ColonAfterSync();
            Func<long, ElementIdentityFacts> byId = id => model.TryGetValue(id, out ElementIdentityFacts f) ? f : null;
            Func<string, ElementIdentityFacts> byUid = uid => model.Values.FirstOrDefault(f => f.UniqueId == uid);
            IdentityLookups lookups = new IdentityLookups { ById = byId, ByUniqueId = byUid, IsCollaborative = () => true };

            // The review's scenario: the agent holds 5001 -> uidA; 5001 is uidD now and uidA lives at 9101.
            JToken row = ElementIdentityGuard.Validate(JArray.Parse("[{\"id\":5001,\"unique_id\":\"uidA\"}]"), byId, byUid)["results"][0];
            Assert.Equal("cambio", (string)row["status"]);
            Assert.Equal(9101, (long)row["current_id"]);
            Assert.Null(row["found"]["unique_id"]);                                    // before R2W: "uidD" here
            Assert.Equal("uidD", (string)row["found"]["holder_unique_id"]);
            Assert.False((bool)row["identifies"]);
            Assert.Contains("ANOTHER element", (string)row["warning"]);

            // The agent resends everything `found` offers as a unique_id: nothing, so the write stays refused (a weak identity in a collaborative model).
            var resend = new JObject { ["target_id"] = 5001, ["expected_category"] = "Text Notes" };
            if (row["found"]["unique_id"] != null) resend["unique_id"] = row["found"]["unique_id"];
            IdentityBinding.Reset();
            Assert.NotNull(IdentityPrepassCore.Early(resend, "horizun_write_params_verified", () => lookups));
            IdentityBinding.Reset();
        }

        [Fact]
        public void No_exit_of_validate_ids_offers_a_unique_id_that_is_not_the_one_that_was_sent()
        {
            Dictionary<long, ElementIdentityFacts> model = ColonAfterSync();
            Func<long, ElementIdentityFacts> byId = id => model.TryGetValue(id, out ElementIdentityFacts f) ? f : null;
            Func<string, ElementIdentityFacts> byUid = uid => model.Values.FirstOrDefault(f => f.UniqueId == uid);

            // Every combination of what a caller may send: id alone, unique_id alone, both, with category / type that match or not, ids that exist or not.
            var checks = new List<string>();
            foreach (string id in new[] { "5001", "9101", "777", null })
                foreach (string uid in new[] { "\"uidA\"", "\"uidD\"", "\"nobody\"", null })
                    foreach (string extra in new[] { "", ",\"expected_category\":\"Text Notes\"", ",\"expected_category\":\"Walls\"", ",\"expected_type\":\"Text 3mm\"", ",\"expected_type\":\"Other\"" })
                    {
                        if (id == null && uid == null) continue;
                        checks.Add("{" + (id != null ? "\"id\":" + id : "") + (id != null && uid != null ? "," : "") + (uid != null ? "\"unique_id\":" + uid : "") + extra + "}");
                    }
            JArray all = JArray.Parse("[" + string.Join(",", checks) + "]");
            JArray results = (JArray)ElementIdentityGuard.Validate(all, byId, byUid)["results"];
            Assert.Equal(checks.Count, results.Count);
            for (int i = 0; i < results.Count; i++)
            {
                JToken found = results[i]["found"];
                string sent = (string)all[i]["unique_id"];
                // R2Y M-1. current_id, when it names an id, is where the element ASKED FOR lives: whoever holds that id must have the unique_id that was sent.
                // It is never the id of an element with another unique_id (a write sent on it would land on the intruder).
                JToken current = results[i]["current_id"];
                if (sent != null && current != null && current.Type == JTokenType.Integer)
                {
                    Assert.True(model.TryGetValue((long)current, out ElementIdentityFacts holder) && holder.UniqueId == sent,
                        "check " + checks[i] + " was answered with current_id=" + current + ", whose occupant is not " + sent);
                }
                if (found == null || found.Type == JTokenType.Null) continue;
                JToken offered = found["unique_id"];
                if (offered != null && offered.Type != JTokenType.Null)
                {
                    Assert.True(sent != null && sent == (string)offered, "check " + checks[i] + " was answered with found.unique_id=" + offered + ", which is not the unique_id sent");
                    Assert.Null(results[i]["identifies"]);
                }
                else
                {
                    Assert.False((bool)results[i]["identifies"], "check " + checks[i]);
                    Assert.NotNull(found["holder_unique_id"]);
                }
            }
        }

        [Fact]
        public void The_notice_follows_the_unique_id_the_model_holds_under_an_id_when_validate_ids_names_it_holder_unique_id()
        {
            Dictionary<long, ElementIdentityFacts> model = ColonAfterSync();
            Func<long, ElementIdentityFacts> byId = id => model.TryGetValue(id, out ElementIdentityFacts f) ? f : null;
            Func<string, ElementIdentityFacts> byUid = uid => model.Values.FirstOrDefault(f => f.UniqueId == uid);

            IdFreshnessRules.ResetForTests();
            IdFreshnessRules.NoteReply(Doc, T, "horizun_create_elements", null, Rows((5001, "uidA")), isWrite: true);
            IdFreshnessRules.NoteSynchronized(Doc, T);
            // The REAL answer of the guard (not a hand-made one): cambio, found.holder_unique_id = uidD.
            JObject reply = ElementIdentityGuard.Validate(JArray.Parse("[{\"id\":5001,\"unique_id\":\"uidA\"}]"), byId, byUid);
            Assert.Null(IdFreshnessRules.NoteReply(Doc, T, "horizun_selection_context", "validate_ids", reply));
            Assert.Null(IdFreshnessRules.Block(Doc));                                  // the id was revalidated: it proved something
            // A second synchronize and a read that returns 5001 with the unique_id the model holds under it: the notice knew it (Held followed the holder),
            // so it reports no change; had Held kept uidA it would say ids_changed uidA -> uidD for an id the agent was already told about.
            IdFreshnessRules.NoteSynchronized(Doc, T);
            Assert.Null(IdFreshnessRules.NoteReply(Doc, T, "horizun_query_model", null, Rows((5001, "uidD"))));
            Assert.Null(IdFreshnessRules.Block(Doc));
            IdFreshnessRules.ResetForTests();
        }

        // ------------------------------------------------------------------ M-a

        private static HrzOwnershipRules.UserRef U(string user, string windows, string sid) =>
            new HrzOwnershipRules.UserRef { User = user, Windows = windows, Sid = sid };

        [Fact]
        public void Ana_and_Beto_sharing_one_windows_account_are_two_people()
        {
            HrzOwnershipRules.UserRef ana = U("ana.studio", "DOM\\cad", "S-1-5-21-9");
            HrzOwnershipRules.UserRef beto = U("beto.studio", "DOM\\cad", "S-1-5-21-9");
            Assert.False(HrzOwnershipRules.SameUser(ana, beto));
            Assert.False(HrzOwnershipRules.SameUser(beto, ana));
            Assert.False(HrzOwnershipRules.IsTrusted(new HrzOwnershipRules.MarkFacts { Present = true, Session = "s0", User = ana }, "s1", beto, false));
            // The cascade of a delete: Ana's element is not Beto's.
            Assert.False(HrzOwnershipRules.Verdict(true, new HrzOwnershipRules.MarkFacts { Present = true, Session = "s0", User = ana }, "s1", beto, false, null, null).Own);
        }

        [Fact]
        public void The_same_windows_account_with_the_same_revit_user_or_a_missing_revit_name_is_still_one_person()
        {
            Assert.True(HrzOwnershipRules.SameUser(U("ana.studio", "DOM\\cad", "S-1"), U("Ana.Studio", "DOM\\cad", "s-1")));
            Assert.True(HrzOwnershipRules.SameUser(U("ana.studio", "DOM\\cad", "S-1"), U("os:cad", "DOM\\cad", "S-1")));      // the other side had no Revit name
            Assert.True(HrzOwnershipRules.SameUser(U("os:cad", "DOM\\cad", "S-1"), U("os:cad", "DOM\\cad", "S-1")));
            Assert.True(HrzOwnershipRules.SameUser(U(null, "DOM\\cad", "S-1"), U("ana.studio", "DOM\\cad", "S-1")));
            // A different SID stays another person whatever the names say.
            Assert.False(HrzOwnershipRules.SameUser(U("ana.studio", "DOM\\cad", "S-1"), U("ana.studio", "DOM\\cad", "S-2")));
        }

        // ------------------------------------------------------------------ M-b

        private static readonly HrzOwnershipRules.UserRef Me = U("ana.studio", "DOM\\ana", "S-1");
        private static readonly HrzOwnershipRules.MarkFacts V1 = new HrzOwnershipRules.MarkFacts { Present = true, Session = "s0", User = null };
        private static readonly HrzOwnershipRules.MarkFacts OfOther = new HrzOwnershipRules.MarkFacts { Present = true, Session = "s0", User = U("beto.studio", "DOM\\beto", "S-2") };
        private static readonly HrzOwnershipRules.MarkFacts NoMark = new HrzOwnershipRules.MarkFacts { Present = false };

        [Fact]
        public void A_version_1_mark_counts_as_ours_for_the_view_and_sheet_gates_in_a_document_that_is_not_collaborative_and_stays_strict_in_a_collaborative_one()
        {
            Assert.Null(HrzOwnershipRules.RequireRefusal("view", 5, V1, "s1", Me, false, false, "allow_unmarked_view", workshared: false));
            Assert.NotNull(HrzOwnershipRules.RequireRefusal("view", 5, V1, "s1", Me, false, false, "allow_unmarked_view", workshared: true));
            Assert.NotNull(HrzOwnershipRules.RequireRefusal("view", 5, V1, "s1", Me, false, false, "allow_unmarked_view"));            // the default is the strict one
            // What the rule does not extend to: another user's mark and no mark, even outside a collaborative document.
            Assert.NotNull(HrzOwnershipRules.RequireRefusal("view", 5, OfOther, "s1", Me, false, false, "allow_unmarked_view", workshared: false));
            Assert.NotNull(HrzOwnershipRules.RequireRefusal("view", 5, NoMark, "s1", Me, false, false, "allow_unmarked_view", workshared: false));
            // The guard keeps asking IsTrusted: a version 1 mark is still not trusted there.
            Assert.False(HrzOwnershipRules.IsTrusted(V1, "s1", Me, false));
        }

        [Fact]
        public void The_refusal_names_the_real_reason_and_not_always_no_mark()
        {
            string collaborativeV1 = HrzOwnershipRules.RequireRefusal("view", 5, V1, "s1", Me, false, false, "allow_unmarked_view", workshared: true);
            Assert.Contains("version 1", collaborativeV1);
            Assert.Contains("collaborative", collaborativeV1);
            Assert.DoesNotContain("carries no HRZ ownership mark", collaborativeV1);

            string other = HrzOwnershipRules.RequireRefusal("view", 5, OfOther, "s1", Me, false, false, "allow_unmarked_view", workshared: true);
            Assert.Contains("another user", other);
            Assert.DoesNotContain("carries no HRZ ownership mark", other);

            string none = HrzOwnershipRules.RequireRefusal("view", 5, NoMark, "s1", Me, false, false, "allow_unmarked_view", workshared: true);
            Assert.Contains("carries no HRZ ownership mark", none);

            var thisSession = new HrzOwnershipRules.MarkFacts { Present = true, Session = "s1", User = Me };
            string copy = HrzOwnershipRules.RequireRefusal("view", 5, thisSession, "s1", Me, false, false, "allow_unmarked_view", workshared: true);
            Assert.Contains("copy", copy);
            Assert.DoesNotContain("carries no HRZ ownership mark", copy);

            foreach (string message in new[] { collaborativeV1, other, none, copy })
            {
                Assert.Contains("allow_unmarked_view=true", message);
                Assert.Contains("Nothing was written", message);
            }
        }

        [Fact]
        public void The_view_gates_ask_the_document_whether_it_is_collaborative_and_the_guide_describes_view_visibility_as_it_is()
        {
            Assert.Contains("overrideName, doc.IsWorkshared)", Source("src", "Horizun.Revit", "Core", "HrzOwnership.cs"));
            string guide = Source("docs", "IDENTITY-GUARD.md");
            Assert.Contains("`view_visibility` has NO override flag", guide);
            Assert.DoesNotContain("for `view_visibility` and for `view_crop` / `sheet_layout` unless", guide);
        }

        // ------------------------------------------------------------------ M-c

        [Fact]
        public void A_row_asked_by_id_in_a_collaborative_model_says_whose_unique_id_it_is()
        {
            var asked = new JObject { ["element_id"] = 5001, ["unique_id"] = "uidD" };
            Assert.True(QueryHolderNote.Apply(asked, askedById: true, collaborative: true));
            Assert.Contains("TODAY", (string)asked["holder_note"]);
            Assert.Contains("does not prove", (string)asked["holder_note"]);

            var byQuery = new JObject { ["element_id"] = 5001, ["unique_id"] = "uidD" };
            Assert.False(QueryHolderNote.Apply(byQuery, askedById: false, collaborative: true));        // a row found by content is the answer to what was asked
            Assert.Null(byQuery["holder_note"]);
            var local = new JObject { ["element_id"] = 5001, ["unique_id"] = "uidD" };
            Assert.False(QueryHolderNote.Apply(local, askedById: true, collaborative: false));          // nothing renumbers ids in a file that is not workshared
            Assert.Null(local["holder_note"]);
            Assert.False(QueryHolderNote.Apply(null, true, true));
        }

        [Fact]
        public void The_holder_note_travels_in_the_reply_only_and_the_command_applies_it_to_the_rows_it_resolved_by_id()
        {
            string command = Source("src", "Horizun.Revit", "Commands", "QueryModelCommand.cs");
            Assert.Contains("bool askedById = request[\"element_ids\"] is JArray askedIds && askedIds.Count > 0;", command);
            Assert.Contains("QueryHolderNote.Apply(json, askedById, source.IsWorkshared);", command);
            // tools/list is measured to the byte: neither the schema nor the contract names the note.
            foreach (string file in Directory.GetFiles(Path.Combine(RepoRoot(), "src"), "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) || file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)) continue;
                if (file.EndsWith("IdFreshnessRules.cs", StringComparison.Ordinal) || file.EndsWith("QueryModelCommand.cs", StringComparison.Ordinal)) continue;
                Assert.DoesNotContain("holder_note", File.ReadAllText(file));
            }
        }

        // ------------------------------------------------------------------ m-1

        [Fact]
        public void A_row_of_a_linked_model_that_names_only_its_link_is_never_taken_for_a_host_id_and_the_command_always_names_the_link()
        {
            IdFreshnessRules.ResetForTests();
            IdFreshnessRules.NoteReply(Doc, T, "horizun_query_model", null, Rows((5001, "uidA")));
            IdFreshnessRules.NoteSynchronized(Doc, T);
            // return_fields without source_kind / link_instance_id used to leave a linked row looking like a host one; with the command's forced fields the row says so.
            var linkedOnlyKind = new JObject { ["rows"] = new JArray(new JObject { ["element_id"] = 5001, ["unique_id"] = "uid-in-link", ["source_kind"] = "link" }) };
            var linkedOnlyId = new JObject { ["rows"] = new JArray(new JObject { ["element_id"] = 5001, ["unique_id"] = "uid-in-link", ["link_instance_id"] = 12 }) };
            Assert.Null(IdFreshnessRules.NoteReply(Doc, T, "horizun_query_model", null, linkedOnlyKind));
            Assert.Null(IdFreshnessRules.NoteReply(Doc, T, "horizun_query_model", null, linkedOnlyId));
            var source = new QueryModelCommand.QueryDocument(new Document { Title = "Estructura" },
                Transform.Identity, null, 12);
            var projected = new JObject { ["element_id"] = 5001, ["unique_id"] = "uid-in-link" };
            source.WriteSourceFields(projected, new HashSet<string> { "name" });
            Assert.Equal("link", (string)projected["source_kind"]);
            Assert.Equal(12, (long)projected["link_instance_id"]);
            Assert.Null(IdFreshnessRules.NoteReply(Doc, T, "horizun_query_model", null,
                new JObject { ["rows"] = new JArray(projected) }));
            Assert.NotNull(IdFreshnessRules.Block(Doc));
            IdFreshnessRules.ResetForTests();
        }

        // ------------------------------------------------------------------ m-2

        [Fact]
        public void A_cancelled_close_loses_nothing_and_a_real_one_forgets_the_notice()
        {
            IdFreshnessRules.ResetForTests();
            IdFreshnessRules.NoteReply(Doc, T, "horizun_query_model", null, Rows((5001, "uidA")));
            IdFreshnessRules.NoteSynchronized(Doc, T);
            Assert.NotNull(IdFreshnessRules.Block(Doc));

            DocumentLifecycleRules.ClosingBegan(42, Doc);                                // DocumentClosing: the person may still say Cancel
            Assert.NotNull(IdFreshnessRules.Block(Doc));                                  // nothing is dropped yet (before R2W it was gone here)
            Assert.False(DocumentLifecycleRules.Closed(42, succeeded: false));            // DocumentClosed with Status Cancelled
            Assert.NotNull(IdFreshnessRules.Block(Doc));
            Assert.Equal(1, IdFreshnessRules.TrackedDocuments);

            DocumentLifecycleRules.ClosingBegan(43, Doc);                                // the second attempt goes through
            Assert.True(DocumentLifecycleRules.Closed(43, succeeded: true));
            Assert.Null(IdFreshnessRules.Block(Doc));
            Assert.Equal(0, IdFreshnessRules.TrackedDocuments);

            // Reopened in the same session: the notice starts empty (what was handed out before is forgotten, documented).
            IdFreshnessRules.NoteReply(Doc, T, "horizun_query_model", null, Rows((5001, "uidA")));
            Assert.Null(IdFreshnessRules.Block(Doc));
            Assert.False(DocumentLifecycleRules.Closed(999, true));                       // a close we never saw begin drops nothing and does not throw
            IdFreshnessRules.ResetForTests();
        }

        // ------------------------------------------------------------------ m-3

        [Fact]
        public void The_elements_a_delete_took_along_leave_the_notice_when_their_row_carries_the_unique_id_and_says_deleted()
        {
            IdFreshnessRules.ResetForTests();
            IdFreshnessRules.NoteReply(Doc, T, "horizun_query_model", null, Rows((1, "a"), (2, "b"), (3, "c")));

            var gone = new JObject { ["id"] = 2, ["cascaded_by"] = 1, ["confirmed_gone"] = true };
            CascadeRowRules.Mark(gone, "b", true, dryRun: false);
            var survives = new JObject { ["id"] = 3, ["cascaded_by"] = 1, ["confirmed_gone"] = false };
            CascadeRowRules.Mark(survives, "c", false, dryRun: false);
            var unknown = new JObject { ["id"] = 4, ["cascaded_by"] = 1, ["confirmed_gone"] = null };
            CascadeRowRules.Mark(unknown, null, null, dryRun: false);
            var rehearsal = new JObject { ["id"] = 9, ["confirmed_gone"] = true };
            CascadeRowRules.Mark(rehearsal, "z", true, dryRun: true);

            Assert.Equal("deleted", (string)gone["verdict"]);
            Assert.Equal("b", (string)gone["unique_id"]);
            Assert.Null(survives["verdict"]);
            Assert.Null(unknown["verdict"]);
            Assert.Null(rehearsal["verdict"]);                                            // a rehearsal deletes nothing

            var reply = new JObject
            {
                ["rows"] = new JArray(new JObject { ["id"] = 1, ["unique_id"] = "a", ["verdict"] = "deleted" }),
                ["cascades"] = new JArray(gone, survives, unknown)
            };
            IdFreshnessRules.NoteReply(Doc, T, "horizun_delete_verified", null, reply, isWrite: true);
            IdFreshnessRules.NoteSynchronized(Doc, T);
            Assert.Equal(1, (int)IdFreshnessRules.Block(Doc)["ids_to_revalidate"]);       // only 3 survives: 1 and 2 are gone, 4 never had a unique_id
            IdFreshnessRules.ResetForTests();
        }

        [Fact]
        public void The_delete_command_reads_the_cascade_unique_ids_in_the_rehearsal_and_marks_every_cascade_row()
        {
            string command = Source("src", "Horizun.Revit", "Commands", "DeleteCommand.cs");
            Assert.Contains("CascadeRowRules.Mark(cascadeRow, c.UniqueId, c.Gone, dryRun);", command);
            Assert.Contains("FillCascadeUniqueIds(doc, cascades);", command);
            Assert.Contains("FillCascadeUniqueIds(doc, planCascades);", command);
            Assert.Contains("CarryCascadeUniqueIds(planCascades, cascades);", command);
        }

        [Fact]
        public void Past_the_cap_a_no_existe_of_an_id_that_was_never_tracked_does_not_turn_the_notice_off()
        {
            IdFreshnessRules.ResetForTests();
            var rows = new JArray();
            for (int i = 1; i <= IdFreshnessRules.MaxTrackedIds + 5; i++) rows.Add(new JObject { ["element_id"] = i, ["unique_id"] = "u" + i });
            IdFreshnessRules.NoteReply(Doc, T, "horizun_model_scan", null, new JObject { ["rows"] = rows });
            IdFreshnessRules.NoteSynchronized(Doc, T);
            IdFreshnessRules.NoteReply(Doc, T, "horizun_model_scan", null, new JObject { ["rows"] = rows });
            Assert.True((bool)IdFreshnessRules.Block(Doc)["tracking_overflow"]);

            // Any id that does not exist answers no_existe: it proves nothing about what the session handed out.
            JObject missing = ElementIdentityGuard.Validate(JArray.Parse("[{\"id\":99999999}]"), id => null, uid => null);
            Assert.Equal("no_existe", (string)missing["results"][0]["status"]);
            IdFreshnessRules.NoteReply(Doc, T, "horizun_selection_context", "validate_ids", missing);
            Assert.NotNull(IdFreshnessRules.Block(Doc));

            // An id the session did hand out, proven (even gone), does.
            JObject held = ElementIdentityGuard.Validate(JArray.Parse("[{\"id\":7}]"), id => null, uid => null);
            IdFreshnessRules.NoteReply(Doc, T, "horizun_selection_context", "validate_ids", held);
            Assert.Null(IdFreshnessRules.Block(Doc));
            IdFreshnessRules.ResetForTests();
        }

        // ------------------------------------------------------------------ m-4

        // ------------------------------------------------------------------ m-5

        [Fact]
        public void The_widened_version_2_schema_has_its_own_guid_and_the_table_of_guids_is_documented()
        {
            string source = Source("src", "Horizun.Revit", "Core", "HrzOwnership.cs");
            const string v1 = "c5a8bf46-fb81-48fe-963e-fd79fb4b700d", r2u = "7e3b1d52-0c4a-4a8e-9f6b-2d51c8a3e9b7", now = "ea8c326c-9b78-4025-bf3e-b44621bdd938";
            Assert.Contains("SchemaGuid = new Guid(\"" + v1 + "\")", source);
            Assert.Contains("SchemaGuidV2 = new Guid(\"" + now + "\")", source);
            Assert.DoesNotContain("new Guid(\"" + r2u + "\")", source);                   // the R2U schema is only mentioned in the comment
            Assert.Contains("HorizunOwnershipUserV2", source);                            // and its name does not collide with the old one
            string guide = Source("docs", "IDENTITY-GUARD.md");
            foreach (string guid in new[] { v1, r2u, now }) Assert.Contains(guid, guide);
            Assert.Contains("NEVER published", guide);
        }

        // ------------------------------------------------------------------ m-6

        [Theory]
        [InlineData("horizun_transform_elements")]
        [InlineData("horizun_manage_views")]
        public void A_copy_made_inside_the_same_document_by_a_horizun_write_counts_as_ours(string tool)
        {
            SessionCreationLedger.ResetForTests();
            Assert.True(HrzOwnershipRules.CreationsAreOurs(tool));
            Assert.True(HrzOwnershipRules.ShouldStamp(tool, true, true, false));
            SessionCreationLedger.RecordFor(tool, "path:m", new[] { "dup-1" });
            Assert.True(SessionCreationLedger.Created("path:m", "dup-1"));
            // Its mark is of this session, and the ledger has the creation: trusted.
            Assert.True(HrzOwnershipRules.Owned("path:m", "dup-1", true, "s1", "user-a", "s1", "user-a"));
            SessionCreationLedger.ResetForTests();
            Assert.Contains("transform_elements` `copy`, `manage_views` `duplicate`", Source("docs", "IDENTITY-GUARD.md"));
        }
    }
}
