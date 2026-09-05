// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// The two blocks that let a zone know its cover and a mat know its openings
// are read the way everything else in the requirement set is read: every key
// admitted by name, every word from a closed list, every number declared and
// never defaulted, and a value a policy would not use refused rather than
// dropped. These pin that, and that a set without either block parses exactly
// as it did before the blocks existed.
// -----------------------------------------------------------------------------
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class StructuralRequirementSetCoverAndOpeningsTests
    {
        private const string Head = @"{
  'schema': 'horizun.structural-requirements/1',
  'requirement_set': { 'id': 'beams', 'version': '1.0.0' },
  'units': 'millimeter',
  'bar_types': [ { 'id': 'T10', 'type_name': '10M', 'nominal_diameter_mm': 10 } ],";

        private static StructuralRequirementSet Load(string body)
        {
            return StructuralRequirementSet.Load(JObject.Parse((Head + body + "}").Replace('\'', '"')));
        }

        private static string ZoneRule(string extra)
        {
            return @"
  'stirrup_zone_rules': [ {
      'id': 'B1',
      'host': { 'element_ids': [1001] },
      'bar_type': 'T10',
      'allow_new_shape': true,
      'profile_mm': [[0,0,0],[0,300,0],[0,300,500],[0,0,500]],
      'along': [1,0,0],
      'span': 'host_length'," + extra + @"
      'zones': [
        { 'name': 'start', 'length_mm': 1000, 'layout': { 'rule': 'maximum_spacing', 'spacing_mm': 100 } },
        { 'name': 'middle', 'layout': { 'rule': 'maximum_spacing', 'spacing_mm': 200 } }
      ]
  } ]";
        }

        private static string MatRule(string extra)
        {
            return @"
  'mat_rules': [ {
      'id': 'S1',
      'host': { 'category': 'OST_Floors' },
      'face_normal': [0,0,1]," + extra + @"
      'components': [
        { 'name': 'top_x', 'bar_type': 'T10', 'direction': [1,0,0], 'offset_from_face_mm': 30,
          'allow_new_shape': true, 'layout': { 'rule': 'maximum_spacing', 'spacing_mm': 200 } }
      ]
  } ]";
        }

        // ---------------------------------------------------------- cover

        [Fact]
        public void AZoneRuleWithoutACoverBlockHasNoCover()
        {
            StructuralRequirementSet s = Load(ZoneRule(""));
            Assert.True(s.Ok, s.Error);
            Assert.Null(s.StirrupZoneRules[0].Cover);
        }

        [Fact]
        public void HostSourceIsReadWithNoDistance()
        {
            StructuralRequirementSet s = Load(ZoneRule("'cover': { 'source': 'host' },"));
            Assert.True(s.Ok, s.Error);
            Assert.Equal("host", s.StirrupZoneRules[0].Cover.Source);
            Assert.Null(s.StirrupZoneRules[0].Cover.DistanceMm);
        }

        [Fact]
        public void DeclaredSourceCarriesItsDistance()
        {
            StructuralRequirementSet s = Load(ZoneRule("'cover': { 'source': 'declared', 'distance_mm': 40 },"));
            Assert.True(s.Ok, s.Error);
            Assert.Equal("declared", s.StirrupZoneRules[0].Cover.Source);
            Assert.Equal(40, s.StirrupZoneRules[0].Cover.DistanceMm.Value);
        }

        [Fact]
        public void ZeroIsALegalDeclaredCover()
        {
            StructuralRequirementSet s = Load(ZoneRule("'cover': { 'source': 'declared', 'distance_mm': 0 },"));
            Assert.True(s.Ok, s.Error);
            Assert.Equal(0, s.StirrupZoneRules[0].Cover.DistanceMm.Value);
        }

        [Fact]
        public void DeclaredWithoutADistanceIsRefusedAtTheField()
        {
            StructuralRequirementSet s = Load(ZoneRule("'cover': { 'source': 'declared' },"));
            Assert.False(s.Ok);
            Assert.Equal(StructuralRequirementSet.CodeMissing, s.Code);
            Assert.Equal("stirrup_zone_rules['B1'].cover.distance_mm", s.Path);
            Assert.Empty(s.StirrupZoneRules);
        }

        [Fact]
        public void HostWithADistanceBesideItIsTwoStatementsAndIsRefused()
        {
            StructuralRequirementSet s = Load(ZoneRule("'cover': { 'source': 'host', 'distance_mm': 40 },"));
            Assert.False(s.Ok);
            Assert.Equal(StructuralRequirementSet.CodeUnknownValue, s.Code);
            Assert.Equal("stirrup_zone_rules['B1'].cover.distance_mm", s.Path);
        }

        [Fact]
        public void AnUnknownSourceWordIsRefusedWithTheTwoWords()
        {
            StructuralRequirementSet s = Load(ZoneRule("'cover': { 'source': 'model' },"));
            Assert.False(s.Ok);
            Assert.Equal(StructuralRequirementSet.CodeUnknownValue, s.Code);
            Assert.Equal("stirrup_zone_rules['B1'].cover.source", s.Path);
            Assert.Contains("host or declared", s.Error);
        }

        [Fact]
        public void ANegativeDeclaredCoverIsRefused()
        {
            StructuralRequirementSet s = Load(ZoneRule("'cover': { 'source': 'declared', 'distance_mm': -5 },"));
            Assert.False(s.Ok);
            Assert.Equal("stirrup_zone_rules['B1'].cover.distance_mm", s.Path);
        }

        [Fact]
        public void ADistanceThatIsNotANumberIsRefusedNotConverted()
        {
            StructuralRequirementSet s = Load(ZoneRule("'cover': { 'source': 'declared', 'distance_mm': '40' },"));
            Assert.False(s.Ok);
            Assert.Equal(StructuralRequirementSet.CodeNotANumber, s.Code);
        }

        [Fact]
        public void ATypoInsideTheCoverBlockNamesThePathAndTheNearestKey()
        {
            StructuralRequirementSet s = Load(ZoneRule("'cover': { 'source': 'declared', 'distance': 40 },"));
            Assert.False(s.Ok);
            Assert.Equal(StructuralRequirementSet.CodeUnknownKey, s.Code);
            Assert.Equal("stirrup_zone_rules['B1'].cover.distance", s.Path);
            Assert.Equal(new[] { "distance_mm" }, s.DidYouMean);
            Assert.Equal(new[] { "distance_mm", "source" }, s.Allowed);
        }

        [Fact]
        public void ACoverThatIsNotAnObjectIsRefused()
        {
            StructuralRequirementSet s = Load(ZoneRule("'cover': 40,"));
            Assert.False(s.Ok);
            Assert.Equal(StructuralRequirementSet.CodeSchema, s.Code);
            Assert.Equal("stirrup_zone_rules['B1'].cover", s.Path);
        }

        [Fact]
        public void CoverIsAdmittedOnTheZoneRuleAndNotOnAZone()
        {
            Assert.Contains("cover", StructuralRequirementSet.StirrupZoneRuleKeys);
            Assert.DoesNotContain("cover", StructuralRequirementSet.ZoneKeys);
            Assert.Equal(new[] { "distance_mm", "source" },
                         StructuralRequirementSet.ZoneCoverKeys.OrderBy(x => x, System.StringComparer.Ordinal));
        }

        // ------------------------------------------------------- openings

        [Fact]
        public void AMatRuleWithoutAnOpeningsBlockHasNone()
        {
            StructuralRequirementSet s = Load(MatRule(""));
            Assert.True(s.Ok, s.Error);
            Assert.Null(s.MatRules[0].Openings);
        }

        [Theory]
        [InlineData("omit")]
        [InlineData("ignore")]
        public void OmitAndIgnoreTakeAMinimumSizeAndNoClearance(string policy)
        {
            StructuralRequirementSet s = Load(MatRule("'openings': { 'policy': '" + policy + "', 'minimum_size_mm': 300 },"));
            Assert.True(s.Ok, s.Error);
            Assert.Equal(policy, s.MatRules[0].Openings.Policy);
            Assert.Equal(300, s.MatRules[0].Openings.MinimumSizeMm);
            Assert.Null(s.MatRules[0].Openings.ClearanceMm);
        }

        [Fact]
        public void TrimTakesAClearance()
        {
            StructuralRequirementSet s = Load(MatRule("'openings': { 'policy': 'trim', 'minimum_size_mm': 300, 'clearance_mm': 50 },"));
            Assert.True(s.Ok, s.Error);
            Assert.Equal("trim", s.MatRules[0].Openings.Policy);
            Assert.Equal(50, s.MatRules[0].Openings.ClearanceMm.Value);
        }

        [Fact]
        public void TrimWithoutAClearanceIsRefusedAtTheField()
        {
            StructuralRequirementSet s = Load(MatRule("'openings': { 'policy': 'trim', 'minimum_size_mm': 300 },"));
            Assert.False(s.Ok);
            Assert.Equal(StructuralRequirementSet.CodeMissing, s.Code);
            Assert.Equal("mat_rules['S1'].openings.clearance_mm", s.Path);
            Assert.Empty(s.MatRules);
        }

        [Theory]
        [InlineData("omit")]
        [InlineData("ignore")]
        public void AClearanceBesideAPolicyThatWouldNotUseItIsRefused(string policy)
        {
            StructuralRequirementSet s = Load(MatRule("'openings': { 'policy': '" + policy + "', 'minimum_size_mm': 300, 'clearance_mm': 50 },"));
            Assert.False(s.Ok);
            Assert.Equal(StructuralRequirementSet.CodeUnknownValue, s.Code);
            Assert.Equal("mat_rules['S1'].openings.clearance_mm", s.Path);
        }

        [Fact]
        public void TheMinimumSizeIsDeclaredNeverDefaulted()
        {
            StructuralRequirementSet s = Load(MatRule("'openings': { 'policy': 'omit' },"));
            Assert.False(s.Ok);
            Assert.Equal(StructuralRequirementSet.CodeMissing, s.Code);
            Assert.Equal("mat_rules['S1'].openings.minimum_size_mm", s.Path);
        }

        [Fact]
        public void AnUnknownPolicyWordIsRefusedWithTheThreeWords()
        {
            StructuralRequirementSet s = Load(MatRule("'openings': { 'policy': 'skip', 'minimum_size_mm': 300 },"));
            Assert.False(s.Ok);
            Assert.Equal(StructuralRequirementSet.CodeUnknownValue, s.Code);
            Assert.Equal("mat_rules['S1'].openings.policy", s.Path);
            Assert.Contains("omit, trim, ignore", s.Error);
        }

        [Fact]
        public void NegativeNumbersAreRefused()
        {
            Assert.False(Load(MatRule("'openings': { 'policy': 'omit', 'minimum_size_mm': -1 },")).Ok);
            Assert.False(Load(MatRule("'openings': { 'policy': 'trim', 'minimum_size_mm': 300, 'clearance_mm': -1 },")).Ok);
        }

        [Fact]
        public void ATypoInsideTheOpeningsBlockNamesThePathAndTheNearestKey()
        {
            StructuralRequirementSet s = Load(MatRule("'openings': { 'policy': 'trim', 'minimum_size_mm': 300, 'clearence_mm': 50 },"));
            Assert.False(s.Ok);
            Assert.Equal(StructuralRequirementSet.CodeUnknownKey, s.Code);
            Assert.Equal("mat_rules['S1'].openings.clearence_mm", s.Path);
            Assert.Equal(new[] { "clearance_mm" }, s.DidYouMean);
            Assert.Equal(new[] { "clearance_mm", "minimum_size_mm", "policy" }, s.Allowed);
        }

        [Fact]
        public void AnOpeningsBlockOnAComponentIsNotAdmitted()
        {
            // The policy belongs to the mat, not to one of its layers: a hole is
            // in the slab, and two layers cannot disagree about whether it exists.
            StructuralRequirementSet s = Load(MatRule("").Replace("'offset_from_face_mm': 30,",
                "'offset_from_face_mm': 30, 'openings': { 'policy': 'omit', 'minimum_size_mm': 1 },"));
            Assert.False(s.Ok);
            Assert.Equal(StructuralRequirementSet.CodeUnknownKey, s.Code);
            Assert.Equal("mat_rules['S1'].components[0].openings", s.Path);
        }

        [Fact]
        public void OpeningsIsAdmittedOnTheMatRule()
        {
            Assert.Contains("openings", StructuralRequirementSet.MatRuleKeys);
            Assert.DoesNotContain("openings", StructuralRequirementSet.MatComponentKeys);
            Assert.Equal(new[] { "clearance_mm", "minimum_size_mm", "policy" },
                         StructuralRequirementSet.MatOpeningsKeys.OrderBy(x => x, System.StringComparer.Ordinal));
        }

        [Fact]
        public void TheDigestOfASetWithTheNewBlocksIsStable()
        {
            JObject a = JObject.Parse((Head + ZoneRule("'cover': { 'source': 'host' },") + "," +
                                       MatRule("'openings': { 'policy': 'omit', 'minimum_size_mm': 300 },") + "}").Replace('\'', '"'));
            JObject b = JObject.Parse((Head + MatRule("'openings': { 'minimum_size_mm': 300, 'policy': 'omit' },") + "," +
                                       ZoneRule("'cover': { 'source': 'host' },") + "}").Replace('\'', '"'));
            Assert.True(StructuralRequirementSet.Load(a).Ok);
            Assert.Equal(StructuralRequirementSet.Sha256Of(a), StructuralRequirementSet.Sha256Of(b));
        }
    }
}
