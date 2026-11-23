// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// horizun_quantities, mode omitted. The tool has four modes and the default is 'volume'. A caller
// that names only keys that ONE mode reads ('categories', 'quantities', 'carbon_factors', ...)
// plainly means that mode, and used to get a refusal ("'categories' is not read in mode
// 'volume'") for leaving out a word. That refusal is the most repeated failure of the tool.
//
// The rule: when 'mode' is ABSENT and the keys sent belong to exactly one other mode, run that mode
// and say so as an assumption (SUPUESTO). Nothing is inferred when:
//   * 'mode' is written: a caller who wrote volume and sent takeoff keys gets the refusal, because
//     that is a contradiction, not a missing datum;
//   * the request ALSO carries a key the inferred mode does not read (tolerance_pct, level, ...):
//     that is keys of two modes, and the reply is a refusal naming the keys of each mode.
// A key counts as SENT only when its value is neither null nor false: "only_disagreements": false
// asks for nothing (and such a key the inferred mode does not read is dropped, and said so).
// Which keys decide: only those of TakeoffOnly / CarbonOnly. Keys several modes read ('phase',
// 'category', 'detail_level') never decide. 'level' is read only by room_finishes, which is never
// inferred, so it does not decide either - beside a takeoff or carbon key it is a mixed request.
// 'include_links' and 'group_by' are read only by takeoff, but they do not decide on their own:
// they are generic words a caller sends while meaning volume, and the volume refusal names them.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public sealed class QuantitiesModeDecision
    {
        /// <summary>The mode to run. Null when the request names one itself or nothing decides.</summary>
        public string InferredMode;
        /// <summary>The key that decided, for the assumption text.</summary>
        public string DecidingKey;
        /// <summary>
        /// Set when mode was not sent and the keys sent belong to more than one mode: the refusal, naming the
        /// keys of each mode. Nothing is inferred then, and nothing runs.
        /// </summary>
        public string Refusal;
    }

    public static class QuantitiesModeInference
    {
        // Keys that only ONE mode reads, and so may decide it. Kept in step with QuantitiesCommand's tables.
        private static readonly string[] TakeoffOnly = { "quantities", "classification_parameter", "categories", "rows_file" };
        private static readonly string[] CarbonOnly = { "carbon_factors", "factor_source" };

        // Keys the INFERRED mode does not read. Any of them sent beside a deciding key means the request
        // mixes modes, and the inference is refused rather than run with a key silently ignored.
        private static readonly Dictionary<string, string[]> NotReadBy = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["takeoff"] = new[] { "tolerance_pct", "only_disagreements", "level", "carbon_factors", "factor_source" },
            ["carbon"] = new[] { "quantities", "classification_parameter", "include_links", "phase", "level", "group_by",
                                 "categories", "rows_file", "detail_level", "tolerance_pct", "only_disagreements" },
        };

        // Which modes read each key, for the refusal.
        private static readonly Dictionary<string, string> ReadBy = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["quantities"] = "takeoff", ["classification_parameter"] = "takeoff", ["categories"] = "takeoff", ["rows_file"] = "takeoff",
            ["include_links"] = "takeoff", ["group_by"] = "takeoff", ["phase"] = "takeoff (group_by='room'), room_finishes",
            ["level"] = "room_finishes", ["carbon_factors"] = "carbon", ["factor_source"] = "carbon",
            ["detail_level"] = "volume, takeoff", ["tolerance_pct"] = "volume", ["only_disagreements"] = "volume",
        };

        /// <summary>True when the request carries <paramref name="key"/> with a value that asks for something (not null, not false).</summary>
        public static bool IsSent(JObject request, string key)
        {
            JToken t = request?[key];
            if (t == null || t.Type == JTokenType.Null || t.Type == JTokenType.Undefined) return false;
            if (t.Type == JTokenType.Boolean && !(bool)t) return false;
            return true;
        }

        public static QuantitiesModeDecision Decide(JObject request)
            => request == null ? new QuantitiesModeDecision()
             : Decide(request["mode"]?.Type == JTokenType.String ? (string)request["mode"] : null, k => IsSent(request, k));

        public static QuantitiesModeDecision Decide(string modeText, Func<string, bool> hasKey)
        {
            var none = new QuantitiesModeDecision();
            if (!string.IsNullOrWhiteSpace(modeText) || hasKey == null) return none;
            var takeoffKeys = TakeoffOnly.Where(hasKey).ToList();
            var carbonKeys = CarbonOnly.Where(hasKey).ToList();
            if (takeoffKeys.Count == 0 && carbonKeys.Count == 0) return none;
            string mode = takeoffKeys.Count > 0 ? "takeoff" : "carbon";
            List<string> deciding = mode == "takeoff" ? takeoffKeys : carbonKeys;
            var foreign = NotReadBy[mode].Where(hasKey).ToList();
            if (foreign.Count > 0)
                return new QuantitiesModeDecision { Refusal = MixedRefusal(mode, deciding, foreign) };
            return new QuantitiesModeDecision { InferredMode = mode, DecidingKey = deciding[0] };
        }

        private static string MixedRefusal(string mode, IList<string> deciding, IList<string> foreign)
        {
            var parts = new List<string>();
            foreach (string k in deciding.Concat(foreign))
                parts.Add("'" + k + "' is read by " + (ReadBy.TryGetValue(k, out string by) ? by : "another mode"));
            return "mode was not sent, and the keys sent belong to more than one mode, so no mode was deduced: " +
                   string.Join("; ", parts) + ". '" + deciding[0] + "' alone would mean mode '" + mode + "', which does not read " +
                   string.Join(", ", foreign.Select(k => "'" + k + "'")) + ". Send mode explicitly ('volume', 'takeoff', 'room_finishes' or 'carbon') " +
                   "and only the keys that mode reads. Nothing was measured.";
        }

        /// <summary>The keys <paramref name="mode"/> does not read (empty for a mode that is never inferred).</summary>
        public static IReadOnlyList<string> NotReadByMode(string mode)
            => mode != null && NotReadBy.TryGetValue(mode, out string[] keys) ? keys : new string[0];

        public static JObject AssumptionRow(QuantitiesModeDecision d)
            => AssumptionNotes.Row(
                "mode = '" + d.InferredMode + "'",
                "mode was not sent and '" + d.DecidingKey + "' is read only by mode '" + d.InferredMode + "', so that mode ran instead of the default 'volume'.",
                "send mode explicitly ('volume', 'takeoff', 'room_finishes' or 'carbon').");

        /// <summary>The words a FAILED run starts with when its mode was deduced, so the refusal is not read as one of the mode the caller chose.</summary>
        public static string FailurePrefix(QuantitiesModeDecision d)
            => "mode was not sent; it was deduced as '" + d.InferredMode + "' from '" + d.DecidingKey + "' (SUPUESTO), and that mode refused: ";
    }
}
