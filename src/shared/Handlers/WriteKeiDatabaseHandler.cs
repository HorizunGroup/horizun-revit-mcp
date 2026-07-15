using System;
using System.Collections.Generic;
using System.IO;
using Autodesk.Revit.UI;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    /// <summary>
    /// General write path for the KEI project SQLite DB, executed inside the Revit
    /// plugin process (WAL + busy_timeout). Complements domain-specific
    /// <c>import_project_equipment</c> with open DML for any project table.
    ///
    /// Rationale: delivery is moving from human-only BIM / 3D modelling toward
    /// AI agents that implement projects faster and more accurately — agents
    /// need to update project metadata (not only equipment) while Revit holds
    /// the single-writer SQLite handle. External processes cannot write this DB
    /// safely; all writes must go through Revit. Prefer
    /// <c>import_project_equipment</c> for typed equipment bulk import; use this
    /// tool for broader project-data DML after validating with
    /// <c>query_kei_database</c> / <c>dryRun</c>.
    /// </summary>
    public class WriteKeiDatabaseHandler : IRevitCommand
    {
        public string Name => "write_kei_database";

        public string Description =>
            "Execute INSERT/UPDATE/DELETE/REPLACE (or WITH…DML) against the KEI project " +
            "SQLite DB via the Revit process (journal_mode=WAL, busy_timeout). " +
            "Not limited to equipment — any project table. " +
            "Pass sql= one statement, or statements= JSON array of statements. " +
            "dryRun=true validates only. DDL (CREATE/DROP/ALTER/PRAGMA/ATTACH) is blocked.";

        public string ParametersSchema => @"{
  ""type"": ""object"",
  ""properties"": {
    ""sql"": {
      ""type"": ""string"",
      ""description"": ""Single DML statement (INSERT/UPDATE/DELETE/REPLACE/WITH…DML)""
    },
    ""statements"": {
      ""type"": ""array"",
      ""description"": ""Multiple DML statements, executed in one transaction"",
      ""items"": { ""type"": ""string"" }
    },
    ""database"": { ""type"": ""string"", ""default"": ""auto"" },
    ""busyTimeoutMs"": { ""type"": ""integer"", ""default"": 30000 },
    ""dryRun"": { ""type"": ""boolean"", ""default"": false }
  }
}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            try
            {
                var request = string.IsNullOrWhiteSpace(paramsJson)
                    ? new JObject()
                    : JObject.Parse(paramsJson);

                string database = request.Value<string>("database") ?? "auto";
                int busyTimeoutMs = request.Value<int?>("busyTimeoutMs") ?? 30000;
                bool dryRun = request.Value<bool?>("dryRun") ?? false;

                var rawList = new List<string>();
                var single = request.Value<string>("sql");
                if (!string.IsNullOrWhiteSpace(single))
                    rawList.Add(single);

                if (request["statements"] is JArray arr)
                {
                    foreach (var t in arr)
                    {
                        var s = t?.Type == JTokenType.String ? t.Value<string>() : t?.ToString();
                        if (!string.IsNullOrWhiteSpace(s))
                            rawList.Add(s);
                    }
                }

                var statements = KeiSqlGuard.NormalizeStatements(rawList, out var normalizeError);
                if (normalizeError != null)
                    return CommandResult.Fail(normalizeError);

                string dbPath = KeiDatabaseResolver.Resolve(app, database);
                if (string.IsNullOrEmpty(dbPath))
                    return CommandResult.Fail(
                        "No active KEI project database found. Open the Revit project and ensure " +
                        @"%APPDATA%\KEI\Database\Projects has a matching *_local.db, " +
                        "or pass database=list via query_kei_database first.");

                if (!File.Exists(dbPath))
                    return CommandResult.Fail($"Database file does not exist: {dbPath}");

                var (projectName, _) = KeiDatabaseResolver.ResolveWithProjectName(app);

                if (dryRun)
                {
                    return CommandResult.Ok(new
                    {
                        action = "write_kei_database",
                        dryRun = true,
                        projectName,
                        database = Path.GetFileName(dbPath),
                        dbPath,
                        statementCount = statements.Count,
                        statements,
                        note = "Validated DML only — no changes applied. " +
                               "AI agents write project data through Revit (WAL); do not replace .db files."
                    });
                }

                var results = new List<object>();
                int totalRows = 0;

                using (var conn = KeiDatabaseResolver.OpenReadWrite(dbPath, busyTimeoutMs))
                using (var tx = conn.BeginTransaction())
                {
                    try
                    {
                        for (int i = 0; i < statements.Count; i++)
                        {
                            string sql = statements[i];
                            using (var cmd = conn.CreateCommand())
                            {
                                cmd.Transaction = tx;
                                cmd.CommandText = sql;
                                int affected = cmd.ExecuteNonQuery();
                                totalRows += affected;
                                results.Add(new
                                {
                                    index = i,
                                    rowsAffected = affected,
                                    sqlPreview = sql.Length > 200 ? sql.Substring(0, 200) + "…" : sql
                                });
                            }
                        }

                        tx.Commit();
                    }
                    catch (SqliteException ex)
                    {
                        try { tx.Rollback(); } catch { /* ignore */ }
                        return CommandResult.Fail(
                            $"SQLite write failed (transaction rolled back): {ex.Message} " +
                            $"(SqliteErrorCode={ex.SqliteErrorCode})");
                    }
                    catch (Exception)
                    {
                        try { tx.Rollback(); } catch { /* ignore */ }
                        throw;
                    }
                }

                return CommandResult.Ok(new
                {
                    action = "write_kei_database",
                    dryRun = false,
                    projectName,
                    database = Path.GetFileName(dbPath),
                    dbPath,
                    statementCount = statements.Count,
                    totalRowsAffected = totalRows,
                    results,
                    note = "Committed inside Revit process with WAL + busy_timeout. " +
                           "Keep Revit open; do not kill the host or replace .db/.db-wal."
                });
            }
            catch (Exception ex)
            {
                return CommandResult.Fail($"Error writing KEI database: {ex.Message}");
            }
        }
    }
}
