// 4.2 front N2 (notes and leaders) without Revit: the request validation and the arithmetic of
// move_note, note_from_model, clone_tag, leader_targets, annotation_staleness, sheet_conflicts, free_spot.
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class NoteLeaderRulesTests
    {
        private static JObject Req(string op) => new JObject { ["target_document"] = "DOC", ["operation"] = op };

        [Fact]
        public void move_note_takes_its_own_fields_and_refuses_the_rest()
        {
            var r = Req("move_note"); r["note_ids"] = new JArray(10, 11); r["delta_mm"] = new JArray(5, -3); r["elbow"] = "L"; r["side"] = "auto"; r["width_mm"] = 60;
            Assert.Null(PlanimetryOpsRules.Validate(r));
            r["view_id"] = 4;
            Assert.Contains("view_id is not applicable", PlanimetryOpsRules.Validate(r));
            r.Remove("view_id");
            r["elbow"] = "diagonal";
            Assert.Contains("elbow must be", PlanimetryOpsRules.Validate(r));
            r["elbow"] = "L"; r["keep_leader_ends"] = false;
            Assert.Contains("only make sense with keep_leader_ends=true", PlanimetryOpsRules.Validate(r));
            r["keep_leader_ends"] = true; r["note_ids"] = new JArray();
            Assert.Contains("note_ids", PlanimetryOpsRules.Validate(r));
            r["note_ids"] = new JArray(1); r["width_mm"] = 1;
            Assert.Contains("width_mm", PlanimetryOpsRules.Validate(r));
            r["width_mm"] = 60; r["frame"] = "paper";
            Assert.Contains("frame must be", PlanimetryOpsRules.Validate(r));
        }

        [Fact]
        public void note_from_model_and_clone_tag_validate_their_entries()
        {
            var n = new JObject { ["element_id"] = 5, ["end_mm"] = new JArray(1, 2, 3), ["note_mm"] = new JArray(4, 5, 3) };
            var r = Req("note_from_model"); r["view_id"] = 3; r["notes"] = new JArray(n); r["source"] = "layers";
            Assert.Null(PlanimetryOpsRules.Validate(r));
            n["end_mm"] = new JArray(1, 2);
            Assert.Contains("end_mm and note_mm", PlanimetryOpsRules.Validate(r));
            n["end_mm"] = new JArray(1, 2, 3); n["colour"] = "red";
            Assert.Contains("colour is not a field", PlanimetryOpsRules.Validate(r));
            n.Remove("colour"); r["source"] = "guess";
            Assert.Contains("source must be", PlanimetryOpsRules.Validate(r));

            var c = Req("clone_tag"); c["model_tag_id"] = 9;
            c["targets"] = new JArray(new JObject { ["element_id"] = 1, ["head_mm"] = new JArray(0, 0, 0) });
            Assert.Null(PlanimetryOpsRules.Validate(c));
            c["targets"] = new JArray(new JObject { ["element_id"] = 1 });
            Assert.Contains("head_mm", PlanimetryOpsRules.Validate(c));
            c.Remove("model_tag_id");
            Assert.Contains("model_tag_id", PlanimetryOpsRules.Validate(c));
        }

        [Fact]
        public void round_two_operations_validate_their_fields()
        {
            var a = Req("arrange_column"); a["view_id"] = 3; a["side"] = "left"; a["gap_mm"] = 4;
            Assert.Null(PlanimetryOpsRules.Validate(a));
            a["side"] = "auto";
            Assert.Contains("side must be left or right", PlanimetryOpsRules.Validate(a));

            var s = Req("set_leader_end"); s["note_ids"] = new JArray(7); s["end_mm"] = new JArray(1, 2, 3);
            Assert.Null(PlanimetryOpsRules.Validate(s));
            s["delta_mm"] = new JArray(1, 1);
            Assert.Contains("exactly one of end_mm", PlanimetryOpsRules.Validate(s));
            s.Remove("end_mm"); s["note_ids"] = new JArray(7, 8);
            Assert.Contains("exactly one text note", PlanimetryOpsRules.Validate(s));

            var rl = Req("reseat_leaders");
            Assert.Contains("view_id or note_ids", PlanimetryOpsRules.Validate(rl));
            rl["view_id"] = 4; rl["max_shift_mm"] = 20;
            Assert.Null(PlanimetryOpsRules.Validate(rl));

            var rt = Req("replace_text"); rt["note_ids"] = new JArray(1); rt["old"] = "A"; rt["new"] = "";
            Assert.Null(PlanimetryOpsRules.Validate(rt));
            rt.Remove("new");
            Assert.Contains("new", PlanimetryOpsRules.Validate(rt));

            var sy = Req("sync_sheet_references"); sy["view_id"] = 2; sy["map"] = new JObject { ["D-01"] = "D-02" };
            Assert.Null(PlanimetryOpsRules.Validate(sy));
            sy["map"] = new JObject { ["D-01"] = 5 };
            Assert.Contains("map must be", PlanimetryOpsRules.Validate(sy));

            var rq = Req("required_notes"); rq["view_id"] = 3; rq["source"] = "material";
            rq["notes"] = new JArray(new JObject { ["element_id"] = 5, ["end_mm"] = new JArray(1, 2, 3), ["note_mm"] = new JArray(4, 5, 3) });
            Assert.Null(PlanimetryOpsRules.Validate(rq));
        }

        [Fact]
        public void view_ray_needs_points_on_the_view()
        {
            var v = new JObject { ["view_id"] = 3, ["points_mm"] = new JArray((object)new JArray(1, 2, 3)) };
            Assert.Null(NoteLeaderRules.ValidateRead("view_ray", v));
            v["points_mm"] = new JArray((object)new JArray(1, 2));
            Assert.Contains("points_mm", NoteLeaderRules.ValidateRead("view_ray", v));
        }

        [Fact]
        public void read_modes_need_their_scope()
        {
            Assert.Contains("view_ids", NoteLeaderRules.ValidateRead("leader_targets", new JObject()));
            Assert.Null(NoteLeaderRules.ValidateRead("leader_targets", new JObject { ["note_ids"] = new JArray(1) }));
            Assert.Contains("view_ids", NoteLeaderRules.ValidateRead("annotation_staleness", new JObject()));
            Assert.Null(NoteLeaderRules.ValidateRead("annotation_staleness", new JObject { ["view_ids"] = new JArray(2), ["changed_unique_ids"] = new JArray("a") }));
            Assert.Contains("UniqueId", NoteLeaderRules.ValidateRead("annotation_staleness", new JObject { ["view_ids"] = new JArray(2), ["changed_unique_ids"] = new JArray(5) }));
            Assert.Contains("sheet_id", NoteLeaderRules.ValidateRead("sheet_conflicts", new JObject()));
            var f = new JObject { ["view_id"] = 3, ["size_mm"] = new JArray(30, 10), ["near_mm"] = new JArray(100, 100) };
            Assert.Null(NoteLeaderRules.ValidateRead("free_spot", f));
            f["size_mm"] = new JArray(0, 10);
            Assert.Contains("size_mm", NoteLeaderRules.ValidateRead("free_spot", f));
        }

        [Fact]
        public void a_sheet_direction_becomes_the_view_direction_through_the_viewport_rotation()
        {
            Assert.Equal(new[] { 1.0, 0.0 }, NoteLeaderRules.SheetToView(1, 0, 0));
            // Rotated clockwise: the view's up points to the sheet's right, so sheet-right is view-up.
            double[] v = NoteLeaderRules.SheetToView(1, 0, NoteLeaderRules.QuarterTurns("Clockwise"));
            Assert.Equal(0.0, v[0], 9); Assert.Equal(1.0, v[1], 9);
            v = NoteLeaderRules.SheetToView(1, 0, NoteLeaderRules.QuarterTurns("Counterclockwise"));
            Assert.Equal(0.0, v[0], 9); Assert.Equal(-1.0, v[1], 9);
            foreach (int q in new[] { 0, 1, 2, 3 })
            {
                double[] there = NoteLeaderRules.SheetToView(3, -7, q);
                double[] back = NoteLeaderRules.ViewToSheet(there[0], there[1], q);
                Assert.Equal(3.0, back[0], 9); Assert.Equal(-7.0, back[1], 9);
            }
            Assert.Equal(0, NoteLeaderRules.QuarterTurns("None"));
        }

        [Fact]
        public void a_tag_that_reads_horizontal_on_a_turned_sheet_is_vertical_in_the_view()
        {
            Assert.Equal("horizontal", NoteLeaderRules.TagOrientationForSheet(true, 0));
            Assert.Equal("vertical", NoteLeaderRules.TagOrientationForSheet(true, 1));
            Assert.Equal("vertical", NoteLeaderRules.TagOrientationForSheet(true, 3));
            Assert.Equal("horizontal", NoteLeaderRules.TagOrientationForSheet(true, 2));
            Assert.Equal("horizontal", NoteLeaderRules.TagOrientationForSheet(false, 1));
        }

        [Fact]
        public void all_note_read_modes_accept_target_document_and_reject_foreign_fields()
        {
            foreach (string mode in NoteLeaderRules.ReadModes)
            {
                Assert.True(NoteLeaderRules.NoteReadFieldAllowed(mode, "target_document"), mode);
                Assert.False(NoteLeaderRules.NoteReadFieldAllowed(mode, "unrelated_field"), mode);
            }
        }

        [Fact]
        public void keynote_preview_distinguishes_missing_present_and_unreadable_values()
        {
            Assert.Contains("destination has no keynote", NoteLeaderRules.KeynoteWarning(true, true, ""));
            Assert.Null(NoteLeaderRules.KeynoteWarning(true, true, "A01"));
            Assert.Contains("could not be read", NoteLeaderRules.KeynoteWarning(true, false, null));
            Assert.Null(NoteLeaderRules.KeynoteWarning(false, true, ""));
        }
        [Fact]
        public void clone_tag_verifies_inherited_orientation_and_does_not_guess_empty_text_cause()
        {
            var dir = new System.IO.DirectoryInfo(System.AppContext.BaseDirectory);
            while (dir != null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "src", "Horizun.Revit", "Commands", "PlanimetryOpsCommand.Notes.cs"))) dir = dir.Parent;
            Assert.NotNull(dir);
            string code = System.IO.File.ReadAllText(System.IO.Path.Combine(dir.FullName, "src", "Horizun.Revit", "Commands", "PlanimetryOpsCommand.Notes.cs"));
            Assert.Contains("TagOrientation expectedOrientation = orientation ?? model.TagOrientation;", code);
            Assert.Contains("tag.TagOrientation == expectedOrientation", code);
            Assert.Contains("edit.Evidence[\"targets_without_keynote\"]", code);
            Assert.DoesNotContain("reports the cause", code);
        }

        [Fact]        public void clone_tag_does_not_claim_an_unmeasured_batch_regeneration()
        {
            var dir = new System.IO.DirectoryInfo(System.AppContext.BaseDirectory);
            while (dir != null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "src", "Horizun.Revit", "Commands", "PlanimetryOpsCommand.Notes.cs"))) dir = dir.Parent;
            Assert.NotNull(dir);
            string code = System.IO.File.ReadAllText(System.IO.Path.Combine(dir.FullName, "src", "Horizun.Revit", "Commands", "PlanimetryOpsCommand.Notes.cs"));
            Assert.DoesNotContain("one for the whole batch", code);
            Assert.Contains("regenerations_in_apply", code);
        }

        [Fact]
        public void tag_batch_runs_all_items_before_one_regeneration_and_reports_it()
        {
            var events = new List<string>();
            var result = NoteLeaderRules.RunTagBatch(new[] { 1, 2, 3, 4 },
                item => events.Add("tag" + item), () => events.Add("regenerate"));
            Assert.Equal(new[] { "tag1", "tag2", "tag3", "tag4", "regenerate" }, events);
            Assert.Equal(1, result.Regenerations);
        }

        [Fact]
        public void the_elbow_is_level_with_the_anchor()
        {
            double[] h = NoteLeaderRules.Elbow("horizontal", new[] { 0.0, 2.0 }, new[] { 10.0, -4.0 });
            Assert.Equal(5.0, h[0], 9); Assert.Equal(2.0, h[1], 9);
            double[] l = NoteLeaderRules.Elbow("L", new[] { 0.0, 2.0 }, new[] { 10.0, -4.0 });
            Assert.Equal(10.0, l[0], 9); Assert.Equal(2.0, l[1], 9);
        }

        [Fact]
        public void the_side_points_away_from_the_text()
        {
            Assert.Equal("left", NoteLeaderRules.ChooseSide(10, 30, 0));
            Assert.Equal("right", NoteLeaderRules.ChooseSide(10, 30, 50));
        }

        [Fact]
        public void a_note_is_shifted_inside_the_crop_by_the_smallest_move()
        {
            double[] s = NoteLeaderRules.ShiftInside(new[] { -2.0, 1.0, 4.0, 3.0 }, new[] { 0.0, 0.0, 10.0, 10.0 }, 0.5);
            Assert.Equal(2.5, s[0], 9); Assert.Equal(0.0, s[1], 9);
            s = NoteLeaderRules.ShiftInside(new[] { 1.0, 1.0, 20.0, 3.0 }, new[] { 0.0, 0.0, 10.0, 10.0 }, 0.5);
            Assert.Equal(0.0, s[0], 9); // wider than the crop: it cannot fit, so it is not moved sideways
        }

        [Fact]
        public void leaders_are_compared_within_a_tenth_of_a_millimetre()
        {
            Assert.True(NoteLeaderRules.SamePoint(new[] { 1.0, 2.0 }, new[] { 1.0, 2.05 }, NoteLeaderRules.LeaderEndToleranceMm));
            Assert.False(NoteLeaderRules.SamePoint(new[] { 1.0, 2.0 }, new[] { 1.0, 2.2 }, NoteLeaderRules.LeaderEndToleranceMm));
        }

        [Fact]
        public void move_note_does_not_replace_the_planned_coordinate_with_the_actual_result()
        {
            var dir = new System.IO.DirectoryInfo(System.AppContext.BaseDirectory);
            while (dir != null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "src", "Horizun.Revit", "Commands", "PlanimetryOpsCommand.Notes.cs"))) dir = dir.Parent;
            Assert.NotNull(dir);
            string code = System.IO.File.ReadAllText(System.IO.Path.Combine(dir.FullName, "src", "Horizun.Revit", "Commands", "PlanimetryOpsCommand.Notes.cs"));
            Assert.DoesNotContain("j.ExpectedCoord = n.Coord;\n                    if (fit != null)", code.Replace("\r\n", "\n"));
        }

        [Fact]        public void leader_distance_is_strict_and_euclidean()
        {
            Assert.False(NoteLeaderRules.SamePoint(new[] { 0.0, 0.0, 0.0 }, new[] { 0.08, 0.08, 0.0 }, 0.1));
            Assert.False(NoteLeaderRules.SamePoint(new[] { 0.0, 0.0, 0.0 }, new[] { 0.1, 0.0, 0.0 }, 0.1));
            Assert.True(NoteLeaderRules.SamePoint(new[] { 0.0, 0.0, 0.0 }, new[] { 0.06, 0.06, 0.0 }, 0.1));
        }

        [Fact]
        public void move_decision_checks_all_ends_and_rolls_back_the_batch()
        {
            foreach (int q in new[] { 0, 1, 2, 3 })
            {
                var anchor = NoteLeaderRules.SheetToView(2, 3, q);
                var end = NoteLeaderRules.SheetToView(10, -4, q);
                var elbow = NoteLeaderRules.Elbow("L", anchor, end);
                Assert.Equal(anchor[1], elbow[1]); Assert.Equal(end[0], elbow[0]);
                var accepted = NoteLeaderRules.DecideLeaderGeometry(new[] { 0.0, 0.09 }, 0.1);
                Assert.True(accepted.Accept); Assert.Equal(0.09, accepted.MaxDeviationMm, 8);
                var failed = NoteLeaderRules.DecideLeaderGeometry(new[] { 0.0, 0.12 }, 0.1);
                Assert.False(failed.Accept); Assert.Equal(0.12, failed.MaxDeviationMm, 8);
            }
        }

        [Fact]
        public void the_layer_reached_is_found_from_the_first_face()
        {
            var t = new List<double> { 20, 100, 20 };
            Assert.Equal(0, NoteLeaderRules.LayerAtDepth(t, 5));
            Assert.Equal(1, NoteLeaderRules.LayerAtDepth(t, 20.5));
            Assert.Equal(2, NoteLeaderRules.LayerAtDepth(t, 139));
            Assert.Equal(-1, NoteLeaderRules.LayerAtDepth(t, 141));
            Assert.Equal(-1, NoteLeaderRules.LayerAtDepth(t, -3));
        }

        [Fact]
        public void layer_text_lists_the_stack_and_its_signature_changes_with_it()
        {
            var layers = new List<NoteLeaderRules.Layer>
            {
                new NoteLeaderRules.Layer { Material = "Mortero", ThicknessMm = 20 },
                new NoteLeaderRules.Layer { Material = "Ladrillo", ThicknessMm = 120.5 }
            };
            string text = NoteLeaderRules.LayersText(layers, "Muro 14");
            Assert.Equal("Muro 14\r1. Mortero 20 mm\r2. Ladrillo 120.5 mm", text);
            string a = NoteLeaderRules.Signature("uid-1", "layers", text);
            Assert.Equal(a, NoteLeaderRules.Signature("uid-1", "layers", text));
            layers[0].ThicknessMm = 25;
            Assert.NotEqual(a, NoteLeaderRules.Signature("uid-1", "layers", NoteLeaderRules.LayersText(layers, "Muro 14")));
        }

        [Fact]
        public void staleness_is_stale_current_or_unknown_but_never_silently_clean()
        {
            var changed = new HashSet<string> { "w2" }; var deleted = new HashSet<string> { "w3" };
            string verdict;
            var r = NoteLeaderRules.StaleReasons("w1", true, "s", "s", "w1", true, changed, deleted, out verdict);
            Assert.Empty(r); Assert.Equal("current", verdict);
            r = NoteLeaderRules.StaleReasons("w2", true, "s", "s", "w2", true, changed, deleted, out verdict);
            Assert.Equal(new[] { "origin_changed" }, r); Assert.Equal("stale", verdict);
            r = NoteLeaderRules.StaleReasons("w3", false, "s", null, "w3", true, changed, deleted, out verdict);
            Assert.Equal(new[] { "origin_deleted" }, r);
            r = NoteLeaderRules.StaleReasons("w1", true, "old", "new", null, true, changed, deleted, out verdict);
            Assert.Equal(new[] { "text_out_of_date" }, r);
            r = NoteLeaderRules.StaleReasons(null, null, null, null, "w2", true, changed, deleted, out verdict);
            Assert.Equal(new[] { "target_changed" }, r);
            r = NoteLeaderRules.StaleReasons(null, null, null, null, null, true, changed, deleted, out verdict);
            Assert.Empty(r); Assert.Equal("unknown_no_target", verdict);
            r = NoteLeaderRules.StaleReasons(null, null, null, null, null, false, changed, deleted, out verdict);
            Assert.Equal("untracked", verdict);
        }

        [Fact]
        public void free_text_note_is_untracked_but_unmeasurable_origin_remains_unknown()
        {
            string verdict;
            var reasons = NoteLeaderRules.StaleReasons(null, null, null, null, null, false, null, null, out verdict);
            Assert.Equal("untracked", verdict);
            Assert.Equal(new[] { "nota de texto libre: sin origen ni directriz, no se puede desactualizar" }, reasons);
            Assert.True(AnnotationDependencyRules.IsClean(0, 0, true, true));
            NoteLeaderRules.StaleReasons("source-1", true, "stored", null, null, false, null, null, out verdict);
            Assert.Equal("unknown_not_measured", verdict);
            Assert.False(AnnotationDependencyRules.IsClean(0, 1, true, true));
            NoteLeaderRules.StaleReasons(null, null, null, null, null, false, null, null, out verdict, hasLeader: true);
            Assert.Equal("unknown_not_measured", verdict);
        }

        [Fact]
        public void material_signature_requires_the_same_source_and_link()
        {
            Assert.Equal("Steel", NoteLeaderRules.MaterialFromMatchingHit("u1", "l1", "u1", "l1", "Steel", "Concrete"));
            Assert.Equal("Concrete", NoteLeaderRules.MaterialFromMatchingHit("u1", null, "u1", null, null, "Concrete"));
            Assert.Null(NoteLeaderRules.MaterialFromMatchingHit("u1", "l1", "u1", "l2", "Steel", "Concrete"));
            Assert.Null(NoteLeaderRules.MaterialFromMatchingHit("u1", null, "u2", null, "Steel", "Concrete"));
            Assert.Null(NoteLeaderRules.MaterialFromMatchingHit("u1", null, null, null, null, "Concrete"));
        }

        [Fact]
        public void unreadable_origin_has_its_own_unknown_verdict()
        {
            string verdict;
            NoteLeaderRules.StaleReasons("source-1", null, "stored", null, "source-1", true, null, null, out verdict);
            Assert.Equal("unknown_origin_not_measured", verdict);
        }
        [Fact]        public void unreadable_origin_signature_is_not_current()
        {
            string verdict;
            NoteLeaderRules.StaleReasons("source-1", true, "stored", null, "source-1", true, null, null, out verdict);
            Assert.Equal("unknown_not_measured", verdict);
        }

        [Fact]
        public void source_cycle_distinguishes_value_layer_deletion_and_missing_measurement()
        {
            Assert.Equal("vigente", NoteLeaderRules.CompareNoteSource("u1", "layers", "brick", true, "u1", "layers", "brick"));
            Assert.Equal("cambio_valor", NoteLeaderRules.CompareNoteSource("u1", "material", "brick", true, "u1", "material", "steel"));
            Assert.Equal("cambio_capa", NoteLeaderRules.CompareNoteSource("u1", "layers", "brick", true, "u1", "material", "brick"));
            Assert.Equal("origen_borrado", NoteLeaderRules.CompareNoteSource("u1", "layers", "brick", false, null, null, null));
            Assert.Equal("sin_medir", NoteLeaderRules.CompareNoteSource("u1", "material", "brick", true, "u2", "material", "brick"));
            Assert.Equal("sin_medir", NoteLeaderRules.CompareNoteSource("u1", "material", "brick", null, null, null, null));
            Assert.Equal("sin_medir", NoteLeaderRules.CompareNoteSource("u1", "material", "brick", true, "u1", "material", null));
        }

        [Fact]
        public void conflicts_are_the_pairs_whose_boxes_touch_within_the_gap()
        {
            var boxes = new List<double[]> { new[] { 0.0, 0, 10, 10 }, new[] { 9.0, 9, 20, 20 }, new[] { 30.0, 30, 40, 40 }, new[] { 41.0, 30, 50, 40 } };
            Assert.Equal(new[] { "0-1" }, NoteLeaderRules.Conflicts(boxes, 0).Select(p => p[0] + "-" + p[1]).ToArray());
            Assert.Equal(new[] { "0-1", "2-3" }, NoteLeaderRules.Conflicts(boxes, 2).Select(p => p[0] + "-" + p[1]).ToArray());
        }

        [Fact]
        public void a_segment_crossing_a_box_conflicts_even_when_its_own_box_would_not()
        {
            var box = new[] { 4.0, 4, 6, 6 };
            Assert.True(NoteLeaderRules.SegmentHitsBox(new[] { 0.0, 0 }, new[] { 10.0, 10 }, box, 0));
            Assert.False(NoteLeaderRules.SegmentHitsBox(new[] { 0.0, 10 }, new[] { 3.0, 10 }, box, 0));
            Assert.False(NoteLeaderRules.SegmentHitsBox(new[] { 0.0, 0 }, new[] { 10.0, 0 }, box, 1));
            Assert.True(NoteLeaderRules.SegmentHitsBox(new[] { 0.0, 3 }, new[] { 10.0, 3 }, box, 1.5));
        }

        [Fact]
        public void the_free_spot_is_the_nearest_place_that_fits_inside_the_bounds()
        {
            var obstacles = new List<double[]> { new[] { 40.0, 40, 60, 60 } };
            double[] bounds = { 0, 0, 100, 100 };
            Assert.Equal(new[] { 10.0, 10.0 }, NoteLeaderRules.FreeSpot(obstacles, 10, 10, new[] { 10.0, 10.0 }, bounds, 0, 1, 50));
            double[] spot = NoteLeaderRules.FreeSpot(obstacles, 10, 10, new[] { 50.0, 50.0 }, bounds, 0, 1, 60);
            Assert.NotNull(spot);
            Assert.False(NoteLeaderRules.Overlap(new[] { spot[0] - 5, spot[1] - 5, spot[0] + 5, spot[1] + 5 }, obstacles[0], 0));
            double d = System.Math.Sqrt(System.Math.Pow(spot[0] - 50, 2) + System.Math.Pow(spot[1] - 50, 2));
            Assert.InRange(d, 14.9, 17.0);
            Assert.Null(NoteLeaderRules.FreeSpot(new List<double[]> { new[] { 0.0, 0, 100, 100 } }, 10, 10, new[] { 50.0, 50.0 }, bounds, 0, 5, 80));
            double[] inside = NoteLeaderRules.FreeSpot(new List<double[]>(), 10, 10, new[] { 2.0, 2.0 }, bounds, 0, 1, 20);
            Assert.True(inside[0] >= 5 && inside[1] >= 5);
        }
    }
}
