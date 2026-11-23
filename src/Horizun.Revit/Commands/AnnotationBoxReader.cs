// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// An annotation's box in the view frame, read through Element.get_BoundingBox(view) and
// CHECKED against the annotation's own anchor (TextNote.Coord, the tag head). Measured in
// the field (Revit 2024): that box came back with its coordinates multiplied by the view
// scale, so every note looked outside the crop. A box that contains the anchor is used as
// read; one that contains it only once divided by the scale is corrected and said so; one
// that contains it neither way is unreliable and returned null. The rule is
// Core/TextNoteLayoutRules.NormalizeAnnotationBox.
// -----------------------------------------------------------------------------
using System;
using Autodesk.Revit.DB;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    internal static class AnnotationBoxReader
    {
        /// <summary>The anchor of an annotation in model space, or null when it has none we can read.</summary>
        public static XYZ Anchor(Element e)
        {
            try
            {
                if (e is TextElement t) return t.Coord;
                if (e is IndependentTag tag) return tag.TagHeadPosition;
                if (e is SpatialElementTag st) return st.TagHeadPosition;
            }
            catch { }
            return null;
        }

        /// <summary>
        /// [minR, minU, maxR, maxU] of the annotation in the view frame (model units), corrected for the scale trap, or
        /// null when there is no box or it is unreliable. <paramref name="method"/> says which (as_read,
        /// divided_by_view_scale, unreliable, no_box, no_anchor_unchecked).
        /// </summary>
        public static double[] ViewBox(Element e, View v, out string method) => ViewBox(e, v, out method, out _);

        /// <summary>As <see cref="ViewBox(Element, View, out string)"/>, also handing back the box exactly as read.</summary>
        public static double[] ViewBox(Element e, View v, out string method, out double[] raw)
        {
            method = "no_box"; raw = null;
            BoundingBoxXYZ b;
            try { b = e.get_BoundingBox(v); } catch { b = null; }
            if (b == null) return null;
            double minR = double.MaxValue, minU = double.MaxValue, maxR = double.MinValue, maxU = double.MinValue;
            foreach (double x in new[] { b.Min.X, b.Max.X }) foreach (double y in new[] { b.Min.Y, b.Max.Y }) foreach (double z in new[] { b.Min.Z, b.Max.Z })
                    {
                        XYZ p = b.Transform.OfPoint(new XYZ(x, y, z));
                        double rr = p.DotProduct(v.RightDirection), uu = p.DotProduct(v.UpDirection);
                        minR = Math.Min(minR, rr); maxR = Math.Max(maxR, rr); minU = Math.Min(minU, uu); maxU = Math.Max(maxU, uu);
                    }
            raw = new[] { minR, minU, maxR, maxU };
            XYZ anchor = Anchor(e);
            if (anchor == null) { method = "no_anchor_unchecked"; return raw; }
            int scale; try { scale = v.Scale; } catch { scale = 1; }
            // 2 mm of paper, plus 1 % of the box, in model units: the anchor of a left/top note sits ON its border.
            double tol = 2.0 / 304.8 * Math.Max(1, scale) + 0.01 * Math.Max(maxR - minR, maxU - minU);
            TextNoteLayoutRules.BoxFix fix = TextNoteLayoutRules.NormalizeAnnotationBox(raw,
                new[] { anchor.DotProduct(v.RightDirection), anchor.DotProduct(v.UpDirection) }, scale, tol);
            method = fix.Method;
            return fix.Box;
        }

        /// <summary>The same check on a raw box, returning a corrected copy (or the box itself) for code that needs a BoundingBoxXYZ.</summary>
        public static BoundingBoxXYZ Fixed(BoundingBoxXYZ b, Element e, View v)
        {
            if (b == null) return null;
            string method;
            ViewBox(e, v, out method);
            if (method != "divided_by_view_scale") return b;
            int scale = v.Scale;
            var copy = new BoundingBoxXYZ { Transform = b.Transform };
            XYZ min = b.Min / scale, max = b.Max / scale;
            // The box was scaled about the view frame's origin; the transform's origin scales with it.
            Transform t = b.Transform;
            if (!t.IsIdentity)
            {
                Transform scaled = Transform.Identity;
                scaled.BasisX = t.BasisX; scaled.BasisY = t.BasisY; scaled.BasisZ = t.BasisZ; scaled.Origin = t.Origin / scale;
                copy.Transform = scaled;
            }
            copy.Min = min; copy.Max = max;
            return copy;
        }
    }
}
