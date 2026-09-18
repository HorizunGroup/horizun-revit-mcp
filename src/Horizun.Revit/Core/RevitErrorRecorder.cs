// -----------------------------------------------------------------------------
// Horizun Revit MCP — original Horizun code.
//
// A ROLLBACK SAYS WHY. MEASURED (campaign 5, a door on a wall being split): a
// set_curve came back "Revit returned RolledBack" and nothing else - the error
// Revit raised (the door no longer had its wall under it) was resolved by Revit
// itself as a rollback and never reached the caller, who could only "retry in
// smaller batches". This preprocessor RECORDS the error-severity failures of one
// transaction and changes nothing: it resolves none, deletes no warning, and
// lets Revit's own handling run exactly as before.
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace Horizun.Revit.Core
{
    public sealed class RevitErrorRecorder : IFailuresPreprocessor
    {
        public readonly List<string> Errors = new List<string>();

        public FailureProcessingResult PreprocessFailures(FailuresAccessor a)
        {
            foreach (FailureMessageAccessor f in a.GetFailureMessages())
            {
                FailureSeverity severity;
                try { severity = f.GetSeverity(); } catch { continue; }
                if (severity == FailureSeverity.Warning || severity == FailureSeverity.None) continue;
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
                string line = desc + ids;
                if (!Errors.Contains(line)) Errors.Add(line);
            }
            return FailureProcessingResult.Continue;
        }

        /// <summary>Attach to a transaction before it starts; returns the recorder.</summary>
        public static RevitErrorRecorder On(Transaction tx)
        {
            var recorder = new RevitErrorRecorder();
            FailureHandlingOptions options = tx.GetFailureHandlingOptions();
            options.SetFailuresPreprocessor(recorder);
            tx.SetFailureHandlingOptions(options);
            return recorder;
        }

        public string Said() => Errors.Count == 0 ? "" : " Revit said: " + string.Join("; ", Errors) + ".";
    }
}
