// -----------------------------------------------------------------------------
// Horizun Core tests — original Horizun code.
//
// Interpretation, pinned. These assert the thing that makes the DWG program
// defensible: that a reading which cannot be justified is REFUSED rather than
// built, and that every score ships with the measurement behind it.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class CadInterpretationTests
    {
        private const string Hash = "sha-of-the-drawing";

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

        private static string WallRule(double minConfidence = 0.0, string layers = "[\"A-WALL*\"]") =>
            @"[{ 'id': 'walls', 'precedence': 10, 'layers': LAYERS, 'produces': 'wall',
                 'category': 'OST_Walls', 'family_type': 'Basic Wall: Generic - 200mm',
                 'height_mm': 3000, 'min_confidence': CONF,
                 'geometry': { 'from': 'double_lines', 'min_thickness_mm': 80, 'max_thickness_mm': 500,
                               'min_overlap_mm': 300, 'min_overlap_fraction': 0.5 } }]"
              .Replace('\'', '"').Replace("LAYERS", layers)
              .Replace("CONF", minConfidence.ToString(System.Globalization.CultureInfo.InvariantCulture));

        private static CadSegment Seg(double x1, double y1, double x2, double y2, string layer) =>
            new CadSegment(new CadPoint(x1, y1), new CadPoint(x2, y2), layer);

        [Fact]
        public void Two_parallel_lines_on_a_claimed_layer_become_a_wall_candidate()
        {
            var segs = new List<CadSegment>
            {
                Seg(0, 0, 6000, 0, "A-WALL-EXTR"),
                Seg(0, 200, 6000, 200, "A-WALL-EXTR")
            };
            CadInterpretation r = CadInterpretationRules.Interpret(segs, Set(WallRule()), Hash);
            Assert.Single(r.Candidates);
            CadCandidate c = r.Candidates[0];
            Assert.Equal("wall", c.ProposedKind);
            Assert.Equal(200, c.ThicknessMm.Value, 6);
            Assert.Equal(3000, c.HeightMm.Value, 6);
            Assert.Equal(100, c.Geometry[0].Y, 6);          // the centreline, between the faces
            Assert.True(c.EligibleForAutomaticApply);
        }

        [Fact]
        public void Every_confidence_score_ships_with_the_measurement_behind_it()
        {
            var segs = new List<CadSegment>
            {
                Seg(0, 0, 6000, 0, "A-WALL-EXTR"),
                Seg(0, 200, 6000, 200, "A-WALL-EXTR")
            };
            CadCandidate c = CadInterpretationRules.Interpret(segs, Set(WallRule()), Hash).Candidates[0];
            Assert.NotEmpty(c.ConfidenceFactors);
            foreach (CadConfidenceFactor f in c.ConfidenceFactors)
            {
                Assert.False(string.IsNullOrWhiteSpace(f.Name));
                Assert.False(string.IsNullOrWhiteSpace(f.Observed), $"factor '{f.Name}' must say what it measured");
                Assert.InRange(f.Score, 0, 1);
                Assert.True(f.Weight > 0);
            }
            Assert.InRange(c.Confidence, 0, 1);
        }

        [Fact]
        public void A_layer_nobody_claims_is_reported_rather_than_silently_dropped()
        {
            var segs = new List<CadSegment>
            {
                Seg(0, 0, 6000, 0, "A-WALL-EXTR"), Seg(0, 200, 6000, 200, "A-WALL-EXTR"),
                Seg(0, 5000, 6000, 5000, "Z-NOTES")
            };
            CadInterpretation r = CadInterpretationRules.Interpret(segs, Set(WallRule()), Hash);
            CadUnclaimed unclaimed = Assert.Single(r.Unclaimed, u => u.Layer == "Z-NOTES");
            Assert.Equal("no_rule_matched", unclaimed.Reason);
            Assert.Equal(1, unclaimed.EntityCount);
        }

        [Fact]
        public void The_layer_map_shows_which_rules_claimed_what()
        {
            var segs = new List<CadSegment> { Seg(0, 0, 6000, 0, "A-WALL-EXTR"), Seg(0, 200, 6000, 200, "A-WALL-EXTR") };
            CadInterpretation r = CadInterpretationRules.Interpret(segs, Set(WallRule()), Hash);
            Assert.Contains("A-WALL-EXTR", r.LayerMap.Keys);
            Assert.Contains("walls", r.LayerMap["A-WALL-EXTR"]);
        }

        [Fact]
        public void A_candidate_under_the_rules_confidence_is_produced_but_NOT_eligible()
        {
            // A 400 mm stub pair: real geometry, weak evidence.
            var segs = new List<CadSegment> { Seg(0, 0, 400, 0, "A-WALL-X"), Seg(0, 200, 400, 200, "A-WALL-X") };
            CadInterpretation r = CadInterpretationRules.Interpret(
                segs, Set(WallRule(minConfidence: 0.95)), Hash);
            CadCandidate c = Assert.Single(r.Candidates);
            Assert.False(c.EligibleForAutomaticApply);
            Assert.Contains(c.IneligibleReasons, x => x.Contains("under the"));
        }

        [Fact]
        public void Two_rules_at_the_same_precedence_make_every_candidate_ineligible_and_name_the_rival()
        {
            string rules = @"[
              { 'id': 'walls', 'precedence': 10, 'layers': ['A-WALL*'], 'produces': 'wall',
                'geometry': { 'from': 'double_lines', 'min_thickness_mm': 80, 'max_thickness_mm': 500,
                              'min_overlap_mm': 300, 'min_overlap_fraction': 0.5 } },
              { 'id': 'rails', 'precedence': 10, 'layers': ['A-WALL-RAIL'], 'produces': 'railing',
                'geometry': { 'from': 'single_lines' } }
            ]".Replace('\'', '"');
            var segs = new List<CadSegment> { Seg(0, 0, 6000, 0, "A-WALL-RAIL"), Seg(0, 200, 6000, 200, "A-WALL-RAIL") };
            CadInterpretation r = CadInterpretationRules.Interpret(segs, Set(rules), Hash);
            Assert.NotEmpty(r.Candidates);
            foreach (CadCandidate c in r.Candidates)
            {
                Assert.False(c.EligibleForAutomaticApply);
                Assert.NotEmpty(c.Alternatives);
                Assert.Contains(c.IneligibleReasons, x => x.Contains("more than one rule claims"));
            }
        }

        [Fact]
        public void A_rule_that_says_reject_on_ambiguity_produces_nothing_from_ambiguous_geometry()
        {
            string rules = @"[
              { 'id': 'walls', 'precedence': 10, 'layers': ['A-WALL*'], 'produces': 'wall', 'on_ambiguous': 'reject',
                'geometry': { 'from': 'double_lines', 'min_thickness_mm': 80, 'max_thickness_mm': 500,
                              'min_overlap_mm': 300, 'min_overlap_fraction': 0.5 } },
              { 'id': 'rails', 'precedence': 10, 'layers': ['A-WALL-RAIL'], 'produces': 'railing',
                'geometry': { 'from': 'single_lines' } }
            ]".Replace('\'', '"');
            var segs = new List<CadSegment> { Seg(0, 0, 6000, 0, "A-WALL-RAIL"), Seg(0, 200, 6000, 200, "A-WALL-RAIL") };
            CadInterpretation r = CadInterpretationRules.Interpret(segs, Set(rules), Hash);
            Assert.DoesNotContain(r.Candidates, c => c.ProposedKind == "wall");
        }

        [Fact]
        public void A_closed_ring_becomes_a_floor_with_its_area_measured()
        {
            string rules = @"[{ 'id': 'slabs', 'layers': ['A-SLAB'], 'produces': 'floor',
                'category': 'OST_Floors',
                'geometry': { 'from': 'closed_loops', 'min_area_mm2': 1000000 } }]".Replace('\'', '"');
            var segs = new List<CadSegment>
            {
                Seg(0, 0, 4000, 0, "A-SLAB"), Seg(4000, 0, 4000, 3000, "A-SLAB"),
                Seg(4000, 3000, 0, 3000, "A-SLAB"), Seg(0, 3000, 0, 0, "A-SLAB")
            };
            CadInterpretation r = CadInterpretationRules.Interpret(segs, Set(rules), Hash);
            CadCandidate c = Assert.Single(r.Candidates);
            Assert.Equal("floor", c.ProposedKind);
            Assert.Equal(12_000_000, c.AreaMm2.Value, 3);
            Assert.True(c.EligibleForAutomaticApply);
        }

        [Fact]
        public void A_ring_that_had_to_be_snapped_shut_says_so_as_an_assumption()
        {
            string rules = @"[{ 'id': 'slabs', 'layers': ['A-SLAB'], 'produces': 'floor',
                'geometry': { 'from': 'closed_loops' } }]".Replace('\'', '"');
            var segs = new List<CadSegment>
            {
                Seg(0, 0, 4000, 0, "A-SLAB"), Seg(4000, 0, 4000, 3000, "A-SLAB"),
                Seg(4000, 3000, 0, 3000, "A-SLAB"), Seg(0, 3000, 0, 6, "A-SLAB")
            };
            CadCandidate c = Assert.Single(CadInterpretationRules.Interpret(segs, Set(rules), Hash).Candidates);
            Assert.Contains(c.Assumptions, a => a.Contains("snapped shut"));
            Assert.True(c.Confidence < 1.0, "a snapped ring cannot score the same as one that was drawn closed");
        }

        [Fact]
        public void An_outline_that_will_not_close_is_never_eligible_and_says_why()
        {
            string rules = @"[{ 'id': 'slabs', 'layers': ['A-SLAB'], 'produces': 'floor',
                'geometry': { 'from': 'closed_loops' } }]".Replace('\'', '"');
            var segs = new List<CadSegment>
            {
                Seg(0, 0, 4000, 0, "A-SLAB"), Seg(4000, 0, 4000, 3000, "A-SLAB"),
                Seg(4000, 3000, 0, 3000, "A-SLAB"), Seg(0, 3000, 0, 900, "A-SLAB")   // a doorway, not a gap
            };
            CadInterpretation r = CadInterpretationRules.Interpret(segs, Set(rules), Hash);
            CadCandidate c = Assert.Single(r.Candidates);
            Assert.False(c.EligibleForAutomaticApply);
            Assert.Contains(c.UnresolvedFacts, u => u.Contains("does not close"));
            Assert.Contains(c.IneligibleReasons, x => x.Contains("open"));
        }

        [Fact]
        public void A_single_line_rule_makes_grids_and_names_what_the_drawing_does_not_carry()
        {
            string rules = @"[{ 'id': 'routes', 'layers': ['M-PIPE'], 'produces': 'pipe',
                'category': 'OST_PipeCurves',
                'geometry': { 'from': 'single_lines', 'min_length_mm': 500 } }]".Replace('\'', '"');
            var segs = new List<CadSegment> { Seg(0, 0, 8000, 0, "M-PIPE") };
            CadCandidate c = Assert.Single(CadInterpretationRules.Interpret(segs, Set(rules), Hash).Candidates);
            Assert.Equal("pipe", c.ProposedKind);
            Assert.Contains(c.UnresolvedFacts, u => u.StartsWith("size:"));
            Assert.Contains(c.UnresolvedFacts, u => u.StartsWith("slope:"));
        }

        [Fact]
        public void Every_candidate_states_what_must_be_re_read_to_call_it_built()
        {
            var segs = new List<CadSegment> { Seg(0, 0, 6000, 0, "A-WALL-EXTR"), Seg(0, 200, 6000, 200, "A-WALL-EXTR") };
            CadCandidate c = CadInterpretationRules.Interpret(segs, Set(WallRule()), Hash).Candidates[0];
            Assert.NotEmpty(c.ExpectedVerification);
            Assert.Contains(c.ExpectedVerification, v => v.Contains("category"));
        }

        [Fact]
        public void An_exact_layer_name_scores_higher_than_a_catch_all()
        {
            var exact = CadRequirementSet.Load(JObject.Parse(@"{
              'schema': 'horizun.cad-requirements/1',
              'requirement_set': { 'id': 'a', 'version': '1', 'title': 't' },
              'source': { 'units': 'millimeter' },
              'tolerances': { 'point_mm': 1, 'gap_mm': 25, 'angle_degrees': 2, 'arc_sagitta_mm': 5 },
              'rules': [{ 'id': 'r', 'layers': ['A-WALL-EXTR'], 'produces': 'wall',
                          'geometry': { 'from': 'double_lines', 'min_thickness_mm': 80, 'max_thickness_mm': 500 } }]
            }".Replace('\'', '"')));
            var catchAll = CadRequirementSet.Load(JObject.Parse(@"{
              'schema': 'horizun.cad-requirements/1',
              'requirement_set': { 'id': 'a', 'version': '1', 'title': 't' },
              'source': { 'units': 'millimeter' },
              'tolerances': { 'point_mm': 1, 'gap_mm': 25, 'angle_degrees': 2, 'arc_sagitta_mm': 5 },
              'rules': [{ 'id': 'r', 'layers': ['*'], 'produces': 'wall',
                          'geometry': { 'from': 'double_lines', 'min_thickness_mm': 80, 'max_thickness_mm': 500 } }]
            }".Replace('\'', '"')));
            Assert.True(CadInterpretationRules.LayerSpecificity(exact.Rules[0], "A-WALL-EXTR") >
                        CadInterpretationRules.LayerSpecificity(catchAll.Rules[0], "A-WALL-EXTR"));
        }

        [Fact]
        public void A_COMPOUND_wall_exports_as_several_parallel_lines_and_the_widest_pair_wins()
        {
            // MEASURED against a DWG this repository exported from Revit 2026: a
            // 352 mm compound wall came back as FOUR parallel lines - two outer
            // faces at +/-176.2 and two core boundaries at -11.1 and +7.9 - and
            // those four form several thickness-valid pairs. The first version
            // silently kept one and dropped the rest.
            var segs = new List<CadSegment>
            {
                Seg(900000, -176.2, 908176, -176.2, "A-WALL-MCUT"),
                Seg(900000, -11.1, 908011, -11.1, "A-WALL-MCUT"),
                Seg(900000, 7.9, 907992, 7.9, "A-WALL-MCUT"),
                Seg(900000, 176.2, 908176, 176.2, "A-WALL-MCUT")
            };
            string rules = @"[{ 'id': 'walls', 'layers': ['A-WALL*'], 'produces': 'wall',
                'geometry': { 'from': 'double_lines', 'min_thickness_mm': 100, 'max_thickness_mm': 400,
                              'min_overlap_mm': 1000, 'min_overlap_fraction': 0.6 } }]".Replace('\'', '"');
            CadInterpretation r = CadInterpretationRules.Interpret(segs, Set(rules), Hash);

            CadCandidate widest = r.Candidates.OrderByDescending(c => c.ThicknessMm ?? 0).First();
            Assert.Equal(352.4, widest.ThicknessMm.Value, 1);
            Assert.Contains(widest.Assumptions, a => a.Contains("also paired at"));
            Assert.NotEmpty(widest.Alternatives);
            Assert.Contains(widest.Assumptions, a => a.Contains("material layers"));
        }

        [Fact]
        public void The_six_lines_of_a_real_compound_wall_produce_ONE_wall()
        {
            // MEASURED off the live fixture, 2026-08-26. The wall type in that
            // model has five material layers, so it exported SIX parallel lines,
            // and six lines admit ten thickness-valid pairings. Refusing to reuse
            // a face is not enough: the outer faces take one pairing and the two
            // next boundaries in were still free to pair with each other, so the
            // plan proposed every wall in the drawing TWICE - once at 352.4 mm
            // and once at 247.7 mm, one inside the other. The live chain caught
            // it as "3 walls drawn, 6 walls planned".
            var segs = new List<CadSegment>
            {
                Seg(900000, -176.213, 908176, -176.213, "A-WALL-MCUT"),
                Seg(900000, -163.513, 908163, -163.513, "A-WALL-MCUT"),
                Seg(900000, -11.112, 908011, -11.112, "A-WALL-MCUT"),
                Seg(900000, 7.938, 907992, 7.938, "A-WALL-MCUT"),
                Seg(900000, 84.138, 907916, 84.138, "A-WALL-MCUT"),
                Seg(900000, 176.213, 908176, 176.213, "A-WALL-MCUT")
            };
            string rules = @"[{ 'id': 'walls', 'layers': ['A-WALL*'], 'produces': 'wall',
                'geometry': { 'from': 'double_lines', 'min_thickness_mm': 100, 'max_thickness_mm': 400,
                              'min_overlap_mm': 1000, 'min_overlap_fraction': 0.6 } }]".Replace('\'', '"');
            CadInterpretation r = CadInterpretationRules.Interpret(segs, Set(rules), Hash);

            CadCandidate only = Assert.Single(r.Candidates);
            Assert.Equal(352.4, only.ThicknessMm.Value, 1);
            // The absorbed readings are REPORTED, not silently dropped.
            Assert.Contains(only.Assumptions, a => a.Contains("4 further lines on this layer lie INSIDE"));
            Assert.Contains(only.Alternatives, a => a.Contains("separate wall"));
            // And ALL SIX lines count as explained: they ARE the wall. Two of
            // them pair at 19 mm and so pair with nothing - reporting those as
            // unaccounted-for geometry would send a reviewer hunting for a wall
            // that is already there.
            Assert.Equal(1.0, r.CoverageFraction, 3);
        }

        [Fact]
        public void Two_walls_face_to_face_are_two_walls_not_one_inside_the_other()
        {
            // The containment rule must not swallow a real neighbour. Two 200 mm
            // walls sharing a face line are four lines in a row - and the second
            // wall's band is NOT inside the first's.
            var segs = new List<CadSegment>
            {
                Seg(0, 0, 6000, 0, "A-WALL-X"),
                Seg(0, 200, 6000, 200, "A-WALL-X"),
                Seg(0, 400, 6000, 400, "A-WALL-X"),
                Seg(0, 600, 6000, 600, "A-WALL-X")
            };
            string rules = @"[{ 'id': 'walls', 'layers': ['A-WALL*'], 'produces': 'wall',
                'geometry': { 'from': 'double_lines', 'min_thickness_mm': 150, 'max_thickness_mm': 250,
                              'min_overlap_mm': 1000, 'min_overlap_fraction': 0.6 } }]".Replace('\'', '"');
            CadInterpretation r = CadInterpretationRules.Interpret(segs, Set(rules), Hash);
            Assert.Equal(2, r.Candidates.Count);
            Assert.All(r.Candidates, c => Assert.Equal(200.0, c.ThicknessMm.Value, 1));
        }

        [Fact]
        public void A_narrower_pairing_alongside_but_OUTSIDE_the_band_stays_its_own_wall()
        {
            var outerA = new CadDoubleLine(new CadPoint(0, 0), new CadPoint(6000, 0), 352.4, "L", 6000, 1, 0, 0, 1);
            var insideIt = new CadDoubleLine(new CadPoint(0, -39.7), new CadPoint(6000, -39.7), 247.7, "L", 6000, 1, 0, 2, 3);
            var besideIt = new CadDoubleLine(new CadPoint(0, 900), new CadPoint(6000, 900), 247.7, "L", 6000, 1, 0, 4, 5);
            var crossingElsewhere = new CadDoubleLine(new CadPoint(20000, 0), new CadPoint(26000, 0), 100, "L", 6000, 1, 0, 6, 7);

            Assert.True(CadTopologyRules.IsInnerBoundaryOf(insideIt, outerA, 2, 1));
            Assert.False(CadTopologyRules.IsInnerBoundaryOf(besideIt, outerA, 2, 1));
            Assert.False(CadTopologyRules.IsInnerBoundaryOf(crossingElsewhere, outerA, 2, 1));
            // and never the other way round: the wider one is not inside the narrower
            Assert.False(CadTopologyRules.IsInnerBoundaryOf(outerA, insideIt, 2, 1));
        }

        [Fact]
        public void Thickness_bounds_tight_enough_leave_no_choice_to_report()
        {
            // The better fix, and the one the assumption message points at: a
            // requirement set whose bounds admit only the outer faces never
            // reaches the tie-break at all.
            var segs = new List<CadSegment>
            {
                Seg(900000, -176.2, 908176, -176.2, "A-WALL-MCUT"),
                Seg(900000, -11.1, 908011, -11.1, "A-WALL-MCUT"),
                Seg(900000, 7.9, 907992, 7.9, "A-WALL-MCUT"),
                Seg(900000, 176.2, 908176, 176.2, "A-WALL-MCUT")
            };
            string rules = @"[{ 'id': 'walls', 'layers': ['A-WALL*'], 'produces': 'wall',
                'geometry': { 'from': 'double_lines', 'min_thickness_mm': 300, 'max_thickness_mm': 400,
                              'min_overlap_mm': 1000, 'min_overlap_fraction': 0.6 } }]".Replace('\'', '"');
            CadInterpretation r = CadInterpretationRules.Interpret(segs, Set(rules), Hash);
            CadCandidate only = Assert.Single(r.Candidates);
            Assert.Equal(352.4, only.ThicknessMm.Value, 1);
            Assert.DoesNotContain(only.Assumptions, a => a.Contains("also paired at"));
            Assert.Empty(only.Alternatives);
        }

        [Fact]
        public void One_face_cannot_belong_to_two_walls()
        {
            // Three parallel lines 200 apart: the middle one could pair either
            // way, and both readings cannot be built.
            var segs = new List<CadSegment>
            {
                Seg(0, 0, 6000, 0, "A-WALL-X"),
                Seg(0, 200, 6000, 200, "A-WALL-X"),
                Seg(0, 400, 6000, 400, "A-WALL-X")
            };
            CadInterpretation r = CadInterpretationRules.Interpret(segs, Set(WallRule()), Hash);
            Assert.Single(r.Candidates);
        }

        [Fact]
        public void Coverage_says_how_much_of_the_drawing_the_reading_accounts_for()
        {
            var segs = new List<CadSegment>
            {
                Seg(0, 0, 6000, 0, "A-WALL-EXTR"), Seg(0, 200, 6000, 200, "A-WALL-EXTR"),
                Seg(0, 9000, 6000, 9000, "Z-NOTES"), Seg(0, 9200, 6000, 9200, "Z-NOTES")
            };
            CadInterpretation r = CadInterpretationRules.Interpret(segs, Set(WallRule()), Hash);
            Assert.Equal(4, r.SegmentsConsidered);
            Assert.Equal(2, r.SegmentsConsumed);
            Assert.Equal(0.5, r.CoverageFraction, 6);
        }

        [Fact]
        public void An_empty_drawing_interprets_to_nothing_without_throwing()
        {
            CadInterpretation r = CadInterpretationRules.Interpret(new List<CadSegment>(), Set(WallRule()), Hash);
            Assert.Empty(r.Candidates);
            Assert.Equal(0, r.CoverageFraction);
        }

        [Fact]
        public void Candidate_ids_are_stable_across_runs_and_across_drawing_direction()
        {
            var forward = new List<CadSegment> { Seg(0, 0, 6000, 0, "A-WALL-EXTR"), Seg(0, 200, 6000, 200, "A-WALL-EXTR") };
            var backward = new List<CadSegment> { Seg(6000, 0, 0, 0, "A-WALL-EXTR"), Seg(6000, 200, 0, 200, "A-WALL-EXTR") };
            string a = CadInterpretationRules.Interpret(forward, Set(WallRule()), Hash).Candidates[0].Id;
            string b = CadInterpretationRules.Interpret(backward, Set(WallRule()), Hash).Candidates[0].Id;
            Assert.Equal(a, b);
        }

        [Fact]
        public void A_different_drawing_gives_different_candidate_ids()
        {
            var segs = new List<CadSegment> { Seg(0, 0, 6000, 0, "A-WALL-EXTR"), Seg(0, 200, 6000, 200, "A-WALL-EXTR") };
            string a = CadInterpretationRules.Interpret(segs, Set(WallRule()), "rev-a").Candidates[0].Id;
            string b = CadInterpretationRules.Interpret(segs, Set(WallRule()), "rev-b").Candidates[0].Id;
            Assert.NotEqual(a, b);
        }
    }
}
