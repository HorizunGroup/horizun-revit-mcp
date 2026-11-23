using System;
using Autodesk.Revit.DB;

namespace Horizun.Revit.Core
{
    internal static class LevelSpanBounds
    {
        internal static double[] Read(BoundingBoxXYZ box)
        {
            if (box == null) return null;
            double low = double.PositiveInfinity, high = double.NegativeInfinity;
            foreach (double x in new[] { box.Min.X, box.Max.X })
                foreach (double y in new[] { box.Min.Y, box.Max.Y })
                    foreach (double z in new[] { box.Min.Z, box.Max.Z })
                    {
                        double world = box.Transform.OfPoint(new XYZ(x, y, z)).Z;
                        low = Math.Min(low, world); high = Math.Max(high, world);
                    }
            return new[] { low, high };
        }
    }
}
