// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// Laps (refuerzo desde cartilla, F2.1): a section layer spliced at declared
// stations, each splice a declared overlap - in mm, or in bar diameters, the
// multiplier being the caller's, never a code's.
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class SectionLapTests
    {


        private static SectionHostFrame Frame() => new SectionHostFrame
        {
            ElementId = 7, OriginMm = new[] { 0.0, 0, 0 }, U = new[] { 1.0, 0, 0 }, V = new[] { 0, 1.0, 0 }, W = new[] { 0, 0, 1.0 },
            UMin = 0, UMax = 6000, VMin = -150, VMax = 150, WMin = -500, WMax = 0
        };























        // ------------------------------------------------------------ the audit

        private static List<double[]> Line(double u0, double u1, double v = 0) =>
            new List<double[]> { new[] { u0, v, 0.0 }, new[] { u1, v, 0.0 } };













        [Fact]
        public void Laps_are_checked_and_say_how_far()
        {
            Assert.Contains(RebarFinding.LapInsufficient, RebarFinding.All);
            Assert.DoesNotContain("lap_insufficient", RebarFinding.NotChecked);
            Assert.Contains("undeclared_lap", RebarFinding.NotChecked);
        }
    }
}
