// -----------------------------------------------------------------------------
// Horizun Core tests — original Horizun code.
//
// THE BARRIER, NOT THE WARNING.
//
// The previous campaign made the conversion SAY that a run with a declared slope
// was being built flat. Saying it stops nothing: the action still carried a flat
// Z, the emitted request still carried the action, and horizun_create_elements
// still built a level drain from a drawing that declares 1%.
//
// So these cases are about a row that cannot be sent rather than a sentence that
// can be skipped. The row is absent from the create request until a fall walk has
// actually written its two heights - which means the block survives the three
// ways a plan reaches a write: this process, a persisted plan re-read later, and
// a caller who assembled the JSON by hand from the reply.
//
// The other half is just as important: a run nobody said anything about is NOT
// blocked. A rule that declares no slope is saying the run is level, and a rule
// that declares zero is saying it too. Blocking those would make the barrier
// useless by making it universal.
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class CadFallBarrierTests
    {
        private const string Hash = "drawing-sha";
        private const string SourceFp = "cadsrc:barrier";

        private static CadRequirementSet Set(string rulesJson)
        {
            string doc = @"{
              'schema': 'horizun.cad-requirements/1',
              'requirement_set': { 'id': 'drain', 'version': '1.0.0', 'title': 'Drainage' },
              'source': { 'units': 'millimeter' },
              'tolerances': { 'point_mm': 1.0, 'gap_mm': 25.0, 'angle_degrees': 2.0, 'arc_sagitta_mm': 5.0 },
              'rules': RULES
            }".Replace('\'', '"').Replace("RULES", rulesJson);
            return CadRequirementSet.Load(JObject.Parse(doc));
        }

        /// <summary>Two sanitary runs and one vent, with the slope the caller passes in.</summary>
        private static string Rules(string slopeClause) => (@"[
          { 'id': 'sani', 'precedence': 10, 'layers': ['P-SANI'], 'produces': 'pipe', 'discipline': 'plumbing',
            'category': 'OST_PipeCurves', 'system_type': 'Sanitary', 'diameter_mm': 150, 'level': 'Level 1',
            'offset_mm': 3000, SLOPE
            'geometry': { 'from': 'single_lines', 'min_length_mm': 100 } },
          { 'id': 'vent', 'precedence': 10, 'layers': ['P-VENT'], 'produces': 'pipe', 'discipline': 'plumbing',
            'category': 'OST_PipeCurves', 'system_type': 'Vent', 'diameter_mm': 50, 'level': 'Level 1',
            'offset_mm': 3000,
            'geometry': { 'from': 'single_lines', 'min_length_mm': 100 } }
        ]").Replace('\'', '"').Replace("SLOPE", slopeClause);

        private static List<CadSegment> Drawing() => new List<CadSegment>
        {
            new CadSegment(new CadPoint(0, 0), new CadPoint(10000, 0), "P-SANI"),
            new CadSegment(new CadPoint(0, 2000), new CadPoint(6000, 2000), "P-VENT")
        };

        private static CadConversionPlan Plan(string slopeClause)
        {
            CadRequirementSet set = Set(Rules(slopeClause));
            CadInterpretation interp = CadInterpretationRules.Interpret(Drawing(), set, Hash);
            return CadConversionPlanRules.Plan(interp, set, SourceFp, false);
        }

        private static IEnumerable<JObject> RowsOf(CadConversionPlan plan) =>
            CadConversionPlanRules.AsCreateRequests(plan, "Test.rvt")
                .SelectMany(r => ((JArray)r["elements"]).Cast<JObject>());

        // ---- the block ---------------------------------------------------

        [Fact]
        public void A_run_whose_rule_declares_a_slope_is_planned_and_not_buildable()
        {
            CadConversionPlan plan = Plan("'slope_percent': 1.0,");
            CadPlannedAction sani = plan.Actions.Single(a => a.Layer == "P-SANI");

            // PLANNED: the reading is settled, the geometry is right, the type is
            // resolved. It is the HEIGHT that nobody has worked out.
            Assert.True(sani.Blocked);
            Assert.Contains("a fall", sani.BlockedUntil);
            Assert.Contains("1", sani.BlockedUntil);

            // And it is not in what gets sent.
            Assert.DoesNotContain(RowsOf(plan), r => r.Value<string>("kind") == "pipe" &&
                                                     ((JArray)r["start"])[0].Value<double>() == 0 &&
                                                     ((JArray)r["end"])[0].Value<double>() == 10000);
        }

        [Fact]
        public void A_run_nobody_declared_a_slope_for_is_built_normally()
        {
            CadConversionPlan plan = Plan("'slope_percent': 1.0,");
            CadPlannedAction vent = plan.Actions.Single(a => a.Layer == "P-VENT");

            // A rule with no slope is SAYING the run is level. Blocking it would
            // make the barrier universal, which is the same as not having one.
            Assert.False(vent.Blocked);
            Assert.Contains(RowsOf(plan), r => ((JArray)r["end"])[0].Value<double>() == 6000);
        }

        [Fact]
        public void A_declared_zero_slope_is_a_decision_and_is_not_blocked()
        {
            CadConversionPlan plan = Plan("'slope_percent': 0.0,");
            Assert.All(plan.Actions, a => Assert.False(a.Blocked));
            Assert.Equal(2, RowsOf(plan).Count());
        }

        [Fact]
        public void The_plan_reports_what_is_held_and_why()
        {
            CadRequirementSet set = Set(Rules("'slope_percent': 2.5,"));
            CadInterpretation interp = CadInterpretationRules.Interpret(Drawing(), set, Hash);
            CadConversionPlan plan = CadConversionPlanRules.Plan(interp, set, SourceFp, false);

            JObject json = CadConversionPlanRules.ToJson(plan, set);
            Assert.Equal(1, json.Value<int>("blocked"));
            Assert.Contains("NOT in execute_plan_request", json.Value<string>("blocked_means"));
            Assert.Contains("a fall", ((JArray)json["blocked_detail"])[0].Value<string>("waiting_for"));
        }

        // ---- lifting it ----------------------------------------------------

        private static CadNetworkOptions Options() => new CadNetworkOptions
        {
            ConnectToleranceMm = 1.0,
            IdentityToleranceMm = 1.0,
            CollinearToleranceDegrees = 2.0
        };

        [Fact]
        public void Naming_the_outfall_lifts_the_block_and_the_run_becomes_buildable()
        {
            CadRequirementSet set = Set(Rules("'slope_percent': 1.0,"));
            CadInterpretation interp = CadInterpretationRules.Interpret(Drawing(), set, Hash);
            CadConversionPlan plan = CadConversionPlanRules.Plan(interp, set, SourceFp, false);
            Assert.True(plan.Actions.Single(a => a.Layer == "P-SANI").Blocked);

            CadNetwork net = CadNetworkRules.Build(Drawing(), Options(),
                CadNetworkRules.DeclarationsFrom(set));
            string outfall = CadFallRules.NodeNear(net, new CadPoint(0, 0), 1.0);
            CadFall fall = CadFallRules.Compute(net, outfall, 0, r => r.SlopePercent);

            CadConversionPlanRules.CadFallApplication applied =
                CadConversionPlanRules.ApplyFall(plan, net, fall, set.PointToleranceMm);
            Assert.True(applied.Ok);
            Assert.Equal(1, applied.ActionsGivenFall);

            CadPlannedAction sani = plan.Actions.Single(a => a.Layer == "P-SANI");
            Assert.False(sani.Blocked);

            // The two ends now differ, and the row is in the request.
            double z0 = ((JArray)sani.Arguments["start"])[2].Value<double>();
            double z1 = ((JArray)sani.Arguments["end"])[2].Value<double>();
            Assert.NotEqual(z0, z1);
            Assert.Contains(RowsOf(plan), r => ((JArray)r["end"])[0].Value<double>() == 10000);
        }

        [Fact]
        public void A_run_the_fall_could_not_reach_stays_blocked_and_stays_unsendable()
        {
            CadRequirementSet set = Set(Rules("'slope_percent': 1.0,"));
            CadInterpretation interp = CadInterpretationRules.Interpret(Drawing(), set, Hash);
            CadConversionPlan plan = CadConversionPlanRules.Plan(interp, set, SourceFp, false);

            // The walk runs on the VENT's end of the drawing, which the sanitary run
            // does not touch - so the sanitary run gets no height from it.
            CadNetwork net = CadNetworkRules.Build(Drawing(), Options(),
                CadNetworkRules.DeclarationsFrom(set));
            string outfall = CadFallRules.NodeNear(net, new CadPoint(0, 2000), 1.0);
            CadFall fall = CadFallRules.Compute(net, outfall, 0, r => r.SlopePercent);
            CadConversionPlanRules.ApplyFall(plan, net, fall, set.PointToleranceMm);

            CadPlannedAction sani = plan.Actions.Single(a => a.Layer == "P-SANI");
            Assert.True(sani.Blocked);
            Assert.DoesNotContain(RowsOf(plan), r => ((JArray)r["end"])[0].Value<double>() == 10000);
        }

        [Fact]
        public void The_block_survives_a_round_trip_through_the_emitted_request()
        {
            // THE CASE THE BARRIER EXISTS FOR. A caller keeps the plan, comes back
            // tomorrow, and sends execute_plan_request.actions to create_elements
            // without reading a word of the reply. What is not in those rows cannot
            // be built, whatever anybody did or did not read.
            CadConversionPlan plan = Plan("'slope_percent': 1.0,");
            List<JObject> requests = CadConversionPlanRules.AsCreateRequests(plan, "Test.rvt");

            int rows = requests.Sum(r => ((JArray)r["elements"]).Count);
            Assert.Equal(1, rows);                                   // the vent, not the drain
            Assert.Equal(2, plan.Actions.Count);                     // both are still PLANNED
            Assert.Single(plan.Actions.Where(a => a.Blocked));
        }
    }
}
