// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// LEVELS AND GRIDS, JUDGED. horizun_audit_model contains zero references to
// Level, Grid or elevation, and no tool in the eight-product benchmark performs
// a duplicate-or-coincident datum check either. This is the cleanest absence in
// the diagnostics programme sitting next to a half that already ships: names and
// elevations are read today by horizun_model_scan and horizun_query_structure,
// and nothing looks at them.
//
// THE INTERESTING CASE IS NOT THE DUPLICATE, IT IS THE NEAR-MISS. Two levels
// named "L02" and "Level 02" one millimetre apart are not a naming problem and
// not an elevation problem; they are one level somebody made twice, and every
// element on the second one is invisible to every schedule filtered on the
// first. Revit will not stop you, because neither name nor elevation collides.
//
// Revit-free by construction: the reading is in the command, the judgement here.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;

namespace Horizun.Revit.Core
{
    public sealed class LevelFact
    {
        public long ElementId;
        public string Name;
        public bool NameReadable;
        public double? ElevationMm;
        /// <summary>Elevation in the PROJECT's own datum, which is not the same number.</summary>
        public double? ProjectElevationMm;
        public bool? IsBuildingStory;
        /// <summary>How many views are associated with this level. Null when it could not be asked.</summary>
        public int? ViewCount;
        /// <summary>How many elements report this as their level. Null when not measured.</summary>
        public long? ElementCount;
    }

    public sealed class GridFact
    {
        public long ElementId;
        public string Name;
        public bool NameReadable;
        public bool GeometryReadable;
        public string Why;
        /// <summary>A straight grid's endpoints in millimetres. Null for a curved one.</summary>
        public double? X1Mm, Y1Mm, X2Mm, Y2Mm;
        public bool IsCurved;

        public double? LengthMm
        {
            get
            {
                if (!X1Mm.HasValue || !Y1Mm.HasValue || !X2Mm.HasValue || !Y2Mm.HasValue) return null;
                double dx = X2Mm.Value - X1Mm.Value, dy = Y2Mm.Value - Y1Mm.Value;
                return Math.Sqrt(dx * dx + dy * dy);
            }
        }

        /// <summary>Angle to the X axis in degrees, folded into [0,180).</summary>
        public double? AngleDegrees
        {
            get
            {
                if (!X1Mm.HasValue || !Y1Mm.HasValue || !X2Mm.HasValue || !Y2Mm.HasValue) return null;
                double a = Math.Atan2(Y2Mm.Value - Y1Mm.Value, X2Mm.Value - X1Mm.Value) * 180.0 / Math.PI;
                while (a < 0) a += 180.0;
                while (a >= 180.0) a -= 180.0;
                return a;
            }
        }
    }

    /// <summary>A pair of datums that are not the same and probably should be.</summary>
    public sealed class DatumCollision
    {
        public string Code;
        public long FirstId, SecondId;
        public string FirstName, SecondName;
        public double? SeparationMm;
        public string Why;
    }

    public static class DatumCheckParts
    {
        public const string DuplicateLevelNames = "duplicate_level_names";
        public const string CoincidentLevels = "coincident_levels";
        public const string LevelsWithoutViews = "levels_without_views";
        public const string LevelsWithoutElements = "levels_without_elements";
        public const string DuplicateGridNames = "duplicate_grid_names";
        public const string CoincidentGrids = "coincident_grids";
        public const string GridsOffAxis = "grids_off_axis";
        public const string GridsUnreadable = "grids_unreadable";
    }

    public static class DatumRules
    {
        /// <summary>
        /// Defaults, all overridable. A tolerance is a decision about the project,
        /// not about the tool, so none of these is compiled into a verdict.
        /// </summary>
        public const double DefaultLevelCoincidenceMm = 1.0;
        public const double DefaultGridCoincidenceMm = 1.0;
        public const double DefaultGridAxisToleranceDegrees = 0.5;

        public const string ToleranceLevelCoincidence = "level_coincidence_mm";
        public const string ToleranceGridCoincidence = "grid_coincidence_mm";
        public const string ToleranceGridAxis = "grid_axis_tolerance_degrees";

