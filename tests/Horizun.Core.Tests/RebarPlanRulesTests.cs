// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// The failure under test: a rebar set longer than its host. Revit creates it
// without complaint - correct element, correct host, correct type - and some of
// the steel stands in the air outside the beam. Nothing in the reply separates
// that from a correct set, so the plan has to separate it, before a transaction
// exists.
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class RebarPlanRulesTests
    {
        private static double[] P(double x, double y, double z) { return new[] { x, y, z }; }

        /// <summary>A beam 4000 long in X, 300 wide in Y, 500 deep in Z, at the origin.</summary>
        private static List<double[]> Beam()
        {
            return RebarPlanRules.BoxCorners(P(0, 0, 0), P(4000, 300, 500));
        }

        /// <summary>One stirrup standing in the YZ plane at x = 0.</summary>
        private static List<double[]> Stirrup()
        {
            return new List<double[]>
            {
                P(0, 40, 40), P(0, 260, 40), P(0, 260, 460), P(0, 40, 460)
            };
        }

        private static List<double> Positions(params double[] d) { return d.ToList(); }

        // ---------------------------------------------------------------- fits

        [Fact]
        public void A_set_that_stays_inside_the_beam_fits()
        {
            RebarFitVerdict v = RebarPlanRules.Fit(Stirrup(), Beam(), P(1, 0, 0),
                                                   Positions(0, 200, 400, 600), 2.0);
            Assert.True(v.Fits);
            Assert.Equal(RebarPlanRules.CodeFits, v.Code);
            Assert.Empty(v.OutsideIndices);
            Assert.Equal(0.0, v.WorstOvershootMm, 6);
        }

        [Fact]
        public void The_verdict_says_it_measured_ONE_AXIS_and_does_not_claim_containment()
        {
            // `fits: true` must not be readable as "the bar is inside the concrete".
            RebarFitVerdict v = RebarPlanRules.Fit(Stirrup(), Beam(), P(1, 0, 0), Positions(0), 2.0);
            Assert.True(v.Fits);
            Assert.Contains("projection onto one axis", v.Why);
            Assert.Contains("does not prove", v.Why);
        }

        // ------------------------------------------------------------ does not

        [Fact]
        public void A_set_that_runs_PAST_THE_END_names_every_position_that_does()
        {
            // Positions 4200 and 4400 are past the 4000 beam.
            RebarFitVerdict v = RebarPlanRules.Fit(Stirrup(), Beam(), P(1, 0, 0),
                                                   Positions(0, 2000, 4000, 4200, 4400), 2.0);
            Assert.False(v.Fits);
            Assert.Equal(RebarPlanRules.CodeSetOutsideHost, v.Code);
            Assert.Equal(new[] { 3, 4 }, v.OutsideIndices);
            Assert.Equal(400.0, v.WorstOvershootMm, 6);
        }

        [Fact]
        public void A_position_in_the_MIDDLE_that_is_wrong_is_still_caught()
        {
            // Checking only the two ends would pass this. The declared positions are
            // what the caller sent, and a caller can send anything.
            RebarFitVerdict v = RebarPlanRules.Fit(Stirrup(), Beam(), P(1, 0, 0),
                                                   Positions(0, 9000, 4000), 2.0);
            Assert.False(v.Fits);
            Assert.Equal(new[] { 1 }, v.OutsideIndices);
        }

        [Fact]
        public void A_bar_ALREADY_outside_its_host_is_a_different_finding_from_a_long_set()
        {
            // Shortening the array would not help. The bar is in the wrong place.
            var away = Stirrup().Select(p => P(p[0] + 9000, p[1], p[2])).ToList();
            RebarFitVerdict v = RebarPlanRules.Fit(away, Beam(), P(1, 0, 0), Positions(0), 2.0);
            Assert.False(v.Fits);
            Assert.Equal(RebarPlanRules.CodeBarOutsideHost, v.Code);
            Assert.Contains("before the set", v.Why);
        }

        [Fact]
        public void A_host_that_could_not_be_MEASURED_is_unknown_and_never_a_pass()
        {
            RebarFitVerdict v = RebarPlanRules.Fit(Stirrup(), new List<double[]>(), P(1, 0, 0),
                                                   Positions(0), 2.0);
            Assert.False(v.Fits);
            Assert.Equal(RebarPlanRules.CodeHostNotMeasured, v.Code);
            Assert.Contains("UNKNOWN", v.Why);
        }

        [Fact]
        public void A_ZERO_direction_is_refused_rather_than_dividing_by_it()
        {
            RebarFitVerdict v = RebarPlanRules.Fit(Stirrup(), Beam(), P(0, 0, 0), Positions(0), 2.0);
            Assert.False(v.Fits);
            Assert.Equal(RebarPlanRules.CodeNormalDegenerate, v.Code);
        }

        // ------------------------------------------------------------- axes

        [Fact]
        public void The_same_set_marching_UP_a_column_is_measured_on_the_Z_axis()
        {
            // A column 300x300 and 3000 tall. Ties every 200 up to 2800 fit; a set
            // that reached 3200 would not - and the arithmetic is the same code.
            List<double[]> column = RebarPlanRules.BoxCorners(P(0, 0, 0), P(300, 300, 3000));
            var tie = new List<double[]> { P(40, 40, 0), P(260, 40, 0), P(260, 260, 0), P(40, 260, 0) };

            RebarFitVerdict ok = RebarPlanRules.Fit(tie, column, P(0, 0, 1), Positions(0, 200, 2800), 2.0);
            Assert.True(ok.Fits);

            RebarFitVerdict bad = RebarPlanRules.Fit(tie, column, P(0, 0, 1), Positions(0, 200, 3200), 2.0);
            Assert.False(bad.Fits);
            Assert.Equal(new[] { 2 }, bad.OutsideIndices);
        }

        [Fact]
        public void An_UNNORMALISED_direction_means_the_same_axis()
        {
            // A requirement set writes [0, 1, 0] and it writes [0, 4000, 0] meaning
            // the same thing. If the direction were not normalised, the second would
            // divide every projected distance by 4000 and everything would "fit".
            RebarFitVerdict a = RebarPlanRules.Fit(Stirrup(), Beam(), P(1, 0, 0), Positions(0, 4200), 2.0);
            RebarFitVerdict b = RebarPlanRules.Fit(Stirrup(), Beam(), P(7, 0, 0), Positions(0, 4200), 2.0);
            Assert.False(a.Fits);
            Assert.False(b.Fits);
            Assert.Equal(a.OutsideIndices, b.OutsideIndices);
            Assert.Equal(a.WorstOvershootMm, b.WorstOvershootMm, 6);
        }

        [Fact]
        public void A_box_is_projected_by_all_EIGHT_corners_not_by_two()
        {
            // On a diagonal direction the extreme corner is not min or max.
            List<double[]> box = RebarPlanRules.BoxCorners(P(0, 0, 0), P(100, 200, 300));
            Span s = RebarPlanRules.SpanOf(box, P(1, -1, 0));
            // The far corner along (1,-1,0)/sqrt2 is (100, 0, z) -> 100/sqrt2.
            Assert.Equal(100.0 / System.Math.Sqrt(2), s.Max, 6);
            // and the near one is (0, 200, z) -> -200/sqrt2.
            Assert.Equal(-200.0 / System.Math.Sqrt(2), s.Min, 6);
        }

        // ------------------------------------------------------------- length

        [Fact]
        public void The_centreline_length_of_an_open_bar_is_the_sum_of_its_segments()
        {
            var l = new List<double[]> { P(0, 0, 0), P(3000, 0, 0), P(3000, 400, 0) };
            Assert.Equal(3400.0, RebarPlanRules.CentrelineLengthMm(l, false), 6);
        }

        [Fact]
        public void A_CLOSED_bar_includes_the_segment_back_to_the_start()
        {
            Assert.Equal(4 * 220.0 + 0.0,
                         RebarPlanRules.CentrelineLengthMm(new List<double[]>
                         {
                             P(0,0,0), P(220,0,0), P(220,220,0), P(0,220,0)
                         }, true), 6);
        }

        [Fact]
        public void Hooks_are_NOT_in_the_expected_length()
        {
            // Revit adds hook length itself and reports the result. An expectation
            // that guessed at it could never be matched by the model, and the
            // verification would fail on every correctly built bar.
            var l = new List<double[]> { P(0, 0, 0), P(1000, 0, 0) };
            Assert.Equal(1000.0, RebarPlanRules.CentrelineLengthMm(l, false), 6);
        }

        // ------------------------------------------------------------- planar

        [Fact]
        public void A_planar_stirrup_is_planar()
        {
            double off;
            Assert.True(RebarPlanRules.IsPlanar(Stirrup(), 1.0, out off));
            Assert.Equal(0.0, off, 6);
        }

        [Fact]
        public void A_bent_run_is_NOT_planar_and_the_deviation_is_a_number()
        {
            var bent = new List<double[]>
            {
                P(0, 40, 40), P(0, 260, 40), P(25, 260, 460), P(0, 40, 460)
            };
            double off;
            Assert.False(RebarPlanRules.IsPlanar(bent, 1.0, out off));
            Assert.True(off > 5 && off < 8, "off = " + off);
        }

        [Fact]
        public void It_names_NO_CULPRIT_because_the_geometry_does_not_support_one()
        {
            // The first version took the plane of the first three points, so a typo
            // in point 2 DEFINED the plane and point 3 was blamed for it. The
            // least-squares plane does not have that bug and has a different
            // property: displace one vertex of a rectangle and all four end up
            // NEARLY equidistant from the fitted plane - measured, 6.2627, 6.2517,
            // 6.2115, 6.2225 - so there is no offender to name and none is named.
            var bent = new List<double[]>
            {
                P(0, 40, 40), P(0, 260, 40), P(25, 260, 460), P(0, 40, 460)
            };
            List<double> d = RebarPlanRules.PlanarityDeviationsMm(bent);
            Assert.Equal(4, d.Count);
            foreach (double x in d) Assert.True(x > 6.0 && x < 6.4, "deviation " + x);
            // The spread between them is a twentieth of a millimetre on a six
            // millimetre error: nothing in that picks a culprit out.
            Assert.True(d.Max() - d.Min() < 0.1);
        }

        [Fact]
        public void A_RUN_THAT_FOLDS_BACK_ON_ITSELF_is_still_measured()
        {
            // MEASURED, and the reason the normal is the smallest eigenvector of the
            // covariance rather than Newell's vector area. Newell sums the SIGNED
            // areas of the projected polygon, and for a run that folds back they
            // CANCEL: this six-point run has a Newell normal of exactly (0,0,0)
            // while its points lie 156.9 mm off any plane through them. Reported as
            // planar, it went straight to the Revit call the check exists to
            // pre-empt - and an exhaustive search over a five-value lattice found
            // 124,288 six-point runs with the same cancellation, so this is a family
            // of ordinary shapes rather than one contrived example.
            var folded = new List<double[]>
            {
                P(0, 0, 0), P(200, 0, 0), P(0, 200, 200),
                P(200, 200, -200), P(0, 200, 0), P(200, 100, 200)
            };
            double off;
            Assert.False(RebarPlanRules.IsPlanar(folded, 2.0, out off));
            Assert.True(off > 150 && off < 160, "deviation " + off);
        }

        [Fact]
        public void A_point_that_is_NOT_A_NUMBER_is_not_reported_as_zero_deviation()
        {
            var bad = new List<double[]>
            {
                P(0, 0, 0), P(100, 0, 0), P(100, 100, 0), P(double.NaN, 0, 0)
            };
            double off;
            Assert.False(RebarPlanRules.IsPlanar(bad, 1000.0, out off));
            Assert.True(double.IsNaN(off));
        }

        [Fact]
        public void Which_point_moved_does_not_change_the_verdict()
        {
            // The same rectangle with a different corner lifted by the same amount is
            // equally non-planar. A rule that answered differently would be reporting
            // the order the points were written in.
            double a, b;
            RebarPlanRules.IsPlanar(new List<double[]>
            {
                P(0, 40, 40), P(0, 260, 40), P(25, 260, 460), P(0, 40, 460)
            }, 1.0, out a);
            RebarPlanRules.IsPlanar(new List<double[]>
            {
                P(25, 40, 40), P(0, 260, 40), P(0, 260, 460), P(0, 40, 460)
            }, 1.0, out b);
            Assert.Equal(a, b, 6);
        }

        [Fact]
        public void Three_points_are_always_planar_and_so_are_collinear_ones()
        {
            double off;
            Assert.True(RebarPlanRules.IsPlanar(new List<double[]> { P(0, 0, 0), P(1, 0, 0), P(2, 0, 0) },
                                                0.001, out off));
            Assert.True(RebarPlanRules.IsPlanar(new List<double[]>
            {
                P(0, 0, 0), P(1, 0, 0), P(2, 0, 0), P(3, 0, 0)
            }, 0.001, out off));
            Assert.Equal(0.0, off, 9);
        }
    }
}
