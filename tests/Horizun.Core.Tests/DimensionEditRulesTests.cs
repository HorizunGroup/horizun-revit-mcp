// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// The rules of editing a dimension, proved without a Revit.
//
// A dimension is two objects wearing one class: single-segment dimensions carry
// their overrides on the element, multi-segment ones on each segment, and Revit
// enforces the split by throwing on one side and silently owning the other. The
// table that encodes that split, the segment-index arithmetic, the "empty string
// removes the override" rule, the 0.1 mm canonical rounding for before-values,
// the exact-move comparison and the terminal-state matrix all live in
// DimensionEditRules.cs precisely so this file can pin every row - including the
// states a live Revit will not produce on demand: a rollback that returned
// Pending, a commit whose re-read disagrees with the reversible-state check.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class DimensionEditRulesTests
    {
        // A single-segment dimension reports NumberOfSegments == 0 (the value lives on
        // the element); a segmented one reports its real count.
        private const int Single = 0;
        private const int Multi = 3;

        // ---- the eligibility table, exhaustively -------------------------------

        [Theory]
        [InlineData("prefix")]
        [InlineData("suffix")]
        [InlineData("above")]
        [InlineData("below")]
        [InlineData("value_override")]
        [InlineData("lock")]
        public void Element_level_overrides_are_allowed_on_a_single_segment_dimension(string field)
        {
            Assert.Null(DimensionEditRules.EligibilityError(field, Single));
        }

        [Theory]
        [InlineData("prefix")]
        [InlineData("suffix")]
        [InlineData("above")]
        [InlineData("below")]
        [InlineData("value_override")]
        [InlineData("lock")]
        public void Element_level_overrides_are_refused_on_a_multi_segment_dimension_pointing_at_segments(string field)
        {
            string why = DimensionEditRules.EligibilityError(field, Multi);

            Assert.NotNull(why);
            // The refusal says where the edit BELONGS, not merely no.
            Assert.Contains("segments[]", why);
        }

        [Fact]
        public void Eq_needs_segments_to_equalise()
        {
            Assert.Null(DimensionEditRules.EligibilityError("eq", Multi));
            Assert.NotNull(DimensionEditRules.EligibilityError("eq", Single));
        }

        [Fact]
        public void Segments_edits_need_a_segmented_dimension()
        {
            Assert.Null(DimensionEditRules.EligibilityError("segments", Multi));

            string why = DimensionEditRules.EligibilityError("segments", Single);
            Assert.NotNull(why);
            // ...and the refusal points back at the element-level fields.
            Assert.Contains("value_override", why);
        }

        [Theory]
        [InlineData("set_type_id")]
        [InlineData("move_by")]
        [InlineData("reset_text_position")]
        public void Type_position_and_text_reset_do_not_care_about_the_segment_count(string field)
        {
            Assert.Null(DimensionEditRules.EligibilityError(field, Single));
            Assert.Null(DimensionEditRules.EligibilityError(field, Multi));
        }

        [Fact]
        public void The_eligibility_table_covers_every_edit_field_and_nothing_else()
        {
            // The table's three rows partition the edit fields exactly. A field added
            // to EditFields without a row here would answer "eligible" by accident;
            // this pins the partition so the compiler-invisible gap fails a test.
            var union = DimensionEditRules.SingleSegmentOnlyFields
                .Concat(DimensionEditRules.MultiSegmentOnlyFields)
                .Concat(DimensionEditRules.AnySegmentFields)
                .OrderBy(x => x, StringComparer.Ordinal)
                .ToArray();
            var declared = DimensionEditRules.EditFields
                .OrderBy(x => x, StringComparer.Ordinal)
                .ToArray();

            Assert.Equal(declared, union);
        }

        [Fact]
        public void A_field_the_table_does_not_know_is_never_eligible()
        {
            Assert.NotNull(DimensionEditRules.EligibilityError("leader", Single));
            Assert.NotNull(DimensionEditRules.EligibilityError("leader", Multi));
        }

        [Fact]
        public void A_count_of_one_is_treated_as_single_segment()
        {
            // Revit reports single-segment dimensions as 0 segments; 1 should not
            // occur, but if a Revit ever reports it the element-level path is the one
            // that can work, and the choice is pinned here rather than made twice.
            Assert.True(DimensionEditRules.IsSingleSegment(0));
            Assert.True(DimensionEditRules.IsSingleSegment(1));
            Assert.False(DimensionEditRules.IsSingleSegment(2));
            Assert.Null(DimensionEditRules.EligibilityError("prefix", 1));
        }

        // ---- segment indices ---------------------------------------------------

        [Fact]
        public void A_segment_index_inside_the_count_is_addressable()
        {
            Assert.Null(DimensionEditRules.SegmentIndexError(0, 3));
            Assert.Null(DimensionEditRules.SegmentIndexError(2, 3));
        }

        [Fact]
        public void A_segment_index_at_or_past_the_count_is_out_of_range_naming_the_valid_range()
        {
            string why = DimensionEditRules.SegmentIndexError(3, 3);

            Assert.NotNull(why);
            Assert.Contains("0..2", why);
        }

        [Fact]
        public void A_negative_segment_index_is_refused()
        {
            Assert.NotNull(DimensionEditRules.SegmentIndexError(-1, 3));
        }

        // ---- duplicate targets -------------------------------------------------

        [Fact]
        public void Duplicate_element_ids_are_reported_once_each_in_first_seen_order()
        {
            List<long> dup = DimensionEditRules.DuplicateIds(new long[] { 7, 9, 7, 12, 9, 7 });

            Assert.Equal(new long[] { 7, 9 }, dup);
        }

        [Fact]
        public void A_batch_without_repeats_reports_no_duplicates()
        {
            Assert.Empty(DimensionEditRules.DuplicateIds(new long[] { 1, 2, 3 }));
            Assert.Empty(DimensionEditRules.DuplicateIds(null));
        }

        // ---- action field classification --------------------------------------

        [Fact]
        public void Every_published_edit_field_classifies_as_an_edit()
        {
            foreach (string field in DimensionEditRules.EditFields)
                Assert.Equal(DimensionEditRules.ActionFieldClass.Edit,
                             DimensionEditRules.ClassifyActionField(field));
        }

        [Theory]
        [InlineData("replace_references")]
        [InlineData("references")]
        [InlineData("set_references")]
        [InlineData("References")]   // case must not be a way past the refusal
        public void Reference_replacement_gets_its_own_class_because_no_path_can_do_it(string field)
        {
            Assert.Equal(DimensionEditRules.ActionFieldClass.ReferenceReplacement,
                         DimensionEditRules.ClassifyActionField(field));
        }

        [Fact]
        public void Element_id_is_identity_and_anything_else_is_unknown()
        {
            Assert.Equal(DimensionEditRules.ActionFieldClass.Identity,
                         DimensionEditRules.ClassifyActionField("element_id"));
            Assert.Equal(DimensionEditRules.ActionFieldClass.Unknown,
                         DimensionEditRules.ClassifyActionField("witness_line"));
            Assert.Equal(DimensionEditRules.ActionFieldClass.Unknown,
                         DimensionEditRules.ClassifyActionField(null));
        }

        // ---- shapes ------------------------------------------------------------

        [Fact]
        public void An_unknown_shape_is_refused_naming_every_known_one()
        {
            bool ok = DimensionEditRules.TryParseShapes(new[] { "linear", "banana" },
                                                        out HashSet<string> shapes, out string error);

            Assert.False(ok);
            Assert.Null(shapes);
            Assert.Contains("banana", error);
            foreach (string known in DimensionEditRules.KnownShapes)
                Assert.Contains(known, error);
        }

        [Fact]
        public void Known_shapes_parse_case_insensitively_and_deduplicate()
        {
            bool ok = DimensionEditRules.TryParseShapes(new[] { "Linear", "LINEAR", "arc_length" },
                                                        out HashSet<string> shapes, out string error);

            Assert.True(ok);
            Assert.Null(error);
            Assert.Equal(2, shapes.Count);
            Assert.Contains("linear", shapes);
            Assert.Contains("arc_length", shapes);
        }

        [Fact]
        public void Revit_shape_and_style_names_classify_to_the_wire_vocabulary()
        {
            Assert.Equal("linear", DimensionEditRules.ClassifyShape("Linear", "Linear"));
            Assert.Equal("angular", DimensionEditRules.ClassifyShape("Angular", "Angular"));
            Assert.Equal("radial", DimensionEditRules.ClassifyShape("Radial", "Radial"));
            Assert.Equal("diameter", DimensionEditRules.ClassifyShape("Diameter", "Diameter"));
            Assert.Equal("arc_length", DimensionEditRules.ClassifyShape("ArcLength", "ArcLength"));
            Assert.Equal("spot_elevation", DimensionEditRules.ClassifyShape("Spot", "SpotElevation"));
            Assert.Equal("spot_coordinate", DimensionEditRules.ClassifyShape("Spot", "SpotCoordinate"));
            Assert.Equal("spot_slope", DimensionEditRules.ClassifyShape("Spot", "SpotSlope"));
        }

        [Fact]
        public void An_unreadable_shape_falls_back_to_the_style_type_and_an_unknown_pair_is_null()
        {
            // The style type is a fact off the same element, not a guess.
            Assert.Equal("linear", DimensionEditRules.ClassifyShape(null, "Linear"));
            Assert.Equal("spot_slope", DimensionEditRules.ClassifyShape("Unknown", "SpotSlope"));
            // ...but two names the table does not know is an unknown, never a guess.
            Assert.Null(DimensionEditRules.ClassifyShape("Unknown", "Callout"));
            Assert.Null(DimensionEditRules.ClassifyShape(null, null));
        }

        // ---- value_override: '' means REMOVE -----------------------------------

        [Fact]
        public void An_empty_string_clears_the_override_and_null_requests_nothing()
        {
            Assert.True(DimensionEditRules.ClearsOverride(""));
            Assert.False(DimensionEditRules.ClearsOverride("3000"));
            Assert.False(DimensionEditRules.ClearsOverride(null));
        }

        [Fact]
        public void A_cleared_override_verifies_against_both_empty_and_null_read_backs()
        {
            // Revit stores "no override" as empty; some reads hand it back as null.
            // Both are the same stored fact, and the deletion must verify against either.
            Assert.True(DimensionEditRules.TextMatches("", ""));
            Assert.True(DimensionEditRules.TextMatches("", null));
            Assert.False(DimensionEditRules.TextMatches("", "3000"));
        }

        [Fact]
        public void Text_comparison_is_ordinal_and_exact()
        {
            Assert.True(DimensionEditRules.TextMatches("TYP.", "TYP."));
            Assert.False(DimensionEditRules.TextMatches("TYP.", "typ."));
            Assert.False(DimensionEditRules.TextMatches("TYP.", "TYP. "));
        }

        // ---- the exact-move comparison -----------------------------------------

        private static readonly double[] V = { 1.0, 2.0, 3.0 };

        private static List<double[]> Pts(params double[][] points) => new List<double[]>(points);

        [Fact]
        public void A_displacement_within_1e7_of_the_vector_passes_at_the_declared_tolerance()
        {
            var before = Pts(new[] { 0.0, 0.0, 0.0 }, new[] { 10.0, 0.0, 0.0 });
            var after = Pts(new[] { 1.0 + 1e-7, 2.0, 3.0 }, new[] { 11.0, 2.0 - 1e-7, 3.0 });

            Assert.True(DimensionEditRules.MovedExactly(before, after, V,
                                                        DimensionEditRules.DefaultMoveToleranceFeet));
        }

        [Fact]
        public void A_displacement_off_by_1e5_fails_at_the_declared_tolerance()
        {
            var before = Pts(new[] { 0.0, 0.0, 0.0 }, new[] { 10.0, 0.0, 0.0 });
            var after = Pts(new[] { 1.0 + 1e-5, 2.0, 3.0 }, new[] { 11.0, 2.0, 3.0 });

            Assert.False(DimensionEditRules.MovedExactly(before, after, V,
                                                         DimensionEditRules.DefaultMoveToleranceFeet));
        }

        [Fact]
        public void Reversed_endpoints_are_the_same_committed_geometry()
        {
            // Revit may normalise a curve by swapping its endpoints; that is not a
            // failed move and must not be reported as one.
            var before = Pts(new[] { 0.0, 0.0, 0.0 }, new[] { 10.0, 0.0, 0.0 });
            var after = Pts(new[] { 11.0, 2.0, 3.0 }, new[] { 1.0, 2.0, 3.0 });

            Assert.True(DimensionEditRules.MovedExactly(before, after, V,
                                                        DimensionEditRules.DefaultMoveToleranceFeet));
        }

        [Fact]
        public void No_samples_is_a_fail_never_a_vacuous_pass()
        {
            Assert.False(DimensionEditRules.MovedExactly(Pts(), Pts(), V, 1e-6));
            Assert.False(DimensionEditRules.MovedExactly(null, Pts(new[] { 0.0, 0.0, 0.0 }), V, 1e-6));
            Assert.False(DimensionEditRules.MovedExactly(Pts(new[] { 0.0, 0.0, 0.0 }), null, V, 1e-6));
        }

        [Fact]
        public void A_sample_count_mismatch_is_a_fail()
        {
            var before = Pts(new[] { 0.0, 0.0, 0.0 }, new[] { 10.0, 0.0, 0.0 });
            var after = Pts(new[] { 1.0, 2.0, 3.0 });

            Assert.False(DimensionEditRules.MovedExactly(before, after, V, 1e-6));
        }

        // ---- canonical rounding ------------------------------------------------

        [Fact]
        public void Rounding_is_stable_under_regeneration_jitter()
        {
            // 1 foot = 304.8 mm. Jitter far below 0.05 mm must not move the canonical
            // string, or the fingerprint would refuse every apply on its own.
            string a = DimensionEditRules.CanonicalTenthMillimetre(1.0);
            string b = DimensionEditRules.CanonicalTenthMillimetre(1.0 + 1e-8);

            Assert.Equal("304.8", a);
            Assert.Equal(a, b);
        }

        [Fact]
        public void A_real_tenth_millimetre_difference_changes_the_canonical_string()
        {
            string a = DimensionEditRules.CanonicalTenthMillimetre(0.0);
            string b = DimensionEditRules.CanonicalTenthMillimetre(0.2 / DimensionEditRules.MillimetresPerFoot);

            Assert.Equal("0.0", a);
            Assert.Equal("0.2", b);
        }

        [Fact]
        public void Negative_zero_and_zero_are_one_string()
        {
            // -0.01 mm rounds to -0.0; "-0.0" and "0.0" are one fact and must be one
            // string, or a sign flip in floating-point noise would read as drift.
            string neg = DimensionEditRules.CanonicalTenthMillimetre(-0.00001 / DimensionEditRules.MillimetresPerFoot);

            Assert.Equal("0.0", neg);
        }

        [Fact]
        public void Canonical_strings_use_invariant_culture()
        {
            // A comma-decimal locale must not change the fingerprint: the decimal
            // separator is a point wherever the process runs.
            Assert.Equal("376.3", DimensionEditRules.CanonicalTenthMillimetre(1.2345));
            Assert.Equal("304.8,609.6,914.4", DimensionEditRules.CanonicalPoint(1.0, 2.0, 3.0));
        }

        // ---- the terminal-state matrix -----------------------------------------

        [Fact]
        public void A_commit_with_every_field_verified_is_the_only_verified_applied()
        {
            Assert.Equal(DimensionEditRules.StateVerifiedApplied,
                         DimensionEditRules.DecideFinalState("Committed", allVerified: true));
        }

        [Fact]
        public void A_commit_whose_re_read_disagrees_is_uncertain_not_partial()
        {
            // The reversible-state check said yes and the committed model says no. Two
            // measurements in contradiction are the absence of knowledge, not half of it.
            Assert.Equal(DimensionEditRules.StateUncertain,
                         DimensionEditRules.DecideFinalState("Committed", allVerified: false));
        }

        [Fact]
        public void A_confirmed_rollback_is_rolled_back()
        {
            Assert.Equal(DimensionEditRules.StateRolledBack,
                         DimensionEditRules.DecideFinalState("RolledBack", allVerified: false));
        }

        [Theory]
        [InlineData("Pending")]
        [InlineData("Error")]
        [InlineData("Proceed")]
        [InlineData("Uninitialized")]
        [InlineData("SomethingNewFromRevit2031")]
        public void An_unconfirmed_rollback_keeps_its_uncertainty(string status)
        {
            // RollBack() returned something other than RolledBack. The model's state is
            // unmeasured, and no wording may smooth that into "clean" - this is the one
            // state a live Revit will not produce on demand, which is why it is pinned here.
            Assert.Equal(DimensionEditRules.StateUncertain,
                         DimensionEditRules.DecideFinalState(status, allVerified: false));
        }

        [Fact]
        public void No_transaction_at_all_is_a_refusal()
        {
            Assert.Equal(DimensionEditRules.StateRefused,
                         DimensionEditRules.DecideFinalState("not_started", allVerified: false));
        }

        [Fact]
        public void A_missing_status_is_uncertain()
        {
            Assert.Equal(DimensionEditRules.StateUncertain,
                         DimensionEditRules.DecideFinalState(null, allVerified: true));
            Assert.Equal(DimensionEditRules.StateUncertain,
                         DimensionEditRules.DecideFinalState("", allVerified: true));
        }

        [Fact]
        public void Verification_cannot_upgrade_a_status_that_did_not_commit()
        {
            // allVerified=true beside a rollback would mean the checks ran against a
            // model that then reverted; the status wins, in every row of the matrix.
            Assert.Equal(DimensionEditRules.StateRolledBack,
                         DimensionEditRules.DecideFinalState("RolledBack", allVerified: true));
            Assert.Equal(DimensionEditRules.StateUncertain,
                         DimensionEditRules.DecideFinalState("Pending", allVerified: true));
            Assert.Equal(DimensionEditRules.StateRefused,
                         DimensionEditRules.DecideFinalState("not_started", allVerified: true));
        }
    }
}
