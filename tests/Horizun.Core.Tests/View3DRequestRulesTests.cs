using System;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class View3DRequestRulesTests
    {
        [Theory]
        [InlineData("eye", "start")]
        [InlineData("target", "end")]
        [InlineData("perspective", "create_perspective")]
        [InlineData("camera", "start/end/up")]
        public void A_camera_spelled_the_wrong_way_is_refused_naming_the_right_one(string key, string use)
        {
            string problem = View3DRequestRules.MisnamedCameraField(new[] { "operation", key, "name" });
            Assert.NotNull(problem);
            Assert.Contains(key, problem);
            Assert.Contains(use, problem);
            Assert.Contains("Nothing was written", problem);
        }

        [Fact]
        public void Honoured_keys_pass()
        {
            Assert.Null(View3DRequestRules.MisnamedCameraField(new[] { "operation", "start", "end", "up", "name", "display_style" }));
            Assert.Null(View3DRequestRules.MisnamedCameraField(null));
        }

        [Fact]
        public void A_camera_needs_both_the_eye_and_the_target()
        {
            Assert.False(View3DRequestRules.WantsCamera(false, false, false, out string none));
            Assert.Null(none);
            Assert.True(View3DRequestRules.WantsCamera(true, true, false, out string ok));
            Assert.Null(ok);
            Assert.True(View3DRequestRules.WantsCamera(true, false, false, out string half));
            Assert.Contains("only start", half);
            Assert.True(View3DRequestRules.WantsCamera(false, true, false, out string other));
            Assert.Contains("only end", other);
            Assert.True(View3DRequestRules.WantsCamera(false, false, true, out string upOnly));
            Assert.Contains("needs start and end", upOnly);
        }

        [Theory]
        [InlineData("shaded", "Shading")]
        [InlineData("Shading", "Shading")]
        [InlineData("SHADED_WITH_EDGES", "ShadingWithEdges")]
        [InlineData("hidden_line", "HLR")]
        [InlineData("hlr", "HLR")]
        [InlineData("wireframe", "Wireframe")]
        [InlineData("consistent_colors", "FlatColors")]
        [InlineData("FlatColors", "FlatColors")]
        [InlineData("realistic", "Realistic")]
        [InlineData("RealisticWithEdges", "RealisticWithEdges")]
        public void Style_words_map_to_revit_display_styles(string word, string expected)
        {
            Assert.Equal(expected, View3DRequestRules.ParseStyle(word, out string error));
            Assert.Null(error);
        }

        [Fact]
        public void An_unknown_style_lists_the_words_that_work_and_an_empty_one_is_no_request()
        {
            Assert.Null(View3DRequestRules.ParseStyle("cartoon", out string error));
            Assert.Contains("shaded", error);
            Assert.Contains("hlr", error);
            Assert.Null(View3DRequestRules.ParseStyle(null, out string none));
            Assert.Null(none);
        }

        [Fact]
        public void Angles_read_the_sight_line_as_azimuth_and_pitch()
        {
            double[] east = View3DRequestRules.Angles(new[] { 1.0, 0, 0 });
            Assert.Equal(0, east[0], 6); Assert.Equal(0, east[1], 6);
            double[] southDown = View3DRequestRules.Angles(new[] { 0.0, -1, -1 });
            Assert.Equal(270, southDown[0], 6);
            Assert.Equal(-45, southDown[1], 6);
        }

        [Fact]
        public void The_camera_description_says_where_the_eye_is_and_how_it_looks()
        {
            var d = View3DRequestRules.Describe(new[] { 1.0, 2, 5.25 }, new[] { 0.0, 1, 0 }, new[] { 0.0, 0, 1 }, true, false, false, "Shading");
            Assert.Equal(5.25, (double)d["eye_internal_feet"][2], 9);
            Assert.Equal(90, (double)d["azimuth_degrees"], 6);
            Assert.Equal("perspective", (string)d["projection"]);
            Assert.Equal("Shading", (string)d["display_style"]);
            Assert.False((bool)d["orientation_locked"]);
        }
    }
}
