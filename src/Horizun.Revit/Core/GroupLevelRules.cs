using System;

namespace Horizun.Revit.Core
{
    public static class GroupLevelRules
    {
        public static double OffsetMmForWorldZ(double worldZMm, double newLevelElevationMm) =>
            worldZMm - newLevelElevationMm;

        public static bool PositionHeld(double beforeX, double beforeY, double beforeZ,
            double afterX, double afterY, double afterZ, double toleranceMm)
        {
            double dx = afterX-beforeX, dy = afterY-beforeY, dz = afterZ-beforeZ;
            return Math.Sqrt(dx*dx + dy*dy + dz*dz) <= toleranceMm;
        }
    }
}
