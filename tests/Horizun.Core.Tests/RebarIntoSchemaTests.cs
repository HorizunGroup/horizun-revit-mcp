// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// Steel that crosses from one element to another (acero 3.0, L1). A rule names
// the elements its bars run into, and the anchored length it needs in each - both
// declared, never inferred: which element a bar is anchored in, and how far, is
// a design decision with somebody's name on it.
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class RebarIntoSchemaTests
    {
        private static StructuralRequirementSet Rule(string extra)
        {
            return StructuralRequirementSet.Load(JObject.Parse(@"{
  'schema': 'horizun.structural-requirements/1', 'units': 'millimeter',
  'requirement_set': { 'id': 'l1', 'version': '1' },
  'bar_types': [ { 'id': 'N5', 'type_name': '#5' } ],
  'reinforcement_rules': [ { 'id': 'D1', 'host': { 'element_ids': [ 100 ] }, 'bar_type': 'N5', 'style': 'standard',
    'curve_mm': [ [ 150, 150, -400 ], [ 150, 150, 600 ] ], 'normal': [ 1, 0, 0 ],
    'layout': { 'rule': 'single' }, 'allow_new_shape': true" + extra + @" } ] }"));
        }

        private static StructuralRequirementSet Layer(string extend, string extra)
        {
            return StructuralRequirementSet.Load(JObject.Parse(@"{
  'schema': 'horizun.structural-requirements/1', 'units': 'millimeter',
  'requirement_set': { 'id': 'l1', 'version': '1' },
  'bar_types': [ { 'id': 'N5', 'type_name': '#5' } ],
  'section_rules': [ { 'id': 'C1', 'host': { 'element_ids': [ 7 ] }, 'cover': { 'distance_mm': 40 },
    'layers': [ { 'name': 'main', 'face': 'perimeter', 'count': 4, 'bar_type': 'N5', 'extend': " + extend + extra + @" } ] } ] }"));
        }

        // A column 3000 tall, 400 x 400, its axis up world Z from z = 0.
        private static SectionHostFrame Column() => new SectionHostFrame
        {
            ElementId = 7, OriginMm = new[] { 0.0, 0, 0 }, U = new[] { 0, 0, 1.0 }, V = new[] { 1.0, 0, 0 }, W = new[] { 0, 1.0, 0 },
            UMin = 0, UMax = 3000, VMin = -200, VMax = 200, WMin = -200, WMax = 200
        };

        private static void Refused(StructuralRequirementSet s, string code, string path)
        {
            Assert.False(s.Ok);
            Assert.Equal(code, s.Code);
            Assert.Equal(path, s.Path);
        }

        // ------------------------------------------------------- reinforcement rules

        [Fact]
        public void IntoAndAnchorageAreRead()
        {
            StructuralRequirementSet s = Rule(", 'into': [ 200, 300 ], 'anchorage': [ { 'element_id': 200, 'min_length_mm': 350 } ]");
            Assert.True(s.Ok, s.Error);
            StructuralRebarRule r = s.RebarRules.Single();
            Assert.Equal(new long[] { 200, 300 }, r.Into.ToArray());
            Assert.Equal(200, r.Anchorage.Single().ElementId);
            Assert.Equal(350, r.Anchorage.Single().MinLengthMm, 6);
        }

        [Fact]
        public void ARuleWithoutIntoHasNone()
        {
            StructuralRequirementSet s = Rule("");
            Assert.True(s.Ok, s.Error);
            Assert.Empty(s.RebarRules.Single().Into);
            Assert.Empty(s.RebarRules.Single().Anchorage);
        }

        [Fact]
        public void AnEmptyIntoIsRefused() =>
            Refused(Rule(", 'into': [ ]"), StructuralRequirementSet.CodeMissing, "reinforcement_rules['D1'].into");

        [Fact]
        public void AnIntoThatIsNotAnElementIdIsRefused() =>
            Refused(Rule(", 'into': [ 'the column' ]"), StructuralRequirementSet.CodeNotANumber, "reinforcement_rules['D1'].into[0]");

        [Fact]
        public void ARepeatedIntoIsRefused() =>
            Refused(Rule(", 'into': [ 200, 200 ]"), StructuralRequirementSet.CodeDuplicateId, "reinforcement_rules['D1'].into[1]");

        [Fact]
        public void AnAnchorageInAnElementNotInIntoIsRefused() =>
            Refused(Rule(", 'into': [ 200 ], 'anchorage': [ { 'element_id': 300, 'min_length_mm': 350 } ]"),
                StructuralRequirementSet.CodeUnresolvedReference, "reinforcement_rules['D1'].anchorage[0].element_id");

        [Fact]
        public void AnAnchorageWithoutIntoIsRefused() =>
            Refused(Rule(", 'anchorage': [ { 'element_id': 300, 'min_length_mm': 350 } ]"),
                StructuralRequirementSet.CodeUnresolvedReference, "reinforcement_rules['D1'].anchorage[0].element_id");

        [Fact]
        public void AnAnchorageOfNothingIsRefused() =>
            Refused(Rule(", 'into': [ 200 ], 'anchorage': [ { 'element_id': 200, 'min_length_mm': 0 } ]"),
                StructuralRequirementSet.CodeUnknownValue, "reinforcement_rules['D1'].anchorage[0].min_length_mm");

        [Fact]
        public void AnAnchorageWithoutALengthIsRefused() =>
            Refused(Rule(", 'into': [ 200 ], 'anchorage': [ { 'element_id': 200 } ]"),
                StructuralRequirementSet.CodeMissing, "reinforcement_rules['D1'].anchorage[0].min_length_mm");

        [Fact]
        public void AMisspeltAnchorageKeyIsRefused()
        {
            StructuralRequirementSet s = Rule(", 'into': [ 200 ], 'anchorage': [ { 'element_id': 200, 'min_lenght_mm': 350 } ]");
            Assert.Equal(StructuralRequirementSet.CodeUnknownKey, s.Code);
            Assert.Contains("min_length_mm", s.DidYouMean);
        }

        [Fact]
        public void AReinforcementRuleAnchorageNamesNoEnd() =>
            // A plain rule is ONE bar: its whole centreline is measured in the element.
            Assert.Equal(StructuralRequirementSet.CodeUnknownKey,
                Rule(", 'into': [ 200 ], 'anchorage': [ { 'element_id': 200, 'min_length_mm': 350, 'end': 'start' } ]").Code);

        // ------------------------------------------------------------ section layers

        [Fact]
        public void ALayerThatLeavesItsHostNeedsInto() =>
            Refused(Layer("{ 'from_mm': -500, 'to_mm': 3000 }", ""), StructuralRequirementSet.CodeGeometry, "section_rules['C1'].layers[0].extend");

        [Fact]
        public void ALayerDeclaredIntoTheFootingRunsBelowItsHost()
        {
            StructuralRequirementSet s = Layer("{ 'from_mm': -500, 'to_mm': 3000 }",
                ", 'into': [ 50 ], 'anchorage': [ { 'element_id': 50, 'min_length_mm': 450, 'end': 'start' } ]");
            Assert.True(s.Ok, s.Error);
            SectionResult r = SectionRules.Expand(s.SectionRules.Single(), Column(), id => 16, null,
                out List<StructuralRebarRule> bars, out _);
            Assert.True(r.Ok, r.Why);
            Assert.All(bars, b => Assert.Equal(-500, b.CurvesMm[0][2], 6));
            Assert.All(bars, b => Assert.Equal(new long[] { 50 }, b.Into.ToArray()));
            Assert.All(bars, b => Assert.Equal(50, b.Anchorage.Single().ElementId));
        }

        [Fact]
        public void AnExtentOutsideTheHostWithoutIntoIsStillRefusedAtExpansion()
        {
            // Past the top, no into: the old refusal, unchanged.
            StructuralRequirementSet s = Layer("{ 'from_mm': 0, 'to_mm': 3600 }", "");
            Assert.True(s.Ok, s.Error);
            SectionResult r = SectionRules.Expand(s.SectionRules.Single(), Column(), id => 16, null, out _, out _);
            Assert.Equal(SectionRules.CodeExtentOutside, r.Code);
        }

        [Fact]
        public void ADowelLappedWithTheColumnBarsIsTheFirstPieceAndOnlyItCarriesTheFootingAnchorage()
        {
            // The dowel: 500 into the footing and lapped 600 with the column bar, which starts at
            // the joint one diameter in. The column bar runs on into the column above.
            StructuralRequirementSet s = Layer("{ 'from_mm': -500, 'to_mm': 3700 }",
                ", 'into': [ 50, 8 ], 'laps': [ { 'at_mm': 300, 'length_mm': 600 } ]" +
                ", 'anchorage': [ { 'element_id': 50, 'min_length_mm': 450, 'end': 'start' }, " +
                "{ 'element_id': 8, 'min_length_mm': 650, 'end': 'end' } ]");
            Assert.True(s.Ok, s.Error);
            SectionResult r = SectionRules.Expand(s.SectionRules.Single(), Column(), id => 16, null,
                out List<StructuralRebarRule> bars, out _);
            Assert.True(r.Ok, r.Why);
            List<StructuralRebarRule> top = bars.Where(b => b.Id.StartsWith("C1#h7#main-top")).ToList();
            Assert.Equal(2, top.Count);
            Assert.Equal(-500, top[0].CurvesMm[0][2], 6); Assert.Equal(600, top[0].CurvesMm[1][2], 6);
            Assert.Equal(0, top[1].CurvesMm[0][2], 6); Assert.Equal(3700, top[1].CurvesMm[1][2], 6);
            Assert.Equal(new long[] { 50 }, top[0].Anchorage.Select(a => a.ElementId).ToArray());
            Assert.Equal(new long[] { 8 }, top[1].Anchorage.Select(a => a.ElementId).ToArray());
            Assert.Equal(600, top[0].LapNextMm.Value, 6);
        }

        [Fact]
        public void ALayerAnchorageNamesItsEnd() =>
            Refused(Layer("{ 'from_mm': -500, 'to_mm': 3000 }", ", 'into': [ 50 ], 'anchorage': [ { 'element_id': 50, 'min_length_mm': 450 } ]"),
                StructuralRequirementSet.CodeMissing, "section_rules['C1'].layers[0].anchorage[0].end");
    }
}
