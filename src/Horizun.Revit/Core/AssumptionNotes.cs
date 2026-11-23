// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// Product rule: a datum that is missing does not block. The command takes a
// reasonable value, MARKS it as a SUPUESTO (assumption) and goes on, with an
// explicit indicator in the reply, so a reader never mistakes a chosen value for
// one the caller asked for. This file is the one place that writes the indicator,
// so every command spells it the same way:
//
//   "assumptions": [ { "flag": "SUPUESTO", "what": "...", "why": "...", "override": "..." } ]
//   "has_assumptions": true
//
// No Revit types: it is linked into the Core tests.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public static class AssumptionNotes
    {
        public const string Flag = "SUPUESTO";

        /// <summary>One assumption row. <paramref name="overrideHow"/> says how the caller replaces the value.</summary>
        public static JObject Row(string what, string why, string overrideHow)
        {
            var row = new JObject { ["flag"] = Flag, ["what"] = what ?? "", ["why"] = why ?? "" };
            if (!string.IsNullOrEmpty(overrideHow)) row["override"] = overrideHow;
            return row;
        }

        /// <summary>
        /// Appends the rows to <paramref name="payload"/> (creating "assumptions" when needed) and
        /// raises "has_assumptions". Null payload or no rows: nothing happens. Returns the payload.
        /// </summary>
        public static JObject Stamp(JObject payload, IEnumerable<JObject> rows)
        {
            if (payload == null || rows == null) return payload;
            JArray list = payload["assumptions"] as JArray;
            foreach (JObject row in rows)
            {
                if (row == null) continue;
                if (list == null) { list = new JArray(); payload["assumptions"] = list; }
                list.Add(row);
            }
            if (list != null && list.Count > 0) payload["has_assumptions"] = true;
            return payload;
        }

        /// <summary>
        /// Stamps a successful command result's payload in place. A failure, or a payload that is
        /// not a JSON object, is returned untouched: an assumption only exists on an answer.
        /// </summary>
        public static CommandResult StampResult(CommandResult result, IEnumerable<JObject> rows)
        {
            if (result == null || !result.Success || rows == null) return result;
            if (result.Data is JObject payload) Stamp(payload, rows);
            return result;
        }

        /// <summary>
        /// A command that RAN on an assumed value and then failed: the failure must say the value was assumed, or the
        /// refusal reads as one of the choice the caller made. The message is prefixed with <paramref name="prefix"/>
        /// and the rows go into the failure's detail ("assumptions", "has_assumptions"); code, fallback and the
        /// rest of the detail are kept. A success, a null result or no rows: returned untouched.
        /// </summary>
        public static CommandResult MarkFailure(CommandResult result, string prefix, IEnumerable<JObject> rows)
        {
            if (result == null || result.Success || rows == null) return result;
            var list = new List<JObject>();
            foreach (JObject row in rows) if (row != null) list.Add(row);
            if (list.Count == 0) return result;
            JObject detail = result.Detail != null ? (JObject)result.Detail.DeepClone() : new JObject();
            Stamp(detail, list);
            return CommandResult.Restore(false, result.Data, (prefix ?? "") + result.Error, result.RevitSaid,
                                         result.Fallback, result.CapabilityGaps, detail);
        }
    }
}
