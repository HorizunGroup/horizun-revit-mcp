// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// Reads the GEOMETRY behind a dimension reference, for item 2.3: a committed
// dimension is held against the geometry it hangs off, not against Revit's own
// Value. The arithmetic lives in Core (DimensionGeometryRules); this file only
// reads Revit and says plainly when it cannot - a reference into a link or into
// a family instance's symbol geometry comes back "unavailable" with a code,
// never as a coordinate somebody guessed.
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Commands
{
    internal static class DimensionGeometryProbe
    {
        private static double[] A(XYZ v) => new[] { v.X, v.Y, v.Z };

        /// <summary>The measurable geometry of one reference, in host model coordinates.</summary>
        public static RefGeometry Read(Document doc, Reference r)
        {
            if (r == null) return RefGeometry.None(DimensionGeometryRules.CodeUnreadable);
            try
            {
                if (r.LinkedElementId != null && r.LinkedElementId != ElementId.InvalidElementId)
                    return RefGeometry.None(DimensionGeometryRules.CodeLinkedReference);
                Element owner = doc.GetElement(r.ElementId);
                if (owner == null) return RefGeometry.None(DimensionGeometryRules.CodeUnreadable);
                // A surface-pattern line ('<face>/k') resolves to the WHOLE face; reading the
                // face plane as the joint would hold the segment to the wrong geometry.
                string stable = null;
                try { stable = r.ConvertToStableRepresentation(doc); } catch { stable = null; }
                string patternFace; int patternIndex;
                if (DimensionPatternRules.TryParse(stable, out patternFace, out patternIndex))
                    return RefGeometry.None(DimensionPatternRules.CodePatternLine);
                if (owner is Grid grid)
                {
                    var line = grid.Curve as Line;
                    return line == null
                        ? RefGeometry.None(DimensionGeometryRules.CodeUnsupportedGeometry)
                        : RefGeometry.Line(A(line.Origin), A(line.Direction));
                }
                if (owner is Level level)
                    return RefGeometry.Plane(new[] { 0.0, 0.0, level.ProjectElevation }, new[] { 0.0, 0.0, 1.0 });
                if (owner is ReferencePlane plane)
                {
                    Plane p = plane.GetPlane();
                    return RefGeometry.Plane(A(p.Origin), A(p.Normal));
                }
                // A family instance's references resolve into SYMBOL geometry whose frame
                // depends on whether the instance geometry was modified (cut by its host);
                // reading it as model coordinates would be a guess.
                if (owner is FamilyInstance) return RefGeometry.None(DimensionGeometryRules.CodeFamilyReference);

                GeometryObject go = owner.GetGeometryObjectFromReference(r);
                switch (go)
                {
                    case PlanarFace face: return RefGeometry.Plane(A(face.Origin), A(face.FaceNormal));
                    case Edge edge:
                    {
                        var line = edge.AsCurve() as Line;
                        return line == null
                            ? RefGeometry.None(DimensionGeometryRules.CodeUnsupportedGeometry)
                            : RefGeometry.Line(A(line.GetEndPoint(0)), A(line.Direction));
                    }
                    case Line l: return RefGeometry.Line(A(l.GetEndPoint(0)), A(l.Direction));
                    case Point pt: return RefGeometry.AtPoint(A(pt.Coord));
                    case null: return RefGeometry.None(DimensionGeometryRules.CodeUnreadable);
                    default: return RefGeometry.None(DimensionGeometryRules.CodeUnsupportedGeometry);
                }
            }
            catch
            {
                return RefGeometry.None(DimensionGeometryRules.CodeUnreadable);
            }
        }

        public static List<RefGeometry> ReadAll(Document doc, IEnumerable<Reference> refs)
        {
            var result = new List<RefGeometry>();
            foreach (Reference r in refs) result.Add(Read(doc, r));
            return result;
        }

        /// <summary>
        /// Whether every point lies inside the view's ACTIVE model crop box, in the crop
        /// box's own frame (its local X/Y are the view plane). Null when the view has no
        /// active crop - nothing to be outside of. The annotation crop is NOT used: its
        /// offsets are paper distances whose conversion was not confirmed live, and the
        /// model crop is the stricter, measurable boundary.
        /// </summary>
        public static bool? InsideCrop(View view, IEnumerable<XYZ> points, out JObject detail)
        {
            detail = null;
            try
            {
                if (view == null || !view.CropBoxActive) return null;
                BoundingBoxXYZ box = view.CropBox;
                if (box == null) return null;
                Transform inv = box.Transform.Inverse;
                var outside = new JArray();
                foreach (XYZ p in points)
                {
                    if (p == null) continue;
                    XYZ local = inv.OfPoint(p);
                    if (!DimensionGeometryRules.InsideCrop(local.X, local.Y, box.Min.X, box.Min.Y, box.Max.X, box.Max.Y,
                                                           DimensionPlanRules.CurveToleranceFeet))
                        outside.Add(new JArray(p.X, p.Y, p.Z));
                }
                detail = new JObject
                {
                    ["basis"] = "model_crop_box",
                    ["outside_points_internal_feet"] = outside
                };
                return outside.Count == 0;
            }
            catch (Exception ex)
            {
                detail = new JObject { ["basis"] = "model_crop_box", ["unreadable"] = ex.Message };
                return false;
            }
        }

        /// <summary>The verdict as JSON, lengths in the request's units.</summary>
        public static JObject ToJson(DimensionGeometryVerdict v, double fromFeet)
        {
            var segs = new JArray();
            foreach (SegmentVerdict s in v.Segments)
                segs.Add(new JObject
                {
                    ["index"] = s.Index,
                    ["status"] = s.Status,
                    ["geometry"] = s.GeometryFeet.HasValue ? (JToken)(s.GeometryFeet.Value * fromFeet) : JValue.CreateNull(),
                    ["measured"] = s.MeasuredFeet.HasValue ? (JToken)(s.MeasuredFeet.Value * fromFeet) : JValue.CreateNull(),
                    ["deviation"] = s.DeviationFeet.HasValue ? (JToken)(s.DeviationFeet.Value * fromFeet) : JValue.CreateNull(),
                    ["reason"] = s.Reason
                });
            return new JObject
            {
                ["status"] = v.Status,
                ["tolerance"] = v.ToleranceFeet * fromFeet,
                ["reference_order_normalised"] = v.Reordered,
                ["segments"] = segs
            };
        }
    }
}