        public const string CodeDuplicateName = "duplicate_name";
        public const string CodeCoincident = "coincident";
        public const string CodeNearCoincident = "near_coincident";

        /// <summary>
        /// Levels sharing a name. Revit permits this across a document more readily
        /// than people expect, and two levels with one name make every schedule
        /// filtered on that name ambiguous.
        /// </summary>
        public static List<DatumCollision> DuplicateLevelNames(IEnumerable<LevelFact> levels)
        {
            return DuplicateNames(NamesOf(levels), "level");
        }

        public static List<DatumCollision> DuplicateGridNames(IEnumerable<GridFact> grids)
        {
            var named = new List<KeyValuePair<long, string>>();
            if (grids != null)
                foreach (GridFact g in grids)
                    if (g != null && g.NameReadable && !string.IsNullOrEmpty(g.Name))
                        named.Add(new KeyValuePair<long, string>(g.ElementId, g.Name));
            return DuplicateNames(named, "grid");
        }

        private static List<KeyValuePair<long, string>> NamesOf(IEnumerable<LevelFact> levels)
        {
            var named = new List<KeyValuePair<long, string>>();
            if (levels != null)
                foreach (LevelFact l in levels)
                    if (l != null && l.NameReadable && !string.IsNullOrEmpty(l.Name))
                        named.Add(new KeyValuePair<long, string>(l.ElementId, l.Name));
            return named;
        }

        private static List<DatumCollision> DuplicateNames(List<KeyValuePair<long, string>> named, string what)
        {
            var found = new List<DatumCollision>();
            // ORDINAL, NOT CASE-INSENSITIVE. "L1" and "l1" are two different names to
            // Revit, and reporting them as a duplicate would be this tool inventing a
            // convention it was not given.
            var seen = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (KeyValuePair<long, string> kv in named)
            {
                long first;
                if (seen.TryGetValue(kv.Value, out first))
                {
                    found.Add(new DatumCollision
                    {
                        Code = CodeDuplicateName,
                        FirstId = first, SecondId = kv.Key,
                        FirstName = kv.Value, SecondName = kv.Value,
                        Why = "two " + what + "s share the name '" + kv.Value + "', so any filter or schedule " +
                              "naming it is ambiguous."
                    });
                }
                else seen[kv.Value] = kv.Key;
            }
            return found;
        }

        /// <summary>
        /// Levels at the same elevation, or near enough that they are one level made
        /// twice. Exactly coincident and near-coincident are reported with different
        /// codes because they usually have different causes - a copy-paste versus a
        /// unit rounding or a hand-typed value.
        /// </summary>
        public static List<DatumCollision> CoincidentLevels(IEnumerable<LevelFact> levels, double toleranceMm)
        {
            var found = new List<DatumCollision>();
            var withElevation = new List<LevelFact>();
            if (levels != null)
                foreach (LevelFact l in levels)
                    if (l != null && l.ElevationMm.HasValue && IsFinite(l.ElevationMm.Value)) withElevation.Add(l);

            withElevation.Sort((a, b) => a.ElevationMm.Value.CompareTo(b.ElevationMm.Value));
            for (int i = 1; i < withElevation.Count; i++)
            {
                LevelFact a = withElevation[i - 1], b = withElevation[i];
                double gap = Math.Abs(b.ElevationMm.Value - a.ElevationMm.Value);
                if (gap > toleranceMm) continue;
                bool exact = gap == 0;
                found.Add(new DatumCollision
                {
                    Code = exact ? CodeCoincident : CodeNearCoincident,
                    FirstId = a.ElementId, SecondId = b.ElementId,
                    FirstName = a.Name, SecondName = b.Name,
                    SeparationMm = gap,
                    Why = exact
                        ? ("'" + a.Name + "' and '" + b.Name + "' are at the same elevation. Two levels in one " +
                           "place are one level made twice, and elements on the second are invisible to every " +
                           "view and schedule filtered on the first.")
                        : ("'" + a.Name + "' and '" + b.Name + "' are " + CoordinateRules.Fmt(gap) +
                           " mm apart, within the declared coincidence tolerance of " +
                           CoordinateRules.Fmt(toleranceMm) + " mm. Neither their names nor their elevations " +
                           "collide, so Revit will never mention it.")
                });
            }
            return found;
        }

