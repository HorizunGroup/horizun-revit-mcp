// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// THE RULES BEHIND horizun_manage_styles color schemes (create / edit / apply /
// legend), without a Revit in the room.
//
// A colour fill scheme is a small lookup table: a parameter, and for each value
// of that parameter a colour and a fill pattern. The ways one goes quietly wrong
// are all in the details, so the details are decided here:
//
//   * ENTRIES ARE A DECLARED LIST. Two entries for one value, a missing colour or
//     a colour that is not #RRGGBB are refused before Revit sees them.
//   * REVIT KEEPS ENTRIES THAT ARE IN USE. SetEntries removes only entries no
//     element uses, so "declare the whole list" cannot be promised: what the
//     re-read can promise is that every REQUESTED entry is there with its colour,
//     and the reply lists the extra entries Revit kept and why.
//   * AN ENTRY WITHOUT A FILL PATTERN COLOURS NOTHING (InvalidElementId means "no
//     pattern"), so a new entry defaults to a solid drafting pattern.
//   * WHAT COLOURS WHAT. The category (rooms, areas, spaces) is the key of both the
//     scheme and its application to a view; the words people use are mapped here.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    /// <summary>One requested entry. Value stays a token: its type is decided by the scheme's storage type.</summary>
    public sealed class ColorEntrySpec
    {
        public JToken Value;
        public string Color;          // #RRGGBB
        public string Caption;        // by-range schemes only
        public bool? Visible;
        public JToken Pattern;        // name or id; null = keep / solid
        public bool InInternalUnits;  // Value already converted for a numeric (double) scheme
        public string Key => ColorSchemeRules.ValueKey(Value);
    }

    public static class ColorSchemeRules
    {
        public const string OpList = "list_color_schemes";
        public const string OpCreate = "create_color_scheme";
        public const string OpEdit = "edit_color_scheme";
        public const string OpApply = "apply_color_scheme";
        public const string OpLegend = "place_color_legend";

        public static readonly IReadOnlyList<string> WriteOperations = new[] { OpCreate, OpEdit, OpApply, OpLegend };

        public const int MaxEntries = 250;

        /// <summary>The built-in category a scheme colours, from the words people use. Null = not an alias.</summary>
        public static string CategoryAlias(string text)
        {
            switch ((text ?? "").Trim().ToLowerInvariant())
            {
                case "rooms": case "room": case "habitaciones": case "habitacion": case "ambientes": return "OST_Rooms";
                case "areas": case "area": case "áreas": case "área": return "OST_Areas";
                case "spaces": case "space": case "espacios": case "espacio": return "OST_MEPSpaces";
                default: return null;
            }
        }

        /// <summary>A stable key for an entry value, so two spellings of one value collide.</summary>
        public static string ValueKey(JToken value)
        {
            if (value == null || value.Type == JTokenType.Null) return null;
            switch (value.Type)
            {
                case JTokenType.Integer: return "n:" + value.Value<long>().ToString(CultureInfo.InvariantCulture);
                case JTokenType.Float:
                {
                    double d = value.Value<double>();
                    return d == Math.Floor(d) && Math.Abs(d) < 1e15
                        ? "n:" + ((long)d).ToString(CultureInfo.InvariantCulture)
                        : "n:" + d.ToString("R", CultureInfo.InvariantCulture);
                }
                case JTokenType.String: return "s:" + value.Value<string>();
                default: return "x:" + value.ToString(Newtonsoft.Json.Formatting.None);
            }
        }

        /// <summary>
        /// Parse an entries array: value and colour on every entry, a value once, a colour that is
        /// #RRGGBB, caption text that is not blank, visibility a boolean, a pattern a name or an id.
        /// </summary>
        public static List<ColorEntrySpec> ParseEntries(JToken token, out string error)
        {
            error = null;
            var array = token as JArray;
            if (array == null) { error = "entries must be an array of { value, color, caption?, visible?, pattern? }."; return null; }
            if (array.Count == 0) { error = "entries is empty: a scheme with no entry colours nothing. Omit entries to leave them as they are."; return null; }
            if (array.Count > MaxEntries) { error = "entries holds " + array.Count + " rows; the limit is " + MaxEntries + "."; return null; }
            var result = new List<ColorEntrySpec>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < array.Count; i++)
            {
                var row = array[i] as JObject;
                if (row == null) { error = "entries[" + i + "] must be an object."; return null; }
                foreach (JProperty p in row.Properties())
                    if (p.Name != "value" && p.Name != "color" && p.Name != "caption" && p.Name != "visible" && p.Name != "pattern")
                    { error = "entries[" + i + "]." + p.Name + " is not an entry attribute (value, color, caption, visible, pattern)."; return null; }
                JToken value = row["value"];
                if (value == null || value.Type == JTokenType.Null || value.Type == JTokenType.Object || value.Type == JTokenType.Array ||
                    (value.Type == JTokenType.String && string.IsNullOrEmpty(value.Value<string>())))
                { error = "entries[" + i + "].value is required: the parameter value this entry colours (text, number or id)."; return null; }
                var spec = new ColorEntrySpec { Value = value.DeepClone() };
                string key = spec.Key;
                if (!seen.Add(key)) { error = "entries[" + i + "].value repeats an earlier entry (" + value + "); one value takes one colour."; return null; }
                byte r, g, b;
                string colorText = row.Value<string>("color");
                if (!ScheduleAppearanceRules.TryParseColor(colorText, out r, out g, out b))
                { error = "entries[" + i + "].color must be #RRGGBB."; return null; }
                spec.Color = ScheduleAppearanceRules.Hex(r, g, b);
                if (row["caption"] != null)
                {
                    string caption = row.Value<string>("caption");
                    if (string.IsNullOrWhiteSpace(caption)) { error = "entries[" + i + "].caption cannot be blank."; return null; }
                    spec.Caption = caption;
                }
                if (row["visible"] != null)
                {
                    if (row["visible"].Type != JTokenType.Boolean) { error = "entries[" + i + "].visible must be true or false."; return null; }
                    spec.Visible = row.Value<bool>("visible");
                }
                if (row["pattern"] != null)
                {
                    if (row["pattern"].Type != JTokenType.String && row["pattern"].Type != JTokenType.Integer)
                    { error = "entries[" + i + "].pattern must be a fill pattern name or id."; return null; }
                    spec.Pattern = row["pattern"].DeepClone();
                }
                result.Add(spec);
            }
            return result;
        }

        public static readonly IReadOnlyList<string> ValueUnits = new[] { "internal", "mm", "m", "feet", "m2", "ft2" };

        /// <summary>
        /// A numeric (double) scheme stores Revit internal units (feet, square feet). Review R3 (5.2): a value sent as
        /// 20 meaning m2 was written as 20 ft2 (about 1.86 m2) and the read-back, raw against raw, called it verified.
        /// The caller now states value_units; each numeric value is converted once, and the verification compares
        /// internal against internal. Returns null when done.
        /// </summary>
        public static string NumericToInternal(List<ColorEntrySpec> entries, string valueUnits)
        {
            if (entries == null) return null;
            double factor;
            switch ((valueUnits ?? "").Trim().ToLowerInvariant())
            {
                case "internal": case "feet": case "ft2": factor = 1.0; break;
                case "mm": factor = 1.0 / 304.8; break;
                case "m": factor = 1.0 / 0.3048; break;
                case "m2": factor = 1.0 / 0.09290304; break;
                case "": return "this scheme colours a numeric (length or area) parameter, stored in Revit internal units: say value_units " +
                                "(mm, m or feet for a length; m2 or ft2 for an area; internal to pass feet / square feet as they are). Nothing was written.";
                default: return "value_units must be one of: " + string.Join(", ", ValueUnits) + ". Nothing was written.";
            }
            foreach (ColorEntrySpec e in entries)
            {
                if (e.InInternalUnits) continue;
                if (e.Value.Type != JTokenType.Integer && e.Value.Type != JTokenType.Float)
                    return "this scheme colours a numeric parameter; entry value '" + e.Value + "' is not a number. Nothing was written.";
                e.Value = new JValue(e.Value.Value<double>() * factor);
                e.InInternalUnits = true;
            }
            return null;
        }

        /// <summary>
        /// Shape of the scheme-level settings shared by create and edit: nothing here needs a document.
        /// Returns null when well formed.
        /// </summary>
        public static string ValidateSettings(JObject r, bool create, out List<ColorEntrySpec> entries)
        {
            entries = null;
            if (r["by_range"] != null && r["by_range"].Type != JTokenType.Boolean) return "by_range must be true or false.";
            if (r["include_links"] != null && r["include_links"].Type != JTokenType.Boolean) return "include_links must be true or false.";
            if (r["title"] != null && string.IsNullOrWhiteSpace(r.Value<string>("title"))) return "title cannot be blank.";
            if (r["parameter"] != null && string.IsNullOrWhiteSpace(r.Value<string>("parameter"))) return "parameter cannot be blank (a name or an id).";
            if (r["value_units"] != null && !ValueUnits.Contains((r.Value<string>("value_units") ?? "").Trim().ToLowerInvariant()))
                return "value_units must be one of: " + string.Join(", ", ValueUnits) + ".";
            if (r["entries"] != null)
            {
                string error;
                entries = ParseEntries(r["entries"], out error);
                if (entries == null) return error;
            }
            if (!create && r["parameter"] == null && r["by_range"] == null && r["include_links"] == null &&
                r["title"] == null && r["entries"] == null)
                return "edit_color_scheme changes nothing: pass parameter, by_range, include_links, title and/or entries.";
            if (r["by_range"] != null && r.Value<bool>("by_range") && r["entries"] != null && entries != null)
            {
                foreach (ColorEntrySpec e in entries)
                    if (e.Value.Type != JTokenType.Integer && e.Value.Type != JTokenType.Float)
                        return "a by_range scheme takes numeric entry values (each is where its colour starts); '" + e.Value + "' is not a number.";
            }
            return null;
        }

        /// <summary>The view of an apply / legend must be named; a clear needs the category.</summary>
        public static string ValidateApply(JObject r)
        {
            if (r["view_id"] == null) return "view_id is required: the view that receives the scheme.";
            if (r["scheme_id"] == null && r["category"] == null)
                return "apply_color_scheme needs scheme_id (the scheme to apply) or, to REMOVE the scheme from the view, only category.";
            return null;
        }

        public static string ValidateLegend(JObject r)
        {
            if (r["view_id"] == null) return "view_id is required: the view that receives the legend (a plan or a legend view).";
            if (r["scheme_id"] == null && r["category"] == null)
                return "place_color_legend needs scheme_id or category: which colour scheme the legend explains.";
            JArray point = r["point"] as JArray;
            if (point == null || point.Count < 2 || point.Count > 3 || point.Any(t => t.Type != JTokenType.Integer && t.Type != JTokenType.Float))
                return "point is required: [x, y] (or [x, y, z]) where the legend's top-left corner goes, in the units of 'units'.";
            return null;
        }
    }
}
