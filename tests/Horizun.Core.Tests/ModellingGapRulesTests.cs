// Six small modelling gaps, without Revit: a footprint roof with holes and per-edge extras, a sloped floor,
// a named reference plane, a column joined to what it carries, a toposolid cut by an element and a rebar cover
// type. These are the rules each command checks BEFORE it opens a transaction.
using System;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class RoofFootprintRulesTests
    {
        [Fact]
        public void Edges_are_counted_across_every_contour_perimeter_first()
        {
            Assert.Equal(8, RoofFootprintRules.EdgeCount(new[] { 4, 4 }));
            Assert.Equal(7, RoofFootprintRules.EdgeCount(new[] { 4, 3 }));
        }

        [Fact]
        public void A_uniform_slope_tilts_the_perimeter_only_never_the_holes()
        {
            bool[] defines = RoofFootprintRules.UniformDefinesSlope(new[] { 4, 4 }, 0.5);
            Assert.Equal(new[] { true, true, true, true, false, false, false, false }, defines);
        }

        [Fact]
        public void A_flat_roof_defines_no_slope_anywhere()
        {
            Assert.All(RoofFootprintRules.UniformDefinesSlope(new[] { 4, 4 }, 0), Assert.False);
        }

        [Fact]
        public void The_projected_area_is_the_perimeter_minus_every_hole()
        {
            // 10 x 10 perimeter, two 2 x 2 holes (drawn the other way round: unsigned).
            Assert.Equal(92.0, RoofFootprintRules.NetFootprintArea(new[] { 100.0, -4.0, 4.0 }), 9);
            Assert.Equal(0.0, RoofFootprintRules.NetFootprintArea(new double[0]));
        }

        [Fact]
        public void An_overhang_switches_the_projected_area_check_off_and_zero_keeps_it()
        {
            Assert.True(RoofFootprintRules.ProjectedAreaCheckApplies(new double[0]));
            Assert.True(RoofFootprintRules.ProjectedAreaCheckApplies(new[] { 0.0, 0.0 }));
            Assert.False(RoofFootprintRules.ProjectedAreaCheckApplies(new[] { 0.0, 1.0 / 304.8 * 600 }));
        }

        [Fact]
        public void A_per_edge_number_must_be_finite()
        {
            Assert.Null(RoofFootprintRules.EdgeNumberError("overhang", null, 0));
            Assert.Null(RoofFootprintRules.EdgeNumberError("overhang", 2.0, 0));
            Assert.Contains("edge_slopes[3].offset", RoofFootprintRules.EdgeNumberError("offset", double.NaN, 3));
            Assert.NotNull(RoofFootprintRules.EdgeNumberError("overhang", double.PositiveInfinity, 1));
        }
    }

    public class ColumnAttachRulesTests
    {
        private static JObject Op(string json) => JObject.Parse(json);

        [Theory]
        [InlineData("base", 0)]
        [InlineData("TOP", 1)]
        [InlineData(" top ", 1)]
        [InlineData("middle", -1)]
        [InlineData(null, -1)]
        public void The_column_end_is_revits_base_or_top_index(string end, int index)
        {
            Assert.Equal(index, ColumnAttachRules.EndIndex(end));
        }

        [Fact]
        public void A_well_formed_attach_and_detach_pass()
        {
            Assert.Null(ColumnAttachRules.Validate(Op("{\"operation\":\"column_attach\",\"element_ids\":[10,11],\"column_end\":\"top\",\"attachment_target_id\":99}")));
            Assert.Null(ColumnAttachRules.Validate(Op("{\"operation\":\"column_detach\",\"element_ids\":[10],\"column_end\":\"base\"}")));
        }

        [Fact]
        public void Attach_needs_its_target_and_detach_refuses_one()
        {
            Assert.Contains("attachment_target_id",
                ColumnAttachRules.Validate(Op("{\"operation\":\"column_attach\",\"element_ids\":[10],\"column_end\":\"top\"}")));
            Assert.Contains("attachment_target_id",
                ColumnAttachRules.Validate(Op("{\"operation\":\"column_detach\",\"element_ids\":[10],\"column_end\":\"top\",\"attachment_target_id\":5}")));
        }

        [Fact]
        public void The_end_is_required_and_never_defaulted()
        {
            Assert.Contains("column_end",
                ColumnAttachRules.Validate(Op("{\"operation\":\"column_attach\",\"element_ids\":[10],\"attachment_target_id\":5}")));
        }

        [Fact]
        public void The_same_column_twice_and_empty_lists_are_refused()
        {
            Assert.Contains("twice", ColumnAttachRules.Validate(Op("{\"operation\":\"column_detach\",\"element_ids\":[10,10],\"column_end\":\"top\"}")));
            Assert.NotNull(ColumnAttachRules.Validate(Op("{\"operation\":\"column_detach\",\"element_ids\":[],\"column_end\":\"top\"}")));
            Assert.NotNull(ColumnAttachRules.Validate(Op("{\"operation\":\"column_detach\",\"element_ids\":[\"a\"],\"column_end\":\"top\"}")));
        }

        [Fact]
        public void Fields_of_other_operations_are_refused_by_name()
        {
            string why = ColumnAttachRules.Validate(Op("{\"operation\":\"column_detach\",\"element_ids\":[1],\"column_end\":\"top\",\"vector\":[0,0,1]}"));
            Assert.Contains("vector", why);
        }

        [Fact]
        public void Column_operations_cannot_share_a_call_with_the_rest()
        {
            Assert.True(ColumnAttachRules.BatchIsPure(new[] { "column_attach", "COLUMN_DETACH" }));
            Assert.False(ColumnAttachRules.BatchIsPure(new[] { "column_attach", "move" }));
            Assert.True(ColumnAttachRules.IsColumnOperation("column_attach"));
            Assert.False(ColumnAttachRules.IsColumnOperation("wall_attach_top"));
            Assert.False(ColumnAttachRules.IsColumnOperation(null));
        }
    }

    public class ReferencePlaneRulesTests
    {
        private static readonly double[] A = { 0, 0, 0 };
        private static readonly double[] B = { 10, 0, 0 };

        [Fact]
        public void A_named_plane_with_two_distinct_ends_is_valid()
        {
            Assert.Null(ReferencePlaneRules.Validate("Eje 3 provisional", A, B, "FloorPlan"));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void The_name_is_required(string name)
        {
            Assert.Contains("name", ReferencePlaneRules.Validate(name, A, B, "FloorPlan"));
        }

        [Fact]
        public void Coincident_or_non_finite_ends_are_refused()
        {
            Assert.Contains("same point", ReferencePlaneRules.Validate("n", A, new[] { 0.0, 0.0, 0.001 / 304.8 }, "FloorPlan"));
            Assert.Contains("finite", ReferencePlaneRules.Validate("n", A, new[] { double.NaN, 0, 0 }, "FloorPlan"));
            Assert.NotNull(ReferencePlaneRules.Validate("n", new[] { 0.0, 0.0 }, B, "FloorPlan"));
        }

        [Fact]
        public void The_cut_vector_is_the_views_direction_normalised()
        {
            double[] cut = ReferencePlaneRules.CutVector(new[] { 0.0, 0.0, -3.0 });
            Assert.Equal(new[] { 0.0, 0.0, -1.0 }, cut);
            Assert.Null(ReferencePlaneRules.CutVector(new[] { 0.0, 0.0, 0.0 }));
            Assert.Null(ReferencePlaneRules.CutVector(null));
        }

        [Fact]
        public void A_line_that_runs_along_the_view_direction_is_a_point_in_that_view()
        {
            double[] up = { 0, 0, 1 };
            Assert.True(ReferencePlaneRules.LineAlongCutVector(A, new[] { 0.0, 0.0, 5.0 }, up));
            Assert.True(ReferencePlaneRules.LineAlongCutVector(A, new[] { 0.0, 0.0, -5.0 }, up));
            Assert.False(ReferencePlaneRules.LineAlongCutVector(A, B, up));
        }
    }

    public class ToposolidExcavationRulesTests
    {
        [Theory]
        [InlineData(2023, false)]
        [InlineData(2024, false)]
        [InlineData(2025, true)]
        [InlineData(2026, true)]
        [InlineData(2027, true)]
        public void The_API_exists_from_revit_2025(int year, bool supported)
        {
            Assert.Equal(supported, ToposolidExcavationRules.Supported(year));
        }

        [Fact]
        public void The_refusal_says_why_and_that_nothing_was_written()
        {
            string old = ToposolidExcavationRules.UnsupportedMessage(2023);
            Assert.Contains("2025", old);
            Assert.Contains("no Toposolid at all", old);
            Assert.Contains("Nothing was written", old);
            Assert.DoesNotContain("no Toposolid at all", ToposolidExcavationRules.UnsupportedMessage(2024));
        }

        [Fact]
        public void The_validator_takes_pairs_for_both_operations()
        {
            Assert.Null(JoinGeometryRules.Validate(JObject.Parse("{\"operation\":\"excavate\",\"target_document\":\"M\",\"pairs\":[[10,20]]}")));
            Assert.Null(JoinGeometryRules.Validate(JObject.Parse("{\"operation\":\"unexcavate\",\"target_document\":\"M\",\"pairs\":[[10,20],[10,21]]}")));
        }

        [Fact]
        public void The_validator_demands_a_target_document_and_refuses_element_ids()
        {
            Assert.Contains("target_document", JoinGeometryRules.Validate(JObject.Parse("{\"operation\":\"excavate\",\"pairs\":[[10,20]]}")));
            Assert.Contains("pairs", JoinGeometryRules.Validate(JObject.Parse("{\"operation\":\"excavate\",\"target_document\":\"M\",\"element_ids\":[10,20]}")));
        }

        [Theory]
        [InlineData("[]")]
        [InlineData("[[10]]")]
        [InlineData("[[10,10]]")]
        [InlineData("[[10,\"x\"]]")]
        [InlineData("[[0,5]]")]
        [InlineData("[[10,20],[10,20]]")]
        public void Malformed_pairs_are_refused_before_anything_runs(string pairs)
        {
            Assert.NotNull(ToposolidExcavationRules.PairsError(JToken.Parse(pairs), null));
        }

        [Fact]
        public void Join_options_do_not_leak_into_excavation()
        {
            string why = JoinGeometryRules.Validate(JObject.Parse("{\"operation\":\"excavate\",\"target_document\":\"M\",\"pairs\":[[10,20]],\"cut_order\":\"first_cuts\"}"));
            Assert.Contains("cut_order", why);
        }

        [Fact]
        public void The_operation_is_recognised_case_insensitively()
        {
            Assert.True(ToposolidExcavationRules.IsExcavation("Excavate"));
            Assert.True(ToposolidExcavationRules.IsExcavation("unexcavate"));
            Assert.False(ToposolidExcavationRules.IsExcavation("join"));
        }
    }
}
