// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// THE REVIT-FREE HALF of horizun_planimetry_ops: the request, and the arithmetic
// of each fix over plain numbers so it can be proved.
//
// field cycle (video 2026-10-24, P2/P6/P8/P9): grids half-way up the elevations and
// not reaching the other side, viewports sticking out of the frame, tags and
// texts on top of each other, and a legend of conventions that did not show the
// families the model uses. audit_planimetry finds some of these; nothing typed
// fixed them.
//
//   datum_extents        a grid or level's 2D extent in a view set to the crop along
//                        its own direction, plus a paper margin; bubbles as asked.
//   fit_viewport         the smallest move that brings a viewport (box + title) inside
//                        the title block less a margin; one that cannot fit is refused
//                        with the scale factor it would need.
//   resolve_overlaps     each overlapping tag or text moved off the other by the
//                        smaller of the two axis moves, plus a gap.
//   legend_from_families a legend duplicated from a source legend, one component per
//                        family type placed in the categories asked, each labelled.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public static class PlanimetryOpsRules
    {
        public static JObject LegendRegenerationEvidence() => new JObject
        {
            ["regenerations"] = 0,
            ["regeneration_scope"] = "explicit calls in legend apply only; Revit and the runner may regenerate separately"
        };
        private static readonly string[] Common = { "target_document", "operation", "dry_run", "confirmation_token", "transaction_name", "idempotency_key", "units" };

        public static readonly Dictionary<string, string[]> Fields = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["datum_extents"] = new[] { "view_ids", "datum_ids", "margin_mm", "bubbles" },
            ["stagger_grid_bubbles"] = new[] { "view_id", "grid_ids", "ends", "bubble_diameter_mm", "gap_mm", "max_offset_mm" },
            ["fit_viewport"] = new[] { "viewport_ids", "margin_mm" },
            ["resolve_overlaps"] = new[] { "view_id", "gap_mm" },
            ["legend_from_families"] = new[] { "source_legend_id", "name", "categories", "spacing_mm", "label" },
            ["move_note"] = NoteLeaderRules.Fields["move_note"],
            ["note_from_model"] = NoteLeaderRules.Fields["note_from_model"],
            ["clone_tag"] = NoteLeaderRules.Fields["clone_tag"],
            ["arrange_column"] = NoteLeaderRules.Fields["arrange_column"],
            ["set_leader_end"] = NoteLeaderRules.Fields["set_leader_end"],
            ["reseat_leaders"] = NoteLeaderRules.Fields["reseat_leaders"],
            ["required_notes"] = NoteLeaderRules.Fields["required_notes"],
            ["replace_text"] = NoteLeaderRules.Fields["replace_text"],
            ["sync_sheet_references"] = NoteLeaderRules.Fields["sync_sheet_references"]
        };

        public static string Validate(JObject r)
        {
            if (r == null) return "parameters must be a JSON object.";
            string op = (r.Value<string>("operation") ?? "").Trim().ToLowerInvariant();
            if (!Fields.TryGetValue(op, out string[] own)) return "operation must be one of " + string.Join(", ", Fields.Keys) + " for horizun_planimetry_ops.";
            var allowed = new HashSet<string>(Common.Concat(own), StringComparer.Ordinal);
            foreach (JProperty p in r.Properties())
                if (!allowed.Contains(p.Name)) return p.Name + " is not applicable to operation '" + op + "'. Nothing ran.";
            if (string.IsNullOrWhiteSpace(r.Value<string>("target_document"))) return "target_document is required.";
            string ids(string k, int max) => !(r[k] is JArray a) || a.Count == 0 || a.Count > max || a.Any(t => t.Type != JTokenType.Integer) ? k + " must list 1.." + max + " ids." : null;
            switch (op)
            {
                case "stagger_grid_bubbles":
                    if (r["view_id"]?.Type != JTokenType.Integer) return "view_id is required.";
                    if (r["grid_ids"] != null && ids("grid_ids", 500) != null) return ids("grid_ids", 500);
                    string ends = r.Value<string>("ends") ?? "both";
                    if (ends != "both" && ends != "end0" && ends != "end1") return "ends must be end0, end1 or both.";
                    foreach (string field in new[] { "bubble_diameter_mm", "max_offset_mm" })
                    {
                        JToken number = r[field];
                        if (number != null && (number.Type != JTokenType.Integer && number.Type != JTokenType.Float || !Finite(number.Value<double>()) || number.Value<double>() <= 0 || number.Value<double>() > 200)) return field + " must be >0 and <=200 paper mm.";
                    }
                    break;
                case "datum_extents":
                    string e = ids("view_ids", 50); if (e != null) return e;
                    if (r["datum_ids"] != null && ids("datum_ids", 500) != null) return ids("datum_ids", 500);
                    string b = r.Value<string>("bubbles");
                    if (b != null && b != "end0" && b != "end1" && b != "both" && b != "none") return "bubbles must be end0, end1, both or none.";
                    break;
                case "fit_viewport": e = ids("viewport_ids", 50); if (e != null) return e; break;
                case "resolve_overlaps": if (r["view_id"]?.Type != JTokenType.Integer) return "view_id is required."; break;
                case "move_note":
                case "note_from_model":
                case "clone_tag":
                case "arrange_column":
                case "set_leader_end":
                case "reseat_leaders":
                case "required_notes":
                case "replace_text":
                case "sync_sheet_references":
                    string noteError = NoteLeaderRules.ValidateWrite(op, r);
                    if (noteError != null) return noteError;
                    break;
                case "legend_from_families":
                    if (r["source_legend_id"]?.Type != JTokenType.Integer) return "source_legend_id (an existing legend with at least one legend component) is required: the API cannot create a legend from nothing.";
                    if (string.IsNullOrWhiteSpace(r.Value<string>("name"))) return "name is required.";
                    if (!(r["categories"] is JArray c) || c.Count == 0 || c.Count > 20 || c.Any(t => t.Type != JTokenType.String)) return "categories must list 1..20 OST_ names.";
                    string l = r.Value<string>("label") ?? "family_and_type";
                    if (l != "family_and_type" && l != "type_name" && l != "type_mark") return "label must be family_and_type, type_name or type_mark.";
                    break;
            }
            foreach (string k in new[] { "margin_mm", "gap_mm", "spacing_mm" })
            {
                JToken t = r[k];
                if (t != null && (t.Type != JTokenType.Integer && t.Type != JTokenType.Float || !Finite(t.Value<double>()) || t.Value<double>() < 0 || t.Value<double>() > 200)) return k + " must be between 0 and 200 (paper mm).";
            }
            return null;
        }

        /// <summary>The [tmin - margin, tmax + margin] range of points projected on a line (parameters along its unit direction).</summary>
        public static double[] Range(IEnumerable<double> ts, double margin)
        {
            var list = ts.ToList();
            return new[] { list.Min() - margin, list.Max() + margin };
        }

        /// <summary>The smallest (dx, dy) that brings box [minX,minY,maxX,maxY] inside usable; null when it is larger than usable.</summary>
        public static double[] FitShift(double[] box, double[] usable)
        {
            double w = box[2] - box[0], h = box[3] - box[1];
            if (w > usable[2] - usable[0] + 1e-9 || h > usable[3] - usable[1] + 1e-9) return null;
            double dx = box[0] < usable[0] ? usable[0] - box[0] : box[2] > usable[2] ? usable[2] - box[2] : 0;
            double dy = box[1] < usable[1] ? usable[1] - box[1] : box[3] > usable[3] ? usable[3] - box[3] : 0;
            return new[] { dx, dy };
        }

        /// <summary>The scale factor a box needs to fit usable (&lt; 1 shrinks).</summary>
        public static double FitFactor(double[] box, double[] usable)
            => Math.Min((usable[2] - usable[0]) / (box[2] - box[0]), (usable[3] - usable[1]) / (box[3] - box[1]));

        public static bool Overlap(double[] a, double[] b) => a[0] < b[2] && b[0] < a[2] && a[1] < b[3] && b[1] < a[3];

        private static bool Finite(double n) => !double.IsNaN(n) && !double.IsInfinity(n);

        public static int BubbleOverlaps(IList<double[]> centers, double diameter)
        {
            int count = 0;
            for (int i = 0; i < centers.Count; i++) for (int j = i + 1; j < centers.Count; j++)
                if (Math.Pow(centers[i][0] - centers[j][0], 2) + Math.Pow(centers[i][1] - centers[j][1], 2) < diameter * diameter - 1e-12) count++;
            return count;
        }

        /// <summary>Move selected heads outward along their grid, preserving fixed obstacles. All values share one unit.</summary>
        public static double[] BubbleOffsets(IList<double[]> centers, IList<double[]> directions, IList<bool> movable, double diameter, double gap, double maxOffset)
        {
            if (!Finite(diameter) || diameter <= 0 || !Finite(gap) || gap < 0 || !Finite(maxOffset) || maxOffset <= 0) throw new ArgumentException("Invalid bubble spacing limits.");
            var offsets = new double[centers.Count];
            var placed = new List<double[]>();
            foreach (int index in Enumerable.Range(0, centers.Count).OrderBy(i => movable[i] ? 1 : 0))
            {
                double step = diameter + gap, offset = 0;
                double[] point = centers[index];
                while (placed.Any(p => Math.Pow(point[0] - p[0], 2) + Math.Pow(point[1] - p[1], 2) < step * step - 1e-12))
                {
                    if (!movable[index]) break;
                    offset += step;
                    if (offset > maxOffset + 1e-9) throw new ArgumentException("Grid bubbles cannot be separated within max_offset_mm. Nothing was written.");
                    point = new[] { centers[index][0] + directions[index][0] * offset, centers[index][1] + directions[index][1] * offset };
                }
                offsets[index] = offset;
                placed.Add(point);
            }
            return offsets;
        }

        /// <summary>The smaller axis move of b that clears a by gap; zero vector when they do not overlap.</summary>
        public static double[] Separate(double[] a, double[] b, double gap)
        {
            if (!Overlap(a, b)) return new[] { 0.0, 0.0 };
            double right = a[2] + gap - b[0], left = a[0] - gap - b[2], up = a[3] + gap - b[1], down = a[1] - gap - b[3];
            double dx = Math.Abs(right) < Math.Abs(left) ? right : left, dy = Math.Abs(up) < Math.Abs(down) ? up : down;
            return Math.Abs(dx) <= Math.Abs(dy) ? new[] { dx, 0.0 } : new[] { 0.0, dy };
        }
    }
}
