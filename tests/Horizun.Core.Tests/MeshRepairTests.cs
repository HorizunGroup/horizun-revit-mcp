using System;
using System.Collections.Generic;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class MeshRepairTests
    {
        // A closed box, then one face replaced by two triangles that leave a thin crack: what Revit's
        // per-face triangulation does along an edge two faces tessellate differently (measured, Revit 2024:
        // a round column of a real model with 4 edges not used once in each direction).
        private static HostMesh Cracked(double crackMm)
        {
            HostMesh m = HostMesh.Box(new double[] { 0, 0, 0 }, new double[] { 1000, 400, 400 });
            // The top face (4, 5, 6, 7) is triangles 2 and 3; remove them and rebuild it with a sliver missing:
            // a 5th vertex a hair off the edge 5-6 splits it, and only one side knows.
            m.Triangles.RemoveRange(2, 2);
            int mid = m.AddVertex(1000 - crackMm, 200, 400);
            m.AddTriangle(4, 5, mid); m.AddTriangle(4, mid, 6); m.AddTriangle(4, 6, 7);
            return m;
        }

        [Fact]
        public void A_crack_no_wider_than_the_chord_tolerance_is_closed_and_said()
        {
            HostMesh m = Cracked(0.5);
            Assert.False(SolidContainment.Diagnose(m).Usable);
            HostMesh fixedMesh = MeshRepair.CloseCracks(m, 1.0, out int closed, out double widest);
            Assert.NotNull(fixedMesh);
            Assert.Equal(1, closed);
            Assert.InRange(widest, 0.49, 0.51);
            Assert.True(SolidContainment.Diagnose(fixedMesh).Usable, SolidContainment.Diagnose(fixedMesh).Why);
            Assert.Contains("crack", fixedMesh.Source);
            // Inside-ness is right after the repair: the centre is inside, a point above is not.
            Assert.InRange(Math.Abs(SolidContainment.WindingNumber(fixedMesh, new double[] { 500, 200, 200 })), 0.99, 1.01);
            Assert.InRange(Math.Abs(SolidContainment.WindingNumber(fixedMesh, new double[] { 500, 200, 500 })), -0.01, 0.01);
        }

        [Fact]
        public void A_hole_wider_than_the_chord_tolerance_is_not_closed()
        {
            HostMesh m = Cracked(30);
            Assert.Null(MeshRepair.CloseCracks(m, 1.0, out int closed, out double widest));
            Assert.Equal(0, closed);
            Assert.True(widest > 1.0);
        }

        [Fact]
        public void A_closed_mesh_is_returned_unchanged()
        {
            HostMesh m = HostMesh.Box(new double[] { 0, 0, 0 }, new double[] { 1, 1, 1 });
            HostMesh same = MeshRepair.CloseCracks(m, 1.0, out int closed, out _);
            Assert.Same(m, same);
            Assert.Equal(0, closed);
        }
    }
}
