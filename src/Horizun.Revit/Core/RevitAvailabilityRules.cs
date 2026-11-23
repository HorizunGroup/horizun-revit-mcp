using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Horizun.Revit.Core
{
    /// <summary>A running Revit.exe as the server sees it: its pid and, when its path could be read, its year.</summary>
    public sealed class RunningRevit
    {
        public int Pid; public string Year;
        public RunningRevit(int pid, string year) { Pid = pid; Year = year; }
    }

    /// <summary>
    /// "No Revit is reachable" read the same whether Revit was CLOSED or OPEN without a bridge (still starting, a modal
    /// dialog - the add-in's Security dialog often opens on another monitor - or the add-in not loaded). Telemetry
    /// 2026-11-06..09: 34 revit_no_disponible in execute_python alone, and the agent could not tell which step to take.
    /// The server counts the Revit.exe processes and says which case it is, with the step for that case.
    /// </summary>
    public static class RevitAvailabilityRules
    {
        public const string Closed = "cerrado";
        public const string RunningWithoutBridge = "abierto_sin_puente";

        private static readonly Regex YearInPath = new Regex(@"Revit (20\d\d)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>The year in a Revit.exe path ("...\Autodesk\Revit 2025\Revit.exe"), or null.</summary>
        public static string YearFromPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            Match m = YearInPath.Match(path);
            return m.Success ? m.Groups[1].Value : null;
        }

        /// <summary>The processes that count for this request: all, or those of the requested year (unknown year counts).</summary>
        public static List<RunningRevit> Relevant(IEnumerable<RunningRevit> running, string year)
            => (running ?? Enumerable.Empty<RunningRevit>())
               .Where(r => string.IsNullOrEmpty(year) || r.Year == null || r.Year == year).ToList();

        public static string State(IEnumerable<RunningRevit> running, string year)
            => Relevant(running, year).Count == 0 ? Closed : RunningWithoutBridge;

        /// <summary>The sentence that follows "no Revit is reachable": which case it is and the step for it.</summary>
        public static string NextStep(IEnumerable<RunningRevit> running, string year)
        {
            List<RunningRevit> relevant = Relevant(running, year);
            string which = string.IsNullOrEmpty(year) ? "Revit" : "Revit " + year;
            if (relevant.Count == 0)
                return " No " + which + " process is running: Revit is CLOSED. Ask the person to open " + which +
                       " with a model (the Horizun add-in loads with it), then call horizun_health. Retrying before that cannot succeed.";
            string list = string.Join(", ", relevant.Select(r => (r.Year == null ? "Revit" : "Revit " + r.Year) + " pid " + r.Pid));
            return " " + which + " IS running (" + list + ") but has not published a bridge: it is still starting, a modal " +
                   "dialog is waiting for an answer (the add-in's Security dialog - choose Always Load - often opens on another " +
                   "monitor), or the Horizun add-in did not load. Ask the person to finish the start or answer the dialog, wait " +
                   "about 30 seconds and call horizun_health; if it still fails with Revit idle, the Horizun add-in is missing for " +
                   "that year: install it.";
        }
    }
}
