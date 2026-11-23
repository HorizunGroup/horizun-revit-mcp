// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// A GRID HAS A LINE AND A LEVEL HAS AN ELEVATION, EVEN WHEN THEY HAVE NO BOX.
//
// MEASURED (Comité de obra, 2026-10-01): horizun_query_model include_bounding_box
// answered bounding_box: null for every grid and level of three linked models.
// In those links get_BoundingBox(null) was null, so the model query had no grid
// geometry to compare. A datum can have view-specific extents.
//
// What a datum does have is what it IS: a grid's curve and a level's elevation, both
// read in host coordinates through the link transform. A row now carries them in a
// `datum` block, and a grid's bounding_box is the box of its curve (labelled as such).
// Spatial filters use the grid's model box when it exists. A level is a plane and
// gets its elevation. A query without a readable extent reports the gap.
//
// Revit-free: the Revit half reads the curve points and the elevation; this shapes them.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public static class DatumGeometryRules
    {
        public const string SourceElement = "element";
        public const string SourceGridCurve = "grid_curve";

        // The query passes the same document to the category collector and the class
        // collector. Keep view-scoped and explicit-ID reads narrowed to their results.
        public static IEnumerable<TElement> DocumentGridCandidates<TDocument, TElement>(TDocument document,
            IEnumerable<TElement> candidates, bool documentScope, bool requestsGrids, bool hasExplicitIds,
            Func<TDocument, IEnumerable<TElement>> collectGrids, Func<TElement, long> id) =>
            documentScope && requestsGrids && !hasExplicitIds
                ? StreamingDistinct.ByKey(candidates.Concat(collectGrids(document)), id)
                : candidates;

        public sealed class GridReading
        {
            public double[][] SpatialBounds { get; internal set; }
            public string Error { get; internal set; }
            internal double[][] RowBounds;
            internal string RowBoundsSource;
            internal JObject Datum;

            public bool Intersects(double[] min, double[] max) =>
                SpatialBounds != null && Enumerable.Range(0, 3)
                    .All(i => SpatialBounds[0][i] <= max[i] && SpatialBounds[1][i] >= min[i]);

            public void WriteTo(JObject row, double scale)
            {
                row["bounding_box"] = RowBounds == null ? (JToken)JValue.CreateNull()
                    : new JObject { ["min"] = P(RowBounds[0], scale), ["max"] = P(RowBounds[1], scale) };
                row["datum"] = Datum;
                if (RowBoundsSource != null) row["bounding_box_source"] = RowBoundsSource;
            }
        }

        public static GridReading ReadGrid(Func<bool> isCurved, Func<bool, IEnumerable<double[]>> readCurve,
            Func<double[], double[]> toHost, Func<double[][]> readModelBounds, Func<double[][]> readGridExtents,
            bool spatialFilter, double scale, string units)
        {
            var result = new GridReading();
            bool curved = false;
            List<double[]> points = null;
            try
            {
                curved = isCurved();
                points = readCurve(curved).Select(toHost).ToList();
                result.RowBounds = points.Count >= 2 ? BoxOf(points) : null;
                if (result.RowBounds == null)
                    throw new InvalidOperationException("grid curve returned fewer than two usable points or invalid coordinates");
            }
            catch (Exception ex) { points = null; result.Error = ex.Message; }

            // Keep the model box for the intersection test when Revit provides one.
            // The output row describes the axis, or the available extents if it could not be read.
            double[][] modelBounds = null;
            if (spatialFilter || result.RowBounds == null)
            {
                try { modelBounds = readModelBounds(); }
                catch (Exception ex) { result.Error = JoinGridErrors(result.Error, "model bounds: " + ex.Message); }
            }
            if (result.RowBounds != null) result.RowBoundsSource = SourceGridCurve;
            else
            {
                result.RowBounds = modelBounds;
                try
                {
                    // Revit does not give model extents via Grid.GetExtents for arc grids.
                    double[][] local = modelBounds == null && !curved ? readGridExtents() : null;
                    if (local != null)
                        result.RowBounds = BoxOf(from x in new[] { local[0][0], local[1][0] }
                                                 from y in new[] { local[0][1], local[1][1] }
                                                 from z in new[] { local[0][2], local[1][2] }
                                                 select toHost(new[] { x, y, z }));
                }
                catch (Exception ex) { result.Error = JoinGridErrors(result.Error, "grid extents: " + ex.Message); }
                if (result.RowBounds != null) result.RowBoundsSource = SourceElement;
            }
            result.SpatialBounds = modelBounds ?? result.RowBounds;
            result.Datum = points != null ? Grid(points[0], points[points.Count - 1], curved, points, scale, units)
                : new JObject { ["kind"] = "grid", ["read_error"] = result.Error };
            return result;
        }

        private static string JoinGridErrors(string previous, string next) =>
            previous == null ? next : previous + "; " + next;

        /// <summary>The axis-aligned box of a set of points (feet), or null for none.</summary>
        public static double[][] BoxOf(IEnumerable<double[]> points)
        {
            var list = points?.ToList();
            if (list == null || list.Count == 0) return null;
            if (list.Any(p => p == null || p.Length != 3 || p.Any(v => double.IsNaN(v) || double.IsInfinity(v))))
                return null;
            return new[]
            {
                new[] { list.Min(p => p[0]), list.Min(p => p[1]), list.Min(p => p[2]) },
                new[] { list.Max(p => p[0]), list.Max(p => p[1]), list.Max(p => p[2]) }
            };
        }

        private static JArray P(double[] p, double scale) =>
            new JArray(p[0] * scale, p[1] * scale, p[2] * scale);

        /// <summary>A grid's line in host coordinates. `points` are the curve's ends (and, when curved, its tessellation).</summary>
        public static JObject Grid(double[] start, double[] end, bool curved, IList<double[]> points, double scale, string units)
        {
            var o = new JObject
            {
                ["kind"] = "grid",
                ["start"] = P(start, scale),
                ["end"] = P(end, scale),
                ["is_curved"] = curved,
                ["units"] = units
            };
            double dx = end[0] - start[0], dy = end[1] - start[1];
            if (!curved && (Math.Abs(dx) > 1e-9 || Math.Abs(dy) > 1e-9))
            {
                // Plan direction, 0..180: a grid drawn the other way is the same grid.
                double deg = Math.Atan2(dy, dx) * 180.0 / Math.PI;
                if (deg < 0) deg += 180.0;
                if (deg >= 180.0 - 1e-9) deg -= 180.0;
                deg = Math.Round(deg, 4);
                // MEASURED live: a grid drawn along +X read "-0.0" (Atan2 of a -0 dy).
                if (deg == 0) deg = 0.0;
                o["plan_angle_degrees"] = deg;
                o["length"] = Math.Sqrt(dx * dx + dy * dy) * scale;
            }
            if (curved && points != null && points.Count > 2) o["points"] = new JArray(points.Select(p => (JToken)P(p, scale)));
            return o;
        }

        /// <summary>A level's elevation in host coordinates (the transform applied to its Z).</summary>
        public static JObject Level(double elevationHostFeet, double? elevationOwnFeet, double scale, string units)
        {
            var o = new JObject
            {
                ["kind"] = "level",
                ["elevation"] = elevationHostFeet * scale,
                ["units"] = units,
                ["basis"] = "host coordinates: the level's own elevation through the link transform",
                ["bounding_box_note"] = "a level is a plane with no model extent; it has an elevation and no box"
            };
            if (elevationOwnFeet.HasValue) o["elevation_in_own_model"] = elevationOwnFeet.Value * scale;
            return o;
        }
    }
}
