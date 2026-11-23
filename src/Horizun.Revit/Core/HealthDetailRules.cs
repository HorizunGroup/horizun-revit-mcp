// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// horizun_health detail=compact|full (4.2-N5, LP-P3-12). Field data: every health
// answer was ~10,000 characters, and it is the first call of every task. Compact
// keeps what answers "which Revit, which build, which document, what may I do" and
// leaves out the diagnostics only a support session reads. It SAYS what it left out
// and how to get it, so a smaller answer is never mistaken for a smaller state.
// -----------------------------------------------------------------------------
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public static class HealthDetailRules
    {
        public static readonly string[] OmittedWhenCompact =
        {
            "horizun_hub", "data_paths", "registry", "jobs", "timings", "clients",
            "recent_horizun_writes", "verification_catalog"
        };

        /// <summary>Removes the heavy blocks in place when compact, and names them. Returns the payload.</summary>
        public static JObject Apply(JObject payload, bool compact, bool includeVerificationCatalog = false)
        {
            if (payload == null) return null;
            payload["detail"] = compact ? "compact" : "full";
            if (!compact) return payload;
            var left = OmittedWhenCompact.Where(k => !(includeVerificationCatalog && k == "verification_catalog") && payload.Remove(k)).ToList();
            // The one bit of the registry a caller branches on stays.
            payload["omitted_in_compact"] = new JArray(left);
            payload["omitted_how_to_get"] = "Call horizun_health with detail=full (include_verification_catalog=true for the catalog).";
            return payload;
        }
    }
}
