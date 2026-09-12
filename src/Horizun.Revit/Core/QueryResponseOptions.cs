using System;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    // Response size is opt-in; existing callers retain the full shape. Identity,
    // unreadable records and federation coverage are never traded for compactness.
    public static class QueryResponseOptions
    {
        public static JObject Prepare(JObject request)
        {
            var normalized = (JObject)request.DeepClone();
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
                    normalized["return_fields"] = new JArray("name", "type", "level", "source_kind", "source_model", "link_instance_id");
            }
            if (mode == "summary")
            {
                foreach (string key in new[] { "cursor", "group_by", "return_parameters", "return_fields", "sum_parameters" })
                    if (normalized[key] != null)
                        throw new ArgumentException("response_mode=summary cannot be combined with " + key + ". Request rows or groups for those details.");
                if (normalized.Value<bool?>("include_bounding_box") == true)
                    throw new ArgumentException("response_mode=summary has no element rows; include_bounding_box requires full or compact.");
            }
            return normalized;
        }

        public static JObject Shape(JObject result, string mode)
        {
            if (mode != "summary") return result;
            // A summary deliberately contains no page. Keep ALL coverage and
            // unreadable evidence; remove only row/page metadata, not findings.
            foreach (string field in new[] { "rows", "returned", "offset", "truncated", "next_cursor", "result_set_fingerprint" }) result.Remove(field);
            result["response_mode"] = "summary";
            result["note"] = "Counts cover the whole matched set. No element rows were returned; use full or compact to inspect them.";
            return result;
        }

        public static T ReadBounds<T>(bool filterNeedsBounds, bool returnBounds, Func<T> read) where T : class =>
            filterNeedsBounds || returnBounds ? read() : null;
    }
}
