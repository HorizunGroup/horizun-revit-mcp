// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// Workset placement, proved by running it. One test here matters more than the
// rest: a check whose coverage is incomplete may FAIL and may never PASS. Get
// that wrong and a team is told their model is clean because somebody happened
// to have a workset closed.
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class WorksetPlacementTests
    {
        private static WorksetRules R(string json) => WorksetPlacementRules.Read(JToken.Parse(json));

        private static WorksetMisplacement E(long id, string category, string actual) =>
            new WorksetMisplacement { ElementId = id, Category = category, ActualWorkset = actual };

        // ------------------------------------------------- the asymmetry

        [Fact]
        public void An_incomplete_check_may_fail_but_can_never_pass()
        {
            // THE ONE THAT MATTERS.
            // Found something, coverage incomplete -> still a fail. Closing a
            // workset cannot un-break a violation somebody already made.
            Assert.Equal(WorksetGate.Fail, WorksetPlacementRules.Outcome(5, 0, false));
            // Found nothing, coverage incomplete -> NOT a pass. "Nothing found" is
            // a claim about every element, and nobody looked at every element.
            Assert.Equal(WorksetGate.NotAssessable, WorksetPlacementRules.Outcome(0, 0, false));
            // Found nothing, coverage complete -> a pass, and only here.
            Assert.Equal(WorksetGate.Pass, WorksetPlacementRules.Outcome(0, 0, true));
        }

        [Fact]
        public void A_count_with_no_threshold_can_neither_pass_nor_fail()
        {
            // The caller set no ceiling, so there is nothing to compare against.
            // Reporting a pass would grade a rule nobody wrote.
            Assert.Equal(WorksetGate.NotAssessable, WorksetPlacementRules.Outcome(0, null, true));
            Assert.Equal(WorksetGate.NotAssessable, WorksetPlacementRules.Outcome(99, null, true));
        }

        [Fact]
        public void Coverage_is_complete_only_when_nothing_was_closed_and_nothing_unreadable()
        {
            Assert.True(WorksetPlacementRules.CoverageComplete(0, 0));
            Assert.False(WorksetPlacementRules.CoverageComplete(1, 0));
            Assert.False(WorksetPlacementRules.CoverageComplete(0, 1));
        }

        [Fact]
        public void The_coverage_note_names_what_was_missed_and_says_it_cannot_pass()
        {
            string note = WorksetPlacementRules.CoverageNote(2, 3);
            Assert.Contains("2 user workset(s) are CLOSED", note);
            Assert.Contains("3 element(s) would not report a workset", note);
            Assert.Contains("LOWER BOUND", note);
            Assert.Contains("cannot PASS", note);

            Assert.Contains("exact", WorksetPlacementRules.CoverageNote(0, 0));
            Assert.DoesNotContain("LOWER BOUND", WorksetPlacementRules.CoverageNote(0, 0));
        }

        [Fact]
        public void The_reply_explains_why_a_closed_workset_is_not_an_empty_one()
        {
            Assert.Contains("not in the document", WorksetPlacementRules.CoverageMeans);
            Assert.Contains("never PASS", WorksetPlacementRules.CoverageMeans);
        }

        // --------------------------------------------------------- placement

        [Fact]
        public void An_element_on_the_wrong_workset_is_found_and_carries_what_was_expected()
        {
            WorksetRules r = R(@"{ ""version"": ""v1"", ""by_category"": { ""Walls"": ""ARQ-Muros"" } }");
            Assert.True(r.Ok, r.Message);

            List<WorksetMisplacement> bad = WorksetPlacementRules.Misplaced(
                new[] { E(1, "Walls", "ARQ-Muros"), E(2, "Walls", "Workset1") }, r);

            WorksetMisplacement only = Assert.Single(bad);
            Assert.Equal(2, only.ElementId);
            Assert.Equal("ARQ-Muros", only.ExpectedWorkset);
        }

        [Fact]
        public void A_category_the_rules_are_silent_about_is_unjudged_not_misplaced()
        {
            // Adding the unjudged to the violations is how a count of "wrong
            // workset" becomes a count of "every element in the model".
            WorksetRules r = R(@"{ ""version"": ""v1"", ""by_category"": { ""Walls"": ""ARQ-Muros"" } }");
            Assert.Empty(WorksetPlacementRules.Misplaced(new[] { E(1, "Doors", "anywhere") }, r));
        }

        [Fact]
        public void An_element_whose_workset_could_not_be_read_is_not_reported_as_misplaced()
        {
            // Unknown is not wrong. It is unreadable, counted elsewhere, and it
            // makes the total a lower bound rather than a finding.
            WorksetRules r = R(@"{ ""version"": ""v1"", ""by_category"": { ""Walls"": ""ARQ-Muros"" } }");
            Assert.Empty(WorksetPlacementRules.Misplaced(new[] { E(1, "Walls", null) }, r));
        }

        [Fact]
        public void With_no_rules_nothing_is_judged_and_nothing_is_declared_clean()
        {
            WorksetRules r = WorksetPlacementRules.Read(null);
            Assert.True(r.Absent);
            Assert.False(r.Ok);
            Assert.Contains("NOT a pass", r.Message);
            Assert.Empty(WorksetPlacementRules.Misplaced(new[] { E(1, "Walls", "anywhere") }, r));
        }

        [Fact]
        public void A_refused_rule_set_is_not_applied_even_though_it_parsed_some_rules()
        {
            // Read() fills ExpectedByCategory as it goes and only THEN meets the bad
            // key. Without the Ok guard those half-parsed rules would be enforced -
            // the caller is told their rules were rejected, and the model is judged
            // against them anyway.
            WorksetRules r = R(@"{ ""version"": ""v1"",
                                   ""by_category"": { ""Walls"": ""ARQ-Muros"" },
                                   ""bogus"": 1 }");
            Assert.False(r.Ok);
            Assert.Equal(WorksetRuleCodes.UnknownKey, r.Code);
            Assert.NotEmpty(r.ExpectedByCategory);          // it really did parse one

            Assert.Empty(WorksetPlacementRules.Misplaced(new[] { E(1, "Walls", "Workset1") }, r));
            Assert.Empty(WorksetPlacementRules.DefaultNamed(new[] { "Workset1" }, r));
        }

        // ------------------------------------------------------ default names

        [Fact]
        public void Default_workset_names_come_from_the_caller_because_revits_own_is_localized()
        {
            // A compiled-in "Workset1" stops matching in a Spanish session, silently
            // - the identical failure the warning identities were rewritten to avoid.
            WorksetRules r = R(@"{ ""version"": ""v1"", ""default_workset_names"": [""Workset1"", ""Subproyecto1""] }");
            List<string> hits = WorksetPlacementRules.DefaultNamed(
                new[] { "ARQ-Muros", "Subproyecto1" }, r);
            Assert.Equal("Subproyecto1", Assert.Single(hits));
        }

        [Fact]
        public void With_no_declared_default_names_nothing_is_flagged_as_a_default()
        {
            WorksetRules r = R(@"{ ""version"": ""v1"" }");
            Assert.Empty(WorksetPlacementRules.DefaultNamed(new[] { "Workset1" }, r));
        }

        // ------------------------------------------------------------- share

        [Fact]
        public void A_share_of_nothing_scanned_is_unknown_rather_than_zero()
        {
            Assert.Null(WorksetPlacementRules.ShareOfScanned(0, 0));
            Assert.Equal(50.0, WorksetPlacementRules.ShareOfScanned(5, 10));
        }

        // ----------------------------------------------------------- refusals

        [Fact]
        public void Rules_without_a_version_are_refused()
        {
            Assert.Equal(WorksetRuleCodes.NoVersion,
                R(@"{ ""by_category"": { ""Walls"": ""W"" } }").Code);
        }

        [Fact]
        public void An_unknown_key_refuses_the_whole_rule_set_with_the_offender_named()
        {
            WorksetRules r = R(@"{ ""version"": ""v1"", ""by_categories"": {} }");
            Assert.Equal(WorksetRuleCodes.UnknownKey, r.Code);
            Assert.Contains("by_categories", r.Message);
            Assert.Contains("by_category, default_workset_names", r.Message);
        }

        [Fact]
        public void A_category_mapped_to_nothing_is_refused_rather_than_skipped()
        {
            // A rule that silently does not run reports every element as acceptable.
            Assert.Equal(WorksetRuleCodes.BadRule,
                R(@"{ ""version"": ""v1"", ""by_category"": { ""Walls"": """" } }").Code);
        }

        [Fact]
        public void A_negative_ceiling_is_refused()
        {
            Assert.Equal(WorksetRuleCodes.BadRule,
                R(@"{ ""version"": ""v1"", ""max_elements_in_wrong_workset"": -1 }").Code);
        }

        [Fact]
        public void A_ceiling_of_zero_is_a_real_ceiling_and_not_an_absent_one()
        {
            // 0 means "none allowed", which is the strictest rule - not "no rule".
            WorksetRules r = R(@"{ ""version"": ""v1"", ""max_elements_in_wrong_workset"": 0 }");
            Assert.True(r.Ok);
            Assert.Equal(0, r.MaxElementsInWrongWorkset);
            Assert.Equal(WorksetGate.Fail, WorksetPlacementRules.Outcome(1, r.MaxElementsInWrongWorkset, true));
        }
    }
}
