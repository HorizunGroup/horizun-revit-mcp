using System;
using System.Collections.Generic;
using System.Linq;

namespace Horizun.Revit.Core
{
    // Geometry is in model feet. Loops and curves may be reordered or reversed by Revit.
    public static class AnnotationMoveRules
    {
        public const double PositionToleranceFeet = 1e-6;

        public static double[] ProjectToPlane(double[] vector, double[] normal)
        {
            if (vector == null || normal == null || vector.Length != 3 || normal.Length != 3 ||
                vector.Concat(normal).Any(x => double.IsNaN(x) || double.IsInfinity(x)))
                throw new ArgumentException("Vector and view normal must be finite 3D vectors.");
            double length2 = normal.Sum(x => x * x);
            if (length2 < 1e-20 || double.IsInfinity(length2)) throw new ArgumentException("View normal is invalid.");
            double dot = Enumerable.Range(0, 3).Sum(i => vector[i] * normal[i]) / length2;
            return Enumerable.Range(0, 3).Select(i => vector[i] - dot * normal[i]).ToArray();
        }
        public sealed class Curve
        {
            public string Kind;
            public List<double[]> Points;
        }

        public static bool PointMoved(double[] before, double[] after, double[] vector, double tolerance = 1e-5)
        {
            if (before == null || after == null || vector == null || before.Length != 3 || after.Length != 3 || vector.Length != 3) return false;
            double sum = 0;
            for (int i = 0; i < 3; i++)
            {
                double d = before[i] + vector[i] - after[i];
                if (double.IsNaN(d) || double.IsInfinity(d)) return false;
                sum += d * d;
            }
            return sum <= tolerance * tolerance;
        }

        public static bool ContoursMoved(List<List<Curve>> before, List<List<Curve>> after, double[] vector)
        {
            if (before == null || after == null || before.Count == 0 || before.Count != after.Count) return false;
            var left = new List<List<Curve>>(after);
            foreach (var loop in before)
            {
                int index = left.FindIndex(candidate => LoopMoved(loop, candidate, vector));
                if (index < 0) return false;
                left.RemoveAt(index);
            }
            return true;
        }

        private static bool LoopMoved(List<Curve> before, List<Curve> after, double[] vector)
        {
            if (before == null || after == null || before.Count == 0 || before.Count != after.Count) return false;
            var left = new List<Curve>(after);
            foreach (var curve in before)
            {
                int index = left.FindIndex(candidate => CurveMoved(curve, candidate, vector));
                if (index < 0) return false;
                left.RemoveAt(index);
            }
            return true;
        }

        private static bool CurveMoved(Curve before, Curve after, double[] vector)
        {
            if (before == null || after == null || before.Kind != after.Kind || before.Points == null || after.Points == null ||
                before.Points.Count < 2 || before.Points.Count != after.Points.Count) return false;
            int n = before.Points.Count;
            return Enumerable.Range(0, n).All(i => PointMoved(before.Points[i], after.Points[i], vector)) ||
                   Enumerable.Range(0, n).All(i => PointMoved(before.Points[i], after.Points[n - 1 - i], vector));
        }

        // A conservative admission estimate, not a guarantee of Revit execution time.
        public static double EstimatedSeconds(int operations, int targets) => Math.Max(operations, targets) * 1.5;
        public static bool WithinBudget(int operations, int targets) => EstimatedSeconds(operations, targets) <= 15;
    }
}
