using System;
using System.IO;
using Xunit;

namespace Horizun.Core.Tests
{
    public sealed class FloorTransformSafetyTests
    {
        private static string TransformSource()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory != null)
            {
                string path = Path.Combine(directory.FullName, "src", "Horizun.Revit", "Commands", "TransformElementsCommand.cs");
                if (File.Exists(path)) return File.ReadAllText(path);
                directory = directory.Parent;
            }
            throw new InvalidOperationException("Repository root not found.");
        }

        [Fact]
        public void Pin_only_batches_do_not_compare_existing_intersections_against_an_empty_baseline()
        {
            string source = TransformSource();
            int commit = source.IndexOf("Guard.Commit(tx, txName)", StringComparison.Ordinal);
            Assert.True(commit > 0);
            string beforeCommit = source.Substring(0, commit);
            Assert.Contains("p.Operation != \"pin\" && p.Operation != \"unpin\"", beforeCommit);
            Assert.Matches(@"if\s*\(guardFinishFloors\)\s*(?:\{\s*)?FloorWallOverlapGuard\.ThrowIfIncreased", beforeCommit);
        }

        [Fact]
        public void Geometric_transforms_can_revert_the_commit_when_the_floor_guard_fails_after_it()
        {
            string source = TransformSource();
            Assert.Contains("bool realGroup = plans.Any(p => p.Operation == \"copy\") || guardFinishFloors;", source);
            Assert.Contains("rehearsal || realGroup ? new TransactionGroup", source);
            int commit = source.IndexOf("Guard.Commit(tx, txName)", StringComparison.Ordinal);
            int guard = source.IndexOf("if (guardFinishFloors)", commit, StringComparison.Ordinal);
            int rollback = source.IndexOf("Guard.RollBack(copyGroup)", guard, StringComparison.Ordinal);
            int assimilate = source.IndexOf("Guard.Assimilate(copyGroup", rollback, StringComparison.Ordinal);
            Assert.True(commit < guard && guard < rollback && rollback < assimilate);
            Assert.Contains("[\"rollback_confirmed\"] = rollback.Confirmed", source.Substring(rollback, assimilate - rollback));
        }
    }
}
