// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// A DELETE PREVIEW THAT ANNOUNCED 3,494 CASCADED ELEMENTS AND SHOWED 200 (field cycle, 2026-10-21).
//
// MEASURED: the dry run of deleting 4,472 walls (and again 16 stairs) said Revit would take
// 3,494 (356) more elements with them, listed the first 200 - ids only, no category - and
// the rest were invisible. To know what else was going, the agent counted the categories of
// the whole model before and after. The ids are capped for size, but the CENSUS need not be:
// a count per category over the whole cascade is a few lines, however long the list is.
//
// Revit-free: the counting and the wording of the cap.
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public static class CascadeCensusRules
    {
        /// <summary>The bucket for a cascaded element whose category could not be read (after a real delete it no longer exists).</summary>
        public const string Unreadable = "(category not readable)";

        /// <summary>Counts per category over the WHOLE cascade, biggest first, never capped.</summary>
        public static JObject ByCategory(IEnumerable<string> categories)
        {
            var counts = new Dictionary<string, int>(System.StringComparer.Ordinal);
            if (categories != null)
                foreach (string c in categories)
                {
                    string key = string.IsNullOrWhiteSpace(c) ? Unreadable : c;
                    counts[key] = counts.TryGetValue(key, out int n) ? n + 1 : 1;
                }
            var result = new JObject();
            foreach (var kv in counts.OrderByDescending(k => k.Value).ThenBy(k => k.Key, System.StringComparer.Ordinal))
                result[kv.Key] = kv.Value;
            return result;
        }

        /// <summary>The sentence that says the id list is capped and that the census above it is not.</summary>
        public static string CapNote(int total, int shown)
        {
            if (total <= shown) return null;
            return "cascaded rows list the first " + shown + " of " + total + " ids (id_cap); cascaded_by_category counts all " +
                   total + ". Raise id_cap to list more.";
        }
    }
}
