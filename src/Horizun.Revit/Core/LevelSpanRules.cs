using System;
using System.Collections.Generic;
using System.Linq;

namespace Horizun.Revit.Core
{
    /// <summary>Pure geometry of the one-storey 4D rule; all elevations are millimetres.</summary>
    public static class LevelSpanRules
    {
        public const double DefaultToleranceMm = 50.0;

        /// <summary>Whether a physical solid stays between its requested level and the next level.</summary>
        public static bool WithinStorey(double bottomMm, double topMm, double baseLevelMm,
            double? nextLevelMm, double toleranceMm = DefaultToleranceMm)
        {
            if (double.IsNaN(bottomMm) || double.IsNaN(topMm) || double.IsNaN(baseLevelMm) ||
                double.IsInfinity(bottomMm) || double.IsInfinity(topMm) || double.IsInfinity(baseLevelMm) ||
                bottomMm > topMm || toleranceMm < 0 || double.IsNaN(toleranceMm) || double.IsInfinity(toleranceMm) ||
                (nextLevelMm.HasValue && (double.IsNaN(nextLevelMm.Value) || double.IsInfinity(nextLevelMm.Value) || nextLevelMm.Value <= baseLevelMm)))
                return false;
            return bottomMm >= baseLevelMm - toleranceMm &&
                   (!nextLevelMm.HasValue || topMm <= nextLevelMm.Value + toleranceMm);
        }

        public static IReadOnlyList<double> Crossed(double bottomMm, double topMm,
            IEnumerable<double> levelElevationsMm, double toleranceMm = DefaultToleranceMm)
        {
            if (levelElevationsMm == null) throw new ArgumentNullException(nameof(levelElevationsMm));
            if (double.IsNaN(bottomMm) || double.IsNaN(topMm) || double.IsInfinity(bottomMm) ||
                double.IsInfinity(topMm) || bottomMm > topMm || toleranceMm < 0 ||
                double.IsNaN(toleranceMm) || double.IsInfinity(toleranceMm))
                throw new ArgumentException("Invalid vertical interval or tolerance.");
            return levelElevationsMm.Where(z => z > bottomMm + toleranceMm && z < topMm - toleranceMm)
                .Distinct().OrderBy(z => z).ToList();
        }
    }
}
