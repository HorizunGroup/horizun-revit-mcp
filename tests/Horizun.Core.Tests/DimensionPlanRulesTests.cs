// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// THE DIMENSION RULES, proved without a building.
//
// AnnotateCommand's dimension operations lean on DimensionPlanRules for every
// decision that does not need Revit: the conditional requirements table, the
// reference arithmetic, option eligibility, the 0.1 mm canonical rounding and
// geometry fingerprints, curve comparison at a named tolerance, the unit
// factors, and the final-state matrix over Revit's real TransactionStatus
// values. Each of those has a failure mode that a live Revit will not produce
// on demand - a rollback that answers Pending above all - so each is pinned
// here, exhaustively where the rule is a table.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class DimensionPlanRulesTests
    {
        // ---- the stored-text rule behind horizun_annotate's text verification -------
        //
        // Measured on Revit 2023 (2026-08-24): TextNote.Create('D7_PROBE') reads back
        // 'D7_PROBE\r'. The strict == that preceded this rule refused every correct
        // text note the bridge ever created - a false NEGATIVE, which is the cheap-
        // looking direction that still teaches callers to distrust the verification.

        [Theory]
        [InlineData("D7_PROBE", "D7_PROBE\r")]                    // the measured case
        [InlineData("D7_PROBE", "D7_PROBE")]                      // a Revit that stops appending
        [InlineData("linea 1\nlinea 2", "linea 1\rlinea 2\r")]    // interior separators re-encoded
        [InlineData("linea 1\r\nlinea 2", "linea 1\rlinea 2")]    // caller sent CRLF
        [InlineData(" ", " \r")]                                  // the whitespace fixture note
        [InlineData("a\n\nb", "a\r\rb\r")]                        // interior BLANK line survives
        public void Stored_text_matches_when_only_the_line_encoding_differs(string requested, string stored)
        {
            Assert.True(DimensionPlanRules.StoredTextMatches(requested, stored));
        }

        [Theory]
        [InlineData("D7_PROBE", "D7_PROBE ")]     // a trailing SPACE is substance, not encoding
        [InlineData("D7_PROBE", "d7_probe\r")]    // case is substance
        [InlineData("a\nb", "a b\r")]             // a line break is not a space
        [InlineData("a\n\nb", "a\rb\r")]          // a swallowed blank line is a changed text
        [InlineData("x", "")]
        public void Stored_text_refuses_when_the_substance_differs(string requested, string stored)
        {
            Assert.False(DimensionPlanRules.StoredTextMatches(requested, stored));
        }

        [Fact]
        public void Stored_text_never_matches_null()
        {
            Assert.False(DimensionPlanRules.StoredTextMatches(null, "x"));
            Assert.False(DimensionPlanRules.StoredTextMatches("x", null));
        }

        // ---- the conditional requirements table, exhaustively -----------------

        public static IEnumerable<object[]> EveryOperationAndItsFields()
        {
            yield return new object[] { DimensionPlanRules.OpText, new[] { "view_id", "point", "text", "text_type_id" } };
            yield return new object[] { DimensionPlanRules.OpTag, new[] { "view_id", "point", "element_id" } };
            yield return new object[] { DimensionPlanRules.OpDimension, new[] { "view_id", "line_start", "line_end", "references" } };
            yield return new object[] { DimensionPlanRules.OpAngular, new[] { "view_id", "arc_center", "arc_radius", "references" } };
            yield return new object[] { DimensionPlanRules.OpRadial, new[] { "view_id", "reference" } };
            yield return new object[] { DimensionPlanRules.OpDiameter, new[] { "view_id", "reference" } };
            yield return new object[] { DimensionPlanRules.OpArcLength, new[] { "view_id", "arc_center", "arc_radius", "arc_reference", "references" } };
            yield return new object[] { DimensionPlanRules.OpSpotElevation, new[] { "view_id", "reference", "point" } };
            yield return new object[] { DimensionPlanRules.OpSpotCoordinate, new[] { "view_id", "reference", "point" } };
        }

        [Theory]
        [MemberData(nameof(EveryOperationAndItsFields))]
        public void The_requirements_table_names_exactly_these_fields(string op, string[] expected)
        {
            Assert.Equal(expected, DimensionPlanRules.RequiredFields(op));
        }

        [Theory]
        [MemberData(nameof(EveryOperationAndItsFields))]
        public void Removing_any_single_required_field_is_reported_by_name(string op, string[] required)
        {
            foreach (string removed in required)
            {
                var present = new HashSet<string>(required, StringComparer.Ordinal);
                present.Remove(removed);
                List<string> missing = DimensionPlanRules.MissingFields(op, present.Contains);
                Assert.Equal(new[] { removed }, missing);
            }
        }

        [Theory]
        [MemberData(nameof(EveryOperationAndItsFields))]
        public void All_fields_present_reports_nothing_missing(string op, string[] required)
        {
            var present = new HashSet<string>(required, StringComparer.Ordinal);
            Assert.Empty(DimensionPlanRules.MissingFields(op, present.Contains));
        }

        [Fact]
        public void Spot_slope_requires_nothing_because_it_is_refused_before_fields_are_read()
        {
            Assert.Empty(DimensionPlanRules.RequiredFields(DimensionPlanRules.OpSpotSlope));
        }

        [Fact]
        public void An_unknown_operation_has_no_table_row_rather_than_an_empty_one()
        {
            // "We do not know its shape" must stay distinguishable from "it needs
            // nothing" - collapsing them would validate garbage as complete.
            Assert.Null(DimensionPlanRules.RequiredFields("dimensionX"));
            Assert.Null(DimensionPlanRules.MissingFields("dimensionX", f => true));
        }

        [Fact]
        public void The_operation_families_are_disjoint_and_complete()
        {
            Assert.True(DimensionPlanRules.IsKnownOperation(DimensionPlanRules.OpText));
            Assert.True(DimensionPlanRules.IsKnownOperation(DimensionPlanRules.OpTag));
            Assert.True(DimensionPlanRules.IsKnownOperation(DimensionPlanRules.OpSpotSlope));
            foreach (string op in DimensionPlanRules.DimensionOperations)
                Assert.True(DimensionPlanRules.IsKnownOperation(op));
            Assert.False(DimensionPlanRules.IsDimensionOperation(DimensionPlanRules.OpSpotSlope));
            Assert.False(DimensionPlanRules.IsDimensionOperation(DimensionPlanRules.OpText));
            Assert.False(DimensionPlanRules.IsKnownOperation("dimensionX"));
            Assert.False(DimensionPlanRules.IsKnownOperation(null));
        }

        // ---- reference-list arithmetic ---------------------------------------

        private static List<string> Refs(int n)
            => Enumerable.Range(0, n).Select(i => "ref-" + i).ToList();

        [Fact]
        public void Linear_accepts_2_to_32_references_and_refuses_outside()
        {
            Assert.Null(DimensionPlanRules.ReferenceListError(DimensionPlanRules.OpDimension, Refs(2)));
            Assert.Null(DimensionPlanRules.ReferenceListError(DimensionPlanRules.OpDimension, Refs(32)));
            Assert.Contains("2..32", DimensionPlanRules.ReferenceListError(DimensionPlanRules.OpDimension, Refs(1)));
            Assert.Contains("2..32", DimensionPlanRules.ReferenceListError(DimensionPlanRules.OpDimension, Refs(33)));
        }

        [Fact]
        public void Angular_and_arc_length_take_exactly_two()
        {
            Assert.Null(DimensionPlanRules.ReferenceListError(DimensionPlanRules.OpAngular, Refs(2)));
            Assert.Contains("exactly 2", DimensionPlanRules.ReferenceListError(DimensionPlanRules.OpAngular, Refs(3)));
            Assert.Contains("exactly 2", DimensionPlanRules.ReferenceListError(DimensionPlanRules.OpArcLength, Refs(1)));
        }

        [Fact]
        public void Operations_without_a_references_array_say_so()
        {
            Assert.Contains("does not take a references array",
                DimensionPlanRules.ReferenceListError(DimensionPlanRules.OpRadial, Refs(1)));
        }

        [Fact]
        public void A_null_or_empty_entry_is_named_by_index()
        {
            var refs = Refs(3); refs[1] = "  ";
            string error = DimensionPlanRules.ReferenceListError(DimensionPlanRules.OpDimension, refs);
            Assert.Contains("references[1]", error);
            Assert.Contains("empty", error);

            refs = Refs(3); refs[2] = null;
            Assert.Contains("references[2]", DimensionPlanRules.ReferenceListError(DimensionPlanRules.OpDimension, refs));
        }

        [Fact]
        public void An_exact_duplicate_names_both_positions()
        {
            var refs = new List<string> { "a", "b", "a" };
            string error = DimensionPlanRules.ReferenceListError(DimensionPlanRules.OpDimension, refs);
            Assert.Contains("references[2]", error);
            Assert.Contains("references[0]", error);
        }

        [Fact]
        public void Two_representations_differing_by_one_character_are_not_duplicates()
        {
            // The comparison is exact-string by contract: claiming two different
            // stable representations "mean the same" would be a guess.
            Assert.Null(DimensionPlanRules.ReferenceListError(DimensionPlanRules.OpDimension,
                new List<string> { "ref:1:SURFACE", "ref:1:SURFACE " }));
        }

        // ---- option availability and eligibility -----------------------------

        [Fact]
        public void Only_linear_carries_overrides_eq_and_lock()
        {
            foreach (string option in new[] { "prefix", "suffix", "above", "below", "value_override", "eq", "lock" })
            {
                Assert.True(DimensionPlanRules.AllowsOption(DimensionPlanRules.OpDimension, option), option);
                Assert.False(DimensionPlanRules.AllowsOption(DimensionPlanRules.OpAngular, option), option);
                Assert.False(DimensionPlanRules.AllowsOption(DimensionPlanRules.OpSpotElevation, option), option);
            }
        }

        [Fact]
        public void Types_are_optional_only_where_the_creating_api_takes_one()
        {
            Assert.True(DimensionPlanRules.AllowsOption(DimensionPlanRules.OpDimension, "dimension_type_id"));
            Assert.True(DimensionPlanRules.AllowsOption(DimensionPlanRules.OpAngular, "dimension_type_id"));
            // RadialDimension.Create / ArcLengthDimension.Create have no type
            // parameter; the document default applies and is bound into the plan.
            Assert.False(DimensionPlanRules.AllowsOption(DimensionPlanRules.OpRadial, "dimension_type_id"));
            Assert.False(DimensionPlanRules.AllowsOption(DimensionPlanRules.OpDiameter, "dimension_type_id"));
            Assert.False(DimensionPlanRules.AllowsOption(DimensionPlanRules.OpArcLength, "dimension_type_id"));
            Assert.False(DimensionPlanRules.AllowsOption(DimensionPlanRules.OpSpotElevation, "dimension_type_id"));
        }

        [Fact]
        public void Leader_and_leader_points_belong_to_the_spots_alone()
        {
            foreach (string option in new[] { "leader", "bend", "end" })
            {
                Assert.True(DimensionPlanRules.AllowsOption(DimensionPlanRules.OpSpotElevation, option), option);
                Assert.True(DimensionPlanRules.AllowsOption(DimensionPlanRules.OpSpotCoordinate, option), option);
                Assert.False(DimensionPlanRules.AllowsOption(DimensionPlanRules.OpDimension, option), option);
                Assert.False(DimensionPlanRules.AllowsOption(DimensionPlanRules.OpRadial, option), option);
            }
        }

        [Fact]
        public void Expected_value_exists_wherever_a_measured_value_does_and_nowhere_else()
        {
            foreach (string op in new[] { DimensionPlanRules.OpDimension, DimensionPlanRules.OpAngular,
                                          DimensionPlanRules.OpRadial, DimensionPlanRules.OpDiameter,
                                          DimensionPlanRules.OpArcLength })
                Assert.True(DimensionPlanRules.AllowsOption(op, "expected_value"), op);
            Assert.False(DimensionPlanRules.AllowsOption(DimensionPlanRules.OpSpotElevation, "expected_value"));
            Assert.False(DimensionPlanRules.AllowsOption(DimensionPlanRules.OpSpotCoordinate, "expected_value"));
        }

        [Fact]
        public void Unavailable_options_are_refused_by_name_with_the_reason()
        {
            List<string> refused = DimensionPlanRules.UnavailableOptions(
                DimensionPlanRules.OpRadial, new[] { "dimension_type_id", "leader" });
            Assert.Equal(2, refused.Count);
            Assert.Contains(refused, r => r.Contains("dimension_type_id") && r.Contains("default type"));
            Assert.Contains(refused, r => r.Contains("leader"));
        }

        [Fact]
        public void An_allowed_option_is_never_refused()
        {
            Assert.Empty(DimensionPlanRules.UnavailableOptions(DimensionPlanRules.OpDimension,
                new[] { "dimension_type_id", "prefix", "eq", "lock", "expected_value" }));
        }

        [Fact]
        public void Single_segment_options_need_exactly_two_references()
        {
            foreach (string option in new[] { "prefix", "suffix", "above", "below", "value_override", "lock" })
            {
                Assert.Empty(DimensionPlanRules.IneligibleOptions(2, new[] { option }));
                Assert.Single(DimensionPlanRules.IneligibleOptions(3, new[] { option }));
                Assert.Contains(option, DimensionPlanRules.IneligibleOptions(3, new[] { option })[0]);
            }
        }

        [Fact]
        public void Eq_needs_at_least_three_references()
        {
            Assert.Single(DimensionPlanRules.IneligibleOptions(2, new[] { "eq" }));
            Assert.Empty(DimensionPlanRules.IneligibleOptions(3, new[] { "eq" }));
            Assert.Empty(DimensionPlanRules.IneligibleOptions(32, new[] { "eq" }));
        }

        [Fact]
        public void Eligibility_reports_every_offending_option_not_just_the_first()
        {
            List<string> refused = DimensionPlanRules.IneligibleOptions(3, new[] { "prefix", "lock", "eq" });
            Assert.Equal(2, refused.Count);   // eq is fine at 3; prefix and lock are not
        }

        // ---- units -----------------------------------------------------------

        [Theory]
        [InlineData("mm", 1.0 / 304.8)]
        [InlineData("m", 1.0 / 0.3048)]
        [InlineData("feet", 1.0)]
        public void Unit_factors_convert_to_internal_feet(string units, double expected)
        {
            double factor;
            Assert.True(DimensionPlanRules.UnitScale(units, out factor));
            Assert.Equal(expected, factor, 12);
        }

        [Fact]
        public void An_unknown_unit_is_refused_not_guessed()
        {
            double factor;
            Assert.False(DimensionPlanRules.UnitScale("inches", out factor));
            Assert.Equal(0.0, factor);
        }

        [Fact]
        public void A_millimetre_request_scales_a_value_the_way_the_command_will()
        {
            double toFeet;
            DimensionPlanRules.UnitScale("mm", out toFeet);
            Assert.Equal(1.0, 304.8 * toFeet, 12);          // 304.8 mm is one foot
            DimensionPlanRules.UnitScale("m", out toFeet);
            Assert.Equal(1.0, 0.3048 * toFeet, 12);         // 0.3048 m is one foot
        }

        [Fact]
        public void The_default_expected_tolerance_is_a_tenth_of_a_millimetre()
        {
            Assert.Equal(0.1 / 304.8, DimensionPlanRules.DefaultExpectedToleranceFeet, 15);
            Assert.Equal(DimensionPlanRules.DefaultExpectedToleranceFeet,
                         DimensionPlanRules.ExpectedToleranceFeet(null, 1.0 / 304.8), 15);
        }

        [Fact]
        public void An_explicit_tolerance_arrives_in_request_units()
        {
            // 2 mm of tolerance, in a request whose units are mm.
            Assert.Equal(2.0 / 304.8, DimensionPlanRules.ExpectedToleranceFeet(2.0, 1.0 / 304.8), 15);
        }

        [Fact]
        public void Angular_expectations_use_degrees_and_their_own_default()
        {
            Assert.Equal(Math.PI, DimensionPlanRules.DegreesToRadians(180.0), 12);
            Assert.Equal(DimensionPlanRules.DegreesToRadians(0.01),
                         DimensionPlanRules.DefaultAngularToleranceRadians, 15);
        }

        // ---- canonical rounding and fingerprints -----------------------------

        [Fact]
        public void One_foot_renders_as_its_millimetres_to_one_decimal()
        {
            Assert.Equal("304.8", DimensionPlanRules.CanonicalFeet(1.0));
            Assert.Equal("0.0", DimensionPlanRules.CanonicalFeet(0.0));
            Assert.Equal("-304.8", DimensionPlanRules.CanonicalFeet(-1.0));
        }

        [Fact]
        public void A_negative_hair_does_not_render_as_negative_zero()
        {
            Assert.Equal("0.0", DimensionPlanRules.CanonicalFeet(-0.00001 / 304.8));
        }

        [Fact]
        public void The_fingerprint_is_stable_under_sub_tenth_millimetre_jitter()
        {
            // 100.00 mm and 100.04 mm are the same canonical coordinate: Revit's
            // regeneration jitters the last digits, and a fingerprint that changed on
            // its own would refuse every apply.
            string a = DimensionPlanRules.GeometryFingerprint(0, "Surface", "face",
                new[] { 100.00 / 304.8, 0.0, 0.0 });
            string b = DimensionPlanRules.GeometryFingerprint(0, "Surface", "face",
                new[] { 100.04 / 304.8, 0.0, 0.0 });
            Assert.Equal(a, b);
        }

        [Fact]
        public void The_fingerprint_is_sensitive_to_a_real_move()
        {
            string a = DimensionPlanRules.GeometryFingerprint(0, "Surface", "face",
                new[] { 100.0 / 304.8, 0.0, 0.0 });
            string b = DimensionPlanRules.GeometryFingerprint(0, "Surface", "face",
                new[] { 100.2 / 304.8, 0.0, 0.0 });
            Assert.NotEqual(a, b);
        }

        [Fact]
        public void The_fingerprint_is_sensitive_to_order_kind_and_reference_type()
        {
            var facts = new[] { 1.0, 2.0, 3.0 };
            string baselinePrint = DimensionPlanRules.GeometryFingerprint(0, "Surface", "face", facts);
            Assert.NotEqual(baselinePrint, DimensionPlanRules.GeometryFingerprint(1, "Surface", "face", facts));
            Assert.NotEqual(baselinePrint, DimensionPlanRules.GeometryFingerprint(0, "Linear", "face", facts));
            Assert.NotEqual(baselinePrint, DimensionPlanRules.GeometryFingerprint(0, "Surface", "curve", facts));
        }

        [Fact]
        public void Combining_fingerprints_is_order_sensitive()
        {
            // A dimension's references are positional: the same two references
            // swapped are a DIFFERENT dimension, and the combined print must say so.
            Assert.NotEqual(DimensionPlanRules.CombineFingerprints(new[] { "a", "b" }),
                            DimensionPlanRules.CombineFingerprints(new[] { "b", "a" }));
            Assert.Equal(DimensionPlanRules.CombineFingerprints(new[] { "a", "b" }),
                         DimensionPlanRules.CombineFingerprints(new[] { "a", "b" }));
        }

        [Fact]
        public void Canonical_points_join_three_canonical_coordinates()
        {
            Assert.Equal("304.8,0.0,-304.8", DimensionPlanRules.CanonicalPoint(1.0, 0.0, -1.0));
        }

        // ---- curve comparison at the named tolerance -------------------------

        [Fact]
        public void A_tenth_of_the_tolerance_passes_and_ten_times_fails()
        {
            var a = new[] { 1.0, 2.0, 3.0 };
            Assert.True(DimensionPlanRules.SamePoint(a, new[] { 1.0 + 1e-7, 2.0, 3.0 },
                DimensionPlanRules.CurveToleranceFeet));
            Assert.False(DimensionPlanRules.SamePoint(a, new[] { 1.0 + 1e-5, 2.0, 3.0 },
                DimensionPlanRules.CurveToleranceFeet));
        }

        [Fact]
        public void Malformed_points_never_pass()
        {
            Assert.False(DimensionPlanRules.SamePoint(null, new[] { 0.0, 0.0, 0.0 }, 1e-6));
            Assert.False(DimensionPlanRules.SamePoint(new[] { 0.0, 0.0 }, new[] { 0.0, 0.0, 0.0 }, 1e-6));
        }

        [Fact]
        public void The_same_segment_reversed_is_the_same_segment()
        {
            var s = new[] { 0.0, 0.0, 0.0 }; var e = new[] { 1.0, 0.0, 0.0 };
            Assert.True(DimensionPlanRules.SameEndpoints(s, e, e, s, 1e-6));
            Assert.True(DimensionPlanRules.SameEndpoints(s, e, s, e, 1e-6));
            Assert.False(DimensionPlanRules.SameEndpoints(s, e, s, new[] { 2.0, 0.0, 0.0 }, 1e-6));
        }

        [Fact]
        public void Point_on_infinite_line_uses_perpendicular_distance()
        {
            var origin = new[] { 0.0, 0.0, 0.0 };
            var direction = new[] { 1.0, 0.0, 0.0 };
            // Far along the line is still ON the line - Revit rebases dimension lines
            // along themselves, and that is not a moved dimension.
            Assert.True(DimensionPlanRules.PointOnLine(origin, direction, new[] { 500.0, 0.0, 0.0 }, 1e-6));
            Assert.True(DimensionPlanRules.PointOnLine(origin, direction, new[] { 500.0, 1e-7, 0.0 }, 1e-6));
            Assert.False(DimensionPlanRules.PointOnLine(origin, direction, new[] { 500.0, 1e-5, 0.0 }, 1e-6));
        }

        [Fact]
        public void A_degenerate_direction_fails_closed()
        {
            // A distance nobody could compute is not a small one.
            Assert.True(double.IsNaN(DimensionPlanRules.DistancePointToLine(
                new[] { 0.0, 0.0, 0.0 }, new[] { 0.0, 0.0, 0.0 }, new[] { 1.0, 0.0, 0.0 })));
            Assert.False(DimensionPlanRules.PointOnLine(
                new[] { 0.0, 0.0, 0.0 }, new[] { 0.0, 0.0, 0.0 }, new[] { 0.0, 0.0, 0.0 }, 1e-6));
        }

        // ---- segment arithmetic ----------------------------------------------

        [Theory]
        [InlineData(2, 0)]   // Revit counts a two-reference dimension as ZERO segments
        [InlineData(3, 2)]
        [InlineData(32, 31)]
        public void Expected_segment_count_follows_revits_own_counting(int references, int segments)
        {
            Assert.Equal(segments, DimensionPlanRules.ExpectedSegmentCount(references));
        }

        [Fact]
        public void The_total_prefers_the_single_value_then_sums_segments_then_stays_unknown()
        {
            Assert.Equal(2.5, DimensionPlanRules.TotalOf(2.5, new List<double> { 9.0 }));
            Assert.Equal(3.0, DimensionPlanRules.TotalOf(null, new List<double> { 1.0, 2.0 }));
            Assert.Null(DimensionPlanRules.TotalOf(null, new List<double>()));
            Assert.Null(DimensionPlanRules.TotalOf(null, null));
        }

        // ---- the final-state matrix ------------------------------------------

        [Fact]
        public void A_verified_apply_with_no_rollback_is_committed_verified()
        {
            Assert.Equal(DimensionPlanRules.StateCommittedVerified,
                DimensionPlanRules.FinalState(true, new string[0]));
            Assert.Equal(DimensionPlanRules.StateCommittedVerified,
                DimensionPlanRules.FinalState(true, null));
        }

        [Fact]
        public void A_verified_claim_standing_next_to_a_rollback_is_a_contradiction_and_reads_uncertain()
        {
            Assert.Equal(DimensionPlanRules.StateUncertain,
                DimensionPlanRules.FinalState(true, new[] { "RolledBack" }));
        }

        [Fact]
        public void Every_rollback_confirmed_reads_rolled_back()
        {
            Assert.Equal(DimensionPlanRules.StateRolledBack,
                DimensionPlanRules.FinalState(false, new[] { "RolledBack" }));
            Assert.Equal(DimensionPlanRules.StateRolledBack,
                DimensionPlanRules.FinalState(false, new[] { "RolledBack", "RolledBack" }));
        }

        [Theory]
        [InlineData("Pending")]
        [InlineData("Error")]
        [InlineData("Committed")]
        [InlineData("rolledback")]   // case matters: Revit's own spelling or nothing
        [InlineData("")]
        [InlineData(null)]
        public void Any_unconfirmed_rollback_keeps_its_uncertainty(string status)
        {
            Assert.Equal(DimensionPlanRules.StateUncertain,
                DimensionPlanRules.FinalState(false, new[] { status }));
            // ...even when it stands beside a confirmed one: one unconfirmed rollback
            // poisons the claim over the whole call.
            Assert.Equal(DimensionPlanRules.StateUncertain,
                DimensionPlanRules.FinalState(false, new[] { "RolledBack", status }));
        }

        [Fact]
        public void Not_verified_and_no_rollback_attempted_is_uncertain_not_clean()
        {
            Assert.Equal(DimensionPlanRules.StateUncertain, DimensionPlanRules.FinalState(false, new string[0]));
            Assert.Equal(DimensionPlanRules.StateUncertain, DimensionPlanRules.FinalState(false, null));
        }

        [Fact]
        public void The_published_state_names_are_the_contracts_words()
        {
            Assert.Equal("committed_verified", DimensionPlanRules.StateCommittedVerified);
            Assert.Equal("rolled_back", DimensionPlanRules.StateRolledBack);
            Assert.Equal("refused", DimensionPlanRules.StateRefused);
            Assert.Equal("stale_plan", DimensionPlanRules.StateStalePlan);
            Assert.Equal("uncertain", DimensionPlanRules.StateUncertain);
        }

        // ---- the no-API refusal texts ----------------------------------------

        [Fact]
        public void The_year_gated_refusal_names_the_api_the_year_and_the_python_impossibility()
        {
            string text = DimensionPlanRules.NoApiThisYear("radial_dimension", "RadialDimension.Create", 2025, "2023");
            Assert.Contains("RadialDimension.Create", text);
            Assert.Contains("2025", text);
            Assert.Contains("Revit 2023", text);
            Assert.Contains("Python", text);
            Assert.Contains("Nothing was written", text);
            // The wording must make the no-fallback decision legible: Python fails
            // the same way, so no script is the next step.
            Assert.Contains("no", text);
        }

        [Fact]
        public void The_no_api_anywhere_refusal_says_python_cannot_either()
        {
            string text = DimensionPlanRules.NoApiAnyYear("spot_slope");
            Assert.Contains("spot_slope", text);
            Assert.Contains("Python", text);
            Assert.Contains("Nothing was written", text);
        }
    }
}
