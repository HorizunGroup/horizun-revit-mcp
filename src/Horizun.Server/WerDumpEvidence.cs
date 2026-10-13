using System;
using System.IO;

namespace Horizun.Server
{
    internal static class WerDumpEvidence
    {
        internal static string DefaultDirectory()
        {
            // Windows Error Reporting writes under the server user's OS profile,
            // not under the bridge's HorizunPaths state root.
            string local = Environment.GetEnvironmentVariable("LOCALAPPDATA");
            return string.IsNullOrWhiteSpace(local) ? null : Path.Combine(local, "CrashDumps");
        }

        internal sealed class Finding
        {
            internal bool Found { get; set; }
            internal bool RecentWrite { get; set; }
            internal long? Bytes { get; set; }
            internal DateTime? LastWriteLocal { get; set; }
        }

        internal static Finding Find(string directory, int pid, DateTime? jobStartedLocal, DateTime nowLocal)
        {
            var none = new Finding();
            if (pid <= 0 || string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return none;
            string path = Path.Combine(directory, "Revit.exe." + pid + ".dmp");
            try
            {
                var file = new FileInfo(path);
                if (!file.Exists) return none;
                DateTime modified = file.LastWriteTime;
                // PIDs are reused. Without a job start time, a dump bearing the
                // same number is not evidence about this job.
                if (!jobStartedLocal.HasValue || modified < jobStartedLocal.Value) return none;
                return new Finding { Found = true, Bytes = file.Length, LastWriteLocal = modified,
                    RecentWrite = nowLocal - modified <= TimeSpan.FromMinutes(5) && nowLocal >= modified };
            }
            catch (IOException) { return none; }
            catch (UnauthorizedAccessException) { return none; }
        }
    }
}
