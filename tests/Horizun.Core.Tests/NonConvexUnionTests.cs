// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// Steel that crosses JOINED elements (acero 3.0, L1). In a real model the
// elements a bar is declared into are joined: a beam cut by a round column, a
// footing notched where a foundation beam sits. Neither cut element is convex,
// and the union used to refuse them with into_union_not_measurable. These are
// built the way Revit hands them over - curved faces tessellated, each element
// triangulated on its own - and the answers are checked against a hand count.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Diagnostics;
using Horizun.Revit.Core;
using Xunit;
using Xunit.Abstractions;

namespace Horizun.Core.Tests
{
    public class NonConvexUnionTests
    {
        private readonly ITestOutputHelper _out;
        public NonConvexUnionTests(ITestOutputHelper output) { _out = output; }

        // ------------------------------------------------------------ shapes

        /// <summary>
        /// A prism: a plan polygon (counter-clockwise) extruded between two heights, its caps
        /// triangulated by the triangles given (indices into the polygon, then into extra cap
        /// points). Built per face, welded by index, outward-facing.
        /// </summary>
        internal static HostMesh Prism(IList<double[]> poly, IList<int[]> capTris, IList<double[]> extra,
            double w0, double w1, Func<double, double, double, double[]> map, double chordMm, string source)
        {
            var m = new HostMesh { Source = source, ChordToleranceMm = chordMm, AnyCurvedFace = chordMm > 0 };
            int n = poly.Count, e = extra == null ? 0 : extra.Count;
            var all = new List<double[]>(poly);
            if (extra != null) all.AddRange(extra);
            int[] bottom = new int[n + e], top = new int[n + e];
            for (int i = 0; i < n + e; i++)
            {
                double[] p = map(all[i][0], all[i][1], w0);
                bottom[i] = m.AddVertex(p[0], p[1], p[2]);
            }
            for (int i = 0; i < n + e; i++)
            {
                double[] p = map(all[i][0], all[i][1], w1);
                top[i] = m.AddVertex(p[0], p[1], p[2]);
            }
            for (int i = 0; i < n; i++)
            {
                int j = (i + 1) % n;
                m.AddTriangle(bottom[i], bottom[j], top[j]);
                m.AddTriangle(bottom[i], top[j], top[i]);
            }
            foreach (int[] t in capTris)
            {
                double[] a = all[t[0]], b = all[t[1]], c = all[t[2]];
                bool ccw = (b[0] - a[0]) * (c[1] - a[1]) - (b[1] - a[1]) * (c[0] - a[0]) > 0;
                int i0 = t[0], i1 = ccw ? t[1] : t[2], i2 = ccw ? t[2] : t[1];
                m.AddTriangle(top[i0], top[i1], top[i2]);
                m.AddTriangle(bottom[i0], bottom[i2], bottom[i1]);
            }
            // The map may be mirror-image; outward is decided by the volume, as Revit's is.
            if (Volume(m) < 0)
                for (int i = 0; i < m.Triangles.Count; i++)
                {
                    int[] t = m.Triangles[i];
                    m.Triangles[i] = new[] { t[0], t[2], t[1] };
                }
            return m;
        }

        private static double Volume(HostMesh m)
        {
            double s = 0;
            foreach (int[] t in m.Triangles)
            {
                double[] a = m.Vertices[t[0]], b = m.Vertices[t[1]], c = m.Vertices[t[2]];
                s += a[0] * (b[1] * c[2] - b[2] * c[1]) + a[1] * (b[2] * c[0] - b[0] * c[2]) + a[2] * (b[0] * c[1] - b[1] * c[0]);
            }
            return s / 6;
        }

        private static double[] Plan(double x, double y, double z) => new[] { x, y, z };

