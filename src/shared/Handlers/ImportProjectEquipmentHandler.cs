using System;
using System.Collections.Generic;
using System.Globalization;
using Autodesk.Revit.UI;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    /// <summary>
    /// Bulk-import project equipment into the KEI SQLite DB via the Revit plugin process.
    /// Uses WAL + busy_timeout; does not replace DB files or kill Revit.
    /// Domain-specialized path for equipment; for arbitrary project-table DML use
    /// <see cref="WriteKeiDatabaseHandler"/> so AI agents can assist full project data work.
    /// </summary>
    public class ImportProjectEquipmentHandler : IRevitCommand
    {
        public string Name => "import_project_equipment";

        public string Description =>
            "Bulk-import equipment types + instances + typed specs into the KEI project SQLite DB " +
            "through the Revit process (WAL-safe, concurrent with KEI). " +
            "Pass items as a JSON array. Each item: projectTypeName (unique), categoryCode, " +
            "nameVN/nameEN, specsVN/specsEN, area, brand, unit, originalTag, specText, status, " +
            "quantity and/or instanceTags, optional specs[{parameterCode,value}]. " +
            "Options: dryRun, replaceMatching (upsert types by name + recreate instances for those types), " +
            "clearAiExtracted (delete all Status=AIExtracted types first), database=auto, busyTimeoutMs.";

        public string ParametersSchema => @"{
  ""type"": ""object"",
  ""properties"": {
    ""items"": {
      ""type"": ""array"",
      ""description"": ""Equipment items to import"",
      ""items"": {
        ""type"": ""object"",
        ""properties"": {
          ""projectTypeName"": { ""type"": ""string"" },
          ""categoryCode"": { ""type"": ""string"", ""default"": ""General"" },
          ""nameVN"": { ""type"": ""string"" },
          ""nameEN"": { ""type"": ""string"" },
          ""specsVN"": { ""type"": ""string"" },
          ""specsEN"": { ""type"": ""string"" },
          ""area"": { ""type"": ""string"" },
          ""brand"": { ""type"": ""string"" },
          ""unit"": { ""type"": ""string"" },
          ""originalTag"": { ""type"": ""string"" },
          ""specText"": { ""type"": ""string"" },
          ""status"": { ""type"": ""string"", ""default"": ""AIExtracted"" },
          ""quantity"": { ""type"": ""integer"", ""default"": 0 },
          ""instanceTags"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } },
          ""specs"": {
            ""type"": ""array"",
            ""items"": {
              ""type"": ""object"",
              ""properties"": {
                ""parameterCode"": { ""type"": ""string"" },
                ""value"": {}
              },
              ""required"": [""parameterCode"", ""value""]
            }
          }
        },
        ""required"": [""projectTypeName""]
      }
    },
    ""dryRun"": { ""type"": ""boolean"", ""default"": false },
    ""replaceMatching"": { ""type"": ""boolean"", ""default"": true },
    ""clearAiExtracted"": { ""type"": ""boolean"", ""default"": false },
    ""database"": { ""type"": ""string"", ""default"": ""auto"" },
    ""busyTimeoutMs"": { ""type"": ""integer"", ""default"": 30000 }
  },
  ""required"": [""items""]
}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            try
            {
                var request = string.IsNullOrWhiteSpace(paramsJson)
                    ? new JObject()
                    : JObject.Parse(paramsJson);

                var items = request["items"] as JArray;
                if (items == null || items.Count == 0)
                    return CommandResult.Fail("items array is required and must not be empty.");

                bool dryRun = request.Value<bool?>("dryRun") ?? false;
                bool replaceMatching = request.Value<bool?>("replaceMatching") ?? true;
                bool clearAiExtracted = request.Value<bool?>("clearAiExtracted") ?? false;
                string database = request.Value<string>("database") ?? "auto";
                int busyTimeoutMs = request.Value<int?>("busyTimeoutMs") ?? 30000;

                var resolved = KeiDatabaseResolver.ResolveWithProjectName(app);
                string dbPath = KeiDatabaseResolver.Resolve(app, database);
                if (string.IsNullOrEmpty(dbPath))
                    return CommandResult.Fail(
                        "No active KEI project database found. Open the Revit project and ensure " +
                        @"%APPDATA%\KEI\Database\Projects has a matching *_local.db.");

                // Pre-validate items
                var prepared = new List<ImportItem>();
                var failed = new List<object>();
                var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                for (int i = 0; i < items.Count; i++)
                {
                    var raw = items[i] as JObject;
                    if (raw == null)
                    {
                        failed.Add(new { index = i, error = "item must be an object" });
                        continue;
                    }

                    var name = (raw.Value<string>("projectTypeName") ?? "").Trim();
                    if (string.IsNullOrEmpty(name))
                    {
                        failed.Add(new { index = i, error = "projectTypeName is required" });
                        continue;
                    }
                    if (!seenNames.Add(name))
                    {
                        failed.Add(new { index = i, projectTypeName = name, error = "duplicate projectTypeName in payload" });
                        continue;
                    }

                    int qty = raw.Value<int?>("quantity") ?? 0;
                    var tags = new List<string>();
                    if (raw["instanceTags"] is JArray tagArr)
                    {
                        foreach (var t in tagArr)
                        {
                            var s = (t?.ToString() ?? "").Trim();
                            if (!string.IsNullOrEmpty(s)) tags.Add(s);
                        }
                    }
                    if (tags.Count == 0 && qty > 0)
                    {
                        for (int n = 1; n <= qty; n++)
                            tags.Add($"{name}-{n:D2}");
                    }

                    prepared.Add(new ImportItem
                    {
                        Index = i,
                        ProjectTypeName = name,
                        CategoryCode = string.IsNullOrWhiteSpace(raw.Value<string>("categoryCode"))
                            ? "General"
                            : raw.Value<string>("categoryCode").Trim(),
                        NameVN = raw.Value<string>("nameVN"),
                        NameEN = raw.Value<string>("nameEN"),
                        SpecsVN = raw.Value<string>("specsVN"),
                        SpecsEN = raw.Value<string>("specsEN"),
                        Area = raw.Value<string>("area"),
                        Brand = raw.Value<string>("brand"),
                        Unit = raw.Value<string>("unit"),
                        OriginalTag = raw.Value<string>("originalTag"),
                        SpecText = raw.Value<string>("specText"),
                        Status = string.IsNullOrWhiteSpace(raw.Value<string>("status"))
                            ? "AIExtracted"
                            : raw.Value<string>("status").Trim(),
                        InstanceTags = tags,
                        Specs = raw["specs"] as JArray
                    });
                }

                if (prepared.Count == 0)
                    return CommandResult.Fail(JsonConvert.SerializeObject(new
                    {
                        code = "NO_VALID_ITEMS",
                        failed
                    }));

                if (dryRun)
                {
                    int dryInstances = 0;
                    foreach (var p in prepared) dryInstances += p.InstanceTags.Count;
                    var sample = new List<object>();
                    for (int s = 0; s < prepared.Count && s < 5; s++)
                    {
                        var p = prepared[s];
                        sample.Add(new
                        {
                            p.ProjectTypeName,
                            p.CategoryCode,
                            p.Area,
                            instanceCount = p.InstanceTags.Count
                        });
                    }
                    return CommandResult.Ok(new
                    {
                        action = "import_project_equipment",
                        dryRun = true,
                        projectName = resolved.projectName,
                        dbPath,
                        itemCount = prepared.Count,
                        instanceCount = dryInstances,
                        failed,
                        sample
                    });
                }

                using (var conn = KeiDatabaseResolver.OpenReadWrite(dbPath, busyTimeoutMs))
                using (var tx = conn.BeginTransaction())
                {
                    try
                    {
                        // Exclusive-ish write intent under WAL
                        using (var begin = conn.CreateCommand())
                        {
                            begin.Transaction = tx;
                            begin.CommandText = "SELECT 1"; // connection already open; rely on IMMEDIATE via busy
                            begin.ExecuteScalar();
                        }

                        if (clearAiExtracted)
                        {
                            // Microsoft.Data.Sqlite executes one statement per command
                            string[] clears =
                            {
                                "DELETE FROM TypedSpecs WHERE ProjectTypeId IN (SELECT ProjectTypeId FROM ProjectEquipmentTypes WHERE Status = 'AIExtracted')",
                                "DELETE FROM OperatingConditions WHERE ProjectTypeId IN (SELECT ProjectTypeId FROM ProjectEquipmentTypes WHERE Status = 'AIExtracted')",
                                "DELETE FROM ProjectEquipments WHERE ProjectTypeId IN (SELECT ProjectTypeId FROM ProjectEquipmentTypes WHERE Status = 'AIExtracted')",
                                "DELETE FROM ProjectEquipmentTypes WHERE Status = 'AIExtracted'"
                            };
                            foreach (var sql in clears)
                            {
                                try
                                {
                                    using (var cmd = conn.CreateCommand())
                                    {
                                        cmd.Transaction = tx;
                                        cmd.CommandText = sql;
                                        cmd.ExecuteNonQuery();
                                    }
                                }
                                catch (SqliteException)
                                {
                                    // Optional tables (e.g. OperatingConditions) may be missing on older DBs
                                }
                            }
                        }

                        var validCategories = LoadCategorySet(conn, tx);
                        var conversion = LoadConversionMap(conn, tx);

                        int typesCreated = 0, typesUpdated = 0, instancesCreated = 0, specsWritten = 0;
                        var succeeded = new List<object>();

                        foreach (var item in prepared)
                        {
                            if (validCategories.Count > 0 && !validCategories.Contains(item.CategoryCode))
                            {
                                failed.Add(new
                                {
                                    index = item.Index,
                                    projectTypeName = item.ProjectTypeName,
                                    error = $"invalid categoryCode '{item.CategoryCode}'",
                                    validOptions = new List<string>(validCategories)
                                });
                                continue;
                            }

                            long typeId;
                            bool updated;
                            UpsertType(conn, tx, item, replaceMatching, out typeId, out updated);
                            if (updated) typesUpdated++; else typesCreated++;

                            if (replaceMatching)
                            {
                                // recreate instances/specs for this type
                                foreach (var sql in new[]
                                {
                                    "DELETE FROM TypedSpecs WHERE ProjectTypeId = @id",
                                    "DELETE FROM OperatingConditions WHERE ProjectTypeId = @id",
                                    "DELETE FROM ProjectEquipments WHERE ProjectTypeId = @id"
                                })
                                {
                                    try
                                    {
                                        using (var del = conn.CreateCommand())
                                        {
                                            del.Transaction = tx;
                                            del.CommandText = sql;
                                            del.Parameters.AddWithValue("@id", typeId);
                                            del.ExecuteNonQuery();
                                        }
                                    }
                                    catch (SqliteException) { /* optional table */ }
                                }
                            }

                            // Typed specs
                            if (item.Specs != null)
                            {
                                foreach (JObject sp in item.Specs)
                                {
                                    var code = sp.Value<string>("parameterCode");
                                    if (string.IsNullOrWhiteSpace(code)) continue;
                                    var key = (item.CategoryCode, code);
                                    if (!conversion.ContainsKey(key) && validCategories.Count > 0)
                                        continue; // skip unknown param for category

                                    WriteSpec(conn, tx, typeId, item.CategoryCode, code, sp["value"], conversion);
                                    specsWritten++;
                                }
                            }

                            // Instances
                            foreach (var tag in item.InstanceTags)
                            {
                                using (var cmd = conn.CreateCommand())
                                {
                                    cmd.Transaction = tx;
                                    cmd.CommandText = @"
INSERT OR IGNORE INTO ProjectEquipments (ProjectTypeId, TagNumber, Status)
VALUES (@typeId, @tag, 'Draft');";
                                    cmd.Parameters.AddWithValue("@typeId", typeId);
                                    cmd.Parameters.AddWithValue("@tag", tag);
                                    instancesCreated += cmd.ExecuteNonQuery();
                                }
                            }

                            succeeded.Add(new
                            {
                                projectTypeId = typeId,
                                projectTypeName = item.ProjectTypeName,
                                categoryCode = item.CategoryCode,
                                action = updated ? "updated" : "created",
                                instanceCount = item.InstanceTags.Count
                            });
                        }

                        tx.Commit();

                        return CommandResult.Ok(new
                        {
                            action = "import_project_equipment",
                            dryRun = false,
                            projectName = resolved.projectName,
                            dbPath,
                            typesCreated,
                            typesUpdated,
                            instancesCreated,
                            specsWritten,
                            succeededCount = succeeded.Count,
                            failedCount = failed.Count,
                            succeeded,
                            failed,
                            note = "Written via Revit plugin SQLite WAL connection. Do not replace .db files while Revit is open."
                        });
                    }
                    catch (Exception ex)
                    {
                        try { tx.Rollback(); } catch { /* ignore */ }
                        return CommandResult.Fail($"Import transaction failed (rolled back): {ex.Message}");
                    }
                }
            }
            catch (SqliteException ex)
            {
                return CommandResult.Fail(
                    $"SQLite error (busy/locked/corrupt?): {ex.Message}. " +
                    "Keep Revit open; do not kill Revit or replace the .db file. Retry with higher busyTimeoutMs.");
            }
            catch (Exception ex)
            {
                return CommandResult.Fail($"Error: {ex.Message}");
            }
        }

        private sealed class ImportItem
        {
            public int Index;
            public string ProjectTypeName;
            public string CategoryCode;
            public string NameVN;
            public string NameEN;
            public string SpecsVN;
            public string SpecsEN;
            public string Area;
            public string Brand;
            public string Unit;
            public string OriginalTag;
            public string SpecText;
            public string Status;
            public List<string> InstanceTags = new List<string>();
            public JArray Specs;
        }

        private static HashSet<string> LoadCategorySet(SqliteConnection conn, SqliteTransaction tx)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = "SELECT CategoryCode FROM EquipmentCategories";
                    using (var r = cmd.ExecuteReader())
                        while (r.Read()) set.Add(r.GetString(0));
                }
            }
            catch
            {
                // table may not exist on empty/old DBs — skip validation
            }
            return set;
        }

        private static Dictionary<(string cat, string code), (double? factor, string unit, string dataType)>
            LoadConversionMap(SqliteConnection conn, SqliteTransaction tx)
        {
            var map = new Dictionary<(string, string), (double?, string, string)>();
            try
            {
                using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText =
                        "SELECT CategoryCode, ParameterCode, ConversionFactor, DisplayUnit, DataType FROM CategoryParameters";
                    using (var r = cmd.ExecuteReader())
                    {
                        while (r.Read())
                        {
                            double? factor = r.IsDBNull(2) ? (double?)null : r.GetDouble(2);
                            string unit = r.IsDBNull(3) ? null : r.GetString(3);
                            string dt = r.IsDBNull(4) ? "numeric" : r.GetString(4);
                            map[(r.GetString(0), r.GetString(1))] = (factor, unit, dt);
                        }
                    }
                }
            }
            catch { /* optional */ }
            return map;
        }

        private static void UpsertType(
            SqliteConnection conn,
            SqliteTransaction tx,
            ImportItem item,
            bool replaceMatching,
            out long typeId,
            out bool updated)
        {
            long? existingId = null;
            using (var find = conn.CreateCommand())
            {
                find.Transaction = tx;
                find.CommandText = "SELECT ProjectTypeId FROM ProjectEquipmentTypes WHERE ProjectTypeName = @name";
                find.Parameters.AddWithValue("@name", item.ProjectTypeName);
                var o = find.ExecuteScalar();
                if (o != null && o != DBNull.Value) existingId = Convert.ToInt64(o);
            }

            if (existingId != null)
            {
                updated = true;
                typeId = existingId.Value;
                using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = @"
UPDATE ProjectEquipmentTypes SET
  CategoryCode = @cat,
  NameVN = @nvn,
  NameEN = @nen,
  SpecsVN = @svn,
  SpecsEN = @sen,
  Area = @area,
  Brand = @brand,
  Unit = @unit,
  OriginalTag = @otag,
  SpecText = @stext,
  Status = @status,
  MasterEquipmentId = COALESCE(MasterEquipmentId, 0)
WHERE ProjectTypeId = @id";
                    cmd.Parameters.AddWithValue("@cat", item.CategoryCode);
                    cmd.Parameters.AddWithValue("@nvn", (object)item.NameVN ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@nen", (object)item.NameEN ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@svn", (object)item.SpecsVN ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@sen", (object)item.SpecsEN ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@area", (object)item.Area ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@brand", (object)item.Brand ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@unit", (object)item.Unit ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@otag", (object)item.OriginalTag ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@stext", (object)item.SpecText ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@status", item.Status);
                    cmd.Parameters.AddWithValue("@id", typeId);
                    cmd.ExecuteNonQuery();
                }
                return;
            }

            updated = false;
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = @"
INSERT INTO ProjectEquipmentTypes
 (MasterEquipmentId, ProjectTypeName, NameVN, NameEN, SpecsVN, SpecsEN,
  Area, Brand, ProjectCapacity, Unit, OriginalTag, CategoryCode, SpecText, Status)
VALUES (0, @name, @nvn, @nen, @svn, @sen, @area, @brand, 0, @unit, @otag, @cat, @stext, @status);
SELECT last_insert_rowid();";
                cmd.Parameters.AddWithValue("@name", item.ProjectTypeName);
                cmd.Parameters.AddWithValue("@nvn", (object)item.NameVN ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@nen", (object)item.NameEN ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@svn", (object)item.SpecsVN ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@sen", (object)item.SpecsEN ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@area", (object)item.Area ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@brand", (object)item.Brand ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@unit", (object)item.Unit ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@otag", (object)item.OriginalTag ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@cat", item.CategoryCode);
                cmd.Parameters.AddWithValue("@stext", (object)item.SpecText ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@status", item.Status);
                typeId = (long)cmd.ExecuteScalar();
            }
        }

        private static void WriteSpec(
            SqliteConnection conn,
            SqliteTransaction tx,
            long typeId,
            string categoryCode,
            string parameterCode,
            JToken valueToken,
            Dictionary<(string cat, string code), (double? factor, string unit, string dataType)> conversion)
        {
            conversion.TryGetValue((categoryCode, parameterCode), out var def);
            string dataType = def.dataType ?? "numeric";
            string unit = def.unit;
            double? factor = def.factor;

            if (string.Equals(dataType, "text", StringComparison.OrdinalIgnoreCase) ||
                valueToken == null ||
                valueToken.Type == JTokenType.String)
            {
                string text = valueToken?.ToString();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = @"
INSERT INTO TypedSpecs (ProjectTypeId, ParameterCode, NumericValue, NumericValueSI, TextValue, Unit)
VALUES (@id, @code, NULL, NULL, @text, @unit)
ON CONFLICT(ProjectTypeId, ParameterCode) DO UPDATE SET
  TextValue = excluded.TextValue,
  Unit = excluded.Unit,
  NumericValue = NULL,
  NumericValueSI = NULL;";
                    cmd.Parameters.AddWithValue("@id", typeId);
                    cmd.Parameters.AddWithValue("@code", parameterCode);
                    cmd.Parameters.AddWithValue("@text", (object)text ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@unit", (object)unit ?? DBNull.Value);
                    cmd.ExecuteNonQuery();
                }
                return;
            }

            double num;
            if (valueToken.Type == JTokenType.Float || valueToken.Type == JTokenType.Integer)
                num = valueToken.Value<double>();
            else if (!double.TryParse(valueToken.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out num))
                return;

            double? si = factor.HasValue ? num * factor.Value : (double?)null;
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = @"
INSERT INTO TypedSpecs (ProjectTypeId, ParameterCode, NumericValue, NumericValueSI, TextValue, Unit)
VALUES (@id, @code, @num, @si, NULL, @unit)
ON CONFLICT(ProjectTypeId, ParameterCode) DO UPDATE SET
  NumericValue = excluded.NumericValue,
  NumericValueSI = excluded.NumericValueSI,
  Unit = excluded.Unit,
  TextValue = NULL;";
                cmd.Parameters.AddWithValue("@id", typeId);
                cmd.Parameters.AddWithValue("@code", parameterCode);
                cmd.Parameters.AddWithValue("@num", num);
                cmd.Parameters.AddWithValue("@si", (object)si ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@unit", (object)unit ?? DBNull.Value);
                cmd.ExecuteNonQuery();
            }
        }
    }
}
