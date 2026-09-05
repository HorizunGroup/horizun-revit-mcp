// -----------------------------------------------------------------------------
// Horizun Revit MCP — original Horizun code.
//
// The discovery file. The MCP server has no way to know which pipe this Revit is
// listening on, so we write it where the server looks:
//   %USERPROFILE%\.horizun\discovery\revit-<year>-<pid>.json
// containing the pipe name, an auth token, and our pid. One file per running
// instance, so several Revit versions - and several copies of one version - can
// run side by side without colliding.
//
// The DIRECTORY is HorizunPaths.DiscoveryDir(), which the server calls too. It
// used to be %LOCALAPPDATA%\Horizun computed independently at each end: a root
// that a parent process can move for one of them and not the other.
//
// The token is 256 bits from a cryptographic RNG. It never leaves this machine —
// the discovery file is written with an ACL restricting it to the current user.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public static class Discovery
    {
        public static string NewToken()
        {
            var bytes = new byte[32];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            return Convert.ToBase64String(bytes);
        }

        /// <summary>
        /// Where the discovery files go. HorizunPaths owns the answer, and the SERVER
        /// asks the same function - the two halves ship separately, and a discovery
        /// directory each computes for itself is a directory they can disagree about.
        /// </summary>
        public static string Dir() => HorizunPaths.DiscoveryDir();

        /// <summary>
        /// One file per RUNNING INSTANCE, not per year.
        ///
        /// It used to be revit-&lt;year&gt;.json, and two Revit 2026 processes are not a
        /// hypothetical here - opening a file starts a second instance on its own, and it
        /// happened twice in one afternoon. With one file per year the second instance
        /// overwrote the first's pipe name, so calls aimed at one arrived at the other;
        /// and when either closed, its shutdown deleted the file the OTHER was still
        /// using, leaving a live Revit that nothing could find.
        ///
        /// The pid is in the name, so instances cannot collide and each removes only its
        /// own on the way out.
        /// </summary>
        public static string FileName(string year) =>
            "revit-" + year + "-" + System.Diagnostics.Process.GetCurrentProcess().Id + ".json";

        /// <summary>
        /// The pre-instance name. Still READ by the server so an add-in that has not been
        /// redeployed keeps working; never written any more.
        /// </summary>
        public static string LegacyFileName(string year) => "revit-" + year + ".json";

        /// <summary>
        /// Where a startup failure is recorded. DELIBERATELY NOT "revit-*.json": the
        /// server globs that pattern for bridges, and a failure is the opposite of one.
        /// </summary>
        public static string FailureFileName(string year) =>
            "startup-failure-" + year + "-" + System.Diagnostics.Process.GetCurrentProcess().Id + ".json";

        /// <summary>
        /// Say WHY there is no bridge, where somebody looking for one will find it.
        ///
        /// An add-in that fails to initialise writes no discovery file, and the only
        /// symptom is a server reporting "no Revit has published a bridge" - which is
        /// what a machine with no Revit open looks like too. The log has the answer and
        /// nobody reads a log they do not know exists. Best effort: a failure that
        /// cannot be recorded must not become a second failure.
        /// </summary>
        public static void WriteStartupFailure(string year, string reason)
        {
            try
            {
                string dir = Dir();
                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, FailureFileName(year));
                var payload = new JObject
                {
                    ["schema"] = 1,
                    ["revit_year"] = year,
                    ["pid"] = System.Diagnostics.Process.GetCurrentProcess().Id,
                    ["failed_utc"] = DateTime.UtcNow.ToString("o"),
                    ["addin_version"] = Build.Version,
                    ["addin_commit"] = Build.Commit,
                    ["reason"] = reason,
                    ["means"] = "the Horizun add-in loaded in this Revit and REFUSED to publish a bridge. No MCP " +
                                "client can reach this instance until the cause below is fixed; the add-in did not " +
                                "take Revit down with it."
                };
                string tmp = path + ".tmp";
                File.WriteAllText(tmp, payload.ToString());
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
                RestrictToCurrentUser(path);
            }
            catch { /* a breadcrumb that cannot be written is not worth a second failure */ }
        }

        /// <summary>Remove this instance's failure breadcrumb, if it left one.</summary>
        public static void ClearStartupFailure(string year)
        {
            try
            {
                string path = Path.Combine(Dir(), FailureFileName(year));
                if (File.Exists(path)) File.Delete(path);
            }
            catch { }
        }

        /// <summary>
        /// Publish where we listen, and WHAT WE CAN DO.
        ///
        /// The server's tool list is compiled into the server, and the two halves are
        /// deployed separately - a plugin installed months ago answers a server built
        /// today. Without the command list here, the server advertises tools the add-in
        /// on the other end has never heard of, and the only symptom is "Unknown
        /// command", which reads like a bug in the caller instead of "your plugin is
        /// older than your server". So the add-in states its version and its actual
        /// command names, and the server can say the true thing.
        ///
        /// schema 2 adds those two fields. A server reading a schema-1 file finds no
        /// command list and must treat that as UNKNOWN - never as "supports nothing".
        /// </summary>
        public static void Write(string year, string pipeName, string token,
                                 string addinVersion, IEnumerable<string> commands)
        {
            string dir = Dir();
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, FileName(year));

            var names = new JArray();
            if (commands != null)
                foreach (string c in commands)
                    if (!string.IsNullOrEmpty(c)) names.Add(c);

            var payload = new JObject
            {
                // schema 3: one file per instance, and the file says which instance.
                ["schema"] = 3,
                ["revit_year"] = year,
                ["pipe_name"] = pipeName,
                ["auth_token"] = token,
                ["pid"] = System.Diagnostics.Process.GetCurrentProcess().Id,
                // Distinguishes two runs of Revit that were assigned the same pid at
                // different times - pids are recycled, and a stale file with a recycled
                // pid would otherwise look alive.
                ["instance_id"] = Guid.NewGuid().ToString("N"),
                ["started_utc"] = DateTime.UtcNow.ToString("o"),
                // What this add-in believes every command takes. The server compares it to
                // its own before forwarding: two builds that disagree about a schema used
                // to differ silently, and the symptom was an argument ignored at this end.
                ["protocol_version"] = Horizun.Contracts.Contract.ProtocolVersion,
                ["contract_hash"] = Horizun.Contracts.Contract.Hash,
                ["addin_version"] = string.IsNullOrEmpty(addinVersion) ? "unknown" : addinVersion,
                ["commands"] = names
            };

            // Atomic write: temp then move, so a reader never sees a half-written file.
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, payload.ToString());
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);

            RestrictToCurrentUser(path);

            // AFTER our own file is safely published: hygiene. Publishing is the job;
            // the sweep failing must never take it down with it.
            SweepStale();
        }

        /// <summary>
        /// Delete discovery files left behind by instances that no longer run.
        ///
        /// A Revit that crashes - or is killed past a modal dialog - never reaches
        /// Delete(), so its file stays. After one afternoon of crash-testing there
        /// were four files and one live process. horizun_target is NOT confused by
        /// them (it reports process_running per file and picks the live one - that
        /// was verified before writing this, because "fixing" a working selector is
        /// how a working selector gets broken), so this is housekeeping, not a guard.
        ///
        /// Two deliberate refusals:
        ///   * Legacy revit-&lt;year&gt;.json files are NEVER touched. Their last segment
        ///     ("2025") parses as an integer that is almost never a live pid, so a
        ///     naive parse would delete the file a not-yet-redeployed add-in is
        ///     actively publishing. Only the three-segment revit-year-pid form is
        ///     considered.
        ///   * A pid now owned by ANY process reads as alive and the file is KEPT.
        ///     Pids are recycled; deleting on "that pid is not a Revit" would need a
        ///     process-name read that can throw on permissions. Conservative is
        ///     correct here - instance_id exists precisely for that ambiguity.
        /// </summary>
        private static void SweepStale()
        {
            try
            {
                int me = System.Diagnostics.Process.GetCurrentProcess().Id;

                // The WHICH-files rule is DiscoverySweep, shared with the server so the two
                // halves cannot drift; this side owns the listing, the pid check and the IO.
                List<string> stale = DiscoverySweep.StaleFiles(
                    Directory.GetFiles(Dir(), "revit-*.json"), PidExists, me);

                int swept = 0;
                foreach (string f in stale)
                {
                    try { File.Delete(f); swept++; }
                    catch { /* one unreadable file must not stop the sweep */ }
                }

                if (swept > 0)
                    Log.Info("discovery: swept " + swept + " stale file(s) left by Revit instance(s) that no longer run");
            }
            catch { /* hygiene only - our own file is already published */ }
        }

        /// <summary>
        /// Bare pid existence - ANY process with that number, not "is a Revit". A pid
        /// that resolves keeps its discovery file; only "no such process" is stale. See
        /// DiscoverySweep for why conservative is correct here.
        /// </summary>
        private static bool PidExists(int pid)
        {
            if (pid <= 0) return false;
            try { using (System.Diagnostics.Process.GetProcessById(pid)) { } return true; }
            catch (ArgumentException) { return false; }        // no such process
            catch { return true; }                             // unreadable -> keep the file
        }

        public static void Delete(string year)
        {
            try
            {
                string path = Path.Combine(Dir(), FileName(year));
                if (File.Exists(path)) File.Delete(path);
            }
            catch { /* best effort on shutdown */ }
        }

        private static void RestrictToCurrentUser(string path)
        {
            try
            {
                var fi = new FileInfo(path);
                var acl = fi.GetAccessControl();
                acl.SetAccessRuleProtection(true, false);
                var me = WindowsIdentity.GetCurrent().User;
                acl.AddAccessRule(new FileSystemAccessRule(
                    me, FileSystemRights.FullControl, AccessControlType.Allow));
                fi.SetAccessControl(acl);
            }
            catch { /* ACL hardening is best-effort; the token still gates access */ }
        }
    }
}
