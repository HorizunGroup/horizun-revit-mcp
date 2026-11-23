// Horizun Revit MCP - original Horizun code.
using System;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    /// <summary>One machine-readable declaration for partial reads, including older producers.</summary>
    public static class ReadResultCoverage
    {
        public static bool Stamp(JObject payload)
        {
            if (payload == null) return false;
            // Application declarations describe writes, not read coverage. A declared
            // no-op listing (requested=0) is still a read and may carry partial coverage.
            var application = payload["application"] as JObject;
            if ((application?["requested"]?.Type == JTokenType.Integer && (long)application["requested"] > 0) ||
                payload["dry_run"]?.Type == JTokenType.Boolean) return false;
            bool partial = IsPartial(payload["result"]) || IsPartial(payload["status"]) ||
                (payload["partial"]?.Type == JTokenType.Boolean && (bool)payload["partial"]);
            if (!partial) return false;
            payload["partial"] = true;
            var details = new JObject();
            foreach (string key in new[] { "coverage", "visibility_coverage", "coverage_complete", "truncated", "next_offset", "scope_note" })
                if (payload[key] != null) details[key] = payload[key].DeepClone();
            payload["read_coverage"] = new JObject
            {
                ["state"] = "partial", ["complete"] = false,
                // Missing coverage is explicitly unknown, never fabricated arithmetic.
                ["coverage_reported"] = payload["coverage"] != null || payload["visibility_coverage"] != null || payload["coverage_complete"] != null,
                ["details"] = details
            };
            return true;
        }

        private static bool IsPartial(JToken token) => token?.Type == JTokenType.String &&
            string.Equals((string)token, "partial", StringComparison.OrdinalIgnoreCase);
    }
}
