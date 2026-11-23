// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// The Revit half of the family version check (rules in Core/FamilyVersionRules):
// read the .rfa's saved-in year with BasicFileInfo - nothing is opened - and, when it
// is newer than this Revit, look next to it for the same family saved in this year
// or older. Called before every path that hands an .rfa to OpenDocumentFile or
// LoadFamily: create_family source_path and nested instances, annotate's room tag
// family, load_tag_family.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    internal static class FamilyVersionGate
    {
        /// <summary>The refusal message when the family is newer than this Revit; null when it may be loaded.</summary>
        public static string NewerError(Application app, string path, out JObject detail)
        {
            detail = null;
            string host = null, saved = null;
            try { host = app?.VersionNumber; } catch { }
            try { saved = BasicFileInfo.Extract(path)?.Format; } catch { saved = null; }
            if (FamilyVersionRules.Judge(saved, host) != FamilyVersionVerdict.Newer) return null;
            List<FamilyAlternate> alternates = FindAlternates(path, host);
            detail = new JObject
            {
                ["reason"] = FamilyVersionRules.Reason,
                ["family_path"] = path,
                ["family_saved_in_version"] = saved,
                ["host_version"] = host,
                ["alternates"] = new JArray(alternates.Select(a => new JObject { ["path"] = a.Path, ["saved_in_version"] = a.SavedIn })),
                ["changes_applied"] = false
            };
            return FamilyVersionRules.NewerMessage(Path.GetFileName(path), saved, host, alternates);
        }

        /// <summary>A coded refusal for a command that returns CommandResult; null when the family may be loaded.</summary>
        public static CommandResult Refusal(Application app, string path)
        {
            string error = NewerError(app, path, out JObject detail);
            return error == null ? null : CommandResult.FailCoded(ErrorCodes.FamiliaVersionMasNueva, error, extra: detail);
        }

        /// <summary>Same family name in the file's folder and its sibling folders, saved in this Revit's year or older.</summary>
        private static List<FamilyAlternate> FindAlternates(string path, string host)
        {
            var found = new List<FamilyAlternate>();
            try
            {
                string stem = Path.GetFileNameWithoutExtension(path);
                string dir = Path.GetDirectoryName(path);
                var folders = new List<string> { dir };
                string parent = Path.GetDirectoryName(dir);
                if (!string.IsNullOrEmpty(parent))
                    try { folders.AddRange(Directory.EnumerateDirectories(parent).Where(d => !string.Equals(d, dir, StringComparison.OrdinalIgnoreCase)).Take(50)); } catch { }
                int examined = 0;
                foreach (string folder in folders)
                {
                    IEnumerable<string> files;
                    try { files = Directory.EnumerateFiles(folder, "*.rfa", SearchOption.TopDirectoryOnly); } catch { continue; }
                    foreach (string f in files)
                    {
                        if (++examined > FamilyVersionRules.MaxAlternatesExamined) return found;
                        if (string.Equals(Path.GetFullPath(f), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase)) continue;
                        string candidateStem = Path.GetFileNameWithoutExtension(f);
                        if (!FamilyVersionRules.SameFamilyName(stem, candidateStem)) continue;
                        string saved;
                        try { saved = BasicFileInfo.Extract(f)?.Format; } catch { continue; }
                        if (FamilyVersionRules.IsAlternate(stem, candidateStem, saved, host))
                            found.Add(new FamilyAlternate { Path = f, SavedIn = saved });
                    }
                }
            }
            catch { }
            return found;
        }
    }
}
