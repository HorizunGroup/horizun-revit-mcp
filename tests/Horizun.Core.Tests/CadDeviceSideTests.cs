// -----------------------------------------------------------------------------
// Horizun Core tests — original Horizun code.
//
// WHICH FACE A DEVICE DRAWN OVER ITS WALL GOES ON.
//
// MEASURED on a second apartment: three of forty-one devices were built in the
// next room, and the audit agreed with all three, because both read the symbol's
// rotation as the way it faces. Against the 49 symbols drawn clearly outside a
// wall - where the drawn point decides - that reading is false: a receptacle block
// faces its +x once its insertion's MIRROR is applied (26 of 26; 15 of 26 without
// the mirror), a switch or data outlet faces its +y, not its +x.
//
// These pin the rule that replaced it (CadDeviceSide): outside, the point; inside,
// a DECLARED facing, or else the centreline beyond a dead band, or nothing.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class CadDeviceSideTests
    {
        private static readonly CadVector North = new CadVector(0, 1);

        [Fact]
        public void Outside_the_wall_the_point_decides_whatever_is_declared()
        {
            string from;
            Assert.Equal(1, CadDeviceSide.Expected(120, 76.2, new CadVector(0, -1), North, 25, out from));
            Assert.Equal(CadDeviceSide.FromPoint, from);
            Assert.Equal(-1, CadDeviceSide.Expected(-80, 76.2, null, North, 25, out from));
        }

        [Fact]
        public void Inside_the_wall_a_declared_facing_that_points_out_of_a_face_decides()
        {
            string from;
            Assert.Equal(-1, CadDeviceSide.Expected(9.5, 76.2, new CadVector(0, -1), North, 25, out from));
            Assert.Equal(CadDeviceSide.FromDeclaredFacing, from);
            // A facing along the wall points out of neither face: the centreline decides.
            Assert.Equal(1, CadDeviceSide.Expected(54.8, 76.2, new CadVector(1, 0), North, 25, out from));
            Assert.Equal(CadDeviceSide.FromCentreline, from);
        }

        [Fact]
        public void Inside_the_wall_without_a_facing_only_a_point_clear_of_the_dead_band_decides()
        {
            string from;
            Assert.Equal(-1, CadDeviceSide.Expected(-46.9, 76.2, null, North, 25, out from));
            Assert.Equal(CadDeviceSide.FromCentreline, from);
            // MEASURED: 6.3 and 9.5 mm from the centreline - one of them built on
            // the wrong face by the side it was drawn on. Neither is evidence.
            Assert.Equal(0, CadDeviceSide.Expected(6.3, 76.2, null, North, 25, out from));
            Assert.Null(from);
            Assert.Equal(0, CadDeviceSide.Expected(-9.5, 76.2, null, North, 25, out from));
        }

        [Fact]
        public void Without_a_width_only_a_declared_facing_says_anything()
        {
            string from;
            Assert.Equal(0, CadDeviceSide.Expected(-300, null, null, North, 25, out from));
            Assert.Equal(1, CadDeviceSide.Expected(-300, null, North, North, 25, out from));
        }

        [Fact]
        public void A_declared_facing_is_mirrored_before_it_is_turned()
        {
            // MEASURED (row 10): a receptacle (+x) inserted with scale x = -1 and
            // turned 90 degrees faces SOUTH; without the mirror it would face north.
            CadVector v = CadDeviceSide.InPlan(new CadVector(1, 0), Math.PI / 2, -1, 1).Value;
            Assert.Equal(0, v.X, 9);
            Assert.Equal(-1, v.Y, 9);
            CadVector w = CadDeviceSide.InPlan(new CadVector(1, 0), Math.PI / 2, 1, 1).Value;
            Assert.Equal(1, w.Y, 9);
            // A switch (+y) turned 180 degrees faces south; a mirror in x leaves it.
            CadVector s = CadDeviceSide.InPlan(new CadVector(0, 1), Math.PI, -1, 1).Value;
            Assert.Equal(-1, s.Y, 9);
            // A facing turned by an unknown angle is not a facing.
            Assert.Null(CadDeviceSide.InPlan(new CadVector(1, 0), null, 1, 1));
        }

        // ---- the declaration ------------------------------------------------------

        private static CadRequirementSet Set(string facing)
        {
            string doc = @"{
              'schema': 'horizun.cad-requirements/1',
              'requirement_set': { 'id': 'elec', 'version': '1.0.0' },
              'source': { 'units': 'millimeter' },
              'tolerances': { 'point_mm': 25, 'gap_mm': 150, 'angle_degrees': 2, 'arc_sagitta_mm': 5 },
              'rules': [ { 'id': 'r', 'layers': ['E-P'], 'produces': 'electrical_fixture',
                           'family_type': 'Duplex Receptacle: Standard', 'level': 'Level 1', 'hosted_on': 'wall',
                           'geometry': { 'from': 'blocks', 'blocks': ['OUT2', 'S'] FACING } } ]
            }".Replace("FACING", facing).Replace('\'', '"');
            return CadRequirementSet.Load(JObject.Parse(doc));
        }

        [Fact]
        public void A_block_facing_must_be_one_of_the_four_axes()
        {
            CadRequirementSet ok = Set(", 'block_facing': { 'OUT2': '+x', 'S': '+y' }");
            Assert.Equal(2, ok.Rules[0].Geometry.BlockFacing.Count);
            var bad = Assert.Throws<CadRequirementSetException>(() => Set(", 'block_facing': { 'OUT2': 'north' }"));
            Assert.Contains("block_facing['OUT2']", bad.Message);
            Assert.Throws<CadRequirementSetException>(() => Set(", 'block_facing': ['OUT2']"));
            Assert.Throws<CadRequirementSetException>(() => Set(", 'block_facing': {}"));
        }

        [Fact]
        public void A_block_facing_on_a_rule_that_reads_line_work_is_refused()
        {
            string doc = @"{
              'schema': 'horizun.cad-requirements/1',
              'requirement_set': { 'id': 'w', 'version': '1' },
              'source': { 'units': 'millimeter' },
              'tolerances': { 'point_mm': 25, 'gap_mm': 150, 'angle_degrees': 2, 'arc_sagitta_mm': 5 },
              'rules': [ { 'id': 'r', 'layers': ['A-WALL'], 'produces': 'wall', 'family_type': 'Basic Wall: G',
                           'level': 'Level 1', 'height_mm': 2700,
                           'geometry': { 'from': 'double_lines', 'min_thickness_mm': 60, 'max_thickness_mm': 400,
                                         'block_facing': { 'X': '+x' } } } ]
            }".Replace('\'', '"');
            Assert.Throws<CadRequirementSetException>(() => CadRequirementSet.Load(JObject.Parse(doc)));
        }

        private static CadIrEntity Insert(string name, double rotationDegrees, double scaleX)
        {
            var e = new CadIrEntity
            {
                Id = "i" + name + rotationDegrees, Kind = CadEntityKind.BlockInstance, Layer = "E-P", BlockName = name,
                RotationRadians = rotationDegrees * Math.PI / 180.0, ScaleX = scaleX, ScaleY = 1
            };
            e.Points.Add(new CadPoint(1000 + rotationDegrees, 0, 0));
            return e;
        }

        [Fact]
        public void The_candidate_and_its_row_carry_the_declared_facing_and_the_dead_band()
        {
            CadRequirementSet set = Set(", 'block_facing': { 'OUT2': '+x' }");
            CadBlockReading r = CadBlockRules.Interpret(new List<CadIrEntity>
            {
                Insert("OUT2", 90, -1),     // mirrored and turned: faces south
                Insert("S", 180, 1)         // no facing declared for it
            }, set, "sha");
            CadCandidate outlet = r.Candidates.Single(c => c.SourceBlockName == "OUT2");
            CadCandidate sw = r.Candidates.Single(c => c.SourceBlockName == "S");
            Assert.Equal(-1, outlet.Facing.Value.Y, 9);
            Assert.Equal("OUT2", outlet.FacingDeclaredBy);
            Assert.Null(sw.Facing);

            var interp = new CadInterpretation();
            interp.Candidates.AddRange(r.Candidates);
            CadConversionPlan plan = CadConversionPlanRules.Plan(interp, set, "src", false);
            JObject outletRow = plan.Actions.Single(a => a.CandidateId == outlet.Id).Arguments;
            JObject switchRow = plan.Actions.Single(a => a.CandidateId == sw.Id).Arguments;
            Assert.Equal(-90, outletRow.Value<double>("facing_degrees"), 4);
            Assert.Equal(25, outletRow.Value<double>("side_dead_band_mm"));
            Assert.Null(switchRow["facing_degrees"]);
            Assert.Equal(25, switchRow.Value<double>("side_dead_band_mm"));
            // The creation contract admits both, on the row as the resolver sends it.
            var sent = new JObject
            {
                ["kind"] = "family_instance", ["point"] = outletRow["point"], ["coordinate_mode"] = "absolute",
                ["type_id"] = 1, ["level_id"] = 2, ["host_id"] = 3,
                ["face_allowance_mm"] = outletRow["face_allowance_mm"],
                ["facing_degrees"] = outletRow["facing_degrees"],
                ["side_dead_band_mm"] = outletRow["side_dead_band_mm"]
            };
            Assert.Null(Horizun.Contracts.ToolInputRules.ValidateCreation(sent, "family_instance"));
        }

        [Fact]
        public void The_plan_the_placement_and_the_audit_all_use_the_one_rule()
        {
            var d = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !System.IO.Directory.Exists(System.IO.Path.Combine(d.FullName, "src"))) d = d.Parent;
            Assert.NotNull(d);
            Func<string, string> read = rel => System.IO.File.ReadAllText(System.IO.Path.Combine(d.FullName, rel));
            string plan = read(@"src\Horizun.Revit\Commands\PlanFromCadCommand.cs");
            string create = read(@"src\Horizun.Revit\Commands\CreateElementsCommand.cs");
            string place = read(@"src\Horizun.Revit\Commands\CreateElementsPlacement.cs");
            string audit = read(@"src\Horizun.Revit\Core\CadAuditRules.cs");

            Assert.Contains("CadDeviceSide.Expected(offset, best.Width * 304.8 / 2.0, facing, normal,", plan);
            Assert.Contains("\"side_of_the_wall_not_stated\"", plan);
            Assert.Contains("p.Input.Value<double?>(\"facing_degrees\") * Math.PI / 180.0", create);
            Assert.Contains("p.Input.Value<double?>(\"side_dead_band_mm\")", create);
            Assert.Contains("double? pointsAt = sideRule ? facingRadians : rotationRadians;", place);
            Assert.Contains("CadDeviceSide.Expected(drawnSide, half, c.Facing, n,", audit);
            Assert.DoesNotContain("the symbol's rotation, because it is drawn inside the wall's thickness", audit);
        }

        [Fact]
        public void A_wall_based_family_is_placed_on_its_wall_line_and_faces_the_drawn_side()
        {
            // MEASURED: Revit put a wall-based test family on the wall's location line,
            // 98.8 mm from the drawn point, and the postcondition refused the whole stage.
            var d = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !System.IO.Directory.Exists(System.IO.Path.Combine(d.FullName, "src"))) d = d.Parent;
            Assert.NotNull(d);
            Func<string, string> read = rel => System.IO.File.ReadAllText(System.IO.Path.Combine(d.FullName, rel));
            string create = read(@"src\Horizun.Revit\Commands\CreateElementsCommand.cs");
            string geometry = read(@"src\Horizun.Revit\Commands\CreateElementsGeometry.cs");

            Assert.Contains("[\"route\"] = \"wall_based_on_the_wall_line\"", create);
            Assert.Contains("p.Start = onLine;", create);
            Assert.Contains("placed.flipFacing();", create);
            Assert.Contains("[\"facing_by\"] = \"reflected_across_the_wall_line\"", create);
            Assert.Contains("side_of_the_wall_not_stated: the symbol is drawn", create);
            Assert.Contains("the drawn point projects beyond the ends of its host wall", create);
            Assert.Contains("Exact(\"facing_side\", Direction(p.HostedFacing),", geometry);
            Assert.Contains("p.FacePlacement == null && p.HostedFacing == null && !reflectedCopy", geometry);
            Assert.Contains("Exact(\"facing_after_reflection\"", geometry);
            Assert.Contains("reflected_twice (a half turn on the same wall)", create);
            Assert.Contains("if (p.HostedEvidence != null) row[\"placement\"] = p.HostedEvidence;", geometry);
        }
    }
}
