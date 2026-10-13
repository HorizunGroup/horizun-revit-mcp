// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// The failure these protect against is an audit that reports a clean model it
// never finished looking at. Every property that cannot be read has to produce a
// finding; a quiet audit and an agreeing audit must never be the same reply.
//
// Both sides of the comparison are the JSON that ReinforcementResolver and
// RebarFacts actually emit, so these cannot drift from either.
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class RebarAuditRulesTests
    {
        private static StructuralTolerances Tol()
        {
            return new StructuralTolerances { LengthMm = 2.0, SpacingMm = 2.0, CoverMm = 1.0 };
        }

        /// <summary>The shape ReinforcementResolver.DescribeRebarRow emits.</summary>
        private static JObject Expected()
        {
            return JObject.Parse(@"{
              'rule_id': 'beam-stirrups',
              'host_id': 5001,
              'bar_type': { 'id': 300, 'name': '10M', 'nominal_diameter_mm': 10.0 },
              'shape': { 'declared': true, 'id': 400, 'name': 'T1' },
              'style': 'stirrup_tie',
              'normal': { 'x': 1.0, 'y': 0.0, 'z': 0.0 },
              'layout': { 'rule': 'maximum_spacing', 'number_of_bar_positions': 9, 'quantity': 9,
                          'array_length_mm': 3800.0, 'resulting_spacing_mm': 475.0,
                          'include_first_bar': true, 'include_last_bar': true,
                          'bars_on_normal_side': true },
              'terminations': {
                 'start': { 'hook_type_id': 700, 'has_hook': true, 'orientation': 'left' },
                 'end':   { 'hook_type_id': 700, 'has_hook': true, 'orientation': 'right' } }
            }".Replace('\'', '"'));
        }

        /// <summary>The shape RebarFacts.Describe emits.</summary>
        private static JObject Observed()
        {
            return JObject.Parse(@"{
              'id': 9001,
              'host': { 'id': 5001, 'resolved': true },
              'bar_type': { 'resolved': true, 'id': 300, 'nominal_diameter_mm': 10.0 },
              'shape': { 'id': 400, 'resolved': true },
              'style_horizun': 'stirrup_tie',
              'layout': { 'rule_horizun': 'maximum_spacing', 'number_of_bar_positions': 9, 'quantity': 9,
                          'array_length_mm': 3800.0, 'measured_pitch_mm': 475.0,
                          'include_first_bar': true, 'include_last_bar': true,
                          'bars_on_normal_side': true,
                          'normal': { 'x': 1.0, 'y': 0.0, 'z': 0.0 } },
              'terminations': [
                 { 'end': 0, 'hook_type_id': 700, 'hook_readable': true, 'orientation': 'left' },
                 { 'end': 1, 'hook_type_id': 700, 'hook_readable': true, 'orientation': 'right' } ],
              'measured': { 'schedule_mark': 'S1' }
            }".Replace('\'', '"'));
        }

        private static List<string> Codes(JArray f)
        {
            return f.OfType<JObject>().Select(o => (string)o["code"]).ToList();
        }

        // ------------------------------------------------- hook corner seat (L10)

        private static JObject SeatExpected(double? seat)
        {
            JObject e = CoupledExpected();
            e.Remove("coupled_ends");
            e["rule_id"] = "VXL#h1#top";
            e["curve_mm"] = JArray.Parse("[[0.0, 0.0, 0.0], [5920.0, 0.0, 0.0]]");
            e["expected_total_steel_length_mm"] = 11840.0;
            if (seat.HasValue) e["hook_corner_seat_mm"] = seat.Value;
            return e;
        }

        private static JObject SeatObserved(double dz)
        {
            JObject o = CoupledObserved(199000.0);
            o["terminations"] = JArray.Parse("[{ 'end': 0, 'hook_type_id': -1, 'hook_readable': true, 'has_hook': false }, { 'end': 1, 'hook_type_id': -1, 'hook_readable': true, 'has_hook': false }]".Replace('\'', '"'));
            o["geometry"] = new JObject { ["centreline_points_mm"] = new JArray(new JArray(1.22, 0.0, dz), new JArray(5921.22, 0.0, dz)) };
            o["measured"] = new JObject { ["bar_centreline_length_mm"] = 5920.0, ["total_length_mm"] = 11840.0 };
            return o;
        }

        [Fact]
        public void The_row_at_the_hooks_corner_may_seat_in_either_pass_and_says_so()
        {
            // The live case: 1.22 along and 3.77 down, 3.96 mm - inside half a 10M stirrup.
            JArray f = RebarAuditRules.CompareBar(SeatExpected(4.75), SeatObserved(-3.77), Tol());
            Assert.DoesNotContain(f.OfType<JObject>(), o => (string)o["severity"] != "info");
            Assert.Contains(RebarFinding.HookCornerSeat, Codes(f));
        }

        [Fact]
        public void A_row_without_the_seat_or_past_it_is_still_a_difference()
        {
            Assert.Contains(RebarFinding.GeometryDiffers, Codes(RebarAuditRules.CompareBar(SeatExpected(null), SeatObserved(-3.77), Tol())));
            List<string> past = Codes(RebarAuditRules.CompareBar(SeatExpected(4.75), SeatObserved(-8.0), Tol()));
            Assert.Contains(RebarFinding.GeometryDiffers, past);
            Assert.DoesNotContain(RebarFinding.HookCornerSeat, past);
        }

        // A HOOKED ROW AT THE HOOKS' CORNER (second steel duel, 2026-10-12): the column's top#p1 carries a 90
        // at its start and came out 3.964 mm off - the measured seat - and was refused, because the seat was
        // only ever accepted on the hook-free comparison.
        private static JObject HookedSeatExpected(double? seat)
        {
            JObject e = SeatExpected(seat);
            e["terminations"] = JObject.Parse("{ 'start': { 'hook_type_id': 700, 'has_hook': true, 'orientation': 'left' }, 'end': { 'hook_type_id': -1, 'has_hook': false } }".Replace('\'', '"'));
            return e;
        }

        private static JObject HookedSeatObserved(double dz)
        {
            JObject o = SeatObserved(dz);
            o["terminations"] = JArray.Parse("[{ 'end': 0, 'hook_type_id': 700, 'hook_readable': true, 'has_hook': true, 'orientation': 'left' }, { 'end': 1, 'hook_type_id': -1, 'hook_readable': true, 'has_hook': false }]".Replace('\'', '"'));
            o["geometry"] = new JObject { ["centreline_points_as_declared_mm"] = new JArray(new JArray(0.0, 0.0, dz), new JArray(5920.0, 0.0, dz)) };
            return o;
        }

        [Fact]
        public void A_hooked_row_at_the_hooks_corner_may_seat_too()
        {
            JArray f = RebarAuditRules.CompareBar(HookedSeatExpected(4.75), HookedSeatObserved(-3.964), Tol());
            Assert.DoesNotContain(f.OfType<JObject>(), o => (string)o["severity"] != "info");
            Assert.Contains(RebarFinding.HookCornerSeat, Codes(f));
        }

        [Fact]
        public void A_hooked_row_without_the_seat_or_past_it_is_still_a_difference()
        {
            Assert.Contains(RebarFinding.GeometryDiffers, Codes(RebarAuditRules.CompareBar(HookedSeatExpected(null), HookedSeatObserved(-3.964), Tol())));
            List<string> past = Codes(RebarAuditRules.CompareBar(HookedSeatExpected(4.75), HookedSeatObserved(-8.0), Tol()));
            Assert.Contains(RebarFinding.GeometryDiffers, past);
            Assert.DoesNotContain(RebarFinding.HookCornerSeat, past);
        }

        [Fact]
        public void A_row_a_crossing_order_moved_off_the_corner_no_longer_touches_it()
        {
            // V-2's top rows, moved 17.5 mm down under V-1's: off the hooks' corner, so no seat to accept.
            Assert.Null(RebarCrossing.SeatAfterMove(4.75, 17.5));
            Assert.Equal(4.75, RebarCrossing.SeatAfterMove(4.75, 0).Value, 6);
            Assert.Equal(4.75, RebarCrossing.SeatAfterMove(4.75, 3.0).Value, 6);
            Assert.Null(RebarCrossing.SeatAfterMove(null, 17.5));
        }

        // ---------------------------------------------------- coupled ends (L7)

        // The second piece of a 16M layer coupled with CPL16M at x = 199000, as Revit 2024 and 2026
        // left it (bar 422788, coupler 422801): its start 12 mm into the coupler, 2948 mm per bar.
        private static JObject CoupledExpected() => JObject.Parse(@"{
              'rule_id': 'P4#h1#bot#p2', 'host_id': 1, 'style': 'standard', 'closed': false,
              'bar_type': { 'id': 53673, 'name': '16M', 'nominal_diameter_mm': 16.0, 'model_diameter_mm': 16.0 },
              'normal': { 'x': 0.0, 'y': 1.0, 'z': 0.0 },
              'layout': { 'rule': 'fixed_number', 'number_of_bar_positions': 2, 'quantity': 2, 'array_length_mm': 204.1,
                          'include_first_bar': true, 'include_last_bar': true, 'bars_on_normal_side': true },
              'curve_mm': [[199000.0, -102.05, 2447.95], [201960.0, -102.05, 2447.95]],
              'expected_total_steel_length_mm': 5920.0,
              'coupled_ends': [0],
              'terminations': { 'start': { 'hook_type_id': -1, 'has_hook': false }, 'end': { 'hook_type_id': -1, 'has_hook': false } }
            }".Replace('\'', '"'));

        private static JObject CoupledObserved(double startX) => JObject.Parse((@"{
              'id': 422788, 'host': { 'id': 1, 'resolved': true },
              'bar_type': { 'resolved': true, 'id': 53673, 'nominal_diameter_mm': 16.0, 'model_diameter_mm': 16.0 },
              'style_horizun': 'standard',
              'layout': { 'rule_horizun': 'fixed_number', 'number_of_bar_positions': 2, 'quantity': 2, 'array_length_mm': 204.1,
                          'include_first_bar': true, 'include_last_bar': true, 'bars_on_normal_side': true,
                          'normal': { 'x': 0.0, 'y': 1.0, 'z': 0.0 } },
              'terminations': [
                 { 'end': 0, 'hook_type_id': -1, 'hook_readable': true, 'has_hook': false, 'coupler_id': 422801, 'coupler_readable': true,
                   'coupler_box_mm': { 'min': [198978.0, -113.05, 2436.95], 'max': [199034.0, 113.05, 2458.95] } },
                 { 'end': 1, 'hook_type_id': -1, 'hook_readable': true, 'has_hook': false, 'coupler_id': -1, 'coupler_readable': true } ],
              'geometry': { 'centreline_points_mm': [[START, -102.05, 2447.95], [201960.0, -102.05, 2447.95]] },
              'measured': { 'bar_centreline_length_mm': LEN, 'total_length_mm': 5900.0 }
            }").Replace("START", startX.ToString(System.Globalization.CultureInfo.InvariantCulture))
               .Replace("LEN", (201960.0 - startX).ToString(System.Globalization.CultureInfo.InvariantCulture)).Replace('\'', '"'));

        [Fact]
        public void A_coupled_end_seated_inside_the_coupler_agrees_and_says_so()
        {
            JArray f = RebarAuditRules.CompareBar(CoupledExpected(), CoupledObserved(199012.0), Tol());
            List<string> errors = f.OfType<JObject>().Where(o => (string)o["severity"] != "info").Select(o => (string)o["code"]).ToList();
            Assert.True(errors.Count == 0, string.Join(" | ", f.OfType<JObject>().Select(o => (string)o["code"] + ":" + (string)o["why"])));
            Assert.Contains(RebarFinding.CoupledEndSeated, Codes(f));
        }

        [Fact]
        public void A_coupled_end_past_the_coupler_is_still_a_difference()
        {
            List<string> codes = Codes(RebarAuditRules.CompareBar(CoupledExpected(), CoupledObserved(199060.0), Tol()));
            Assert.Contains(RebarFinding.LengthDiffers, codes);
            Assert.DoesNotContain(RebarFinding.CoupledEndSeated, codes);
        }

        // ------------------------------------------------------------- agrees

        [Fact]
        public void A_model_that_carries_what_was_asked_produces_no_findings()
        {
            JArray f = RebarAuditRules.CompareBar(Expected(), Observed(), Tol());
            Assert.Empty(f);
            Assert.Equal("agrees", (string)RebarAuditRules.Summarise(f)["verdict"]);
        }

        // ------------------------------------------------------- disagreements

        [Fact]
        public void A_different_bar_TYPE_is_a_finding_with_both_ids()
        {
            JObject o = Observed();
            o["bar_type"]["id"] = 301;
            JArray f = RebarAuditRules.CompareBar(Expected(), o, Tol());
            JObject one = f.OfType<JObject>().Single(x => (string)x["code"] == RebarFinding.TypeDiffers);
            Assert.Equal(300, (int)one["expected"]);
            Assert.Equal(301, (int)one["observed"]);
            Assert.True((bool)one["fixable"]);
            Assert.Equal("horizun_apply_reinforcement", (string)one["suggested_typed_action"]);
        }

        [Fact]
        public void ONE_BAR_MISSING_from_the_set_is_caught_by_the_count()
        {
            // The single most common real difference: somebody deleted a stirrup.
            JObject o = Observed();
            o["layout"]["quantity"] = 8;
            JArray f = RebarAuditRules.CompareBar(Expected(), o, Tol());
            JObject one = f.OfType<JObject>().First(x => (string)x["code"] == RebarFinding.QuantityDiffers);
            Assert.Equal(9, (int)one["expected"]);
            Assert.Equal(8, (int)one["observed"]);
        }

        [Fact]
        public void POSITIONS_and_BARS_are_compared_separately()
        {
            // A set with a suppressed end bar has 9 positions and 8 bars, and both
            // numbers are real. Comparing only one of them misses half the ways a
            // set can differ.
            JObject e = Expected(), o = Observed();
            o["layout"]["quantity"] = 8;
            o["layout"]["number_of_bar_positions"] = 9;
            JArray f = RebarAuditRules.CompareBar(e, o, Tol());
            Assert.Single(f.OfType<JObject>(), x => (string)x["code"] == RebarFinding.QuantityDiffers);
        }

        [Fact]
        public void A_hook_that_turns_the_other_way_is_a_finding_naming_the_END()
        {
            JObject o = Observed();
            ((JArray)o["terminations"])[1]["orientation"] = "left";
            JArray f = RebarAuditRules.CompareBar(Expected(), o, Tol());
            JObject one = f.OfType<JObject>().Single(x => (string)x["code"] == RebarFinding.OrientationDiffers);
            Assert.Contains("end end", (string)one["why"]);
            Assert.Equal("right", (string)one["expected"]);
            Assert.Equal("left", (string)one["observed"]);
        }

        [Fact]
        public void A_label_Revit_flipped_on_a_hook_DRAWN_as_declared_is_not_a_finding_and_says_so()
        {
            // MEASURED, Revit 2024 (anonymized regression case roof): a straight bar with 180 degree hooks declared 'left' at both ends
            // drew them on the left (HookSide, from the drawn centreline) and GetHookOrientation read 'right'.
            JObject o = Observed();
            ((JArray)o["terminations"])[1]["orientation"] = "left";
            ((JArray)o["terminations"])[1]["drawn_orientation"] = "right";
            JArray f = RebarAuditRules.CompareBar(Expected(), o, Tol());
            Assert.DoesNotContain(f.OfType<JObject>(), x => (string)x["code"] == RebarFinding.OrientationDiffers);
            JObject note = f.OfType<JObject>().Single(x => (string)x["code"] == RebarFinding.OrientationLabelDiffersFromDrawing);
            Assert.Equal("info", (string)note["severity"]);
            Assert.Contains("drawn", (string)note["why"]);
        }

        [Fact]
        public void A_hook_DRAWN_the_other_way_is_a_finding_whatever_the_label_says()
        {
            JObject o = Observed();
            ((JArray)o["terminations"])[1]["drawn_orientation"] = "left";   // label 'right' agrees, the drawing does not
            JArray f = RebarAuditRules.CompareBar(Expected(), o, Tol());
            JObject one = f.OfType<JObject>().Single(x => (string)x["code"] == RebarFinding.OrientationDiffers);
            Assert.Equal("right", (string)one["expected"]);
            Assert.Contains("drawn", (string)one["why"]);
        }

        [Fact]
        public void A_drawing_that_could_not_be_measured_leaves_the_label_to_decide()
        {
            JObject o = Observed();
            ((JArray)o["terminations"])[1]["orientation"] = "left";
            ((JArray)o["terminations"])[1]["drawn_orientation"] = null;
            JArray f = RebarAuditRules.CompareBar(Expected(), o, Tol());
            Assert.Single(f.OfType<JObject>(), x => (string)x["code"] == RebarFinding.OrientationDiffers);
        }

        [Fact]
        public void A_single_bar_has_no_array_length_and_no_side_to_report()
        {
            // MEASURED live 2026-10-08 (a column's one intermediate bar per face): Revit will not
            // report an array length or a side for a set of one, and the audit filed both as
            // `unreadable` - a correct bar reported as partly audited.
            JObject e = Expected(), o = Observed();
            e["layout"] = JObject.Parse("{ 'rule': 'single', 'number_of_bar_positions': 1, 'quantity': 1, 'array_length_mm': 0, 'bars_on_normal_side': true }".Replace('\'', '"'));
            o["layout"] = JObject.Parse("{ 'rule_horizun': 'single', 'number_of_bar_positions': 1, 'quantity': 1, 'normal': { 'x': 1.0, 'y': 0.0, 'z': 0.0 } }".Replace('\'', '"'));
            JArray f = RebarAuditRules.CompareBar(e, o, Tol());
            Assert.DoesNotContain("unreadable", Codes(f));
            Assert.DoesNotContain(RebarFinding.SideDiffers, Codes(f));
        }

        [Fact]
        public void A_bar_built_under_an_expanded_rule_id_is_not_an_orphan()
        {
            // Zones, mat components and section layers are rules of the set under expanded ids.
            HashSet<string> ids = RebarAuditRules.KnownRuleIds(new[] { "B1" }, new[] { "Z1#start", "V12#h501#top", null });
            Assert.Contains("B1", ids);
            Assert.Contains("Z1#start", ids);
            Assert.Contains("V12#h501#top", ids);
            Assert.DoesNotContain("V12#h999#top", ids);
        }

        [Fact]
        public void An_end_without_a_hook_has_no_orientation_to_disagree_about()
        {
            // MEASURED live 2026-10-08 (section rules): straight 16M bars declared with no hook
            // read back 'right' at both ends against the default 'left', and the audit reported
            // two orientation findings per set about a hook that does not exist.
            JObject e = Expected(), o = Observed();
            foreach (string w in new[] { "start", "end" })
            {
                e["terminations"][w]["hook_type_id"] = null;
                e["terminations"][w]["has_hook"] = false;
                e["terminations"][w]["orientation"] = "left";
            }
            foreach (JObject t in ((JArray)o["terminations"]).OfType<JObject>())
            {
                t["hook_type_id"] = -1;
                t["orientation"] = "right";
            }
            JArray f = RebarAuditRules.CompareBar(e, o, Tol());
            Assert.DoesNotContain(RebarFinding.OrientationDiffers, Codes(f));
            Assert.DoesNotContain(RebarFinding.HookDiffers, Codes(f));
        }

        [Fact]
        public void An_array_length_INSIDE_the_tolerance_is_not_a_finding()
        {
            JObject o = Observed();
            o["layout"]["array_length_mm"] = 3801.5;
            Assert.Empty(RebarAuditRules.CompareBar(Expected(), o, Tol()));
        }

        [Fact]
        public void An_array_length_OUTSIDE_the_tolerance_is_one_and_says_what_it_allowed()
        {
            JObject o = Observed();
            o["layout"]["array_length_mm"] = 3810.0;
            JObject one = RebarAuditRules.CompareBar(Expected(), o, Tol())
                .OfType<JObject>().Single(x => (string)x["code"] == RebarFinding.ArrayLengthDiffers);
            // The plan here declares no allowance, so the bound collapses to
            // equality and the finding says so: nothing short is permitted, plus
            // the length tolerance.
            Assert.Equal("0 mm short, plus 2 mm", (string)one["tolerance"]);
        }

        // -------------------------------- the array length is a BOUND, not equality
        //
        // Revit lays a set out over somewhere between the declared array length and
        // one MODEL bar diameter less than it, and eleven live measurements across
        // Revit 2023 and 2026 found no rule that says which. Comparing for equality
        // raised a finding on every correctly built array whose bar was thicker
        // than the tolerance - which is every real bar.

        [Fact]
        public void An_array_a_whole_model_diameter_short_is_NOT_a_finding_when_the_plan_allows_it()
        {
            JObject e = Expected();
            e["layout"]["array_length_shortfall_allowed_mm"] = 12.0;
            JObject o = Observed();
            o["layout"]["array_length_mm"] = 3788.0;      // declared 3800, a 12 mm bar
            Assert.DoesNotContain(RebarFinding.ArrayLengthDiffers,
                                  Codes(RebarAuditRules.CompareBar(e, o, Tol())));
        }

        [Fact]
        public void An_array_shorter_than_the_allowance_still_is_a_finding()
        {
            JObject e = Expected();
            e["layout"]["array_length_shortfall_allowed_mm"] = 12.0;
            JObject o = Observed();
            o["layout"]["array_length_mm"] = 3780.0;      // 20 short against 12 allowed + 2
            JObject one = RebarAuditRules.CompareBar(e, o, Tol())
                .OfType<JObject>().Single(x => (string)x["code"] == RebarFinding.ArrayLengthDiffers);
            Assert.Equal("12 mm short, plus 2 mm", (string)one["tolerance"]);
        }

        [Fact]
        public void An_array_LONGER_than_declared_is_a_finding_even_with_an_allowance()
        {
            // The allowance is one-sided on purpose. Nothing measured has ever
            // produced an array longer than the declaration, so it is not a case
            // this understands - and an unknown is not a pass.
            JObject e = Expected();
            e["layout"]["array_length_shortfall_allowed_mm"] = 12.0;
            JObject o = Observed();
            o["layout"]["array_length_mm"] = 3815.0;
            Assert.Contains(RebarFinding.ArrayLengthDiffers,
                            Codes(RebarAuditRules.CompareBar(e, o, Tol())));
        }

        [Fact]
        public void A_bar_hosted_by_something_ELSE_is_caught()
        {
            JObject o = Observed();
            o["host"]["id"] = 5099;
            JArray f = RebarAuditRules.CompareBar(Expected(), o, Tol());
            Assert.Contains(RebarFinding.HostMissing, Codes(f));
        }

        [Fact]
        public void A_bar_whose_host_does_not_RESOLVE_is_caught_too()
        {
            JObject o = Observed();
            o["host"]["resolved"] = false;
            JArray f = RebarAuditRules.CompareBar(Expected(), o, Tol());
            JObject one = f.OfType<JObject>().Single(x => (string)x["code"] == RebarFinding.HostMissing);
            Assert.False((bool)one["fixable"]);
        }

        // ----------------------------------------------- unknown is not a pass

        [Fact]
        public void A_property_that_could_not_be_READ_produces_a_finding_not_silence()
        {
            JObject o = Observed();
            o["layout"]["quantity"] = null;
            JArray f = RebarAuditRules.CompareBar(Expected(), o, Tol());
            JObject one = f.OfType<JObject>().First(x => (string)x["code"] == RebarFinding.Unreadable);
            Assert.Equal(RebarSeverity.Unknown, (string)one["severity"]);
            Assert.Equal(RebarFinding.QuantityDiffers, (string)one["about"]);
            Assert.Contains("UNKNOWN IS NOT A PASS", (string)one["why"]);
        }

        [Fact]
        public void An_unreadable_ORIENTATION_is_unknown_rather_than_a_difference()
        {
            JObject o = Observed();
            ((JArray)o["terminations"])[0]["orientation"] = null;
            JArray f = RebarAuditRules.CompareBar(Expected(), o, Tol());
            Assert.Contains(RebarFinding.Unreadable, Codes(f));
            Assert.DoesNotContain(RebarFinding.OrientationDiffers, Codes(f));
        }

        [Fact]
        public void A_model_with_NOTHING_WRONG_and_something_unread_is_INCOMPLETE_not_clean()
        {
            // The whole reason the verdict is not a boolean.
            JObject o = Observed();
            o["layout"]["array_length_mm"] = null;
            JObject s = RebarAuditRules.Summarise(RebarAuditRules.CompareBar(Expected(), o, Tol()));
            Assert.Equal("incomplete", (string)s["verdict"]);
            Assert.Equal(0, (int)s["errors"]);
            Assert.Equal(1, (int)s["unknown"]);
            Assert.Contains("partly audited", (string)s["verdict_means"]);
        }

        [Fact]
        public void The_summary_names_what_this_bridge_does_NOT_check()
        {
            // A gap nobody wrote down is indistinguishable from a gap nobody found.
            JObject s = RebarAuditRules.Summarise(new JArray());
            var not = ((JArray)s["not_checked"]).Select(t => (string)t).ToList();
            // F2.1: a lap a section rule DECLARES is measured; one nobody declared is not.
            Assert.DoesNotContain("lap_insufficient", not);
            Assert.Contains("undeclared_lap", not);
            // L7: a coupler a section rule declares is checked on both bars; one Revit
            // judged incompatible is still not.
            Assert.DoesNotContain("missing_coupler", not);
            Assert.Contains("coupler_incompatible", not);
            // acero 3.0: bars of different rules ARE measured against each other (overlapping_bar), and the
            // same bar built twice is looked for too (duplicate_bar, stage 1c) - against bars no rule built as
            // well. Whether those unattributed bars CROSS the set's is still not judged by the audit.
            Assert.DoesNotContain("overlapping_bar", not);
            Assert.DoesNotContain("duplicate_bar", not);
            Assert.Contains("overlap_with_unattributed_bar", not);
        }

        [Fact]
        public void A_SINGLE_bar_is_not_asked_about_a_first_or_last_bar_it_cannot_have()
        {
            // MEASURED LIVE, 2026-08-28: Revit THROWS for IncludeFirstBar,
            // IncludeLastBar and BarsOnNormalSide on a single-bar set - there is no
            // first, no last and no side for an array of one. Reading that as
            // `unreadable` reported every correct single-bar rule as INCOMPLETE: a
            // partly audited model, for three questions that never arose.
            JObject e = Expected(), o = Observed();
            e["layout"]["rule"] = "single";
            e["layout"]["quantity"] = 1;
            e["layout"]["number_of_bar_positions"] = 1;
            e["layout"]["array_length_mm"] = 0.0;
            e["layout"]["resulting_spacing_mm"] = null;
            o["layout"]["rule_horizun"] = "single";
            o["layout"]["quantity"] = 1;
            o["layout"]["number_of_bar_positions"] = 1;
            o["layout"]["array_length_mm"] = 0.0;
            o["layout"]["include_first_bar"] = null;
            o["layout"]["include_last_bar"] = null;
            // single has no pitch and no array, on either side.
            e["layout"]["resulting_spacing_mm"] = null;
            o["layout"]["measured_pitch_mm"] = null;

            JArray f = RebarAuditRules.CompareBar(e, o, Tol());
            Assert.Empty(f);
            Assert.Equal("agrees", (string)RebarAuditRules.Summarise(f)["verdict"]);
        }

        [Fact]
        public void But_a_DISTRIBUTED_set_that_will_not_report_those_flags_is_still_unknown()
        {
            // The exemption is about the question not arising, not about the flag
            // being awkward to read. On any layout that has an array, silence is
            // still silence.
            JObject o = Observed();
            o["layout"]["include_first_bar"] = null;
            JArray f = RebarAuditRules.CompareBar(Expected(), o, Tol());
            Assert.Contains(RebarFinding.Unreadable, Codes(f));
        }

        // ---------------------------------------------------------- provenance

        [Fact]
        public void A_bar_with_NO_provenance_is_reported_and_is_not_an_error()
        {
            // A bar somebody modelled by hand carries none. That is a fact about
            // attribution, not a fault in the model.
            JArray f = RebarAuditRules.CheckProvenance(
                new JObject { ["written"] = false }, "r", 9001, "set", "abc");
            JObject one = f.OfType<JObject>().Single();
            Assert.Equal(RebarFinding.ProvenanceMissing, (string)one["code"]);
            Assert.Equal(RebarSeverity.Info, (string)one["severity"]);
        }

        [Fact]
        public void A_bar_built_from_an_OLDER_version_of_the_set_is_reported_not_judged()
        {
            JArray f = RebarAuditRules.CheckProvenance(
                new JObject { ["written"] = true, ["requirement_set_sha256"] = "old" }, "r", 9001, "set", "new");
            JObject one = f.OfType<JObject>().Single();
            Assert.Equal(RebarFinding.StaleRequirementSet, (string)one["code"]);
            Assert.Equal(RebarSeverity.Info, (string)one["severity"]);
            Assert.Contains("reported rather than judged", (string)one["why"]);
        }

        [Fact]
        public void A_bar_from_the_SAME_version_produces_nothing()
        {
            Assert.Empty(RebarAuditRules.CheckProvenance(
                new JObject { ["written"] = true, ["requirement_set_sha256"] = "same" }, "r", 9001, "set", "same"));
        }

        // --------------------------------------------------------------- marks

        // Marks are pinned in RebarMarkConflictTests: one mark on identical sets is correct,
        // one mark on different bars is bar_mark_conflict.

        // ------------------------------------------------------------ vocabulary

        [Fact]
        public void Every_finding_code_is_in_the_published_list()
        {
            var produced = new List<string>();
            JObject o = Observed();
            o["bar_type"]["id"] = 999;
            o["layout"]["quantity"] = 8;
            o["layout"]["rule_horizun"] = "fixed_number";
            o["host"]["id"] = 1;
            ((JArray)o["terminations"])[0]["hook_type_id"] = 0;
            produced.AddRange(Codes(RebarAuditRules.CompareBar(Expected(), o, Tol())));
            produced.AddRange(Codes(RebarAuditRules.CheckProvenance(new JObject { ["written"] = false }, "r", 1, "s", "h")));
            Assert.NotEmpty(produced);
            foreach (string c in produced) Assert.Contains(c, RebarFinding.All);
        }

        [Fact]
        public void Every_finding_carries_its_evidence()
        {
            JObject o = Observed();
            o["layout"]["quantity"] = 8;
            foreach (JObject f in RebarAuditRules.CompareBar(Expected(), o, Tol()).OfType<JObject>())
            {
                Assert.NotNull(f["code"]);
                Assert.NotNull(f["severity"]);
                Assert.NotNull(f["tolerance"]);
                Assert.False(string.IsNullOrWhiteSpace((string)f["why"]));
                Assert.NotNull(f["expected"]);
            }
        }
    }
}
