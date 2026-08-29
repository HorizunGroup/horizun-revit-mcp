// -----------------------------------------------------------------------------
// A rebar has FLAT ends. Treating it as a capsule made the most ordinary bar in
// structural engineering - one that runs the full length of its host - report
// half a diameter of steel in the air. These pin the choice that fixed it, and
// its cost: within one radius of an end, this is a centreline test.
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class SolidContainmentEndsTests
    {
        private static HostMesh Beam()
        {
            return HostMesh.Box(new double[] { 0, -150, 0 }, new double[] { 4000, 150, 600 });
        }

        [Fact]
        public void ABarFlushWithBothEndsOfItsHostIsInside()
        {
            var bar = new List<double[]>
            {
                new double[] { 0, 0, 300 },
                new double[] { 4000, 0, 300 }
            };
            ContainmentVerdict v = SolidContainment.Classify(Beam(), bar, 8, null, 1, 25);
            Assert.Equal(SolidContainment.Inside, v.Word);
            Assert.Equal(0, v.WorstOutsideMm);
            Assert.False(v.ClosedLoop);
        }

        [Fact]
        public void ABarFlushWithTheEndStillFailsItsCover()
        {
            var bar = new List<double[]>
            {
                new double[] { 0, 0, 300 },
                new double[] { 4000, 0, 300 }
            };
            ContainmentVerdict v = SolidContainment.Classify(Beam(), bar, 8, 40, 1, 25);
            Assert.Equal(SolidContainment.InsideCoverViolated, v.Word);
            Assert.Equal(40.0, v.CoverShortfallMm, 6);
        }

        [Fact]
        public void ABarPastTheEndIsStillCaughtDespiteTheTaper()
        {
            var bar = new List<double[]>
            {
                new double[] { 100, 0, 300 },
                new double[] { 4100, 0, 300 }
            };
            ContainmentVerdict v = SolidContainment.Classify(Beam(), bar, 8, null, 1, 25);
            Assert.Equal(SolidContainment.PartiallyOutside, v.Word);
            Assert.Equal(100.0, v.WorstOutsideMm, 6);
        }

        [Fact]
        public void ATaperOnlyEverReachesOneRadiusInFromEachEnd()
        {
            // 5 mm past the end: less than the 8 mm radius, so the taper is active
            // there - and the answer is still the honest 5 mm, not nothing.
            var bar = new List<double[]>
            {
                new double[] { 100, 0, 300 },
                new double[] { 4005, 0, 300 }
            };
            ContainmentVerdict v = SolidContainment.Classify(Beam(), bar, 8, null, 1, 1);
            Assert.Equal(SolidContainment.PartiallyOutside, v.Word);
            Assert.Equal(5.0, v.WorstOutsideMm, 3);
        }

        [Fact]
        public void AClosedStirrupCarriesItsFullRadiusAllTheWayRound()
        {
            // A rectangle in the YZ plane at x = 2000, 40 mm in from each face on
            // three sides and 145 mm out on the fourth - which for a 8 mm radius is
            // 3 mm of steel outside. No point of a closed shape is an end.
            var stirrup = new List<double[]>
            {
                new double[] { 2000, -110, 40 },
                new double[] { 2000, 145, 40 },
                new double[] { 2000, 145, 560 },
                new double[] { 2000, -110, 560 },
                new double[] { 2000, -110, 40 }
            };
            ContainmentVerdict v = SolidContainment.Classify(Beam(), stirrup, 8, null, 1, 10);
            Assert.True(v.ClosedLoop);
            Assert.Equal(SolidContainment.PartiallyOutside, v.Word);
            Assert.Equal(3.0, v.WorstOutsideMm, 3);
        }

        [Fact]
        public void AWellPlacedClosedStirrupIsInside()
        {
            var stirrup = new List<double[]>
            {
                new double[] { 2000, -102, 48 },
                new double[] { 2000, 102, 48 },
                new double[] { 2000, 102, 552 },
                new double[] { 2000, -102, 552 },
                new double[] { 2000, -102, 48 }
            };
            ContainmentVerdict v = SolidContainment.Classify(Beam(), stirrup, 8, 40, 1, 10);
            Assert.True(v.ClosedLoop);
            Assert.Equal(SolidContainment.Inside, v.Word);
            Assert.Equal(40.0, v.MinSurfaceClearanceMm, 6);
        }
    }
}