        /// <summary>A round column, radius 200, as an n-sided prism - the way Face.Triangulate hands it over.</summary>
        internal static HostMesh RoundColumn(int sides, double z0, double z1, double chordMm)
        {
            var poly = new List<double[]>();
            for (int k = 0; k < sides; k++)
            {
                double a = 2 * Math.PI * k / sides;
                poly.Add(new[] { 200 * Math.Cos(a), 200 * Math.Sin(a) });
            }
            var caps = new List<int[]>();
            for (int k = 0; k < sides; k++) caps.Add(new[] { sides, k, (k + 1) % sides });
            return Prism(poly, caps, new List<double[]> { new double[] { 0, 0 } }, z0, z1, Plan, chordMm,
                "a round column, " + sides + " sides");
        }

        /// <summary>
        /// A 300 wide x 400 deep beam from x = 3000 into a round column at the origin, its end
        /// cut by the column's TRUE cylinder and that cut tessellated in arcSegments chords.
        /// </summary>
        internal static HostMesh NotchedBeam(int arcSegments, double chordMm)
        {
            double phi0 = Math.Asin(150.0 / 200.0);
            var poly = new List<double[]> { new double[] { 3000, -150 }, new double[] { 3000, 150 } };
            for (int k = 0; k <= arcSegments; k++)
            {
                double a = phi0 - 2 * phi0 * k / arcSegments;   // +phi0 down to -phi0
                poly.Add(new[] { 200 * Math.Cos(a), 200 * Math.Sin(a) });
            }
            int n = poly.Count;
            var caps = new List<int[]>();
            for (int k = 0; k < n; k++) caps.Add(new[] { n, k, (k + 1) % n });
            return Prism(poly, caps, new List<double[]> { new double[] { 2000, 0 } }, 0, 400, Plan, chordMm,
                "a beam cut by a round column, the cut in " + arcSegments + " chords");
        }

        /// <summary>
        /// A 1600 x 1600 x 500 footing, top at z = 0, with a slot 300 wide and 200 deep across
        /// its top along X, where a foundation beam sits.
        /// </summary>
        internal static HostMesh NotchedFooting()
        {
            // The section in (y, z), extruded along x from -800 to 800.
            var poly = new List<double[]>
            {
                new double[] { -800, -500 }, new double[] { 800, -500 }, new double[] { 800, 0 }, new double[] { 150, 0 },
                new double[] { 150, -200 }, new double[] { -150, -200 }, new double[] { -150, 0 }, new double[] { -800, 0 }
            };
            var caps = new List<int[]>
            {
                new[] { 0, 1, 4 }, new[] { 1, 2, 4 }, new[] { 2, 3, 4 },
                new[] { 0, 4, 5 }, new[] { 0, 5, 7 }, new[] { 5, 6, 7 }
            };
            return Prism(poly, caps, null, -800, 800, (y, z, x) => new[] { x, y, z }, 0, "a footing with a slot");
        }

        /// <summary>The foundation beam in the slot: 300 x 500, its bottom at the slot's floor.</summary>
        internal static HostMesh FoundationBeam() =>
            HostMesh.Box(new double[] { -3000, -150, -200 }, new double[] { 800, 150, 300 });

        private static HostMesh UnionOf(params HostMesh[] parts)
        {
            string why;
            HostMesh u = HostMesh.Union(parts, out why);
            Assert.True(u != null, why);
            return u;
        }

        private static List<double[]> Line(double[] a, double[] b) => new List<double[]> { a, b };

        // The column runs from z = -1000 to z = 400, flush with the beam's top.
        private static HostMesh Column(double chordMm = 1.0) => RoundColumn(48, -1000, 400, chordMm);

        // ------------------------------------------------------------- shapes are closed

        [Fact]
        public void TheShapesAreClosedAndTheCutOnesAreNotConvex()
        {
            foreach (HostMesh m in new[] { Column(), NotchedBeam(12, 1), NotchedFooting(), FoundationBeam() })
                Assert.True(SolidContainment.Diagnose(m).Usable, m.Source + ": " + SolidContainment.Diagnose(m).Why);
            Assert.NotNull(HostUnion.Planes(Column()));
            Assert.Null(HostUnion.Planes(NotchedBeam(12, 1)));
            Assert.Null(HostUnion.Planes(NotchedFooting()));
        }

