using System;

namespace Horizun.Revit.Core
{
    public static class PlanimetrySlopeRules
    {
        public static double RatioFromHeights(double x0, double x1, double z0, double z1)
        {
            if (!Finite(x0) || !Finite(x1) || !Finite(z0) || !Finite(z1) || Math.Abs(x1 - x0) < 1e-9)
                throw new ArgumentException("Slope arrow requires two distinct finite stations and elevations.");
            return (z1 - z0) / (x1 - x0);
        }

        public static double[] ElevationsOnPlane(double x0, double y0, double x1, double y1,
            double originX, double originZ, double normalX, double normalY, double normalZ,
            double originY = 0)
        {
            if (Math.Abs(normalZ) < 1e-9) throw new ArgumentException("Surface is vertical, not a ceiling plane.");
            double Elevation(double x, double y) => originZ -
                (normalX * (x - originX) + normalY * (y - originY)) / normalZ;
            return new[] { Elevation(x0, y0), Elevation(x1, y1) };
        }

        public static bool Matches(double actual0, double actual1, double expected0, double expected1, double tolerance)
            => Finite(actual0) && Finite(actual1) && Finite(expected0) && Finite(expected1) &&
               Finite(tolerance) && tolerance >= 0 &&
               Math.Abs(actual0 - expected0) <= tolerance && Math.Abs(actual1 - expected1) <= tolerance;

        private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    }
}
