// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// THE REVIT-FREE HALF of horizun_capture_view framing and image size.
//
// MEASURED (INFORME_P6 6.4, Revit 2023): element_ids with three Structural Rebar
// framed a crop of +-100 ft and the PNG came back 900 x 9000 px. A bar's model
// bounding box is not its body; its geometry is. And ExportImage with a
// horizontal fit sets the WIDTH to pixel_size whatever the aspect, so a tall
// frame becomes an image ten times taller than asked.
//
// Kept apart so the decisions are tested without Revit (Core.Tests links it).
// -----------------------------------------------------------------------------
using System;

namespace Horizun.Revit.Core
{
    public static class CaptureFrameRules
    {
        public static string ValidateCapture(bool template, bool orientationRequested, bool orthographic3d, int pixelSize)
        {
            if (template) return "A view template cannot be captured; select a view instead.";
            if (pixelSize < 64 || pixelSize > 8192) return "pixel_size must be between 64 and 8192; got " + pixelSize + ".";
            if (orientationRequested && !orthographic3d) return "orientation requires an orthographic 3D view.";
            return null;
        }

        public static string ElementProblem(long id, bool exists, bool hasBounds)
        {
            if (!exists) return "element_ids contains an element that does not exist: " + id + ".";
            if (!hasBounds) return "No requested element has a model bounding box or readable geometry; nothing can be framed.";
            return null;
        }

        public static string EmptyViewProblem(bool measured, int candidateCount)
            => measured && candidateCount == 0
                ? "The view has no candidate elements to render. Check its visibility and crop before capture."
                : null;

        /// <summary>A model box this many times larger than the geometry (on any axis) is not the element's body.</summary>
        public const double OversizeFactor = 4.0;

        /// <summary>...and larger by at least this much (feet), so a tiny piece's rounding never switches basis.</summary>
        public const double OversizeMarginFeet = 3.0;

        /// <summary>
        /// The box to frame an element by, {minX,minY,minZ,maxX,maxY,maxZ}: its model box, unless that box is far
        /// larger than the geometry it holds (rebar), in which case the geometry. Null when neither exists.
        /// </summary>
        public static double[] ChooseBounds(double[] modelBox, double[] geometryBox, out string basis)
        {
            bool model = Valid(modelBox), geometry = Valid(geometryBox);
            if (!model && !geometry) { basis = null; return null; }
            if (!geometry) { basis = "model_box"; return modelBox; }
            if (!model) { basis = "geometry"; return geometryBox; }
            for (int axis = 0; axis < 3; axis++)
            {
                double m = modelBox[axis + 3] - modelBox[axis], g = Math.Max(geometryBox[axis + 3] - geometryBox[axis], 0);
                if (m - g > OversizeMarginFeet && m > OversizeFactor * Math.Max(g, 1e-6))
                {
                    basis = "geometry";
                    return geometryBox;
                }
            }
            basis = "model_box";
            return modelBox;
        }

        /// <summary>An export fitted by width came out taller than pixel_size: export again fitted by height.</summary>
        public static bool NeedsHeightFit(int width, int height, int pixelSize)
            => width > 0 && height > 0 && height > pixelSize && height > width;

        /// <summary>Neither side of the image exceeds the requested pixel_size (one pixel of rounding allowed).</summary>
        public static bool WithinPixelSize(int width, int height, int pixelSize)
            => width <= pixelSize + 1 && height <= pixelSize + 1;

        private static bool Valid(double[] b)
        {
            if (b == null || b.Length != 6) return false;
            for (int i = 0; i < 6; i++) if (double.IsNaN(b[i]) || double.IsInfinity(b[i])) return false;
            return b[3] >= b[0] && b[4] >= b[1] && b[5] >= b[2];
        }
    }
}