        // -------------------------------------------- (a) a beam cut by a round column

        [Fact]
        public void ABeamCutByARoundColumnIsMeasuredNotRefused()
        {
            string why;
            HostMesh u = HostMesh.Union(new[] { NotchedBeam(12, 1), Column() }, out why);
            Assert.True(u != null, why);
        }

        [Fact]
        public void ABarFromTheBeamIntoTheColumnIsInside_TheJointIsNotAir()
        {
            // 50 mm under the top, on the beam's axis, from near the far end to 100 mm past the
            // column's centre. Nearest air everywhere: the top face, 50 mm; less 8 mm of radius.
            HostMesh u = UnionOf(NotchedBeam(12, 1), Column());
            ContainmentVerdict v = SolidContainment.Classify(u,
                Line(new double[] { 2900, 0, 350 }, new double[] { -100, 0, 350 }), 8, 40, 2, 25);
            Assert.True(v.Word == SolidContainment.Inside, v.Why);
            Assert.InRange(v.MinSurfaceClearanceMm, 41.5, 42.0 + 1e-6);
            Assert.Contains("ray", v.HowMeasured);
        }

        [Fact]
        public void APointOnTheJointIsAsFarFromAirAsTheBeamSideFace()
        {
            // On the beam's axis, mid-depth, on the cut: the nearest air is the beam's side face,
            // 150 mm away. Measured against either element alone it is ON a face - 0 mm.
            HostMesh u = UnionOf(NotchedBeam(12, 1), Column());
            double dev;
            double sd = SolidContainment.SignedDistance(u, new double[] { 200, 0, 200 }, out dev);
            Assert.InRange(sd, 150 - SolidContainment.RayBoundMm(150) - 1e-6, 150 + 1e-6);
        }

        [Fact]
        public void APointOnTheJointNearTheTopIsNearTheTop()
        {
            // The false negative to avoid: on the joint, but 8 mm under a REAL face.
            HostMesh u = UnionOf(NotchedBeam(12, 1), Column());
            double dev;
            double sd = SolidContainment.SignedDistance(u, new double[] { 200, 0, 392 }, out dev);
            Assert.InRange(sd, 8 - SolidContainment.RayBoundMm(8) - 1e-6, 8 + 1e-6);
        }

        [Fact]
        public void TheSetAnswerSaysHowTheJointWasMeasured()
        {
            HostMesh u = UnionOf(NotchedBeam(12, 1), Column());
            SetContainment c = RebarContainment.Check(u,
                Line(new double[] { 2900, 0, 350 }, new double[] { -100, 0, 350 }), false, null, null, 8, 40, 2, 25);
            Newtonsoft.Json.Linq.JObject j = c.ToJson();
            Assert.Equal(SolidContainment.Inside, (string)j["containment"]);
            Assert.True((bool)j["joints"]["elements_not_convex"]);
            Assert.Equal(1.0, (double)j["joints"]["seam_width_mm"], 6);
            Assert.Equal(2562, (int)j["joints"]["rays"]);
            Assert.Contains("JOINED", (string)j["how_measured"]);

            // A union of convex elements says nothing of the kind: it is measured as before.
            string why;
            HostMesh boxes = HostMesh.Union(new[] { FoundationBeam(),
                HostMesh.Box(new double[] { -800, -800, -700 }, new double[] { 800, 800, -200 }) }, out why);
            Assert.Null(RebarContainment.Check(boxes,
                Line(new double[] { -2900, 0, -150 }, new double[] { 700, 0, -150 }), false, null, null, 8, 40, 2, 25)
                .ToJson()["joints"]);
        }

        [Fact]
        public void ABarOutOfTheColumnsFarSideIsPartlyOutside()
        {
            HostMesh u = UnionOf(NotchedBeam(12, 1), Column());
            ContainmentVerdict v = SolidContainment.Classify(u,
                Line(new double[] { 2900, 0, 200 }, new double[] { -260, 0, 200 }), 8, null, 2, 25);
            Assert.Equal(SolidContainment.PartiallyOutside, v.Word);
        }

