// -----------------------------------------------------------------------------
// Horizun Core tests — original Horizun code.
//
// THE ACCOUNTING COUNTS EVERYTHING, THE LIST IS BOUNDED.
//
// MEASURED on a second apartment: 140 symbols considered, 54 candidates, 67
// outside the rules - and "unaccounted: 19", because the classification added up
// the listed unclaimed names and the list stops at fifty. The totals now come from
// the whole reading. A text guard: both files need a UIApplication to run.
// -----------------------------------------------------------------------------
using System;
using System.IO;
using Xunit;

namespace Horizun.Core.Tests
{
    public class CadBlockAccountingTests
    {
        private static string Source(params string[] parts)
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !Directory.Exists(Path.Combine(d.FullName, "src"))) d = d.Parent;
            Assert.NotNull(d);
            return File.ReadAllText(Path.Combine(d.FullName, Path.Combine(parts)));
        }

        [Fact]
        public void The_unclaimed_total_is_counted_from_every_name_and_the_classification_uses_it()
        {
            string source = Source("src", "Horizun.Revit", "Commands", "CadBlockSource.cs");
            Assert.Contains("report[\"unclaimed_instances\"] = blocks.Unclaimed.Sum(u => u.Count);", source);
            Assert.Contains(".Take(UnclaimedListed)", source);
            Assert.Contains("report[\"unclaimed_blocks_truncated\"]", source);

            string plan = Source("src", "Horizun.Revit", "Commands", "PlanFromCadCommand.cs");
            Assert.Contains("int unclaimed = blocksReport.Value<int?>(\"unclaimed_instances\") ?? 0;", plan);
            Assert.DoesNotContain("foreach (JObject u in (blocksReport[\"unclaimed_blocks\"]", plan);
        }

        [Fact]
        public void Candidates_already_built_are_counted_as_built_not_as_held_for_review()
        {
            // MEASURED: a second plan on a model where all 39 devices agreed
            // reported "held_for_review: 39".
            string plan = Source("src", "Horizun.Revit", "Commands", "PlanFromCadCommand.cs");
            Assert.Contains("[\"already_built\"] = alreadyThere,", plan);
            Assert.Contains("int reviewed = plan.Deferred.Count - mirrorPending - alreadyThere;", plan);
            Assert.Contains("- mirrorPending - alreadyThere", plan);
        }

        [Fact]
        public void A_row_whose_family_needs_a_host_the_rule_did_not_declare_is_withdrawn_in_both_routes()
        {
            // MEASURED: one panelboard row the rehearsal could not place took the
            // other 41 rows of its atomic stage with it.
            string plan = Source("src", "Horizun.Revit", "Commands", "PlanFromCadCommand.cs");
            Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(plan,
                System.Text.RegularExpressions.Regex.Escape("WithdrawHostless(doc, creates, withdrawn);")).Count);
            Assert.Contains("\"family_needs_a_host_the_rule_does_not_declare\"", plan);
            // The same test the placement applies, so plan and apply cannot disagree.
            string place = Source("src", "Horizun.Revit", "Commands", "CreateElementsGeometry.cs");
            Assert.Contains("placement != FamilyPlacementType.OneLevelBased && placement != FamilyPlacementType.TwoLevelsBased", place);
            Assert.Contains("placement == FamilyPlacementType.OneLevelBased || placement == FamilyPlacementType.TwoLevelsBased", plan);
            // And the classification counts it under its own name.
            Assert.Contains("[\"family_needs_a_host_the_rule_does_not_declare\"] = withdrawnSymbols.Count(", plan);
        }
    }
}
