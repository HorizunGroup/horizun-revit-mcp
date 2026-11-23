using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    // Response size is opt-in; existing callers retain the full shape. Identity,
    // unreadable records and federation coverage are never traded for compactness.
    public static class QueryResponseOptions
    {
        public static HashSet<string> ProjectedFields(IEnumerable<string> fields)
        {
            var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
                "source_reference", "unique_id", "category", "built_in_category", "name", "family", "type", "type_id", "level",
                "is_element_type", "source_kind", "source_model", "link_instance_id", "host_id", "host_category",
                "is_view_template", "view_template_id", "view_type", "view_range", "sketch" };
            var selected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string field in fields)
            {
                if (string.Equals(field, "element_id", StringComparison.OrdinalIgnoreCase)) continue;
                if (!known.Contains(field)) throw new ArgumentException("return_fields '" + field + "' is not a row field. Known: " + string.Join(", ", known.OrderBy(x => x)) + ". element_id is always present.");
                selected.Add(field);
            }
            return selected;
        }

        // null means all fields; an empty set means the explicit element_id-only projection.
        public static JObject ApplyIdOnlyProjection(JObject row, HashSet<string> fields)
        {
            if (fields != null && fields.Count == 0)
            {
                foreach (var property in row.Properties().Where(p => p.Name != "element_id").ToList())
                    property.Remove();
            }
            return row;
        }

        public static JObject Prepare(JObject request) => Prepare(request, out _);

        public static JObject Prepare(JObject request, out JArray changes)
        {
            changes = new JArray();
            var normalized = (JObject)request.DeepClone();
            // A host ViewId cannot scope a linked document. Keep an explicit true
            // for the command's refusal, but do not make every ordinary view query
            // contradict itself through the model-scope federation default.
            string scope = normalized.Value<string>("scope") ?? "model";
            if (normalized["include_links"] == null &&
                (string.Equals(scope, "view", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(scope, "current_view", StringComparison.OrdinalIgnoreCase)))
                normalized["include_links"] = false;
            string mode = normalized.Value<string>("response_mode") ?? "full";
            string cache = normalized.Value<string>("cache_mode") ?? "bypass";
            if (cache != "bypass" && cache != "reuse") throw new ArgumentException("cache_mode must be bypass or reuse.");
            if (normalized["include_diagnostics"] != null && normalized["include_diagnostics"].Type != JTokenType.Boolean)
                throw new ArgumentException("include_diagnostics must be boolean.");
            if (mode != "full" && mode != "compact" && mode != "summary")
                throw new ArgumentException("response_mode must be full, compact or summary.");
            if (mode == "compact")
            {
                if (normalized["parameter_format"] == null) normalized["parameter_format"] = "compact";
                if (normalized["return_fields"] == null)
                    normalized["return_fields"] = new JArray("category", "name", "type", "level", "source_kind", "source_model", "link_instance_id");
            }
            if (mode == "summary")
            {
                var remove = new List<string> { "cursor", "offset", "return_parameters", "return_fields", "include_bounding_box", "include_room", "phase" };
                if (!(normalized["group_by"] is JArray groups) || groups.Count == 0) remove.Add("sum_parameters");
                foreach (string key in remove)
                    if (normalized[key] != null)
                    {
                        changes.Add(new JObject { ["field"] = key, ["from"] = normalized[key].DeepClone(), ["to"] = JValue.CreateNull(), ["reason"] = "summary has no paginated element rows" });
                        normalized.Remove(key);
                    }
            }
            return normalized;
        }

        public static JObject Shape(JObject result, string mode)
        {
            if (mode != "summary") return result;
            // A summary deliberately contains no page. Keep ALL coverage and
            // unreadable evidence; remove only row/page metadata, not findings.
            foreach (string field in new[] { "rows", "returned", "page_limit", "remaining", "offset", "truncated", "next_cursor", "result_set_fingerprint" }) result.Remove(field);
            result["response_mode"] = "summary";
            result["note"] = result.Value<bool?>("coverage_complete") == false
                ? "Counts cover the observed matched set; coverage is incomplete. No element rows were returned; inspect coverage before treating counts as totals."
                : "Counts cover the whole matched set. No element rows were returned; use full or compact to inspect them.";
            return result;
        }

        // A budget stop is incomplete collection, not ordinary pagination. Every
        // response shape must carry it before the cache considers storing the result.
        public static void ApplyCollectionCoverage(JObject result, bool? complete, JObject cooperativeReport = null)
        {
            if (cooperativeReport != null) result["cooperative"] = cooperativeReport;
            if (!complete.HasValue) return;
            result["collection_complete"] = complete.Value;
            result["partial"] = !complete.Value;
            if (complete.Value) return;
            result["coverage_complete"] = false;
            result["partial_reason"] = "collection_stopped_before_scope_end";
            if (result["cooperative"] is JObject report)
            {
                report["collection_continuation_available"] = false;
                report["cursor_means"] = "Collection stopped before the end. Pagination covers only the collected prefix; it cannot resume the unexamined elements. Re-run with a narrower selector or a larger cooperative budget.";
            }
        }

        public static T ReadBounds<T>(bool filterNeedsBounds, bool returnBounds, Func<T> read) where T : class =>
            filterNeedsBounds || returnBounds ? read() : null;

        public static void DescribeCoverage(JObject result, JObject request)
        {
            var missing = new JArray();
            var actions = new JArray();
            Action<string, JToken, string, JObject, string> add = (kind, count, reason, args, instruction) =>
            {
                missing.Add(new JObject { ["kind"] = kind, ["count"] = count ?? JValue.CreateNull(), ["reason"] = reason });
                actions.Add(new JObject { ["instruction"] = instruction, ["suggested_call"] = new JObject { ["tool"] = "horizun_query_model", ["arguments"] = args } });
            };
            Func<JObject> retry = () => (JObject)request.DeepClone();
            if (result.Value<bool?>("collection_complete") == false)
            {
                var args = retry(); args.Remove("cursor"); args.Remove("offset"); args.Remove("cooperative"); args["confirm_large_operation"] = true;
                add("unexamined_elements", null, "The cooperative budget stopped collection; the number not examined is unknown.", args, "This repeats the entire query without the cooperative budget and confirms a large scope; alternatively narrow categories, sources or scope.");
            }
            string cursor = result.Value<string>("next_cursor");
            if (!string.IsNullOrEmpty(cursor))
            {
                var args = retry(); args.Remove("offset"); args["cursor"] = cursor;
                int remaining = Math.Max(0, result.Value<int?>("matched_total").GetValueOrDefault() - result.Value<int?>("offset").GetValueOrDefault() - result.Value<int?>("returned").GetValueOrDefault());
                add("remaining_rows", new JValue(remaining), "The row limit left matched rows on later pages.", args, "Request the next page using this cursor.");
            }
            int unreadable = result.Value<int?>("unreadable_total") ?? 0;
            if (unreadable > 0)
            {
                var args = retry(); args.Remove("bounding_box"); args["response_mode"] = "full"; args.Remove("cursor"); args.Remove("offset");
                add("unreadable", new JValue(unreadable), "Some categories, elements or geometry could not be read; see unreadable for the reasons and identities.", args, "Inspect unreadable and retry a smaller scope; without the bounding box the retry is an inspection, not the same spatial query.");
                ((JObject)missing[missing.Count - 1])["details"] = result["unreadable"]?.DeepClone() ?? new JArray();
            }
            var federation = result["federated_coverage"] as JObject;
            if (federation?.Value<bool?>("coverage_complete") == false)
            {
                add("unavailable_sources_or_worksets", null, "Links or worksets were unavailable; their element count is unknown.", retry(), "Load the unavailable links and open worksets, then repeat the query.");
                ((JObject)missing[missing.Count - 1])["details"] = federation.DeepClone();
            }
            if (result.Value<bool?>("groups_truncated") == true)
            {
                var args = retry(); args.Remove("group_by"); args.Remove("sum_parameters"); args["response_mode"] = "full";
                add("remaining_groups", result["groups_omitted"]?.DeepClone(), "The group limit omitted groups.", args, "Inspect paginated rows or repeat with a narrower scope.");
            }
            if (missing.Count > 0) { result["missing"] = missing; result["how_to_complete"] = actions; }
        }
    }
}
