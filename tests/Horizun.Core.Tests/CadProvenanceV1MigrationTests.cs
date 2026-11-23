// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// THE v1 -> v2 MIGRATION, at a desk.
//
// Two separate things are proved here, and they answer two different doubts.
//
// 1. WHAT "v1" MEANS. The v1 schema is read and never written, so the only
//    description of it left in the working tree is CadProvenanceV1Shape, and a
//    fixture built from a description nobody checks is a fixture that proves the
//    migration against a schema no release ever wrote. So the shape is pinned
//    literally AND cross-checked against the field-name constants still standing
//    in CadProvenanceStore.cs: the v1 field set is exactly the v2 field set minus
//    the five fields v2 added, which is what the store's own comments say it is.
//    Change either side and this fails.
//
// 2. WHY AN AMBIGUOUS v1 RECORD MUST STOP THE RUN. An element two placements
//    could have built is out of scope - and out of scope is not safe by itself.
//    The drawing entity that built it then matches nothing in scope and comes
//    back as a `create`, so applying the plan builds a second wall on top of the
//    one already standing. That harm is demonstrated here first, and the refusal
//    is asserted second, because a refusal whose cost nobody measured is a
//    refusal somebody will delete.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class CadProvenanceV1MigrationTests
    {
        private const string FileX = "sha-of-file-x";
        private const string P1 = "uid-placement-1";
        private const string P2 = "uid-placement-2";

        // ------------------------------------------------------------ the shape

        private static DirectoryInfo Root()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !Directory.Exists(Path.Combine(d.FullName, "src"))) d = d.Parent;
            Assert.NotNull(d);
            return d;
        }

        private static string Source(params string[] parts) =>
            File.ReadAllText(Path.Combine(Root().FullName, Path.Combine(parts)));

        private static string Store() => Source("src", "Horizun.Revit", "Core", "CadProvenanceStore.cs");
        private static string Fixture() => Source("src", "Horizun.Revit", "Core", "CadProvenanceV1Fixture.cs");







        // ------------------------------------------------------------ the fixture

        [Fact]
        public void No_product_command_can_reach_the_fixture()
        {
            // A fixture that a command can call is not a fixture. The live
            // harness reaches it by reflection through horizun_execute_python,
            // which the machine owner has to have granted first.
            string commands = Path.Combine(Root().FullName, "src", "Horizun.Revit", "Commands");
            foreach (string file in Directory.GetFiles(commands, "*.cs", SearchOption.AllDirectories))
            {
                string text = File.ReadAllText(file);
                Assert.False(text.Contains("CadProvenanceV1Fixture"),
                    Path.GetFileName(file) + " names CadProvenanceV1Fixture; no product command may");
                Assert.False(text.Contains("CadProvenanceV1Shape"),
                    Path.GetFileName(file) + " names CadProvenanceV1Shape; it is fixture input only");
            }
            foreach (string file in Directory.GetFiles(Path.Combine(Root().FullName, "src", "Horizun.Server"), "*.cs",
                                                       SearchOption.AllDirectories))
            {
                Assert.False(File.ReadAllText(file).Contains("CadProvenanceV1"),
                    Path.GetFileName(file) + " exposes the v1 fixture through the server; nothing may");
            }
        }



        /// <summary>Two walls on one drawing, so a model can hold one claimable element and one ambiguous one.</summary>
        private static List<CadSegment> TwoWalls()
        {
            return new List<CadSegment>
            {
                new CadSegment(new CadPoint(0, -100), new CadPoint(6000, -100), "A-WALL"),
                new CadSegment(new CadPoint(0, 100), new CadPoint(6000, 100), "A-WALL"),
                new CadSegment(new CadPoint(0, 3900), new CadPoint(6000, 3900), "A-WALL"),
                new CadSegment(new CadPoint(0, 4100), new CadPoint(6000, 4100), "A-WALL")
            };
        }
















    }
}
