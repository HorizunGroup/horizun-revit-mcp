// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// THE REVIT-FREE HALF OF AN ELEMENT SELECTOR. horizun_delete_verified mode=query and
// horizun_measure_geometry take the same small selector instead of an id list the caller
// had to build in Python first:
//
//   { "category": "Pipes", "family": "Pipe Types", "prefix": "HZ_",
//     "parameter": {"name": "Comments", "value": "temp"}   (or "Comments=temp"),
//     "created_since": "2026-10-23T08:00:00Z" }
//
// Every key narrows (AND). A selector with no key is refused: "everything" is never an
// accident. `created_since` is read from the HRZ ownership mark (utc), the only creation
// time a Revit element has; an element WITHOUT the mark cannot be dated, so it is never
// matched by a dated selector and is counted in `undatable` instead of being guessed.
// An element whose facts cannot be read is `unknown`, never a silent non-match.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public sealed class SelectorSpec
    {
        public string Category;
        public string Family;
        public string Prefix;
        public string ParameterName;
        public string ParameterValue;
        public DateTime? CreatedSinceUtc;

        public bool HasParameter => !string.IsNullOrEmpty(ParameterName);
    }

    /// <summary>One parameter read: found or not, readable or not, and its text.</summary>
    public struct SelectorParam
    {
        public bool Found;
        public bool Readable;
        public string Text;
        public static SelectorParam Missing => new SelectorParam { Found = false, Readable = true };
        public static SelectorParam Unreadable => new SelectorParam { Found = true, Readable = false };
        public static SelectorParam Of(string text) => new SelectorParam { Found = true, Readable = true, Text = text };
    }

    /// <summary>What the command read off one element, in plain values.</summary>
    public sealed class SelectorFacts
    {
        public string CategoryName;
        public string CategoryToken;
        public string Family;
        public string TypeName;
        public string Name;
        public Func<string, SelectorParam> Parameter;
        public DateTime? CreatedUtc;
        public bool HasMark;
    }

    public enum SelectorVerdict { Match, NoMatch, Unknown, Undatable }

    public static class ElementSelectorRules
    {
        public static readonly string[] Keys = { "category", "family", "prefix", "parameter", "created_since" };

        public static bool TryParse(JToken token, out SelectorSpec spec, out string error)
        {
            spec = null; error = null;
            var o = token as JObject;
            if (o == null) { error = "selector must be an object with category, family, prefix, parameter or created_since."; return false; }
            foreach (JProperty p in o.Properties())
                if (Array.IndexOf(Keys, p.Name) < 0)
                { error = "selector key '" + p.Name + "' is not one of " + string.Join(", ", Keys) + "."; return false; }
            var s = new SelectorSpec
            {
                Category = Text(o["category"]), Family = Text(o["family"]), Prefix = Text(o["prefix"])
            };
            JToken par = o["parameter"];
            if (par != null && par.Type != JTokenType.Null)
            {
                if (par.Type == JTokenType.String)
                {
                    string raw = (string)par;
                    int eq = raw.IndexOf('=');
                    if (eq <= 0) { error = "selector.parameter as text must read Name=Value."; return false; }
                    s.ParameterName = raw.Substring(0, eq).Trim();
                    s.ParameterValue = raw.Substring(eq + 1).Trim();
                }
                else if (par is JObject po)
                {
                    s.ParameterName = Text(po["name"]);
                    s.ParameterValue = po["value"] == null || po["value"].Type == JTokenType.Null ? null : po["value"].ToString();
                    if (string.IsNullOrEmpty(s.ParameterName) || s.ParameterValue == null)
                    { error = "selector.parameter needs name and value."; return false; }
                }
                else { error = "selector.parameter must be {name, value} or 'Name=Value'."; return false; }
            }
            string since = Text(o["created_since"]);
            if (o["created_since"] != null && o["created_since"].Type == JTokenType.Date)
            {
                // A JSON reader that already turned the text into a date: take the instant, in UTC.
                s.CreatedSinceUtc = o["created_since"].Value<DateTime>().ToUniversalTime();
                since = null;
            }
            if (since != null)
            {
                if (!TryParseUtc(since, out DateTime utc))
                { error = "selector.created_since must be an ISO 8601 timestamp such as 2026-10-23T08:00:00Z."; return false; }
                s.CreatedSinceUtc = utc;
            }
            if (s.Category == null && s.Family == null && s.Prefix == null && !s.HasParameter && s.CreatedSinceUtc == null)
            { error = "selector is empty: name at least one of " + string.Join(", ", Keys) + ". Selecting everything is never implied."; return false; }
            spec = s;
            return true;
        }

        public static bool TryParseUtc(string text, out DateTime utc)
        {
            return DateTime.TryParse(text, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out utc);
        }

        public static SelectorVerdict Evaluate(SelectorSpec s, SelectorFacts f)
        {
            if (s.Category != null && !CategoryMatches(s.Category, f)) return SelectorVerdict.NoMatch;
            if (s.Family != null && !Same(s.Family, f.Family)) return SelectorVerdict.NoMatch;
            if (s.Prefix != null && !(StartsWith(f.Family, s.Prefix) || StartsWith(f.TypeName, s.Prefix) || StartsWith(f.Name, s.Prefix)))
                return SelectorVerdict.NoMatch;
            if (s.HasParameter)
            {
                SelectorParam p = f.Parameter == null ? SelectorParam.Missing : f.Parameter(s.ParameterName);
                if (!p.Found) return SelectorVerdict.NoMatch;
                if (!p.Readable) return SelectorVerdict.Unknown;
                if (!ParameterValueMatches(p.Text, s.ParameterValue)) return SelectorVerdict.NoMatch;
            }
            if (s.CreatedSinceUtc.HasValue)
            {
                if (!f.HasMark || !f.CreatedUtc.HasValue) return SelectorVerdict.Undatable;
                if (f.CreatedUtc.Value < s.CreatedSinceUtc.Value) return SelectorVerdict.NoMatch;
            }
            return SelectorVerdict.Match;
        }

        public static bool CategoryMatches(string wanted, SelectorFacts f)
        {
            return Same(wanted, f.CategoryName) || Same(wanted, f.CategoryToken);
        }

        public static bool ParameterValueMatches(string actual, string expected)
        {
            if (actual == null || expected == null) return false;
            if (string.Equals(actual.Trim(), expected.Trim(), StringComparison.OrdinalIgnoreCase)) return true;
            if (double.TryParse(actual, NumberStyles.Float, CultureInfo.InvariantCulture, out double a) &&
                double.TryParse(expected, NumberStyles.Float, CultureInfo.InvariantCulture, out double b))
                return Math.Abs(a - b) <= 1e-6 * Math.Max(1.0, Math.Abs(b));
            return false;
        }

        private static bool Same(string a, string b) =>
            a != null && b != null && string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

        private static bool StartsWith(string text, string prefix) =>
            text != null && text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);

        private static string Text(JToken t)
        {
            if (t == null || t.Type == JTokenType.Null) return null;
            string s = t.ToString().Trim();
            return s.Length == 0 ? null : s;
        }
    }
}
