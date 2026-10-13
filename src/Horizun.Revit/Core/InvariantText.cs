// -----------------------------------------------------------------------------
// Horizun Core - original Horizun code. Revit-free.
//
// NUMBERS AND SCALARS AS TEXT, THE SAME ON EVERY MACHINE.
//
// Audit of 2026-10-08 (the machines this runs on are Spanish-locale):
//
//   * double.TryParse(s, NumberStyles.Any, Invariant) reads "1,5" as 15 - the
//     comma is taken as a thousands separator - so a value of one and a half
//     passed "gt 10". It also read "(5)" as -5 and accepted currency symbols.
//   * JValue.ToString() formats with the CURRENT culture: 1.5 became "1,5" on a
//     Spanish machine and true became "True", so comparisons against Revit's
//     invariant text ("1.5", "1" for Yes) silently never matched.
//
// MeasuredNumber reads a measured text as a number or refuses; it never guesses
// between a decimal and a grouping separator. JsonScalar turns a JSON scalar into
// the invariant text Revit's readers produce.
// -----------------------------------------------------------------------------
using System;
using System.Globalization;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public static class MeasuredNumber
    {
        private static readonly Regex DecimalComma = new Regex(@"^\s*([+-]?)(\d+),(\d+)\s*$", RegexOptions.CultureInvariant);

        /// <summary>
        /// The number a measured text states, or false. Invariant form ("1.5", "-2e3")
        /// first. A single comma and no point is a decimal comma ("1,5", "0,125"),
        /// EXCEPT when it reads equally as a thousands group - one to three digits not
        /// starting with 0, then exactly three ("1,234") - which is refused as
        /// ambiguous. Text with both separators, currency, parentheses or units is not a
        /// number here. Nothing is ever multiplied by a power of ten on a guess.
        /// </summary>
        public static bool TryParse(string text, out double value)
        {
            value = 0;
            if (string.IsNullOrWhiteSpace(text)) return false;
            if (text.IndexOf(',') < 0)
                return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && IsFinite(value);
            Match m = DecimalComma.Match(text);
            if (!m.Success) return false;
            string whole = m.Groups[2].Value, fraction = m.Groups[3].Value;
            bool couldBeGrouping = fraction.Length == 3 && whole.Length <= 3 && whole[0] != '0';
            if (couldBeGrouping) return false;
            return double.TryParse(m.Groups[1].Value + whole + "." + fraction, NumberStyles.Float,
                                   CultureInfo.InvariantCulture, out value) && IsFinite(value);
        }

        private static bool IsFinite(double v) => !double.IsNaN(v) && !double.IsInfinity(v);

        /// <summary>Equal within a relative tolerance fit for values that went through text ("R" round-trips).</summary>
        public static bool Same(double a, double b) =>
            a == b || Math.Abs(a - b) <= 1e-9 * Math.Max(1.0, Math.Max(Math.Abs(a), Math.Abs(b)));
    }

    public static class JsonScalar
    {
        /// <summary>
        /// A JSON scalar as the invariant text Revit's readers produce: integers and
        /// floats in the invariant culture (floats round-trip, "R"), booleans as the
        /// stored integer of a Yes/No parameter ("1"/"0"), strings as they are. Null
        /// stays null. Never the current culture.
        /// </summary>
        public static string ToInvariantString(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null || token.Type == JTokenType.Undefined) return null;
            switch (token.Type)
            {
                case JTokenType.Integer: return ((long)token).ToString(CultureInfo.InvariantCulture);
                case JTokenType.Float: return ((double)token).ToString("R", CultureInfo.InvariantCulture);
                case JTokenType.Boolean: return (bool)token ? "1" : "0";
                case JTokenType.String: return (string)token;
                default:
                    return token is JValue v
                        ? Convert.ToString(v.Value, CultureInfo.InvariantCulture)
                        : token.ToString(Newtonsoft.Json.Formatting.None);
            }
        }

        /// <summary>
        /// Does a measured text equal a JSON rule value? Numbers (and booleans, as 1/0)
        /// compare BY VALUE through MeasuredNumber; strings compare case-insensitively,
        /// as the requirement-set documents. Culture never enters.
        /// </summary>
        public static bool MeasuredEquals(string measured, JToken expected)
        {
            if (measured == null || expected == null) return false;
            if (expected.Type == JTokenType.Integer || expected.Type == JTokenType.Float || expected.Type == JTokenType.Boolean)
            {
                double want = expected.Type == JTokenType.Boolean ? ((bool)expected ? 1 : 0) : (double)expected;
                return MeasuredNumber.TryParse(measured, out double have) && MeasuredNumber.Same(have, want);
            }
            return string.Equals(measured, ToInvariantString(expected), StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Does a parameter value a CAD rule writes agree with what the element holds?
    /// Decided by the rule value's TYPE, the way horizun_write_params_verified writes
    /// it: a number goes in through Set(double) in internal units and is compared with
    /// the STORED double; a boolean is the Yes/No integer 1/0; a string goes in
    /// through Set(string) or SetValueString and is compared, exactly, with the stored
    /// text or the displayed text. The display text alone used to decide, against the
    /// rule value's current-culture ToString, so numbers and booleans never agreed.
    /// </summary>
    public static class CadParameterValues
    {
        public static bool Agrees(JToken wanted, string stored, string display)
        {
            if (wanted == null || wanted.Type == JTokenType.Null) return true;
            switch (wanted.Type)
            {
                case JTokenType.Integer:
                case JTokenType.Float:
                case JTokenType.Boolean:
                    return JsonScalar.MeasuredEquals(stored, wanted);
                default:
                    string text = JsonScalar.ToInvariantString(wanted);
                    return string.Equals(text, stored, StringComparison.Ordinal) ||
                           (display != null && string.Equals(text, display, StringComparison.Ordinal));
            }
        }

        /// <summary>The rule value as findings print it: culture-free, null for a JSON null.</summary>
        public static string ExpectedText(JToken wanted) => JsonScalar.ToInvariantString(wanted);
    }

    /// <summary>
    /// Reading back a JSON file this bridge wrote. JObject.Parse turns ISO 8601 TEXT
    /// into a DateTime; re-printed, it loses trailing zeros (a stored hash no longer
    /// matches) and Value&lt;string&gt;() prints it as "12/31/2026 23:00:00", which
    /// neither sorts nor is an xs:dateTime (audit 2026-10-08). Text stays text here.
    /// </summary>
    public static class JsonText
    {
        public static JObject ParseObject(string text)
        {
            using (var reader = new Newtonsoft.Json.JsonTextReader(new System.IO.StringReader(text ?? ""))
                   { DateParseHandling = Newtonsoft.Json.DateParseHandling.None })
            {
                JObject o = JObject.Load(reader);
                // Same strictness as JObject.Parse: nothing but comments after the object.
                while (reader.Read())
                    if (reader.TokenType != Newtonsoft.Json.JsonToken.Comment)
                        throw new Newtonsoft.Json.JsonReaderException("Additional text found in JSON after the root object.");
                return o;
            }
        }

        /// <summary>The instant an ISO 8601 timestamp names, or null. Text without an offset is read as UTC.</summary>
        public static DateTimeOffset? Instant(string iso)
        {
            if (string.IsNullOrWhiteSpace(iso)) return null;
            return DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture,
                       DateTimeStyles.AssumeUniversal | DateTimeStyles.AllowWhiteSpaces, out DateTimeOffset at)
                ? at : (DateTimeOffset?)null;
        }

        /// <summary>
        /// Orders timestamps by INSTANT (ordinal text sorts "-05:00" offsets and
        /// re-formatted dates wrongly); unparseable text sorts first, then ordinally.
        /// </summary>
        public static readonly System.Collections.Generic.IComparer<string> ByInstant = new InstantComparer();

        private sealed class InstantComparer : System.Collections.Generic.IComparer<string>
        {
            public int Compare(string a, string b)
            {
                DateTimeOffset? x = Instant(a), y = Instant(b);
                if (x.HasValue && y.HasValue)
                {
                    int c = x.Value.UtcDateTime.CompareTo(y.Value.UtcDateTime);
                    if (c != 0) return c;
                }
                else if (x.HasValue != y.HasValue) return x.HasValue ? 1 : -1;
                return string.CompareOrdinal(a, b);
            }
        }
    }
}
