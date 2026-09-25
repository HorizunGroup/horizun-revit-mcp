// -----------------------------------------------------------------------------
// Horizun Revit MCP — original Horizun code.
//
// A ROLLBACK SAYS WHY. MEASURED (campaign 5, a door on a wall being split): a
// set_curve came back "Revit returned RolledBack" and nothing else - the error
// Revit raised (the door no longer had its wall under it) was resolved by Revit
// itself as a rollback and never reached the caller, who could only "retry in
// smaller batches". This preprocessor RECORDS the error-severity failures of one
// transaction and changes nothing by default: it resolves none, deletes no
// warning, and lets Revit's own handling run exactly as before.
//
// CAPTURING WARNINGS TOO, opt-in (2026-09-25 field session): renaming a level
// raises Copy/Monitor alerts as WARNING-severity failures (9 in the reported
// case), which the default mode above silently ignores. captureWarnings=true
// records them the same way, without deleting them - Revit's own handling still
// runs unchanged; only what is OBSERVED grows. Existing callers are unaffected:
// the new parameters default to the old behaviour.
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace Horizun.Revit.Core
{
    public sealed class RevitErrorRecorder : IFailuresPreprocessor
    {
        public readonly List<string> Errors = new List<string>();

        /// <summary>Warning-severity messages, recorded only when captureWarnings was requested.</summary>
        public readonly List<string> Warnings = new List<string>();

        private readonly bool _captureWarnings;
        private readonly bool _deleteWarnings;

        public RevitErrorRecorder() : this(false, false) { }

        public RevitErrorRecorder(bool captureWarnings, bool deleteWarnings)
        {
            _captureWarnings = captureWarnings;
            _deleteWarnings = deleteWarnings;
        }

        public FailureProcessingResult PreprocessFailures(FailuresAccessor a)
        {
            foreach (FailureMessageAccessor f in a.GetFailureMessages())
            {
                FailureSeverity severity;
                try { severity = f.GetSeverity(); } catch { continue; }
                if (severity == FailureSeverity.None) continue;
                if (severity == FailureSeverity.Warning)
                {
                    if (!_captureWarnings) continue;
                    string line = Line(f);
                    if (!Warnings.Contains(line)) Warnings.Add(line);
                    if (_deleteWarnings) { try { a.DeleteWarning(f); } catch { } }
                    continue;
                }
                string errorLine = Line(f);
                if (!Errors.Contains(errorLine)) Errors.Add(errorLine);
            }
            return FailureProcessingResult.Continue;
        }

        private static string Line(FailureMessageAccessor f)
        {
            string desc;
            try { desc = f.GetDescriptionText(); } catch { desc = "(description unreadable)"; }
            string ids = "";
            try
            {
                ICollection<ElementId> failing = f.GetFailingElementIds();
                if (failing != null && failing.Count > 0)
                    ids = " [elements " + string.Join(", ", failing.Take(10).Select(Rid.Value)) + "]";
            }
            catch { }
            return desc + ids;
        }

        /// <summary>
        /// Attach to a transaction before it starts; returns the recorder. captureWarnings
        /// also records WARNING-severity messages (e.g. Copy/Monitor alerts); deleteWarnings
        /// additionally dismisses them so a headless run is never blocked by a pending dialog.
        /// Both default to false, so every existing call site keeps its old behaviour.
        /// </summary>
        public static RevitErrorRecorder On(Transaction tx, bool captureWarnings = false, bool deleteWarnings = false)
        {
            var recorder = new RevitErrorRecorder(captureWarnings, deleteWarnings);
            FailureHandlingOptions options = tx.GetFailureHandlingOptions();
            options.SetFailuresPreprocessor(recorder);
            tx.SetFailureHandlingOptions(options);
            return recorder;
        }

        public string Said() => Errors.Count == 0 ? "" : " Revit said: " + string.Join("; ", Errors) + ".";
    }
}
