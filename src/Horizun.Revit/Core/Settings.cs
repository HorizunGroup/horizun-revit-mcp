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

        /// <summary>
        /// The sentence to show a caller who asked for a disabled capability: what is off,
        /// where to turn it on, and what turning it on means.
        /// </summary>
        public static string ExecutePythonRefusal()
        {
            return "horizun_execute_python is DISABLED. It runs arbitrary code inside Revit on the UI thread, " +
                   "with the full API and the rights of the signed-in user, so it is not part of the default " +
                   "surface. To enable it, put {\"enable_execute_python\": true} in " + Path() + " - it is " +
                   "re-read on every call, so no restart is needed. Prefer a typed command for anything " +
                   "recurring: a typed command can be verified, and this cannot.";
        }

        private static bool ReadBool(string key, bool fallback)
        {
            try
            {
                string p = Path();
                if (!File.Exists(p)) return fallback;

                JObject o = JObject.Parse(File.ReadAllText(p));
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
