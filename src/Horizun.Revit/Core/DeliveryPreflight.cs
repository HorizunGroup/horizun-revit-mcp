// -----------------------------------------------------------------------------
// Horizun Revit MCP — original Horizun code.
//
// The delivery profile preflight: every predictable error of every stage,
// found BEFORE the first write, and reported by stage and field.
//
// DeliveryPlan.Build proves the profile's skeleton. The arguments INSIDE each
// stage used to be validated only when that stage ran - so a wrong selector in
// the last view's dimension set surfaced after the first view had already been
// annotated. This file walks the same argument rules the stage commands apply,
// statically, over the whole profile, and returns every finding at once.
//
// Two honesty rules:
//   * this is not the stage's own state check. The model can change after the
//     preflight; every writer still rehearses against the document it finds.
//   * what a preflight CANNOT decide is listed as undetermined, with the stage
//     that will decide it, instead of being counted as checked.
//
// Revit-free: the host adds existence/compatibility findings from the
// document through the same finding shape (see PlanViewsCommand).
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public static class DeliveryPreflight
    {
        public const string Schema = "horizun.delivery-preflight/1";

        private static readonly string[] SetKeys =
        {
            "role", "operation", "element_ids", "reference_targets", "selector", "axis", "side", "offset",
            "dimension_type_id", "chain_separation", "probe_point", "distance_space", "link_instance_id"
        };
        private static readonly string[] TagKeys =
        {
            "element_ids", "tag_type_id", "tag_mode", "orientation", "add_leader", "skip_existing",
            "clearance", "max_displacement", "distance_space", "accept_unmeasurable"
        };
        private static readonly string[] PackingKeys = { "sheet_id", "sheets", "items", "margin", "gap", "tolerance" };
        private static readonly string[] SheetKeys = { "sheet_id", "usable_rect", "reserved_zones", "margin", "gap", "tolerance" };
        private static readonly string[] ItemKeys = { "key", "view_id", "schedule_id" };
        private static readonly string[] PublicationKeys =
        {
            "format", "view_ids", "output_path", "overwrite", "pdf_combine", "emit_manifest", "pdf_print", "units"
        };

        public static JObject Finding(string stage, string field, string code, string message)
        {
            return new JObject { ["stage"] = stage, ["field"] = field, ["code"] = code, ["message"] = message };
        }

        /// <summary>
        /// The static preflight of a whole profile. hostYear gates year-bound
        /// options (pdf_print.export_in_background). Never throws for a profile
        /// error: every error is a finding.
        /// </summary>
        public static JObject Static(JObject profile, int hostYear)
        {
            var errors = new List<JObject>();
            var undetermined = new List<JObject>();
            var checkedItems = new List<string>();
            if (profile == null)
            {
                errors.Add(Finding("profile", "delivery_profile", "missing", "delivery_profile is required."));
                return Result(errors, undetermined, checkedItems);
            }
            string units = profile.Value<string>("units") ?? "mm";

            // ---- views: dimension sets and tags ------------------------------------
            if (profile["views"] is JArray views)
            {
                foreach (JToken vt in views)
                {
                    JObject v = vt as JObject;
                    if (v == null) continue;
                    string viewId = v["view_id"]?.ToString() ?? "?";
                    if (v["dimension_sets"] is JArray sets)
                    {
                        string stage = "dimensions_" + viewId;
                        var roles = new HashSet<string>(StringComparer.Ordinal);
                        for (int i = 0; i < sets.Count; i++)
                        {
                            JObject spec = sets[i] as JObject;
                            string at = "dimension_sets[" + i + "]";
                            if (spec == null) { errors.Add(Finding(stage, at, "type", "each dimension set must be an object")); continue; }
                            foreach (JProperty p in spec.Properties())
                                if (!SetKeys.Contains(p.Name))
                                    errors.Add(Finding(stage, at + "." + p.Name, "unknown_field",
                                        "unknown dimension set field; known: " + string.Join(", ", SetKeys)));
                            string role = spec.Value<string>("role");
                            if (string.IsNullOrWhiteSpace(role)) errors.Add(Finding(stage, at + ".role", "missing", "role is required and nonempty"));
                            else if (!roles.Add(role)) errors.Add(Finding(stage, at + ".role", "duplicate", "role '" + role + "' is used twice in this view"));
                            string op = spec.Value<string>("operation");
                            bool intent = op == "intent_dimension";
                            if (!intent && !AutoDimensionRules.KnownOperations.Contains(op))
                                errors.Add(Finding(stage, at + ".operation", "unknown",
                                    "operation must be intent_dimension or one of " + string.Join(", ", AutoDimensionRules.KnownOperations)));
                            if (!IsPositiveNumber(spec["offset"]))
                                errors.Add(Finding(stage, at + ".offset", "invalid", "offset must be a finite positive number in " + units));
                            if (!IsPositiveInteger(spec["dimension_type_id"]))
                                errors.Add(Finding(stage, at + ".dimension_type_id", "invalid", "dimension_type_id must be a positive element id"));
                            string side = spec.Value<string>("side");
                            if (side != "positive" && side != "negative")
                                errors.Add(Finding(stage, at + ".side", "invalid", "side must be positive or negative"));
                            if (spec["axis"] != null && !new[] { "auto", "horizontal", "vertical" }.Contains(spec.Value<string>("axis")))
                                errors.Add(Finding(stage, at + ".axis", "invalid", "axis must be auto, horizontal or vertical"));
                            if (spec["selector"] != null && !DimensionReferenceRules.KnownSelectors.Contains(spec.Value<string>("selector")))
                                errors.Add(Finding(stage, at + ".selector", "invalid",
                                    "selector must be one of " + string.Join(", ", DimensionReferenceRules.KnownSelectors)));
                            if (spec["distance_space"] != null && spec.Value<string>("distance_space") != "model" && spec.Value<string>("distance_space") != "paper")
                                errors.Add(Finding(stage, at + ".distance_space", "invalid", "distance_space must be model or paper"));
                            if (spec["chain_separation"] != null && !IsPositiveNumber(spec["chain_separation"]))
                                errors.Add(Finding(stage, at + ".chain_separation", "invalid", "chain_separation must be a finite positive number"));
                            if (spec["link_instance_id"] != null)
                                errors.Add(Finding(stage, at + ".link_instance_id", "refused",
                                    "linked datum references are refused by the dimension planner (measured: Revit rejects them); dimension linked geometry through explicit references instead"));
                            if (intent)
                            {
                                bool hasIds = spec["element_ids"] != null, hasTargets = spec["reference_targets"] != null;
                                if (hasIds == hasTargets)
                                    errors.Add(Finding(stage, at, "invalid", "intent_dimension takes exactly one of element_ids or reference_targets"));
                                if (hasIds) CheckIdList(errors, stage, at + ".element_ids", spec["element_ids"], 2, 32);
                                if (hasTargets)
                                {
                                    JArray targets = spec["reference_targets"] as JArray;
                                    if (targets == null || targets.Count < 2 || targets.Count > 32)
                                        errors.Add(Finding(stage, at + ".reference_targets", "invalid", "reference_targets takes 2..32 entries"));
                                    else
                                        for (int j = 0; j < targets.Count; j++)
                                        {
                                            JObject rt = targets[j] as JObject;
                                            string tat = at + ".reference_targets[" + j + "]";
                                            if (rt == null) { errors.Add(Finding(stage, tat, "type", "each reference target must be an object")); continue; }
                                            foreach (JProperty p in rt.Properties())
                                                if (p.Name != "element_id" && p.Name != "selector" && p.Name != "probe_point")
                                                    errors.Add(Finding(stage, tat + "." + p.Name, "unknown_field", "reference targets take element_id, selector and probe_point"));
                                            if (!IsPositiveInteger(rt["element_id"]))
                                                errors.Add(Finding(stage, tat + ".element_id", "invalid", "element_id must be a positive element id"));
                                            string sel = rt.Value<string>("selector");
                                            if (!DimensionReferenceRules.KnownSelectors.Contains(sel))
                                                errors.Add(Finding(stage, tat + ".selector", "invalid",
                                                    "selector must be one of " + string.Join(", ", DimensionReferenceRules.KnownSelectors)));
                                            bool needsProbe = sel == DimensionReferenceRules.SelectorNearestFace || sel == DimensionReferenceRules.SelectorFarthestFace;
                                            if (needsProbe && !IsPoint3(rt["probe_point"]))
                                                errors.Add(Finding(stage, tat + ".probe_point", "missing", sel + " requires probe_point [x,y,z]"));
                                            if (rt["probe_point"] != null && !IsPoint3(rt["probe_point"]))
                                                errors.Add(Finding(stage, tat + ".probe_point", "invalid", "probe_point must be [x,y,z] numbers"));
                                        }
                                }
                            }
                            else if (spec["element_ids"] != null) CheckIdList(errors, stage, at + ".element_ids", spec["element_ids"], 1, 500);
                            undetermined.Add(Finding(stage, at, "decided_by_rehearsal",
                                "reference availability, compatibility and the measured dimension value are proved by the annotate rehearsal in the displayed view"));
                        }
                    }
                    if (v["tags"] is JObject tags)
                    {
                        string stage = "tags_" + viewId;
                        foreach (JProperty p in tags.Properties())
                            if (!TagKeys.Contains(p.Name))
                                errors.Add(Finding(stage, "tags." + p.Name, "unknown_field", "unknown tags field; known: " + string.Join(", ", TagKeys)));
                        CheckIdList(errors, stage, "tags.element_ids", tags["element_ids"], 1, 500);
                        if (tags["tag_type_id"] != null && !IsPositiveInteger(tags["tag_type_id"]))
                            errors.Add(Finding(stage, "tags.tag_type_id", "invalid", "tag_type_id must be a positive element id"));
                        if (tags["tag_mode"] != null && !new[] { "by_category", "multi_category", "material" }.Contains(tags.Value<string>("tag_mode")))
                            errors.Add(Finding(stage, "tags.tag_mode", "invalid", "tag_mode must be by_category, multi_category or material"));
                        if (tags["orientation"] != null && !new[] { "horizontal", "vertical" }.Contains(tags.Value<string>("orientation")))
                            errors.Add(Finding(stage, "tags.orientation", "invalid", "orientation must be horizontal or vertical"));
                        foreach (string flag in new[] { "add_leader", "skip_existing" })
                            if (tags[flag] != null && tags[flag].Type != JTokenType.Boolean)
                                errors.Add(Finding(stage, "tags." + flag, "invalid", flag + " must be a boolean"));
                        foreach (string dist in new[] { "clearance", "max_displacement" })
                            if (tags[dist] != null && !IsNonNegativeNumber(tags[dist]))
                                errors.Add(Finding(stage, "tags." + dist, "invalid", dist + " must be a finite non-negative number in " + units));
                        if (tags["distance_space"] != null && tags.Value<string>("distance_space") != "model" && tags.Value<string>("distance_space") != "paper")
                            errors.Add(Finding(stage, "tags.distance_space", "invalid", "distance_space must be model or paper"));
                        try { AnnotationVisibility.ReadAccepted(tags["accept_unmeasurable"]); }
                        catch (ArgumentException ex) { errors.Add(Finding(stage, "tags.accept_unmeasurable", "invalid", ex.Message)); }
                        undetermined.Add(Finding(stage, "tags", "decided_by_rehearsal",
                            "tag family compatibility, readable tag text, annotation coverage and collision-free placement are proved by the annotate rehearsal"));
                    }
                }
                checkedItems.Add("views.dimension_sets");
                checkedItems.Add("views.tags");
            }

            // ---- packing --------------------------------------------------------
            if (profile["packing"] is JObject packing)
            {
                const string stage = "pack";
                foreach (JProperty p in packing.Properties())
                    if (!PackingKeys.Contains(p.Name))
                        errors.Add(Finding(stage, "packing." + p.Name, "unknown_field", "unknown packing field; known: " + string.Join(", ", PackingKeys)));
                foreach (string d in new[] { "margin", "gap" })
                    if (packing[d] != null && !IsNonNegativeNumber(packing[d]))
                        errors.Add(Finding(stage, "packing." + d, "invalid", d + " must be a finite non-negative number in " + units));
                if (packing["tolerance"] != null && !IsPositiveNumber(packing["tolerance"]))
                    errors.Add(Finding(stage, "packing.tolerance", "invalid", "tolerance must be a finite positive number"));
                JArray layouts = packing["sheets"] as JArray ?? new JArray(packing.DeepClone());
                for (int i = 0; i < layouts.Count; i++)
                {
                    JObject layout = layouts[i] as JObject;
                    string at = packing["sheets"] == null ? "packing" : "packing.sheets[" + i + "]";
                    if (layout == null) continue;
                    if (packing["sheets"] != null)
                        foreach (JProperty p in layout.Properties())
                            if (!SheetKeys.Contains(p.Name))
                                errors.Add(Finding(stage, at + "." + p.Name, "unknown_field", "unknown candidate-sheet field; known: " + string.Join(", ", SheetKeys)));
                    if (layout["usable_rect"] != null)
                    {
                        try { DeliveryLayoutRules.ReadBox(layout["usable_rect"], 1.0); }
                        catch (ArgumentException ex) { errors.Add(Finding(stage, at + ".usable_rect", "invalid", ex.Message)); }
                    }
                    if (layout["reserved_zones"] != null)
                    {
                        JArray zones = layout["reserved_zones"] as JArray;
                        if (zones == null) errors.Add(Finding(stage, at + ".reserved_zones", "invalid", "reserved_zones must be an array of [minX,minY,maxX,maxY]"));
                        else for (int z = 0; z < zones.Count; z++)
                        {
                            try { DeliveryLayoutRules.ReadBox(zones[z], 1.0); }
                            catch (ArgumentException ex) { errors.Add(Finding(stage, at + ".reserved_zones[" + z + "]", "invalid", ex.Message)); }
                        }
                    }
                }
                if (packing["items"] is JArray items)
                {
                    var keys = new HashSet<string>(StringComparer.Ordinal);
                    for (int i = 0; i < items.Count; i++)
                    {
                        JObject item = items[i] as JObject;
                        string at = "packing.items[" + i + "]";
                        if (item == null) { errors.Add(Finding(stage, at, "type", "each item must be an object")); continue; }
                        foreach (JProperty p in item.Properties())
                            if (!ItemKeys.Contains(p.Name))
                                errors.Add(Finding(stage, at + "." + p.Name, "unknown_field", "items take key, view_id or schedule_id"));
                        string key = item.Value<string>("key");
                        if (string.IsNullOrWhiteSpace(key)) errors.Add(Finding(stage, at + ".key", "missing", "key is required and nonempty"));
                        else if (!keys.Add(key)) errors.Add(Finding(stage, at + ".key", "duplicate", "key '" + key + "' is used twice"));
                        bool hasView = item["view_id"] != null, hasSchedule = item["schedule_id"] != null;
                        if (hasView == hasSchedule)
                            errors.Add(Finding(stage, at, "invalid", "each item takes exactly one of view_id or schedule_id"));
                        if (hasView && !IsPositiveInteger(item["view_id"])) errors.Add(Finding(stage, at + ".view_id", "invalid", "view_id must be a positive element id"));
                        if (hasSchedule && !IsPositiveInteger(item["schedule_id"])) errors.Add(Finding(stage, at + ".schedule_id", "invalid", "schedule_id must be a positive element id"));
                    }
                }
                undetermined.Add(Finding(stage, "packing", "decided_by_rehearsal",
                    "capacity (whether every item fits the usable rectangles at unchanged scale) is proved by the packing rehearsal; a refusal there commits nothing"));
                checkedItems.Add("packing");
            }

            // ---- publication -----------------------------------------------------
            if (profile["publication"] is JObject publication)
            {
                const string stage = "publish";
                foreach (JProperty p in publication.Properties())
                    if (!PublicationKeys.Contains(p.Name))
                        errors.Add(Finding(stage, "publication." + p.Name, "unknown_field", "unknown publication field; known: " + string.Join(", ", PublicationKeys)));
                string output = publication.Value<string>("output_path");
                if (string.IsNullOrWhiteSpace(output)) errors.Add(Finding(stage, "publication.output_path", "missing", "output_path is required"));
                else
                {
                    bool rooted = false;
                    try { rooted = Path.IsPathRooted(output) && output.IndexOfAny(Path.GetInvalidPathChars()) < 0; } catch { rooted = false; }
                    if (!rooted) errors.Add(Finding(stage, "publication.output_path", "invalid", "output_path must be an absolute, well-formed path"));
                    else if (!string.Equals(Path.GetExtension(output), ".pdf", StringComparison.OrdinalIgnoreCase))
                        errors.Add(Finding(stage, "publication.output_path", "invalid", "output_path must end in .pdf for format pdf"));
                }
                foreach (string flag in new[] { "overwrite", "pdf_combine", "emit_manifest" })
                    if (publication[flag] != null && publication[flag].Type != JTokenType.Boolean)
                        errors.Add(Finding(stage, "publication." + flag, "invalid", flag + " must be a boolean"));
                string pubUnits = publication.Value<string>("units") ?? units;
                try { PdfPrintPolicy.Parse(publication["pdf_print"], pubUnits, hostYear); }
                catch (ArgumentException ex) { errors.Add(Finding(stage, "publication.pdf_print", "invalid", ex.Message)); }
                undetermined.Add(Finding(stage, "publication", "decided_by_export",
                    "file production, page counts, paper size/orientation and hashes are proved by the export itself after audit and visual approval"));
                checkedItems.Add("publication");
            }

            // ---- requirement set --------------------------------------------------
            if (profile["requirement_set"] is JObject requirements)
            {
                try { PlanimetryRequirementSet.Load(requirements); }
                catch (Exception ex) { errors.Add(Finding("audit", "requirement_set", "invalid", ex.Message)); }
                checkedItems.Add("requirement_set");
            }

            return Result(errors, undetermined, checkedItems);
        }

        /// <summary>Combine the static result with host findings into one report.</summary>
        public static JObject Merge(JObject staticResult, IEnumerable<JObject> hostErrors, IEnumerable<JObject> hostUndetermined,
                                    IEnumerable<string> hostChecked)
        {
            var errors = ((JArray)staticResult["errors"]).Cast<JObject>().Concat(hostErrors ?? Enumerable.Empty<JObject>()).ToList();
            var undetermined = ((JArray)staticResult["undetermined"]).Cast<JObject>().Concat(hostUndetermined ?? Enumerable.Empty<JObject>()).ToList();
            var checkedItems = ((JArray)staticResult["checked"]).Values<string>().Concat(hostChecked ?? Enumerable.Empty<string>()).ToList();
            return Result(errors, undetermined, checkedItems);
        }

        private static JObject Result(List<JObject> errors, List<JObject> undetermined, List<string> checkedItems)
        {
            return new JObject
            {
                ["schema"] = Schema,
                ["ok"] = errors.Count == 0,
                ["error_count"] = errors.Count,
                ["errors"] = new JArray(errors.Select(e => e.DeepClone())),
                ["undetermined"] = new JArray(undetermined.Select(u => u.DeepClone())),
                ["checked"] = new JArray(checkedItems.Distinct()),
                ["means"] = "errors are predictable from the profile and the document as they stand and refuse the plan before any write; " +
                            "undetermined names what only a rehearsal or the export can decide; the model can still change after this preflight, " +
                            "so every stage keeps its own state check."
            };
        }

        // ---- helpers -----------------------------------------------------------
        private static void CheckIdList(List<JObject> errors, string stage, string field, JToken token, int min, int max)
        {
            JArray ids = token as JArray;
            if (ids == null) { errors.Add(Finding(stage, field, "missing", field + " must be an array of " + min + ".." + max + " element ids")); return; }
            if (ids.Count < min || ids.Count > max) { errors.Add(Finding(stage, field, "invalid", field + " takes " + min + ".." + max + " element ids")); return; }
            if (ids.Any(id => !IsPositiveInteger(id))) { errors.Add(Finding(stage, field, "invalid", field + " must contain positive element ids only")); return; }
            if (ids.Values<long>().Distinct().Count() != ids.Count) errors.Add(Finding(stage, field, "duplicate", field + " contains a duplicate id"));
        }

        private static bool IsPositiveInteger(JToken t) { return t != null && t.Type == JTokenType.Integer && (long)t > 0; }
        private static bool IsNumberToken(JToken t) { return t != null && (t.Type == JTokenType.Integer || t.Type == JTokenType.Float); }
        private static bool IsPositiveNumber(JToken t) { return IsNumberToken(t) && DeliveryLayoutRules.Finite((double)t) && (double)t > 0; }
        private static bool IsNonNegativeNumber(JToken t) { return IsNumberToken(t) && DeliveryLayoutRules.Finite((double)t) && (double)t >= 0; }
        private static bool IsPoint3(JToken t)
        {
            JArray a = t as JArray;
            return a != null && a.Count == 3 && a.All(x => IsNumberToken(x) && DeliveryLayoutRules.Finite((double)x));
        }
    }
}
