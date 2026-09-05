// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// THE RE-AUDIT OF A FINDING THAT IS AN INVENTORY RATHER THAN A DEFECT LIST.
//
// Measured on Revit 2026 (probe D10.5, campaign artifact of 2026-09-04): a link
// whose file is on disk was reloaded, horizun_manage_links re-read
// GetLinkedFileStatus and answered Loaded, the action's state was `applied` -
// and the re-audit answered PERSISTENT, because it judged by disappearance and
// the links check lists every link type with its status, loaded ones included.
//
// Every test below fails against the code before the fix and passes after it,
// EXCEPT the two that pin the defect-list behaviour, which must not move.
//
// The distinction is declared once, per recipe, as CorrectionRecipe.Postcondition:
//   removed_from_finding   - pin, delete, apply a template: the element goes away
//   item_leaves_the_filter - reload: the element stays and its typed code changes
//
// An INVENTORY item that is no longer listed is NOT a success: the type may have
// been deleted, or fallen past `top`. That case is not_verifiable, which is the
// answer that says so.
// -----------------------------------------------------------------------------
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class CorrectionInventoryReAuditTests
    {
        private const string SetFp = "fs:inv";
        private const string Doc = "HZ_M2026";
        private const string DocFp = "df:inv";

        /// <summary>An audit whose links finding is an INVENTORY: two types, one of
        /// each state, exactly as horizun_audit_model emits it.</summary>
        private static FindingSetRecord Audit(params JObject[] linkItems)
        {
            var findings = new JArray(
                new JObject
                {
                    ["check"] = AuditCheckNames.Links, ["finding_id"] = "f:linktypes", ["is_issue"] = true,
                    ["shown"] = linkItems.Length, ["total"] = linkItems.Length,
                    ["items"] = new JArray(linkItems.Cast<JToken>().ToArray())
                },
                new JObject
                {
                    ["check"] = AuditCheckNames.OrphanGroupTypes, ["finding_id"] = "f:groups", ["is_issue"] = true,
                    ["shown"] = 1, ["total"] = 1,
                    ["items"] = new JArray(new JObject { ["element_id"] = "900" })
                });
            return FindingSetRecord.From(SetFp, Doc, DocFp, 20, "2026-01-01T00:00:00Z", findings);
        }

        private static JObject Link(long id, string status) =>
            new JObject { ["id"] = id.ToString(), ["status"] = status, ["name"] = "HZ_OLD_2023.rvt" };

        private static JObject Fresh(params JObject[] items) => new JObject
        {
            ["check"] = AuditCheckNames.Links, ["finding_id"] = "f:after", ["is_issue"] = true,
            ["count"] = items.Length, ["truncated"] = false,
            ["items"] = new JArray(items.Cast<JToken>().ToArray())
        };

        /// <summary>The action the reload recipe builds, with its apply outcome forced.</summary>
        private static CorrectionAction Reload(FindingSetRecord audit, bool applied, params long[] ids)
        {
            var request = new JObject { ["finding_id"] = "f:linktypes" };
            if (ids.Length > 0) request["element_ids"] = new JArray(ids.Select(x => (JToken)x));
            CorrectionAction a = CorrectionSelection.Select(audit, new JArray(request), CorrectionRegistry.Default)
                                                    .Single();
            foreach (CorrectionStep s in a.Steps)
            {
                s.RehearsalOk = true;
                s.ApplyOk = applied;
                s.ApplyState = applied ? "verified_applied" : "failed";
            }
            a.State = applied ? CorrectionActionState.Applied : CorrectionActionState.Failed;
            return a;
        }

        // ------------------------------------------------------------ the regression

        [Fact]
        public void A_reloaded_link_that_now_reads_Loaded_is_CORRECTED_though_it_is_still_listed()
        {
            CorrectionAction a = Reload(Audit(Link(70, "Unloaded")), true);
            JObject r = ReAuditRules.Compare(a, Fresh(Link(70, "Loaded")), false);

            Assert.Equal(ReAuditOutcome.Corrected, (string)r["outcome"]);
            Assert.Equal(new long[] { 70 }, r["elements"]["corrected"].Select(t => (long)t).ToArray());
            Assert.Equal(CorrectionPostcondition.ItemLeavesTheFilter, (string)r["postcondition"]);
            // The evidence, not just the verdict: what the item's own code reads now.
            Assert.Equal("status", (string)r["item_state_field"]);
            Assert.Equal("Loaded", (string)r["item_state_after"]["70"]);
            Assert.Contains("inventory", (string)r["why"]);
        }

        [Fact]
        public void A_link_that_still_reads_Unloaded_is_PERSISTENT()
        {
            CorrectionAction a = Reload(Audit(Link(70, "Unloaded")), true);
            JObject r = ReAuditRules.Compare(a, Fresh(Link(70, "Unloaded")), false);

            Assert.Equal(ReAuditOutcome.Persistent, (string)r["outcome"]);
            Assert.Equal("Unloaded", (string)r["item_state_after"]["70"]);
            Assert.Contains("the audit is the judge", (string)r["why"]);
        }

        [Fact]
        public void An_inventory_item_that_is_no_longer_listed_is_NOT_a_success()
        {
            // The link type is gone from the inventory entirely. It may have been
            // deleted; it may sit past top. Neither is "reloaded".
            CorrectionAction a = Reload(Audit(Link(70, "Unloaded")), true);
            JObject r = ReAuditRules.Compare(a, Fresh(Link(71, "Loaded")), false);

            Assert.Equal(ReAuditOutcome.NotVerifiable, (string)r["outcome"]);
            Assert.Empty(r["elements"]["corrected"]);
            Assert.Equal("not_listed", (string)r["item_state_after"]["70"]);
            Assert.Contains("absence is not a success", (string)r["why"]);
        }

        [Fact]
        public void An_item_listed_without_a_readable_status_is_NOT_VERIFIABLE()
        {
            CorrectionAction a = Reload(Audit(Link(70, "Unloaded")), true);
            var noStatus = new JObject { ["id"] = "70", ["name"] = "HZ_OLD_2023.rvt" };
            JObject r = ReAuditRules.Compare(a, Fresh(noStatus), false);

            Assert.Equal(ReAuditOutcome.NotVerifiable, (string)r["outcome"]);
            Assert.Empty(r["elements"]["corrected"]);
        }

        [Fact]
        public void Two_types_one_reloaded_and_one_still_unloaded_is_persistent_and_names_both()
        {
            FindingSetRecord audit = Audit(Link(70, "Unloaded"), Link(71, "Unloaded"));
            CorrectionAction a = Reload(audit, true);      // the recipe selects BOTH unloaded types
            JObject r = ReAuditRules.Compare(a, Fresh(Link(70, "Loaded"), Link(71, "Unloaded")), false);

            Assert.Equal(ReAuditOutcome.Persistent, (string)r["outcome"]);
            Assert.Equal(new long[] { 70 }, r["elements"]["corrected"].Select(t => (long)t).ToArray());
            Assert.Equal(new long[] { 71 }, r["elements"]["persistent"].Select(t => (long)t).ToArray());
            Assert.Equal("Loaded", (string)r["item_state_after"]["70"]);
            Assert.Equal("Unloaded", (string)r["item_state_after"]["71"]);
        }

        [Fact]
        public void A_type_the_action_did_not_select_does_not_change_its_verdict()
        {
            // 71 is loaded at audit time, so the filter excludes it from the action.
            // It is still listed afterwards - it is an inventory - and that must not
            // make the reload of 70 read as persistent.
            FindingSetRecord audit = Audit(Link(70, "Unloaded"), Link(71, "Loaded"));
            CorrectionAction a = Reload(audit, true);
            Assert.Equal(new long[] { 70 }, a.SelectedElementIds.ToArray());

            JObject r = ReAuditRules.Compare(a, Fresh(Link(70, "Loaded"), Link(71, "Loaded")), false);
            Assert.Equal(ReAuditOutcome.Corrected, (string)r["outcome"]);
        }

        [Fact]
        public void A_reload_whose_typed_call_failed_is_FAILED_whatever_the_inventory_says()
        {
            CorrectionAction a = Reload(Audit(Link(70, "Unloaded")), false);
            JObject r = ReAuditRules.Compare(a, Fresh(Link(70, "Loaded")), false);

            // The child did not apply. A status that reads Loaded for another reason
            // is not this action's doing, and the re-audit does not award it.
            Assert.Equal(ReAuditOutcome.Failed, (string)r["outcome"]);
            Assert.Equal(new long[] { 70 }, r["elements"]["failed"].Select(t => (long)t).ToArray());
        }

        [Fact]
        public void A_check_that_could_not_re_run_is_still_not_verifiable()
        {
            CorrectionAction a = Reload(Audit(Link(70, "Unloaded")), true);
            JObject r = ReAuditRules.Compare(a, null, true);
            Assert.Equal(ReAuditOutcome.NotVerifiable, (string)r["outcome"]);
            Assert.True((bool)r["after"]["check_failed"]);
        }

        // ------------------------------------------- the defect lists must NOT move

        [Fact]
        public void A_DELETION_recipe_is_still_judged_by_disappearance()
        {
            CorrectionAction del = CorrectionSelection.Select(
                Audit(Link(70, "Unloaded")),
                new JArray(new JObject { ["finding_id"] = "f:groups", ["element_ids"] = new JArray(900L) }),
                CorrectionRegistry.Default).Single();
            foreach (CorrectionStep s in del.Steps) { s.RehearsalOk = true; s.ApplyOk = true; }
            del.State = CorrectionActionState.Applied;

            var gone = new JObject
            {
                ["check"] = AuditCheckNames.OrphanGroupTypes, ["finding_id"] = "f:after", ["is_issue"] = false,
                ["count"] = 0, ["truncated"] = false, ["items"] = new JArray()
            };
            JObject r = ReAuditRules.Compare(del, gone, false);
            Assert.Equal(ReAuditOutcome.Corrected, (string)r["outcome"]);
            Assert.Equal(CorrectionPostcondition.RemovedFromFinding, (string)r["postcondition"]);
            Assert.Equal(JTokenType.Null, r["item_state_after"].Type);

            var still = new JObject
            {
                ["check"] = AuditCheckNames.OrphanGroupTypes, ["finding_id"] = "f:after", ["is_issue"] = true,
                ["count"] = 1, ["truncated"] = false,
                ["items"] = new JArray(new JObject { ["element_id"] = 900L })
            };
            Assert.Equal(ReAuditOutcome.Persistent, (string)ReAuditRules.Compare(del, still, false)["outcome"]);
        }

        [Fact]
        public void Only_the_recipes_that_fix_in_place_declare_the_inventory_postcondition()
        {
            // The registry is the single place this is decided, and it is worth
            // failing loudly if a new recipe picks the wrong one by copy-paste.
            foreach (var kv in CorrectionRegistry.Default)
            {
                string expected = kv.Key == AuditCheckNames.Links
                    ? CorrectionPostcondition.ItemLeavesTheFilter
                    : CorrectionPostcondition.RemovedFromFinding;
                Assert.Equal(expected, kv.Value.Postcondition);
            }

            // And it is published, so a client can read what will be checked.
            JObject described = CorrectionRegistry.Describe();
            JObject links = described["entries"].Children<JObject>()
                .Single(e => (string)e["finding_type"] == AuditCheckNames.Links);
            Assert.Equal(CorrectionPostcondition.ItemLeavesTheFilter, (string)links["postcondition"]);
            Assert.Contains("absence is NOT success", (string)described["postcondition_means"]);
        }
    }
}
