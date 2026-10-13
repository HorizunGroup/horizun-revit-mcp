// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// Backlog #49: a ramp's built slope was rise over the DIAGONAL of its plan box, so a
// straight ramp 1.5 m wide read flatter than it is. The run of a straight ramp is the
// box's long side. (The figure only feeds the measurement's note.)
// -----------------------------------------------------------------------------
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class RampBuiltSlopeTests
    {
        [Fact]
        public void A_straight_ramp_runs_along_its_long_side_not_its_diagonal()
        {
            Assert.Equal(12000.0, AccessRules.RampRunMm(12000, 1500), 6);
            Assert.Equal(12000.0, AccessRules.RampRunMm(1500, 12000), 6);
            Assert.Equal(8.33, System.Math.Round(100.0 * 1000 / AccessRules.RampRunMm(12000, 1500), 2), 6);
        }

        [Fact]
        public void The_geometry_measurement_uses_the_run_rule()
        {
            string src = System.IO.File.ReadAllText(System.IO.Path.Combine(Root(), "src", "Horizun.Revit", "Core", "AccessGeometry.cs"));
            Assert.Contains("AccessRules.RampRunMm(runX, runY)", src);
            Assert.DoesNotContain("Math.Sqrt(runX * runX + runY * runY)", src);
        }

        private static string Root()
        {
            var d = new System.IO.DirectoryInfo(System.AppContext.BaseDirectory);
            while (d != null && !System.IO.File.Exists(System.IO.Path.Combine(d.FullName, "AGENTS.md"))) d = d.Parent;
            return d.FullName;
        }
    }
}
