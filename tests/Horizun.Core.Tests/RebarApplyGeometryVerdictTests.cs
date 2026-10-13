// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// ACERO 3.0, stage 1c: the apply's re-read judges each built bar's GEOMETRY with the
// audit's own rule. MEASURED (Revit 2024, anonymized regression case COLUMN-A longitudinal built alone): a bar
// built without hold_ends that Revit moved 11.45 mm off its plan - its handles pinned
// ToCover on a neighbour's face - came back `verified: true` from the apply, and only
// horizun_audit_reinforcement reported geometry_differs. These fakes carry the shapes
// ReinforcementResolver.DescribeRebarRow and RebarFacts.Describe actually emit.
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class RebarApplyGeometryVerdictTests
    {
        private static StructuralTolerances Tol() => new StructuralTolerances { LengthMm = 2.0, SpacingMm = 2.0, CoverMm = 1.0, AngleDegrees = 1.0 };

        private static string Pts(IEnumerable<double[]> p) =>
            "[" + string.Join(",", p.Select(q => "[" + string.Join(",", q.Select(v => v.ToString("R", CultureInfo.InvariantCulture))) + "]")) + "]";

        private static string F(double v) => v.ToString("R", CultureInfo.InvariantCulture);

        private static JObject Expected(List<double[]> curve, double totalMm, string barType, double nominal, double bend,
                                        bool closed = false, bool hookStart = false, bool hookEnd = false, string style = "standard") =>
            JObject.Parse(@"{
          'rule_id': 'COLUMN-A', 'host_id': 5001,
          'bar_type': { 'id': 300, 'name': 'BT', 'nominal_diameter_mm': NOM, 'model_diameter_mm': NOM,
                        'standard_bend_diameter_mm': BEND, 'stirrup_tie_bend_diameter_mm': BEND },
          'shape': { 'declared': false, 'revit_will_match_or_create': true },
          'style': 'STYLE', 'closed': CLOSED,
          'curve_mm': CURVE,
          'expected_total_steel_length_mm': TOTAL,
          'normal': { 'x': 0.0, 'y': 1.0, 'z': 0.0 },
          'layout': { 'rule': 'single', 'number_of_bar_positions': 1, 'quantity': 1, 'array_length_mm': 0.0,
                      'include_first_bar': true, 'include_last_bar': true, 'bars_on_normal_side': true },
          'terminations': { 'start': { 'hook_type_id': HS, 'has_hook': HSB, 'orientation': 'left' },
                            'end':   { 'hook_type_id': HE, 'has_hook': HEB, 'orientation': 'left' } }
        }".Replace("NOM", F(nominal)).Replace("BEND", F(bend)).Replace("STYLE", style)
          .Replace("CLOSED", closed ? "true" : "false").Replace("CURVE", Pts(curve)).Replace("TOTAL", F(totalMm))
          .Replace("HSB", hookStart ? "true" : "false").Replace("HEB", hookEnd ? "true" : "false")
          .Replace("HS", hookStart ? "77" : "-1").Replace("HE", hookEnd ? "77" : "-1")
          .Replace("BT", barType).Replace('\'', '"'));

        private static JObject Observed(List<double[]> asDeclared, double oneBarMm, double nominal, double bend, string style = "standard") =>
            JObject.Parse(@"{
          'id': 9001, 'host': { 'id': 5001, 'resolved': true },
          'bar_type': { 'resolved': true, 'id': 300, 'nominal_diameter_mm': NOM, 'model_diameter_mm': NOM,
                        'standard_bend_diameter_mm': BEND, 'stirrup_tie_bend_diameter_mm': BEND },
          'shape': { 'id': 400, 'resolved': true }, 'style_horizun': 'STYLE',
          'layout': { 'rule_horizun': 'single', 'number_of_bar_positions': 1, 'quantity': 1,
                      'include_first_bar': true, 'include_last_bar': true, 'bars_on_normal_side': true,
                      'normal': { 'x': 0.0, 'y': 1.0, 'z': 0.0 } },
          'terminations': [ { 'end': 0, 'hook_type_id': -1, 'hook_readable': true, 'orientation': 'left' },
                            { 'end': 1, 'hook_type_id': -1, 'hook_readable': true, 'orientation': 'left' } ],
          'measured': { 'total_length_mm': ONE, 'bar_centreline_length_mm': ONE },
          'geometry': { 'centreline_points_as_declared_mm': PTS }
        }".Replace("NOM", F(nominal)).Replace("BEND", F(bend)).Replace("STYLE", style)
          .Replace("ONE", F(oneBarMm)).Replace("PTS", Pts(asDeclared)).Replace('\'', '"'));

        private static RebarConstraintRules.Summary Pinned(int toCover, params long[] others)
        {
            var handles = new List<RebarConstraintRules.Handle>();
            for (int i = 0; i < toCover; i++)
                handles.Add(new RebarConstraintRules.Handle { Name = "end" + i, ConstraintType = "ToCover", ToCover = true,
                                                              TargetElementId = i < others.Length ? others[i] : 5001 });
            handles.Add(new RebarConstraintRules.Handle { Name = "plane", ConstraintType = "FixedDistanceToHostFace", TargetElementId = 5001 });
            return RebarConstraintRules.Summarise(5001, handles);
        }

        private static List<string> Codes(JToken f) => f.OfType<JObject>().Select(o => (string)o["code"]).ToList();

        // ------------------------------------------------- the anonymized regression case bar: straight, moved at one end

        private static List<double[]> Straight() => new List<double[]> { new double[] { 0, 0, 0 }, new double[] { 4000, 0, 0 } };
        private static List<double[]> StraightMoved() => new List<double[]> { new double[] { 11.45, 0, 0 }, new double[] { 4000, 0, 0 } };

        [Fact]
        public void A_bar_Revit_pulled_11_45_mm_off_at_one_end_is_not_verified()
        {
            JObject v = RebarAuditRules.GeometryMatchesPlan(Expected(Straight(), 4000, "16M", 16, 96),
                Observed(StraightMoved(), 3988.55, 16, 96), Tol(), false, Pinned(2, 7001, 7002));
            Assert.False((bool)v["verified"]);
            Assert.Contains(RebarFinding.GeometryDiffers, Codes(v["findings"]));
            Assert.Contains(RebarFinding.LengthDiffers, Codes(v["findings"]));
            Assert.Equal(11.45, (double)v["worst_mm"], 2);
            Assert.Equal(2.0, (double)v["tolerance_mm"], 9);
            // The measured cause, named: the handles Revit holds to a cover, and hold_ends.
            Assert.Contains("hold_ends", (string)v["why"]);
            Assert.Equal(2, (int)v["evidence"]["handles_to_cover"]);
            Assert.Equal(new long[] { 7001, 7002 }, v["evidence"]["other_elements"].Select(t => (long)t).ToArray());
        }

        [Fact]
        public void The_apply_and_the_audit_say_the_same_thing_about_that_bar()
        {
            JObject want = Expected(Straight(), 4000, "16M", 16, 96), got = Observed(StraightMoved(), 3988.55, 16, 96);
            List<string> audit = Codes(RebarAuditRules.CompareBar(want, got, Tol()));
            List<string> apply = Codes(RebarAuditRules.GeometryMatchesPlan(want, got, Tol(), false, null)["findings"]);
            Assert.Contains(RebarFinding.GeometryDiffers, audit);
            Assert.All(apply, c => Assert.Contains(c, audit));
        }

        [Fact]
        public void With_hold_ends_asked_for_the_why_does_not_blame_its_absence()
        {
            JObject v = RebarAuditRules.GeometryMatchesPlan(Expected(Straight(), 4000, "16M", 16, 96),
                Observed(StraightMoved(), 3988.55, 16, 96), Tol(), true, Pinned(2, 7001, 7002));
            Assert.False((bool)v["verified"]);
            Assert.DoesNotContain("did not ask for hold_ends", (string)v["why"]);
        }

        [Fact]
        public void Without_a_constraint_reading_the_verdict_is_the_same_and_names_no_cause()
        {
            JObject v = RebarAuditRules.GeometryMatchesPlan(Expected(Straight(), 4000, "16M", 16, 96),
                Observed(StraightMoved(), 3988.55, 16, 96), Tol(), false, null);
            Assert.False((bool)v["verified"]);
            Assert.Null(v["evidence"]);
            Assert.DoesNotContain("hold_ends", (string)v["why"]);
        }

        [Fact]
        public void The_same_bar_where_it_was_planned_is_verified()
        {
            JObject v = RebarAuditRules.GeometryMatchesPlan(Expected(Straight(), 4000, "16M", 16, 96),
                Observed(Straight(), 4000, 16, 96), Tol(), false, Pinned(0));
            Assert.True((bool)v["verified"]);
            Assert.Empty(v["findings"]);
            Assert.Equal(0, (double)v["worst_mm"], 6);
        }

        // ------------------------------------------- the holdends E1 bar: a U whose legs Revit pulled 6 mm

        private static List<double[]> U(double inset) => new List<double[]>
        {
            new double[] { inset, 0, -200 }, new double[] { inset, 0, 0 },
            new double[] { 6000 - inset, 0, 0 }, new double[] { 6000 - inset, 0, -200 }
        };

        // R = 96/2 + 16/2 = 56 at each of two right angles: 2 x 56(2 - pi/2) shorter than the polyline.
        private static double UDrawnMm(double inset) => 200 + 200 + (6000 - 2 * inset) - 2 * 56 * (2 - System.Math.PI / 2);

        [Fact]
        public void A_bent_bar_whose_legs_Revit_pulled_6_mm_is_not_verified()
        {
            JObject v = RebarAuditRules.GeometryMatchesPlan(Expected(U(0), 6400, "16M", 16, 96),
                Observed(U(6), UDrawnMm(6), 16, 96), Tol(), false, Pinned(2, 7001, 7002));
            Assert.False((bool)v["verified"]);
            Assert.Contains(RebarFinding.GeometryDiffers, Codes(v["findings"]));
            Assert.Equal(6.0, (double)v["worst_mm"], 2);
            Assert.Contains("hold_ends", (string)v["why"]);
        }

        [Fact]
        public void A_bent_bar_that_matches_after_corner_rounding_is_verified()
        {
            JObject v = RebarAuditRules.GeometryMatchesPlan(Expected(U(0), 6400, "16M", 16, 96),
                Observed(U(0), UDrawnMm(0), 16, 96), Tol(), false, null);
            Assert.True((bool)v["verified"], (string)v["why"]);
            Assert.Empty(v["notes_accepted"]);
        }

        [Fact]
        public void A_bent_bar_left_at_its_sharp_polyline_length_is_not_verified() =>
            // The rounding is a prediction, not an allowance: 6400 is not what Revit draws.
            Assert.Contains(RebarFinding.LengthDiffers, Codes(RebarAuditRules.GeometryMatchesPlan(
                Expected(U(0), 6400, "16M", 16, 96), Observed(U(0), 6400, 16, 96), Tol(), false, null)["findings"]));

        // ----------------------------------------------------- the notes the audit accepts, accepted

        [Fact]
        public void A_hooked_end_carried_on_along_the_bar_is_an_accepted_note()
        {
            // MEASURED (L2): a 10M bar hooked at its start reads 4.748 mm longer there, on its own line.
            var want = new List<double[]> { new double[] { 0, 0, 0 }, new double[] { 500, 0, 0 } };
            var got = new List<double[]> { new[] { -4.748, 0, 0 }, new[] { 500.0, 0, 0 } };
            JObject v = RebarAuditRules.GeometryMatchesPlan(Expected(want, 500, "10M", 9.5, 40, hookStart: true),
                Observed(got, 640, 9.5, 40), Tol(), false, null);
            Assert.True((bool)v["verified"], (string)v["why"]);
            Assert.Equal(new[] { RebarFinding.HookEndExtension }, Codes(v["notes_accepted"]));
        }

        [Fact]
        public void A_hooked_stirrup_open_at_its_first_corner_is_an_accepted_note()
        {
            var square = new List<double[]>
            {
                new double[] { 0, -110, -110 }, new double[] { 0, 110, -110 }, new double[] { 0, 110, 110 },
                new double[] { 0, -110, 110 }, new double[] { 0, -110, -110 }
            };
            var open = new List<double[]>
            {
                new double[] { 0, -105.25, -110 }, new double[] { 0, 110, -110 }, new double[] { 0, 110, 110 },
                new double[] { 0, -110, 110 }, new double[] { 0, -110, -105.25 }
            };
            JObject v = RebarAuditRules.GeometryMatchesPlan(
                Expected(square, 880, "10M", 9.5, 40, closed: true, hookStart: true, hookEnd: true, style: "stirrup_tie"),
                Observed(open, 1000, 9.5, 40, "stirrup_tie"), Tol(), false, null);
            Assert.True((bool)v["verified"], (string)v["why"]);
            Assert.Contains(RebarFinding.HookCornerOpening, Codes(v["notes_accepted"]));
        }

        [Fact]
        public void The_same_extension_at_an_unhooked_end_is_not_verified()
        {
            var want = new List<double[]> { new double[] { 0, 0, 0 }, new double[] { 500, 0, 0 } };
            var got = new List<double[]> { new[] { -4.748, 0, 0 }, new[] { 500.0, 0, 0 } };
            JObject v = RebarAuditRules.GeometryMatchesPlan(Expected(want, 500, "10M", 9.5, 40, hookEnd: true),
                Observed(got, 640, 9.5, 40), Tol(), false, null);
            Assert.False((bool)v["verified"]);
        }

        // --------------------------------------------------------------- unknown is not a pass

        [Fact]
        public void A_bar_whose_centreline_could_not_be_read_is_not_verified()
        {
            JObject got = Observed(Straight(), 4000, 16, 96);
            got.Remove("geometry");
            JObject v = RebarAuditRules.GeometryMatchesPlan(Expected(Straight(), 4000, "16M", 16, 96), got, Tol(), false, null);
            Assert.False((bool)v["verified"]);
            Assert.Contains("Unknown is not a pass", (string)v["why"]);
        }

        [Fact]
        public void A_placed_shape_is_left_to_its_placement_check()
        {
            JObject want = Expected(Straight(), 4000, "16M", 16, 96);
            want["placement"] = new JObject { ["kind"] = "hoop" };
            JObject v = RebarAuditRules.GeometryMatchesPlan(want, Observed(StraightMoved(), 1, 16, 96), Tol(), false, null);
            Assert.Equal(JTokenType.Null, v["verified"].Type);
            Assert.False((bool)v["applicable"]);
        }

        [Fact]
        public void Every_accepted_note_is_one_the_audit_files_as_info() =>
            Assert.All(RebarAuditRules.AcceptedGeometryNotes, c => Assert.Contains(c, RebarFinding.All));
    }
}
