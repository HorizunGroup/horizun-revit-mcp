// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// Entities written in their own OCS (extrusion not +Z) are put into world
// coordinates, and nested blocks are placed relative to their definition's base
// point (audit 2026-10-08, lote 7). Synthetic extractor rows; no drawing data.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class CadOcsAndBlockBaseTests
    {
        private static string T(params string[] f) => string.Join("\t", f);

        [Fact]
        public void The_arbitrary_axis_of_minus_Z_reflects_X()
        {
            CadPoint w = CadOcs.ToWorld(new CadPoint(10, 5, 0), 0, 0, -1);
            Assert.Equal(-10, w.X, 9);
            Assert.Equal(5, w.Y, 9);
            Assert.True(CadOcs.IsMirroredPlan(0, 0, -1));
            Assert.True(CadOcs.IsWorld(0, 0, 1));
            Assert.Equal(Math.PI - 0.5, CadOcs.DirectionToWorld(0.5, 0, 0, -1).Value, 9);
            Assert.Null(CadOcs.DirectionToWorld(0.5, 0.6, 0, 0.8));   // tilted: no plan angle
        }

        [Fact]
        public void A_tilted_extrusion_uses_the_arbitrary_axis_algorithm()
        {
            // N = X: |Nx| >= 1/64, so Ax = Z x N = Y and Ay = N x Ax = Z.
            CadPoint w = CadOcs.ToWorld(new CadPoint(1, 2, 3), 1, 0, 0);
            Assert.Equal(3, w.X, 9);
            Assert.Equal(1, w.Y, 9);
            Assert.Equal(2, w.Z, 9);
        }

        [Fact]
        public void A_mirrored_arc_lands_reflected_and_runs_clockwise()
        {
            var lines = new List<string>
            {
                "H\tinsunits\t4",
                // OCS centre (10,0), radius 5, 0 .. pi/2 radians, extrusion (0,0,-1)
                T("E", "*Model_Space", "A1", "ARC", "E-LITE", "10", "0", "0", "5", "0", "1.5707963"),
                T("X", "A1", "0", "0", "-1"),
                "H\tdone\t1"
            };
            CadIrEntity e = CadDwgExtract.Parse(lines).Entities.Single();
            Assert.Equal(-10, e.Arc.Centre.X, 4);
            Assert.Equal(-15, e.Arc.Start.X, 4);   // OCS (15,0)
            Assert.Equal(-10, e.Arc.End.X, 4);     // OCS (10,5)
            Assert.Equal(5, e.Arc.End.Y, 4);
            Assert.Equal(Math.PI / 2, e.Arc.SweepRadians, 5);
            Assert.True(e.Arc.Clockwise);
            Assert.Equal(-10, e.Points[0].X, 4);
        }

        [Fact]
        public void An_arc_with_the_default_extrusion_is_untouched()
        {
            var lines = new List<string>
            {
                "H\tinsunits\t4",
                T("E", "*Model_Space", "A2", "ARC", "E-LITE", "10", "0", "0", "5", "0", "1.5707963"),
                "H\tdone\t1"
            };
            CadIrEntity e = CadDwgExtract.Parse(lines).Entities.Single();
            Assert.Equal(10, e.Arc.Centre.X, 4);
            Assert.False(e.Arc.Clockwise);
        }

        [Fact]
        public void A_mirrored_insert_is_a_reflection_with_the_turn_reversed()
        {
            var lines = new List<string>
            {
                "H\tinsunits\t4",
                T("E", "*Model_Space", "I1", "INSERT", "E-SYM", "100", "0", "0", "0.5", "1", "1", "1", "DOOR"),
                T("X", "I1", "0", "0", "-1"),
                "H\tdone\t1"
            };
            CadIrEntity e = CadDwgExtract.Parse(lines).Entities.Single();
            Assert.Equal(-100, e.Points[0].X, 6);
            Assert.Equal(-1.0, e.ScaleX.Value, 9);
            Assert.Equal(2 * Math.PI - 0.5, e.RotationRadians.Value, 9);

            // The block's local (1,0) lands where the OCS reading puts it: OCS R(0.5)(1,0), reflected.
            CadPlacedBlock placed = CadBlockPlacement.Place(new[] { e }).Placed.Single();
            CadPoint w = placed.Place(new CadPoint(1, 0, 0));
            Assert.Equal(-100 - Math.Cos(0.5), w.X, 9);
            Assert.Equal(Math.Sin(0.5), w.Y, 9);
        }

        [Fact]
        public void A_nested_block_is_placed_relative_to_its_parent_definitions_base_point()
        {
            var lines = new List<string>
            {
                "H\tinsunits\t4",
                T("K", "PARENT", "0", "", "5", "5", "0"),
                T("K", "CHILD", "0", "", "0", "0", "0"),
                // CHILD drawn at (5,5) inside PARENT, whose base point is (5,5)
                T("E", "PARENT", "C1", "INSERT", "E-SYM", "5", "5", "0", "0", "1", "1", "1", "CHILD"),
                // PARENT placed at (1000,2000)
                T("E", "*Model_Space", "P1", "INSERT", "E-SYM", "1000", "2000", "0", "0", "1", "1", "1", "PARENT"),
                "H\tdone\t1"
            };
            CadDwgReading r = CadDwgExtract.Parse(lines);
            Assert.Equal(5, r.BlockBases["PARENT"].X, 9);

            CadPlacementReading placement = CadBlockPlacement.Place(r.Entities, CadBlockPlacement.DefaultMaxDepth, r.BlockBases);
            CadPlacedBlock child = placement.Placed.Single(p => p.Source.BlockName == "CHILD");
            Assert.Equal(1000, child.At.X, 9);   // on the parent's insertion point, not 5 mm off it
            Assert.Equal(2000, child.At.Y, 9);

            // And a point of PARENT's own definition at its base point sits on the insertion point.
            CadPlacedBlock parent = placement.Placed.Single(p => p.Source.BlockName == "PARENT");
            CadPoint at = parent.Place(new CadPoint(5, 5, 0));
            Assert.Equal(1000, at.X, 9);
            Assert.Equal(2000, at.Y, 9);
        }

        [Fact]
        public void An_extraction_without_base_points_reads_them_as_the_origin()
        {
            var lines = new List<string>
            {
                "H\tinsunits\t4",
                T("K", "OLD", "0", ""),
                "H\tdone\t1"
            };
            Assert.Empty(CadDwgExtract.Parse(lines).BlockBases);
        }
    }
}
