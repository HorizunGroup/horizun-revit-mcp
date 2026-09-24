using System;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class ArchitecturalEditRulesTests
    {
        private static JObject J(string s) => JObject.Parse(s);

        [Fact]
        public void Linear_step_is_the_vector_for_second_and_a_share_of_it_for_last()
        {
            Assert.Equal(new[] { 3.0, 0, 0 }, ArchitecturalEditRules.ArrayStepVector(new[] { 3.0, 0, 0 }, 4, false));
            Assert.Equal(new[] { 1.0, 2, 0 }, ArchitecturalEditRules.ArrayStepVector(new[] { 3.0, 6, 0 }, 4, true));
        }

        [Fact]
        public void Radial_last_splits_a_full_turn_count_ways_and_a_partial_turn_count_minus_one()
        {
            Assert.Equal(Math.PI / 2, ArchitecturalEditRules.ArrayStepAngle(Math.PI / 2, 4, false), 12);
            Assert.Equal(2 * Math.PI / 4, ArchitecturalEditRules.ArrayStepAngle(2 * Math.PI, 4, true), 12);
            Assert.Equal(Math.PI / 3, ArchitecturalEditRules.ArrayStepAngle(Math.PI, 4, true), 12);
        }

        [Theory]
        [InlineData(false, 1, false)] [InlineData(false, 2, true)] [InlineData(true, 2, false)]
        [InlineData(true, 3, true)] [InlineData(true, 200, true)] [InlineData(false, 201, false)]
        public void Array_counts_follow_revits_ranges(bool radial, int count, bool ok) =>
            Assert.Equal(ok, ArchitecturalEditRules.ValidateArrayCount(radial, count) == null);

        [Fact]
        public void Slab_vertex_reading_names_the_convention_that_held()
        {
            Assert.Equal("absolute", ArchitecturalEditRules.SlabVertexConvention(10.5, 10, 0.5, 1e-4));
            Assert.Equal("offset", ArchitecturalEditRules.SlabVertexConvention(0.5, 10, 0.5, 1e-4));
            Assert.Null(ArchitecturalEditRules.SlabVertexConvention(3, 10, 0.5, 1e-4));
        }

        [Fact]
        public void Covered_length_merges_overlaps_and_clips()
        {
            double c = ArchitecturalEditRules.CoveredLength(new[] { new[] { 0.0, 4 }, new[] { 3.0, 6 }, new[] { 9.0, 12 } }, 0, 10);
            Assert.Equal(7, c, 9);
            Assert.Equal(0, ArchitecturalEditRules.CoveredLength(null, 0, 10));
        }

        [Fact]
        public void Curtain_operations_refuse_fields_that_do_not_belong()
        {
            Assert.Null(ArchitecturalEditRules.ValidateCurtain(J("{operation:'read',element_id:1}")));
            Assert.Contains("mode", ArchitecturalEditRules.ValidateCurtain(J("{operation:'read',element_id:1,mode:'add'}")));
            Assert.Contains("exactly one", ArchitecturalEditRules.ValidateCurtain(J("{operation:'add_grid_line',element_id:1,direction:'u'}")));
            Assert.Null(ArchitecturalEditRules.ValidateCurtain(J("{operation:'add_grid_line',element_id:1,direction:'v',offset:900}")));
            Assert.Contains("mullion_type_id", ArchitecturalEditRules.ValidateCurtain(J("{operation:'set_mullions',element_id:1,grid_line_id:2,mode:'add'}")));
            Assert.Contains("operation must", ArchitecturalEditRules.ValidateCurtain(J("{operation:'explode',element_id:1}")));
            Assert.Contains("exactly one", ArchitecturalEditRules.ValidateCurtain(J("{operation:'set_panel_type',element_id:1,type_id:3}")));
        }

        [Fact]
        public void Slab_shape_modify_takes_vertices_or_one_crease()
        {
            Assert.Null(ArchitecturalEditRules.ValidateSlabShape(J("{operation:'modify_subelement',element_id:1,points:[[0,0,50]]}")));
            Assert.Null(ArchitecturalEditRules.ValidateSlabShape(J("{operation:'modify_subelement',element_id:1,start:[0,0],end:[1,0],offset:5}")));
            Assert.NotNull(ArchitecturalEditRules.ValidateSlabShape(J("{operation:'modify_subelement',element_id:1,points:[[0,0,5]],offset:5}")));
            Assert.NotNull(ArchitecturalEditRules.ValidateSlabShape(J("{operation:'add_point',element_id:1}")));
            Assert.NotNull(ArchitecturalEditRules.ValidateSlabShape(J("{operation:'reset_shape'}")));
        }

        [Fact]
        public void Railing_takes_a_host_or_a_path_never_both()
        {
            Assert.Null(ArchitecturalEditRules.ValidateRailing(J("{type_id:1,host_id:2,placement:'stringer'}")));
            Assert.Null(ArchitecturalEditRules.ValidateRailing(J("{type_id:1,path:[[0,0,0],[1000,0,0]],level_id:3}")));
            Assert.NotNull(ArchitecturalEditRules.ValidateRailing(J("{type_id:1,host_id:2,path:[[0,0,0],[1,0,0]],level_id:3}")));
            Assert.NotNull(ArchitecturalEditRules.ValidateRailing(J("{type_id:1,path:[[0,0,0],[1,0,0]]}")));
            Assert.NotNull(ArchitecturalEditRules.ValidateRailing(J("{host_id:2}")));
            Assert.NotNull(ArchitecturalEditRules.ValidateRailing(J("{type_id:1,path:[[0,0,0],[1,0,0]],level_id:3,placement:'treads'}")));
        }
    }
}
