using System;
using System.IO;
using Xunit;

namespace Horizun.Core.Tests
{
    public class CopyFinishUndoWiringTests
    {
        private static string Read(string name)
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory != null && !Directory.Exists(Path.Combine(directory.FullName, "src", "Horizun.Revit"))) directory = directory.Parent;
            Assert.NotNull(directory);
            return File.ReadAllText(Path.Combine(directory.FullName, "src", "Horizun.Revit", "Commands", name));
        }

        [Fact]
        public void Existing_floor_overlap_is_measured_before_creation_and_reported_as_a_no_op()
        {
            string source = Read("RoomFinishesCommand.cs");
            Assert.Contains("JArray overlaps = ExistingFinishFloors(doc, fp, offset, existingFloors)", source);
            Assert.Contains("BooleanOperationsUtils.ExecuteBooleanOperation(proposed, solid, BooleanOperationsType.Intersect)", source);
            Assert.Contains("RoomFinishPlanRules.RangesOverlap(bottom, top, box.Min.Z, box.Max.Z)", source);
            Assert.Contains("existing_finish_floor_overlap_xy_and_z", source);
            Assert.Contains("[\"skipped_rooms\"] = skipped", source);
            Assert.Contains("[\"no_op\"] = true", source);
        }

        [Fact]
        public void Created_dependencies_are_recorded_by_owner_before_an_undo_can_delete_them()
        {
            string source = Read("UndoCapture.cs");
            Assert.Contains("owner.GetDependentElements(null)", source);
            Assert.Contains("TransformCopyRules.KnownOwnedDependent(dependent.GetType().Name)", source);
            Assert.Contains("system.Elements.Cast<Element>().Any(member => !known.Contains", source);
            Assert.Contains("inverse[\"created_dependents\"] = dependents", source);
            Assert.Contains("list.Add(Rid.Value(dependentId))", source);
            Assert.Contains("e.After = States(doc, list)", source);
        }

    }
}
