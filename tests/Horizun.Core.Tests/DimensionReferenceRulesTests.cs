// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// The Revit-free rules behind horizun_get_dimension_references. Each block pins
// a way the command could quietly answer the wrong question:
//
//   * an unknown selector silently dropped looks like a selector applied;
//   * nearest/farthest without a probe point is "nearest to WHERE?" - refusing
//     is the only honest answer, and inventing a default point is not;
//   * a non-deterministic order makes two identical calls disagree, which reads
//     as model drift that never happened;
//   * a truncated page without an exact total is a partial answer wearing the
//     shape of a whole one;
//   * a fingerprint that shifts under field reordering, or under sub-noise
//     jitter, would flag drift on every regeneration - and one that misses a
//     real 0.2 mm move would approve a reference that moved;
//   * choosing one of two equivalent candidates is a guess presented as an
//     answer - both must come back, marked, in one group.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class DimensionReferenceRulesTests
    {
        private const double FeetPerMm = 1.0 / 304.8;

        // ---- selector parsing ------------------------------------------------

        [Fact]
        public void Unknown_selector_is_refused_naming_the_known_ones()
        {
            List<string> selectors;
            string error;
            bool ok = DimensionReferenceRules.TryParseSelectors(
                new[] { "centerline", "face_of_glory" }, out selectors, out error);

            Assert.False(ok);
            Assert.Null(selectors);
            Assert.Contains("face_of_glory", error);
            // The refusal must TEACH: every known selector is named, so the caller
            // can fix the request without a second round trip.
            foreach (string known in DimensionReferenceRules.KnownSelectors)
                Assert.Contains(known, error);
        }

        [Fact]
        public void Selectors_parse_case_insensitively_to_canonical_names_and_deduplicate()
        {
            List<string> selectors;
            string error;
            bool ok = DimensionReferenceRules.TryParseSelectors(
                new[] { "CENTERLINE", " centerline ", "Edge" }, out selectors, out error);

            Assert.True(ok);
            Assert.Null(error);
            Assert.Equal(new[] { "centerline", "edge" }, selectors);
        }

        [Fact]
        public void Absent_selectors_mean_defaults_and_an_explicitly_empty_list_is_refused()
        {
            List<string> selectors;
            string error;
            Assert.True(DimensionReferenceRules.TryParseSelectors(null, out selectors, out error));
            Assert.Null(selectors); // null = "use the per-element defaults"
            Assert.Null(error);

            Assert.False(DimensionReferenceRules.TryParseSelectors(new string[0], out selectors, out error));
            Assert.Contains("at least one selector", error);
        }

        // ---- probe point -----------------------------------------------------

        [Theory]
        [InlineData("nearest_face")]
        [InlineData("farthest_face")]
        public void Nearest_and_farthest_require_a_probe_point(string selector)
        {
            Assert.True(DimensionReferenceRules.RequiresProbePoint(new[] { selector }));

            string error = DimensionReferenceRules.ValidateProbeRequirement(new[] { selector }, probeProvided: false);
            Assert.NotNull(error);
            Assert.Contains("probe_point", error);
            Assert.Contains(selector, error);
            // The refusal must also say nothing was done - the point of refusing
            // early is that there is nothing half-inspected to wonder about.
            Assert.Contains("nothing was inspected", error, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void A_provided_probe_point_or_a_selector_set_without_nearest_needs_no_probe()
        {
            Assert.Null(DimensionReferenceRules.ValidateProbeRequirement(new[] { "nearest_face" }, probeProvided: true));
            Assert.Null(DimensionReferenceRules.ValidateProbeRequirement(new[] { "centerline", "edge" }, probeProvided: false));
            Assert.Null(DimensionReferenceRules.ValidateProbeRequirement(null, probeProvided: false));
            Assert.False(DimensionReferenceRules.RequiresProbePoint(new[] { "centerline" }));
            Assert.False(DimensionReferenceRules.RequiresProbePoint(null));
        }

        // ---- element_ids XOR filter -----------------------------------------

        [Fact]
        public void Exactly_one_of_element_ids_or_filter_is_required()
        {
            Assert.NotNull(DimensionReferenceRules.ValidateTargetChoice(hasElementIds: true, hasFilter: true));
            Assert.NotNull(DimensionReferenceRules.ValidateTargetChoice(hasElementIds: false, hasFilter: false));
            Assert.Null(DimensionReferenceRules.ValidateTargetChoice(hasElementIds: true, hasFilter: false));
            Assert.Null(DimensionReferenceRules.ValidateTargetChoice(hasElementIds: false, hasFilter: true));
        }

        [Fact]
        public void Target_counts_are_bounded_at_200_and_the_errors_say_so()
        {
            Assert.NotNull(DimensionReferenceRules.ValidateElementIdCount(0));
            Assert.Null(DimensionReferenceRules.ValidateElementIdCount(1));
            Assert.Null(DimensionReferenceRules.ValidateElementIdCount(DimensionReferenceRules.MaxTargets));

            string tooMany = DimensionReferenceRules.ValidateElementIdCount(DimensionReferenceRules.MaxTargets + 1);
            Assert.NotNull(tooMany);
            Assert.Contains("200", tooMany);

            string filterError = DimensionReferenceRules.FilterTooBroadError(345);
            Assert.Contains("345", filterError);
            Assert.Contains("200", filterError);
            Assert.Contains("Nothing was inspected", filterError);
        }

        // ---- deterministic ordering -----------------------------------------

        private static CandidateKey Key(long id, string selector, string refType, string fingerprint,
                                        string stable = "")
            => new CandidateKey
            {
                ElementId = id, Selector = selector, ReferenceType = refType,
                Fingerprint = fingerprint, StableRepresentation = stable
            };

        [Fact]
        public void The_same_candidates_sort_the_same_whatever_order_they_arrived_in()
        {
            var expected = new List<CandidateKey>
            {
                Key(10, "centerline", "centerline", "aaa"),
                Key(10, "edge", "edge", "bbb"),
                Key(10, "edge", "edge", "ccc"),
                Key(10, "exterior_face", "face", "aaa"),
                Key(42, "centerline", "centerline", "zzz"),
            };

            // Two different arrival orders - a collector re-enumerating is exactly
            // the kind of thing that must not reorder an answer.
            var arrivalA = new List<CandidateKey> { expected[3], expected[0], expected[4], expected[2], expected[1] };
            var arrivalB = new List<CandidateKey> { expected[4], expected[2], expected[1], expected[0], expected[3] };
            arrivalA.Sort(DimensionReferenceRules.CompareCandidates);
            arrivalB.Sort(DimensionReferenceRules.CompareCandidates);

            for (int i = 0; i < expected.Count; i++)
            {
                Assert.Same(expected[i], arrivalA[i]);
                Assert.Same(expected[i], arrivalB[i]);
            }
        }

        [Fact]
        public void Coincident_geometry_falls_back_to_the_stable_representation_tiebreak()
        {
            CandidateKey first = Key(7, "edge", "edge", "same", "ref-a");
            CandidateKey second = Key(7, "edge", "edge", "same", "ref-b");
            Assert.True(DimensionReferenceRules.CompareCandidates(first, second) < 0);
            Assert.True(DimensionReferenceRules.CompareCandidates(second, first) > 0);
        }

        // ---- paging and truncation ------------------------------------------

        [Fact]
        public void Truncation_is_declared_beside_the_exact_total()
        {
            PageSlice firstPage = DimensionReferenceRules.Page(total: 250, maxResults: 100, offset: 0);
            Assert.Equal(250, firstPage.Total);
            Assert.Equal(100, firstPage.Count);
            Assert.True(firstPage.Truncated);

            PageSlice lastPage = DimensionReferenceRules.Page(total: 250, maxResults: 100, offset: 200);
            Assert.Equal(250, lastPage.Total);
            Assert.Equal(50, lastPage.Count);
            Assert.False(lastPage.Truncated);

            // Past the end: an empty page, not an error, and the exact total still
            // tells the caller where the rows stopped.
            PageSlice pastTheEnd = DimensionReferenceRules.Page(total: 250, maxResults: 100, offset: 300);
            Assert.Equal(0, pastTheEnd.Count);
            Assert.False(pastTheEnd.Truncated);
            Assert.Equal(250, pastTheEnd.Total);

            PageSlice empty = DimensionReferenceRules.Page(total: 0, maxResults: 100, offset: 0);
            Assert.Equal(0, empty.Count);
            Assert.False(empty.Truncated);
        }

        [Fact]
        public void Paging_arguments_are_refused_out_loud_not_clamped_quietly()
        {
            int max, off;
            Assert.Null(DimensionReferenceRules.ValidatePaging(null, null, out max, out off));
            Assert.Equal(DimensionReferenceRules.DefaultResults, max);
            Assert.Equal(0, off);

            Assert.NotNull(DimensionReferenceRules.ValidatePaging(0, null, out max, out off));
            Assert.NotNull(DimensionReferenceRules.ValidatePaging(501, null, out max, out off));
            Assert.NotNull(DimensionReferenceRules.ValidatePaging(100, -1, out max, out off));
            Assert.Null(DimensionReferenceRules.ValidatePaging(500, 0, out max, out off));
            Assert.Null(DimensionReferenceRules.ValidatePaging(1, 12345, out max, out off));
        }

        // ---- the geometry fingerprint ---------------------------------------

        [Fact]
        public void Fingerprint_is_stable_under_field_reordering()
        {
            string forward = DimensionReferenceRules.GeometryFingerprint(new GeometryFacts()
                .Add("kind", "plane")
                .AddXyz("normal", 0.0, 0.0, 1.0)
                .Add("offset", 0.33)
                .Add("area", 12.5));
            string reversed = DimensionReferenceRules.GeometryFingerprint(new GeometryFacts()
                .Add("area", 12.5)
                .Add("offset", 0.33)
                .AddXyz("normal", 0.0, 0.0, 1.0)
                .Add("kind", "plane"));

            Assert.Equal(forward, reversed);
            Assert.Equal(64, forward.Length); // SHA-256 hex
            Assert.True(forward.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')));
        }

        [Fact]
        public void Fingerprint_moves_with_a_0_2_mm_change_and_holds_through_0_01_mm_noise()
        {
            // 0.33 ft sits mid-cell on the 0.1 mm grid (0.33 * 3048 = 1005.84), so the
            // noise case is not straddling a rounding boundary by accident.
            const double baseOffsetFeet = 0.33;
            string baseline = FingerprintWithOffset(baseOffsetFeet);

            string moved = FingerprintWithOffset(baseOffsetFeet + 0.2 * FeetPerMm);
            Assert.NotEqual(baseline, moved);

            string jittered = FingerprintWithOffset(baseOffsetFeet + 0.01 * FeetPerMm);
            Assert.Equal(baseline, jittered);
        }

        private static string FingerprintWithOffset(double offsetFeet)
            => DimensionReferenceRules.GeometryFingerprint(new GeometryFacts()
                .Add("kind", "plane")
                .AddXyz("normal", 0.0, 0.0, 1.0)
                .Add("offset", offsetFeet));

        [Fact]
        public void Fingerprint_refuses_the_inputs_that_would_make_it_a_lie()
        {
            // Zero facts: every empty candidate would share one identity.
            Assert.Throws<ArgumentException>(() =>
                DimensionReferenceRules.GeometryFingerprint(new GeometryFacts()));
            Assert.Throws<ArgumentException>(() =>
                DimensionReferenceRules.GeometryFingerprint(null));
            // A duplicate name would let the second value silently shadow the first.
            Assert.Throws<ArgumentException>(() => new GeometryFacts().Add("x", 1.0).Add("x", 2.0));
            // NaN is "never measured", and an identity for it would be believed.
            Assert.Throws<ArgumentException>(() => new GeometryFacts().Add("x", double.NaN));
            Assert.Throws<ArgumentException>(() => new GeometryFacts().Add("x", double.PositiveInfinity));
        }

        [Fact]
        public void Quantisation_is_the_documented_0_1_mm_grid()
        {
            Assert.Equal(3048, DimensionReferenceRules.QuantizeFeet(1.0));       // 1 ft = 304.8 mm = 3048 ticks
            Assert.Equal(-3048, DimensionReferenceRules.QuantizeFeet(-1.0));
            Assert.Equal(0, DimensionReferenceRules.QuantizeFeet(0.0));
            Assert.Equal(1, DimensionReferenceRules.QuantizeFeet(0.1 * FeetPerMm)); // one tick = 0.1 mm
        }

        // ---- ambiguity -------------------------------------------------------

        [Fact]
        public void Several_answers_to_a_single_answer_selector_are_all_marked_in_one_group()
        {
            bool ambiguous;
            string group;
            DimensionReferenceRules.ShapeAmbiguity("centerline", 123, candidateCount: 2,
                                                   out ambiguous, out group);
            Assert.True(ambiguous);
            Assert.Equal("123:centerline", group);
        }

        [Fact]
        public void One_answer_is_never_ambiguous_and_enumerating_selectors_never_are()
        {
            bool ambiguous;
            string group;
            DimensionReferenceRules.ShapeAmbiguity("centerline", 123, candidateCount: 1,
                                                   out ambiguous, out group);
            Assert.False(ambiguous);
            Assert.Null(group);

            // Five edges is the expected shape of the answer, not a tie to disclose.
            DimensionReferenceRules.ShapeAmbiguity("edge", 123, candidateCount: 5,
                                                   out ambiguous, out group);
            Assert.False(ambiguous);
            Assert.Null(group);
        }

        [Fact]
        public void Single_answer_selectors_are_exactly_the_ones_whose_question_names_one_reference()
        {
            Assert.True(DimensionReferenceRules.ExpectsSingleAnswer("centerline"));
            Assert.True(DimensionReferenceRules.ExpectsSingleAnswer("exterior_face"));
            Assert.True(DimensionReferenceRules.ExpectsSingleAnswer("interior_face"));
            Assert.True(DimensionReferenceRules.ExpectsSingleAnswer("nearest_face"));
            Assert.True(DimensionReferenceRules.ExpectsSingleAnswer("farthest_face"));
            Assert.False(DimensionReferenceRules.ExpectsSingleAnswer("edge"));
            Assert.False(DimensionReferenceRules.ExpectsSingleAnswer("endpoint"));
            Assert.False(DimensionReferenceRules.ExpectsSingleAnswer("grid"));
            Assert.False(DimensionReferenceRules.ExpectsSingleAnswer("level"));
            Assert.False(DimensionReferenceRules.ExpectsSingleAnswer("reference_plane"));
        }

        [Fact]
        public void Distance_ties_within_0_1_mm_are_returned_whole_never_decided()
        {
            // Two faces 0.05 mm apart in distance are the same answer; the third is not.
            var distances = new List<double> { 1.0, 1.0 + 0.05 * FeetPerMm, 2.0 };

            List<int> nearest = DimensionReferenceRules.TiedIndices(distances, farthest: false);
            Assert.Equal(new[] { 0, 1 }, nearest);

            List<int> farthest = DimensionReferenceRules.TiedIndices(distances, farthest: true);
            Assert.Equal(new[] { 2 }, farthest);

            Assert.Empty(DimensionReferenceRules.TiedIndices(new List<double>(), farthest: false));
            Assert.Empty(DimensionReferenceRules.TiedIndices(null, farthest: false));
        }

        // ---- incompatibility codes ------------------------------------------

        [Fact]
        public void The_wire_codes_are_pinned_so_the_contract_cannot_drift_silently()
        {
            Assert.Equal("non_planar_face", DimensionReferenceRules.CodeNonPlanarFace);
            Assert.Equal("no_stable_centerline", DimensionReferenceRules.CodeNoStableCenterline);
            Assert.Equal("unsupported_edge_curve", DimensionReferenceRules.CodeUnsupportedEdgeCurve);
            Assert.Equal("link_references_not_supported", DimensionReferenceRules.CodeLinkReferencesNotSupported);
            Assert.Equal("selector_not_applicable", DimensionReferenceRules.WarningSelectorNotApplicable);
            Assert.Equal("view_geometry_fallback", DimensionReferenceRules.WarningViewGeometryFallback);
        }

        [Fact]
        public void Planar_faces_and_line_or_arc_edges_carry_dimensions_and_the_rest_say_why_not()
        {
            Assert.Null(DimensionReferenceRules.ClassifyForDimension("face", "plane"));
            Assert.Null(DimensionReferenceRules.ClassifyForDimension("edge", "line"));
            Assert.Null(DimensionReferenceRules.ClassifyForDimension("edge", "arc")); // radial / arc-length
            Assert.Null(DimensionReferenceRules.ClassifyForDimension("endpoint", "point"));
            Assert.Null(DimensionReferenceRules.ClassifyForDimension("centerline", "line"));
            Assert.Null(DimensionReferenceRules.ClassifyForDimension("grid", "line"));
            Assert.Null(DimensionReferenceRules.ClassifyForDimension("level", "plane"));
            Assert.Null(DimensionReferenceRules.ClassifyForDimension("reference_plane", "plane"));

            IncompatibilityReason curved = DimensionReferenceRules.ClassifyForDimension("face", "cylindrical_face");
            Assert.Equal(DimensionReferenceRules.CodeNonPlanarFace, curved.Code);
            Assert.Contains("cylindrical_face", curved.Message);

            IncompatibilityReason spline = DimensionReferenceRules.ClassifyForDimension("edge", "nurb_spline");
            Assert.Equal(DimensionReferenceRules.CodeUnsupportedEdgeCurve, spline.Code);

            IncompatibilityReason noCenterline = DimensionReferenceRules.NoStableCenterline(null);
            Assert.Equal(DimensionReferenceRules.CodeNoStableCenterline, noCenterline.Code);
            Assert.Contains("no stable centerline", noCenterline.Message, StringComparison.OrdinalIgnoreCase);

            Assert.Throws<ArgumentException>(() => new IncompatibilityReason("", "message without a code"));
        }

        /// <summary>
        /// Naming a link INSTANCE in element_ids is still not an answer - but the reason
        /// is no longer "links are unsupported". It is that the instance and an element
        /// inside it are different subjects, and the message has to hand the caller the
        /// call that WOULD work rather than a dead end.
        /// </summary>
        [Fact]
        public void A_link_instance_in_element_ids_is_redirected_to_linked_targets()
        {
            string message = DimensionReferenceRules.LinkReferencesMessage(4242);
            Assert.Contains("4242", message);
            Assert.Contains("linked_targets", message);
            Assert.Contains("link_instance_id", message);
            Assert.Contains("linked_element_ids", message);
            // The distinction the whole feature rests on: those ids are not host ids.
            Assert.Contains("LINKED document", message);
            // And it must not claim the instance itself was inspected.
            Assert.Contains("not inspected", message);
        }

        /// <summary>
        /// The measured 2026-08-26 split: linked GEOMETRY constructs, linked DATUMS are
        /// rejected by NewDimension itself. The code is what a client branches on and
        /// the message must carry both the measurement and the way out.
        /// </summary>
        [Fact]
        public void The_linked_datum_rejection_carries_its_code_and_the_measured_way_out()
        {
            IncompatibilityReason reason = DimensionReferenceRules.LinkedDatumRejected();
            Assert.Equal("linked_datum_rejected_by_dimension_api", reason.Code);
            Assert.Equal(DimensionReferenceRules.CodeLinkedDatumRejected, reason.Code);
            Assert.Contains("measured live", reason.Message);
            Assert.Contains("faces", reason.Message);
            Assert.Contains("Invalid number of references", reason.Message);
        }

        [Fact]
        public void Revit_2023_linked_geometry_limit_names_all_measured_arrangements_and_the_supported_years()
        {
            IncompatibilityReason reason = DimensionReferenceRules.LinkedGeometryRejectedByRevit2023();

            Assert.Equal("linked_geometry_rejected_by_revit_2023_dimension_api", reason.Code);
            Assert.Equal(DimensionReferenceRules.CodeLinkedGeometryRejectedByRevit2023, reason.Code);
            Assert.Contains("host+link", reason.Message);
            Assert.Contains("two faces of one linked wall", reason.Message);
            Assert.Contains("two link instances", reason.Message);
            Assert.Contains("Revit 2024+", reason.Message);
            Assert.Contains("Nothing was written", reason.Message);
        }

        // ---- applicability ---------------------------------------------------

        [Fact]
        public void Defaults_for_a_wall_cover_its_class_and_never_the_probe_selectors()
        {
            var wall = new ElementTraits
            {
                IsHostObject = true, HasLocationCurve = true, HasSolidGeometry = true
            };
            IReadOnlyList<string> defaults = DimensionReferenceRules.ApplicableSelectors(wall);

            Assert.Contains("centerline", defaults);
            Assert.Contains("exterior_face", defaults);
            Assert.Contains("interior_face", defaults);
            Assert.Contains("edge", defaults);
            Assert.Contains("endpoint", defaults);
            // nearest/farthest need a probe point; a default that fails without one
            // would make the no-arguments call unusable. They are opt-in by name.
            Assert.DoesNotContain("nearest_face", defaults);
            Assert.DoesNotContain("farthest_face", defaults);
            Assert.DoesNotContain("grid", defaults);
        }

        [Fact]
        public void Datum_elements_answer_only_their_own_selector()
        {
            Assert.Equal(new[] { "grid" },
                DimensionReferenceRules.ApplicableSelectors(new ElementTraits { IsGrid = true }));
            Assert.Equal(new[] { "level" },
                DimensionReferenceRules.ApplicableSelectors(new ElementTraits { IsLevel = true }));
            Assert.Equal(new[] { "reference_plane" },
                DimensionReferenceRules.ApplicableSelectors(new ElementTraits { IsReferencePlane = true }));
        }

        [Fact]
        public void A_selector_that_does_not_apply_is_named_in_the_warning_never_substituted()
        {
            var duct = new ElementTraits { HasLocationCurve = true, HasSolidGeometry = true };
            Assert.False(DimensionReferenceRules.SelectorApplies("exterior_face", duct));
            Assert.True(DimensionReferenceRules.SelectorApplies("centerline", duct));
            Assert.True(DimensionReferenceRules.SelectorApplies("nearest_face", duct)); // applies; probe is separate

            string why = DimensionReferenceRules.WhyNotApplicable("exterior_face", 99, duct);
            Assert.Contains("exterior_face", why);
            Assert.Contains("99", why);
            Assert.Contains("guess", why);
        }

        [Fact]
        public void An_element_with_no_traits_gets_an_empty_default_set_not_an_invented_one()
        {
            Assert.Empty(DimensionReferenceRules.ApplicableSelectors(new ElementTraits()));
            Assert.Empty(DimensionReferenceRules.ApplicableSelectors(null));
            Assert.False(DimensionReferenceRules.SelectorApplies("edge", null));
        }

        // ---- units -----------------------------------------------------------

        [Fact]
        public void Unit_scales_cover_mm_m_feet_and_refuse_the_rest()
        {
            double toFeet, fromFeet;
            Assert.True(DimensionReferenceRules.TryUnitScales("mm", out toFeet, out fromFeet));
            Assert.Equal(1.0, 304.8 * toFeet / 1.0, 10);
            Assert.Equal(304.8, fromFeet, 10);

            Assert.True(DimensionReferenceRules.TryUnitScales("m", out toFeet, out fromFeet));
            Assert.Equal(0.3048, fromFeet, 10);

            Assert.True(DimensionReferenceRules.TryUnitScales("feet", out toFeet, out fromFeet));
            Assert.Equal(1.0, toFeet, 10);
            Assert.Equal(1.0, fromFeet, 10);

            Assert.False(DimensionReferenceRules.TryUnitScales("inches", out toFeet, out fromFeet));
            Assert.False(DimensionReferenceRules.TryUnitScales(null, out toFeet, out fromFeet));
        }
    }
}
