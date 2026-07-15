using System;
using System.IO;
using Autodesk.Revit.UI;

namespace RvtMcp.Plugin.Handlers
{
    /// <summary>
    /// Returns the KEI database path for the currently active Revit document.
    /// </summary>
    public class GetActiveProjectDbHandler : IRevitCommand
    {
        public string Name => "get_active_project_db";
        public string Description => "Get the KEI SQLite database path for the currently active Revit project (WAL-aware resolve).";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{}}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            try
            {
                var doc = app.ActiveUIDocument?.Document;
                if (doc == null)
                    return CommandResult.Fail("No active document in Revit.");

                if (doc.IsFamilyDocument)
                    return CommandResult.Fail("Active document is a family, not a project.");

                var (projectName, dbPath) = KeiDatabaseResolver.ResolveWithProjectName(app);
                if (string.IsNullOrEmpty(projectName))
                    return CommandResult.Fail("Cannot determine project name from active document.");

                if (dbPath == null)
                {
                    string baseName = KeiDatabaseResolver.SanitizeFileName(projectName);
                    dbPath = Path.Combine(KeiDatabaseResolver.GetProjectsFolder(), $"{baseName}_local.db");
                }

                long walBytes = 0;
                var walPath = dbPath + "-wal";
                if (File.Exists(walPath))
                    walBytes = new FileInfo(walPath).Length;

                return CommandResult.Ok(new
                {
                    projectName,
                    databasePath = dbPath,
                    exists = File.Exists(dbPath),
                    fileName = Path.GetFileName(dbPath),
                    walBytes,
                    note = "Open DB via query_kei_database (read), write_kei_database (DML), or import_project_equipment — do not replace the file while Revit is open."
                });
            }
            catch (Exception ex)
            {
                return CommandResult.Fail($"Error resolving active project DB: {ex.Message}");
            }
        }
    }
}
