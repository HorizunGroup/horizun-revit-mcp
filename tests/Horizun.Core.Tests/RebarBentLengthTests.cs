// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// A bent bar with no hooks: the declaration draws a sharp corner, Revit draws an
// arc of the bar type's bend radius, and the bar is SHORTER by R(2 tan(t/2) - t)
// per corner. MEASURED live (Revit 2024, acero 3.0 L1, J4): an L of 500 + 1890 mm
// in 16M, bend diameter 96 mm, read 2370 against 2390 declared - and the audit
// called a correctly built bar `length_differs`.
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class RebarBentLengthTests
    {
        private static StructuralTolerances Tol() => new StructuralTolerances { LengthMm = 2.0, SpacingMm = 2.0, CoverMm = 1.0, AngleDegrees = 1.0 };

        private static JObject Expected(double bendDiameterMm) => JObject.Parse(@"{
          'rule_id': 'J4', 'host_id': 5001,
          'bar_type': { 'id': 300, 'nominal_diameter_mm': 16.0, 'model_diameter_mm': 16.0, 'standard_bend_diameter_mm': BEND },
          'shape': { 'declared': false, 'revit_will_match_or_create': true },
          'style': 'standard', 'closed': false,
          'curve_mm': [ [ 0, 0, 0 ], [ 0, 0, 500 ], [ 1890, 0, 500 ] ],
          'expected_total_steel_length_mm': 2390.0,
          'normal': { 'x': 0.0, 'y': 1.0, 'z': 0.0 },
          'layout': { 'rule': 'single', 'number_of_bar_positions': 1, 'quantity': 1, 'array_length_mm': 0.0,
                      'include_first_bar': true, 'include_last_bar': true, 'bars_on_normal_side': true },
          'terminations': { 'start': { 'hook_type_id': -1, 'has_hook': false, 'orientation': 'left' },
                            'end':   { 'hook_type_id': -1, 'has_hook': false, 'orientation': 'left' } }
        }".Replace("BEND", bendDiameterMm.ToString(System.Globalization.CultureInfo.InvariantCulture)).Replace('\'', '"'));

        private static JObject Observed(double lengthMm) => JObject.Parse(@"{
          'id': 9001, 'host': { 'id': 5001, 'resolved': true },
          'bar_type': { 'resolved': true, 'id': 300, 'nominal_diameter_mm': 16.0 },
          'shape': { 'id': 400, 'resolved': true }, 'style_horizun': 'standard',
          'layout': { 'rule_horizun': 'single', 'number_of_bar_positions': 1, 'quantity': 1,
                      'include_first_bar': true, 'include_last_bar': true, 'bars_on_normal_side': true,
                      'normal': { 'x': 0.0, 'y': 1.0, 'z': 0.0 } },
          'terminations': [ { 'end': 0, 'hook_type_id': -1, 'hook_readable': true, 'orientation': 'left' },
                            { 'end': 1, 'hook_type_id': -1, 'hook_readable': true, 'orientation': 'left' } ],
          'measured': { 'total_length_mm': LEN }
        }".Replace("LEN", lengthMm.ToString(System.Globalization.CultureInfo.InvariantCulture)).Replace('\'', '"'));

        private static List<string> Codes(JArray f) => f.OfType<JObject>().Select(o => (string)o["code"]).ToList();

        [Fact]
        public void A_right_angle_bend_shortens_the_bar_by_its_arc()
        {
            // R = 48: 2 * 48 - pi/2 * 48 = 20.6 mm shorter.
            Assert.Equal(20.6, RebarAuditRules.BendShorteningMm(new List<double[]>
                { new double[] { 0, 0, 0 }, new double[] { 0, 0, 500 }, new double[] { 1890, 0, 500 } }, false, 48), 1);
        }

        [Fact]
        public void The_bar_Revit_bent_agrees_with_the_L_it_was_asked_for() =>
            Assert.DoesNotContain(RebarFinding.LengthDiffers, Codes(RebarAuditRules.CompareBar(Expected(96), Observed(2366.0), Tol())));

        [Fact]
        public void A_bar_cut_short_by_more_than_the_bend_is_still_a_difference() =>
            Assert.Contains(RebarFinding.LengthDiffers, Codes(RebarAuditRules.CompareBar(Expected(96), Observed(2350), Tol())));

        [Fact]
        public void A_bar_left_sharp_at_the_declared_length_now_reads_too_long() =>
            // The correction is a prediction, not an allowance: 2390 is no longer what Revit draws.
            Assert.Contains(RebarFinding.LengthDiffers, Codes(RebarAuditRules.CompareBar(Expected(96), Observed(2390), Tol())));

        [Fact]
        public void With_no_bend_radius_reported_the_comparison_stays_against_the_sharp_corner() =>
            Assert.Contains(RebarFinding.LengthDiffers, Codes(RebarAuditRules.CompareBar(Expected(0), Observed(2366.0), Tol())));

        // Revit ROUNDS a bar's TotalLength by the bar type's rule (measured L2, Revit 2024: three
        // cranked bars read 11310 = 3 x 3770 against 3 x 3767.85 declared, and the API's rounding
        // value is in display units). The audit compares the EXACT centreline length instead.
        private static JObject Exact(JObject o, double oneBarMm)
        {
            o["measured"]["bar_centreline_length_mm"] = oneBarMm;
            return o;
        }

        [Fact]
        public void The_exact_centreline_length_wins_over_a_rounded_total() =>
            Assert.DoesNotContain(RebarFinding.LengthDiffers, Codes(RebarAuditRules.CompareBar(Expected(96), Exact(Observed(2370), 2366.0), Tol())));

        [Fact]
        public void An_exact_length_that_is_wrong_is_a_difference_whatever_the_rounded_total_says() =>
            Assert.Contains(RebarFinding.LengthDiffers, Codes(RebarAuditRules.CompareBar(Expected(96), Exact(Observed(2370), 2380), Tol())));

        // MEASURED (acero 3.0 L3, Revit 2024): a closed 10M stirrup with no hooks, 190.5 x 490.5 mm
        // (1362 sharp), measures 1330.13 exactly - three bends of R = 40/2 + 9.5/2 = 24.75, not four:
        // the bar starts and ends at its first corner, which Revit does not bend.
        [Fact]
        public void A_closed_bar_is_not_bent_at_the_corner_it_starts_and_ends_at() =>
            Assert.Equal(1362 - 1330.13, RebarAuditRules.BendShorteningMm(new List<double[]>
                { new double[] { 0, 0, 0 }, new double[] { 0, 0, -490.5 }, new double[] { 0, 190.5, -490.5 }, new double[] { 0, 190.5, 0 } },
                true, 24.75), 1);

        [Fact]
        public void A_straight_bar_has_nothing_to_shorten() =>
            Assert.Equal(0, RebarAuditRules.BendShorteningMm(new List<double[]>
                { new double[] { 0, 0, 0 }, new double[] { 1000, 0, 0 } }, false, 48), 9);
    }
}
