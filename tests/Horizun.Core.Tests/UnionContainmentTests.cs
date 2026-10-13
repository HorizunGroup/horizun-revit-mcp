// -----------------------------------------------------------------------------
// A bar that crosses from one element to another (acero 3.0, L1): a dowel from
// the footing into the column, a beam bar anchored in the column, a column bar
// lapped into the storey above. Its containment is measured against the UNION
// of the elements it was declared into, and the face two of them share is not
// a boundary - concrete meets concrete there, not air.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class UnionContainmentTests
    {
        // A footing 1600 x 1600 x 500 with its top at z = 0, and a 400 x 400 column on it, 3000 tall.
        private static HostMesh Footing() => HostMesh.Box(new double[] { -800, -800, -500 }, new double[] { 800, 800, 0 });
        private static HostMesh Column() => HostMesh.Box(new double[] { -200, -200, 0 }, new double[] { 200, 200, 3000 });

        private static List<double[]> Line(double[] a, double[] b) => new List<double[]> { a, b };

        private static HostMesh UnionOf(params HostMesh[] parts)
        {
            string why;
            HostMesh u = HostMesh.Union(parts, out why);
            Assert.True(u != null, why);
            return u;
        }

        // A dowel: 400 mm down in the footing, 600 mm up in the column, 50 mm in from two column faces.
        private static List<double[]> Dowel() => Line(new double[] { 150, 150, -400 }, new double[] { 150, 150, 600 });

        [Fact]
        public void ADowelIsInsideTheUnionAndOutsideEitherElementAlone()
        {
            Assert.Equal(SolidContainment.Inside, SolidContainment.Classify(UnionOf(Footing(), Column()), Dowel(), 8, null, 1, 25).Word);
            Assert.Equal(SolidContainment.PartiallyOutside, SolidContainment.Classify(Footing(), Dowel(), 8, null, 1, 25).Word);
            Assert.Equal(SolidContainment.PartiallyOutside, SolidContainment.Classify(Column(), Dowel(), 8, null, 1, 25).Word);
        }

        [Fact]
        public void TheSharedFaceIsNotABoundary_TheCoverIsMeasuredToTheAirAcrossTheJoint()
        {
            // 50 mm to the two nearest column faces, less an 8 mm radius: 42 mm of concrete.
            ContainmentVerdict v = SolidContainment.Classify(UnionOf(Footing(), Column()), Dowel(), 8, 40, 1, 25);
            Assert.Equal(SolidContainment.Inside, v.Word);
            Assert.InRange(v.MinSurfaceClearanceMm, 41.5, 42.5);

            // Just below the joint the nearest air is still the column face's foot, not the joint
            // itself 10 mm above: the footing's top face under the column is concrete on both sides.
            ContainmentVerdict below = SolidContainment.Classify(UnionOf(Footing(), Column()),
                Line(new double[] { 150, 150, -400 }, new double[] { 150, 150, -10 }), 8, null, 1, 5);
            Assert.True(below.MinSurfaceClearanceMm > 40, "clearance " + below.MinSurfaceClearanceMm);
        }

        [Fact]
        public void ACoverTheColumnCannotGiveIsStillACoverViolation()
        {
            ContainmentVerdict v = SolidContainment.Classify(UnionOf(Footing(), Column()), Dowel(), 8, 75, 1, 25);
            Assert.Equal(SolidContainment.InsideCoverViolated, v.Word);
        }

        [Fact]
        public void ABarThatLeavesTheUnionIsPartlyOutside()
        {
            // Up through the column and 200 mm out of its top.
            ContainmentVerdict v = SolidContainment.Classify(UnionOf(Footing(), Column()),
                Line(new double[] { 150, 150, -400 }, new double[] { 150, 150, 3200 }), 8, null, 1, 25);
            Assert.Equal(SolidContainment.PartiallyOutside, v.Word);
        }

        [Fact]
        public void OverlappingElementsAreOneSolid()
        {
            // A beam run straight through the column, not joined: the two solids overlap.
            HostMesh beam = HostMesh.Box(new double[] { -1500, -150, 2400 }, new double[] { 1500, 150, 3000 });
            ContainmentVerdict v = SolidContainment.Classify(UnionOf(Column(), beam),
                Line(new double[] { -1400, 0, 2500 }, new double[] { 1400, 0, 2500 }), 8, null, 1, 25);
            Assert.Equal(SolidContainment.Inside, v.Word);
        }

        [Fact]
        public void AFaceTwoElementsShareOnTheOutsideStaysABoundary()
        {
            // A beam over the column, flush with its -Y face: both faces lie in y = -200 and face
            // the same way, so the strip where they coincide is AIR on one side. Removing both
            // would put the nearest boundary 100 mm away instead of 50.
            HostMesh beam = HostMesh.Box(new double[] { -200, -200, 2500 }, new double[] { 3000, 200, 3000 });
            ContainmentVerdict v = SolidContainment.Classify(UnionOf(Column(), beam),
                Line(new double[] { -100, -150, 2700 }, new double[] { 100, -150, 2700 }), 8, null, 1, 5);
            Assert.Equal(SolidContainment.Inside, v.Word);
            Assert.InRange(v.MinSurfaceClearanceMm, 41.5, 42.5);
        }

        [Fact]
        public void ANonConvexElementIsMeasuredRatherThanRefused()
        {
            // Two separate boxes in one mesh: closed, but not convex. It used to refuse the whole
            // union; a joined, cut element is the ordinary case in a real model, so it is measured.
            HostMesh two = Footing();
            HostMesh far = HostMesh.Box(new double[] { 2000, -800, -500 }, new double[] { 3600, 800, 0 });
            int b = two.Vertices.Count;
            foreach (double[] p in far.Vertices) two.AddVertex(p[0], p[1], p[2]);
            foreach (int[] t in far.Triangles) two.AddTriangle(t[0] + b, t[1] + b, t[2] + b);
            Assert.True(SolidContainment.Diagnose(two).Usable);

            HostMesh u = UnionOf(two, Column());
            ContainmentVerdict v = SolidContainment.Classify(u, Dowel(), 8, 40, 1, 25);
            Assert.Equal(SolidContainment.Inside, v.Word);
            Assert.InRange(v.MinSurfaceClearanceMm, 41.5, 42.0 + 1e-6);

            // A bar in the second box is in concrete; one in the air between the boxes is not.
            Assert.Equal(SolidContainment.Inside, SolidContainment.Classify(u,
                Line(new double[] { 2100, 0, -250 }, new double[] { 3500, 0, -250 }), 8, null, 1, 25).Word);
            Assert.Equal(SolidContainment.CompletelyOutside, SolidContainment.Classify(u,
                Line(new double[] { 1000, 0, -250 }, new double[] { 1800, 0, -250 }), 8, null, 1, 25).Word);
            Assert.Equal(1600, SolidContainment.LengthInside(two,
                Line(new double[] { 0, 0, -250 }, new double[] { 2800, 0, -250 })), 6);
        }

        [Fact]
        public void AnOpenElementIsRefused()
        {
            HostMesh open = Column();
            open.Triangles.RemoveAt(0);
            string why;
            Assert.Null(HostMesh.Union(new[] { Footing(), open }, out why));
            Assert.Contains("element 2", why);
        }

        [Fact]
        public void OneElementAloneIsItsOwnUnion()
        {
            string why;
            HostMesh u = HostMesh.Union(new[] { Column() }, out why);
            Assert.NotNull(u);
            ContainmentVerdict v = SolidContainment.Classify(u,
                Line(new double[] { 150, 150, 100 }, new double[] { 150, 150, 2900 }), 8, 40, 1, 25);
            Assert.Equal(SolidContainment.Inside, v.Word);
            Assert.InRange(v.MinSurfaceClearanceMm, 41.5, 42.5);
        }

        // ------------------------------------------------------ anchored length

        [Fact]
        public void TheLengthInsideEachElementIsExact()
        {
            Assert.Equal(400, SolidContainment.LengthInside(Footing(), Dowel()), 6);
            Assert.Equal(600, SolidContainment.LengthInside(Column(), Dowel()), 6);
        }

        [Fact]
        public void AHookedFootCountsInTheElementItLiesIn()
        {
            // The dowel with a 300 mm foot along the bottom of the footing.
            var bar = new List<double[]>
            {
                new double[] { 450, 150, -400 }, new double[] { 150, 150, -400 }, new double[] { 150, 150, 600 }
            };
            Assert.Equal(700, SolidContainment.LengthInside(Footing(), bar), 6);
            Assert.Equal(600, SolidContainment.LengthInside(Column(), bar), 6);
        }

        [Fact]
        public void TheAnchoredLengthOfASetIsItsShortestBar()
        {
            // Two dowels 100 mm apart along Y, at y = 50 and y = 150; the footing is cut short at
            // y = 100, so the second one is not in it at all.
            HostMesh footing = HostMesh.Box(new double[] { -800, -800, -500 }, new double[] { 800, 100, 0 });
            double[] got = RebarContainment.AnchoredLengths(UnionOf(footing, Column()),
                Line(new double[] { 150, 50, -400 }, new double[] { 150, 50, 600 }),
                new List<double> { 0, 100 }, new double[] { 0, 1, 0 });
            Assert.Equal(2, got.Length);
            Assert.Equal(0, got[0], 6);      // footing: the second bar (y = 150) is past its edge
            Assert.Equal(600, got[1], 6);    // column: both bars run 600 mm in it
        }
    }
}
