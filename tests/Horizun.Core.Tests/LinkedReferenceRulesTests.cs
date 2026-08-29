// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// The Revit-free rules behind dimensioning into an RVT link. Each block pins a
// way a linked dimension could be created against something the caller never
// approved:
//
//   * the three ids are not interchangeable, and a request that names one
//     placement twice cannot be told apart later - refusing beats guessing;
//   * a transform is geometry: it must fingerprint on the SAME 0.1 mm grid, so
//     regeneration jitter keeps the identity and a real nudge changes it;
//   * a mirrored link is not the same placement as an unmirrored one even when
//     every basis component quantises alike, so handedness is IN the hash;
//   * drift has an order - an instance that is gone must not be reported as a
//     moved transform, because the two have different fixes;
//   * a half-read transform must throw rather than hash, or the plan hands out
//     a stable-looking identity for a placement nobody measured.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class LinkedReferenceRulesTests
    {
        private const double FeetPerMm = 1.0 / 304.8;

        private static LinkTransformFacts Placement(double xFeet = 0, double yFeet = 0, double zFeet = 0,
                                                    bool mirrored = false, bool rotated = false)
        {
            return new LinkTransformFacts
            {
                Origin = new[] { xFeet, yFeet, zFeet },
                BasisX = rotated ? new[] { 0.0, 1.0, 0.0 } : new[] { 1.0, 0.0, 0.0 },
                BasisY = rotated ? new[] { -1.0, 0.0, 0.0 } : new[] { 0.0, 1.0, 0.0 },
                BasisZ = new[] { 0.0, 0.0, 1.0 },
                Determinant = mirrored ? -1.0 : 1.0,
                IsIdentity = !rotated && !mirrored && xFeet == 0 && yFeet == 0 && zFeet == 0,
                HasRotation = rotated || mirrored
            };
        }

        private static LinkBinding Binding(string transformFingerprint,
                                           string instanceUid = "inst-1",
                                           string documentIdentity = "MOD_EST|abcdef0123456789",
                                           string linkedUid = "elem-1")
        {
            return new LinkBinding
            {
                LinkInstanceId = 880011,
                InstanceUniqueId = instanceUid,
                LinkTypeId = 880002,
                LinkName = "MOD_EST_A.rvt",
                DocumentTitle = "MOD_EST_A",
                DocumentIdentity = documentIdentity,
                LinkedElementId = 211001,
                LinkedElementUniqueId = linkedUid,
                TransformFingerprint = transformFingerprint
            };
        }

        // ---- request validation ----------------------------------------------

        [Fact]
        public void Null_linked_targets_is_simply_absent_not_an_error()
        {
            List<LinkTargetRequest> normalized;
            int total;
            Assert.Null(LinkedReferenceRules.ValidateLinkTargets(null, out normalized, out total));
            Assert.Empty(normalized);
            Assert.Equal(0, total);
        }

        [Fact]
        public void The_same_link_instance_named_twice_is_refused_rather_than_merged_silently()
        {
            var a = new LinkTargetRequest { LinkInstanceId = 880011 };
            a.LinkedElementIds.Add(1);
            var b = new LinkTargetRequest { LinkInstanceId = 880011 };
            b.LinkedElementIds.Add(2);

            List<LinkTargetRequest> normalized;
            int total;
            string error = LinkedReferenceRules.ValidateLinkTargets(new[] { a, b }, out normalized, out total);

            Assert.NotNull(error);
            Assert.Contains("880011", error);
            Assert.Contains("more than once", error);
        }

        [Fact]
        public void An_entry_with_no_linked_element_ids_is_refused_naming_its_instance()
        {
            var empty = new LinkTargetRequest { LinkInstanceId = 4242 };

            List<LinkTargetRequest> normalized;
            int total;
            string error = LinkedReferenceRules.ValidateLinkTargets(new[] { empty }, out normalized, out total);

            Assert.NotNull(error);
            Assert.Contains("4242", error);
        }

        [Fact]
        public void Duplicate_linked_ids_inside_one_entry_collapse_and_the_total_counts_the_collapsed_set()
        {
            var entry = new LinkTargetRequest { LinkInstanceId = 7 };
            entry.LinkedElementIds.AddRange(new long[] { 11, 12, 11, 12, 13 });

            List<LinkTargetRequest> normalized;
            int total;
            Assert.Null(LinkedReferenceRules.ValidateLinkTargets(new[] { entry }, out normalized, out total));

            Assert.Single(normalized);
            Assert.Equal(new long[] { 11, 12, 13 }, normalized[0].LinkedElementIds.ToArray());
            Assert.Equal(3, total);
        }

        [Fact]
        public void More_link_instances_than_the_limit_is_refused_with_the_limit_named()
        {
            var entries = new List<LinkTargetRequest>();
            for (int i = 0; i <= LinkedReferenceRules.MaxLinkTargets; i++)
            {
                var e = new LinkTargetRequest { LinkInstanceId = 1000 + i };
                e.LinkedElementIds.Add(1);
                entries.Add(e);
            }

            List<LinkTargetRequest> normalized;
            int total;
            string error = LinkedReferenceRules.ValidateLinkTargets(entries, out normalized, out total);

            Assert.NotNull(error);
            Assert.Contains(LinkedReferenceRules.MaxLinkTargets.ToString(), error);
        }

        // ---- transform fingerprinting ----------------------------------------

        [Fact]
        public void The_same_placement_fingerprints_the_same_twice()
        {
            string a = LinkedReferenceRules.TransformFingerprint(Placement(10, 20, 0));
            string b = LinkedReferenceRules.TransformFingerprint(Placement(10, 20, 0));
            Assert.Equal(a, b);
        }

        [Fact]
        public void Regeneration_jitter_below_the_grid_keeps_the_identity()
        {
            // 0.01 mm - a tenth of the grid step, far below anything a person would
            // call "the link moved".
            double jitter = 0.01 * FeetPerMm;
            string still = LinkedReferenceRules.TransformFingerprint(Placement(10, 20, 0));
            string jittered = LinkedReferenceRules.TransformFingerprint(Placement(10 + jitter, 20, 0));
            Assert.Equal(still, jittered);
        }

        [Fact]
        public void A_real_move_of_a_millimetre_changes_the_identity()
        {
            string before = LinkedReferenceRules.TransformFingerprint(Placement(10, 20, 0));
            string after = LinkedReferenceRules.TransformFingerprint(Placement(10 + 1.0 * FeetPerMm, 20, 0));
            Assert.NotEqual(before, after);
        }

        [Fact]
        public void A_rotated_placement_and_a_translated_one_are_different_identities()
        {
            string translated = LinkedReferenceRules.TransformFingerprint(Placement(10, 20, 0));
            string rotated = LinkedReferenceRules.TransformFingerprint(Placement(10, 20, 0, rotated: true));
            Assert.NotEqual(translated, rotated);
        }

        [Fact]
        public void Handedness_is_inside_the_hash_so_a_mirrored_link_is_not_the_same_placement()
        {
            // Deliberately identical basis components: ONLY the determinant's sign
            // differs. Without handedness in the canonical form these two would hash
            // alike and a mirrored link would pass a stale check.
            LinkTransformFacts right = Placement(0, 0, 0);
            LinkTransformFacts left = Placement(0, 0, 0);
            left.Determinant = -1.0;

            Assert.Equal("right", right.Handedness);
            Assert.Equal("left", left.Handedness);
            Assert.True(left.HasReflection);
            Assert.False(right.HasReflection);
            Assert.NotEqual(LinkedReferenceRules.TransformFingerprint(right),
                            LinkedReferenceRules.TransformFingerprint(left));
        }

        [Fact]
        public void A_half_read_transform_throws_instead_of_hashing_something_nobody_measured()
        {
            LinkTransformFacts broken = Placement();
            broken.BasisY = null;
            Assert.Throws<ArgumentException>(() => LinkedReferenceRules.TransformFingerprint(broken));

            LinkTransformFacts nan = Placement();
            nan.Origin = new[] { double.NaN, 0.0, 0.0 };
            Assert.Throws<ArgumentException>(() => LinkedReferenceRules.TransformFingerprint(nan));

            LinkTransformFacts shortVector = Placement();
            shortVector.BasisZ = new[] { 0.0, 0.0 };
            Assert.Throws<ArgumentException>(() => LinkedReferenceRules.TransformFingerprint(shortVector));
        }

        // ---- document identity -------------------------------------------------

        [Fact]
        public void Document_identity_carries_the_title_in_clear_and_the_path_only_as_a_hash()
        {
            string identity = LinkedReferenceRules.DocumentIdentity(
                "MOD_EST_A", @"C:\Users\someone\Projects\Client Name\MOD_EST_A.rvt");

            Assert.StartsWith("MOD_EST_A|", identity);
            Assert.DoesNotContain("someone", identity);
            Assert.DoesNotContain("Client Name", identity);
            Assert.DoesNotContain(@"\", identity);
        }

        [Fact]
        public void Two_different_paths_under_the_same_title_are_different_identities()
        {
            string a = LinkedReferenceRules.DocumentIdentity("MOD_EST_A", @"C:\a\MOD_EST_A.rvt");
            string b = LinkedReferenceRules.DocumentIdentity("MOD_EST_A", @"C:\b\MOD_EST_A.rvt");
            Assert.NotEqual(a, b);
        }

        [Fact]
        public void A_link_with_no_path_still_has_an_identity_and_says_so()
        {
            string identity = LinkedReferenceRules.DocumentIdentity("MOD_EST_A", null);
            Assert.Equal("MOD_EST_A|no-path", identity);
        }

        // ---- drift detection ---------------------------------------------------

        [Fact]
        public void An_unmoved_placement_reports_no_drift()
        {
            string f = LinkedReferenceRules.TransformFingerprint(Placement(10, 20, 0));
            Assert.Null(LinkedReferenceRules.DetectDrift(Binding(f), Binding(f)));
        }

        [Fact]
        public void A_moved_transform_is_reported_as_link_transform_moved()
        {
            string before = LinkedReferenceRules.TransformFingerprint(Placement(10, 20, 0));
            string after = LinkedReferenceRules.TransformFingerprint(Placement(11, 20, 0));

            string code = LinkedReferenceRules.DetectDrift(Binding(before), Binding(after));

            Assert.Equal(LinkedReferenceRules.CodeLinkTransformMoved, code);
            string message = LinkedReferenceRules.DriftMessage(code, Binding(before), Binding(after));
            Assert.Contains("THE MODEL MOVED AFTER THE DRY RUN", message);
            Assert.Contains("880011", message);
        }

        [Fact]
        public void A_vanished_instance_is_reported_as_the_instance_not_as_a_moved_transform()
        {
            // The transform ALSO differs. The instance is the more fundamental fact and
            // must win, because "reload the link" and "the link is gone" are different
            // instructions to the person reading the refusal.
            string before = LinkedReferenceRules.TransformFingerprint(Placement(10, 20, 0));
            string after = LinkedReferenceRules.TransformFingerprint(Placement(11, 20, 0));

            string code = LinkedReferenceRules.DetectDrift(
                Binding(before, instanceUid: "inst-1"),
                Binding(after, instanceUid: "inst-2"));

            Assert.Equal(LinkedReferenceRules.CodeLinkInstanceChanged, code);
        }

        [Fact]
        public void A_missing_current_binding_is_the_instance_changing_not_a_pass()
        {
            string f = LinkedReferenceRules.TransformFingerprint(Placement());
            Assert.Equal(LinkedReferenceRules.CodeLinkInstanceChanged,
                         LinkedReferenceRules.DetectDrift(Binding(f), null));
        }

        [Fact]
        public void A_relinked_file_under_the_same_instance_is_reported_as_the_document_changing()
        {
            string f = LinkedReferenceRules.TransformFingerprint(Placement());
            string code = LinkedReferenceRules.DetectDrift(
                Binding(f, documentIdentity: "MOD_EST|1111111111111111"),
                Binding(f, documentIdentity: "MOD_EST|2222222222222222"));

            Assert.Equal(LinkedReferenceRules.CodeLinkedDocumentChanged, code);
        }

        [Fact]
        public void A_replaced_linked_element_is_reported_before_a_moved_transform()
        {
            string before = LinkedReferenceRules.TransformFingerprint(Placement(10, 0, 0));
            string after = LinkedReferenceRules.TransformFingerprint(Placement(12, 0, 0));

            string code = LinkedReferenceRules.DetectDrift(
                Binding(before, linkedUid: "elem-1"),
                Binding(after, linkedUid: "elem-9"));

            Assert.Equal(LinkedReferenceRules.CodeLinkedElementChanged, code);
        }

        [Fact]
        public void A_null_planned_binding_never_drifts_because_a_host_reference_has_no_link_to_move()
        {
            Assert.Null(LinkedReferenceRules.DetectDrift(null, null));
            Assert.Null(LinkedReferenceRules.DetectDrift(null, Binding("anything")));
        }

        [Fact]
        public void Every_drift_code_has_a_sentence_and_an_unknown_code_has_none()
        {
            string f = LinkedReferenceRules.TransformFingerprint(Placement());
            foreach (string code in new[]
            {
                LinkedReferenceRules.CodeLinkInstanceChanged,
                LinkedReferenceRules.CodeLinkedDocumentChanged,
                LinkedReferenceRules.CodeLinkedElementChanged,
                LinkedReferenceRules.CodeLinkTransformMoved
            })
            {
                string message = LinkedReferenceRules.DriftMessage(code, Binding(f), Binding(f));
                Assert.False(string.IsNullOrWhiteSpace(message), code + " has no sentence.");
            }
            Assert.Null(LinkedReferenceRules.DriftMessage("something_else", Binding(f), Binding(f)));
        }

        // ---- refusal sentences -------------------------------------------------

        [Fact]
        public void Every_refusal_names_the_ids_a_reader_needs_to_act_on_it()
        {
            Assert.Contains("880011", LinkedReferenceRules.NotALinkInstance(880011));

            string unloaded = LinkedReferenceRules.LinkUnloaded(880011, "MOD_EST_A.rvt", "Unloaded");
            Assert.Contains("880011", unloaded);
            Assert.Contains("MOD_EST_A.rvt", unloaded);
            Assert.Contains("Unloaded", unloaded);

            string missing = LinkedReferenceRules.LinkedElementMissing(880011, 211001, "MOD_EST_A");
            Assert.Contains("211001", missing);
            Assert.Contains("MOD_EST_A", missing);

            string nested = LinkedReferenceRules.NestedLinkNotSupported(880011, 211001);
            Assert.Contains("CreateLinkReference", nested);

            Assert.Contains("211001", LinkedReferenceRules.LinkedElementIsType(880011, 211001));
            Assert.Contains("211001", LinkedReferenceRules.LinkReferenceNotCreatable(880011, 211001, "why"));
            Assert.Contains("211001", LinkedReferenceRules.LinkReferenceUnreadable(880011, 211001, "why"));
            Assert.Contains("880011", LinkedReferenceRules.LinkDocumentUnavailable(880011, "MOD_EST_A.rvt"));
        }

        [Fact]
        public void An_unnamed_link_still_produces_a_readable_sentence()
        {
            string unloaded = LinkedReferenceRules.LinkUnloaded(5, null, null);
            Assert.Contains("an unnamed link", unloaded);
            Assert.Contains("not loaded", unloaded);
        }

        // ---- ordering ----------------------------------------------------------

        [Fact]
        public void Host_rows_sort_before_linked_rows_and_linked_rows_sort_by_instance_then_element()
        {
            LinkBinding host = null;
            LinkBinding first = new LinkBinding { LinkInstanceId = 100, LinkedElementId = 5 };
            LinkBinding sameInstanceLaterElement = new LinkBinding { LinkInstanceId = 100, LinkedElementId = 9 };
            LinkBinding laterInstance = new LinkBinding { LinkInstanceId = 200, LinkedElementId = 1 };

            Assert.True(LinkedReferenceRules.CompareProvenance(host, first) < 0);
            Assert.True(LinkedReferenceRules.CompareProvenance(first, host) > 0);
            Assert.Equal(0, LinkedReferenceRules.CompareProvenance(host, null));
            Assert.True(LinkedReferenceRules.CompareProvenance(first, sameInstanceLaterElement) < 0);
            Assert.True(LinkedReferenceRules.CompareProvenance(sameInstanceLaterElement, laterInstance) < 0);
        }

        [Fact]
        public void The_provenance_order_is_a_total_order_over_a_mixed_answer()
        {
            var rows = new List<LinkBinding>
            {
                new LinkBinding { LinkInstanceId = 200, LinkedElementId = 1 },
                null,
                new LinkBinding { LinkInstanceId = 100, LinkedElementId = 9 },
                new LinkBinding { LinkInstanceId = 100, LinkedElementId = 5 }
            };

            rows.Sort((a, b) => LinkedReferenceRules.CompareProvenance(a, b));

            Assert.Null(rows[0]);
            Assert.Equal(100, rows[1].LinkInstanceId);
            Assert.Equal(5, rows[1].LinkedElementId);
            Assert.Equal(100, rows[2].LinkInstanceId);
            Assert.Equal(9, rows[2].LinkedElementId);
            Assert.Equal(200, rows[3].LinkInstanceId);
        }
    }
}
