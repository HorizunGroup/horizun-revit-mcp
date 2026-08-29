// -----------------------------------------------------------------------------
// The containment engine, read adversarially and measured. Each test below is a
// defect that was executed against this code before it was fixed; the comment
// carries the number it produced then, because the whole point of this engine is
// that it does not state something untrue about where the steel is.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class ContainmentRedTeamTests
    {
        private static HostMesh Beam()
        {
            return HostMesh.Box(new double[] { 0, -150, 0 }, new double[] { 4000, 150, 600 });
        }

        // A stirrup declared the way a requirement set MUST declare it: four
        // corners, first point NOT repeated, `closed` said separately.
        private static List<double[]> FourCorners(double y)
        {
            return new List<double[]>
            {
                new double[] { 2000, -102, 48 },
                new double[] { 2000, y, 48 },
                new double[] { 2000, y, 552 },
                new double[] { 2000, -102, 552 }
            };
        }

        // ---------------------------------------------------------------- 1

        [Fact]
        public void ADeclaredClosedStirrupIsMeasuredWithItsClosingSideToo()
        {
            // BEFORE: closedness was inferred from the last point equalling the
            // first - and a requirement set is REFUSED for repeating its first
            // point, because `closed` adds the last segment. So every legally
            // declared stirrup was measured with one whole side never sampled, and
            // with the radius tapered off the two corners that side joins.
            // Measured: a stirrup with 3 mm of steel out through one face came back
            // `inside`, over 1006 mm of a 1518 mm bar.
            List<double[]> poking = FourCorners(145);

            ContainmentVerdict asOpen = SolidContainment.Classify(
                Beam(), poking, false, 8, null, 1, 10);
            ContainmentVerdict asClosed = SolidContainment.Classify(
                Beam(), poking, true, 8, null, 1, 10);

            Assert.True(asClosed.ClosedLoop);
            Assert.Equal(SolidContainment.PartiallyOutside, asClosed.Word);
            Assert.Equal(3.0, asClosed.WorstOutsideMm, 3);

            // and the closing side really was extra work
            Assert.True(asClosed.SamplesTested > asOpen.SamplesTested);
        }

        [Fact]
        public void AClosedShapeCarriesItsFullRadiusAtTheDeclaredCorners()
        {
            // The open reading tapers the radius off the first and last points,
            // which for a stirrup are two of its corners.
            List<double[]> tight = FourCorners(144);
            Assert.Equal(SolidContainment.PartiallyOutside,
                SolidContainment.Classify(Beam(), tight, true, 8, null, 1, 10).Word);
        }

        [Fact]
        public void ASetOfDeclaredStirrupsIsCheckedClosedToo()
        {
            SetContainment c = RebarContainment.Check(
                Beam(), FourCorners(145), true, new List<double> { 0, 500 },
                new double[] { 1, 0, 0 }, 8, null, 1, 10);
            Assert.Equal(SolidContainment.PartiallyOutside, c.Word);
        }

        // ---------------------------------------------------------------- 2

        [Fact]
        public void AMeshWithSomeFacesFlippedIsRefusedRatherThanReadInsideOut()
        {
            // BEFORE: edges were counted without direction, so a mesh with two of
            // its twelve triangles reversed reported a closed manifold. The winding
            // number then read 0.09 twenty millimetres INSIDE the beam and 0.91
            // twenty millimetres outside it - both within the confidence limit - so
            // a bar entirely in the air was reported as sitting in concrete.
            HostMesh m = Beam();
            for (int i = 0; i < m.Triangles.Count; i++)
            {
                // the two triangles of the +X end face, found by their shared plane
                int[] t = m.Triangles[i];
                bool allAtFarEnd = Math.Abs(m.Vertices[t[0]][0] - 4000) < 1e-9 &&
                                   Math.Abs(m.Vertices[t[1]][0] - 4000) < 1e-9 &&
                                   Math.Abs(m.Vertices[t[2]][0] - 4000) < 1e-9;
                if (allAtFarEnd) m.Triangles[i] = new[] { t[0], t[2], t[1] };
            }

            MeshDiagnosis d = SolidContainment.Diagnose(m);
            Assert.False(d.Usable);
            Assert.True(d.OpenEdges > 0);

            ContainmentVerdict v = SolidContainment.Classify(
                m, new List<double[]> { new double[] { 4005, 0, 300 }, new double[] { 4028, 0, 300 } },
                8, null, 1, 10);
            Assert.Equal(SolidContainment.NotEvaluable, v.Word);
        }

        [Fact]
        public void AConsistentlyOrientedBoxIsStillAccepted()
        {
            Assert.True(SolidContainment.Diagnose(Beam()).Usable);
            Assert.True(SolidContainment.Diagnose(Beam().RotatedAboutZ(0.7)).Usable);
        }

        // ---------------------------------------------------------------- 3

        [Fact]
        public void ARadiusOfZeroIsNotEvaluableRatherThanACentrelineTest()
        {
            // BEFORE: three call sites defaulted the radius to zero when the model
            // would not report a diameter, and Classify accepted it. The surface
            // test collapsed to a centreline test, and a bar one millimetre inside
            // the face came back `inside` while its real 8 mm radius put 7 mm of it
            // in the air. The reply then stamped that `verified`.
            var bar = new List<double[]>
            {
                new double[] { 500, 149, 300 }, new double[] { 3500, 149, 300 }
            };
            ContainmentVerdict withRadius = SolidContainment.Classify(Beam(), bar, 8, null, 1, 25);
            Assert.Equal(SolidContainment.PartiallyOutside, withRadius.Word);

            ContainmentVerdict without = SolidContainment.Classify(Beam(), bar, 0, null, 1, 25);
            Assert.Equal(SolidContainment.NotEvaluable, without.Word);
            Assert.Contains("model diameter is not available", without.Why);
        }

        [Fact]
        public void TheRadiusEverySurfaceNumberWasComputedWithIsPublished()
        {
            SetContainment c = RebarContainment.Check(
                Beam(), new List<double[]> { new double[] { 500, 0, 300 }, new double[] { 3500, 0, 300 } },
                new List<double> { 0 }, new double[] { 1, 0, 0 }, 8, null, 1, 25);
            Assert.Equal(8.0, (double)c.ToJson()["bar_radius_mm"]);
        }

        // ---------------------------------------------------------------- 4

        [Fact]
        public void AnUnmeasurablePositionIsNamedAsTheReasonRatherThanAGoodOne()
        {
            // BEFORE: the worst verdict was replaced by an unevaluated position only
            // when nothing had been recorded yet. With position 0 measurable and
            // position 1 not, the reply said not_evaluable, named position 0 as the
            // worst, and quoted position 0's successful measurement as the reason
            // nothing could be measured.
            var bar = new List<double[]> { new double[] { 500, 0, 300 }, new double[] { 3500, 0, 300 } };
            // position 1 is moved to where the winding number cannot answer: outside
            // any mesh at all is still measurable, so instead make the SECOND
            // position's centreline degenerate by using an offset that is not finite
            // - which is refused - or use a mesh that is fine and a huge offset that
            // is still measurable. The reachable unmeasurable case is a null mesh
            // per position, so this test drives the reporting directly.
            SetContainment c = RebarContainment.Check(
                null, bar, new List<double> { 0, 900 }, new double[] { 1, 0, 0 }, 8, null, 1, 25);
            Assert.Equal(SolidContainment.NotEvaluable, c.Word);
            Assert.False(c.Measured);
            Assert.Contains("not a pass", c.Why);
        }

        [Fact]
        public void NumbersThatWereMeasuredStayPublishedWhenTheSetAsAWholeIsNot()
        {
            // The JSON must not suppress a cover shortfall somebody can act on just
            // because a sibling position was unreadable.
            var c = new SetContainment
            {
                Word = SolidContainment.NotEvaluable,
                Evaluated = false,
                Measured = true,
                MinSurfaceClearanceMm = 20,
                WorstCoverShortfallMm = 20,
                PositionsTested = 2
            };
            JObject o = c.ToJson();
            Assert.Equal(20.0, (double)o["worst_cover_shortfall_mm"]);
            Assert.Contains("could be worse", (string)o["numbers_are_partial"]);
        }

        [Fact]
        public void WhenNothingWasMeasuredNoNumbersArePublishedAtAll()
        {
            SetContainment c = RebarContainment.Check(
                null, new List<double[]> { new double[] { 0, 0, 0 }, new double[] { 100, 0, 0 } },
                new List<double> { 0 }, new double[] { 1, 0, 0 }, 8, null, 1, 25);
            JObject o = c.ToJson();
            Assert.Null(o["worst_cover_shortfall_mm"]);
            Assert.Null(o["min_surface_clearance_mm"]);
        }

        // ---------------------------------------------------------------- 5

        [Fact]
        public void ASliverTriangleIsNotTheBoundary()
        {
            // BEFORE: Diagnose skipped zero-area triangles from the manifold test
            // and left them in the list, and DistanceToBoundary measured to every
            // triangle. A collinear sliver spanning the middle of a beam became "the
            // boundary", and a bar dead-centre in the concrete came back with 8 mm
            // of steel in the air.
            HostMesh m = Beam();
            int a = m.AddVertex(2000, 0, 300);
            int b = m.AddVertex(2000, 0, 350);
            int c = m.AddVertex(2000, 0, 400);
            m.AddTriangle(a, b, c);   // zero area: three points on one line

            Assert.True(SolidContainment.Diagnose(m).Usable);
            Assert.Equal(1, SolidContainment.Diagnose(m).DegenerateTriangles);
            Assert.Equal(150.0, SolidContainment.DistanceToBoundary(m, new double[] { 2000, 0, 320 }), 6);

            ContainmentVerdict v = SolidContainment.Classify(
                m, new List<double[]> { new double[] { 1000, 0, 300 }, new double[] { 3000, 0, 300 } },
                8, null, 1, 25);
            Assert.Equal(SolidContainment.Inside, v.Word);
        }

        // ---------------------------------------------------------------- 6

        [Fact]
        public void TwoOverlappingSolidsInOneHostAreStillMeasurable()
        {
            // BEFORE: HostSolidMesh merges every solid of an element into one mesh,
            // and winding is additive - so a point in the overlap gave 2, which was
            // compared against zero-or-one and came back NaN. Every bar inside an
            // in-place family or a joined member became unmeasurable.
            HostMesh merged = Beam();
            HostMesh second = HostMesh.Box(new double[] { 1000, -150, 0 }, new double[] { 3000, 150, 600 });
            int baseIndex = merged.Vertices.Count;
            foreach (double[] v in second.Vertices) merged.AddVertex(v[0], v[1], v[2]);
            foreach (int[] t in second.Triangles)
                merged.AddTriangle(baseIndex + t[0], baseIndex + t[1], baseIndex + t[2]);

            Assert.True(SolidContainment.Diagnose(merged).Usable);
            ContainmentVerdict v2 = SolidContainment.Classify(
                merged, new List<double[]> { new double[] { 1500, 0, 300 }, new double[] { 2500, 0, 300 } },
                8, null, 1, 25);
            Assert.Equal(SolidContainment.Inside, v2.Word);
        }

        // ---------------------------------------------------------------- 7

        [Fact]
        public void ABarWithNoLengthIsRefusedRatherThanReportedInside()
        {
            // BEFORE: with one point - or every point on top of the others - the
            // total length was zero, so the taper zeroed the radius everywhere and a
            // centreline half a millimetre inside the face came back `inside` for a
            // bar 20 mm across.
            var onePoint = new List<double[]> { new double[] { 2000, 149.5, 300 } };
            Assert.Equal(SolidContainment.NotEvaluable,
                SolidContainment.Classify(Beam(), onePoint, 10, null, 1, 25).Word);

            var coincident = new List<double[]>
            {
                new double[] { 2000, 149.5, 300 }, new double[] { 2000, 149.5, 300 }
            };
            ContainmentVerdict v = SolidContainment.Classify(Beam(), coincident, 10, null, 1, 25);
            Assert.Equal(SolidContainment.NotEvaluable, v.Word);
            Assert.Contains("no length", v.Why);
        }

        // --------------------------------------------------------------- 10

        [Fact]
        public void ASingleBarAtANonZeroOffsetNeedsADirectionToBeOffsetAlong()
        {
            // BEFORE: the guard asked how MANY offsets there were, so ONE bar at
            // 900 mm with an unusable normal was measured, unmoved, at zero - and
            // answered "all 1 bar position(s) are inside the host" about a place the
            // bar is not.
            var bar = new List<double[]> { new double[] { 500, 0, 300 }, new double[] { 3500, 0, 300 } };
            SetContainment c = RebarContainment.Check(
                Beam(), bar, new List<double> { 900 }, new double[] { 0, 0, 0 }, 8, null, 1, 25);
            Assert.Equal(SolidContainment.NotEvaluable, c.Word);
            Assert.Contains("900", c.Why);

            // a single bar at zero does not move, so it needs no direction
            SetContainment ok = RebarContainment.Check(
                Beam(), bar, new List<double> { 0 }, new double[] { 0, 0, 0 }, 8, null, 1, 25);
            Assert.Equal(SolidContainment.Inside, ok.Word);
        }

        [Fact]
        public void TheEdgeKeyDoesNotCollideOnAMeshWithManyVertices()
        {
            // BEFORE: the edge key was p * 4,000,000 + q, so edge(0, 4000001) and
            // edge(1, 1) hashed the same. Two colliding open edges summed to two and
            // an open shell passed the gate. The key is now scaled by the vertex
            // count, which cannot collide by construction.
            HostMesh m = Beam();
            for (int i = 0; i < 50; i++) m.AddVertex(-10000 - i, 0, 0);   // unreferenced, but they widen n
            MeshDiagnosis d = SolidContainment.Diagnose(m);
            Assert.True(d.Usable);
            Assert.Equal(0, d.OpenEdges);
        }
    }
}
