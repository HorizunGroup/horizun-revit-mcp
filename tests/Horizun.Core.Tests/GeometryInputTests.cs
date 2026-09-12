using System;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public sealed class GeometryInputTests
    {
        [Fact]
        public void Boundary_comparison_accepts_splits_but_rejects_gaps_and_extra_edges()
        {
            var whole = new[] { new[] { 0d, 0, 10, 0 } };
            Assert.True(GeometryInput.SameBoundaryXY(whole, new[] { new[] { 4d, 0, 0, 0 }, new[] { 4d, 0, 10, 0 } }));
            Assert.False(GeometryInput.SameBoundaryXY(whole, new[] { new[] { 0d, 0, 4, 0 }, new[] { 5d, 0, 10, 0 } }));
            Assert.False(GeometryInput.SameBoundaryXY(whole, new[] { new[] { 0d, 0, 11, 0 } }));
            Assert.False(GeometryInput.SameBoundaryXY(whole, new[] { new[] { 0d, 1, 10, 1 } }));
        }

        [Fact]
        public void Ceiling_at_eight_feet_cannot_verify_nine_feet_one_and_one_eighth_inches()
        {
            var check = new PostconditionCheck("ceiling_elevation");
            check.Measure("ceiling_elevation", 9 + 1.125 / 12, 8, GeometryInput.Tolerance,"feet","reference face");
            Assert.False(check.AllVerified);
            Assert.Equal(9.09375, check.ToJson()["properties"][0]["requested"].Value<double>());
        }
        [Fact]
        public void Coordinate_modes_do_not_double_the_level_elevation()
        {
            Assert.Equal(10.32, GeometryInput.AbsoluteZ(10.32,10.32,"absolute"));
            Assert.Equal(10.32, GeometryInput.AbsoluteZ(0,10.32,"level_offset"));
            Assert.Equal(12.32, GeometryInput.AbsoluteZ(2,10.32,"level_offset"));
            Assert.Throws<ArgumentException>(()=>GeometryInput.AbsoluteZ(0,10.32,null));
            Assert.Throws<ArgumentException>(()=>GeometryInput.AbsoluteZ(0,null,"level_offset"));
        }
        [Fact]
        public void Eight_twelve_slope_is_a_ratio_not_radians()
        {
            double degrees = Math.Atan(8.0/12)*180/Math.PI;
            Assert.Equal(8.0/12,GeometryInput.SlopeRatio(degrees),12);
            Assert.NotEqual(degrees*Math.PI/180,GeometryInput.SlopeRatio(degrees));
            Assert.Throws<ArgumentException>(()=>GeometryInput.SlopeRatio(90));
        }
        [Fact]
        public void Perimeter_hole_and_explicit_closing_point_are_valid()
        {
            var profile = JArray.Parse("[[[0,0,0],[10,0,0],[10,8,0],[0,8,0],[0,0,0]],[[3,3,0],[3,5,0],[5,5,0],[5,3,0]]]");
            var loops=GeometryInput.HorizontalProfile(profile,1,0.002);
            Assert.Equal(2,loops.Count); Assert.Equal(4,loops[0].Count);
        }
        [Theory]
        [InlineData("[[[0,0,0],[10,8,0],[0,8,0],[10,0,0]]]", "self-intersects")]
        [InlineData("[[[0,0,0],[10,0,0],[10,8,1],[0,8,0]]]", "horizontal plane")]
        [InlineData("[[[0,0,0],[0.001,0,0],[10,8,0]]]", "too short")]
        [InlineData("[[[0,0,0],[10,0,0],[10,8,0],[0,8,0]],[[20,20,0],[21,20,0],[21,21,0]]]", "outside")]
        [InlineData("[[[0,0,0],[10,0,0],[10,8,0],[0,8,0]],[[0,3,0],[2,3,0],[2,5,0]]]", "touches")]
        public void Invalid_profiles_are_refused_before_any_Revit_API(string json,string reason)
        {
            Assert.Contains(reason,Assert.Throws<ArgumentException>(()=>GeometryInput.HorizontalProfile(JArray.Parse(json),1,0.002)).Message);
        }
        [Fact]
        public void Missing_or_unmeasured_geometry_cannot_verify()
        {
            var missing=new PostconditionCheck("kind","z").Compare("kind","Ceiling","Ceiling");
            Assert.False(missing.AllVerified);
            missing.Measure("z",9.09375,double.NaN,GeometryInput.Tolerance,"feet","face");
            Assert.False(missing.AllVerified); Assert.False(missing.AllMeasured);
        }
    }
}
