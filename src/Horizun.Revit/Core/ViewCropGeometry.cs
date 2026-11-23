// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// THE REVIT-FREE HALF OF horizun_view_crop (FIELD FEEDBACK 2.1).
//
// A view's crop lives in the CropBox's OWN frame (CropBox.Transform: origin and
// three orthonormal axes Revit chooses). A rotated plan, a section or a callout
// all differ only in that frame. The caller speaks in one of two conventions:
//
//   plan-like views (looking straight down or up): a rectangle in MODEL X/Y. Its
//     four corners are carried into the crop frame and the crop is their bounding
//     rectangle there - for a view rotated against the model the crop that
//     results is the smallest one that contains the asked rectangle, and the
//     reply says so (requested vs target).
//   every other view (sections, elevations, their callouts and details): a
//     rectangle on the VIEW's own axes, each coordinate the projection of a model
//     point onto that axis (x = P . right, y = P . up). It does not depend on the
//     view origin, so the same numbers mean the same place before and after.
//
// Paper size is model size / view scale. Lengths here are internal feet unless
// the name says mm.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    /// <summary>An orthonormal frame: origin and axes in model coordinates.</summary>
    public sealed class CropFrame
    {
        public double[] Origin = { 0, 0, 0 }, X = { 1, 0, 0 }, Y = { 0, 1, 0 }, Z = { 0, 0, 1 };

        public static double Dot(double[] a, double[] b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];

        /// <summary>Model point into the frame.</summary>
        public double[] ToLocal(double[] p)
        {
            var d = new[] { p[0] - Origin[0], p[1] - Origin[1], p[2] - Origin[2] };
            return new[] { Dot(d, X), Dot(d, Y), Dot(d, Z) };
        }

        /// <summary>Frame point into the model.</summary>
        public double[] ToModel(double[] l)
            => new[]
            {
                Origin[0] + l[0] * X[0] + l[1] * Y[0] + l[2] * Z[0],
                Origin[1] + l[0] * X[1] + l[1] * Y[1] + l[2] * Z[1],
                Origin[2] + l[0] * X[2] + l[1] * Y[2] + l[2] * Z[2]
            };
    }

    public static class ViewCropGeometry
    {
        public const double FeetPerMm = 1.0 / 304.8;
        /// <summary>The verification tolerance: 1 mm of model.</summary>
        public const double ToleranceFeet = 1.0 * FeetPerMm;
        /// <summary>Revit refuses an annotation crop offset under 1/8 inch (RevitAPI.xml): 3.175 mm of paper.</summary>
        public const double MinAnnotationOffsetMm = 3.175;

        /// <summary>The view looks straight down (or up): the caller's rectangle is in model X/Y.</summary>
        public static bool IsPlanLike(double[] viewDirection)
            => viewDirection != null && Math.Abs(viewDirection[2]) > 0.999;

        /// <summary>A rectangle [minX, minY, maxX, maxY] enclosing the points' X/Y, grown by margin on every side.</summary>
        public static double[] Bounds(IEnumerable<double[]> points, double margin)
        {
            List<double[]> pts = (points ?? Enumerable.Empty<double[]>()).Where(p => p != null && p.Length >= 2).ToList();
            if (pts.Count == 0) throw new ArgumentException("no point to bound");
            if (pts.Any(p => !Finite(p[0]) || !Finite(p[1]))) throw new ArgumentException("a point is not finite");
            if (!Finite(margin) || margin < 0) throw new ArgumentException("margin must be finite and non-negative");
            return new[] { pts.Min(p => p[0]) - margin, pts.Min(p => p[1]) - margin, pts.Max(p => p[0]) + margin, pts.Max(p => p[1]) + margin };
        }

        /// <summary>Bounds in crop-frame coordinates, in min XYZ then max XYZ order.</summary>
        public static double[] VolumeBounds(IReadOnlyList<double[]> points, double margin)
        {
            double[] xy = Bounds(points, margin);
            if (points.Any(p => p == null || p.Length != 3 || !Finite(p[2])))
                throw new ArgumentException("a crop point has no finite depth");
            return new[] { xy[0], xy[1], points.Min(p => p[2]) - margin,
                           xy[2], xy[3], points.Max(p => p[2]) + margin };
        }

        internal static (double[] Crop, double[] Section) ElementCropBounds(CropFrame view, CropFrame section,
            IReadOnlyList<double[]> modelCorners, double margin)
            => (Bounds(modelCorners.Select(view.ToLocal), margin), SectionBounds(section, modelCorners, margin));

        internal static double[] SectionBounds(CropFrame nativeSection, IEnumerable<double[]> modelCorners, double margin)
            => VolumeBounds(modelCorners.Select(nativeSection.ToLocal).ToList(), margin);

        internal static JObject BindSectionBoxTarget(PlannedElement view, CropFrame frame, double[] bounds, double[] cropRect = null)
        {
            var target = new JObject
            {
                ["local_min_ft"] = new JArray(bounds.Take(3)),
                ["local_max_ft"] = new JArray(bounds.Skip(3)),
                ["origin_ft"] = new JArray(frame.Origin),
                ["x"] = new JArray(frame.X), ["y"] = new JArray(frame.Y), ["z"] = new JArray(frame.Z)
            };
            view.ProposedValues = view.ProposedValues ?? new Dictionary<string, string>();
            view.ProposedValues["section_box"] = target.ToString(Newtonsoft.Json.Formatting.None);
            if (cropRect != null)
                view.ProposedValues["crop_box"] = new JArray(cropRect).ToString(Newtonsoft.Json.Formatting.None);
            return target;
        }

        /// <summary>Bounds of the section box after clipping it by the view rectangle along the viewing direction.</summary>
        internal static double[] ClippedSectionBounds(CropFrame view, CropFrame section, double[] box, double[] rect)
        {
            var corners = new double[8][];
            for (int i = 0; i < corners.Length; i++)
                corners[i] = view.ToLocal(section.ToModel(new[]
                {
                    box[(i & 1) == 0 ? 0 : 3], box[(i & 2) == 0 ? 1 : 4], box[(i & 4) == 0 ? 2 : 5]
                }));
            if (rect == null) return VolumeBounds(corners, 0);

            int[][] faces = { new[] { 0, 1, 3, 2 }, new[] { 4, 5, 7, 6 }, new[] { 0, 1, 5, 4 },
                              new[] { 2, 3, 7, 6 }, new[] { 0, 2, 6, 4 }, new[] { 1, 3, 7, 5 } };
            var points = new List<double[]>();
            foreach (int[] face in faces)
            {
                List<double[]> polygon = face.Select(i => corners[i]).ToList();
                for (int i = 0; i < 4 && polygon.Count > 0; i++)
                    polygon = ClipFace(polygon, i % 2, rect[i], i < 2);
                points.AddRange(polygon);
            }
            if (points.Count == 0) return null;
            double[] bounds = VolumeBounds(points, 0);
            return bounds[3] - bounds[0] > 1e-9 && bounds[4] - bounds[1] > 1e-9 ? bounds : null;
        }

        private static List<double[]> ClipFace(List<double[]> polygon, int axis, double edge, bool keepAbove)
        {
            var kept = new List<double[]>();
            double SignedDistance(double[] p) => keepAbove ? p[axis] - edge : edge - p[axis];
            double[] previous = polygon[polygon.Count - 1];
            foreach (double[] current in polygon)
            {
                double a = SignedDistance(previous), b = SignedDistance(current);
                if ((a >= 0) != (b >= 0))
                {
                    double t = a / (a - b);
                    kept.Add(previous.Zip(current, (p, q) => p + (q - p) * t).ToArray());
                }
                if (b >= 0) kept.Add(current);
                previous = current;
            }
            return kept;
        }

        /// <summary>The crop-frame rectangle for a model X/Y rectangle (plan-like views), at elevation z.</summary>
        public static double[] TargetFromModelRect(CropFrame f, double[] rect, double z)
        {
            var corners = new[]
            {
                new[] { rect[0], rect[1], z }, new[] { rect[2], rect[1], z },
                new[] { rect[2], rect[3], z }, new[] { rect[0], rect[3], z }
            };
            return Bounds(corners.Select(f.ToLocal), 0);
        }

        /// <summary>The crop-frame rectangle for a rectangle on the view's axes (x = P . right, y = P . up).</summary>
        public static double[] TargetFromViewAxes(CropFrame f, double[] rect)
        {
            double ox = CropFrame.Dot(f.Origin, f.X), oy = CropFrame.Dot(f.Origin, f.Y);
            return new[] { Math.Min(rect[0], rect[2]) - ox, Math.Min(rect[1], rect[3]) - oy, Math.Max(rect[0], rect[2]) - ox, Math.Max(rect[1], rect[3]) - oy };
        }

        /// <summary>The caller's convention for a crop-frame rectangle: model X/Y bounds (plan-like) or view-axis coordinates.</summary>
        public static double[] InCallerTerms(CropFrame f, double[] local, bool planLike, double z = 0)
        {
            if (planLike)
            {
                var corners = new[]
                {
                    new[] { local[0], local[1], z }, new[] { local[2], local[1], z },
                    new[] { local[2], local[3], z }, new[] { local[0], local[3], z }
                };
                return Bounds(corners.Select(f.ToModel), 0);
            }
            double ox = CropFrame.Dot(f.Origin, f.X), oy = CropFrame.Dot(f.Origin, f.Y);
            return new[] { local[0] + ox, local[1] + oy, local[2] + ox, local[3] + oy };
        }

        /// <summary>A model length in feet, as millimetres on paper at 1:scale.</summary>
        public static double PaperMm(double modelFeet, int scale)
        {
            if (scale <= 0) throw new ArgumentException("scale must be positive");
            return modelFeet * 304.8 / scale;
        }

        /// <summary>Every edge within the tolerance.</summary>
        public static bool Matches(double[] a, double[] b, double tolerance)
            => a != null && b != null && a.Length == 4 && b.Length == 4 &&
               Enumerable.Range(0, 4).All(i => Finite(b[i]) && Math.Abs(a[i] - b[i]) <= tolerance);

        /// <summary>A rectangle in feet as {min:[x,y], max:[x,y]} in millimetres.</summary>
        public static JObject RectMm(double[] r)
            => new JObject
            {
                ["min"] = new JArray(Math.Round(r[0] * 304.8, 1), Math.Round(r[1] * 304.8, 1)),
                ["max"] = new JArray(Math.Round(r[2] * 304.8, 1), Math.Round(r[3] * 304.8, 1))
            };

        internal static JObject DescribeCrop(CropFrame frame, double[] box, bool planLike, int scale)
        {
            JArray Mm(double[] values) => new JArray(values.Select(value => Math.Round(value / FeetPerMm, 1)));
            double w = box[3] - box[0], h = box[4] - box[1];
            return new JObject
            {
                ["model_min"] = Mm(frame.ToModel(box.Take(3).ToArray())),
                ["model_max"] = Mm(frame.ToModel(box.Skip(3).ToArray())),
                ["convention"] = planLike ? "model_xy" : "view_axes",
                ["rect"] = RectMm(InCallerTerms(frame, new[] { box[0], box[1], box[3], box[4] }, planLike, frame.Origin[2])),
                ["size_mm"] = Mm(new[] { w, h }),
                ["paper_mm"] = scale > 0 ? new JArray(Math.Round(PaperMm(w, scale), 1), Math.Round(PaperMm(h, scale), 1)) : null,
                ["rotated"] = planLike && Math.Abs(Math.Abs(frame.X[0]) - 1) > 1e-6
            };
        }

        internal static void DescribeSectionClippedCrop(JObject result, CropFrame view, double[] cameraBox,
            bool cameraActive, CropFrame section, double[] sectionBox, int scale)
        {
            double[] rect = cameraActive ? new[] { cameraBox[0], cameraBox[1], cameraBox[3], cameraBox[4] } : null;
            double[] bounds = ClippedSectionBounds(view, section, sectionBox, rect);
            if (cameraActive) result["camera_crop"] = result["crop"]?.DeepClone();
            if (bounds == null)
            {
                result["crop"] = JValue.CreateNull();
                result["effective_crop"] = JValue.CreateNull();
                result["effective_crop_status"] = "empty";
                return;
            }
            JObject effective = DescribeCrop(view, bounds, false, scale);
            effective["source"] = cameraActive ? "clipped_section_box_bounds" : "section_box_bounds";
            effective["depth_mm"] = Math.Round((bounds[5] - bounds[2]) / FeetPerMm, 1);
            result["effective_crop"] = effective;
            if (cameraActive) result["crop"] = effective.DeepClone();
        }

        private static bool Finite(double v) => !double.IsNaN(v) && !double.IsInfinity(v);

        // ---- request shape ---------------------------------------------------------------

        private static readonly string[] Common = { "target_document", "operation", "view_id", "dry_run", "confirmation_token", "idempotency_key" };

        public static readonly Dictionary<string, string[]> Fields = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["read"] = new string[0],
            ["read_section_box"] = new string[0],
            ["section_box_active"] = new[] { "value", "allow_unmarked_view" },
            ["set_section_box"] = new[] { "min", "max", "keep_transform", "allow_unmarked_view" },
            ["set_crop_shape"] = new[] { "points", "allow_unmarked_view" },
            ["reset_crop_shape"] = new[] { "allow_unmarked_view" },
            ["set_crop_box"] = new[] { "min", "max", "depth", "allow_unmarked_view" },
            ["set_crop_from_elements"] = new[] { "element_ids", "margin", "allow_unmarked_view" },
            ["set_crop_from_room"] = new[] { "room_id", "margin", "allow_unmarked_view" },
            ["set_annotation_crop_offsets"] = new[] { "offsets", "allow_unmarked_view" },
            ["crop_active"] = new[] { "value", "allow_unmarked_view" },
            ["crop_visible"] = new[] { "value", "allow_unmarked_view" }
        };

        /// <summary>Null when the request is well formed for its operation; otherwise why not.</summary>
        public static string Validate(JObject r)
        {
            if (r == null) return "parameters must be a JSON object.";
            string op = (r.Value<string>("operation") ?? "").Trim().ToLowerInvariant();
            if (!Fields.TryGetValue(op, out string[] own))
                return "operation must be one of " + string.Join(", ", Fields.Keys) + " for horizun_view_crop.";
            var allowed = new HashSet<string>(Common.Concat(own), StringComparer.Ordinal);
            foreach (JProperty p in r.Properties())
                if (!allowed.Contains(p.Name)) return p.Name + " is not applicable to operation '" + op + "'. Nothing ran.";
            if (r["view_id"]?.Type != JTokenType.Integer) return "view_id is required.";
            if (op == "set_section_box")
            {
                if (!Triple(r["min"]) || !Triple(r["max"])) return "min and max must each be [x, y, z] finite mm in internal model coordinates.";
                if (Enumerable.Range(0, 3).Any(i => r["max"][i].Value<double>() <= r["min"][i].Value<double>())) return "max must exceed min on all three axes.";
                if (r["keep_transform"] != null && r["keep_transform"].Type != JTokenType.Boolean) return "keep_transform must be a boolean.";
            }
            if (op == "set_crop_box")
            {
                if (!Pair(r["min"]) || !Pair(r["max"])) return "min and max are required, each [x, y] in mm.";
                if (r["max"][0].Value<double>() <= r["min"][0].Value<double>() || r["max"][1].Value<double>() <= r["min"][1].Value<double>())
                    return "max must exceed min on both axes.";
                if (r["depth"] != null && !(Number(r["depth"]) && r.Value<double>("depth") > 0)) return "depth must be a positive number of mm.";
            }
            if (op == "set_crop_from_elements")
            {
                if (!(r["element_ids"] is JArray ids) || ids.Count == 0 || ids.Count > 500 || ids.Any(t => t.Type != JTokenType.Integer))
                    return "element_ids must list 1..500 element ids.";
            }
            if (op == "set_crop_from_room" && r["room_id"]?.Type != JTokenType.Integer) return "room_id is required.";
            if (r["margin"] != null && !(Number(r["margin"]) && r.Value<double>("margin") >= 0)) return "margin must be a non-negative number of mm.";
            if (op == "set_annotation_crop_offsets")
            {
                if (!(r["offsets"] is JArray o) || o.Count != 4 || o.Any(t => !Number(t) || t.Value<double>() < MinAnnotationOffsetMm))
                    return "offsets must be [left, right, top, bottom] in mm of paper, each >= 3.2 (Revit's minimum is 1/8 inch).";
            }
            if ((op == "crop_active" || op == "crop_visible" || op == "section_box_active") && r["value"]?.Type != JTokenType.Boolean) return "value (true or false) is required.";
            if (r["allow_unmarked_view"] != null && r["allow_unmarked_view"].Type != JTokenType.Boolean) return "allow_unmarked_view must be a boolean.";
            return null;
        }

        public const int MaxPolygonVertices = Detail2DRules.MaxCurvesPerLoop;

        public static string ReadPolygon(JToken token, out double[][] vertices)
        {
            vertices = null;
            if (token is JArray supplied && supplied.Count > MaxPolygonVertices + 1)
                return "points accepts at most " + MaxPolygonVertices + " vertices plus the repeated first point.";
            if (!(token is JArray points) || points.Count < 4 ||
                points.Any(p => !Pair(p) || p.Any(c => !Finite(c.Value<double>()))))
                return "points must contain at least three finite [x, y] vertices in view-plane mm and the repeated first point.";

            var closed = points.Select(p => new[] { p[0].Value<double>(), p[1].Value<double>(), 0d }).ToArray();
            if (!closed[0].SequenceEqual(closed[closed.Length - 1]))
                return "points must close the polygon by repeating the first vertex exactly.";

            var open = closed.Take(closed.Length - 1).ToArray();
            string error = Detail2DRules.ValidateLoop(open);
            if (error != null) return "points: " + error;
            vertices = open.Select(p => new[] { p[0], p[1] }).ToArray();
            return null;
        }

        public static PostconditionCheck VerifyPolygon(double[][] requested, double[][] actual, bool shapeSet, bool split, bool active)
        {
            var check = new PostconditionCheck("shape_set", "split", "vertices", "crop_active");
            check.Compare("shape_set", true, shapeSet);
            check.Compare("split", false, split);
            check.Record("vertices", PolygonMm(requested), PolygonMm(actual), PolygonMatches(requested, actual, ToleranceFeet));
            check.Compare("crop_active", true, active);
            return check;
        }

        public static JArray PolygonMm(IEnumerable<double[]> vertices) =>
            new JArray(vertices.Select(p => new JArray(p[0] * 304.8, p[1] * 304.8)));

        public static bool PolygonMatches(IReadOnlyList<double[]> requested, IReadOnlyList<double[]> actual, double tolerance)
        {
            int n = requested.Count;
            if (n < 3 || actual.Count != n) return false;

            // Revit may choose a different starting edge and reverse the winding.
            for (int start = 0; start < n; start++)
            {
                foreach (int direction in new[] { 1, -1 })
                {
                    int i = 0;
                    for (; i < n; i++)
                    {
                        double[] read = actual[(start + direction * i + n) % n];
                        double dx = read[0] - requested[i][0], dy = read[1] - requested[i][1];
                        if (!(dx * dx + dy * dy <= tolerance * tolerance)) break;
                    }
                    if (i == n) return true;
                }
            }
            return false;
        }

        internal static bool Number(JToken t) => t != null && (t.Type == JTokenType.Integer || t.Type == JTokenType.Float);
        internal static bool Triple(JToken t) => t is JArray a && a.Count == 3 && a.All(x => Number(x) && Finite(x.Value<double>()));
        internal static bool Pair(JToken t) => t is JArray a && a.Count == 2 && a.All(Number);
    }
}
