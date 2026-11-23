using System;

namespace Horizun.Revit.Core
{
    /// <summary>UI diagnostics must not inherit the ten-minute modelling budget.</summary>
    public static class CommandDeadlinePolicy
    {
        public const int HealthMs = 5000;
        public const int HealthReplyGraceMs = 1000;
        public const int NavigateMs = 15000;
        public const int OpenDocumentQueueMs = 20000;
        /// <summary>
        /// A Python run that is still going this long after the call arrived hands its caller the
        /// job_id instead of holding the request open (5.0.4). Field 2026-11: 60 of 73 timeouts in
        /// three days were scripts that held a client for its whole 600 s limit.
        /// </summary>
        public const int PythonHandoffMs = 45000;

        /// <summary>
        /// True for the commands whose RUNNING call is handed off as a job at <see cref="PythonHandoffMs"/>.
        /// 5.0.6 adds horizun_export: field 2026-11 (5.0.2, Revit 2024) a 16-sheet combined PDF held its
        /// client the whole 600 s and reported nothing; the export keeps running and its files land in
        /// the job record exactly as a script's output does.
        /// </summary>
        public static bool HandsOff(string command) => command == "horizun_execute_python" || command == "horizun_export";

        public static int WaitMs(string command, int requestedMs)
        {
            if (requestedMs <= 0) throw new ArgumentOutOfRangeException(nameof(requestedMs));
            int cap = command == "horizun_health" ? HealthMs
                : command == "horizun_navigate" ? NavigateMs : requestedMs;
            return Math.Min(requestedMs, cap);
        }

        public static int RemainingMs(string command, int requestedMs, int elapsedMs, bool started)
        {
            int cap = WaitMs(command, requestedMs);
            // Opening a real file can take minutes; only its wait behind OTHER work is short.
            if (!started && command == "horizun_open_document") cap = Math.Min(cap, OpenDocumentQueueMs);
            // Only once it RUNS: a script waiting behind other work keeps the full wait, and a
            // modal that blocks it is still named by the dispatcher's own probe.
            if (started && HandsOff(command)) cap = Math.Min(cap, PythonHandoffMs);
            return Math.Max(0, cap - Math.Max(0, elapsedMs));
        }

        public static int DispatchWaitMs(string command, int requestedMs)
        {
            int limit = WaitMs(command, requestedMs);
            // Return the queue/modal diagnosis before the transport stops listening.
            return command == "horizun_health" ? Math.Min(limit, HealthMs - HealthReplyGraceMs) : limit;
        }
    }
}
