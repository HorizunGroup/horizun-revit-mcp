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