        [Fact]
        public void ABarTooCloseToTheBeamSideIsStillOutside()
        {
            // y = 145 with an 8 mm radius: 3 mm of steel through the side face, into the column too.
            HostMesh u = UnionOf(NotchedBeam(12, 1), Column());
            ContainmentVerdict v = SolidContainment.Classify(u,
                Line(new double[] { 2900, 145, 200 }, new double[] { 0, 145, 200 }), 8, null, 2, 25);
            Assert.Equal(SolidContainment.PartiallyOutside, v.Word);
            Assert.InRange(v.WorstOutsideMm, 2.9, 3.0 + SolidContainment.RayBoundMm(10) + 1e-6);
        }

        [Fact]
        public void EachElementAloneStillCallsTheCrossingBarOutside()
        {
            List<double[]> bar = Line(new double[] { 2900, 0, 350 }, new double[] { -100, 0, 350 });
            Assert.NotEqual(SolidContainment.Inside, SolidContainment.Classify(NotchedBeam(12, 1), bar, 8, null, 2, 25).Word);
            Assert.NotEqual(SolidContainment.Inside, SolidContainment.Classify(Column(), bar, 8, null, 2, 25).Word);
        }

        [Fact]
        public void TessellatedApartTheSeamBetweenThemIsConcrete()
        {
            // The cut in 13 chords and the column in 48 sides: two inscribed polygons that do not
            // coincide, with slivers of "air" between them under a millimetre thick. Thinner than
            // the published chord tolerance, so a seam, not a face.
            HostMesh u = UnionOf(NotchedBeam(13, 1), Column());
            ContainmentVerdict v = SolidContainment.Classify(u,
                Line(new double[] { 2900, 60, 350 }, new double[] { -100, 60, 350 }), 8, 40, 2, 25);
            Assert.True(v.Word == SolidContainment.Inside, v.Why);
            Assert.InRange(v.MinSurfaceClearanceMm, 41.5, 42.0 + 1e-6);
        }

        [Fact]
        public void WithNoCurvedFaceDeclaredTheSameSliversAreAir()
        {
            // The same slivers, from meshes that claim to be exact: nothing licenses calling them
            // concrete, so a bar crossing one is not inside.
            HostMesh u = UnionOf(NotchedBeam(13, 0), Column(0));
            ContainmentVerdict v = SolidContainment.Classify(u,
                Line(new double[] { 2900, 60, 350 }, new double[] { -100, 60, 350 }), 8, 40, 2, 1);
            Assert.NotEqual(SolidContainment.Inside, v.Word);
        }

        // ----------------------------------------- (b) a footing notched for a foundation beam

        [Fact]
        public void TheFoundationBeamsBottomBarRunsThroughTheSlot()
        {
            HostMesh u = UnionOf(FoundationBeam(), NotchedFooting());
            ContainmentVerdict v = SolidContainment.Classify(u,
                Line(new double[] { -2900, 0, -150 }, new double[] { 700, 0, -150 }), 8, 40, 2, 25);
            Assert.True(v.Word == SolidContainment.Inside, v.Why);
            Assert.InRange(v.MinSurfaceClearanceMm, 41.5, 42.0 + 1e-6);
        }

        [Fact]
        public void ABarUpFromTheFootingIntoTheBeamCrossesTheSlotFloor()
        {
            // From 50 mm above the footing's bottom to 50 mm under the beam's top.
            HostMesh u = UnionOf(FoundationBeam(), NotchedFooting());
            ContainmentVerdict v = SolidContainment.Classify(u,
                Line(new double[] { 0, 0, -450 }, new double[] { 0, 0, 250 }), 8, 40, 2, 25);
            Assert.True(v.Word == SolidContainment.Inside, v.Why);
            Assert.InRange(v.MinSurfaceClearanceMm, 49.5, 50.0 + 1e-6);
        }

