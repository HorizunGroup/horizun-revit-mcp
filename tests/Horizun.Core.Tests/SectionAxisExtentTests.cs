// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// EXTENTS FROM A NAMED AXIS (stage 1c): a schedule card measures its bars and its
// stirrup zones from its own axis labels, and says nothing about where the host's
// start face is. A section rule can now say the same - "from axis B, 0..1200 mm
// toward C" - and the plan places it by finding grids B and C in the model and
// where the host's axis crosses them. Every name the plan cannot place, and every
// host the axes do not bound, is refused by name; nothing is placed on a guess.
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class SectionAxisExtentTests
    {
        private static StructuralRequirementSet Set(string layers, string stirrups = null)
        {
            return StructuralRequirementSet.Load(JObject.Parse(@"{
  'schema': 'horizun.structural-requirements/1', 'units': 'millimeter',
  'requirement_set': { 'id': 'ax', 'version': '1' },
  'bar_types': [ { 'id': 'N5', 'type_name': '#5' }, { 'id': 'N3', 'type_name': '#3' } ],
  'hook_types': [ { 'id': 'H90', 'type_name': 'Standard - 90 deg.' } ],
  'section_rules': [ { 'id': 'V1', 'host': { 'element_ids': [ 7 ] }, 'cover': { 'distance_mm': 40 },
    'layers': [ " + layers + @" ]" + (stirrups != null ? ", 'stirrups': " + stirrups : "") + @" } ] }"));
        }

        private const string FromB = "{ 'name': 'bottom', 'face': 'bottom', 'count': 2, 'bar_type': 'N5', 'start': { 'hook_type': 'H90' }, " +
                                     "'extend': { 'axis': 'B', 'toward': 'C', 'from_mm': 200, 'to_mm': 2200 } }";

        // A beam 300 x 500 along world X, its solid from x = 150 to x = 5850 (between column faces);
        // grid B crosses its axis at x = 0 and grid C at x = 6000 - or the other way round.
        private static SectionHostFrame Beam(double bAt = 0, double cAt = 6000) => new SectionHostFrame
        {
            ElementId = 7, OriginMm = new[] { 0.0, 0, 0 }, U = new[] { 1.0, 0, 0 }, V = new[] { 0, 1.0, 0 }, W = new[] { 0, 0, 1.0 },
            UMin = 150, UMax = 5850, VMin = -150, VMax = 150, WMin = -250, WMax = 250,
            AxisStations = new Dictionary<string, double> { ["B"] = bAt, ["C"] = cAt }
        };

        private static SectionResult Expand(StructuralRequirementSet s, SectionHostFrame f, out List<StructuralRebarRule> bars, out List<StructuralStirrupZoneRule> zones)
        {
            Assert.True(s.Ok, s.Error);
            return SectionRules.ExpandAll(s.SectionRules.Single(), f, id => id == "N3" ? 9.5 : 16, null, null, out bars, out zones);
        }

        [Fact]
        public void A_layer_runs_between_stations_measured_from_a_named_axis_toward_another()
        {
            SectionResult r = Expand(Set(FromB), Beam(), out List<StructuralRebarRule> bars, out _);
            Assert.True(r.Ok, r.Why);
            Assert.All(bars, b => Assert.Equal(200, b.CurvesMm[0][0], 6));
            Assert.All(bars, b => Assert.Equal(2200, b.CurvesMm[b.CurvesMm.Count - 1][0], 6));
            Assert.All(bars, b => Assert.Equal("H90", b.Start.HookTypeId));
            Assert.All(bars, b => Assert.Null(b.End.HookTypeId));
        }

        [Fact]
        public void When_the_axis_lies_at_the_host_s_far_end_the_bar_is_laid_the_other_way_with_its_start_hook_there()
        {
            // Grid B at x = 6000, C at x = 0: 0..2000 from B toward C is x = 4000..6000 - past the face.
            SectionResult outside = Expand(Set(FromB.Replace("'from_mm': 200, 'to_mm': 2200", "'from_mm': 0, 'to_mm': 2000")), Beam(6000, 0), out _, out _);
            Assert.Equal(SectionRules.CodeExtentOutside, outside.Code);

            SectionResult r = Expand(Set(FromB.Replace("'from_mm': 200, 'to_mm': 2200", "'from_mm': 300, 'to_mm': 2300")), Beam(6000, 0),
                                     out List<StructuralRebarRule> bars, out _);
            Assert.True(r.Ok, r.Why);
            Assert.All(bars, b => Assert.Equal(3700, b.CurvesMm[0][0], 6));
            Assert.All(bars, b => Assert.Equal(5700, b.CurvesMm[b.CurvesMm.Count - 1][0], 6));
            Assert.All(bars, b => Assert.Null(b.Start.HookTypeId));
            Assert.All(bars, b => Assert.Equal("H90", b.End.HookTypeId));
        }

        [Fact]
        public void Stirrups_from_a_named_axis_set_the_zones_offsets_and_turn_with_the_axes()
        {
            const string st = "{ 'bar_type': 'N3', 'extent': { 'axis': 'B', 'toward': 'C', 'from_mm': 200, 'to_mm': 5800 }, 'zones': [ " +
                              "{ 'name': 'z1', 'length_mm': 800, 'layout': { 'rule': 'maximum_spacing', 'spacing_mm': 80, 'include_last_bar': false } }, " +
                              "{ 'name': 'z2', 'layout': { 'rule': 'maximum_spacing', 'spacing_mm': 200 } } ] }";
            SectionResult r = Expand(Set(FromB, st), Beam(), out _, out List<StructuralStirrupZoneRule> zones);
            Assert.True(r.Ok, r.Why);
            StructuralStirrupZoneRule z = zones.Single();
            Assert.Equal(50, z.StartOffsetMm, 6);    // 200 from B is 50 past the face at 150
            Assert.Equal(50, z.EndOffsetMm, 6);
            Assert.Equal(new[] { "z1", "z2" }, z.Zones.Select(q => q.Name));

            SectionResult turned = Expand(Set(FromB.Replace("'from_mm': 200, 'to_mm': 2200", "'from_mm': 300, 'to_mm': 2300"), st), Beam(6000, 0),
                                          out _, out List<StructuralStirrupZoneRule> tz);
            Assert.True(turned.Ok, turned.Why);
            Assert.Equal(new[] { "z2", "z1" }, tz.Single().Zones.Select(q => q.Name));
            Assert.False(tz.Single().Zones[1].Layout.IncludeFirstBar);   // z1's last bar is now its first
            Assert.True(tz.Single().Zones[1].Layout.IncludeLastBar);
        }

        [Fact]
        public void An_axis_the_plan_could_not_place_refuses_the_rule_by_its_code()
        {
            SectionHostFrame f = Beam();
            f.AxisStations.Remove("C");
            f.AxisProblems["C"] = SectionRules.CodeAxisNotUnique + ": 2 grids are named 'C'.";
            Assert.Equal(SectionRules.CodeAxisNotUnique, Expand(Set(FromB), f, out _, out _).Code);

            SectionHostFrame g = Beam();
            g.AxisStations.Remove("C");
            Assert.Equal(SectionRules.CodeAxisNotInModel, Expand(Set(FromB), g, out _, out _).Code);
        }

        [Fact]
        public void A_host_the_two_axes_do_not_bound_is_refused()
        {
            // The card's span is B..C, and this host is the NEXT span's beam: 7000..12000.
            Assert.Equal(SectionRules.CodeHostNotBetweenAxes, Expand(Set(FromB), Beam(-13000, -7000), out _, out _).Code);
            Assert.Equal(SectionRules.CodeAxesCoincide, Expand(Set(FromB), Beam(3000, 3000.2), out _, out _).Code);
        }

        [Fact]
        public void A_member_of_two_spans_modelled_as_two_hosts_takes_in_each_the_steel_that_lies_in_it()
        {
            // One mark, two beams: B..C is 0..6000 and C..D 6000..12000 along the same axis line.
            const string layers = "{ 'name': 'b1', 'face': 'bottom', 'count': 2, 'bar_type': 'N5', 'extend': { 'axis': 'B', 'toward': 'D', 'from_mm': 300, 'to_mm': 5700 } }, " +
                                  "{ 'name': 'b2', 'face': 'bottom', 'count': 2, 'bar_type': 'N5', 'extend': { 'axis': 'B', 'toward': 'D', 'from_mm': 6300, 'to_mm': 11700 } }";
            const string st = "{ 'bar_type': 'N3', 'extent': { 'axis': 'B', 'toward': 'D', 'from_mm': 200, 'to_mm': 5800 }, " +
                              "'zones': [ { 'name': 'z', 'layout': { 'rule': 'maximum_spacing', 'spacing_mm': 200 } } ] }";
            StructuralRequirementSet s = Set(layers, st);
            Assert.True(s.Ok, s.Error);
            SectionHostFrame first = Beam(), second = Beam();
            first.AxisStations = new Dictionary<string, double> { ["B"] = 0, ["D"] = 12000 };
            second.ElementId = 8; second.UMin = 6150; second.UMax = 11850;
            second.AxisStations = new Dictionary<string, double> { ["B"] = 0, ["D"] = 12000 };

            SectionResult r1 = SectionRules.ExpandAll(s.SectionRules.Single(), first, id => id == "N3" ? 9.5 : 16, null, null, out List<StructuralRebarRule> bars1, out List<StructuralStirrupZoneRule> zones1);
            Assert.True(r1.Ok, r1.Why);
            Assert.All(bars1, b => Assert.Contains("#b1", b.Id));
            Assert.Single(zones1);
            Assert.Equal(new[] { "layer 'b2'" }, r1.NotHere);

            SectionResult r2 = SectionRules.ExpandAll(s.SectionRules.Single(), second, id => id == "N3" ? 9.5 : 16, null, null, out List<StructuralRebarRule> bars2, out List<StructuralStirrupZoneRule> zones2);
            Assert.True(r2.Ok, r2.Why);
            Assert.All(bars2, b => Assert.Contains("#b2", b.Id));
            Assert.Empty(zones2);
        }

        [Fact]
        public void A_host_none_of_the_rule_lands_on_gets_nothing_and_is_told_why()
        {
            // Another span's beam of the same mark: the rule is that beam's neighbour's, not a refusal of
            // this one. The resolver refuses a rule that lands on NO host of its mark.
            SectionHostFrame f = Beam();
            f.UMin = 6150; f.UMax = 11850;
            f.AxisStations = new Dictionary<string, double> { ["B"] = 0, ["C"] = 12000 };
            SectionResult r = Expand(Set(FromB), f, out List<StructuralRebarRule> bars, out List<StructuralStirrupZoneRule> zones);
            Assert.True(r.Ok, r.Why);
            Assert.Empty(bars);
            Assert.Empty(zones);
            Assert.Equal(new[] { "layer 'bottom'" }, r.NotHere);
        }

        [Fact]
        public void A_grid_s_station_is_where_the_host_s_axis_crosses_it_in_plan()
        {
            // A sloped beam rising 1 in 4 along +X, and a grid at x = 3000 drawn short of it.
            double s = 1 / System.Math.Sqrt(1 + 0.0625);
            double? t = SectionRules.AxisStation(new[] { 0.0, 0, 0 }, new[] { s, 0, 0.25 * s }, new[] { 3000.0, 500 }, new[] { 3000.0, 900 }, "B", out string p);
            Assert.Null(p);
            Assert.Equal(3000 / s, t.Value, 6);
            Assert.Null(SectionRules.AxisStation(new[] { 0.0, 0, 0 }, new[] { 1.0, 0, 0 }, new[] { 0.0, 100 }, new[] { 5000.0, 100 }, "A", out p));
            Assert.StartsWith(SectionRules.CodeAxisParallel, p);
        }

        [Fact]
        public void A_lap_on_an_axis_layer_is_a_station_from_that_axis_and_turns_with_it()
        {
            string lapped = FromB.Replace("'start':", "'laps': [ { 'at_mm': 900, 'length_mm': 600 } ], 'start':");
            SectionResult r = Expand(Set(lapped), Beam(), out List<StructuralRebarRule> bars, out _);
            Assert.True(r.Ok, r.Why);
            Assert.Equal(2, bars.Count);
            Assert.Equal(new[] { 200.0, 1200 }, new[] { bars[0].CurvesMm[0][0], bars[0].CurvesMm[1][0] });
            Assert.Equal(new[] { 600.0, 2200 }, new[] { bars[1].CurvesMm[0][0], bars[1].CurvesMm[1][0] });
            Assert.Equal("H90", bars[0].Start.HookTypeId);

            // B at the far end: 300..2300 from B is x = 3700..5700, the lap at 900 from B is x = 5100.
            SectionResult t = Expand(Set(lapped.Replace("'from_mm': 200, 'to_mm': 2200", "'from_mm': 300, 'to_mm': 2300")), Beam(6000, 0),
                                     out List<StructuralRebarRule> turned, out _);
            Assert.True(t.Ok, t.Why);
            Assert.Equal(new[] { 3700.0, 5400 }, new[] { turned[0].CurvesMm[0][0], turned[0].CurvesMm[1][0] });
            Assert.Equal(new[] { 4800.0, 5700 }, new[] { turned[1].CurvesMm[0][0], turned[1].CurvesMm[1][0] });
            Assert.Equal("H90", turned[1].End.HookTypeId);
        }

        // ---- BARS BETWEEN CONTINUOUS BARS (layer.between) ----------------------------------------
        private const string Three = "{ 'name': 'top', 'face': 'top', 'count': 3, 'bar_type': 'N5', 'extend': { 'from_mm': 1000, 'to_mm': 4000 } }";

        private static string Mid(int count, double from = 1500, double to = 3000, string between = "top", string face = "top") =>
            "{ 'name': 'mid', 'face': '" + face + "', 'count': " + count + ", 'bar_type': 'N5', 'between': '" + between + "', " +
            "'extend': { 'from_mm': " + from + ", 'to_mm': " + to + " } }";

        [Fact]
        public void A_between_layer_lays_one_bar_in_each_gap_of_the_layer_it_names()
        {
            SectionResult r = Expand(Set(Three + ", " + Mid(2)), Beam(), out List<StructuralRebarRule> bars, out _);
            Assert.True(r.Ok, r.Why);
            StructuralRebarRule top = bars.Single(b => b.Id.Contains("#top")), mid = bars.Single(b => b.Id.Contains("#mid"));
            // No stirrup: the bar centres run from -102 to 102 across (cover 40, half a 16 mm bar), step 102.
            Assert.Equal(-102, top.CurvesMm[0][1], 6);
            Assert.Equal(-51, mid.CurvesMm[0][1], 6);
            Assert.Equal(102, mid.Layout.ArrayLengthMm.Value, 6);
            Assert.Equal(2, mid.Layout.Number);
            Assert.Equal(top.CurvesMm[0][2], mid.CurvesMm[0][2], 6);
            Assert.Equal(150 + 1500, mid.CurvesMm[0][0], 6);   // from_mm is from the start face at x = 150
            // Without between the two layers stand in each other.
            Assert.Equal(SectionRules.CodeLayersCoincide, Expand(Set(Three + ", " + Mid(2).Replace("'between': 'top', ", "")), Beam(), out _, out _).Code);
        }

        [Fact]
        public void A_between_layer_that_does_not_fill_the_gaps_one_bar_each_is_refused_by_name()
        {
            Assert.Equal(SectionRules.CodeBetweenCount, Expand(Set(Three + ", " + Mid(3)), Beam(), out _, out _).Code);
            Assert.Equal(SectionRules.CodeBetweenOutside, Expand(Set(Three + ", " + Mid(2, 500, 2000)), Beam(), out _, out _).Code);
            string tight = Three.Replace("'count': 3", "'count': 9");
            Assert.Equal(SectionRules.CodeBetweenNoRoom, Expand(Set(tight + ", " + Mid(8)), Beam(), out _, out _).Code);
        }

        [Fact]
        public void A_between_layer_names_an_earlier_layer_on_its_own_face()
        {
            Assert.False(Set(Mid(2) + ", " + Three).Ok);                                       // named before it is declared
            Assert.False(Set(Three + ", " + Mid(2, between: "nowhere")).Ok);
            Assert.False(Set(Three + ", " + Mid(2, face: "bottom")).Ok);                       // another face
            Assert.False(Set(Three.Replace("'top'", "'left'") + ", " + Mid(2, between: "left", face: "left")).Ok);   // sides are spread, not gapped
            Assert.False(Set(Three + ", " + Mid(2).Replace("'between'", "'laps': [ { 'at_mm': 2000, 'length_mm': 300 } ], 'between'")).Ok);
        }

        [Fact]
        public void An_axis_extent_names_two_different_axes_and_runs_forward()
        {
            Assert.False(Set(FromB.Replace("'toward': 'C'", "'toward': 'B'")).Ok);
            Assert.False(Set(FromB.Replace("'to_mm': 2200", "'to_mm': 50")).Ok);
            // A lap on an axis layer is a station from that axis (round 3); a share of the host's length is not.
            StructuralRequirementSet ratio = Set(FromB.Replace("'start':", "'laps': [ { 'at_ratio': 0.5, 'length_mm': 600 } ], 'start':"));
            Assert.False(ratio.Ok);
            Assert.Contains("at_ratio", ratio.Error);
            StructuralRequirementSet cranked = Set(FromB.Replace("'start':", "'crank': { 'at_mm': 900, 'run_mm': 100, 'inward_mm': 20 }, 'start':"));
            Assert.False(cranked.Ok);
            Assert.Contains("crank", cranked.Error);
            Assert.False(Set(FromB, "{ 'bar_type': 'N3', 'start_offset_mm': 50, 'extent': { 'axis': 'B', 'toward': 'C', 'from_mm': 200, 'to_mm': 5800 }, " +
                                   "'zones': [ { 'name': 'z', 'layout': { 'rule': 'maximum_spacing', 'spacing_mm': 200 } } ] }").Ok);
        }
    }
}
