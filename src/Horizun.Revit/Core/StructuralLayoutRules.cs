// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// Structural layout arithmetic without Revit: where grid lines cross, which
// crossings already carry a column, and which consecutive crossings along one
// grid make a beam span. The planner feeds this file plain segments and
// existing positions; it takes back points, spans and named omissions - and
// like every planner in this repository, an ambiguity is a refusal, not a
// guess someone's structure inherits.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;

namespace Horizun.Revit.Core
{
    /// <summary>A grid line in plan: a straight segment with a name. Feet.</summary>
    public sealed class GridSegment
    {
        public string Name;
        public long ElementId;
        public double X1, Y1, X2, Y2;
    }

    public sealed class GridIntersection
    {
        public double X, Y;
        public string GridA, GridB;
        public long GridAId, GridBId;
        /// <summary>Parameter along each grid, for deterministic ordering.</summary>
        public double TA, TB;
    }

    public sealed class BeamSpan
    {
        public string Grid;
        public long GridId;
        public double X1, Y1, X2, Y2;
        public string FromCrossing, ToCrossing;
    }

    public static class StructuralLayoutRules
    {
        /// <summary>Two positions are the same column location within this. 5 mm.</summary>
        public const double SamePlaceToleranceFeet = 5.0 / 304.8;

        /// <summary>Grids meeting shallower than this are treated as parallel. Degrees.</summary>
        public const double MinCrossingAngleDegrees = 1.0;

        public const string CodeAlreadyPresent = "already_present";
        public const string CodeNoIntersections = "no_grid_intersections";
        public const string CodeSpanTooShort = "span_too_short";

        /// <summary>
        /// Every pairwise crossing of the segments, WITHIN both extents. Parallel and
        /// near-parallel pairs cross nowhere; a crossing outside a grid's drawn extent
        /// is not a place that grid names. Deterministic order: by grid pair, then
        /// along the first grid.
        /// </summary>
        public static List<GridIntersection> Intersections(IList<GridSegment> grids)
        {
            var result = new List<GridIntersection>();
            if (grids == null) return result;
            for (int i = 0; i < grids.Count; i++)
                for (int j = i + 1; j < grids.Count; j++)
                {
                    GridIntersection crossing = Cross(grids[i], grids[j]);
                    if (crossing != null) result.Add(crossing);
                }
            result.Sort((a, b) =>
            {
                int byGrid = string.CompareOrdinal(a.GridA, b.GridA);
                if (byGrid != 0) return byGrid;
                int byOther = string.CompareOrdinal(a.GridB, b.GridB);
                if (byOther != 0) return byOther;
                return a.TA.CompareTo(b.TA);
            });
            return result;
        }

        private static GridIntersection Cross(GridSegment g1, GridSegment g2)
        {
            double d1x = g1.X2 - g1.X1, d1y = g1.Y2 - g1.Y1;
            double d2x = g2.X2 - g2.X1, d2y = g2.Y2 - g2.Y1;
            double denom = d1x * d2y - d1y * d2x;
            double len1 = Math.Sqrt(d1x * d1x + d1y * d1y), len2 = Math.Sqrt(d2x * d2x + d2y * d2y);
            if (len1 < 1e-9 || len2 < 1e-9) return null;
            double sinAngle = Math.Abs(denom) / (len1 * len2);
            if (sinAngle < Math.Sin(MinCrossingAngleDegrees * Math.PI / 180.0)) return null;
            double t = ((g2.X1 - g1.X1) * d2y - (g2.Y1 - g1.Y1) * d2x) / denom;
            double u = ((g2.X1 - g1.X1) * d1y - (g2.Y1 - g1.Y1) * d1x) / denom;
            if (t < -1e-9 || t > 1 + 1e-9 || u < -1e-9 || u > 1 + 1e-9) return null;
            return new GridIntersection
            {
                X = g1.X1 + t * d1x, Y = g1.Y1 + t * d1y,
                GridA = g1.Name, GridB = g2.Name, GridAId = g1.ElementId, GridBId = g2.ElementId,
                TA = t, TB = u
            };
        }

