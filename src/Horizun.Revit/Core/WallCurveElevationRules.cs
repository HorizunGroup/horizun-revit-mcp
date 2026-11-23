using System;
using System.Globalization;

namespace Horizun.Revit.Core
{
    /// <summary>
    /// Field case (2026-11-09): set_curve on a wall with a 3.0 base offset, the line sent at z=3.0; the wall rose to 6.0 and
    /// the reply said verified_applied, because only the LocationCurve's ends were re-read and they were where they were sent.
    /// Measured on Revit 2025 the same day: a wall's LocationCurve lies at its base LEVEL's elevation, NOT at its base; the base
    /// offset is added on top and is not changed by setting the curve. A line at z=3 over a level at 0 leaves the offset at 3
    /// and floats the solid to 3 + 3 = 6. So (1) the rehearsal refuses a line whose z differs from the wall's current line,
    /// and (2) the verification measures the solid's base (line z + base offset), which must not move.
    /// </summary>
    public static class WallCurveElevationRules
    {
        public const string Code = "elevacion_de_muro_distinta";

        /// <summary>Null when the requested line keeps the wall's line z; otherwise the refusal (nothing written).</summary>
        public static string Refusal(long elementId, double currentZ, double wantZ0, double wantZ1, double baseOffset, double tolerance)
        {
            if (double.IsNaN(currentZ)) return null;
            if (Math.Abs(wantZ0 - currentZ) <= tolerance && Math.Abs(wantZ1 - currentZ) <= tolerance) return null;
            string offset = double.IsNaN(baseOffset) ? "its base offset" : "its base offset (" + Mm(baseOffset) + " mm)";
            return "ElementId " + elementId + " is a wall: its line lies at its base level's elevation (z " + Mm(currentZ) + " mm), and Revit " +
                   "adds " + offset + " on top of the line's z without changing the offset. The requested line (z " + Mm(wantZ0) + " / " +
                   Mm(wantZ1) + " mm) would float the wall " + Mm((wantZ0 + wantZ1) / 2 - currentZ) + " mm off its base. To move it in plan " +
                   "send start and end at z " + Mm(currentZ) + " mm; to change its height write WALL_BASE_OFFSET (or its base level) with " +
                   "horizun_write_params_verified. Nothing was written. (" + Code + ")";
        }

        /// <summary>
        /// Null when the wall's base (line z + base offset, feet) is where it was; otherwise the reason. A NaN is a failure:
        /// nothing unmeasured is verified.
        /// </summary>
        public static string Judge(double baseBefore, double baseAfter, double tolerance)
        {
            if (double.IsNaN(baseBefore) || double.IsNaN(baseAfter))
                return "The wall's base (line z + base offset) could not be read before and after the write, so its height was not " +
                       "verified. (" + Code + ")";
            if (Math.Abs(baseAfter - baseBefore) <= tolerance) return null;
            return "The wall's base (line z + base offset) moved from " + Mm(baseBefore) + " to " + Mm(baseAfter) + " mm: the line " +
                   "landed on the requested ends, but the wall's solid is not where it was. (" + Code + ")";
        }

        private static string Mm(double feet) => Math.Round(feet * 304.8, 1).ToString(CultureInfo.InvariantCulture);
    }
}
