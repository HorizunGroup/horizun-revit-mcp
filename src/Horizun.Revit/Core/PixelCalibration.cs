using System;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    /// <summary>Affine calibration from three anchors, checked on independent holdout anchors.</summary>
    public static class PixelCalibration
    {
        public static JObject Fit(double[][] plane, double[][] pixels, double[] origin, double[] right, double[] up, double tolerancePixels)
        {
            if (plane == null || pixels == null || plane.Length < 6 || pixels.Length != plane.Length)
                throw new ArgumentException("Three fit anchors and at least three independent check anchors are required.");
            if (origin.Length != 3 || right.Length != 3 || up.Length != 3 || tolerancePixels <= 0 || !Finite(tolerancePixels))
                throw new ArgumentException("Invalid frame or tolerance.");
            if (origin.Concat(right).Concat(up).Any(v => !Finite(v)) ||
                Math.Abs(right.Sum(v => v * v) - 1) > 1e-8 || Math.Abs(up.Sum(v => v * v) - 1) > 1e-8 || Math.Abs(right.Zip(up, (a, b) => a * b).Sum()) > 1e-8)
                throw new ArgumentException("Frame must have finite orthonormal axes.");
            for (int i = 0; i < plane.Length; i++)
                if (plane[i].Length != 2 || pixels[i].Length != 2 || plane[i].Concat(pixels[i]).Any(v => !Finite(v))) throw new ArgumentException("Invalid anchor.");
            for (int i = 0; i < plane.Length; i++) for (int j = i + 1; j < plane.Length; j++)
                if (Math.Abs(plane[i][0] - plane[j][0]) + Math.Abs(plane[i][1] - plane[j][1]) < 1e-8) throw new ArgumentException("Calibration/check anchors must be distinct.");
            double x1 = plane[1][0] - plane[0][0], y1 = plane[1][1] - plane[0][1];
            double x2 = plane[2][0] - plane[0][0], y2 = plane[2][1] - plane[0][1];
            double det = x1 * y2 - x2 * y1;
            if (!Finite(det) || Math.Abs(det) < 1e-9) throw new ArgumentException("Fit anchors are collinear, too close or overflowed.");
            var coefficients = new double[2][];
            for (int axis = 0; axis < 2; axis++)
            {
                double d1 = pixels[1][axis] - pixels[0][axis], d2 = pixels[2][axis] - pixels[0][axis];
                double a = (d1 * y2 - d2 * y1) / det, b = (x1 * d2 - x2 * d1) / det;
                coefficients[axis] = new[] { a, b, pixels[0][axis] - a * plane[0][0] - b * plane[0][1] };
            }
            double sx = Math.Sqrt(coefficients[0][0] * coefficients[0][0] + coefficients[1][0] * coefficients[1][0]);
            double sy = Math.Sqrt(coefficients[0][1] * coefficients[0][1] + coefficients[1][1] * coefficients[1][1]);
            double orthogonality = Math.Abs(coefficients[0][0] * coefficients[0][1] + coefficients[1][0] * coefficients[1][1]);
            if (!Finite(sx) || !Finite(sy) || !Finite(orthogonality) || coefficients.SelectMany(c => c).Any(v => !Finite(v)) || sx <= 0 || sy <= 0 || Math.Abs(sx - sy) / Math.Max(sx, sy) > 0.005 || orthogonality / (sx * sy) > 0.005)
                throw new ArgumentException("Export calibration is not a uniform orthographic projection.");
            var checks = new JArray(); double maxError = 0;
            for (int i = 3; i < plane.Length; i++)
            {
                double dx = coefficients[0][0] * plane[i][0] + coefficients[0][1] * plane[i][1] + coefficients[0][2] - pixels[i][0];
                double dy = coefficients[1][0] * plane[i][0] + coefficients[1][1] * plane[i][1] + coefficients[1][2] - pixels[i][1];
                double error = Math.Sqrt(dx * dx + dy * dy);
                if (!Finite(error)) throw new ArgumentException("Reprojection overflowed.");
                maxError = Math.Max(maxError, error);
                checks.Add(new JObject { ["anchor"] = i, ["error_pixels"] = error });
            }
            if (maxError > tolerancePixels) throw new ArgumentException("Independent reprojection error exceeds tolerance: " + maxError);
            var matrix = new JArray();
            foreach (var c in coefficients)
            {
                var row = Enumerable.Range(0, 3).Select(i => c[0] * right[i] + c[1] * up[i]).ToArray();
                var affine = row.Concat(new[] { c[2] - row.Zip(origin, (a, b) => a * b).Sum() }).ToArray();
                if (affine.Any(v => !Finite(v))) throw new ArgumentException("World transform overflowed.");
                matrix.Add(new JArray(affine));
            }
            return new JObject
            {
                ["matrix_2x4"] = matrix,
                ["pixels_per_foot"] = (sx + sy) / 2,
                ["fit_anchor_count"] = 3,
                ["independent_checks"] = checks,
                ["max_error_pixels"] = maxError,
                ["tolerance_pixels"] = tolerancePixels,
                ["pixel_origin"] = "top_left",
                ["pixel_coordinates"] = "pixel centers: first center is (0.5,0.5)",
                ["model_units"] = "feet",
                ["coordinate_reference"] = "internal_origin"
            };
        }
        private static bool Finite(double x) => !double.IsNaN(x) && !double.IsInfinity(x);
    }
}
