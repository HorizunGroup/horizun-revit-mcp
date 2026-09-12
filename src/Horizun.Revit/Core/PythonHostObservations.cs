using System;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public static class PythonHostObservations
    {
        public static JObject Warnings(Document doc)
        {
            try
            {
                if (doc == null || !doc.IsValidObject) throw new InvalidOperationException("No valid captured document.");
                var warnings = doc.GetWarnings();
                return new JObject
                {
                    ["observed"] = true,
                    ["count"] = warnings.Count,
                    ["truncated"] = warnings.Count > 2000,
                    ["signatures"] = new JArray(warnings.Take(2000).Select(w => w.GetFailureDefinitionId().Guid.ToString() + ":" +
                        string.Join(",", w.GetFailingElements().Select(Rid.Value).OrderBy(x => x)) + ":" +
                        string.Join(",", w.GetAdditionalElements().Select(Rid.Value).OrderBy(x => x))))
                };
            }
            catch (Exception ex) { return new JObject { ["observed"] = false, ["error"] = ex.Message }; }
        }
        public static JObject Delta(JObject before, JObject after)
        {
            bool complete = before.Value<bool>("observed") && after.Value<bool>("observed") &&
                before.Value<bool?>("truncated") != true && after.Value<bool?>("truncated") != true;
            var a = (before["signatures"] as JArray ?? new JArray()).Values<string>().ToArray();
            var b = (after["signatures"] as JArray ?? new JArray()).Values<string>().ToArray();
            return new JObject
            {
                ["before"] = before,
                ["after"] = after,
                ["persistent_warning_comparison_complete"] = complete,
                ["new"] = complete ? new JArray(b.Except(a)) : null,
                ["resolved"] = complete ? new JArray(a.Except(b)) : null,
                ["suppressed_warning_coverage"] = "unknown",
                ["note"] = "GetWarnings compares persistent warnings on the captured document. A script may suppress transient failures; an empty delta does not prove no warnings were raised."
            };
        }
        public static JObject CreatedIds(Document doc, JToken output)
        {
            var ids = output is JObject data ? data["created_ids"] as JArray : null;
            var rows = new JArray();
            if (ids != null) foreach (var token in ids.Take(2000))
            {
                var row = new JObject { ["reported_id"] = token.DeepClone() };
                try
                {
                    if (doc == null || !doc.IsValidObject) throw new InvalidOperationException("Captured document is unavailable; ID existence is unknown.");
                    if (token.Type != JTokenType.Integer || !Rid.CanRepresent(token.Value<long>())) throw new ArgumentException("Invalid element id.");
                    Element e = doc?.GetElement(Rid.Make(token.Value<long>()));
                    row["exists"] = e != null; row["unique_id"] = e?.UniqueId;
                    row["category_id"] = e?.Category == null ? null : (JToken)Rid.Value(e.Category.Id);
                    row["category"] = e?.Category?.Name;
                }
                catch (Exception ex) { row["exists"] = null; row["error"] = ex.Message; }
                rows.Add(row);
            }
            return new JObject
            {
                ["scope"] = "reported_ids_existence_and_category_only",
                ["reported"] = ids?.Count,
                ["checked"] = rows.Count,
                ["truncated"] = ids != null && ids.Count > rows.Count,
                ["rows"] = rows,
                ["geometry_verified"] = false,
                ["creation_by_this_script_verified"] = false
            };
        }
    }
}
