// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// EXTENTS FROM A NAMED AXIS (stage 1c): a schedule card measures its bars and its
// stirrup zones from its own axis labels, and says nothing about where the host's
// start face is. A section rule can now say the same - "from axis B, 0..1200 mm
// toward C" - and the plan places it by finding grids B and C in the model and
// where the host's axis crosses them. Every name the plan cannot place, and every
// host the axes do not bound, is refused by name; nothing is placed on a guess.
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class SectionAxisExtentTests
    {


        private const string FromB = "{ 'name': 'bottom', 'face': 'bottom', 'count': 2, 'bar_type': 'N5', 'start': { 'hook_type': 'H90' }, " +
                                     "'extend': { 'axis': 'B', 'toward': 'C', 'from_mm': 200, 'to_mm': 2200 } }";

        // A beam 300 x 500 along world X, its solid from x = 150 to x = 5850 (between column faces);
        // grid B crosses its axis at x = 0 and grid C at x = 6000 - or the other way round.
        private static SectionHostFrame Beam(double bAt = 0, double cAt = 6000) => new SectionHostFrame
        {
            ElementId = 7, OriginMm = new[] { 0.0, 0, 0 }, U = new[] { 1.0, 0, 0 }, V = new[] { 0, 1.0, 0 }, W = new[] { 0, 0, 1.0 },
            UMin = 150, UMax = 5850, VMin = -150, VMax = 150, WMin = -250, WMax = 250,
            AxisStations = new Dictionary<string, double> { ["B"] = bAt, ["C"] = cAt }
        };

















        [Fact]
        public void A_grid_s_station_is_where_the_host_s_axis_crosses_it_in_plan()
        {
            // A sloped beam rising 1 in 4 along +X, and a grid at x = 3000 drawn short of it.
            double s = 1 / System.Math.Sqrt(1 + 0.0625);
            double? t = SectionRules.AxisStation(new[] { 0.0, 0, 0 }, new[] { s, 0, 0.25 * s }, new[] { 3000.0, 500 }, new[] { 3000.0, 900 }, "B", out string p);
            Assert.Null(p);
            Assert.Equal(3000 / s, t.Value, 6);
            Assert.Null(SectionRules.AxisStation(new[] { 0.0, 0, 0 }, new[] { 1.0, 0, 0 }, new[] { 0.0, 100 }, new[] { 5000.0, 100 }, "A", out p));
            Assert.StartsWith(SectionRules.CodeAxisParallel, p);
        }



        // ---- BARS BETWEEN CONTINUOUS BARS (layer.between) ----------------------------------------
        private const string Three = "{ 'name': 'top', 'face': 'top', 'count': 3, 'bar_type': 'N5', 'extend': { 'from_mm': 1000, 'to_mm': 4000 } }";

        private static string Mid(int count, double from = 1500, double to = 3000, string between = "top", string face = "top") =>
            "{ 'name': 'mid', 'face': '" + face + "', 'count': " + count + ", 'bar_type': 'N5', 'between': '" + between + "', " +
            "'extend': { 'from_mm': " + from + ", 'to_mm': " + to + " } }";














    }
}
