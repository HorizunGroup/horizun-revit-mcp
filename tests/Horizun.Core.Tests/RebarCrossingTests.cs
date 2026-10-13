// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// crossing_order (acero 3.0, etapa 1c): the DECLARED order in which bars of different
// rules pass each other. Without it, two sets whose bars pass through each other are
// both refused (bars_intersect) - MEASURED: Revit pushes a crossing bar until it
// touches the other, and says nothing. With it, the plan displaces the lower-priority
// set by the declared offset, in a declared or unambiguously derived direction, and
// the overlap check runs again on the RESULT.
//
// The three cases are the anonymized regression case's (stage 1b): beams crossing at a joint, a mat
// passing through beam bars, and the top and bottom U bars of one beam whose legs
// coincide. The fakes are the shapes ReinforcementResolver.DescribeRebarRow and
// RebarFacts.Describe emit.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class RebarCrossingTests
    {
        private const double Tol = 2.0;

        // ------------------------------------------------------------ fakes

        private static JArray Pts(IEnumerable<double[]> p) => new JArray(p.Select(x => (object)new JArray(x[0], x[1], x[2])).ToArray());
        private static double[] P(double x, double y, double z) => new[] { x, y, z };

        /// <summary>A buildable row as ReinforcementResolver.DescribeRebarRow publishes it.</summary>
        private static JObject Row(string ruleId, long host, double modelDia, IList<double[]> curve, double[] normal,
                                   double[] positions, string declared = null, string layout = null, int index = 0)
        {
            var belongs = new JObject { ["declared_rule_id"] = declared ?? ruleId };
            if (layout != null) belongs["layout"] = layout;
            return new JObject
            {
                ["index"] = index,
                ["rule_id"] = ruleId,
                ["host_id"] = host,
                ["will_build"] = true,
                ["required"] = true,
                ["bar_type"] = new JObject { ["id"] = 300, ["name"] = "B" + modelDia, ["nominal_diameter_mm"] = modelDia, ["model_diameter_mm"] = modelDia },
                ["style"] = "standard",
                ["closed"] = false,
                ["curve_mm"] = Pts(curve),
                ["normal"] = new JObject { ["x"] = normal[0], ["y"] = normal[1], ["z"] = normal[2] },
                ["layout"] = new JObject
                {
                    ["rule"] = positions.Length == 1 ? "single" : "fixed_number",
                    ["number_of_bar_positions"] = positions.Length,
                    ["include_first_bar"] = true,
                    ["include_last_bar"] = true,
                    ["bars_on_normal_side"] = true,
                    ["positions_mm"] = new JArray(positions.Select(v => (object)Math.Abs(v)).ToArray()),
                    ["signed_positions_mm"] = new JArray(positions.Select(v => (object)v).ToArray())
                },
                ["belongs_to"] = belongs
            };
        }

        private sealed class Fake
        {
            public JObject Row;
            public double[] HostCentre;
            public List<double[]> Curve;
        }

        private static Fake F(string ruleId, long host, double dia, IList<double[]> curve, double[] normal, double[] positions,
                              double[] hostCentre, string declared = null, string layout = null)
            => new Fake
            {
                Row = Row(ruleId, host, dia, curve, normal, positions, declared, layout),
                HostCentre = hostCentre,
                Curve = curve.Select(p => (double[])p.Clone()).ToList()
            };

        private static List<OverlapMember> Members(IList<Fake> fakes)
        {
            var members = new List<OverlapMember>();
            for (int i = 0; i < fakes.Count; i++)
            {
                OverlapMember m = RebarOverlap.FromPlanRow(fakes[i].Row);
                m.Tag = i;
                m.HostCentreMm = fakes[i].HostCentre;
                members.Add(m);
            }
            return members;
        }

        private static OverlapReport Check(IList<Fake> fakes, StructuralClearances c = null) =>
            RebarOverlap.Check(Members(fakes), c ?? new StructuralClearances(), Tol);

        /// <summary>What the resolver does with a decision: every moved row's curve displaced, then measured again.</summary>
        private static List<Fake> Displaced(IList<Fake> fakes, CrossingDecision d)
        {
            var result = new List<Fake>();
            foreach (Fake f in fakes)
            {
                CrossingMove m;
                if (!d.Moves.TryGetValue(CrossingDecision.Key((string)f.Row["rule_id"], (long)f.Row["host_id"]), out m))
                {
                    result.Add(f);
                    continue;
                }
                List<double[]> moved;
                Dictionary<int, double[]> arcs;
                string why = RebarCrossing.Displace(f.Curve, new Dictionary<int, double[]>(), false, m, out moved, out arcs);
                Assert.Null(why);
                var row = (JObject)f.Row.DeepClone();
                row["curve_mm"] = Pts(moved);
                result.Add(new Fake { Row = row, HostCentre = f.HostCentre, Curve = moved });
            }
            return result;
        }

        private static StructuralCrossingOrder Order(string json) => StructuralCrossingOrder.Parse(JObject.Parse(json.Replace('\'', '"')));

        // Two beams crossing at a joint, 300 x 500, bottoms at z = 0: BX along X, BY along Y. 16 mm bars,
        // 40 mm cover to the stirrup... the bottom bars at z = 50 and the top bars at z = 450, in BOTH beams.
        private static List<Fake> Joint() => new List<Fake>
        {
            F("BX-bot", 5001, 16, new[] { P(-2000, 0, 50), P(2000, 0, 50) }, new double[] { 0, 1, 0 }, new double[] { 0 }, P(0, 0, 250)),
            F("BX-top", 5001, 16, new[] { P(-2000, 0, 450), P(2000, 0, 450) }, new double[] { 0, 1, 0 }, new double[] { 0 }, P(0, 0, 250)),
            F("BY-bot", 5002, 16, new[] { P(0, -2000, 50), P(0, 2000, 50) }, new double[] { 1, 0, 0 }, new double[] { 0 }, P(0, 0, 250)),
            F("BY-top", 5002, 16, new[] { P(0, -2000, 450), P(0, 2000, 450) }, new double[] { 1, 0, 0 }, new double[] { 0 }, P(0, 0, 250))
        };

        // ------------------------------------------------- duplicate_bar (lot 4)

        [Fact]
        public void A_crossing_order_never_moves_one_bar_built_twice_apart_and_the_displaced_plan_is_checked_for_duplicates()
        {
            // Two rules laying the SAME bar are not two bars crossing: an order naming them does not resolve it by
            // moving one of them - duplicate_bar is never excused, and it is never counted as a crossing.
            var fakes = new List<Fake>
            {
                F("BX-bot", 5001, 16, new[] { P(-2000, 0, 50), P(2000, 0, 50) }, new double[] { 0, 1, 0 }, new double[] { 0 }, P(0, 0, 250)),
                F("BX-dup", 5001, 16, new[] { P(-2000, 0, 50), P(2000, 0, 50) }, new double[] { 0, 1, 0 }, new double[] { 0 }, P(0, 0, 250)),
                F("BY-bot", 5002, 16, new[] { P(0, -2000, 50), P(0, 2000, 50) }, new double[] { 1, 0, 0 }, new double[] { 0 }, P(0, 0, 250))
            };
            OverlapReport before = Check(fakes);
            Assert.Single(before.Pairs, p => p.IsDuplicate);
            CrossingDecision d = RebarCrossing.Decide(before, Order(@"{ 'offset': 'diameter', 'orders': [
                { 'id': 'x-over-all', 'stays': [ 'BX-bot' ], 'moves': [ 'BX-dup', 'BY-*' ], 'move': 'translate', 'direction': 'toward_host_core' } ] }"), Tol);
            Assert.False(d.Moves.ContainsKey(CrossingDecision.Key("BX-dup", 5001)));
            // Nor is it refused as a crossing it is not: its code stays duplicate_bar.
            Assert.False(d.Refused.ContainsKey(CrossingDecision.Key("BX-dup", 5001)));
            Assert.True(d.Moves.ContainsKey(CrossingDecision.Key("BY-bot", 5002)));
            // A declared vector would move it straight off the other - and still does not.
            CrossingDecision v = RebarCrossing.Decide(before, Order(@"{ 'offset': 'diameter', 'orders': [
                { 'id': 'dup-up', 'stays': [ 'BX-bot' ], 'moves': [ 'BX-dup' ], 'move': 'translate', 'direction': [0, 0, 1] } ] }"), Tol);
            Assert.Empty(v.Moves);
            Assert.Empty(v.Refused);
            // After the displacement the crossing is cleared and the duplicate is still there, still refused by name.
            OverlapReport after = Check(Displaced(fakes, d));
            OverlapPair dup = Assert.Single(after.Pairs);
            Assert.True(dup.IsDuplicate);
            Assert.Equal(new[] { "BX-bot", "BX-dup" }, new[] { dup.A.RuleId, dup.B.RuleId });
            Assert.All(RebarOverlap.Refusals(after), r => Assert.Equal(RebarOverlap.CodeDuplicate, r.Code));
        }

        // ---------------------------------------------------------- the joint

        [Fact]
        public void Beams_crossing_at_a_joint_intersect_without_an_order()
        {
            OverlapReport r = Check(Joint());
            Assert.Equal(2, r.Pairs.Count);
            Assert.All(r.Pairs, p => Assert.Equal(RebarOverlap.KindIntersection, p.Kind));
            Assert.All(r.Pairs, p => Assert.Equal(RebarOverlap.RelationCrossing, p.Relation));
        }

        [Fact]
        public void A_family_order_moves_the_y_beams_bars_toward_their_core_and_the_result_clears()
        {
            List<Fake> joint = Joint();
            OverlapReport before = Check(joint);
            CrossingDecision d = RebarCrossing.Decide(before, Order(@"{ 'offset': 'diameter', 'orders': [
                { 'id': 'x-over-y', 'stays': [ 'BX-*' ], 'moves': [ 'BY-*' ], 'move': 'translate', 'direction': 'toward_host_core' } ] }"), Tol);

            Assert.Empty(d.Refused);
            Assert.Equal(2, d.Moves.Count);
            CrossingMove bot = d.Moves[CrossingDecision.Key("BY-bot", 5002)];
            CrossingMove top = d.Moves[CrossingDecision.Key("BY-top", 5002)];
            // The common perpendicular of two horizontal bars is vertical; the core of a beam is above its
            // bottom bars and below its top bars.
            Assert.Equal(new[] { 0.0, 0.0, 16.0 }, bot.VectorMm.Select(v => Math.Round(v, 9)));
            Assert.Equal(new[] { 0.0, 0.0, -16.0 }, top.VectorMm.Select(v => Math.Round(v, 9)));
            Assert.Equal(16.0, bot.OffsetMm, 9);
            Assert.Equal("BX-bot", (string)bot.Against.Single()["rule_id"]);
            Assert.Contains("x-over-y", bot.OrderIds);
            Assert.False(d.Moves.ContainsKey(CrossingDecision.Key("BX-bot", 5001)));

            // THE RESULT, measured again: the bars now touch (crossing bars are tied in contact) and nothing passes through.
            OverlapReport after = Check(Displaced(joint, d));
            Assert.Empty(after.Pairs);
        }

        [Fact]
        public void The_order_names_a_family_by_glob_and_a_layer_after_its_hash()
        {
            OverlapMember m = new OverlapMember { RuleId = "V1#h5001#bottom", DeclaredRuleId = "V1" };
            Assert.True(RebarCrossing.Matches("V1", m));                // the declared rule: every row it expands to
            Assert.True(RebarCrossing.Matches("V1#h*#bottom", m));      // one layer of it, in any host
            Assert.False(RebarCrossing.Matches("V1#h*#top", m));
            Assert.True(RebarCrossing.Matches("*#bottom", m));
            Assert.False(RebarCrossing.Matches("V", m));
            Assert.True(RebarCrossing.Matches("V?", m));
        }

        // ---------------------------------------------------------------- mats

        [Fact]
        public void A_mat_layer_passing_through_beam_bars_moves_the_declared_way_by_contact()
        {
            // A slab mat's bottom Y layer (12 mm) at the beam bottom bars' level, crossing BX-bot (16 mm).
            var fakes = new List<Fake>
            {
                F("BX-bot", 5001, 16, new[] { P(-2000, 0, 50), P(2000, 0, 50) }, new double[] { 0, 1, 0 }, new double[] { 0 }, P(0, 0, 250)),
                F("M1#bottom_y", 7001, 12, new[] { P(-1000, -2000, 50), P(-1000, 2000, 50) }, new double[] { 1, 0, 0 },
                  new double[] { 0, 500, 1000, 1500, 2000 }, P(0, 0, 100), declared: "M1", layout: "M1#bottom_y")
            };
            OverlapReport before = Check(fakes);
            Assert.Equal(RebarOverlap.KindIntersection, before.Pairs.Single().Kind);
            Assert.Equal(5, before.Pairs.Single().BarPairs);

            CrossingDecision d = RebarCrossing.Decide(before, Order(@"{ 'offset': 'diameter', 'orders': [
                { 'id': 'mat-over-beams', 'stays': [ 'BX-*' ], 'moves': [ 'M1#bottom_*' ], 'move': 'translate',
                  'direction': [ 0, 0, 2 ], 'offset': 'contact' } ] }"), Tol);
            CrossingMove m = d.Moves.Values.Single();
            Assert.Equal("M1#bottom_y", m.RuleId);
            // contact: the two radii, 8 + 6 - the bars end up touching.
            Assert.Equal(14.0, m.OffsetMm, 9);
            Assert.Equal(new[] { 0.0, 0.0, 14.0 }, m.VectorMm.Select(v => Math.Round(v, 9)));
            Assert.Empty(Check(Displaced(fakes, d)).Pairs);
        }

        // -------------------------------------------------------------- U legs

        // One beam, x 0..4000, z 0..500: the top U's legs run down from the top bars, the bottom U's up from
        // the bottom bars, in the same plane and at the same station - the anonymized regression case's beam group.
        private static List<Fake> UBars() => new List<Fake>
        {
            F("U-SUP", 5001, 12, new[] { P(50, 0, 100), P(50, 0, 450), P(3950, 0, 450), P(3950, 0, 100) }, new double[] { 0, 1, 0 }, new double[] { 0 }, P(2000, 0, 250)),
            F("U-INF", 5001, 12, new[] { P(50, 0, 400), P(50, 0, 50), P(3950, 0, 50), P(3950, 0, 400) }, new double[] { 0, 1, 0 }, new double[] { 0 }, P(2000, 0, 250))
        };

        [Fact]
        public void The_bottom_U_legs_nest_inside_the_top_U_legs_and_the_result_clears()
        {
            List<Fake> u = UBars();
            OverlapReport before = Check(u);
            OverlapPair p = before.Pairs.Single();
            Assert.Equal(RebarOverlap.KindIntersection, p.Kind);
            Assert.Equal(RebarOverlap.RelationAlongside, p.Relation);

            CrossingDecision d = RebarCrossing.Decide(before, Order(@"{ 'offset': 'diameter', 'orders': [
                { 'id': 'u-legs', 'stays': [ 'U-SUP' ], 'moves': [ 'U-INF' ], 'move': 'legs', 'legs': 'both' } ] }"), Tol);
            CrossingMove m = d.Moves.Values.Single();
            Assert.Equal(RebarCrossing.ModeLegs, m.Mode);
            Assert.Equal(RebarCrossing.LegsBoth, m.Legs);
            Assert.Equal(12.0, m.OffsetMm, 9);

            List<Fake> after = Displaced(u, d);
            List<double[]> moved = after[1].Curve;
            // Each leg moves INWARD along the run by one diameter; the run is two diameters shorter.
            Assert.Equal(new[] { 62.0, 0, 400 }, moved[0]);
            Assert.Equal(new[] { 62.0, 0, 50 }, moved[1]);
            Assert.Equal(new[] { 3938.0, 0, 50 }, moved[2]);
            Assert.Equal(new[] { 3938.0, 0, 400 }, moved[3]);
            Assert.Empty(Check(after).Pairs);
        }

        [Theory]
        [InlineData("start", 62.0, 3950.0)]
        [InlineData("end", 50.0, 3938.0)]
        public void One_leg_can_be_moved_alone(string legs, double startX, double endX)
        {
            var move = new CrossingMove { RuleId = "U", HostId = 1, Mode = RebarCrossing.ModeLegs, Legs = legs, OffsetMm = 12 };
            List<double[]> moved;
            Dictionary<int, double[]> arcs;
            Assert.Null(RebarCrossing.Displace(UBars()[1].Curve, new Dictionary<int, double[]>(), false, move, out moved, out arcs));
            Assert.Equal(startX, moved[0][0], 9);
            Assert.Equal(startX, moved[1][0], 9);
            Assert.Equal(endX, moved[2][0], 9);
            Assert.Equal(endX, moved[3][0], 9);
        }

        [Fact]
        public void Legs_are_refused_by_name_where_a_bar_has_none_or_closes_or_bends_in_an_arc()
        {
            var move = new CrossingMove { RuleId = "U", HostId = 1, Mode = RebarCrossing.ModeLegs, Legs = RebarCrossing.LegsBoth, OffsetMm = 12 };
            List<double[]> moved;
            Dictionary<int, double[]> arcs;
            // A straight bar has no legs.
            Assert.NotNull(RebarCrossing.Displace(new List<double[]> { P(0, 0, 0), P(1000, 0, 0) }, new Dictionary<int, double[]>(), false, move, out moved, out arcs));
            // An L has one: "both" cannot be read on it.
            Assert.NotNull(RebarCrossing.Displace(new List<double[]> { P(0, 0, 300), P(0, 0, 0), P(1000, 0, 0) }, new Dictionary<int, double[]>(), false, move, out moved, out arcs));
            // A stirrup closes: it has no ends to move.
            Assert.NotNull(RebarCrossing.Displace(UBars()[1].Curve, new Dictionary<int, double[]>(), true, move, out moved, out arcs));
            // An arc anywhere in the chain.
            Assert.NotNull(RebarCrossing.Displace(UBars()[1].Curve, new Dictionary<int, double[]> { [1] = P(2000, 0, 20) }, false, move, out moved, out arcs));
            // A run shorter than the two legs' move would fold the bar over itself.
            var big = new CrossingMove { RuleId = "U", HostId = 1, Mode = RebarCrossing.ModeLegs, Legs = RebarCrossing.LegsBoth, OffsetMm = 2000 };
            Assert.NotNull(RebarCrossing.Displace(UBars()[1].Curve, new Dictionary<int, double[]>(), false, big, out moved, out arcs));
        }

        [Fact]
        public void A_translation_moves_every_vertex_and_every_arc_point()
        {
            var move = new CrossingMove { RuleId = "H", HostId = 1, Mode = RebarCrossing.ModeTranslate, VectorMm = P(0, 0, 10), OffsetMm = 10 };
            List<double[]> moved;
            Dictionary<int, double[]> arcs;
            Assert.Null(RebarCrossing.Displace(new List<double[]> { P(0, 0, 0), P(100, 0, 0) },
                new Dictionary<int, double[]> { [0] = P(50, 20, 0) }, false, move, out moved, out arcs));
            Assert.Equal(new[] { 0.0, 0, 10 }, moved[0]);
            Assert.Equal(new[] { 100.0, 0, 10 }, moved[1]);
            Assert.Equal(new[] { 50.0, 20, 10 }, arcs[0]);
        }

        // ------------------------------------------------------ never guessed

        [Fact]
        public void Toward_the_core_is_refused_where_the_bars_run_alongside_there_is_no_crossing_axis()
        {
            List<Fake> u = UBars();
            CrossingDecision d = RebarCrossing.Decide(Check(u), Order(@"{ 'offset': 'diameter', 'orders': [
                { 'id': 'u', 'stays': [ 'U-SUP' ], 'moves': [ 'U-INF' ], 'move': 'translate', 'direction': 'toward_host_core' } ] }"), Tol);
            Assert.Empty(d.Moves);
            CrossingRefusal r = d.Refused.Values.Single();
            Assert.Equal("U-INF", r.RuleId);
            Assert.Equal(RebarCrossing.CodeAmbiguous, r.Code);
            Assert.Contains("alongside", r.Why);
        }

        [Fact]
        public void Toward_the_core_is_refused_where_the_core_lies_on_the_crossing_plane()
        {
            List<Fake> joint = Joint().Take(1).Concat(Joint().Skip(2).Take(1)).ToList();
            joint[1].HostCentre = P(0, 0, 50);   // the host's centre at the bars' own level: up or down cannot be read
            CrossingDecision d = RebarCrossing.Decide(Check(joint), Order(@"{ 'offset': 'diameter', 'orders': [
                { 'id': 'x', 'stays': [ 'BX-*' ], 'moves': [ 'BY-*' ], 'move': 'translate', 'direction': 'toward_host_core' } ] }"), Tol);
            Assert.Empty(d.Moves);
            Assert.Equal(RebarCrossing.CodeAmbiguous, d.Refused.Values.Single().Code);
        }

        [Fact]
        public void Toward_the_core_without_a_host_centre_is_not_evaluable_and_never_a_guess()
        {
            List<Fake> joint = Joint().Take(1).Concat(Joint().Skip(2).Take(1)).ToList();
            joint[1].HostCentre = null;
            CrossingDecision d = RebarCrossing.Decide(Check(joint), Order(@"{ 'offset': 'diameter', 'orders': [
                { 'id': 'x', 'stays': [ 'BX-*' ], 'moves': [ 'BY-*' ], 'move': 'translate', 'direction': 'toward_host_core' } ] }"), Tol);
            Assert.Empty(d.Moves);
            Assert.Equal(RebarCrossing.CodeAmbiguous, d.Refused.Values.Single().Code);
        }

        [Fact]
        public void Two_orders_that_disagree_on_which_bar_moves_refuse_both_bars()
        {
            List<Fake> joint = Joint().Take(1).Concat(Joint().Skip(2).Take(1)).ToList();
            CrossingDecision d = RebarCrossing.Decide(Check(joint), Order(@"{ 'offset': 'diameter', 'orders': [
                { 'id': 'a', 'stays': [ 'BX-*' ], 'moves': [ 'BY-*' ], 'move': 'translate', 'direction': [0,0,1] },
                { 'id': 'b', 'stays': [ 'BY-*' ], 'moves': [ 'BX-*' ], 'move': 'translate', 'direction': [0,0,1] } ] }"), Tol);
            Assert.Empty(d.Moves);
            Assert.Equal(2, d.Refused.Count);
            Assert.All(d.Refused.Values, r => Assert.Equal(RebarCrossing.CodeAmbiguous, r.Code));
        }

        [Fact]
        public void One_set_asked_to_move_two_ways_is_a_conflict_not_a_sum()
        {
            // BY-bot crosses BX-bot and a second X beam's bottom bar; the two orders send it opposite ways.
            var fakes = new List<Fake>
            {
                Joint()[0],
                F("BX2-bot", 5003, 16, new[] { P(-2000, 1000, 50), P(2000, 1000, 50) }, new double[] { 0, 1, 0 }, new double[] { 0 }, P(0, 1000, 250)),
                Joint()[2]
            };
            CrossingDecision d = RebarCrossing.Decide(Check(fakes), Order(@"{ 'offset': 'diameter', 'orders': [
                { 'id': 'up', 'stays': [ 'BX-*' ], 'moves': [ 'BY-*' ], 'move': 'translate', 'direction': [0,0,1] },
                { 'id': 'down', 'stays': [ 'BX2-*' ], 'moves': [ 'BY-*' ], 'move': 'translate', 'direction': [0,0,-1] } ] }"), Tol);
            Assert.Empty(d.Moves);
            CrossingRefusal r = d.Refused.Values.Single();
            Assert.Equal(RebarCrossing.CodeConflict, r.Code);
            Assert.Contains("up", r.Why);
            Assert.Contains("down", r.Why);
        }

        [Fact]
        public void The_same_way_twice_is_one_move_by_the_larger_offset()
        {
            var fakes = new List<Fake>
            {
                Joint()[0],
                F("BX2-bot", 5003, 20, new[] { P(-2000, 1000, 50), P(2000, 1000, 50) }, new double[] { 0, 1, 0 }, new double[] { 0 }, P(0, 1000, 250)),
                Joint()[2]
            };
            CrossingDecision d = RebarCrossing.Decide(Check(fakes), Order(@"{ 'offset': 'contact', 'orders': [
                { 'id': 'x', 'stays': [ 'BX*' ], 'moves': [ 'BY-*' ], 'move': 'translate', 'direction': [0,0,1] } ] }"), Tol);
            CrossingMove m = d.Moves.Values.Single();
            Assert.Equal(18.0, m.OffsetMm, 9);   // contact with the 20 mm bar: 8 + 10
            Assert.Equal(2, m.Against.Count);
        }

        [Fact]
        public void A_pair_no_order_covers_is_left_to_the_overlap_check()
        {
            CrossingDecision d = RebarCrossing.Decide(Check(Joint()), Order(@"{ 'offset': 'diameter', 'orders': [
                { 'id': 'u', 'stays': [ 'U-SUP' ], 'moves': [ 'U-INF' ], 'move': 'legs', 'legs': 'both' } ] }"), Tol);
            Assert.Empty(d.Moves);
            Assert.Empty(d.Refused);
            Assert.Equal(2, d.PairsNotCovered);
            Assert.Equal(0, d.PairsCovered);
        }

        // ------------------------------------------------- cover is still held

        [Fact]
        public void A_displaced_bar_is_still_measured_against_its_host_and_its_cover()
        {
            // BY-bot pushed DOWN by one diameter: 50 - 16 = 34 to its centre, 26 clear to the soffit, against 40 of cover.
            HostMesh box = HostMesh.Box(new[] { -150.0, -2000, 0 }, new[] { 150.0, 2000, 500 });
            var move = new CrossingMove { RuleId = "BY-bot", HostId = 5002, Mode = RebarCrossing.ModeTranslate, VectorMm = P(0, 0, -16), OffsetMm = 16 };
            List<double[]> moved;
            Dictionary<int, double[]> arcs;
            Assert.Null(RebarCrossing.Displace(new List<double[]> { P(0, -1900, 50), P(0, 1900, 50) }, new Dictionary<int, double[]>(), false, move, out moved, out arcs));
            SetContainment c = RebarContainment.Check(box, moved, false, new List<double> { 0 }, new double[] { 1, 0, 0 }, 8, 40, Tol, 25);
            Assert.Equal(SolidContainment.InsideCoverViolated, c.Word);

            // What the resolver says about it: refused BY NAME as a displacement that breaks cover - never squeezed.
            string code, why;
            RebarCrossing.RefuseMovedRow(move, "bar_short_of_the_declared_cover", "26 mm clear against 40", out code, out why);
            Assert.Equal(RebarCrossing.CodeBreaksCover, code);
            Assert.Contains("bar_short_of_the_declared_cover", why);
            Assert.Contains("16", why);
        }

        // ------------------------------------------------------------ the audit

        [Fact]
        public void The_audit_measures_each_displacement_against_the_declared_position()
        {
            var move = new CrossingMove { RuleId = "BY-bot", HostId = 5002, Mode = RebarCrossing.ModeTranslate, VectorMm = P(0, 0, 16), OffsetMm = 16 };
            var declared = new List<double[]> { P(0, -2000, 50), P(0, 2000, 50) };
            JObject ok = RebarCrossing.Measure(move, declared, new List<double[]> { P(0.2, -2000, 66.4), P(0, 2000, 65.7) }, Tol);
            Assert.True((bool)ok["agrees"]);
            Assert.Equal(16.4, (double)ok["measured_start_mm"], 6);
            Assert.Equal(15.7, (double)ok["measured_end_mm"], 6);

            JObject pushed = RebarCrossing.Measure(move, declared, new List<double[]> { P(0, -2000, 50), P(0, 2000, 50) }, Tol);
            Assert.False((bool)pushed["agrees"]);

            var legs = new CrossingMove { RuleId = "U-INF", HostId = 5001, Mode = RebarCrossing.ModeLegs, Legs = RebarCrossing.LegsBoth, OffsetMm = 12 };
            JObject l = RebarCrossing.Measure(legs, UBars()[1].Curve,
                new List<double[]> { P(62, 0, 400), P(62, 0, 50), P(3938, 0, 50), P(3938, 0, 400) }, Tol);
            Assert.True((bool)l["agrees"]);
            Assert.Equal(12.0, (double)l["measured_start_mm"], 6);
            Assert.Equal(12.0, (double)l["measured_end_mm"], 6);

            JObject unreadable = RebarCrossing.Measure(legs, UBars()[1].Curve, null, Tol);
            Assert.Null((bool?)unreadable["agrees"]);
        }

        // ------------------------------------------------------------ the schema

        private static StructuralRequirementSet SetWith(string crossing) => StructuralRequirementSet.Load(JObject.Parse((@"{
  'schema': 'horizun.structural-requirements/1', 'units': 'millimeter',
  'requirement_set': { 'id': 'co', 'version': '1' },
  'bar_types': [ { 'id': 'N16', 'type_name': '16M' } ],
  'crossing_order': " + crossing + @",
  'reinforcement_rules': [
    { 'id': 'BX-bot', 'host': { 'element_ids': [ 1 ] }, 'bar_type': 'N16', 'allow_new_shape': true, 'style': 'standard',
      'curve_mm': [ [0,0,0], [1000,0,0] ], 'normal': [0,1,0], 'layout': { 'rule': 'single' } },
    { 'id': 'BY-bot', 'host': { 'element_ids': [ 2 ] }, 'bar_type': 'N16', 'allow_new_shape': true, 'style': 'standard',
      'curve_mm': [ [0,0,0], [1000,0,0] ], 'normal': [0,1,0], 'layout': { 'rule': 'single' } } ] }").Replace('\'', '"')));

        [Fact]
        public void A_crossing_order_is_read_with_its_offsets_and_its_orders()
        {
            StructuralRequirementSet s = SetWith(@"{ 'offset': { 'mm': 20 }, 'orders': [
                { 'id': 'a', 'stays': [ 'BX-*' ], 'moves': [ 'BY-bot' ], 'move': 'translate', 'direction': [ 0, 0, 1 ], 'offset': 'contact' },
                { 'id': 'b', 'stays': [ 'BX-bot' ], 'moves': [ 'BY-*' ], 'move': 'legs', 'legs': 'end' } ] }");
            Assert.True(s.Ok, s.Error);
            Assert.NotNull(s.CrossingOrder);
            Assert.Equal(CrossingOffset.KindMm, s.CrossingOrder.Offset.Kind);
            Assert.Equal(20.0, s.CrossingOrder.Offset.Mm, 9);
            Assert.Equal(2, s.CrossingOrder.Orders.Count);
            Assert.Equal(CrossingOffset.KindContact, s.CrossingOrder.Orders[0].Offset.Kind);
            Assert.Equal(new[] { 0.0, 0, 1 }, s.CrossingOrder.Orders[0].Direction);
            Assert.Null(s.CrossingOrder.Orders[1].Offset);
            Assert.Equal("end", s.CrossingOrder.Orders[1].Legs);

            StructuralRequirementSet none = StructuralRequirementSet.Load(JObject.Parse(SetJsonWithout()));
            Assert.True(none.Ok, none.Error);
            Assert.Null(none.CrossingOrder);
        }

        private static string SetJsonWithout() => @"{
  'schema': 'horizun.structural-requirements/1', 'units': 'millimeter',
  'requirement_set': { 'id': 'co', 'version': '1' },
  'bar_types': [ { 'id': 'N16', 'type_name': '16M' } ],
  'reinforcement_rules': [
    { 'id': 'r1', 'host': { 'element_ids': [ 1 ] }, 'bar_type': 'N16', 'allow_new_shape': true, 'style': 'standard',
      'curve_mm': [ [0,0,0], [1000,0,0] ], 'normal': [0,1,0], 'layout': { 'rule': 'single' } } ] }".Replace('\'', '"');

        [Theory]
        [InlineData("[]", "schema_not_recognised", "crossing_order")]
        [InlineData("{ 'orders': [ { 'id': 'a', 'stays': [ 'BX-bot' ], 'moves': [ 'BY-bot' ], 'move': 'legs', 'legs': 'both' } ] }", "required_field_missing", "crossing_order.offset")]
        [InlineData("{ 'offset': 'half', 'orders': [ { 'id': 'a', 'stays': [ 'BX-bot' ], 'moves': [ 'BY-bot' ], 'move': 'legs', 'legs': 'both' } ] }", "value_not_in_vocabulary", "crossing_order.offset")]
        [InlineData("{ 'offset': { 'mm': 0 }, 'orders': [ { 'id': 'a', 'stays': [ 'BX-bot' ], 'moves': [ 'BY-bot' ], 'move': 'legs', 'legs': 'both' } ] }", "value_not_in_vocabulary", "crossing_order.offset.mm")]
        [InlineData("{ 'offset': { 'mm': 'NaN' }, 'orders': [ { 'id': 'a', 'stays': [ 'BX-bot' ], 'moves': [ 'BY-bot' ], 'move': 'legs', 'legs': 'both' } ] }", "value_is_not_a_finite_number", "crossing_order.offset.mm")]
        [InlineData("{ 'offset': 'diameter', 'orderz': [] }", "unknown_key", "crossing_order.orderz")]
        [InlineData("{ 'offset': 'diameter', 'orders': [] }", "required_field_missing", "crossing_order.orders")]
        [InlineData("{ 'offset': 'diameter', 'orders': [ { 'stays': [ 'BX-bot' ], 'moves': [ 'BY-bot' ], 'move': 'legs', 'legs': 'both' } ] }", "required_field_missing", "crossing_order.orders[0].id")]
        [InlineData("{ 'offset': 'diameter', 'orders': [ { 'id': 'a', 'stays': [ 'BX-bot' ], 'moves': [ 'BY-bot' ], 'move': 'legs', 'legs': 'both' }, { 'id': 'a', 'stays': [ 'BX-bot' ], 'moves': [ 'BY-bot' ], 'move': 'legs', 'legs': 'both' } ] }", "duplicate_id", "crossing_order.orders['a'].id")]
        [InlineData("{ 'offset': 'diameter', 'orders': [ { 'id': 'a', 'stays': [], 'moves': [ 'BY-bot' ], 'move': 'legs', 'legs': 'both' } ] }", "required_field_missing", "crossing_order.orders['a'].stays")]
        [InlineData("{ 'offset': 'diameter', 'orders': [ { 'id': 'a', 'stays': [ 'BX-bot' ], 'moves': [ 'nobody*' ], 'move': 'legs', 'legs': 'both' } ] }", "reference_not_declared", "crossing_order.orders['a'].moves")]
        [InlineData("{ 'offset': 'diameter', 'orders': [ { 'id': 'a', 'stays': [ 'BX-bot' ], 'moves': [ 'BX-bot' ], 'move': 'legs', 'legs': 'both' } ] }", "value_not_in_vocabulary", "crossing_order.orders['a'].moves")]
        [InlineData("{ 'offset': 'diameter', 'orders': [ { 'id': 'a', 'stays': [ 'BX-bot' ], 'moves': [ 'BY-bot' ], 'move': 'swap' } ] }", "value_not_in_vocabulary", "crossing_order.orders['a'].move")]
        [InlineData("{ 'offset': 'diameter', 'orders': [ { 'id': 'a', 'stays': [ 'BX-bot' ], 'moves': [ 'BY-bot' ], 'move': 'translate' } ] }", "required_field_missing", "crossing_order.orders['a'].direction")]
        [InlineData("{ 'offset': 'diameter', 'orders': [ { 'id': 'a', 'stays': [ 'BX-bot' ], 'moves': [ 'BY-bot' ], 'move': 'translate', 'direction': 'up' } ] }", "value_not_in_vocabulary", "crossing_order.orders['a'].direction")]
        [InlineData("{ 'offset': 'diameter', 'orders': [ { 'id': 'a', 'stays': [ 'BX-bot' ], 'moves': [ 'BY-bot' ], 'move': 'translate', 'direction': [0,0,0] } ] }", "geometry_not_usable", "crossing_order.orders['a'].direction")]
        [InlineData("{ 'offset': 'diameter', 'orders': [ { 'id': 'a', 'stays': [ 'BX-bot' ], 'moves': [ 'BY-bot' ], 'move': 'translate', 'direction': [0,0,1], 'legs': 'both' } ] }", "value_not_in_vocabulary", "crossing_order.orders['a'].legs")]
        [InlineData("{ 'offset': 'diameter', 'orders': [ { 'id': 'a', 'stays': [ 'BX-bot' ], 'moves': [ 'BY-bot' ], 'move': 'legs' } ] }", "required_field_missing", "crossing_order.orders['a'].legs")]
        [InlineData("{ 'offset': 'diameter', 'orders': [ { 'id': 'a', 'stays': [ 'BX-bot' ], 'moves': [ 'BY-bot' ], 'move': 'legs', 'legs': 'both', 'direction': [0,0,1] } ] }", "value_not_in_vocabulary", "crossing_order.orders['a'].direction")]
        [InlineData("{ 'offset': 'diameter', 'orders': [ { 'id': 'a', 'stays': [ 'BX-bot' ], 'moves': [ 'BY-bot' ], 'move': 'legs', 'legs': 'middle' } ] }", "value_not_in_vocabulary", "crossing_order.orders['a'].legs")]
        public void A_crossing_order_that_cannot_be_read_refuses_the_set_by_name(string crossing, string code, string path)
        {
            StructuralRequirementSet s = SetWith(crossing);
            Assert.False(s.Ok);
            Assert.Equal(code, s.Code);
            Assert.Equal(path, s.Path);
        }

        [Fact]
        public void The_codes_are_published_and_distinct()
        {
            foreach (string c in new[] { RebarCrossing.CodeAmbiguous, RebarCrossing.CodeConflict, RebarCrossing.CodeNotApplicable, RebarCrossing.CodeBreaksCover })
                Assert.Contains(c, RebarCrossing.AllCodes);
            Assert.Equal(RebarCrossing.AllCodes.Length, RebarCrossing.AllCodes.Distinct().Count());
        }

        [Fact]
        public void A_piece_of_a_chunked_set_keeps_only_the_orders_whose_rules_it_still_carries()
        {
            JObject set = JObject.Parse(@"{
  'schema': 'horizun.structural-requirements/1', 'units': 'millimeter',
  'requirement_set': { 'id': 'ch', 'version': '1' },
  'bar_types': [ { 'id': 'N5', 'type_name': '#5' } ],
  'crossing_order': { 'offset': 'diameter', 'orders': [
      { 'id': 'v1-over-v2', 'stays': [ 'V1' ], 'moves': [ 'V2' ], 'move': 'translate', 'direction': 'toward_host_core' },
      { 'id': 'v1-own', 'stays': [ 'V1#h*#top' ], 'moves': [ 'V1#h*#bottom', 'V2' ], 'move': 'legs', 'legs': 'both' } ] },
  'section_rules': [
    { 'id': 'V1', 'host': { 'mark': 'V-1' }, 'cover': { 'distance_mm': 40 },
      'layers': [ { 'name': 'top', 'face': 'top', 'count': 2, 'bar_type': 'N5', 'extend': 'host_length' } ] },
    { 'id': 'V2', 'host': { 'mark': 'V-2' }, 'cover': { 'distance_mm': 40 },
      'layers': [ { 'name': 'top', 'face': 'top', 'count': 2, 'bar_type': 'N5', 'extend': 'host_length' } ] } ] }".Replace('\'', '"'));
            var hosts = new List<RequirementSetChunks.Host>
            {
                new RequirementSetChunks.Host { ElementId = 11, Category = "OST_StructuralFraming", TypeName = "B", Mark = "V-1", Level = "L1" },
                new RequirementSetChunks.Host { ElementId = 21, Category = "OST_StructuralFraming", TypeName = "B", Mark = "V-2", Level = "L2" }
            };
            RequirementSetChunks.Plan p = RequirementSetChunks.Split(set, hosts, RequirementSetChunks.ByLevel, 5);
            Assert.True(p.Ok, p.Why);
            StructuralRequirementSet l1 = StructuralRequirementSet.Load(p.Chunks.Single(c => c.Key == "L1").RequirementSet);
            Assert.True(l1.Ok, l1.Error);
            // V2 is not in this piece: the first order loses its only mover and goes; the second keeps V1's layers.
            Assert.Equal("v1-own", l1.CrossingOrder.Orders.Single().Id);
            Assert.Equal(new[] { "V1#h*#bottom" }, l1.CrossingOrder.Orders.Single().Moves);
            StructuralRequirementSet l2 = StructuralRequirementSet.Load(p.Chunks.Single(c => c.Key == "L2").RequirementSet);
            Assert.True(l2.Ok, l2.Error);
            Assert.Null(l2.CrossingOrder);
        }

        [Fact]
        public void The_decision_publishes_every_move_and_every_refusal()
        {
            List<Fake> joint = Joint();
            CrossingDecision d = RebarCrossing.Decide(Check(joint), Order(@"{ 'offset': 'diameter', 'orders': [
                { 'id': 'x-over-y', 'stays': [ 'BX-*' ], 'moves': [ 'BY-*' ], 'move': 'translate', 'direction': 'toward_host_core' } ] }"), Tol);
            JObject j = d.ToJson();
            Assert.Equal(2, (int)j["pairs_covered"]);
            Assert.Equal(0, (int)j["pairs_not_covered"]);
            JArray moves = (JArray)j["displacements"];
            Assert.Equal(2, moves.Count);
            JObject first = (JObject)moves.First(x => (string)x["rule_id"] == "BY-bot");
            Assert.Equal(16.0, (double)first["moved_by_mm"], 9);
            Assert.Equal("translate", (string)first["mode"]);
            Assert.Equal(new[] { 0.0, 0, 1 }, ((JArray)first["direction"]).Select(v => (double)v));
            Assert.NotNull(first["reason"]);
            Assert.Empty((JArray)j["refused"]);
        }
    }
}
