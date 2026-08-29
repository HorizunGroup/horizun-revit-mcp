// -----------------------------------------------------------------------------
// Defects that only Revit could find. Each of these passed 2442 offline tests and
// five clean builds, and failed the first time it met a model. The comment on
// each one says what Revit said.
// -----------------------------------------------------------------------------
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class LiveFoundDefectsTests
    {
        private const string Head = @"{
          ""schema"": ""horizun.structural-requirements/1"",
          ""requirement_set"": { ""id"": ""live"", ""version"": ""1.0.0"" },
          ""units"": ""millimeter"",
          ""bar_types"": [ { ""id"": ""t"", ""type_name"": ""12M"", ""nominal_diameter_mm"": 12 } ],";

        private static StructuralRequirementSet Parse(string body)
        {
            return StructuralRequirementSet.Load(JObject.Parse(Head + body + "}"));
        }

        private static string Zone(string profile, string closed = "true")
        {
            return @"
              ""stirrup_zone_rules"": [{
                ""id"": ""B1"", ""host"": {""element_ids"":[1]}, ""bar_type"": ""t"",
                ""profile_mm"": " + profile + @", ""closed"": " + closed + @",
                ""along"": [1,0,0], ""span_mm"": 3000,
                ""zones"": [{ ""name"": ""all"",
                  ""layout"": {""rule"":""maximum_spacing"",""spacing_mm"":100} }] }]";
        }

        [Fact]
        public void AClosedZoneProfileThatRepeatsItsFirstPointIsRefusedAtParseTime()
        {
            // LIVE, Revit 2026, 2026-08-28. This declaration parsed, planned, resolved
            // and issued a confirmation token - and then the apply came back with
            // "3 required row(s) could not be resolved: curve_degenerate;
            // curve_degenerate; curve_degenerate", which is Revit refusing a
            // zero-length segment from deep inside its geometry engine with a message
            // about nothing in particular.
            //
            // The reinforcement path had refused exactly this by name for months.
            // profile_mm was read by a generic point-list reader that checks only
            // that the numbers are numbers, so the zone path had neither guard.
            StructuralRequirementSet s = Parse(Zone("[[0,0,0],[0,300,0],[0,300,600],[0,0,600],[0,0,0]]"));
            Assert.False(s.Ok);
            Assert.Contains("repeats its first point", s.Error);
            Assert.Contains("closed adds the last segment", s.Error);
        }

        [Fact]
        public void TheSameProfileIsFineWhenItIsNotDeclaredClosed()
        {
            // An OPEN bar that happens to return to its start is a legal thing to
            // draw - it is closed by its own geometry, not by a second last segment.
            StructuralRequirementSet s = Parse(
                Zone("[[0,0,0],[0,300,0],[0,300,600],[0,0,600],[0,0,0]]", "false"));
            Assert.True(s.Ok, s.Error);
        }

        [Fact]
        public void AZoneProfileWithAZeroLengthSegmentIsRefusedAndTheIndexIsNamed()
        {
            StructuralRequirementSet s = Parse(Zone("[[0,0,0],[0,300,0],[0,300,0],[0,300,600]]"));
            Assert.False(s.Ok);
            Assert.Contains("index 1 and 2", s.Error);
            Assert.Contains("zero length", s.Error);
        }

        [Fact]
        public void FourCornersDeclaredOnceIsWhatAStirrupLooksLike()
        {
            StructuralRequirementSet s = Parse(Zone("[[0,0,0],[0,300,0],[0,300,600],[0,0,600]]"));
            Assert.True(s.Ok, s.Error);
            Assert.Equal(4, s.StirrupZoneRules[0].ProfileMm.Count);
            Assert.True(s.StirrupZoneRules[0].Closed);
        }
    }
}
