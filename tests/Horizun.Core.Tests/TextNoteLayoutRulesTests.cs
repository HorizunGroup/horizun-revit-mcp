// horizun_annotate operation=text verified on PAPER (5.2), without Revit. Each field defect from the
// "detalles constructivos de Colon" report (Revit 2024) has a test that the old behaviour fails:
// the old code accepted any width, never counted printed lines, used get_BoundingBox(view) as read,
// left the leader side to Revit and placed leaders without asking what they point at.
using System;
using System.IO;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class TextNoteLayoutRulesTests
    {
        private const double Ft = 304.8;

        // ---- 1. width in paper millimetres ---------------------------------------------------

        [Fact]
        public void a_model_length_given_as_width_is_refused_and_the_paper_value_is_named()
        {
            // Field: Width multiplied by the scale gave a note 1.75 m wide. At 1:50, 35 mm of paper is 1750 mm of model.
            string why = TextNoteLayoutRules.WidthProblem(1750, 2 / Ft, 600 / Ft, 50);
            Assert.NotNull(why);
            Assert.Contains("MODEL length", why);
            Assert.Contains("35 mm", why);
            Assert.Contains("Nothing was written", why);
        }

        [Fact]
        public void a_width_below_the_type_minimum_is_refused_because_the_note_stops_wrapping()
        {
            string why = TextNoteLayoutRules.WidthProblem(1.5, 3.2 / Ft, 600 / Ft, 50);
            Assert.NotNull(why);
            Assert.Contains("GetMinimumAllowedWidth", why);
            Assert.Contains("one long line", why);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-5)]
        [InlineData(double.NaN)]
        [InlineData(double.PositiveInfinity)]
        public void a_width_that_is_not_a_positive_length_is_refused(double mm)
            => Assert.NotNull(TextNoteLayoutRules.WidthProblem(mm, 2 / Ft, 600 / Ft, 50));

        [Fact]
        public void a_paper_width_inside_the_allowed_range_passes_and_converts_to_paper_feet()
        {
            Assert.Null(TextNoteLayoutRules.WidthProblem(35, 2 / Ft, 600 / Ft, 50));
            Assert.Equal(35 / Ft, TextNoteLayoutRules.PaperFeet(35), 12);
            Assert.True(TextNoteLayoutRules.WidthMatches(35 / Ft, 35.05 / Ft));
            Assert.False(TextNoteLayoutRules.WidthMatches(35 / Ft, 35 * 50 / Ft));   // the scaled value is a mismatch
        }

        // ---- 2. printed lines ------------------------------------------------------------------

        [Fact]
        public void the_note_that_did_not_wrap_fails_expected_lines_although_its_width_re_reads_right()
        {
            // Probes: one line 3.5 mm high, each further line +3.0 mm. The field note printed ONE line where 3 were wanted.
            double one = 3.5 / Ft, pitch = 3.0 / Ft;
            int? printed = TextNoteLayoutRules.LinesFromHeight(3.5 / Ft, one, pitch);
            Assert.Equal(1, printed);
            JObject v = TextNoteLayoutRules.LinesVerdict(3, printed, null, 1, out bool ok);
            Assert.False(ok);
            Assert.Contains("did not wrap", v.Value<string>("lines_reason"));
            Assert.Equal(1, v.Value<int>("lines_measured"));
        }

        [Fact]
        public void three_printed_lines_are_counted_from_the_paper_height()
        {
            Assert.Equal(3, TextNoteLayoutRules.LinesFromHeight(9.6 / Ft, 3.5 / Ft, 3.0 / Ft));
            JObject v = TextNoteLayoutRules.LinesVerdict(3, 3, 3, 1, out bool ok);
            Assert.True(ok);
        }

        [Fact]
        public void a_height_between_two_line_counts_is_not_a_count()
            => Assert.Null(TextNoteLayoutRules.LinesFromHeight(5.0 / Ft, 3.5 / Ft, 3.0 / Ft));

        [Fact]
        public void an_unusable_calibration_gives_no_count_and_an_expectation_then_fails_closed()
        {
            Assert.Null(TextNoteLayoutRules.LinesFromHeight(9 / Ft, 3.5 / Ft, 0));
            JObject v = TextNoteLayoutRules.LinesVerdict(2, null, null, 1, out bool ok);
            Assert.False(ok);
            Assert.Contains("could not be measured", v.Value<string>("lines_reason"));
        }

        [Fact]
        public void two_measurements_that_disagree_make_the_count_unknown()
        {
            JObject v = TextNoteLayoutRules.LinesVerdict(2, 2, 3, 1, out bool ok);
            Assert.False(ok);
            Assert.Equal(JTokenType.Null, v["lines_measured"].Type);
            Assert.NotNull(v.Value<string>("lines_uncertain"));
        }

        [Fact]
        public void without_an_expectation_the_count_is_reported_and_nothing_fails()
        {
            JObject v = TextNoteLayoutRules.LinesVerdict(null, 2, null, 1, out bool ok);
            Assert.True(ok);
            Assert.Equal(2, v.Value<int>("lines_measured"));
        }

        [Theory]
        [InlineData("one", 1)]
        [InlineData("one\rtwo", 2)]
        [InlineData("one\r\ntwo\nthree", 3)]
        [InlineData("trailing\r", 1)]
        [InlineData("", 1)]
        public void hard_breaks_count_once_each(string text, int lines)
            => Assert.Equal(lines, TextNoteLayoutRules.HardLineCount(text));

        // ---- the get_BoundingBox(view) trap ------------------------------------------------------

        [Fact]
        public void a_box_with_coordinates_multiplied_by_the_scale_is_corrected_against_the_anchor()
        {
            // The note sits at (10, 20) ft; its true box is 10..11.5 x 19.6..20. Read back multiplied by 50.
            double[] scaled = { 500, 980, 575, 1000 };
            var fix = TextNoteLayoutRules.NormalizeAnnotationBox(scaled, new[] { 10.0, 20.0 }, 50, 0.4);
            Assert.True(fix.Corrected);
            Assert.Equal("divided_by_view_scale", fix.Method);
            Assert.Equal(new[] { 10.0, 19.6, 11.5, 20.0 }, fix.Box.Select(x => Math.Round(x, 6)).ToArray());
        }

        [Fact]
        public void a_box_that_contains_its_anchor_is_used_as_read()
        {
            var fix = TextNoteLayoutRules.NormalizeAnnotationBox(new[] { 10.0, 19.6, 11.5, 20.0 }, new[] { 10.0, 20.0 }, 50, 0.4);
            Assert.False(fix.Corrected);
            Assert.Equal("as_read", fix.Method);
        }

        [Fact]
        public void a_box_that_contains_its_anchor_neither_way_is_unreliable_and_never_used()
        {
            var fix = TextNoteLayoutRules.NormalizeAnnotationBox(new[] { 100.0, 100.0, 101.0, 101.0 }, new[] { 10.0, 20.0 }, 50, 0.4);
            Assert.Null(fix.Box);
            Assert.Equal("unreliable", fix.Method);
        }

        [Fact]
        public void the_planimetry_reads_route_annotation_boxes_through_the_anchor_check()
        {
            string root = Repo();
            string ops = File.ReadAllText(Path.Combine(root, "src", "Horizun.Revit", "Commands", "PlanimetryOpsCommand.cs"));
            int start = ops.IndexOf("private static double[] Box2D(", StringComparison.Ordinal);
            Assert.True(start > 0);
            string body = ops.Substring(start, 400);
            Assert.Contains("AnnotationBoxReader.ViewBox", body);
            Assert.DoesNotContain("get_BoundingBox(v)", body);
            string sheet = File.ReadAllText(Path.Combine(root, "src", "Horizun.Revit", "Commands", "QueryPlanimetryCommand.Notes.cs"));
            Assert.Contains("AnnotationBoxReader.Fixed(tn.get_BoundingBox(view), tn, view)", sheet);
        }

        [Fact]
        public void a_correct_note_rotated_90_degrees_is_counted_by_its_height_alone_and_passes()
        {
            // Three printed lines, 40 mm wide, rotated 90 degrees in a plan: the axis-aligned view box is 40 mm HIGH (the
            // width), which used to read as 13 lines, disagree with the height, and roll a correct note back.
            double one = 3.5 / Ft, pitch = 3.0 / Ft;
            int? byHeight = TextNoteLayoutRules.LinesFromHeight(9.5 / Ft, one, pitch);
            int? oldByBox = TextNoteLayoutRules.LinesFromHeight(39.5 / Ft, one, pitch);
            Assert.Equal(3, byHeight);
            Assert.Equal(13, oldByBox);
            TextNoteLayoutRules.LinesVerdict(3, byHeight, oldByBox, 1, out bool oldOk);
            Assert.False(oldOk);

            double rotation = 90 * Math.PI / 180;
            string skip = TextNoteLayoutRules.BoxCountSkip(Math.Cos(rotation), false);
            Assert.Equal("skipped_rotated_or_3d", skip);
            int? byBox = skip == null ? oldByBox : null;
            JObject v = TextNoteLayoutRules.LinesVerdict(3, byHeight, byBox, 1, out bool ok);
            Assert.True(ok);
            Assert.Equal(3, v.Value<int>("lines_measured"));
        }

        [Fact]
        public void the_view_box_counts_only_an_unrotated_note_outside_3d_views()
        {
            Assert.Null(TextNoteLayoutRules.BoxCountSkip(1.0, false));
            Assert.Null(TextNoteLayoutRules.BoxCountSkip(-1.0, false));
            Assert.Equal("skipped_rotated_or_3d", TextNoteLayoutRules.BoxCountSkip(1.0, true));
            Assert.Equal("skipped_rotated_or_3d", TextNoteLayoutRules.BoxCountSkip(Math.Cos(30 * Math.PI / 180), false));
            Assert.Equal("skipped_rotated_or_3d", TextNoteLayoutRules.BoxCountSkip(double.NaN, false));
        }

        [Fact]
        public void the_text_command_skips_the_box_count_and_calibrates_only_for_expected_lines()
        {
            string src = File.ReadAllText(Path.Combine(Repo(), "src", "Horizun.Revit", "Commands", "AnnotateCommand.TextNotes.cs"));
            Assert.Contains("TextNoteLayoutRules.BoxCountSkip(baseDotRight, v is View3D)", src);
            Assert.Contains("box_measured_before_leader", src);
            Assert.Contains("!p.TextPlan.Any || p.TextPlan.ExpectedLines == null", src);
        }

        [Fact]
        public void only_annotate_starts_a_3d_ray_back_toward_the_viewer()
        {
            string root = Repo();
            string reader = File.ReadAllText(Path.Combine(root, "src", "Horizun.Revit", "Commands", "LeaderTargetReader.cs"));
            Assert.Contains("bool backOff3D = false", reader);
            Assert.Contains("if (backOff3D && view is View3D", reader);
            foreach (string f in new[] { "QueryPlanimetryCommand.Notes.cs", "PlanimetryOpsCommand.Notes.cs" })
                Assert.DoesNotContain("backOff3D", File.ReadAllText(Path.Combine(root, "src", "Horizun.Revit", "Commands", f)));
        }

        // ---- 4. the side the leader leaves from ---------------------------------------------------

        [Fact]
        public void a_right_aligned_note_with_its_leader_end_inside_the_box_is_refused()
        {
            // Field: right-aligned note, elbow inside the text box: Revit drew the arrow from the other side across the sheet.
            double[] span = TextNoteLayoutRules.BoxSpan("Right", 100, 40);       // paper mm: 60 .. 100
            Assert.Equal(new[] { 60.0, 100.0 }, span);
            string side = TextNoteLayoutRules.ResolveSide("right", span[0], span[1], 90, 0.5, out string error);
            Assert.Null(side);
            Assert.Contains("OTHER side", error);
            side = TextNoteLayoutRules.ResolveSide("auto", span[0], span[1], 80, 0.5, out error);
            Assert.Null(side);
        }

        [Fact]
        public void auto_picks_the_side_the_end_is_clear_of_and_an_explicit_side_must_hold()
        {
            Assert.Equal("right", TextNoteLayoutRules.ResolveSide("auto", 60, 100, 120, 0.5, out _));
            Assert.Equal("left", TextNoteLayoutRules.ResolveSide("auto", 60, 100, 30, 0.5, out _));
            Assert.Null(TextNoteLayoutRules.ResolveSide("left", 60, 100, 120, 0.5, out string error));
            Assert.Contains("left", error);
            Assert.Null(TextNoteLayoutRules.ResolveSide("up", 60, 100, 120, 0.5, out error));
        }

        [Fact]
        public void the_elbow_half_way_to_a_clear_end_is_outside_the_box_and_one_inside_is_caught()
        {
            double anchor = 100, end = 120, elbow = (anchor + end) / 2;
            Assert.False(TextNoteLayoutRules.ElbowInsideBox(elbow, 60, 100, 0.5));
            Assert.True(TextNoteLayoutRules.ElbowInsideBox(95, 60, 100, 0.5));
            Assert.Equal(new[] { 80.0, 120.0 }, TextNoteLayoutRules.BoxSpan("center", 100, 40));
            Assert.Equal(new[] { 100.0, 140.0 }, TextNoteLayoutRules.BoxSpan("Left", 100, 40));
        }

        // ---- 3. what the leader points at ---------------------------------------------------------

        [Fact]
        public void leader_target_forms_are_exclusive_and_complete()
        {
            Assert.Equal("point", TextNoteLayoutRules.LeaderTargetForm(true, false, false, false, out _));
            Assert.Equal("element", TextNoteLayoutRules.LeaderTargetForm(false, true, false, false, out _));
            Assert.Equal("linked_element", TextNoteLayoutRules.LeaderTargetForm(true, false, true, true, out _));
            Assert.Null(TextNoteLayoutRules.LeaderTargetForm(false, true, true, true, out string e1)); Assert.NotNull(e1);
            Assert.Null(TextNoteLayoutRules.LeaderTargetForm(false, false, true, false, out string e2)); Assert.NotNull(e2);
            Assert.Null(TextNoteLayoutRules.LeaderTargetForm(false, false, false, false, out string e3)); Assert.NotNull(e3);
        }

        [Fact]
        public void a_ray_that_meets_another_element_is_refused_naming_it_and_its_nested_link()
        {
            string why = TextNoteLayoutRules.HitMismatch("element 9 of link instance 5", "Walls", 1234, "Basic Wall : Muro 15", "Estructura.rvt", 5, 2);
            Assert.Contains("Walls 1234", why);
            Assert.Contains("Basic Wall : Muro 15", why);
            Assert.Contains("link instance 5 'Estructura.rvt'", why);
            Assert.Contains("nested 2 levels", why);
            Assert.Contains("not element 9 of link instance 5", why);
        }

        // ---- 5. a note nobody will see -----------------------------------------------------------------

        [Fact]
        public void a_view_with_annotation_categories_off_is_warned_and_an_isometric_says_why_it_matters()
        {
            Assert.Null(TextNoteLayoutRules.HiddenWarning(false, false, false, true));
            string w = TextNoteLayoutRules.HiddenWarning(true, false, false, true);
            Assert.Contains("NOT be visible", w);
            Assert.Contains("levels and grids", w);
            Assert.Contains("Text Notes", TextNoteLayoutRules.HiddenWarning(false, true, false, false));
        }

        // ---- the 3D ray origin ---------------------------------------------------------------------

        [Fact]
        public void a_ray_from_far_back_enters_the_section_box_at_its_near_face()
        {
            double? t = TextNoteLayoutRules.RayBoxEntry(new[] { 0.0, 0, 100 }, new[] { 0.0, 0, -1 }, new[] { -5.0, -5, -5 }, new[] { 5.0, 5, 5 });
            Assert.Equal(95, t.Value, 9);
            Assert.Null(TextNoteLayoutRules.RayBoxEntry(new[] { 20.0, 0, 100 }, new[] { 0.0, 0, -1 }, new[] { -5.0, -5, -5 }, new[] { 5.0, 5, 5 }));
        }

        // ---- fields that belong to text only --------------------------------------------------------

        [Fact]
        public void a_tag_or_dimension_carrying_text_note_fields_is_refused_instead_of_dropping_them()
        {
            Assert.Null(TextNoteLayoutRules.ForeignFields("text", f => true));
            string why = TextNoteLayoutRules.ForeignFields("tag", f => f == "width_mm" || f == "leader_target");
            Assert.Contains("width_mm, leader_target", why);
            Assert.Null(TextNoteLayoutRules.ForeignFields("dimension", f => false));
        }

        // ---- horizun_api_help carries the traps --------------------------------------------------------

        [Fact]
        public void api_help_reports_the_measured_text_and_box_traps_for_a_text_note()
        {
            JArray traps = ApiHelpRules.FieldTraps(typeof(Autodesk.Revit.DB.TextNote));
            Assert.Contains(traps, t => t.Value<string>("trap").Contains("OTHER side"));
            Assert.Contains(traps, t => t.Value<string>("trap").Contains("get_BoundingBox(view)"));
            Assert.Empty(ApiHelpRules.FieldTraps(typeof(string)));
        }

        private static string Repo()
        {
            string dir = AppContext.BaseDirectory;
            while (dir != null && !File.Exists(Path.Combine(dir, "global.json"))) dir = Path.GetDirectoryName(dir);
            return dir ?? throw new InvalidOperationException("repository root not found");
        }
    }
}
