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
// THE DEFAULTS ARE THE FULL SURFACE. During this early stage the product decision
// is that a fresh install exposes everything, including horizun_execute_python:
// an absent file, or a file without these keys, reads as permission_profile=
// unsafe_code and enable_execute_python=true. An EXPLICIT choice in the file -
// read_only, safe_write, full_write, or enable_execute_python=false - is always
// respected.
//
// One asymmetry is deliberate: a file that EXISTS but cannot be parsed falls
// CLOSED (read_only, Python off), not open. The owner may have written an
// explicit restriction into that file, and a corrupted byte must never convert
// "I turned this off" into "everything is enabled".
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
        /// Is horizun_execute_python allowed to run? Default TRUE.
        ///
        /// It runs arbitrary code inside Revit, on the UI thread, with full API access
        /// and the rights of the signed-in user. During this early stage it ships as the
        /// standing fallback for whatever the typed commands do not cover, so an absent
        /// file or an absent key means ON. An explicit enable_execute_python=false in
        /// the file is always respected, and a file that exists but cannot be parsed
        /// falls CLOSED - a corrupted explicit refusal must never read as consent.
        /// </summary>
        public static bool ExecutePythonEnabled
        {
            get
            {
                FileState state;
                JObject o = Read(out state);
                if (state == FileState.Malformed) return false;
                JToken t = o?["enable_execute_python"];
                if (t == null || t.Type != JTokenType.Boolean) return true;
                return (bool)t;
            }
        }

        /// <summary>read_only | safe_write | full_write | unsafe_code (default).</summary>
        public static string PermissionProfile
        {
            get
            {
                FileState state;
                JObject o = Read(out state);
                if (state == FileState.Malformed) return "read_only"; // an unreadable choice never elevates
                string p = o?.Value<string>("permission_profile");
                if (string.IsNullOrWhiteSpace(p)) return "unsafe_code"; // absent key: the default-on posture
                p = p.ToLowerInvariant();
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
                 contract.Name == "horizun_relinquish_all" || contract.Name == "horizun_export" ||
                 contract.Name == "horizun_power_bi_push" || contract.Name == "horizun_create_family"))
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
                         "enable_execute_python=true. Both DEFAULT to enabled, so this machine's " + Path() +
                         " restricts one of them (or is malformed, which falls closed). That explicit " +
                         "choice is respected; only the machine's owner reverses it.";
                return false;
            }
            return true;
        }

        /// <summary>
        /// The sentence to show a caller who asked for a capability this machine has
        /// switched off: what is off, why it is off, and where the owner turns it back on.
        /// </summary>
        public static string ExecutePythonRefusal()
        {
            return "horizun_execute_python is DISABLED ON THIS MACHINE. It is enabled by default, so this state " +
                   "is a deliberate choice recorded in " + Path() + " (an explicit enable_execute_python=false " +
                   "or a permission_profile below unsafe_code), or that file exists but could not be parsed - " +
                   "a malformed settings file falls closed rather than open. Respect the choice: do not edit the " +
                   "file yourself. If the MACHINE'S OWNER wants it back, they can run " +
                   "scripts/enable-execute-python.ps1, which restores exactly the two keys " +
                   "({\"permission_profile\":\"unsafe_code\",\"enable_execute_python\":true}) while preserving " +
                   "every other setting, and reverts with -Disable. The add-in re-reads settings on every call, " +
                   "but the MCP client caches the tool list at startup, so restart the client for the tool to " +
                   "appear. Meanwhile, use the typed commands: they cover most operations and verify their work.";
        }

        /// <summary>
        /// One raw string setting, for callers that take an injected reader (the receipt
        /// ledger's retention). Guarded like every read of a file the user owns.
        /// </summary>
        public static string RawValue(string key)
        {
            try { return Read()?.Value<string>(key); } catch { return null; }
        }

        /// <summary>
        /// Three states, because two of them look identical and must not act identical:
        /// an ABSENT file means "the owner never chose" and the defaults apply, while a
        /// MALFORMED file may be a corrupted explicit choice and everything falls closed.
        /// </summary>
        private enum FileState { Absent, Readable, Malformed }

        private static JObject Read(out FileState state)
        {
            try
            {
                string p = Path();
                if (!File.Exists(p)) { state = FileState.Absent; return new JObject(); }
                JObject o = JObject.Parse(File.ReadAllText(p));
                state = FileState.Readable;
                return o;
            }
            catch { state = FileState.Malformed; return new JObject(); }
        }

        private static JObject Read()
        {
            FileState ignored;
            return Read(out ignored);
        }

        private static HashSet<string> Strings(JArray a)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (a != null) foreach (JToken t in a)
                if (t.Type == JTokenType.String && !string.IsNullOrWhiteSpace((string)t)) result.Add((string)t);
            return result;
        }
    }
}