        /// <summary>
        /// Split the crossings into the ones to place and the ones a column already
        /// occupies. "Occupied" is measured by distance to an existing column position,
        /// never by name - the model is the authority on where its columns are.
        /// </summary>
        public static void DedupColumns(IList<GridIntersection> crossings, IList<double[]> existingXY,
                                        out List<GridIntersection> toPlace, out List<GridIntersection> alreadyPresent)
        {
            toPlace = new List<GridIntersection>();
            alreadyPresent = new List<GridIntersection>();
            var claimed = new List<double[]>();
            foreach (GridIntersection crossing in crossings ?? new List<GridIntersection>())
            {
                bool occupied = false;
                if (existingXY != null)
                    foreach (double[] existing in existingXY)
                        if (Distance(crossing.X, crossing.Y, existing[0], existing[1]) <= SamePlaceToleranceFeet)
                        { occupied = true; break; }
                // Two grids crossing a third at nearly one point: the SECOND crossing
                // of the same spot is a duplicate of the first, not a second column.
                if (!occupied)
                    foreach (double[] mine in claimed)
                        if (Distance(crossing.X, crossing.Y, mine[0], mine[1]) <= SamePlaceToleranceFeet)
                        { occupied = true; break; }
                if (occupied) alreadyPresent.Add(crossing);
                else { toPlace.Add(crossing); claimed.Add(new[] { crossing.X, crossing.Y }); }
            }
        }

        /// <summary>
        /// Consecutive crossings ALONG one grid make beam spans. A span shorter than
        /// `minSpanFeet` is omitted by name - a 3 cm beam is a modelling accident.
        /// Existing beam midpoints suppress their span the same way columns do.
        /// </summary>
        public static void BeamSpans(IList<GridIntersection> crossings, string gridName, long gridId,
                                     IList<double[]> existingMidpointsXY, double minSpanFeet,
                                     out List<BeamSpan> spans, out int suppressedExisting, out int suppressedShort)
        {
            spans = new List<BeamSpan>();
            suppressedExisting = 0; suppressedShort = 0;
            var along = new List<GridIntersection>();
            foreach (GridIntersection crossing in crossings ?? new List<GridIntersection>())
            {
                if (crossing.GridAId == gridId || crossing.GridBId == gridId) along.Add(crossing);
            }
            along.Sort((a, b) => Param(a, gridId).CompareTo(Param(b, gridId)));
            for (int i = 0; i + 1 < along.Count; i++)
            {
                GridIntersection from = along[i], to = along[i + 1];
                double length = Distance(from.X, from.Y, to.X, to.Y);
                if (length <= SamePlaceToleranceFeet) continue;           // the same crossing twice
                if (length < minSpanFeet) { suppressedShort++; continue; }
                double midX = (from.X + to.X) / 2, midY = (from.Y + to.Y) / 2;
                bool exists = false;
                if (existingMidpointsXY != null)
                    foreach (double[] existing in existingMidpointsXY)
                        if (Distance(midX, midY, existing[0], existing[1]) <= SamePlaceToleranceFeet)
                        { exists = true; break; }
                if (exists) { suppressedExisting++; continue; }
                spans.Add(new BeamSpan
                {
                    Grid = gridName, GridId = gridId,
                    X1 = from.X, Y1 = from.Y, X2 = to.X, Y2 = to.Y,
                    FromCrossing = OtherGrid(from, gridId), ToCrossing = OtherGrid(to, gridId)
                });
            }
        }

        private static double Param(GridIntersection crossing, long gridId) =>
            crossing.GridAId == gridId ? crossing.TA : crossing.TB;

        private static string OtherGrid(GridIntersection crossing, long gridId) =>
            crossing.GridAId == gridId ? crossing.GridB : crossing.GridA;

        private static double Distance(double x1, double y1, double x2, double y2)
        {
            double dx = x1 - x2, dy = y1 - y2;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        public static string Describe(GridIntersection crossing) =>
            (crossing.GridA ?? "?") + " x " + (crossing.GridB ?? "?") + " at (" +
            (crossing.X * 304.8).ToString("0.0", CultureInfo.InvariantCulture) + ", " +
            (crossing.Y * 304.8).ToString("0.0", CultureInfo.InvariantCulture) + ") mm";
    }
}
