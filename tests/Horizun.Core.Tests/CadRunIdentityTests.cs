// -----------------------------------------------------------------------------
// Horizun Core tests — original Horizun code.
//
// THE CLAIM THE WHOLE MEP ROUTE RESTS ON.
//
// horizun_cad_networks reads a drawing as runs. horizun_plan_from_cad reads the
// same drawing as candidates and horizun_apply_cad_plan stamps each candidate's
// SEMANTIC ID on the element it builds. horizun_cad_connect then resolves a
// junction's members by that id.
//
// All of that works only if a run and the candidate built from it have the SAME
// semantic id. They do, because both sides perform the same merge with the same
// tolerances and run the same identity function over the result — but "because
// both sides do the same thing" is a statement about two files that can drift
// apart in one edit, on either side, with no symptom until somebody's junctions
// silently resolve to nothing.
//
// These are that promise. They also pin the other half of it: the ids agree ONLY
// when the tolerances agree, and a reading taken at a different tolerance must
// produce DIFFERENT ids rather than nearly-right ones — because an id that
// resolves to nothing is a skipped junction with a reason, and an id that
// resolves to the wrong element is two pipes joined wrongly in a way no plan view
// shows.
//
// NOT RUN in this phase. Written to be run when running tests is authorised.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class CadRunIdentityTests
    {
        private const string Hash = "sha-of-the-drawing";

        /// <summary>A set whose only rule turns single lines on one layer into pipes.</summary>
        private static CadRequirementSet Set(double pointMm = 1.0, double angleDegrees = 2.0) =>
            CadRequirementSet.Load(JObject.Parse(@"{
              'schema': 'horizun.cad-requirements/1',
              'requirement_set': { 'id': 'mep-demo', 'version': '1.0.0', 'title': 'MEP demo' },
              'source': { 'units': 'millimeter' },
              'tolerances': { 'point_mm': POINT, 'gap_mm': 25.0, 'angle_degrees': ANGLE, 'arc_sagitta_mm': 5.0 },
              'rules': [{ 'id': 'sanitary', 'precedence': 10, 'layers': ['P-SANI*'], 'produces': 'pipe',
                          'category': 'OST_PipeCurves', 'system_type': 'Sanitary',
                          'diameter_mm': 100, 'offset_mm': 2400, 'min_confidence': 0.0,
                          'geometry': { 'from': 'single_lines', 'min_length_mm': 100 } }]
            }".Replace('\'', '"')
               .Replace("POINT", pointMm.ToString("0.####", CultureInfo.InvariantCulture))
               .Replace("ANGLE", angleDegrees.ToString("0.####", CultureInfo.InvariantCulture))));

        private static CadSegment Seg(double x1, double y1, double x2, double y2,
                                      string layer = "P-SANI-MAIN") =>
            new CadSegment(new CadPoint(x1, y1), new CadPoint(x2, y2), layer,
                           CadCurveKind.Line, 0, x1 + "," + y1 + "-" + x2 + "," + y2);

        private static CadNetworkOptions Matching(CadRequirementSet set) => new CadNetworkOptions
        {
            ConnectToleranceMm = set.PointToleranceMm,
            CollinearToleranceDegrees = set.AngleToleranceDegrees,
            IdentityToleranceMm = set.PointToleranceMm,
            GapReviewDistanceMm = 50.0,
            ThroughToleranceDegrees = 15.0
        };

        [Fact]
        public void A_run_and_the_candidate_built_from_it_have_the_same_semantic_id()
        {
            // One straight main, drawn in three pieces, plus a branch - so the
            // merge actually does something and the ids are not trivially equal.
            var segments = new List<CadSegment>
            {
                Seg(0, 0, 1000, 0), Seg(1000, 0, 2000, 0), Seg(2000, 0, 3000, 0),
                Seg(1500, 0, 1500, 900)
            };

            CadRequirementSet set = Set();
            CadNetwork network = CadNetworkRules.Build(segments, Matching(set));
            CadInterpretation interpretation = CadInterpretationRules.Interpret(segments, set, Hash);

            var runIds = network.Runs.Select(r => r.SemanticId)
                                     .OrderBy(x => x, StringComparer.Ordinal).ToList();
            var candidateIds = interpretation.Candidates.Select(c => c.SemanticId)
                                             .OrderBy(x => x, StringComparer.Ordinal).ToList();

            Assert.NotEmpty(runIds);
            Assert.Equal(candidateIds, runIds);
        }

        [Fact]
        public void The_geometry_ids_agree_as_well_as_the_semantic_ones()
        {
            // The pair is what tells "somebody moved this to another layer" from
            // "somebody deleted it and drew a new one", so both halves have to hold.
            var segments = new List<CadSegment> { Seg(0, 0, 4000, 0) };
            CadRequirementSet set = Set();

            CadNetwork network = CadNetworkRules.Build(segments, Matching(set));
            CadInterpretation interpretation = CadInterpretationRules.Interpret(segments, set, Hash);

            Assert.Equal(interpretation.Candidates.Single().GeometryId, network.Runs.Single().GeometryId);
        }

        [Fact]
        public void A_reading_at_a_different_tolerance_produces_a_different_id_not_a_nearly_right_one()
        {
            // THE OTHER HALF OF THE PROMISE. An id that resolves to nothing is a
            // skipped junction with a reason. An id that resolves to the WRONG
            // element is two pipes joined wrongly, invisibly.
            var segments = new List<CadSegment> { Seg(0, 0, 4000, 0) };
            CadRequirementSet set = Set();

            CadNetworkOptions coarse = Matching(set);
            coarse.IdentityToleranceMm = 25.0;

            CadNetwork network = CadNetworkRules.Build(segments, coarse);
            CadInterpretation interpretation = CadInterpretationRules.Interpret(segments, set, Hash);

            Assert.NotEqual(interpretation.Candidates.Single().SemanticId, network.Runs.Single().SemanticId);
        }

        [Fact]
        public void The_identity_tolerance_defaults_to_the_connect_tolerance()
        {
            var options = new CadNetworkOptions { ConnectToleranceMm = 3.0 };
            Assert.Equal(3.0, options.IdentityTolerance, 9);

            options.IdentityToleranceMm = 1.0;
            Assert.Equal(1.0, options.IdentityTolerance, 9);
        }

        [Fact]
        public void Every_run_publishes_the_tolerance_its_id_was_computed_at()
        {
            // Without it, two readings of one drawing produce ids that cannot be
            // compared and nothing says why.
            CadRequirementSet set = Set(pointMm: 2.5);
            CadNetwork network = CadNetworkRules.Build(
                new List<CadSegment> { Seg(0, 0, 4000, 0) }, Matching(set));

            Assert.Equal(2.5, network.Runs.Single().IdentityToleranceMm, 9);
            Assert.Equal(2.5, network.Runs.Single().ToJson().Value<double>("identity_tolerance_mm"), 9);
        }

        [Fact]
        public void A_connection_intent_is_written_in_the_shape_that_carries_it_out()
        {
            // The reading's `connections` array is the connect command's
            // `junctions` array. If this drifts, somebody has to translate between
            // two replies by hand on the one step where a mistake is invisible.
            var segments = new List<CadSegment> { Seg(0, 0, 1000, 0), Seg(1000, 0, 1000, 800) };
            CadRequirementSet set = Set();
            CadNetwork network = CadNetworkRules.Build(segments, Matching(set));

            JObject intent = network.Connections.Single().ToJson();
            Assert.NotNull(intent["id"]);
            Assert.Equal("elbow", intent.Value<string>("fitting"));
            Assert.True(intent.Value<bool>("automatic"));
            Assert.True(intent.Value<bool>("sendable"));

            var at = intent["at"] as JArray;
            Assert.NotNull(at);
            Assert.Equal(3, at.Count);
            Assert.Equal(1000, at[0].Value<double>(), 3);

            var elements = intent["elements"] as JArray;
            Assert.NotNull(elements);
            Assert.Equal(2, elements.Count);
            Assert.All(elements, e =>
                Assert.StartsWith("cadsem:", (e as JObject).Value<string>("semantic_id")));
        }

        [Fact]
        public void A_tee_sends_its_through_pair_first_and_its_branch_last()
        {
            // Revit's own argument order. Sending them as they were enumerated
            // puts the branch in the run, which is a fitting facing the wrong way
            // in a model that otherwise looks right.
            var segments = new List<CadSegment>
            {
                Seg(0, 0, 1000, 0), Seg(1000, 0, 2000, 0), Seg(1000, 0, 1000, 900)
            };
            CadRequirementSet set = Set();
            CadNetwork network = CadNetworkRules.Build(segments, Matching(set));

            CadJunction tee = network.Junctions.Single(j => j.Kind == CadJunctionKind.Tee);
            CadConnectionIntent intent = network.Connections.Single(c => c.JunctionNode == tee.NodeKey);

            Assert.Equal("tee", intent.Fitting);
            Assert.Equal(3, intent.MemberSemanticIds.Count);

            var byId = network.Runs.ToDictionary(r => r.Id, r => r.SemanticId, StringComparer.Ordinal);
            Assert.Equal(byId[tee.ThroughRunIds[0]], intent.MemberSemanticIds[0]);
            Assert.Equal(byId[tee.ThroughRunIds[1]], intent.MemberSemanticIds[1]);
            Assert.Equal(byId[tee.BranchRunId], intent.MemberSemanticIds[2]);
        }

        [Fact]
        public void A_layer_the_requirement_set_claims_is_declared_from_the_set_itself()
        {
            // One artefact says what a layer is. Two would let a run be BUILT at
            // one height and CONNECTED at another with both replies looking right.
            CadRequirementSet set = Set();
            Func<string, CadNetworkRules.CadRunDeclaration> declare =
                CadNetworkRules.DeclarationsFrom(set);

            CadNetworkRules.CadRunDeclaration d = declare("P-SANI-MAIN");
            Assert.NotNull(d);
            Assert.Equal("Sanitary", d.SystemType);
            Assert.Equal(100, d.DiameterMm.Value, 6);
            Assert.Equal(2400, d.ElevationMm.Value, 6);
            Assert.Equal("requirement_set", d.Source);
            Assert.Equal("sanitary", d.RuleId);

            Assert.Null(declare("A-WALL-EXTR"));
        }

        [Fact]
        public void Two_declarations_are_the_same_only_when_every_field_agrees()
        {
            // SameAs decides whether an explicit declaration and the requirement
            // set disagree, and a disagreement refuses the whole call. Every field
            // it compares must be one a caller can actually express, or a layer
            // whose RULE declares a fall would disagree with a declaration that
            // could not mention one.
            var a = new CadNetworkRules.CadRunDeclaration
            {
                SystemType = "Sanitary", DiameterMm = 100, ElevationMm = 2400, SlopePercent = 1.0
            };
            Assert.True(a.SameAs(new CadNetworkRules.CadRunDeclaration
            {
                SystemType = "Sanitary", DiameterMm = 100, ElevationMm = 2400, SlopePercent = 1.0
            }));

            Assert.False(a.SameAs(new CadNetworkRules.CadRunDeclaration
            {
                SystemType = "Storm", DiameterMm = 100, ElevationMm = 2400, SlopePercent = 1.0
            }));
            Assert.False(a.SameAs(new CadNetworkRules.CadRunDeclaration
            {
                SystemType = "Sanitary", DiameterMm = 150, ElevationMm = 2400, SlopePercent = 1.0
            }));
            Assert.False(a.SameAs(new CadNetworkRules.CadRunDeclaration
            {
                SystemType = "Sanitary", DiameterMm = 100, ElevationMm = 400, SlopePercent = 1.0
            }));
            Assert.False(a.SameAs(new CadNetworkRules.CadRunDeclaration
            {
                SystemType = "Sanitary", DiameterMm = 100, ElevationMm = 2400, SlopePercent = 2.0
            }));
            Assert.False(a.SameAs(null));
        }

        [Fact]
        public void A_declared_nothing_and_a_declared_zero_are_not_the_same_declaration()
        {
            // Null is "nobody said" and zero is "somebody said level". Collapsing
            // them would make a fall walk pass through a layer nobody declared.
            var unsaid = new CadNetworkRules.CadRunDeclaration { SystemType = "Sanitary" };
            var level = new CadNetworkRules.CadRunDeclaration { SystemType = "Sanitary", SlopePercent = 0.0 };
            Assert.False(unsaid.SameAs(level));
            Assert.True(unsaid.SameAs(new CadNetworkRules.CadRunDeclaration { SystemType = "Sanitary" }));
        }

        [Fact]
        public void A_rules_declared_slope_reaches_the_run()
        {
            // It was parsed, validated and dropped: a rule declaring 1% produced a
            // horizontal pipe, in the one discipline where the slope IS the design.
            CadRequirementSet set = CadRequirementSet.Load(JObject.Parse(@"{
              'schema': 'horizun.cad-requirements/1',
              'requirement_set': { 'id': 'fall', 'version': '1.0.0', 'title': 'Fall' },
              'source': { 'units': 'millimeter' },
              'tolerances': { 'point_mm': 1.0, 'gap_mm': 25.0, 'angle_degrees': 2.0, 'arc_sagitta_mm': 5.0 },
              'rules': [{ 'id': 'sanitary', 'precedence': 10, 'layers': ['P-SANI*'], 'produces': 'pipe',
                          'category': 'OST_PipeCurves', 'system_type': 'Sanitary', 'diameter_mm': 100,
                          'slope_percent': 1.5, 'min_confidence': 0.0,
                          'geometry': { 'from': 'single_lines' } }]
            }".Replace('\'', '"')));

            CadNetwork net = CadNetworkRules.Build(
                new List<CadSegment> { Seg(0, 0, 4000, 0) }, Matching(set),
                CadNetworkRules.DeclarationsFrom(set));

            Assert.Equal(1.5, net.Runs.Single().SlopePercent.Value, 6);
        }

        [Fact]
        public void Two_rules_claiming_one_layer_at_equal_precedence_declare_nothing_and_say_so()
        {
            // CadRequirementSet.RulesFor returns a tie as a tie on purpose. Taking
            // the first would give the run whichever system sorted first by id, in
            // a reply that looked completely ordinary.
            CadRequirementSet set = CadRequirementSet.Load(JObject.Parse(@"{
              'schema': 'horizun.cad-requirements/1',
              'requirement_set': { 'id': 'tie', 'version': '1.0.0', 'title': 'Tie' },
              'source': { 'units': 'millimeter' },
              'tolerances': { 'point_mm': 1.0, 'gap_mm': 25.0, 'angle_degrees': 2.0, 'arc_sagitta_mm': 5.0 },
              'rules': [
                { 'id': 'a-sanitary', 'precedence': 10, 'layers': ['P-*'], 'produces': 'pipe',
                  'category': 'OST_PipeCurves', 'system_type': 'Sanitary', 'diameter_mm': 100,
                  'min_confidence': 0.0, 'geometry': { 'from': 'single_lines' } },
                { 'id': 'b-domestic', 'precedence': 10, 'layers': ['P-*'], 'produces': 'pipe',
                  'category': 'OST_PipeCurves', 'system_type': 'Domestic Cold Water', 'diameter_mm': 25,
                  'min_confidence': 0.0, 'geometry': { 'from': 'single_lines' } }
              ]
            }".Replace('\'', '"')));

            var ties = new List<JObject>();
            Func<string, CadNetworkRules.CadRunDeclaration> declare =
                CadNetworkRules.DeclarationsFrom(set, ties);

            Assert.Null(declare("P-SANI-MAIN"));
            JObject tie = Assert.Single(ties);
            Assert.Equal("P-SANI-MAIN", tie.Value<string>("layer"));
            Assert.Equal(10, tie.Value<int>("precedence"));
            Assert.Equal(2, (tie["rules"] as JArray).Count);

            // Asking twice records the tie once: a reply with one entry per
            // segment on the layer would bury the finding in its own repetition.
            Assert.Null(declare("P-SANI-MAIN"));
            Assert.Single(ties);
        }
    }
}
