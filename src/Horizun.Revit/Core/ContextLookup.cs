using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    // Revit-free response rules shared by context queries and lookup diagnostics.
    internal static class ContextLookup
    {
        public static JObject Page(string title, IEnumerable<JObject> levels, IEnumerable<JObject> views, int offset, int maxRows)
        {
            // Field 2026-11-07..10: 6 refusals for max_rows out of range. A page size is clamped, not refused.
            if (offset < 0) offset = 0;
            maxRows = Math.Max(1, Math.Min(500, maxRows));
            var l = levels.OrderBy(r => (double)r["elevation_m"]).ThenBy(r => (long)r["id"]).ToList();
            var v = views.OrderBy(r => (string)r["name"], StringComparer.Ordinal).ThenBy(r => (long)r["id"]).ToList();
            bool more = (long)offset + maxRows < Math.Max(l.Count, v.Count);
            return new JObject
            {
                ["title"] = title, ["operation"] = "context", ["units"] = "m",
                ["levels"] = new JArray(l.Skip(offset).Take(maxRows)),
                ["views"] = new JArray(v.Skip(offset).Take(maxRows)),
                ["levels_total"] = l.Count, ["views_total"] = v.Count,
                ["offset"] = offset, ["next_offset"] = more ? (JToken)(offset + maxRows) : JValue.CreateNull(),
                ["truncated"] = more, ["coverage_complete"] = true, ["include_templates"] = false
            };
        }

        public static JObject Missing(string kind, string requested, IEnumerable<JObject> candidates)
        {
            string normalized = Normalize(requested);
            var closest = candidates.Select(row => new
                {
                    Row = row,
                    Distance = Distance(normalized, Normalize((string)row["name"]))
                })
                .OrderBy(r => r.Distance).ThenBy(r => (string)r.Row["name"], StringComparer.Ordinal)
                .ThenBy(r => (long)r.Row["id"]).Take(3).Select(r => r.Row).ToList();
            return new JObject
            {
                ["kind"] = kind, ["requested"] = requested,
                ["suggestions"] = new JArray(closest),
                ["next_step"] = "Query get_document_info operation=context for valid IDs; choose explicitly. Nothing was substituted."
            };
        }

        public static string Message(JObject detail) => "Unknown " + detail["kind"] + " '" + detail["requested"] +
            "'. Closest valid names: " + string.Join(", ", ((JArray)detail["suggestions"]).Select(r =>
                "'" + r["name"] + "' (id=" + r["id"] + ")")) + ". " + detail["next_step"];

        private static string Normalize(string text) => new string((text ?? "").Trim().ToLowerInvariant()
            .Normalize(NormalizationForm.FormD).Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).ToArray());

        private static int Distance(string a, string b)
        {
            // Bound malformed requests while retaining the full original name in the diagnostic.
            a = a.Substring(0, Math.Min(256, a.Length)); b = b.Substring(0, Math.Min(256, b.Length));
            var row = Enumerable.Range(0, b.Length + 1).ToArray();
            for (int i = 1; i <= a.Length; i++)
            {
                int previous = row[0]; row[0] = i;
                for (int j = 1; j <= b.Length; j++)
                {
                    int old = row[j];
                    row[j] = Math.Min(Math.Min(row[j] + 1, row[j - 1] + 1), previous + (a[i - 1] == b[j - 1] ? 0 : 1));
                    previous = old;
                }
            }
            return row[b.Length];
        }
    }
}
