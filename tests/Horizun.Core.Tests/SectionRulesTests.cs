// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// Section rules (refuerzo desde cartilla, F1.1): bars declared relative to the
// host's section expand into ordinary reinforcement rules in model coordinates,
// for a horizontal beam, one turned 30 degrees in plan and one inclined 10
// degrees; a host that is not a rectangular prism, or whose cover cannot be read,
// is refused by name. Frames are synthetic; their shape is the one the resolver
// builds (origin, U along, V = W x U, W up in the section, extents in mm).
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class SectionRulesTests
    {
        private const double D16 = 16, D10 = 10;

        private static double Dia(string id) => id == "N5" ? D16 : id == "N3" ? D10 : 0;

        private static SectionHostFrame Frame(double[] u, double[] w, long id = 501)
        {
            double[] v = { w[1] * u[2] - w[2] * u[1], w[2] * u[0] - w[0] * u[2], w[0] * u[1] - w[1] * u[0] };
            return new SectionHostFrame
            {
                ElementId = id, OriginMm = new[] { 1000.0, 2000.0, 3000.0 }, U = u, V = v, W = w,
                UMin = 0, UMax = 6000, VMin = -150, VMax = 150, WMin = -500, WMax = 0
            };
        }

        private static StructuralRequirementSet Set(string sectionJson)
        {
            return StructuralRequirementSet.Load(JObject.Parse(@"{
  'schema': 'horizun.structural-requirements/1', 'units': 'millimeter',
  'requirement_set': { 'id': 'cartilla', 'version': '1' },
  'bar_types': [ { 'id': 'N5', 'type_name': '#5' }, { 'id': 'N3', 'type_name': '#3' } ],
  'hook_types': [ { 'id': 'H135', 'type_name': 'Stirrup/Tie - 135 deg.' } ],
  'section_rules': [ " + sectionJson + @" ] }"));
        }

        private const string Beam = @"{
  'id': 'V12', 'host': { 'category': 'OST_StructuralFraming', 'mark': 'V-12' },
  'cover': { 'distance_mm': 40 },
  'layers': [
    { 'face': 'top', 'count': 3, 'bar_type': 'N5', 'extend': 'host_length' },
    { 'face': 'bottom', 'count': 2, 'bar_type': 'N5', 'extend': 'host_length' } ],
  'stirrups': { 'bar_type': 'N3', 'start': { 'hook_type': 'H135' }, 'end': { 'hook_type': 'H135' },
    'zones': [ { 'name': 'all', 'layout': { 'rule': 'maximum_spacing', 'spacing_mm': 150 } } ] },
  'mark': 'V-12' }";

        private static void Close(double[] want, double[] got)
        {
            for (int i = 0; i < 3; i++) Assert.Equal(want[i], got[i], 6);
        }

        [Fact]
        public void A_beam_section_parses_with_its_stirrups_read_as_a_zone_rule()
        {
            StructuralRequirementSet s = Set(Beam);
            Assert.True(s.Ok, s.Error);
            StructuralSectionRule r = s.SectionRules.Single();
            Assert.Equal("V-12", r.Host.Mark);
            Assert.Equal(40, r.Cover.CommonMm);
            Assert.Equal(2, r.Layers.Count);
            Assert.NotNull(r.Stirrups);
            Assert.Equal("all", r.Stirrups.Zones.Single().Name);
        }

        [Fact]
        public void A_horizontal_beam_expands_into_top_and_bottom_rows_and_a_stirrup_run()
        {
            StructuralSectionRule r = Set(Beam).SectionRules.Single();
            SectionResult res = SectionRules.Expand(r, Frame(new[] { 1.0, 0, 0 }, new[] { 0, 0, 1.0 }), Dia, null,
                                                    out List<StructuralRebarRule> bars, out StructuralStirrupZoneRule z);
            Assert.True(res.Ok, res.Why);
            Assert.Equal(2, bars.Count);

            StructuralRebarRule top = bars.Single(b => b.Id == "V12#h501#top");
            // w = 0 - 40 - 10 - 8 = -58; v from -150 + 58 = -92 to 92; u from 40 to 5960
            Close(new[] { 1040.0, 2000 - 92, 3000 - 58 }, top.CurvesMm[0]);
            Close(new[] { 6960.0, 2000 - 92, 3000 - 58 }, top.CurvesMm[1]);
            Close(new[] { 0, 1.0, 0 }, top.NormalMm);
            Assert.Equal(RebarLayout.FixedNumber, top.Layout.Layout);
            Assert.Equal(3, top.Layout.Number);
            Assert.Equal(184, top.Layout.ArrayLengthMm.Value, 6);
            Assert.Equal(new long[] { 501 }, top.Host.ElementIds);
            Assert.Equal("V-12", top.Mark);

            StructuralRebarRule bottom = bars.Single(b => b.Id == "V12#h501#bottom");
            Assert.Equal(3000 - 500 + 58, bottom.CurvesMm[0][2], 6);

            Assert.Equal("V12#h501#stirrups", z.Id);
            Assert.Equal(6000, z.SpanMm.Value, 6);
            Close(new[] { 1.0, 0, 0 }, z.AlongMm);
            // centreline: 40 + 5 inside each face
            Close(new[] { 1000.0, 2000 - 105, 3000 - 45 }, z.ProfileMm[0]);    // starts top-left: the hooks go at the top
            Close(new[] { 1000.0, 2000 + 105, 3000 - 455 }, z.ProfileMm[2]);
            Assert.Equal("H135", z.Start.HookTypeId);
        }

        [Fact]
        public void Corner_bars_are_planned_in_the_stirrup_bends_where_Revit_puts_them()
        {
            // MEASURED live 2026-10-08 (Revit 2026, 300 x 600 beam at z 2400..3000, cover 40,
            // 16M rows in a 10M stirrup with a 40 mm stirrup bend): top bars 60.979 mm in
            // from the faces once the hooks are at the bottom, bottom bars 65.729 from the
            // bottom and 60.979 from the sides. With the hooks at the TOP the two swap.
            Assert.Equal(3.529, SectionRules.CornerInsetMm(40, 15.9), 3);
            StructuralSectionRule r = Set(Beam).SectionRules.Single();
            SectionHostFrame f = Frame(new[] { 1.0, 0, 0 }, new[] { 0, 0, 1.0 });
            Assert.True(SectionRules.Expand(r, f, id => id == "N5" ? 15.9 : 9.5, id => id == "N3" ? 40.0 : 0.0, null,
                                            out List<StructuralRebarRule> bars, out _).Ok);
            StructuralRebarRule top = bars.Single(b => b.Id.EndsWith("#top"));
            StructuralRebarRule bottom = bars.Single(b => b.Id.EndsWith("#bottom"));
            Assert.Equal(3000 - 65.729, top.CurvesMm[0][2], 3);       // the hooks' corner: half a stirrup further in
            Assert.Equal(3000 - 500 + 60.979, bottom.CurvesMm[0][2], 3);
            Assert.Equal(2000 - 150 + 60.979, bottom.CurvesMm[0][1], 3);
            Assert.Equal(178.041, bottom.Layout.ArrayLengthMm.Value, 3);
        }

        [Fact]
        public void The_row_at_the_hooks_corner_carries_half_a_stirrup_of_seat_and_no_other_row_does()
        {
            // MEASURED, Revit 2024 and 2026 (acero L5 W1, L10 XL): which leg the hooks' second pass lies
            // along is Revit's tie-break - identical beams in one model came out either way by position -
            // and the corner bar seats in that pass's bend: 0 or 3.964 mm off the plan. Only the row the
            // hooks' corner touches carries the allowance, and it is half a stirrup.
            StructuralRequirementSet s = Set(Beam);
            StructuralSectionRule r = s.SectionRules[0];
            Assert.True(SectionRules.Expand(r, Frame(new[] { 1.0, 0, 0 }, new[] { 0, 0, 1.0 }), id => id == "N5" ? 15.9 : 9.5,
                id => id == "N3" ? 40.0 : 0.0, null, out List<StructuralRebarRule> bars, out _).Ok);
            Assert.Equal(4.75, bars.Single(b => b.Id.EndsWith("#top")).HookCornerSeatMm.Value, 6);
            Assert.Null(bars.Single(b => b.Id.EndsWith("#bottom")).HookCornerSeatMm);
        }

        [Fact]
        public void A_beam_turned_30_degrees_in_plan_keeps_its_rows_in_its_own_section()
        {
            double c = Math.Cos(Math.PI / 6), sn = Math.Sin(Math.PI / 6);
            StructuralSectionRule r = Set(Beam).SectionRules.Single();
            SectionHostFrame f = Frame(new[] { c, sn, 0 }, new[] { 0, 0, 1.0 });
            Assert.True(SectionRules.Expand(r, f, Dia, null, out List<StructuralRebarRule> bars, out _).Ok);
            StructuralRebarRule top = bars.Single(b => b.Id.EndsWith("#top"));
            Close(f.At(40, -92, -58), top.CurvesMm[0]);
            Close(f.At(5960, -92, -58), top.CurvesMm[1]);
            Close(new[] { -sn, c, 0 }, top.NormalMm);   // across the beam, not along model Y
        }

        [Fact]
        public void A_beam_inclined_10_degrees_runs_its_bars_on_the_slope()
        {
            double a = 10 * Math.PI / 180;
            StructuralSectionRule r = Set(Beam).SectionRules.Single();
            SectionHostFrame f = Frame(new[] { Math.Cos(a), 0, Math.Sin(a) }, new[] { -Math.Sin(a), 0, Math.Cos(a) });
            Assert.True(SectionRules.Expand(r, f, Dia, null, out List<StructuralRebarRule> bars, out StructuralStirrupZoneRule z).Ok);
            StructuralRebarRule top = bars.Single(b => b.Id.EndsWith("#top"));
            double[] d = { top.CurvesMm[1][0] - top.CurvesMm[0][0], top.CurvesMm[1][1] - top.CurvesMm[0][1], top.CurvesMm[1][2] - top.CurvesMm[0][2] };
            double len = Math.Sqrt(d[0] * d[0] + d[1] * d[1] + d[2] * d[2]);
            Assert.Equal(5920, len, 6);
            Assert.Equal(Math.Tan(a), d[2] / d[0], 9);   // on the slope
            Close(new[] { Math.Cos(a), 0, Math.Sin(a) }, z.AlongMm);
        }

        [Fact]
        public void A_section_that_is_not_a_rectangular_prism_is_refused_by_name()
        {
            SectionHostFrame f = Frame(new[] { 1.0, 0, 0 }, new[] { 0, 0, 1.0 });
            f.NotRectangular = "solid volume 0.71 of its bounding box";
            SectionResult res = SectionRules.Expand(Set(Beam).SectionRules.Single(), f, Dia, null, out _, out _);
            Assert.Equal(SectionRules.CodeNotRectangular, res.Code);
        }

        [Fact]
        public void A_host_cover_that_cannot_be_read_is_refused_not_taken_as_zero()
        {
            StructuralSectionRule r = Set(Beam.Replace("{ 'distance_mm': 40 }", "{ 'source': 'host' }")).SectionRules.Single();
            SectionResult res = SectionRules.Expand(r, Frame(new[] { 1.0, 0, 0 }, new[] { 0, 0, 1.0 }), Dia, null, out _, out _);
            Assert.Equal(SectionRules.CodeNoCover, res.Code);
            Assert.True(SectionRules.Expand(r, Frame(new[] { 1.0, 0, 0 }, new[] { 0, 0, 1.0 }), Dia, 40.0, out _, out _).Ok);
        }

        [Fact]
        public void Perimeter_is_four_corners_and_nothing_else()
        {
            string four = Beam.Replace(@"{ 'face': 'top', 'count': 3, 'bar_type': 'N5', 'extend': 'host_length' },
    { 'face': 'bottom', 'count': 2, 'bar_type': 'N5', 'extend': 'host_length' }",
                "{ 'face': 'perimeter', 'count': 4, 'bar_type': 'N5', 'extend': 'host_length' }");
            Assert.True(SectionRules.Expand(Set(four).SectionRules.Single(), Frame(new[] { 1.0, 0, 0 }, new[] { 0, 0, 1.0 }), Dia, null,
                                            out List<StructuralRebarRule> bars, out _).Ok);
            Assert.Equal(new[] { "V12#h501#perimeter-bottom", "V12#h501#perimeter-top" }, bars.Select(b => b.Id).OrderBy(x => x));
            SectionResult six = SectionRules.Expand(Set(four.Replace("'count': 4", "'count': 6")).SectionRules.Single(),
                                                    Frame(new[] { 1.0, 0, 0 }, new[] { 0, 0, 1.0 }), Dia, null, out _, out _);
            Assert.Equal(SectionRules.CodePerimeterCount, six.Code);
        }

        [Fact]
        public void Clear_span_is_refused_until_supports_are_read()
        {
            string cs = Beam.Replace("'count': 2, 'bar_type': 'N5', 'extend': 'host_length'", "'count': 2, 'bar_type': 'N5', 'extend': 'clear_span'");
            SectionResult res = SectionRules.Expand(Set(cs).SectionRules.Single(), Frame(new[] { 1.0, 0, 0 }, new[] { 0, 0, 1.0 }), Dia, null, out _, out _);
            Assert.Equal(SectionRules.CodeClearSpan, res.Code);
        }

        [Fact]
        public void Side_bars_sit_between_the_top_and_bottom_rows()
        {
            string side = Beam.Replace("'layers': [", "'layers': [ { 'face': 'left', 'count': 1, 'bar_type': 'N5', 'extend': 'host_length' },");
            Assert.True(SectionRules.Expand(Set(side).SectionRules.Single(), Frame(new[] { 1.0, 0, 0 }, new[] { 0, 0, 1.0 }), Dia, null,
                                            out List<StructuralRebarRule> bars, out _).Ok);
            StructuralRebarRule left = bars.Single(b => b.Id.EndsWith("#left"));
            Assert.Equal(RebarLayout.Single, left.Layout.Layout);
            Assert.Equal(3000 - 250, left.CurvesMm[0][2], 6);   // midway between -442 and -58
            Assert.Equal(2000 - 92, left.CurvesMm[0][1], 6);
        }

        [Fact]
        public void The_cover_takes_one_form_only()
        {
            StructuralRequirementSet both = Set(Beam.Replace("{ 'distance_mm': 40 }", "{ 'distance_mm': 40, 'source': 'host' }"));
            Assert.False(both.Ok);
            StructuralRequirementSet partial = Set(Beam.Replace("{ 'distance_mm': 40 }", "{ 'top_mm': 40, 'bottom_mm': 40 }"));
            Assert.False(partial.Ok);
            Assert.Contains("2 of the five faces", partial.Error);
        }

        [Fact]
        public void Two_rows_on_one_face_need_two_names()
        {
            string twice = Beam.Replace("'face': 'bottom'", "'face': 'top'");
            StructuralRequirementSet s = Set(twice);
            Assert.False(s.Ok);
            Assert.Equal(StructuralRequirementSet.CodeDuplicateId, s.Code);
        }

        [Fact]
        public void An_unknown_face_is_refused_with_the_vocabulary()
        {
            StructuralRequirementSet s = Set(Beam.Replace("'face': 'bottom'", "'face': 'underside'"));
            Assert.False(s.Ok);
            Assert.Contains("top, bottom, left, right, perimeter", s.Error);
        }
    }
}
