// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// A GRID THAT CANNOT BE SEEN ON THE UPPER FLOORS (field cycle, 2026-10-21).
//
// MEASURED: a grid made by horizun_create_elements kind=grid only appeared in the
// plan views whose cut plane fell inside the grid's own vertical (3D) extent: the
// first floor and the foundation, never floors 2 to roof. Dimensions on those grids
// in the upper plans were created with unavailable references (72 blocking findings).
// Revit has the setting - DatumPlane.Maximize3DExtents, "Maximize 3D Extents" in the
// UI - but no typed command offered it.
//
// This file is the Revit-free half: which extent mode a request means, what it
// means for a grid's vertical extent to "cover" the levels of the model, and the
// honest report of what was read back. A Z on a grid's start/end is NOT an
// elevation (a grid is a vertical datum plane), so it is flattened and said so.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public static class GridExtentRules
    {
        public const string Maximize = "maximize";
        public const string None = "none";

        /// <summary>Default for a new grid: its 3D extent spans every level, so it shows in every plan.</summary>
        public static string Parse(string raw, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(raw)) return Maximize;
            string v = raw.Trim().ToLowerInvariant();
            if (v == Maximize || v == None) return v;
            error = "extent_3d must be 'maximize' (the grid's vertical extent spans every level of the model, the default) " +
                    "or 'none' (Revit's own default extent); got '" + raw + "'.";
            return null;
        }

        /// <summary>
        /// Does the vertical extent [minZ, maxZ] reach every level? A level is covered when
        /// minZ - tolerance &lt;= elevation &lt;= maxZ + tolerance. All values in the same
        /// internal unit (feet). `uncovered` lists the elevations that are not.
        /// </summary>
        public static bool CoversLevels(double minZ, double maxZ, IEnumerable<double> levelElevations, double tolerance, out List<double> uncovered)
        {
            uncovered = new List<double>();
            if (double.IsNaN(minZ) || double.IsNaN(maxZ) || minZ > maxZ) return false;
            if (levelElevations != null)
                foreach (double z in levelElevations)
                    if (z < minZ - tolerance || z > maxZ + tolerance) uncovered.Add(z);
            return uncovered.Count == 0;
        }

        /// <summary>Margin above the highest level and below the lowest, in feet (3.048 m).</summary>
        public const double VerticalMarginFeet = 10.0;

        /// <summary>
        /// The vertical extent a grid needs to show on every level: from the lowest level minus the
        /// margin to the highest plus the margin. MEASURED (Revit 2023, 2026-10-23):
        /// Grid.Maximize3DExtents also stretches the grid ALONG its line - a grid asked from y=-3.94 ft
        /// to y=40.50 ft came back -50 to 50 - so the create re-read refused it and extend_grids_3d
        /// moved the bubbles. SetVerticalExtents(bottom, top) changes the vertical reach alone.
        /// False when there is no level to span (nothing to cover).
        /// </summary>
        public static bool VerticalRange(IEnumerable<double> levelElevations, out double bottom, out double top)
        {
            bottom = top = double.NaN;
            var z = (levelElevations ?? Enumerable.Empty<double>()).Where(v => !double.IsNaN(v) && !double.IsInfinity(v)).ToList();
            if (z.Count == 0) return false;
            bottom = z.Min() - VerticalMarginFeet;
            top = z.Max() + VerticalMarginFeet;
            return true;
        }

        /// <summary>A grid is a vertical plane: any Z on its endpoints is dropped, and the drop is reported.</summary>
        public static bool NeedsFlatten(double startZ, double endZ) => Math.Abs(startZ) > 1e-9 || Math.Abs(endZ) > 1e-9;

        public static string FlattenNote(double startZ, double endZ, string units) =>
            "start/end Z (" + startZ.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + ", " +
            endZ.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + " " + units +
            ") was ignored: a grid is a vertical datum plane and has no elevation. Its vertical reach is extent_3d.";

        /// <summary>The block the row carries: what the extent is, read back, against the levels.</summary>
        public static JObject Report(string mode, double minZ, double maxZ, int levelCount, IList<double> uncovered)
        {
            return new JObject
            {
                ["extent_3d"] = mode,
                ["min_z_feet"] = minZ,
                ["max_z_feet"] = maxZ,
                ["levels_checked"] = levelCount,
                ["levels_not_covered_feet"] = new JArray((uncovered ?? new List<double>()).Cast<object>().ToArray())
            };
        }
    }
}
