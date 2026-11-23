using System;
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    /// <summary>
    /// Review M7 of int/4.2, reconciled with R2R: the identity guard reaches the writers 4.2 added (planimetry_ops, sheet_layout,
    /// view_visibility, manage_worksets, annotate, edit_dimensions) and a node that names several ids checks each key named in
    /// the keyed form `identity`. The M7 design (expected_ids, ids checked by existence, fields declared in the 4.2 schemas) was
    /// retired in favour of R2R's: the dispatcher's IdentityPrepass consumes the fields before any validator, and the server
    /// accepts them on a covered tool without declaring them. What the removed schema test protected (that a covered writer
    /// does not refuse the fields) is covered by IdentityWireTests (every covered tool, over stdio) and by the validator theory
    /// of ElementIdentityGuardR2RTests.
    /// </summary>
    public class IntegrationReviewIdentityTests
    {
        private static readonly Dictionary<long, ElementIdentityFacts> Model = new Dictionary<long, ElementIdentityFacts>
        {
            [10] = new ElementIdentityFacts { Id = 10, UniqueId = "other", Category = "Walls", Type = "B" },
            [20] = new ElementIdentityFacts { Id = 20, UniqueId = "wanted", Category = "Walls", Type = "A" },
            [30] = new ElementIdentityFacts { Id = 30, UniqueId = "view-a", Category = "Views", Type = "Floor Plan" },
            [31] = new ElementIdentityFacts { Id = 31, UniqueId = "view-b", Category = "Views", Type = "Floor Plan" },
            [40] = new ElementIdentityFacts { Id = 40, UniqueId = "sheet-a", Category = "Sheets", Type = "A1" },
            [41] = new ElementIdentityFacts { Id = 41, UniqueId = "vp-a", Category = "Viewports", Type = "Viewport" },
            [50] = new ElementIdentityFacts { Id = 50, UniqueId = "link-a", Category = "RvtLinks", Type = "Link" },
            [60] = new ElementIdentityFacts { Id = 60, UniqueId = "tag-a", Category = "Tags", Type = "Tag" },
        };

        private static ElementIdentityFacts ById(long id) => Model.TryGetValue(id, out var e) ? e : null;
        private static ElementIdentityFacts ByUid(string uid) => Model.Values.FirstOrDefault(m => m.UniqueId == uid);

        private static JObject Run(string tool, string json, out JObject request)
        {
            request = JObject.Parse(json);
            return ElementIdentityGuard.ResolveRequest(request, tool, ById, ByUid);
        }

        private static void AssertOk(JObject error) => Assert.True(error == null, error?.ToString());

        [Theory]
        [InlineData("horizun_planimetry_ops")]
        [InlineData("horizun_sheet_layout")]
        [InlineData("horizun_view_visibility")]
        [InlineData("horizun_manage_worksets")]
        [InlineData("horizun_annotate")]
        [InlineData("horizun_edit_dimensions")]
        public void The_4_2_writers_are_covered(string tool) => Assert.True(ElementIdentityGuard.Covers(tool));

        [Fact]
        public void A_second_id_named_in_identity_that_names_nothing_is_refused_with_its_key()
        {
            var error = Run("horizun_sheet_layout", "{\"operation\":\"move_viewport\",\"sheet_id\":40,\"viewport_id\":999,\"identity\":{\"sheet_id\":\"sheet-a\",\"viewport_id\":\"vp-a\"}}", out _);
            Assert.NotNull(error);
            Assert.Equal(ElementIdentityGuard.Mismatch, (string)error["reason"]);
            Assert.Equal("viewport_id", (string)error["key"]);
            Assert.False((bool)error["write_started"]);
        }

        [Fact]
        public void identity_states_each_id_of_a_node_and_a_reused_one_is_refused_with_its_key()
        {
            string good = "{\"operation\":\"move_viewport\",\"sheet_id\":40,\"viewport_id\":41,\"identity\":{\"sheet_id\":\"sheet-a\",\"viewport_id\":\"vp-a\"}}";
            AssertOk(Run("horizun_sheet_layout", good, out JObject accepted));
            Assert.Null(accepted["identity"]);   // consumed after a successful check, like the other identity fields
            Assert.Equal(40, (int)accepted["sheet_id"]);

            string reused = "{\"operation\":\"move_viewport\",\"sheet_id\":40,\"viewport_id\":20,\"identity\":{\"sheet_id\":\"sheet-a\",\"viewport_id\":\"vp-a\"}}";
            var error = Run("horizun_sheet_layout", reused, out _);
            Assert.Equal(ElementIdentityGuard.Mismatch, (string)error["reason"]);
            Assert.Equal("viewport_id", (string)error["key"]);
            Assert.Equal("vp-a", (string)error["expected"]["unique_id"]);
            Assert.Equal("wanted", (string)error["found"]["unique_id"]);
        }

        [Fact]
        public void view_visibility_checks_the_elements_and_the_views_of_one_node()
        {
            string ok = "{\"operation\":\"hide\",\"view_id\":30,\"element_ids\":[20],\"unique_ids\":[\"wanted\"]}";
            AssertOk(Run("horizun_view_visibility", ok, out _));
            // element_ids is the subject; view_ids is checked through identity and its second entry has been reused.
            string bad = "{\"operation\":\"marker_scale\",\"element_ids\":[20],\"unique_ids\":[\"wanted\"],\"view_ids\":[30,10],\"identity\":{\"view_ids\":[\"view-a\",\"view-b\"]}}";
            var error = Run("horizun_view_visibility", bad, out _);
            Assert.Equal(ElementIdentityGuard.Mismatch, (string)error["reason"]);
            Assert.Equal("view_ids", (string)error["key"]);
            Assert.Equal(1, (int)error["position"]);
        }

        [Fact]
        public void borrow_checks_the_elements_it_would_check_out()
        {
            AssertOk(Run("horizun_manage_worksets", "{\"operation\":\"borrow\",\"element_ids\":[20],\"unique_ids\":[\"wanted\"]}", out _));
            var error = Run("horizun_manage_worksets", "{\"operation\":\"borrow\",\"element_ids\":[10],\"unique_ids\":[\"wanted\"]}", out _);
            Assert.Equal(ElementIdentityGuard.Mismatch, (string)error["reason"]);
        }

        [Fact]
        public void Rows_nested_in_planimetry_ops_are_checked()
        {
            string json = "{\"operation\":\"set_leader_end\",\"notes\":[{\"element_id\":20,\"unique_id\":\"wanted\",\"end_mm\":[0,0],\"note_mm\":[1,1]}," +
                          "{\"element_id\":10,\"unique_id\":\"wanted\",\"end_mm\":[0,0],\"note_mm\":[1,1]}]}";
            var error = Run("horizun_planimetry_ops", json, out _);
            Assert.Equal(ElementIdentityGuard.Mismatch, (string)error["reason"]);
            Assert.Equal(1, (int)error["index"]);
        }

        [Fact]
        public void note_ids_and_model_tag_id_of_planimetry_ops_are_host_ids_the_guard_checks()
        {
            AssertOk(Run("horizun_planimetry_ops", "{\"operation\":\"move_note\",\"note_ids\":[20],\"unique_ids\":[\"wanted\"],\"delta_mm\":[1,1]}", out _));
            var swapped = Run("horizun_planimetry_ops", "{\"operation\":\"move_note\",\"note_ids\":[10],\"unique_ids\":[\"wanted\"],\"delta_mm\":[1,1]}", out _);
            Assert.Equal(ElementIdentityGuard.Mismatch, (string)swapped["reason"]);
            Assert.Equal("note_ids", (string)swapped["key"]);

            AssertOk(Run("horizun_planimetry_ops", "{\"operation\":\"clone_tag\",\"model_tag_id\":60,\"unique_id\":\"tag-a\",\"targets\":[{\"element_id\":20,\"head_mm\":[0,0]}]}", out _));
            var wrongTag = Run("horizun_planimetry_ops", "{\"operation\":\"clone_tag\",\"model_tag_id\":10,\"unique_id\":\"tag-a\",\"targets\":[{\"element_id\":20,\"head_mm\":[0,0]}]}", out _);
            Assert.Equal(ElementIdentityGuard.Mismatch, (string)wrongTag["reason"]);
            Assert.Equal("model_tag_id", (string)wrongTag["key"]);
        }

        [Fact]
        public void A_notes_row_with_link_instance_id_checks_only_the_instance_and_refuses_an_identity_on_the_linked_element()
        {
            // The row's element_id belongs to the LINKED document: the bare unique_id speaks for the instance (it goes first in the spec).
            string ok = "{\"operation\":\"note_from_model\",\"view_id\":30,\"notes\":[{\"element_id\":777,\"link_instance_id\":50,\"unique_id\":\"link-a\",\"end_mm\":[0,0],\"note_mm\":[1,1]}]}";
            AssertOk(Run("horizun_planimetry_ops", ok, out JObject accepted));
            Assert.Equal(777, (int)accepted["notes"][0]["element_id"]);
            Assert.Null(accepted["notes"][0]["unique_id"]);

            string wrongInstance = ok.Replace("\"link_instance_id\":50", "\"link_instance_id\":10");
            Assert.Equal(ElementIdentityGuard.Mismatch, (string)Run("horizun_planimetry_ops", wrongInstance, out _)["reason"]);

            string onLinked = "{\"operation\":\"note_from_model\",\"view_id\":30,\"notes\":[{\"element_id\":20,\"link_instance_id\":50,\"identity\":{\"element_id\":\"wanted\"},\"end_mm\":[0,0],\"note_mm\":[1,1]}]}";
            var refused = Run("horizun_planimetry_ops", onLinked, out _);
            Assert.Equal(ElementIdentityGuard.ArgumentErrorCode, (string)refused["error_code"]);
            Assert.Contains("LINKED", (string)refused["detail"]);
        }

        [Fact]
        public void A_tag_checks_its_host_and_its_view_through_identity()
        {
            string ok = "{\"actions\":[{\"operation\":\"tag\",\"view_id\":30,\"element_id\":20,\"unique_id\":\"wanted\",\"identity\":{\"view_id\":\"view-a\"}}]}";
            AssertOk(Run("horizun_annotate", ok, out _));
            var error = Run("horizun_annotate", "{\"actions\":[{\"operation\":\"tag\",\"view_id\":999,\"element_id\":20,\"unique_id\":\"wanted\",\"identity\":{\"view_id\":\"view-a\"}}]}", out _);
            Assert.Equal(ElementIdentityGuard.Mismatch, (string)error["reason"]);
            Assert.Equal("view_id", (string)error["key"]);
        }

        [Fact]
        public void Sheet_layout_items_rows_are_checked_by_view_id()
        {
            AssertOk(Run("horizun_sheet_layout", "{\"operation\":\"auto_layout\",\"sheet_id\":40,\"items\":[{\"view_id\":30,\"unique_id\":\"view-a\"}]}", out _));
            var error = Run("horizun_sheet_layout", "{\"operation\":\"auto_layout\",\"sheet_id\":40,\"items\":[{\"view_id\":31,\"unique_id\":\"view-a\"}]}", out _);
            Assert.Equal(ElementIdentityGuard.Mismatch, (string)error["reason"]);
            Assert.Equal("view_id", (string)error["key"]);
        }

        [Fact]
        public void A_plan_reaches_the_new_writers_and_refuses_the_whole_call_before_any_child_runs()
        {
            var plan = JObject.Parse("{\"actions\":[" +
                "{\"tool\":\"horizun_sheet_layout\",\"arguments\":{\"operation\":\"move_viewport\",\"sheet_id\":40,\"unique_id\":\"sheet-a\"}}," +
                "{\"tool\":\"horizun_view_visibility\",\"arguments\":{\"operation\":\"hide\",\"view_id\":30,\"element_ids\":[10],\"unique_ids\":[\"wanted\"]}}]}");
            var error = ElementIdentityGuard.ResolveRequest(plan, "horizun_execute_plan", ById, ByUid);
            Assert.NotNull(error);
            Assert.Equal(ElementIdentityGuard.Mismatch, (string)error["reason"]);
            Assert.Equal("actions[1].arguments", (string)error["path"]);
        }

        [Fact]
        public void check_variation_reads_only_so_an_identity_on_it_is_refused_saying_so()
        {
            Assert.True(ElementIdentityGuard.WritesNothing("horizun_manage_groups", JObject.Parse("{\"operation\":\"check_variation\"}")));
            Assert.False(ElementIdentityGuard.WritesNothing("horizun_manage_groups", JObject.Parse("{\"operation\":\"copy_to_levels\"}")));
            var error = ElementIdentityGuard.ShapeCheck(JObject.Parse("{\"operation\":\"check_variation\",\"group_ids\":[20],\"unique_ids\":[\"wanted\"]}"), "horizun_manage_groups");
            Assert.Equal(ElementIdentityGuard.ArgumentErrorCode, (string)error["error_code"]);
        }

        [Fact]
        public void expected_ids_is_retired_and_is_not_an_identity_name()
        {
            // M7's expected_ids no longer exists: the keyed form is `identity`.
            Assert.DoesNotContain("expected_ids", ElementIdentityGuard.AcceptedNames);
        }

        [Fact]
        public void A_node_with_one_id_behaves_as_before()
        {
            AssertOk(Run("horizun_sheet_layout", "{\"operation\":\"move_viewport\",\"sheet_id\":40,\"unique_id\":\"sheet-a\"}", out _));
            AssertOk(Run("horizun_sheet_layout", "{\"operation\":\"move_viewport\",\"sheet_id\":40,\"viewport_id\":41}", out _));   // no identity field: untouched
        }
    }
}
