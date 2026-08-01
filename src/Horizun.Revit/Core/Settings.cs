// -----------------------------------------------------------------------------
// Horizun MCP - original Horizun code.
//
// The few things that must be switched on deliberately.
//
// Read by BOTH halves: the server decides whether a tool is even advertised, the
// add-in decides whether it will run. Two checks, not one - the server and the
// plugin ship separately, so a stale server must not be able to enable something
// the machine's owner turned off, and a client that calls a tool it never saw in
// tools/list must still be refused at the far end.
//
// Deliberately dull: a JSON file the owner can read and edit in Notepad, at
//
//     %USERPROFILE%\.horizun\settings.json
//
// The location comes from HorizunPaths, which BOTH halves share. It used to be
// computed here from LocalApplicationData and again in six other places; see
// HorizunPaths.cs for why a per-process environment variable is the wrong root
// for state two processes must agree on.
//
// It is re-read on every use rather than cached, so switching something off takes
// effect on the next call instead of the next Revit restart. That matters most
// for the one setting that exists today: arbitrary code execution.
//
// Absence is OFF. A file that cannot be read, parsed, or found leaves every
// setting at its default, and every default here is the safe one.
// -----------------------------------------------------------------------------
using System;
using System.IO;
using System.Collections.Generic;
using Horizun.Contracts;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public static class Settings
    {
        public static string Path()
        {
            return HorizunPaths.SettingsPath();
        }

        /// <summary>
        /// Is horizun_execute_python allowed to run? Default FALSE.
        ///
        /// It runs arbitrary code inside Revit, on the UI thread, with full API access and
        /// the rights of the signed-in user. Nothing about that is wrong for a developer
        /// at their own machine and nothing about it belongs in a production surface, so
        /// it is off until somebody says otherwise in a file they own.
        /// </summary>
        public static bool ExecutePythonEnabled => ReadBool("enable_execute_python", false);

        /// <summary>read_only | safe_write (default) | full_write | unsafe_code.</summary>
        public static string PermissionProfile
        {
            get
            {
                JObject o = Read();
                string p = (o?.Value<string>("permission_profile") ?? "safe_write").ToLowerInvariant();
                return p == "read_only" || p == "safe_write" || p == "full_write" || p == "unsafe_code"
                    ? p : "read_only"; // malformed privilege never elevates
            }
        }

        public static bool IsToolAllowed(CommandContract contract, out string reason)
        {
            reason = null;
            if (contract == null) { reason = "Unknown tool contract."; return false; }
            JObject settings = Read();
            HashSet<string> denied = Strings(settings?["denied_tools"] as JArray);
            HashSet<string> allowed = Strings(settings?["allowed_tools"] as JArray);
            if (denied.Contains(contract.Name))
            { reason = contract.Name + " is disabled by denied_tools in " + Path() + "."; return false; }
            if (allowed.Count > 0 && !allowed.Contains(contract.Name))
            { reason = contract.Name + " is outside the allowed_tools allowlist in " + Path() + "."; return false; }

            string profile = PermissionProfile;
            if (profile == "read_only" &&
                (contract.Effect == ToolEffect.Mutating || contract.Effect == ToolEffect.MutatingUnlessDryRun ||
                 contract.Effect == ToolEffect.DocumentSession))
            { reason = contract.Name + " is hidden/refused by permission_profile=read_only in " + Path() + "."; return false; }
            if (profile == "safe_write" &&
                (contract.Effect == ToolEffect.DocumentSession ||
                 contract.Name == "horizun_open_document" || contract.Name == "horizun_save_document" ||
                 contract.Name == "horizun_relinquish_all" || contract.Name == "horizun_export"))
            {
                reason = contract.Name + " changes the Revit document session or writes external files and is " +
                         "hidden/refused by permission_profile=safe_write in " + Path() +
                         ". Use full_write only on machines authorized for those side effects.";
                return false;
            }
            if (contract.Name == "horizun_execute_python" &&
                (profile != "unsafe_code" || !ExecutePythonEnabled))
            {
                reason = "horizun_execute_python requires BOTH permission_profile=unsafe_code and " +
                         "enable_execute_python=true in " + Path() + ".";
                return false;
            }
            return true;
        }

        /// <summary>
        /// The sentence to show a caller who asked for a disabled capability: what is off,
        /// where to turn it on, and what turning it on means.
        /// </summary>
        public static string ExecutePythonRefusal()
        {
            return "horizun_execute_python is DISABLED. It runs arbitrary code inside Revit on the UI thread, " +
                   "with the full API and the rights of the signed-in user, so it is not part of the default " +
                   "surface. To enable it, put {\"permission_profile\":\"unsafe_code\"," +
                   "\"enable_execute_python\":true} in " + Path() + " - it is " +
                   "re-read on every call, so no restart is needed. Prefer a typed command for anything " +
                   "recurring: a typed command can be verified, and this cannot.";
        }

        private static JObject Read()
        {
            try
            {
                string p = Path();
                return File.Exists(p) ? JObject.Parse(File.ReadAllText(p)) : new JObject();
            }
            catch { return new JObject(); }
        }

        private static HashSet<string> Strings(JArray a)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (a != null) foreach (JToken t in a)
                if (t.Type == JTokenType.String && !string.IsNullOrWhiteSpace((string)t)) result.Add((string)t);
            return result;
        }

        private static bool ReadBool(string key, bool fallback)
        {
            try
            {
                JObject o = Read();
                JToken t = o[key];
                if (t == null || t.Type != JTokenType.Boolean) return fallback;
                return (bool)t;
            }
            catch
            {
                // An unreadable or malformed settings file must not enable anything. The
                // safe default is the one that survives a mistake in this file.
                return fallback;
            }
        }
    }
}
