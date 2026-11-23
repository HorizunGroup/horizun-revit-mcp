using System;
using System.IO;
using Xunit;

namespace Horizun.Core.Tests
{
    public class PlaceGroupCopyFirstTests
    {
        [Fact]
        public void Place_group_copies_the_pattern_before_searching_orientations_and_says_which_route_placed_it()
        {
            // Field 2026-11 (5.0.2): the orientation search (up to 100 placements, each with mirrors and
            // regenerations, in the rehearsal and again in the apply) held a client the whole 600 s.
            string src = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Horizun.Revit", "Commands", "ManageGroupsCommand.cs"));
            int caseAt = src.IndexOf("case \"place_group\":", StringComparison.Ordinal);
            int copyAt = src.IndexOf("TryCopyPattern(doc, p)", caseAt, StringComparison.Ordinal);
            int searchAt = src.IndexOf("GroupPatternRules.CandidateAngles", caseAt, StringComparison.Ordinal);
            Assert.True(caseAt > 0 && copyAt > caseAt && searchAt > copyAt);
            Assert.Contains("if (placed == null)", src.Substring(copyAt, searchAt - copyAt));
            Assert.Contains("o[\"placed_by\"] = p.PlacedBy", src);
            int helper = src.IndexOf("private static Group TryCopyPattern", StringComparison.Ordinal);
            string body = src.Substring(helper, src.IndexOf("private static bool PatternMatches", helper, StringComparison.Ordinal) - helper);
            Assert.Contains("made.Count == 1", body);
            Assert.Contains("g.GroupType.Id == p.Type.Id", body);
            Assert.Contains("PatternMatches(doc, p, g)", body);
            Assert.Contains("Guard.RollBack(copy)", body);
        }

        private static string RepoRoot()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null)
            {
                if (File.Exists(Path.Combine(d.FullName, "AGENTS.md")) &&
                    Directory.Exists(Path.Combine(d.FullName, "src", "Horizun.Revit", "Commands")))
                    return d.FullName;
                d = d.Parent;
            }
            throw new InvalidOperationException("repo root not found");
        }
    }
}
