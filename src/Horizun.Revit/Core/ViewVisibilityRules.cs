// -----------------------------------------------------------------------------
// Horizun Core - original Horizun code. Revit-free.
//
// horizun_view_visibility's argument rules (FIELD FEEDBACK 2.6): what
// each operation needs, refused before Revit is touched.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public static class ViewVisibilityRules
    {
        public static readonly string[] Operations =
            { "read", "hide", "unhide", "set_overrides", "clear_overrides", "hide_foreign_markers", "marker_scale", "copy_link_overrides", "datum_cleanup", "set_scale", "explain_overrides",
              "override_report", "link_datum_cleanup", "graphics_report", "copy_view_graphics" };

        public const int MaxIds = 2000;

        /// <summary>copy_link_overrides: at most this many target views per call.</summary>
        public const int MaxLinkTargets = 200;

        /// <summary>The operations that read family_names / type_names.</summary>
        public static readonly string[] NameSelectorOperations = { "hide", "unhide", "set_overrides", "clear_overrides", "explain_overrides", "override_report" };

        /// <summary>override_report: at most this many views per call.</summary>
        public const int MaxReportViews = 200;

        internal static bool StampGraphicsCoverage(JArray views)
        {
            foreach (JObject row in views)
                row["complete"] = ((JArray)row["errors"]).Count == 0 &&
                                  ((row["category_warnings"] as JArray)?.Count ?? 0) == 0;
            return views.All(row => row.Value<bool>("complete"));
        }

        public static string[] GraphicsIncludes(JObject r)
        {
            if (r["include"] == null) return new[] { "filters", "categories" };
            if (!(r["include"] is JArray a) || a.Count < 1 || a.Any(t => t.Type != JTokenType.String)) return null;
            string[] values = a.Values<string>().ToArray();
            return values.Distinct().Count() != values.Length || values.Any(v => !new[] { "filters", "categories", "elements" }.Contains(v)) ? null : values;
        }

        public static string Validate(JObject r)
        {
            string op = (r.Value<string>("operation") ?? "").Trim().ToLowerInvariant();
            if (!Operations.Contains(op)) return "operation must be one of " + string.Join(", ", Operations) + ".";
            if (op != "copy_view_graphics" && r["include"] != null) return "include applies only to copy_view_graphics.";
            if (op == "copy_view_graphics")
            {
                if (GraphicsIncludes(r) == null) return "include must be a non-empty, unique list from filters, categories, elements.";
                if (r["view_id"]?.Type != JTokenType.Integer || (long)r["view_id"] < 1 || !(r["view_ids"] is JArray destinations) || destinations.Count < 1 || destinations.Count > MaxLinkTargets ||
                    destinations.Any(t => t.Type != JTokenType.Integer || (long)t < 1 || (long)t == (long)r["view_id"]))
                    return "copy_view_graphics needs a source view_id and 1..200 other destination view_ids.";
                if (r["element_ids"] != null && (!GraphicsIncludes(r).Contains("elements") || !(r["element_ids"] is JArray elements) || elements.Count > MaxIds || elements.Any(t => t.Type != JTokenType.Integer || (long)t < 1)))
                    return "element_ids needs include=elements and at most 2000 positive ids.";
                if (GraphicsIncludes(r).Contains("elements") && (!(r["element_ids"] is JArray selected) || selected.Count == 0))
                    return "include=elements requires explicit element_ids.";
                foreach (var p in r.Properties())
                    if (!new[] { "operation", "view_id", "view_ids", "include", "element_ids", "target_document", "target_document_title", "dry_run", "confirmation_token", "idempotency_key", "units" }.Contains(p.Name))
                        return p.Name + " does not apply to copy_view_graphics.";
                return null;
            }
            if (op == "graphics_report")
            {
                if (!(r["view_ids"] is JArray reportViews) || reportViews.Count < 1 || reportViews.Count > MaxReportViews ||
                    reportViews.Any(t => t.Type != JTokenType.Integer || (long)t < 1))
                    return "graphics_report needs 1..200 integer view_ids.";
                foreach (var p in r.Properties())
                    if (!new[] { "operation", "view_ids", "target_document", "target_document_title", "dry_run", "confirmation_token", "idempotency_key", "units" }.Contains(p.Name))
                        return p.Name + " does not apply to graphics_report.";
                return null;
            }
            int ids = (r["element_ids"] as JArray)?.Count ?? 0;
            int cats = (r["categories"] as JArray)?.Count ?? 0;
            int views = (r["view_ids"] as JArray)?.Count ?? 0;
            int names = (r["family_names"] as JArray)?.Count ?? 0, typeNames = (r["type_names"] as JArray)?.Count ?? 0;
            string selector = NameSelectorError(r);
            if (selector != null) return selector;
            // Review m3: family_names / type_names select elements for these operations only. Accepted and ignored by the
            // others (datum_cleanup, set_scale, marker_scale, copy_link_overrides, read) was a silent no-op.
            if (!NameSelectorOperations.Contains(op) &&
                ((r["family_names"] != null && r["family_names"].Type != JTokenType.Null) || (r["type_names"] != null && r["type_names"].Type != JTokenType.Null)))
                return "family_names / type_names do not apply to " + op + "; they select elements for " + string.Join(", ", NameSelectorOperations) +
                       ". Nothing was written.";
            if (ids > MaxIds) return "element_ids holds at most " + MaxIds + " ids.";
            // 5.2.1: the template choice and the name separator belong to link_datum_cleanup only; elsewhere they were a silent no-op.
            if (op != "link_datum_cleanup")
                foreach (string f in new[] { "template_mode", "name_separator" })
                    if (r[f] != null && r[f].Type != JTokenType.Null) return f + " applies to link_datum_cleanup only.";
            // P5: the print policy and the category clean-up belong to set_overrides only.
            if (op != "set_overrides")
                foreach (string f in new[] { "unprinted", "clear_category_overrides" })
                    if (r[f] != null && r[f].Type != JTokenType.Null) return f + " applies to set_overrides only. Nothing was written.";
            if (r["unprinted"] != null && r["unprinted"].Type != JTokenType.Null &&
                !OverridePrintRules.Policies.Contains(r["unprinted"].Type == JTokenType.String ? r.Value<string>("unprinted") : ""))
                return "unprinted must be one of " + string.Join(", ", OverridePrintRules.Policies) + ".";
            if (r["clear_category_overrides"] != null && r["clear_category_overrides"].Type != JTokenType.Null && r["clear_category_overrides"].Type != JTokenType.Boolean)
                return "clear_category_overrides must be a boolean.";
            if (op == "override_report")
            {
                if (views == 0 || views > MaxReportViews) return "override_report needs view_ids: 1.." + MaxReportViews + " views to report on.";
                if (ids == 0 && names == 0 && typeNames == 0) return "override_report needs element_ids, family_names and/or type_names.";
                foreach (string f in new[] { "view_id", "categories", "coarser_than" })
                    if (r[f] != null && r[f].Type != JTokenType.Null) return f + " does not apply to override_report, which only reads. Nothing was written.";
                if (r["overrides"] != null && r["overrides"].Type != JTokenType.Null)
                    return r["overrides"] is JObject o ? OverrideKeysError(o) : "overrides must be an object.";
                return null;
            }
            if (op == "marker_scale")
            {
                if (views == 0) return "marker_scale needs view_ids: the OWN views whose markers to hide at coarser scales.";
                int? s = r.Value<int?>("coarser_than");
                if (!s.HasValue || s.Value < 1 || s.Value > 24000) return "marker_scale needs coarser_than, a scale 1..24000 (1 hides the marker in every view coarser than 1:1).";
                return null;
            }
            if (op == "copy_link_overrides")
            {
                if (r.Value<long?>("view_id") == null)
                    return "copy_link_overrides needs view_id: the SOURCE view whose RVT Links visibility/graphics to copy.";
                if (views == 0) return "copy_link_overrides needs view_ids: the OWN target views that receive the source's RVT Links settings.";
                if (views > MaxLinkTargets) return "copy_link_overrides takes at most " + MaxLinkTargets + " target views per call.";
                long source = r.Value<long>("view_id");
                if (((JArray)r["view_ids"]).Any(t => t.Type == JTokenType.Integer && t.Value<long>() == source))
                    return "view_ids must not contain the source view_id " + source + ".";
                foreach (string f in new[] { "element_ids", "overrides", "coarser_than" })
                    if (r[f] != null && r[f].Type != JTokenType.Null)
                        return f + " does not apply to copy_link_overrides (categories may name the categories to count as evidence). Nothing was written.";
                return null;
            }
            if (r.Value<long?>("view_id") == null) return op + " needs view_id.";
            if (op == "datum_cleanup")
            {
                string why;
                if (ViewDatumRules.ParseKinds(r["datum_kinds"], out why) == null) return why;
                if (ViewDatumRules.ParseCriteria(r["criteria"], out why) == null) return why;
                if (r["keep_ids"] != null && (!(r["keep_ids"] is JArray keep) || keep.Any(t => t.Type != JTokenType.Integer) || keep.Count > MaxIds))
                    return "keep_ids must be an array of at most " + MaxIds + " integer element ids.";
                foreach (string f in new[] { "element_ids", "categories", "overrides", "view_ids", "coarser_than", "scale", "rehide_foreign_markers" })
                    if (r[f] != null && r[f].Type != JTokenType.Null)
                        return f + " does not apply to datum_cleanup (use keep_ids to protect datums). Nothing was written.";
                return null;
            }
            if (op == "link_datum_cleanup")
            {
                string why;
                if (ViewDatumRules.ParseKinds(r["datum_kinds"], out why) == null) return why;
                if (LinkDatumFilterRules.ParseTemplateMode(r["template_mode"], out why) == null) return why;
                if (r["name_separator"] != null && r["name_separator"].Type != JTokenType.Null && r["name_separator"].Type != JTokenType.Boolean)
                    return "name_separator must be a boolean.";
                foreach (string f in new[] { "element_ids", "categories", "overrides", "view_ids", "coarser_than", "scale", "criteria", "keep_ids", "rehide_foreign_markers" })
                    if (r[f] != null && r[f].Type != JTokenType.Null)
                        return f + " does not apply to link_datum_cleanup (datum_kinds chooses grids and/or levels). Nothing was written.";
                return null;
            }
            if (op == "set_scale")
            {
                string why = ViewDatumRules.ScaleError(r["scale"] != null && r["scale"].Type == JTokenType.Integer ? r.Value<long>("scale") : (long?)null);
                if (why != null) return why;
                if (r["rehide_foreign_markers"] != null && r["rehide_foreign_markers"].Type != JTokenType.Boolean)
                    return "rehide_foreign_markers must be a boolean.";
                foreach (string f in new[] { "element_ids", "categories", "overrides", "view_ids", "coarser_than", "datum_kinds", "criteria", "keep_ids" })
                    if (r[f] != null && r[f].Type != JTokenType.Null)
                        return f + " does not apply to set_scale. Nothing was written.";
                return null;
            }
            if (op == "explain_overrides")
            {
                if (ids == 0 && cats == 0 && names == 0 && typeNames == 0) return "explain_overrides needs element_ids, categories, family_names and/or type_names.";
                foreach (string f in new[] { "overrides", "view_ids", "coarser_than" })
                    if (r[f] != null && r[f].Type != JTokenType.Null) return f + " does not apply to explain_overrides, which only reads.";
                return null;
            }
            if (op == "hide" || op == "unhide" || op == "set_overrides" || op == "clear_overrides")
            {
                if (ids == 0 && cats == 0 && names == 0 && typeNames == 0) return op + " needs element_ids, categories, family_names and/or type_names.";
                if (op == "set_overrides" && !(r["overrides"] is JObject))
                    return "set_overrides needs overrides (line_color, line_weight, line_pattern, halftone, transparency, surface_color, cut_color, cut_line_color).";
                if (op == "set_overrides")
                {
                    string keys = OverrideKeysError((JObject)r["overrides"]);
                    if (keys != null) return keys;
                    if (!((JObject)r["overrides"]).Properties().Any())
                        return "set_overrides needs at least one override field.";
                }
            }
            return null;
        }

        public static readonly string[] OverrideKeys =
            { "line_color", "projection_line_color", "line_weight", "line_pattern", "halftone", "transparency", "surface_color", "surface_pattern", "cut_color", "cut_pattern", "cut_line_color" };

        /// <summary>Unknown or contradictory override keys; null when the object is well formed.</summary>
        public static string OverrideKeysError(JObject o)
        {
            var unknown = o.Properties().FirstOrDefault(p => !OverrideKeys.Contains(p.Name));
            if (unknown != null)
                return "Unknown override '" + unknown.Name + "'; use line_color for the projection line. Nothing was written.";
            if (o["line_color"] != null && o["projection_line_color"] != null)
                return "Specify line_color or projection_line_color, not both. Nothing was written.";
            return null;
        }

        public const int MaxNames = 50;

        /// <summary>family_names / type_names: lists of up to 50 plain names (4.2-N4, CT-P2-2c). Null when well formed.</summary>
        public static string NameSelectorError(JObject r)
        {
            foreach (string f in new[] { "family_names", "type_names" })
            {
                if (r[f] == null || r[f].Type == JTokenType.Null) continue;
                if (!(r[f] is JArray a) || a.Count == 0 || a.Count > MaxNames || a.Any(t => t.Type != JTokenType.String || string.IsNullOrWhiteSpace(t.Value<string>())))
                    return f + " must be a list of 1.." + MaxNames + " names.";
            }
            if (r["family_names"] != null && r["type_names"] != null && r["family_names"].Type != JTokenType.Null && r["type_names"].Type != JTokenType.Null &&
                ((JArray)r["family_names"]).Count > 1)
                return "type_names narrows ONE family: give family_names with a single family, or type_names alone.";
            return null;
        }

        /// <summary>
        /// Does a type belong to the selection? family_names matches the type's family ("Basic Wall", "M_Door-Single");
        /// type_names its own name; both given: both must match. Case-insensitive, trimmed.
        /// </summary>
        public static bool SelectsType(string familyName, string typeName, IList<string> families, IList<string> types)
        {
            bool famOk = families == null || families.Count == 0 || families.Any(f => string.Equals(f.Trim(), (familyName ?? "").Trim(), StringComparison.OrdinalIgnoreCase));
            bool typeOk = types == null || types.Count == 0 || types.Any(t => string.Equals(t.Trim(), (typeName ?? "").Trim(), StringComparison.OrdinalIgnoreCase));
            return famOk && typeOk && ((families != null && families.Count > 0) || (types != null && types.Count > 0));
        }

        /// <summary>
        /// copy_link_overrides postcondition: the link settings the API CAN read, compared key by
        /// key between the source view and a target. Returns the keys that differ (empty = same).
        /// A key missing on one side is a difference; a null on both sides is equal.
        /// </summary>
        public static List<string> LinkSettingsDiffer(JObject source, JObject target)
        {
            var keys = new SortedSet<string>(StringComparer.Ordinal);
            if (source != null) foreach (var p in source.Properties()) keys.Add(p.Name);
            if (target != null) foreach (var p in target.Properties()) keys.Add(p.Name);
            return keys.Where(k => !JToken.DeepEquals(source?[k] ?? JValue.CreateNull(), target?[k] ?? JValue.CreateNull())).ToList();
        }

        /// <summary>
        /// copy_link_overrides evidence, never a postcondition: categories whose link elements are
        /// visible (count > 0) in one view and not the other. Counts legitimately differ between
        /// views (level, crop, view range); presence of a category the source hides is the signal.
        /// </summary>
        public static JArray CategoryPresenceDiffers(JObject sourceCounts, JObject targetCounts)
        {
            var keys = new SortedSet<string>(StringComparer.Ordinal);
            if (sourceCounts != null) foreach (var p in sourceCounts.Properties()) keys.Add(p.Name);
            if (targetCounts != null) foreach (var p in targetCounts.Properties()) keys.Add(p.Name);
            var rows = new JArray();
            foreach (string k in keys)
            {
                long s = sourceCounts?.Value<long?>(k) ?? 0, t = targetCounts?.Value<long?>(k) ?? 0;
                if ((s > 0) != (t > 0)) rows.Add(new JObject { ["category"] = k, ["visible_in_source"] = s, ["visible_in_target"] = t });
            }
            return rows;
        }
    }
}
