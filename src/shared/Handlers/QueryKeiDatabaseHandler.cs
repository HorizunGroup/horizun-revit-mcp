using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class QueryKeiDatabaseHandler : IRevitCommand
    {
        public string Name => "query_kei_database";
        public string Description =>
            "Read-only SELECT/PRAGMA against the KEI project SQLite DB (auto-resolved from active Revit document). " +
            "Presets: overview, equipment, schema, categories, list databases.";

        public string ParametersSchema =>
            @"{""type"":""object"",""properties"":{""preset"":{""type"":""string"",""enum"":[""overview"",""equipment"",""schema"",""categories""]},""sql"":{""type"":""string""},""database"":{""type"":""string"",""description"":""auto | list | substring of db filename""},""limit"":{""type"":""integer"",""default"":100}}}";

        private static readonly Dictionary<string, string> PresetQueries =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["overview"] = @"
SELECT 'Elements' AS [table], COUNT(*) AS [count] FROM Elements
UNION ALL SELECT 'ProjectEquipmentTypes', COUNT(*) FROM ProjectEquipmentTypes
UNION ALL SELECT 'ProjectEquipments', COUNT(*) FROM ProjectEquipments
UNION ALL SELECT 'TypedSpecs', COUNT(*) FROM TypedSpecs
UNION ALL SELECT 'EquipmentCategories', COUNT(*) FROM EquipmentCategories
UNION ALL SELECT 'SupplyItemsCache', COUNT(*) FROM SupplyItemsCache",
                ["equipment"] = @"
SELECT t.ProjectTypeId, t.ProjectTypeName, t.CategoryCode, t.Area, t.Brand, t.Status,
       (SELECT COUNT(*) FROM ProjectEquipments pe WHERE pe.ProjectTypeId = t.ProjectTypeId) AS InstanceCount
FROM ProjectEquipmentTypes t
ORDER BY t.ProjectTypeId
LIMIT 200",
                ["schema"] = @"
SELECT m.name AS [table], group_concat(p.name, ', ') AS [columns]
FROM sqlite_master m
JOIN pragma_table_info(m.name) p
WHERE m.type = 'table' AND m.name NOT LIKE 'sqlite_%'
GROUP BY m.name
ORDER BY m.name",
                ["categories"] = @"
SELECT CategoryCode, DisplayNameVN, DisplayNameEN, HydraulicRole
FROM EquipmentCategories
ORDER BY SortOrder"
            };

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            try
            {
                var request = string.IsNullOrWhiteSpace(paramsJson)
                    ? new JObject()
                    : JObject.Parse(paramsJson);

                var preset = request.Value<string>("preset") ?? "";
                var query = request.Value<string>("sql") ?? "";
                var database = request.Value<string>("database") ?? "auto";
                var limit = request.Value<int?>("limit") ?? 100;

                var folder = KeiDatabaseResolver.GetProjectsFolder();
                if (!Directory.Exists(folder))
                    return CommandResult.Fail($"KEI database folder not found: {folder}");

                if (database.Equals("list", StringComparison.OrdinalIgnoreCase))
                {
                    var dbs = Directory.GetFiles(folder, "*.db")
                        .Select(f =>
                        {
                            var fi = new FileInfo(f);
                            var walFile = f + "-wal";
                            long walSize = File.Exists(walFile) ? new FileInfo(walFile).Length : 0;
                            return new
                            {
                                name = fi.Name,
                                sizeMB = Math.Round(fi.Length / 1024.0 / 1024.0, 1),
                                walKB = Math.Round(walSize / 1024.0, 1),
                                lastModified = fi.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss"),
                                isActive = walSize > 0
                            };
                        })
                        .OrderByDescending(x => x.isActive)
                        .ThenByDescending(x => x.lastModified)
                        .ToList();

                    return CommandResult.Ok(new { folder, databases = dbs });
                }

                string dbPath = KeiDatabaseResolver.Resolve(app, database);
                if (dbPath == null)
                    return CommandResult.Fail(
                        "No active KEI database detected. Open a Revit project or pass database='list' / a filename substring.");

                string sql;
                if (!string.IsNullOrEmpty(preset))
                {
                    if (!PresetQueries.TryGetValue(preset, out sql))
                        return CommandResult.Fail(
                            $"Unknown preset '{preset}'. Available: {string.Join(", ", PresetQueries.Keys)}");
                }
                else if (!string.IsNullOrEmpty(query))
                {
                    var trimmed = query.TrimStart();
                    if (!trimmed.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)
                        && !trimmed.StartsWith("PRAGMA", StringComparison.OrdinalIgnoreCase)
                        && !trimmed.StartsWith("WITH", StringComparison.OrdinalIgnoreCase))
                    {
                        return CommandResult.Fail("Only SELECT, PRAGMA, and WITH queries are allowed (read-only).");
                    }
                    sql = query;
                }
                else
                {
                    sql = PresetQueries["overview"];
                    preset = "overview";
                }

                if (string.IsNullOrEmpty(preset) &&
                    sql.IndexOf("LIMIT", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    sql = sql.TrimEnd().TrimEnd(';') + $" LIMIT {limit}";
                }

                var rows = new List<Dictionary<string, object>>();
                using (var conn = KeiDatabaseResolver.OpenReadOnly(dbPath))
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = sql;
                    using (var reader = cmd.ExecuteReader())
                    {
                        int rowCount = 0;
                        while (reader.Read() && rowCount < limit)
                        {
                            var row = new Dictionary<string, object>();
                            for (int i = 0; i < reader.FieldCount; i++)
                                row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                            rows.Add(row);
                            rowCount++;
                        }
                    }
                }

                return CommandResult.Ok(new
                {
                    database = Path.GetFileName(dbPath),
                    dbPath,
                    preset = string.IsNullOrEmpty(preset) ? "custom" : preset,
                    sql = sql.Trim(),
                    rowCount = rows.Count,
                    rows
                });
            }
            catch (Exception ex)
            {
                return CommandResult.Fail($"Error querying KEI database: {ex.Message}");
            }
        }
    }
}