        /// <summary>
        /// Grids that lie on top of one another. Two straight grids are coincident
        /// when they are parallel within the angle tolerance AND the perpendicular
        /// distance between their lines is inside the distance tolerance. A curved
        /// grid is not compared - it is reported as not evaluated, never as clear.
        /// </summary>
        public static List<DatumCollision> CoincidentGrids(IEnumerable<GridFact> grids, double distanceToleranceMm,
                                                           double angleToleranceDegrees)
        {
            var found = new List<DatumCollision>();
            var straight = new List<GridFact>();
            if (grids != null)
                foreach (GridFact g in grids)
                    if (g != null && g.GeometryReadable && !g.IsCurved && g.AngleDegrees.HasValue) straight.Add(g);

            for (int i = 0; i < straight.Count; i++)
                for (int j = i + 1; j < straight.Count; j++)
                {
                    GridFact a = straight[i], b = straight[j];
                    double da = AngleGap(a.AngleDegrees.Value, b.AngleDegrees.Value);
                    if (da > angleToleranceDegrees) continue;

                    double d = PerpendicularDistanceMm(a, b);
                    if (double.IsNaN(d) || d > distanceToleranceMm) continue;

                    found.Add(new DatumCollision
                    {
                        Code = d == 0 ? CodeCoincident : CodeNearCoincident,
                        FirstId = a.ElementId, SecondId = b.ElementId,
                        FirstName = a.Name, SecondName = b.Name,
                        SeparationMm = d,
                        Why = "'" + a.Name + "' and '" + b.Name + "' are parallel within " +
                              CoordinateRules.Fmt(angleToleranceDegrees) + " degrees and " +
                              CoordinateRules.Fmt(d) + " mm apart. Two grids on one line is one grid made twice."
                    });
                }
            return found;
        }

        /// <summary>
        /// Grids that are neither on the X axis nor the Y axis nor on the building's
        /// dominant angle. The dominant angle is MEASURED from the grids themselves
        /// rather than assumed to be zero: a building rotated 30 degrees has every
        /// grid off the world axes and nothing wrong with it, and a tool that
        /// reported all of them would be reporting the site plan.
        /// </summary>
        public static List<GridFact> GridsOffAxis(IEnumerable<GridFact> grids, double angleToleranceDegrees,
                                                  out double? dominantDegrees)
        {
            int ignored, alsoIgnored;
            return GridsOffAxis(grids, angleToleranceDegrees, out dominantDegrees, out ignored, out alsoIgnored);
        }

