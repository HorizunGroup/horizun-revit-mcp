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
// THE DEFAULTS ARE SAFE FOR AN UNATTENDED AGENT. A fresh install permits typed
// writes inside the active document, but it cannot open/close documents, write
// external files or execute arbitrary code: an absent file, or a file without
// these keys, reads as permission_profile=safe_write and
// enable_execute_python=false. Elevation is always an explicit owner decision.
//
// One asymmetry is deliberate: a file that EXISTS but cannot be parsed falls
// CLOSED (read_only, Python off), not open. The owner may have written an
// explicit restriction into that file, and a corrupted byte must never convert
// "I turned this off" into "everything is enabled".
//
// WHAT EACH PROFILE MEANS. The ladder is cumulative, and each rung is decided by
// ToolEffect rather than by a list of tool names - a list is what let an
// externally-effecting tool be admitted by a profile that forbids external
// effects, because the tool was added to the enum and not to the list:
//
//   read_only    reads, and steers the host (which Revit answers, what is
//                selected). It does not change the model, does not open or close
//                a document session, and writes NOTHING outside the model.
//   safe_write   the above, plus typed writes INSIDE the document.
//   full_write   the above, plus document sessions and typed external writes.
//   unsafe_code  the above, plus eligibility for horizun_execute_python. It is
//                never the implicit default.
//
// ToolEffect.HostState is why read_only still admits something that is not a
// pure read: horizun_target chooses WHICH Revit every later call talks to, and a
// read-only machine that cannot choose its Revit cannot read. It used to share a
// classification with the workbook writer, and refusing the whole bucket would
// have broken the profile this fix exists to protect.
//
// The matrix is asserted for every profile against every ToolEffect value in
// SettingsEffectMatrixTests, so a new effect nobody classified fails a test
// instead of quietly inheriting whichever branch happens to miss it.
// -----------------------------------------------------------------------------
using System;
using System.IO;
using System.Text;
using System.Collections.Generic;
using System.Threading;
using Horizun.Contracts;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public static class Settings
    {
        private static readonly object WriteLock = new object();

        public static string Path()
        {
            return HorizunPaths.SettingsPath();
        }

        /// <summary>
        /// Is horizun_execute_python allowed to run? Default FALSE.
        ///
        /// It runs arbitrary code inside Revit, on the UI thread, with full API access
        /// and the rights of the signed-in user. Enabling it requires an explicit true
        /// AND permission_profile=unsafe_code. An absent or non-boolean key means OFF.
        /// A separate execute_python_ui_grant_until_utc written by Revit may grant a
        /// bounded exception without leaving standing consent.
        /// </summary>
        public static bool ExecutePythonEnabled
        {
            get
            {
                FileState state;
                JObject o = Read(out state);
                if (state == FileState.Malformed) return false;
                if (TemporaryExecutePythonGrant(o, DateTimeOffset.UtcNow, out _)) return true;
                JToken t = o?["enable_execute_python"];
                return t != null && t.Type == JTokenType.Boolean && (bool)t;
            }
        }

        /// <summary>
        /// Active human grant written by the Revit ribbon. Unlike the durable admin
        /// switch it does not change permission_profile and therefore cannot leave the
        /// machine elevated after expiry.
        /// </summary>
        public static DateTimeOffset? ExecutePythonTemporaryGrantUntilUtc
        {
            get
            {
                FileState state;
                JObject o = Read(out state);
                if (state == FileState.Malformed) return null;
                return TemporaryExecutePythonGrant(o, DateTimeOffset.UtcNow, out DateTimeOffset until)
                    ? (DateTimeOffset?)until : null;
            }
        }

        /// <summary>read_only | safe_write (default) | full_write | unsafe_code.</summary>
        public static string PermissionProfile
        {
            get
            {
                FileState state;
                JObject o = Read(out state);
                if (state == FileState.Malformed) return "read_only"; // an unreadable choice never elevates
                string p = o?.Value<string>("permission_profile");
                if (string.IsNullOrWhiteSpace(p)) return "safe_write";
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
            bool temporaryPythonGrant = contract.Name == "horizun_execute_python" &&
                                        profile != "read_only" &&
                                        ExecutePythonTemporaryGrantUntilUtc != null;

            // ExternalSideEffect is consulted by BOTH restrictive profiles, and that is
            // the fix rather than a detail. The classification exists precisely to mean
            // "this reaches outside the model", and neither profile used to ask about it
            // - so every tool carrying it was admitted by both. horizun_excel_write_rows
            // is one: a machine set to read_only refused to move a wall and then rewrote
            // a workbook on disk. Deciding on the ENUM rather than on a list of names is
            // what keeps the next externally-effecting tool from repeating it.
            if (!temporaryPythonGrant && profile == "read_only" &&
                (contract.Effect == ToolEffect.Mutating || contract.Effect == ToolEffect.MutatingUnlessDryRun ||
                 contract.Effect == ToolEffect.DocumentSession || contract.Effect == ToolEffect.ExternalSideEffect))
            {
                reason = contract.Name + " is hidden/refused by permission_profile=read_only in " + Path() +
                         ": read_only changes nothing - not the model, not the document session, and nothing " +
                         "written outside it.";
                return false;
            }
            if (!temporaryPythonGrant && profile == "safe_write" &&
                (contract.Effect == ToolEffect.DocumentSession || contract.Effect == ToolEffect.ExternalSideEffect ||
                 // Named as well as classified: these write outside the model while being
                 // classified MutatingUnlessDryRun, so the effect alone does not catch them.
                 contract.Name == "horizun_open_document" || contract.Name == "horizun_save_document" ||
                 contract.Name == "horizun_relinquish_all" || contract.Name == "horizun_export" ||
                 contract.Name == "horizun_power_bi_push" || contract.Name == "horizun_create_family"))
            {
                reason = contract.Name + " changes the Revit document session or writes external files and is " +
                         "hidden/refused by permission_profile=safe_write in " + Path() +
                         ". safe_write permits typed writes INSIDE the document only. Use full_write only on " +
                         "machines authorized for those side effects.";
                return false;
            }
            if (contract.Name == "horizun_execute_python" &&
                (!temporaryPythonGrant &&
                 (profile != "unsafe_code" || !ExecutePythonEnabled)))
            {
                reason = "horizun_execute_python requires explicit permission_profile=unsafe_code and " +
                         "enable_execute_python=true in " + Path() + ", OR a still-active temporary grant made " +
                         "from the Revit ribbon. It is OFF on a fresh install. Only the machine's owner may " +
                         "grant or renew that privilege.";
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
            return "horizun_execute_python is DISABLED ON THIS MACHINE. This is the safe default: arbitrary " +
                   "code requires explicit owner consent in " + Path() + ". Respect the choice: do not edit the " +
                   "file yourself. If the MACHINE'S OWNER needs the developer escape hatch, they can grant a " +
                   "time-limited or durable opt-in with scripts/enable-execute-python.ps1; -Disable revokes it. " +
                   "The add-in re-reads settings on every call, and the server announces a standard " +
                   "tools/list_changed notification. If a client does not implement that notification, restart " +
                   "it once for the tool to appear. Meanwhile, use typed commands: they cover most operations " +
                   "and verify their work.";
        }

        /// <summary>
        /// Grant arbitrary Python from an explicit Revit UI action. The grant is bounded
        /// to four hours even if a caller passes a larger duration; the ribbon currently
        /// asks for one hour. The durable administrator switch is deliberately untouched.
        /// </summary>
        public static bool TryGrantExecutePythonTemporarily(
            TimeSpan duration, out DateTimeOffset untilUtc, out string error)
        {
            untilUtc = default(DateTimeOffset);
            error = null;
            if (duration <= TimeSpan.Zero || duration > TimeSpan.FromHours(4))
            {
                error = "The temporary Python grant must be greater than zero and no longer than four hours.";
                return false;
            }

            DateTimeOffset requestedUntil = DateTimeOffset.UtcNow.Add(duration);
            untilUtc = requestedUntil;
            return TryUpdate(o =>
            {
                o["execute_python_ui_grant_until_utc"] = requestedUntil.ToString("O");
                return true;
            }, out error);
        }

        /// <summary>
        /// The Revit OFF button is an emergency stop, not merely expiry cleanup: it
        /// revokes both the temporary UI grant and any durable enable flag. It leaves
        /// the permission profile unchanged because an administrator may still need
        /// full_write for typed external operations.
        /// </summary>
        public static bool TryRevokeExecutePython(out string error)
        {
            return TryUpdate(o =>
            {
                o["enable_execute_python"] = false;
                o.Remove("execute_python_ui_grant_until_utc");
                return true;
            }, out error);
        }

        public static bool TryClearExecutePythonTemporaryGrant(out string error)
        {
            return TryUpdate(o =>
            {
                o.Remove("execute_python_ui_grant_until_utc");
                return true;
            }, out error);
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
        /// Raw retention setting with malformed-file provenance preserved. Returning
        /// null means the owner never selected this key and bounded defaults may apply;
        /// an unreadable settings file returns a deliberately invalid value so retention
        /// fails closed instead of mistaking corruption for absence and deleting data.
        /// </summary>
        public static string RetentionValue(string key)
        {
            FileState state;
            JObject o = Read(out state);
            return state == FileState.Malformed ? "invalid-settings-file" : o?.Value<string>(key);
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

        private static bool TemporaryExecutePythonGrant(
            JObject settings, DateTimeOffset nowUtc, out DateTimeOffset untilUtc)
        {
            untilUtc = default(DateTimeOffset);
            JToken t = settings?["execute_python_ui_grant_until_utc"];
            if (t == null) return false;
            if (t.Type == JTokenType.Date)
            {
                object value = ((JValue)t).Value;
                if (value is DateTimeOffset dto) untilUtc = dto;
                else if (value is DateTime dt)
                {
                    if (dt.Kind == DateTimeKind.Unspecified)
                        dt = DateTime.SpecifyKind(dt, DateTimeKind.Utc);
                    untilUtc = new DateTimeOffset(dt.ToUniversalTime());
                }
                else return false;
                return untilUtc.ToUniversalTime() > nowUtc.ToUniversalTime();
            }
            if (t.Type != JTokenType.String) return false;
            if (!DateTimeOffset.TryParse(
                    (string)t,
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal |
                    System.Globalization.DateTimeStyles.AdjustToUniversal,
                    out untilUtc)) return false;
            return untilUtc > nowUtc;
        }

        private static bool TryUpdate(Func<JObject, bool> update, out string error)
        {
            error = null;
            lock (WriteLock)
            {
                using (var mutex = new Mutex(false, "Local\\Horizun.Revit.Settings.V1"))
                {
                    bool held = false;
                    try
                    {
                        try { held = mutex.WaitOne(TimeSpan.FromSeconds(15)); }
                        catch (AbandonedMutexException) { held = true; }
                        if (!held)
                        {
                            error = "Timed out waiting for another Revit process to finish updating settings.json. Nothing changed.";
                            return false;
                        }
                        return TryUpdateLocked(update, out error);
                    }
                    catch (Exception ex)
                    {
                        error = "Could not acquire the cross-process settings lock: " + ex.Message;
                        return false;
                    }
                    finally { if (held) try { mutex.ReleaseMutex(); } catch { } }
                }
            }
        }

        private static bool TryUpdateLocked(Func<JObject, bool> update, out string error)
        {
            error = null;
            string path = Path();
            string temp = null;
            try
            {
                JObject settings;
                string originalRaw = null;
                bool originallyExisted = File.Exists(path);
                if (originallyExisted)
                {
                    originalRaw = File.ReadAllText(path);
                    try { settings = JObject.Parse(originalRaw); }
                    catch
                    {
                        error = "settings.json is malformed. It remains fail-closed and was not overwritten: " + path;
                        return false;
                    }
                }
                else settings = new JObject();

                if (!update(settings))
                {
                    error = "The requested settings update was refused.";
                    return false;
                }

                string directory = System.IO.Path.GetDirectoryName(path);
                if (string.IsNullOrWhiteSpace(directory))
                {
                    error = "The settings path has no parent directory: " + path;
                    return false;
                }
                Directory.CreateDirectory(directory);
                temp = System.IO.Path.Combine(directory, ".settings-" + Guid.NewGuid().ToString("N") + ".tmp");
                File.WriteAllText(temp, settings.ToString(Newtonsoft.Json.Formatting.Indented), new UTF8Encoding(false));

                // Non-cooperating editors do not take our named mutex. Refuse their
                // concurrent change instead of restoring an older privilege snapshot.
                bool existsNow = File.Exists(path);
                if (existsNow != originallyExisted ||
                    (existsNow && !string.Equals(File.ReadAllText(path), originalRaw, StringComparison.Ordinal)))
                {
                    error = "settings.json changed while the Revit permission dialog was open. Nothing was overwritten; try again.";
                    return false;
                }

                if (existsNow)
                {
                    string backup = path + ".horizun-ui-bak-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff") +
                                    "-" + Guid.NewGuid().ToString("N");
                    File.Replace(temp, path, backup);
                    PruneUiBackups(directory, System.IO.Path.GetFileName(path));
                }
                else File.Move(temp, path);
                temp = null;
                return true;
            }
            catch (Exception ex)
            {
                error = "Could not update " + path + ": " + ex.Message;
                return false;
            }
            finally
            {
                if (temp != null) try { File.Delete(temp); } catch { }
            }
        }

        private static void PruneUiBackups(string directory, string settingsFileName)
        {
            try
            {
                var backups = new DirectoryInfo(directory)
                    .GetFiles(settingsFileName + ".horizun-ui-bak-*");
                Array.Sort(backups, (a, b) => b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc));
                for (int i = 3; i < backups.Length; i++)
                    try { backups[i].Delete(); } catch { }
            }
            catch { /* backup retention must never turn a successful revoke into failure */ }
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
