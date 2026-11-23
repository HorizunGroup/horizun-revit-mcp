using System;
using System.IO;
using Xunit;

namespace Horizun.Core.Tests
{
    public class RoomFinishWallCoverageTests
    {
        private static string Source(string name = "RoomFinishesCommand.cs")
        {
            string baseline = Environment.GetEnvironmentVariable("HORIZUN_TEST_ROOM_FINISH_SOURCE");
            if (name == "RoomFinishesCommand.cs" && !string.IsNullOrWhiteSpace(baseline)) return File.ReadAllText(baseline);
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory != null)
            {
                string path = Path.Combine(directory.FullName, "src", "Horizun.Revit", "Commands", name);
                if (File.Exists(path)) return File.ReadAllText(path);
                directory = directory.Parent;
            }
            throw new InvalidOperationException("repository root not found");
        }

        [Fact]
        public void Floor_creation_and_committed_readback_do_not_limit_overlap_to_boundary_owners()
        {
            string source = Source();
            Assert.DoesNotContain("foreach (long w in fp.Walls)", source);
            Assert.DoesNotContain("foreach (long wall in fp.Walls)", source);
            Assert.Contains("foreach (long w in FloorWallCandidates(d, f, fp.Walls))", source);
            Assert.Contains("foreach (long wall in FloorWallCandidates(d, f, fp.Walls))", source);
            Assert.Contains("new BoundingBoxIntersectsFilter(outline, 1e-6)", source);
            Assert.Contains("RoomFinishPlanRules.WallsForFloorVerification(boundaryWalls, nearby)", source);
            Assert.Contains("w.get_BoundingBox(null) == null", source);
        }

        [Fact]
        public void Wall_edits_recheck_the_adjacent_finish_floors_against_other_host_walls()
        {
            string source = Source("FloorWallOverlapGuard.cs");
            Assert.Contains("if (includeDependentFloors)", source);
            Assert.Contains("changed.Add(floor)", source);
            Assert.Contains("snapshot.ElementIds.Add(Rid.Value(e.Id))", source);
            Assert.Contains("Capture(doc, ids, false)", source);
            Assert.Contains("FloorWallOverlapChangeProblem(old, current", source);
            Assert.Contains("FLOOR_PARAM_IS_STRUCTURAL", source);
        }

        [Fact]
        public void Nonstructural_floor_protection_does_not_depend_on_the_material_layer_label()
        {
            // room_finishes accepts a one-layer FloorType labelled Structure too.
            // Such a nonstructural floor must remain protected after copying or reconstruction.
            string source = Source("FloorWallOverlapGuard.cs");
            Assert.DoesNotContain("GetCompoundStructure()", source);
            Assert.Contains("return structural.AsInteger() != 1", source);
        }
    }
}