        /// <summary>
        /// The same, and it also says HOW MANY grids agreed with the dominant angle
        /// and how many distinct angle families there are.
        ///
        /// MEASURED ON A REAL MODEL: a document that already carried orthogonal grids
        /// reported `off_axis: 8` when a rotated building was added to it, and the
        /// number was correct and useless. There were TWO grid families and the rule
        /// reported the minority - which is defensible, and impossible to understand
        /// from one integer. A reader needs to know that the building has two grid
        /// systems before they can decide which one is wrong.
        /// </summary>
        public static List<GridFact> GridsOffAxis(IEnumerable<GridFact> grids, double angleToleranceDegrees,
                                                  out double? dominantDegrees, out int onDominantAxis,
                                                  out int angleFamilies)
        {
            dominantDegrees = null;
            onDominantAxis = 0;
            angleFamilies = 0;
            var straight = new List<GridFact>();
            if (grids != null)
                foreach (GridFact g in grids)
                    if (g != null && g.GeometryReadable && !g.IsCurved && g.AngleDegrees.HasValue) straight.Add(g);
            if (straight.Count == 0) return new List<GridFact>();

            // The dominant angle is the one the most grids agree on, counting a grid
            // and its perpendicular as agreeing - an orthogonal grid set has two
            // families and they are one decision.
            double best = 0; int bestVotes = -1;
            foreach (GridFact candidate in straight)
            {
                int votes = 0;
                foreach (GridFact other in straight)
                    if (OnAxisOf(other.AngleDegrees.Value, candidate.AngleDegrees.Value, angleToleranceDegrees)) votes++;
                if (votes > bestVotes) { bestVotes = votes; best = candidate.AngleDegrees.Value; }
            }
            dominantDegrees = best;
            onDominantAxis = bestVotes;

            // HOW MANY DISTINCT FAMILIES. Each is a grid direction (and its
            // perpendicular) that no earlier family already covered. More than one is
            // the fact that makes an off-axis count readable.
            var families = new List<double>();
            foreach (GridFact g in straight)
            {
                bool covered = false;
                foreach (double f in families)
                    if (OnAxisOf(g.AngleDegrees.Value, f, angleToleranceDegrees)) { covered = true; break; }
                if (!covered) families.Add(g.AngleDegrees.Value);
            }
            angleFamilies = families.Count;

            var off = new List<GridFact>();
            foreach (GridFact g in straight)
                if (!OnAxisOf(g.AngleDegrees.Value, best, angleToleranceDegrees)) off.Add(g);
            return off;
        }

        private static bool OnAxisOf(double angle, double axis, double toleranceDegrees)
        {
            return AngleGap(angle, axis) <= toleranceDegrees ||
                   AngleGap(angle, axis + 90.0) <= toleranceDegrees;
        }

        /// <summary>Smallest gap between two undirected angles, in degrees.</summary>
        internal static double AngleGap(double a, double b)
        {
            double d = Math.Abs(a - b) % 180.0;
            return d > 90.0 ? 180.0 - d : d;
        }

        /// <summary>
        /// Distance from grid b's first point to the infinite line through grid a.
        /// NaN when either has no usable geometry - never zero, which would read as
        /// "they are on top of each other".
        /// </summary>
        internal static double PerpendicularDistanceMm(GridFact a, GridFact b)
        {
            if (!a.X1Mm.HasValue || !a.Y1Mm.HasValue || !a.X2Mm.HasValue || !a.Y2Mm.HasValue) return double.NaN;
            if (!b.X1Mm.HasValue || !b.Y1Mm.HasValue) return double.NaN;
            double dx = a.X2Mm.Value - a.X1Mm.Value, dy = a.Y2Mm.Value - a.Y1Mm.Value;
            double len = Math.Sqrt(dx * dx + dy * dy);
            if (len <= 0) return double.NaN;
            double cross = Math.Abs(dx * (b.Y1Mm.Value - a.Y1Mm.Value) - dy * (b.X1Mm.Value - a.X1Mm.Value));
            return cross / len;
        }

        /// <summary>Levels nothing draws. A level with no view is usually a datum somebody left behind.</summary>
        public static List<LevelFact> LevelsWithoutViews(IEnumerable<LevelFact> levels, out long notMeasured)
        {
            notMeasured = 0;
            var bare = new List<LevelFact>();
            if (levels == null) return bare;
            foreach (LevelFact l in levels)
            {
                if (l == null) continue;
                if (!l.ViewCount.HasValue) { notMeasured++; continue; }
                if (l.ViewCount.Value == 0) bare.Add(l);
            }
            return bare;
        }

        /// <summary>Levels nothing is built on. Reported separately from levels nothing draws.</summary>
        public static List<LevelFact> LevelsWithoutElements(IEnumerable<LevelFact> levels, out long notMeasured)
        {
            notMeasured = 0;
            var bare = new List<LevelFact>();
            if (levels == null) return bare;
            foreach (LevelFact l in levels)
            {
                if (l == null) continue;
                if (!l.ElementCount.HasValue) { notMeasured++; continue; }
                if (l.ElementCount.Value == 0) bare.Add(l);
            }
            return bare;
        }

        private static bool IsFinite(double v) { return !double.IsNaN(v) && !double.IsInfinity(v); }
    }
}
