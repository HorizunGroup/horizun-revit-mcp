using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Horizun.Contracts;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    // R2Q - closing P1-4 (renumbered ids): the observations of the second independent review (M-1..M-4, m-1..m-5).
    public class ElementIdentityGuardR2QTests
    {
        private static readonly Dictionary<long, ElementIdentityFacts> Elements = new Dictionary<long, ElementIdentityFacts>
        {
            [20] = new ElementIdentityFacts { Owned = true, Id = 20, UniqueId = "wanted", Category = "Text Notes", CategoryBuiltIn = "OST_TextNotes", Type = "A" },
            [30] = new ElementIdentityFacts { Owned = true, Id = 30, UniqueId = "view-a", Category = "Views", CategoryBuiltIn = "OST_Views", Type = "Floor Plan", Family = "Floor Plan" },
            [31] = new ElementIdentityFacts { Owned = true, Id = 31, UniqueId = "view-b", Category = "Views", CategoryBuiltIn = "OST_Views", Type = "Floor Plan", Family = "Floor Plan" },
            [40] = new ElementIdentityFacts { Owned = true, Id = 40, UniqueId = "wall-a", Category = "Walls", CategoryBuiltIn = "OST_Walls", Type = "Generic - 200mm", Family = "Basic Wall" },
            [41] = new ElementIdentityFacts { Owned = true, Id = 41, UniqueId = "wall-b", Category = "Walls", CategoryBuiltIn = "OST_Walls", Type = "Standard", Family = "Curtain Wall" },
            [50] = new ElementIdentityFacts { Owned = true, Id = 50, UniqueId = "topo-a", Category = "Topography", CategoryBuiltIn = "OST_Topography", Type = "Toposolid", Family = "Toposolid" },
            [51] = new ElementIdentityFacts { Owned = true, Id = 51, UniqueId = "topo-b", Category = "Topography", CategoryBuiltIn = "OST_Topography", Type = "Toposolid", Family = "Toposolid" }
        };

        private static ElementIdentityFacts ById(long id) => Elements.TryGetValue(id, out var e) ? e : null;
        private static ElementIdentityFacts ByUid(string uid) => Elements.Values.FirstOrDefault(v => v.UniqueId == uid);
        private static JObject Req(string json) => JObject.Parse(json);
        private static JObject Check(JObject r, string tool) => ElementIdentityGuard.ResolveRequest(r, tool, ById, ByUid, () => false);

        private static void AssertMismatch(JObject error, string key, string foundUid)
        {
            Assert.NotNull(error);
            Assert.Equal("precondicion_no_cumplida", (string)error["error_code"]);
            Assert.Equal("elemento_no_coincide", (string)error["reason"]);
            Assert.Equal(key, (string)error["key"]);
            Assert.Equal(foundUid, (string)error["found"]["unique_id"]);
            Assert.False((bool)error["write_started"]);
        }

        // ------------------------------------------------------------------ M-1: the whole tree

        [Fact]
        public void M1_a_writer_without_ids_refuses_identity_fields_at_any_depth()
        {
            var deep = Req("{\"fields\":[{\"a\":1},{\"nested\":{\"unique_id\":\"x\"}}]}");
            var error = ElementIdentityGuard.RejectUnsupported(deep, "horizun_create_schedule");
            Assert.Equal("entrada_invalida", (string)error["error_code"]);
            Assert.Equal("fields[1].nested", (string)error["path"]);
            Assert.False((bool)error["write_started"]);
            var viaMap = Req("{\"items\":[{\"identity\":{\"view_id\":\"v\"}}]}");
            Assert.Equal("items[0]", (string)ElementIdentityGuard.RejectUnsupported(viaMap, "horizun_create_schedule")["path"]);
            Assert.Null(ElementIdentityGuard.RejectUnsupported(Req("{\"fields\":[{\"a\":1}]}"), "horizun_create_schedule"));
        }

        [Fact]
        public void An_identity_text_a_tool_defines_for_itself_is_not_the_identity_map()
        {
            // require_gate.override.identity is WHO signed an override (save_document, export); plan_views has a top-level one.
            var save = Req("{\"target_document\":\"d\",\"require_gate\":{\"profile\":{},\"override\":{\"identity\":\"ana\"}}}");
            Assert.Null(ElementIdentityGuard.RejectUnsupported(save, "horizun_save_document"));
            Assert.Null(Check(Req("{\"format\":\"ifc\",\"require_gate\":{\"override\":{\"identity\":\"ana\"}}}"), "horizun_export"));
            Assert.Null(ElementIdentityGuard.RejectUnsupported(Req("{\"operation\":\"delivery_approve\",\"identity\":\"ana\"}"), "horizun_plan_views"));
            // ... but the object form is the map everywhere, and a text identity on any other tool is refused, not ignored.
            var nonText = Req("{\"view_id\":31,\"identity\":\"view-a\"}");
            Assert.Equal("entrada_invalida", (string)Check(nonText, "horizun_link_schedule")["error_code"]);
            Assert.Equal("entrada_invalida", (string)ElementIdentityGuard.RejectUnsupported(Req("{\"identity\":\"x\"}"), "horizun_create_schedule")["error_code"]);
            AssertMismatch(Check(Req("{\"format\":\"pdf\",\"view_ids\":[31],\"identity\":{\"view_ids\":[\"view-a\"]}}"), "horizun_export"), "view_ids", "view-b");
        }

        [Fact]
        public void M1_execute_python_stays_exempt_at_any_depth()
        {
            Assert.Null(ElementIdentityGuard.RejectUnsupported(Req("{\"inputs\":{\"rows\":[{\"unique_id\":\"x\"}]}}"), "horizun_execute_python"));
        }

        [Fact]
        public void M1_plan_children_are_walked_at_every_depth_and_the_plan_levels_refuse_identity()
        {
            var deepChild = Req("{\"actions\":[{\"key\":\"a\",\"tool\":\"horizun_family_apply\",\"arguments\":{\"rows\":[{\"deeper\":{\"unique_ids\":[\"x\"]}}]}}]}");
            var error = Check(deepChild, "horizun_execute_plan");
            Assert.Equal("entrada_invalida", (string)error["error_code"]);
            Assert.Equal("actions[0].arguments.rows[0].deeper", (string)error["path"]);

            Assert.Equal("entrada_invalida", (string)Check(Req("{\"unique_id\":\"x\",\"actions\":[]}"), "horizun_execute_plan")["error_code"]);
            var besideTool = Req("{\"actions\":[{\"key\":\"a\",\"tool\":\"horizun_set_keynote\",\"unique_id\":\"wanted\",\"arguments\":{\"element_ids\":[20],\"keynote\":\"K\"}}]}");
            var e2 = Check(besideTool, "horizun_execute_plan");
            Assert.Equal("entrada_invalida", (string)e2["error_code"]);
            Assert.Equal("actions[0]", (string)e2["path"]);
            var nested = Req("{\"actions\":[{\"key\":\"a\",\"tool\":\"horizun_execute_plan\",\"arguments\":{\"actions\":[{\"key\":\"b\",\"tool\":\"horizun_set_keynote\",\"arguments\":{\"element_ids\":[40],\"unique_ids\":[\"wall-b\"],\"keynote\":\"K\"}}]}}]}");
            Assert.Equal("elemento_no_coincide", (string)Check(nested, "horizun_execute_plan")["reason"]);
        }

        [Fact]
        public void M1_a_covered_tool_checks_every_nested_node_that_carries_identity()
        {
            var bad = Req("{\"target_document\":\"d\",\"sheets\":[{\"sheet_id\":30}],\"items\":[{\"view_id\":31},{\"view_id\":30,\"identity\":{\"view_id\":\"view-b\"}}]}");
            var error = Check(bad, "horizun_pack_sheets");
            AssertMismatch(error, "view_id", "view-a");
            Assert.Equal("items[1]", (string)error["path"]);
            Assert.Equal(1, (int)error["index"]);
        }

        // ------------------------------------------------------------------ M-2: identity by key

        [Fact]
        public void M2_place_view_verifies_the_sheet_as_well_as_the_view()
        {
            var r = Req("{\"actions\":[{\"operation\":\"place_view\",\"view_id\":31,\"sheet_id\":30,\"identity\":{\"view_id\":\"view-b\",\"sheet_id\":\"view-b\"}}]}");
            AssertMismatch(Check(r, "horizun_manage_views"), "sheet_id", "view-a");
            Assert.NotNull(r["actions"][0]["identity"]);   // nothing is consumed on a refusal
            var ok = Req("{\"actions\":[{\"operation\":\"place_view\",\"view_id\":31,\"sheet_id\":30,\"identity\":{\"view_id\":\"view-b\",\"sheet_id\":\"view-a\"}}]}");
            Assert.Null(Check(ok, "horizun_manage_views"));
            Assert.Null(ok["actions"][0]["identity"]);
            Assert.Equal(30L, (long)ok["actions"][0]["sheet_id"]);
        }

        [Fact]
        public void M2_hide_isolate_and_overrides_verify_the_view_and_the_elements()
        {
            var badView = Req("{\"actions\":[{\"operation\":\"hide_elements\",\"view_id\":31,\"element_ids\":[40,41],\"identity\":{\"view_id\":\"view-a\",\"element_ids\":[\"wall-a\",\"wall-b\"]}}]}");
            AssertMismatch(Check(badView, "horizun_manage_views"), "view_id", "view-b");
            var badElement = Req("{\"actions\":[{\"operation\":\"isolate_elements\",\"view_id\":30,\"element_ids\":[40,41],\"identity\":{\"view_id\":\"view-a\",\"element_ids\":[\"wall-a\",\"wall-a\"]}}]}");
            var error = Check(badElement, "horizun_manage_views");
            AssertMismatch(error, "element_ids", "wall-b");
            Assert.Equal(1, (int)error["position"]);
            var ok = Req("{\"actions\":[{\"operation\":\"set_element_overrides\",\"view_id\":30,\"element_ids\":[40],\"identity\":{\"view_id\":\"view-a\",\"element_ids\":[\"wall-a\"]}}]}");
            Assert.Null(Check(ok, "horizun_manage_views"));
        }

        [Fact]
        public void M2_apply_filter_apply_template_and_scope_box_verify_the_second_id()
        {
            var filter = Req("{\"actions\":[{\"operation\":\"apply_filter\",\"view_id\":30,\"filter_id\":41,\"identity\":{\"filter_id\":\"wall-a\"}}]}");
            AssertMismatch(Check(filter, "horizun_manage_views"), "filter_id", "wall-b");
            var template = Req("{\"actions\":[{\"operation\":\"apply_template\",\"view_id\":30,\"template_view_id\":31,\"identity\":{\"template_view_id\":\"view-a\"}}]}");
            AssertMismatch(Check(template, "horizun_manage_views"), "template_view_id", "view-b");
            var box = Req("{\"actions\":[{\"operation\":\"assign_scope_box\",\"view_id\":30,\"scope_box_id\":41,\"identity\":{\"scope_box_id\":\"wall-a\"}}]}");
            AssertMismatch(Check(box, "horizun_manage_views"), "scope_box_id", "wall-b");
        }

        [Fact]
        public void M2_annotate_tag_verifies_the_view_as_well_as_the_host()
        {
            var r = Req("{\"actions\":[{\"operation\":\"tag\",\"view_id\":30,\"element_id\":40,\"identity\":{\"view_id\":\"view-b\",\"element_id\":\"wall-a\"}}]}");
            AssertMismatch(Check(r, "horizun_annotate"), "view_id", "view-a");
            var ok = Req("{\"actions\":[{\"operation\":\"tag\",\"view_id\":30,\"element_id\":40,\"identity\":{\"view_id\":\"view-a\",\"element_id\":\"wall-a\"}}]}");
            Assert.Null(Check(ok, "horizun_annotate"));
        }

        [Fact]
        public void M2_fix_planimetry_verifies_the_second_id_of_clear_override_and_set_view_template()
        {
            var clear = Req("{\"actions\":[{\"operation\":\"clear_element_override\",\"view_id\":30,\"element_id\":40,\"identity\":{\"element_id\":\"wall-a\",\"view_id\":\"view-b\"}}]}");
            AssertMismatch(Check(clear, "horizun_fix_planimetry"), "view_id", "view-a");
            var template = Req("{\"actions\":[{\"operation\":\"set_view_template\",\"view_id\":30,\"template_id\":31,\"identity\":{\"template_id\":\"view-a\"}}]}");
            AssertMismatch(Check(template, "horizun_fix_planimetry"), "template_id", "view-b");
        }

        [Fact]
        public void M2_manage_groups_verifies_group_ids_and_type_id()
        {
            var members = Req("{\"operation\":\"add_members\",\"group_ids\":[41],\"element_ids\":[40],\"identity\":{\"group_ids\":[\"wall-a\"],\"element_ids\":[\"wall-a\"]}}");
            AssertMismatch(Check(members, "horizun_manage_groups"), "group_ids", "wall-b");
            var swap = Req("{\"operation\":\"swap_type\",\"element_ids\":[40],\"type_id\":41,\"identity\":{\"type_id\":\"wall-a\"}}");
            AssertMismatch(Check(swap, "horizun_manage_groups"), "type_id", "wall-b");
        }

        [Fact]
        public void M2_toposolid_writers_verify_the_toposolid_by_key_and_by_the_bare_unique_id()
        {
            foreach (string tool in new[] { "horizun_embed_floors_in_toposolid", "horizun_grade_toposolid_around_floors" })
            {
                var keyed = Req("{\"toposolid_id\":51,\"element_ids\":[40],\"identity\":{\"toposolid_id\":\"topo-a\",\"element_ids\":[\"wall-a\"]}}");
                AssertMismatch(Check(keyed, tool), "toposolid_id", "topo-b");
                // The bare unique_id cannot say WHICH of two id keys it is about when a list is present: refused, pointing at identity.
                var bare = Check(Req("{\"toposolid_id\":51,\"element_ids\":[40],\"unique_id\":\"topo-a\"}"), tool);
                Assert.Equal("entrada_invalida", (string)bare["error_code"]);
                Assert.Contains("identity", (string)bare["detail"]);
                var floors = Req("{\"toposolid_id\":50,\"element_ids\":[40,41],\"unique_ids\":[\"wall-a\",\"wall-a\"]}");
                var error = Check(floors, tool);
                AssertMismatch(error, "element_ids", "wall-b");
                Assert.Equal(1, (int)error["position"]);
                var ok = Req("{\"toposolid_id\":50,\"element_ids\":[40],\"identity\":{\"toposolid_id\":{\"unique_id\":\"topo-a\",\"expected_category\":\"OST_Topography\"},\"element_ids\":[\"wall-a\"]}}");
                Assert.Null(Check(ok, tool));
                Assert.Null(ok["identity"]);
                Assert.Equal(50L, (long)ok["toposolid_id"]);
            }
        }

        [Fact]
        public void M2_the_object_and_shared_forms_check_category_and_type_per_key()
        {
            var category = Req("{\"toposolid_id\":50,\"element_ids\":[40],\"identity\":{\"toposolid_id\":{\"expected_category\":\"Walls\"}}}");
            Assert.Equal("elemento_no_coincide", (string)Check(category, "horizun_embed_floors_in_toposolid")["reason"]);
            var shared = Req("{\"toposolid_id\":50,\"element_ids\":[40,41],\"identity\":{\"element_ids\":{\"expected_category\":\"Walls\",\"expected_type\":\"Standard\"}}}");
            var error = Check(shared, "horizun_embed_floors_in_toposolid");
            Assert.Equal(0, (int)error["position"]);   // wall-a is a Generic - 200mm, not a Standard
            var okShared = Req("{\"toposolid_id\":50,\"element_ids\":[40,41],\"identity\":{\"element_ids\":{\"expected_category\":\"OST_Walls\"}}}");
            Assert.Null(Check(okShared, "horizun_embed_floors_in_toposolid"));
        }

        [Theory]
        [InlineData("{\"toposolid_id\":50,\"element_ids\":[40],\"identity\":{\"toposolid_id\":\"topo-a\",\"nope_id\":\"x\"}}", "nope_id")]
        [InlineData("{\"toposolid_id\":\"${s.id}\",\"element_ids\":[40],\"identity\":{\"toposolid_id\":\"topo-a\"}}", "unresolved")]
        [InlineData("{\"toposolid_id\":50,\"element_ids\":[40],\"identity\":{\"toposolid_id\":{\"unique_id\":\"topo-a\",\"colour\":\"red\"}}}", "colour")]
        [InlineData("{\"toposolid_id\":50,\"element_ids\":[40],\"identity\":{\"toposolid_id\":{}}}", "nothing")]
        [InlineData("{\"toposolid_id\":50,\"element_ids\":[40],\"identity\":{\"element_ids\":[\"wall-a\",\"wall-b\"]}}", "same length")]
        [InlineData("{\"toposolid_id\":50,\"element_ids\":[40],\"identity\":{}}", "identity must be an object")]
        [InlineData("{\"toposolid_id\":50,\"element_ids\":[40],\"identity\":[\"topo-a\"]}", "identity must be an object")]
        [InlineData("{\"toposolid_id\":50,\"element_ids\":[40],\"unique_ids\":[\"wall-a\"],\"identity\":{\"element_ids\":[\"wall-a\"]}}", "twice")]
        public void M2_a_malformed_identity_is_an_argument_error_that_names_the_problem(string json, string mentions)
        {
            var error = Check(Req(json), "horizun_embed_floors_in_toposolid");
            Assert.NotNull(error);
            Assert.Equal("entrada_invalida", (string)error["error_code"]);
            Assert.Contains(mentions, (string)error["detail"], StringComparison.OrdinalIgnoreCase);
            Assert.False((bool)error["write_started"]);
        }

        [Fact]
        public void M2_nothing_is_consumed_when_a_later_key_fails()
        {
            var r = Req("{\"actions\":[{\"operation\":\"place_view\",\"view_id\":31,\"sheet_id\":31,\"identity\":{\"view_id\":\"view-b\",\"sheet_id\":\"view-a\"}}," +
                        "{\"operation\":\"place_view\",\"view_id\":31,\"sheet_id\":30,\"identity\":{\"sheet_id\":\"view-a\"}}]}");
            Assert.NotNull(Check(r, "horizun_manage_views"));
            Assert.NotNull(r["actions"][0]["identity"]);
            Assert.NotNull(r["actions"][1]["identity"]);
        }

        [Fact]
        public void M2_bare_fields_are_still_accepted_and_the_check_is_against_the_documented_key()
        {
            // manage_views: the first of view_id, sheet_id, viewport_id that is present. Not the sheet.
            var place = Req("{\"actions\":[{\"operation\":\"place_view\",\"view_id\":31,\"sheet_id\":30,\"unique_id\":\"view-b\"}]}");
            Assert.Null(Check(place, "horizun_manage_views"));   // the sheet was NOT checked by the bare form: use identity
            Assert.Null(place["actions"][0]["unique_id"]);
            var wrongView = Req("{\"actions\":[{\"operation\":\"place_view\",\"view_id\":31,\"sheet_id\":30,\"unique_id\":\"view-a\"}]}");
            AssertMismatch(Check(wrongView, "horizun_manage_views"), "view_id", "view-b");
        }

        // ------------------------------------------------------------------ M-3: validate_ids without an expectation

        [Fact]
        public void M3_validate_ids_with_nothing_to_compare_is_sin_expectativa_never_coincide()
        {
            var r = ElementIdentityGuard.Validate(JArray.Parse("[{\"id\":20},{\"id\":999},{\"id\":20,\"unique_id\":\"wanted\"}]"), ById, ByUid);
            var rows = (JArray)r["results"];
            Assert.Equal("sin_expectativa", (string)rows[0]["status"]);
            Assert.Equal(20L, (long)rows[0]["found"]["id"]);
            Assert.Equal("no_existe", (string)rows[1]["status"]);
            Assert.Equal("coincide", (string)rows[2]["status"]);
            Assert.Equal(1, (int)r["summary"]["sin_expectativa"]);
            Assert.Equal(1, (int)r["summary"]["coincide"]);
            Assert.False((bool)r["summary"]["all_match"]);
            var alone = ElementIdentityGuard.Validate(JArray.Parse("[{\"id\":20}]"), ById, ByUid);
            Assert.False((bool)alone["summary"]["all_match"]);
            Assert.Equal(0, (int)alone["summary"]["coincide"]);
        }

        [Fact]
        public void M3_validate_ids_all_match_needs_a_unique_id_on_every_entry()
        {
            // R2R (M-2): a category alone is a coincide_categoria, not a match: only unique_id identifies.
            var r = ElementIdentityGuard.Validate(JArray.Parse("[{\"id\":20,\"unique_id\":\"wanted\"},{\"id\":40,\"expected_category\":\"OST_Walls\"}]"), ById, ByUid);
            Assert.False((bool)r["summary"]["all_match"]);
            var strong = ElementIdentityGuard.Validate(JArray.Parse("[{\"id\":20,\"unique_id\":\"wanted\"},{\"id\":40,\"unique_id\":\"wall-a\",\"expected_category\":\"OST_Walls\"}]"), ById, ByUid);
            Assert.True((bool)strong["summary"]["all_match"]);
        }

        // ------------------------------------------------------------------ m-2: OST_* categories

        [Theory]
        [InlineData("OST_Walls", true)]
        [InlineData("ost_walls", true)]
        [InlineData("Walls", true)]
        [InlineData("OST_Doors", false)]
        public void M2b_expected_category_accepts_the_built_in_name_and_the_localized_one(string expected, bool ok)
        {
            var r = Req("{\"element_ids\":[40],\"expected_category\":\"" + expected + "\",\"keynote\":\"K\"}");
            Assert.Equal(ok, Check(r, "horizun_set_keynote") == null);
            var v = ElementIdentityGuard.Validate(JArray.Parse("[{\"id\":40,\"expected_category\":\"" + expected + "\"}]"), ById, ByUid);
            Assert.Equal(ok ? "coincide_categoria" : "cambio", (string)v["results"][0]["status"]);
        }

        // ------------------------------------------------------------------ M-4: every writer that receives existing ids

        [Theory]
        [InlineData("horizun_sheet_layout", "{\"operation\":\"move_viewport\",\"sheet_id\":30,\"viewport_id\":41,\"identity\":{\"viewport_id\":\"wall-a\"}}", "viewport_id")]
        [InlineData("horizun_planimetry_ops", "{\"operation\":\"fit_viewport\",\"viewport_ids\":[41],\"identity\":{\"viewport_ids\":[\"wall-a\"]}}", "viewport_ids")]
        [InlineData("horizun_planimetry_ops", "{\"operation\":\"resolve_overlaps\",\"view_ids\":[31],\"unique_ids\":[\"view-a\"]}", "view_ids")]
        [InlineData("horizun_room_finishes", "{\"operation\":\"floors\",\"room_ids\":[41],\"unique_ids\":[\"wall-a\"]}", "room_ids")]
        [InlineData("horizun_room_finishes", "{\"operation\":\"baseboards\",\"room_ids\":[40],\"type_id\":41,\"identity\":{\"type_id\":\"wall-a\"}}", "type_id")]
        [InlineData("horizun_pack_sheets", "{\"sheets\":[{\"sheet_id\":31,\"unique_id\":\"view-a\"}],\"items\":[{\"view_id\":30}]}", "sheet_id")]
        [InlineData("horizun_pack_sheets", "{\"sheet_id\":30,\"items\":[{\"schedule_id\":41,\"identity\":{\"schedule_id\":\"wall-a\"}}]}", "schedule_id")]
        [InlineData("horizun_manage_revisions", "{\"actions\":[{\"operation\":\"update_revision\",\"revision_id\":40,\"sheet_ids\":[41],\"identity\":{\"sheet_ids\":[\"wall-a\"]}}]}", "sheet_ids")]
        [InlineData("horizun_manage_revisions", "{\"actions\":[{\"operation\":\"create_revision\",\"clouds\":[{\"view_id\":31,\"identity\":{\"view_id\":\"view-a\"}}]}]}", "view_id")]
        [InlineData("horizun_manage_schedules", "{\"actions\":[{\"operation\":\"rename\",\"schedule_id\":41,\"unique_id\":\"wall-a\"}]}", "schedule_id")]
        [InlineData("horizun_structural_connections", "{\"actions\":[{\"key\":\"a\",\"member_ids\":[40,41],\"unique_ids\":[\"wall-a\",\"wall-a\"]}]}", "member_ids")]
        [InlineData("horizun_manage_materials", "{\"operation\":\"edit\",\"material_id\":41,\"identity\":{\"material_id\":\"wall-a\"}}", "material_id")]
        [InlineData("horizun_link_schedule", "{\"view_id\":31,\"identity\":{\"view_id\":\"view-a\"}}", "view_id")]
        [InlineData("horizun_capture_view", "{\"view_id\":31,\"unique_id\":\"view-a\"}", "view_id")]
        [InlineData("horizun_export", "{\"format\":\"pdf\",\"view_ids\":[31],\"unique_ids\":[\"view-a\"]}", "view_ids")]
        [InlineData("horizun_export", "{\"format\":\"ifc\",\"ifc_filter_view_id\":31,\"unique_id\":\"view-a\"}", "ifc_filter_view_id")]
        [InlineData("horizun_model_diff", "{\"operation\":\"colorize\",\"view_id\":31,\"unique_id\":\"view-a\"}", "view_id")]
        public void M4_each_remaining_writer_refuses_a_reused_id_before_writing(string tool, string json, string key)
        {
            Assert.True(ElementIdentityGuard.Covers(tool), tool + " must be covered");
            var r = Req(json);
            var error = Check(r, tool);
            Assert.NotNull(error);
            Assert.Equal("elemento_no_coincide", (string)error["reason"]);
            Assert.Equal(key, (string)error["key"]);
            Assert.False((bool)error["write_started"]);
        }

        [Fact]
        public void M4_the_matching_forms_of_the_new_writers_are_consumed()
        {
            var r = Req("{\"actions\":[{\"operation\":\"update_revision\",\"revision_id\":40,\"sheet_ids\":[40],\"identity\":{\"revision_id\":\"wall-a\",\"sheet_ids\":[\"wall-a\"]}}]}");
            Assert.Null(Check(r, "horizun_manage_revisions"));
            Assert.Null(r["actions"][0]["identity"]);
            Assert.Equal(40L, (long)r["actions"][0]["revision_id"]);
            var rooms = Req("{\"operation\":\"floors\",\"room_ids\":[40],\"unique_ids\":[\"wall-a\"]}");
            Assert.Null(Check(rooms, "horizun_room_finishes"));
            Assert.Null(rooms["unique_ids"]);
        }

        [Fact]
        public void M4_every_mutating_contract_tool_with_id_arguments_is_covered_refused_or_justified()
        {
            var unjustified = new SortedSet<string>(StringComparer.Ordinal);
            foreach (CommandContract c in Contract.All)
            {
                if (c.Effect == ToolEffect.ReadOnly || c.InputSchema == null) continue;
                if (!HasIdArgument(c.InputSchema)) continue;
                if (ElementIdentityGuard.Covers(c.Name) || ElementIdentityGuard.Refuses(c.Name)) continue;
                if (c.Name == "horizun_execute_python") continue;   // not protectable by construction (documented)
                if (NotElementIds.ContainsKey(c.Name)) continue;
                unjustified.Add(c.Name);
            }
            Assert.True(unjustified.Count == 0,
                "writers that receive id-like arguments and neither verify them nor appear in the justified list: " + string.Join(", ", unjustified));
            foreach (string tool in NotElementIds.Keys)
                Assert.False(ElementIdentityGuard.Covers(tool), tool + " is covered; remove it from the justified list");
        }

        // Writers whose id-like arguments are not existing elements of the ACTIVE document that the write acts on.
        private static readonly Dictionary<string, string> NotElementIds = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["horizun_save_document"] = "accept_findings_ids selects pending ledger findings, not elements to mutate; unmatched ids are refused",
            ["horizun_budget_compare"] = "dataset_id / workspace_id name a Power BI dataset, not a model element",
            ["horizun_power_bi_push"] = "dataset_id / workspace_id name a Power BI dataset, not a model element",
            ["horizun_cde_cloud"] = "hub, project, item and issue ids belong to the cloud service, not to the model",
            ["horizun_exchange"] = "correlation_id / step_id name records of an exchange manifest",
            ["horizun_information_container"] = "transmittal_id names a record of the information container",
            ["horizun_project_context"] = "requirement_ids name requirements of project-context.json",
            ["horizun_request_python_access"] = "request_id names the access request",
            ["horizun_submit_job"] = "resume_from_job_id names a job of the bridge",
            ["horizun_navigate"] = "moves the user's selection or view and writes nothing to the model; it does not pass through the write gate",
            ["horizun_verify_changes"] = "a read-back check of the session's own writes; it writes nothing to the model",
        };

        private static bool HasIdArgument(JToken node)
        {
            if (node is JObject o)
            {
                foreach (JProperty p in o.Properties())
                {
                    if (p.Name == "properties" && p.Value is JObject props)
                    {
                        foreach (JProperty arg in props.Properties())
                            if (IsIdName(arg.Name) || HasIdArgument(arg.Value)) return true;
                    }
                    else if (HasIdArgument(p.Value)) return true;
                }
            }
            else if (node is JArray a)
                foreach (JToken item in a) if (HasIdArgument(item)) return true;
            return false;
        }

        private static bool IsIdName(string n) => n == "ids" || n.EndsWith("_id", StringComparison.Ordinal) || n.EndsWith("_ids", StringComparison.Ordinal);

        // ------------------------------------------------------------------ m-1: top level of annotate / edit_dimensions

        [Theory]
        [InlineData("horizun_annotate", "{\"view_id\":30,\"unique_id\":\"view-a\",\"actions\":[{\"operation\":\"text\",\"view_id\":30}]}")]
        [InlineData("horizun_annotate", "{\"identity\":{\"view_id\":\"view-a\"},\"actions\":[{\"operation\":\"text\",\"view_id\":30}]}")]
        [InlineData("horizun_edit_dimensions", "{\"element_id\":40,\"unique_id\":\"wall-a\",\"actions\":[{\"element_id\":40}]}")]
        public void M1minor_unique_id_at_the_top_level_of_a_batch_tool_is_refused_and_says_it_goes_per_action(string tool, string json)
        {
            var error = Check(Req(json), tool);
            Assert.Equal("entrada_invalida", (string)error["error_code"]);
            Assert.Contains("each action", (string)error["detail"]);
            Assert.False((bool)error["write_started"]);
        }

        [Fact]
        public void M1minor_the_same_fields_inside_an_action_still_work()
        {
            var ok = Req("{\"actions\":[{\"operation\":\"text\",\"view_id\":30,\"unique_id\":\"view-a\",\"text\":\"x\"}]}");
            Assert.Null(Check(ok, "horizun_annotate"));
            var dim = Req("{\"actions\":[{\"element_id\":40,\"identity\":{\"element_id\":\"wall-a\"}}]}");
            Assert.Null(Check(dim, "horizun_edit_dimensions"));
        }

        [Fact]
        public void M1minor_a_plan_child_annotate_refuses_top_level_identity_too()
        {
            var plan = Req("{\"actions\":[{\"key\":\"a\",\"tool\":\"horizun_annotate\",\"arguments\":{\"unique_id\":\"view-a\",\"actions\":[]}}]}");
            Assert.Equal("entrada_invalida", (string)Check(plan, "horizun_execute_plan")["error_code"]);
        }

        // ------------------------------------------------------------------ schema, docs and wiring

        [Theory]
        [InlineData("horizun_manage_views")]
        [InlineData("horizun_annotate")]
        [InlineData("horizun_fix_planimetry")]
        [InlineData("horizun_detail_2d")]
        [InlineData("horizun_manage_groups")]
        [InlineData("horizun_transform_elements")]
        public void Declaring_tools_publish_the_identity_map_and_short_text_on_which_key_the_bare_id_checks(string tool)
        {
            string schema = Contract.Find(tool).InputSchema.ToString();
            Assert.Contains("\"identity\"", schema);
            Assert.Contains("{id_key: unique_id|{unique_id,expected_*}}", schema);
            foreach (JProperty p in DescriptionsOf(Contract.Find(tool).InputSchema))
                if (p.Name == "identity" || p.Name == "unique_id")
                    Assert.True(((string)p.Value["description"] ?? "").Length < 100, tool + "." + p.Name + " description must stay under the repeated-text threshold");
        }

        private static IEnumerable<JProperty> DescriptionsOf(JToken node)
        {
            if (node is JObject o)
            {
                if (o["properties"] is JObject props)
                    foreach (JProperty p in props.Properties())
                        if (p.Value is JObject) yield return p;
                foreach (JProperty p in o.Properties())
                    foreach (JProperty inner in DescriptionsOf(p.Value)) yield return inner;
            }
            else if (node is JArray a)
                foreach (JToken t in a)
                    foreach (JProperty inner in DescriptionsOf(t)) yield return inner;
        }

        [Theory]
        [InlineData("horizun_manage_views", "view_id, sheet_id, viewport_id")]
        [InlineData("horizun_fix_planimetry", "element_id, viewport_id, sheet_id, view_id")]
        [InlineData("horizun_annotate", "tag element_id, else view_id")]
        [InlineData("horizun_edit_dimensions", "element_id")]
        public void The_schema_says_in_a_few_words_which_key_a_bare_unique_id_is_compared_with(string tool, string mentions)
        {
            string desc = (string)FindProperty(Contract.Find(tool).InputSchema, "unique_id")["description"];
            Assert.Contains(mentions, desc);
        }

        private static JObject FindProperty(JToken node, string name)
        {
            if (node is JObject o)
            {
                if (o["properties"] is JObject props && props[name] is JObject hit) return hit;
                foreach (JProperty p in o.Properties())
                {
                    JObject inner = FindProperty(p.Value, name);
                    if (inner != null) return inner;
                }
            }
            else if (node is JArray a)
                foreach (JToken t in a)
                {
                    JObject inner = FindProperty(t, name);
                    if (inner != null) return inner;
                }
            return null;
        }

        [Fact]
        public void M4_execute_plan_arguments_description_matches_what_the_gate_does()
        {
            JObject plan = Contract.Find("horizun_execute_plan").InputSchema;
            string text = (string)plan["properties"]["actions"]["items"]["properties"]["arguments"]["description"];
            Assert.Contains("Identity fields: checked plan-wide first", text);
            Assert.DoesNotContain("write_params_verified, delete_verified, set_keynote and transform_elements", text);
        }

        private static string Source(params string[] parts)
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !Directory.Exists(Path.Combine(d.FullName, "src"))) d = d.Parent;
            Assert.NotNull(d);
            return File.ReadAllText(Path.Combine(new[] { d.FullName }.Concat(parts).ToArray()));
        }

        [Fact]
        public void M2b_query_planimetry_reports_the_category_of_the_element_not_the_one_the_rule_asked_for()
        {
            string src = Source("src", "Horizun.Revit", "Core", "PlanimetryInventory.cs");
            Assert.Contains("new UntaggedElement { Id = id, Category = Try(() => e.Category == null ? null : e.Category.Name, null) ?? categoryName", src);
            Assert.DoesNotContain("new UntaggedElement { Id = id, Category = categoryName", src);
        }

        [Fact]
        public void M2b_identity_facts_carry_the_built_in_category_name()
        {
            string src = Source("src", "Horizun.Revit", "Core", "DocumentGate.cs");
            Assert.Contains("CategoryBuiltIn = builtIn", src);
            Assert.Contains("e.Category.BuiltInCategory.ToString()", src);
        }

        [Fact]
        public void The_identity_guard_document_exists_and_names_every_covered_tool_with_its_bare_key()
        {
            string doc = Source("docs", "IDENTITY-GUARD.md");
            foreach (string tool in ElementIdentityGuard.CoveredTools)
                Assert.Contains(tool, doc);
            Assert.Contains("Reload Latest", doc);
            Assert.Contains("linked element", doc, StringComparison.OrdinalIgnoreCase);   // m-3: a tag over a link
            Assert.Contains("id_freshness", doc);
        }

        // ------------------------------------------------------------------ m-5: what clears id_freshness

        [Theory]
        [InlineData("horizun_selection_context", "validate_ids", false)]
        [InlineData("horizun_selection_context", "context", true)]
        [InlineData("horizun_selection_context", null, true)]
        [InlineData("horizun_query_model", null, true)]
        [InlineData("horizun_health", null, false)]
        public void M5_validate_ids_answers_about_ids_the_caller_already_holds_so_it_is_not_a_read_that_hands_ids_out(string tool, string operation, bool reads)
        {
            Assert.Equal(reads, IdFreshnessRules.ReadsIds(tool, operation));
        }

        [Fact]
        public void M5_the_notice_says_exactly_what_clears_it()
        {
            IdFreshnessRules.ResetForTests();
            const string doc = "path:c:/models/r2q.rvt";
            IdFreshnessRules.NoteSynchronized(doc, DateTime.UtcNow);
            string note = (string)IdFreshnessRules.Block(doc)["note"];
            Assert.Contains("A read of other elements does not clear it", note);
            Assert.Contains("validate_ids", note);
            IdFreshnessRules.ResetForTests();
        }
    }
}
