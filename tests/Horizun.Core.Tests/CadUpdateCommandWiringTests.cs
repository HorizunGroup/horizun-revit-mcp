// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// PLACEMENT IDENTITY, GUARDED IN SOURCE where it cannot be executed.
//
// The decisions live in CadPlacementRules and CadUpdateRules and are exercised
// directly by CadPlacementScopeTests. What those tests cannot see is whether the
// two Revit-bound commands actually CALL them, read the new arguments, and put
// the results in the reply - and that seam is exactly where the geometry_id
// defect lived: the apply read a field the plan never emitted, so every element
// an incremental run created was stamped with GeometryId null, and no test could
// fail because the rules were right and the wiring was not.
//
// So these guards read the command sources and assert the seams. A guard on
// text is a weak test; it is the strongest one available for a file that needs
// a UIApplication to run, and it is the pattern this repository uses for that.
// -----------------------------------------------------------------------------
using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace Horizun.Core.Tests
{
    public class CadUpdateCommandWiringTests
    {
        private static DirectoryInfo Root()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !Directory.Exists(Path.Combine(d.FullName, "src"))) d = d.Parent;
            Assert.NotNull(d);
            return d;
        }

        private static string Source(params string[] parts)
        {
            return File.ReadAllText(Path.Combine(Root().FullName, Path.Combine(parts)));
        }

        private static string Plan() => Source("src", "Horizun.Revit", "Commands", "PlanCadUpdateCommand.cs");
        private static string Apply() => Source("src", "Horizun.Revit", "Commands", "ApplyCadUpdateCommand.cs");
        private static string Store() => Source("src", "Horizun.Revit", "Core", "CadProvenanceStore.cs");
        private static string Contract() => Source("src", "Horizun.Contracts", "Contract.cs");

        [Fact]
        public void A_placed_DWG_is_sampled_by_its_total_transform_so_a_move_can_be_verified()
        {
            // Measured 2026-09-03: the typed move refused an ImportInstance as
            // unsampleable, and an incremental update cannot detect a placement it
            // cannot move under test. Instance covers ImportInstance and links alike.
            string src = Source("src", "Horizun.Revit", "Commands", "TransformElementsCommand.cs");
            Assert.Contains("var instance = e as Instance;", src);
            Assert.Contains("instance.GetTotalTransform()", src);
        }

        [Fact]
        public void A_hosted_instance_keeps_its_host_or_the_transform_is_rolled_back_and_a_turn_turns_it()
        {
            // MEASURED on a face-hosted receptacle: a 300 mm move along its wall left
            // the face, Revit kept the device with NO host, and the point check
            // verified the move. And a turn about an axis through the element's own
            // point moves no point, so the point check alone verifies any turn.
            string src = Source("src", "Horizun.Revit", "Commands", "TransformElementsCommand.cs");
            int apply = src.IndexOf("foreach (Plan p in plans) Apply(doc, p);", StringComparison.Ordinal);
            int guard = src.IndexOf("foreach (Plan p in plans) GuardHosts(doc, p);", StringComparison.Ordinal);
            int commit = src.IndexOf("Guard.Commit(tx, txName);", StringComparison.Ordinal);
            Assert.True(apply > 0 && guard > apply && commit > guard, "the host guard runs after the change and before the commit");
            Assert.Contains("\"host_changed: \" + p.Operation", src);
            Assert.Contains("double? off = OffItsFaceMm(element);", src);
            Assert.Contains("private const double FaceToleranceMm = 0.5;", src);
            Assert.Contains("if (host.HasValue) p.HostBefore[raw] = host.Value;", src);
            Assert.Contains("if (op == \"rotate\" || op == \"mirror\") p.Axes[raw] = Axes(element);", src);
            Assert.Contains("case \"mirror\": ElementTransformUtils.MirrorElements(doc, p.Ids, p.MirrorPlane, false); break;", src);
            Assert.Contains("p.Rotation.OfVector(axesBefore[1]).IsAlmostEqualTo(axesAfter[1], 1e-6)", src);
            Assert.Contains("detail[\"orientation_not_turned\"] = why;", src);
            Assert.Contains("rolled back whole with host_changed", Contract());
        }

        [Fact]
        public void A_procedure_can_hand_the_rehearsal_tokens_on_whole_and_an_empty_plan_writes_nothing()
        {
            string src = Source("src", "Horizun.Revit", "Commands", "ApplyCadPlanCommand.cs");
            Assert.Contains("(request[\"confirmation_tokens\"] as JObject)?.Value<string>((string)action[\"key\"])", src);
            Assert.Contains("[\"state\"] = \"nothing_to_apply\"", src);
            Assert.Contains("[\"rehearsal\"] = new JObject { [\"tokens_by_key\"] = new JObject() }", src);
            // The empty answer comes after the binding was re-measured, never before.
            int drift = src.IndexOf("if (drift.Count > 0)", StringComparison.Ordinal);
            int empty = src.IndexOf("[\"state\"] = \"nothing_to_apply\"", StringComparison.Ordinal);
            Assert.True(drift > 0 && empty > drift);
            Assert.Contains("\"\"confirmation_tokens\"\": { \"\"type\"\": \"\"object\"\"", Contract());
        }

        // ------------------------------------------------------- geometry_id

        [Fact]
        public void The_plan_emits_geometry_id_and_the_apply_stores_it()
        {
            // The defect: the apply read geometry_id off a candidate_index row
            // the plan never wrote. Both halves are asserted, because either one
            // alone reproduces the null.
            Assert.Contains("[\"geometry_id\"] = a.GeometryId", Plan());
            Assert.Contains("GeometryId = entry.Value<string>(\"geometry_id\")", Apply());
        }

        // ------------------------------------------------------- scope

        [Fact]
        public void The_plan_scopes_by_placement_and_reads_both_lineage_arguments()
        {
            string src = Plan();
            Assert.Contains("CadFacts.Placement(facts)", src);
            Assert.Contains("CadPlacementRules.Resolve(minesUnderThisSet, placement, lineage,", src);
            Assert.Contains("request[\"supersedes_sha256\"]", src);
            Assert.Contains("request[\"supersedes_placement_ids\"]", src);
            // The scoped overload, not the file-scoped one.
            Assert.Contains("CadUpdateRules.Plan(interpretation.Candidates, subjects, set, scope,", src);
            Assert.DoesNotContain("CadUpdateRules.Plan(interpretation.Candidates, subjects, set,\n" +
                                  "                                                   facts.FileSha256", src);
            // ...and the verdict reaches the reply.
            Assert.Contains("[\"scope\"] = scope.ToJson()", src);
            Assert.Contains("[\"identity\"] = identity.ToJson()", src);
        }

        [Fact]
        public void An_ambiguous_v1_record_is_refused_BEFORE_anything_else_is_decided()
        {
            // The ordering is the guard. An ambiguous v1 element is out of scope,
            // and out of scope is not safe by itself: its drawing entity then
            // matches nothing and is planned as a create, so applying builds a
            // second wall on top of the one standing (CadProvenanceV1MigrationTests
            // measures exactly that). The refusal therefore has to come before the
            // claimable count, before the transform comparison, and before any
            // action is derived - not after, and not only when nothing else is
            // claimable.
            string src = Plan();
            int ambiguous = src.IndexOf("if (scope.AmbiguousV1.Count > 0)", StringComparison.Ordinal);
            int claimable = src.IndexOf("if (scope.ClaimableCount == 0)", StringComparison.Ordinal);
            int move = src.IndexOf("CadPlacementRules.CompareTransforms(s.Provenance, placement)", StringComparison.Ordinal);
            int plan = src.IndexOf("CadUpdateRules.Plan(interpretation.Candidates", StringComparison.Ordinal);
            Assert.True(ambiguous > 0, "the plan must refuse an ambiguous v1 scope");
            Assert.Contains("CadPlacementRules.AmbiguousV1Refusal(scope, title)", src);
            Assert.True(ambiguous < claimable, "ambiguity is refused before the claimable-count guard");
            Assert.True(ambiguous < move, "ambiguity is refused before the placement move is compared");
            Assert.True(ambiguous < plan, "ambiguity is refused before a single action is derived");
        }

        [Fact]
        public void A_run_that_can_claim_nothing_refuses_instead_of_reporting_zero_changes()
        {
            string src = Plan();
            Assert.Contains("if (scope.ClaimableCount == 0)", src);
            Assert.Contains("CadPlacementRules.UnidentifiedRefusal(scope, title)", src);
            Assert.Contains("supersedes_unstated:", src);
            Assert.Contains("supersedes_ambiguous:", src);
        }

        // ------------------------------------------------------- transform

        [Fact]
        public void A_moved_placement_is_refused_unless_the_caller_accepts_it_at_both_ends()
        {
            string plan = Plan();
            Assert.Contains("CadPlacementRules.CompareTransforms(s.Provenance, placement)", plan);
            Assert.Contains("request.Value<bool?>(\"accept_placement_move\")", plan);
            Assert.Contains("\"placement_moved: CAD instance \"", plan);
            Assert.Contains("acceptMove ? move : null", plan);
            Assert.Contains("[\"placement_move_accepted\"] = move != null && acceptMove", plan);

            string apply = Apply();
            Assert.Contains("provenanceTemplate.Value<bool?>(\"placement_move_accepted\")", apply);
            Assert.Contains("request.Value<bool?>(\"accept_placement_move\")", apply);
            Assert.Contains("if (planUnderMove && !acceptMove)", apply);
        }

        // ------------------------------------------------------- migration

        [Fact]
        public void The_store_keeps_every_older_guid_reads_newest_first_and_writes_only_v3()
        {
            string src = Store();
            Assert.Contains("SchemaGuidV1 = new Guid(\"7b2f4c18-5d3a-4e6b-9a71-3c0f8e2d15a4\")", src);
            Assert.Contains("SchemaGuidV2 = new Guid(\"c4a7e9d2-6b18-4f3c-8e5a-2d91f07b6c43\")", src);
            Assert.Contains("SchemaGuidV3 = new Guid(\"5e0d3b7a-91c4-4f26-b8e3-7a2c6d19f40e\")", src);
            Assert.Contains("SchemaGuidV4 = new Guid(\"ff45f28f-b5b8-4ebd-bdc0-1bec2bd373f5\")", src);
            Assert.Contains("public const int CurrentVersion = 4;", src);
            Assert.Contains("new SchemaBuilder(SchemaGuidV4)", src);
            Assert.DoesNotContain("new SchemaBuilder(SchemaGuidV3)", src);
            Assert.DoesNotContain("new SchemaBuilder(SchemaGuidV2)", src);
            Assert.DoesNotContain("new SchemaBuilder(SchemaGuidV1)", src);
            Assert.Contains("AllSchemaGuids = { SchemaGuidV4, SchemaGuidV3, SchemaGuidV2, SchemaGuidV1 }", src);
            // Read: v4, v3, v2, then v1; placement fields from v2 on, the reading from v3, the set from v4.
            int v4 = src.IndexOf("Schema schema = Schema.Lookup(SchemaGuidV4);", StringComparison.Ordinal);
            int v3 = src.IndexOf("(schema = Schema.Lookup(SchemaGuidV3)) != null", StringComparison.Ordinal);
            int v2 = src.IndexOf("(schema = Schema.Lookup(SchemaGuidV2)) != null", StringComparison.Ordinal);
            int v1 = src.IndexOf("schema = Schema.Lookup(SchemaGuidV1);", StringComparison.Ordinal);
            Assert.True(v4 > 0 && v3 > v4 && v2 > v3 && v1 > v2, "Read must look for v4, then v3, v2, v1");
            Assert.Contains("if (v4)\n                    p.SourceSetSha256", src);
            Assert.Contains("if (v2)\n                {\n                    p.PlacementId", src);
            Assert.Contains("if (v3)\n                {\n                    p.InterpretationVersion", src);
            // Write: the older entities are removed AFTER the v3 write landed.
            Assert.Contains("element.SetEntity(entity);", src);
            Assert.True(src.IndexOf("RemoveOlder(element);", StringComparison.Ordinal) >
                        src.IndexOf("element.SetEntity(entity);", StringComparison.Ordinal));
            Assert.Contains("foreach (Guid guid in new[] { SchemaGuidV3, SchemaGuidV2, SchemaGuidV1 })", src);
        }

        [Fact]
        public void Every_collector_of_stamped_elements_goes_through_the_store()
        {
            // A filter on the current GUID alone loses every v1 conversion the
            // day the writer moves to v2. There must be exactly ONE place that
            // enumerates the GUIDs, and it is the store.
            foreach (string file in Directory.GetFiles(Path.Combine(Root().FullName, "src", "Horizun.Revit"), "*.cs",
                                                       SearchOption.AllDirectories))
            {
                if (file.EndsWith("CadProvenanceStore.cs", StringComparison.Ordinal)) continue;
                string text = File.ReadAllText(file);
                Assert.False(Regex.IsMatch(text, @"ExtensibleStorageFilter\(CadProvenanceStore\.SchemaGuid"),
                    Path.GetFileName(file) + " filters on CadProvenanceStore.SchemaGuid directly; use CadProvenanceStore.Holders(doc)");
            }
            Assert.Contains("CadProvenanceStore.Holders(doc)", Plan());
            Assert.Contains("CadProvenanceStore.Holders(doc)",
                            Source("src", "Horizun.Revit", "Commands", "AuditCadModelCommand.cs"));
        }

        [Fact]
        public void Both_writers_stamp_the_placement_and_the_apply_migrates_v1_records_with_a_count()
        {
            string first = Source("src", "Horizun.Revit", "Commands", "ApplyCadPlanCommand.cs");
            Assert.Contains("PlacementId = facts.UniqueId", first);
            Assert.Contains("PlacementTransform = facts.TransformFingerprint", first);
            Assert.Contains("CadPlacementRules.EncodeOrigin(facts.TransformOrigin)", first);

            string apply = Apply();
            Assert.Contains("StampPlacement(p, placementTemplate)", apply);
            Assert.Contains("RestampKey = \"cad-update-restamp\"", apply);
            Assert.Contains("[\"provenance_rewritten\"] = restamped", apply);
            Assert.Contains("[\"migrated_from_v1\"] = migrated", apply);

            string plan = Plan();
            Assert.Contains("Restamp(update, scope, move != null && acceptMove, subjects, facts.FileSha256, sourceSet,", plan);
            // the drawing is read WITH its references: the set's identity travels beside the host's
            Assert.Contains("string sourceSet = CadDwgCache.SourceSetSha256(facts.ExternalPath, facts.FileSha256);", plan);
            Assert.Contains("SourceFileSha256 = facts.FileSha256,", first);
            Assert.Contains("SourceSetSha256 = CadDwgCache.SourceSetSha256(facts.ExternalPath, facts.FileSha256),", first);
            Assert.DoesNotContain("?? facts.FileSha256", plan);
            Assert.Contains("[\"key\"] = \"cad-update-restamp\"", plan);
            // What the update verified is carried to the new revision; a relayered
            // match is not, or the change the review is about would disappear.
            Assert.Contains("reason = CadPlacementRules.RestampCarried", plan);
            Assert.Contains("a.Classification != CadChange.Relayered", plan);
            // ...and only what the update left as it is: a held change keeps its revision.
            Assert.Contains("else if (a.Kind == \"leave\" && a.CandidateId != null", plan);
            Assert.Contains("if (reason == CadPlacementRules.RestampCarried || reason == CadPlacementRules.RestampAccepted)", apply);
        }

        [Fact]
        public void An_earlier_version_of_the_same_rules_is_claimed_only_when_declared()
        {
            // MEASURED (revision C): a rules change - one test type mapped to another -
            // refused as scope_unidentified, because every element named the old rules.
            string plan = Plan();
            Assert.Contains("request[\"supersedes_requirement_set_sha256\"]", plan);
            Assert.Contains("rulesLineage.Contains(s.Provenance.RequirementSetSha256))", plan);
            // another set's elements are refused, not claimed
            Assert.Contains("rules_lineage_other_set:", plan);
            Assert.Contains("!string.Equals(s.Provenance.RequirementSetId, set.Id, StringComparison.Ordinal)", plan);
            Assert.Contains("reason = CadPlacementRules.RestampRulesSuperseded;", plan);
            Assert.Contains("[\"claimed_under_earlier_rules\"] = underEarlierRules", plan);

            string apply = Apply();
            // newer rules are claimed only when every action applied
            Assert.Contains("if (reason == CadPlacementRules.RestampRulesSuperseded && failures > 0)", apply);
            Assert.Contains("p.RequirementSetSha256 = provenanceTemplate.Value<string>(\"requirement_set_sha256\")", apply);
            Assert.Contains("supersedes_requirement_set_sha256", Source("src", "Horizun.Contracts", "Contract.cs"));
        }

        [Fact]
        public void An_accepted_split_is_carried_out_whole_or_held_whole()
        {
            string plan = Plan();
            // decided before its pieces are planned: width, hosted instances, occupancy
            Assert.Contains("HoldSplit(update, a, \"kept_piece_is_another_thickness\"", plan);
            Assert.Contains("double widthTolerance = keptRule?.WallTypeToleranceMm ?? set.ThicknessToleranceMm;", plan);
            // another width is a retype when the set lists the type; the dependents are classified piece by piece
            Assert.Contains("JObject retype = RetypeOperation(doc, a, interpretation, set, out noType);", plan);
            Assert.Contains("HoldSplit(update, a, \"dependents_need_a_person\", dependentsHeld);", plan);
            Assert.Contains("EmitSubstitution(doc, fi, host, set, target, \"cad-update-substitute-\" + sub++, actions, createIndex,", plan);
            Assert.Contains("CadSplitRules.ApplyDecisions(pieces, deps, depCtx?.Decisions", plan);
            Assert.Contains("Rehome(doc, update, set, target, actions, createIndex);", plan);
            string apply = Apply();
            Assert.Contains("CadSplitDependents.Resolve(args, cid => CreatedFor(touched, index, cid), false);", apply);
            Assert.Contains("[\"substitutions\"] = new JArray(", apply);
            // a re-shape puts back what its wall carried along; every action after a write is rehearsed in place
            Assert.Contains("Dictionary<long, Tuple<XYZ, long>> before = HostedPoints(doc, args);", apply);
            Assert.Contains("[\"hosted_kept_in_place\"] = keptInPlace", apply);
            Assert.Contains("bool hadPlaceholder = args.ToString(Formatting.None).Contains(CadSplitDependents.CreatedFor) ||", apply);
            Assert.Contains("wroteAlready;", apply);
            Assert.Contains("if (failures == 0 &&", apply);
            Assert.Contains("[\"hosted_to_substitute\"] = new JArray(", plan);
            Assert.Contains("HoldSplit(update, a, \"kept_piece_occupied\"", plan);
            // its pieces are measured against the element's NEW line, not the one it stands on
            Assert.Contains("reshapedTo);", plan);
            Assert.Contains("reshapedTo.TryGetValue(Rid.Value(w.Id), out next)",
                            Source("src", "Horizun.Revit", "Commands", "PlanFromCadCommand.cs"));
            // a piece that cannot be built holds the element and drops its siblings
            Assert.Contains("HoldSplit(update, a, \"a_piece_cannot_be_built\"", plan);
            Assert.Contains("[\"reason\"] = \"split_held_with_its_element\"", plan);
            // the element is shortened before its pieces are built
            Assert.Contains("if (splitApplied && firstMove > 0)", plan);
            // a shortened wall never leaves what it hosts behind
            Assert.Contains("a.Evidence[\"hosted_outside_the_new_line\"] = beyond;", plan);
            // old id -> the pieces
            Assert.Contains("[\"splits\"] = new JArray(update.Of(\"set_curve\")", plan);
        }

        // ------------------------------------------------------- retries

        [Fact]
        public void The_apply_consults_the_ledger_before_running_and_records_after()
        {
            string apply = Apply();
            Assert.Contains("CadUpdateLedger.Decide(idempotencyKey, actionsFingerprint)", apply);
            Assert.Contains("replay[\"replayed\"] = true", apply);
            Assert.Contains("CadUpdateLedger.LastPartialFor(placementId)", apply);
            Assert.Contains("[\"previous_partial\"] = previousPartial", apply);
            // Recorded after the write, with the real state.
            int record = apply.IndexOf("CadUpdateLedger.Record(idempotencyKey, actionsFingerprint, placementId,", StringComparison.Ordinal);
            int commit = apply.IndexOf("t.Commit();", StringComparison.Ordinal);
            Assert.True(record > commit, "the ledger must record what actually happened, after the commit");
            Assert.Contains("failures == 0 ? \"applied\" : \"partial\", result)", apply);
        }

        // ------------------------------------------------------- contract

        [Fact]
        public void The_contract_declares_the_new_arguments_on_both_tools()
        {
            string contract = Contract();
            int plan = contract.IndexOf("Name = \"horizun_plan_cad_update\"", StringComparison.Ordinal);
            int apply = contract.IndexOf("Name = \"horizun_apply_cad_update\"", StringComparison.Ordinal);
            Assert.True(plan > 0 && apply > 0);
            string planSchema = contract.Substring(plan, contract.IndexOf("new CommandContract", plan, StringComparison.Ordinal) - plan);
            string applySchema = contract.Substring(apply, contract.IndexOf("new CommandContract", apply, StringComparison.Ordinal) - apply);
            Assert.Contains("\"\"supersedes_placement_ids\"\"", planSchema);
            Assert.Contains("\"\"accept_placement_move\"\"", planSchema);
            Assert.Contains("\"\"accept_placement_move\"\"", applySchema);
        }
    }
}
