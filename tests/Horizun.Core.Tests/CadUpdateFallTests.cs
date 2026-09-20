// -----------------------------------------------------------------------------
// Horizun Core tests — original Horizun code.
//
// A REVISION THAT FLATTENS A DRAIN IS WORSE THAN ONE THAT REBUILDS IT.
//
// The conversion plan got its barrier first. The update route had none, and its
// two shapes fail differently:
//
//   `create` adds a level run beside neighbours that fall - a step in the drain;
//
//   `set_curve` takes an element that ALREADY EXISTS, possibly built to a fall
//   with fittings on both ends, and re-shapes it onto the drawing's flat line.
//   That destroys height that was there, on an element that keeps its id, its
//   parameters and everything hosted on it - which is exactly what makes it look
//   like a careful, surgical update.
//
// So both are blocked until a fall walk has written the two heights, and a
// blocked action publishes NO coordinates: the caller has nothing to send.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class CadUpdateFallTests
    {
        private const string Sha = "drawing-sha";

        private static CadRequirementSet Set(string slopeClause)
        {
            string doc = (@"{
              'schema': 'horizun.cad-requirements/1',
              'requirement_set': { 'id': 'drain', 'version': '1.0.0', 'title': 'Drainage' },
              'source': { 'units': 'millimeter' },
              'tolerances': { 'point_mm': 1.0, 'gap_mm': 25.0, 'angle_degrees': 2.0, 'arc_sagitta_mm': 5.0 },
              'rules': [
                { 'id': 'sani', 'precedence': 10, 'layers': ['P-SANI'], 'produces': 'pipe',
                  'discipline': 'plumbing', 'category': 'OST_PipeCurves', 'system_type': 'Sanitary',
                  'diameter_mm': 150, 'level': 'Level 1', 'offset_mm': 3000, SLOPE
                  'geometry': { 'from': 'single_lines', 'min_length_mm': 100 } }
              ]
            }").Replace('\'', '"').Replace("SLOPE", slopeClause);
            return CadRequirementSet.Load(JObject.Parse(doc));
        }

        private static List<CadSegment> Drawing() => new List<CadSegment>
        {
            new CadSegment(new CadPoint(0, 0), new CadPoint(10000, 0), "P-SANI")
        };

        private static CadUpdate Update(string slopeClause, out CadRequirementSet set,
                                        out List<CadCandidate> candidates)
        {
            set = Set(slopeClause);
            CadInterpretation interp = CadInterpretationRules.Interpret(Drawing(), set, Sha);
            candidates = interp.Candidates;

            // Nothing in the model yet, so every candidate is a create.
            return CadUpdateRules.Plan(candidates, new List<CadAuditSubject>(), set, Sha);
        }

        private static CadNetworkOptions Options() => new CadNetworkOptions
        {
            ConnectToleranceMm = 1.0,
            IdentityToleranceMm = 1.0,
            CollinearToleranceDegrees = 2.0
        };

        // ---- the barrier -----------------------------------------------------

        [Fact]
        public void A_revision_that_would_rebuild_a_sloped_run_is_blocked()
        {
            CadRequirementSet set; List<CadCandidate> candidates;
            CadUpdate update = Update("'slope_percent': 1.0,", out set, out candidates);

            CadUpdateAction create = update.Actions.Single(a => a.Kind == "create");
            Assert.True(create.Blocked);
            Assert.False(create.Automatic);
            Assert.Contains("a fall", create.BlockedUntil);

            // AND IT PUBLISHES NO COORDINATES. That is the part that stops a write:
            // the caller sends geometry_mm to horizun_create_elements, and there is
            // none to send.
            JObject json = create.ToJson();
            Assert.Null(json["geometry_mm"]);
            Assert.Contains("carries no coordinates", json.Value<string>("geometry_mm_withheld"));
        }

        [Fact]
        public void A_run_nobody_declared_a_slope_for_is_not_blocked()
        {
            CadRequirementSet set; List<CadCandidate> candidates;
            CadUpdate update = Update("", out set, out candidates);

            CadUpdateAction create = update.Actions.Single(a => a.Kind == "create");
            Assert.False(create.Blocked);
            Assert.NotNull(create.ToJson()["geometry_mm"]);
        }

        [Fact]
        public void A_declared_zero_slope_is_a_decision_and_is_not_blocked()
        {
            CadRequirementSet set; List<CadCandidate> candidates;
            CadUpdate update = Update("'slope_percent': 0.0,", out set, out candidates);
            Assert.All(update.Actions, a => Assert.False(a.Blocked));
        }

        [Fact]
        public void The_action_carries_the_bore_it_needs_to_convert_an_invert()
        {
            CadRequirementSet set; List<CadCandidate> candidates;
            CadUpdate update = Update("'slope_percent': 1.0,", out set, out candidates);
            Assert.Equal(150, update.Actions.Single(a => a.Kind == "create").DiameterMm.Value, 6);
        }

        // ---- lifting it ------------------------------------------------------

        [Fact]
        public void Naming_the_outfall_writes_the_two_heights_and_lifts_the_block()
        {
            CadRequirementSet set; List<CadCandidate> candidates;
            CadUpdate update = Update("'slope_percent': 1.0,", out set, out candidates);
            CadUpdateAction create = update.Actions.Single(a => a.Kind == "create");
            Assert.True(create.Blocked);

            CadNetwork net = CadNetworkRules.Build(Drawing(), Options(),
                CadNetworkRules.DeclarationsFrom(set));
            string outfall = CadFallRules.NodeNear(net, new CadPoint(0, 0), 1.0);
            CadFall fall = CadFallRules.Compute(net, outfall, 0, r => r.SlopePercent);

            string refusal;
            List<JObject> notGiven = CadUpdateRules.ApplyFall(update, net, fall, set.PointToleranceMm,
                                                              out refusal);
            Assert.Null(refusal);
            Assert.Empty(notGiven);
            Assert.False(create.Blocked);

            // 10 m at 1% is 100 mm of fall; both ends carry half the 150 mm bore.
            Assert.Equal(75, create.Geometry[0].Z, 3);
            Assert.Equal(175, create.Geometry[create.Geometry.Count - 1].Z, 3);
            Assert.NotNull(create.ToJson()["geometry_mm"]);
            Assert.Contains("CENTRELINES", create.Says);
        }

        [Fact]
        public void A_run_with_no_bore_stays_blocked_and_still_publishes_nothing()
        {
            CadRequirementSet set; List<CadCandidate> candidates;
            CadUpdate update = Update("'slope_percent': 1.0,", out set, out candidates);
            CadUpdateAction create = update.Actions.Single(a => a.Kind == "create");
            create.DiameterMm = null;                       // the rule declared no bore

            CadNetwork net = CadNetworkRules.Build(Drawing(), Options(),
                CadNetworkRules.DeclarationsFrom(set));
            CadFall fall = CadFallRules.Compute(net, CadFallRules.NodeNear(net, new CadPoint(0, 0), 1.0),
                                                0, r => r.SlopePercent);
            string refusal;
            List<JObject> notGiven = CadUpdateRules.ApplyFall(update, net, fall, 1.0, out refusal);

            Assert.Null(refusal);
            Assert.Contains("bore", notGiven.Single().Value<string>("why"));
            Assert.True(create.Blocked);
            Assert.Null(create.ToJson()["geometry_mm"]);
        }

        [Fact]
        public void A_refused_walk_applies_to_nothing_and_leaves_every_block_standing()
        {
            CadRequirementSet set; List<CadCandidate> candidates;
            CadUpdate update = Update("'slope_percent': 1.0,", out set, out candidates);

            CadNetwork net = CadNetworkRules.Build(Drawing(), Options(),
                CadNetworkRules.DeclarationsFrom(set));
            CadFall fall = CadFallRules.Compute(net, "no-such-node", 0, r => r.SlopePercent);

            string refusal;
            CadUpdateRules.ApplyFall(update, net, fall, 1.0, out refusal);
            Assert.Contains("fall_refused", refusal);
            Assert.All(update.Actions.Where(a => a.Kind == "create"), a => Assert.True(a.Blocked));
        }

        [Fact]
        public void A_fall_is_never_applied_backwards_when_position_cannot_decide()
        {
            CadRequirementSet set; List<CadCandidate> candidates;
            CadUpdate update = Update("'slope_percent': 1.0,", out set, out candidates);

            CadNetwork net = CadNetworkRules.Build(Drawing(), Options(),
                CadNetworkRules.DeclarationsFrom(set));
            CadFall fall = CadFallRules.Compute(net, CadFallRules.NodeNear(net, new CadPoint(0, 0), 1.0),
                                                0, r => r.SlopePercent);

            // A tolerance wider than the run: both pairings fit, so which end is
            // upstream is a coin toss - and a coin toss is a pipe that may run uphill.
            string refusal;
            List<JObject> notGiven = CadUpdateRules.ApplyFall(update, net, fall, 20000, out refusal);
            Assert.Contains("which end is downstream", notGiven.Single().Value<string>("why"));
            Assert.True(update.Actions.Single(a => a.Kind == "create").Blocked);
        }
    }
}
