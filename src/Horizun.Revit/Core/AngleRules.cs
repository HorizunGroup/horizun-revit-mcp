using System;

namespace Horizun.Revit.Core
{
    /// <summary>Pure angle arithmetic for re-reads, in radians.</summary>
    public static class AngleRules
    {
        private const double Turn = 2 * Math.PI;

        /// <summary>The angle in [0, 2*pi).</summary>
        public static double Normalize(double radians)
        {
            double r = radians % Turn;
            if (r < 0) r += Turn;
            return r >= Turn ? 0 : r;
        }

        /// <summary>
        /// <paramref name="actual"/> moved by whole turns to the copy nearest
        /// <paramref name="expected"/>, so a tolerance compares the real angular gap.
        /// MEASURED (Revit 2023, 2026-10-23): a row asking 360 degrees normalised to 0
        /// while Revit stored a hair under 2*pi, and comparing the two normalised values
        /// refused a correct placement by a whole turn.
        /// </summary>
        public static double NearestTurn(double expected, double actual)
        {
            if (double.IsNaN(expected) || double.IsInfinity(expected) || double.IsNaN(actual) || double.IsInfinity(actual))
                return actual;
            double gap = Normalize(actual - expected);
            if (gap > Math.PI) gap -= Turn;
            return expected + gap;
        }
    }
}
