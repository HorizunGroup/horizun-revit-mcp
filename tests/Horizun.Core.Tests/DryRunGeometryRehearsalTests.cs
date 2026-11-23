// 5.0.5, measured on a course model in Revit 2024: the dry run of create_elements said "valid" for a floor that
// crossed a wall and for stairs whose top passed a level; the apply then refused, writing nothing. And the refusal
// called a basic wall a "Curtain wall".
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class DryRunGeometryRehearsalTests
    {
        [Fact]
        public void Batches_the_apply_measures_on_geometry_are_rehearsed_in_revit()
        {
            Assert.True(GeometryRehearsalRules.ShouldRehearse(1, stairs: true, newFloor: false, newWall: false, modelHasWalls: false, modelHasFinishFloors: false));
            Assert.True(GeometryRehearsalRules.ShouldRehearse(1, false, newFloor: true, newWall: false, modelHasWalls: true, modelHasFinishFloors: false));
            Assert.True(GeometryRehearsalRules.ShouldRehearse(2, false, newFloor: true, newWall: true, modelHasWalls: false, modelHasFinishFloors: false));
            Assert.True(GeometryRehearsalRules.ShouldRehearse(4, false, newFloor: false, newWall: true, modelHasWalls: true, modelHasFinishFloors: true));
            // Nothing the guard could find: a wall with no finish floor, a floor with no wall, other kinds.
            Assert.False(GeometryRehearsalRules.ShouldRehearse(4, false, false, newWall: true, modelHasWalls: true, modelHasFinishFloors: false));
            Assert.False(GeometryRehearsalRules.ShouldRehearse(1, false, newFloor: true, newWall: false, modelHasWalls: false, modelHasFinishFloors: true));
            Assert.False(GeometryRehearsalRules.ShouldRehearse(3, false, false, false, true, true));
            // Large batches stay argument-only and say so.
            Assert.False(GeometryRehearsalRules.ShouldRehearse(201, false, true, true, true, true));
            Assert.True(GeometryRehearsalRules.ShouldRehearse(200, false, true, true, true, true));
            Assert.Contains("validation_mode=revit_rollback", GeometryRehearsalRules.SkippedNote);
        }

        [Fact]
        public void The_dry_run_uses_the_rule_and_marks_a_rehearsal_refusal()
        {
            string cmd = Source("src", "Horizun.Revit", "Commands", "CreateElementsCommand.Execution.cs");
            Assert.Contains("bool measuredOnApply = RehearsesGeometryChecks(doc, plans);", cmd);
            Assert.Contains("validationMode == \"revit_rollback\" || measuredOnApply ||", cmd);
            Assert.Contains("failed[\"apply_would_refuse\"] = true;", cmd);
            Assert.Contains("result[\"geometry_checks_note\"] = GeometryRehearsalRules.SkippedNote;", cmd);
            string span = Source("src", "Horizun.Revit", "Commands", "CreateElementsLevelSpan.cs");
            Assert.Contains("GeometryRehearsalRules.ShouldRehearse(plans.Count, stairs, newFloor, newWall, modelHasWalls, modelHasFinishFloors)", span);
        }

        [Fact]
        public void A_geometry_conflict_is_a_precondition_not_a_schema_error()
        {
            Assert.Equal(ErrorCodes.PrecondicionNoCumplida, GeometryRehearsalRules.ConflictErrorCode("floor_wall_overlap_increased: [{...}]"));
            Assert.Equal(ErrorCodes.PrecondicionNoCumplida, GeometryRehearsalRules.ConflictErrorCode("Stairs level_span crosses Mid at 1500 mm"));
            Assert.Null(GeometryRehearsalRules.ConflictErrorCode("Creation returned no element."));
            Assert.Null(GeometryRehearsalRules.ConflictErrorCode(null));
            // The live refusal, classified from what it carries: the declared code wins over the message's words.
            var c = ErrorCodes.Classify("Atomic creation failed at item 0: floor_wall_overlap_increased: ... otherwise specify an explicit overlap tolerance.",
                new Newtonsoft.Json.Linq.JObject { ["code"] = "revit_creation_failed", ["error_code"] = GeometryRehearsalRules.ConflictErrorCode("floor_wall_overlap_increased: x") });
            Assert.Equal(ErrorCodes.PrecondicionNoCumplida, c.Code);
            Assert.Contains("[\"error_code\"] = GeometryRehearsalRules.ConflictErrorCode(ex.Message)", Source("src", "Horizun.Revit", "Commands", "CreateElementsCommand.Execution.cs"));
            Assert.Contains("[\"error_code\"] = GeometryRehearsalRules.ConflictErrorCode(ex.Message),", Source("src", "Horizun.Revit", "Commands", "CreateStairsGeometry.cs"));
            Assert.Contains("throw new InvalidOperationException(\"Stairs level_span crosses \" +", Source("src", "Horizun.Revit", "Commands", "CreateStairsGeometry.cs"));
        }

        [Fact]
        public void A_basic_wall_is_never_called_a_curtain_wall()
        {
            string basic = CurtainFloorOverlapRules.Guidance(CurtainFloorOverlapRules.SolidIntersectionMethod, 77,
                CurtainFloorOverlapRules.WallLabel("Basic wall", "Generic - 200mm"));
            Assert.StartsWith("Basic wall 'Generic - 200mm' 77 overlap measured by solid_intersection.", basic);
            Assert.DoesNotContain("Curtain", basic);
            Assert.StartsWith("Wall 5 ", CurtainFloorOverlapRules.Guidance(CurtainFloorOverlapRules.SolidIntersectionMethod, 5, null));
            Assert.Equal("Stacked wall", CurtainFloorOverlapRules.WallLabel("Stacked wall", " "));
            string guard = Source("src", "Horizun.Revit", "Commands", "FloorWallOverlapGuard.cs");
            Assert.DoesNotContain("CurtainFloorOverlapRules.Guidance(method, wall) }", guard);
            Assert.DoesNotContain("CurtainFloorOverlapRules.Guidance(after.Methods[key], pair.Item2) }", guard);
            Assert.Contains("snapshot.Labels[key] = label;", guard);
        }

        private static string Source(params string[] parts)
        {
            DirectoryInfo d = new DirectoryInfo(Here());
            while (d != null && !Directory.Exists(Path.Combine(d.FullName, "src"))) d = d.Parent;
            Assert.NotNull(d);
            return File.ReadAllText(Path.Combine(new[] { d.FullName }.Concat(parts).ToArray()));
        }

        private static string Here([CallerFilePath] string path = "") => Path.GetDirectoryName(path);
    }
}
