using System;
using System.IO;
using Xunit;

namespace Horizun.Core.Tests
{
    public class FloorCornerOverlapWiringTests
    {
        private static string Source()
        {
            string baseline = Environment.GetEnvironmentVariable("HORIZUN_TEST_FLOOR_CORNER_SOURCE");
            if (!string.IsNullOrWhiteSpace(baseline)) return File.ReadAllText(baseline);
            for (DirectoryInfo dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                string path = Path.Combine(dir.FullName, "src", "Horizun.Revit", "Commands", "JoinGeometryCommand.Corners.cs");
                if (File.Exists(path)) return File.ReadAllText(path);
            }
            throw new FileNotFoundException("JoinGeometryCommand.Corners.cs");
        }

        [Fact]
        public void Corner_extension_captures_finish_floors_before_writing_and_checks_increases_after_regeneration()
        {
            string source = Source();
            int capture = source.IndexOf("floorOverlap = FloorWallOverlapGuard.Capture(d, finishFloorIds)", StringComparison.Ordinal);
            Assert.True(capture >= 0, "Corner edits need a pre-write finish-floor intersection snapshot.");
            int mutate = source.IndexOf("WallUtils.AllowWallJoinAtEnd(w, c.End)", StringComparison.Ordinal);
            int regenerate = source.IndexOf("d.Regenerate()", mutate, StringComparison.Ordinal);
            Assert.True(regenerate > mutate, "Corner edits must regenerate before floor preservation is measured.");
            int verify = source.IndexOf("FloorWallOverlapGuard.ThrowIfIncreased(d, floorOverlap, affectedWallIds)", regenerate, StringComparison.Ordinal);
            Assert.True(capture >= 0 && mutate > capture && regenerate > mutate && verify > regenerate,
                "Corner edits must not retain a new finish floor/wall crossing introduced by extension or auto joins.");
            Assert.Contains("FloorWallOverlapGuard.IsFinishFloor(d, f)", source);
        }

        [Fact]
        public void Corner_committed_readback_rechecks_floor_wall_preservation()
        {
            string source = Source();
            int verify = source.IndexOf("edit.Verify = d =>", StringComparison.Ordinal);
            Assert.True(verify >= 0);
            Assert.Contains("FloorWallOverlapGuard.ThrowIfIncreased(d, floorOverlap, affectedWallIds)", source.Substring(verify));
        }
    }
}
