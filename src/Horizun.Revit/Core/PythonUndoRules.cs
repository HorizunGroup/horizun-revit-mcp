// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// WHAT horizun_undo CAN HONESTLY SAY ABOUT A PYTHON RUN (4.2-N5, CT-P1-5f).
//
// A typed write records an inverse before it commits. A script gives the bridge no
// such moment, but Revit's own DocumentChanged tally says what the run left in the
// model. That is enough for exactly one inverse: DELETE what the script created.
// Modified and deleted elements have no recorded previous value, so a run that
// touched any of them is journalled as NOT undoable, with the counts - the journal
// stays truthful and the newest batch is never silently skipped.
//
// Revit-free: the decision is provable at a desk.
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public sealed class PythonUndoPlan
    {
        public List<long> Created = new List<long>();
        public int Modified;
        public int Deleted;
        public bool Undoable;
        public string Reason;
    }

    public static class PythonUndoRules
    {
        public const string Tool = "horizun_execute_python";
        public const int MaxCreated = 2000;

        /// <summary>Only a real run is journalled: not a preflight, not a read-only or rehearsal run.</summary>
        public static bool Applies(string tool, JObject request)
        {
            if (tool != Tool || request == null) return false;
            if (request.Value<bool?>("preflight") == true) return false;
            if (request.Value<bool?>("read_only") == true || request.Value<bool?>("rehearsal") == true) return false;
            return true;
        }

        /// <summary>
        /// A preflight, read-only or rehearsal run is rolled back by the handler itself, so whatever the
        /// change watcher saw inside it never reached the model: it must not be recorded as an
        /// unrecorded (not undoable) write either. Field case: a read_only script answered
        /// "undoable: false" and blocked undo_last although nothing was left in the model.
        /// </summary>
        public static bool RolledBackByDesign(string tool, JObject request) =>
            tool == Tool && request != null &&
            (request.Value<bool?>("preflight") == true || request.Value<bool?>("read_only") == true || request.Value<bool?>("rehearsal") == true);

        /// <summary>Null when the run left nothing in the model (nothing to journal).</summary>
        public static PythonUndoPlan Decide(IEnumerable<long> created, int modified, int deleted, int documentsChanged, bool observationComplete = true)
        {
            List<long> ids = (created ?? Enumerable.Empty<long>()).Distinct().OrderBy(x => x).ToList();
            if (ids.Count == 0 && modified == 0 && deleted == 0) return null;
            var plan = new PythonUndoPlan { Created = ids, Modified = modified, Deleted = deleted };
            if (!observationComplete)
                plan.Reason = "Change observation was incomplete; undo coverage cannot be established.";
            else if (modified > 0 || deleted > 0)
                plan.Reason = "The script also " + string.Join(" and ", new[]
                    {
                        modified > 0 ? "modified " + modified + " element(s)" : null,
                        deleted > 0 ? "deleted " + deleted + " element(s)" : null
                    }.Where(s => s != null)) +
                    ". A script leaves no previous values, so only what it created could be reversed, and a partial undo " +
                    "would be a second, unreviewed edit. Reverse it by hand or restore from the last save.";
            else if (documentsChanged > 1)
                plan.Reason = "The script changed more than one open document; undo acts on one.";
            else if (ids.Count > MaxCreated)
                plan.Reason = "The script created " + ids.Count + " elements, over the " + MaxCreated + " an undo batch records.";
            plan.Undoable = plan.Reason == null && ids.Count > 0;
            return plan;
        }
    }
}
