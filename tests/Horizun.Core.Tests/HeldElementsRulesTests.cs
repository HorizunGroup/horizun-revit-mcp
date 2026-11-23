// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// manage_worksets operation=loans (feedback §9): what I hold before I synchronize.
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class HeldElementsRulesTests
    {
        private static List<HeldRow> Rows(int n, string category = "Walls") =>
            Enumerable.Range(1, n).Select(i => new HeldRow { ElementId = 1000 - i, UniqueId = "u" + i, Category = category, Workset = "Shared" }).ToList();

        [Fact]
        public void A_model_that_is_not_collaborative_is_not_applicable_not_empty()
        {
            JObject r = HeldElementsRules.NotApplicable("Casa");
            Assert.False((bool)r["applicable"]);
            Assert.False((bool)r["workshared"]);
            Assert.True((bool)r["read_only"]);
            Assert.Null(r["counts"]);
            Assert.Contains("not collaborative", (string)r["note"]);
        }

        [Fact]
        public void Owned_splits_into_through_worksets_and_borrowed_without_counting_twice()
        {
            var owned = new List<HeldWorkset> { new HeldWorkset { Id = 5, Name = "Arq", IsOpen = true } };
            JObject r = HeldElementsRules.Summarize("Casa", "ana", 5000, 120, 20, 7, 0, 0, false, Rows(20), owned, null);
            Assert.Equal(100, (int)r["counts"]["owned_through_my_worksets"]);
            Assert.Equal(20, (int)r["counts"]["borrowed"]);
            Assert.Equal(120, (int)r["counts"]["owned_total"]);
            Assert.Equal(7, (int)r["counts"]["held_by_others"]);
            Assert.Equal("complete", (string)r["coverage"]);
            Assert.Equal("Arq", (string)r["owned_worksets"][0]["name"]);
            Assert.Equal(20, (int)r["borrowed_by_category"]["Walls"]);
            Assert.False((bool)r["borrowed_elements_truncated"]);
        }

        [Fact]
        public void The_element_list_is_capped_and_says_so_but_the_counts_are_not()
        {
            JObject r = HeldElementsRules.Summarize("Casa", "ana", 9000, 500, 450, 0, 0, 0, false, Rows(450), null, null, 200);
            Assert.Equal(200, (int)r["borrowed_elements_listed"]);
            Assert.True((bool)r["borrowed_elements_truncated"]);
            Assert.Equal(450, (int)r["counts"]["borrowed"]);
            Assert.Equal(450, (int)r["borrowed_by_category"]["Walls"]);
            // sorted by element id so two reads list the same elements
            var ids = r["borrowed_elements"].Select(t => (long)t["element_id"]).ToList();
            Assert.Equal(ids.OrderBy(i => i).ToList(), ids);
        }

        [Fact]
        public void Closed_worksets_or_unreadable_elements_make_the_answer_partial_never_a_clean_nothing()
        {
            var closed = new List<HeldWorkset> { new HeldWorkset { Id = 9, Name = "Estructura", IsOpen = false } };
            JObject r = HeldElementsRules.Summarize("Casa", "ana", 100, 0, 0, 0, 0, 0, false, null, null, closed);
            Assert.Equal("partial", (string)r["coverage"]);
            Assert.Contains("closed", (string)r["coverage_notes"][0]);
            Assert.Equal("Estructura", (string)r["closed_worksets"][0]["name"]);

            JObject u = HeldElementsRules.Summarize("Casa", "ana", 100, 0, 0, 0, 3, 0, false, null, null, null);
            Assert.Equal("partial", (string)u["coverage"]);
            Assert.Equal(3, (int)u["counts"]["unreadable"]);
        }

        [Fact]
        public void An_unreadable_worksets_table_or_username_is_partial_with_unreliable_counts_never_complete()
        {
            JObject r = HeldElementsRules.Summarize("Casa", null, 100, 0, 0, 0, 0, 0, true, null, null, null);
            Assert.Equal("partial", (string)r["coverage"]);
            Assert.True((bool)r["ownership_census_incomplete"]);
            Assert.False((bool)r["counts_reliable"]);
            Assert.Contains("ownership_census_incomplete", (string)r["coverage_notes"][0]);
            Assert.DoesNotContain("of 0 element", (string)r["coverage_notes"][0]);     // a worksets failure is not "0 elements unreadable"
            Assert.Contains("worksets", (string)r["coverage_notes"][0]);
        }

        [Fact]
        public void A_borrowed_id_that_cannot_be_re_read_stays_borrowed_and_is_not_also_unreadable()
        {
            JObject r = HeldElementsRules.Summarize("Casa", "ana", 100, 10, 10, 0, 0, 2, false, Rows(8), null, null);
            Assert.Equal(10, (int)r["counts"]["borrowed"]);
            Assert.Equal(0, (int)r["counts"]["unreadable"]);
            Assert.Equal(2, (int)r["counts"]["borrowed_not_listed"]);
            Assert.Equal(8, (int)r["borrowed_elements_listed"]);
            Assert.Equal("partial", (string)r["coverage"]);
            Assert.True((bool)r["counts_reliable"]);
        }

        [Fact]
        public void Owned_worksets_of_every_kind_are_listed_with_their_kind()
        {
            var owned = new List<HeldWorkset>
            {
                new HeldWorkset { Id = 5, Name = "Arq", IsOpen = true, Kind = "UserWorkset" },
                new HeldWorkset { Id = 9, Name = "Plan 1", IsOpen = true, Kind = "ViewWorkset" },
                new HeldWorkset { Id = 11, Name = "Familia X", IsOpen = true, Kind = "FamilyWorkset" }
            };
            JObject r = HeldElementsRules.Summarize("Casa", "ana", 100, 0, 0, 0, 0, 0, false, null, owned, null);
            Assert.Equal(new[] { "UserWorkset", "ViewWorkset", "FamilyWorkset" }, r["owned_worksets"].Select(w => (string)w["kind"]).ToArray());
        }

        [Fact]
        public void A_detached_model_is_not_applicable_with_its_reason()
        {
            JObject r = HeldElementsRules.NotApplicable("Casa_detached", "detached");
            Assert.False((bool)r["applicable"]);
            Assert.Equal("detached", (string)r["reason"]);
            Assert.Contains("DETACHED", (string)r["note"]);
        }

        [Fact]
        public void It_points_to_the_way_to_give_the_loan_back()
        {
            string next = (string)HeldElementsRules.Summarize("Casa", "ana", 1, 0, 0, 0, 0, 0, false, null, null, null)["next"];
            Assert.Contains("relinquish=borrowed_only", next);
            Assert.Contains("relinquish_own", next);
        }
    }
}
