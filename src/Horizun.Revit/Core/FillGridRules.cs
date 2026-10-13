// -----------------------------------------------------------------------------
// Horizun Core - original Horizun code. Revit-free.
//
// HOW A FILL GRID'S DIRECTION IS COMPARED (backlog #50). A crosshatch pattern has
// two grids; the second one runs at the first one's angle plus 90 degrees. Only
// grid 0 used to be re-read, so a crosshatch whose second grid came back wrong
// still read as verified. A line family at angle a is the same family at a + pi,
// so directions are compared modulo pi.
// -----------------------------------------------------------------------------
using System;

namespace Horizun.Revit.Core
{
    public static class FillGridRules
    {
        /// <summary>The direction the second grid of a crosshatch is created at.</summary>
        public static double CrosshatchSecondAngle(double firstAngleRad) => firstAngleRad + Math.PI / 2;

        /// <summary>The smallest difference between two line directions, modulo pi: 0 to pi/2.</summary>
        public static double LineDirectionDelta(double expectedRad, double actualRad)
        {
            double d = (actualRad - expectedRad) % Math.PI;
            if (d < 0) d += Math.PI;
            return Math.Min(d, Math.PI - d);
        }
    }
}
