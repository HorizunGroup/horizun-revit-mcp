using System;
using System.Collections.Generic;

namespace Horizun.Revit.Core
{
    public sealed class BoundaryEdge
    {
        public double[] Start { get; }
        public double[] Middle { get; }
        public double[] End { get; }
        public BoundaryEdge(double[] start, double[] middle, double[] end)
        { Start = start; Middle = middle; End = end; }
    }

    public sealed class RoomBoundaryAssessment
    {
        public bool Closed { get; internal set; }
        public int GapCount { get; internal set; }
        public int DuplicatePairs { get; internal set; }
    }

    public static class RoomBoundaryRules
    {
        public static RoomBoundaryAssessment Assess(IReadOnlyList<List<BoundaryEdge>> loops, double tolerance)
        {
            if (loops == null || tolerance < 0 || double.IsNaN(tolerance))
                throw new ArgumentException("Boundary loops and a nonnegative tolerance are required.");
            var result = new RoomBoundaryAssessment();
            var all = new List<BoundaryEdge>();
            if (loops.Count == 0) { result.Closed = false; return result; }
            foreach (List<BoundaryEdge> loop in loops)
            {
                if (loop == null || loop.Count == 0) { result.GapCount++; continue; }
                for (int i = 0; i < loop.Count; i++)
                {
                    BoundaryEdge current = loop[i], next = loop[(i + 1) % loop.Count];
                    if (current == null || next == null || !Near(current.End, next.Start, tolerance)) result.GapCount++;
                    if (current != null) all.Add(current);
                }
            }
            for (int i = 0; i < all.Count; i++)
                for (int j = i + 1; j < all.Count; j++)
                    if (Same(all[i], all[j], tolerance)) result.DuplicatePairs++;
            result.Closed = result.GapCount == 0;
            return result;
        }

        private static bool Same(BoundaryEdge a, BoundaryEdge b, double tolerance)
            => Near(a.Middle, b.Middle, tolerance) &&
               ((Near(a.Start, b.Start, tolerance) && Near(a.End, b.End, tolerance)) ||
                (Near(a.Start, b.End, tolerance) && Near(a.End, b.Start, tolerance)));

        private static bool Near(double[] a, double[] b, double tolerance)
        {
            if (a == null || b == null || a.Length < 2 || b.Length < 2) return false;
            double dx = a[0] - b[0], dy = a[1] - b[1];
            return dx * dx + dy * dy <= tolerance * tolerance;
        }
    }
}
