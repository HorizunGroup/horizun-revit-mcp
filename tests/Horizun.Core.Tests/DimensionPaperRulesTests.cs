using System.Collections.Generic;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    // Field feedback 2026-11-17 (Revit 2024, workshared, links): each test names the
    // defect it pins. The old behaviour had no rule at all for these cases.
    public class DimensionPaperRulesTests
    {
        // ---- 1. NewDimension drops a linked grid in silence ------------------------

        [Fact]
        public void A_linked_grid_dropped_by_NewDimension_is_named_by_index()
        {
            var requested = new[] { "w1:3:SURFACE", "lnk:0:RVTLINK/grid9", "w2:3:SURFACE" };
            var types = new[] { "REFERENCE_TYPE_SURFACE", "REFERENCE_TYPE_LINEAR", "REFERENCE_TYPE_SURFACE" };
            // What Revit 2024 committed: the two faces, the linked grid gone, no error.
            var read = new[] { "w1:3:SURFACE", "w2:3:SURFACE" };
            var readTypes = new[] { "REFERENCE_TYPE_SURFACE", "REFERENCE_TYPE_SURFACE" };
            List<int> dropped = DimensionLinkRules.Dropped(requested, types, read, readTypes);
            Assert.Equal(new[] { 1 }, dropped);
            string message = DimensionLinkRules.DroppedMessage(dropped, requested);
            Assert.Contains("references[1] (lnk:0:RVTLINK/grid9)", message);
            Assert.Contains("refused", message);
        }

        [Fact]
        public void As_many_references_read_back_as_requested_is_never_a_drop()
        {
            var requested = new[] { "w1:3:SURFACE", "lnk:0:RVTLINK/lw:2788:6:SURFACE", "lnk:0:RVTLINK/lw:2788:5:SURFACE" };
            var types = new[] { "REFERENCE_TYPE_SURFACE", "REFERENCE_TYPE_SURFACE", "REFERENCE_TYPE_SURFACE" };
            // The linked faces read back spelled another way: same count, so nothing was dropped.
            var read = new[] { "w1:3:SURFACE", "lnk:0:RVTLINK:2788:6:SURFACE", "lnk:0:RVTLINK:2788:5:SURFACE" };
            Assert.Empty(DimensionLinkRules.Dropped(requested, types, read, types));
            // One fewer still names the mismatches.
            Assert.NotEmpty(DimensionLinkRules.Dropped(requested, types, new[] { read[0], read[1] }, new[] { types[0], types[1] }));
        }

        [Fact]
        public void Canonicalised_bare_references_are_not_reported_as_dropped()
        {
            var requested = new[] { "g1", "g2" };
            var types = new[] { "REFERENCE_TYPE_NONE", "REFERENCE_TYPE_NONE" };
            var read = new[] { "g1:0:SURFACE", "g2:0:SURFACE" };
            var readTypes = new[] { "REFERENCE_TYPE_SURFACE", "REFERENCE_TYPE_SURFACE" };
            Assert.Empty(DimensionLinkRules.Dropped(requested, types, read, readTypes));
            Assert.Null(DimensionLinkRules.DroppedMessage(new List<int>(), requested));
        }

        [Fact]
        public void Symbol_geometry_references_are_wrapped_as_instance_references()
        {
            // Before: the symbol reference travelled as-is and pointed at the family TYPE.
            Assert.Equal("inst-1:0:INSTANCE:sym-9:12:SURFACE",
                         DimensionLinkRules.InstanceWrapped("inst-1", "sym-9:12:SURFACE"));
            Assert.Null(DimensionLinkRules.InstanceWrapped("inst-1", "inst-1:0:INSTANCE:sym-9:12:SURFACE"));
            Assert.Null(DimensionLinkRules.InstanceWrapped("inst-1", "inst-1:7:SURFACE"));
            Assert.Null(DimensionLinkRules.InstanceWrapped("", "sym-9:12:SURFACE"));
        }

        // ---- 2. Level planes ------------------------------------------------------

        [Theory]
        [InlineData("level:311", true, 311)]
        [InlineData("level:0", false, 0)]
        [InlineData("level:", false, 0)]
        [InlineData("level:12a", false, 0)]
        [InlineData("abc-uid:0:SURFACE", false, 0)]
        public void Level_tokens_name_a_host_level(string text, bool ok, long id)
        {
            long got;
            Assert.Equal(ok, DimensionLinkRules.TryParseLevelToken(text, out got));
            if (ok) Assert.Equal(id, got);
        }

        // ---- 3. Pattern (tile) joints -----------------------------------------------

        [Theory]
        [InlineData("f-uid:4:SURFACE/12", true, "f-uid:4:SURFACE", 12)]
        [InlineData("lnk:0:RVTLINK/f-uid:4:SURFACE/3", true, "lnk:0:RVTLINK/f-uid:4:SURFACE", 3)]
        [InlineData("i:0:INSTANCE:s:2:SURFACE/0", true, "i:0:INSTANCE:s:2:SURFACE", 0)]
        [InlineData("lnk:0:RVTLINK/f-uid:4:SURFACE", false, null, -1)]
        [InlineData("f-uid:4:SURFACE/", false, null, -1)]
        [InlineData("f-uid:4:LINEAR/2", false, null, -1)]
        public void Pattern_references_parse_face_and_index(string stable, bool ok, string face, int k)
        {
            string f; int got;
            Assert.Equal(ok, DimensionPatternRules.TryParse(stable, out f, out got));
            Assert.Equal(face, f);
            Assert.Equal(k, got);
        }

        [Fact]
        public void Nearest_joint_is_the_minimum_and_a_tie_is_refused()
        {
            string error;
            var measured = new List<DimensionPatternRules.JointCandidate> { J(4, 0.9, 0.9), J(5, 0.1, 0.1), J(6, 0.6, -0.6) };
            Assert.Equal(5, DimensionPatternRules.Nearest(measured, 1e-4, out error).K);
            Assert.Null(error);

            var tie = new List<DimensionPatternRules.JointCandidate> { J(2, 0.5, -0.5), J(3, 0.5, 0.5) };
            Assert.Null(DimensionPatternRules.Nearest(tie, 1e-4, out error));
            Assert.Contains("equidistant", error);

            Assert.Null(DimensionPatternRules.Nearest(new List<DimensionPatternRules.JointCandidate>(), 1e-4, out error));
            Assert.Contains("no pattern line", error);
        }

        [Fact]
        public void Nearest_request_token_is_recognised()
        {
            string face;
            Assert.True(DimensionPatternRules.IsNearestRequest("f:4:SURFACE/?", out face));
            Assert.Equal("f:4:SURFACE", face);
            Assert.False(DimensionPatternRules.IsNearestRequest("f:4:SURFACE/3", out face));
        }

        [Fact]
        public void A_joint_segment_outside_the_tiled_face_fails()
        {
            // Face spans 0..10 ft along the dimension; a wall face at 0, a joint at 12.
            List<double> boundaries = DimensionPatternRules.BoundaryScalars(new[] { 6.0 }, new[] { 12.0 }, 1e-4);
            Assert.Equal(new[] { 0.0, 12.0 }, boundaries);
            var v = DimensionPatternRules.CheckBoundaries(boundaries, new double?[] { 0.0, null }, 1, 0.0, 10.0, 1e-3);
            Assert.Equal("failed", v.Status);
            Assert.Equal(new[] { 12.0 }, v.Outside);

            var inside = DimensionPatternRules.CheckBoundaries(
                DimensionPatternRules.BoundaryScalars(new[] { 3.0 }, new[] { 6.0 }, 1e-4),
                new double?[] { 0.0, null }, 1, 0.0, 10.0, 1e-3);
            Assert.Equal("verified", inside.Status);
        }

        [Fact]
        public void Chain_boundaries_collapse_and_unexplained_counts_are_never_a_pass()
        {
            List<double> b = DimensionPatternRules.BoundaryScalars(new[] { 1.0, 3.0 }, new[] { 2.0, 2.0 }, 1e-4);
            Assert.Equal(new[] { 0.0, 2.0, 4.0 }, b);
            var v = DimensionPatternRules.CheckBoundaries(b, new double?[] { null, null, null }, 1, 0.0, 10.0, 1e-3);
            Assert.Equal("unavailable", v.Status);
        }

        [Fact]
        public void Spot_slope_refusal_offers_an_alternative()
        {
            // Before: "Place spot slopes by hand in Revit." and nothing else.
            string text = DimensionPlanRules.NoApiAnyYear("spot_slope");
            Assert.Contains("two spot_elevation", text);
            Assert.Contains("horizun_query_dimensions", text);
        }

        private static DimensionPatternRules.JointCandidate J(int k, double distance, double? position)
            => new DimensionPatternRules.JointCandidate { K = k, Distance = distance, Position = position };

        // Review 606ecc92 B2: the old rule dropped distance 0 (`v <= 0`) before choosing,
        // so a point placed ON the joint picked the next joint instead.
        [Fact]
        public void A_point_on_the_joint_is_an_exact_hit_not_skipped()
        {
            string error;
            var c = new List<DimensionPatternRules.JointCandidate> { J(0, 0.0, 0.0), J(1, 0.3, 0.3), J(2, 0.3, -0.3) };
            var best = DimensionPatternRules.Nearest(c, 1e-4, out error);
            Assert.Null(error);
            Assert.Equal(0, best.K);
            Assert.True(best.Exact);
        }

        // B2: the probe plane sits outside the face, so the joint under the point is a
        // normal-length probe; its position comes from the midpoint, its distance from it.
        [Fact]
        public void A_probe_gives_the_joint_position_and_its_distance_to_the_point()
        {
            // plane at -1.0, joint at 0.5, point at 0.5: probe value 1.5, midpoint -0.25.
            var c = DimensionPatternRules.FromProbe(4, -1.0, 1.5, -0.25, 0.5);
            Assert.Equal(0.5, c.Position.Value, 9);
            Assert.Equal(0.0, c.Distance, 9);
            Assert.Null(DimensionPatternRules.FromProbe(4, -1.0, null, -0.25, 0.5));
            Assert.Null(DimensionPatternRules.FromProbe(4, -1.0, 1.5, null, 0.5));
            // Value and midpoint disagree: the probe did not measure the joint.
            Assert.Null(DimensionPatternRules.FromProbe(4, -1.0, 0.7, -0.25, 0.5));
        }

        // B2: two indices on the same physical joint were reported as "equidistant".
        [Fact]
        public void Two_indices_on_one_joint_are_aliases_not_ambiguous()
        {
            string error;
            var c = new List<DimensionPatternRules.JointCandidate> { J(7, 0.2, 1.2), J(3, 0.2, 1.2), J(9, 0.5, 1.5) };
            var best = DimensionPatternRules.Nearest(c, 1e-4, out error);
            Assert.Null(error);
            Assert.Equal(3, best.K);
            Assert.Equal(new[] { 7 }, best.Aliases);
            Assert.False(best.Exact);

            // Without a readable position nothing is merged: still a tie.
            var unknownPos = new List<DimensionPatternRules.JointCandidate> { J(7, 0.2, null), J(3, 0.2, 1.2) };
            Assert.Null(DimensionPatternRules.Nearest(unknownPos, 1e-4, out error));
        }

        // B2: nothing may assume a uniform module (running bond, mixed tile sizes).
        [Fact]
        public void Non_uniform_joints_pick_the_measured_nearest()
        {
            string error;
            double point = 0.6;
            double[] joints = { 0.0, 0.2, 0.9, 1.0, 1.45 };
            var c = new List<DimensionPatternRules.JointCandidate>();
            for (int k = 0; k < joints.Length; k++) c.Add(J(k, System.Math.Abs(joints[k] - point), joints[k]));
            Assert.Equal(2, DimensionPatternRules.Nearest(c, 1e-4, out error).K);
        }

        [Fact]
        public void A_chosen_joint_must_be_a_segment_end_after_the_commit()
        {
            var boundaries = new List<double> { 0.0, 0.9, 2.0 };
            Assert.Empty(DimensionPatternRules.JointsMissingFromBoundaries(boundaries, new[] { 0.9 }, 1e-3));
            Assert.Equal(new[] { 0 }, DimensionPatternRules.JointsMissingFromBoundaries(boundaries, new[] { 1.2 }, 1e-3));
        }

        // ---- 4. Will it print ----------------------------------------------------------

        [Fact]
        public void A_dimension_to_a_hidden_link_will_not_print()
        {
            var v = DimensionPrintRules.Evaluate(null, new[] { DimensionPrintRules.ReasonLinkHidden + ": link 55" }, null);
            Assert.Equal(false, v.WillPrint);
            Assert.Contains("NOT PRINT", DimensionPrintRules.RequirePrintableError(3, v, false));

            var noBox = DimensionPrintRules.Evaluate(false, new string[0], null);
            Assert.Equal(false, noBox.WillPrint);
            Assert.Contains(DimensionPrintRules.ReasonNoBox, noBox.Reasons[0]);
        }

        [Fact]
        public void Unread_facts_never_read_as_printable()
        {
            Assert.Null(DimensionPrintRules.Evaluate(null, null, null).WillPrint);
            Assert.Null(DimensionPrintRules.Evaluate(true, null, new[] { "filters not read" }).WillPrint);
            Assert.Equal(true, DimensionPrintRules.Evaluate(true, null, null).WillPrint);
            Assert.Null(DimensionPrintRules.RequirePrintableError(0, DimensionPrintRules.Evaluate(null, null, null), false));
        }

        // Review 606ecc92 B3: a dimension outside the crop, hidden itself, in a hidden
        // category or under a template that hides it read will_print=true.
        [Fact]
        public void Dimension_level_facts_decide_will_not_print()
        {
            var hidden = new List<string>(); var unknown = new List<string>();
            DimensionPrintRules.DimensionFacts(false, false, false, false, 0, false, null, hidden, unknown);
            Assert.Equal(false, DimensionPrintRules.Evaluate(true, hidden, unknown).WillPrint);
            Assert.Contains(DimensionPrintRules.ReasonOutsideCrop, hidden[0]);

            foreach (var facts in new[] { new bool?[] { true, true, false, false }, new bool?[] { true, false, true, false },
                                          new bool?[] { true, false, false, true } })
            {
                hidden.Clear(); unknown.Clear();
                DimensionPrintRules.DimensionFacts(facts[0], facts[1], facts[2], facts[3], 0, false, null, hidden, unknown);
                Assert.Equal(false, DimensionPrintRules.Evaluate(true, hidden, unknown).WillPrint);
            }

            hidden.Clear(); unknown.Clear();
            DimensionPrintRules.DimensionFacts(true, false, false, false, 0, true, false, hidden, unknown);
            Assert.Contains(DimensionPrintRules.ReasonWorksetHidden, hidden[0]);
        }

        // B3: what is not evaluated (view filters, an unread fact) makes the verdict null, never true.
        [Fact]
        public void Unevaluated_filters_make_the_verdict_null_and_require_printable_refuses_it()
        {
            var hidden = new List<string>(); var unknown = new List<string>();
            DimensionPrintRules.DimensionFacts(true, false, false, false, 2, false, null, hidden, unknown);
            var v = DimensionPrintRules.Evaluate(true, hidden, unknown);
            Assert.Null(v.WillPrint);
            Assert.Contains("FILTERS cannot be evaluated", DimensionPrintRules.RequirePrintableError(0, v, true));
            Assert.Contains("FILTERS cannot be evaluated", DimensionPrintRules.RequirePrintableError(0, v, false));

            hidden.Clear(); unknown.Clear();
            DimensionPrintRules.DimensionFacts(true, false, false, false, 0, false, null, hidden, unknown);
            var clean = DimensionPrintRules.Evaluate(true, hidden, unknown);
            Assert.Equal(true, clean.WillPrint);
            Assert.Null(DimensionPrintRules.RequirePrintableError(0, clean, true));
            // Committed with no box read: null, refused under require_printable.
            Assert.NotNull(DimensionPrintRules.RequirePrintableError(0, DimensionPrintRules.Evaluate(null, hidden, unknown), true));
        }

        // Review 3ac117fb (1): a template that does not control V/G Annotation was read as
        // hiding the category, giving a false will_print:false.
        [Fact]
        public void A_template_counts_only_when_it_controls_annotation_visibility()
        {
            Assert.Equal(false, DimensionPrintRules.TemplateHidesCategory(true, false, true));
            Assert.Equal(true, DimensionPrintRules.TemplateHidesCategory(true, true, true));
            Assert.Equal(false, DimensionPrintRules.TemplateHidesCategory(false, null, null));
            Assert.Null(DimensionPrintRules.TemplateHidesCategory(true, null, true));
        }

        // Review 3ac117fb (4): with view filters the refusal must say WHY - filters cannot be evaluated.
        [Fact]
        public void Require_printable_with_view_filters_says_filters_cannot_be_evaluated()
        {
            var hidden = new List<string>(); var unknown = new List<string>();
            DimensionPrintRules.DimensionFacts(true, false, false, false, 1, false, null, hidden, unknown);
            var v = DimensionPrintRules.Evaluate(true, hidden, unknown);
            string e = DimensionPrintRules.RequirePrintableError(0, v, true);
            Assert.Contains("FILTERS cannot be evaluated", e);
            Assert.Contains("refused because", e);
        }

        // Review 3ac117fb (3): the joint check read the face extent first and stayed
        // "unavailable" on family / linked faces although the segment ends were readable.
        [Fact]
        public void Joint_check_needs_only_the_segment_ends()
        {
            List<int> missing;
            Assert.Equal("verified", DimensionPatternRules.JointCheck(new List<double> { 0.0, 0.9 }, new[] { 0.9 }, 1e-3, out missing));
            Assert.Equal("failed", DimensionPatternRules.JointCheck(new List<double> { 0.0, 0.9 }, new[] { 0.5 }, 1e-3, out missing));
            Assert.Equal(new[] { 0 }, missing);
            Assert.Equal("unavailable", DimensionPatternRules.JointCheck(new List<double>(), new[] { 0.5 }, 1e-3, out missing));
        }

        // Review 3ac117fb (2): Face.Project returning null was refused as "off the face".
        [Fact]
        public void No_projection_is_unknown_not_off_the_face()
        {
            Assert.Null(SpotElevationRules.InsideFromProjection(false, 0, 1e-3));
            Assert.Null(SpotElevationRules.EdgeError(0, SpotElevationRules.InsideFromProjection(false, 0, 1e-3), null));
            Assert.Equal(true, SpotElevationRules.InsideFromProjection(true, 0.0001, 1e-3));
            Assert.Equal(false, SpotElevationRules.InsideFromProjection(true, 0.5, 1e-3));
        }

        // Review 606ecc92 B1: the RCP band was taken as view depth..top (looking down), so a
        // ceiling at +2.70 with the view depth at +3.50 was refused as "outside the view range".
        [Fact]
        public void Ceiling_plan_band_runs_from_cut_plane_to_view_depth()
        {
            double ceiling = 2.70 / 0.3048, cut = 2.30 / 0.3048, depth = 3.50 / 0.3048;
            // The old reading refused it.
            Assert.NotNull(SpotElevationRules.DepthError(0, "plan", ceiling, depth, depth));

            bool? up = SpotElevationRules.LooksUp(true, -1.0);
            Assert.Equal(true, up);
            double? min, max;
            Assert.True(SpotElevationRules.PlanBand(up, cut, depth, depth, out min, out max));
            Assert.Null(SpotElevationRules.DepthError(0, "plan", ceiling, min, max));
            Assert.NotNull(SpotElevationRules.DepthError(0, "plan", 3.80 / 0.3048, min, max));

            // Type and direction disagree, or the planes contradict the direction: no pre-check.
            Assert.Null(SpotElevationRules.LooksUp(true, 1.0));
            Assert.Null(SpotElevationRules.LooksUp(false, -1.0));
            Assert.False(SpotElevationRules.PlanBand(true, depth, null, cut, out min, out max));
            Assert.False(SpotElevationRules.PlanBand(null, cut, depth, depth, out min, out max));

            // Looking down is unchanged: view depth .. top.
            Assert.True(SpotElevationRules.PlanBand(false, 1.2, 2.3, -0.2, out min, out max));
            Assert.Equal(-0.2, min); Assert.Equal(2.3, max);
        }

        // ---- 5. Printed text ------------------------------------------------------------

        [Fact]
        public void The_type_format_decides_the_printed_text_when_the_type_has_its_own()
        {
            // Before: 'presented' was Dimension.ValueString, i.e. always the PROJECT format.
            Assert.Equal(DimensionPrintRules.SourceDimensionType, DimensionPrintRules.FormatSource(false));
            Assert.Equal(DimensionPrintRules.SourceProject, DimensionPrintRules.FormatSource(true));
            Assert.Equal(DimensionPrintRules.SourceProject, DimensionPrintRules.FormatSource(null));
        }

        [Fact]
        public void Override_replaces_the_value_and_keeps_prefix_and_suffix()
        {
            Assert.Equal("L=1200 mm", DimensionPrintRules.Compose("L=", "1200", " mm", null));
            Assert.Equal("L=VAR mm", DimensionPrintRules.Compose("L=", "1200", " mm", "VAR"));
            Assert.Equal("1200", DimensionPrintRules.Compose(null, "1200", null, ""));
            Assert.Null(DimensionPrintRules.Compose("a", null, "b", null));
        }

        // ---- 6. Spot elevation ------------------------------------------------------------

        [Fact]
        public void A_spot_on_the_face_edge_is_refused_with_the_fix()
        {
            string e = SpotElevationRules.EdgeError(2, true, 0.0);
            Assert.Contains("EDGE", e);
            Assert.Contains("1 mm inside", e);
            Assert.Null(SpotElevationRules.EdgeError(2, true, 0.1));
            Assert.Null(SpotElevationRules.EdgeError(2, null, null));
            Assert.Contains("INSIDE", SpotElevationRules.EdgeError(2, false, null));
        }

        [Fact]
        public void A_spot_outside_the_visible_depth_is_refused_with_the_fix()
        {
            string e = SpotElevationRules.DepthError(0, "section", 5.0, 0.0, 3.0);
            Assert.Contains("far clip", e);
            Assert.Null(SpotElevationRules.DepthError(0, "section", 2.0, 0.0, 3.0));
            Assert.Contains("view range", SpotElevationRules.DepthError(0, "plan", -2.0, -1.0, 4.0));
            Assert.Null(SpotElevationRules.DepthError(0, "plan", -2.0, null, null));
        }

        // ---- live 2025 (Snowdon): a committed spot came back with no text at all ----

        [Theory]
        [InlineData("Project Base Point", "project_base_point")]
        [InlineData("Punto base del proyecto", "project_base_point")]
        [InlineData("Survey Point", "survey_point")]
        [InlineData("Punto de reconocimiento", "survey_point")]
        [InlineData("Origen interno", "internal_origin")]
        [InlineData("Punto topográfico", "survey_point")]
        [InlineData("Shared", "survey_point")]
        [InlineData("Relative", "relative")]
        [InlineData("Internal Origin", "internal_origin")]
        [InlineData("", null)]
        [InlineData("Something else", null)]
        public void Spot_base_is_classified_from_its_name_or_left_unknown(string name, string expected)
        {
            Assert.Equal(expected, SpotBaseRules.Classify(name));
        }
    }
}