        [Fact]
        public void TheSlotFloorIsNotAir_ButTheFootingTopBesideTheBeamIs()
        {
            HostMesh u = UnionOf(FoundationBeam(), NotchedFooting());
            double dev;
            // On the slot's floor under the beam's axis: nearest air is the footing top beside the
            // beam, at (0, 150, 0) - 250 mm.
            double floor = SolidContainment.SignedDistance(u, new double[] { 0, 0, -200 }, out dev);
            // The footing's top is clipped at the beam's side plane 0.001 mm out (HostUnion.PlaneEpsMm).
            Assert.InRange(floor, 250 - SolidContainment.RayBoundMm(250) - 1e-6, 250 + 0.001);
            // In the footing beside the slot, 20 mm under its top: 20 mm.
            double top = SolidContainment.SignedDistance(u, new double[] { 0, 400, -20 }, out dev);
            Assert.InRange(top, 20 - 1e-6, 20 + 1e-6);
        }

        [Fact]
        public void APointInTheSlotWithNoBeamIsAir()
        {
            double dev;
            HostMesh u = UnionOf(NotchedFooting(), RoundColumn(48, 0, 3000, 1));
            Assert.True(SolidContainment.SignedDistance(u, new double[] { 0, 0, -100 }, out dev) < 0);
        }

        // ------------------------------------------- (c) anchored length through a cut element

        [Fact]
        public void TheLengthInsideACutElementIsExact()
        {
            // Across the footing along Y, 100 mm under its top: the slot takes 300 of 1400.
            List<double[]> across = Line(new double[] { 0, -700, -100 }, new double[] { 0, 700, -100 });
            Assert.Equal(1100, SolidContainment.LengthInside(NotchedFooting(), across), 6);
            Assert.Equal(300, SolidContainment.LengthInside(FoundationBeam(), across), 6);

            // Along the beam into the column: the cut is at x = 200 on the axis (a vertex of both).
            List<double[]> along = Line(new double[] { 2900, 0, 200 }, new double[] { -100, 0, 200 });
            Assert.Equal(2700, SolidContainment.LengthInside(NotchedBeam(12, 1), along), 6);
            Assert.Equal(300, SolidContainment.LengthInside(Column(), along), 6);
        }

        [Fact]
        public void AHookedBarThroughTheSlotCountsOnlyItsConcrete()
        {
            // Down 400 from the beam into the footing past the slot floor, then a 300 foot along +Y.
            // Footing: z -200 to -450 is 250 (under the floor), then the foot: y 0 to 150 is under
            // the slot floor too - still footing, since the slot stops at z = -200. So 250 + 300.
            var bar = new List<double[]>
            {
                new double[] { 0, 0, 200 }, new double[] { 0, 0, -450 }, new double[] { 0, 300, -450 }
            };
            Assert.Equal(550, SolidContainment.LengthInside(NotchedFooting(), bar), 6);
            Assert.Equal(400, SolidContainment.LengthInside(FoundationBeam(), bar), 6);
        }

        [Fact]
        public void TheAnchoredLengthOfASetIsMeasuredInTheCutElement()
        {
            HostMesh u = UnionOf(FoundationBeam(), NotchedFooting());
            double[] got = RebarContainment.AnchoredLengths(u,
                Line(new double[] { 0, -700, -100 }, new double[] { 0, 700, -100 }),
                new List<double> { 0, -150 }, new double[] { 0, 0, 1 });
            // Second position at z = -250, under the slot floor: the footing holds all 1400.
            Assert.Equal(1100, got[1], 6);
            Assert.Equal(0, got[0], 6);
        }

        // ------------------------------------------------------------- performance

