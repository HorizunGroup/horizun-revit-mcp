// -----------------------------------------------------------------------------
// Revit-host wiring tests for horizun_split_multilayer_walls.
//
// The verifier, the executor and the collector cannot be constructed without a
// Revit Document, so the properties a code review actually found missing are
// pinned at source level here while the production assemblies are compiled
// against every supported Revit API.
//
// Each test below corresponds to a specific finding. They are deliberately
// literal: a test that says "the switch dispatches every registered kind" is
// worth having precisely because the failure it catches - somebody adding a
// dependency class and forgetting its verifier - reads as a passing build and a
// contract that quietly over-claims.
//
// WHAT THESE TESTS ARE NOT: they do not prove the verifiers are CORRECT against a
// real model. That is the live phase, and nothing here should be read as
// standing in for it.
// -----------------------------------------------------------------------------
using System;
using System.IO;
using System.Linq;
using Horizun.Contracts;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class WallSplitWiringTests
    {
        private static string RepoRoot()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null)
            {
                if (File.Exists(Path.Combine(d.FullName, "AGENTS.md")) &&
                    Directory.Exists(Path.Combine(d.FullName, "src", "Horizun.Revit")))
                    return d.FullName;
                d = d.Parent;
            }
            throw new InvalidOperationException("repository root not found");
        }

        private static string Source(string relative) =>
            File.ReadAllText(Path.Combine(RepoRoot(), relative.Replace('/', Path.DirectorySeparatorChar)));

        private static string Verifier() => Source("src/Horizun.Revit/Commands/WallSplitVerifier.cs");
        private static string Executor() => Source("src/Horizun.Revit/Commands/WallSplitExecutor.cs");
        private static string Facts() => Source("src/Horizun.Revit/Commands/WallSplitFacts.cs");

        // Ordinal, non-overlapping. Used where the point is HOW MANY reads survive, not
        // merely whether any do.
        private static int CountOf(string haystack, string needle)
        {
            int n = 0, i = 0;
            while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { n++; i += needle.Length; }
            return n;
        }
        private static string Types() => Source("src/Horizun.Revit/Commands/WallSplitTypes.cs");
        private static string Command() => Source("src/Horizun.Revit/Commands/SplitMultilayerWallsCommand.cs");

        // Source with every full-line // comment removed. Used wherever the assertion is
        // about a CALL existing rather than about words existing: commenting a line out
        // leaves its text in the file and satisfies a naive Contains.
        private static string CodeOnly(string source) =>
            string.Join("\n", source.Split('\n')
                .Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));

        // ---- P0-1: no preserved_by_identity without a verifier --------------------

        /// <summary>
        /// The dispatch switch ONLY. Written this way after the first version of this test
        /// was found vacuous: it asserted "case DependencyKinds.Tag:" appears in the file,
        /// and a SECOND switch - the one mapping kinds to failure codes - satisfied it. So
        /// deleting the real dispatch left the test green. The region is extracted first,
        /// and each kind is asserted together with the call it must make.
        /// </summary>
        private static string DispatchSwitch()
        {
            string verifier = Verifier();
            int start = verifier.IndexOf("switch (before.Kind)", StringComparison.Ordinal);
            Assert.True(start >= 0, "the dependency dispatch switch was not found at all");
            int end = verifier.IndexOf("private static string KindFailureCode", start, StringComparison.Ordinal);
            Assert.True(end > start, "the dispatch switch does not end where expected");
            return verifier.Substring(start, end - start);
        }

        [Fact]
        public void Every_registered_dependency_kind_is_dispatched_by_the_verifier()
        {
            // THE test the director asked for: adding a class to WithVerifier without
            // writing its verifier fails here rather than shipping as an assertion.
            string dispatch = DispatchSwitch();

            var expected = new (string Member, string Call)[]
            {
                ("FamilyInstance", "VerifyFamilyInstance("),
                ("Opening", "VerifyOpening("),
                ("WallSweep", "VerifySweep("),
                ("Reveal", "VerifySweep("),
                ("EmbeddedWall", "VerifyEmbeddedWall("),
                ("Dimension", "VerifyDimension("),
                ("Tag", "VerifyTag("),
                ("WallFoundation", "VerifyFoundation("),
                ("Rebar", "VerifyRebar("),
                ("RebarContainer", "VerifyReinforcementSystem("),
                ("AreaReinforcement", "VerifyReinforcementSystem("),
                ("PathReinforcement", "VerifyReinforcementSystem("),
                ("FabricArea", "VerifyReinforcementSystem("),
                ("FabricSheet", "VerifyReinforcementSystem(")
            };

            Assert.Equal(DependencyKinds.WithVerifier.Length, expected.Length);

            foreach ((string member, string call) in expected)
            {
                int label = dispatch.IndexOf("case DependencyKinds." + member + ":", StringComparison.Ordinal);
                Assert.True(label >= 0, member + " is registered as having a verifier but is not dispatched");

                // The call must follow this label before the next one, so a case that falls
                // through to nothing cannot pass.
                int nextCase = dispatch.IndexOf("case DependencyKinds.", label + 1, StringComparison.Ordinal);
                int nextDefault = dispatch.IndexOf("default:", label + 1, StringComparison.Ordinal);
                int limit = new[] { nextCase, nextDefault, dispatch.Length }
                    .Where(i => i > label).Min();

                string body = dispatch.Substring(label, limit - label);
                bool handled = body.Contains(call) ||
                               // Reveal and WallSweep legitimately share one verifier, so a
                               // label that only falls through to the next case is allowed
                               // as long as THAT case makes the call.
                               (body.Trim().EndsWith(":", StringComparison.Ordinal) &&
                                dispatch.Substring(label).Contains(call));

                Assert.True(handled, member + " is dispatched but never reaches " + call);
            }
        }

        [Fact]
        public void The_dispatch_switch_cannot_silently_succeed_on_an_unknown_kind()
        {
            string dispatch = DispatchSwitch();
            Assert.Contains("default:", dispatch);
            Assert.Contains("no verifier is registered for kind", dispatch);
        }

        [Fact]
        public void Each_dependency_kind_has_a_named_verify_method()
        {
            string verifier = Verifier();
            foreach (string method in new[]
                     {
                         "VerifyFamilyInstance", "VerifyOpening", "VerifySweep",
                         "VerifyEmbeddedWall", "VerifyDimension", "VerifyTag",
                         "VerifyFoundation", "VerifyRebar", "VerifyReinforcementSystem"
                     })
                Assert.Contains("private static string " + method + "(", verifier);
        }

        [Fact]
        public void The_collector_never_hands_out_a_disposition_of_its_own()
        {
            // The disposition is DERIVED, in one place. A literal assignment of
            // PreservedByIdentity in the collector would be a way round the coverage rule.
            string facts = Facts();
            Assert.Contains("Disposition = DependencyKinds.DispositionFor(kind)", facts);
            Assert.DoesNotContain("Disposition = DependencyDisposition.PreservedByIdentity", facts);
        }

        [Fact]
        public void An_unrecognised_dependency_blocks_the_wall_before_a_transaction()
        {
            string facts = Facts();
            Assert.Contains("return DependencyKinds.Unrecognised;", facts);
            Assert.Contains("WallSplitCodes.UnsupportedDependency", facts);
            // And the refusal happens in the READ pass, which runs before any transaction.
            Assert.Contains("before any transaction is opened rather than converted with a known loss", facts);
        }

        // ---- P0-2: joins are captured AND restored AND verified -------------------

        [Fact]
        public void Original_joins_are_captured_with_end_flags_and_elements_at_join()
        {
            string facts = Facts();
            Assert.Contains("WallUtils.IsWallJoinAllowedAtEnd(wall, 0)", facts);
            Assert.Contains("WallUtils.IsWallJoinAllowedAtEnd(wall, 1)", facts);
            Assert.Contains("get_ElementsAtJoin(0)", facts);
            Assert.Contains("get_ElementsAtJoin(1)", facts);
            Assert.Contains("JoinGeometryUtils.GetJoinedElements(doc, wall)", facts);
            Assert.Contains("IsCuttingElementInJoin", facts);
        }

        [Fact]
        public void Captured_joins_are_actually_restored_and_not_merely_recorded()
        {
            // The finding was that EndJoinIds were captured and never used. The restore has
            // to exist AND be called.
            string executor = Executor();
            Assert.Contains("private static Fail RestoreJoins(", executor);
            Assert.Contains("Fail joins = RestoreJoins(doc, carrier, approved.Joins);", executor);
            Assert.Contains("WallUtils.AllowWallJoinAtEnd", executor);
            Assert.Contains("WallUtils.DisallowWallJoinAtEnd", executor);
            Assert.Contains("SwitchJoinOrder", executor);
        }

        [Fact]
        public void A_join_that_cannot_be_restored_is_a_refusal_not_a_note()
        {
            string executor = Executor();
            string verifier = Verifier();
            Assert.Contains("WallSplitCodes.VerifyJoinNotRestored", executor);
            Assert.Contains("WallSplitCodes.VerifyJoinNotRestored", verifier);
            Assert.Contains("all_original_joins_restored", verifier);
        }

        [Fact]
        public void The_secondary_wall_join_policy_is_stated_rather_than_left_implicit()
        {
            Assert.Contains("secondary_wall_join_policy", Verifier());
            Assert.Contains("joined to the CARRIER only", Verifier());
        }

        // ---- P0-3: the whole FamilyInstance snapshot is compared ------------------

        [Fact]
        public void Every_captured_instance_property_is_compared()
        {
            string verifier = Verifier();
            foreach (string key in new[]
                     {
                         "mirrored_preserved", "facing_orientation_preserved", "phase_demolished_preserved",
                         "workset_preserved", "design_option_preserved", "pinned_preserved",
                         "rotation_preserved", "level_preserved", "phase_created_preserved",
                         "subcomponent_symbols_preserved", "subcomponent_identity_preserved",
                         "bounds_verified", "sill_height", "head_height"
                     })
                Assert.Contains(key, verifier);
        }

        [Fact]
        public void The_copier_and_the_verifier_read_ONE_policy()
        {
            // THE DEFECT THIS REPLACES. Two tables encoded the same fact and disagreed:
            // NeverCopied (executor) held 14 entries INCLUDING bip:HOST_AREA_COMPUTED,
            // AllowedToChange (verifier) held 4 and did not. So the copier declined to
            // copy a Revit-computed parameter and the verifier called its change
            // unexplained - and every wall with a door rolled back on it, measured.
            string ex = CodeOnly(Executor());
            string ve = CodeOnly(Verifier());

            Assert.DoesNotContain("NeverCopied", ex);
            Assert.DoesNotContain("AllowedToChange", ve);

            Assert.Contains("WallLayerRules.ShouldCopy(key)", ex);
            Assert.Contains("WallLayerRules.MayChangeWithoutExplanation(key)", ve);
            Assert.Contains("WallLayerRules.ParameterReason(key)", ve);
        }

        [Fact]
        public void A_property_that_legitimately_changes_reports_before_after_and_the_rule()
        {
            string verifier = Verifier();
            Assert.Contains("parameters_changed_by_design", verifier);
            Assert.Contains("parameters_changed_unexpectedly", verifier);
            Assert.Contains("bounds_normal_component_excluded_because", verifier);
        }

        // ---- P0-4: provenance is written AND read back ----------------------------

        [Fact]
        public void Provenance_is_written_through_a_verifying_call_that_can_fail()
        {
            string types = Types();
            Assert.Contains("public static string WriteVerified(", types);
            Assert.Contains("Stamp back = ReadStamp(element);", types);
            foreach (string field in new[]
                     {
                         "schema_version", "source_wall_unique_id", "plan_fingerprint",
                         "original_wall_type_id", "layer_index", "role", "sibling_unique_ids"
                     })
                Assert.Contains("Drift(element, \"" + field + "\")", types);
        }

        [Fact]
        public void A_provenance_failure_rolls_the_wall_back()
        {
            string executor = Executor();
            Assert.Contains("string stampFailure = WallSplitProvenance.WriteVerified(", executor);
            Assert.Contains("return new Fail(WallSplitCodes.ProvenanceVerificationFailed, stampFailure);", executor);
            // ...and for the siblings too, not only the carrier.
            Assert.Contains("foreach (KeyValuePair<int, Wall> pair in created)", executor);
        }

        [Fact]
        public void Idempotency_inspects_the_whole_sibling_set_not_just_the_carrier()
        {
            string types = Types();
            Assert.Contains("public static string InspectSiblingSet(", types);
            foreach (string signal in new[]
                     {
                         "siblings_missing", "siblings_unstamped", "duplicate_layer_indices",
                         "expected_wall_count", "siblings_present", "siblings_from_another_conversion",
                         "sibling_lists_divergent", "carriers_found", "expected_layer_indices_missing",
                         "roles_incorrect", "type_fingerprints_incorrect", "type_names_incorrect",
                         "not_single_layer", "extra_walls_with_this_plan"
                     })
                Assert.Contains(signal, types);

            Assert.Contains("WallSplitCodes.RepairablePartialState", types);
            Assert.Contains("WallSplitCodes.AlreadySplit", types);
        }

        [Fact]
        public void An_incomplete_sibling_set_fails_verification()
        {
            Assert.Contains("WallSplitCodes.VerifySiblingSetIncomplete", Verifier());
            Assert.Contains("ExtrasScan.SkippedByConstruction, null, out siblings)", Verifier());
        }

        // ---- P0-5: the same verifier runs twice -----------------------------------

        [Fact]
        public void The_detailed_verifier_runs_before_the_subtransaction_commit_and_after_the_outer_commit()
        {
            Assert.Contains("VerificationPhase.BeforeSubTransactionCommit", Executor());
            Assert.Contains("VerificationPhase.AfterOuterCommit", Command());
            Assert.Contains("WallSplitVerifier.Run(doc, outcome.Expectation, VerificationPhase.AfterOuterCommit)",
                            Command());
        }

        [Fact]
        public void The_post_commit_pass_is_not_a_mere_existence_check()
        {
            // The finding was `doc.GetElement(id) is Wall`. It must be gone.
            string command = Command();
            Assert.DoesNotContain("doc.GetElement(Rid.Make(l.ResultingWallId)) is Wall", command);
            Assert.Contains("post_commit_failures", command);
        }

        [Fact]
        public void The_limit_of_the_post_commit_pass_is_stated_honestly()
        {
            string command = Command();
            Assert.Contains("post_commit_limitation", command);
            Assert.Contains("cannot undo", command);
            Assert.Contains("can_roll_back", Verifier());
        }

        // ---- P0-6: fingerprint, builder and matcher agree -------------------------

        [Fact]
        public void The_builder_applies_the_wrapping_facts_the_fingerprint_contains()
        {
            string types = Types();
            Assert.Contains("CarryWrapping(sourceType, single);", types);
            Assert.Contains("target.OpeningWrapping = from.OpeningWrapping;", types);
            Assert.Contains("target.EndCap = from.EndCap;", types);
        }

        [Fact]
        public void The_matcher_and_the_builder_run_the_same_comparison()
        {
            string types = Types();
            Assert.Contains("private static string CompareIdentity(", types);
            // Both paths call it, and both then recompute the digest from the model.
            Assert.Contains("CompareIdentity(doc, candidate, structure, layers[0], source, assembly)", types);
            Assert.Contains("string mismatch = CompareIdentity(doc, made, back, layers[0], source, assembly);", types);
            Assert.Contains("static string FingerprintOf(", types);
        }

        [Fact]
        public void A_new_type_is_re_read_before_it_is_accepted()
        {
            string types = Types();
            Assert.Contains("string failure = Confirm(doc, made, source, assembly, layer);", types);
            Assert.Contains("recomputed from the model, is not the one the plan approved", types);
        }

        // ---- P0-7: cuts are probed at several points ------------------------------

        [Fact]
        public void The_cut_is_probed_at_five_points_not_one()
        {
            string verifier = Verifier();
            Assert.Contains("private static List<XYZ> ProbePoints(", verifier);
            Assert.Contains("points_checked", verifier);
            Assert.Contains("material_along_ray_mm", verifier);
            Assert.Contains("probes", verifier);
        }

        [Fact]
        public void An_unmeasurable_probe_is_a_failure_and_never_a_pass()
        {
            string verifier = Verifier();
            Assert.Contains("bool clear = measured && inside <= WallLayerRules.ToleranceFeet;", verifier);
            Assert.Contains("or the geometry could not be measured", verifier);
        }

        [Fact]
        public void The_cut_check_does_not_rest_on_the_join_alone()
        {
            // AreElementsJoined is checked, but it is not what proves the hole.
            string verifier = Verifier();
            Assert.Contains("MaterialAlongRay", verifier);
            Assert.Contains("WallSplitCodes.VerifyOpeningMissing", verifier);
        }

        // ---- P1-8: the dry run counts what it says it counts -----------------------

        [Fact]
        public void The_dry_run_separates_walls_to_create_from_dependency_counts()
        {
            string command = Command();
            Assert.DoesNotContain("objects_requiring_reconstruction", command);
            foreach (string key in new[]
                     {
                         "secondary_walls_to_create", "dependencies_preserved_by_identity",
                         "dependencies_requiring_reconstruction", "dependencies_blocking",
                         "dependencies_not_applicable"
                     })
                Assert.Contains(key, command);
        }

        // ---- the naming rule, still exactly as specified ---------------------------

        // ---- P0-1: provenance is read BEFORE the plan -----------------------------

        [Fact]
        public void Provenance_is_read_before_anything_is_planned()
        {
            // THE ordering bug. After a conversion the carrier is single-layer, so a Read
            // that planned first would refuse it as `single_layer` and never consult the
            // stamp - making the contract's promise of already_split unreachable from the
            // public flow. The stamp read has to come first, literally.
            string facts = Facts();

            int read = facts.IndexOf("public static WallSplitSubject Read(", StringComparison.Ordinal);
            int provenance = facts.IndexOf("ReadProvenanceState(doc, wall, subject, provenance)", read, StringComparison.Ordinal);
            int blocking = facts.IndexOf("ReadBlockingConditions(doc, wall", read, StringComparison.Ordinal);
            int plan = facts.IndexOf("WallLayerRules.Plan(subject.Assembly)", read, StringComparison.Ordinal);

            Assert.True(provenance > read, "the provenance state is never read in Read");
            Assert.True(blocking > provenance, "eligibility is read before the provenance stamp");
            Assert.True(plan > provenance, "the plan is computed before the provenance stamp");
        }

        [Fact]
        public void A_stamped_wall_short_circuits_before_eligibility_or_planning()
        {
            Assert.Contains("if (ReadProvenanceState(doc, wall, subject, provenance)) return subject;", Facts());
        }

        [Fact]
        public void Every_provenance_state_the_contract_names_is_reachable()
        {
            string facts = Facts();
            foreach (string state in new[]
                     {
                         "WallSplitCodes.AlreadySplit",
                         "WallSplitCodes.RepairablePartialState",
                         "WallSplitCodes.ProvenanceInvalid",
                         "WallSplitCodes.ExistingPlanConflict"
                     })
                Assert.Contains(state, facts);
        }

        [Fact]
        public void A_secondary_sibling_is_diagnosed_through_its_carrier()
        {
            string facts = Facts();
            string types = Types();
            Assert.Contains("public static Element FindCarrier(", types);
            Assert.Contains("WallSplitProvenance.FindCarrier(doc, wall)", facts);
            Assert.Contains("SelectedSecondarySibling", facts);
        }

        [Fact]
        public void An_already_converted_wall_is_never_eligible_and_never_reaches_a_transaction()
        {
            string facts = Facts();
            Assert.Contains("public bool AlreadyConverted => ProvenanceState != null;", facts);
            Assert.Contains("Rejection == null && !AlreadyConverted && Plan != null && Plan.Eligible", facts);

            // And the executor asserts the ordering cannot regress underneath it.
            Assert.Contains("if (WallSplitProvenance.ReadStamp(carrier).Present)", Executor());
        }

        /// <summary>
        /// BOTH replies, separately. The first version of this test asserted that
        /// "already_converted" appeared somewhere in the file, and mutation showed that
        /// deleting it from the DRY RUN left the test green because the apply reply still
        /// carried it. Each region is extracted and asserted on its own.
        /// </summary>
        [Fact]
        public void The_batch_reports_converted_walls_in_their_own_bucket()
        {
            string command = Command();
            Assert.Contains("List<WallSplitSubject> alreadyConverted", command);
            Assert.Contains("private static JObject Converted(", command);
            Assert.Contains("[\"transaction_opened\"] = false", command);

            int dryStart = command.IndexOf("if (dryRun)", StringComparison.Ordinal);
            int dryEnd = command.IndexOf("// ---- apply", dryStart, StringComparison.Ordinal);
            Assert.True(dryEnd > dryStart, "the dry-run region was not found");
            string dryRun = command.Substring(dryStart, dryEnd - dryStart);

            Assert.Contains("\"already_converted\"", dryRun);
            Assert.Contains("\"already_split_walls\"", dryRun);
            Assert.Contains("\"partial_state_walls\"", dryRun);

            int replyStart = command.IndexOf("var reply = new JObject", StringComparison.Ordinal);
            int replyEnd = command.IndexOf("ApplicationOutcome.StampApplied(reply", replyStart, StringComparison.Ordinal);
            Assert.True(replyEnd > replyStart, "the apply reply was not found");
            string reply = command.Substring(replyStart, replyEnd - replyStart);

            Assert.Contains("\"already_converted\"", reply);
        }

        // ---- P0-2: the sibling set is proved, not counted -------------------------

        [Fact]
        public void The_provenance_record_carries_what_the_sibling_check_needs()
        {
            string types = Types();
            foreach (string field in new[]
                     {
                         "FieldExpectedWallCount", "FieldExpectedLayerIndices", "FieldExpectedRoleByLayer",
                         "FieldTypeFingerprint", "FieldExpectedTypeName"
                     })
                Assert.Contains("public const string " + field, types);
        }

        [Fact]
        public void Every_stamped_field_is_read_back_and_compared()
        {
            string types = Types();
            foreach (string field in new[]
                     {
                         "schema_version", "source_wall_unique_id", "plan_fingerprint", "original_wall_type_id",
                         "layer_index", "role", "sibling_unique_ids", "converted_at",
                         "expected_wall_count", "expected_layer_indices", "expected_role_by_layer",
                         "type_fingerprint", "expected_type_name"
                     })
                Assert.Contains("Drift(element, \"" + field + "\")", types);
        }

        [Fact]
        public void The_sibling_check_detects_every_failure_mode_the_review_named()
        {
            string types = Types();
            foreach (string signal in new[]
                     {
                         "siblings_missing",                    // hermano faltante
                         "extra_walls_with_this_plan",          // hermano adicional
                         "sibling_lists_divergent",             // lista divergente
                         "carriers_found",                      // dos carriers
                         "roles_incorrect",                     // role incorrecto
                         "expected_layer_indices_missing",      // indice faltante
                         "duplicate_layer_indices",             // indice duplicado
                         "type_fingerprints_incorrect",         // fingerprint diferente
                         "siblings_from_another_conversion",    // hermano de otra conversion
                         "not_single_layer"                     // ya no es monocapa
                     })
                Assert.Contains(signal, types);
        }

        [Fact]
        public void Already_split_is_returned_only_when_nothing_fired()
        {
            string types = Types();
            Assert.Contains("bool complete =", types);
            Assert.Contains("carriers.Count == 1", types);
            Assert.Contains("extras.Count == 0", types);
            Assert.Contains("report.Value<bool?>(\"extra_scan_ran\") == true", types);
            Assert.Contains("complete ? WallSplitCodes.AlreadySplit : WallSplitCodes.RepairablePartialState", types);
        }

        [Fact]
        public void A_scan_that_could_not_run_is_not_a_scan_that_found_nothing()
        {
            string types = Types();
            Assert.Contains("[\"extra_scan_ran\"] = false", types);
            Assert.Contains("A scan that could not run is NOT a scan that found nothing", types);
        }

        [Fact]
        public void The_extras_scan_is_indexed_once_per_call_and_skipped_where_it_cannot_have_an_answer()
        {
            // The O(N x M) shape flagged as D-22 in the phase-0 audit: a document-wide wall
            // collector per wall inspected. The index is built once; and inside the
            // SubTransaction that minted the fingerprint the question has no answer to find,
            // so it is SKIPPED with that reason rather than run for nothing.
            string types = Types();
            Assert.Contains("public sealed class WallProvenanceIndex", types);
            Assert.Contains("ExtrasScan.SkippedByConstruction", types);
            Assert.Contains("public enum ExtrasScan", types);

            Assert.Contains("WallProvenanceIndex provenance = WallProvenanceIndex.Build(doc);", Command());
            Assert.Contains("ExtrasScan.Indexed, provenance, out report)", Facts());
            Assert.Contains("ExtrasScan.SkippedByConstruction, null, out siblings)", Verifier());
        }

        [Fact]
        public void There_is_no_shallow_provenance_check_left_beside_the_thorough_one()
        {
            // A StateOf(element, fingerprint) that answered from the carrier's stamp alone
            // used to sit here unused. It is deleted rather than left: a shallow check next
            // to a thorough one is a trap, because it cannot tell a finished conversion from
            // one somebody has since deleted three walls out of, and it is the one a future
            // caller would reach for.
            string types = Types();
            Assert.DoesNotContain("public static string StateOf(", types);
            Assert.Contains("InspectSiblingSet is the answer", types);
        }

        [Fact]
        public void A_skipped_scan_and_a_failed_scan_are_different_answers()
        {
            string types = Types();
            // Skipped: allowed, and says why. Failed: blocks completeness.
            Assert.Contains("[\"extra_scan_ran\"] = JValue.CreateNull()", types);
            Assert.Contains("mode == ExtrasScan.SkippedByConstruction || report.Value<bool?>(\"extra_scan_ran\") == true",
                            types);
        }

        // ---- P0-3: every captured join field is consumed --------------------------

        [Fact]
        public void Every_field_WallJoinFacts_captures_is_compared_by_the_verifier()
        {
            // The shape this catches is "captured and never used", which is exactly how the
            // end joins were lost twice. Each field below must appear in the verifier.
            string verifier = Verifier();
            foreach (string field in new[]
                     {
                         "before.GeometricJoinIds",
                         "before.CutByOther",
                         "before.JoinAllowedAtEnd0",
                         "before.JoinAllowedAtEnd1",
                         "before.EndFlagsRead",
                         "before.ElementsAtEnd0",
                         "before.ElementsAtEnd1",
                         "before.ElementsAtJoinRead"
                     })
                Assert.Contains(field, verifier);
        }

        [Fact]
        public void The_elements_at_each_end_are_compared_IN_ORDER()
        {
            string verifier = Verifier();
            Assert.Contains("end0Now.SequenceEqual(before.ElementsAtEnd0)", verifier);
            Assert.Contains("end1Now.SequenceEqual(before.ElementsAtEnd1)", verifier);
        }

        [Fact]
        public void The_cut_order_is_re_read_after_the_restoration()
        {
            string verifier = Verifier();
            Assert.Contains("IsCuttingElementInJoin(doc, carrier, other)", verifier);
            Assert.Contains("cut_order_changed", verifier);
            Assert.Contains("cut_order_preserved", verifier);
        }

        [Fact]
        public void A_join_fact_that_could_not_be_read_is_not_reported_as_preserved()
        {
            string verifier = Verifier();
            Assert.Contains("[\"elements_at_join_preserved\"] = JValue.CreateNull()", verifier);
            Assert.Contains("[\"end_flags_preserved\"] = JValue.CreateNull()", verifier);
            Assert.Contains("Unknown is not verified", verifier);
            Assert.Contains("cut_order_unreadable", verifier);
        }

        // ---- P0-4: the token binds real state -------------------------------------

        [Fact]
        public void The_token_binds_dependency_STATE_and_not_a_list_of_ids()
        {
            string facts = Facts();
            Assert.DoesNotContain("subject.Dependencies.Select(d => d.UniqueId)", facts);
            Assert.Contains("Select(d => FingerprintOf(d.Snapshot))", facts);
            Assert.Contains("FingerprintOf(subject.Joins)", facts);
            Assert.Contains("WallStateFingerprint(subject.Wall)", facts);
        }

        [Fact]
        public void Every_field_of_a_dependency_snapshot_enters_its_fingerprint()
        {
            string facts = Facts();
            foreach (string fact in new[]
                     {
                         "insert.symbol_id", "insert.level_id", "insert.hand_flipped", "insert.facing_flipped",
                         "insert.mirrored", "insert.phase_created", "insert.phase_demolished", "insert.workset",
                         "insert.design_option", "insert.pinned", "insert.subcomponent_unique_ids",
                         "insert.subcomponent_symbol_ids", "insert.rotation", "insert.bounds",
                         "opening.rectangular", "opening.boundary_points",
                         "sweep.type", "sweep.profile_id", "sweep.distance", "sweep.wall_offset",
                         "wall.base_level", "wall.top_level", "wall.curve_digest",
                         "dimension.references", "dimension.value",
                         "tag.element_ids", "tag.unique_ids", "tag.reference_count"
                     })
                Assert.Contains("\"" + fact + "\"", facts);
        }

        [Fact]
        public void The_wall_state_fingerprint_covers_its_constraints_and_phases()
        {
            string facts = Facts();
            Assert.Contains("public static string WallStateFingerprint(Wall wall)", facts);
            foreach (string parameter in new[]
                     {
                         "WALL_BASE_CONSTRAINT", "WALL_HEIGHT_TYPE", "WALL_TOP_OFFSET",
                         "WALL_ATTR_ROOM_BOUNDING", "WALL_STRUCTURAL_USAGE_PARAM",
                         "PHASE_CREATED", "PHASE_DEMOLISHED", "ELEM_PARTITION_PARAM", "WALL_KEY_REF_PARAM"
                     })
                Assert.Contains("BuiltInParameter." + parameter, facts);
            Assert.Contains("book.Add(\"pinned\"", facts);
        }

        [Fact]
        public void The_expectation_is_built_from_the_APPROVED_state_not_the_re_read_one()
        {
            // If the fingerprint ever missed something, building what we verify AGAINST out
            // of the freshly-read model would make the conversion agree with itself.
            string executor = Executor();
            Assert.Contains("CarrierId = approved.ElementId", executor);
            Assert.Contains("Dependencies = approved.Dependencies", executor);
            Assert.Contains("Joins = approved.Joins", executor);
            Assert.Contains("WallSplitPlan plan = approved.Plan;", executor);
            Assert.Contains("XYZ normal = approved.ExteriorNormal;", executor);
            Assert.Contains("BUILT FROM THE APPROVED STATE", executor);
        }

        // ---- P1: tags and the reverse census --------------------------------------

        [Fact]
        public void A_tag_keeps_its_whole_set_of_tagged_elements()
        {
            string facts = Facts();
            string verifier = Verifier();
            Assert.Contains("public List<long> TaggedElementIds", facts);
            Assert.Contains("public List<string> TaggedUniqueIds", facts);
            Assert.Contains("TaggedReferenceCount", facts);
            Assert.Contains("TagHasNonLocalReference", facts);
            Assert.Contains("ids.SequenceEqual(before.TaggedElementIds)", verifier);
            Assert.Contains("uniqueIds.SequenceEqual(before.TaggedUniqueIds)", verifier);
        }

        [Fact]
        public void The_reverse_census_asks_the_annotations_rather_than_the_wall()
        {
            string facts = Facts();
            Assert.Contains("public sealed class WallReverseCensus", facts);
            Assert.Contains("OfClass(typeof(Dimension))", facts);
            Assert.Contains("OfClass(typeof(IndependentTag))", facts);
            Assert.Contains("census.For(Rid.Value(wall.Id))", facts.Replace("reverse.For", "census.For"));
        }

        [Fact]
        public void The_reverse_census_is_built_once_per_call_not_once_per_wall()
        {
            string command = Command();
            Assert.Contains("WallReverseCensus reverse = WallReverseCensus.Build(doc);", command);
            Assert.Contains("options.AllowArcWalls, reverse, provenance)", command);
        }

        [Fact]
        public void An_uninterpretable_reference_blocks_rather_than_being_ignored()
        {
            string facts = Facts();
            Assert.Contains("IsUninterpretable", facts);
            Assert.Contains("DependencyDisposition.UnsupportedBlocking", facts);
            Assert.Contains("It is blocking rather than assumed harmless", facts);
        }

        [Fact]
        public void A_reverse_census_that_could_not_run_blocks_every_wall_in_the_batch()
        {
            string facts = Facts();
            Assert.Contains("if (!reverse.ScanRan)", facts);
            Assert.Contains("Unknown is not empty.", facts);
        }

        [Fact]
        public void A_reference_into_a_LINK_is_not_attributed_to_this_wall()
        {
            string facts = Facts();
            Assert.Contains("Rid.Value(reference.LinkedElementId) > 0", facts);
            Assert.Contains("pretending otherwise would", facts);
        }

        // ---- findings from the adversarial review ---------------------------------
        //
        // Twelve findings survived independent refutation. Each fix is pinned here, named
        // after the hole it closes, so a regression reads as the specific defect returning
        // rather than as an anonymous assertion breaking.

        [Fact]
        public void The_apply_time_revalidation_reads_the_wall_with_the_SAME_inputs()
        {
            // Convert re-read the wall with the two document-wide reads OMITTED, so the
            // stale check compared a census WITH annotations against one WITHOUT them. Any
            // wall carrying a dimension or a tag refused as stale_plan and could never be
            // converted at all.
            string executor = Executor();
            Assert.Contains("options.Reverse, options.Provenance);", executor);
            Assert.Contains("public WallReverseCensus Reverse;", executor);
            Assert.Contains("public WallProvenanceIndex Provenance;", executor);
            Assert.Contains("options.Reverse = reverse;", Command());
            Assert.Contains("options.Provenance = provenance;", Command());
        }

        [Fact]
        public void The_cut_proof_covers_openings_and_embedded_walls_not_only_family_instances()
        {
            // An Opening is a first-class insert and was never probed; nor was an embedded
            // curtain wall. Both punch a hole the secondary layers have to reproduce.
            string verifier = Verifier();
            int start = verifier.IndexOf("private static void VerifyCuts(", StringComparison.Ordinal);
            int end = verifier.IndexOf("private sealed class CutSubject", start, StringComparison.Ordinal);
            Assert.True(end > start, "VerifyCuts was not found");
            string cuts = verifier.Substring(start, end - start);

            Assert.Contains("case DependencyKinds.Opening:", cuts);
            Assert.Contains("case DependencyKinds.EmbeddedWall:", cuts);
            Assert.Contains("case DependencyKinds.FamilyInstance:", cuts);
        }

        [Fact]
        public void An_insert_nobody_could_measure_fails_the_cut_proof()
        {
            // It used to be filtered out of the list with no row, no note and no failure.
            // The GUARD is asserted, not the prose beside it: mutation showed that checking
            // for the message alone left the test green when the guard itself was disabled.
            string verifier = Verifier();
            Assert.Contains("inserts_unprobeable", verifier);
            Assert.Contains("CutSubject unmeasurable = subjects.FirstOrDefault(x => x.Bounds == null);", verifier);
            Assert.Contains("if (unmeasurable != null && layers.Count > 0)", verifier);
            Assert.Contains("cut is not a verified cut.", verifier);
        }

        [Fact]
        public void An_empty_cut_proof_says_nothing_was_probed_rather_than_reading_as_verified()
        {
            string verifier = Verifier();
            Assert.Contains("public JObject CutCoverage", verifier);
            Assert.Contains("[\"cut_coverage\"] = CutCoverage,", verifier);
            Assert.Contains("[\"probed\"] = false", verifier);
            Assert.Contains("No probe was run and none is claimed.", verifier);
        }

        [Fact]
        public void The_embedded_wall_verifier_is_given_the_carrier_and_checks_the_relationship()
        {
            // It was the only dependency verifier that did not even take the carrier as an
            // argument, while the contract said the embedded wall "stays embedded in it".
            string verifier = Verifier();
            Assert.Contains("VerifyEmbeddedWall(Document doc, DependencySnapshot before, Wall after, Wall carrier",
                            verifier);
            Assert.Contains("still_related_to_carrier", verifier);
        }

        [Fact]
        public void A_sweep_is_measured_in_model_space_and_not_only_against_its_host()
        {
            // Distance and WallOffset are measured FROM the host, so they cannot fail when
            // the host itself moves - the position check had nothing behind it.
            // REGION-SCOPED. This assertion was BITING and went vacuous the moment the
            // foundation verifier gained its own position_deviation_mm - a second occurrence
            // of the same string, satisfying a test that meant to be about sweeps. That is
            // the fifth time this suite has been caught that way; the lesson is not "be
            // careful", it is "assert inside the region you mean".
            string verifier = Verifier();
            int start = verifier.IndexOf("private static string VerifySweep(", StringComparison.Ordinal);
            int end = verifier.IndexOf("private static string VerifyEmbeddedWall(", start, StringComparison.Ordinal);
            Assert.True(end > start, "VerifySweep was not found");
            string sweep = verifier.Substring(start, end - start);

            Assert.Contains("position_deviation_mm", sweep);
            Assert.Contains("before.SweepBounds", sweep);
            Assert.Contains("public BoundingBoxXYZ SweepBounds;", Facts());
            Assert.Contains("host_face_width_change_mm", sweep);
            Assert.Contains("carrier.Width", sweep);
            Assert.Contains("expected.Plan.TotalWidthFeet", sweep);
            Assert.Contains("expected.CarrierOffsetFeet + faceWidthChangeFeet", sweep);
            Assert.Contains("SweepWallSide", Facts());
        }

        [Fact]
        public void Already_split_requires_the_layers_to_still_BE_where_they_belong()
        {
            // THE finding. already_split was decided entirely from stamp-vs-stamp and
            // stamp-vs-TYPE comparisons, so a conversion this tool itself reported as
            // failing its post-commit check read back as "a completed split, present and
            // coherent" on the very next call - and the tool then refused to touch it.
            string types = Types();
            Assert.Contains("private static string MeasureLayerPositions(", types);
            Assert.Contains("geometryOk && geometryMeasured &&", types);
            Assert.Contains("expected_offset_from_carrier_mm", types);
            Assert.Contains("WallSplitExecutor.Deviation(actual, target)", types);
        }

        [Fact]
        public void A_geometry_check_that_could_not_run_does_not_yield_already_split()
        {
            string types = Types();
            Assert.Contains("does not report a set as", types);
            Assert.Contains("complete on the strength of its stamps alone", types);
        }

        [Fact]
        public void A_rollback_that_did_not_confirm_is_not_reported_as_exactly_as_it_was()
        {
            // Guard.RollbackResult.Confirmed existed and nothing consulted it: a Pending or
            // an Error read as a clean rollback.
            //
            // BOTH rollback paths are asserted separately - the planned failure and the
            // thrown one. Mutation showed that asserting the line once left the test green
            // when only one of the two was broken.
            string executor = Executor();
            Assert.Contains("[\"rollback_confirmed\"]", executor);

            int failStart = executor.IndexOf("Fail failure = Convert(", StringComparison.Ordinal);
            int failEnd = executor.IndexOf("catch (Exception ex)", failStart, StringComparison.Ordinal);
            Assert.True(failEnd > failStart, "the planned-failure path was not found");
            string plannedPath = executor.Substring(failStart, failEnd - failStart);

            Assert.Contains("Guard.RollbackResult rollback = Guard.RollBack(sub);", plannedPath);
            Assert.Contains("outcome.RollbackConfirmed = rollback.Confirmed;", plannedPath);
            Assert.Contains("if (!rollback.Confirmed)", plannedPath);
            Assert.Contains("AND THE ROLLBACK DID NOT CONFIRM", plannedPath);

            int throwStart = failEnd;
            int throwEnd = executor.IndexOf("private sealed class Fail", throwStart, StringComparison.Ordinal);
            Assert.True(throwEnd > throwStart, "the thrown-failure path was not found");
            string thrownPath = executor.Substring(throwStart, throwEnd - throwStart);

            Assert.Contains("outcome.RollbackConfirmed = rollback.Confirmed;", thrownPath);
            Assert.Contains("if (!rollback.Confirmed)", thrownPath);
        }

        [Fact]
        public void The_measured_offset_is_actually_measured_and_not_always_zero()
        {
            // LayerOutcome.ObservedOffsetMm was emitted to callers as a measurement and was
            // assigned nowhere in the repository, so it was always 0.0. The helper that
            // could have computed it had no caller either.
            string executor = Executor();
            string verifier = Verifier();
            Assert.Contains("public static double ObservedOffsetMm(", executor);
            Assert.Contains("WallSplitExecutor.ObservedOffsetMm(expected.OriginalCurve", verifier);
            Assert.Contains("ObservedOffsetMm = measured?.Value<double?>(\"observed_offset_mm\")", executor);
        }

        [Fact]
        public void The_origin_parameter_reports_what_happened_to_it()
        {
            // It was silently not carried when absent, read-only or not text, and the whole
            // block sat inside a catch that swallowed the rest.
            string executor = Executor();
            Assert.Contains("skipped.Add(key + \" (absent on \"", executor);
            Assert.Contains("else if (to.IsReadOnly) readOnly.Add(key);", executor);
            Assert.Contains("(the write was refused)", executor);
        }

        [Fact]
        public void Every_published_failure_code_is_emitted_by_some_path()
        {
            // A code in the closed set that nothing emits is a promise to a client that will
            // never be kept: it branches on a value it can never receive. Two were found
            // this way - matches_existing_plan, which was deleted, and
            // verify_unexpected_warning, which is now actually reported.
            // The pure core emits the eligibility codes from Plan(); the Revit half emits
            // the rest. Both are scanned, or the test would report Plan's codes as dead.
            string all = Facts() + Verifier() + Executor() + Types() + Command() +
                         Source("src/Horizun.Revit/Core/WallLayerRules.cs");

            var unemitted = WallSplitCodes.All
                .Where(code => !all.Contains("WallSplitCodes." + Member(code), StringComparison.Ordinal))
                .ToList();

            Assert.True(unemitted.Count == 0,
                "these codes are published and nothing emits them: " + string.Join(", ", unemitted));
        }

        /// <summary>snake_case code back to the constant that names it.</summary>
        private static string Member(string code)
            => string.Concat(code.Split('_').Select(part =>
                   part.Length == 0 ? part : char.ToUpperInvariant(part[0]) + part.Substring(1)));

        [Fact]
        public void The_code_to_member_mapping_is_not_silently_wrong()
        {
            // If Member() produced names that match nothing, the test above would pass
            // vacuously for every code at once.
            Assert.Equal("NotAWall", Member("not_a_wall"));
            Assert.Equal("VerifyUnexpectedWarning", Member("verify_unexpected_warning"));
            Assert.Equal("StalePlan", Member("stale_plan"));
        }

        [Fact]
        public void The_exterior_normal_is_corroborated_by_a_second_source()
        {
            // I2 cannot catch a wrong normal: the layers are PLACED along it and MEASURED
            // along the same one, so a flip agrees with itself. The only defence is a second
            // source, checked before anything is written.
            string facts = Facts();
            Assert.Contains("private static bool CorroborateNormal(", facts);
            Assert.Contains("public bool NormalCorroborated;", facts);
            Assert.Contains("exterior_normal_corroborated", Command());

            // AS A GUARD, inside Read, before anything is planned. Asserting that the method
            // and its prose exist is not the same as asserting anything calls it - mutation
            // showed that disabling the guard left a presence-only test green. This is the
            // fourth assertion in this suite to fail that way; presence is not wiring.
            int read = facts.IndexOf("public static WallSplitSubject Read(", StringComparison.Ordinal);
            int census = facts.IndexOf("subject.Dependencies = TakeCensus(", read, StringComparison.Ordinal);
            Assert.True(census > read, "the census call was not found inside Read");
            string beforeCensus = facts.Substring(read, census - read);

            Assert.Contains("if (!CorroborateNormal(wall, subject))", beforeCensus);
            Assert.Contains("return subject;", beforeCensus);
            Assert.Contains("would build the wall inside-out and verify it as correct", beforeCensus);
        }

        [Fact]
        public void Parameters_are_compared_for_every_dependency_kind_not_only_instances()
        {
            // They are captured for all seven kinds. They used to be compared for one.
            string verifier = Verifier();
            int start = verifier.IndexOf("foreach (DependencySnapshot before in expected.Dependencies)",
                                         StringComparison.Ordinal);
            int end = verifier.IndexOf("private static string KindFailureCode", start, StringComparison.Ordinal);
            string loop = verifier.Substring(start, end - start);
            Assert.Contains("if (failure == null) failure = CompareParameters(after, before, check);", loop);
        }

        [Fact]
        public void Only_shape_owned_rebar_dimensions_are_excused_after_geometry_is_proved()
        {
            string verifier = Verifier();
            Assert.Contains("IsVerifiedRebarShapeParameter(after, parameter, check)", verifier);
            Assert.Contains("centreline_constraint_preserved", verifier);
            Assert.Contains("shape.GetRebarShapeDefinition()", verifier);
            Assert.Contains("definition.GetParameters().Any", verifier);
            Assert.Contains("Failure to prove ownership is not permission", verifier);
        }

        [Fact]
        public void A_layer_wall_that_faces_the_wrong_way_fails()
        {
            string verifier = Verifier();
            Assert.Contains("faces_same_way_as_carrier", verifier);
            Assert.Contains("exterior side is the carrier's interior side", verifier);
        }

        [Fact]
        public void The_report_distinguishes_the_planned_type_name_from_the_one_chosen()
        {
            string executor = Executor();
            Assert.Contains("public string PlannedTypeName;", executor);
            Assert.Contains("[\"type_name_is_variant\"]", executor);
        }

        // ---- FASE 11: dependencias estructurales -----------------------------------

        [Fact]
        public void The_structural_census_asks_RebarHostData_directly()
        {
            // GetDependentElements does not reliably return reinforcement, and a bar set
            // that never reaches the ledger is a bar set nothing verifies.
            string facts = Facts();
            Assert.Contains("RebarHostData.GetRebarHostData(wall)", facts);
            foreach (string read in new[]
                     {
                         "GetRebarsInHost", "GetAreaReinforcementsInHost", "GetPathReinforcementsInHost",
                         "GetFabricAreasInHost", "GetFabricSheetsInHost", "GetRebarContainersInHost"
                     })
                Assert.Contains(read, facts);
        }

        [Fact]
        public void A_host_whose_reinforcement_cannot_be_enumerated_blocks()
        {
            string facts = Facts();
            Assert.Contains("if (!asked)", facts);
            Assert.Contains("Unknown is not empty, and a wall whose reinforcement cannot be enumerated", facts);
        }

        [Fact]
        public void A_wall_that_is_not_a_reinforcement_host_is_an_ANSWER_not_an_absence()
        {
            string facts = Facts();
            Assert.Contains("this wall is not a reinforcement host", facts);
            Assert.Contains("Asked and answered, not assumed.", facts);
        }

        [Fact]
        public void The_rebar_reading_algorithm_is_reused_and_not_reimplemented()
        {
            // RebarFacts.Describe is this bridge's existing reader. Duplicating it would mean
            // two algorithms that can disagree about the same bar.
            string facts = Facts();
            string verifier = Verifier();
            Assert.Contains("RebarFacts.Describe(doc, bar, includePositions: true)", facts);
            Assert.Contains("RebarFacts.Describe(doc, after, includePositions: true)", verifier);
            Assert.Contains("RebarFacts.CentrelinePointsMm(", facts);
            Assert.Contains("RebarFacts.CentrelinePointsMm(", verifier);

            // And the containment answer comes from the existing rules, not a second copy.
            Assert.Contains("RebarContainment.Check(", verifier);
            Assert.Contains("HostSolidMesh.Usable(carrier", verifier);
        }

        [Fact]
        public void Rebar_containment_against_the_CORE_CARRIER_is_what_decides()
        {
            // The check with real teeth: a bar that fitted a 350 mm compound wall can easily
            // be outside a 150 mm core.
            string verifier = Verifier();
            Assert.Contains("WallSplitCodes.RebarOutsideCoreCarrier", verifier);
            Assert.Contains("inside_core_carrier", verifier);
            Assert.Contains("It has NOT been moved to", verifier);
        }

        [Fact]
        public void An_unmeasurable_containment_is_not_an_inside_one()
        {
            string verifier = Verifier();
            Assert.Contains("Unknown is not inside.", verifier);
            Assert.Contains("bool inside = containment.Measured &&", verifier);
        }

        [Fact]
        public void The_foundation_must_stay_on_the_carrier_and_not_on_a_finish_layer()
        {
            string verifier = Verifier();
            Assert.Contains("VerifyFoundation(DependencySnapshot before, WallFoundation after", verifier);
            Assert.Contains("wall_is_carrier", verifier);
            Assert.Contains("a footing under the wrong wall", verifier);
            Assert.Contains("WallSplitCodes.VerifyFoundationGeometry", verifier);
        }

        [Fact]
        public void A_reinforcement_system_is_verified_by_its_MEMBERS()
        {
            // "It still exists" would pass a system that lost three of its bars.
            string verifier = Verifier();
            Assert.Contains("members_lost", verifier);
            Assert.Contains("members_gained", verifier);
            Assert.Contains("A system that lost bars is still a system", verifier);
        }

        [Fact]
        public void A_system_that_cannot_be_read_completely_is_a_refusal_with_its_own_code()
        {
            string verifier = Verifier();
            Assert.Contains("WallSplitCodes.UnsupportedReinforcementKind", verifier);
            Assert.Contains("refusal, not a warning", verifier);
        }

        [Fact]
        public void The_cover_is_part_of_the_walls_own_state_fingerprint()
        {
            // The cover decides where every bar sits, so a cover edited between the dry run
            // and the apply is a different wall to reinforce.
            string facts = Facts();
            Assert.Contains("private static void AddCover(FactBook book, Wall wall)", facts);
            Assert.Contains("AddCover(book, wall);", facts);
            Assert.Contains("GetCommonCoverType()", facts);
            Assert.Contains("GetExposedFaces()", facts);
        }

        [Fact]
        public void Every_structural_fact_enters_the_dependency_fingerprint()
        {
            string facts = Facts();
            foreach (string fact in new[]
                     {
                         "foundation.wall_id", "foundation.level_id", "foundation.offset", "foundation.curve_digest",
                         "foundation.bounds",
                         "rebar.host_id", "rebar.bar_type_id", "rebar.shape_id", "rebar.layout_rule",
                         "rebar.positions", "rebar.quantity", "rebar.position_digests", "rebar.terminations",
                         "system.host_id", "system.member_ids", "system.member_unique_ids",
                         "system.boundary_ids", "system.layers"
                     })
                Assert.Contains("\"" + fact + "\"", facts);
        }

        [Fact]
        public void Bar_positions_are_ORDERED_in_the_fingerprint()
        {
            // A bar set is a sequence. The third bar moving is not the same set with its
            // members shuffled, so sorting them would hide a real change.
            Assert.Contains("AddList(\"rebar.position_digests\", snapshot.RebarPositionDigests, ordered: true)",
                            Facts());
        }

        [Fact]
        public void Rebar_position_reading_is_symmetric_and_the_complete_set_is_anchored_in_model_space()
        {
            string facts = Facts();
            string verifier = Verifier();
            Assert.Contains("described?[\"bar_positions\"] is JArray positions", facts);
            Assert.Contains("WallSplitFacts.ReadRebarPositionDigests(described)", verifier);
            Assert.DoesNotContain("described[\"geometry\"]?[\"bar_positions\"]", facts);
            Assert.DoesNotContain("described[\"geometry\"]?[\"bar_positions\"]", verifier);
            Assert.Contains("rebar.centreline_points", facts);
            Assert.Contains("nowPositions.SequenceEqual(before.RebarPositionDigests)", verifier);
            Assert.Contains("centreline_worst_deviation_mm", verifier);
            Assert.Contains("kept_world_position", verifier);
            Assert.Contains("followed_carrier_curve", verifier);
            Assert.Contains("new { Name = \"followed_exterior_face\", Offset = expected.CarrierOffsetFeet + faceWidthChangeFeet },", verifier);
            Assert.Contains("new { Name = \"followed_interior_face\", Offset = expected.CarrierOffsetFeet - faceWidthChangeFeet }", verifier);
            Assert.Contains("selectedModeCounts[pointMode.Key]++", verifier);
            Assert.Contains("centreline_constraint_preserved", verifier);
        }

        [Fact]
        public void A_RebarInSystem_is_verified_through_its_system_and_not_twice()
        {
            string facts = Facts();
            Assert.Contains("if (element is RebarInSystem) return DependencyKinds.Structural;", facts);
            Assert.Contains("verifying it", facts);
        }

        [Fact]
        public void Revits_private_rebar_node_is_not_allowed_to_hide_or_block_the_real_bars()
        {
            string facts = Facts();
            Assert.Contains("IsInternalRebarNode(doc, wall, element)", facts);
            Assert.Contains("element.GetType() != typeof(Element)", facts);
            Assert.Contains("bar.GetHostId()", facts);
            Assert.Contains("children.Count == 0", facts);
            Assert.Contains("if (bar == null) return false", facts);
        }

        [Fact]
        public void Rectangular_openings_are_cut_explicitly_through_secondary_layers()
        {
            string executor = Executor();
            Assert.Contains("ReplicateRectangularOpenings(doc, approved.Dependencies, created.Values", executor);
            Assert.Contains("doc.Create.NewOpening(layerWall", executor);
            Assert.Contains("generated_cut_ids", executor);
            Assert.Contains("The ray probes below remain the authority", executor);
        }

        [Fact]
        public void Sweep_position_accepts_only_the_two_observed_Revit_semantics()
        {
            string verifier = Verifier();
            Assert.Contains("position_if_host_moved_deviation_mm", verifier);
            Assert.Contains("position_if_world_stationary_deviation_mm", verifier);
            Assert.Contains("Math.Min(movedDeviation, stationaryDeviation)", verifier);
            Assert.Contains("neither where it was nor where the carrier's displacement puts it", verifier);
        }

        // ---- FASE 12: what the live campaign found ---------------------------------

        [Fact]
        public void The_cross_section_check_reads_the_TYPED_property_and_no_magic_number()
        {
            // THE FIRST LIVE DEFECT. The check was `ReadInt(WALL_CROSS_SECTION) != 0` on the
            // assumption that 0 meant vertical. Measured on Revit 2023-2027 the enum is
            // SingleSlanted=0, Vertical=1, Tapered=2 - so it refused EVERY ordinary wall as
            // "slanted" and would have ACCEPTED a genuinely slanted one, which is the case
            // the refusal exists to prevent. Twenty-one live cases failed on it at once.
            string facts = Facts();
            Assert.Contains("section = wall.CrossSection;", facts);
            Assert.Contains("section.Value != WallCrossSection.Vertical", facts);
            Assert.DoesNotContain("BuiltInParameter.WALL_CROSS_SECTION, 0)", facts);
            Assert.DoesNotContain("if (cross != 0)", facts);
        }

        [Fact]
        public void A_cross_section_that_cannot_be_read_is_not_treated_as_vertical()
        {
            string facts = Facts();
            Assert.Contains("Unknown is", facts);
            Assert.Contains("not vertical", facts);
        }

        [Fact]
        public void No_eligibility_check_compares_a_Revit_enum_to_a_bare_integer()
        {
            // The general form of the same mistake. Every enum-valued eligibility fact is
            // read through its typed property or its named constant; a comparison against a
            // literal is how an assumption about a numbering gets compiled in.
            string facts = Facts();
            int read = facts.IndexOf("private static WallSplitRejection ReadBlockingConditions", StringComparison.Ordinal);
            int end = facts.IndexOf("private static WallAssemblyFacts ReadAssembly", read, StringComparison.Ordinal);
            Assert.True(end > read, "ReadBlockingConditions was not found");
            string region = facts.Substring(read, end - read);

            // CODE ONLY. The comment above the fix names the parameter it stopped reading,
            // and an assertion that cannot tell prose from code would fail on the very
            // explanation of the defect it is guarding against.
            string code = string.Join(" ", region.Split('\n')
                .Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));

            // The two integer reads that remain are BOOLEAN parameters, where 0 and non-zero
            // are the whole domain and there is no enum to get wrong.
            foreach (string allowed in new[] { "WALL_TOP_IS_ATTACHED", "WALL_BOTTOM_IS_ATTACHED" })
                Assert.Contains(allowed, code);
            Assert.DoesNotContain("WALL_CROSS_SECTION", code);
        }

        [Fact]
        public void There_is_no_fallback_to_the_live_curve_reference()
        {
            // THE FIX'S OWN DEFECT, caught in review. The first version of it was:
            //
            //     try   { subject.LocationCurve = location.Curve.CreateTransformed(...); }
            //     catch { subject.LocationCurve = location.Curve; }
            //
            // The catch reinstates the exact reference whose staleness IS the defect. A
            // fallback like that does not remove the failure, it makes it rare - and a rare
            // version of this one reaches somebody's model instead of a test.
            string facts = Facts();
            Assert.DoesNotContain("catch { subject.LocationCurve = location.Curve; }", facts);
            Assert.DoesNotContain("subject.LocationCurve = location.Curve;", facts);

            // What happens instead: a refusal, before any transaction exists.
            Assert.Contains("if (detached == null)", facts);
            Assert.Contains("an independent copy of this wall's location curve could not be made", facts);
            Assert.Contains("subject.LocationCurve = detached;", facts);
        }

        [Fact]
        public void A_curve_that_cannot_be_detached_is_refused_before_any_write()
        {
            // The refusal has to sit in the READ pass, which runs before a transaction is
            // opened - not somewhere in the executor where the carrier is already converted.
            string facts = Facts();
            int read = facts.IndexOf("private static WallSplitRejection ReadBlockingConditions", StringComparison.Ordinal);
            int end = facts.IndexOf("private static WallAssemblyFacts ReadAssembly", read, StringComparison.Ordinal);
            Assert.True(end > read, "ReadBlockingConditions was not found");
            string region = facts.Substring(read, end - read);

            Assert.Contains("Curve detached = null;", region);
            Assert.Contains("if (detached == null)", region);
            Assert.Contains("WallSplitCodes.UnsupportedCurve", region);
            Assert.Contains("Nothing was written.", region);
        }

        [Fact]
        public void The_original_curve_is_an_independent_copy_not_the_live_reference()
        {
            // SECOND LIVE DEFECT. LocationCurve.Curve hands back a wrapper over geometry
            // Revit owns; converting the carrier REPLACES that curve, and every later read
            // off the old wrapper throws. The carrier's target was computed before the
            // conversion and worked; the secondary layers' curves were computed after it and
            // every single one came back null - "layer 01's curve could not be built" - on
            // eleven live cases in a row, on ordinary straight walls.
            string facts = Facts();
            Assert.Contains("location.Curve.CreateTransformed(Transform.Identity)", facts);
            Assert.Contains("AN INDEPENDENT COPY, not the live reference", facts);

            // AND THE COPY IS WHAT GETS STORED. Asserting that the call appears says
            // nothing about where its result goes: mutation showed that assigning
            // location.Curve instead left this test green while reinstating the defect.
            Assert.Contains("subject.LocationCurve = detached;", facts);
        }

        [Fact]
        public void No_curve_fact_is_read_from_the_live_reference_once_the_copy_exists()
        {
            // THE RESIDUAL, found in review after the fallback was removed. The copy was
            // being made and stored correctly, and then the class, the length and the curve
            // KIND were all still read off location.Curve:
            //
            //     subject.CurveClass = location.Curve.GetType().Name;
            //     if (location.Curve.Length < ...)
            //     if (location.Curve is Line)  /  if (location.Curve is Arc)
            //
            // Nothing is written at this point, so this did not crash. It is still wrong,
            // and not only stylistically: those three facts DECIDE whether the wall is
            // accepted, so they must describe the object the executor will actually use. A
            // check that vouches for a different curve than the one being split is a check
            // that can pass for the wrong reason.
            string facts = Facts();
            int read = facts.IndexOf("private static WallSplitRejection ReadBlockingConditions", StringComparison.Ordinal);
            int end = facts.IndexOf("private static WallAssemblyFacts ReadAssembly", read, StringComparison.Ordinal);
            Assert.True(end > read, "ReadBlockingConditions was not found");
            string region = facts.Substring(read, end - read);

            // CODE ONLY - the comments in this region quote location.Curve to explain the
            // defect, and an assertion that cannot tell prose from code would fail on the
            // explanation of the very thing it guards.
            string code = string.Join("\n", region.Split('\n')
                .Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));

            // EXACTLY TWO reads of the live reference survive, both before the copy exists:
            // the null guard, and the single call that produces the copy.
            Assert.Equal(2, CountOf(code, "location.Curve"));
            Assert.Contains("if (location == null || location.Curve == null)", code);
            Assert.Contains("location.Curve.CreateTransformed(Transform.Identity)", code);

            // And after the copy is stored, not one more.
            int stored = code.IndexOf("subject.LocationCurve = detached;", StringComparison.Ordinal);
            Assert.True(stored > 0, "the copy is not stored");
            Assert.DoesNotContain("location.Curve", code.Substring(stored));

            // Each of the four facts, named, off the copy.
            Assert.Contains("subject.CurveClass = detached.GetType().Name;", code);
            Assert.Contains("if (detached.Length < WallLayerRules.ToleranceFeet)", code);
            Assert.Contains("if (detached is Line)", code);
            Assert.Contains("if (detached is Arc)", code);
        }

        [Fact]
        public void A_throwing_detach_leaves_the_copy_null_so_the_wall_is_refused()
        {
            // CreateTransformed can fail two ways: return null, or throw. Both have to end
            // in the same refusal, and the catch is where a well-meaning recovery would go
            // in - which is exactly how the removed fallback got written the first time.
            string facts = Facts();
            int read = facts.IndexOf("private static WallSplitRejection ReadBlockingConditions", StringComparison.Ordinal);
            int end = facts.IndexOf("private static WallAssemblyFacts ReadAssembly", read, StringComparison.Ordinal);
            string region = facts.Substring(read, end - read);

            // ANCHORED TO THE DETACH BLOCK. This method has an earlier catch (Exception ex)
            // of its own, and searching from the top of the region found that one instead -
            // the assertion then read a slice that had nothing to do with detaching.
            int block = region.IndexOf("Curve detached = null;", StringComparison.Ordinal);
            Assert.True(block > 0, "the detach block was not found");
            int caught = region.IndexOf("catch (Exception ex)", block, StringComparison.Ordinal);
            int refuse = region.IndexOf("if (detached == null)", block, StringComparison.Ordinal);
            Assert.True(caught > block, "the detach failure is not caught at all");
            Assert.True(refuse > caught, "the refusal does not follow the catch");

            // THE CATCH ASSIGNS NOTHING TO detached. It records why, and lets the null
            // reach the refusal below. Any assignment here is a recovery, and a recovery
            // here can only be the live reference.
            string handler = region.Substring(caught, refuse - caught);
            Assert.DoesNotContain("detached =", handler);
            Assert.Contains("detachFailure = ex.Message;", handler);

            // The message says what failed and that nothing happened to the model.
            Assert.Contains("an independent copy of this wall's location curve could not be made", region);
            Assert.Contains("Nothing was written.", region);
        }

        [Fact]
        public void Every_layer_curve_is_built_before_anything_is_written()
        {
            // Fail-early. A wall whose curves cannot be built is refused before the first
            // write rather than rolled back after the carrier has already been converted.
            string executor = Executor();
            int pre = executor.IndexOf("var targetCurves = new Dictionary<int, Curve>();", StringComparison.Ordinal);
            int convert = executor.IndexOf("carrier.ChangeTypeId(", StringComparison.Ordinal);
            Assert.True(pre > 0, "the curves are not precomputed at all");
            Assert.True(pre < convert, "the curves are computed AFTER the carrier is converted");

            // AND NOTHING RECOMPUTES A TARGET AFTERWARDS. The old form of this assertion
            // named one variable - OffsetCurve(originalCurve - so renaming the variable, or
            // offsetting from anything else, walked straight past it. What matters is that
            // no curve is CONSTRUCTED at all once the carrier has been converted: after that
            // point every placement reads targetCurves, which was filled while the original
            // was still the original.
            int joins = executor.IndexOf("private static Fail RestoreJoins", convert, StringComparison.Ordinal);
            Assert.True(joins > convert, "the end of Convert was not found");
            string afterConvert = executor.Substring(convert, joins - convert);
            Assert.DoesNotContain("OffsetCurve(", afterConvert);
            Assert.Contains("targetCurves[layer.LayerIndex]", afterConvert);
        }

        [Fact]
        public void The_joined_but_disjoint_warning_is_never_suppressed()
        {
            // THIS TEST GUARDS A REVERSAL, so it is written to fail on the tempting fix
            // rather than on the current code.
            //
            // Converting a 7-layer wall whose carrier is layer 05 leaves two STANDING
            // Revit warnings - "joined but do not intersect", between the carrier and
            // layers 01 and 02, which the layers in between separate from it. Adding
            // those failure ids to the expected set makes all_verified go true again and
            // corrects NOTHING: the join between two walls that do not touch is still
            // there. Silencing the complaint deletes the only evidence it exists.
            //
            // It was written, measured, and reverted. The ids stay out until the
            // executor stops constructing the invalid join.
            string cmd = CodeOnly(Command());
            Assert.DoesNotContain("JoiningDisjoint", cmd);
            Assert.DoesNotContain("ExpectedBetweenOwnWalls", cmd);
            Assert.DoesNotContain("AllOurs", cmd);
        }

        [Fact]
        public void Exactly_one_warning_is_expected_by_construction()
        {
            // The expected set is a whitelist of things this operation is ALLOWED to
            // produce silently, so its size is the interesting property: every entry is
            // a class of evidence nobody will ever see again. There is one.
            string cmd = CodeOnly(Command());
            int at = cmd.IndexOf("private static readonly HashSet<FailureDefinitionId> Expected", StringComparison.Ordinal);
            Assert.True(at > 0, "the expected set was not found");
            int close = cmd.IndexOf("};", at, StringComparison.Ordinal);
            string set = cmd.Substring(at, close - at);

            Assert.Contains("BuiltInFailures.OverlapFailures.WallsOverlap", set);
            Assert.Equal(1, CountOf(set, "BuiltInFailures."));
        }

        [Fact]
        public void A_warning_that_is_not_expected_is_reported_rather_than_deleted()
        {
            // The consequence that matters: anything not on the whitelist travels back
            // with the reply and takes all_verified down. Deleting it here would be
            // indistinguishable, from outside, from the operation having been clean.
            string cmd = CodeOnly(Command());
            int at = cmd.IndexOf("public FailureProcessingResult PreprocessFailures", StringComparison.Ordinal);
            Assert.True(at > 0, "the preprocessor was not found");
            string body = cmd.Substring(at);

            // Exactly one DeleteWarning, and it is inside the expected-set branch.
            Assert.Equal(1, CountOf(body, "accessor.DeleteWarning(failure);"));
            int guard = body.IndexOf("if (Expected.Contains(failure.GetFailureDefinitionId()))", StringComparison.Ordinal);
            int del = body.IndexOf("accessor.DeleteWarning(failure);", StringComparison.Ordinal);
            Assert.True(guard > 0 && guard < del, "the only deletion is not guarded by the expected set");

            Assert.Contains("_unexpected.Add(failure.GetDescriptionText());", body);
        }

        [Fact]
        public void The_executor_asks_the_rule_instead_of_calling_All_on_the_cut_checks()
        {
            // The rule is unit-tested for real, so what is left to get wrong is the
            // WIRING: the executor bypassing it and going back to the unguarded .All()
            // that published a pass over an empty set.
            string ex = CodeOnly(Executor());
            Assert.Contains("WallLayerRules.CutClaim(layer.IsCoreCarrier, layer.Materialised,", ex);
            Assert.Contains("WallLayerRules.CutNotProbedReason(", ex);

            // And the coverage flag is READ, not assumed: without it a wall whose probe
            // never ran would still reach the check-count branch.
            Assert.Contains("verdict.CutCoverage.Value<bool?>(\"probed\")", ex);

            // The old shape is gone: no All() over cut_verified anywhere.
            Assert.DoesNotContain(".All(c => c.Value<bool>(\"cut_verified\"))", ex);
        }

        [Fact]
        public void A_rolled_back_wall_withdraws_every_verification_claim()
        {
            // FillLayerOutcomes runs INSIDE Convert, before the !verdict.Passed return, so
            // the layer rows exist whatever happens. When the wall then rolls back, those
            // rows describe walls the rollback has just undone - and they were still
            // carrying geometry_verified, naming_verified, single_layer_verified,
            // join_verified and cut_verified. A claim about a wall that no longer exists
            // cannot be checked by anybody and reads as though it could.
            string ex = CodeOnly(Executor());

            // The withdrawal happens on the failure path.
            //
            // NOT asserted: that it happens BEFORE Guard.RollBack. That assertion was
            // written and mutation killed it - outcome.Layers is a plain C# list, so
            // rolling the SubTransaction back does not touch it and either order emits
            // identical JSON. An assertion no mutation can break is describing a
            // preference, not a requirement, and it was removed rather than kept.
            Assert.Contains("foreach (LayerOutcome layer in outcome.Layers) layer.ClaimsWithdrawn = true;", ex);

            // And EVERY verified flag is nulled, not just the cut one.
            foreach (string field in new[] { "naming_verified", "geometry_verified",
                                             "single_layer_verified", "join_verified" })
                Assert.Contains("[\"" + field + "\"] = ClaimsWithdrawn ? (JToken)JValue.CreateNull()", ex);

            Assert.Contains("(ClaimsWithdrawn || !CutVerified.HasValue)", ex);
            Assert.Contains("[\"cut_probed\"] = !ClaimsWithdrawn && CutProbed", ex);
        }

        [Fact]
        public void The_executor_builds_a_chain_and_refuses_a_join_across_a_gap()
        {
            // Measured on four identical seven-layer walls with a door: no joins at all
            // left every secondary layer holding exactly its own thickness of material;
            // the star and the chain both cut all of them. So the join carries the cut and
            // the cut is transitive - and only the star joins walls that are apart, which
            // is what Revit records "joined but do not intersect" about, permanently.
            string ex = CodeOnly(Executor());

            Assert.Contains("WallLayerRules.ChainEdges(", ex);
            Assert.Contains("WallLayerRules.LayersTouch(a.ExpectedOffsetFeet, a.WidthFeet,", ex);
            Assert.Contains("WallSplitCodes.VerifyJoinDisjoint", ex);

            // The star is gone: nothing joins the carrier to every created wall.
            Assert.DoesNotContain("JoinGeometryUtils.JoinGeometry(doc, carrier, layerWall)", ex);

            // THE CARRIER IS ONE OF THE LAYERS. `created` holds only the walls this step
            // makes; the carrier is the ORIGINAL wall and is never added to it. The first
            // version of this chain looked every layer up in `created` alone and refused
            // its own wall - measured live: "the chain needs layers 04 and 05 and one of
            // them has no wall", the whole wall rolled back, confirmed.
            Assert.Contains("var wallsByLayer = new Dictionary<int, Wall>(created);", ex);
            Assert.Contains("wallsByLayer[approved.Plan.CoreCarrierLayerIndex] = carrier;", ex);
            Assert.Contains("wallsByLayer.TryGetValue(edge[0], out Wall wa)", ex);
            Assert.DoesNotContain("created.TryGetValue(edge[0]", ex);

            // and the graph is re-read over the SAME set, carrier included
            Assert.Contains("new HashSet<long>(wallsByLayer.Values.Select(w => Rid.Value(w.Id)))", ex);

            // And the graph is RE-READ, not assumed from the calls succeeding. The GUARD
            // is asserted, not the vocabulary around it: mutation showed that turning the
            // condition into `if (false)` left every one of these names in place, so a
            // Contains on the names alone was proving nothing.
            Assert.Contains("JoinGeometryUtils.GetJoinedElements(doc, w)", ex);
            Assert.Contains("WallSplitCodes.VerifyJoinUnexpected", ex);
            Assert.Contains("if (!expectedEdges.Contains(key))", ex);
            Assert.Contains("if (!seenEdges.Contains(key))", ex);
            Assert.Contains("if (!siblingIds.Contains(other))", ex);
        }

        [Fact]
        public void The_verifier_holds_the_model_to_the_chain()
        {
            // Same rule, same source: the verifier computes the expected graph from
            // WallLayerRules.ChainEdges too, so the two cannot describe different graphs
            // the way NeverCopied and AllowedToChange described different parameters.
            string ve = CodeOnly(Verifier());
            Assert.Contains("WallLayerRules.ChainEdges(", ve);
            Assert.Contains("WallLayerRules.LayersTouch(", ve);
            Assert.Contains("chain_missing", ve);
            Assert.Contains("chain_extra", ve);
            Assert.Contains("chain_foreign", ve);
            Assert.Contains("chain_disjoint", ve);
            Assert.Contains("chain_intact", ve);

            // THE GUARDS THEMSELVES. Each of these was `if (false)`-ed by a mutation and
            // the surrounding names all survived, so naming them proved nothing.
            Assert.Contains("if (disjointEdges.Count > 0)", ve);
            Assert.Contains("if (missing.Count > 0)", ve);
            Assert.Contains("if (extraEdges.Count > 0 || foreignEdges.Count > 0)", ve);
            Assert.Contains("chain_intact\"] = missing.Count == 0 && extraEdges.Count == 0 &&", ve);

            // The old star gate is gone.
            Assert.DoesNotContain("are not joined to the carrier, so", ve);
        }

        [Fact]
        public void The_type_name_is_still_original_material_number()
        {
            Assert.Equal("EXT - Ladrillo - 01", WallLayerRules.ComposeTypeName("EXT", "Ladrillo", 1));
            Assert.Equal("EXT - MATERIAL_SIN_ASIGNAR - 07", WallLayerRules.ComposeTypeName("EXT", "  ", 7));
            Assert.Equal("EXT - M - 10", WallLayerRules.ComposeTypeName("EXT", "M", 10));
        }

        [Fact]
        public void The_verifier_holds_the_wall_to_its_expected_type_name()
        {
            Assert.Contains("naming_verified", Verifier());
            Assert.Contains("WallSplitCodes.VerifyTypeMismatch", Verifier());
        }

        // ---- an empty selection is not a request to convert everything -------------

        /// <summary>
        /// ResolveScope reads element_ids only when it has entries, and otherwise falls
        /// through to view_id and then to EVERY WALL IN THE DOCUMENT. That default is
        /// documented and fine for an OMITTED field, but an array that is present and
        /// empty is what a caller sends when its own filter matched nothing - and reading
        /// that as the whole model converts a document on the strength of an empty
        /// selection.
        ///
        /// The guard has to come BEFORE ResolveScope, which is what this pins: a check
        /// that ran afterwards would be deciding what to do with walls it had already
        /// collected, and the ordering is the whole property.
        ///
        /// Comments are stripped first. The reasoning for this guard is written in a
        /// comment directly above it, so a Contains over the raw file would stay green
        /// with the guard itself deleted.
        /// </summary>
        [Fact]
        public void An_empty_element_ids_is_refused_before_the_scope_is_ever_resolved()
        {
            string code = CodeOnly(Command());

            int guard = code.IndexOf("declaredIds != null && declaredIds.Count == 0",
                                     StringComparison.Ordinal);
            Assert.True(guard >= 0,
                "a present-but-empty element_ids must be refused, not widened to the whole model");

            int resolve = code.IndexOf("ResolveScope(doc, request", StringComparison.Ordinal);
            Assert.True(resolve >= 0, "the call to ResolveScope was not found");

            Assert.True(guard < resolve,
                "the empty-selection guard must run BEFORE ResolveScope; after it, the walls " +
                "of the whole document have already been collected");
        }

        /// <summary>
        /// And the refusal has to be in the CONTRACT, not only in the code, because the
        /// schema description is the only place a caller finds out what an empty array
        /// does before sending one.
        /// </summary>
        [Fact]
        public void The_contract_tells_callers_that_an_empty_element_ids_is_refused()
        {
            CommandContract c = Contract.Find("horizun_split_multilayer_walls");
            Assert.NotNull(c);

            string ids = c.InputSchema["properties"]["element_ids"]["description"].ToString();
            Assert.Contains("EMPTY array is REFUSED", ids, StringComparison.Ordinal);
        }
    }
}
