using System;
using System.Collections.Generic;
using System.Linq;

namespace Horizun.Revit.Core
{
    /// <summary>Revit-free decisions and thresholds used by the model-coherence audit.</summary>
    public static class AuditCoherenceRules
    {
        public const double DoorSideOffsetMm = 600.0;
        public const double DoorDropToleranceMm = 300.0;
        public const double BalconyDoorDistanceMm = 1000.0;
        public const double FloorFinishToleranceMm = 50.0;
        public const double SupportToleranceMm = 10.0;
        public const double StructuralOverlapThresholdM3 = 0.001;

        public static bool IsUnsupported(double distanceMm) => distanceMm > SupportToleranceMm;
        public static bool IsStructuralOverlap(double volumeM3) => volumeM3 > StructuralOverlapThresholdM3;
        public static bool IsFloorNearLevel(double elevationDifferenceMm) => Math.Abs(elevationDifferenceMm) < FloorFinishToleranceMm;
        public static bool IsBalconyDoorNearEdge(double distanceMm) => distanceMm <= BalconyDoorDistanceMm;
        public static bool IsDropBeyondTolerance(double dropMm) => dropMm > DoorDropToleranceMm;

        public static bool IsDefaultOrNeutralGrey(string materialName, int? red, int? green, int? blue,
                                                   bool hasAppearanceTexture)
        {
            string name = (materialName ?? string.Empty).Trim();
            if (name.Length == 0 || name.Equals("Default", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Por defecto", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("<Por categoría>", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("<By Category>", StringComparison.OrdinalIgnoreCase)) return true;
            if (hasAppearanceTexture || !red.HasValue || !green.HasValue || !blue.HasValue) return false;
            int r = red.Value, g = green.Value, b = blue.Value;
            return Math.Abs(r - g) <= 3 && Math.Abs(g - b) <= 3 && r >= 80 && r <= 200;
        }

        public static bool HasRailingInfill(bool hasBalusterPatternFamily, int balusterCount,
                                             bool hasPanel, bool hasGlass, int intermediateRailCount)
        {
            return (hasBalusterPatternFamily && balusterCount > 0) || hasPanel || hasGlass || intermediateRailCount > 0;
        }

        public static bool HasRoomDoor(IEnumerable<string> fromRoomUniqueIds, IEnumerable<string> toRoomUniqueIds,
                                       string roomUniqueId)
        {
            if (string.IsNullOrEmpty(roomUniqueId)) return false;
            return (fromRoomUniqueIds ?? Enumerable.Empty<string>()).Concat(toRoomUniqueIds ?? Enumerable.Empty<string>())
                .Any(id => string.Equals(id, roomUniqueId, StringComparison.Ordinal));
        }

        public static bool AreCollinearAndContiguous(double angleDifferenceDegrees, double perpendicularOffsetMm,
                                                      double gapMm, double angularToleranceDegrees = 1.0,
                                                      double offsetToleranceMm = 10.0)
        {
            return Math.Abs(angleDifferenceDegrees) <= angularToleranceDegrees &&
                   Math.Abs(perpendicularOffsetMm) <= offsetToleranceMm && gapMm <= offsetToleranceMm;
        }

        /// <summary>Perpendicular distance (mm) from point (px,py) to the infinite line through (ax,ay)-(bx,by).</summary>
        public static double PointLineOffsetMm(double px, double py, double ax, double ay, double bx, double by)
        {
            double dx = bx - ax, dy = by - ay, len = Math.Sqrt(dx * dx + dy * dy);
            if (len < 1e-9) return Math.Sqrt((px - ax) * (px - ax) + (py - ay) * (py - ay));
            return Math.Abs((px - ax) * dy - (py - ay) * dx) / len;
        }

        /// <summary>
        /// Lines of beams that continue one another: same elevation (both ends within
        /// <paramref name="toleranceMm"/>), parallel within 1 degree, on one line within the
        /// tolerance, and whose spans touch or overlap along that line. Only lines of two or
        /// more beams are returned; a beam that overlaps another on its line is marked.
        /// </summary>
        public static List<List<CoherenceBeam>> CollinearRuns(IList<CoherenceBeam> beams, double toleranceMm = 10.0)
        {
            var list = (beams ?? new List<CoherenceBeam>()).Where(b => b != null && b.LengthMm > toleranceMm).ToList();
            var parent = Enumerable.Range(0, list.Count).ToArray();
            int Find(int i) { while (parent[i] != i) i = parent[i] = parent[parent[i]]; return i; }
            for (int i = 0; i < list.Count; i++)
                for (int j = i + 1; j < list.Count; j++)
                {
                    CoherenceBeam a = list[i], b = list[j];
                    if (Math.Abs(a.Z1 - b.Z1) > toleranceMm && Math.Abs(a.Z1 - b.Z2) > toleranceMm) continue;
                    if (Math.Abs(a.Z2 - b.Z1) > toleranceMm && Math.Abs(a.Z2 - b.Z2) > toleranceMm) continue;
                    double angle = AngleBetweenDegrees(a, b);
                    double offset = Math.Max(PointLineOffsetMm(b.X1, b.Y1, a.X1, a.Y1, a.X2, a.Y2),
                                             PointLineOffsetMm(b.X2, b.Y2, a.X1, a.Y1, a.X2, a.Y2));
                    double gap = GapAlongMm(a, b);
                    if (!AreCollinearAndContiguous(angle, offset, Math.Max(0, gap), 1.0, toleranceMm)) continue;
                    if (gap < -toleranceMm) { a.OverlapsAnother = true; b.OverlapsAnother = true; }
                    parent[Find(i)] = Find(j);
                }
            return Enumerable.Range(0, list.Count).GroupBy(Find)
                .Where(g => g.Count() > 1)
                .Select(g => g.Select(i => list[i]).ToList()).ToList();
        }

        /// <summary>Acute angle between two beams' plan directions, 0..90 degrees.</summary>
        public static double AngleBetweenDegrees(CoherenceBeam a, CoherenceBeam b)
        {
            double ax = a.X2 - a.X1, ay = a.Y2 - a.Y1, bx = b.X2 - b.X1, by = b.Y2 - b.Y1;
            double la = Math.Sqrt(ax * ax + ay * ay), lb = Math.Sqrt(bx * bx + by * by);
            if (la < 1e-9 || lb < 1e-9) return 90;
            double cos = Math.Abs((ax * bx + ay * by) / (la * lb));
            return Math.Acos(Math.Min(1.0, cos)) * 180.0 / Math.PI;
        }

        /// <summary>
        /// Distance (mm) between two collinear spans measured along the first one's line:
        /// positive is a gap, zero is touching, negative is the length they overlap.
        /// </summary>
        public static double GapAlongMm(CoherenceBeam a, CoherenceBeam b)
        {
            double dx = a.X2 - a.X1, dy = a.Y2 - a.Y1, len = Math.Sqrt(dx * dx + dy * dy);
            if (len < 1e-9) return double.MaxValue;
            dx /= len; dy /= len;
            double b1 = (b.X1 - a.X1) * dx + (b.Y1 - a.Y1) * dy, b2 = (b.X2 - a.X1) * dx + (b.Y2 - a.Y1) * dy;
            double lo = Math.Min(b1, b2), hi = Math.Max(b1, b2);
            if (hi < 0) return -hi;
            if (lo > len) return lo - len;
            return -(Math.Min(hi, len) - Math.Max(lo, 0));
        }

        public static IDictionary<string, IList<T>> GroupSections<T>(IEnumerable<T> rows, Func<T, string> section)
        {
            return (rows ?? Enumerable.Empty<T>()).GroupBy(x => section(x) ?? string.Empty, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => (IList<T>)g.ToList(), StringComparer.Ordinal);
        }
    }

    /// <summary>One straight beam in plan, in millimetres, as the coherence audit compares it.</summary>
    public sealed class CoherenceBeam
    {
        public long Id { get; set; }
        public string UniqueId { get; set; }
        public long TypeId { get; set; }
        public string TypeName { get; set; }
        public double X1 { get; set; }
        public double Y1 { get; set; }
        public double Z1 { get; set; }
        public double X2 { get; set; }
        public double Y2 { get; set; }
        public double Z2 { get; set; }
        public bool OverlapsAnother { get; set; }
        public double LengthMm => Math.Sqrt((X2 - X1) * (X2 - X1) + (Y2 - Y1) * (Y2 - Y1));
    }
}
