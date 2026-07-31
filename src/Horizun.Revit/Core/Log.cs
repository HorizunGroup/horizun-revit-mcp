// -----------------------------------------------------------------------------
// Horizun MCP — original Horizun code.
//
// The log exists for exactly one moment: the plugin failed to start on a machine
// that is not yours, and the only symptom is that nothing happened.
//
// A Revit add-in cannot report a startup failure to anybody. Throwing takes Revit
// down with it — unacceptable — so App.OnStartup swallows the exception, and the
// result is a silence indistinguishable from "not installed". This file turns that
// silence into a line on disk with a timestamp.
//
// Rules it follows, because a logger that breaks the thing it observes is worse
// than no logger:
//   * It NEVER throws. Every operation is wrapped; a log failure is dropped.
//   * It writes plain UTF-8 lines, one per event, appended — readable by anyone
//     with Notepad, and by us over the pipe.
//   * It caps its own size, so an add-in loaded twice a day for two years does
//     not quietly fill a disk.
//   * It records no model content and no parameter values — only what happened,
//     to which command, and how long it took.
// -----------------------------------------------------------------------------
using System;
using System.IO;
using System.Text;

namespace Horizun.Revit.Core
{
    public static class Log
    {
        private const long MaxBytes = 2 * 1024 * 1024;   // 2 MB, then it rolls once.
        private static readonly object Gate = new object();
        private static string _path;

        /// <summary>
        /// %USERPROFILE%\.horizun\logs\revit-&lt;year&gt;.log — beside the server's own log,
        /// which is the point: when the two halves disagree, the two logs are read
        /// together, and a support call should not begin by asking where they are.
        /// </summary>
        public static string PathFor(string year)
        {
            return Path.Combine(HorizunPaths.LogsDir(),
                                "revit-" + (string.IsNullOrEmpty(year) ? "unknown" : year) + ".log");
        }

        /// <summary>Point the log at a year's file. Safe to call more than once.</summary>
        public static void Start(string year)
        {
            try
            {
                _path = PathFor(year);
                Directory.CreateDirectory(Path.GetDirectoryName(_path));
                Roll();
            }
            catch { _path = null; }
        }

        public static void Info(string message) => Write("INFO ", message);
        public static void Warn(string message) => Write("WARN ", message);

        /// <summary>
        /// An exception, with its type and message, the inner one (usually the real cause),
        /// and the stack. The stack is the whole point: "ArgumentNullException: handle" names
        /// no file and no line, and without the frames the next step is guesswork.
        /// </summary>
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
                string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " [" + level + "] " + message;
                lock (Gate) File.AppendAllText(path, line + Environment.NewLine, new UTF8Encoding(false));
            }
            catch { /* a logger must never be the reason something failed */ }
        }

        /// <summary>Keep one previous file and start fresh once the current one is large.</summary>
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
