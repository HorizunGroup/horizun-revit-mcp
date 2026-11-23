// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// WHICH PROPERTY, AND WHAT VALUE. A created row that fails its re-read used to be
// refused with "Requested properties do not match the committed element." and
// nothing else when the failing check was not in the row's own postconditions:
// the production checklist (type, structural, section, the declared ARC) and the
// read errors live beside it. Measured 5.0.4: curved scan walls were refused that
// way, with no property and no value, and had to be rebuilt with Python. This
// reads every place a created row can fail and names each one with what was
// asked and what the committed model holds.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public static class CreatedRowMismatch
    {
        /// <summary>One line per failing property of a created row; empty only when nothing failed.</summary>
        public static List<string> Describe(JObject row)
        {
            var wrong = new List<string>();
            if (row == null) return wrong;
            var named = new HashSet<string>(StringComparer.Ordinal);
            foreach (string list in new[] { "postconditions", "production_postconditions" })
            {
                if (!(row[list] is JObject check)) continue;
                foreach (JObject property in (check["properties"] as JArray ?? new JArray()).OfType<JObject>())
                {
                    if (property.Value<bool?>("matches") == true) continue;
                    string name = property.Value<string>("property") ?? "(unnamed)";
                    if (!named.Add(name)) continue;
                    if (name == "curve" && row["curve_verified"] is JObject) continue;   // said below, with its numbers
                    JToken error = property["error"];
                    string found = error != null && error.Type != JTokenType.Null
                        ? "(unreadable: " + error + ")"
                        : Show(property["found_in_committed_model"], property.Value<string>("unit"));
                    string line = name + ": asked " + Show(property["requested"], property.Value<string>("unit")) + ", model has " + found;
                    if (property["tolerance"] != null && property["tolerance"].Type != JTokenType.Null)
                        line += " (tolerance " + Show(property["tolerance"], property.Value<string>("unit")) + ")";
                    wrong.Add(line);
                }
                foreach (JToken missing in check["missing"] as JArray ?? new JArray())
                    if (named.Add((string)missing)) wrong.Add(missing + ": required and never checked");
            }
            if (row["curve_verified"] is JObject curve && curve.Value<bool?>("verified") != true)
            {
                var parts = new List<string>();
                foreach (string key in new[] { "built", "shape_off_mm", "third_off_mm", "centre_off_mm", "radius_off_mm", "radius_mm", "tolerance_mm" })
                    if (curve[key] != null && curve[key].Type != JTokenType.Null) parts.Add(key + " " + Show(curve[key], null));
                wrong.Add("curve: asked " + (curve.Value<string>("requested") ?? "arc") + ", model has " +
                          (parts.Count > 0 ? string.Join(", ", parts) : "(unreadable)") +
                          (curve["means"] != null ? " - " + curve.Value<string>("means") : ""));
            }
            foreach (string block in new[] { "section_verified", "diameter_verified", "structural_verified", "identity_verified" })
                if (row[block] is JObject b && b.Value<bool?>("verified") == false && !named.Contains(block))
                    wrong.Add(block.Replace("_verified", "") + ": " + b.ToString(Newtonsoft.Json.Formatting.None));
            if (row["source_comparison"] is JObject source && source.Value<bool?>("matches") == false)
                wrong.Add("source_reference: " + (source.Value<string>("error") ?? "the stored source trace differs from the one asked for"));
            if (row["error"] != null && row["error"].Type != JTokenType.Null)
                wrong.Add("re-read: " + Show(row["error"], null));
            return wrong;
        }

        /// <summary>The refusal: the properties, or - when the row carried no detail at all - says so instead of a bare period.</summary>
        public static string Message(JObject row)
        {
            var wrong = Describe(row);
            return "Requested properties do not match the committed element" +
                   (wrong.Count > 0 ? ": " + string.Join("; ", wrong) + "."
                                    : ": the re-read failed without naming a property (row verified=false, no failing check recorded).");
        }

        private static string Show(JToken value, string unit)
        {
            if (value == null || value.Type == JTokenType.Null) return "(none)";
            if ((value.Type == JTokenType.Float || value.Type == JTokenType.Integer) && unit == "feet")
                return (value.Value<double>() * 304.8).ToString("0.###", CultureInfo.InvariantCulture) + " mm";
            if (value.Type == JTokenType.Float)
                return value.Value<double>().ToString("0.####", CultureInfo.InvariantCulture) + (unit == "millimetres" ? " mm" : "");
            if (value.Type == JTokenType.Integer) return value.Value<long>().ToString(CultureInfo.InvariantCulture);
            if (value.Type == JTokenType.String) return value.Value<string>();
            return value.ToString(Newtonsoft.Json.Formatting.None);
        }
    }
}
