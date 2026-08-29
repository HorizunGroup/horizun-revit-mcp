// -----------------------------------------------------------------------------
// Horizun Core tests — original Horizun code.
//
// The plan, pinned. What gets built, in what order, what is deliberately left
// out, and what the plan is bound to - all provable before a transaction opens.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class CadConversionPlanTests
    {
        private const string Hash = "drawing-sha";
        private const string SourceFp = "cadsrc:abc123";

        private static CadRequirementSet Set(string rulesJson)
        {
            string doc = @"{
              'schema': 'horizun.cad-requirements/1',
              'requirement_set': { 'id': 'demo', 'version': '1.0.0', 'title': 'Demo' },
              'source': { 'units': 'millimeter' },
              'tolerances': { 'point_mm': 1.0, 'gap_mm': 25.0, 'angle_degrees': 2.0, 'arc_sagitta_mm': 5.0 },
              'rules': RULES
            }".Replace('\'', '"').Replace("RULES", rulesJson);
            return CadRequirementSet.Load(JObject.Parse(doc));
        }

        private static CadSegment Seg(double x1, double y1, double x2, double y2, string layer) =>
            new CadSegment(new CadPoint(x1, y1), new CadPoint(x2, y2), layer);

        private static readonly string WallsAndSlabs = @"[
          { 'id': 'walls', 'precedence': 10, 'layers': ['A-WALL*'], 'produces': 'wall', 'discipline': 'architecture',
            'category': 'OST_Walls', 'family_type': 'Basic Wall: Generic - 200mm', 'level': 'Level 1', 'height_mm': 3000,
            'geometry': { 'from': 'double_lines', 'min_thickness_mm': 80, 'max_thickness_mm': 500,
                          'min_overlap_mm': 300, 'min_overlap_fraction': 0.5 } },
          { 'id': 'slabs', 'precedence': 10, 'layers': ['A-SLAB'], 'produces': 'floor', 'discipline': 'architecture',
            'category': 'OST_Floors', 'family_type': 'Floor: Generic 150mm', 'level': 'Level 1',
            'geometry': { 'from': 'closed_loops' } },
          { 'id': 'grids', 'precedence': 10, 'layers': ['S-GRID'], 'produces': 'grid', 'discipline': 'structure',
            'geometry': { 'from': 'single_lines', 'min_length_mm': 1000 } }
        ]".Replace('\'', '"');

        private static List<CadSegment> Drawing() => new List<CadSegment>
        {
            // a wall
            Seg(0, 0, 6000, 0, "A-WALL-EXTR"), Seg(0, 200, 6000, 200, "A-WALL-EXTR"),
            // a slab
            Seg(0, 3000, 4000, 3000, "A-SLAB"), Seg(4000, 3000, 4000, 6000, "A-SLAB"),
            Seg(4000, 6000, 0, 6000, "A-SLAB"), Seg(0, 6000, 0, 3000, "A-SLAB"),
            // a grid line
            Seg(-2000, -2000, 12000, -2000, "S-GRID")
        };

        private static CadConversionPlan Build(bool includeIneligible = false)
        {
            CadRequirementSet set = Set(WallsAndSlabs);
            CadInterpretation interp = CadInterpretationRules.Interpret(Drawing(), set, Hash);
            return CadConversionPlanRules.Plan(interp, set, SourceFp, includeIneligible);
        }

        [Fact]
        public void A_drawing_becomes_typed_actions_with_arguments_ready_to_send()
        {
            CadConversionPlan plan = Build();
            Assert.NotEmpty(plan.Actions);
            CadPlannedAction wall = Assert.Single(plan.Actions, a => a.Kind == "wall");
            Assert.NotNull(wall.Arguments["start"]);
            Assert.NotNull(wall.Arguments["end"]);
            Assert.Equal(3000.0, (double)wall.Arguments["height"]);
            Assert.Equal("Basic Wall: Generic - 200mm", (string)wall.Arguments["type_name"]);
            Assert.Equal("Level 1", (string)wall.Arguments["level_name"]);
        }

        [Fact]
        public void Grids_come_before_hosts_and_hosts_before_slabs()
        {
            CadConversionPlan plan = Build();
            int grid = plan.Actions.First(a => a.Kind == "grid").Stage;
            int wall = plan.Actions.First(a => a.Kind == "wall").Stage;
            int floor = plan.Actions.First(a => a.Kind == "floor").Stage;
            Assert.True(grid < wall, "a grid is the frame everything else is measured from");
            Assert.True(wall < floor, "a slab is bounded by walls that must already exist");
        }

        [Fact]
        public void Actions_come_out_sorted_by_stage_and_the_order_is_deterministic()
        {
            CadConversionPlan a = Build();
            CadConversionPlan b = Build();
            Assert.Equal(a.Actions.Select(x => x.CandidateId), b.Actions.Select(x => x.CandidateId));
            for (int i = 1; i < a.Actions.Count; i++)
                Assert.True(a.Actions[i - 1].Stage <= a.Actions[i].Stage);
        }

        [Fact]
        public void A_candidate_needing_review_is_deferred_with_its_reason_carried_through()
        {
            // min_confidence 0.99 puts the wall under review without changing the drawing.
            string rules = WallsAndSlabs.Replace("'id': 'walls', 'precedence': 10".Replace('\'', '"'),
                                                 "\"id\": \"walls\", \"min_confidence\": 0.99, \"precedence\": 10");
            CadRequirementSet set = Set(rules);
            CadInterpretation interp = CadInterpretationRules.Interpret(Drawing(), set, Hash);
            CadConversionPlan plan = CadConversionPlanRules.Plan(interp, set, SourceFp);

            Assert.DoesNotContain(plan.Actions, a => a.Kind == "wall");
            CadDeferred d = Assert.Single(plan.Deferred, x => x.ProposedKind == "wall");
            Assert.NotEmpty(d.Reasons);
            Assert.Contains(d.Reasons, r => r.Contains("under the"));
        }

        [Fact]
        public void Nothing_ineligible_is_planned_unless_review_is_explicitly_bypassed()
        {
            string rules = WallsAndSlabs.Replace("\"id\": \"walls\", \"precedence\": 10",
                                                 "\"id\": \"walls\", \"min_confidence\": 0.99, \"precedence\": 10");
            CadRequirementSet set = Set(rules);
            CadInterpretation interp = CadInterpretationRules.Interpret(Drawing(), set, Hash);

            Assert.DoesNotContain(CadConversionPlanRules.Plan(interp, set, SourceFp, includeIneligible: false).Actions,
                                  a => a.Kind == "wall");
            Assert.Contains(CadConversionPlanRules.Plan(interp, set, SourceFp, includeIneligible: true).Actions,
                            a => a.Kind == "wall");
        }

        [Fact]
        public void Bypassing_review_changes_the_plan_fingerprint()
        {
            string rules = WallsAndSlabs.Replace("\"id\": \"walls\", \"precedence\": 10",
                                                 "\"id\": \"walls\", \"min_confidence\": 0.99, \"precedence\": 10");
            CadRequirementSet set = Set(rules);
            CadInterpretation interp = CadInterpretationRules.Interpret(Drawing(), set, Hash);
            Assert.NotEqual(
                CadConversionPlanRules.Plan(interp, set, SourceFp, false).PlanFingerprint,
                CadConversionPlanRules.Plan(interp, set, SourceFp, true).PlanFingerprint);
        }

        [Fact]
        public void A_kind_this_bridge_cannot_build_is_deferred_by_name_not_approximated()
        {
            string rules = @"[{ 'id': 'stairs', 'layers': ['A-STRS'], 'produces': 'stair',
                'geometry': { 'from': 'closed_loops' } }]".Replace('\'', '"');
            var segs = new List<CadSegment>
            {
                Seg(0, 0, 2000, 0, "A-STRS"), Seg(2000, 0, 2000, 4000, "A-STRS"),
                Seg(2000, 4000, 0, 4000, "A-STRS"), Seg(0, 4000, 0, 0, "A-STRS")
            };
            CadRequirementSet set = Set(rules);
            CadInterpretation interp = CadInterpretationRules.Interpret(segs, set, Hash);
            CadConversionPlan plan = CadConversionPlanRules.Plan(interp, set, SourceFp);
            Assert.Empty(plan.Actions);
            Assert.Contains(plan.Deferred, d => d.Reasons.Any(r => r.Contains("no typed way to build")));
        }

        [Fact]
        public void The_plan_fingerprint_moves_when_the_drawing_moves()
        {
            CadRequirementSet set = Set(WallsAndSlabs);
            string a = CadConversionPlanRules.Plan(
                CadInterpretationRules.Interpret(Drawing(), set, Hash), set, SourceFp).PlanFingerprint;

            List<CadSegment> moved = Drawing();
            moved[0] = Seg(0, 0, 6500, 0, "A-WALL-EXTR");
            moved[1] = Seg(0, 200, 6500, 200, "A-WALL-EXTR");
            string b = CadConversionPlanRules.Plan(
                CadInterpretationRules.Interpret(moved, set, Hash), set, SourceFp).PlanFingerprint;
            Assert.NotEqual(a, b);
        }

        [Fact]
        public void The_plan_fingerprint_moves_when_the_RULES_move()
        {
            CadRequirementSet a = Set(WallsAndSlabs);
            CadRequirementSet b = Set(WallsAndSlabs.Replace("3000", "2700"));
            Assert.NotEqual(
                CadConversionPlanRules.Plan(CadInterpretationRules.Interpret(Drawing(), a, Hash), a, SourceFp).PlanFingerprint,
                CadConversionPlanRules.Plan(CadInterpretationRules.Interpret(Drawing(), b, Hash), b, SourceFp).PlanFingerprint);
        }

        [Fact]
        public void The_plan_fingerprint_moves_when_the_SOURCE_moves()
        {
            CadRequirementSet set = Set(WallsAndSlabs);
            CadInterpretation interp = CadInterpretationRules.Interpret(Drawing(), set, Hash);
            Assert.NotEqual(
                CadConversionPlanRules.Plan(interp, set, "cadsrc:one").PlanFingerprint,
                CadConversionPlanRules.Plan(interp, set, "cadsrc:two").PlanFingerprint);
        }

        [Fact]
        public void The_same_inputs_give_the_same_fingerprint()
        {
            Assert.Equal(Build().PlanFingerprint, Build().PlanFingerprint);
        }

        [Fact]
        public void A_reading_that_covers_little_of_the_drawing_warns_before_it_is_approved()
        {
            string rules = @"[{ 'id': 'grids', 'layers': ['S-GRID'], 'produces': 'grid',
                'geometry': { 'from': 'single_lines' } }]".Replace('\'', '"');
            CadRequirementSet set = Set(rules);
            CadInterpretation interp = CadInterpretationRules.Interpret(Drawing(), set, Hash);
            CadConversionPlan plan = CadConversionPlanRules.Plan(interp, set, SourceFp);
            Assert.Contains(plan.Warnings, w => w.Contains("% of the drawn segments"));
            Assert.Contains(plan.Warnings, w => w.Contains("no rule claims"));
        }

        [Fact]
        public void An_empty_plan_says_so_rather_than_looking_like_success()
        {
            string rules = @"[{ 'id': 'nothing', 'layers': ['X-NOPE'], 'produces': 'wall',
                'geometry': { 'from': 'double_lines', 'min_thickness_mm': 80, 'max_thickness_mm': 500 } }]".Replace('\'', '"');
            CadRequirementSet set = Set(rules);
            CadConversionPlan plan = CadConversionPlanRules.Plan(
                CadInterpretationRules.Interpret(Drawing(), set, Hash), set, SourceFp);
            Assert.Empty(plan.Actions);
            Assert.Contains(plan.Warnings, w => w.Contains("nothing is planned"));
        }

        [Fact]
        public void The_plan_becomes_one_create_request_per_stage_in_order()
        {
            List<JObject> requests = CadConversionPlanRules.AsCreateRequests(Build(), "HZ_TARGET");
            Assert.NotEmpty(requests);
            var stages = requests.Select(r => (int)r["stage"]).ToList();
            Assert.Equal(stages.OrderBy(x => x).ToList(), stages);
            foreach (JObject r in requests)
            {
                Assert.Equal("HZ_TARGET", (string)r["target_document"]);
                Assert.Equal("mm", (string)r["units"]);
                Assert.NotEmpty((JArray)r["elements"]);
            }
        }

        [Fact]
        public void A_big_stage_is_split_into_bounded_batches()
        {
            CadConversionPlan plan = Build();
            List<JObject> requests = CadConversionPlanRules.AsCreateRequests(plan, "HZ_TARGET", maxPerBatch: 1);
            Assert.Equal(plan.Actions.Count, requests.Sum(r => ((JArray)r["elements"]).Count));
            Assert.All(requests, r => Assert.True(((JArray)r["elements"]).Count <= 1));
        }

        [Fact]
        public void The_json_report_carries_the_requirement_set_stamp_and_the_coverage()
        {
            CadRequirementSet set = Set(WallsAndSlabs);
            CadConversionPlan plan = CadConversionPlanRules.Plan(
                CadInterpretationRules.Interpret(Drawing(), set, Hash), set, SourceFp);
            JObject json = CadConversionPlanRules.ToJson(plan, set);
            Assert.Equal(set.Sha256, (string)json["requirement_set"]["sha256"]);
            Assert.Equal(plan.PlanFingerprint, (string)json["plan_fingerprint"]);
            Assert.NotNull(json["coverage"]["fraction"]);
            Assert.NotNull(json["counts_by_kind"]);
            Assert.NotNull(json["counts_by_discipline"]);
        }

        [Fact]
        public void Every_planned_action_carries_what_must_be_re_read_to_call_it_built()
        {
            foreach (CadPlannedAction a in Build().Actions)
                Assert.NotEmpty(a.ExpectedVerification);
        }
    }
}
