// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// horizun_write_params_verified with a SELECTOR: the same value(s) written to every
// element a selector matches, instead of a writes list the caller built in Python.
//
// field cycle ELE (2026-10-24, L6): 6,929 elements x 2 provenance parameters = 13,858
// writes went through horizun_execute_python because write_params_verified only took
// an explicit list or a CSV. The selector is the one horizun_delete_verified mode=query
// and horizun_measure_geometry already take (ElementSelectorRules); the expansion here
// is pure: ids x parameters -> write rows, skipping a cell that already holds a value
// when only_if_empty asks for it. The rows then ride the command's ordinary path -
// resolved, rehearsed, bound by the token, written in ONE transaction and re-read row by
// row after the commit.
//
// Never partial by accident: more targets than MaxTargets, or more rows than MaxWrites,
// refuse the whole call (the caller narrows the selector); an element whose facts cannot
// be read against the selector refuses it too, because what it matches is then unknown.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public static class SelectorWriteRules
    {
        public const int MaxTargets = 10000;
        public const int MaxParameters = 10;
        public const int MaxWrites = 20000;

        /// <summary>Null when `set` is a usable {parameter: value} map; otherwise why not.</summary>
        public static string SetError(JToken set)
        {
            if (!(set is JObject o) || !o.Properties().Any())
                return "set is required with selector: {parameter: value}, the value every matched element receives.";
            if (o.Count > MaxParameters) return "set names " + o.Count + " parameters; at most " + MaxParameters + " per call.";
            foreach (JProperty p in o.Properties())
            {
                if (string.IsNullOrWhiteSpace(p.Name)) return "set has a blank parameter name.";
                JTokenType t = p.Value.Type;
                if (t != JTokenType.String && t != JTokenType.Integer && t != JTokenType.Float && t != JTokenType.Boolean && t != JTokenType.Null)
                    return "set['" + p.Name + "'] must be a string, number, boolean or null - one value for every matched element.";
            }
            return null;
        }

        /// <summary>
        /// ids x parameters -> write rows {target_id, parameter, value}. isEmpty(id, parameter) answers whether the cell
        /// holds no value now: true, false, or null when it could not be read (then the row is generated and the
        /// command names the reason on that row - an unreadable cell is never skipped as if it were full).
        /// </summary>
        public static JArray Expand(IList<long> ids, JObject set, bool onlyIfEmpty, Func<long, string, bool?> isEmpty, out int skippedNotEmpty)
        {
            skippedNotEmpty = 0;
            var rows = new JArray();
            foreach (long id in ids)
                foreach (JProperty p in set.Properties())
                {
                    if (onlyIfEmpty && isEmpty != null && isEmpty(id, p.Name) == false) { skippedNotEmpty++; continue; }
                    rows.Add(new JObject { ["target_id"] = id, ["parameter"] = p.Name, ["value"] = p.Value.DeepClone() });
                }
            return rows;
        }

        /// <summary>The refusal for a selector that matched too much, or null.</summary>
        public static string SizeError(int matched, bool truncated, int parameters)
        {
            if (truncated || matched > MaxTargets)
                return "The selector matches more than " + MaxTargets + " elements; a partial write is never silently chosen. " +
                       "Narrow it (category, family, prefix, parameter or created_since), or split it by level.";
            if ((long)matched * parameters > MaxWrites)
                return matched + " elements x " + parameters + " parameters is more than " + MaxWrites + " writes in one call; " +
                       "split the parameters or the selector.";
            return null;
        }
    }
}
