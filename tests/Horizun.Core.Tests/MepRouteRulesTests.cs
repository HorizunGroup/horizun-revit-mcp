// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// The route fold: collinear vertices merge and are NAMED, short segments
// refuse in millimetres, corners are measured turns.
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class MepRouteRulesTests
    {
        private const double Ft = 304.8;
        private static double[] P(double x, double y, double z = 0) => new[] { x / Ft, y / Ft, z / Ft };

        [Fact]
        public void An_L_becomes_two_segments_with_their_vertices()
        {
            string error = MepRouteRules.Segments(new List<double[]> { P(0, 0), P(3000, 0), P(3000, 2000) },
                out List<RouteSegment> segments, out List<int> merged);
            Assert.Null(error);
            Assert.Equal(2, segments.Count);
            Assert.Empty(merged);
            Assert.Equal(0, segments[0].FromVertex);
            Assert.Equal(1, segments[0].ToVertex);
            Assert.Equal(2, segments[1].ToVertex);
        }

        [Fact]
        public void A_collinear_vertex_is_merged_and_named()
        {
            string error = MepRouteRules.Segments(
                new List<double[]> { P(0, 0), P(1500, 0), P(3000, 0), P(3000, 2000) },
                out List<RouteSegment> segments, out List<int> merged);
            Assert.Null(error);
            Assert.Equal(2, segments.Count);        // the straight middle folded away
            Assert.Equal(new List<int> { 1 }, merged);
            Assert.Equal(2, segments[0].ToVertex);  // the kept corner is the real one
        }

        [Fact]
        public void A_degenerate_segment_refuses_in_millimetres()
        {
            string error = MepRouteRules.Segments(new List<double[]> { P(0, 0), P(20, 0), P(3000, 0) },
                out _, out _);
            Assert.NotNull(error);
            Assert.Contains("segment_too_short", error);
            Assert.Contains("20.0 mm", error);
            Assert.Contains("vertex 1", error);
        }

        [Fact]
        public void One_point_is_not_a_route()
        {
            string error = MepRouteRules.Segments(new List<double[]> { P(0, 0) }, out _, out _);
            Assert.Contains("route_needs_two_points", error);
        }

        [Fact]
        public void The_turn_is_zero_straight_through_and_ninety_at_a_corner()
        {
            Assert.Equal(0, MepRouteRules.TurnDegrees(P(0, 0), P(1000, 0), P(2000, 0)), 6);
            Assert.Equal(90, MepRouteRules.TurnDegrees(P(0, 0), P(1000, 0), P(1000, 1000)), 6);
        }

        [Fact]
        public void A_vertical_riser_is_a_segment_like_any_other()
        {
            string error = MepRouteRules.Segments(
                new List<double[]> { P(0, 0, 0), P(3000, 0, 0), P(3000, 0, 2600) },
                out List<RouteSegment> segments, out _);
            Assert.Null(error);
            Assert.Equal(2, segments.Count);
        }
    }
}
