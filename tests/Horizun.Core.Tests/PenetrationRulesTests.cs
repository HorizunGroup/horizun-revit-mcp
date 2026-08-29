// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// Penetration arithmetic. The rectangle a wall opening is cut from, and the
// gates in front of it - each refusal is a case that would cost a staged
// clash to reproduce live.
// -----------------------------------------------------------------------------
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class PenetrationRulesTests
    {
        [Fact]
        public void Exactly_one_mep_curve_makes_a_penetration_pair()
        {
            Assert.True(PenetrationRules.ClassifyPair(true, false, out bool penetrantIsA, out _, out _));
            Assert.True(penetrantIsA);
            Assert.True(PenetrationRules.ClassifyPair(false, true, out penetrantIsA, out _, out _));
            Assert.False(penetrantIsA);
        }

        [Fact]
        public void Two_curves_are_a_routing_clash_not_a_penetration()
        {
            Assert.False(PenetrationRules.ClassifyPair(true, true, out _, out string code, out string reason));
            Assert.Equal(PenetrationRules.CodeNotAPenetrationPair, code);
            Assert.Contains("routing clash", reason);
        }

        [Fact]
        public void Two_hosts_are_not_a_penetration_either()
        {
            Assert.False(PenetrationRules.ClassifyPair(false, false, out _, out string code, out _));
            Assert.Equal(PenetrationRules.CodeNotAPenetrationPair, code);
        }

        [Fact]
        public void A_linked_host_is_refused_before_the_structural_gate()
        {
            Assert.False(PenetrationRules.HostPermitted(false, true, true, out string code, out string reason));
            Assert.Equal(PenetrationRules.CodeHostIsLinked, code);
            Assert.Contains("LINKED document", reason);
        }

        [Fact]
        public void A_structural_host_needs_the_explicit_opt_in()
        {
            Assert.False(PenetrationRules.HostPermitted(true, true, false, out string code, out string reason));
            Assert.Equal(PenetrationRules.CodeStructuralHostRequiresOptIn, code);
            Assert.Contains("engineering decision", reason);

            Assert.True(PenetrationRules.HostPermitted(true, true, true, out _, out _));
            Assert.True(PenetrationRules.HostPermitted(true, false, false, out _, out _));
        }

        [Fact]
        public void The_opening_rectangle_is_centred_and_cleared_all_around()
        {
            // A 100 mm-wide penetrant crossing along +X at P=(10,20,5) ft with 25 mm
            // clearance: the horizontal span is along Y (up x X), the vertical along Z.
            double w = 100 / 304.8, c = 25 / 304.8;
            Assert.True(PenetrationRules.OpeningCorners(10, 20, 5, 1, 0, 0, w, w, c,
                out double[] p1, out double[] p2, out _, out _));
            Assert.Equal(10, p1[0], 6); Assert.Equal(10, p2[0], 6);           // no span along the run
            Assert.Equal(20 - (w / 2 + c), p1[1], 6);
            Assert.Equal(20 + (w / 2 + c), p2[1], 6);
            Assert.Equal(5 - (w / 2 + c), p1[2], 6);
            Assert.Equal(5 + (w / 2 + c), p2[2], 6);
        }

        [Fact]
        public void A_skewed_run_still_spans_perpendicular_to_itself()
        {
            // Penetrant at 45 degrees in plan: the horizontal span must be the
            // horizontal perpendicular, unit length - not a diagonal artifact.
            double s = System.Math.Sqrt(0.5);
            Assert.True(PenetrationRules.OpeningCorners(0, 0, 0, s, s, 0, 1, 1, 0,
                out double[] p1, out double[] p2, out _, out _));
            double spanX = p2[0] - p1[0], spanY = p2[1] - p1[1];
            Assert.Equal(1.0, System.Math.Sqrt(spanX * spanX + spanY * spanY), 6);
            // Perpendicular: dot with the run direction is zero.
            Assert.Equal(0.0, spanX * s + spanY * s, 6);
        }

        [Fact]
        public void A_near_vertical_penetrant_refuses_as_a_floor_case()
        {
            Assert.False(PenetrationRules.OpeningCorners(0, 0, 0, 0, 0.1, 0.99, 1, 1, 0,
                out _, out _, out string code, out string reason));
            Assert.Equal(PenetrationRules.CodeOpeningWallsOnly, code);
            Assert.Contains("sleeve", reason);
        }

        [Fact]
        public void Clustering_is_transitive_and_off_by_default()
        {
            var points = new System.Collections.Generic.List<double[]>
            {
                new[] { 0.0, 0, 0 }, new[] { 0.4, 0, 0 }, new[] { 0.8, 0, 0 }, new[] { 10.0, 0, 0 }
            };
            var separate = PenetrationRules.Cluster(points, 0);
            Assert.Equal(4, separate.Count);
            // radius 0.5: 0-1 within, 1-2 within -> one transitive chain; 3 alone.
            var grouped = PenetrationRules.Cluster(points, 0.5);
            Assert.Equal(2, grouped.Count);
            Assert.Equal(3, grouped[0].Count);
            Assert.Single(grouped[1]);
        }

        [Fact]
        public void The_cluster_rectangle_spans_every_member()
        {
            var c1 = new System.Collections.Generic.List<double[]> { new[] { 0.0, 0, 0 }, new[] { 2.0, -1, 0 } };
            var c2 = new System.Collections.Generic.List<double[]> { new[] { 1.0, 1, 1 }, new[] { 3.0, 0.5, 2 } };
            Assert.True(PenetrationRules.ClusterCorners(c1, c2, out double[] lo, out double[] hi));
            Assert.Equal(new[] { 0.0, -1, 0 }, lo);
            Assert.Equal(new[] { 3.0, 1, 2 }, hi);
        }

        [Fact]
        public void Opening_sizes_are_positive_and_bounded()
        {
            Assert.True(PenetrationRules.ValidateOpeningSize(1, 1, out _));
            Assert.False(PenetrationRules.ValidateOpeningSize(0, 1, out string reason));
            Assert.Contains("positive", reason);
            Assert.False(PenetrationRules.ValidateOpeningSize(100, 1, out reason));
            Assert.Contains("20 m", reason);
        }

        [Fact]
        public void No_direction_or_no_size_refuses_rather_than_inventing()
        {
            Assert.False(PenetrationRules.OpeningCorners(0, 0, 0, 0, 0, 0, 1, 1, 0,
                out _, out _, out string code, out _));
            Assert.Equal(PenetrationRules.CodeNoCrossSection, code);
            Assert.False(PenetrationRules.OpeningCorners(0, 0, 0, 1, 0, 0, 0, 1, 0,
                out _, out _, out code, out _));
            Assert.Equal(PenetrationRules.CodeNoCrossSection, code);
        }
    }
}
