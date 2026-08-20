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
            "is opened, so nothing is upgraded. A file whose header cannot be read also comes back with its " +
            "first 8 bytes (signature/signature_means/is_revit_container), because Revit's own message for that " +
            "case only offers Revit-file causes and a ZIP renamed .rvt is not one. Read-only.";

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
            int readable = 0, unreadable = 0, missing = 0, notRevitFiles = 0;
            foreach (string p in plan.Files)
            {
                JObject probe = BasicFileProbe.Read(p);
                files.Add(probe);

                bool? exists = probe["exists"]?.Type == JTokenType.Boolean ? (bool?)probe.Value<bool>("exists") : null;
                if (exists == false) missing++;
                else if (probe["read_error"] != null && probe["read_error"].Type != JTokenType.Null)
                {
                    unreadable++;
                    // Counted separately because it is a DIFFERENT finding, and the one
                    // Revit's own error message hides: a file whose bytes are not a Revit
                    // container at all is not an unreadable model, it is not a model.
                    if (probe["is_revit_container"]?.Type == JTokenType.Boolean &&
                        probe.Value<bool>("is_revit_container") == false) notRevitFiles++;
                }
                else readable++;
            }

            return CommandResult.Ok(new JObject
            {
                ["files"] = files,
                ["count"] = plan.Files.Count,
                ["readable"] = readable,
                ["unreadable"] = unreadable,
                ["missing"] = missing,
                ["not_revit_files"] = notRevitFiles,
                ["total_matched"] = plan.TotalMatched,
                ["truncated"] = plan.Truncated,
                ["truncated_note"] = plan.Truncated
                    ? "More than " + FileInfoPaths.MaxFiles + " files matched; only the first " +
                      FileInfoPaths.MaxFiles + " were read. Narrow the folder or pattern, or pass paths explicitly."
                    : null,
                ["note"] = "Read from disk with BasicFileInfo. NOTHING was opened, so nothing was upgraded, and no " +
                           "document needed to be open. Each file's read_error names why it could not be read, when it could not.",
                ["unreadable_note"] =
                    "Every file with a read_error also carries 'signature' (its first " + FileSignature.Bytes +
                    " bytes in hex), 'signature_means' and 'is_revit_container'. READ THOSE BEFORE BELIEVING THE " +
                    "read_error: Revit's message for an unreadable header offers two causes and both are about " +
                    "Revit files, so a ZIP renamed .rvt reads as 'a newer format file'. " +
                    FileSignature.Ole + " is the OLE container a real .rvt/.rfa uses - only there is the version " +
                    "story worth believing. " + FileSignature.Zip + " is a ZIP and is not a model at all; " +
                    "not_revit_files counts those. Anything else comes back as hex with nothing claimed about it."
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
