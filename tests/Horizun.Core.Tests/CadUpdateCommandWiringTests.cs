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
        public void The_store_keeps_the_v1_guid_reads_it_second_and_writes_only_v2()
        {
            string src = Store();
            Assert.Contains("SchemaGuidV1 = new Guid(\"7b2f4c18-5d3a-4e6b-9a71-3c0f8e2d15a4\")", src);
            Assert.Contains("SchemaGuidV2 = new Guid(\"c4a7e9d2-6b18-4f3c-8e5a-2d91f07b6c43\")", src);
            Assert.Contains("public const int CurrentVersion = 2;", src);
            Assert.Contains("new SchemaBuilder(SchemaGuidV2)", src);
            Assert.DoesNotContain("new SchemaBuilder(SchemaGuidV1)", src);
            // Read: v2 first, v1 as the fallback, placement fields only from v2.
            int v2 = src.IndexOf("Schema.Lookup(SchemaGuidV2);\n                if (schema != null)", StringComparison.Ordinal);
            int v1 = src.IndexOf("schema = Schema.Lookup(SchemaGuidV1);", StringComparison.Ordinal);
            Assert.True(v2 > 0 && v1 > v2, "Read must look for v2 before falling back to v1");
            Assert.Contains("if (v2)\n                {\n                    p.PlacementId", src);
            // Write: the v1 entity is removed AFTER the v2 write landed.
            Assert.Contains("element.SetEntity(entity);", src);
            Assert.True(src.IndexOf("RemoveV1(element);", StringComparison.Ordinal) >
                        src.IndexOf("element.SetEntity(entity);", StringComparison.Ordinal));
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
            Assert.Contains("Restamp(update, scope, move != null && acceptMove)", plan);
            Assert.Contains("[\"key\"] = \"cad-update-restamp\"", plan);
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
