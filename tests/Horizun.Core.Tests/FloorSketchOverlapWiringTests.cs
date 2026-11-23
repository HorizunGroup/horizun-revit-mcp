using System;
using System.IO;
using Xunit;

namespace Horizun.Core.Tests
{
    public sealed class FloorSketchOverlapWiringTests
    {
        private static string Source(string file)
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory != null)
            {
                string path = Path.Combine(directory.FullName, "src", "Horizun.Revit", "Commands", file);
                if (File.Exists(path)) return File.ReadAllText(path);
                directory = directory.Parent;
            }
            throw new InvalidOperationException("Repository root not found.");
        }

        [Theory]
        [InlineData("TransformElementsCommand.EditSketch.cs", "EditSketchOp")]
        [InlineData("TransformElementsCommand.RealignWallSketch.cs", "RealignOp")]
        public void A_sketch_scope_cannot_retain_new_floor_wall_intersections_when_its_native_commit_succeeds(string file, string operation)
        {
            string source = Source(file);
            int capture = source.IndexOf("FloorWallOverlapGuard.Capture(", StringComparison.Ordinal);
            int commit = source.IndexOf("commit: true", StringComparison.Ordinal);
            int guard = source.IndexOf("FloorWallOverlapGuard.ThrowIfIncreased(", StringComparison.Ordinal);
            int assimilate = source.IndexOf("Guard.Assimilate(group, " + operation + ")", StringComparison.Ordinal);
            Assert.True(capture >= 0 && capture < commit, "Measure floor/wall baseline before committing the sketch scope.");
            Assert.True(commit < guard && guard < assimilate, "Check committed solids while the outer group can still roll back.");
            Assert.Contains("Guard.RollBack(group)", source.Substring(guard));
            Assert.Contains("FloorWallOverlapGuard.IsFinishFloor(doc, f)", source);
        }
    }
}
