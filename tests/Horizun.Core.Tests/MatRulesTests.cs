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

        // ---------------------------------- a sloped face (acero 3.0, L6)

        // A stair's waist slab: 1200 wide, rising 2000 over 4000 on plan, 300 deep measured
        // vertically, its ends cut vertical - the sheared prism slab_shape makes of a floor.
        private static HostMesh Waist()
        {
            HostMesh m = HostMesh.Box(new double[] { 0, 0, -300 }, new double[] { 1200, 4000, 0 });
            foreach (double[] v in m.Vertices) v[2] += v[1] * 0.5;
            return m;
        }

        [Fact]
        public void A_mat_under_a_sloped_face_is_measured_on_that_face_not_on_the_whole_solid()
        {
            // MEASURED, Revit 2024 (acero L6, T1): the extents were taken over every vertex, and the
            // vertical end cuts reach further along the slope than the face does, so the first bar
            // across the slope was laid entirely outside the slab and the plan refused it.
            double len = Math.Sqrt(4000 * 4000 + 2000 * 2000);
            var rule = new StructuralMatRule
            {
                Id = "T1", FaceNormalMm = new[] { 0, 2000 / len, -4000 / len }, Mark = "W1",
                Components = new List<MatComponentRequest> { Comp("across", X, 47, 250, 40, 47) }
            };
            List<StructuralRebarRule> made;
            MatResult r = MatRules.Expand(rule, Waist(), Dia, out made);
            Assert.True(r.Ok, r.Why);
            double[] p = made.Single().CurvesMm[0];
            Assert.InRange(p[1], 0.0, 4000.0);
            // The face is 4472.1 long along the slope; the solid's projection 4606.3.
            Assert.Equal(len - 2 * 47, made.Single().Layout.ArrayLengthMm.Value, 3);
        }

        [Fact]
        public void A_stepped_host_is_still_measured_on_the_whole_solid()
        {
            // Two planes facing up (a step): the outermost face alone would quietly drop the lower
            // step; the extents stay the solid's and containment refuses a bar over the lower one.
            HostMesh m = HostMesh.Box(new double[] { 0, 0, 0 }, new double[] { 6000, 4000, 200 });
            HostMesh step = HostMesh.Box(new double[] { 6000, 0, 0 }, new double[] { 8000, 4000, 100 });
            int b = m.Vertices.Count;
            foreach (double[] v in step.Vertices) m.AddVertex(v[0], v[1], v[2]);
            foreach (int[] t in step.Triangles) m.AddTriangle(b + t[0], b + t[1], b + t[2]);
            List<StructuralRebarRule> made;
            MatResult r = MatRules.Expand(Rule(Comp("top_x", X, 31)), m, Dia, out made);
            Assert.True(r.Ok, r.Why);
            Assert.Equal(8000 - 50, made.Single().CurvesMm[1][0] - made.Single().CurvesMm[0][0], 6);
        }

        // ---------------------------------- a host whose faces differ (acero 3.0, L3)

        // MEASURED (Revit 2024): a strip footing has no COMMON cover - its bottom is "cast against
        // earth" at 75 mm, its sides 40 - and a layer declared 74 mm from the bottom was drawn
        // 82.95 mm up (75 + half a 16M bar), so the audit found it 8.95 mm off. The cover of the
        // face the mat lies under is what the bar is held to; a layer below it is refused.
        [Fact]
        public void A_layer_below_the_cover_of_its_own_face_is_refused_when_the_host_has_no_common_cover()
        {
            List<StructuralRebarRule> made;
            MatResult r = MatRules.Expand(Rule(Comp("bottom_y", Y, 74)), Slab(), Dia, null, 75, out made);
            Assert.Equal(MatRules.CodeOffsetBelowFaceCover, r.Code);
            Assert.Contains("75", r.Why);
            Assert.Empty(made);
        }

        [Fact]
        public void A_layer_at_or_past_its_face_cover_plus_half_the_bar_is_laid()
        {
            List<StructuralRebarRule> made;
            MatResult r = MatRules.Expand(Rule(Comp("bottom_y", Y, 81)), Slab(), Dia, null, 75, out made);
            Assert.True(r.Ok, r.Why);
        }

        [Fact]
        public void With_a_common_cover_the_face_cover_is_not_asked() =>
            Assert.True(MatRules.Expand(Rule(Comp("top_x", X, 31, end: 25, side: 31)), Slab(), Dia, 25, 75, out _).Ok);

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

        

        

        

        

        

        [Fact]
        public void A_mat_with_no_covers_or_depth_is_laid_at_the_host_cover_and_says_it_assumed_them()
        {
            var c = Comp("top_x", X, 0, 150, 0, 0);
            c.OffsetDeclared = false; c.EndCoverDeclared = false; c.SideCoverDeclared = false;
            MatResult r = MatRules.Expand(Rule(c), Slab(), Dia, 20, out List<StructuralRebarRule> rules);
            Assert.True(r.Ok, r.Why);
            Assert.Equal(new[] { "mat_offset_assumed", "mat_end_cover_assumed", "mat_side_cover_assumed" }, r.Assumptions.Select(a => (string)a["code"]).ToArray());
            Assert.Equal(26.0, (double)r.Assumptions[0]["value_mm"], 3);   // cover 20 + radius 6
            Assert.Equal(20.0, (double)r.Assumptions[1]["value_mm"], 3);
            Assert.Equal(26.0, (double)r.Assumptions[2]["value_mm"], 3);
            Assert.NotEmpty(rules);
        }

        [Fact]
        public void A_second_layer_with_no_depth_sits_under_the_first_by_its_diameter()
        {
            var a = Comp("top_x", X, 0, 150, 0, 0); var b = Comp("top_y", Y, 0, 150, 0, 0);
            foreach (var c in new[] { a, b }) { c.OffsetDeclared = false; c.EndCoverDeclared = false; c.SideCoverDeclared = false; }
            MatResult r = MatRules.Expand(Rule(a, b), Slab(), Dia, 20, out List<StructuralRebarRule> rules);
            Assert.True(r.Ok, r.Why);
            Assert.Equal(26.0, r.Components[0].OffsetFromFaceMm, 3);
            Assert.Equal(38.0, r.Components[1].OffsetFromFaceMm, 3);   // under the first layer's 12 mm bar
        }

        [Fact]
        public void A_layer_with_no_depth_under_a_declared_one_starts_from_the_declared_depth()
        {
            // 60 mm declared, then an undeclared layer: 60 + 6 + 6 = 72, never the 26 a bare cover would give
            // (which would put it ABOVE the declared layer).
            var a = Comp("top_x", X, 60, 150, 20, 26);
            var b = Comp("top_y", Y, 0, 150, 0, 0);
            b.OffsetDeclared = false; b.EndCoverDeclared = false; b.SideCoverDeclared = false;
            MatResult r = MatRules.Expand(Rule(a, b), Slab(), Dia, 20, out List<StructuralRebarRule> rules);
            Assert.True(r.Ok, r.Why);
            Assert.Equal(60.0, r.Components[0].OffsetFromFaceMm, 3);
            Assert.Equal(72.0, r.Components[1].OffsetFromFaceMm, 3);
            JObject depth = Assert.Single(r.Assumptions, x => (string)x["code"] == "mat_offset_assumed");
            Assert.Equal("top_y", (string)depth["component"]);
            Assert.Equal(72.0, (double)depth["value_mm"], 3);
        }

        [Fact]
        public void Mixed_layers_assumed_declared_assumed_each_sit_under_every_layer_before_them()
        {
            var a = Comp("l1", X, 0, 150, 0, 0);
            var b = Comp("l2", Y, 50, 150, 20, 26);   // declared below l1: kept as declared
            var c = Comp("l3", X, 0, 150, 0, 0);
            foreach (var x in new[] { a, c }) { x.OffsetDeclared = false; x.EndCoverDeclared = false; x.SideCoverDeclared = false; }
            MatResult r = MatRules.Expand(Rule(a, b, c), Slab(), Dia, 20, out List<StructuralRebarRule> rules);
            Assert.True(r.Ok, r.Why);
            Assert.Equal(26.0, r.Components[0].OffsetFromFaceMm, 3);
            Assert.Equal(50.0, r.Components[1].OffsetFromFaceMm, 3);
            Assert.Equal(62.0, r.Components[2].OffsetFromFaceMm, 3);   // under l2 (50 + 6 + 6), not only l1 (26 + 12 = 38)
            Assert.Equal(2, r.Assumptions.Count(x => (string)x["code"] == "mat_offset_assumed"));
            Assert.Equal(26.0, MatRules.AssumedLayerDepthMm(20, 6, null), 6);
        }

        [Fact]
        public void A_foundation_with_no_cover_is_assumed_at_75_mm_against_the_ground_and_marked()
        {
            var c = Comp("bottom_x", X, 0, 150, 0, 0);
            c.OffsetDeclared = false; c.EndCoverDeclared = false; c.SideCoverDeclared = false;
            var footing = new List<MatHostPart> { new MatHostPart { Mesh = Slab(), AgainstGround = true } };
            MatResult r = MatRules.Expand(Rule(c), footing, Dia, out List<StructuralRebarRule> rules);
            Assert.True(r.Ok, r.Why);
            Assert.Equal(81.0, r.Components[0].OffsetFromFaceMm, 3);   // 75 + radius 6
            JObject cover = Assert.Single(r.Assumptions, x => (string)x["code"] == "mat_cover_assumed");
            Assert.Equal(75.0, (double)cover["value_mm"], 3);
            Assert.Contains("against the ground", (string)cover["why"]);
            Assert.Equal(75.0, (double)r.Assumptions.Single(x => (string)x["code"] == "mat_end_cover_assumed")["value_mm"], 3);
            Assert.Equal(81.0, (double)r.Assumptions.Single(x => (string)x["code"] == "mat_side_cover_assumed")["value_mm"], 3);
            foreach (JObject row in r.Assumptions)
            {
                Assert.Equal("SUPUESTO", (string)row["flag"]);
                Assert.False(string.IsNullOrEmpty((string)row["what"]));
                Assert.False(string.IsNullOrEmpty((string)row["override"]));
                Assert.DoesNotContain("mm mm", (string)row["what"] + (string)row["why"] + (string)row["override"]);
            }
        }

        [Fact]
        public void Any_other_host_with_no_cover_is_assumed_at_25_mm_and_marked()
        {
            var c = Comp("top_x", X, 0, 150, 0, 0);
            c.OffsetDeclared = false; c.EndCoverDeclared = false; c.SideCoverDeclared = false;
            MatResult r = MatRules.Expand(Rule(c), Slab(), Dia, out List<StructuralRebarRule> rules);
            Assert.True(r.Ok, r.Why);
            Assert.Equal(31.0, r.Components[0].OffsetFromFaceMm, 3);
            JObject cover = Assert.Single(r.Assumptions, x => (string)x["code"] == "mat_cover_assumed");
            Assert.Equal(25.0, (double)cover["value_mm"], 3);
            Assert.Contains("75 mm is taken for a foundation", (string)cover["why"]);
        }

        

        

        

        [Fact]
        public void Two_crossing_layers_in_one_plane_clash_only_where_their_patches_meet()
        {
            // Negatives over a beam along X (bars along Y, y 0..650) and over a beam along Y (bars along X,
            // spread from y 750): one plane, crossing directions, and no point in common.
            var overY = new MatComponentPlan { StartPointMm = new[] { 325.0, 20, 0 }, EndPointMm = new[] { 325.0, 650, 0 },
                                               DistributionDirection = new[] { 1.0, 0, 0 }, ArrayLengthMm = 3000 };
            var overX = new MatComponentPlan { StartPointMm = new[] { 20.0, 750, 0 }, EndPointMm = new[] { 650.0, 750, 0 },
                                               DistributionDirection = new[] { 0, 1.0, 0 }, ArrayLengthMm = 1800 };
            Assert.False(MatRules.PatchesOverlap(overY, overX, 12.7));
            overX.StartPointMm = new[] { 20.0, 400, 0 }; overX.EndPointMm = new[] { 650.0, 400, 0 };
            Assert.True(MatRules.PatchesOverlap(overY, overX, 12.7));
        }
    }
}
