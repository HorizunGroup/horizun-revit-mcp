// -----------------------------------------------------------------------------
// Is the bar in the concrete? These are the cases the projection check could not
// tell apart, and the one that made this file necessary is RotatedBeam_*: a bar
// that the old test passes and that is 566 mm out in the air.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class SolidContainmentTests
    {
        // A beam: 4000 long, 300 wide, 600 deep, its axis along X, centred on Y.
        private static HostMesh Beam()
        {
            return HostMesh.Box(new double[] { 0, -150, 0 }, new double[] { 4000, 150, 600 });
        }

        private static List<double[]> Line(double[] a, double[] b)
        {
            return new List<double[]> { a, b };
        }

        // ------------------------------------------------------------ the mesh

        [Fact]
        public void ABoxIsAClosedMeshOfTwelveTriangles()
        {
            MeshDiagnosis d = SolidContainment.Diagnose(Beam());
            Assert.True(d.Usable);
            Assert.Equal(12, d.TriangleCount);
            Assert.Equal(0, d.OpenEdges);
            Assert.Equal(0, d.DegenerateTriangles);
        }

        [Fact]
        public void AnOpenShellIsRefusedRatherThanReadAsEverythingOutside()
        {
            HostMesh m = Beam();
            m.Triangles.RemoveAt(0);          // one triangle of the bottom face
            MeshDiagnosis d = SolidContainment.Diagnose(m);
            Assert.False(d.Usable);
            Assert.True(d.OpenEdges > 0);
            Assert.Contains("not a consistently oriented closed surface", d.Why);

            // and the classification refuses too, rather than calling it outside
            ContainmentVerdict v = SolidContainment.Classify(
                m, Line(new double[] { 2000, 0, 300 }, new double[] { 3000, 0, 300 }), 8, null, 1, 50);
            Assert.Equal(SolidContainment.NotEvaluable, v.Word);
            Assert.False(v.Evaluated);
        }

        [Fact]
        public void AMeshWithNoTrianglesIsNotEvaluable()
        {
            Assert.False(SolidContainment.Diagnose(new HostMesh()).Usable);
            Assert.False(SolidContainment.Diagnose(null).Usable);
        }

        [Fact]
        public void ATriangleReferringToAMissingVertexIsRefused()
        {
            HostMesh m = Beam();
            m.AddTriangle(0, 1, 9999);
            Assert.False(SolidContainment.Diagnose(m).Usable);
        }

        // --------------------------------------------------------- the winding

        [Fact]
        public void TheWindingNumberIsOneInsideAndZeroOutside()
        {
            HostMesh m = Beam();
            Assert.Equal(1.0, Math.Abs(SolidContainment.WindingNumber(m, new double[] { 2000, 0, 300 })), 3);
            Assert.Equal(0.0, Math.Abs(SolidContainment.WindingNumber(m, new double[] { 9000, 0, 300 })), 3);
        }

        [Fact]
        public void TheDistanceToTheBoundaryIsTheDistanceToTheNearestFace()
        {
            HostMesh m = Beam();
            // dead centre: 150 to each side face, 300 to top and bottom, 2000 to the ends
            Assert.Equal(150.0, SolidContainment.DistanceToBoundary(m, new double[] { 2000, 0, 300 }), 6);
            // 40 mm in from the +Y face
            Assert.Equal(40.0, SolidContainment.DistanceToBoundary(m, new double[] { 2000, 110, 300 }), 6);
            // outside, 100 mm past the far end
            Assert.Equal(100.0, SolidContainment.DistanceToBoundary(m, new double[] { 4100, 0, 300 }), 6);
        }

        [Fact]
        public void TheSignedDistanceIsPositiveInsideAndNegativeOutside()
        {
            HostMesh m = Beam();
            double dev;
            Assert.True(SolidContainment.SignedDistance(m, new double[] { 2000, 0, 300 }, out dev) > 0);
            Assert.True(SolidContainment.SignedDistance(m, new double[] { 4100, 0, 300 }, out dev) < 0);
            Assert.True(dev < 0.01);
        }

        // ----------------------------------------------------- the five answers

        [Fact]
        public void ABarWellInsideWithItsCoverMetIsInside()
        {
            ContainmentVerdict v = SolidContainment.Classify(
                Beam(), Line(new double[] { 200, 0, 300 }, new double[] { 3800, 0, 300 }),
                8, 30, 1, 50);
            Assert.Equal(SolidContainment.Inside, v.Word);
            Assert.True(v.Evaluated);
            Assert.Equal(0, v.WorstOutsideMm);
            Assert.Equal(0, v.CoverShortfallMm);
            Assert.Equal(142.0, v.MinSurfaceClearanceMm, 6);   // 150 to the side, less the 8 mm radius
        }

        [Fact]
        public void ABarExactlyAtItsDeclaredCoverIsStillInside()
        {
            // 40 mm cover, 16 mm bar: the centre sits 48 mm in, the surface at 40
            ContainmentVerdict v = SolidContainment.Classify(
                Beam(), Line(new double[] { 200, 102, 300 }, new double[] { 3800, 102, 300 }),
                8, 40, 1, 50);
            Assert.Equal(SolidContainment.Inside, v.Word);
            Assert.Equal(40.0, v.MinSurfaceClearanceMm, 6);
        }

        [Fact]
        public void ABarInsideTheConcreteButShortOfItsCoverSaysSo()
        {
            // surface 20 mm from the face where 40 was declared
            ContainmentVerdict v = SolidContainment.Classify(
                Beam(), Line(new double[] { 200, 122, 300 }, new double[] { 3800, 122, 300 }),
                8, 40, 1, 50);
            Assert.Equal(SolidContainment.InsideCoverViolated, v.Word);
            Assert.True(v.Evaluated);
            Assert.Equal(20.0, v.MinSurfaceClearanceMm, 6);
            Assert.Equal(20.0, v.CoverShortfallMm, 6);
            Assert.Equal(0, v.WorstOutsideMm);
        }

        [Fact]
        public void ABarWhoseCentreIsInsideButWhoseSurfacePokesOutIsPartiallyOutside()
        {
            // centre 5 mm in from the face, radius 8: 3 mm of steel in the air
            ContainmentVerdict v = SolidContainment.Classify(
                Beam(), Line(new double[] { 200, 145, 300 }, new double[] { 3800, 145, 300 }),
                8, 40, 1, 50);
            Assert.Equal(SolidContainment.PartiallyOutside, v.Word);
            Assert.Equal(3.0, v.WorstOutsideMm, 6);
        }

        [Fact]
        public void ABarThatLeavesThroughTheEndIsPartiallyOutside()
        {
            ContainmentVerdict v = SolidContainment.Classify(
                Beam(), Line(new double[] { 200, 0, 300 }, new double[] { 4500, 0, 300 }),
                8, null, 1, 50);
            Assert.Equal(SolidContainment.PartiallyOutside, v.Word);
            Assert.True(v.WorstOutsideMm > 400);
        }

        [Fact]
        public void ABarNowhereNearTheHostIsCompletelyOutside()
        {
            ContainmentVerdict v = SolidContainment.Classify(
                Beam(), Line(new double[] { 200, 900, 300 }, new double[] { 3800, 900, 300 }),
                8, 40, 1, 50);
            Assert.Equal(SolidContainment.CompletelyOutside, v.Word);
            Assert.True(v.Evaluated);
        }

        // ------------------------------------------------- the rotated host

        [Fact]
        public void RotatedBeam_TheProjectionCheckPassesABarThatIsHalfAMetreOutInTheAir()
        {
            HostMesh rotated = Beam().RotatedAboutZ(Math.PI / 4);
            MeshDiagnosis d = SolidContainment.Diagnose(rotated);
            Assert.True(d.Usable);

            // A point 566 mm from the beam's axis, and comfortably inside the
            // AXIS-ALIGNED box Revit reports for the rotated beam.
            var bar = Line(new double[] { 2400, 1600, 300 }, new double[] { 2500, 1500, 300 });

            List<double[]> aabbCorners = RebarPlanRules.BoxCorners(d.MinMm, d.MaxMm);
            foreach (double[] p in bar)
                for (int k = 0; k < 3; k++)
                    Assert.InRange(p[k], d.MinMm[k], d.MaxMm[k]);   // inside the box Revit hands you

            // The projection check - distributed vertically - is satisfied.
            RebarFitVerdict fit = RebarPlanRules.Fit(bar, aabbCorners, new double[] { 0, 0, 1 },
                                                     new List<double> { 0 }, 1.0);
            Assert.True(fit.Fits);

            // The solid check is not.
            ContainmentVerdict v = SolidContainment.Classify(rotated, bar, 8, 40, 1, 25);
            Assert.Equal(SolidContainment.CompletelyOutside, v.Word);
            Assert.True(v.Evaluated);
        }

        [Fact]
        public void RotatedBeam_ABarOnTheRotatedAxisIsInside()
        {
            HostMesh rotated = Beam().RotatedAboutZ(Math.PI / 4);
            double c = Math.Cos(Math.PI / 4);
            var bar = Line(new double[] { 200 * c, 200 * c, 300 }, new double[] { 3800 * c, 3800 * c, 300 });
            ContainmentVerdict v = SolidContainment.Classify(rotated, bar, 8, 40, 1, 25);
            Assert.Equal(SolidContainment.Inside, v.Word);
            Assert.Equal(142.0, v.MinSurfaceClearanceMm, 3);
        }

        [Fact]
        public void RotatedBeam_ABarJustOutsideTheRotatedFaceIsCaught()
        {
            HostMesh rotated = Beam().RotatedAboutZ(Math.PI / 4);
            double c = Math.Cos(Math.PI / 4);
            // 148 mm off the axis on the rotated perpendicular: the centreline is
            // 2 mm inside the face, and the 8 mm radius takes the steel 6 mm past it
            double px = -c * 148, py = c * 148;
            var bar = Line(new double[] { 200 * c + px, 200 * c + py, 300 },
                           new double[] { 3800 * c + px, 3800 * c + py, 300 });
            ContainmentVerdict v = SolidContainment.Classify(rotated, bar, 8, 40, 1, 25);
            Assert.Equal(SolidContainment.PartiallyOutside, v.Word);
            Assert.Equal(6.0, v.WorstOutsideMm, 3);
        }

        // ------------------------------------------- a mid-span excursion

        [Fact]
        public void ABarThatDipsOutBetweenItsEndPointsIsCaught()
        {
            // both ends are comfortably inside; the middle vertex is not
            var bar = new List<double[]>
            {
                new double[] { 500, 0, 300 },
                new double[] { 2000, 400, 300 },
                new double[] { 3500, 0, 300 }
            };
            ContainmentVerdict v = SolidContainment.Classify(Beam(), bar, 8, null, 1, 25);
            Assert.Equal(SolidContainment.PartiallyOutside, v.Word);
        }

        [Fact]
        public void SamplingCatchesAnExcursionThatTheVerticesAloneWouldMiss()
        {
            // Straight bar from inside to inside, but the host is two boxes with a
            // gap: the ends are in concrete and the middle is in the air. Only the
            // samples between the vertices can see it.
            HostMesh left = HostMesh.Box(new double[] { 0, -150, 0 }, new double[] { 1000, 150, 600 });
            var bar = Line(new double[] { 100, 0, 300 }, new double[] { 3000, 0, 300 });
            ContainmentVerdict v = SolidContainment.Classify(left, bar, 8, null, 1, 25);
            Assert.Equal(SolidContainment.PartiallyOutside, v.Word);
            Assert.True(v.SamplesTested > 100);
        }

        // ------------------------------------------------------ bad numbers

        [Theory]
        [InlineData(double.NaN)]
        [InlineData(double.PositiveInfinity)]
        [InlineData(-1.0)]
        public void ARadiusThatIsNotAFiniteNonNegativeNumberIsRefused(double radius)
        {
            ContainmentVerdict v = SolidContainment.Classify(
                Beam(), Line(new double[] { 200, 0, 300 }, new double[] { 3800, 0, 300 }),
                radius, null, 1, 50);
            Assert.Equal(SolidContainment.NotEvaluable, v.Word);
            Assert.False(v.Evaluated);
        }

        [Theory]
        [InlineData(double.NaN)]
        [InlineData(double.NegativeInfinity)]
        [InlineData(-0.5)]
        public void AToleranceThatIsNotFiniteAndNonNegativeIsRefused(double tol)
        {
            ContainmentVerdict v = SolidContainment.Classify(
                Beam(), Line(new double[] { 200, 0, 300 }, new double[] { 3800, 0, 300 }),
                8, null, tol, 50);
            Assert.Equal(SolidContainment.NotEvaluable, v.Word);
        }

        [Fact]
        public void ACoverThatIsNotFiniteIsRefused()
        {
            ContainmentVerdict v = SolidContainment.Classify(
                Beam(), Line(new double[] { 200, 0, 300 }, new double[] { 3800, 0, 300 }),
                8, double.NaN, 1, 50);
            Assert.Equal(SolidContainment.NotEvaluable, v.Word);
        }

        [Fact]
        public void ACentrelinePointThatIsNotFiniteIsRefused()
        {
            ContainmentVerdict v = SolidContainment.Classify(
                Beam(), Line(new double[] { 200, 0, 300 }, new double[] { double.NaN, 0, 300 }),
                8, null, 1, 50);
            Assert.Equal(SolidContainment.NotEvaluable, v.Word);
            Assert.Contains("finite", v.Why);
        }

        [Fact]
        public void AnEmptyCentrelineIsRefusedRatherThanTriviallyInside()
        {
            ContainmentVerdict v = SolidContainment.Classify(Beam(), new List<double[]>(), 8, null, 1, 50);
            Assert.Equal(SolidContainment.NotEvaluable, v.Word);
        }

        // -------------------------------------------------------- sampling

        [Fact]
        public void TheSampleCountIsCappedByWideningTheStepRatherThanByStoppingEarly()
        {
            var line = Line(new double[] { 0, 0, 0 }, new double[] { 1000000, 0, 0 });
            double used;
            List<double[]> s = SolidContainment.Sample(line, 1.0, out used);
            Assert.True(s.Count <= SolidContainment.MaxSamples);
            Assert.True(used > 1.0);
            // the last sample is still the end of the line
            Assert.Equal(1000000, s.Last()[0], 3);
        }

        [Fact]
        public void SamplingKeepsEveryVertexOfThePolyline()
        {
            var line = new List<double[]>
            {
                new double[] { 0, 0, 0 },
                new double[] { 100, 0, 0 },
                new double[] { 100, 100, 0 }
            };
            double used;
            List<double[]> s = SolidContainment.Sample(line, 1000, out used);
            Assert.Equal(3, s.Count);
            Assert.Equal(100, s[1][0], 6);
            Assert.Equal(100, s[2][1], 6);
        }

        [Fact]
        public void AStepThatIsNotAPositiveNumberSamplesNothingAndIsRefused()
        {
            double used;
            Assert.Empty(SolidContainment.Sample(
                Line(new double[] { 0, 0, 0 }, new double[] { 100, 0, 0 }), 0, out used));

            // Classify substitutes its own default rather than refusing, and says so
            ContainmentVerdict v = SolidContainment.Classify(
                Beam(), Line(new double[] { 200, 0, 300 }, new double[] { 3800, 0, 300 }), 8, null, 1, 0);
            Assert.Equal(SolidContainment.Inside, v.Word);
            Assert.Equal(25.0, v.SampleStepMm);
        }

        // ------------------------------------------------------- vocabulary

        [Fact]
        public void TheWeakestOfSeveralAnswersIsTheAnswerForTheSet()
        {
            Assert.Equal(SolidContainment.Inside, SolidContainment.Weakest(
                new[] { SolidContainment.Inside, SolidContainment.Inside }));
            Assert.Equal(SolidContainment.InsideCoverViolated, SolidContainment.Weakest(
                new[] { SolidContainment.Inside, SolidContainment.InsideCoverViolated }));
            Assert.Equal(SolidContainment.NotEvaluable, SolidContainment.Weakest(
                new[] { SolidContainment.InsideCoverViolated, SolidContainment.NotEvaluable }));
            Assert.Equal(SolidContainment.PartiallyOutside, SolidContainment.Weakest(
                new[] { SolidContainment.NotEvaluable, SolidContainment.PartiallyOutside }));
            Assert.Equal(SolidContainment.CompletelyOutside, SolidContainment.Weakest(
                new[] { SolidContainment.PartiallyOutside, SolidContainment.CompletelyOutside }));
        }

        [Fact]
        public void NothingMeasuredIsNotEvaluableRatherThanInside()
        {
            Assert.Equal(SolidContainment.NotEvaluable, SolidContainment.Weakest(new string[0]));
        }

        [Fact]
        public void AnUnknownContainmentWordThrowsRatherThanBeingIgnored()
        {
            Assert.Throws<ArgumentException>(() => SolidContainment.Weakest(new[] { "probably_fine" }));
        }

        [Fact]
        public void EveryPublishedWordIsOneTheWeakestFunctionAccepts()
        {
            foreach (string w in SolidContainment.AllWords)
                Assert.Equal(w, SolidContainment.Weakest(new[] { w }));
        }

        // ------------------------------------------------ tessellation is said

        [Fact]
        public void AnApproximatedBoundaryIsDeclaredInTheVerdict()
        {
            HostMesh m = Beam();
            m.AnyCurvedFace = true;
            m.ChordToleranceMm = 2.5;
            ContainmentVerdict v = SolidContainment.Classify(
                m, Line(new double[] { 200, 0, 300 }, new double[] { 3800, 0, 300 }), 8, 40, 1, 50);
            Assert.True(v.CurvedBoundaryApproximated);
            Assert.Equal(2.5, v.ChordToleranceMm);
        }
    }
}
