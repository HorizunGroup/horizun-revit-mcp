// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// Backlog #50: a crosshatch fill pattern re-read only grid 0; the second grid was
// never verified.
// -----------------------------------------------------------------------------
using System;
using System.IO;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class FillGridRulesTests
    {
        [Theory]
        [InlineData(Math.PI / 2, Math.PI / 2, 0.0)]
        [InlineData(Math.PI / 2, -Math.PI / 2, 0.0)]
        [InlineData(Math.PI / 6, Math.PI / 6 + Math.PI, 0.0)]
        [InlineData(0.0, 0.1, 0.1)]
        [InlineData(0.0, Math.PI - 0.1, 0.1)]
        public void Line_directions_compare_modulo_pi(double expected, double actual, double delta)
            => Assert.Equal(delta, FillGridRules.LineDirectionDelta(expected, actual), 9);

        [Fact]
        public void The_second_grid_of_a_crosshatch_is_square_to_the_first()
            => Assert.Equal(0.0, FillGridRules.LineDirectionDelta(FillGridRules.CrosshatchSecondAngle(0.3), 0.3 - Math.PI / 2), 9);

        [Fact]
        public void A_crosshatch_reads_its_second_grid_back()
        {
            string src = File.ReadAllText(Path.Combine(Root(), "src", "Horizun.Revit", "Commands", "ManageStylesCommand.cs"));
            Assert.Contains("GetFillGrids()[1]", src);
            Assert.Contains("FillGridRules.LineDirectionDelta(FillGridRules.CrosshatchSecondAngle(angle)", src);
            Assert.Contains("\"spacing2\"", src);
        }

        private static string Root()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, "AGENTS.md"))) d = d.Parent;
            return d.FullName;
        }
    }
}
