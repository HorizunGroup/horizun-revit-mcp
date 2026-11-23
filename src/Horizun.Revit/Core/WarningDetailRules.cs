using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
namespace Horizun.Revit.Core
{
    public static class WarningDetailRules
    {
        public static string ParseRequest(JObject request, string title, out int offset, out int size, out int budget)
        {
            offset = 0; size = 100; budget = 65536;
            foreach (var property in request.Properties())
                if (property.Name != "operation" && property.Name != "target_document" && property.Name != "offset" && property.Name != "page_size" && property.Name != "byte_budget" && property.Name != "confirm_large_operation")
                    return "warnings: unknown argument " + property.Name;
            string target = (string)request["target_document"];
            if (string.IsNullOrWhiteSpace(target) || !string.Equals(target, title, StringComparison.OrdinalIgnoreCase))
                return "target_document must name the active document: " + title;
            try
            {
                foreach (string key in new[] { "offset", "page_size", "byte_budget" })
                    if (request[key] != null && request[key].Type != JTokenType.Integer) return key + " must be an integer";
                offset = request.Value<int?>("offset") ?? 0;
                size = request.Value<int?>("page_size") ?? 100;
                budget = request.Value<int?>("byte_budget") ?? 65536;
            }
            catch (Exception ex) { return "Invalid pagination: " + ex.Message; }
            if (offset < 0 || size < 1 || size > 1000 || budget < 1024 || budget > 524288)
                return "offset must be nonnegative, page_size 1..1000 and byte_budget 1024..524288";
            return null;
        }

        public static JObject Suggestion(string description, string document)
        {
            string s = (description ?? "").ToLowerInvariant();
            string tool = null, operation = null, note = null;
            if ((s.Contains("identical") && s.Contains("same place")) || s.Contains("elementos idénticos en el mismo lugar"))
            { tool = "horizun_delete_verified"; note = "Choose which duplicate to delete after inspecting the individual occurrence; never delete all members."; }
            else if ((s.Contains("joined") && s.Contains("do not intersect")) || (s.Contains("unidos") && s.Contains("no se intersecan")))
            { tool = "horizun_join_geometry"; operation = "unjoin"; note = "Supply the two elements of one occurrence and their unique_ids."; }
            else if ((s.Contains("wall") && s.Contains("overlap")) || (s.Contains("muros") && s.Contains("superpuest")))
            { tool = "horizun_join_geometry"; operation = "join"; note = "Inspect each wall pair and choose the correction; overlap does not prove a join is appropriate."; }
            else if ((s.Contains("room") && s.Contains("not enclosed")) || (s.Contains("habitación") && s.Contains("no está cerrada")))
            { tool = "horizun_create_elements"; note = "First inspect horizun_room_boundaries; supply a room_separator only if the missing boundary is known, including its level, plan view and curves."; }
            if (tool == null) return null;
            var args = new JObject { ["target_document"] = document };
            if (operation != null) args["operation"] = operation;
            if (tool != "horizun_room_boundaries") args["dry_run"] = true;
            if (tool == "horizun_delete_verified") args["mode"] = "ids";
            if (tool == "horizun_create_elements") args["elements"] = new JArray(new JObject { ["kind"] = "room_separator" });
            return new JObject { ["tool"] = tool, ["arguments"] = args, ["requires_input"] = note };
        }
        public static List<JObject> Group(IEnumerable<JObject> facts)
        {
            return facts.GroupBy(f => new { Description = (string)f["description"], Severity = (string)f["severity"] })
                .OrderBy(g => g.Key.Description, StringComparer.Ordinal).ThenBy(g => g.Key.Severity, StringComparer.Ordinal)
                .Select(g => new JObject { ["description"] = g.Key.Description, ["severity"] = g.Key.Severity,
                    ["occurrences"] = g.Count(), ["warnings"] = new JArray(g.Select(x => x.DeepClone())) }).ToList();
        }
        // Offset counts individual element records (and one record for an empty warning), preserving occurrences.
        public static JObject Page(List<JObject> groups, int offset, int limit, int budget)
        {
            var records = new List<JObject>();
            foreach (JObject group in groups)
                foreach (JObject warning in (JArray)group["warnings"])
                {
                    var elements = (JArray)warning["elements"];
                    var entries = elements.Count == 0 ? new JToken[] { JValue.CreateNull() } : elements.ToArray();
                    foreach (JToken element in entries)
                        records.Add(new JObject { ["description"] = group["description"].DeepClone(), ["severity"] = group["severity"].DeepClone(),
                            ["occurrences"] = group["occurrences"].DeepClone(), ["warning_index"] = warning["warning_index"],
                            ["element"] = element.DeepClone(), ["ids_complete"] = warning["ids_complete"], ["ids_error"] = warning["ids_error"],
                            ["suggested_call"] = warning["suggested_call"]?.DeepClone() });
                }
            if (offset < 0 || offset > records.Count || limit < 1 || budget < 1024) throw new ArgumentException("offset, limit or byte_budget is out of range");
            var rows = new JArray();
            JObject result = null;
            for (int i = offset; i < records.Count && rows.Count < limit; i++)
            {
                rows.Add(records[i]);
                result = Response(rows, records.Count, offset);
                if (Encoding.UTF8.GetByteCount(result.ToString(Formatting.None)) > budget) { rows.RemoveAt(rows.Count - 1); break; }
            }
            result = Response(rows, records.Count, offset);
            if (Encoding.UTF8.GetByteCount(result.ToString(Formatting.None)) > budget || (rows.Count == 0 && offset < records.Count))
                throw new ArgumentException("byte_budget is too small for the next warning record; increase it");
            return result;
        }
        private static JObject Response(JArray rows, int total, int offset)
        {
            int next = offset + rows.Count;
            return new JObject { ["records"] = rows, ["total_records"] = total, ["offset"] = offset,
                ["has_more"] = next < total, ["next_offset"] = next < total ? (JToken)next : JValue.CreateNull(),
                ["paging_note"] = "Offsets apply to this snapshot only; model changes between calls can change pages." };
        }
    }
}
