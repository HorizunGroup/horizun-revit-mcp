// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// section_mark_diagnosis (backlog §7): the arithmetic that says why the mark of a section is not in
// a plan. View range against the Z of the section, scale, category, filters, phase, crop, verdict.
// A fact that cannot be read is "unknown" with a reason, never 0 and never "no".
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class SectionMarkRulesTests
    {
        // a ground-floor plan: cut 1200, top 2300, bottom 0, view depth -300 (mm, absolute)
        private static JObject Range(double? zMin, double? zMax, ZBound top = null, ZBound bottom = null, ZBound depth = null) =>
            SectionMarkRules.ZRange(ZBound.At(1200), top ?? ZBound.At(2300), bottom ?? ZBound.At(0), depth ?? ZBound.At(-300), zMin, zMax, null);

        [Fact]
        public void a_section_that_crosses_the_primary_range_does_not_block()
        {
            JObject c = Range(-500, 3000);
            Assert.Equal("no", (string)c["blocks_mark"]);
            Assert.Equal("crosses_primary_range", (string)c["measured"]["relation"]);
        }

        [Fact]
        public void a_section_touching_the_top_plane_still_crosses()
        {
            Assert.Equal("no", (string)Range(1000, 2300)["blocks_mark"]);
        }

        [Fact]
        public void a_section_above_the_top_blocks_with_the_gap()
        {
            JObject c = Range(3000, 5000);
            Assert.Equal("yes", (string)c["blocks_mark"]);
            Assert.Equal("above_top", (string)c["measured"]["relation"]);
            Assert.Equal(700.0, (double)c["measured"]["gap_mm"]);
            Assert.Equal(2300.0, (double)c["measured"]["top_z_mm"]);
        }

        [Fact]
        public void a_section_below_the_view_depth_blocks()
        {
            JObject c = Range(-2000, -900);
            Assert.Equal("yes", (string)c["blocks_mark"]);
            Assert.Equal("below_view_depth", (string)c["measured"]["relation"]);
            Assert.Equal(600.0, (double)c["measured"]["gap_mm"]);
        }

        [Fact]
        public void a_section_only_in_the_view_depth_band_is_not_declared_blocking()
        {
            JObject c = Range(-250, -100);
            Assert.Equal("unknown", (string)c["blocks_mark"]);
            Assert.Equal("only_in_view_depth_band", (string)c["measured"]["relation"]);
        }

        [Fact]
        public void an_unlimited_top_makes_everything_above_the_bottom_cross()
        {
            Assert.Equal("no", (string)SectionMarkRules.ZRange(ZBound.At(91000), ZBound.Unlimited(), ZBound.At(0), ZBound.At(-300), 90000, 95000, null)["blocks_mark"]);
        }

        [Fact]
        public void an_unreadable_plane_or_box_is_unknown_with_a_reason_never_zero()
        {
            JObject a = Range(0, 100, top: ZBound.Unknown("no level above"));
            Assert.Equal("unknown", (string)a["blocks_mark"]);
            Assert.Contains("no level above", (string)a["reason"]);
            Assert.Equal("no level above", (string)a["measured"]["top_z_mm"]["unknown"]);

            JObject b = SectionMarkRules.ZRange(ZBound.At(1), ZBound.At(2), ZBound.At(0), ZBound.At(-1), null, null, "crop box unreadable");
            Assert.Equal("unknown", (string)b["blocks_mark"]);
            Assert.Equal("crop box unreadable", (string)b["reason"]);
        }

        [Fact]
        public void a_section_given_upside_down_is_ordered_before_the_comparison()
        {
            Assert.Equal("yes", (string)Range(5000, 3000)["blocks_mark"]);
        }

        [Fact]
        public void a_view_without_range_says_not_applicable()
        {
            Assert.Equal("not_applicable", (string)SectionMarkRules.ZRangeNotApplicable("ThreeD")["blocks_mark"]);
        }

        [Theory]
        [InlineData(100, 50, "yes")]     // 1:100 is coarser than 1:50
        [InlineData(50, 50, "no")]
        [InlineData(20, 50, "no")]
        public void the_scale_hides_the_mark_when_the_view_is_coarser(int view, int limit, string blocks)
        {
            JObject c = SectionMarkRules.Scale(view, limit, null);
            Assert.Equal(blocks, (string)c["blocks_mark"]);
            Assert.Equal(view, (int)c["measured"]["view_scale"]);
            Assert.Equal(limit, (int)c["measured"]["hide_coarser_than"]);
        }

        [Fact]
        public void a_scale_that_cannot_be_read_is_unknown()
        {
            Assert.Equal("unknown", (string)SectionMarkRules.Scale(100, null, "no parameter")["blocks_mark"]);
            Assert.Equal("unknown", (string)SectionMarkRules.Scale(null, 50, null)["blocks_mark"]);
            Assert.Equal("unknown", (string)SectionMarkRules.Scale(100, 0, null)["blocks_mark"]);
        }

        [Fact]
        public void a_hidden_category_blocks_and_names_the_template()
        {
            var cats = new List<KeyValuePair<string, bool?>> { new KeyValuePair<string, bool?>("OST_Sections", true), new KeyValuePair<string, bool?>("OST_Viewers", false) };
            JObject c = SectionMarkRules.CategoryHidden(cats, "Plan template");
            Assert.Equal("yes", (string)c["blocks_mark"]);
            Assert.Contains("Plan template", (string)c["reason"]);
        }

        [Fact]
        public void categories_visible_do_not_block_and_an_unread_one_is_unknown()
        {
            var ok = new List<KeyValuePair<string, bool?>> { new KeyValuePair<string, bool?>("OST_Sections", false) };
            Assert.Equal("no", (string)SectionMarkRules.CategoryHidden(ok, null)["blocks_mark"]);
            var unread = new List<KeyValuePair<string, bool?>> { new KeyValuePair<string, bool?>("OST_Sections", false), new KeyValuePair<string, bool?>("OST_Viewers", null) };
            Assert.Equal("unknown", (string)SectionMarkRules.CategoryHidden(unread, null)["blocks_mark"]);
            Assert.Equal("unknown", (string)SectionMarkRules.CategoryHidden(new List<KeyValuePair<string, bool?>>(), null)["blocks_mark"]);
        }

        [Fact]
        public void a_filter_with_visibility_off_blocks_only_when_it_matches()
        {
            var matching = new List<FilterVerdict> { new FilterVerdict { Name = "F1", Visible = false, Enabled = true, Matches = true } };
            Assert.Equal("yes", (string)SectionMarkRules.Filters(matching, null)["blocks_mark"]);
            var other = new List<FilterVerdict> { new FilterVerdict { Name = "F1", Visible = false, Enabled = true, Matches = false }, new FilterVerdict { Name = "F2", Visible = true, Enabled = true, Matches = true } };
            Assert.Equal("no", (string)SectionMarkRules.Filters(other, null)["blocks_mark"]);
            var undecided = new List<FilterVerdict> { new FilterVerdict { Name = "F1", Visible = false, Enabled = true, Matches = null, Reason = "boom" } };
            Assert.Equal("unknown", (string)SectionMarkRules.Filters(undecided, null)["blocks_mark"]);
            Assert.Equal("no", (string)SectionMarkRules.Filters(new List<FilterVerdict>(), null)["blocks_mark"]);
        }

        [Fact]
        public void the_phase_filter_hides_only_with_dont_show()
        {
            Assert.Equal("yes", (string)SectionMarkRules.Phase("DontShow", "New", "Show New", null)["blocks_mark"]);
            Assert.Equal("no", (string)SectionMarkRules.Phase("ShowByCategory", "New", "Show New", null)["blocks_mark"]);
            JObject u = SectionMarkRules.Phase(null, null, null, "no phase");
            Assert.Equal("unknown", (string)u["blocks_mark"]);
            Assert.Equal("no phase", (string)u["reason"]);
        }

        [Theory]
        [InlineData(-5, 5, 5, 5, true)]       // crosses the rectangle
        [InlineData(2, 2, 8, 8, true)]        // wholly inside
        [InlineData(20, 0, 30, 0, false)]     // wholly to the right
        [InlineData(-5, 20, 5, 20, false)]    // wholly above
        [InlineData(-20, -20, 20, 20, true)]  // diagonal through
        [InlineData(10, 5, 15, 5, true)]      // touches the edge
        [InlineData(11, 5, 15, 5, false)]
        public void the_section_line_against_the_crop_rectangle(double x0, double y0, double x1, double y1, bool expected)
        {
            Assert.Equal(expected, SectionMarkRules.SegmentIntersectsRect(x0, y0, x1, y1, 0, 0, 10, 10));
        }

        [Fact]
        public void the_crop_blocks_only_when_active_rectangular_and_missed()
        {
            double[] rect = { 0, 0, 10000, 10000 };
            Assert.Equal("no", (string)SectionMarkRules.Crop(false, false, null, null, null)["blocks_mark"]);
            Assert.Equal("yes", (string)SectionMarkRules.Crop(true, false, new double[] { 20000, 0, 30000, 0 }, rect, null)["blocks_mark"]);
            Assert.Equal("no", (string)SectionMarkRules.Crop(true, false, new double[] { -5000, 5000, 5000, 5000 }, rect, null)["blocks_mark"]);
            Assert.Equal("unknown", (string)SectionMarkRules.Crop(true, true, new double[] { 20000, 0, 30000, 0 }, rect, null)["blocks_mark"]);
            Assert.Equal("unknown", (string)SectionMarkRules.Crop(true, false, null, null, "unreadable")["blocks_mark"]);
        }

        private static JObject C(string code, string blocks) => new JObject { ["cause"] = code, ["blocks_mark"] = blocks };

        [Fact]
        public void the_verdict_separates_visible_measured_unruled_and_unknown()
        {
            Assert.Equal("visible", (string)SectionMarkRules.Verdict(true, new List<JObject> { C("view_range", "no"), C("phase", "unknown") }, null)["verdict"]);

            JObject conflict = SectionMarkRules.Verdict(true, new List<JObject> { C("view_range", "yes") }, null);
            Assert.Equal("measurement_conflict", (string)conflict["verdict"]);
            Assert.Contains("view_range", (string)conflict["reason"]);

            JObject measured = SectionMarkRules.Verdict(false, new List<JObject> { C("view_range", "yes"), C("phase", "unknown"), C("view_filter", "no") }, null);
            Assert.Equal("not_visible_cause_measured", (string)measured["verdict"]);
            Assert.Equal("view_range", (string)measured["blocking_causes"][0]);
            Assert.Equal("phase", (string)measured["unknown_causes"][0]);

            Assert.Equal("not_visible_cause_not_ruled_out", (string)SectionMarkRules.Verdict(false, new List<JObject> { C("phase", "unknown") }, null)["verdict"]);
            Assert.Equal("not_visible_no_cause_found", (string)SectionMarkRules.Verdict(false, new List<JObject> { C("phase", "no"), C("view_range", "not_applicable") }, null)["verdict"]);

            JObject unknown = SectionMarkRules.Verdict(null, new List<JObject>(), "viewers unreadable");
            Assert.Equal("unknown", (string)unknown["verdict"]);
            Assert.Equal("viewers unreadable", (string)unknown["reason"]);
        }

        [Fact]
        public void the_read_mode_is_registered_and_validated()
        {
            Assert.Contains("section_mark_diagnosis", NoteLeaderRules.ReadModes);
            Assert.Contains("section_mark_diagnosis", PlanimetryInventoryArgs.Modes);
            Assert.Contains("view_id", NoteLeaderRules.ValidateRead("section_mark_diagnosis", new JObject()));
            Assert.Null(NoteLeaderRules.ValidateRead("section_mark_diagnosis", new JObject { ["view_id"] = 5 }));
            Assert.Null(NoteLeaderRules.ValidateRead("section_mark_diagnosis", new JObject { ["view_id"] = 5, ["section_view_id"] = 9 }));
            Assert.Contains("section_view_id", NoteLeaderRules.ValidateRead("section_mark_diagnosis", new JObject { ["view_id"] = 5, ["section_view_id"] = "x" }));
            Assert.True(NoteLeaderRules.NoteReadFieldAllowed("section_mark_diagnosis", "section_view_id"));
            Assert.False(NoteLeaderRules.NoteReadFieldAllowed("section_mark_diagnosis", "points_mm"));
        }

        [Fact]
        public void a_disabled_filter_hides_nothing_and_an_unreadable_enabled_state_is_unknown()
        {
            var off = new List<FilterVerdict> { new FilterVerdict { Name = "F", Visible = false, Enabled = false, Matches = true } };
            Assert.Equal("no", (string)SectionMarkRules.Filters(off, null)["blocks_mark"]);
            var unread = new List<FilterVerdict> { new FilterVerdict { Name = "F", Visible = false, Enabled = null, Matches = true } };
            Assert.Equal("unknown", (string)SectionMarkRules.Filters(unread, null)["blocks_mark"]);
            Assert.Equal("unknown", (string)SectionMarkRules.Filters(new List<FilterVerdict>(), "no viewer")["blocks_mark"]);
        }

        [Fact]
        public void a_view_element_without_phase_is_not_applicable_never_unknown()
        {
            JObject c = SectionMarkRules.Phase("NotApplicable", "New", "Show New", null);
            Assert.Equal("not_applicable", (string)c["blocks_mark"]);
        }

        [Fact]
        public void a_crop_shape_that_cannot_be_read_is_unknown_and_a_non_plan_is_not_applicable()
        {
            double[] rect = { 0, 0, 10000, 10000 };
            Assert.Equal("unknown", (string)SectionMarkRules.Crop(true, null, new double[] { 20000, 0, 30000, 0 }, rect, null)["blocks_mark"]);
            Assert.Equal("unknown", (string)SectionMarkRules.CropNotMeasured("Section")["blocks_mark"]);
        }

        [Fact]
        public void a_section_that_crosses_the_range_but_not_the_cut_plane_is_unknown()
        {
            // cut plane at 1200: a section from 1500 to 2000 is inside the range, above the cut plane
            JObject c = Range(1500, 2000);
            Assert.Equal("unknown", (string)c["blocks_mark"]);
            Assert.Equal("crosses_range_not_cut_plane", (string)c["measured"]["relation"]);
            Assert.Equal("no", (string)Range(1200, 1300)["blocks_mark"]);
            Assert.Equal("unknown", (string)SectionMarkRules.ZRange(ZBound.Unknown("x"), ZBound.At(2300), ZBound.At(0), ZBound.At(-300), 100, 200, null)["blocks_mark"]);
        }

        [Fact]
        public void the_edges_of_the_range_use_the_epsilon_and_open_planes()
        {
            // the section ends exactly on the bottom plane: it still crosses (cut plane inside it)
            Assert.Equal("no", (string)SectionMarkRules.ZRange(ZBound.At(-50), ZBound.At(2300), ZBound.At(0), ZBound.At(-300), -900, 0, null)["blocks_mark"]);
            // ends 0.005 mm below the bottom: inside the epsilon, still crosses
            Assert.Equal("no", (string)SectionMarkRules.ZRange(ZBound.At(-0.0025), ZBound.At(2300), ZBound.At(0), ZBound.At(-300), -900, -0.005, null)["blocks_mark"]);
            // ends 0.5 mm below the bottom and exactly on the view depth: the band, unknown
            Assert.Equal("only_in_view_depth_band", (string)Range(-900, -0.5)["measured"]["relation"]);
            JObject onDepth = Range(-900, -300);
            Assert.Equal("only_in_view_depth_band", (string)onDepth["measured"]["relation"]);
            // 0.02 mm below the view depth: beyond the epsilon, below it
            Assert.Equal("below_view_depth", (string)Range(-900, -300.02)["measured"]["relation"]);
            // an open bottom plane makes everything below the top cross; an open depth makes nothing "below the view depth"
            Assert.Equal("no", (string)SectionMarkRules.ZRange(ZBound.At(-850), ZBound.At(2300), ZBound.Unlimited(), ZBound.Unlimited(), -900, -800, null)["blocks_mark"]);
            Assert.Equal("only_in_view_depth_band", (string)SectionMarkRules.ZRange(ZBound.At(1200), ZBound.At(2300), ZBound.At(0), ZBound.Unlimited(), -90000, -800, null)["measured"]["relation"]);
        }

        [Fact]
        public void older_modes_refuse_the_fields_of_the_section_and_note_modes()
        {
            var r = new JObject { ["mode"] = "views", ["section_view_id"] = 5, ["view_id"] = 3 };
            string refusal = PlanimetryInventoryArgs.IgnoredFieldRefusal(r, "views");
            Assert.Contains("section_view_id", refusal);
            Assert.Contains("view_id", refusal);
            Assert.Contains("Nothing was read", refusal);
            Assert.Null(PlanimetryInventoryArgs.IgnoredFieldRefusal(new JObject { ["mode"] = "views", ["view_ids"] = new JArray(1), ["max_rows"] = 5 }, "views"));
        }

        [Fact]
        public void a_section_cannot_be_asked_against_itself()
        {
            Assert.Contains("same view", NoteLeaderRules.ValidateRead("section_mark_diagnosis", new JObject { ["view_id"] = 5, ["section_view_id"] = 5 }));
        }

        [Fact]
        public void only_a_viewer_with_the_name_of_the_section_that_is_not_drawn_in_it_can_be_the_mark()
        {
            // (isView, isMarkCategory, referencesOtherView, drawnInSection, nameEqualsSection, otherViewHasThatName)
            Assert.Null(SectionMarkRules.DiscardReason(false, true, false, false, true, false));
            Assert.Equal("dependent_view", SectionMarkRules.DiscardReason(true, true, true, true, true, true));   // a view is a view first
            Assert.Equal("not_a_viewer", SectionMarkRules.DiscardReason(false, false, false, false, true, false));
            Assert.Equal("references_other_view", SectionMarkRules.DiscardReason(false, true, true, false, true, false));
            Assert.Equal("drawn_in_section", SectionMarkRules.DiscardReason(false, true, false, true, false, true));
            Assert.Equal("viewer_of_other_view", SectionMarkRules.DiscardReason(false, true, false, false, false, true));
            Assert.Equal("name_differs", SectionMarkRules.DiscardReason(false, true, false, false, false, false));
        }

        [Fact]
        public void the_category_is_compared_by_raw_id_and_viewers_passes()
        {
            // BuiltInCategory values are ints (negative for Viewers); a null category must never decide the match
            long viewers = -2000290, sections = -2000200, walls = -2000011;
            Assert.True(SectionMarkRules.IsMarkCategory(viewers, new[] { sections, viewers }));
            Assert.True(SectionMarkRules.IsMarkCategory(sections, new[] { sections, viewers }));
            Assert.False(SectionMarkRules.IsMarkCategory(-1, new[] { sections, viewers }));
            Assert.False(SectionMarkRules.IsMarkCategory(walls, new[] { sections, viewers }));
        }

        [Fact]
        public void the_mark_is_found_only_when_every_valid_candidate_agrees()
        {
            string reason;
            Assert.True(SectionMarkRules.MarkFound(1, 1, out reason)); Assert.Null(reason);
            Assert.True(SectionMarkRules.MarkFound(2, 2, out reason));
            Assert.False(SectionMarkRules.MarkFound(2, 0, out reason)); Assert.Null(reason);
            Assert.Null(SectionMarkRules.MarkFound(0, 0, out reason)); Assert.Contains("no viewer", reason);
            Assert.Null(SectionMarkRules.MarkFound(2, 1, out reason)); Assert.Contains("1 of the 2", reason);
            // and an unknown mark is never "visible"
            Assert.Equal("unknown", (string)SectionMarkRules.Verdict(null, new List<JObject>(), reason)["verdict"]);
        }

        [Fact]
        public void api_help_warns_that_move_element_does_not_move_a_section_and_create_reference_can_be_null()
        {
            var map = (System.Collections.IDictionary)typeof(ApiHelpRules).GetField("FieldTrapsByType",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static).GetValue(null);
            string text = string.Join(" ", (string[])map["Autodesk.Revit.DB.ViewSection"]);
            Assert.Contains("MoveElement", text);
            Assert.Contains("CropBox", text);
            Assert.Contains("CreateReferenceSection", text);
            Assert.Contains("can return null", text);
            Assert.DoesNotContain("5 cases", text);
        }

        [Fact]
        public void Category_rows_carry_where_each_value_was_read_from()
        {
            var cats = new List<KeyValuePair<string, bool?>> { new KeyValuePair<string, bool?>("OST_Sections", false), new KeyValuePair<string, bool?>("OST_Viewers", false) };
            var ev = new Dictionary<string, JObject>
            {
                ["OST_Sections"] = new JObject { ["can_be_hidden"] = true, ["read_from"] = "view_and_template" },
                ["OST_Viewers"] = new JObject { ["can_be_hidden"] = false, ["read_from"] = "cannot_be_hidden" },
            };
            JObject c = SectionMarkRules.CategoryHidden(cats, "Plan template", ev);
            Assert.Equal("no", (string)c["blocks_mark"]);
            var rows = (JArray)c["measured"]["categories"];
            Assert.Equal("view_and_template", (string)rows[0]["read_from"]);
            Assert.False((bool)rows[1]["can_be_hidden"]);
            Assert.Equal("cannot_be_hidden", (string)rows[1]["read_from"]);
            // An unreadable row keeps the cause unknown whatever its evidence says.
            var unread = new List<KeyValuePair<string, bool?>> { new KeyValuePair<string, bool?>("OST_Sections", null) };
            Assert.Equal("unknown", (string)SectionMarkRules.CategoryHidden(unread, null,
                new Dictionary<string, JObject> { ["OST_Sections"] = new JObject { ["read_from"] = "unreadable" } })["blocks_mark"]);
        }
    }
}
