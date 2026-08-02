// -----------------------------------------------------------------------------
// Horizun MCP server - original Horizun code.
//
// Which Revit this session is pointed at, as ONE value that changes ALL AT ONCE.
//
// It used to be two static fields, StickyYear and StickyPid, written one after the
// other:
//
//     PipeClient.StickyPid  = wantPid;
//     PipeClient.StickyYear = null;
//
// and read the same way, from a different thread, in CallTool:
//
//     string year = PipeClient.StickyYear ?? TargetYear;
//     Discovered d = PipeClient.Resolve(year, PipeClient.StickyPid, out ambiguity);
//
// Two writes and two reads with no relationship between them. The server answers
// requests concurrently on purpose - that is the whole reason job_status can be
// asked while a scan runs - so a call in flight can read the pair BETWEEN the two
// writes and see a combination that was never chosen: the old year with the new
// pid, or the new year with a pid left over from the target before it. A pid wins
// over a year in Resolve, so the visible failure is the worst-shaped one: the
// command goes to the instance the caller USED to be pointed at, silently, and the
// reply looks exactly like a correct answer about the wrong model.
//
// A selection is now a single immutable object behind a single reference. There is
// no window: a reader either sees the whole old target or the whole new one, and a
// reference assignment cannot tear. Nothing here can express "a year AND a pid",
// which is the other half of the fix - see Targets.cs, which refuses a request
// that names both rather than letting one silently overwrite the other.
// -----------------------------------------------------------------------------
using System;

namespace Horizun.Server
{
    /// <summary>
    /// The Revit chosen for this session, or automatic. Immutable: a target is
    /// replaced, never edited, so no reader can observe a half-changed one.
    /// </summary>
    internal sealed class TargetSelection
    {
        /// <summary>
        /// Nothing chosen: the one running instance, and a REFUSAL when more than one is
        /// running. Shared, because it carries no state to tell two of them apart.
        /// </summary>
        public static readonly TargetSelection Automatic = new TargetSelection(null, null);

        /// <summary>The year chosen by horizun_target, or null. Never set with Pid.</summary>
        public string Year { get; }

        /// <summary>The instance chosen by horizun_target, or null. Never set with Year.</summary>
        public int? Pid { get; }

        private TargetSelection(string year, int? pid) { Year = year; Pid = pid; }

        public static TargetSelection ByYear(string year)
        {
            if (string.IsNullOrWhiteSpace(year)) throw new ArgumentException("A year target needs a year.", nameof(year));
            return new TargetSelection(year.Trim(), null);
        }

        public static TargetSelection ByPid(int pid)
        {
            if (pid <= 0) throw new ArgumentException("A process id target needs a real pid.", nameof(pid));
            return new TargetSelection(null, pid);
        }

        public bool IsAutomatic => Year == null && Pid == null;

        /// <summary>How this session was pointed, in the words the reply uses.</summary>
        public string Describe(string envYear) =>
            Pid != null ? "horizun_target (a specific process)"
          : Year != null ? "horizun_target (a year)"
          : !string.IsNullOrEmpty(envYear) ? "HORIZUN_REVIT_YEAR"
          : "automatic (the one running instance; MORE THAN ONE is refused, not guessed)";
    }
}
