// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// WHICH files horizun_file_info reads (story 5.20). The disk listing is injected,
// so the rules that matter - refuse when nothing was named, union explicit paths
// with a folder sweep, dedup case-insensitively with explicit paths winning, cap
// a runaway sweep - are proved without a disk.
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class FileInfoPathsTests
    {
        // A folder lister that returns a fixed set and records how it was called.
        private static System.Func<string, string, bool, IEnumerable<string>> Lister(
            List<(string folder, string pattern, bool recursive)> calls, params string[] matches)
        {
            return (folder, pattern, recursive) =>
            {
                calls?.Add((folder, pattern, recursive));
                return matches;
            };
        }

        [Fact]
        public void Neither_paths_nor_folder_is_refused()
        {
            FileInfoPlan plan = FileInfoPaths.Resolve(null, null, null, false, Lister(null));
            Assert.False(plan.Ok);
            Assert.Contains("paths", plan.Error);
            Assert.Contains("folder", plan.Error);
        }

        [Fact]
        public void Explicit_paths_alone_are_read_in_order_without_touching_the_lister()
        {
            bool listerCalled = false;
            var plan = FileInfoPaths.Resolve(
                new[] { @"C:\a\one.rvt", @"C:\a\two.rvt" }, null, null, false,
                (f, p, r) => { listerCalled = true; return new string[0]; });

            Assert.True(plan.Ok);
            Assert.Equal(new[] { @"C:\a\one.rvt", @"C:\a\two.rvt" }, plan.Files.ToArray());
            Assert.False(listerCalled);   // no folder given -> the lister is never called
        }

        [Fact]
        public void A_folder_is_swept_with_the_default_pattern_when_none_is_given()
        {
            var calls = new List<(string, string, bool)>();
            var plan = FileInfoPaths.Resolve(null, @"C:\models", null, false,
                Lister(calls, @"C:\models\a.rvt"));

            Assert.True(plan.Ok);
            Assert.Single(calls);
            Assert.Equal("*.rvt", calls[0].Item2);      // default pattern
            Assert.False(calls[0].Item3);               // not recursive
            Assert.Equal(new[] { @"C:\models\a.rvt" }, plan.Files.ToArray());
        }

        [Fact]
        public void A_given_pattern_and_recursive_flag_reach_the_lister()
        {
            var calls = new List<(string, string, bool)>();
            FileInfoPaths.Resolve(null, @"C:\fam", "*.rfa", true, Lister(calls, @"C:\fam\x.rfa"));

            Assert.Equal("*.rfa", calls[0].Item2);
            Assert.True(calls[0].Item3);
        }

        [Fact]
        public void Explicit_paths_come_first_then_folder_matches()
        {
            var plan = FileInfoPaths.Resolve(
                new[] { @"C:\a\one.rvt" }, @"C:\models", null, false,
                Lister(null, @"C:\models\b.rvt", @"C:\models\c.rvt"));

            Assert.Equal(new[] { @"C:\a\one.rvt", @"C:\models\b.rvt", @"C:\models\c.rvt" }, plan.Files.ToArray());
        }

        [Fact]
        public void Duplicates_are_removed_case_insensitively_first_occurrence_wins()
        {
            var plan = FileInfoPaths.Resolve(
                new[] { @"C:\a\One.rvt" }, @"C:\a", null, false,
                Lister(null, @"C:\a\one.rvt", @"C:\a\two.rvt"));

            // The folder's "one.rvt" is the same file as the explicit "One.rvt"; the
            // explicit one is kept and the folder duplicate dropped.
            Assert.Equal(new[] { @"C:\a\One.rvt", @"C:\a\two.rvt" }, plan.Files.ToArray());
        }

        [Fact]
        public void Blank_entries_are_ignored()
        {
            var plan = FileInfoPaths.Resolve(
                new[] { "", "   ", @"C:\a\one.rvt" }, null, null, false, Lister(null));
            Assert.Equal(new[] { @"C:\a\one.rvt" }, plan.Files.ToArray());
        }

        [Fact]
        public void A_folder_that_matches_nothing_with_no_paths_is_ok_and_empty()
        {
            // Not a refusal: a folder with no Revit files is a real, useful answer.
            var plan = FileInfoPaths.Resolve(null, @"C:\empty", null, false, Lister(null /* no matches */));
            Assert.True(plan.Ok);
            Assert.Empty(plan.Files);
        }

        [Fact]
        public void A_sweep_past_the_cap_is_truncated_not_refused()
        {
            var many = Enumerable.Range(0, FileInfoPaths.MaxFiles + 50)
                                 .Select(i => @"C:\m\f" + i + ".rvt").ToArray();
            var plan = FileInfoPaths.Resolve(null, @"C:\m", null, false, Lister(null, many));

            Assert.True(plan.Ok);
            Assert.True(plan.Truncated);
            Assert.Equal(FileInfoPaths.MaxFiles, plan.Files.Count);
            Assert.Equal(FileInfoPaths.MaxFiles + 50, plan.TotalMatched);
        }

        [Fact]
        public void A_lister_that_throws_becomes_an_error_not_a_crash()
        {
            var plan = FileInfoPaths.Resolve(null, @"C:\x", null, false,
                (f, p, r) => throw new System.UnauthorizedAccessException("denied"));
            Assert.False(plan.Ok);
            Assert.Contains("denied", plan.Error);
        }
    }
}