        [Fact]
        public void TheRaySetAndItsBoundArePublished()
        {
            _out.WriteLine(SolidContainment.RayCount + " rays, covering angle " + SolidContainment.RayCoveringAngleDeg +
                           " deg, bound at 50/100/150 mm: " + SolidContainment.RayBoundMm(50) + " / " +
                           SolidContainment.RayBoundMm(100) + " / " + SolidContainment.RayBoundMm(150));
            Assert.Equal(2562, SolidContainment.RayCount);
            // Well under the 2 mm length tolerance at any clearance a cover check turns on.
            Assert.True(SolidContainment.RayBoundMm(150) < 0.2 * 2, SolidContainment.RayBoundMm(150).ToString());
            // Every direction in space is within the covering angle of a ray: checked by brute force.
            double worst = 0;
            var rnd = new Random(7);
            for (int i = 0; i < 20000; i++)
            {
                double z = 2 * rnd.NextDouble() - 1, a = 2 * Math.PI * rnd.NextDouble(), r = Math.Sqrt(1 - z * z);
                double[] d = { r * Math.Cos(a), r * Math.Sin(a), z };
                double best = -1;
                foreach (double[] ray in UnionJoints.Directions)
                    best = Math.Max(best, ray[0] * d[0] + ray[1] * d[1] + ray[2] * d[2]);
                worst = Math.Max(worst, Math.Acos(Math.Min(1, best)));
            }
            Assert.True(worst <= UnionJoints.CoveringAngle + 1e-9, worst + " > " + UnionJoints.CoveringAngle);
        }

        [Fact]
        public void ARealisticSetStaysFast()
        {
            // 100 bars on the beam's top and bottom, 25 mm samples, every one crossing the cut.
            HostMesh u = UnionOf(NotchedBeam(24, 1), RoundColumn(96, -1000, 400, 1));
            var offsets = new List<double>();
            for (int i = 0; i < 50; i++) offsets.Add(-100 + 200.0 * i / 49);
            var sw = Stopwatch.StartNew();
            SetContainment top = RebarContainment.Check(u,
                Line(new double[] { 2900, 0, 350 }, new double[] { -100, 0, 350 }), false, offsets,
                new double[] { 0, 1, 0 }, 8, 40, 2, 25);
            SetContainment bottom = RebarContainment.Check(u,
                Line(new double[] { 2900, 0, 50 }, new double[] { -100, 0, 50 }), false, offsets,
                new double[] { 0, 1, 0 }, 8, 40, 2, 25);
            sw.Stop();
            _out.WriteLine("100 bars, " + (u.Parts[0].Triangles.Count + u.Parts[1].Triangles.Count) +
                           " triangles: " + sw.ElapsedMilliseconds + " ms");
            Assert.Equal(SolidContainment.Inside, top.Word);
            Assert.Equal(SolidContainment.Inside, bottom.Word);
            _out.WriteLine("ray samples in the worst bar: " + top.Worst.RaySamples + " / " + bottom.Worst.RaySamples);
            Assert.True(sw.Elapsed.TotalSeconds < 30, sw.ElapsedMilliseconds + " ms");
        }

        [Fact]
        public void AFineMeshStaysFast()
        {
            // A column of 1000 sides (4000 triangles) and a cut of 200 chords: a few thousand
            // triangles, as a detailed element arrives. 50 bars crossing the joint.
            HostMesh u = UnionOf(NotchedBeam(200, 1), RoundColumn(1000, -1000, 400, 1));
            var offsets = new List<double>();
            for (int i = 0; i < 50; i++) offsets.Add(-100 + 200.0 * i / 49);
            var sw = Stopwatch.StartNew();
            SetContainment top = RebarContainment.Check(u,
                Line(new double[] { 2900, 0, 350 }, new double[] { -100, 0, 350 }), false, offsets,
                new double[] { 0, 1, 0 }, 8, 40, 2, 25);
            sw.Stop();
            _out.WriteLine("50 bars, " + (u.Parts[0].Triangles.Count + u.Parts[1].Triangles.Count) +
                           " triangles: " + sw.ElapsedMilliseconds + " ms");
            Assert.Equal(SolidContainment.Inside, top.Word);
            Assert.True(sw.Elapsed.TotalSeconds < 120, sw.ElapsedMilliseconds + " ms");
        }
    }
}
