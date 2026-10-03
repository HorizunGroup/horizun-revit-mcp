// horizun_quantities: a key that another mode reads is refused in a mode that does not read it.
//
// WHY: the modes share one argument list, so a caller can send carbon with a phase ("the carbon
// of the new construction"), room_finishes with a category, or a takeoff with a factor table.
// Each would be silently ignored and the reply would read as though it had been honoured - a
// carbon total over every phase, finishes of every room. The volume mode already refuses the
// takeoff keys (QuantitiesCommand.Execute); this is the same rule for the other three modes.
// The takeoff row lists only the keys this branch introduced, so a takeoff that worked before
// is answered exactly as before.

using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Commands
{
    public partial class QuantitiesCommand
    {
        // mode -> the keys other modes read and this one does not.
        private static readonly Dictionary<string, string[]> ForeignKeysByMode = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["takeoff"] = new[] { "level", "carbon_factors", "factor_source" },
            ["room_finishes"] = new[] { "quantities", "classification_parameter", "include_links", "category",
                                        "carbon_factors", "factor_source", "detail_level", "tolerance_pct", "only_disagreements",
                                        "categories", "rows_file" },
            ["carbon"] = new[] { "quantities", "classification_parameter", "include_links", "phase", "level", "categories", "rows_file",
                                 "detail_level", "tolerance_pct", "only_disagreements" },
        };

        // key -> the modes that do read it, for the refusal's hint.
        private static readonly Dictionary<string, string> ModesReadingKey = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["quantities"] = "takeoff",
            ["classification_parameter"] = "takeoff",
            ["include_links"] = "takeoff",
            ["categories"] = "takeoff",
            ["rows_file"] = "takeoff",
            ["group_by"] = "takeoff (group_by='room')",
            ["category"] = "volume, takeoff, carbon",
            ["phase"] = "room_finishes, takeoff with group_by='room'",
            ["level"] = "room_finishes",
            ["carbon_factors"] = "carbon",
            ["factor_source"] = "carbon",
            ["detail_level"] = "volume, takeoff",
            ["tolerance_pct"] = "volume",
            ["only_disagreements"] = "volume",
        };

        /// <summary>The refusal for the first key <paramref name="mode"/> does not read, else null.
        /// Null for 'volume' and unknown modes: those are answered by Execute itself.</summary>
        private static string ForeignModeArgument(string mode, JObject request)
        {
            string[] keys;
            if (mode == null || !ForeignKeysByMode.TryGetValue(mode, out keys)) return null;
            string key = keys.FirstOrDefault(k => request[k] != null);
            if (key == null) return null;
            return "'" + key + "' is not read in mode '" + mode + "': it would be silently ignored, and the reply would " +
                   "read as though it had been honoured. It is read by: " + ModesReadingKey[key] + ". Drop it, or use " +
                   "that mode. Nothing was measured.";
        }
    }
}
