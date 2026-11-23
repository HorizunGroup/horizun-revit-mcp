// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code. Revit-free.
//
// horizun_query_planimetry mode=iso_inventory and mode=rasters read two small
// populations and take FEW arguments. The schema is shared by every mode, so a
// caller can send note_ids, points_mm, cooperative... and, before this rule, get a
// confident answer that ignored them (review m2). Anything outside this list is
// refused by name; nothing is accepted and silently ignored.
// -----------------------------------------------------------------------------
using System;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public static class PlanimetryInventoryArgs
    {
        public static readonly string[] Modes =
        { "inventory", "sheets", "views", "unplaced_views", "placements", "annotations", "references", "annotation_overlaps",
          "leader_targets", "annotation_staleness", "sheet_conflicts", "free_spot", "view_ray", "section_mark_diagnosis", "iso_inventory", "rasters" };

        public static string ParseMode(JObject request, out string mode)
        {
            mode = (request.Value<string>("mode") ?? "inventory").ToLowerInvariant();
            return Modes.Contains(mode, StringComparer.Ordinal) ? null : "mode must be one of: " + string.Join(", ", Modes) + ".";
        }

        /// <summary>Fields that only the note / section read modes use. The schema is shared, so an older mode must refuse them by name.</summary>
        public static readonly string[] NoteModeOnlyFields =
        { "section_view_id", "view_id", "note_ids", "points_mm", "changed_unique_ids", "deleted_unique_ids", "sheet_id", "size_mm", "near_mm",
          "gap_mm", "step_mm", "max_radius_mm", "include_linked_symbols" };

        /// <summary>Null when the request carries none of them; otherwise the sentence that refuses it (nothing is read).</summary>
        public static string IgnoredFieldRefusal(JObject request, string mode)
        {
            if (request == null) return null;
            var extra = request.Properties().Select(p => p.Name).Where(n => NoteModeOnlyFields.Contains(n, StringComparer.Ordinal)).OrderBy(n => n, StringComparer.Ordinal).ToList();
            if (extra.Count == 0) return null;
            return "mode=" + mode + " does not use " + string.Join(", ", extra) + " (they belong to the note and section read modes). Refused rather than accepted and ignored. Nothing was read.";
        }

        public static readonly string[] Allowed = { "mode", "sheet_ids", "view_ids", "units", "max_rows", "cursor", "time_budget_s" };

        /// <summary>Null when every argument is one the mode reads; otherwise the sentence that refuses the call.</summary>
        public static string Refusal(JObject request, string mode)
        {
            if (request == null) return null;
            var extra = request.Properties().Select(p => p.Name)
                .Where(n => !Allowed.Contains(n, StringComparer.Ordinal)).OrderBy(n => n, StringComparer.Ordinal).ToList();
            if (extra.Count == 0) return null;
            return "mode=" + mode + " takes " + string.Join(", ", Allowed.Where(a => a != "mode")) + " only. Refused rather than " +
                   "accepted and ignored: " + string.Join(", ", extra) + ". Nothing was read.";
        }
    }
}
