using System;
using System.Collections.Generic;

namespace Horizun.Revit.Core
{
    /// <summary>
    /// hold_ends (acero 3.0, stage 1b): Revit pulls a bar's end onto the cover of the element it runs into
    /// (measured, Revit 2024, anonymized regression case: up to 12 mm on a beam bar anchored in a column). With hold_ends the apply
    /// re-pins the bar's handles to fixed distances from the host faces until the drawn bar is back on the
    /// plan. This is the measure: the planned and drawn vertices, corresponding one to one.
    /// </summary>
    public static class HoldEndsRules
    {
        /// <summary>A held bar is within this of its plan at every vertex.</summary>
        public const double ToleranceMm = 1.0;

        /// <summary>
        /// The largest and the summed distance between corresponding planned and drawn vertices. False (NaN)
        /// when the two have different vertex counts: Revit drew another shape, and there is no correspondence.
        /// </summary>
        public static bool Deviation(IList<double[]> planned, IList<double[]> drawn, out double worstMm, out double totalMm)
        {
            worstMm = double.NaN; totalMm = double.NaN;
            if (planned == null || drawn == null || planned.Count == 0 || planned.Count != drawn.Count) return false;
            worstMm = 0; totalMm = 0;
            for (int i = 0; i < planned.Count; i++)
            {
                double dx = planned[i][0] - drawn[i][0], dy = planned[i][1] - drawn[i][1], dz = planned[i][2] - drawn[i][2];
                double d = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                worstMm = Math.Max(worstMm, d); totalMm += d;
            }
            return true;
        }

        public static bool Held(double worstMm) => !double.IsNaN(worstMm) && worstMm <= ToleranceMm;
    }
}
