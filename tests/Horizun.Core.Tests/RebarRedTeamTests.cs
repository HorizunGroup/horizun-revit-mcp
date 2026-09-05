// -----------------------------------------------------------------------------
// One test per defect an adversarial read of this session's own code turned up.
// Each was executed against the code before it was fixed; the comment says what
// it did then, because a regression test whose failure mode is not written down
// is a test somebody will delete.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class RebarRedTeamTests
    {
        private static HostMesh Slab(double atX = 0)
        {
            return HostMesh.Box(new[] { atX, 0.0, 0.0 }, new[] { atX + 6000, 4000.0, 200.0 });
        }

        private static MatComponentRequest Comp(string name, double[] dir, double offset)
        {
            return new MatComponentRequest
            {
                Name = name,
                DirectionMm = dir,
                BarTypeId = "t12",
                OffsetFromFaceMm = offset,
                EndCoverMm = 25,
                SideCoverMm = 25,
                Layout = new RebarLayoutRequest { Layout = RebarLayout.MaximumSpacing, SpacingMm = 200 }
            };
        }

        // ---------------------------------------------------------------- 1

        [Fact]
        public void UnnamedCrossingMatLayersInOnePlaneAreRefusedToo()
        {
            // BEFORE: the same-plane check looked the radius up BY NAME, and the
            // name had already been defaulted to componentN while the lookup
            // compared against the declared name, which was null. Radius came back
            // zero for every unnamed component, and `if (separation >= radii)
            // continue` is true for an absolute value every time. Declared WITH
            // names the identical mat was refused; without them it was built.
            var rule = new StructuralMatRule
            {
                Id = "S1",
                FaceNormalMm = new double[] { 0, 0, 1 },
                Components = new List<MatComponentRequest>
                {
                    Comp(null, new double[] { 1, 0, 0 }, 31),
                    Comp(null, new double[] { 0, 1, 0 }, 31)
                }
            };
            List<StructuralRebarRule> made;
            MatResult r = MatRules.Expand(rule, Slab(), id => 12, out made);
            Assert.False(r.Ok);
            Assert.Equal(MatRules.CodeLayersShareAPlane, r.Code);
            Assert.Empty(made);
        }

        [Fact]
        public void WithNoDiameterAtAllTwoCrossingLayersAtOneDepthAreStillRefused()
        {
            var rule = new StructuralMatRule
            {
                Id = "S1",
                FaceNormalMm = new double[] { 0, 0, 1 },
                Components = new List<MatComponentRequest>
                {
                    Comp("x", new double[] { 1, 0, 0 }, 31),
                    Comp("y", new double[] { 0, 1, 0 }, 31)
                }
            };
            List<StructuralRebarRule> made;
            MatResult r = MatRules.Expand(rule, Slab(), id => 0, out made);
            Assert.False(r.Ok);
            Assert.Equal(MatRules.CodeLayersShareAPlane, r.Code);

            // ... and with no diameter, a real separation is no longer judged
            rule.Components[1].OffsetFromFaceMm = 43;
            Assert.True(MatRules.Expand(rule, Slab(), id => 0, out made).Ok);
        }

        // ---------------------------------------------------------------- 2

        [Fact]
        public void AMatDirectionInsideTheToleranceIsSquaredUpBeforeItIsUsedAsAnAxis()
        {
            // BEFORE: a direction half a degree out of the face was accepted, and
            // the point arithmetic rebuilt a model point as along*da + across*db +
            // up*dc - which is only that point when the three are orthonormal. The
            // leaked term is da*(along.up), and `da` is a MODEL coordinate. Measured
            // on a slab 50 m from the origin: the bar came out at z = 605..657 on a
            // 200 mm slab whose top is at z = 200. Four hundred millimetres above
            // the concrete, from a declaration the code had just accepted.
            double halfADegree = Math.Tan(0.5 * Math.PI / 180.0);
            var rule = new StructuralMatRule
            {
                Id = "S1",
                FaceNormalMm = new double[] { 0, 0, 1 },
                Components = new List<MatComponentRequest>
                {
                    Comp("top_x", new double[] { 1, 0, halfADegree }, 31)
                }
            };
            List<StructuralRebarRule> made;
            MatResult r = MatRules.Expand(rule, Slab(50000), id => 12, out made);
            Assert.True(r.Ok, r.Why);

            // the bar sits 31 mm under a face at z = 200, both ends, wherever the
            // slab is in the model
            Assert.Equal(169, made[0].CurvesMm[0][2], 6);
            Assert.Equal(169, made[0].CurvesMm[1][2], 6);
            Assert.Equal(50025, made[0].CurvesMm[0][0], 6);
            Assert.Contains("squared up", r.Components[0].Why);
        }

        [Fact]
        public void AnExactlyPerpendicularDirectionIsNotDescribedAsCorrected()
        {
            var rule = new StructuralMatRule
            {
                Id = "S1",
                FaceNormalMm = new double[] { 0, 0, 1 },
                Components = new List<MatComponentRequest> { Comp("top_x", new double[] { 1, 0, 0 }, 31) }
            };
            List<StructuralRebarRule> made;
            MatResult r = MatRules.Expand(rule, Slab(50000), id => 12, out made);
            Assert.True(r.Ok);
            Assert.DoesNotContain("squared up", r.Components[0].Why);
        }

        // ---------------------------------------------------------------- 3

        [Fact]
        public void TheMirroredZoneCannotCollideWithADeclaredZoneName()
        {
            // BEFORE: the duplicate-name pass ran over the DECLARED zones, before
            // symmetry appended the mirror. An unnamed first zone plus a zone called
            // "end" produced names zone1, end, end - two bar sets in different
            // places sharing one rule id, which provenance keys on and the audit
            // matches on.
            var zones = new List<StirrupZoneRequest>
            {
                new StirrupZoneRequest
                {
                    LengthMm = 1000,
                    Layout = new RebarLayoutRequest { Layout = RebarLayout.MaximumSpacing, SpacingMm = 100 }
                },
                new StirrupZoneRequest
                {
                    Name = "zone1_mirrored",
                    Layout = new RebarLayoutRequest { Layout = RebarLayout.MaximumSpacing, SpacingMm = 200 }
                }
            };
            StirrupZoneResult r = StirrupZoneRules.Plan(6000, zones, true, 0, 0, null, 10);
            Assert.False(r.Ok);
            Assert.Equal(StirrupZoneRules.CodeNameRepeated, r.Code);
        }

        [Fact]
        public void EveryZoneNameInAPlannedRunIsDistinct()
        {
            var zones = new List<StirrupZoneRequest>
            {
                new StirrupZoneRequest
                {
                    Name = "ends", LengthMm = 1000,
                    Layout = new RebarLayoutRequest { Layout = RebarLayout.MaximumSpacing, SpacingMm = 100, IncludeLastBar = false }
                },
                new StirrupZoneRequest
                {
                    Name = "middle",
                    Layout = new RebarLayoutRequest { Layout = RebarLayout.MaximumSpacing, SpacingMm = 200, IncludeLastBar = false }
                }
            };
            StirrupZoneResult r = StirrupZoneRules.Plan(6000, zones, true, 0, 0, null, 10);
            Assert.True(r.Ok, r.Why);
            var names = r.Zones.Select(z => z.Name).ToList();
            Assert.Equal(names.Count, names.Distinct().Count());
        }

        // ---------------------------------------------------------------- 4

        private const string Head = @"{
          ""schema"": ""horizun.structural-requirements/1"",
          ""requirement_set"": { ""id"": ""rt"", ""version"": ""1.0.0"" },
          ""units"": ""millimeter"",
          ""bar_types"": [ { ""id"": ""t"", ""type_name"": ""12M"", ""nominal_diameter_mm"": 12 } ],";

        private static StructuralRequirementSet Parse(string body)
        {
            return StructuralRequirementSet.Load(JObject.Parse(Head + body + "}"));
        }

        [Fact]
        public void AReinforcementRuleCannotTakeAnIdAZoneRuleWillExpandInto()
        {
            // BEFORE: the parser's own refusal text promised that a zone rule
            // expands into <id>#<zone> and that its id shares the namespace - and
            // only the top-level id was registered. A rule literally called
            // "B1#start" parsed happily beside a zone rule that expands to exactly
            // that. Two sets, one rule id.
            StructuralRequirementSet s = Parse(@"
              ""reinforcement_rules"": [{
                ""id"": ""B1#start"", ""host"": {""element_ids"":[1]}, ""bar_type"": ""t"",
                ""style"": ""standard"", ""curve_mm"": [[0,0,0],[1000,0,0]], ""normal"": [0,1,0],
                ""layout"": { ""rule"": ""single"" } }],
              ""stirrup_zone_rules"": [{
                ""id"": ""B1"", ""host"": {""element_ids"":[1]}, ""bar_type"": ""t"",
                ""profile_mm"": [[0,0,0],[100,0,0]], ""along"": [1,0,0], ""span_mm"": 3000,
                ""zones"": [
                  { ""name"": ""start"", ""length_mm"": 1000,
                    ""layout"": {""rule"":""maximum_spacing"",""spacing_mm"":100} },
                  { ""name"": ""middle"",
                    ""layout"": {""rule"":""maximum_spacing"",""spacing_mm"":200} } ] }]");
            Assert.False(s.Ok);
            Assert.Contains("B1#start", s.Error);
        }

        [Fact]
        public void AMatComponentIdIsReservedTheSameWay()
        {
            StructuralRequirementSet s = Parse(@"
              ""reinforcement_rules"": [{
                ""id"": ""S1#top_x"", ""host"": {""element_ids"":[1]}, ""bar_type"": ""t"",
                ""style"": ""standard"", ""curve_mm"": [[0,0,0],[1000,0,0]], ""normal"": [0,1,0],
                ""layout"": { ""rule"": ""single"" } }],
              ""mat_rules"": [{
                ""id"": ""S1"", ""host"": {""element_ids"":[1]}, ""face_normal"": [0,0,1],
                ""components"": [{ ""name"": ""top_x"", ""direction"": [1,0,0], ""bar_type"": ""t"",
                  ""offset_from_face_mm"": 31,
                  ""layout"": {""rule"":""maximum_spacing"",""spacing_mm"":150} }] }]");
            Assert.False(s.Ok);
            Assert.Contains("S1#top_x", s.Error);
        }

        // ------------------------------------------------------------ 6, 7, 8

        [Fact]
        public void ASpacingThatIsAStringIsRefusedRatherThanThrowing()
        {
            // BEFORE: Value<double?>() THREW FormatException out of Load, whose
            // contract is to return a refusal rather than throw - and whose own
            // ReadCurves names this exact hazard twenty lines away.
            StructuralRequirementSet s = Parse(@"
              ""reinforcement_rules"": [{
                ""id"": ""r"", ""host"": {""element_ids"":[1]}, ""bar_type"": ""t"",
                ""style"": ""standard"", ""curve_mm"": [[0,0,0],[1000,0,0]], ""normal"": [0,1,0],
                ""layout"": { ""rule"": ""maximum_spacing"", ""spacing_mm"": ""abc"" } }]");
            Assert.False(s.Ok);
            Assert.Contains("not a number", s.Error);
        }

        [Fact]
        public void ASpacingThatIsTrueIsRefusedRatherThanReadAsOneMillimetre()
        {
            // BEFORE: Newtonsoft converts the boolean true to 1.0, so
            // "spacing_mm": true was a one-millimetre pitch - 901 stirrups over a
            // 900 mm array, accepted and planned.
            StructuralRequirementSet s = Parse(@"
              ""reinforcement_rules"": [{
                ""id"": ""r"", ""host"": {""element_ids"":[1]}, ""bar_type"": ""t"",
                ""style"": ""standard"", ""curve_mm"": [[0,0,0],[1000,0,0]], ""normal"": [0,1,0],
                ""layout"": { ""rule"": ""maximum_spacing"", ""spacing_mm"": true,
                              ""array_length_mm"": 900 } }]");
            Assert.False(s.Ok);
            Assert.Contains("not a number", s.Error);
        }

        [Fact]
        public void AFractionalBarCountIsRefusedRatherThanRounded()
        {
            // BEFORE: Value<int?>() converted 2.6 to 3 - the identical failure
            // ReadSelector refuses by name three hundred lines earlier, where 1.5
            // arrived as element 2.
            StructuralRequirementSet s = Parse(@"
              ""reinforcement_rules"": [{
                ""id"": ""r"", ""host"": {""element_ids"":[1]}, ""bar_type"": ""t"",
                ""style"": ""standard"", ""curve_mm"": [[0,0,0],[1000,0,0]], ""normal"": [0,1,0],
                ""layout"": { ""rule"": ""fixed_number"", ""number"": 2.6, ""array_length_mm"": 900 } }]");
            Assert.False(s.Ok);
            Assert.Contains("not a whole number", s.Error);
        }

        [Fact]
        public void AZoneLayoutGetsTheSameStrictNumbers()
        {
            StructuralRequirementSet s = Parse(@"
              ""stirrup_zone_rules"": [{
                ""id"": ""B1"", ""host"": {""element_ids"":[1]}, ""bar_type"": ""t"",
                ""profile_mm"": [[0,0,0],[100,0,0]], ""along"": [1,0,0], ""span_mm"": 3000,
                ""zones"": [{ ""name"": ""all"",
                  ""layout"": {""rule"":""maximum_spacing"",""spacing_mm"":""abc""} }] }]");
            Assert.False(s.Ok);
            Assert.Contains("not a number", s.Error);
        }

        // ---------------------------------------------------------------- 9

        [Fact]
        public void ARefusedSetCarriesNoZoneOrMatRulesEither()
        {
            // BEFORE: Fail cleared CoverRules and RebarRules - its own comment says
            // "a caller that reads RebarRules without checking Ok must not find a
            // plausible half of somebody's reinforcement" - and the two rule kinds
            // added later never joined the list. A refused set carried a live
            // stirrup zone rule, which is now the thing that expands into the most
            // steel.
            StructuralRequirementSet s = Parse(@"
              ""stirrup_zone_rules"": [{
                ""id"": ""B1"", ""host"": {""element_ids"":[1]}, ""bar_type"": ""t"",
                ""profile_mm"": [[0,0,0],[100,0,0]], ""along"": [1,0,0], ""span_mm"": 3000,
                ""zones"": [{ ""name"": ""all"",
                  ""layout"": {""rule"":""maximum_spacing"",""spacing_mm"":100} }] }],
              ""mat_rules"": [{
                ""id"": ""S1"", ""host"": {""element_ids"":[1]}, ""face_normal"": [0,0,1],
                ""components"": [{ ""name"": ""x"", ""direction"": [1,0,0], ""bar_type"": ""NOPE"",
                  ""offset_from_face_mm"": 31,
                  ""layout"": {""rule"":""maximum_spacing"",""spacing_mm"":150} }] }]");
            Assert.False(s.Ok);
            Assert.Empty(s.StirrupZoneRules);
            Assert.Empty(s.MatRules);
            Assert.Empty(s.RebarRules);
            Assert.Empty(s.CoverRules);
        }

        // --------------------------------------------------------------- 10

        [Fact]
        public void ZonesWhoseBarsOverlapAtTheBoundaryAreRefused()
        {
            // BEFORE: a layout was allowed to derive an extent up to a tenth of a
            // millimetre longer than its zone - the tolerance that absorbs float
            // noise - and that tenth put the previous zone's last bar PAST the next
            // zone's first. The gap went negative, the coincidence test only fires
            // below a millionth, and the result reported a negative closest distance
            // and called it fine. Two stirrups a tenth of a millimetre apart on a
            // ten millimetre bar are the same bar twice.
            var zones = new List<StirrupZoneRequest>
            {
                new StirrupZoneRequest
                {
                    Name = "a", LengthMm = 1000,
                    Layout = new RebarLayoutRequest
                    {
                        Layout = RebarLayout.NumberWithSpacing, Number = 6, SpacingMm = 200.02
                    }
                },
                new StirrupZoneRequest
                {
                    Name = "b",
                    Layout = new RebarLayoutRequest { Layout = RebarLayout.MaximumSpacing, SpacingMm = 200 }
                }
            };
            StirrupZoneResult r = StirrupZoneRules.Plan(6000, zones, false, 0, 0, null, 10);
            Assert.False(r.Ok);
            Assert.Equal(StirrupZoneRules.CodeBarsCoincide, r.Code);
            Assert.Contains("overlap", r.Why);
        }

        [Fact]
        public void AHonestGapBetweenZonesIsStillAccepted()
        {
            var zones = new List<StirrupZoneRequest>
            {
                new StirrupZoneRequest
                {
                    Name = "a", LengthMm = 1000,
                    Layout = new RebarLayoutRequest { Layout = RebarLayout.MaximumSpacing, SpacingMm = 100, IncludeLastBar = false }
                },
                new StirrupZoneRequest
                {
                    Name = "b",
                    Layout = new RebarLayoutRequest
                    {
                        Layout = RebarLayout.MaximumSpacing, SpacingMm = 200
                    }
                }
            };
            StirrupZoneResult r = StirrupZoneRules.Plan(6000, zones, false, 0, 0, null, 10);
            Assert.True(r.Ok, r.Why);
            Assert.True(r.ClosestBetweenZonesMm > 0);
        }
    }
}
