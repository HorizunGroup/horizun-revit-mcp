// -----------------------------------------------------------------------------
// A mat that knows about the holes in its slab.
//
// The host mesh already carries the hole - Revit's solids subtract openings -
// so the work is reading it back: the rings of the face, the stretch of each
// bar that would be over the void, and what the DECLARED policy does about it.
// These pin all three, the boundary where a bar sits exactly on an edge, and
// the refusal that stops a mat crossing a hole nobody has said anything about.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class MatOpeningsTests
    {
        private static readonly double[] Up = { 0, 0, 1 };
        private static readonly double[] X = { 1, 0, 0 };
        private static readonly double[] Y = { 0, 1, 0 };

        /// <summary>
        /// A slab with a rectangular hole through it, built as a 3 x 3 grid of
        /// cells with the middle cell missing and every cell edge that is used
        /// once extruded into a wall. Vertices are shared, so the mesh is welded
        /// the way HostSolidMesh delivers one, and every directed edge is used
        /// once each way - which the manifold test below confirms.
        /// </summary>
        public static HostMesh SlabWithHole(double x0, double y0, double x1, double y1, double z0, double z1,
                                            double hx0, double hy0, double hx1, double hy1)
        {
            var m = new HostMesh { Source = "a slab with a hole, built in code" };
            double[] xs = { x0, hx0, hx1, x1 };
            double[] ys = { y0, hy0, hy1, y1 };
            var idx = new Dictionary<string, int>();
            var bottomOf = new Dictionary<int, int>();
            Func<int, int, bool, int> v = (ix, iy, top) =>
            {
                string k = ix + "," + iy + "," + top;
                int i;
                if (!idx.TryGetValue(k, out i))
                {
                    i = m.AddVertex(xs[ix], ys[iy], top ? z1 : z0);
                    idx[k] = i;
                }
                return i;
            };
            var directed = new Dictionary<long, int>();
            long n = 1000;
            Action<int, int> edge = (a, b) =>
            {
                int had;
                directed[a * n + b] = directed.TryGetValue(a * n + b, out had) ? had + 1 : 1;
            };
            for (int ix = 0; ix < 3; ix++)
                for (int iy = 0; iy < 3; iy++)
                {
                    if (ix == 1 && iy == 1) continue;
                    int a = v(ix, iy, true), b = v(ix + 1, iy, true), c = v(ix + 1, iy + 1, true), d = v(ix, iy + 1, true);
                    m.AddTriangle(a, b, c); m.AddTriangle(a, c, d);
                    edge(a, b); edge(b, c); edge(c, d); edge(d, a);
                    int a2 = v(ix, iy, false), b2 = v(ix + 1, iy, false), c2 = v(ix + 1, iy + 1, false), d2 = v(ix, iy + 1, false);
                    m.AddTriangle(a2, c2, b2); m.AddTriangle(a2, d2, c2);
                    bottomOf[a] = a2; bottomOf[b] = b2; bottomOf[c] = c2; bottomOf[d] = d2;
                }
            foreach (KeyValuePair<long, int> e in directed)
            {
                int a = (int)(e.Key / n), b = (int)(e.Key % n);
                if (directed.ContainsKey(b * n + a)) continue;   // interior of the face
                int ab = bottomOf[a], bb = bottomOf[b];
                m.AddTriangle(b, a, ab);
                m.AddTriangle(b, ab, bb);
            }
            return m;
        }

        /// <summary>6000 x 4000 x 200 with a 1000 x 1000 hole in the middle.</summary>
        private static HostMesh Holed()
        {
            return SlabWithHole(0, 0, 6000, 4000, 0, 200, 2000, 1500, 3000, 2500);
        }

        private static MatComponentRequest Comp(string name, double[] dir, double spacing = 200,
                                                double side = 25, double end = 25)
        {
            return new MatComponentRequest
            {
                Name = name,
                DirectionMm = dir,
                BarTypeId = "t12",
                OffsetFromFaceMm = 31,
                EndCoverMm = end,
                SideCoverMm = side,
                Layout = new RebarLayoutRequest { Layout = RebarLayout.MaximumSpacing, SpacingMm = spacing }
            };
        }

        private static StructuralMatRule Rule(StructuralMatOpenings openings, params MatComponentRequest[] comps)
        {
            return new StructuralMatRule
            {
                Id = "S1",
                FaceNormalMm = Up,
                Openings = openings,
                Components = comps.ToList()
            };
        }

        private static StructuralMatOpenings Policy(string policy, double min = 0, double? clearance = null)
        {
            return new StructuralMatOpenings { Policy = policy, MinimumSizeMm = min, ClearanceMm = clearance };
        }

        private static double Dia12(string id)
        {
            return 12;
        }

        private static double NoDia(string id)
        {
            return 0;
        }

        // ------------------------------------------------------------ the mesh

        [Fact]
        public void TheSlabWithAHoleIsAClosedConsistentlyOrientedSurface()
        {
            MeshDiagnosis d = SolidContainment.Diagnose(Holed());
            Assert.True(d.Usable, d.Why);
            Assert.Equal(0, d.OpenEdges);
        }

        [Fact]
        public void AStraightBarOverTheHoleIsPartiallyOutsideTheSlab()
        {
            // The premise of this whole file: the hole IS in the mesh.
            ContainmentVerdict over = SolidContainment.Classify(Holed(),
                new List<double[]> { new double[] { 25, 2000, 169 }, new double[] { 5975, 2000, 169 } }, 6, null, 2, 25);
            Assert.Equal(SolidContainment.PartiallyOutside, over.Word);
            ContainmentVerdict beside = SolidContainment.Classify(Holed(),
                new List<double[]> { new double[] { 25, 1000, 169 }, new double[] { 5975, 1000, 169 } }, 6, null, 2, 25);
            Assert.Equal(SolidContainment.Inside, beside.Word);
        }

        // ------------------------------------------------------- face loops

        [Fact]
        public void TheFaceLoopsAreTheOutlineAndTheHole()
        {
            FaceLoops loops = MatOpenings.ExtractFaceLoops(Holed(), Up, 200, MatOpenings.PlaneToleranceMm);
            Assert.True(loops.Ok, loops.Why);
            Assert.Equal(16, loops.TrianglesInFace);
            Assert.Equal(24000000, loops.Outer.AreaMm2, 3);
            Assert.Equal(4, loops.Outer.PointsMm.Count);          // grid corners on straight edges dropped
            Assert.Single(loops.Openings);
            Assert.Equal(1000000, loops.Openings[0].AreaMm2, 3);
            Assert.Equal(4, loops.Openings[0].PointsMm.Count);
            Assert.All(loops.Openings[0].PointsMm, p => Assert.Equal(200, p[2], 9));
        }

        [Fact]
        public void ASolidSlabHasAnOutlineAndNoOpening()
        {
            FaceLoops loops = MatOpenings.ExtractFaceLoops(
                HostMesh.Box(new double[] { 0, 0, 0 }, new double[] { 6000, 4000, 200 }), Up, 200, 0.5);
            Assert.True(loops.Ok, loops.Why);
            Assert.Empty(loops.Openings);
            Assert.Equal(4, loops.Outer.PointsMm.Count);
        }

        [Fact]
        public void TheBottomFaceCarriesTheSameHole()
        {
            FaceLoops loops = MatOpenings.ExtractFaceLoops(Holed(), new double[] { 0, 0, -1 }, 0, 0.5);
            Assert.True(loops.Ok, loops.Why);
            Assert.Single(loops.Openings);
            Assert.All(loops.Openings[0].PointsMm, p => Assert.Equal(0, p[2], 9));
        }

        [Fact]
        public void APlaneWithNoTrianglesIsRefusedWithTheReason()
        {
            FaceLoops loops = MatOpenings.ExtractFaceLoops(Holed(), Up, 100, 0.5);
            Assert.False(loops.Ok);
            Assert.Contains("no triangle", loops.Why);
        }

        // --------------------------------------------------------- crossing

        private static readonly List<double[]> Hole = new List<double[]>
        {
            new double[] { 2000, 1500 }, new double[] { 3000, 1500 }, new double[] { 3000, 2500 }, new double[] { 2000, 2500 }
        };

        [Fact]
        public void ABarThroughTheMiddleCrossesTheWholeHole()
        {
            List<double[]> iv = MatOpenings.CrossingIntervals(Hole, 2000, 0);
            Assert.Single(iv);
            Assert.Equal(2000, iv[0][0], 6);
            Assert.Equal(3000, iv[0][1], 6);
        }

        [Fact]
        public void TheBarsRadiusWidensTheCrossing()
        {
            List<double[]> iv = MatOpenings.CrossingIntervals(Hole, 2000, 6);
            Assert.Single(iv);
            Assert.Equal(1994, iv[0][0], 6);
            Assert.Equal(3006, iv[0][1], 6);
        }

        [Fact]
        public void ABarExactlyOnTheEdgeWithNoRadiusIsClearOnBothEdges()
        {
            Assert.Empty(MatOpenings.CrossingIntervals(Hole, 1500, 0));
            Assert.Empty(MatOpenings.CrossingIntervals(Hole, 2500, 0));
            Assert.Single(MatOpenings.CrossingIntervals(Hole, 1500.001, 0));
            Assert.Single(MatOpenings.CrossingIntervals(Hole, 2499.999, 0));
        }

        [Fact]
        public void ABarExactlyOnTheEdgeWithARadiusHasHalfItsBodyOverTheVoid()
        {
            // On the edge, the bar's body is within its radius of the whole edge -
            // and of the two corners, so the stretch reaches a radius past each.
            List<double[]> lo = MatOpenings.CrossingIntervals(Hole, 1500, 6);
            List<double[]> hi = MatOpenings.CrossingIntervals(Hole, 2500, 6);
            Assert.Single(lo);
            Assert.Single(hi);
            Assert.Equal(1994, lo[0][0], 3);
            Assert.Equal(3006, lo[0][1], 3);
            Assert.Equal(1994, hi[0][0], 3);
            Assert.Equal(3006, hi[0][1], 3);
            Assert.Empty(MatOpenings.CrossingIntervals(Hole, 1493.9, 6));
            Assert.NotEmpty(MatOpenings.CrossingIntervals(Hole, 1494.1, 6));
        }

        [Fact]
        public void ANonConvexOpeningGivesTwoStretchesOnOneBar()
        {
            // A U: the bar at v = 2000 passes through both arms and not the gap.
            var u = new List<double[]>
            {
                new double[] { 1000, 1000 }, new double[] { 1400, 1000 }, new double[] { 1400, 3000 },
                new double[] { 1200, 3000 }, new double[] { 1200, 1200 }, new double[] { 1000, 1200 }
            };
            // that is an L; add the second arm
            u = new List<double[]>
            {
                new double[] { 1000, 1000 }, new double[] { 2000, 1000 }, new double[] { 2000, 3000 },
                new double[] { 1800, 3000 }, new double[] { 1800, 1200 }, new double[] { 1200, 1200 },
                new double[] { 1200, 3000 }, new double[] { 1000, 3000 }
            };
            List<double[]> iv = MatOpenings.CrossingIntervals(u, 2000, 0);
            Assert.Equal(2, iv.Count);
            Assert.Equal(1000, iv[0][0], 6); Assert.Equal(1200, iv[0][1], 6);
            Assert.Equal(1800, iv[1][0], 6); Assert.Equal(2000, iv[1][1], 6);
            // and the bar at v = 1100 crosses the base as one stretch
            List<double[]> baseIv = MatOpenings.CrossingIntervals(u, 1100, 0);
            Assert.Single(baseIv);
            Assert.Equal(1000, baseIv[0][0], 6); Assert.Equal(2000, baseIv[0][1], 6);
        }

        [Fact]
        public void ComplementAndWidenDoWhatTrimNeeds()
        {
            var removed = MatOpenings.Widen(new List<double[]> { new[] { 1994.0, 3006.0 } }, 50);
            Assert.Equal(1944, removed[0][0], 6);
            Assert.Equal(3056, removed[0][1], 6);
            List<double[]> left = MatOpenings.Complement(removed, 25, 5975);
            Assert.Equal(2, left.Count);
            Assert.Equal(25, left[0][0], 6); Assert.Equal(1944, left[0][1], 6);
            Assert.Equal(3056, left[1][0], 6); Assert.Equal(5975, left[1][1], 6);
            Assert.Empty(MatOpenings.Complement(new List<double[]> { new[] { 0.0, 6000.0 } }, 25, 5975));
        }

        // ----------------------------------------------------- the policies

        private static List<StructuralRebarRule> Expand(StructuralMatRule rule, out MatResult r,
                                                        Func<string, double> dia = null)
        {
            List<StructuralRebarRule> made;
            r = MatRules.Expand(rule, Holed(), dia ?? Dia12, null, out made);
            return made;
        }

        // top_x at a maximum of 200 across 3950: 20 gaps of 197.5, bars at
        // v = 25 + 197.5 k. With a 6 mm radius the hole at 1500..2500 catches
        // k = 8 (1605) to k = 12 (2395): five bars.
        private static readonly int[] Crossing = { 8, 9, 10, 11, 12 };

        [Fact]
        public void NoPolicyAndABarThatWouldCrossIsRefusedByName()
        {
            MatResult r;
            List<StructuralRebarRule> made = Expand(Rule(null, Comp("top_x", X)), out r);
            Assert.False(r.Ok);
            Assert.Equal(MatRules.CodeOpeningsNeedAPolicy, r.Code);
            Assert.Empty(made);
            Assert.Contains("opening 0", r.Why);
            Assert.Contains("1000 mm along the bars by 1000 mm across", r.Why);
            Assert.Contains("5 of 21 bar(s)", r.Why);
            Assert.Contains("omit", r.Why);
            Assert.Contains("trim", r.Why);
            Assert.Contains("ignore", r.Why);
            Assert.Equal(1, r.OpeningsFound);
        }

        [Fact]
        public void NoPolicyAndNoBarNearTheHoleBuildsAsToday()
        {
            // A 10 mm sleeve between two bars: nothing crosses it, so the mat is
            // one rule per component exactly as before - with the sleeve reported.
            HostMesh sleeve = SlabWithHole(0, 0, 6000, 4000, 0, 200, 2000, 1500, 2010, 1510);
            List<StructuralRebarRule> made;
            MatResult r = MatRules.Expand(Rule(null, Comp("top_x", X)), sleeve, Dia12, null, out made);
            Assert.True(r.Ok, r.Why);
            Assert.Single(made);
            Assert.Equal("S1#top_x", made[0].Id);
            Assert.NotNull(made[0].OpeningContext);
            Assert.Equal(1, r.OpeningsFound);
            Assert.Empty(r.Components[0].Openings.BarsCrossing);
            Assert.Equal(21, r.Components[0].Openings.BarsKept);
            Assert.Equal(1, r.Components[0].RulesExpanded);
        }

        [Fact]
        public void ASolidSlabIsUntouchedByAnOpeningsBlock()
        {
            List<StructuralRebarRule> made;
            MatResult r = MatRules.Expand(Rule(Policy("omit"), Comp("top_x", X)),
                HostMesh.Box(new double[] { 0, 0, 0 }, new double[] { 6000, 4000, 200 }), Dia12, null, out made);
            Assert.True(r.Ok, r.Why);
            Assert.Single(made);
            Assert.Equal("S1#top_x", made[0].Id);
            Assert.Null(made[0].OpeningContext);
            Assert.Null(r.Components[0].Openings);
            Assert.Equal(0, r.OpeningsFound);
        }

        [Fact]
        public void OmitDropsTheCrossingBarsAndSplitsTheRestIntoTwoRuns()
        {
            MatResult r;
            List<StructuralRebarRule> made = Expand(Rule(Policy("omit"), Comp("top_x", X)), out r);
            Assert.True(r.Ok, r.Why);
            MatOpeningReport rep = r.Components[0].Openings;
            Assert.Equal("omit", rep.Policy);
            Assert.Equal(21, rep.BarsPlanned);
            Assert.Equal(Crossing, rep.BarsOmitted.ToArray());
            Assert.Equal(Crossing, rep.BarsCrossing.ToArray());
            Assert.Empty(rep.BarsTrimmed);
            Assert.Equal(16, rep.BarsKept);
            Assert.Equal(1, rep.OpeningsConsidered);

            Assert.Equal(2, made.Count);
            Assert.Equal("S1#top_x#run1", made[0].Id);
            Assert.Equal("S1#top_x#run2", made[1].Id);
            Assert.Equal(RebarLayout.NumberWithSpacing, made[0].Layout.Layout);
            Assert.Equal(8, made[0].Layout.Number);
            Assert.Equal(197.5, made[0].Layout.SpacingMm.Value, 6);
            Assert.Equal(25, made[0].CurvesMm[0][1], 6);                 // run 1 starts at bar 0
            Assert.Equal(25 + 197.5 * 13, made[1].CurvesMm[0][1], 6);    // run 2 starts at bar 13
            Assert.Equal(8, made[1].Layout.Number);
            Assert.Equal(25, made[1].CurvesMm[0][0], 6);                 // full length, both runs
            Assert.Equal(5975, made[1].CurvesMm[1][0], 6);
            Assert.All(made, m => Assert.Same(made[0].OpeningContext, m.OpeningContext));
            Assert.Equal(2, r.Components[0].RulesExpanded);
            Assert.Equal(2, rep.Runs.Count);
            Assert.Equal(0, rep.Runs[0].FirstBar); Assert.Equal(7, rep.Runs[0].LastBar);
            Assert.Equal(13, rep.Runs[1].FirstBar); Assert.Equal(20, rep.Runs[1].LastBar);
        }

        [Fact]
        public void TheSplitRunsReproduceTheOriginalStationsExactly()
        {
            MatResult r;
            List<StructuralRebarRule> made = Expand(Rule(Policy("omit"), Comp("top_x", X)), out r);
            var stations = new List<double>();
            foreach (StructuralRebarRule m in made)
            {
                RebarLayoutPlan p = RebarLayoutRules.Resolve(m.Layout);
                Assert.True(p.Ok, p.Error);
                foreach (double pos in p.PositionsMm) stations.Add(m.CurvesMm[0][1] + pos);
            }
            var expected = Enumerable.Range(0, 21).Where(k => !Crossing.Contains(k)).Select(k => 25 + 197.5 * k).ToList();
            Assert.Equal(expected.Count, stations.Count);
            for (int i = 0; i < expected.Count; i++) Assert.Equal(expected[i], stations[i], 6);
        }

        [Fact]
        public void TrimShortensTheCrossingBarsByTheClearanceAndKeepsBothStretches()
        {
            MatResult r;
            List<StructuralRebarRule> made = Expand(Rule(Policy("trim", 0, 50), Comp("top_x", X)), out r);
            Assert.True(r.Ok, r.Why);
            MatOpeningReport rep = r.Components[0].Openings;
            Assert.Equal(5, rep.BarsTrimmed.Count);
            Assert.Empty(rep.BarsOmitted);
            Assert.Equal(21, rep.BarsKept);
            MatTrimmedBar t = rep.BarsTrimmed[0];
            Assert.Equal(8, t.Bar);
            Assert.Equal(2, t.SegmentsMm.Count);
            Assert.Equal(25, t.SegmentsMm[0][0], 6);
            Assert.Equal(1944, t.SegmentsMm[0][1], 6);       // 1994 body edge less the 50 clearance
            Assert.Equal(3056, t.SegmentsMm[1][0], 6);
            Assert.Equal(5975, t.SegmentsMm[1][1], 6);
            Assert.Single(t.RemovedMm);
            Assert.Empty(t.DroppedMm);

            // run1 = bars 0..7 whole, run2 = bars 8..12 in two stretches, run3 = 13..20 whole
            Assert.Equal(4, made.Count);
            Assert.Equal("S1#top_x#run1", made[0].Id);
            Assert.Equal("S1#top_x#run2#seg1", made[1].Id);
            Assert.Equal("S1#top_x#run2#seg2", made[2].Id);
            Assert.Equal("S1#top_x#run3", made[3].Id);
            Assert.Equal(5, made[1].Layout.Number);
            Assert.Equal(1944, made[1].CurvesMm[1][0], 6);
            Assert.Equal(3056, made[2].CurvesMm[0][0], 6);
            Assert.Equal(25 + 197.5 * 8, made[1].CurvesMm[0][1], 6);
            Assert.Equal(made[1].CurvesMm[0][1], made[2].CurvesMm[0][1], 6);
            Assert.Equal(4, rep.Runs.Count);
            Assert.True(rep.Runs[1].Trimmed);
            Assert.False(rep.Runs[0].Trimmed);
        }

        [Fact]
        public void TrimWithAHoleAtTheBarsEndLeavesOneStretchAndNamesTheDroppedSliver()
        {
            // The hole runs to 5 mm from the slab edge on the bars' far side: what
            // is left past it, less the clearance, is nothing buildable.
            HostMesh edgeHole = SlabWithHole(0, 0, 6000, 4000, 0, 200, 4000, 1500, 5995, 2500);
            List<StructuralRebarRule> made;
            MatResult r = MatRules.Expand(Rule(Policy("trim", 0, 50), Comp("top_x", X)), edgeHole, Dia12, null, out made);
            Assert.True(r.Ok, r.Why);
            MatTrimmedBar t = r.Components[0].Openings.BarsTrimmed[0];
            Assert.Single(t.SegmentsMm);
            Assert.Equal(25, t.SegmentsMm[0][0], 6);
            Assert.Equal(3944, t.SegmentsMm[0][1], 6);
            Assert.Empty(t.DroppedMm);   // 6001 - 50 is past 5975: nothing left, nothing dropped
        }

        [Fact]
        public void ASingleBarRunIsASingle()
        {
            // Bars at 100 across a 3950 array: 40 gaps of 98.75; with the 6 mm radius
            // the hole catches 1494..2506, and one bar (k = 40, at 3975) is the last
            // run alone once a second hole eats its neighbours.
            HostMesh two = SlabWithHole(0, 0, 6000, 4000, 0, 200, 2000, 3600, 3000, 3900);
            List<StructuralRebarRule> made;
            MatResult r = MatRules.Expand(Rule(Policy("omit"), Comp("top_x", X, 100)), two, Dia12, null, out made);
            Assert.True(r.Ok, r.Why);
            StructuralRebarRule lastRun = made[made.Count - 1];
            Assert.Equal(RebarLayout.Single, lastRun.Layout.Layout);
            Assert.Equal(3975, lastRun.CurvesMm[0][1], 6);
        }

        [Fact]
        public void IgnoreBuildsAsDeclaredAndReportsTheCrossings()
        {
            MatResult r;
            List<StructuralRebarRule> made = Expand(Rule(Policy("ignore"), Comp("top_x", X)), out r);
            Assert.True(r.Ok, r.Why);
            Assert.Single(made);
            Assert.Equal("S1#top_x", made[0].Id);
            Assert.Equal(RebarLayout.MaximumSpacing, made[0].Layout.Layout);
            Assert.Equal(Crossing, r.Components[0].Openings.BarsCrossing.ToArray());
            Assert.Equal(21, r.Components[0].Openings.BarsKept);
            Assert.Equal("ignore", made[0].OpeningContext.Policy);
        }

        [Fact]
        public void AnOpeningBelowTheDeclaredMinimumIsIgnoredAndSaidSo()
        {
            // The hole's largest dimension is its diagonal, 1414 mm.
            MatResult r;
            List<StructuralRebarRule> made = Expand(Rule(Policy("omit", 1500), Comp("top_x", X)), out r);
            Assert.True(r.Ok, r.Why);
            Assert.Single(made);
            Assert.Equal("S1#top_x", made[0].Id);
            MatOpeningReport rep = r.Components[0].Openings;
            Assert.Equal(0, rep.OpeningsConsidered);
            Assert.Equal(1, rep.OpeningsIgnored);
            Assert.Empty(rep.BarsOmitted);
            Assert.Contains("ignored", made[0].OpeningContext.Openings[0].Why);
            Assert.Equal(1414.214, made[0].OpeningContext.Openings[0].DiameterMm, 3);

            List<StructuralRebarRule> considered = Expand(Rule(Policy("omit", 1414), Comp("top_x", X)), out r);
            Assert.Equal(2, considered.Count);
        }

        [Fact]
        public void EveryBarOverTheHoleIsRefusedRatherThanBuildingNothing()
        {
            HostMesh wide = SlabWithHole(0, 0, 6000, 4000, 0, 200, 100, 1500, 5900, 2500);
            List<StructuralRebarRule> made;
            MatResult r = MatRules.Expand(Rule(Policy("omit"), Comp("top_y", Y, 200, side: 200)), wide, Dia12, null, out made);
            Assert.False(r.Ok);
            Assert.Equal(MatRules.CodeOpeningsLeaveNoBars, r.Code);
            Assert.Empty(made);
        }

        [Fact]
        public void TheOtherLayerIsCutByTheSameHoleInItsOwnFrame()
        {
            // top_y bars run along y. Across is up x along = -x, so the array
            // marches from x = 5975 DOWN: bar k sits at x = 5975 - k * 198.333 (30
            // gaps over 5950). The hole spans x 2000..3000, 1994..3006 with the radius.
            MatResult r;
            List<StructuralRebarRule> made = Expand(Rule(Policy("omit"), Comp("top_y", Y)), out r);
            Assert.True(r.Ok, r.Why);
            Assert.Equal(5975, made[0].CurvesMm[0][0], 6);
            var omitted = r.Components[0].Openings.BarsOmitted;
            double pitch = 5950.0 / 30;
            Assert.All(omitted, k => Assert.InRange(5975 - pitch * k, 1994 - 1e-6, 3006 + 1e-6));
            Assert.Equal(Enumerable.Range(0, 31).Count(k => 5975 - pitch * k >= 1994 && 5975 - pitch * k <= 3006),
                         omitted.Count);
            Assert.Equal(6, omitted.Count);
            Assert.Equal(2, made.Count);
        }

        // ---------------------------------------------- boundary counting

        [Fact]
        public void BarsExactlyOnTheHoleEdgeAreClearWithNoRadiusAndCaughtWithOne()
        {
            // fixed_number 41 over the 4000 extent with no side cover: bars at every
            // 100 from 0, so k = 15 sits on the hole's lower edge and k = 25 on its upper.
            var comp = new MatComponentRequest
            {
                Name = "top_x", DirectionMm = X, BarTypeId = "t12", OffsetFromFaceMm = 31,
                EndCoverMm = 0, SideCoverMm = 0,
                Layout = new RebarLayoutRequest { Layout = RebarLayout.FixedNumber, Number = 41, ArrayLengthMm = 4000 }
            };
            MatResult r;
            Expand(Rule(Policy("omit"), comp), out r, NoDia);
            Assert.True(r.Ok, r.Why);
            Assert.Equal(Enumerable.Range(16, 9).ToArray(), r.Components[0].Openings.BarsOmitted.ToArray());

            Expand(Rule(Policy("omit"), comp), out r, Dia12);
            Assert.True(r.Ok, r.Why);
            Assert.Equal(Enumerable.Range(15, 11).ToArray(), r.Components[0].Openings.BarsOmitted.ToArray());
        }

        [Fact]
        public void ASuppressedEndBarIsNotCountedAsAKeptBar()
        {
            var comp = Comp("top_x", X);
            comp.Layout.IncludeLastBar = false;
            MatResult r;
            List<StructuralRebarRule> made = Expand(Rule(Policy("omit"), comp), out r);
            Assert.True(r.Ok, r.Why);
            Assert.Equal(20, r.Components[0].Openings.BarsPlanned);
            Assert.Equal(15, r.Components[0].Openings.BarsKept);
            Assert.Equal(8, made[0].Layout.Number);                       // bars 0..7
            Assert.Equal(25, made[0].CurvesMm[0][1], 6);                   // run 1 starts at bar 0
        }

        // ------------------------------------------------ after the commit

        [Fact]
        public void TheDrawnRunsAreClearOfTheHoleAndABarOverItIsNot()
        {
            MatResult r;
            List<StructuralRebarRule> made = Expand(Rule(Policy("omit"), Comp("top_x", X)), out r);
            MatOpeningContext ctx = made[1].OpeningContext;
            // run 2 as Revit would draw it: bar 0 at its first station, seven offsets of 197.5
            var bar = new List<double[]> { made[1].CurvesMm[0], made[1].CurvesMm[1] };
            var offsets = Enumerable.Range(0, 8).Select(k => 197.5 * k).ToList();
            MatOpeningCheck clear = MatOpenings.CheckBars(ctx, bar, offsets, 2);
            Assert.True(clear.Evaluated);
            Assert.Equal(8, clear.PositionsTested);
            Assert.Empty(clear.Crossing);
            Assert.Equal(0, clear.WorstOverlapMm, 6);

            // a bar the model put over the hole
            var over = new List<double[]> { new double[] { 25, 2000, 169 }, new double[] { 5975, 2000, 169 } };
            MatOpeningCheck bad = MatOpenings.CheckBars(ctx, over, new List<double> { 0 }, 2);
            Assert.Equal(new[] { 0 }, bad.Crossing.ToArray());
            Assert.Equal(1012, bad.WorstOverlapMm, 6);
        }

        [Fact]
        public void ATrimmedBarThatStopsInsideTheClearanceIsNamed()
        {
            MatResult r;
            List<StructuralRebarRule> made = Expand(Rule(Policy("trim", 0, 50), Comp("top_x", X)), out r);
            MatOpeningContext ctx = made[1].OpeningContext;
            var ok = new List<double[]> { made[1].CurvesMm[0], made[1].CurvesMm[1] };
            MatOpeningCheck good = MatOpenings.CheckBars(ctx, ok, new List<double> { 0 }, 2);
            Assert.Empty(good.Crossing);
            Assert.Empty(good.ShortOfClearance);
            Assert.Equal(50, good.MinGapMm.Value, 6);

            var tooClose = new List<double[]> { made[1].CurvesMm[0], new[] { 1970.0, made[1].CurvesMm[1][1], made[1].CurvesMm[1][2] } };
            MatOpeningCheck close = MatOpenings.CheckBars(ctx, tooClose, new List<double> { 0 }, 2);
            Assert.Empty(close.Crossing);
            Assert.Equal(new[] { 0 }, close.ShortOfClearance.ToArray());
            Assert.Equal(24, close.MinGapMm.Value, 6);
        }

        [Fact]
        public void TheReportSerialisesEveryDecision()
        {
            MatResult r;
            List<StructuralRebarRule> made = Expand(Rule(Policy("trim", 0, 50), Comp("top_x", X)), out r);
            var json = made[0].OpeningContext.ToJson();
            Assert.Equal("trim", (string)json["policy"]);
            Assert.Equal(5, json["component"]["bars_trimmed"].Count());
            Assert.Equal(1919, (double)json["component"]["bars_trimmed"][0]["segment_lengths_mm"][0], 3);
            Assert.Equal(2919, (double)json["component"]["bars_trimmed"][0]["segment_lengths_mm"][1], 3);
            Assert.Equal(4, json["component"]["runs"].Count());
            Assert.True((bool)json["openings"][0]["considered"]);
            Assert.NotNull(json["component"]["no_replacement_steel"]);
        }

        [Fact]
        public void TheNewCodesArePublishedAndDistinct()
        {
            Assert.Contains(MatRules.CodeOpeningsNeedAPolicy, MatRules.AllCodes);
            Assert.Contains(MatRules.CodeOpeningsNotExtractable, MatRules.AllCodes);
            Assert.Contains(MatRules.CodeOpeningsLeaveNoBars, MatRules.AllCodes);
            Assert.Equal(MatRules.AllCodes.Length, MatRules.AllCodes.Distinct().Count());
        }
    }
}
