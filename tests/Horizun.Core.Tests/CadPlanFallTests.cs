// -----------------------------------------------------------------------------
// Horizun Core tests — original Horizun code.
//
// A PLAN THAT KNOWS THE FALL AND BUILDS IT FLAT IS THE WORST OF THE THREE.
//
// There are three ways a drainage conversion can end. It can be built flat and
// say so — honest, and useless. It can be built to the fall the drawing implies.
// Or it can be built flat while a block in the reply says the fall was computed,
// which is the one that ships, because everything about it looks finished.
//
// So these cases are about the join: the inverts the walk computed reaching the
// two ends of the planned runs, and every way that join can be wrong while
// looking right.
//
// Two of them are worth naming here. WHICH END IS WHICH is decided by position,
// never by order — a drawn line has a first point and a second point and that is
// the order somebody clicked, so a fall applied by order is a pipe running uphill
// in a model that passes every check. And AN INVERT IS NOT A CENTRELINE: the walk
// computes inside-bottom heights because that is what a drainage drawing puts on
// its nodes, and create_elements places a run on its centreline. Half a diameter
// is 75 mm on a 150 mm drain — the difference between clearing the beam and not.
//
// NOT RUN in this phase. Written to be run when running tests is authorised.
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class CadPlanFallTests
    {
        private const double Tolerance = 1.0;

        private static CadSegment Seg(double x1, double y1, double x2, double y2,
                                      string layer = "P-SANI") =>
            new CadSegment(new CadPoint(x1, y1), new CadPoint(x2, y2), layer,
                           CadCurveKind.Line, 0, x1 + "," + y1 + "-" + x2 + "," + y2);

        private static CadNetworkOptions Options() => new CadNetworkOptions
        {
            ConnectToleranceMm = Tolerance,
            IdentityToleranceMm = Tolerance,
            CollinearToleranceDegrees = 2.0,
            GapReviewDistanceMm = 50.0,
            ThroughToleranceDegrees = 15.0
        };

        private static CadNetwork Net(IEnumerable<CadSegment> segments, double? slope = 1.0) =>
            CadNetworkRules.Build(segments.ToList(), Options(),
                layer => new CadNetworkRules.CadRunDeclaration { SlopePercent = slope });

        /// <summary>A 10 m run and a 5 m branch off its far end.</summary>
        private static CadNetwork Branch(double? slope = 1.0) =>
            Net(new[] { Seg(0, 0, 10000, 0), Seg(10000, 0, 10000, 5000, "P-BRANCH") }, slope);

        private static string NodeAt(CadNetwork net, double x, double y) =>
            CadFallRules.NodeNear(net, new CadPoint(x, y), Tolerance);

        private static CadRun RunFrom(CadNetwork net, double x, double y) =>
            net.Runs.First(r => r.StartNode == NodeAt(net, x, y) || r.EndNode == NodeAt(net, x, y));

        /// <summary>
        /// A planned pipe over one run of the network, flat at <paramref name="z"/>,
        /// carrying that run's semantic id — which is the only thing the join uses.
        /// </summary>
        private static CadPlannedAction Pipe(CadRun run, double? bore = 150, double z = 3000,
                                             bool reversed = false, string kind = "pipe")
        {
            CadPoint a = reversed ? run.End : run.Start;
            CadPoint b = reversed ? run.Start : run.End;
            var args = new JObject
            {
                ["kind"] = kind,
                ["start"] = new JArray(a.X, a.Y, z),
                ["end"] = new JArray(b.X, b.Y, z)
            };
            if (bore.HasValue) args["diameter"] = bore.Value;
            return new CadPlannedAction
            {
                Kind = kind,
                CandidateId = "c-" + run.Id,
                SemanticId = run.SemanticId,
                Layer = run.Layer,
                Arguments = args
            };
        }

        private static CadConversionPlan PlanOf(params CadPlannedAction[] actions)
        {
            var plan = new CadConversionPlan { PlanFingerprint = "fp-before" };
            plan.Actions.AddRange(actions);
            return plan;
        }

        private static double Z(CadPlannedAction a, string end) =>
            ((JArray)a.Arguments[end])[2].Value<double>();

        // ---- the fall arrives ------------------------------------------------

        [Fact]
        public void The_end_further_from_the_outfall_is_placed_higher()
        {
            CadNetwork net = Branch();
            CadRun run = RunFrom(net, 0, 0);
            CadPlannedAction action = Pipe(run);
            CadConversionPlan plan = PlanOf(action);

            CadFall fall = CadFallRules.Compute(net, NodeAt(net, 0, 0), 0, r => r.SlopePercent);
            CadConversionPlanRules.CadFallApplication applied =
                CadConversionPlanRules.ApplyFall(plan, net, fall, Tolerance);

            Assert.True(applied.Ok);
            Assert.Equal(1, applied.ActionsGivenFall);

            // 10 m at 1% is 100 mm of rise; both ends carry half the 150 mm bore.
            Assert.Equal(75, Z(action, "start"), 3);
            Assert.Equal(175, Z(action, "end"), 3);
        }

        [Fact]
        public void A_run_drawn_the_other_way_round_still_falls_the_same_way()
        {
            CadNetwork net = Branch();
            CadRun run = RunFrom(net, 0, 0);

            // The SAME run, with the two points in the order somebody happened to
            // click them. Applying the fall by order would put the outfall end
            // 100 mm above the far end: a pipe running uphill, in a model that
            // passes every connectivity check there is.
            CadPlannedAction action = Pipe(run, reversed: true);
            CadConversionPlan plan = PlanOf(action);

            CadFall fall = CadFallRules.Compute(net, NodeAt(net, 0, 0), 0, r => r.SlopePercent);
            CadConversionPlanRules.CadFallApplication applied =
                CadConversionPlanRules.ApplyFall(plan, net, fall, Tolerance);

            Assert.True(applied.Ok);
            Assert.Equal(175, Z(action, "start"), 3);
            Assert.Equal(75, Z(action, "end"), 3);
        }

        [Fact]
        public void The_height_is_a_centreline_and_the_invert_is_half_a_bore_below_it()
        {
            CadNetwork net = Branch();
            CadRun run = RunFrom(net, 0, 0);
            CadPlannedAction action = Pipe(run, bore: 200);
            CadConversionPlan plan = PlanOf(action);

            CadFall fall = CadFallRules.Compute(net, NodeAt(net, 0, 0), 500, r => r.SlopePercent);
            CadConversionPlanRules.CadFallApplication applied =
                CadConversionPlanRules.ApplyFall(plan, net, fall, Tolerance);

            Assert.True(applied.Ok);

            // The outfall invert is 500; the pipe's centre is 100 mm above it.
            Assert.Equal(600, Z(action, "start"), 3);
            Assert.Equal(700, Z(action, "end"), 3);
        }

        [Fact]
        public void The_fall_and_the_upstream_end_are_recorded_on_the_action()
        {
            CadNetwork net = Branch();
            CadRun run = RunFrom(net, 0, 0);
            CadPlannedAction action = Pipe(run);
            CadConversionPlan plan = PlanOf(action);

            CadFall fall = CadFallRules.Compute(net, NodeAt(net, 0, 0), 0, r => r.SlopePercent);
            CadConversionPlanRules.ApplyFall(plan, net, fall, Tolerance);

            string said = string.Join(" ", action.ExpectedVerification);
            Assert.Contains("100", said);
            Assert.Contains("upstream", said);
            Assert.Contains("CENTRELINE", said);
        }

        [Fact]
        public void Both_runs_of_a_branch_take_their_own_heights()
        {
            CadNetwork net = Branch();
            CadRun trunk = net.Runs.Single(r => r.Layer == "P-SANI");
            CadRun branch = net.Runs.Single(r => r.Layer == "P-BRANCH");
            CadPlannedAction a = Pipe(trunk), b = Pipe(branch);
            CadConversionPlan plan = PlanOf(a, b);

            CadFall fall = CadFallRules.Compute(net, NodeAt(net, 0, 0), 0, r => r.SlopePercent);
            CadConversionPlanRules.CadFallApplication applied =
                CadConversionPlanRules.ApplyFall(plan, net, fall, Tolerance);

            Assert.Equal(2, applied.ActionsGivenFall);
            Assert.Empty(applied.NotGivenFall);

            // The branch starts where the trunk ends: 100 mm of rise, and climbs
            // another 50 over its own 5 m.
            double high = new[] { Z(b, "start"), Z(b, "end") }.Max();
            double low = new[] { Z(b, "start"), Z(b, "end") }.Min();
            Assert.Equal(175, low, 3);
            Assert.Equal(225, high, 3);
        }

        // ---- and every way the join can be wrong -------------------------------

        [Fact]
        public void A_run_with_no_declared_bore_takes_no_fall_at_all()
        {
            CadNetwork net = Branch();
            CadRun run = RunFrom(net, 0, 0);
            CadPlannedAction action = Pipe(run, bore: null);
            CadConversionPlan plan = PlanOf(action);

            CadFall fall = CadFallRules.Compute(net, NodeAt(net, 0, 0), 0, r => r.SlopePercent);
            CadConversionPlanRules.CadFallApplication applied =
                CadConversionPlanRules.ApplyFall(plan, net, fall, Tolerance);

            Assert.True(applied.Ok);
            Assert.Equal(0, applied.ActionsGivenFall);

            // Placed half a diameter wrong is not a rounding: it is left flat, and
            // the reason names the bore rather than the geometry.
            Assert.Equal(3000, Z(action, "start"), 3);
            Assert.Equal(3000, Z(action, "end"), 3);
            Assert.Contains("bore", applied.NotGivenFall.Single().Value<string>("why"));
        }

        [Fact]
        public void A_node_the_outfall_reaches_at_two_heights_applies_to_nothing()
        {
            // A triangle: the far corner is 15 m away round two sides and 11.18 m
            // away down the diagonal, so the walk gives it two different inverts
            // and refuses to choose.
            CadNetwork net = Net(new[]
            {
                Seg(0, 0, 10000, 0),
                Seg(10000, 0, 10000, 5000),
                Seg(10000, 5000, 0, 0)
            });
            var actions = net.Runs.Select(r => Pipe(r)).ToArray();
            CadConversionPlan plan = PlanOf(actions);

            CadFall fall = CadFallRules.Compute(net, NodeAt(net, 0, 0), 0, r => r.SlopePercent);
            Assert.NotEmpty(fall.Conflicts);

            CadConversionPlanRules.CadFallApplication applied =
                CadConversionPlanRules.ApplyFall(plan, net, fall, Tolerance);

            Assert.False(applied.Ok);
            Assert.Contains("fall_conflicts", applied.Refusal);
            Assert.Equal(0, applied.ActionsGivenFall);

            // NOTHING was touched — not the runs the walk was sure about either.
            Assert.All(actions, a => Assert.Equal(3000, Z(a, "start"), 3));
            Assert.All(actions, a => Assert.Equal(3000, Z(a, "end"), 3));
            Assert.Equal("fp-before", plan.PlanFingerprint);
        }

        [Fact]
        public void A_refused_walk_applies_to_nothing()
        {
            CadNetwork net = Branch();
            CadPlannedAction action = Pipe(RunFrom(net, 0, 0));
            CadConversionPlan plan = PlanOf(action);

            CadFall fall = CadFallRules.Compute(net, "no-such-node", 0, r => r.SlopePercent);
            Assert.False(fall.Ok);

            CadConversionPlanRules.CadFallApplication applied =
                CadConversionPlanRules.ApplyFall(plan, net, fall, Tolerance);

            Assert.False(applied.Ok);
            Assert.Contains("fall_refused", applied.Refusal);
            Assert.Equal(3000, Z(action, "start"), 3);
            Assert.Equal("fp-before", plan.PlanFingerprint);
        }

        [Fact]
        public void A_run_the_walk_never_reached_keeps_its_flat_elevation()
        {
            // A second network, not touching the first: the walk reaches nothing of
            // it, and its runs are named rather than quietly left out.
            CadNetwork net = Net(new[]
            {
                Seg(0, 0, 10000, 0),
                Seg(50000, 50000, 60000, 50000, "P-ELSEWHERE")
            });
            CadRun far = net.Runs.Single(r => r.Layer == "P-ELSEWHERE");
            CadPlannedAction action = Pipe(far);
            CadConversionPlan plan = PlanOf(action);

            CadFall fall = CadFallRules.Compute(net, NodeAt(net, 0, 0), 0, r => r.SlopePercent);
            CadConversionPlanRules.CadFallApplication applied =
                CadConversionPlanRules.ApplyFall(plan, net, fall, Tolerance);

            Assert.True(applied.Ok);
            Assert.Equal(0, applied.ActionsGivenFall);
            Assert.Equal(3000, Z(action, "start"), 3);
            Assert.Contains("never reached", applied.NotGivenFall.Single().Value<string>("why"));
        }

        [Fact]
        public void An_action_whose_ends_are_somewhere_else_is_not_given_a_fall()
        {
            CadNetwork net = Branch();
            CadRun run = RunFrom(net, 0, 0);
            CadPlannedAction action = Pipe(run);

            // The id still matches and the geometry no longer does. Taking the
            // heights anyway would put this drawing's fall on a run that is not
            // where the drawing says it is.
            action.Arguments["start"] = new JArray(80000.0, 80000.0, 3000.0);
            action.Arguments["end"] = new JArray(90000.0, 80000.0, 3000.0);
            CadConversionPlan plan = PlanOf(action);

            CadFall fall = CadFallRules.Compute(net, NodeAt(net, 0, 0), 0, r => r.SlopePercent);
            CadConversionPlanRules.CadFallApplication applied =
                CadConversionPlanRules.ApplyFall(plan, net, fall, Tolerance);

            Assert.Equal(0, applied.ActionsGivenFall);
            Assert.Contains("not where", applied.NotGivenFall.Single().Value<string>("why"));
        }

        [Fact]
        public void When_position_cannot_say_which_end_is_downstream_nothing_is_applied()
        {
            CadNetwork net = Branch();
            CadRun run = RunFrom(net, 0, 0);
            CadPlannedAction action = Pipe(run);
            CadConversionPlan plan = PlanOf(action);

            CadFall fall = CadFallRules.Compute(net, NodeAt(net, 0, 0), 0, r => r.SlopePercent);

            // A tolerance wider than the run itself: both pairings fit, so which end
            // is upstream is a coin toss — and a coin toss here is a pipe that may
            // run uphill.
            CadConversionPlanRules.CadFallApplication applied =
                CadConversionPlanRules.ApplyFall(plan, net, fall, 20000);

            Assert.Equal(0, applied.ActionsGivenFall);
            Assert.Contains("which end is", applied.NotGivenFall.Single().Value<string>("why"));
            Assert.Equal(3000, Z(action, "start"), 3);
        }

        [Fact]
        public void A_run_that_already_carries_a_fall_is_not_overwritten()
        {
            CadNetwork net = Branch();
            CadRun run = RunFrom(net, 0, 0);
            CadPlannedAction action = Pipe(run);
            ((JArray)action.Arguments["end"])[2] = 2900.0;   // somebody sloped it already
            CadConversionPlan plan = PlanOf(action);

            CadFall fall = CadFallRules.Compute(net, NodeAt(net, 0, 0), 0, r => r.SlopePercent);
            CadConversionPlanRules.CadFallApplication applied =
                CadConversionPlanRules.ApplyFall(plan, net, fall, Tolerance);

            Assert.Equal(0, applied.ActionsGivenFall);
            Assert.Equal(3000, Z(action, "start"), 3);
            Assert.Equal(2900, Z(action, "end"), 3);
            Assert.Contains("already", applied.NotGivenFall.Single().Value<string>("why"));
        }

        [Fact]
        public void An_action_that_is_not_a_run_is_left_alone_and_not_reported()
        {
            CadNetwork net = Branch();
            CadRun run = RunFrom(net, 0, 0);
            CadPlannedAction wall = Pipe(run, kind: "wall");
            CadPlannedAction pipe = Pipe(net.Runs.Single(r => r.Layer == "P-BRANCH"));
            CadConversionPlan plan = PlanOf(wall, pipe);

            CadFall fall = CadFallRules.Compute(net, NodeAt(net, 0, 0), 0, r => r.SlopePercent);
            CadConversionPlanRules.CadFallApplication applied =
                CadConversionPlanRules.ApplyFall(plan, net, fall, Tolerance);

            Assert.Equal(1, applied.ActionsGivenFall);
            Assert.Empty(applied.NotGivenFall);   // a wall has no fall to miss
            Assert.Equal(3000, Z(wall, "start"), 3);
        }

        [Fact]
        public void An_id_that_names_two_runs_is_two_matches_and_not_one()
        {
            CadNetwork net = Branch();
            CadRun trunk = net.Runs.Single(r => r.Layer == "P-SANI");
            CadRun branch = net.Runs.Single(r => r.Layer == "P-BRANCH");

            // Two runs answering to one id: the model would be built from whichever
            // sorted first, and the reply would look completely ordinary.
            branch.SemanticId = trunk.SemanticId;

            CadPlannedAction action = Pipe(trunk);
            CadConversionPlan plan = PlanOf(action);

            CadFall fall = CadFallRules.Compute(net, NodeAt(net, 0, 0), 0, r => r.SlopePercent);
            CadConversionPlanRules.CadFallApplication applied =
                CadConversionPlanRules.ApplyFall(plan, net, fall, Tolerance);

            Assert.Equal(0, applied.ActionsGivenFall);
            Assert.Contains("more than one run", applied.NotGivenFall.Single().Value<string>("why"));
        }

        // ---- and what the plan itself is left saying --------------------------

        [Fact]
        public void A_changed_plan_loses_the_fingerprint_it_was_reviewed_under()
        {
            CadNetwork net = Branch();
            CadPlannedAction action = Pipe(RunFrom(net, 0, 0));
            CadConversionPlan plan = PlanOf(action);

            CadFall fall = CadFallRules.Compute(net, NodeAt(net, 0, 0), 0, r => r.SlopePercent);
            CadConversionPlanRules.CadFallApplication applied =
                CadConversionPlanRules.ApplyFall(plan, net, fall, Tolerance);

            // The fingerprint is how an apply knows it was handed the plan that was
            // reviewed. This plan now builds at different heights.
            Assert.True(applied.NeedsRefingerprinting);
            Assert.Null(plan.PlanFingerprint);
        }

        [Fact]
        public void A_plan_nothing_changed_keeps_its_fingerprint()
        {
            CadNetwork net = Branch();
            CadPlannedAction action = Pipe(RunFrom(net, 0, 0), bore: null);
            CadConversionPlan plan = PlanOf(action);

            CadFall fall = CadFallRules.Compute(net, NodeAt(net, 0, 0), 0, r => r.SlopePercent);
            CadConversionPlanRules.CadFallApplication applied =
                CadConversionPlanRules.ApplyFall(plan, net, fall, Tolerance);

            Assert.False(applied.NeedsRefingerprinting);
            Assert.Equal("fp-before", plan.PlanFingerprint);
        }

        [Fact]
        public void The_plan_warns_when_some_of_its_runs_were_left_flat()
        {
            CadNetwork net = Branch();
            CadConversionPlan plan = PlanOf(
                Pipe(net.Runs.Single(r => r.Layer == "P-SANI")),
                Pipe(net.Runs.Single(r => r.Layer == "P-BRANCH"), bore: null));

            CadFall fall = CadFallRules.Compute(net, NodeAt(net, 0, 0), 0, r => r.SlopePercent);
            CadConversionPlanRules.ApplyFall(plan, net, fall, Tolerance);

            // A plan half in fall is the one that ships: it looks more finished
            // than the flat one it replaced.
            Assert.Contains(plan.Warnings, w => w.Contains("flat elevation"));
        }

        [Fact]
        public void A_layer_with_no_declared_slope_is_named_as_the_thing_to_fix()
        {
            // Not "the walk never reached it": the walk reached it and stopped,
            // because nothing said how steep this layer runs. That is one line of
            // the requirement set, and the reply should say so rather than send
            // somebody to look at the drawing.
            CadNetwork net = Branch(slope: null);
            CadPlannedAction action = Pipe(RunFrom(net, 0, 0));
            CadConversionPlan plan = PlanOf(action);

            CadFall fall = CadFallRules.Compute(net, NodeAt(net, 0, 0), 0, r => r.SlopePercent);
            Assert.NotEmpty(fall.Blocked);

            CadConversionPlanRules.CadFallApplication applied =
                CadConversionPlanRules.ApplyFall(plan, net, fall, Tolerance);

            Assert.Equal(0, applied.ActionsGivenFall);
            string why = applied.NotGivenFall.Single().Value<string>("why");
            Assert.Contains("declares no slope for layer", why);
            Assert.Contains("P-SANI", why);
            Assert.Equal(3000, Z(action, "start"), 3);
        }

        private const string FlatExpectation =
            "its endpoints match the drawn line within the point tolerance";

        [Fact]
        public void The_flat_endpoint_expectation_is_corrected_on_a_run_that_took_a_fall()
        {
            CadNetwork net = Branch();
            CadPlannedAction action = Pipe(RunFrom(net, 0, 0));
            action.ExpectedVerification.Add(FlatExpectation);
            CadConversionPlan plan = PlanOf(action);

            CadFall fall = CadFallRules.Compute(net, NodeAt(net, 0, 0), 0, r => r.SlopePercent);
            CadConversionPlanRules.ApplyFall(plan, net, fall, Tolerance);

            // In plan it still holds; in Z it does not, on purpose. Two statements
            // about one element, one of them wrong, is how a verification list
            // stops being read at all.
            Assert.DoesNotContain(FlatExpectation, action.ExpectedVerification);
            Assert.Contains(action.ExpectedVerification, v => v.Contains("IN PLAN"));
        }

        [Fact]
        public void A_run_left_flat_keeps_the_expectation_that_is_still_true_of_it()
        {
            CadNetwork net = Branch();
            CadPlannedAction action = Pipe(RunFrom(net, 0, 0), bore: null);
            action.ExpectedVerification.Add(FlatExpectation);
            CadConversionPlan plan = PlanOf(action);

            CadFall fall = CadFallRules.Compute(net, NodeAt(net, 0, 0), 0, r => r.SlopePercent);
            CadConversionPlanRules.ApplyFall(plan, net, fall, Tolerance);

            Assert.Contains(FlatExpectation, action.ExpectedVerification);
        }

        [Fact]
        public void The_reply_says_which_kind_of_height_it_wrote()
        {
            CadNetwork net = Branch();
            CadConversionPlan plan = PlanOf(Pipe(RunFrom(net, 0, 0)));

            CadFall fall = CadFallRules.Compute(net, NodeAt(net, 0, 0), 0, r => r.SlopePercent);
            JObject json = CadConversionPlanRules.ApplyFall(plan, net, fall, Tolerance).ToJson();

            Assert.True(json.Value<bool>("applied"));
            Assert.Contains("CENTRELINE", json.Value<string>("heights_are"));
            Assert.Contains("INVERT", json.Value<string>("heights_are"));

            // "1 run took a fall" is the same sentence whether there was one run
            // or three hundred. The denominator is published beside it.
            Assert.Equal(1, json.Value<int>("actions_given_fall"));
            Assert.Equal(1, json.Value<int>("runs_that_could_have_taken_one"));
        }
    }
}
