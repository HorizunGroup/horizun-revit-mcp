// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// The arithmetic behind auto_dimension_*. Each block pins a way a generated
// dimension plan could look finished and be wrong:
//
//   * grids in two directions chained together measure distances between lines
//     that never meet, and the drawing looks plausible;
//   * "auto" that is not measured is a preference wearing the word automatic;
//   * two references at the same position make a zero-length segment - the plan
//     must refuse, not emit it;
//   * a duplicate check that respects Revit's own reference order re-plans every
//     chain already on the sheet;
//   * "complete" claimed over a partial answer is the failure mode that gets
//     signed.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class AutoDimensionRulesTests
    {
        private const double FeetPerMm = 1.0 / 304.8;

        private static AutoDimensionCandidate C(double x, double y, double? dx = null, double? dy = null,
                                                string label = null, long subject = 0, long? link = null,
                                                string stable = null)
        {
            return new AutoDimensionCandidate
            {
                X = x, Y = y, DirectionX = dx, DirectionY = dy,
                Label = label, SubjectId = subject, LinkInstanceId = link,
                Source = "grid",
                StableRepresentation = stable ?? ("ref-" + label + "-" + subject)
            };
        }

        // ---- axis resolution --------------------------------------------------

        [Fact]
        public void Auto_picks_the_axis_the_references_actually_spread_along()
        {
            var wide = new[] { C(0, 0), C(50, 0), C(100, 0) };
            double sx, sy;
            Assert.Equal("horizontal", AutoDimensionRules.ResolveAxis(wide, "auto", out sx, out sy));
            Assert.Equal(100, sx, 6);
            Assert.Equal(0, sy, 6);

            var tall = new[] { C(0, 0), C(0, 50), C(0, 100) };
            Assert.Equal("vertical", AutoDimensionRules.ResolveAxis(tall, "auto", out sx, out sy));
            Assert.Equal(0, sx, 6);
            Assert.Equal(100, sy, 6);
        }

        [Fact]
        public void An_explicit_axis_wins_even_when_it_is_the_narrow_one()
        {
            // The caller may know something the spread does not - a chain deliberately
            // measured across a narrow direction is a normal drawing.
            var wide = new[] { C(0, 0), C(100, 3) };
            double sx, sy;
            Assert.Equal("vertical", AutoDimensionRules.ResolveAxis(wide, "vertical", out sx, out sy));
        }

        [Fact]
        public void A_tie_resolves_deterministically_rather_than_on_floating_point_noise()
        {
            var square = new[] { C(0, 0), C(10, 10) };
            double sx, sy;
            Assert.Equal("horizontal", AutoDimensionRules.ResolveAxis(square, "auto", out sx, out sy));
            Assert.Equal(sx, sy, 9);
        }

        [Fact]
        public void An_empty_set_has_no_spread_and_still_answers()
        {
            double sx, sy;
            Assert.Equal("horizontal", AutoDimensionRules.ResolveAxis(new AutoDimensionCandidate[0], "auto",
                                                                     out sx, out sy));
            Assert.Equal(0, sx);
            Assert.Equal(0, sy);
        }

        [Fact]
        public void An_unknown_axis_or_side_is_refused_naming_the_known_ones()
        {
            Assert.Null(AutoDimensionRules.ValidateAxis("auto"));
            Assert.Null(AutoDimensionRules.ValidateAxis("horizontal"));
            Assert.Contains("auto, horizontal or vertical", AutoDimensionRules.ValidateAxis("sideways"));
            Assert.Null(AutoDimensionRules.ValidateSide("negative"));
            Assert.Contains("positive or negative", AutoDimensionRules.ValidateSide("left"));
        }

        // ---- direction grouping ------------------------------------------------

        [Fact]
        public void Grids_in_two_directions_become_two_chains()
        {
            var candidates = new[]
            {
                C(0, 0, 0, 1, "A"), C(10, 0, 0, 1, "B"), C(20, 0, 0, 1, "C"),
                C(0, 0, 1, 0, "1"), C(0, 10, 1, 0, "2")
            };

            List<AutoDimensionCandidate> ungroupable;
            List<List<AutoDimensionCandidate>> groups =
                AutoDimensionRules.GroupByDirection(candidates, AutoDimensionRules.DirectionToleranceDegrees,
                                                    out ungroupable);

            Assert.Equal(2, groups.Count);
            Assert.Empty(ungroupable);
            Assert.Equal(3, groups[0].Count);
            Assert.Equal(2, groups[1].Count);
        }

        [Fact]
        public void Antiparallel_is_the_same_family_so_a_reversed_grid_does_not_split_it()
        {
            var candidates = new[] { C(0, 0, 0, 1, "A"), C(10, 0, 0, -1, "B") };

            List<AutoDimensionCandidate> ungroupable;
            var groups = AutoDimensionRules.GroupByDirection(candidates,
                AutoDimensionRules.DirectionToleranceDegrees, out ungroupable);

            Assert.Single(groups);
            Assert.Equal(2, groups[0].Count);
        }

        [Fact]
        public void A_direction_beyond_the_tolerance_is_its_own_family()
        {
            // Five degrees off - ten times the tolerance. A deliberately splayed grid.
            double rad = 5.0 * Math.PI / 180.0;
            var candidates = new[]
            {
                C(0, 0, 0, 1, "A"), C(10, 0, 0, 1, "B"),
                C(20, 0, Math.Sin(rad), Math.Cos(rad), "S")
            };

            List<AutoDimensionCandidate> ungroupable;
            var groups = AutoDimensionRules.GroupByDirection(candidates,
                AutoDimensionRules.DirectionToleranceDegrees, out ungroupable);

            Assert.Equal(2, groups.Count);
        }

        [Fact]
        public void Rounding_noise_below_the_tolerance_keeps_one_family()
        {
            double rad = 0.1 * Math.PI / 180.0;
            var candidates = new[]
            {
                C(0, 0, 0, 1, "A"),
                C(10, 0, Math.Sin(rad), Math.Cos(rad), "B")
            };

            List<AutoDimensionCandidate> ungroupable;
            var groups = AutoDimensionRules.GroupByDirection(candidates,
                AutoDimensionRules.DirectionToleranceDegrees, out ungroupable);

            Assert.Single(groups);
        }

        [Fact]
        public void A_reference_with_no_direction_is_reported_rather_than_dropped_into_the_first_group()
        {
            var candidates = new[] { C(0, 0, 0, 1, "A"), C(10, 0, 0, 1, "B"), C(5, 5, null, null, "point") };

            List<AutoDimensionCandidate> ungroupable;
            var groups = AutoDimensionRules.GroupByDirection(candidates,
                AutoDimensionRules.DirectionToleranceDegrees, out ungroupable);

            Assert.Single(groups);
            Assert.Single(ungroupable);
            Assert.Equal("point", ungroupable[0].Label);
        }

        [Fact]
        public void A_degenerate_direction_vector_is_ungroupable_not_a_division_by_zero()
        {
            var candidates = new[] { C(0, 0, 0, 0, "zero"), C(1, 1, double.NaN, 1, "nan") };

            List<AutoDimensionCandidate> ungroupable;
            var groups = AutoDimensionRules.GroupByDirection(candidates,
                AutoDimensionRules.DirectionToleranceDegrees, out ungroupable);

            Assert.Empty(groups);
            Assert.Equal(2, ungroupable.Count);
        }

        [Fact]
        public void The_group_key_folds_a_direction_and_its_opposite_onto_one_name()
        {
            string up = AutoDimensionRules.GroupKey(new[] { C(0, 0, 0, 1, "A") });
            string down = AutoDimensionRules.GroupKey(new[] { C(0, 0, 0, -1, "B") });
            Assert.Equal(up, down);
            Assert.NotEqual(up, AutoDimensionRules.GroupKey(new[] { C(0, 0, 1, 0, "1") }));
            Assert.Equal("no-direction", AutoDimensionRules.GroupKey(new[] { C(0, 0, null, null, "p") }));
        }

        // ---- ordering ----------------------------------------------------------

        [Fact]
        public void A_chain_is_ordered_by_projected_position_along_its_axis()
        {
            var group = new List<AutoDimensionCandidate> { C(30, 0, 0, 1, "C"), C(10, 0, 0, 1, "A"), C(20, 0, 0, 1, "B") };

            List<AutoDimensionCandidate> ordered; string code;
            Assert.Null(AutoDimensionRules.OrderAlongAxis(group, "horizontal", out ordered, out code));
            Assert.Equal(new[] { "A", "B", "C" }, ordered.Select(c => c.Label).ToArray());
            Assert.Null(code);
        }

        [Fact]
        public void Two_references_at_the_same_position_refuse_the_chain_naming_both()
        {
            var group = new List<AutoDimensionCandidate> { C(10, 0, 0, 1, "A"), C(10, 0, 0, 1, "A-duplicate") };

            List<AutoDimensionCandidate> ordered; string code;
            string error = AutoDimensionRules.OrderAlongAxis(group, "horizontal", out ordered, out code);

            Assert.NotNull(error);
            Assert.Equal(AutoDimensionRules.CodeCoincidentReferences, code);
            Assert.Contains("A", error);
            Assert.Contains("A-duplicate", error);
            Assert.Null(ordered);
        }

        [Fact]
        public void Separation_just_above_the_tolerance_is_accepted_and_just_below_is_refused()
        {
            double justOver = 0.11 * FeetPerMm;
            double justUnder = 0.09 * FeetPerMm;

            List<AutoDimensionCandidate> ordered; string code;
            Assert.Null(AutoDimensionRules.OrderAlongAxis(
                new List<AutoDimensionCandidate> { C(0, 0, 0, 1, "A"), C(justOver, 0, 0, 1, "B") },
                "horizontal", out ordered, out code));

            Assert.NotNull(AutoDimensionRules.OrderAlongAxis(
                new List<AutoDimensionCandidate> { C(0, 0, 0, 1, "A"), C(justUnder, 0, 0, 1, "B") },
                "horizontal", out ordered, out code));
            Assert.Equal(AutoDimensionRules.CodeCoincidentReferences, code);
        }

        [Fact]
        public void A_group_of_one_is_refused_with_its_own_code_rather_than_as_a_coincidence()
        {
            List<AutoDimensionCandidate> ordered; string code;
            string error = AutoDimensionRules.OrderAlongAxis(
                new List<AutoDimensionCandidate> { C(0, 0, 0, 1, "lonely") }, "horizontal", out ordered, out code);

            Assert.NotNull(error);
            Assert.Equal(AutoDimensionRules.CodeGroupTooSmall, code);
        }

        [Fact]
        public void A_chain_measured_across_its_narrow_axis_is_refused_for_having_no_spread()
        {
            // Two grids 10 ft apart horizontally, chained VERTICALLY: nothing to measure.
            var group = new List<AutoDimensionCandidate> { C(0, 0, 0, 1, "A"), C(10, 0, 0, 1, "B") };

            List<AutoDimensionCandidate> ordered; string code;
            string error = AutoDimensionRules.OrderAlongAxis(group, "vertical", out ordered, out code);

            Assert.NotNull(error);
            // Coincidence fires first because the two ARE at the same vertical position;
            // either verdict is a refusal, and both name a real reason.
            Assert.True(code == AutoDimensionRules.CodeNoSpread ||
                        code == AutoDimensionRules.CodeCoincidentReferences, code);
        }

        [Fact]
        public void The_order_tiebreak_is_ordinal_so_two_runs_cannot_disagree()
        {
            var a = C(10, 0, 0, 1, "A", stable: "zzz");
            var b = C(10.5, 0, 0, 1, "B", stable: "aaa");

            List<AutoDimensionCandidate> one, two; string code;
            AutoDimensionRules.OrderAlongAxis(new List<AutoDimensionCandidate> { a, b }, "horizontal", out one, out code);
            AutoDimensionRules.OrderAlongAxis(new List<AutoDimensionCandidate> { b, a }, "horizontal", out two, out code);

            Assert.Equal(one.Select(c => c.Label), two.Select(c => c.Label));
        }

        // ---- line placement ----------------------------------------------------

        [Fact]
        public void A_horizontal_chain_puts_its_line_offset_above_and_running_past_both_ends()
        {
            var ordered = new List<AutoDimensionCandidate> { C(0, 0, 0, 1, "A"), C(100, 0, 0, 1, "B") };
            var chain = new AutoDimensionChain();

            AutoDimensionRules.PlaceLine(ordered, "horizontal", "positive", 10, chain);

            Assert.Equal(10, chain.StartY, 6);
            Assert.Equal(10, chain.EndY, 6);
            Assert.True(chain.StartX < 0, "the line must run PAST the first reference");
            Assert.True(chain.EndX > 100, "the line must run PAST the last reference");
            Assert.Equal("horizontal", chain.Axis);
        }

        [Fact]
        public void The_negative_side_puts_the_line_on_the_other_side_of_the_references()
        {
            var ordered = new List<AutoDimensionCandidate> { C(0, 0, 0, 1, "A"), C(100, 0, 0, 1, "B") };
            var positive = new AutoDimensionChain();
            var negative = new AutoDimensionChain();

            AutoDimensionRules.PlaceLine(ordered, "horizontal", "positive", 10, positive);
            AutoDimensionRules.PlaceLine(ordered, "horizontal", "negative", 10, negative);

            Assert.Equal(10, positive.StartY, 6);
            Assert.Equal(-10, negative.StartY, 6);
        }

        [Fact]
        public void A_vertical_chain_runs_along_y_and_offsets_along_x()
        {
            var ordered = new List<AutoDimensionCandidate> { C(0, 0, 1, 0, "1"), C(0, 80, 1, 0, "2") };
            var chain = new AutoDimensionChain();

            AutoDimensionRules.PlaceLine(ordered, "vertical", "positive", 6, chain);

            Assert.Equal(6, chain.StartX, 6);
            Assert.Equal(6, chain.EndX, 6);
            Assert.True(chain.StartY < 0);
            Assert.True(chain.EndY > 80);
        }

        [Fact]
        public void A_zero_offset_still_produces_a_witness_tail_rather_than_a_line_of_zero_length()
        {
            var ordered = new List<AutoDimensionCandidate> { C(0, 0, 0, 1, "A"), C(10, 0, 0, 1, "B") };
            var chain = new AutoDimensionChain();

            AutoDimensionRules.PlaceLine(ordered, "horizontal", "positive", 0, chain);

            Assert.True(chain.EndX - chain.StartX > 10);
        }

        [Fact]
        public void Placing_a_line_for_no_references_throws_rather_than_inventing_one()
        {
            Assert.Throws<ArgumentException>(() =>
                AutoDimensionRules.PlaceLine(new List<AutoDimensionCandidate>(), "horizontal", "positive", 10,
                                             new AutoDimensionChain()));
        }

        [Fact]
        public void Successive_chains_stack_so_the_second_does_not_land_on_the_first()
        {
            Assert.Equal(10, AutoDimensionRules.StackedOffset(10, 10, 0), 9);
            Assert.Equal(20, AutoDimensionRules.StackedOffset(10, 10, 1), 9);
            Assert.Equal(30, AutoDimensionRules.StackedOffset(10, 10, 2), 9);
            Assert.Throws<ArgumentException>(() => AutoDimensionRules.StackedOffset(10, 10, -1));
        }

        // ---- duplicate detection ------------------------------------------------

        [Fact]
        public void An_existing_chain_is_recognised_however_revit_happens_to_order_its_references()
        {
            var planned = new[] { "ref-A", "ref-B", "ref-C" };
            string existing = AutoDimensionRules.ChainIdentityUnordered(new[] { "ref-C", "ref-A", "ref-B" });

            Assert.True(AutoDimensionRules.IsDuplicate(planned, new[] { existing }));
        }

        [Fact]
        public void A_chain_over_a_different_reference_set_is_not_a_duplicate()
        {
            string existing = AutoDimensionRules.ChainIdentityUnordered(new[] { "ref-A", "ref-B" });
            Assert.False(AutoDimensionRules.IsDuplicate(new[] { "ref-A", "ref-B", "ref-C" }, new[] { existing }));
            Assert.False(AutoDimensionRules.IsDuplicate(new[] { "ref-A", "ref-B" }, null));
        }

        [Fact]
        public void The_ordered_identity_is_order_sensitive_because_a_dimension_is_positional()
        {
            Assert.NotEqual(AutoDimensionRules.ChainIdentity(new[] { "a", "b" }),
                            AutoDimensionRules.ChainIdentity(new[] { "b", "a" }));
            Assert.Equal(AutoDimensionRules.ChainIdentityUnordered(new[] { "a", "b" }),
                         AutoDimensionRules.ChainIdentityUnordered(new[] { "b", "a" }));
        }

        [Fact]
        public void A_reference_cannot_forge_a_boundary_between_two_others()
        {
            // "a" + separator + "b" must not collide with a single reference that
            // contains the separator character itself.
            string two = AutoDimensionRules.ChainIdentity(new[] { "a", "b" });
            string forged = AutoDimensionRules.ChainIdentity(new[] { "a" + (char)31 + "b" });
            Assert.NotEqual(two, forged);
        }

        // ---- coverage -----------------------------------------------------------

        [Fact]
        public void Complete_is_claimed_only_when_every_reference_found_made_it_into_a_chain()
        {
            Assert.Equal("complete", AutoDimensionRules.Coverage(found: 5, planned: 5, omitted: 0));
            Assert.Equal("partial", AutoDimensionRules.Coverage(found: 5, planned: 4, omitted: 1));
            Assert.Equal("none", AutoDimensionRules.Coverage(found: 5, planned: 0, omitted: 5));
            Assert.Equal("nothing_found", AutoDimensionRules.Coverage(found: 0, planned: 0, omitted: 0));
        }

        [Fact]
        public void A_plan_that_covered_everything_but_omitted_something_is_not_complete()
        {
            // planned == found can only coexist with omitted > 0 through a counting bug;
            // the verdict must fall to partial rather than take the optimistic branch.
            Assert.Equal("partial", AutoDimensionRules.Coverage(found: 5, planned: 5, omitted: 2));
        }

        // ---- identity and description --------------------------------------------

        [Fact]
        public void A_candidate_identity_separates_host_from_linked_and_link_from_link()
        {
            Assert.NotEqual(C(0, 0, subject: 42).Identity, C(0, 0, subject: 42, link: 100).Identity);
            Assert.NotEqual(C(0, 0, subject: 42, link: 100).Identity, C(0, 0, subject: 42, link: 200).Identity);
        }

        [Fact]
        public void A_description_names_the_link_when_there_is_one()
        {
            Assert.Contains("link instance 880011", AutoDimensionRules.Describe(C(0, 0, label: "A", link: 880011)));
            Assert.DoesNotContain("link", AutoDimensionRules.Describe(C(0, 0, label: "A")));
            Assert.Contains("element 42", AutoDimensionRules.Describe(C(0, 0, subject: 42)));
            Assert.Equal("(none)", AutoDimensionRules.Describe(null));
        }

        [Fact]
        public void An_unknown_operation_is_refused_naming_every_known_one()
        {
            string error = AutoDimensionRules.OperationError("auto_dimension_everything");
            foreach (string known in AutoDimensionRules.KnownOperations) Assert.Contains(known, error);
            Assert.Contains("auto_tags", error);
            Assert.Contains("intent_dimension", error);
        }
    }
}
