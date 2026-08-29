// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// The coordination ledger on disk: one JSON file per DOCUMENT under the data
// root, written atomically, read back after every write. The rules that decide
// what a finding does live in CoordinationRules; this file only keeps them.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public static class CoordinationLedger
    {
        public const int Schema = 1;

        public static string Dir() => Path.Combine(HorizunPaths.DataRoot(), "coordination");

        /// <summary>The document's ledger file, keyed by title + path hash - filename-safe.</summary>
        public static string PathFor(string documentTitle, string documentPath)
        {
            string identity = (documentTitle ?? "untitled") + "\x1f" + (documentPath ?? "");
            string key;
            using (var sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(identity));
                var hex = new StringBuilder(16);
                for (int i = 0; i < 8; i++) hex.Append(hash[i].ToString("x2", CultureInfo.InvariantCulture));
                key = hex.ToString();
            }
            return Path.Combine(Dir(), key + ".json");
        }

        public static Dictionary<string, CoordinationFinding> Load(string path, out string documentTitle)
        {
            documentTitle = null;
            var findings = new Dictionary<string, CoordinationFinding>(StringComparer.Ordinal);
            if (!File.Exists(path)) return findings;
            JObject root = JObject.Parse(File.ReadAllText(path));
            documentTitle = root.Value<string>("document");
            if (root["findings"] is JObject block)
                foreach (var property in block.Properties())
                    if (property.Value is JObject row)
                        findings[property.Name] = FromJson(property.Name, row);
            return findings;
        }

        public static void Save(string path, string documentTitle,
                                Dictionary<string, CoordinationFinding> findings)
        {
            var block = new JObject();
            foreach (var pair in findings) block[pair.Key] = ToJson(pair.Value);
            var root = new JObject
            {
                ["schema"] = Schema,
                ["document"] = documentTitle,
                ["updated_utc"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                ["findings"] = block
            };
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            string tmp = path + ".tmp-" + Guid.NewGuid().ToString("N");
            File.WriteAllText(tmp, root.ToString());
            if (File.Exists(path)) File.Replace(tmp, path, null);
            else File.Move(tmp, path);
        }

        public static JObject ToJson(CoordinationFinding f)
        {
            var row = new JObject
            {
                ["scope"] = f.Scope,
                ["status"] = f.Status,
                ["side_a"] = f.SideA,
                ["side_b"] = f.SideB,
                ["category_a"] = f.CategoryA,
                ["category_b"] = f.CategoryB,
                ["first_seen_utc"] = f.FirstSeenUtc,
                ["last_seen_utc"] = f.LastSeenUtc,
                ["times_seen"] = f.TimesSeen,
                ["regression"] = f.Regression
            };
            if (f.Assignee != null) row["assignee"] = f.Assignee;
            if (f.Note != null) row["note"] = f.Note;
            if (f.ResolvedUtc != null) row["resolved_utc"] = f.ResolvedUtc;
            if (f.UpdatedUtc != null) row["updated_utc"] = f.UpdatedUtc;
            if (f.PointMm != null) row["point_mm"] = new JArray(f.PointMm);
            if (f.History != null && f.History.Count > 0)
                row["history"] = new JArray(f.History.Select(entry => (JToken)new JObject
                {
                    ["at_utc"] = entry.AtUtc, ["kind"] = entry.Kind, ["text"] = entry.Text
                }));
            return row;
        }

        public static CoordinationFinding FromJson(string id, JObject row)
        {
            var f = new CoordinationFinding
            {
                Id = id,
                Scope = row.Value<string>("scope"),
                Status = row.Value<string>("status") ?? CoordinationRules.StatusOpen,
                Assignee = row.Value<string>("assignee"),
                Note = row.Value<string>("note"),
                SideA = row.Value<string>("side_a"),
                SideB = row.Value<string>("side_b"),
                CategoryA = row.Value<string>("category_a"),
                CategoryB = row.Value<string>("category_b"),
                FirstSeenUtc = row.Value<string>("first_seen_utc"),
                LastSeenUtc = row.Value<string>("last_seen_utc"),
                ResolvedUtc = row.Value<string>("resolved_utc"),
                UpdatedUtc = row.Value<string>("updated_utc"),
                TimesSeen = row.Value<int?>("times_seen") ?? 0,
                Regression = row.Value<bool?>("regression") == true
            };
            if (row["point_mm"] is JArray point && point.Count == 3)
                f.PointMm = new[] { (double)point[0], (double)point[1], (double)point[2] };
            if (row["history"] is JArray history)
                foreach (JToken token in history)
                    if (token is JObject entry)
                        f.History.Add(new CoordinationEvent
                        {
                            AtUtc = entry.Value<string>("at_utc"),
                            Kind = entry.Value<string>("kind"),
                            Text = entry.Value<string>("text")
                        });
            return f;
        }
    }
}
