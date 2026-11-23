using System;
using System.Collections.Generic;
using System.Linq;

namespace Horizun.Revit.Core
{
    // All coordinates are metres in the same document/link placement.
    internal sealed class MeshSurface
    {
        private const double Tolerance = 1e-9;
        private readonly MeshTriangleSet exact = new MeshTriangleSet();
        private readonly List<Triangle> triangles = new List<Triangle>();
        private Triangle[] indexedTriangles;
        private BoundsNode index;

        internal void Add(double[][] vertices)
        {
            if (!exact.Add(vertices)) return;
            triangles.Add(new Triangle(vertices, triangles.Count));
            indexedTriangles = null;
            index = null;
        }

        internal sealed class QueryMetrics
        {
            internal long BoundsChecks;
        }

        internal static IEnumerable<double[][]> Uncovered(double[][] vertices, List<MeshSurface> parents, QueryMetrics metrics = null)
        {
            if (parents == null) { yield return vertices; yield break; }
            if (parents.Any(parent => parent.exact.Contains(vertices))) yield break;

            var face = new Triangle(vertices);
            Point normal = Point.Cross(face.Points[1] - face.Points[0], face.Points[2] - face.Points[0]);
            double length = Math.Sqrt(Point.Dot(normal, normal));
            if (length <= Tolerance * Tolerance) { yield return vertices; yield break; }
            normal *= 1 / length;
            var fragments = new List<Point[]> { face.Points };
            foreach (MeshSurface parent in parents)
                foreach (Triangle cover in parent.Overlapping(face.Bounds, metrics))
                {
                    if (cover.Points.Any(p =>
                        Math.Abs(Point.Dot(p - face.Points[0], normal)) > Tolerance)) continue;
                    fragments = fragments.SelectMany(fragment => Subtract(fragment, cover.Points, normal)).ToList();
                    if (fragments.Count == 0) yield break;
                }

            if (fragments.Count == 1 && ReferenceEquals(fragments[0], face.Points))
            {
                yield return vertices;
                yield break;
            }
            foreach (Point[] fragment in fragments)
                for (int i = 1; i + 1 < fragment.Length; i++)
                {
                    var triangle = new[] { fragment[0], fragment[i], fragment[i + 1] };
                    if (HasArea(triangle, normal)) yield return triangle.Select(p => p.Array()).ToArray();
                }
        }

        // Clip the intersection against the three edges. Each discarded outside
        // piece is convex and disjoint from the other pieces. Together they are
        // exactly the part of the child face this parent triangle did not cover.
        private static IEnumerable<Point[]> Subtract(Point[] fragment, Point[] cover, Point normal)
        {
            double signedArea = Point.Dot(Point.Cross(cover[1] - cover[0], cover[2] - cover[0]), normal);
            if (Math.Abs(signedArea) <= Tolerance * Tolerance) return new[] { fragment };
            var remainder = fragment;
            var pieces = new List<Point[]>();
            for (int i = 0; i < 3; i++)
            {
                Point origin = cover[i], edge = cover[(i + 1) % 3] - origin;
                Point inward = Point.Cross(normal, edge) * (Math.Sign(signedArea) / Math.Sqrt(Point.Dot(edge, edge)));
                var inside = new List<Point>();
                var outside = new List<Point>();
                Point previous = remainder[remainder.Length - 1];
                double previousDistance = Distance(previous, origin, inward);
                foreach (Point current in remainder)
                {
                    double distance = Distance(current, origin, inward);
                    if (previousDistance * distance < 0)
                    {
                        Point crossing = previous + (current - previous) * (previousDistance / (previousDistance - distance));
                        inside.Add(crossing);
                        outside.Add(crossing);
                    }
                    if (distance >= 0) inside.Add(current);
                    if (distance <= 0) outside.Add(current);
                    previous = current;
                    previousDistance = distance;
                }
                if (!HasArea(inside, normal)) return new[] { fragment };
                if (HasArea(outside, normal)) pieces.Add(outside.ToArray());
                remainder = inside.ToArray();
            }
            return pieces;
        }

        private static double Distance(Point point, Point origin, Point direction)
        {
            double distance = Point.Dot(point - origin, direction);
            return Math.Abs(distance) <= Tolerance ? 0 : distance;
        }

        private static bool HasArea(IList<Point> polygon, Point normal)
        {
            for (int i = 1; i + 1 < polygon.Count; i++)
                if (Math.Abs(Point.Dot(Point.Cross(polygon[i] - polygon[0], polygon[i + 1] - polygon[0]), normal)) > Tolerance * Tolerance)
                    return true;
            return false;
        }

