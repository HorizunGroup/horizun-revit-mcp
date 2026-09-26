// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// Parsing naviscoord's handoff, source_file normalization, the immovable-side
// letter, and the clash-resolve preference that must respect it. Every example
// below is shaped after naviscoord-mcp's own server/tests/test_interop.py.
// -----------------------------------------------------------------------------
using System.Linq;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class NavisworksHandoffTests
    {
        private const string OneIssueHandoff = @"{
  ""schema"": ""naviscoord.coordination/1"",
  ""source"": { ""tool"": ""naviscoord"", ""document"": ""Federated"" },
  ""issues"": [
    {
      ""issue_id"": ""I-001"", ""priority"": ""high"", ""severity"": 8.5,
      ""side_a_discipline"": ""Structure"", ""side_b_discipline"": ""Mechanical"",
      ""level"": ""L02"", ""centroid"": [1.0, 2.0, 3.0], ""max_penetration_mm"": 45.0,
      ""responsible"": ""Mechanical"", ""immovable_side"": ""Structure"",
      ""suggested_action"": ""reroute the duct"",
      ""targets"": [
        { ""side"": ""a"", ""path_id"": ""p1"", ""revit_element_id"": ""111"", ""source_file"": ""Structure.nwc"",
          ""discipline"": ""Structure"", ""category"": ""Structural Framing"", ""name"": ""Beam"", ""actionable_in_revit"": true },
        { ""side"": ""b"", ""path_id"": ""p2"", ""revit_element_id"": ""222"", ""source_file"": ""Mechanical - NWC (1).nwc"",
          ""discipline"": ""Mechanical"", ""category"": ""Ducts"", ""name"": ""Duct"", ""actionable_in_revit"": true }
      ]
    },
    {
      ""issue_id"": ""I-002"", ""priority"": ""low"", ""folded_into"": ""I-001"",
      ""targets"": []
    }
  ]
}";

        [Fact]
        public void A_folded_issue_is_omitted()
        {
            NavisHandoffParseResult r = NavisworksHandoff.Parse(OneIssueHandoff);
            Assert.True(r.Ok);
            Assert.Single(r.Issues);
            Assert.Equal("I-001", r.Issues[0].IssueId);
        }

        [Fact]
        public void Centroid_converts_from_metres_to_millimetres()
        {
            NavisIssue issue = NavisworksHandoff.Parse(OneIssueHandoff).Issues[0];
            Assert.Equal(new[] { 1000.0, 2000.0, 3000.0 }, issue.CentroidMm);
        }

        [Fact]
        public void Targets_split_by_side()
        {
            NavisIssue issue = NavisworksHandoff.Parse(OneIssueHandoff).Issues[0];
            Assert.Single(issue.Side("a"));
            Assert.Single(issue.Side("b"));
            Assert.Equal("111", issue.Side("a").First().RevitElementId);
            Assert.Equal(222, issue.Side("b").First().ElementIdValue());
        }

        [Fact]
        public void Immovable_side_resolves_to_the_lettered_side_it_names()
        {
            NavisIssue issue = NavisworksHandoff.Parse(OneIssueHandoff).Issues[0];
            // immovable_side = "Structure" = side_a_discipline -> side A is immovable.
            Assert.True(issue.ImmovableSideIsA());
        }

        [Fact]
        public void Immovable_side_is_unknown_when_both_sides_share_a_discipline()
        {
            var issue = new NavisIssue { ImmovableSide = "Mechanical", SideADiscipline = "Mechanical", SideBDiscipline = "Mechanical" };
            Assert.Null(issue.ImmovableSideIsA());
        }

        [Fact]
        public void Immovable_side_is_null_when_the_issue_names_none()
        {
            var issue = new NavisIssue { SideADiscipline = "Structure", SideBDiscipline = "Mechanical" };
            Assert.Null(issue.ImmovableSideIsA());
        }

        [Theory]
        [InlineData("Mechanical.nwc", "Mechanical")]
        [InlineData("Mechanical - NWC (1).nwc", "Mechanical")]
        [InlineData("STRUCTURE_NWC.nwd", "STRUCTURE")]
        [InlineData(@"C:\exports\Piping.nwc", "Piping")]
        public void Source_file_normalization_strips_extension_and_nwc_decoration(string raw, string expected)
        {
            Assert.Equal(expected, NavisworksHandoff.NormalizeSourceFile(raw));
        }

        [Fact]
        public void Source_file_matches_a_revit_document_title_case_insensitively()
        {
            Assert.True(NavisworksHandoff.SourceFileMatches("Mechanical - NWC (1).nwc", "mechanical"));
            Assert.False(NavisworksHandoff.SourceFileMatches("Mechanical.nwc", "Structure"));
        }

        private const string Worklist = @"{
  ""schema"": ""naviscoord.coordination.worklist/1"",
  ""source"": {},
  ""models"": [
    { ""source_file"": ""Mechanical.nwc"", ""discipline"": ""Mechanical"", ""count"": 1,
      ""items"": [ { ""issue_id"": ""I-001"", ""priority"": ""high"", ""severity"": 8.5, ""revit_element_id"": ""222"",
                     ""element"": ""Duct"", ""level"": ""L02"", ""action"": ""reroute"" } ] }
  ]
}";

        [Fact]
        public void The_worklist_parses_but_is_marked_one_sided()
        {
            NavisHandoffParseResult r = NavisworksHandoff.Parse(Worklist);
            Assert.True(r.Ok);
            Assert.True(r.OneSidedOnly);
            Assert.Single(r.Issues);
            Assert.Single(r.Issues[0].Targets);
            Assert.Equal("a", r.Issues[0].Targets[0].Side);
        }

        [Fact]
        public void An_unrecognised_schema_with_no_issues_array_is_refused()
        {
            NavisHandoffParseResult r = NavisworksHandoff.Parse(@"{ ""schema"": ""something.else/1"" }");
            Assert.False(r.Ok);
            Assert.Contains("schema", r.Error);
        }

        [Fact]
        public void Not_json_at_all_is_refused_with_a_reason()
        {
            NavisHandoffParseResult r = NavisworksHandoff.Parse("not json");
            Assert.False(r.Ok);
        }
    }

    /// <summary>The immovable-side preference on top of ChooseMover's natural pick.</summary>
    public class ClashResolveImmovableSideTests
    {
        [Fact]
        public void No_preference_leaves_the_natural_mover_unchanged()
        {
            Assert.True(ClashResolveRules.EnforceImmovableSide(0, true, true, null, out int finalMover, out string code, out string reason));
            Assert.Equal(0, finalMover); Assert.Null(code); Assert.Null(reason);
        }

        [Fact]
        public void A_preference_that_already_agrees_changes_nothing()
        {
            // Natural mover is B (1); immovable side is A (0) - already respected.
            Assert.True(ClashResolveRules.EnforceImmovableSide(1, true, true, 0, out int finalMover, out string code, out _));
            Assert.Equal(1, finalMover); Assert.Null(code);
        }

        [Fact]
        public void The_choice_inverts_when_the_other_side_can_move()
        {
            // Both movable; natural mover is A (smaller section), but A is marked immovable.
            Assert.True(ClashResolveRules.EnforceImmovableSide(0, true, true, 0, out int finalMover, out string code, out string reason));
            Assert.Equal(1, finalMover);
            Assert.Null(code);
            Assert.Contains("inverted", reason);
        }

        [Fact]
        public void The_proposal_is_refused_when_the_only_mover_is_the_immovable_side()
        {
            // Only A is movable+host; A is marked immovable. B cannot take over.
            Assert.False(ClashResolveRules.EnforceImmovableSide(0, true, false, 0, out int finalMover, out string code, out string reason));
            Assert.Equal(-1, finalMover);
            Assert.Equal(ClashResolveRules.CodeImmovableSide, code);
            Assert.Contains("side A is immovable", reason);
        }

        [Fact]
        public void No_natural_mover_at_all_is_unaffected_by_a_preference()
        {
            Assert.True(ClashResolveRules.EnforceImmovableSide(-1, false, false, 0, out int finalMover, out _, out _));
            Assert.Equal(-1, finalMover);
        }
    }

    /// <summary>CoordinationRules.Merge propagates navis provenance and flips the letter on a pair swap.</summary>
    public class CoordinationNavisMergeTests
    {
        [Fact]
        public void A_new_finding_carries_the_external_provenance()
        {
            var ledger = new System.Collections.Generic.Dictionary<string, CoordinationFinding>();
            var hit = new CoordinationDetected
            {
                SideA = "aaa||u1", SideB = "zzz||u2", CategoryA = "Walls", CategoryB = "Pipes",
                ExternalSource = "navisworks", ExternalIssueId = "I-001", Priority = "high",
                Responsible = "Mechanical", ImmovableDiscipline = "Structure", ImmovableSideIsA = true,
                SuggestedAction = "reroute the duct"
            };
            CoordinationRules.Merge(ledger, new[] { hit }, "t1", true, "navisworks");
            CoordinationFinding f = ledger[CoordinationRules.FindingId("aaa||u1", "zzz||u2")];
            Assert.Equal("navisworks", f.ExternalSource);
            Assert.Equal("I-001", f.ExternalIssueId);
            Assert.Equal("reroute the duct", f.SuggestedAction);
            Assert.True(f.ImmovableSideIsA);
        }

        [Fact]
        public void The_immovable_letter_flips_when_the_pair_order_swaps()
        {
            // SideA > SideB ordinally here, so Merge/NormalizePair swaps them - the letter
            // recorded on the finding must describe the SWAPPED (ledger) order, not the
            // caller's original a/b.
            var ledger = new System.Collections.Generic.Dictionary<string, CoordinationFinding>();
            var hit = new CoordinationDetected
            {
                SideA = "zzz||u9", SideB = "aaa||u1", CategoryA = "Ducts", CategoryB = "Beams",
                ImmovableSideIsA = true   // true against the CALLER's a ("zzz||u9")
            };
            CoordinationRules.Merge(ledger, new[] { hit }, "t1", true, "s1");
            CoordinationFinding f = ledger[CoordinationRules.FindingId("zzz||u9", "aaa||u1")];
            // The ledger's SideA is "aaa||u1" (swapped), so the immovable side - still
            // "zzz||u9" physically - is now SideB in ledger terms.
            Assert.Equal("aaa||u1", f.SideA);
            Assert.False(f.ImmovableSideIsA);
        }

        [Fact]
        public void A_plain_re_detection_does_not_blank_out_earlier_navis_provenance()
        {
            var ledger = new System.Collections.Generic.Dictionary<string, CoordinationFinding>();
            var navisHit = new CoordinationDetected
            {
                SideA = "host||u1", SideB = "MEP||u2", CategoryA = "Walls", CategoryB = "Pipes",
                ExternalSource = "navisworks", ExternalIssueId = "I-001"
            };
            CoordinationRules.Merge(ledger, new[] { navisHit }, "t1", true, "navisworks");
            var plainHit = new CoordinationDetected { SideA = "host||u1", SideB = "MEP||u2", CategoryA = "Walls", CategoryB = "Pipes" };
            CoordinationRules.Merge(ledger, new[] { plainHit }, "t2", true, "navisworks");
            CoordinationFinding f = ledger[CoordinationRules.FindingId("host||u1", "MEP||u2")];
            Assert.Equal("navisworks", f.ExternalSource);
            Assert.Equal("I-001", f.ExternalIssueId);
        }
    }
}
