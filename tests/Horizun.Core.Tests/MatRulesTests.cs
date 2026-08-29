// -----------------------------------------------------------------------------
// "Top X at 150, top Y at 200" - one sentence, four centrelines the model
// already knows. These pin what is derived from the host, and the one failure
// nothing else in this bridge would catch: two crossing layers built inside one
// another, both of them inside the host and both meeting their cover.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class MatRulesTests
    {
        // A slab: 6000 x 4000 on plan, 200 thick, its top at z = 200.
        private static HostMesh Slab()
        {
            return HostMesh.Box(new double[] { 0, 0, 0 }, new double[] { 6000, 4000, 200 });
        }

        private static readonly double[] Up = { 0, 0, 1 };
        private static readonly double[] X = { 1, 0, 0 };
        private static readonly double[] Y = { 0, 1, 0 };

        private static MatComponentRequest Comp(string name, double[] dir, double offset,
                                                double spacing = 150, double end = 25, double side = 25)
        {
            return new MatComponentRequest
            {
                Name = name,
                DirectionMm = dir,
                BarTypeId = "t12",
                OffsetFromFaceMm = offset,
                EndCoverMm = end,
                SideCoverMm = side,
                Layout = new RebarLayoutRequest { Layout = RebarLayout.MaximumSpacing, SpacingMm = spacing }
            };
        }

        private static StructuralMatRule Rule(params MatComponentRequest[] comps)
        {
            return new StructuralMatRule
            {
                Id = "S1",
                FaceNormalMm = Up,
                Mark = "M1",
                Components = comps.ToList()
            };
        }

        private static double Dia(string id)
        {
            return 12;
        }

        // ------------------------------------------------- what it derives

        [Fact]
        public void TheFaceIsTheOutermostPlaneAlongTheDeclaredNormal()
        {
            List<StructuralRebarRule> made;
            MatResult r = MatRules.Expand(Rule(Comp("top_x", X, 31)), Slab(), Dia, out made);
            Assert.True(r.Ok, r.Why);
            Assert.Equal(200, r.FaceOffsetMm, 9);
        }

        [Fact]
        public void TheBottomFaceIsFoundByPointingTheNormalTheOtherWay()
        {
            StructuralMatRule rule = Rule(Comp("bottom_x", X, 31));
            rule.FaceNormalMm = new double[] { 0, 0, -1 };
            List<StructuralRebarRule> made;
            MatResult r = MatRules.Expand(rule, Slab(), Dia, out made);
            Assert.True(r.Ok, r.Why);
            Assert.Equal(0, r.FaceOffsetMm, 9);
            // the bar sits 31 mm ABOVE the soffit: measured along the declared normal,
            // which points down, so the centreline is at +31 in model z
            Assert.Equal(31, made[0].CurvesMm[0][2], 6);
        }

        [Fact]
        public void TheBarRunsTheHostsExtentLessTheEndCoverAtEachEnd()
        {
            List<StructuralRebarRule> made;
            MatResult r = MatRules.Expand(Rule(Comp("top_x", X, 31, end: 25)), Slab(), Dia, out made);
            Assert.True(r.Ok, r.Why);
            Assert.Equal(5950, r.Components[0].BarLengthMm, 6);      // 6000 - 25 - 25
            Assert.Equal(25, made[0].CurvesMm[0][0], 6);
            Assert.Equal(5975, made[0].CurvesMm[1][0], 6);
        }

        [Fact]
        public void TheArrayRunsAcrossTheHostLessTheSideCover()
        {
            List<StructuralRebarRule> made;
            MatResult r = MatRules.Expand(Rule(Comp("top_x", X, 31, side: 25)), Slab(), Dia, out made);
            Assert.True(r.Ok, r.Why);
            Assert.Equal(3950, r.Components[0].ArrayLengthMm, 6);    // 4000 - 25 - 25
            Assert.Equal(3950, made[0].Layout.ArrayLengthMm);
            Assert.Equal(25, made[0].CurvesMm[0][1], 6);             // the first bar
        }

        [Fact]
        public void TheBarSitsTheDeclaredDepthUnderTheFace()
        {
            List<StructuralRebarRule> made;
            MatRules.Expand(Rule(Comp("top_x", X, 31)), Slab(), Dia, out made);
            Assert.Equal(169, made[0].CurvesMm[0][2], 6);            // 200 - 31
            Assert.Equal(169, made[0].CurvesMm[1][2], 6);
        }

        [Fact]
        public void TheSetMarchesAcrossItsOwnBarsAndNotAlongThem()
        {
            List<StructuralRebarRule> made;
            MatRules.Expand(Rule(Comp("top_x", X, 31)), Slab(), Dia, out made);
            // bars run along X, so the array marches along Y
            Assert.Equal(0, Math.Abs(made[0].NormalMm[0]), 6);
            Assert.Equal(1, Math.Abs(made[0].NormalMm[1]), 6);
        }

        [Fact]
        public void FourComponentsBecomeFourReinforcementRulesNamedAfterThem()
        {
            List<StructuralRebarRule> made;
            MatResult r = MatRules.Expand(Rule(
                Comp("top_x", X, 31), Comp("top_y", Y, 55),
                Comp("bottom_x", X, 145), Comp("bottom_y", Y, 169)), Slab(), Dia, out made);
            Assert.True(r.Ok, r.Why);
            Assert.Equal(new[] { "S1#top_x", "S1#top_y", "S1#bottom_x", "S1#bottom_y" },
                         made.Select(x => x.Id).ToArray());
            foreach (StructuralRebarRule m in made)
            {
                Assert.Equal(StructuralStyle.Standard, m.Style);
                Assert.False(m.Closed);
                Assert.Equal(2, m.CurvesMm.Count);
                Assert.Equal("M1", m.Mark);
            }
        }

        [Fact]
        public void EachComponentMayCarryItsOwnSpacingAndMark()
        {
            MatComponentRequest a = Comp("top_x", X, 31, spacing: 150);
            MatComponentRequest b = Comp("top_y", Y, 55, spacing: 200);
            b.Mark = "M2";
            List<StructuralRebarRule> made;
            MatRules.Expand(Rule(a, b), Slab(), Dia, out made);
            Assert.Equal(150, made[0].Layout.SpacingMm);
            Assert.Equal(200, made[1].Layout.SpacingMm);
            Assert.Equal("M1", made[0].Mark);
            Assert.Equal("M2", made[1].Mark);
        }

        // ----------------------------------------------------- a rotated host

        [Fact]
        public void ARotatedSlabIsMeasuredInItsOwnDirectionsRatherThanTheWorldsl()
        {
            HostMesh turned = Slab().RotatedAboutZ(Math.PI / 6);
            double c = Math.Cos(Math.PI / 6), s = Math.Sin(Math.PI / 6);
            var alongTheSlab = new[] { c, s, 0.0 };

            StructuralMatRule rule = Rule(Comp("top_x", alongTheSlab, 31));
            List<StructuralRebarRule> made;
            MatResult r = MatRules.Expand(rule, turned, Dia, out made);
            Assert.True(r.Ok, r.Why);
            // the 6000 extent is still 6000 when it is measured along the slab
            Assert.Equal(5950, r.Components[0].BarLengthMm, 3);
            Assert.Equal(3950, r.Components[0].ArrayLengthMm, 3);
        }

        // ------------------------------------------------------- it refuses

        [Fact]
        public void TwoCrossingLayersInOnePlaneAreRefusedBecauseNothingElseWouldSaySo()
        {
            List<StructuralRebarRule> made;
            MatResult r = MatRules.Expand(Rule(Comp("top_x", X, 31), Comp("top_y", Y, 31)), Slab(), Dia, out made);
            Assert.False(r.Ok);
            Assert.Equal(MatRules.CodeLayersShareAPlane, r.Code);
            Assert.Contains("inside one another", r.Why);
            Assert.Empty(made);
        }

        [Fact]
        public void TwoCrossingLayersOneDiameterApartAreAccepted()
        {
            List<StructuralRebarRule> made;
            MatResult r = MatRules.Expand(Rule(Comp("top_x", X, 31), Comp("top_y", Y, 43)), Slab(), Dia, out made);
            Assert.True(r.Ok, r.Why);
            Assert.Equal(2, made.Count);
        }

        [Fact]
        public void TwoParallelLayersAtTheSameDepthAreNotTheSameMistake()
        {
            // two sets running the same way at one elevation is a legitimate thing to
            // declare - alternate bars of different types, for instance - and they do
            // not cross, so they do not occupy each other.
            List<StructuralRebarRule> made;
            MatResult r = MatRules.Expand(Rule(Comp("a", X, 31), Comp("b", X, 31)), Slab(), Dia, out made);
            Assert.True(r.Ok, r.Why);
        }

        [Fact]
        public void ABarDirectionThatDivesIntoTheConcreteIsRefused()
        {
            List<StructuralRebarRule> made;
            MatResult r = MatRules.Expand(Rule(Comp("tilted", new double[] { 1, 0, 0.2 }, 31)),
                                          Slab(), Dia, out made);
            Assert.False(r.Ok);
            Assert.Equal(MatRules.CodeDirectionNotInFace, r.Code);
            Assert.Contains("out of the face", r.Why);
        }

        [Fact]
        public void ABarDirectionAlongTheFaceNormalLeavesNothingForTheArrayToMarchIn()
        {
            List<StructuralRebarRule> made;
            MatResult r = MatRules.Expand(Rule(Comp("vertical", new double[] { 0, 0, 1 }, 31)),
                                          Slab(), Dia, out made);
            Assert.False(r.Ok);
            Assert.Equal(MatRules.CodeDirectionNotInFace, r.Code);
        }

        [Fact]
        public void ATinyToleranceOffPerpendicularIsStillInTheFace()
        {
            // 0.5 degrees: a direction typed from a survey rather than from an axis
            double tiny = Math.Tan(0.5 * Math.PI / 180.0);
            List<StructuralRebarRule> made;
            MatResult r = MatRules.Expand(Rule(Comp("almost", new double[] { 1, 0, tiny }, 31)),
                                          Slab(), Dia, out made);
            Assert.True(r.Ok, r.Why);
        }

        [Fact]
        public void EndCoverThatSwallowsTheHostIsRefused()
        {
            List<StructuralRebarRule> made;
            MatResult r = MatRules.Expand(Rule(Comp("top_x", X, 31, end: 4000)), Slab(), Dia, out made);
            Assert.False(r.Ok);
            Assert.Equal(MatRules.CodeNoRoomAlong, r.Code);
        }

        [Fact]
        public void SideCoverThatSwallowsTheHostIsRefused()
        {
            List<StructuralRebarRule> made;
            MatResult r = MatRules.Expand(Rule(Comp("top_x", X, 31, side: 3000)), Slab(), Dia, out made);
            Assert.False(r.Ok);
            Assert.Equal(MatRules.CodeNoRoomAcross, r.Code);
        }

        [Fact]
        public void NoComponentsIsRefusedRatherThanInvented()
        {
            List<StructuralRebarRule> made;
            MatResult r = MatRules.Expand(Rule(), Slab(), Dia, out made);
            Assert.False(r.Ok);
            Assert.Equal(MatRules.CodeNoComponents, r.Code);
            Assert.Equal(MatRules.CodeNoComponents, MatRules.Expand(null, Slab(), Dia, out made).Code);
        }

        [Fact]
        public void AHostWithNoUsableBoundaryIsRefusedRatherThanGuessed()
        {
            List<StructuralRebarRule> made;
            MatResult r = MatRules.Expand(Rule(Comp("top_x", X, 31)), new HostMesh(), Dia, out made);
            Assert.False(r.Ok);
            Assert.Equal(MatRules.CodeNoBoundary, r.Code);
        }

        [Fact]
        public void AFaceNormalThatIsNotADirectionIsRefused()
        {
            StructuralMatRule rule = Rule(Comp("top_x", X, 31));
            rule.FaceNormalMm = new double[] { 0, 0, 0 };
            List<StructuralRebarRule> made;
            MatResult r = MatRules.Expand(rule, Slab(), Dia, out made);
            Assert.False(r.Ok);
            Assert.Equal(MatRules.CodeNormalNotUsable, r.Code);
        }

        [Theory]
        [InlineData(double.NaN)]
        [InlineData(-1)]
        public void AnOffsetThatIsNotAFiniteNonNegativeDistanceIsRefused(double offset)
        {
            List<StructuralRebarRule> made;
            MatResult r = MatRules.Expand(Rule(Comp("top_x", X, offset)), Slab(), Dia, out made);
            Assert.False(r.Ok);
            Assert.Equal(MatRules.CodeOffsetNotUsable, r.Code);
        }

        [Fact]
        public void ARepeatedComponentNameIsRefusedBecauseEachBecomesItsOwnSet()
        {
            List<StructuralRebarRule> made;
            MatResult r = MatRules.Expand(Rule(Comp("x", X, 31), Comp("x", Y, 55)), Slab(), Dia, out made);
            Assert.False(r.Ok);
            Assert.Equal(MatRules.CodeNameRepeated, r.Code);
        }

        [Fact]
        public void ALayoutTheZoneCannotResolveIsReportedAgainstItsComponent()
        {
            MatComponentRequest c = Comp("top_x", X, 31);
            c.Layout = new RebarLayoutRequest { Layout = RebarLayout.MaximumSpacing, SpacingMm = -5 };
            List<StructuralRebarRule> made;
            MatResult r = MatRules.Expand(Rule(c), Slab(), Dia, out made);
            Assert.False(r.Ok);
            Assert.Equal(MatRules.CodeLayoutRefused, r.Code);
            Assert.Contains("'top_x'", r.Why);
        }

        [Fact]
        public void EveryRefusalCodeIsPublishedAndDistinct()
        {
            Assert.Equal(MatRules.AllCodes.Length, MatRules.AllCodes.Distinct().Count());
            Assert.Contains(MatRules.CodeLayersShareAPlane, MatRules.AllCodes);
        }

        // --------------------------------------------------------- parsing

        private const string Head = @"{
          ""schema"": ""horizun.structural-requirements/1"",
          ""requirement_set"": { ""id"": ""mat"", ""version"": ""1.0.0"" },
          ""units"": ""millimeter"",
          ""bar_types"": [ { ""id"": ""t12"", ""type_name"": ""12M"", ""nominal_diameter_mm"": 12 } ],";

        private const string OneMat = @"
          ""mat_rules"": [{
            ""id"": ""S1"",
            ""host"": { ""element_ids"": [42] },
            ""face_normal"": [0,0,1],
            ""components"": [
              { ""name"": ""top_x"", ""direction"": [1,0,0], ""bar_type"": ""t12"",
                ""offset_from_face_mm"": 31, ""end_cover_mm"": 25, ""side_cover_mm"": 25,
                ""layout"": { ""rule"": ""maximum_spacing"", ""spacing_mm"": 150 } },
              { ""name"": ""top_y"", ""direction"": [0,1,0], ""bar_type"": ""t12"",
                ""offset_from_face_mm"": 43,
                ""layout"": { ""rule"": ""maximum_spacing"", ""spacing_mm"": 200 } }
            ]
          }]";

        private static StructuralRequirementSet Parse(string body)
        {
            return StructuralRequirementSet.Load(JObject.Parse(Head + body + "}"));
        }

        [Fact]
        public void AWellFormedMatRuleParses()
        {
            StructuralRequirementSet s = Parse(OneMat);
            Assert.True(s.Ok, s.Error);
            Assert.Single(s.MatRules);
            Assert.Equal(2, s.MatRules[0].Components.Count);
            Assert.Equal(31, s.MatRules[0].Components[0].OffsetFromFaceMm);
            Assert.Equal(0, s.MatRules[0].Components[1].EndCoverMm);   // absent means zero, not invented
        }

        [Fact]
        public void AMatRuleWithoutAFaceNormalIsRefusedBecauseASlabHasTwoFaces()
        {
            StructuralRequirementSet s = Parse(OneMat.Replace(@"""face_normal"": [0,0,1],", ""));
            Assert.False(s.Ok);
            Assert.Contains("two faces", s.Error);
        }

        [Fact]
        public void AComponentWithoutAnOffsetIsRefusedRatherThanDerivedFromACover()
        {
            StructuralRequirementSet s = Parse(OneMat.Replace(@"""offset_from_face_mm"": 31,", ""));
            Assert.False(s.Ok);
            Assert.Contains("is a decision", s.Error);
        }

        [Fact]
        public void AComponentWithoutADirectionIsRefused()
        {
            StructuralRequirementSet s = Parse(OneMat.Replace(@"""direction"": [1,0,0],", ""));
            Assert.False(s.Ok);
        }

        [Fact]
        public void AMatRuleSharesTheRuleIdNamespace()
        {
            StructuralRequirementSet s = Parse(OneMat + @",
              ""stirrup_zone_rules"": [{
                ""id"": ""S1"", ""host"": {""element_ids"":[1]}, ""bar_type"": ""t12"",
                ""profile_mm"": [[0,0,0],[100,0,0]], ""along"": [1,0,0], ""span_mm"": 1000,
                ""zones"": [{ ""name"": ""all"", ""layout"": {""rule"":""maximum_spacing"",""spacing_mm"":100} }]
              }]");
            Assert.False(s.Ok);
            Assert.Contains("more than one rule", s.Error);
        }

        [Fact]
        public void AnUnknownBarTypeInAComponentIsRefusedByName()
        {
            StructuralRequirementSet s = Parse(OneMat.Replace(@"""bar_type"": ""t12"",
                ""offset_from_face_mm"": 31,", @"""bar_type"": ""t99"",
                ""offset_from_face_mm"": 31,"));
            Assert.False(s.Ok);
            Assert.Contains("t99", s.Error);
        }
    }
}
