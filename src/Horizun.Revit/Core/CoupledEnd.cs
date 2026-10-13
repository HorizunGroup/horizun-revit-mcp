using System;

namespace Horizun.Revit.Core
{
    /// <summary>
    /// Where a coupled bar ends (acero 3.0, L7). MEASURED on Revit 2024 and 2026: two pieces declared to
    /// meet at a station and joined by a coupler come back with a gap inside the coupler - CPL16M, 56 mm
    /// long, left the second piece 12 mm short of the station - and the coupler type publishes no
    /// parameter that predicts it. So a coupled end is held to what a coupled splice IS: on the bar's
    /// declared line, inside the coupler's own body, with the declared station inside it too. Then the
    /// distance it moved is the trim, and everything else about the bar is compared exactly.
    /// </summary>
    public static class CoupledEnd
    {
        /// <summary>
        /// The trim of a coupled end, in mm, or -1 when it is not a seated coupled end: `declared` is
        /// the declared end at the splice, `otherEnd` the bar's other declared end (for its line),
        /// `observed` the end Revit drew, `boxMin`/`boxMax` the coupler's bounding box, `tolMm` the
        /// length tolerance.
        /// </summary>
        public static double TrimMm(double[] declared, double[] otherEnd, double[] observed, double[] boxMin, double[] boxMax, double tolMm)
        {
            if (declared == null || otherEnd == null || observed == null || boxMin == null || boxMax == null) return -1;
            double[] axis = { otherEnd[0] - declared[0], otherEnd[1] - declared[1], otherEnd[2] - declared[2] };
            double len = Math.Sqrt(axis[0] * axis[0] + axis[1] * axis[1] + axis[2] * axis[2]);
            if (len < 1e-9) return -1;
            double[] d = { observed[0] - declared[0], observed[1] - declared[1], observed[2] - declared[2] };
            double along = (d[0] * axis[0] + d[1] * axis[1] + d[2] * axis[2]) / len;
            double perp2 = d[0] * d[0] + d[1] * d[1] + d[2] * d[2] - along * along;
            if (Math.Sqrt(Math.Max(0, perp2)) > tolMm) return -1;
            if (!Inside(declared, boxMin, boxMax, tolMm) || !Inside(observed, boxMin, boxMax, tolMm)) return -1;
            return Math.Abs(along);
        }

        private static bool Inside(double[] p, double[] lo, double[] hi, double tol)
        {
            for (int i = 0; i < 3; i++)
                if (p[i] < Math.Min(lo[i], hi[i]) - tol || p[i] > Math.Max(lo[i], hi[i]) + tol) return false;
            return true;
        }
    }
}