        private IEnumerable<Triangle> Overlapping(Bounds bounds, QueryMetrics metrics)
        {
            if (triangles.Count == 0) return Array.Empty<Triangle>();
            if (index == null)
            {
                indexedTriangles = triangles.ToArray();
                index = new BoundsNode(indexedTriangles, 0, indexedTriangles.Length);
            }
            var matches = new List<Triangle>();
            index.Find(indexedTriangles, bounds, matches, metrics);
            // Preserve the clipping order, including the triangulation of any remainder.
            matches.Sort((a, b) => a.Order.CompareTo(b.Order));
            return matches;
        }

        private sealed class BoundsNode
        {
            private const int LeafSize = 8;
            private readonly Bounds bounds;
            private readonly int start, end;
            private readonly BoundsNode left, right;

            internal BoundsNode(Triangle[] items, int start, int end)
            {
                this.start = start;
                this.end = end;
                bounds = items[start].Bounds;
                for (int i = start + 1; i < end; i++) bounds = bounds.Union(items[i].Bounds);
                if (end - start <= LeafSize) return;

                int axis = bounds.LongestAxis();
                Array.Sort(items, start, end - start, Comparer<Triangle>.Create((a, b) =>
                    a.Bounds.Center(axis).CompareTo(b.Bounds.Center(axis))));
                int middle = start + (end - start) / 2;
                left = new BoundsNode(items, start, middle);
                right = new BoundsNode(items, middle, end);
            }

            internal void Find(Triangle[] items, Bounds query, List<Triangle> matches, QueryMetrics metrics)
            {
                if (metrics != null) metrics.BoundsChecks++;
                if (!bounds.Overlaps(query)) return;
                if (left != null)
                {
                    left.Find(items, query, matches, metrics);
                    right.Find(items, query, matches, metrics);
                    return;
                }
                for (int i = start; i < end; i++)
                {
                    if (metrics != null) metrics.BoundsChecks++;
                    if (items[i].Bounds.Overlaps(query)) matches.Add(items[i]);
                }
            }
        }

        private sealed class Triangle
        {
            internal readonly Point[] Points;
            internal readonly Bounds Bounds;
            internal readonly int Order;

            internal Triangle(double[][] vertices, int order = 0)
            {
                Points = vertices.Select(v => new Point(v[0], v[1], v[2])).ToArray();
                Bounds = new Bounds(Point.Min(Points[0], Point.Min(Points[1], Points[2])),
                    Point.Max(Points[0], Point.Max(Points[1], Points[2])));
                Order = order;
            }
        }

        private readonly struct Bounds
        {
            private readonly Point min, max;
            internal Bounds(Point min, Point max) { this.min = min; this.max = max; }
            internal Bounds Union(Bounds other) => new Bounds(Point.Min(min, other.min), Point.Max(max, other.max));
            internal double Center(int axis) => min.Along(axis) * 0.5 + max.Along(axis) * 0.5;
            internal int LongestAxis()
            {
                Point extent = max - min;
                if (extent.X >= extent.Y && extent.X >= extent.Z) return 0;
                return extent.Y >= extent.Z ? 1 : 2;
            }
            internal bool Overlaps(Bounds other) =>
                min.X <= other.max.X + Tolerance && other.min.X <= max.X + Tolerance &&
                min.Y <= other.max.Y + Tolerance && other.min.Y <= max.Y + Tolerance &&
                min.Z <= other.max.Z + Tolerance && other.min.Z <= max.Z + Tolerance;
        }

        private readonly struct Point
        {
            internal readonly double X, Y, Z;
            internal Point(double x, double y, double z) { X = x; Y = y; Z = z; }
            internal double Along(int axis) => axis == 0 ? X : axis == 1 ? Y : Z;
            internal static Point Min(Point a, Point b) => new Point(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Min(a.Z, b.Z));
            internal static Point Max(Point a, Point b) => new Point(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y), Math.Max(a.Z, b.Z));
            internal double[] Array() => new[] { X, Y, Z };
            public static Point operator +(Point a, Point b) => new Point(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
            public static Point operator -(Point a, Point b) => new Point(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
            public static Point operator *(Point p, double f) => new Point(p.X * f, p.Y * f, p.Z * f);
            internal static double Dot(Point a, Point b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
            internal static Point Cross(Point a, Point b) => new Point(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
        }
    }
}
