// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// QUALITY OVER TIME, append-only. One JSON object per line in
// %USERPROFILE%\.horizun\quality-history\<project>.jsonl, each a summary of one
// horizun_model_scan or horizun_audit_model run: when, which document, which
// version, whether the run saw the whole model, and the numbers it measured.
//
// Nothing is interpreted into a score here. The metrics are the totals the scan
// and audit themselves reported, flattened to "section.bucket" names; a run that
// could not see everything is recorded with complete=false, and a trend reader
// must not compare it as if it had. A malformed line is COUNTED and reported,
// never skipped silently - a history with a hole in it is still a history, but
// the reader must know about the hole.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public sealed class QualityReadResult
    {
        public List<JObject> Records = new List<JObject>();
        public int MalformedLines;
        public bool FileExists;
    }

    public static class QualityHistory
    {
        public const string RecordSchema = "horizun.quality-record/1";
        public const int MaxMetrics = 400;

        /// <summary>A file-name-safe project key. Never empty, never a path.</summary>
        public static string ProjectKey(string raw)
        {
            var sb = new StringBuilder();
            foreach (char c in (raw ?? "").Trim())
                sb.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '.' ? c : '_');
            string key = sb.ToString().Trim('.', '_');
            if (key.Length > 80) key = key.Substring(0, 80);
            if (key.EndsWith(".rvt", StringComparison.OrdinalIgnoreCase)) key = key.Substring(0, key.Length - 4);
            return key.Length == 0 ? "default" : key;
        }

        public static string PathFor(string dataRoot, string project)
            => Path.Combine(dataRoot, "quality-history", ProjectKey(project) + ".jsonl");

        /// <summary>
        /// Every numeric "total" a model_scan reported, as section.bucket = total, plus
        /// the section failures. A section that failed contributes NO metric: its
        /// count is unknown, and a zero would be a lie.
        /// </summary>
        public static JObject SummarizeScan(JObject scan, out bool complete, out JArray failedSections)
        {
            var metrics = new SortedDictionary<string, double>(StringComparer.Ordinal);
            complete = scan?.Value<bool?>("complete") == true;
            failedSections = scan?["sections_failed"] as JArray ?? new JArray();
            if (scan?["sections"] is JObject sections)
                foreach (JProperty section in sections.Properties())
                {
                    if (!(section.Value is JObject body) || body.Value<string>("status") != "ok") continue;
                    Collect(section.Name, body, metrics, 0);
                }
            return ToJson(metrics);
        }

        private static void Collect(string prefix, JObject node, SortedDictionary<string, double> metrics, int depth)
        {
            if (depth > 3 || metrics.Count >= MaxMetrics) return;
            foreach (JProperty p in node.Properties())
            {
                if (p.Name == "total" && IsNumber(p.Value)) { metrics[prefix] = p.Value.Value<double>(); continue; }
                if (p.Value is JObject child)
                {
                    if (IsNumber(child["total"])) metrics[prefix + "." + p.Name] = child["total"].Value<double>();
                    else Collect(prefix + "." + p.Name, child, metrics, depth + 1);
                }
                else if (depth == 0 && IsNumber(p.Value) && (p.Name.EndsWith("_count", StringComparison.Ordinal) || p.Name == "file_size_mb"))
                    metrics[prefix + "." + p.Name] = p.Value.Value<double>();
                if (metrics.Count >= MaxMetrics) return;
            }
        }

        /// <summary>
        /// An audit_model run: each finding's count (and whether it is an issue), the
        /// health score when a profile produced one, and the gate verdict.
        /// </summary>
        public static JObject SummarizeAudit(JObject audit, out bool complete, out JArray failedChecks)
        {
            var metrics = new SortedDictionary<string, double>(StringComparer.Ordinal);
            failedChecks = new JArray();
            if (audit?["checks_failed"] is JArray cf)
                foreach (JToken t in cf) failedChecks.Add(t is JObject o ? (JToken)o.Value<string>("check") : t);
            foreach (JObject f in (audit?["findings"] as JArray ?? new JArray()).OfType<JObject>())
            {
                string check = f.Value<string>("check");
                if (string.IsNullOrWhiteSpace(check)) continue;
                if (IsNumber(f["count"])) metrics["finding." + check] = f["count"].Value<double>();
                if (f["is_issue"] != null && f["is_issue"].Type == JTokenType.Boolean)
                    metrics["issue." + check] = f.Value<bool>("is_issue") ? 1 : 0;
                if (metrics.Count >= MaxMetrics) break;
            }
            if (IsNumber(audit?["health"]?["score"])) metrics["health.score"] = audit["health"]["score"].Value<double>();
            complete = failedChecks.Count == 0 && audit?.Value<bool?>("coverage_complete") != false;
            return ToJson(metrics);
        }

        private static bool IsNumber(JToken t) => t != null && (t.Type == JTokenType.Integer || t.Type == JTokenType.Float);

        private static JObject ToJson(SortedDictionary<string, double> metrics)
        {
            var o = new JObject();
            foreach (var kv in metrics) o[kv.Key] = kv.Value;
            return o;
        }

        public static JObject Record(DateTime utc, string project, JObject document, string source,
                                     bool complete, JArray failed, JObject metrics, JObject extra = null)
        {
            var r = new JObject
            {
                ["schema"] = RecordSchema,
                ["recorded_utc"] = utc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
                ["project"] = ProjectKey(project),
                ["document"] = document ?? new JObject(),
                ["source"] = source,
                ["complete"] = complete,
                ["failed"] = failed ?? new JArray(),
                ["metrics"] = metrics ?? new JObject()
            };
            if (extra != null) foreach (JProperty p in extra.Properties()) r[p.Name] = p.Value.DeepClone();
            return r;
        }

        /// <summary>Append ONE line. The line is written in a single call so a reader never sees half of it.</summary>
        public static void Append(string path, JObject record)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            byte[] line = Encoding.UTF8.GetBytes(record.ToString(Formatting.None) + "\n");
            using (var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read))
            {
                fs.Write(line, 0, line.Length);
                fs.Flush(true);
            }
        }

        public static QualityReadResult Parse(string text)
        {
            var r = new QualityReadResult { FileExists = text != null };
            if (text == null) return r;
            foreach (string raw in text.Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;
                try
                {
                    // Dates stay strings: a record must read back exactly as it was written.
                    JObject o = JsonConvert.DeserializeObject<JObject>(line,
                        new JsonSerializerSettings { DateParseHandling = DateParseHandling.None });
                    if (o == null) { r.MalformedLines++; continue; }
                    if (o.Value<string>("schema") == RecordSchema) r.Records.Add(o); else r.MalformedLines++;
                }
                catch (JsonException) { r.MalformedLines++; }
            }
            return r;
        }

        public static QualityReadResult Read(string path)
        {
            if (!File.Exists(path)) return Parse(null);
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var reader = new StreamReader(fs, Encoding.UTF8))
                return Parse(reader.ReadToEnd());
        }

        /// <summary>
        /// The series, wide: one row per record, ready for horizun_power_bi_push rows.
        /// `metrics` filters (exact names or a trailing '*' prefix); empty keeps all.
        /// </summary>
        public static List<JObject> Rows(IEnumerable<JObject> records, IList<string> metrics, out List<string> columns)
        {
            var list = records.OrderBy(r => r.Value<string>("recorded_utc"), StringComparer.Ordinal).ToList();
            var names = new SortedSet<string>(StringComparer.Ordinal);
            foreach (JObject r in list)
                if (r["metrics"] is JObject m)
                    foreach (JProperty p in m.Properties())
                        if (Wanted(p.Name, metrics)) names.Add(p.Name);
            columns = names.ToList();
            var rows = new List<JObject>();
            foreach (JObject r in list)
            {
                var row = new JObject
                {
                    ["recorded_utc"] = r["recorded_utc"],
                    ["project"] = r["project"],
                    ["document"] = r["document"]?["title"],
                    ["version_guid"] = r["document"]?["version_guid"],
                    ["source"] = r["source"],
                    ["complete"] = r["complete"]
                };
                foreach (string n in columns)
                {
                    JToken v = r["metrics"]?[n];
                    row[n] = v == null ? JValue.CreateNull() : v.DeepClone();
                }
                rows.Add(row);
            }
            return rows;
        }

        private static bool Wanted(string name, IList<string> filter)
        {
            if (filter == null || filter.Count == 0) return true;
            foreach (string f in filter)
            {
                if (string.IsNullOrEmpty(f)) continue;
                if (f.EndsWith("*", StringComparison.Ordinal) ? name.StartsWith(f.Substring(0, f.Length - 1), StringComparison.Ordinal)
                                                              : name == f) return true;
            }
            return false;
        }

        public static string Csv(List<JObject> rows, List<string> columns)
        {
            var head = new List<string> { "recorded_utc", "project", "document", "version_guid", "source", "complete" };
            head.AddRange(columns);
            var sb = new StringBuilder(string.Join(",", head.Select(ModelDiffRules.CsvCell))).Append("\r\n");
            foreach (JObject row in rows)
                sb.Append(string.Join(",", head.Select(h => ModelDiffRules.CsvCell(Cell(row[h]))))).Append("\r\n");
            return sb.ToString();
        }

        private static string Cell(JToken t)
        {
            if (t == null || t.Type == JTokenType.Null) return "";
            if (t.Type == JTokenType.Float) return t.Value<double>().ToString("R", CultureInfo.InvariantCulture);
            if (t.Type == JTokenType.Boolean) return t.Value<bool>() ? "true" : "false";
            return t.ToString();
        }
    }
}
