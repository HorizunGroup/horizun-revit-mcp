// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// THE REVIT-FREE HALF of horizun_room_finishes: what each operation takes, which
// floor type a room's use asks for, the area a room's finish boundary encloses,
// and when a measured floor area agrees with it.
//
// field cycle ARQ (2026-10-24, A4/A5): one tile floor per storey ran THROUGH the
// walls and counted false quantities, and baseboards were one sweep per wall,
// placed by hand. The link room -> finish floor (contour from the room's finish
// faces, type by the room's use) and room -> baseboard (the wall faces that bound
// the room, on the room's side) did not exist.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public static class RoomFinishPlanRules
    {
        public const int MaxRooms = 200;
        /// <summary>Relative tolerance between the floor's computed area and the area its loops enclose.</summary>
        public const double AreaTolerance = 0.005;
        public const double BoundaryGapToleranceMm = 1;

        /// <summary>Positive overlap, not merely touching at another storey's datum.</summary>
        public static bool RangesOverlap(double minA, double maxA, double minB, double maxB, double tolerance = 1e-6) =>
            !double.IsNaN(minA) && !double.IsNaN(maxA) && !double.IsNaN(minB) && !double.IsNaN(maxB) &&
            Math.Min(maxA, maxB) - Math.Max(minA, minB) > tolerance;

        /// <summary>
        /// Where a nearby wall stands relative to a created finish floor, from the two vertical extents.
        /// A wall that ends below the floor, starts above it, or ends inside the floor's own thickness
        /// (the wall of the storey below that the floor rests on) cannot carry the floor through it, so
        /// those three need no solid measurement. Only a wall that rises above the floor's top surface
        /// is measured. Null: the wall crosses the floor's top and must be measured.
        /// </summary>
        public static string WallVerticalRelation(double floorBottom, double floorTop, double wallBottom, double wallTop, double tolerance)
        {
            if (new[] { floorBottom, floorTop, wallBottom, wallTop, tolerance }.Any(v => double.IsNaN(v) || double.IsInfinity(v))) return null;
            if (wallTop <= floorBottom + tolerance) return "wall_below_floor";
            if (wallBottom >= floorTop - tolerance) return "wall_above_floor";
            if (wallTop <= floorTop + tolerance) return "floor_rests_on_wall";
            return null;
        }

        /// <summary>A room boundary omits non-room-bounding finish panels and independent walls.
        /// Every nearby wall must also be measured before retaining the floor.</summary>
        public static IReadOnlyList<long> WallsForFloorVerification(IEnumerable<long> boundaryWalls, IEnumerable<long> nearbyWalls) =>
            (boundaryWalls ?? Enumerable.Empty<long>()).Concat(nearbyWalls ?? Enumerable.Empty<long>()).Distinct().OrderBy(id => id).ToList();

        /// <summary>Preserve pre-existing intersections, but never retain an increase or an unknown measurement.</summary>
        public static string FloorWallOverlapChangeProblem(double? beforeM3, double? afterM3, double toleranceM3)
        {
            if (!beforeM3.HasValue || double.IsNaN(beforeM3.Value) || double.IsInfinity(beforeM3.Value) || beforeM3.Value < 0)
                return "floor_wall_overlap_before_unmeasured";
            if (!afterM3.HasValue || double.IsNaN(afterM3.Value) || double.IsInfinity(afterM3.Value) || afterM3.Value < 0)
                return "floor_wall_overlap_after_unmeasured";
            if (double.IsNaN(toleranceM3) || double.IsInfinity(toleranceM3) || toleranceM3 < 0)
                throw new ArgumentOutOfRangeException(nameof(toleranceM3));
            return afterM3.Value - beforeM3.Value > toleranceM3 ? "floor_wall_overlap_increased" : null;
        }

        private static readonly string[] Common =
            { "target_document", "units", "operation", "dry_run", "confirmation_token", "transaction_name", "idempotency_key", "room_ids" };

        public static readonly Dictionary<string, string[]> Fields = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["floors"] = new[] { "type_by_use", "default_type_id", "offset_mm", "copy_parameters", "boundary_gap_tolerance_mm" },
            ["baseboards"] = new[] { "type_id", "height_mm" }
        };

        public static string Validate(JObject r)
        {
            if (r == null) return "parameters must be a JSON object.";
            string op = (r.Value<string>("operation") ?? "").Trim().ToLowerInvariant();
            if (!Fields.TryGetValue(op, out string[] own)) return "operation must be floors or baseboards for horizun_room_finishes.";
            var allowed = new HashSet<string>(Common.Concat(own), StringComparer.Ordinal);
            foreach (JProperty p in r.Properties())
                if (!allowed.Contains(p.Name)) return p.Name + " is not applicable to operation '" + op + "'. Nothing ran.";
            if (string.IsNullOrWhiteSpace(r.Value<string>("target_document"))) return "target_document is required.";
            if (!(r["room_ids"] is JArray ids) || ids.Count == 0 || ids.Count > MaxRooms || ids.Any(t => t.Type != JTokenType.Integer))
                return "room_ids must list 1.." + MaxRooms + " room ids.";
            if (ids.Select(t => t.Value<long>()).Distinct().Count() != ids.Count) return "room_ids names the same room twice.";
            if (op == "floors")
            {
                if (r["boundary_gap_tolerance_mm"] != null &&
                    ((r["boundary_gap_tolerance_mm"].Type != JTokenType.Integer && r["boundary_gap_tolerance_mm"].Type != JTokenType.Float) ||
                     !(r.Value<double>("boundary_gap_tolerance_mm") > 0) || double.IsInfinity(r.Value<double>("boundary_gap_tolerance_mm"))))
                    return "boundary_gap_tolerance_mm must be a finite number > 0.";
                if (r["type_by_use"] == null && r["default_type_id"] == null)
                    return "give type_by_use {parameter, map: {value: floor_type_id}} and/or default_type_id: the floor type is never guessed.";
                if (r["default_type_id"] != null && r["default_type_id"].Type != JTokenType.Integer) return "default_type_id must be a floor type id.";
                if (r["type_by_use"] != null)
                {
                    if (!(r["type_by_use"] is JObject u) || string.IsNullOrWhiteSpace(u.Value<string>("parameter")) || !(u["map"] is JObject map) || !map.Properties().Any())
                        return "type_by_use must be {parameter: room parameter name, map: {value: floor_type_id}}.";
                    if (u.Properties().Any(p => p.Name != "parameter" && p.Name != "map")) return "type_by_use takes parameter and map only.";
                    if (map.Properties().Any(p => p.Value.Type != JTokenType.Integer)) return "type_by_use.map values must be floor type ids.";
                }
                if (r["offset_mm"] != null && r["offset_mm"].Type != JTokenType.Integer && r["offset_mm"].Type != JTokenType.Float) return "offset_mm must be a number.";
                if (r["copy_parameters"] != null && (!(r["copy_parameters"] is JObject cp) || cp.Properties().Any(p => p.Value.Type != JTokenType.String || string.IsNullOrWhiteSpace((string)p.Value))))
                    return "copy_parameters must be {room parameter: floor parameter}.";
            }
            else
            {
                if (r["type_id"]?.Type != JTokenType.Integer) return "type_id (a wall sweep type, the baseboard) is required.";
                if (r["height_mm"] != null && r["height_mm"].Type != JTokenType.Integer && r["height_mm"].Type != JTokenType.Float) return "height_mm must be a number.";
            }
            return null;
        }

        /// <summary>The floor type for a room: the map entry for its use value, else the default, else null (refused).</summary>
        public static long? TypeFor(string useValue, IDictionary<string, long> map, long? defaultType)
        {
            if (useValue != null && map != null && map.TryGetValue(useValue.Trim(), out long t)) return t;
            return defaultType;
        }

        /// <summary>Signed shoelace area of one closed loop of plan points.</summary>
        public static double SignedArea(IList<double[]> loop)
        {
            double a = 0;
            for (int i = 0; i < loop.Count; i++)
            {
                double[] p = loop[i], q = loop[(i + 1) % loop.Count];
                a += p[0] * q[1] - q[0] * p[1];
            }
            return a / 2;
        }

        /// <summary>The area a room's loops enclose: the largest loop less the others (its holes).</summary>
        public static double EnclosedArea(IList<IList<double[]>> loops)
        {
            if (loops == null || loops.Count == 0) return 0;
            var areas = loops.Select(l => Math.Abs(SignedArea(l))).OrderByDescending(x => x).ToList();
            return areas[0] - areas.Skip(1).Sum();
        }

        public static bool AreaAgrees(double expected, double measured)
            => expected > 0 && Math.Abs(measured - expected) <= Math.Max(AreaTolerance * expected, 1e-4);

        public sealed class OrderedBoundary
        {
            public readonly List<int> Indices = new List<int>();
            public readonly List<bool> Reversed = new List<bool>();
            public double MaximumGap;
            public bool Closed;
        }

        /// <summary>Order unoriented boundary segments by nearest endpoint, including the closing seam.
        /// Coordinates and tolerance use the same units; MaximumGap is measured, never rounded to zero.</summary>
        public static OrderedBoundary OrderBoundary(IList<double[][]> segments, double tolerance)
        {
            var result = new OrderedBoundary();
            if (segments == null || segments.Count == 0) return result;
            var remaining = new HashSet<int>(Enumerable.Range(1, segments.Count - 1));
            result.Indices.Add(0); result.Reversed.Add(false);
            double[] first = segments[0][0], end = segments[0][1];
            while (remaining.Count > 0)
            {
                int next = -1; bool reverse = false; double best = double.MaxValue;
                foreach (int index in remaining.OrderBy(x => x))
                    for (int side = 0; side < 2; side++)
                    {
                        double gap = Distance(end, segments[index][side]);
                        if (gap < best) { best = gap; next = index; reverse = side == 1; }
                    }
                result.MaximumGap = Math.Max(result.MaximumGap, best);
                result.Indices.Add(next); result.Reversed.Add(reverse); remaining.Remove(next);
                end = segments[next][reverse ? 0 : 1];
            }
            result.MaximumGap = Math.Max(result.MaximumGap, Distance(end, first));
            result.Closed = result.MaximumGap <= tolerance;
            return result;
        }

        private static double Distance(double[] a, double[] b)
        {
            double total = 0;
            for (int i = 0; i < a.Length; i++) total += (a[i] - b[i]) * (a[i] - b[i]);
            return Math.Sqrt(total);
        }
    }
}
