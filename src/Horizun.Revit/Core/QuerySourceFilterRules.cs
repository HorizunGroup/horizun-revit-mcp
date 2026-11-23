// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// READ THE MODEL YOU MEAN, NOT EVERY MODEL AND THEN THROW MOST AWAY.
//
// MEASURED (Comité de obra, 2026-10-01): reading the levels of 92 elements of ONE
// link through horizun_query_model meant paging 987 rows of the host and every other
// link, because the query had no way to name its source. source_kind/source_model
// were row fields and group keys, never filters.
//
// source_models names documents by title (exactly, case-insensitive; "host" is the
// active document) and link_instance_ids names link placements. A source is read when
// it matches ANY entry; every other document is never collected. An entry that
// matches nothing refuses the query with the sources that do exist - a typo must not
// come back as a confident zero.
//
// Revit-free: the Revit half lists the loaded sources; this decides which are read.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public sealed class QuerySourceFilter
    {
        public const string HostKeyword = "host";

        private readonly HashSet<string> _names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<long> _linkIds = new HashSet<long>();

        /// <summary>One document the query could read.</summary>
        public sealed class Source
        {
            public string Kind;      // "host" | "link"
            public string Title;
            public long? LinkInstanceId;
        }

        public bool Active => _names.Count > 0 || _linkIds.Count > 0;
        public bool NamesALink => _linkIds.Count > 0 || _names.Any(n => !string.Equals(n, HostKeyword, StringComparison.OrdinalIgnoreCase));
        public bool NamesLinkOtherThanHost(string hostTitle) => _linkIds.Count > 0 || _names.Any(n =>
            !string.Equals(n, HostKeyword, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(n, hostTitle, StringComparison.OrdinalIgnoreCase));

        /// <summary>Parse source_models / link_instance_ids. Absent or empty means no filter.</summary>
        public static QuerySourceFilter Parse(JObject request, out string error)
        {
            error = null;
            var f = new QuerySourceFilter();
            JToken names = request?["source_models"];
            if (names != null && names.Type != JTokenType.Null)
            {
                if (!(names is JArray arr) || arr.Any(t => t.Type != JTokenType.String || string.IsNullOrWhiteSpace((string)t)))
                { error = "source_models must be an array of non-empty document titles (or \"host\")."; return null; }
                foreach (JToken t in arr) f._names.Add(((string)t).Trim());
            }
            JToken ids = request?["link_instance_ids"];
            if (ids != null && ids.Type != JTokenType.Null)
            {
                if (!(ids is JArray arr) || arr.Any(t => t.Type != JTokenType.Integer))
                { error = "link_instance_ids must be an array of integers (RevitLinkInstance ids, as rows report link_instance_id)."; return null; }
                foreach (JToken t in arr) f._linkIds.Add((long)t);
            }
            return f;
        }

        public bool Admits(Source s)
        {
            if (!Active) return true;
            if (string.Equals(s.Kind, "host", StringComparison.Ordinal))
                return _names.Contains(HostKeyword) || (s.Title != null && _names.Contains(s.Title));
            return (s.LinkInstanceId.HasValue && _linkIds.Contains(s.LinkInstanceId.Value)) ||
                   (s.Title != null && _names.Contains(s.Title));
        }

        /// <summary>The refusal when an entry matches no available source; null when every entry matches.</summary>
        public string Unmatched(IList<Source> available)
        {
            if (!Active) return null;
            var missing = new List<string>();
            foreach (string n in _names)
            {
                bool hit = string.Equals(n, HostKeyword, StringComparison.OrdinalIgnoreCase) ||
                           available.Any(s => string.Equals(s.Title, n, StringComparison.OrdinalIgnoreCase));
                if (!hit) missing.Add("source_models '" + n + "'");
            }
            foreach (long id in _linkIds)
                if (!available.Any(s => s.LinkInstanceId == id)) missing.Add("link_instance_ids " + id);
            if (missing.Count == 0) return null;
            return string.Join(", ", missing) + " matched no loaded source. Available: " +
                   string.Join("; ", available.Select(s => s.Kind == "host"
                       ? "\"host\" (" + s.Title + ")"
                       : "'" + s.Title + "' (link_instance_id " + s.LinkInstanceId + ")")) +
                   ". An unloaded link has no document to read. Nothing was read.";
        }

        public JObject ToJson(IList<Source> available)
        {
            var read = available.Where(Admits).ToList();
            return new JObject
            {
                ["source_models"] = new JArray(_names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase)),
                ["link_instance_ids"] = new JArray(_linkIds.OrderBy(i => i)),
                ["sources_read"] = new JArray(read.Select(s => (JToken)new JObject
                {
                    ["source_kind"] = s.Kind, ["source_model"] = s.Title,
                    ["link_instance_id"] = s.LinkInstanceId.HasValue ? (JToken)s.LinkInstanceId.Value : JValue.CreateNull()
                })),
                ["sources_skipped"] = available.Count - read.Count,
                ["means"] = "Only the sources in sources_read were collected; matched_total, summary and groups count them alone."
            };
        }
    }
}
