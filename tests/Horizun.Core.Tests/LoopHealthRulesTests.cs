using System;
using System.Collections.Generic;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class LoopHealthRulesTests
    {
        private const double ShortCurve = 0.00256; // Revit's ShortCurveTolerance, feet (about 0.78 mm)
        private const double Mm = 1 / 304.8;

        private static List<double[]> P(params double[][] pts) => new List<double[]>(pts);
        private static double[] V(double xMm, double yMm) => new[] { xMm * Mm, yMm * Mm, 0.0 };

        [Fact]
        public void A_plain_rectangle_is_healthy()
        {
            Assert.Empty(LoopHealthRules.Problems(P(V(0, 0), V(5000, 0), V(5000, 3000), V(0, 3000)), ShortCurve));
        }

        [Fact]
        public void A_neck_narrower_than_the_short_curve_tolerance_is_a_problem_naming_both_edges()
        {
            // A dumbbell whose waist is 0.3 mm wide: valid to a geometry library, refused by Revit at apply.
            var dumbbell = P(V(0, 0), V(4000, 0), V(4000, 2000), V(5000, 2000), V(5000, 3000), V(4000, 3000),
                             V(4000, 2000.3), V(0, 2000.3));
            List<string> problems = LoopHealthRules.Problems(dumbbell, ShortCurve);
            Assert.NotEmpty(problems);
            Assert.Contains(problems, p => p.Contains("come within 0.3 mm"));
            Assert.Contains(problems, p => p.Contains("0.78"));
        }

        [Fact]
        public void The_same_neck_wide_enough_is_fine()
        {
            var ok = P(V(0, 0), V(4000, 0), V(4000, 2000), V(5000, 2000), V(5000, 3000), V(4000, 3000),
                       V(4000, 2003), V(0, 2003));
            Assert.Empty(LoopHealthRules.Problems(ok, ShortCurve));
        }

        [Fact]
        public void A_vertex_that_nearly_touches_an_edge_is_a_problem()
        {
            // Vertex 5 sits 0.4 mm above edge 0-1.
            var touching = P(V(0, 0), V(5000, 0), V(5000, 3000), V(2500, 3000), V(2500, 0.4), V(2400, 3000), V(0, 3000));
            Assert.Contains(LoopHealthRules.Problems(touching, ShortCurve), p => p.Contains("nearly touches"));
        }

        [Fact]
        public void An_edge_that_doubles_back_is_a_spike_of_width_zero()
        {
            var spike = P(V(0, 0), V(5000, 0), V(8000, 0), V(5000, 0.0), V(5000, 3000), V(0, 3000));
            Assert.Contains(LoopHealthRules.Problems(spike, ShortCurve), p => p.Contains("double back"));
        }

        [Fact]
        public void Distances_between_segments_are_exact()
        {
            Assert.Equal(0, LoopHealthRules.SegmentDistance(V(0, 0), V(10, 10), V(0, 10), V(10, 0)), 12);          // crossing
            Assert.Equal(5 * Mm, LoopHealthRules.SegmentDistance(V(0, 0), V(10, 0), V(0, 5), V(10, 5)), 12);      // parallel
            Assert.Equal(5 * Mm, LoopHealthRules.SegmentDistance(V(0, 0), V(10, 0), V(15, 0), V(25, 0)), 12);     // collinear apart
        }

        [Fact]
        public void The_profile_reader_refuses_a_narrow_neck_before_anything_is_written()
        {
            var profile = JArray.Parse(@"[[[0,0,0],[4000,0,0],[4000,2000,0],[5000,2000,0],[5000,3000,0],[4000,3000,0],[4000,2000.3,0],[0,2000.3,0]]]");
            var ex = Assert.Throws<ArgumentException>(() => GeometryInput.HorizontalProfile(profile, Mm, ShortCurve));
            Assert.Contains("profile[0]", ex.Message);
            Assert.Contains("come within", ex.Message);
            Assert.Contains("nothing was written", ex.Message);
        }

        [Fact]
        public void The_profile_reader_still_accepts_a_healthy_profile()
        {
            var profile = JArray.Parse(@"[[[0,0,0],[5000,0,0],[5000,3000,0],[0,3000,0]]]");
            var contours = GeometryInput.HorizontalProfile(profile, Mm, ShortCurve);
            Assert.Single(contours);
            Assert.Equal(4, contours[0].Count);
        }
    }
}
