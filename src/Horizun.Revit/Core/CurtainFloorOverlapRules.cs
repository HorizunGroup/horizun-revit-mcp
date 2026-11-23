using System;
using System.Collections.Generic;
using System.Linq;

namespace Horizun.Revit.Core
{
    /// <summary>Decision and user guidance for curtain-wall/floor intersection measurements.</summary>
    public static class CurtainFloorOverlapRules
    {
        public const string SolidIntersectionMethod = "solid_intersection";
        public const string OrientedPrismMethod = "curtain_wall_oriented_prism";
        public const string WallBoundsMethod = "curtain_wall_bounding_box";
        public const string ComponentBoundsMethod = "panel_mullion_bounding_box";
        public const string WarningMethod = "unmeasured";
        public const string Recommendation = "If the floor should stop at the wall, draw its edge at the wall's interior face; if it is a roof, create a roof instead of a floor; otherwise specify an explicit overlap tolerance.";

        public struct Point2
        {
            public readonly double X;
            public readonly double Y;
            public Point2(double x, double y) { X = x; Y = y; }
        }

        /// <summary>Four corners of a straight wall's oriented horizontal footprint.</summary>
        public static Point2[] OrientedFootprint(double startX, double startY, double endX, double endY, double width)
        {
            double dx = endX - startX, dy = endY - startY;
            double length = Math.Sqrt(dx * dx + dy * dy);
            if (!IsFinite(length) || length <= 0 || !IsFinite(width) || width <= 0)
                throw new ArgumentOutOfRangeException(nameof(width), "A finite nonzero line and width are required.");
            double half = width / 2, nx = -dy / length * half, ny = dx / length * half;
            return new[] { new Point2(startX + nx, startY + ny), new Point2(endX + nx, endY + ny),
                new Point2(endX - nx, endY - ny), new Point2(startX - nx, startY - ny) };
        }

        /// <summary>Area where the oriented wall footprint intersects an axis-aligned rectangle.</summary>
        public static double OrientedFootprintIntersectionArea(Point2[] footprint, double minX, double minY, double maxX, double maxY)
        {
            if (footprint == null || footprint.Length != 4 || !IsFinite(minX) || !IsFinite(minY) ||
                !IsFinite(maxX) || !IsFinite(maxY) || maxX < minX || maxY < minY) return 0;
            var polygon = footprint.ToList();
            polygon = Clip(polygon, p => p.X >= minX, (a, b) => IntersectX(a, b, minX));
            polygon = Clip(polygon, p => p.X <= maxX, (a, b) => IntersectX(a, b, maxX));
            polygon = Clip(polygon, p => p.Y >= minY, (a, b) => IntersectY(a, b, minY));
            polygon = Clip(polygon, p => p.Y <= maxY, (a, b) => IntersectY(a, b, maxY));
            if (polygon.Count < 3) return 0;
            double twice = 0;
            for (int i = 0; i < polygon.Count; i++)
            {
                Point2 a = polygon[i], b = polygon[(i + 1) % polygon.Count];
                twice += a.X * b.Y - b.X * a.Y;
            }
            return Math.Abs(twice) / 2;
        }

        private static List<Point2> Clip(List<Point2> input, Func<Point2, bool> inside, Func<Point2, Point2, Point2> intersection)
        {
            var output = new List<Point2>();
            if (input.Count == 0) return output;
            Point2 previous = input[input.Count - 1]; bool previousInside = inside(previous);
            foreach (Point2 current in input)
            {
                bool currentInside = inside(current);
                if (currentInside != previousInside) output.Add(intersection(previous, current));
                if (currentInside) output.Add(current);
                previous = current; previousInside = currentInside;
            }
            return output;
        }

        private static Point2 IntersectX(Point2 a, Point2 b, double x)
        {
            double t = (x - a.X) / (b.X - a.X);
            return new Point2(x, a.Y + t * (b.Y - a.Y));
        }

        private static Point2 IntersectY(Point2 a, Point2 b, double y)
        {
            double t = (y - a.Y) / (b.Y - a.Y);
            return new Point2(a.X + t * (b.X - a.X), y);
        }

        private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

        public static bool IsApproximateMethod(string method) =>
            !string.Equals(method, SolidIntersectionMethod, StringComparison.Ordinal);

        public static string Problem(double? overlapM3, double toleranceM3)
        {
            if (!overlapM3.HasValue || double.IsNaN(overlapM3.Value) || double.IsInfinity(overlapM3.Value) || overlapM3.Value < 0)
                return "floor_wall_overlap_unmeasured";
            if (double.IsNaN(toleranceM3) || double.IsInfinity(toleranceM3) || toleranceM3 < 0)
                throw new ArgumentOutOfRangeException(nameof(toleranceM3));
            return overlapM3.Value > toleranceM3 ? "floor_wall_overlap_increased" : null;
        }

        public static string Problem(double? overlapM3, double toleranceM3, string method)
        {
            string problem = Problem(overlapM3, toleranceM3);
            return problem == "floor_wall_overlap_increased" && IsApproximateMethod(method)
                ? "floor_wall_overlap_approximate" : problem;
        }

        /// <summary>
        /// How the wall is named in the guidance: its real kind and type ("Basic wall 'Generic - 200mm'"),
        /// never "Curtain wall" for a basic one (5.0.5, measured on a course model: a basic wall/floor clash was
        /// reported as a curtain wall). Null or blank falls back to "Wall".
        /// </summary>
        public static string WallLabel(string kind, string typeName)
        {
            string k = string.IsNullOrWhiteSpace(kind) ? "Wall" : kind.Trim();
            return string.IsNullOrWhiteSpace(typeName) ? k : k + " '" + typeName.Trim() + "'";
        }

        public static string Guidance(string method, long wallId) => Guidance(method, wallId, "Curtain wall");

        public static string Guidance(string method, long wallId, string wallLabel) =>
            (string.IsNullOrWhiteSpace(wallLabel) ? "Wall" : wallLabel) + " " + wallId + (method == WarningMethod
                ? " could not be measured; creation continued with a warning. "
                : IsApproximateMethod(method)
                    ? " overlap estimated by " + method + "; this is approximate, not an exact solid intersection, and creation continued with a warning. "
                    : " overlap measured by " + method + ". ") + Recommendation;
    }
}
