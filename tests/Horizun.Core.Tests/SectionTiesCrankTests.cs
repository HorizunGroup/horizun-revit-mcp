// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// Acero 3.0, L2: crossties across a column section, laid at the stirrups' stations
// beside them, one per intermediate bar of a named layer; and a bayoneta (crank) at
// a section change: the bar runs straight, then moves a declared distance inward
// over a declared run, and carries on.
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class SectionTiesCrankTests
    {
        private static StructuralRequirementSet Set(string layers, string extra)
        {
            return StructuralRequirementSet.Load(JObject.Parse(@"{
  'schema': 'horizun.structural-requirements/1', 'units': 'millimeter',
  'requirement_set': { 'id': 'l2', 'version': '1' },
  'bar_types': [ { 'id': 'N16', 'type_name': '16M' }, { 'id': 'N10', 'type_name': '10M' } ],
  'hook_types': [ { 'id': 'H135', 'type_name': 'Stirrup/Tie - 135 deg.' }, { 'id': 'H90', 'type_name': 'Stirrup/Tie - 90 deg.' } ],
  'section_rules': [ { 'id': 'C1', 'host': { 'element_ids': [ 7 ] }, 'cover': { 'distance_mm': 40 },
    'layers': " + layers + extra + @" } ] }"));
        }

        private const string Stirrups = @", 'stirrups': { 'bar_type': 'N10', 'allow_new_shape': true,
      'start': { 'hook_type': 'H135' }, 'end': { 'hook_type': 'H135' },
      'zones': [ { 'name': 'all', 'layout': { 'rule': 'maximum_spacing', 'spacing_mm': 150 } } ] }";

        private const string Layers = @"[ { 'face': 'perimeter', 'count': 4, 'bar_type': 'N16', 'extend': 'host_length', 'allow_new_shape': true },
      { 'name': 'sides', 'face': 'left', 'count': 2, 'bar_type': 'N16', 'extend': 'host_length', 'allow_new_shape': true } ]";

        // A column 3000 tall, 600 wide (V) by 400 deep (W).
        private static SectionHostFrame Column() => new SectionHostFrame
        {
            ElementId = 7, OriginMm = new[] { 0.0, 0, 0 }, U = new[] { 0, 0, 1.0 }, V = new[] { 1.0, 0, 0 }, W = new[] { 0, 1.0, 0 },
            UMin = 0, UMax = 3000, VMin = -300, VMax = 300, WMin = -200, WMax = 200
        };

        private static SectionResult Expand(StructuralRequirementSet s, out List<StructuralRebarRule> bars, out List<StructuralStirrupZoneRule> zones)
        {
            Assert.True(s.Ok, s.Error);
            return SectionRules.ExpandAll(s.SectionRules.Single(), Column(), id => id == "N10" ? 10 : 16, null, null, out bars, out zones);
        }

        // ------------------------------------------------------------------ ties

        [Fact]
        public void A_tie_crosses_the_width_at_every_intermediate_bar_of_its_layer()
        {
            SectionResult r = Expand(Set(Layers, Stirrups + @", 'ties': [ { 'name': 'x', 'bar_type': 'N10', 'layer': 'sides',
                'start': { 'hook_type': 'H135' }, 'end': { 'hook_type': 'H90' } } ]"), out _, out List<StructuralStirrupZoneRule> zones);
            Assert.True(r.Ok, r.Why);
            List<StructuralStirrupZoneRule> ties = zones.Where(z => z.Id.Contains("#tie-x-")).ToList();
            Assert.Equal(2, ties.Count);
            foreach (StructuralStirrupZoneRule t in ties)
            {
                Assert.False(t.Closed);
                Assert.Equal(2, t.ProfileMm.Count);
                // From one stirrup leg to the other: v = -300+40+5 to 300-40-5.
                Assert.Equal(-255, t.ProfileMm[0][0], 6); Assert.Equal(255, t.ProfileMm[1][0], 6);
                // Beside the stirrup, not in its plane: half a stirrup and half a tie further along.
                Assert.Equal(10, t.ProfileMm[0][2], 6);
                Assert.Equal("N10", t.BarTypeId);
                Assert.Equal("H135", t.Start.HookTypeId); Assert.Equal("H90", t.End.HookTypeId);
                Assert.Equal(new[] { 0.0, 0, 1.0 }, t.AlongMm);
            }
            // At the two intermediate bars of 'sides', not at the corners.
            double[] ws = ties.Select(t => t.ProfileMm[0][1]).OrderBy(x => x).ToArray();
            Assert.True(ws[0] > -200 + 40 + 10 + 8 + 1 && ws[1] < 200 - 40 - 10 - 8 - 1);
        }

        [Fact]
        public void Ties_need_stirrups_whose_zones_they_follow() =>
            Assert.Equal(StructuralRequirementSet.CodeMissing,
                Set(Layers, @", 'ties': [ { 'name': 'x', 'bar_type': 'N10', 'layer': 'sides' } ]").Code);

        [Fact]
        public void A_tie_on_a_layer_the_rule_does_not_have_is_refused() =>
            Assert.Equal(StructuralRequirementSet.CodeUnresolvedReference,
                Set(Layers, Stirrups + @", 'ties': [ { 'name': 'x', 'bar_type': 'N10', 'layer': 'nope' } ]").Code);

        [Fact]
        public void A_tie_on_the_perimeter_is_refused_it_has_no_intermediate_bar() =>
            Assert.Equal(StructuralRequirementSet.CodeUnknownValue,
                Set(@"[ { 'name': 'p', 'face': 'perimeter', 'count': 4, 'bar_type': 'N16', 'extend': 'host_length' } ]",
                    Stirrups + @", 'ties': [ { 'name': 'x', 'bar_type': 'N10', 'layer': 'p' } ]").Code);

        // ------------------------------------------- cover below the host's (L4)

        // MEASURED (Revit 2024, acero 3.0 L4): joists whose common cover is 40 mm, reinforced by a
        // section rule declaring 25 - the stirrup array came out 30 mm short (Revit clamps it to the
        // HOST's cover) and the bottom bars 26 to 30 mm off. A declared cover below the host's
        // cannot happen in the model, so it is refused before any write.
        [Fact]
        public void A_declared_cover_below_the_hosts_is_refused_with_both_numbers()
        {
            StructuralRequirementSet s = Set(Layers, Stirrups);
            Assert.True(s.Ok, s.Error);
            string why = SectionRules.CoverBelowHost(s.SectionRules.Single(), 50);
            Assert.NotNull(why);
            Assert.Contains("40", why); Assert.Contains("50", why);
        }

        [Fact]
        public void A_declared_cover_at_or_above_the_hosts_is_kept()
        {
            StructuralRequirementSet s = Set(Layers, Stirrups);
            Assert.Null(SectionRules.CoverBelowHost(s.SectionRules.Single(), 40));
            Assert.Null(SectionRules.CoverBelowHost(s.SectionRules.Single(), 25));
            Assert.Null(SectionRules.CoverBelowHost(s.SectionRules.Single(), null));
        }

        // ----------------------------------------------------------------- crank

        [Fact]
        public void A_crank_moves_the_bar_inward_over_its_run_and_keeps_it_there()
        {
            SectionResult r = Expand(Set(@"[ { 'name': 'top', 'face': 'top', 'count': 3, 'bar_type': 'N16', 'allow_new_shape': true,
                'extend': 'host_length', 'crank': { 'at_mm': 2500, 'run_mm': 300, 'inward_mm': 50 } } ]", ""),
                out List<StructuralRebarRule> bars, out _);
            Assert.True(r.Ok, r.Why);
            StructuralRebarRule b = bars.Single();
            Assert.Equal(4, b.CurvesMm.Count);
            double w = b.CurvesMm[0][1];
            Assert.Equal(2500, b.CurvesMm[1][2], 6); Assert.Equal(w, b.CurvesMm[1][1], 6);
            Assert.Equal(2800, b.CurvesMm[2][2], 6); Assert.Equal(w - 50, b.CurvesMm[2][1], 6);
            Assert.Equal(2960, b.CurvesMm[3][2], 6); Assert.Equal(w - 50, b.CurvesMm[3][1], 6);
            // Across the width nothing moves, so the set still marches along V.
            Assert.All(b.CurvesMm, p => Assert.Equal(b.CurvesMm[0][0], p[0], 6));
        }

        [Fact]
        public void A_crank_outside_the_bar_is_refused()
        {
            SectionResult r = Expand(Set(@"[ { 'name': 'top', 'face': 'top', 'count': 3, 'bar_type': 'N16',
                'extend': 'host_length', 'crank': { 'at_mm': 2800, 'run_mm': 300, 'inward_mm': 50 } } ]", ""), out _, out _);
            Assert.Equal(SectionRules.CodeCrankOutside, r.Code);
        }

        [Fact]
        public void A_crank_with_laps_is_refused_by_name()
        {
            SectionResult r = Expand(Set(@"[ { 'name': 'top', 'face': 'top', 'count': 3, 'bar_type': 'N16', 'extend': 'host_length',
                'crank': { 'at_mm': 2500, 'run_mm': 300, 'inward_mm': 50 }, 'laps': [ { 'at_mm': 1000, 'length_mm': 600 } ] } ]", ""), out _, out _);
            Assert.Equal(SectionRules.CodeCrankWithLaps, r.Code);
        }

        [Fact]
        public void A_crank_with_a_missing_value_is_refused() =>
            Assert.Equal(StructuralRequirementSet.CodeMissing,
                Set(@"[ { 'name': 'top', 'face': 'top', 'count': 3, 'bar_type': 'N16', 'extend': 'host_length',
                    'crank': { 'at_mm': 2500, 'run_mm': 300 } } ]", "").Code);
    }
}
