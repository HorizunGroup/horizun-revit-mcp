// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// THE RULES BEHIND horizun_manage_schedules set_appearance / split / unsplit,
// without a Revit in the room.
//
// A schedule's look is a handful of independent switches (grid lines, title,
// headings) plus a cell style per region (title row, heading rows, body rows,
// one column). The API has no "set the font of the title": it has a
// TableCellStyle whose override flags decide WHICH of its characteristics apply,
// and a style written without its flag is accepted and ignored. So the decisions
// that make "I asked for bold and it did nothing" impossible live here:
//
//   * a request is parsed ONCE into a StyleSpec, and every attribute that is
//     present becomes one entry of the list the command both writes and re-reads;
//   * a colour is #RRGGBB or it is refused here, never half-parsed by Revit;
//   * which rows of the header section are the title and which are the column
//     headings is arithmetic (HeaderLayout), proved without opening a document;
//   * Revit has NO API for conditional formatting, calculated cell colours or
//     rules that colour a cell by its value. That is said once, here, with the
//     alternatives, instead of being re-discovered by a retry loop.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    /// <summary>The border of one cell, one entry per side; null = not requested.</summary>
    public sealed class BorderSpec
    {
        public string Top, Bottom, Left, Right;
        public bool Any => Top != null || Bottom != null || Left != null || Right != null;
        public IEnumerable<KeyValuePair<string, string>> Sides()
        {
            if (Top != null) yield return new KeyValuePair<string, string>("top", Top);
            if (Bottom != null) yield return new KeyValuePair<string, string>("bottom", Bottom);
            if (Left != null) yield return new KeyValuePair<string, string>("left", Left);
            if (Right != null) yield return new KeyValuePair<string, string>("right", Right);
        }
    }

    /// <summary>One cell style as requested. Every nullable is "not asked for".</summary>
    public sealed class StyleSpec
    {
        public string Font;
        public double? SizePt;
        public bool? Bold, Italic, Underline;
        public string Color, Fill;       // #RRGGBB, upper-case
        public string HAlign, VAlign;    // canonical names
        public BorderSpec Border = new BorderSpec();

        public bool Any => Font != null || SizePt != null || Bold != null || Italic != null || Underline != null ||
                           Color != null || Fill != null || HAlign != null || VAlign != null || Border.Any;

        /// <summary>The attribute names this style asks for, in a stable order - the checklist the re-read must cover.</summary>
        public List<string> Attributes()
        {
            var names = new List<string>();
            if (Font != null) names.Add("font");
            if (SizePt != null) names.Add("size_pt");
            if (Bold != null) names.Add("bold");
            if (Italic != null) names.Add("italic");
            if (Underline != null) names.Add("underline");
            if (Color != null) names.Add("color");
            if (Fill != null) names.Add("fill");
            if (HAlign != null) names.Add("h_align");
            if (VAlign != null) names.Add("v_align");
            foreach (KeyValuePair<string, string> side in Border.Sides()) names.Add("border_" + side.Key);
            return names;
        }
    }

    public static class ScheduleAppearanceRules
    {
        public const string OpSetAppearance = "set_appearance";
        public const string OpSplit = "split";
        public const string OpUnsplit = "unsplit";

        public const string ScopeTitle = "title";
        public const string ScopeHeaders = "headers";
        public const string ScopeBody = "body";
        public const string ScopeField = "field";

        public static readonly IReadOnlyList<string> Scopes = new[] { ScopeTitle, ScopeHeaders, ScopeBody, ScopeField };

        public static readonly IReadOnlyList<string> StyleKeys = new[]
        {
            "font", "size_pt", "bold", "italic", "underline", "color", "fill", "h_align", "v_align", "border"
        };

        /// <summary>The sentence a caller reaching for conditional formatting gets: a limit of Revit, not of an argument.</summary>
        public const string ConditionalFormatRefusal =
            "Revit has no API for conditional formatting of schedule cells (colour by value, rules, icons): " +
            "neither this tool nor horizun_execute_python can create it. What does work: a filter in the schedule " +
            "(horizun_manage_schedules set_filters) to split a table into one per state, a per-column style " +
            "(set_appearance scope=field) for a fixed look, and horizun_manage_views color_by_value for colour by " +
            "value in a VIEW. Conditional formatting already present in a schedule stays untouched.";

        public const int MaxSegments = 50;

        // ---- colours ---------------------------------------------------------------

        /// <summary>#RRGGBB (the # optional) to its three bytes; false for anything else.</summary>
        public static bool TryParseColor(string text, out byte r, out byte g, out byte b)
        {
            r = g = b = 0;
            if (string.IsNullOrWhiteSpace(text)) return false;
            string h = text.Trim().TrimStart('#');
            if (h.Length != 6) return false;
            foreach (char c in h)
                if (!Uri.IsHexDigit(c)) return false;
            r = byte.Parse(h.Substring(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            g = byte.Parse(h.Substring(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            b = byte.Parse(h.Substring(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            return true;
        }

        public static string Hex(byte r, byte g, byte b)
            => "#" + r.ToString("X2", CultureInfo.InvariantCulture) + g.ToString("X2", CultureInfo.InvariantCulture) +
               b.ToString("X2", CultureInfo.InvariantCulture);

        public static string CanonicalHAlign(string text)
        {
            switch ((text ?? "").Trim().ToLowerInvariant())
            {
                case "left": return "left";
                case "center": case "centre": return "center";
                case "right": return "right";
                default: return null;
            }
        }

        public static string CanonicalVAlign(string text)
        {
            switch ((text ?? "").Trim().ToLowerInvariant())
            {
                case "top": return "top";
                case "middle": case "center": return "middle";
                case "bottom": return "bottom";
                default: return null;
            }
        }

        // ---- the style object ------------------------------------------------------

        /// <summary>
        /// Parse a request's style object. Returns the spec, or null with the reason. An empty
        /// object is an error - a call that changes nothing is a call that did not understand.
        /// </summary>
        public static StyleSpec ParseStyle(JObject style, out string error)
        {
            error = null;
            if (style == null) { error = "style must be an object."; return null; }
            foreach (JProperty p in style.Properties())
                if (!StyleKeys.Contains(p.Name))
                { error = "style." + p.Name + " is not a style attribute. Known: " + string.Join(", ", StyleKeys) + "."; return null; }

            var spec = new StyleSpec();
            if (style["font"] != null)
            {
                string font = style.Value<string>("font");
                if (string.IsNullOrWhiteSpace(font) || font.Length > 100)
                { error = "style.font must be a font name of 1..100 characters."; return null; }
                spec.Font = font.Trim();
            }
            if (style["size_pt"] != null)
            {
                double? size = style.Value<double?>("size_pt");
                if (size == null || double.IsNaN(size.Value) || double.IsInfinity(size.Value) || size.Value < 1 || size.Value > 400)
                { error = "style.size_pt must be a text size in points between 1 and 400."; return null; }
                spec.SizePt = size.Value;
            }
            if (!ReadBool(style, "bold", out spec.Bold, out error)) return null;
            if (!ReadBool(style, "italic", out spec.Italic, out error)) return null;
            if (!ReadBool(style, "underline", out spec.Underline, out error)) return null;
            if (!ReadColor(style, "color", out spec.Color, out error)) return null;
            if (!ReadColor(style, "fill", out spec.Fill, out error)) return null;
            if (style["h_align"] != null)
            {
                spec.HAlign = CanonicalHAlign(style.Value<string>("h_align"));
                if (spec.HAlign == null) { error = "style.h_align must be left, center or right."; return null; }
            }
            if (style["v_align"] != null)
            {
                spec.VAlign = CanonicalVAlign(style.Value<string>("v_align"));
                if (spec.VAlign == null) { error = "style.v_align must be top, middle or bottom."; return null; }
            }
            if (style["border"] != null)
            {
                var border = style["border"] as JObject;
                if (border == null) { error = "style.border must be an object with top, bottom, left and/or right."; return null; }
                foreach (JProperty p in border.Properties())
                {
                    string value = p.Value.Type == JTokenType.Null ? null : p.Value.ToString().Trim();
                    if (value == null || value.Length == 0)
                    { error = "style.border." + p.Name + " must name a line style (or none)."; return null; }
                    switch (p.Name)
                    {
                        case "top": spec.Border.Top = value; break;
                        case "bottom": spec.Border.Bottom = value; break;
                        case "left": spec.Border.Left = value; break;
                        case "right": spec.Border.Right = value; break;
                        default:
                            error = "style.border." + p.Name + " is not a side; use top, bottom, left, right."; return null;
                    }
                }
            }
            if (!spec.Any) { error = "style changes nothing: pass at least one of " + string.Join(", ", StyleKeys) + "."; return null; }
            return spec;
        }

        private static bool ReadBool(JObject o, string key, out bool? value, out string error)
        {
            value = null; error = null;
            if (o[key] == null) return true;
            if (o[key].Type != JTokenType.Boolean) { error = "style." + key + " must be true or false."; return false; }
            value = o.Value<bool>(key);
            return true;
        }

        private static bool ReadColor(JObject o, string key, out string hex, out string error)
        {
            hex = null; error = null;
            if (o[key] == null) return true;
            byte r, g, b;
            if (!TryParseColor(o.Value<string>(key), out r, out g, out b))
            { error = "style." + key + " must be #RRGGBB."; return false; }
            hex = Hex(r, g, b);
            return true;
        }

        // ---- the action ------------------------------------------------------------

        /// <summary>
        /// Shape of a set_appearance action, without a document: scope, style, the two
        /// definition switches. Returns null when well formed.
        /// </summary>
        public static string ValidateAppearance(JObject a, out StyleSpec spec)
        {
            spec = null;
            if (a["conditional_format"] != null || a["conditional_formatting"] != null) return ConditionalFormatRefusal;

            bool hasStyle = a["style"] != null, hasText = a["text_type"] != null;
            bool hasGrid = a["show_grid"] != null, hasTitle = a["show_title"] != null;
            if (!hasStyle && !hasText && !hasGrid && !hasTitle)
                return "set_appearance changes nothing: pass text_type and/or style (with scope), show_grid and/or show_title.";
            if (hasGrid && a["show_grid"].Type != JTokenType.Boolean) return "show_grid must be true or false.";
            if (hasTitle && a["show_title"].Type != JTokenType.Boolean) return "show_title must be true or false.";

            string scope = a.Value<string>("scope");
            if (hasStyle || hasText)
            {
                if (scope == null)
                    return "set_appearance with style or text_type needs scope: " + string.Join(", ", Scopes) + ".";
                if (!Scopes.Contains(scope))
                    return "scope '" + scope + "' is not one of: " + string.Join(", ", Scopes) + ".";
                if (hasText)
                {
                    if (scope == ScopeField)
                        return "text_type is the text style of a REGION (title, headers or body); a column takes a style (font, size, colour...).";
                    if (a["text_type"].Type != JTokenType.String && a["text_type"].Type != JTokenType.Integer)
                        return "text_type must be the name or the id of a text type.";
                }
                if (hasStyle)
                {
                    string error;
                    spec = ParseStyle(a["style"] as JObject, out error);
                    if (spec == null) return error;
                }
                if (scope == ScopeField && a["parameter_id"] == null && a["field_index"] == null && a["name"] == null)
                    return "scope=field styles ONE column: name it with parameter_id, name or field_index.";
            }
            else if (scope != null)
                return "scope only means something with style or text_type; pass one of them or drop scope.";
            return null;
        }

        // ---- the header section ------------------------------------------------------

        /// <summary>
        /// Which rows of the HEADER section are the title and which are the column headings.
        /// Row 0 is the title when the title is shown; every row after it (or from row 0
        /// when there is no title) is a heading row. A schedule with headings hidden has
        /// no heading rows even if the section still reports one.
        /// </summary>
        public static void HeaderLayout(int headerRows, bool showTitle, bool showHeaders,
                                        out int[] titleRows, out int[] headingRows)
        {
            if (headerRows < 0) headerRows = 0;
            int first = showTitle && headerRows > 0 ? 1 : 0;
            titleRows = showTitle && headerRows > 0 ? new[] { 0 } : new int[0];
            headingRows = showHeaders ? Enumerable.Range(first, Math.Max(0, headerRows - first)).ToArray() : new int[0];
        }

        // ---- split / unsplit -----------------------------------------------------------

        /// <summary>
        /// split takes ONE of parts (equal segments) or heights_mm (the height limit of every
        /// segment except the last; Revit decides the last from the whole table).
        /// </summary>
        /// <summary>
        /// Split(IList&lt;double&gt;) takes the BODY height of each segment, while GetSegmentHeight returns the
        /// schedule header PLUS that body (RevitAPI.xml). So the read-back is right when every segment reads its
        /// asked body plus one and the same header height (>= 0), within the tolerance. Review R3 (5.2): comparing
        /// the raw height made every schedule with a title or headers fail and roll back.
        /// </summary>
        public static bool SegmentHeightsMatch(IList<double> gotMm, IList<double> askedMm, double toleranceMm,
            out double headerMm, out string why)
        {
            headerMm = 0; why = null;
            if (gotMm == null || askedMm == null || gotMm.Count < askedMm.Count)
            { why = "segment heights could not be read for every asked segment"; return false; }
            if (askedMm.Count == 0) return true;
            headerMm = gotMm[0] - askedMm[0];
            if (headerMm < -toleranceMm)
            { why = "segment 0 reads " + Mm(gotMm[0]) + " mm, less than the " + Mm(askedMm[0]) + " mm body asked for"; return false; }
            for (int i = 1; i < askedMm.Count; i++)
            {
                double header = gotMm[i] - askedMm[i];
                if (Math.Abs(header - headerMm) > toleranceMm)
                {
                    why = "segment " + i + " reads " + Mm(gotMm[i]) + " mm: body " + Mm(gotMm[i] - headerMm) + " mm with the " +
                          Mm(headerMm) + " mm header of segment 0, asked for " + Mm(askedMm[i]) + " mm";
                    return false;
                }
            }
            if (headerMm < 0) headerMm = 0;
            return true;
        }

        private static string Mm(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

        public static string ValidateSplit(JObject a, out int segmentCount, out List<double> heightsMm)
        {
            segmentCount = 0; heightsMm = null;
            bool hasParts = a["parts"] != null, hasHeights = a["heights_mm"] != null;
            if (hasParts == hasHeights) return "split takes parts (equal segments) OR heights_mm (the height limit of each segment except the last), not both and not neither.";
            if (hasParts)
            {
                int? parts = a["parts"].Type == JTokenType.Integer ? a.Value<int?>("parts") : null;
                if (parts == null || parts.Value < 2 || parts.Value > MaxSegments)
                    return "parts must be an integer from 2 to " + MaxSegments + ".";
                segmentCount = parts.Value;
                return null;
            }
            var heights = a["heights_mm"] as JArray;
            if (heights == null || heights.Count < 1 || heights.Count > MaxSegments - 1)
                return "heights_mm must list 1.." + (MaxSegments - 1) + " heights in millimetres (one per segment except the last).";
            heightsMm = new List<double>();
            foreach (JToken t in heights)
            {
                if (t.Type != JTokenType.Integer && t.Type != JTokenType.Float) return "heights_mm entries must be numbers.";
                double h = t.Value<double>();
                if (double.IsNaN(h) || double.IsInfinity(h) || h <= 0 || h > 100000)
                    return "every heights_mm entry must be greater than zero (and at most 100000).";
                heightsMm.Add(h);
            }
            segmentCount = heightsMm.Count + 1;
            return null;
        }

        // ---- canonical appearance (for the diff and the token) -----------------------

        /// <summary>The lines of the "appearance:" section of a schedule's canonical definition.</summary>
        public static List<string> CanonicalLines(bool showGrid, bool showTitle, int segments, IEnumerable<string> styles)
        {
            var lines = new List<string>
            {
                "grid=" + (showGrid ? "true" : "false"),
                "title=" + (showTitle ? "true" : "false"),
                "segments=" + segments.ToString(CultureInfo.InvariantCulture)
            };
            if (styles != null) lines.AddRange(styles);
            return lines;
        }
    }
}
