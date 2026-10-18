// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// Bastones (refuerzo desde cartilla, F2.2): extra bars laid from one end of a
// member, a declared run in mm or a share of the member's measured length.
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class SectionBastonTests
    {
        private static StructuralRequirementSet Set(string extend)
        {
            return StructuralRequirementSet.Load(JObject.Parse(@"{
  'schema': 'horizun.structural-requirements/1', 'units': 'millimeter',
  'requirement_set': { 'id': 'cartilla', 'version': '1' },
  'bar_types': [ { 'id': 'N5', 'type_name': '#5' } ],
  'section_rules': [ { 'id': 'V1', 'host': { 'mark': 'V-1' }, 'cover': { 'distance_mm': 40 },
    'layers': [ { 'name': 'top-left', 'face': 'top', 'count': 2, 'bar_type': 'N5', 'extend': " + extend + @" } ] } ] }"));
        }

        private static SectionHostFrame Frame() => new SectionHostFrame
        {
            ElementId = 7, OriginMm = new[] { 0.0, 0, 0 }, U = new[] { 1.0, 0, 0 }, V = new[] { 0, 1.0, 0 }, W = new[] { 0, 0, 1.0 },
            UMin = 0, UMax = 6000, VMin = -150, VMax = 150, WMin = -500, WMax = 0
        };

        [Fact]
        public void A_baston_from_the_start_runs_a_quarter_of_the_member()
        {
            StructuralRequirementSet s = Set("{ 'from': 'start', 'run_ratio': 0.25 }");
            Assert.True(s.Ok, s.Error);
            Assert.True(SectionRules.Expand(s.SectionRules.Single(), Frame(), id => 16, null, out List<StructuralRebarRule> bars, out _).Ok);
            Assert.Equal(40, bars[0].CurvesMm[0][0], 6);
            Assert.Equal(40 + 1500, bars[0].CurvesMm[1][0], 6);
        }

        [Fact]
        public void A_baston_from_the_end_runs_back_a_declared_length()
        {
            StructuralRequirementSet s = Set("{ 'from': 'end', 'run_mm': 2000 }");
            Assert.True(SectionRules.Expand(s.SectionRules.Single(), Frame(), id => 16, null, out List<StructuralRebarRule> bars, out _).Ok);
            Assert.Equal(5960 - 2000, bars[0].CurvesMm[0][0], 6);
            Assert.Equal(5960, bars[0].CurvesMm[1][0], 6);
        }

        [Fact]
        public void A_baston_longer_than_the_member_is_refused()
        {
            StructuralRequirementSet s = Set("{ 'from': 'start', 'run_mm': 7000 }");
            SectionResult r = SectionRules.Expand(s.SectionRules.Single(), Frame(), id => 16, null, out _, out _);
            Assert.Equal(SectionRules.CodeExtentOutside, r.Code);
        }

        [Fact]
        public void A_perimeter_baston_keeps_its_run_on_every_face()
        {
            StructuralRequirementSet s = StructuralRequirementSet.Load(JObject.Parse(@"{
  'schema': 'horizun.structural-requirements/1', 'units': 'millimeter',
  'requirement_set': { 'id': 'cartilla', 'version': '1' },
  'bar_types': [ { 'id': 'N5', 'type_name': '#5' } ],
  'section_rules': [ { 'id': 'V1', 'host': { 'mark': 'V-1' }, 'cover': { 'distance_mm': 40 },
    'layers': [ { 'name': 'corners', 'face': 'perimeter', 'count': 4, 'bar_type': 'N5',
                  'extend': { 'from': 'end', 'run_mm': 1000 } } ] } ] }"));
            Assert.True(s.Ok, s.Error);
            Assert.True(SectionRules.Expand(s.SectionRules.Single(), Frame(), id => 16, null, out List<StructuralRebarRule> bars, out _).Ok);
            Assert.Equal(2, bars.Count);
            foreach (StructuralRebarRule b in bars)
            {
                Assert.Equal(5960 - 1000, b.CurvesMm[0][0], 6);
                Assert.Equal(5960, b.CurvesMm[1][0], 6);
            }
        }

        private static StructuralRequirementSet TwoTopLayers(string first, string second) =>
            StructuralRequirementSet.Load(JObject.Parse(@"{
  'schema': 'horizun.structural-requirements/1', 'units': 'millimeter',
  'requirement_set': { 'id': 'cartilla', 'version': '1' },
  'bar_types': [ { 'id': 'N5', 'type_name': '#5' } ],
  'section_rules': [ { 'id': 'V1', 'host': { 'mark': 'V-1' }, 'cover': { 'distance_mm': 40 },
    'layers': [ { 'name': 'a', 'face': 'top', 'count': 2, 'bar_type': 'N5', 'extend': " + first + @" },
                { 'name': 'b', 'face': 'top', 'count': 2, 'bar_type': 'N5', 'extend': " + second + @" } ] } ] }"));

        [Fact]
        public void Two_layers_on_one_face_over_the_same_stretch_are_refused()
        {
            // Both are spread across the same width: the bars would stand in each other.
            StructuralRequirementSet s = TwoTopLayers("'host_length'", "{ 'from': 'start', 'run_ratio': 0.25 }");
            Assert.True(s.Ok, s.Error);
            SectionResult r = SectionRules.Expand(s.SectionRules.Single(), Frame(), id => 16, null, out _, out _);
            Assert.Equal(SectionRules.CodeLayersCoincide, r.Code);
        }

        [Fact]
        public void A_middle_bar_beside_a_corner_pair_on_one_face_is_laid()
        {
            // 4#5 + 4#4 in a square column: the pair stands at the corners, the single bar at the
            // middle of the face - they share the face and the stretch, never a station.
            StructuralRequirementSet s = StructuralRequirementSet.Load(JObject.Parse(@"{
  'schema': 'horizun.structural-requirements/1', 'units': 'millimeter',
  'requirement_set': { 'id': 'cartilla', 'version': '1' },
  'bar_types': [ { 'id': 'N5', 'type_name': '#5' }, { 'id': 'N4', 'type_name': '#4' } ],
  'section_rules': [ { 'id': 'C1', 'host': { 'mark': 'C-1' }, 'cover': { 'distance_mm': 40 },
    'layers': [ { 'name': 'top', 'face': 'top', 'count': 2, 'bar_type': 'N5', 'extend': 'host_length' },
                { 'name': 'top-m', 'face': 'top', 'count': 1, 'bar_type': 'N4', 'extend': 'host_length' } ] } ] }"));
            Assert.True(s.Ok, s.Error);
            SectionResult r = SectionRules.Expand(s.SectionRules.Single(), Frame(), id => 16, null, out List<StructuralRebarRule> bars, out _);
            Assert.True(r.Ok, r.Code + " " + r.Why);
            StructuralRebarRule mid = bars.Single(b => b.Id.Contains("top-m"));
            Assert.Equal(0, mid.CurvesMm[0][1], 6);   // the middle of a face 300 wide centred on v = 0
        }

        [Fact]
        public void Bastones_from_each_end_share_a_face()
        {
            StructuralRequirementSet s = TwoTopLayers("{ 'from': 'start', 'run_ratio': 0.25 }", "{ 'from': 'end', 'run_ratio': 0.25 }");
            Assert.True(SectionRules.Expand(s.SectionRules.Single(), Frame(), id => 16, null, out List<StructuralRebarRule> bars, out _).Ok);
            Assert.Equal(2, bars.Count);
        }

        [Fact]
        public void A_baston_states_one_run_exactly()
        {
            Assert.False(Set("{ 'from': 'start' }").Ok);
            Assert.False(Set("{ 'from': 'start', 'run_mm': 1000, 'run_ratio': 0.2 }").Ok);
            Assert.False(Set("{ 'from': 'middle', 'run_mm': 1000 }").Ok);
            Assert.False(Set("{ 'from': 'start', 'run_ratio': 1.5 }").Ok);
            Assert.False(Set("{ 'from': 'start', 'run_mm': 1000, 'to_mm': 2000 }").Ok);
        }
    }
}
