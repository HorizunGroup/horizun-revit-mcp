// horizun_annotate, six defects measured live in Revit 2025.4 (build 7ec39c59, sample model), each pinned here
// without Revit: the pure rule behind every fix.
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class AnnotateLiveFixesTests
    {
        // ---- 1. width_mm against the VIEW (2000 mm of paper in a 153 mm crop was accepted and "verified") ----------

        [Fact]
        public void A_width_wider_than_the_active_crop_on_paper_is_refused_with_the_suggested_number()
        {
            string why = TextNoteLayoutRules.WidthFrameProblem(2000, 16, 153.0, 100.0, 0, 841, false);
            Assert.NotNull(why);
            Assert.Contains("active crop", why);
            Assert.Contains("125 mm", why);                 // 2000 / 16
            Assert.Contains("width_mm 125", why);
            Assert.Contains("allow_outside_crop", why);
            Assert.Contains("Nothing was written", why);
        }

        [Fact]
        public void A_width_that_fits_the_crop_is_accepted()
        {
            Assert.Null(TextNoteLayoutRules.WidthFrameProblem(125, 16, 153.0, 100.0, 0, 841, false));
            Assert.Null(TextNoteLayoutRules.WidthFrameProblem(153.0, 16, 153.0, 100.0, 0, 841, false));
        }

        [Fact]
        public void Without_a_crop_the_limit_is_an_A1_sheet()
        {
            Assert.Null(TextNoteLayoutRules.WidthFrameProblem(841, 50, null, null, 0, TextNoteLayoutRules.UncroppedPaperLimitMm, false));
            string why = TextNoteLayoutRules.WidthFrameProblem(900, 50, null, null, 0, TextNoteLayoutRules.UncroppedPaperLimitMm, false);
            Assert.Contains("no active crop", why);
            Assert.Contains("841", why);
            Assert.Contains("width_mm 18", why);            // 900 / 50
        }

        [Fact]
        public void allow_outside_crop_lifts_the_frame_check_on_purpose()
        {
            Assert.Null(TextNoteLayoutRules.WidthFrameProblem(2000, 16, 153.0, 100.0, 0, 841, true));
        }

        [Fact]
        public void A_scale_of_one_gives_no_model_length_suggestion()
        {
            string why = TextNoteLayoutRules.WidthFrameProblem(2000, 1, 153.0, 100.0, 0, 841, false);
            Assert.NotNull(why);
            Assert.DoesNotContain("MODEL length", why);
        }

        // ---- 2. a leader aimed at an element the plan CUTS --------------------------------------------------------

        [Fact]
        public void A_note_turned_to_about_90_degrees_is_compared_with_the_crop_height()
        {
            // crop 153 wide x 100 high on paper: 120 mm fits along the width, not along the height
            Assert.Null(TextNoteLayoutRules.WidthFrameProblem(120, 16, 153.0, 100.0, 0, 841, false));
            string why = TextNoteLayoutRules.WidthFrameProblem(120, 16, 153.0, 100.0, 90, 841, false);
            Assert.NotNull(why);
            Assert.Contains("100 mm high", why);
            Assert.NotNull(TextNoteLayoutRules.WidthFrameProblem(120, 16, 153.0, 100.0, 270, 841, false));
            Assert.NotNull(TextNoteLayoutRules.WidthFrameProblem(120, 16, 153.0, 100.0, -85, 841, false));
            Assert.Null(TextNoteLayoutRules.WidthFrameProblem(120, 16, 153.0, 100.0, 45, 841, false));   // 45 degrees: along the width
            Assert.True(TextNoteLayoutRules.RunsAlongHeight(99));
            Assert.False(TextNoteLayoutRules.RunsAlongHeight(180));
        }

        // ---- 2. a leader aimed at an element the plan CUTS --------------------------------------------------------

        [Fact]
        public void Nothing_against_it_lets_a_start_point_inside_the_cut_wall_count_in_a_plan()
        {
            string blocker = TextNoteLayoutRules.InsideHitBlocker(true, true, false, false, true, true, false);
            Assert.Null(blocker);
            Assert.True(TextNoteLayoutRules.CutPlaneInsideCountsAsHit(false, blocker, true));
            Assert.False(TextNoteLayoutRules.CutPlaneInsideCountsAsHit(false, blocker, false));    // started outside the solid
            Assert.True(TextNoteLayoutRules.CutPlaneInsideCountsAsHit(true, "anything", false));   // the ray's own first hit is the target
        }

        [Theory]
        [InlineData(false, true, false, false, true, true, false, "not a plan view")]
        [InlineData(true, false, false, false, true, true, false, "cut plane could not be read")]
        [InlineData(true, true, true, false, true, true, false, "link")]
        [InlineData(true, true, false, false, true, true, true, "room, space, area or mass")]
        [InlineData(true, true, false, true, true, true, false, "hidden in this view")]
        [InlineData(true, true, false, false, false, true, false, "category is hidden")]
        [InlineData(true, true, false, false, true, false, false, "collector does not list it")]
        public void Every_visibility_guard_blocks_the_inside_hit(bool plan, bool cutRead, bool link, bool hidden, bool catVisible, bool inCollector, bool spatial, string expected)
        {
            string blocker = TextNoteLayoutRules.InsideHitBlocker(plan, cutRead, link, hidden, catVisible, inCollector, spatial);
            Assert.NotNull(blocker);
            Assert.Contains(expected, blocker);
            // a blocked start point never counts, even inside the solid
            Assert.False(TextNoteLayoutRules.CutPlaneInsideCountsAsHit(false, blocker, true));
        }

        [Fact]
        public void A_face_touch_is_not_inside_the_solid()
        {
            double probe = 0.02;
            Assert.True(TextNoteLayoutRules.SegmentProvesInside(0.02, probe, 0));
            Assert.True(TextNoteLayoutRules.SegmentProvesInside(0.019, probe, 0));
            Assert.False(TextNoteLayoutRules.SegmentProvesInside(0.01, probe, 0));       // half the probe: the point is on a face
            Assert.False(TextNoteLayoutRules.SegmentProvesInside(0.0, probe, 0));        // degenerate
            Assert.False(TextNoteLayoutRules.SegmentProvesInside(0.02, probe, 0.005));   // the segment does not contain the point
            Assert.False(TextNoteLayoutRules.SegmentProvesInside(0.02, 0.0, 0));
        }

        // ---- 3. keynote_material: a KEYNOTE tag whose INSTANCE Key Source is Material ---------------------------------

        [Fact]
        public void Key_source_is_read_from_the_string_Revit_2025_stores()
        {
            Assert.Equal(TagRules.KeySourceElement, TagRules.KeySourceFromText("Element"));
            Assert.Equal(TagRules.KeySourceMaterial, TagRules.KeySourceFromText(" material "));
            Assert.Equal(TagRules.KeySourceUser, TagRules.KeySourceFromText("User"));
            Assert.Null(TagRules.KeySourceFromText("Other"));
            Assert.Null(TagRules.KeySourceFromText(null));
            Assert.Equal("Material", TagRules.KeySourceText(TagRules.KeySourceMaterial));
            Assert.Null(TagRules.KeySourceText(7));
        }

        [Fact]
        public void A_material_keynote_is_proven_only_by_an_instance_that_reads_Material()
        {
            Assert.Null(TagRules.KeynoteVerificationFailure("keynote_material", TagRules.KeySourceFromText("Material"), "09 91 00", "09 91 00", "09 91 00", null));
            Assert.Null(TagRules.KeynoteVerificationFailure("keynote_material", 1, "09 91 00", "09 91 00", "Painted gypsum", "Painted gypsum"));
            // Source Material alone proves nothing: the tag must read and SHOW the keynote of the material of its face.
            Assert.Contains("unknown", TagRules.KeynoteVerificationFailure("keynote_material", 1, "09 91 00", null, "09 91 00", null));
            Assert.Contains("reads key", TagRules.KeynoteVerificationFailure("keynote_material", 1, "05 40 00", "09 91 00", "05 40 00", null));
            Assert.Contains("shows", TagRules.KeynoteVerificationFailure("keynote_material", 1, "09 91 00", "09 91 00", "?", "Painted gypsum"));
            Assert.Contains("could not be read", TagRules.KeynoteVerificationFailure("keynote_material", 1, "09 91 00", "09 91 00", null, null));
            Assert.Contains("expects key source 1", TagRules.KeynoteVerificationFailure("keynote_material", TagRules.KeySourceFromText("Element"), null, null, "x", null));
            Assert.Contains("unreadable key source", TagRules.KeynoteVerificationFailure("keynote_material", TagRules.KeySourceFromText("???"), null, null, "x", null));
        }

        [Fact]
        public void The_keynote_type_category_serves_every_keynote_mode()
        {
            Assert.Null(TagRules.TypeCategoryRefusal("keynote_material", "OST_Walls", "OST_KeynoteTags"));
            Assert.Contains("OST_KeynoteTags", TagRules.TypeCategoryRefusal("keynote_material", "OST_Walls", "OST_MaterialTags"));
            Assert.Equal("OST_KeynoteTags", TagRules.RequiredTypeCategory("keynote_material"));
            Assert.Equal("OST_MaterialTags", TagRules.RequiredTypeCategory("material"));
        }

        // ---- 4. a room tag whose type shows no area ---------------------------------------------------------------

        [Fact]
        public void A_tag_type_that_shows_no_area_is_proven_by_number_and_name()
        {
            Assert.Null(RoomTagRules.TextProblem("Residential Lobby106", "106", "Residential Lobby", "953 SF", 88.5));
            Assert.Null(RoomTagRules.TextProblem("Residential Lobby\r\n106", "106", "Residential Lobby", "953 SF", 88.5));
            Assert.Null(RoomTagRules.TextProblem("106 Residential Lobby", "106", "Residential Lobby", "953 SF", 88.5));
        }

        [Fact]
        public void The_full_text_with_the_area_still_passes_as_before()
        {
            Assert.Null(RoomTagRules.TextProblem("101\r\nDormitorio\r\n18,25 m²", "101", "Dormitorio", "18,25 m²", 18.25));
            Assert.Null(RoomTagRules.TextProblem("Habitación 1127.596 m²", "1", "Habitación 1", "27.596 m²", 27.5962));
        }

        [Fact]
        public void Without_the_number_or_the_name_a_room_tag_text_is_refused()
        {
            Assert.Contains("number and name", RoomTagRules.TextProblem("Residential Lobby", "106", "Residential Lobby", "953 SF", 88.5));
            Assert.Contains("number and name", RoomTagRules.TextProblem("106", "106", "Residential Lobby", "953 SF", 88.5));
            Assert.Contains("number and name", RoomTagRules.TextProblem("Other Room 106", "106", "Residential Lobby", "953 SF", 88.5));
            Assert.Contains("number and name", RoomTagRules.TextProblem("Residential Lobby1067", "106", "Residential Lobby", "953 SF", 88.5));
            Assert.Contains("number and name", RoomTagRules.TextProblem("", "106", "Residential Lobby", "953 SF", 88.5));
        }

        [Fact]
        public void An_area_that_is_shown_but_is_not_the_rooms_is_never_accepted()
        {
            string why = RoomTagRules.TextProblem("Residential Lobby106\r\n500 SF", "106", "Residential Lobby", "953 SF", 88.5);
            Assert.NotNull(why);
            Assert.Contains("shows an area", why);
            Assert.Contains("953 SF", why);
            Assert.True(RoomTagRules.ShowsAnArea("12,5 m²"));
            Assert.True(RoomTagRules.ShowsAnArea("953 SF"));
            Assert.False(RoomTagRules.ShowsAnArea("Residential Lobby106"));
        }

        // ---- 5. a room tag head outside the room, with no leader ---------------------------------------------------

        [Fact]
        public void A_room_tag_without_a_leader_must_be_inside_its_room()
        {
            string why = RoomTagRules.OutsideRoomRefusal(false, false, 828537);
            Assert.NotNull(why);
            Assert.Contains("outside room 828537", why);
            Assert.Contains("add_leader", why);
            Assert.Contains("Nothing was written", why);
            Assert.Null(RoomTagRules.OutsideRoomRefusal(false, true, 828537));   // a leader lets the head sit outside
            Assert.Null(RoomTagRules.OutsideRoomRefusal(true, false, 828537));
            Assert.Null(RoomTagRules.OutsideRoomRefusal(null, false, 828537));   // unknown: Revit decides, as before
        }

        [Fact]
        public void The_room_is_asked_at_its_own_base_plus_half_its_height_at_most_a_foot()
        {
            // level ProjectElevation 10 ft, base offset 0.5 ft, upper level at 20 ft with limit offset -1 ft: height 8.5 ft -> +1 ft (half is 4.25)
            Assert.Equal(11.5, RoomTagRules.ProbeZFeet(10, 0.5, 20, -1, null).Value, 9);
            // a 1.2 ft room: half its height (0.6 ft) is below the foot
            Assert.Equal(10.6, RoomTagRules.ProbeZFeet(10, 0, 11.2, 0, null).Value, 9);
            // no upper limit: the unbounded height decides (base -3, height 20 -> +1 ft)
            Assert.Equal(-2.0, RoomTagRules.ProbeZFeet(-3, 0, null, 0, 20).Value, 9);
            // a project whose levels sit far from zero keeps the same offsets from the room's own base
            Assert.Equal(1251.0, RoomTagRules.ProbeZFeet(1250, 0, 1262, 0, null).Value, 9);
            // no positive height: Revit decides (null), never "outside"
            Assert.Null(RoomTagRules.ProbeZFeet(10, 0, 10, 0, null));
            Assert.Null(RoomTagRules.ProbeZFeet(10, 0, null, 0, null));
            Assert.Null(RoomTagRules.ProbeZFeet(10, 0, 9, 0, null));
            Assert.Null(RoomTagRules.ProbeZFeet(10, 0, null, 0, 0));
        }

        // ---- 7. a best_effort dry run with a token is "rehearsed", not "rejected" ------------------------------------

        [Fact]
        public void A_best_effort_rehearsal_with_a_token_reads_rehearsed_and_one_with_failed_rows_reads_rejected()
        {
            var ok = new Newtonsoft.Json.Linq.JObject();
            ApplicationOutcome.StampRehearsal(ok, 12, 0, 0, 0);
            Assert.Equal(ApplicationState.Rehearsed, ApplicationOutcome.Read(ok));
            var refused = new Newtonsoft.Json.Linq.JObject();
            ApplicationOutcome.StampRehearsal(refused, 12, 0, 4, 0);
            Assert.Equal(ApplicationState.Rejected, ApplicationOutcome.Read(refused));
        }

        // ---- 6. will_apply / will_skip of a rejected dry run -------------------------------------------------------

        [Fact]
        public void A_rejected_batch_applies_nothing_and_skips_every_index()
        {
            // Field: 3 invalid of 15 returned the same 12 valid indices in will_apply and will_skip.
            var valid = Enumerable.Range(0, 12).ToList();
            AnnotationNoteRules.ApplySets(15, new[] { 12, 13, 14 }, null, out List<int> apply, out List<int> skip);
            Assert.Empty(apply);
            Assert.Equal(Enumerable.Range(0, 15), skip);
            // even if a caller passed constructible rows, an invalid batch applies nothing
            AnnotationNoteRules.ApplySets(15, new[] { 12 }, valid, out apply, out skip);
            Assert.Empty(apply);
            Assert.Equal(15, skip.Count);
        }

        [Fact]
        public void A_rehearsed_batch_applies_the_constructible_rows_and_skips_the_rest()
        {
            AnnotationNoteRules.ApplySets(6, new int[0], new[] { 4, 0, 2, 2 }, out List<int> apply, out List<int> skip);
            Assert.Equal(new[] { 0, 2, 4 }, apply);
            Assert.Equal(new[] { 1, 3, 5 }, skip);
            Assert.Empty(apply.Intersect(skip));
        }
    }
}
