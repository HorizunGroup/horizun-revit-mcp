// -----------------------------------------------------------------------------
// A mat that follows its face (acero 3.0). A roof is not one plane: measured on a
// real roof, two floors - a flat strip and a 6% slope meeting at a break - carry
// top and bottom bars that run across both, bent at the break, each layer at its
// cover from the face it lies under, and bars across the slope that are each
// straight but at their own height. These pin that geometry on meshes built in
// code with the real shape of the data: closed, welded, outward-wound prisms, the
// two roof pieces as two separate elements that share no vertex.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class MatFollowFaceTests
    {
        private static readonly double[] Up = { 0, 0, 1 };
        private static readonly double[] Down = { 0, 0, -1 };
        private static readonly double[] X = { 1, 0, 0 };
        private static readonly double[] Y = { 0, 1, 0 };
        private const double Slope = 0.06;
        private static readonly double SlopeLen = Math.Sqrt(1 + Slope * Slope);

        // ------------------------------------------------------------ meshes

        /// <summary>
        /// A closed prism over a plan grid: the cells marked true are concrete, the top and the bottom
        /// are the given height functions (planar per cell), the sides vertical. Every vertex is a grid
        /// point shared by the triangles that use it - welded, as HostSolidMesh delivers a host.
        /// </summary>
        internal static HostMesh Prism(double[] xs, double[] ys, bool[,] cell,
                                       Func<double, double, double> top, Func<double, double, double> bottom, string source)
        {
            var m = new HostMesh { Source = source };
            var topIx = new Dictionary<(int, int), int>();
            var botIx = new Dictionary<(int, int), int>();
            int T(int i, int j)
            {
                if (!topIx.TryGetValue((i, j), out int k)) { k = m.AddVertex(xs[i], ys[j], top(xs[i], ys[j])); topIx[(i, j)] = k; }
                return k;
            }
            int B(int i, int j)
            {
                if (!botIx.TryGetValue((i, j), out int k)) { k = m.AddVertex(xs[i], ys[j], bottom(xs[i], ys[j])); botIx[(i, j)] = k; }
                return k;
            }
            int nx = xs.Length - 1, ny = ys.Length - 1;
            bool Has(int i, int j) => i >= 0 && j >= 0 && i < nx && j < ny && cell[i, j];
            for (int i = 0; i < nx; i++)
                for (int j = 0; j < ny; j++)
                {
                    if (!cell[i, j]) continue;
                    m.AddTriangle(T(i, j), T(i + 1, j), T(i + 1, j + 1));
                    m.AddTriangle(T(i, j), T(i + 1, j + 1), T(i, j + 1));
                    m.AddTriangle(B(i, j), B(i + 1, j + 1), B(i + 1, j));
                    m.AddTriangle(B(i, j), B(i, j + 1), B(i + 1, j + 1));
                    // The cell's boundary counter-clockwise from above; a side wherever the neighbour is air.
                    var edges = new[]
                    {
                        ((i, j), (i + 1, j), Has(i, j - 1)),
                        ((i + 1, j), (i + 1, j + 1), Has(i + 1, j)),
                        ((i + 1, j + 1), (i, j + 1), Has(i, j + 1)),
                        ((i, j + 1), (i, j), Has(i - 1, j))
                    };
                    foreach (var e in edges)
                    {
                        if (e.Item3) continue;
                        int ta = T(e.Item1.Item1, e.Item1.Item2), tb = T(e.Item2.Item1, e.Item2.Item2);
                        int ba = B(e.Item1.Item1, e.Item1.Item2), bb = B(e.Item2.Item1, e.Item2.Item2);
                        m.AddTriangle(ba, bb, tb);
                        m.AddTriangle(ba, tb, ta);
                    }
                }
            return m;
        }

        private static bool[,] Full(int nx, int ny)
        {
            var c = new bool[nx, ny];
            for (int i = 0; i < nx; i++) for (int j = 0; j < ny; j++) c[i, j] = true;
            return c;
        }

        // The roof: a flat strip 0..1500 and a slope 1500..5500 rising 6%, 3000 wide, 120 deep
        // measured vertically, the strip's top at 3000. Two elements; they share no vertex.
        private static HostMesh Strip() =>
            Prism(new double[] { 0, 1500 }, new double[] { 0, 3000 }, Full(1, 1), (x, y) => 3000, (x, y) => 2880, "flat strip");
        private static HostMesh SlopePiece() =>
            Prism(new double[] { 1500, 5500 }, new double[] { 0, 3000 }, Full(1, 1),
                  (x, y) => 3000 + Slope * (x - 1500), (x, y) => 2880 + Slope * (x - 1500), "6% slope");

        private static List<MatHostPart> Roof() => new List<MatHostPart>
        {
            new MatHostPart { ElementId = 101, Mesh = Strip() },
            new MatHostPart { ElementId = 102, Mesh = SlopePiece() }
        };

        private static MatComponentRequest Follow(string name, double[] dir, double offset, double spacing = 200,
                                                  double end = 25, double side = 25, double minFacet = 100)
        {
            return new MatComponentRequest
            {
                Name = name, DirectionMm = dir, BarTypeId = "t12", OffsetFromFaceMm = offset, EndCoverMm = end, SideCoverMm = side,
                Layout = new RebarLayoutRequest { Layout = RebarLayout.MaximumSpacing, SpacingMm = spacing },
                FollowFace = new StructuralFollowFace { PlanarityMm = 0.5, MinFacetMm = minFacet }
            };
        }

        private static StructuralMatRule Rule(string id, double[] normal, long host, IEnumerable<long> into, params MatComponentRequest[] comps)
        {
            return new StructuralMatRule
            {
                Id = id, FaceNormalMm = normal, Mark = "R",
                Host = new StructuralHostSelector { ElementIds = new List<long> { host } },
                Into = into == null ? new List<long>() : into.ToList(),
                Components = comps.ToList()
            };
        }

        private static double Dia(string id) => 12;

        private static double PlaneDistance(double[] p, double[] n, double[] on)
        {
            double l = Math.Sqrt(n[0] * n[0] + n[1] * n[1] + n[2] * n[2]);
            return Math.Abs((p[0] - on[0]) * n[0] + (p[1] - on[1]) * n[1] + (p[2] - on[2]) * n[2]) / l;
        }

        // The offset planes of the roof's top: the strip's 40 under z = 3000, the slope's 40 under
        // its own plane along its own normal - 40 * sqrt(1 + 0.06^2) measured vertically.
        private static double FlatTopOffset(double off) => 3000 - off;
        private static double SlopeTopOffset(double x, double off) => 3000 + Slope * (x - 1500) - off * SlopeLen;

        // ----------------------------------------------- the roof, along the slope

        [Fact]
        public void Top_bars_along_the_slope_run_across_both_pieces_bent_where_the_offset_planes_meet()
        {
            MatComponentRequest c = Follow("top_x", X, 40);
            c.Start = new StructuralTermination { HookTypeId = "h180" };
            c.End = new StructuralTermination { HookTypeId = "h180" };
            MatResult r = MatRules.Expand(Rule("R1", Up, 101, new long[] { 102 }, c), Roof(), Dia, out List<StructuralRebarRule> rules);

            Assert.True(r.Ok, r.Code + ": " + r.Why);
            // Every station has the same section: one shape, one Revit set, the plain id.
            StructuralRebarRule bar = Assert.Single(rules);
            Assert.Equal("R1#top_x", bar.Id);
            Assert.Equal(3, bar.CurvesMm.Count);

            // THE BEND: where 3000 - 40 meets the slope's offset, 1.199 mm past the break - not AT the
            // break, where the bar would sit 40 under one face and 39.93 under the other.
            double xBend = 1500 + 40 * (SlopeLen - 1) / Slope;
            Assert.Equal(25, bar.CurvesMm[0][0], 6);
            Assert.Equal(FlatTopOffset(40), bar.CurvesMm[0][2], 6);
            Assert.Equal(xBend, bar.CurvesMm[1][0], 6);
            Assert.Equal(FlatTopOffset(40), bar.CurvesMm[1][2], 6);
            Assert.Equal(5500 - 25, bar.CurvesMm[2][0], 6);
            Assert.Equal(SlopeTopOffset(5475, 40), bar.CurvesMm[2][2], 6);

            // THE COVER, to the face each stretch lies under: 40 to the strip's top on the first leg and
            // 40 to the slope's top on the second, measured perpendicular to each.
            double[] slopeN = { -Slope, 0, 1 };
            double[] onSlope = { 1500, 0, 3000 };
            Assert.Equal(40, bar.CurvesMm[0][2] < 3000 ? 3000 - bar.CurvesMm[0][2] : double.NaN, 6);
            Assert.Equal(40, PlaneDistance(bar.CurvesMm[1], slopeN, onSlope), 6);
            Assert.Equal(40, PlaneDistance(bar.CurvesMm[2], slopeN, onSlope), 6);

            // ONE HOST: most of the bar lies on the slope, so the slope hosts it and it runs into the strip,
            // and containment is measured against both.
            Assert.Equal(new long[] { 102 }, bar.Host.ElementIds);
            Assert.Equal(new long[] { 101 }, bar.Into);
            Assert.True(bar.ScopedByMat);
            // THE HOOKS stay on the bar's two ends, which are at the face's edges.
            Assert.Equal("h180", bar.Start.HookTypeId);
            Assert.Equal("h180", bar.End.HookTypeId);
            // The bars march across the roof, the normal of their own plane.
            Assert.Equal(new double[] { 0, 1, 0 }, bar.NormalMm.Select(v => Math.Round(v, 9)).ToArray());
            Assert.Equal(RebarLayout.MaximumSpacing, bar.Layout.Layout);

            Assert.NotNull(bar.FollowFace);
            JObject j = bar.FollowFace.ToJson();
            Assert.Equal(1, (int)j["bends"]);
            Assert.Equal(102, (long)j["host_element_id"]);
            Assert.Equal(2, ((JArray)j["facets_under"]).Count);
        }

        [Fact]
        public void Bottom_bars_bend_on_the_other_side_of_the_break_because_from_below_it_is_a_ridge()
        {
            MatResult r = MatRules.Expand(Rule("R2", Down, 101, new long[] { 102 }, Follow("bot_x", X, 40)), Roof(), Dia,
                                          out List<StructuralRebarRule> rules);
            Assert.True(r.Ok, r.Code + ": " + r.Why);
            StructuralRebarRule bar = Assert.Single(rules);
            Assert.Equal(3, bar.CurvesMm.Count);
            // From below the break is convex: the offset planes meet 1.199 mm BEFORE it, under the strip.
            double xBend = 1500 - 40 * (SlopeLen - 1) / Slope;
            Assert.Equal(xBend, bar.CurvesMm[1][0], 6);
            Assert.Equal(2880 + 40, bar.CurvesMm[0][2], 6);
            Assert.Equal(2880 + 40, bar.CurvesMm[1][2], 6);
            Assert.Equal(2880 + Slope * (5475 - 1500) + 40 * SlopeLen, bar.CurvesMm[2][2], 6);
            Assert.Equal(40, PlaneDistance(bar.CurvesMm[2], new[] { Slope, 0, -1 }, new double[] { 1500, 0, 2880 }), 6);
        }

        // ------------------------------------------------ the roof, across the slope

        [Fact]
        public void Bars_across_the_slope_are_each_at_their_own_height_and_equal_heights_share_a_set()
        {
            MatResult r = MatRules.Expand(Rule("R1", Up, 101, new long[] { 102 }, Follow("top_y", Y, 52)), Roof(), Dia,
                                          out List<StructuralRebarRule> rules);
            Assert.True(r.Ok, r.Code + ": " + r.Why);
            MatComponentPlan plan = Assert.Single(r.Components);

            // Every bar straight, every one at the offset face's height at its own station.
            int barsOnStrip = 0, barsOnSlope = 0;
            foreach (StructuralRebarRule b in rules)
            {
                Assert.Equal(2, b.CurvesMm.Count);
                double x = b.CurvesMm[0][0];
                Assert.Equal(x, b.CurvesMm[1][0], 9);
                Assert.Equal(b.CurvesMm[0][2], b.CurvesMm[1][2], 9);
                // In the valley the bar is under the HIGHER of the two offset planes.
                double expected = Math.Max(FlatTopOffset(52), SlopeTopOffset(x, 52));
                Assert.Equal(expected, b.CurvesMm[0][2], 6);
                Assert.Equal(25, b.CurvesMm[0][1], 6);
                Assert.Equal(2975, b.CurvesMm[1][1], 6);
                if (x < 1500) barsOnStrip += b.Layout.Layout == RebarLayout.Single ? 1 : b.Layout.Number.Value;
                else { barsOnSlope++; Assert.Equal(RebarLayout.Single, b.Layout.Layout); }
            }
            // THE STRIP'S STATIONS share one height and are one set; each slope station is its own.
            List<StructuralRebarRule> stripSets = rules.Where(b => b.CurvesMm[0][0] < 1500).ToList();
            Assert.Single(stripSets);
            Assert.True(barsOnStrip > 1);
            Assert.Equal(plan.FollowStations.Value, barsOnStrip + barsOnSlope);
            Assert.Equal(1 + barsOnSlope, rules.Count);
            Assert.Equal(rules.Count, plan.FollowRuns.Value);
            // Deterministic ids, in station order: the audit matches on them.
            Assert.Equal(Enumerable.Range(1, rules.Count).Select(i => "R1#top_y#run" + i), rules.Select(b => b.Id));
            // The strip's bars are hosted by the strip, the slope's by the slope - nothing runs into anything.
            foreach (StructuralRebarRule b in rules)
            {
                Assert.Empty(b.Into);
                if (b.CurvesMm[0][0] < 1500) Assert.Equal(new long[] { 101 }, b.Host.ElementIds);
                else Assert.Equal(new long[] { 102 }, b.Host.ElementIds);
            }
        }

        [Fact]
        public void A_station_on_the_break_line_takes_the_crease_not_the_face_it_happens_to_be_over()
        {
            // The array is placed so one bar sits 0.6 mm past the break, inside the 1.199 mm where the
            // strip's offset plane still governs (a valley: the higher of the two).
            var c = Follow("top_y", Y, 40, 200);
            c.Layout = new RebarLayoutRequest { Layout = RebarLayout.Single };
            c.SpreadFromMm = 5500 - 1500.6;   // across runs towards -x: 0 at x = 5500
            c.SpreadToMm = 5500 - 1500.6 + 1;
            c.SideCoverMm = 25;
            MatResult r = MatRules.Expand(Rule("R1", Up, 101, new long[] { 102 }, c), Roof(), Dia, out List<StructuralRebarRule> rules);
            Assert.True(r.Ok, r.Code + ": " + r.Why);
            StructuralRebarRule b = Assert.Single(rules);
            Assert.Equal(1500.6, b.CurvesMm[0][0], 6);
            Assert.Equal(FlatTopOffset(40), b.CurvesMm[0][2], 6);
            Assert.True(FlatTopOffset(40) > SlopeTopOffset(1500.6, 40));
        }

        // ------------------------------------------- a slab of variable thickness

        // Flat bottom at 0, the top rising from 200 to 400 over 4000: one element, two non-parallel faces.
        private static HostMesh Wedge() =>
            Prism(new double[] { 0, 4000 }, new double[] { 0, 3000 }, Full(1, 1), (x, y) => 200 + 0.05 * x, (x, y) => 0, "wedge");

        [Fact]
        public void A_variable_thickness_slab_keeps_its_bottom_bars_flat_and_lays_its_top_bars_on_the_slope()
        {
            var parts = new List<MatHostPart> { new MatHostPart { ElementId = 7, Mesh = Wedge() } };
            MatResult bot = MatRules.Expand(Rule("V1", Down, 7, null, Follow("bx", X, 40)), parts, Dia, out List<StructuralRebarRule> bottom);
            Assert.True(bot.Ok, bot.Code + ": " + bot.Why);
            StructuralRebarRule b = Assert.Single(bottom);
            Assert.Equal(2, b.CurvesMm.Count);
            Assert.Equal(40, b.CurvesMm[0][2], 9);
            Assert.Equal(40, b.CurvesMm[1][2], 9);
            // One element: the rule's own host selector, no into.
            Assert.Equal(new long[] { 7 }, b.Host.ElementIds);
            Assert.Empty(b.Into);

            MatResult top = MatRules.Expand(Rule("V2", Up, 7, null, Follow("tx", X, 40), Follow("ty", Y, 52)), parts, Dia,
                                            out List<StructuralRebarRule> tops);
            Assert.True(top.Ok, top.Code + ": " + top.Why);
            StructuralRebarRule tx = Assert.Single(tops, t => t.Id == "V2#tx");
            Assert.Equal(2, tx.CurvesMm.Count);
            double[] n = { -0.05, 0, 1 };
            double[] on = { 0, 0, 200 };
            Assert.Equal(40, PlaneDistance(tx.CurvesMm[0], n, on), 6);
            Assert.Equal(40, PlaneDistance(tx.CurvesMm[1], n, on), 6);
            Assert.Equal(25, tx.CurvesMm[0][0], 6);
            Assert.Equal(3975, tx.CurvesMm[1][0], 6);
            // Across the slope every bar is its own height: as many sets as stations, all singles.
            List<StructuralRebarRule> ty = tops.Where(t => t.Id.StartsWith("V2#ty", StringComparison.Ordinal)).ToList();
            Assert.True(ty.Count > 10);
            Assert.All(ty, t => Assert.Equal(RebarLayout.Single, t.Layout.Layout));
            Assert.All(ty, t => Assert.Equal(40 + 12, PlaneDistance(t.CurvesMm[0], n, on), 6));
            Assert.Equal(ty.Count, ty.Select(t => Math.Round(t.CurvesMm[0][2], 6)).Distinct().Count());
        }

        // ------------------------------------------------------- an opening

        // 6000 x 4000, flat top at 200, a 1000 x 1000 hole in the middle.
        private static HostMesh Holed()
        {
            var cells = Full(3, 3);
            cells[1, 1] = false;
            return Prism(new double[] { 0, 2500, 3500, 6000 }, new double[] { 0, 1500, 2500, 4000 }, cells, (x, y) => 200, (x, y) => 0, "holed");
        }

        [Fact]
        public void Over_an_opening_the_followed_bars_split_into_runs_with_deterministic_ids()
        {
            var parts = new List<MatHostPart> { new MatHostPart { ElementId = 9, Mesh = Holed() } };
            StructuralMatRule rule = Rule("H1", Up, 9, null, Follow("x", X, 40, 150));
            rule.Openings = new StructuralMatOpenings { Policy = StructuralMatOpenings.PolicyOmit, MinimumSizeMm = 300 };
            MatResult r = MatRules.Expand(rule, parts, Dia, out List<StructuralRebarRule> rules);
            Assert.True(r.Ok, r.Code + ": " + r.Why);
            Assert.Equal(1, r.OpeningsFound);
            Assert.Equal(new[] { "H1#x#run1", "H1#x#run2" }, rules.Select(x => x.Id));
            // The first run ends below the hole, the second starts above it; no bar of either is over it.
            double lastBelow = rules[0].CurvesMm[0][1] + rules[0].Layout.SpacingMm.Value * (rules[0].Layout.Number.Value - 1);
            Assert.True(lastBelow + 6 <= 1500 + 1e-6);
            Assert.True(rules[1].CurvesMm[0][1] - 6 >= 2500 - 1e-6);
            Assert.All(rules, x => Assert.NotNull(x.OpeningContext));

            // TRIM: the bars through the hole become two stretches each, hooks only at the outer ends.
            rule.Openings = new StructuralMatOpenings { Policy = StructuralMatOpenings.PolicyTrim, MinimumSizeMm = 300, ClearanceMm = 50 };
            rule.Components[0].Start = new StructuralTermination { HookTypeId = "h90" };
            rule.Components[0].End = new StructuralTermination { HookTypeId = "h90" };
            r = MatRules.Expand(rule, parts, Dia, out rules);
            Assert.True(r.Ok, r.Code + ": " + r.Why);
            Assert.Equal(new[] { "H1#x#run1", "H1#x#run2#seg1", "H1#x#run2#seg2", "H1#x#run3" }, rules.Select(x => x.Id));
            StructuralRebarRule left = rules[1], right = rules[2];
            // The clearance is measured from the bar's BODY, as on a flat mat: the hole's edge, the bar's radius, 50.
            Assert.Equal(2500 - 6 - 50, left.CurvesMm[1][0], 6);
            Assert.Equal(3500 + 6 + 50, right.CurvesMm[0][0], 6);
            Assert.Equal("h90", left.Start.HookTypeId);
            Assert.Null(left.End.HookTypeId);
            Assert.Null(right.Start.HookTypeId);
            Assert.Equal("h90", right.End.HookTypeId);

            // NO POLICY: refused by name, as a flat mat is.
            rule.Openings = null;
            r = MatRules.Expand(rule, parts, Dia, out rules);
            Assert.Equal(MatRules.CodeOpeningsNeedAPolicy, r.Code);
            Assert.Empty(rules);
        }

        [Fact]
        public void A_sloped_face_with_an_opening_applies_the_policy_to_the_hole_it_reads_for_itself()
        {
            // The wedge's top, holed: no triangle lies in the plane of its highest point, which is where
            // a flat mat reads its rings - the follower reads the facing triangles' own rings instead.
            var cells = Full(3, 3);
            cells[1, 1] = false;
            HostMesh m = Prism(new double[] { 0, 1500, 2500, 4000 }, new double[] { 0, 1000, 2000, 3000 }, cells,
                               (x, y) => 200 + 0.05 * x, (x, y) => 0, "holed wedge");
            var parts = new List<MatHostPart> { new MatHostPart { ElementId = 4, Mesh = m } };
            StructuralMatRule rule = Rule("W1", Up, 4, null, Follow("x", X, 40, 150));
            rule.Openings = new StructuralMatOpenings { Policy = StructuralMatOpenings.PolicyOmit, MinimumSizeMm = 300 };
            MatResult r = MatRules.Expand(rule, parts, Dia, out List<StructuralRebarRule> rules);
            Assert.True(r.Ok, r.Code + ": " + r.Why);
            Assert.Equal(1, r.OpeningsFound);
            Assert.Equal(new[] { "W1#x#run1", "W1#x#run2" }, rules.Select(x => x.Id));
            double[] n = { -0.05, 0, 1 };
            Assert.All(rules, x => Assert.Equal(40, PlaneDistance(x.CurvesMm[0], n, new double[] { 0, 0, 200 }), 6));
        }

        // ---------------------------------------------------------- refusals

        [Fact]
        public void A_curved_face_is_refused_rather_than_bent_at_its_tessellation()
        {
            // A barrel top in six facets, every triangle of it the tessellation of a curved face.
            double[] xs = { 0, 500, 1000, 1500, 2000, 2500, 3000 };
            Func<double, double, double> top = (x, y) => 300 - (x - 1500) * (x - 1500) / 20000.0;
            HostMesh m = Prism(xs, new double[] { 0, 2000 }, Full(6, 1), top, (x, y) => 0, "barrel");
            m.AnyCurvedFace = true;
            for (int i = 0; i < m.Triangles.Count; i++)
            {
                int[] t = m.Triangles[i];
                if (m.Vertices[t[0]][2] > 1 && m.Vertices[t[1]][2] > 1 && m.Vertices[t[2]][2] > 1) m.CurvedTriangles.Add(i);
            }
            var parts = new List<MatHostPart> { new MatHostPart { ElementId = 5, Mesh = m } };
            MatResult r = MatRules.Expand(Rule("C1", Up, 5, null, Follow("x", X, 40)), parts, Dia, out List<StructuralRebarRule> rules);
            Assert.Equal(MatRules.CodeFaceNotPlanar, r.Code);
            Assert.Contains("CURVED", r.Why);
            Assert.Contains("degrees apart", r.Why);
            Assert.Empty(rules);

            // The same mesh with its curved faces unattributed is UNKNOWN, and unknown is not planar.
            m.CurvedTriangles.Clear();
            r = MatRules.Expand(Rule("C1", Up, 5, null, Follow("x", X, 40)), parts, Dia, out rules);
            Assert.Equal(MatRules.CodeFaceNotPlanar, r.Code);
            Assert.Contains("not attributed", r.Why);
        }

        [Fact]
        public void A_facet_shorter_than_the_declared_minimum_is_refused_with_its_length()
        {
            // A 60 mm chamfer facet between two flat stretches at different heights.
            Func<double, double, double> top = (x, y) => x <= 2000 ? 300 : x >= 2060 ? 330 : 300 + (x - 2000) * 0.5;
            HostMesh m = Prism(new double[] { 0, 2000, 2060, 4000 }, new double[] { 0, 2000 }, Full(3, 1), top, (x, y) => 0, "chamfer");
            var parts = new List<MatHostPart> { new MatHostPart { ElementId = 5, Mesh = m } };
            MatResult r = MatRules.Expand(Rule("F1", Up, 5, null, Follow("x", X, 40, 200, 25, 25, 100)), parts, Dia, out _);
            Assert.Equal(MatRules.CodeFacetTooSmall, r.Code);
            Assert.Contains("min_facet_mm of 100 mm", r.Why);

            // Declared smaller, it is a real break and the bar bends twice.
            r = MatRules.Expand(Rule("F1", Up, 5, null, Follow("x", X, 40, 200, 25, 25, 20)), parts, Dia, out List<StructuralRebarRule> rules);
            Assert.True(r.Ok, r.Code + ": " + r.Why);
            Assert.Equal(4, Assert.Single(rules).CurvesMm.Count);
        }

        [Fact]
        public void A_face_that_steps_under_the_bar_is_refused_with_the_step()
        {
            var parts = new List<MatHostPart>
            {
                new MatHostPart { ElementId = 1, Mesh = Prism(new double[] { 0, 2000 }, new double[] { 0, 2000 }, Full(1, 1), (x, y) => 200, (x, y) => 0, "low") },
                new MatHostPart { ElementId = 2, Mesh = Prism(new double[] { 2000, 4000 }, new double[] { 0, 2000 }, Full(1, 1), (x, y) => 250, (x, y) => 0, "high") }
            };
            MatResult r = MatRules.Expand(Rule("S1", Up, 1, new long[] { 2 }, Follow("x", X, 40)), parts, Dia, out _);
            Assert.Equal(MatRules.CodeFaceSteps, r.Code);
            Assert.Contains("steps by 50 mm", r.Why);
        }

        [Fact]
        public void Into_beside_a_flat_component_is_refused_because_nothing_would_read_it()
        {
            MatComponentRequest flat = Follow("flat", Y, 52);
            flat.FollowFace = null;
            MatResult r = MatRules.Expand(Rule("R9", Up, 101, new long[] { 102 }, Follow("top_x", X, 40), flat), Roof(), Dia, out _);
            Assert.Equal(MatRules.CodeIntoNeedsFollowFace, r.Code);
        }

        [Fact]
        public void A_flat_mat_is_untouched_by_the_follower()
        {
            // The same slab, one rule flat and one followed: on a flat face both give the same bar.
            HostMesh box = HostMesh.Box(new double[] { 0, 0, 0 }, new double[] { 6000, 4000, 200 });
            MatComponentRequest flat = Follow("x", X, 31, 150);
            flat.FollowFace = null;
            MatResult a = MatRules.Expand(Rule("A", Up, 1, null, flat), box, Dia, out List<StructuralRebarRule> ra);
            MatResult b = MatRules.Expand(Rule("A", Up, 1, null, Follow("x", X, 31, 150)),
                                          new List<MatHostPart> { new MatHostPart { Mesh = box } }, Dia, out List<StructuralRebarRule> rb);
            Assert.True(a.Ok && b.Ok, a.Why + b.Why);
            StructuralRebarRule fa = Assert.Single(ra), fb = Assert.Single(rb);
            Assert.Equal(fa.Id, fb.Id);
            for (int i = 0; i < 2; i++)
                for (int k = 0; k < 3; k++) Assert.Equal(fa.CurvesMm[i][k], fb.CurvesMm[i][k], 6);
            Assert.Equal(fa.Layout.Layout, fb.Layout.Layout);
            Assert.Null(fa.FollowFace);
        }

        [Fact]
        public void Every_follow_face_code_is_in_the_closed_set()
        {
            foreach (string code in new[] { MatRules.CodeFollowFaceNotUsable, MatRules.CodeNoFaceToFollow, MatRules.CodeNoFaceUnderBar,
                                             MatRules.CodeFaceSteps, MatRules.CodeFaceNotPlanar, MatRules.CodeFacetTooSmall,
                                             MatRules.CodeIntoNeedsFollowFace })
                Assert.Contains(code, MatRules.AllCodes);
            Assert.Equal(MatRules.AllCodes.Length, MatRules.AllCodes.Distinct().Count());
        }

        // ------------------------------------------------------------ parsing

        private static JObject Set(JObject comp, JArray into = null)
        {
            var mat = new JObject
            {
                ["id"] = "R1", ["host"] = new JObject { ["element_ids"] = new JArray(101) }, ["face_normal"] = new JArray(0, 0, 1),
                ["components"] = new JArray(comp)
            };
            if (into != null) mat["into"] = into;
            return new JObject
            {
                ["schema"] = "horizun.structural-requirements/1", ["units"] = "millimeter",
                ["requirement_set"] = new JObject { ["id"] = "t", ["version"] = "1" },
                ["bar_types"] = new JArray(new JObject { ["id"] = "t12", ["type_name"] = "12M" }),
                ["mat_rules"] = new JArray(mat)
            };
        }

        private static JObject Comp(JToken followFace)
        {
            var c = new JObject
            {
                ["name"] = "top_x", ["direction"] = new JArray(1, 0, 0), ["bar_type"] = "t12", ["offset_from_face_mm"] = 40,
                ["end_cover_mm"] = 25, ["side_cover_mm"] = 25,
                ["layout"] = new JObject { ["rule"] = "maximum_spacing", ["spacing_mm"] = 200 }
            };
            if (followFace != null) c["follow_face"] = followFace;
            return c;
        }

        [Fact]
        public void The_set_declares_follow_face_and_into_and_refuses_what_is_missing()
        {
            StructuralRequirementSet s = StructuralRequirementSet.Load(Set(Comp(new JObject { ["planarity_mm"] = 0.5, ["min_facet_mm"] = 100 }), new JArray(102)));
            Assert.True(s.Ok, s.Code + ": " + s.Error);
            StructuralMatRule m = Assert.Single(s.MatRules);
            Assert.Equal(new long[] { 102 }, m.Into);
            Assert.Equal(0.5, m.Components[0].FollowFace.PlanarityMm);
            Assert.Equal(100, m.Components[0].FollowFace.MinFacetMm);

            s = StructuralRequirementSet.Load(Set(Comp(new JObject { ["min_facet_mm"] = 100 })));
            Assert.Equal(StructuralRequirementSet.CodeMissing, s.Code);
            Assert.Contains("planarity_mm", s.Error);

            s = StructuralRequirementSet.Load(Set(Comp(new JObject { ["planarity_mm"] = 0.5, ["min_facet_mm"] = 100, ["tolerance"] = 1 })));
            Assert.Equal(StructuralRequirementSet.CodeUnknownKey, s.Code);

            s = StructuralRequirementSet.Load(Set(Comp(new JObject { ["planarity_mm"] = 0, ["min_facet_mm"] = 100 })));
            Assert.Equal(StructuralRequirementSet.CodeUnknownValue, s.Code);

            s = StructuralRequirementSet.Load(Set(Comp(true)));
            Assert.Equal(StructuralRequirementSet.CodeSchema, s.Code);

            s = StructuralRequirementSet.Load(Set(Comp(new JObject { ["planarity_mm"] = 0.5, ["min_facet_mm"] = 100 }), new JArray(101)));
            Assert.Equal(StructuralRequirementSet.CodeUnknownValue, s.Code);
            Assert.Contains("own host", s.Error);
        }
    }
}
