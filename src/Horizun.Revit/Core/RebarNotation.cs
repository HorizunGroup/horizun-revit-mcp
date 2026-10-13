// -----------------------------------------------------------------------------
// Horizun Core - original Horizun code. Revit-free.
//
// THE BAR NOTATION OF A DETAIL SCHEDULE, READ AS A PURE FUNCTION (refuerzo desde
// cartilla, F3).
//
//   4#5                       four number-5 bars
//   4#5 L=6.20                ... 6.20 m long
//   2#4+1#5                   two groups in one row
//   4Ø16                      four 16 mm bars
//   E#3 c/0.10                a stirrup, one spacing
//   E#3 1@0.05 10@0.10 R@0.20 a stirrup by zones; R = the rest of the run
//
// Lengths and spacings are metres in the schedule and millimetres here. A decimal
// comma (es-CO) is read the way MeasuredNumber reads it: "6,20" is six point two,
// "1,234" is refused because it reads equally as a thousands group. What this does
// not recognise is REFUSED with a code: a schedule cell read wrong is steel placed
// wrong, so nothing is guessed - no implied units, no "probably meant".
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public static class RebarNotation
    {
        public const string KindLongitudinal = "longitudinal", KindStirrup = "stirrup";
        public const string SystemBarNumber = "bar_number", SystemDiameterMm = "diameter_mm";

        public const string CodeEmpty = "notation_empty";
        public const string CodeUnrecognized = "notation_unrecognized";
        public const string CodeBadNumber = "notation_bad_number";
        public const string CodeZeroCount = "notation_zero_count";
        public const string CodeNotPositive = "notation_not_positive";
        public const string CodeMixedSpacing = "notation_mixed_spacing";
        public const string CodeRestNotLast = "notation_rest_not_last";
        public const string CodeNoSpacing = "notation_no_spacing";
        public const string CodeBarOutOfRange = "notation_bar_out_of_range";
        public const string CodeMixedSystems = "notation_mixed_systems";

        /// <summary>Bar numbers #2..#18 (ASTM / NTC); diameters 6..50 mm. Outside: refused, not clamped.</summary>
        public const int MinBarNumber = 2, MaxBarNumber = 18, MinDiameterMm = 6, MaxDiameterMm = 50;

        public sealed class Bar
        {
            public int Count;
            public string System;
            public int Size;
        }

        public sealed class Zone
        {
            /// <summary>Null for the rest zone.</summary>
            public int? Count;
            public bool Rest;
            public double SpacingMm;
        }

        public sealed class Result
        {
            public string Source;
            public bool Ok;
            public string Code, Why;
            public string Kind;
            public List<Bar> Bars = new List<Bar>();
            public double? LengthMm;
            public double? SpacingMm;
            public List<Zone> Zones = new List<Zone>();

            public JObject ToJson()
            {
                var o = new JObject { ["source"] = Source, ["ok"] = Ok };
                if (!Ok) { o["code"] = Code; o["why"] = Why; return o; }
                o["kind"] = Kind;
                o["bars"] = new JArray(Bars.Select(b => new JObject { ["count"] = b.Count, ["system"] = b.System, ["size"] = b.Size }));
                o["length_mm"] = LengthMm.HasValue ? (JToken)LengthMm.Value : JValue.CreateNull();
                o["spacing_mm"] = SpacingMm.HasValue ? (JToken)SpacingMm.Value : JValue.CreateNull();
                o["zones"] = new JArray(Zones.Select(z => new JObject
                {
                    ["count"] = z.Count.HasValue ? (JToken)z.Count.Value : JValue.CreateNull(),
                    ["rest"] = z.Rest, ["spacing_mm"] = z.SpacingMm
                }));
                return o;
            }
        }

        private static readonly Regex Group = new Regex(@"^(\d+)(#|Ø)(\d+)$", RegexOptions.CultureInvariant);
        private static readonly Regex StirrupHead = new Regex(@"^[Ee](#|Ø)(\d+)$", RegexOptions.CultureInvariant);
        private static readonly Regex ZoneToken = new Regex(@"^(\d+|[Rr])@(.+)$", RegexOptions.CultureInvariant);

        // ---- L10: many lines at once, for horizun_query_structure mode=parse_notation ----

        public const string CodeLinesNotUsable = "notation_lines_not_usable";

        /// <summary>The same ceiling as a reconcile schedule: one call reads at most this many lines.</summary>
        public const int MaxLines = 5000;

        /// <summary>
        /// Read every line, in order. A refused LINE carries its code and never stops the
        /// others; a request that is not a list of 1..MaxLines strings is refused WHOLE
        /// (null, with code and error) - a number or an object where a schedule cell was
        /// expected is not a cell this reads.
        /// </summary>
        public static JObject ParseLines(JToken lines, out string code, out string error)
        {
            code = null; error = null;
            var a = lines as JArray;
            if (a == null || a.Count == 0 || a.Count > MaxLines)
            {
                code = CodeLinesNotUsable;
                error = "lines must be a list of 1 to " + MaxLines + " schedule cells, each a string (e.g. \"4#5 L=6.20\"). Nothing was read.";
                return null;
            }
            for (int i = 0; i < a.Count; i++)
            {
                if (a[i] == null || a[i].Type != JTokenType.String)
                {
                    code = CodeLinesNotUsable;
                    error = "lines[" + i + "] is " + (a[i] == null ? "missing" : a[i].Type.ToString().ToLowerInvariant()) +
                            ", not a string: a schedule cell is read as written. Nothing was read.";
                    return null;
                }
            }
            var rows = new JArray();
            var byCode = new SortedDictionary<string, int>(StringComparer.Ordinal);
            int read = 0;
            for (int i = 0; i < a.Count; i++)
            {
                Result r = Parse((string)a[i]);
                JObject row = r.ToJson();
                row.AddFirst(new JProperty("index", i));
                rows.Add(row);
                if (r.Ok) read++;
                else byCode[r.Code] = (byCode.TryGetValue(r.Code, out int n) ? n : 0) + 1;
            }
            var refusals = new JObject();
            foreach (KeyValuePair<string, int> kv in byCode) refusals[kv.Key] = kv.Value;
            return new JObject
            {
                ["rows"] = rows,
                ["summary"] = new JObject
                {
                    ["lines"] = a.Count, ["read"] = read, ["refused"] = a.Count - read, ["refusals_by_code"] = refusals
                },
                ["units"] = new JObject
                {
                    ["length_mm"] = "L= in the schedule is metres; here millimetres.",
                    ["spacing_mm"] = "c/ and n@ in the schedule are metres; here millimetres.",
                    ["size"] = "a bar NUMBER when system is bar_number (#5), a diameter in mm when it is diameter_mm (Ø16). " +
                               "Which bar type in the model a number names is not decided here."
                },
                ["refusal_codes"] = new JArray(CodeEmpty, CodeUnrecognized, CodeBadNumber, CodeZeroCount, CodeNotPositive,
                                               CodeMixedSpacing, CodeRestNotLast, CodeNoSpacing, CodeBarOutOfRange, CodeMixedSystems),
                ["writes_nothing"] = true
            };
        }

        public static Result Parse(string text)
        {
            var r = new Result { Source = text };
            if (string.IsNullOrWhiteSpace(text)) return Fail(r, CodeEmpty, "the notation is empty.");
            string s = Normalize(text);
            List<string> tokens = s.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).ToList();

            Match head = StirrupHead.Match(tokens[0]);
            if (head.Success) return Stirrup(r, head, tokens.Skip(1).ToList());
            return Longitudinal(r, tokens);
        }

        // ---- longitudinal: GROUP(+GROUP)* [L=metres] -------------------------------
        private static Result Longitudinal(Result r, List<string> tokens)
        {
            r.Kind = KindLongitudinal;
            if (tokens.Count > 2) return Fail(r, CodeUnrecognized, "'" + r.Source + "' has more parts than bar groups and one L=.");
            string[] groups = tokens[0].Split('+');
            foreach (string g in groups)
            {
                Match m = Group.Match(g);
                if (!m.Success)
                    return Fail(r, CodeUnrecognized, "'" + g + "' is not a bar group (count, then #number or Ø millimetres, e.g. 4#5 or 4Ø16).");
                Result bad = AddBar(r, m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value, false);
                if (bad != null) return bad;
            }
            if (r.Bars.Select(b => b.System).Distinct().Count() > 1)
                return Fail(r, CodeMixedSystems, "'" + r.Source + "' mixes bar numbers and millimetre diameters in one row; state them in one system.");
            if (tokens.Count == 2)
            {
                if (!tokens[1].StartsWith("L=", StringComparison.OrdinalIgnoreCase) || tokens[1].Length == 2)
                    return Fail(r, CodeUnrecognized, "'" + tokens[1] + "' is not a length (L=metres).");
                double mm;
                Result bad = Metres(r, tokens[1].Substring(2), "the length", out mm);
                if (bad != null) return bad;
                r.LengthMm = mm;
            }
            r.Ok = true;
            return r;
        }

        // ---- stirrup: E#n then c/metres, or zones n@metres ... [R@metres] -------------
        private static Result Stirrup(Result r, Match head, List<string> rest)
        {
            r.Kind = KindStirrup;
            Result bad = AddBar(r, "1", head.Groups[1].Value, head.Groups[2].Value, true);
            if (bad != null) return bad;
            if (rest.Count == 0) return Fail(r, CodeNoSpacing, "the stirrup '" + r.Source + "' states no spacing (c/metres, or zones n@metres).");

            bool single = rest.Any(t => t.StartsWith("c/", StringComparison.OrdinalIgnoreCase));
            bool zones = rest.Any(t => ZoneToken.IsMatch(t));
            if (single && zones)
                return Fail(r, CodeMixedSpacing, "'" + r.Source + "' states a single spacing (c/) AND zones; it must be one or the other.");
            if (single)
            {
                if (rest.Count != 1 || rest[0].Length <= 2)
                    return Fail(r, CodeUnrecognized, "'" + string.Join(" ", rest) + "' is not one spacing (c/metres).");
                double mm;
                bad = Metres(r, rest[0].Substring(2), "the spacing", out mm);
                if (bad != null) return bad;
                r.SpacingMm = mm;
                r.Ok = true;
                return r;
            }
            foreach (string t in rest)
            {
                Match z = ZoneToken.Match(t);
                if (!z.Success) return Fail(r, CodeUnrecognized, "'" + t + "' is not a stirrup zone (n@metres, or R@metres for the rest).");
                if (r.Zones.Any(x => x.Rest))
                    return Fail(r, CodeRestNotLast, "'" + r.Source + "': the rest zone (R@) must be the last zone, and only one.");
                double mm;
                bad = Metres(r, z.Groups[2].Value, "a zone spacing", out mm);
                if (bad != null) return bad;
                bool isRest = z.Groups[1].Value.Equals("R", StringComparison.OrdinalIgnoreCase);
                int? count = null;
                if (!isRest)
                {
                    // TryParse, not Parse (backlog #56): a count past an int threw OverflowException
                    // out of the parser instead of refusing with a code.
                    int n;
                    if (!int.TryParse(z.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out n))
                        return Fail(r, CodeBadNumber, "'" + t + "' places more stirrups than this reads as a count.");
                    if (n == 0) return Fail(r, CodeZeroCount, "'" + t + "' places zero stirrups.");
                    count = n;
                }
                r.Zones.Add(new Zone { Count = count, Rest = isRest, SpacingMm = mm });
            }
            r.Ok = true;
            return r;
        }

        private static Result AddBar(Result r, string countText, string mark, string sizeText, bool stirrup)
        {
            int count, size;
            if (!int.TryParse(countText, NumberStyles.None, CultureInfo.InvariantCulture, out count) ||
                !int.TryParse(sizeText, NumberStyles.None, CultureInfo.InvariantCulture, out size))
                return Fail(r, CodeUnrecognized, "'" + countText + mark + sizeText + "' is not a count and a bar size.");
            if (count == 0) return Fail(r, CodeZeroCount, "'" + countText + mark + sizeText + "' places zero bars.");
            bool number = mark == "#";
            if (number ? size < MinBarNumber || size > MaxBarNumber : size < MinDiameterMm || size > MaxDiameterMm)
                return Fail(r, CodeBarOutOfRange, (number ? "#" + size + " is outside #" + MinBarNumber + "..#" + MaxBarNumber
                                                          : "Ø" + size + " mm is outside " + MinDiameterMm + ".." + MaxDiameterMm + " mm") +
                                                  "; refused rather than read as some other bar.");
            r.Bars.Add(new Bar { Count = count, System = number ? SystemBarNumber : SystemDiameterMm, Size = size });
            return null;
        }

        private static Result Metres(Result r, string text, string what, out double mm)
        {
            mm = 0;
            double m;
            if (!MeasuredNumber.TryParse(text, out m))
                return Fail(r, CodeBadNumber, what + " '" + text + "' is not a number in metres this reads without guessing " +
                                              "(a decimal point, or a single unambiguous decimal comma).");
            if (m <= 0) return Fail(r, CodeNotPositive, what + " '" + text + "' must be greater than zero.");
            mm = Math.Round(m * 1000.0, 6);
            return null;
        }

        /// <summary>One spelling: Ø for the diameter signs, no spaces around + = @ # Ø /, single spaces.</summary>
        private static string Normalize(string text)
        {
            string s = text.Trim().Replace('ø', 'Ø').Replace('∅', 'Ø');
            s = Regex.Replace(s, @"\s*([+=@#Ø/])\s*", "$1", RegexOptions.CultureInvariant);
            return Regex.Replace(s, @"\s+", " ", RegexOptions.CultureInvariant);
        }

        private static Result Fail(Result r, string code, string why)
        {
            r.Ok = false; r.Code = code; r.Why = why;
            r.Bars.Clear(); r.Zones.Clear(); r.LengthMm = null; r.SpacingMm = null;
            return r;
        }
    }
}
