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

    }
}
