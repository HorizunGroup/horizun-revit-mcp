// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// WHICH discovery files are orphaned, decided out of names and pid liveness -
// the one rule both halves apply (story 5.24).
//
// A discovery file (revit-<year>-<pid>.json) is written by a running Revit and
// deleted by it on the way out. A Revit that crashes - or is killed past a modal
// dialog - never reaches Delete(), so its file stays. The add-in swept these, but
// ONLY when an instance published one, i.e. when a Revit starts. Kill Revit and
// ask the server again without starting a new one and the orphan sits there for
// the server to trip over. So the server sweeps too, and to keep the two from
// drifting they decide with the SAME function, here, Revit-free and unit-tested.
//
// TWO REFUSALS, carried over exactly from the add-in's original sweep:
//   * Legacy two-segment names (revit-<year>.json) are NEVER returned. Their last
//     segment ("2025") parses as an integer that is almost never a live pid, so a
//     naive parse would delete the file a not-yet-redeployed add-in is actively
//     publishing. Only the three-segment revit-<year>-<pid> form is considered.
//   * A pid that is ALIVE keeps its file. "Alive" is bare existence - ANY process
//     with that number - not "is a Revit": pids are recycled, and deleting on "that
//     pid is not a Revit" would need a name read that can throw on permissions.
//     Conservative is correct; instance_id exists precisely for that ambiguity.
//
// It returns names to delete and deletes nothing itself: the decision is testable
// without a filesystem, and each caller owns its own IO and error handling.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;

namespace Horizun.Revit.Core
{
    public static class DiscoverySweep
    {
        /// <summary>
        /// The orphaned files among <paramref name="fileNames"/> (file names or full
        /// paths; the returned entries are whatever was passed in). <paramref name="isPidAlive"/>
        /// answers whether a pid still belongs to any process. <paramref name="selfPid"/>
        /// is the caller's own pid so it never sweeps its own file; pass a value no file
        /// carries (e.g. -1) to disable that skip, which is what the server does - it is
        /// not a Revit and owns no discovery file.
        /// </summary>
        public static List<string> StaleFiles(IEnumerable<string> fileNames, Func<int, bool> isPidAlive, int selfPid)
        {
            var stale = new List<string>();
            if (fileNames == null || isPidAlive == null) return stale;

            foreach (string name in fileNames)
            {
                if (string.IsNullOrEmpty(name)) continue;

                string bare;
                try { bare = Path.GetFileNameWithoutExtension(name); }
                catch { continue; }

                string[] parts = bare.Split('-');
                if (parts.Length != 3) continue;              // legacy two-segment form: never touch
                int pid;
                if (!int.TryParse(parts[2], out pid)) continue;
                if (pid == selfPid) continue;                 // our own file

                bool alive;
                try { alive = isPidAlive(pid); }
                catch { alive = true; }                        // an unreadable liveness answer KEEPS the file

                if (!alive) stale.Add(name);
            }
            return stale;
        }
    }
}
