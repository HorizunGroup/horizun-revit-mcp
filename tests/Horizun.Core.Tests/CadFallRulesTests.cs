// -----------------------------------------------------------------------------
// Horizun Core tests — original Horizun code.
//
// A DRAIN THAT FALLS THE WRONG WAY LOOKS EXACTLY LIKE ONE THAT FALLS THE RIGHT WAY.
//
// In plan they are the same line. In a section they are the same line. The only
// thing that distinguishes them is which end is higher, and the drawing does not
// say — it has a first point and a second point, which is the order somebody
// clicked.
//
// So every case here is about direction and about refusing to invent it. The
// arithmetic is one multiplication; the value is entirely in what the walk will
// not conclude: no outfall means no downhill, a run with no declared slope stops
// the walk rather than being laid level, a node the outfall reaches two ways with
// two different inverts is a looped drain and a design decision, and a run this
// outfall never reaches is named rather than quietly excluded.
//
// NOT RUN in this phase. Written to be run when running tests is authorised.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class CadFallRulesTests
    {
        private static CadSegment Seg(double x1, double y1, double x2, double y2,
                                      string layer = "P-SANI") =>
            new CadSegment(new CadPoint(x1, y1), new CadPoint(x2, y2), layer,
                           CadCurveKind.Line, 0, x1 + "," + y1 + "-" + x2 + "," + y2);

        private static CadNetworkOptions Options() => new CadNetworkOptions
        {
            ConnectToleranceMm = 1.0,
            CollinearToleranceDegrees = 2.0,
            GapReviewDistanceMm = 50.0,
            ThroughToleranceDegrees = 15.0
        };

        /// <summary>A run of 10 m and a branch of 5 m, meeting at a corner.</summary>
        private static CadNetwork Branch(double? slope = 1.0)
        {
            var segments = new List<CadSegment>
            {
                Seg(0, 0, 10000, 0),
                Seg(10000, 0, 10000, 5000, "P-BRANCH")
            };
            return CadNetworkRules.Build(segments, Options(), layer =>
                new CadNetworkRules.CadRunDeclaration { SlopePercent = slope });
        }

        private static string NodeAt(CadNetwork net, double x, double y) =>
            CadFallRules.NodeNear(net, new CadPoint(x, y), 1.0);

        [Fact]
        public void The_invert_rises_away_from_the_outfall_by_length_times_slope()
        {
            CadNetwork net = Branch();
            string outfall = NodeAt(net, 0, 0);
            Assert.NotNull(outfall);

            CadFall fall = CadFallRules.Compute(net, outfall, 0, r => r.SlopePercent);
            Assert.True(fall.Ok);

            // 10 m at 1% is 100 mm; the branch adds 5 m at 1%, so 150 mm.
            CadInvert corner = fall.Inverts.Single(i => i.NodeKey == NodeAt(net, 10000, 0));
            Assert.Equal(100, corner.RiseMm, 3);

            CadInvert top = fall.Inverts.Single(i => i.NodeKey == NodeAt(net, 10000, 5000));
            Assert.Equal(150, top.RiseMm, 3);
            Assert.Equal(15000, top.PathLengthMm, 3);
            Assert.Equal(2, top.Hops);
        }

        [Fact]
        public void The_outfall_invert_shifts_every_height_together()
        {
            CadNetwork net = Branch();
            CadFall fall = CadFallRules.Compute(net, NodeAt(net, 0, 0), -2500, r => r.SlopePercent);

            CadRunFall main = fall.Runs.Single(r => Math.Abs(r.StartZMm - r.EndZMm) > 75);
            Assert.Equal(-2500, Math.Min(main.StartZMm, main.EndZMm), 3);
            Assert.Equal(-2400, Math.Max(main.StartZMm, main.EndZMm), 3);
        }

        [Fact]
        public void The_upstream_end_is_decided_by_the_network_not_by_drawing_order()
        {
            // THE WHOLE POINT. The same two runs, drained from the other end,
            // must slope the other way - and the drawing is identical.
            CadNetwork net = Branch();

            CadFall down = CadFallRules.Compute(net, NodeAt(net, 0, 0), 0, r => r.SlopePercent);
            CadFall up = CadFallRules.Compute(net, NodeAt(net, 10000, 5000), 0, r => r.SlopePercent);

            string mainId = net.Runs.Single(r => r.LengthMm > 9000).Id;
            CadRunFall a = down.Runs.Single(r => r.RunId == mainId);
            CadRunFall b = up.Runs.Single(r => r.RunId == mainId);
            Assert.NotEqual(a.Upstream, b.Upstream);
        }

        [Fact]
        public void Without_an_outfall_nothing_is_computed()
        {
            CadNetwork net = Branch();
            CadFall fall = CadFallRules.Compute(net, null, 0, r => r.SlopePercent);
            Assert.False(fall.Ok);
            Assert.Equal("no_outfall_named", fall.Refusal);
            Assert.Empty(fall.Runs);
        }

        [Fact]
        public void An_outfall_that_is_not_a_node_of_the_network_is_refused()
        {
            CadNetwork net = Branch();
            CadFall fall = CadFallRules.Compute(net, "not-a-node", 0, r => r.SlopePercent);
            Assert.False(fall.Ok);
            Assert.Equal("outfall_is_not_a_node_of_this_network", fall.Refusal);
        }

        [Fact]
        public void A_point_out_of_tolerance_finds_no_node_rather_than_the_nearest_one()
        {
            // An outfall on the wrong node inverts an entire layout while looking
            // plausible, so the nearest node is not taken when it is far away.
            CadNetwork net = Branch();
            Assert.Null(CadFallRules.NodeNear(net, new CadPoint(4000, 4000), 50));
            Assert.NotNull(CadFallRules.NodeNear(net, new CadPoint(4000, 4000), 10000));
        }

        [Fact]
        public void A_run_with_no_declared_slope_stops_the_walk_rather_than_being_laid_level()
        {
            // A drain laid level is a drain that does not drain, and every invert
            // beyond an unknown fall is unknown too.
            var segments = new List<CadSegment>
            {
                Seg(0, 0, 10000, 0, "P-KNOWN"),
                Seg(10000, 0, 10000, 5000, "P-UNKNOWN")
            };
            CadNetwork net = CadNetworkRules.Build(segments, Options(), layer =>
                layer == "P-KNOWN"
                    ? new CadNetworkRules.CadRunDeclaration { SlopePercent = 1.0 }
                    : new CadNetworkRules.CadRunDeclaration());

            CadFall fall = CadFallRules.Compute(net, NodeAt(net, 0, 0), 0, r => r.SlopePercent);

            Assert.Single(fall.Runs);
            JObject blocked = Assert.Single(fall.Blocked);
            Assert.Equal("no_declared_slope", blocked.Value<string>("reason"));
            Assert.Contains("does not drain", blocked.Value<string>("means"));

            // And the node beyond it has no invert at all.
            Assert.DoesNotContain(fall.Inverts, i => i.NodeKey == NodeAt(net, 10000, 5000));
        }

        [Fact]
        public void A_zero_slope_is_a_declaration_and_is_not_the_same_as_none()
        {
            // Null means nobody said; zero means somebody said level. The walk
            // passes through a declared zero and stops at a null.
            var segments = new List<CadSegment>
            {
                Seg(0, 0, 10000, 0),
                Seg(10000, 0, 10000, 5000, "P-FLAT")
            };
            CadNetwork net = CadNetworkRules.Build(segments, Options(), layer =>
                new CadNetworkRules.CadRunDeclaration { SlopePercent = layer == "P-FLAT" ? 0.0 : 1.0 });

            CadFall fall = CadFallRules.Compute(net, NodeAt(net, 0, 0), 0, r => r.SlopePercent);

            Assert.Equal(2, fall.Runs.Count);
            Assert.Empty(fall.Blocked);
            Assert.Equal(100, fall.Inverts.Single(i => i.NodeKey == NodeAt(net, 10000, 5000)).RiseMm, 3);
        }

        [Fact]
        public void A_loop_the_outfall_reaches_two_ways_is_a_conflict_and_is_not_resolved()
        {
            // A TRIANGLE, not a square. A square loop is reachable both ways by
            // the SAME distance, so it produces no disagreement and would prove
            // nothing. Here the far corner is 9487 mm away along the diagonal and
            // 12000 mm away round the two legs, so the two paths give inverts
            // 25 mm apart - and a drain cannot be at two heights.
            var segments = new List<CadSegment>
            {
                Seg(0, 0, 9000, 0),
                Seg(9000, 0, 9000, 3000),
                Seg(0, 0, 9000, 3000)
            };
            CadNetwork net = CadNetworkRules.Build(segments, Options(), layer =>
                new CadNetworkRules.CadRunDeclaration { SlopePercent = 1.0 });

            CadFall fall = CadFallRules.Compute(net, NodeAt(net, 0, 0), 0, r => r.SlopePercent);

            Assert.NotEmpty(fall.Conflicts);
            Assert.Contains("two heights", (fall.Conflicts[0] as JObject).Value<string>("means"));
        }

        [Fact]
        public void On_a_loop_every_run_ends_where_its_node_says_it_does()
        {
            // THE DEFECT THIS PINS. The far node of a loop takes its invert from
            // the shorter path, and the conflict says so. The run arriving by the
            // LONGER path used to report an end height from its own arithmetic -
            // so the reply carried two numbers for one place, in the block that
            // exists to say what the heights are.
            var segments = new List<CadSegment>
            {
                Seg(0, 0, 9000, 0),
                Seg(9000, 0, 9000, 3000),
                Seg(0, 0, 9000, 3000)
            };
            CadNetwork net = CadNetworkRules.Build(segments, Options(), layer =>
                new CadNetworkRules.CadRunDeclaration { SlopePercent = 1.0 });

            CadFall fall = CadFallRules.Compute(net, NodeAt(net, 0, 0), 0, r => r.SlopePercent);
            Assert.NotEmpty(fall.Conflicts);

            var invertOf = fall.Inverts.ToDictionary(i => i.NodeKey, i => i.RiseMm, StringComparer.Ordinal);
            var runById = net.Runs.ToDictionary(r => r.Id, StringComparer.Ordinal);

            foreach (CadRunFall run in fall.Runs)
            {
                CadRun geometry = runById[run.RunId];
                Assert.Equal(invertOf[geometry.StartNode], run.StartZMm, 6);
                Assert.Equal(invertOf[geometry.EndNode], run.EndZMm, 6);
            }
        }

        [Fact]
        public void A_run_this_outfall_never_reaches_is_named()
        {
            // A drainage network in two pieces is a finding, not a smaller network.
            var segments = new List<CadSegment>
            {
                Seg(0, 0, 10000, 0),
                Seg(50000, 0, 60000, 0, "P-ELSEWHERE")
            };
            CadNetwork net = CadNetworkRules.Build(segments, Options(), layer =>
                new CadNetworkRules.CadRunDeclaration { SlopePercent = 1.0 });

            CadFall fall = CadFallRules.Compute(net, NodeAt(net, 0, 0), 0, r => r.SlopePercent);

            Assert.Single(fall.Runs);
            Assert.Single(fall.Unreachable);
            Assert.Equal(1, fall.ToJson()["summary"].Value<int>("runs_unreachable"));
        }

        [Fact]
        public void The_summary_says_what_each_category_means_rather_than_only_counting()
        {
            CadNetwork net = Branch();
            CadFall fall = CadFallRules.Compute(net, NodeAt(net, 0, 0), 0, r => r.SlopePercent);
            string means = fall.ToJson()["summary"].Value<string>("means");

            Assert.Contains("BLOCKED", means);
            Assert.Contains("CONFLICT", means);
            Assert.Contains("UNREACHABLE", means);
        }
    }
}
