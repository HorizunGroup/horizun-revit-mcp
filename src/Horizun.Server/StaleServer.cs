using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using Newtonsoft.Json.Linq;

namespace Horizun.Server
{
    internal static class StaleServer
    {
        internal static JObject InstalledDiagnosis()
        {
            try
            {
                string running = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
                // The installer uses this environment variable for its default installation,
                // which is an executable location, not Horizun's shared state directory.
                string root = Environment.GetEnvironmentVariable("LOCALAPPDATA");
                if (string.IsNullOrWhiteSpace(root)) return null;
                string dll = Path.Combine(root, "Programs", "Horizun", "MCP", "server", "horizun-mcp.dll");
                if (!File.Exists(dll)) return null;
                return Diagnose(running, FileVersionInfo.GetVersionInfo(dll).ProductVersion);
            }
            catch { return null; }
        }

        internal static JObject Diagnose(string running, string installed)
        {
            if (!Parse(running, out var oldVersion, out var oldPre, out var oldCommit) ||
                !Parse(installed, out var newVersion, out var newPre, out var newCommit)) return null;
            int comparison = newVersion.CompareTo(oldVersion);
            if (comparison == 0 && oldPre != null && newPre == null) comparison = 1;
            if (comparison == 0 && oldPre != null && newPre != null)
            {
                var oldMatch = System.Text.RegularExpressions.Regex.Match(oldPre, @"^([A-Za-z-]+)([0-9]+)$");
                var newMatch = System.Text.RegularExpressions.Regex.Match(newPre, @"^([A-Za-z-]+)([0-9]+)$");
                if (oldMatch.Success && newMatch.Success &&
                    string.Equals(oldMatch.Groups[1].Value, newMatch.Groups[1].Value, StringComparison.OrdinalIgnoreCase) &&
                    long.TryParse(oldMatch.Groups[2].Value, out long oldNumber) &&
                    long.TryParse(newMatch.Groups[2].Value, out long newNumber))
                    comparison = newNumber.CompareTo(oldNumber);
            }
            // Unrelated prerelease identifiers alone do not establish chronological order.
            if (comparison <= 0) return null;
            string oldLabel = running.Split('+')[0], newLabel = installed.Split('+')[0];
            return new JObject {
                ["stale_server_process"] = true,
                ["server_version"] = oldLabel, ["server_commit"] = oldCommit,
                ["installed_version"] = newLabel, ["installed_commit"] = newCommit,
                ["remediation"] = "Restart the MCP client to start the installed server."
            };
        }

        private static bool Parse(string value, out Version version, out string prerelease, out string commit)
        {
            version = null; prerelease = null; commit = null;
            if (string.IsNullOrWhiteSpace(value)) return false;
            string[] metadata = value.Split('+');
            if (metadata.Length > 1) commit = metadata[1];
            string[] parts = metadata[0].Split(new[] { '-' }, 2);
            if (parts.Length > 1) prerelease = parts[1];
            return Version.TryParse(parts[0], out version);
        }
    }
}
