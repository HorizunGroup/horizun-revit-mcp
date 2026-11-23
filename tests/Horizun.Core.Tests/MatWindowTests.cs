// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// Acero 3.0, L4: a mat component laid over PART of its host - the top bars over a
// support (negativos), a band of bars around a column. `run` limits where the bars
// run along their own direction, `spread` where the array marches across, both in
// mm measured from the host's own extents. Every number is the caller's.
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class MatWindowTests
    {
        // A slab 6000 x 4000 x 200, its top at z = 200.
        private static HostMesh Slab() => HostMesh.Box(new double[] { 0, 0, 0 }, new double[] { 6000, 4000, 200 });

        private static MatComponentRequest Comp(double? runFrom = null, double? runTo = null, double? spreadFrom = null, double? spreadTo = null) =>
            new MatComponentRequest
            {
                Name = "neg", DirectionMm = new double[] { 1, 0, 0 }, BarTypeId = "t12", OffsetFromFaceMm = 31,
                EndCoverMm = 25, SideCoverMm = 31,
                RunFromMm = runFrom, RunToMm = runTo, SpreadFromMm = spreadFrom, SpreadToMm = spreadTo,
                Layout = new RebarLayoutRequest { Layout = RebarLayout.MaximumSpacing, SpacingMm = 200 }
            };

        private static MatResult Expand(MatComponentRequest c, out List<StructuralRebarRule> made) =>
            MatRules.Expand(new StructuralMatRule { Id = "N1", FaceNormalMm = new double[] { 0, 0, 1 }, Components = new List<MatComponentRequest> { c } },
                Slab(), id => 12, 25, out made);

        [Fact]
        public void Top_bars_over_a_support_run_only_over_their_window()
        {
            MatResult r = Expand(Comp(runFrom: 2400, runTo: 3600), out List<StructuralRebarRule> made);
            Assert.True(r.Ok, r.Why);
            StructuralRebarRule b = made.Single();
            Assert.Equal(2400, b.CurvesMm[0][0], 6);
            Assert.Equal(3600, b.CurvesMm[1][0], 6);
            Assert.Equal(169, b.CurvesMm[0][2], 6);
        }

        [Fact]
        public void A_band_marches_only_across_its_window()
        {
            MatResult r = Expand(Comp(spreadFrom: 1000, spreadTo: 2000), out List<StructuralRebarRule> made);
            Assert.True(r.Ok, r.Why);
            StructuralRebarRule b = made.Single();
            Assert.Equal(1000, b.CurvesMm[0][1], 6);
            Assert.Equal(1000, b.Layout.ArrayLengthMm.Value, 6);
            // The bars still run the whole slab, less the end cover.
            Assert.Equal(25, b.CurvesMm[0][0], 6); Assert.Equal(5975, b.CurvesMm[1][0], 6);
        }

        [Fact]
        public void A_window_inside_the_end_cover_is_refused() =>
            Assert.Equal(MatRules.CodeWindowOutsideTheHost, Expand(Comp(runFrom: 10, runTo: 1200), out _).Code);

        [Fact]
        public void A_window_past_the_host_is_refused() =>
            Assert.Equal(MatRules.CodeWindowOutsideTheHost, Expand(Comp(spreadFrom: 3500, spreadTo: 4500), out _).Code);




    }
}
