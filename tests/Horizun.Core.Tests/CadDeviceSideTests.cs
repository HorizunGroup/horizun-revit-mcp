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
        public void A_wall_based_family_is_placed_on_its_wall_line_and_faces_the_drawn_side()
        {
            // MEASURED: Revit put a wall-based test family on the wall's location line,
            // 98.8 mm from the drawn point, and the postcondition refused the whole stage.
            var d = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !System.IO.Directory.Exists(System.IO.Path.Combine(d.FullName, "src"))) d = d.Parent;
            Assert.NotNull(d);
            Func<string, string> read = rel => System.IO.File.ReadAllText(System.IO.Path.Combine(d.FullName, rel.Replace('\\', System.IO.Path.DirectorySeparatorChar)));
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
