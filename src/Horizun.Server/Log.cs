// -----------------------------------------------------------------------------
// Horizun MCP server - original Horizun code.
//
// The server's half of the record. The plugin has kept a log since the day a
// silent startup failure was indistinguishable from "not installed"; this side
// had none, and that is a worse blind spot than it looks.
//
// A stdio MCP server cannot report anything about itself. Its stdout IS the
// protocol - a stray line there corrupts the stream - and its stderr belongs to
// whatever launched it, usually swallowed. So when a call goes wrong, the user
// sees one sentence in a chat client and there is nothing on disk to compare it
// against: not which Revit was picked out of two, not how long the call took
// before it gave up, not whether the process died mid-session.
//
// Same rules as the plugin's log, for the same reasons:
//   * It NEVER throws. A logger that breaks the thing it observes is worse than
//     no logger.
//   * One appended UTF-8 line per event, readable in Notepad.
//   * It caps its own size and rolls once.
//   * It records tool NAMES, outcomes and durations - never arguments. Those
//     carry model content, file paths and parameter values, and a log file is
//     not the place for a client's data.
// -----------------------------------------------------------------------------
using System;
using System.IO;
using System.Text;

namespace Horizun.Server
{
    internal static class Log
    {
        private const long MaxBytes = 2 * 1024 * 1024;   // 2 MB, then it rolls once.
        private static readonly object Gate = new object();
        private static string _path;

        /// <summary>
        /// %USERPROFILE%\.horizun\logs\server.log - beside the plugin's logs, from the
        /// SAME function the plugin calls. Two independent computations of "beside"
        /// are two things that can drift apart; see HorizunPaths.cs.
        /// </summary>
        public static string Path()
        {
            return System.IO.Path.Combine(Horizun.Revit.Core.HorizunPaths.LogsDir(), "server.log");
        }

        public static void Start()
        {
            try
            {
                _path = Path();
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path));
                Roll();
            }
            catch { _path = null; }
        }

        public static void Info(string message) => Write("INFO ", message);
        public static void Warn(string message) => Write("WARN ", message);

        public static void Error(string message, Exception ex)
        {
            var sb = new StringBuilder(message ?? "");
            for (Exception e = ex; e != null; e = e.InnerException)
                sb.Append(" | ").Append(e.GetType().Name).Append(": ").Append(e.Message);

            if (ex != null && !string.IsNullOrEmpty(ex.StackTrace))
            {
                sb.Append(Environment.NewLine).Append("    stack:");
                foreach (string frame in ex.StackTrace.Split('\n'))
                {
                    string f = frame.Trim();
                    if (f.Length > 0) sb.Append(Environment.NewLine).Append("      ").Append(f);
                }
            }
            Write("ERROR", sb.ToString());
        }

        private static void Write(string level, string message)
        {
            string path = _path;
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") +
                              " [" + level + "] (pid " + System.Diagnostics.Process.GetCurrentProcess().Id + ") " +
                              message;
                lock (Gate) File.AppendAllText(path, line + Environment.NewLine, new UTF8Encoding(false));
            }
            catch { /* a logger must never be the reason something failed */ }
        }

        private static void Roll()
        {
            try
            {
                var fi = new FileInfo(_path);
                if (!fi.Exists || fi.Length < MaxBytes) return;
                string previous = _path + ".1";
                if (File.Exists(previous)) File.Delete(previous);
                File.Move(_path, previous);
            }
            catch { }
        }
    }
}
