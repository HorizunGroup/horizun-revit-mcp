// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// Read a Revit file's header off disk WITHOUT opening it (story 5.20).
//
// BasicFileInfo.Extract reads the format, the saved version and the worksharing
// flags straight from the file, with no document open and no active document
// required - which is the first thing every batch needs and, until this command,
// was hand-written in execute_python every time. This is the reader; horizun_file_info
// is the command over it.
//
// Every failure is a NAMED value, never a blank that reads like "no problem": a
// file that is not there, a header BasicFileInfo cannot parse, and a version it
// read but could not turn into a year are three different facts, and a batch that
// triages formats has to tell them apart.
//
// A private ProbeFile in DocumentSessionCommand reads the same header for the
// open/save guards; it is coupled to that command's stat/error shaping and left
// as is. This is the canonical reader for the file-info surface.
// -----------------------------------------------------------------------------
using System;
using System.IO;
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public static class BasicFileProbe
    {
        /// <summary>
        /// One file's facts as JSON. Never throws: an unreadable file comes back with
        /// exists/read_error set, so a folder sweep never dies on one bad entry.
        /// </summary>
        public static JObject Read(string path)
        {
            var result = new JObject { ["path"] = path };

            if (string.IsNullOrWhiteSpace(path))
            {
                result["exists"] = null;
                result["read_error"] = "No path was given.";
                return result;
            }

            bool exists;
            try { exists = File.Exists(path); }
            catch (Exception ex)
            {
                result["exists"] = null;
                result["read_error"] = "Could not test the path: " + ex.Message;
                return result;
            }

            result["exists"] = exists;
            if (!exists) { result["read_error"] = "File does not exist."; return result; }

            try
            {
                var fi = new FileInfo(path);
                result["bytes"] = fi.Length;
                result["mb"] = Math.Round(fi.Length / 1048576.0, 2);
                result["modified_utc"] = fi.LastWriteTimeUtc.ToString("o");
            }
            catch (Exception ex) { result["stat_error"] = ex.Message; }

            try
            {
                BasicFileInfo info = BasicFileInfo.Extract(path);
                if (info == null)
                {
                    result["read_error"] = "BasicFileInfo.Extract returned nothing. This is probably not a Revit file.";
                    return result;
                }
                try
                {
                    result["format"] = Safe(() => info.Format);
                    // SavedInVersion is on the 2022 API and gone by 2026: reflection keeps
                    // one source for both without claiming it read empty where it is absent.
                    result["saved_in_version"] = ReflectString(info, "SavedInVersion");
                    result["is_workshared"] = SafeBool(() => info.IsWorkshared);
                    result["is_central"] = SafeBool(() => info.IsCentral);
                    result["is_local"] = SafeBool(() => info.IsLocal);
                    result["central_path"] = Safe(() => info.CentralPath);
                    result["revit_version"] = OpenGuard.NormalizeVersion(result.Value<string>("format")) ??
                                              OpenGuard.NormalizeVersion(result.Value<string>("saved_in_version"));
                    if (result["revit_version"] == null || result["revit_version"].Type == JTokenType.Null)
                        result["read_error"] = "BasicFileInfo read the file but no Revit year could be parsed from it " +
                                               "(format='" + (result.Value<string>("format") ?? "") + "', saved_in_version='" +
                                               (result.Value<string>("saved_in_version") ?? "") + "').";
                }
                finally
                {
                    var d = info as IDisposable;
                    if (d != null) { try { d.Dispose(); } catch { } }
                }
            }
            catch (Exception ex)
            {
                result["read_error"] = "BasicFileInfo could not read this file: " + ex.Message;
            }

            return result;
        }

        private static JToken Safe(Func<string> read) { try { return read(); } catch { return null; } }
        private static JToken SafeBool(Func<bool> read) { try { return read(); } catch { return null; } }

        private static JToken ReflectString(object target, string propertyName)
        {
            if (target == null) return null;
            try
            {
                var prop = target.GetType().GetProperty(propertyName);
                if (prop == null) return "(not available in this Revit API version)";
                var v = prop.GetValue(target, null);
                return v == null ? null : (JToken)v.ToString();
            }
            catch (Exception ex) { return "(unreadable: " + ex.Message + ")"; }
        }
    }
}
