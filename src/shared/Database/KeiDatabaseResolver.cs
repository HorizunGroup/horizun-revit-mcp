using System;
using System.IO;
using System.Linq;
using Autodesk.Revit.UI;
using Microsoft.Data.Sqlite;

namespace RvtMcp.Plugin
{
    /// <summary>
    /// Resolve KEI project SQLite path from the active Revit document and open
    /// connections with WAL + busy_timeout so MCP can share the DB with KEI UI
    /// without file-replace or killing Revit.
    ///
    /// Context: project delivery is shifting from humans alone authoring BIM /
    /// 3D models to AI agents that help implement projects faster and more
    /// accurately. Agents must read/write project SQLite through the Revit
    /// process (single writer + WAL) — never by replacing files from outside.
    /// </summary>
    public static class KeiDatabaseResolver
    {
        private static readonly string ProjectsFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "KEI", "Database", "Projects");

        private static readonly object _sqliteInitLock = new object();
        private static bool _sqliteProviderReady;

        /// <summary>
        /// Microsoft.Data.Sqlite requires SQLitePCL.Batteries.Init() once per process
        /// before any connection. KEI host may already have done this; plugin must not assume.
        /// </summary>
        public static void EnsureSqliteProvider()
        {
            if (_sqliteProviderReady) return;
            lock (_sqliteInitLock)
            {
                if (_sqliteProviderReady) return;
                try
                {
                    SQLitePCL.Batteries_V2.Init();
                }
                catch
                {
                    // Older package name / already initialized via Batteries.Init()
                    try { SQLitePCL.Batteries.Init(); } catch { /* rethrow on open if still broken */ }
                }
                _sqliteProviderReady = true;
            }
        }

        public static string GetProjectsFolder() => ProjectsFolder;

        public static string Resolve(UIApplication app, string database = "auto")
        {
            if (!Directory.Exists(ProjectsFolder)) return null;

            if (string.IsNullOrWhiteSpace(database) ||
                database.Equals("auto", StringComparison.OrdinalIgnoreCase))
                return ResolveFromRevitDocument(app) ?? DetectByWalHeuristic();

            return Directory.GetFiles(ProjectsFolder, "*.db")
                .FirstOrDefault(f => Path.GetFileName(f)
                    .IndexOf(database, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        public static string ResolveFromRevitDocument(UIApplication app)
        {
            try
            {
                var doc = app?.ActiveUIDocument?.Document;
                if (doc == null || doc.IsFamilyDocument) return null;

                string rawName = Path.GetFileNameWithoutExtension(doc.PathName);
                if (string.IsNullOrEmpty(rawName)) rawName = doc.Title;
                if (string.IsNullOrEmpty(rawName)) return null;

                string baseName = SanitizeFileName(rawName);

                string workingFileName;
                if (doc.IsWorkshared)
                {
                    string userName = SanitizeFileName(doc.Application.Username);
                    string suffix = $"_{userName}";
                    workingFileName = baseName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
                        ? $"{baseName}_local.db"
                        : $"{baseName}{suffix}_local.db";
                }
                else
                {
                    workingFileName = $"{baseName}_local.db";
                }

                string workingDbPath = Path.Combine(ProjectsFolder, workingFileName);
                if (File.Exists(workingDbPath)) return workingDbPath;

                string committedPath = Path.Combine(ProjectsFolder, $"{baseName}.db");
                if (File.Exists(committedPath)) return committedPath;

                return null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Prefer the local DB with the largest active WAL (likely the open project).
        /// </summary>
        public static string DetectByWalHeuristic()
        {
            if (!Directory.Exists(ProjectsFolder)) return null;

            return Directory.GetFiles(ProjectsFolder, "*_local.db")
                .Select(db =>
                {
                    var walFile = db + "-wal";
                    long walSize = File.Exists(walFile) ? new FileInfo(walFile).Length : 0;
                    return new { Path = db, WalSize = walSize, LastWrite = new FileInfo(db).LastWriteTime };
                })
                .Where(x => x.WalSize > 0)
                .OrderByDescending(x => x.WalSize)
                .ThenByDescending(x => x.LastWrite)
                .Select(x => x.Path)
                .FirstOrDefault();
        }

        public static (string projectName, string dbPath) ResolveWithProjectName(UIApplication app)
        {
            var doc = app?.ActiveUIDocument?.Document;
            if (doc == null || doc.IsFamilyDocument)
                return (null, null);

            string rawName = Path.GetFileNameWithoutExtension(doc.PathName);
            if (string.IsNullOrEmpty(rawName)) rawName = doc.Title;
            if (string.IsNullOrEmpty(rawName)) return (null, null);

            return (rawName, ResolveFromRevitDocument(app));
        }

        public static SqliteConnection OpenReadOnly(string dbPath, int busyTimeoutMs = 5000)
        {
            EnsureSqliteProvider();
            var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
            conn.Open();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "PRAGMA query_only = ON";
                cmd.ExecuteNonQuery();
                cmd.CommandText = $"PRAGMA busy_timeout = {Math.Max(0, busyTimeoutMs)}";
                cmd.ExecuteNonQuery();
                cmd.CommandText = "PRAGMA cache_size = -4000";
                cmd.ExecuteNonQuery();
            }
            return conn;
        }

        /// <summary>
        /// Read-write connection: WAL journal + busy_timeout for concurrent KEI access.
        /// Never replace the .db file while Revit/KEI holds the handle.
        /// </summary>
        public static SqliteConnection OpenReadWrite(string dbPath, int busyTimeoutMs = 30000)
        {
            EnsureSqliteProvider();
            var conn = new SqliteConnection($"Data Source={dbPath}");
            conn.Open();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = $"PRAGMA busy_timeout = {Math.Max(0, busyTimeoutMs)}";
                cmd.ExecuteNonQuery();
                cmd.CommandText = "PRAGMA journal_mode = WAL";
                cmd.ExecuteNonQuery();
                cmd.CommandText = "PRAGMA synchronous = NORMAL";
                cmd.ExecuteNonQuery();
            }
            return conn;
        }

        public static string SanitizeFileName(string name)
        {
            if (string.IsNullOrEmpty(name)) return name;
            foreach (char c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return name.Trim();
        }
    }
}
