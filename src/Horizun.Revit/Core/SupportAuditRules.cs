// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// THE REVIT-FREE HALF of horizun_audit_support (read-only): what each mode takes
// and the judgements it makes, over boxes and lines so they can be proved.
//
// field cycle (video 2026-10-24, F3/A7/H4/E1/L1): a floating patio roof at +13.60,
// a slab missing under a storey, pipes and fixtures in the air, the EST model
// "corrido" against ARQ - every one of them was found by LOOKING at the video,
// none by a tool.
//
// Modes:
//   floating           elements with nothing under (or, within above_tolerance, over)
//                      them: a support's box must overlap the element's box in plan and
//                      reach its bottom within tolerance (or contain it).
//   loose_beam_ends  structural framing ends that land in no column, wall, beam or
//                      foundation box (expanded by tolerance).
//   coverage_gaps      rooms whose floor area is not covered by a floor top face at the
//                      room's level: sampled on a grid inside the room.
//   model_offset       grids of the host against the same-named grids of a link: the
//                      offset of each, the median and p95, the rotation and the least-
//                      squares translation that would align them.
// METHOD, said in every reply: box-level for floating and beams - a box that
// overlaps is counted as support, so a diagonal wall's box can hide a real gap
// (false "supported"), never invent one.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public static class SupportAuditRules
    {
        public static readonly string[] Modes = { "floating", "slab_supports", "loose_beam_ends", "coverage_gaps", "model_offset" };
        public const int MaxElements = 5000;
        public const int MaxRows = 500;

        /// <summary>Use the union of positive-volume solid boxes, falling back only when none exist.</summary>
        public static double[] ElementBox(IEnumerable<double[]> solids, double[] fallback, out bool usedFallback)
        {
            var boxes = solids.ToList();
            usedFallback = boxes.Count == 0;
            if (usedFallback) return fallback;
            return new[] { boxes.Min(b => b[0]), boxes.Min(b => b[1]), boxes.Min(b => b[2]),
                boxes.Max(b => b[3]), boxes.Max(b => b[4]), boxes.Max(b => b[5]) };
        }

        /// <summary>A ground assumption, not a measurement of terrain.</summary>
        public static bool OnGround(bool isFloorOrFoundation, double bottom, double? lowestLevel)
            => isFloorOrFoundation && lowestLevel.HasValue && bottom <= lowestLevel.Value;

        public static readonly Dictionary<string, string[]> Fields = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["floating"] = new[] { "categories", "level_id", "tolerance_mm", "above_tolerance_mm", "include_links" },
            ["slab_supports"] = new[] { "categories", "level_id", "tolerance_mm", "include_links" },
            ["loose_beam_ends"] = new[] { "level_id", "tolerance_mm", "include_links" },
            ["coverage_gaps"] = new[] { "level_id", "room_ids", "grid_mm", "include_links" },
            ["model_offset"] = new[] { "link_instance_id", "tolerance_mm" }
        };

        public static string Validate(JObject r)
        {
            if (r == null) return "parameters must be a JSON object.";
            string mode = r.Value<string>("mode");
            if (mode == null || !Fields.TryGetValue(mode, out string[] own)) return "mode must be one of " + string.Join(", ", Modes) + ".";
            foreach (JProperty p in r.Properties())
                if (p.Name != "mode" && p.Name != "target_document" && Array.IndexOf(own, p.Name) < 0)
                    return p.Name + " is not applicable to mode '" + mode + "'. Nothing ran.";
            if (mode == "floating" && (!(r["categories"] is JArray c) || c.Count == 0 || c.Count > 10 || c.Any(t => t.Type != JTokenType.String)))
                return "floating needs categories: 1..10 OST_ names (e.g. OST_Roofs, OST_PipeCurves, OST_PlumbingFixtures).";
            if (mode == "slab_supports" && r["categories"] != null && (!(r["categories"] is JArray sc) || sc.Count == 0 || sc.Count > 4 || sc.Any(t => t.Type != JTokenType.String || !new[] { "OST_Floors", "OST_StructuralFraming", "OST_StructuralColumns", "OST_StructuralFoundation" }.Contains((string)t))))
                return "slab_supports categories must contain 1..4 structural OST_ names.";
            if (mode == "coverage_gaps" && r["level_id"] == null && r["room_ids"] == null) return "coverage_gaps needs level_id or room_ids.";
            if (mode == "model_offset" && r["link_instance_id"]?.Type != JTokenType.Integer) return "model_offset needs link_instance_id.";
            foreach (string k in new[] { "tolerance_mm", "above_tolerance_mm", "grid_mm" })
            {
                JToken t = r[k];
                if (t != null && (t.Type != JTokenType.Integer && t.Type != JTokenType.Float || t.Value<double>() < 0 || t.Value<double>() > 5000)) return k + " must be between 0 and 5000.";
            }
            if (r["grid_mm"] != null && r.Value<double>("grid_mm") < 100) return "grid_mm must be at least 100.";
            return null;
        }

        /// <summary>Boxes are [minX,minY,minZ,maxX,maxY,maxZ].</summary>
        public static bool PlanOverlap(double[] a, double[] b) => a[0] <= b[3] && b[0] <= a[3] && a[1] <= b[4] && b[1] <= a[4];

        /// <summary>Is the element's box carried by the support's box: from below (top reaches its bottom within tol, or the
        /// support contains its bottom), or from above within aboveTol.</summary>
        public static bool Supports(double[] support, double[] e, double tol, double aboveTol)
        {
            if (!PlanOverlap(support, e)) return false;
            double bottom = e[2], top = e[5];
            if (bottom >= support[2] - tol && bottom <= support[5] + tol) return true;
            return aboveTol > 0 && support[2] >= top - tol && support[2] <= top + aboveTol;
        }

        /// <summary>The vertical gap from the element's bottom down to the nearest support top under it in plan; null when nothing is under it.</summary>
        public static double? GapBelow(IEnumerable<double[]> supports, double[] e)
        {
            double? best = null;
            foreach (double[] s in supports)
            {
                if (!PlanOverlap(s, e) || s[5] > e[2]) continue;
                double gap = e[2] - s[5];
                if (best == null || gap < best) best = gap;
            }
            return best;
        }

        public static bool Contains(double[] box, double[] p, double tol)
            => p[0] >= box[0] - tol && p[0] <= box[3] + tol && p[1] >= box[1] - tol && p[1] <= box[4] + tol && p[2] >= box[2] - tol && p[2] <= box[5] + tol;

        /// <summary>Least-squares translation (dx, dy) with n_i . d = offset_i; null when the normals do not span the plan
        /// (all grids parallel: only one direction can be measured).</summary>
        public static double[] Translation(IList<double[]> normals, IList<double> offsets)
        {
            double a = 0, b = 0, c = 0, u = 0, v = 0;
            for (int i = 0; i < normals.Count; i++)
            {
                double nx = normals[i][0], ny = normals[i][1];
                a += nx * nx; b += nx * ny; c += ny * ny; u += nx * offsets[i]; v += ny * offsets[i];
            }
            double det = a * c - b * b;
            if (Math.Abs(det) < 1e-6 * Math.Max(1, a + c)) return null;
            return new[] { (c * u - b * v) / det, (a * v - b * u) / det };
        }

        public static double Percentile(IList<double> values, double p)
        {
            if (values == null || values.Count == 0) return double.NaN;
            var s = values.OrderBy(x => x).ToList();
            double rank = p * (s.Count - 1);
            int lo = (int)Math.Floor(rank), hi = (int)Math.Ceiling(rank);
            return s[lo] + (s[hi] - s[lo]) * (rank - lo);
        }
    }
}
