// -----------------------------------------------------------------------------
// Horizun MCP server - original Horizun code.
//
// THE TARGET, REMEMBERED FOR A SESSION THAT OUTLIVES ITS SERVER PROCESS.
//
// field cycle (2026-10-24, T2): every agent called horizun_target about 64 times - one
// before each write - because the harness (hz-call.ps1) starts a NEW server process
// per call, and a target chosen in memory dies with the process. Two ways now:
//
//   HORIZUN_REVIT_PID=<pid>     pins one instance from the first line, no tool call.
//                               A pid that published no bridge is NOT dropped back to
//                               automatic: calls fail naming it, because silently
//                               sending a write to another Revit is the failure this
//                               whole selection exists to prevent.
//   HORIZUN_SESSION_KEY=<key>   horizun_target with pid or year REMEMBERS the choice
//                               under <data root>/session-targets/<key>.json, and the
//                               next server of that key starts on it. A remembered pid
//                               whose Revit is gone is discarded and said, never
//                               restored onto another process; 'auto' forgets it.
// Without a key nothing is written: two agents sharing one default file would
// point each other at the wrong Revit.
// -----------------------------------------------------------------------------
using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace Horizun.Server
{
    internal static class TargetMemory
    {
        public const string PidVariable = "HORIZUN_REVIT_PID";
        public const string SessionVariable = "HORIZUN_SESSION_KEY";

        /// <summary>What the startup decided, for health and horizun_target to say.</summary>
        public static string StartupNote { get; private set; }

        public static string SessionKey()
        {
            string k = Environment.GetEnvironmentVariable(SessionVariable);
            return string.IsNullOrWhiteSpace(k) ? null : k.Trim();
        }

        /// <summary>A key as a file name: letters, digits, dot, dash and underscore; anything else refused.</summary>
        public static bool ValidKey(string key) => key != null && key.Length <= 80 && Regex.IsMatch(key, "^[A-Za-z0-9._-]+$");

        public static string FileFor(string key, string root) => Path.Combine(root, "session-targets", key + ".json");

        /// <summary>The selection a new server starts on, and why. Reads the environment and the remembered file.</summary>
        public static TargetSelection Initial(Func<int, bool> pidPublishedAndAlive, string root, out string note)
        {
            note = null;
            string pidText = Environment.GetEnvironmentVariable(PidVariable);
            if (!string.IsNullOrWhiteSpace(pidText))
            {
                if (int.TryParse(pidText.Trim(), out int pid) && pid > 0)
                {
                    note = PidVariable + "=" + pid + (pidPublishedAndAlive(pid) ? "" : " - but no running Revit with that pid has published a bridge; calls will fail, not move to another Revit");
                    return TargetSelection.ByPid(pid, PidVariable);
                }
                note = PidVariable + " is not a process id ('" + pidText + "'); ignored.";
            }
            string key = SessionKey();
            if (key == null) return TargetSelection.Automatic;
            if (!ValidKey(key)) { note = SessionVariable + " '" + key + "' is not a usable key (letters, digits, . _ - up to 80); nothing is remembered."; return TargetSelection.Automatic; }
            TargetSelection remembered = Read(key, root, out string why);
            if (remembered == null) { if (why != null) note = why; return TargetSelection.Automatic; }
            if (remembered.Pid != null && !pidPublishedAndAlive(remembered.Pid.Value))
            {
                Forget(key, root);
                note = "the target remembered for session '" + key + "' (pid " + remembered.Pid + ") is no longer running; it was forgotten and the selection is automatic.";
                return TargetSelection.Automatic;
            }
            note = "restored from session '" + key + "'";
            return remembered;
        }

        public static void SetStartupNote(string note) => StartupNote = note;

        public static TargetSelection Read(string key, string root, out string why)
        {
            why = null;
            string file = FileFor(key, root);
            if (!File.Exists(file)) return null;
            try
            {
                JObject o = JObject.Parse(File.ReadAllText(file));
                string origin = "remembered for session '" + key + "'";
                if (o["pid"]?.Type == JTokenType.Integer && !string.IsNullOrWhiteSpace((string)o["document"]))
                    return TargetSelection.ByDocument((int)o["pid"], (string)o["document"], origin);
                if (o["pid"]?.Type == JTokenType.Integer) return TargetSelection.ByPid((int)o["pid"], origin);
                if (!string.IsNullOrWhiteSpace((string)o["year"])) return TargetSelection.ByYear((string)o["year"], origin);
                why = "the remembered target for session '" + key + "' names neither a pid nor a year; ignored.";
            }
            catch (Exception ex) { why = "the remembered target for session '" + key + "' could not be read (" + ex.Message + "); ignored."; }
            return null;
        }

        /// <summary>Remember (or, for automatic, forget) the selection under the session key. Returns what was done, or null with no key.</summary>
        public static string Remember(TargetSelection t, string root)
        {
            string key = SessionKey();
            if (key == null || !ValidKey(key)) return null;
            if (t == null || t.IsAutomatic) { Forget(key, root); return "forgotten for session '" + key + "'"; }
            string file = FileFor(key, root);
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            var o = new JObject { ["pid"] = t.Pid, ["year"] = t.Year, ["document"] = t.Document, ["saved_utc"] = DateTime.UtcNow.ToString("o") };
            string tmp = file + ".tmp";
            File.WriteAllText(tmp, o.ToString());
            if (File.Exists(file)) File.Delete(file);
            File.Move(tmp, file);
            return "remembered for session '" + key + "'";
        }

        public static void Forget(string key, string root)
        {
            try { string f = FileFor(key, root); if (File.Exists(f)) File.Delete(f); } catch { }
        }
    }
}
