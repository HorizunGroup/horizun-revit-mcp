using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    // Shared response policy, testable without a Revit document.
    public static class PurgeResponse
    {
        public const string PartialCoverage = "Performance Adviser examines unused families and types only; materials, styles and other purgeable elements were not examined. A zero count or convergence does not mean the model is fully purged.";

        public static void StampCoverage(JObject report, string source)
        {
            report["purge_supported"] = true;
            report["purge_source"] = source;
            if (source == "performance_adviser") report["coverage_note"] = PartialCoverage;
        }

        public static void AppendCoverageNote(JObject report, string source)
        {
            if (source != "performance_adviser") return;
            var note = (string)report["note"];
            report["note"] = string.IsNullOrEmpty(note) ? PartialCoverage : note + " " + PartialCoverage;
        }

        public static CommandResult Unavailable(JObject report, string reason, bool writeStarted)
        {
            var outcomes = new[] { new ActionOutcome { Index = 0, Error = reason ?? "Purge source unavailable", UnsupportedReason = FallbackSignal.ReasonUnsupportedCapability } };
            return FallbackDecision.Attach(CommandResult.Ok(report), FallbackDecision.Decide(outcomes, writeStarted));
        }
    }
}
