// -----------------------------------------------------------------------------
// Horizun MCP - original Horizun code.
//
// horizun_file_info - read Revit files' headers off disk, WITHOUT opening any and
// WITHOUT needing an active document (story 5.20).
//
// The first thing every batch does is triage a folder: which year is each file,
// is it workshared, is it a central? Until this command that was hand-written in
// execute_python every time, and - worse - the bridge required an active document
// even for work that never touches one, so a batch had to create a blank project
// by hand just to have something "active". This reads BasicFileInfo straight from
// disk. Nothing is opened, so nothing is upgraded, and no document need be open.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public sealed class FileInfoCommand : ICommand
    {
        public string Name => "horizun_file_info";

        public string Description =>
            "Read Revit files' headers off disk (format/saved version, is_workshared, is_central, is_local, " +
            "central path) WITHOUT opening any of them and without an active document - the folder triage every " +
            "batch starts with. Pass 'paths' (a list) or 'folder' (swept for 'pattern', default *.rvt). Nothing " +
            "is opened, so nothing is upgraded. Read-only.";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            JObject req;
            try { req = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JObject.Parse(paramsJson); }
            catch (Exception ex) { return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message); }

            List<string> paths = null;
            if (req["paths"] is JArray arr)
            {
                paths = new List<string>();
                foreach (JToken t in arr)
                    if (t.Type == JTokenType.String) paths.Add((string)t);
            }
            string folder = req.Value<string>("folder");
            string pattern = req.Value<string>("pattern");
            bool recursive = req.Value<bool?>("recursive") ?? false;

            FileInfoPlan plan = FileInfoPaths.Resolve(paths, folder, pattern, recursive, ListFolder);
            if (!plan.Ok) return CommandResult.Fail(plan.Error);

            var files = new JArray();
            int readable = 0, unreadable = 0, missing = 0;
            foreach (string p in plan.Files)
            {
                JObject probe = BasicFileProbe.Read(p);
                files.Add(probe);

                bool? exists = probe["exists"]?.Type == JTokenType.Boolean ? (bool?)probe.Value<bool>("exists") : null;
                if (exists == false) missing++;
                else if (probe["read_error"] != null && probe["read_error"].Type != JTokenType.Null) unreadable++;
                else readable++;
            }

            return CommandResult.Ok(new JObject
            {
                ["files"] = files,
                ["count"] = plan.Files.Count,
                ["readable"] = readable,
                ["unreadable"] = unreadable,
                ["missing"] = missing,
                ["total_matched"] = plan.TotalMatched,
                ["truncated"] = plan.Truncated,
                ["truncated_note"] = plan.Truncated
                    ? "More than " + FileInfoPaths.MaxFiles + " files matched; only the first " +
                      FileInfoPaths.MaxFiles + " were read. Narrow the folder or pattern, or pass paths explicitly."
                    : null,
                ["note"] = "Read from disk with BasicFileInfo. NOTHING was opened, so nothing was upgraded, and no " +
                           "document needed to be open. Each file's read_error names why it could not be read, when it could not."
            });
        }

        /// <summary>The disk half, injected into the Revit-free resolver so its rule is testable.</summary>
        private static IEnumerable<string> ListFolder(string folder, string pattern, bool recursive)
        {
            if (!Directory.Exists(folder))
                throw new DirectoryNotFoundException("folder does not exist: " + folder);
            var opt = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            return Directory.GetFiles(folder, pattern, opt).OrderBy(p => p, StringComparer.OrdinalIgnoreCase);
        }
    }
}
