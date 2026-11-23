using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Horizun.Server
{
    // Keep the original process handle as well as its start time: a recycled pid is
    // never mistaken for the process that launched this server.
    internal sealed class ParentLifetime : IDisposable
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct ProcessBasicInformation
        {
            public IntPtr Reserved1, PebBaseAddress, Reserved2a, Reserved2b, UniqueProcessId, ParentProcessId;
        }

        [DllImport("ntdll.dll")]
        private static extern int NtQueryInformationProcess(IntPtr process, int infoClass,
            out ProcessBasicInformation info, int length, out int returned);

        private readonly Process _parent;
        private readonly DateTime _startedUtc;
        private ParentLifetime(Process parent, DateTime startedUtc) { _parent = parent; _startedUtc = startedUtc; }

        public static ParentLifetime Capture()
        {
            if (Environment.GetEnvironmentVariable("HORIZUN_PARENT_WATCH") == "0") return NotWatched("disabled by HORIZUN_PARENT_WATCH=0");
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return NotWatched("unsupported operating system");
            try
            {
                using (var current = Process.GetCurrentProcess())
                {
                    ProcessBasicInformation info;
                    int returned;
                    if (NtQueryInformationProcess(current.Handle, 0, out info,
                        Marshal.SizeOf<ProcessBasicInformation>(), out returned) != 0) return NotWatched("parent query failed");
                    int pid = checked((int)info.ParentProcessId.ToInt64());
                    Process parent;
                    try { parent = Process.GetProcessById(pid); }
                    catch (ArgumentException) { return NotWatched("parent process does not exist"); }
                    try
                    {
                        var started = parent.StartTime.ToUniversalTime();
                        if (started > current.StartTime.ToUniversalTime())
                        {
                            parent.Dispose();
                            return NotWatched("parent pid was reused before capture");
                        }
                        return new ParentLifetime(parent, started);
                    }
                    catch { parent.Dispose(); throw; }
                }
            }
            catch (Exception ex) { return NotWatched("capture failed: " + ex.Message.Replace("\r", " ").Replace("\n", " ")); }
        }

        private static ParentLifetime NotWatched(string cause)
        {
            Log.Warn("parent not watched: " + cause);
            return null;
        }

        public bool HasExited()
        {
            try { return _parent.HasExited || _parent.StartTime.ToUniversalTime() != _startedUtc; }
            catch (InvalidOperationException) { return true; }
            catch (Exception ex) { Log.Warn("parent process status unavailable: " + ex.Message); return false; }
        }

        public void Dispose() { _parent?.Dispose(); }
    }
}
