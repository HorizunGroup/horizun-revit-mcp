// -----------------------------------------------------------------------------
// Horizun MCP - original Horizun code.
//
// WHERE HORIZUN KEEPS ITS STATE. One answer, for both halves.
//
// This file exists because there were SEVEN answers. Settings, discovery, jobs
// and the two logs each computed their own path from
// Environment.SpecialFolder.LocalApplicationData, in two projects that ship
// separately - and every one of them was a different line of code that happened
// to agree.
//
// They only happen to agree while LocalApplicationData means the same thing in
// every process. It does not have to:
//
//   - A PACKAGED HOST REDIRECTS IT. Under MSIX/AppContainer, FOLDERID_LocalAppData
//     resolves into the package's own LocalCache, per package. The MCP server is
//     launched by the MCP client and Revit is launched by the user, so if the
//     client is packaged the two get different roots and neither is told.
//   - A different user or elevation context is a different profile outright.
//   - Folder-redirection policy moves it for some processes and not others.
//
// MEASURED, because the first version of this header got it wrong and the test
// written from that mistake could not have failed: on .NET 8,
// Environment.GetFolderPath(SpecialFolder.LocalApplicationData) does NOT read
// %LOCALAPPDATA%. Setting that variable in-process changes the variable and
// leaves GetFolderPath returning C:\Users\<user>\AppData\Local. The same is true
// of SpecialFolder.UserProfile. Both go to the Win32 known-folder API. So a test
// that moves the environment variable proves nothing about the old code, and the
// property is pinned by HorizunPathsSourceTests instead - which reads the shipped
// sources and fails if any state path is computed from LocalApplicationData again.
//
// The failure mode is not an error. The server writes a job record Revit never
// sees, or reads a discovery directory the add-in never wrote to, and every
// symptom points somewhere else: "no Revit has published a bridge" while Revit
// sits there with the add-in loaded and its own log growing.
//
// So the root is the user's HOME, which is the one location every process owned
// by a user agrees on and which nothing virtualises per-package:
//
//     %USERPROFILE%\.horizun\
//         settings.json
//         discovery\revit-<year>-<pid>.json
//         jobs\<id>.jsonl
//         logs\revit-<year>.log, server.log
//
// NOTHING HERE IS CACHED. The environment is read on every call, exactly as
// Settings.cs re-reads its file on every use, so a test can move the root and a
// running process can be asked what it resolves to right now rather than what it
// resolved to at startup. These are string operations on two environment
// variables; the cost of being able to answer honestly is smaller than the cost
// of a stale answer.
//
// Linked into the server rather than duplicated - the same reason Settings.cs is.
// It carries no `using Autodesk.*`, so nothing Revit comes with it.
// -----------------------------------------------------------------------------
using System;
using System.IO;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    /// <summary>
    /// What a single path is, and whether this process could actually use it.
    /// Reported rather than assumed: a data root that exists and cannot be written
    /// to is the failure that looks like "the feature is broken".
    /// </summary>
    public sealed class PathProbe
    {
        public string Path;
        public bool Exists;
        public bool Readable;
        public bool Writable;
        /// <summary>Why a probe failed, in the words the OS used. Null when nothing failed.</summary>
        public string Error;

        public JObject ToJson()
        {
            var o = new JObject
            {
                ["path"] = Path,
                ["exists"] = Exists,
                ["readable"] = Readable,
                ["writable"] = Writable
            };
            if (Error != null) o["error"] = Error;
            return o;
        }
    }

    public static class HorizunPaths
    {
        /// <summary>
        /// The escape hatch, and the ONLY thing that overrides the home directory.
        ///
        /// It exists for two callers: a test that needs a root it can delete, and a
        /// machine whose home directory is on a network share too slow to hold a job
        /// log. It is reported by name in Describe() - a root that came from an
        /// environment variable must never look like the default, because a variable
        /// set for one process and not the other is the exact failure this file is
        /// written against.
        /// </summary>
        public const string RootOverrideVariable = "HORIZUN_DATA_ROOT";

        /// <summary>The folder name under the user's home. A dot-directory, by Unix habit.</summary>
        public const string FolderName = ".horizun";

        /// <summary>
        /// Where the root came from, so Describe() can say it and a support call does
        /// not turn into a guess.
        /// </summary>
        public static string ResolvedFrom()
        {
            if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(RootOverrideVariable)))
                return RootOverrideVariable;
            if (!string.IsNullOrWhiteSpace(SafeSpecialFolder()))
                return "SpecialFolder.UserProfile";
            if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("USERPROFILE")))
                return "%USERPROFILE%";
            if (!string.IsNullOrWhiteSpace(HomeDriveAndPath()))
                return "%HOMEDRIVE%%HOMEPATH%";
            return "unresolved";
        }

        /// <summary>
        /// The data root. Settings, discovery, jobs and logs all live under it.
        ///
        /// Four sources, in this order, and LocalApplicationData IS NOT ONE OF THEM.
        /// That is the point of the file: see the header for what goes wrong when two
        /// processes disagree about it.
        ///
        /// The API comes BEFORE the environment variable, which is the opposite of the
        /// order this file first shipped with. %USERPROFILE% is inheritable: a parent
        /// process can hand its child a different value, and the MCP server is a child
        /// of the MCP client. SpecialFolder.UserProfile goes to the known-folder API,
        /// which a parent cannot move - so the more trustworthy source is consulted
        /// first and the variable is the fallback for the contexts where the API
        /// returns nothing.
        /// </summary>
        public static string DataRoot()
        {
            string over = Environment.GetEnvironmentVariable(RootOverrideVariable);
            if (!string.IsNullOrWhiteSpace(over)) return over.Trim();

            string home = SafeSpecialFolder();
            if (string.IsNullOrWhiteSpace(home)) home = Environment.GetEnvironmentVariable("USERPROFILE");
            if (string.IsNullOrWhiteSpace(home)) home = HomeDriveAndPath();

            if (string.IsNullOrWhiteSpace(home))
                // Every source failed. There is no safe guess here: falling back to
                // LocalApplicationData would reintroduce exactly the split this file
                // exists to close, and doing it silently would hide it a second time.
                throw new InvalidOperationException(
                    "Horizun cannot determine the user's home directory: SpecialFolder.UserProfile, " +
                    "%USERPROFILE% and %HOMEDRIVE%%HOMEPATH% are all empty. Set " +
                    RootOverrideVariable + " to the folder Horizun should keep its state in. It is NOT falling " +
                    "back to LocalApplicationData: that folder is redirected per-package and per-user, so the " +
                    "MCP server and Revit can resolve it differently, and a silent fallback would split the " +
                    "state the two halves share.");

            return System.IO.Path.Combine(home.Trim(), FolderName);
        }

        public static string SettingsPath() => System.IO.Path.Combine(DataRoot(), "settings.json");
        public static string DiscoveryDir() => System.IO.Path.Combine(DataRoot(), "discovery");
        public static string JobsDir() => System.IO.Path.Combine(DataRoot(), "jobs");
        public static string IdempotencyDir() => System.IO.Path.Combine(DataRoot(), "idempotency");
        public static string LogsDir() => System.IO.Path.Combine(DataRoot(), "logs");

        /// <summary>
        /// The pre-0.3 location, READ ONLY and never written.
        ///
        /// Reported by Describe() when it still holds files, because "Horizun sees no
        /// Revit" on a machine with a full %LOCALAPPDATA%\Horizun is a question worth
        /// answering before someone spends an afternoon on it. Nothing reads state
        /// from here - a silent fallback would restore the split rather than close it.
        /// </summary>
        public static string LegacyDataRoot()
        {
            try
            {
                string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                return string.IsNullOrWhiteSpace(local) ? null : System.IO.Path.Combine(local, "Horizun");
            }
            catch { return null; }
        }

        /// <summary>
        /// Create the four directories. Best effort: a process that cannot create them
        /// still runs, and Describe() is what says so.
        /// </summary>
        public static void EnsureDirectories()
        {
            foreach (string d in new[] { DataRoot(), DiscoveryDir(), JobsDir(), IdempotencyDir(), LogsDir() })
            {
                try { Directory.CreateDirectory(d); } catch { }
            }
        }

        /// <summary>
        /// Can this process read and write here, right now?
        ///
        /// Writability is MEASURED, by creating a uniquely named probe file and
        /// deleting it - the only way to answer it. Checking an ACL answers a
        /// different question, and read-only media, a full disk and a mandatory-lock
        /// policy all pass an ACL check and fail a write.
        /// </summary>
        public static PathProbe ProbeDirectory(string dir)
        {
            var p = new PathProbe { Path = dir };
            try
            {
                p.Exists = Directory.Exists(dir);
                if (!p.Exists)
                {
                    // Not existing is not the same as not usable. Creating it is what
                    // the add-in and the server both do on startup anyway.
                    try { Directory.CreateDirectory(dir); p.Exists = Directory.Exists(dir); }
                    catch (Exception ex) { p.Error = ex.GetType().Name + ": " + ex.Message; return p; }
                }

                try { Directory.GetFileSystemEntries(dir); p.Readable = true; }
                catch (Exception ex) { p.Error = "read: " + ex.GetType().Name + ": " + ex.Message; }

                string probe = System.IO.Path.Combine(dir, ".hz-probe-" + Guid.NewGuid().ToString("N").Substring(0, 8));
                try
                {
                    File.WriteAllText(probe, "horizun");
                    p.Writable = true;
                }
                catch (Exception ex)
                {
                    p.Error = (p.Error == null ? "" : p.Error + "; ") + "write: " + ex.GetType().Name + ": " + ex.Message;
                }
                finally
                {
                    try { if (File.Exists(probe)) File.Delete(probe); } catch { }
                }
            }
            catch (Exception ex)
            {
                p.Error = ex.GetType().Name + ": " + ex.Message;
            }
            return p;
        }

        /// <summary>
        /// The same question for a FILE. An absent settings.json is not a fault - the
        /// defaults are the safe ones - so absence is reported as writable-if-its-
        /// directory-is, never as an error.
        /// </summary>
        public static PathProbe ProbeFile(string path)
        {
            var p = new PathProbe { Path = path };
            try
            {
                p.Exists = File.Exists(path);
                if (!p.Exists)
                {
                    PathProbe parent = ProbeDirectory(System.IO.Path.GetDirectoryName(path));
                    p.Readable = parent.Readable;
                    p.Writable = parent.Writable;
                    if (parent.Error != null) p.Error = "containing directory: " + parent.Error;
                    return p;
                }

                try
                {
                    using (var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                        s.ReadByte();
                    p.Readable = true;
                }
                catch (Exception ex) { p.Error = "read: " + ex.GetType().Name + ": " + ex.Message; }

                try
                {
                    // FileMode.Open, not Create or Truncate: this must prove the file can be
                    // written WITHOUT destroying what is in it.
                    using (new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete)) { }
                    p.Writable = true;
                }
                catch (Exception ex)
                {
                    p.Error = (p.Error == null ? "" : p.Error + "; ") + "write: " + ex.GetType().Name + ": " + ex.Message;
                }
            }
            catch (Exception ex)
            {
                p.Error = ex.GetType().Name + ": " + ex.Message;
            }
            return p;
        }

        /// <summary>
        /// Everything horizun_health and horizun_target report about where state lives.
        ///
        /// Both halves call THIS, so "the server and the add-in disagree about the data
        /// root" is a thing you can see by putting two replies side by side, rather
        /// than a thing you deduce from a missing file three hours later.
        /// </summary>
        public static JObject Describe()
        {
            var o = new JObject();
            string root;
            try { root = DataRoot(); }
            catch (Exception ex)
            {
                o["data_root"] = null;
                o["resolved_from"] = "unresolved";
                o["error"] = ex.Message;
                return o;
            }

            o["data_root"] = root;
            o["resolved_from"] = ResolvedFrom();
            o["settings_path"] = SettingsPath();
            o["discovery_path"] = DiscoveryDir();
            o["jobs_path"] = JobsDir();
            o["logs_path"] = LogsDir();

            o["access"] = new JObject
            {
                ["data_root"] = ProbeDirectory(root).ToJson(),
                ["settings_path"] = ProbeFile(SettingsPath()).ToJson(),
                ["discovery_path"] = ProbeDirectory(DiscoveryDir()).ToJson(),
                ["jobs_path"] = ProbeDirectory(JobsDir()).ToJson(),
                ["logs_path"] = ProbeDirectory(LogsDir()).ToJson()
            };

            o["why_not_localappdata"] =
                "LocalApplicationData is redirected per-package under MSIX/AppContainer, and is a different " +
                "folder outright under a different user or elevation context. The MCP server is launched by the " +
                "MCP client and Revit is launched by the user, so the two can resolve it differently and neither " +
                "is told - the server then writes jobs Revit never sees and reads a discovery directory the " +
                "add-in never wrote to, and reports 'no Revit has published a bridge' with Revit running. The " +
                "user profile is not package-redirected, so both halves reach one folder.";

            // Only mentioned when it still holds something. An empty legacy folder is
            // noise; one with files in it explains a symptom.
            try
            {
                string legacy = LegacyDataRoot();
                if (legacy != null && Directory.Exists(legacy))
                {
                    int files = Directory.GetFiles(legacy, "*", SearchOption.AllDirectories).Length;
                    if (files > 0)
                    {
                        o["legacy_data_root"] = legacy;
                        o["legacy_file_count"] = files;
                        o["legacy_note"] =
                            "State from before 0.3 is still in the old %LOCALAPPDATA% location. NOTHING READS IT - " +
                            "it is named here so a machine with old discovery files does not look like a machine " +
                            "with a broken bridge. Delete it when you are satisfied nothing is missing.";
                    }
                }
            }
            catch { }

            return o;
        }

        private static string SafeSpecialFolder()
        {
            try { return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile); }
            catch { return null; }
        }

        private static string HomeDriveAndPath()
        {
            try
            {
                string drive = Environment.GetEnvironmentVariable("HOMEDRIVE");
                string path = Environment.GetEnvironmentVariable("HOMEPATH");
                if (string.IsNullOrWhiteSpace(drive) || string.IsNullOrWhiteSpace(path)) return null;
                return drive.Trim() + path.Trim();
            }
            catch { return null; }
        }
    }
}
