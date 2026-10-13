// -----------------------------------------------------------------------------
// Horizun MCP - original Horizun code.
//
// Pure 2D overlap geometry for the tag/text-note overlap check in
// horizun_verify_changes. SpatialCoherence.cs measures shared SOLID volume in
// model space, which sees nothing here: a tag or a text note is annotation,
// drawn flat in a view, not a solid. Two tags stacked on top of each other in
// a plan are numbers nobody can read - a real defect a solid-intersection
// check cannot see. This file only compares axis-aligned rectangles; the
// Revit-side gathering (which elements, which view, which bounding box) lives
// in TagOverlapCheck.cs.
// -----------------------------------------------------------------------------
using System;

namespace Horizun.Revit.Core
{
    public static class TagOverlapRules
    {
        /// <summary>An axis-aligned rectangle in one view's own coordinates. Callers normalise
        /// Min/Max themselves; this type does not assume which corner is which.</summary>
        public struct Rect
        {
            public double MinX, MinY, MaxX, MaxY;

            public static Rect Normalized(double x0, double y0, double x1, double y1) =>
                new Rect { MinX = Math.Min(x0, x1), MinY = Math.Min(y0, y1), MaxX = Math.Max(x0, x1), MaxY = Math.Max(y0, y1) };
        }

        /// <summary>Shared area of two rectangles, 0 when they only touch or do not meet at all -
        /// touching edges are not an overlap finding.</summary>
        public static double OverlapArea(Rect a, Rect b)
        {
            double w = Math.Min(a.MaxX, b.MaxX) - Math.Max(a.MinX, b.MinX);
            double h = Math.Min(a.MaxY, b.MaxY) - Math.Max(a.MinY, b.MinY);
            if (w <= 0 || h <= 0) return 0;
            return w * h;
        }

        public static bool Overlaps(Rect a, Rect b) => OverlapArea(a, b) > 0;

        /// <summary>A BoundingBoxXYZ's Transform, as plain vectors (null for the identity).</summary>
        public sealed class BoxTransform
        {
            public double[] Origin, BasisX, BasisY, BasisZ;

            public double[] Apply(double[] p) => new[]
            {
                Origin[0] + BasisX[0] * p[0] + BasisY[0] * p[1] + BasisZ[0] * p[2],
                Origin[1] + BasisX[1] * p[0] + BasisY[1] * p[1] + BasisZ[1] * p[2],
                Origin[2] + BasisX[2] * p[0] + BasisY[2] * p[1] + BasisZ[2] * p[2]
            };
        }

        /// <summary>
        /// The rectangle a box covers ON SCREEN in one view (backlog #12): its eight corners,
        /// through the box's own transform, projected onto the view's right and up directions.
        /// Comparing model X/Y read every tag of a section or an elevation as flat (overlap 0,
        /// "clean" by construction) and a rotated plan's side-by-side tags as overlapping.
        /// </summary>
        public static Rect ViewRect(double[] min, double[] max, BoxTransform transform, double[] right, double[] up)
        {
            double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
            for (int i = 0; i < 8; i++)
            {
                double[] p = { (i & 1) == 0 ? min[0] : max[0], (i & 2) == 0 ? min[1] : max[1], (i & 4) == 0 ? min[2] : max[2] };
                if (transform != null) p = transform.Apply(p);
                double x = p[0] * right[0] + p[1] * right[1] + p[2] * right[2];
                double y = p[0] * up[0] + p[1] * up[1] + p[2] * up[2];
                x0 = Math.Min(x0, x); x1 = Math.Max(x1, x);
                y0 = Math.Min(y0, y); y1 = Math.Max(y1, y);
            }
            return Rect.Normalized(x0, y0, x1, y1);
        }
    }
}
