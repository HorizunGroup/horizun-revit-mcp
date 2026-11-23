using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class ElementIdentityGuardTests
    {
        private static readonly Dictionary<long, ElementIdentityFacts> Elements = new Dictionary<long, ElementIdentityFacts>
        {
            [10] = new ElementIdentityFacts { Owned = true, Id = 10, UniqueId = "other", Category = "Text Notes", Type = "B" },
            [20] = new ElementIdentityFacts { Owned = true, Id = 20, UniqueId = "wanted", Category = "Text Notes", Type = "A" }
        };

        private static ElementIdentityFacts ById(long id) => Elements.TryGetValue(id, out var e) ? e : null;
        private static ElementIdentityFacts ByUid(string uid) => uid == "wanted" ? Elements[20] : uid == "other" ? Elements[10] : null;

        [Fact]
        public void Reused_id_is_refused_before_any_batch_write()
        {
            var rows = JArray.Parse("[{\"target_id\":20},{\"target_id\":10,\"unique_id\":\"wanted\"}]");
            int writes = 0;
            var error = ElementIdentityGuard.Resolve(rows, ById, ByUid);
            if (error == null) writes = rows.Count;
            Assert.Equal(0, writes);
            Assert.Equal("elemento_no_coincide", (string)error["reason"]);
            Assert.Equal("wanted", (string)error["expected"]["unique_id"]);
            Assert.Equal("other", (string)error["found"]["unique_id"]);
        }

        [Fact]
        public void Unique_id_resolves_to_current_id()
        {
            var rows = JArray.Parse("[{\"unique_id\":\"wanted\"}]");
            Assert.Null(ElementIdentityGuard.Resolve(rows, ById, ByUid));
            Assert.Equal(20L, (long)rows[0]["target_id"]);
        }

        [Fact]
        public void Bare_id_preserves_existing_behavior()
        {
            var rows = JArray.Parse("[{\"target_id\":10}]");
            Assert.Null(ElementIdentityGuard.Resolve(rows, ById, ByUid));
            Assert.Equal(10L, (long)rows[0]["target_id"]);
        }

        [Fact]
        public void Expected_type_mismatch_is_refused()
        {
            var rows = JArray.Parse("[{\"target_id\":10,\"expected_type\":\"A\"}]");
            Assert.NotNull(ElementIdentityGuard.Resolve(rows, ById, ByUid));
        }

        [Fact]
        public void A_late_mismatch_does_not_even_normalize_an_earlier_row()
        {
            var rows = JArray.Parse("[{\"unique_id\":\"wanted\"},{\"target_id\":10,\"unique_id\":\"wanted\"}]");
            Assert.NotNull(ElementIdentityGuard.Resolve(rows, ById, ByUid));
            Assert.Null(rows[0]["target_id"]);
        }

        [Fact]
        public void Unknown_uid_and_conflicting_project_target_are_refused()
        {
            Assert.NotNull(ElementIdentityGuard.Resolve(JArray.Parse("[{\"unique_id\":\"missing\"}]"), ById, ByUid));
            Assert.NotNull(ElementIdentityGuard.Resolve(JArray.Parse("[{\"unique_id\":\"wanted\",\"target\":\"project_info\"}]"), ById, ByUid));
        }

        private static JObject Req(string json) => JObject.Parse(json);
        private static JObject Check(JObject r, string tool) => ElementIdentityGuard.ResolveRequest(r, tool, ById, ByUid, () => false);

        [Fact]
        public void Delete_with_reused_id_is_refused_and_nothing_is_normalized()
        {
            var r = Req("{\"mode\":\"ids\",\"ids\":[20,10],\"unique_ids\":[\"wanted\",\"wanted\"]}");
            var error = Check(r, "horizun_delete_verified");
            Assert.Equal("elemento_no_coincide", (string)error["reason"]);
            Assert.Equal(1, (int)error["position"]);
            Assert.Equal("other", (string)error["found"]["unique_id"]);
            Assert.NotNull(r["unique_ids"]);
            Assert.False((bool)error["write_started"]);
        }

        [Fact]
        public void Delete_with_matching_unique_ids_is_consumed_and_ids_stay()
        {
            var r = Req("{\"mode\":\"ids\",\"ids\":[20,10],\"unique_ids\":[\"wanted\",\"other\"]}");
            Assert.Null(Check(r, "horizun_delete_verified"));
            Assert.Null(r["unique_ids"]);
            Assert.Equal(new long[] { 20, 10 }, r["ids"].Select(t => (long)t).ToArray());
        }

        [Fact]
        public void Delete_with_unique_ids_alone_resolves_ids()
        {
            var r = Req("{\"mode\":\"ids\",\"unique_ids\":[\"wanted\",\"other\"]}");
            Assert.Null(Check(r, "horizun_delete_verified"));
            Assert.Equal(new long[] { 20, 10 }, r["ids"].Select(t => (long)t).ToArray());
        }

        [Fact]
        public void Keynote_unique_ids_alone_resolve_element_ids_and_unknown_uid_refuses()
        {
            var ok = Req("{\"unique_ids\":[\"other\"],\"keynote\":\"K\"}");
            Assert.Null(Check(ok, "horizun_set_keynote"));
            Assert.Equal(10L, (long)ok["element_ids"][0]);
            var bad = Req("{\"unique_ids\":[\"gone\"],\"keynote\":\"K\"}");
            Assert.NotNull(Check(bad, "horizun_set_keynote"));
            Assert.Null(bad["element_ids"]);
        }

        [Fact]
        public void Expected_category_and_type_apply_to_every_id_of_the_list()
        {
            Assert.Null(Check(Req("{\"element_ids\":[10,20],\"expected_category\":\"text notes\",\"keynote\":\"K\"}"), "horizun_set_keynote"));
            var error = Check(Req("{\"element_ids\":[10,20],\"expected_type\":\"A\",\"keynote\":\"K\"}"), "horizun_set_keynote");
            Assert.Equal(0, (int)error["position"]);
            Assert.Equal("B", (string)error["found"]["type"]);
        }

        [Fact]
        public void Transform_operation_with_reused_id_is_refused_before_any_operation()
        {
            var r = Req("{\"operations\":[{\"operation\":\"pin\",\"element_ids\":[20],\"unique_ids\":[\"wanted\"]}," +
                        "{\"operation\":\"move\",\"element_ids\":[10],\"unique_ids\":[\"wanted\"]}]}");
            var error = Check(r, "horizun_transform_elements");
            Assert.Equal("elemento_no_coincide", (string)error["reason"]);
            Assert.Equal("operations[1]", (string)error["path"]);
            Assert.NotNull(r["operations"][0]["unique_ids"]);
        }

        [Fact]
        public void Plan_child_arguments_are_checked_by_the_plan_gate()
        {
            var bad = Req("{\"actions\":[{\"key\":\"a\",\"tool\":\"horizun_delete_verified\",\"arguments\":{\"mode\":\"ids\",\"ids\":[10],\"unique_ids\":[\"wanted\"]}}]}");
            var error = Check(bad, "horizun_execute_plan");
            Assert.Equal("actions[0].arguments", (string)error["path"]);
            var good = Req("{\"actions\":[{\"key\":\"a\",\"tool\":\"horizun_write_params_verified\",\"arguments\":{\"writes\":[{\"unique_id\":\"wanted\",\"parameter\":\"p\",\"value\":1}]}}]}");
            Assert.Null(Check(good, "horizun_execute_plan"));
            Assert.Equal(20L, (long)good["actions"][0]["arguments"]["writes"][0]["target_id"]);
        }

        [Fact]
        public void Requests_without_expectations_and_uncovered_tools_are_untouched()
        {
            var plain = Req("{\"mode\":\"ids\",\"ids\":[10,99]}");
            var copy = plain.DeepClone();
            Assert.Null(Check(plain, "horizun_delete_verified"));
            Assert.True(JToken.DeepEquals(copy, plain));
            // A tool that is neither verified nor refused keeps its request exactly as sent when it carries no identity.
            var untouched = Req("{\"element_id\":10}");
            Assert.Null(Check(untouched, "horizun_annotate"));
            Assert.Equal(10L, (long)untouched["element_id"]);
        }

        [Fact]
        public void Unresolved_reference_next_to_an_identity_is_refused()
        {
            var r = Req("{\"element_ids\":[\"${scan.rows.0.element_id}\"],\"unique_ids\":[\"wanted\"],\"keynote\":\"K\"}");
            Assert.NotNull(Check(r, "horizun_set_keynote"));
        }

        [Theory]
        [InlineData("horizun_delete_verified")]
        [InlineData("horizun_set_keynote")]
        [InlineData("horizun_transform_elements")]
        [InlineData("horizun_write_params_verified")]
        [InlineData("horizun_annotate")]
        [InlineData("horizun_manage_views")]
        [InlineData("horizun_fix_planimetry")]
        [InlineData("horizun_edit_dimensions")]
        [InlineData("horizun_detail_2d")]
        [InlineData("horizun_manage_groups")]
        public void Every_declaring_tool_schema_declares_the_identity_fields(string tool)
        {
            string schema = Horizun.Contracts.Contract.Find(tool).InputSchema.ToString();
            Assert.Contains("\"expected_category\"", schema);
            Assert.Contains("\"expected_type\"", schema);
            Assert.True(schema.Contains("\"unique_ids\"") || schema.Contains("\"unique_id\""));
            Assert.True(ElementIdentityGuard.Covers(tool));
        }

        // ---------------------------------------------------------------- M1: every writer that takes ids

        private static readonly Dictionary<long, ElementIdentityFacts> More = new Dictionary<long, ElementIdentityFacts>
        {
            [30] = new ElementIdentityFacts { Owned = true, Id = 30, UniqueId = "view-a", Category = "Views", Type = "Floor Plan", Family = "Floor Plan" },
            [31] = new ElementIdentityFacts { Owned = true, Id = 31, UniqueId = "view-b", Category = "Views", Type = "Floor Plan", Family = "Floor Plan" },
            [40] = new ElementIdentityFacts { Owned = true, Id = 40, UniqueId = "wall-a", Category = "Walls", Type = "Generic - 200mm", Family = "Basic Wall" },
            [41] = new ElementIdentityFacts { Owned = true, Id = 41, UniqueId = "wall-b", Category = "Walls", Type = "Standard", Family = "Curtain Wall" },
            [50] = new ElementIdentityFacts { Owned = true, Id = 50, UniqueId = "link-a", Category = "RVT Links", Type = "arch.rvt", Family = "RVT Link" }
        };

        private static ElementIdentityFacts ById2(long id) => More.TryGetValue(id, out var e) ? e : ById(id);
        private static ElementIdentityFacts ByUid2(string uid) => More.Values.FirstOrDefault(v => v.UniqueId == uid) ?? ByUid(uid);
        private static JObject Check2(JObject r, string tool) => ElementIdentityGuard.ResolveRequest(r, tool, ById2, ByUid2, () => false);

        [Fact]
        public void Annotate_tag_with_a_reused_host_id_is_refused_and_text_is_checked_against_its_view()
        {
            var bad = Req("{\"actions\":[{\"operation\":\"tag\",\"view_id\":30,\"element_id\":41,\"unique_id\":\"wall-a\"}]}");
            var error = Check2(bad, "horizun_annotate");
            Assert.Equal("elemento_no_coincide", (string)error["reason"]);
            Assert.Equal("actions[0]", (string)error["path"]);
            Assert.Equal("wall-b", (string)error["found"]["unique_id"]);
            Assert.NotNull(bad["actions"][0]["unique_id"]);

            var good = Req("{\"actions\":[{\"operation\":\"text\",\"view_id\":30,\"unique_id\":\"view-a\",\"text\":\"x\"}," +
                           "{\"operation\":\"tag\",\"view_id\":30,\"unique_id\":\"wall-a\"}]}");
            Assert.Null(Check2(good, "horizun_annotate"));
            Assert.Null(good["actions"][0]["unique_id"]);
            Assert.Equal(40L, (long)good["actions"][1]["element_id"]);

            var wrongView = Req("{\"actions\":[{\"operation\":\"text\",\"view_id\":31,\"unique_id\":\"view-a\",\"text\":\"x\"}]}");
            Assert.Equal("elemento_no_coincide", (string)Check2(wrongView, "horizun_annotate")["reason"]);
        }

        [Fact]
        public void Annotate_tag_checks_every_further_element_of_one_tag()
        {
            // 41 was renumbered: it now names wall-b, a wall like the one meant. Only the unique_id tells them apart.
            var bare = Req("{\"actions\":[{\"operation\":\"tag\",\"view_id\":30,\"element_id\":40,\"element_ids\":[41],\"unique_ids\":[\"wall-a\"]}]}");
            var error = Check2(bare, "horizun_annotate");
            Assert.Equal("elemento_no_coincide", (string)error["reason"]);
            Assert.Equal("wall-b", (string)error["found"]["unique_id"]);

            var named = Req("{\"actions\":[{\"operation\":\"tag\",\"view_id\":30,\"element_id\":40,\"element_ids\":[41]," +
                            "\"identity\":{\"element_id\":\"wall-a\",\"element_ids\":[\"wall-a\"]}}]}");
            Assert.Equal("elemento_no_coincide", (string)Check2(named, "horizun_annotate")["reason"]);

            var good = Req("{\"actions\":[{\"operation\":\"tag\",\"view_id\":30,\"element_id\":40,\"element_ids\":[41]," +
                           "\"identity\":{\"element_id\":\"wall-a\",\"element_ids\":[\"wall-b\"]}}]}");
            Assert.Null(Check2(good, "horizun_annotate"));
            Assert.Contains("element_ids", ElementIdentityGuard.DocumentedKeys("horizun_annotate", true));
        }

        [Fact]
        public void Manage_views_bare_unique_id_checks_the_element_list_else_the_first_view_key_and_the_sheet_needs_identity()
        {
            var hide = Req("{\"actions\":[{\"operation\":\"hide_elements\",\"view_id\":30,\"element_ids\":[40,41],\"unique_ids\":[\"wall-a\",\"wall-a\"]}]}");
            var error = Check2(hide, "horizun_manage_views");
            Assert.Equal(1, (int)error["position"]);
            Assert.Equal("actions[0]", (string)error["path"]);
            var place = Req("{\"actions\":[{\"operation\":\"place_view\",\"view_id\":31,\"sheet_id\":30,\"unique_id\":\"view-a\"}]}");
            Assert.Equal("elemento_no_coincide", (string)Check2(place, "horizun_manage_views")["reason"]);
            var ok = Req("{\"actions\":[{\"operation\":\"place_view\",\"view_id\":31,\"sheet_id\":30,\"unique_id\":\"view-b\"}]}");
            Assert.Null(Check2(ok, "horizun_manage_views"));
            Assert.Null(ok["actions"][0]["unique_id"]);
        }

        [Theory]
        [InlineData("horizun_fix_planimetry", "{\"actions\":[{\"operation\":\"move_viewport\",\"viewport_id\":41,\"unique_id\":\"wall-a\"}]}")]
        [InlineData("horizun_edit_dimensions", "{\"actions\":[{\"element_id\":41,\"unique_id\":\"wall-a\"}]}")]
        [InlineData("horizun_detail_2d", "{\"actions\":[{\"operation\":\"set_line_style\",\"element_id\":41,\"unique_id\":\"wall-a\"}]}")]
        [InlineData("horizun_manage_groups", "{\"operation\":\"ungroup\",\"group_ids\":[41],\"unique_ids\":[\"wall-a\"]}")]
        [InlineData("horizun_create_elements", "{\"elements\":[{\"kind\":\"door\",\"host_id\":41,\"unique_id\":\"wall-a\"}]}")]
        public void Each_remaining_writer_refuses_a_reused_id_before_writing(string tool, string json)
        {
            var r = Req(json);
            var error = Check2(r, tool);
            Assert.Equal("elemento_no_coincide", (string)error["reason"]);
            Assert.False((bool)error["write_started"]);
            Assert.Equal("wall-b", (string)error["found"]["unique_id"]);
        }

        [Fact]
        public void A_tool_with_no_default_id_field_cannot_resolve_a_unique_id_alone()
        {
            var r = Req("{\"actions\":[{\"operation\":\"hide_elements\",\"view_id\":30,\"unique_ids\":[\"wall-a\"]}]}");
            var error = Check2(r, "horizun_manage_views");
            Assert.Equal("entrada_invalida", (string)error["error_code"]);
            Assert.Contains("alone", (string)error["detail"]);
        }

        [Fact]
        public void Plan_children_of_the_new_tools_are_checked_by_the_plan_gate()
        {
            var bad = Req("{\"actions\":[{\"key\":\"a\",\"tool\":\"horizun_annotate\",\"arguments\":{\"actions\":[{\"operation\":\"tag\",\"view_id\":30,\"element_id\":41,\"unique_id\":\"wall-a\"}]}}]}");
            var error = Check2(bad, "horizun_execute_plan");
            Assert.Equal("elemento_no_coincide", (string)error["reason"]);
            Assert.Equal("actions[0].arguments.actions[0]", (string)error["path"]);
            var unsupported = Req("{\"actions\":[{\"key\":\"a\",\"tool\":\"horizun_family_apply\",\"arguments\":{\"unique_id\":\"x\"}}]}");
            Assert.Equal("entrada_invalida", (string)Check2(unsupported, "horizun_execute_plan")["error_code"]);
        }

        [Theory]
        [InlineData("horizun_create_schedule")]
        public void A_writer_without_ids_to_check_refuses_identity_fields_instead_of_ignoring_them(string tool)
        {
            var r = Req("{\"target_document\":\"d\",\"unique_id\":\"x\"}");
            var error = ElementIdentityGuard.RejectUnsupported(r, tool);
            Assert.NotNull(error);
            Assert.Equal("entrada_invalida", (string)error["error_code"]);
            Assert.False((bool)error["write_started"]);
            Assert.Null(ElementIdentityGuard.RejectUnsupported(Req("{\"target_document\":\"d\"}"), tool));
        }

        [Fact]
        public void Execute_python_is_exempt_because_its_arguments_are_the_scripts_own_and_covered_tools_are_not_double_checked()
        {
            Assert.Null(ElementIdentityGuard.RejectUnsupported(Req("{\"code\":\"x\",\"unique_id\":\"x\"}"), "horizun_execute_python"));
            Assert.Null(ElementIdentityGuard.RejectUnsupported(Req("{\"unique_id\":\"x\"}"), "horizun_delete_verified"));
            Assert.False(ElementIdentityGuard.Covers("horizun_execute_python"));
        }

        // ---------------------------------------------------------------- minor findings

        [Fact]
        public void M1_a_whole_value_reference_in_the_id_list_is_refused_and_never_overwritten()
        {
            var r = Req("{\"element_ids\":\"${scan.ids}\",\"unique_ids\":[\"wanted\"],\"keynote\":\"K\"}");
            var error = Check(r, "horizun_set_keynote");
            Assert.Equal("entrada_invalida", (string)error["error_code"]);
            Assert.Contains("element_ids", (string)error["detail"]);
            Assert.Equal("${scan.ids}", (string)r["element_ids"]);
            Assert.NotNull(r["unique_ids"]);
            var del = Req("{\"mode\":\"ids\",\"ids\":\"${scan.ids}\",\"unique_ids\":[\"wanted\"]}");
            Assert.Equal("entrada_invalida", (string)Check(del, "horizun_delete_verified")["error_code"]);
            Assert.Equal("${scan.ids}", (string)del["ids"]);
        }

        [Fact]
        public void M3_expected_type_accepts_family_colon_type_and_compares_it_whole()
        {
            Assert.Null(Check2(Req("{\"element_ids\":[40],\"expected_type\":\"Basic Wall: Generic - 200mm\",\"keynote\":\"K\"}"), "horizun_set_keynote"));
            Assert.Null(Check2(Req("{\"element_ids\":[40],\"expected_type\":\"basic wall:generic - 200mm\",\"keynote\":\"K\"}"), "horizun_set_keynote"));
            Assert.Null(Check2(Req("{\"element_ids\":[40],\"expected_type\":\"Generic - 200mm\",\"keynote\":\"K\"}"), "horizun_set_keynote"));
            // Same type name in another family: only the whole label tells them apart.
            Assert.Null(Check2(Req("{\"element_ids\":[41],\"expected_type\":\"Curtain Wall: Standard\",\"keynote\":\"K\"}"), "horizun_set_keynote"));
            var wrongFamily = Check2(Req("{\"element_ids\":[41],\"expected_type\":\"Basic Wall: Standard\",\"keynote\":\"K\"}"), "horizun_set_keynote");
            Assert.Equal("elemento_no_coincide", (string)wrongFamily["reason"]);
            Assert.Equal("Curtain Wall", (string)wrongFamily["found"]["family"]);
        }

        [Fact]
        public void M5_identity_fields_on_a_selector_or_a_mode_without_ids_are_an_argument_error()
        {
            foreach (string json in new[]
            {
                "{\"mode\":\"purge_unused\",\"expected_category\":\"Walls\"}",
                "{\"mode\":\"query\",\"selector\":{\"category\":\"Walls\"},\"expected_type\":\"A\"}"
            })
            {
                var error = Check(Req(json), "horizun_delete_verified");
                Assert.Equal("entrada_invalida", (string)error["error_code"]);
                Assert.Equal("identidad_invalida", (string)error["reason"]);
                Assert.NotNull(error["detail"]);
            }
            var write = Check(Req("{\"selector\":{\"category\":\"Walls\"},\"expected_category\":\"Walls\",\"set\":{\"p\":1}}"), "horizun_write_params_verified");
            Assert.Equal("entrada_invalida", (string)write["error_code"]);
            Assert.Contains("selector", (string)write["detail"]);
        }

        [Fact]
        public void M2_category_name_has_one_rule_with_the_text_note_type_fallback()
        {
            Assert.Equal("Walls", ElementIdentityGuard.CategoryNameFor(() => "Walls", false, () => "Text Notes"));
            Assert.Null(ElementIdentityGuard.CategoryNameFor(() => null, false, () => "Text Notes"));
            Assert.Equal("Text Notes", ElementIdentityGuard.CategoryNameFor(() => null, true, () => "Text Notes"));
            Assert.Equal("OST_TextNotes", ElementIdentityGuard.CategoryNameFor(() => null, true, () => null));
            Assert.Equal("Text Notes", ElementIdentityGuard.CategoryNameFor(() => throw new System.InvalidOperationException(), true, () => "Text Notes"));
        }

        // ---------------------------------------------------------------- validate_ids

        [Fact]
        public void Validate_ids_answers_match_changed_and_missing_per_entry()
        {
            var checks = JArray.Parse("[" +
                "{\"id\":20,\"unique_id\":\"wanted\",\"expected_category\":\"Text Notes\",\"expected_type\":\"A\"}," +   // 0 match
                "{\"id\":10,\"unique_id\":\"wanted\"}," +                                                                // 1 the id now names another element
                "{\"id\":999,\"unique_id\":\"wanted\"}," +                                                               // 2 renumbered: the uid lives under 20
                "{\"id\":999,\"unique_id\":\"gone\"}," +                                                                 // 3 missing
                "{\"unique_id\":\"other\"}," +                                                                           // 4 uid alone resolves
                "{\"id\":20,\"expected_type\":\"B\"}," +                                                                 // 5 type moved
                "{\"id\":\"x\"}," +                                                                                      // 6 invalid
                "{\"expected_type\":\"A\"}" +                                                                            // 7 invalid
                "]");
            var result = ElementIdentityGuard.Validate(checks, ById, ByUid);
            var rows = (JArray)result["results"];
            Assert.Equal("coincide", (string)rows[0]["status"]);
            Assert.Equal("cambio", (string)rows[1]["status"]);
            Assert.StartsWith("id_reasignado", (string)rows[1]["reason"]);
            Assert.Equal(10L, (long)rows[1]["found"]["id"]);
            Assert.Equal("cambio", (string)rows[2]["status"]);
            Assert.StartsWith("id_renumerado", (string)rows[2]["reason"]);
            Assert.Equal(20L, (long)rows[2]["current_id"]);
            Assert.Equal("no_existe", (string)rows[3]["status"]);
            Assert.Equal("coincide", (string)rows[4]["status"]);
            Assert.Equal(10L, (long)rows[4]["current_id"]);
            Assert.Equal("cambio", (string)rows[5]["status"]);
            Assert.Equal("entrada_invalida", (string)rows[6]["status"]);
            Assert.Equal("entrada_invalida", (string)rows[7]["status"]);
            var summary = result["summary"];
            Assert.Equal(8, (int)summary["checked"]);
            Assert.Equal(2, (int)summary["coincide"]);
            Assert.Equal(3, (int)summary["cambio"]);
            Assert.Equal(1, (int)summary["no_existe"]);
            Assert.Equal(2, (int)summary["entrada_invalida"]);
            Assert.False((bool)summary["all_match"]);
        }

        [Fact]
        public void Validate_ids_reports_a_reused_id_with_the_element_that_took_it_and_where_the_wanted_one_lives()
        {
            var r = ElementIdentityGuard.Validate(JArray.Parse("[{\"id\":10,\"unique_id\":\"wanted\"}]"), ById, ByUid);
            var row = (JObject)r["results"][0];
            Assert.Equal("cambio", (string)row["status"]);
            Assert.Equal("other", (string)row["found"]["holder_unique_id"]);
            Assert.Equal(20L, (long)row["current_id"]);
        }

        [Fact]
        public void Validate_ids_all_match_only_when_every_entry_matches()
        {
            var r = ElementIdentityGuard.Validate(JArray.Parse("[{\"id\":20,\"unique_id\":\"wanted\"},{\"id\":10,\"unique_id\":\"other\"}]"), ById, ByUid);
            Assert.True((bool)r["summary"]["all_match"]);
            // An id with nothing to compare is not a match (R2Q M-3).
            var idOnly = ElementIdentityGuard.Validate(JArray.Parse("[{\"id\":20,\"unique_id\":\"wanted\"},{\"id\":10}]"), ById, ByUid);
            Assert.False((bool)idOnly["summary"]["all_match"]);
        }

        [Fact]
        public void Validate_ids_is_declared_in_the_selection_context_schema_and_operation_list()
        {
            string schema = Horizun.Contracts.Contract.Find("horizun_selection_context").InputSchema.ToString();
            Assert.Contains("validate_ids", schema);
            Assert.Contains("\"checks\"", schema);
            Assert.Contains("validate_ids", SelectionContextOps.All);
        }
    }

    public class IdFreshnessRulesTests
    {
        [Fact]
        public void A_synchronize_after_the_ids_were_handed_out_raises_the_notice_and_a_read_that_returns_them_again_clears_it()
        {
            IdFreshnessRules.ResetForTests();
            const string doc = "path:c:/models/a.rvt";
            Assert.Null(IdFreshnessRules.Block(doc));
            var rows = Newtonsoft.Json.Linq.JObject.Parse("{\"rows\":[{\"element_id\":1,\"unique_id\":\"a\"}]}");
            IdFreshnessRules.NoteReply(doc, System.DateTime.UtcNow, "horizun_query_model", null, rows);          // a read before any sync raises nothing
            Assert.Null(IdFreshnessRules.Block(doc));
            IdFreshnessRules.NoteSynchronized(doc, System.DateTime.UtcNow);
            var block = IdFreshnessRules.Block(doc);
            Assert.True((bool)block["ids_may_have_changed"]);
            Assert.Equal(1, (int)block["syncs_since_last_read"]);
            Assert.Contains("validate_ids", (string)block["note"]);
            IdFreshnessRules.NoteSynchronized(doc, System.DateTime.UtcNow);
            Assert.Equal(2, (int)IdFreshnessRules.Block(doc)["syncs_since_last_read"]);
            Assert.Null(IdFreshnessRules.Block("path:c:/models/other.rvt"));   // per document
            IdFreshnessRules.NoteReply(doc, System.DateTime.UtcNow, "horizun_query_model", null, rows);
            Assert.Null(IdFreshnessRules.Block(doc));
        }

        [Theory]
        [InlineData("horizun_query_model", true)]
        [InlineData("horizun_query_planimetry", true)]
        [InlineData("horizun_selection_context", true)]
        [InlineData("horizun_health", false)]
        [InlineData("horizun_get_document_info", false)]
        [InlineData("horizun_set_keynote", false)]
        public void Only_reads_that_return_element_ids_clear_the_notice(string tool, bool reads)
        {
            Assert.Equal(reads, IdFreshnessRules.ReadsIds(tool));
        }
    }
}
