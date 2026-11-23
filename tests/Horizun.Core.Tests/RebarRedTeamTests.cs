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





        // ---------------------------------------------------------------- 4

        private const string Head = @"{
          ""schema"": ""horizun.structural-requirements/1"",
          ""requirement_set"": { ""id"": ""rt"", ""version"": ""1.0.0"" },
          ""units"": ""millimeter"",
          ""bar_types"": [ { ""id"": ""t"", ""type_name"": ""12M"", ""nominal_diameter_mm"": 12 } ],";




















    }
}
