using System;
using System.Collections.Generic;

namespace Horizun.Revit.Core
{
    /// <summary>
    /// The order in which a column's base and top are set, so that no step in between leaves it
    /// with zero or negative height (acero 3.0, L8). MEASURED on Revit 2024: an error posted by an
    /// intermediate regeneration ("Column height is not 0.0") survives to the commit even when the
    /// next step makes the column valid, and the whole transaction rolls back.
    /// </summary>
    public static class ColumnConstraintOrder
    {
        public static int FamilyDatumIndex(IReadOnlyList<double> elevations, double originZ, double tolerance)
        {
            if (elevations == null || !Finite(originZ) || !Finite(tolerance) || tolerance < 0)
                throw new ArgumentException("column family datum inputs must be finite");
            int found = -1;
            for (int i = 0; i < elevations.Count; i++)
            {
                if (!Finite(elevations[i])) throw new ArgumentException("unreadable column family datum");
                if (Math.Abs(elevations[i] - originZ) > tolerance) continue;
                if (found >= 0) throw new ArgumentException("column profile matches more than one family level; datum is ambiguous");
                found = i;
            }
            return found;
        }

        public static string SolidSpanProblem(double wantedBase, double? wantedTop, double[] measured, double tolerance)
        {
            if (!Finite(wantedBase) || wantedTop.HasValue && !Finite(wantedTop.Value) || !Finite(tolerance) || tolerance < 0)
                throw new ArgumentException("column span inputs must be finite");
            if (measured == null || measured.Length != 2 || !Finite(measured[0]) || !Finite(measured[1]) || measured[1] <= measured[0]) return "column_solid_unmeasured";
            if (Math.Abs(measured[0] - wantedBase) > tolerance) return "column_solid_base_mismatch";
            if (wantedTop.HasValue && Math.Abs(measured[1] - wantedTop.Value) > tolerance) return "column_solid_top_mismatch";
            return null;
        }

        private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

        /// <summary>
        /// True when the top is to be set first: the wanted top lies above the CURRENT base, so the
        /// column stays positive while its base then moves. Otherwise the wanted base lies below the
        /// current top (a valid wanted column has base below top, and its top is at or below the
        /// current base), and the base goes first. Elevations in any one unit.
        /// </summary>
        public static bool TopFirst(double currentBase, double currentTop, double wantBase, double wantTop)
        {
            if (wantTop > currentBase) return true;
            return !(wantBase < currentTop);
        }
    }
}
