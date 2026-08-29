// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// NAMES A DRAWING CANNOT SUPPLY.
//
// MEASURED on Revit 2026: no string is reachable from imported DWG geometry at
// any depth. Text arrives as curves on its own layer - the layer name survives
// and the words do not - so a grid bubble reading "A" is, to this bridge, a few
// arcs. Names therefore come from the requirement set.
//
// Most of what follows is REFUSALS, and that is the design. The tempting
// fallback - "the first line is grid 1" - orders by whatever the reading
// happened to return first, which is not stable between runs let alone between
// machines, and a grid named that way puts the wrong reference on every
// dimension drawn from it without anything in the model saying so.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class CadNamingTests
    {
        private static CadNaming Naming(string json)
        {
            string doc = @"{
              'schema': 'horizun.cad-requirements/1',
              'requirement_set': { 'id': 'naming', 'version': '1.0.0', 'title': 'Naming' },
              'source': { 'units': 'millimeter' },
              'tolerances': { 'point_mm': 1.0, 'gap_mm': 25.0, 'angle_degrees': 2.0, 'arc_sagitta_mm': 5.0 },
              'rules': [{ 'id': 'grids', 'precedence': 10, 'layers': ['S-GRID*'], 'produces': 'grid',
                          'category': 'OST_Grids', 'naming': NAMING,
                          'geometry': { 'from': 'single_lines', 'min_length_mm': 1000 } }]
            }".Replace('\'', '"').Replace("NAMING", json.Replace('\'', '"'));
            return CadRequirementSet.Load(JObject.Parse(doc)).Rules[0].Naming;
        }

        private static CadRequirementSetException Refused(string json)
        {
            return Assert.Throws<CadRequirementSetException>(() => Naming(json));
        }

        /// <summary>A candidate at a known place, with a semantic id of its own.</summary>
        private static CadCandidate At(string id, double x, double y = 0)
        {
            return new CadCandidate
            {
                SemanticId = id,
                ProposedKind = "grid",
                Geometry = new List<CadPoint> { new CadPoint(x, y), new CadPoint(x, y + 8000) }
            };
        }

        // ------------------------------------------------------------- ordered

        [Fact]
        public void An_ordered_naming_gives_each_grid_the_name_its_POSITION_earns()
        {
            CadNaming n = Naming("{ 'strategy': 'ordered', 'axis': 'x', 'direction': 'ascending', " +
                                 "'values': ['1', '2', '3'] }");
            var candidates = new List<CadCandidate> { At("c", 9000), At("a", 1000), At("b", 5000) };

            CadNamingOutcome r = CadNamingRules.Assign(n, candidates, 1.0);

            Assert.False(r.Refused);
            Assert.Equal("1", r.Names["a"]);
            Assert.Equal("2", r.Names["b"]);
            Assert.Equal("3", r.Names["c"]);
            Assert.Equal(new[] { "a", "b", "c" }, r.CanonicalOrder);
        }

        [Fact]
        public void Descending_is_a_different_answer_and_the_set_has_to_say_which()
        {
            CadNaming n = Naming("{ 'strategy': 'ordered', 'axis': 'x', 'direction': 'descending', " +
                                 "'values': ['1', '2', '3'] }");
            var candidates = new List<CadCandidate> { At("a", 1000), At("b", 5000), At("c", 9000) };

            CadNamingOutcome r = CadNamingRules.Assign(n, candidates, 1.0);
            Assert.Equal("1", r.Names["c"]);
            Assert.Equal("3", r.Names["a"]);
        }

        [Fact]
        public void The_evidence_says_what_each_name_was_earned_ON()
        {
            // A reviewer must be able to check the assignment without re-deriving
            // it. "position 2 of 3 along x ascending, at 5000 mm" is checkable.
            CadNaming n = Naming("{ 'strategy': 'ordered', 'axis': 'x', 'values': ['1', '2'] }");
            CadNamingOutcome r = CadNamingRules.Assign(
                n, new List<CadCandidate> { At("a", 1000), At("b", 5000) }, 1.0);

            Assert.Contains("position 1 of 2 along x ascending", r.Evidence["a"]);
            Assert.Contains("5000", r.Evidence["b"]);
        }

        [Fact]
        public void More_grids_than_names_names_NOTHING_rather_than_shifting_every_name_after_the_gap()
        {
            // Naming three of four grids "1 2 3" leaves the fourth unnamed AND
            // means grid 4 might really be grid 3. Partial is the dangerous case.
            CadNaming n = Naming("{ 'strategy': 'ordered', 'axis': 'x', 'values': ['1', '2', '3'] }");
            var candidates = new List<CadCandidate> { At("a", 1000), At("b", 5000), At("c", 9000), At("d", 13000) };

            CadNamingOutcome r = CadNamingRules.Assign(n, candidates, 1.0);
            Assert.True(r.Refused);
            Assert.Contains(r.Problems, x => x.Contains("4 candidate") && x.Contains("3 name"));
        }

        [Fact]
        public void More_names_than_grids_is_refused_too_and_says_which_are_spare()
        {
            CadNaming n = Naming("{ 'strategy': 'ordered', 'axis': 'x', 'values': ['1', '2', '3', '4'] }");
            var candidates = new List<CadCandidate> { At("a", 1000), At("b", 5000) };

            CadNamingOutcome r = CadNamingRules.Assign(n, candidates, 1.0);
            Assert.True(r.Refused);
            Assert.Contains("3", r.Unmatched);
            Assert.Contains("4", r.Unmatched);
        }

        [Fact]
        public void TWO_GRIDS_AT_ONE_COORDINATE_have_no_first_one_and_the_naming_says_so()
        {
            // The failure an enumeration-order fallback hides completely: there
            // IS no first, so whichever gets "1" is whichever the reading
            // returned first - and that changes between runs.
            CadNaming n = Naming("{ 'strategy': 'ordered', 'axis': 'x', 'values': ['1', '2'], " +
                                 "'order_tolerance_mm': 50 }");
            var candidates = new List<CadCandidate> { At("a", 5000), At("b", 5010) };

            CadNamingOutcome r = CadNamingRules.Assign(n, candidates, 1.0);
            Assert.True(r.Refused);
            Assert.Contains(r.Problems, x => x.Contains("no first one"));
        }

        // ------------------------------------------------------ by_semantic_id

        [Fact]
        public void A_semantic_map_names_by_IDENTITY_and_survives_a_re_issue()
        {
            CadNaming n = Naming("{ 'strategy': 'by_semantic_id', 'names_by_semantic_id': " +
                                 "{ 'a': 'A', 'b': 'B' } }");
            CadNamingOutcome r = CadNamingRules.Assign(
                n, new List<CadCandidate> { At("b", 5000), At("a", 1000) }, 1.0);

            Assert.False(r.Refused);
            Assert.Equal("A", r.Names["a"]);
            Assert.Equal("B", r.Names["b"]);
            Assert.Contains("survives a re-issue", r.Evidence["a"]);
        }

        [Fact]
        public void A_mapping_for_an_id_the_drawing_no_longer_has_is_refused_by_default()
        {
            // Usually means the drawing changed under the set - which is exactly
            // the thing a silent skip would hide.
            CadNaming n = Naming("{ 'strategy': 'by_semantic_id', 'names_by_semantic_id': " +
                                 "{ 'a': 'A', 'gone': 'B' } }");
            CadNamingOutcome r = CadNamingRules.Assign(n, new List<CadCandidate> { At("a", 1000) }, 1.0);

            Assert.True(r.Refused);
            Assert.Contains("B", r.Unmatched);
        }

        [Fact]
        public void on_unnamed_leave_unnamed_is_an_explicit_choice_a_set_can_make()
        {
            // Naming half a drawing is legitimate when somebody says so out loud.
            CadNaming n = Naming("{ 'strategy': 'by_semantic_id', 'on_unnamed': 'leave_unnamed', " +
                                 "'names_by_semantic_id': { 'a': 'A' } }");
            CadNamingOutcome r = CadNamingRules.Assign(
                n, new List<CadCandidate> { At("a", 1000), At("b", 5000) }, 1.0);

            Assert.False(r.Refused);
            Assert.Equal("A", r.Names["a"]);
            Assert.Contains("b", r.Unnamed);
        }

        // --------------------------------------------------------- by_position

        [Fact]
        public void A_declared_position_names_whatever_is_actually_there()
        {
            CadNaming n = Naming("{ 'strategy': 'by_position', 'by_position': [" +
                                 "{ 'x_mm': 1000, 'tolerance_mm': 100, 'name': 'A' }," +
                                 "{ 'x_mm': 5000, 'tolerance_mm': 100, 'name': 'B' } ] }");
            CadNamingOutcome r = CadNamingRules.Assign(
                n, new List<CadCandidate> { At("a", 1010), At("b", 4995) }, 1.0);

            Assert.False(r.Refused);
            Assert.Equal("A", r.Names["a"]);
            Assert.Equal("B", r.Names["b"]);
        }

        [Fact]
        public void TWO_candidates_inside_one_declared_position_is_refused_not_guessed()
        {
            CadNaming n = Naming("{ 'strategy': 'by_position', 'by_position': [" +
                                 "{ 'x_mm': 1000, 'tolerance_mm': 5000, 'name': 'A' } ] }");
            CadNamingOutcome r = CadNamingRules.Assign(
                n, new List<CadCandidate> { At("a", 1000), At("b", 2000) }, 1.0);

            Assert.True(r.Refused);
            Assert.Contains(r.Problems, x => x.Contains("2 candidates within"));
        }

        [Fact]
        public void A_position_can_carry_a_room_NUMBER_as_well_as_a_name()
        {
            // A ROOM IS A POINT, and a room's identity is a name AND a number.
            // The fixture is a point rather than a line because a line's average
            // Y is its midpoint, which is not where anybody would declare a room.
            var room = new CadCandidate
            {
                SemanticId = "r1", ProposedKind = "room",
                Geometry = new List<CadPoint> { new CadPoint(1000, 0) }
            };
            CadNaming n = Naming("{ 'strategy': 'by_position', 'on_unnamed': 'leave_unnamed', " +
                                 "'by_position': [ { 'x_mm': 1000, 'y_mm': 0, 'tolerance_mm': 100, " +
                                 "'name': 'Office', 'number': '101' } ] }");

            CadNamingOutcome r = CadNamingRules.Assign(n, new List<CadCandidate> { room }, 1.0);

            Assert.Equal("Office", r.Names["r1"]);
            Assert.Equal("101", r.Numbers["r1"]);
        }

        // -------------------------------------------------------- collisions

        [Fact]
        public void Two_candidates_given_ONE_name_is_refused_before_anything_is_built()
        {
            CadNaming n = Naming("{ 'strategy': 'by_semantic_id', 'on_unnamed': 'leave_unnamed', " +
                                 "'names_by_semantic_id': { 'a': 'A', 'b': 'A' } }");
            CadNamingOutcome r = CadNamingRules.Assign(
                n, new List<CadCandidate> { At("a", 1000), At("b", 5000) }, 1.0);

            Assert.True(r.Refused);
            Assert.Contains(r.Problems, x => x.Contains("assigned to 2 candidates"));
        }

        [Fact]
        public void A_name_the_MODEL_already_holds_is_refused_before_half_the_batch_is_built()
        {
            // Revit refuses a duplicate grid name AT CREATION, so discovering it
            // there takes the whole batch down after building part of it.
            CadNaming n = Naming("{ 'strategy': 'by_semantic_id', 'names_by_semantic_id': { 'a': 'A' } }");
            CadNamingOutcome r = CadNamingRules.Assign(
                n, new List<CadCandidate> { At("a", 1000) }, 1.0, new[] { "A", "B" });

            Assert.True(r.Refused);
            Assert.Contains(r.Problems, x => x.Contains("already holds something called 'A'"));
        }

        // ------------------------------------------------ the loader refuses

        [Fact]
        public void A_naming_block_with_no_strategy_is_refused()
        {
            Assert.Contains("needs a strategy", Refused("{ 'axis': 'x', 'values': ['1'] }").Message);
        }

        [Fact]
        public void An_ordered_naming_with_no_AXIS_is_refused()
        {
            // Ordering without naming the axis is ordering by whatever came back
            // first, which is the whole failure mode.
            Assert.Contains("must be x, y or distance_from_origin",
                            Refused("{ 'strategy': 'ordered', 'values': ['1'] }").Message);
        }

        [Fact]
        public void An_empty_name_is_refused_because_it_is_not_a_name()
        {
            Assert.Contains("empty name",
                Refused("{ 'strategy': 'ordered', 'axis': 'x', 'values': ['1', ''] }").Message);
        }

        [Fact]
        public void A_repeated_name_in_the_list_is_refused()
        {
            Assert.Contains("repeats 'A'",
                Refused("{ 'strategy': 'ordered', 'axis': 'x', 'values': ['A', 'A'] }").Message);
        }

        [Fact]
        public void A_by_position_entry_with_no_tolerance_is_refused()
        {
            // Matching a coordinate exactly is matching nothing.
            Assert.Contains("positive tolerance_mm",
                Refused("{ 'strategy': 'by_position', 'by_position': [ { 'x_mm': 0, 'name': 'A' } ] }").Message);
        }

        [Fact]
        public void An_unknown_strategy_is_refused_with_the_list()
        {
            Assert.Contains("Known: ordered, by_semantic_id, by_position",
                Refused("{ 'strategy': 'alphabetical' }").Message);
        }

        [Fact]
        public void A_misspelt_naming_key_is_refused_rather_than_silently_ignored()
        {
            Assert.Contains("unknown naming key 'stratergy'",
                Refused("{ 'stratergy': 'ordered', 'strategy': 'ordered', 'axis': 'x', " +
                        "'values': ['1'] }").Message);
        }

        [Fact]
        public void There_is_no_on_unnamed_option_that_INVENTS_a_name()
        {
            Assert.Contains("no option that invents a name",
                Refused("{ 'strategy': 'ordered', 'axis': 'x', 'values': ['1'], " +
                        "'on_unnamed': 'generate' }").Message);
        }
    }
}
