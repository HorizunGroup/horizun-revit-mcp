// -----------------------------------------------------------------------------
// Horizun Core - original Horizun code. Revit-free.
//
// AN ARC, A CIRCLE, A LIGHT POLYLINE, AN INSERT OR A TEXT IS WRITTEN IN ITS OWN
// OBJECT COORDINATE SYSTEM, NOT IN THE DRAWING'S.
//
// Those entities carry an extrusion direction (group 210). When it is not +Z -
// MIRROR flips it to -Z, and drawings made that way are common - their points and
// angles are in the OCS the DXF reference defines with the Arbitrary Axis
// Algorithm. Read as world coordinates, a mirrored arc sat on the wrong side of
// the drawing, reflected (audit 2026-10-08, lote 7). This turns OCS into world.
// -----------------------------------------------------------------------------
using System;

namespace Horizun.Revit.Core
{
    public static class CadOcs
    {
        /// <summary>The DXF reference's threshold: below it the arbitrary axis is built from world Y.</summary>
        private const double ArbitraryAxisLimit = 1.0 / 64.0;

        /// <summary>True when the extrusion is +Z, i.e. OCS and world agree.</summary>
        public static bool IsWorld(double nx, double ny, double nz)
        {
            double len = Math.Sqrt(nx * nx + ny * ny + nz * nz);
            if (len < 1e-12) return true;   // no direction at all is the default, +Z
            return Math.Abs(nx / len) < 1e-9 && Math.Abs(ny / len) < 1e-9 && nz / len > 0;
        }

        /// <summary>True when the extrusion is -Z: a plan reflection about world Y, nothing tilted.</summary>
        public static bool IsMirroredPlan(double nx, double ny, double nz)
        {
            double len = Math.Sqrt(nx * nx + ny * ny + nz * nz);
            if (len < 1e-12) return false;
            return Math.Abs(nx / len) < 1e-9 && Math.Abs(ny / len) < 1e-9 && nz / len < 0;
        }

        /// <summary>The OCS axes (Ax, Ay, N) of an extrusion direction, by the Arbitrary Axis Algorithm.</summary>
        public static void Axes(double nx, double ny, double nz, out double[] ax, out double[] ay, out double[] n)
        {
            double len = Math.Sqrt(nx * nx + ny * ny + nz * nz);
            if (len < 1e-12) { nx = 0; ny = 0; nz = 1; len = 1; }
            n = new[] { nx / len, ny / len, nz / len };
            double[] w = Math.Abs(n[0]) < ArbitraryAxisLimit && Math.Abs(n[1]) < ArbitraryAxisLimit
                ? new[] { 0.0, 1.0, 0.0 }     // world Y x N
                : new[] { 0.0, 0.0, 1.0 };    // world Z x N
            ax = Normalise(Cross(w, n));
            ay = Normalise(Cross(n, ax));
        }

        /// <summary>A point given in the OCS of <paramref name="nx"/>,<paramref name="ny"/>,<paramref name="nz"/>, in world coordinates.</summary>
        public static CadPoint ToWorld(CadPoint ocs, double nx, double ny, double nz)
        {
            double[] ax, ay, n;
            Axes(nx, ny, nz, out ax, out ay, out n);
            return new CadPoint(ocs.X * ax[0] + ocs.Y * ay[0] + ocs.Z * n[0],
                                ocs.X * ax[1] + ocs.Y * ay[1] + ocs.Z * n[1],
                                ocs.X * ax[2] + ocs.Y * ay[2] + ocs.Z * n[2]);
        }

        /// <summary>
        /// An OCS angle (measured about the extrusion from the OCS X axis) as a plan angle
        /// in world, for the two plan cases: +Z unchanged, -Z reflected (pi - angle for a
        /// direction). Null for a tilted extrusion, which has no plan angle.
        /// </summary>
        public static double? DirectionToWorld(double ocsAngle, double nx, double ny, double nz)
        {
            if (IsWorld(nx, ny, nz)) return ocsAngle;
            if (!IsMirroredPlan(nx, ny, nz)) return null;
            return Normalise(Math.PI - ocsAngle);
        }

        private static double Normalise(double a)
        {
            a %= 2 * Math.PI;
            return a < 0 ? a + 2 * Math.PI : a;
        }

        private static double[] Cross(double[] a, double[] b) =>
            new[] { a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0] };

        private static double[] Normalise(double[] v)
        {
            double l = Math.Sqrt(v[0] * v[0] + v[1] * v[1] + v[2] * v[2]);
            return l < 1e-15 ? v : new[] { v[0] / l, v[1] / l, v[2] / l };
        }
    }
}
