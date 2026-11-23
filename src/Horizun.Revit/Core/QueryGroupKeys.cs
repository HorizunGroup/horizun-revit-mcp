// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// group_by: ["parameter:<name>"] in horizun_query_model (5.0.4). Field 2026-11: the most
// repeated Python script of one device read walls and floors, filtered them by a Comments
// prefix and counted them per comment and level - a histogram the typed query could not
// group because its keys were only category, level, type, family and source. The value
// grouped on is the one a person sees (AsValueString), and an absent or unreadable
// parameter is its own group, named, never folded into an empty string.
// -----------------------------------------------------------------------------
using System;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public static class QueryGroupKeys
    {
        public const string Prefix = "parameter:";
        public const string Absent = "(absent)";
        public const string Unreadable = "(unreadable)";
        public const string Empty = "(empty)";

        /// <summary>The parameter a "parameter:&lt;name&gt;" key names, or null for any other key.</summary>
        public static string ParameterOf(string key)
        {
            if (key == null || !key.StartsWith(Prefix, StringComparison.Ordinal)) return null;
            string name = key.Substring(Prefix.Length).Trim();
            return name.Length == 0 ? null : name;
        }

        /// <summary>The group a row falls in for one projected parameter cell.</summary>
        public static string ValueOf(JObject cell)
        {
            if (cell == null || cell["read_error"] != null) return Unreadable;
            if (cell.Value<bool?>("exists") == false) return Absent;
            string shown = cell["display"]?.Type == JTokenType.String ? (string)cell["display"] : null;
            if (string.IsNullOrEmpty(shown))
            {
                JToken raw = cell["raw"];
                shown = raw == null || raw.Type == JTokenType.Null ? null : raw.ToString();
            }
            return string.IsNullOrEmpty(shown) ? Empty : shown;
        }
    }
}
