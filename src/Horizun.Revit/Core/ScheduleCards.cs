// -----------------------------------------------------------------------------
// Horizun Core - original Horizun code. Revit-free, net48-safe.
//
// READING SCHEDULE CARDS (acero 3.0, L10): the primitives of a vector sheet -
// line segments, polylines and positioned texts, in drawing millimetres with y up
// - read into rows, the rows into bar notation through RebarNotation (F3), and
// the beam cards into a proposed horizun.structural-requirements/1 set keyed to
// hosts by mark.
//
// The same code reads a PDF (the server extracts the primitives with PdfPig) and
// a DWG (CAD IR entities that carry text: the accoreconsole reading). It never looks at a file.
//
// Three kinds of content:
//   * beam cards - a framed card with a big title, axis labels above the frame,
//     bars as lines with a callout just above ("2#6 L=6.40"), 90-degree hook ticks
//     at their ends, stirrup zones as dimension segments with callouts ("60e#3" over
//     "c/.30", "11@.08", "14#3@.07"), "B=25 H=40", the stirrup total ("SON 36") and
//     the member count ("Son 2 Vigas");
//   * wall cards - vertical bars as polylines beside rotated callouts, level lines
//     under "+2.50" texts, horizontal bars as "2#3" over "c/.20";
//   * bending tables - rows under a header whose column names the CALLER declares.
//
// NOTHING IS GUESSED. The scale and the tolerances are arguments, never inferred.
// What cannot be read with certainty is refused with a code and the reason: a
// callout with no line under it, two lines equally close, a line two callouts
// claim, a note RebarNotation refuses, two hook ticks at one end. Every
// self-check is a named result (pass, fail, not_run) - a sum that does not add up
// is reported, never adjusted.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public static class ScheduleCards
    {
        public const string SchemaName = "horizun.schedule-reading/1";

        // ---- whole-request refusals --------------------------------------------------
        public const string CodeScaleNotDeclared = "scale_not_declared";
        public const string CodeTolerancesNotDeclared = "tolerances_not_declared";
        public const string CodeOptionsNotUsable = "options_not_usable";
        public const string CodePrimitivesNotUsable = "primitives_not_usable";
        public const string CodeCadTextAnchorNotDeclared = "cad_text_anchor_not_declared";

        // ---- per-sheet, per-card, per-row refusals -----------------------------------
        public const string CodeSheetHasNoText = "sheet_has_no_text";
        public const string CodeTextFormattingNotRead = "text_formatting_not_read";
        public const string CodeTextHeightUnknown = "text_height_unknown";
        public const string CodeTextPlacementNotRead = "text_placement_not_read";
        public const string CodeFrameNotFound = "card_frame_not_found";
        public const string CodeFrameShared = "card_frame_shared";
        public const string CodeCardKindAmbiguous = "card_kind_ambiguous";
        public const string CodeCardHasNoCallouts = "card_has_no_callouts";
        public const string CodeCalloutWithoutLine = "callout_without_line";
        public const string CodeTwoLinesEquallyClose = "callout_two_lines_equally_close";
        public const string CodeLineClaimedTwice = "line_claimed_by_two_callouts";
        public const string CodeNotationRefused = "callout_notation_refused";
        public const string CodeHookAmbiguous = "hook_ambiguous";
        public const string CodeZoneSpacingMissing = "zone_spacing_missing";
        public const string CodeZoneSpacingAmbiguous = "zone_spacing_ambiguous";
        public const string CodeZoneBarNotStated = "zone_bar_not_stated";
        public const string CodeZoneGroupNotPlaced = "zone_group_not_placed";
        public const string CodeCalloutNotRecognised = "callout_not_recognised";
        public const string CodeBarGroupCountDiffers = "bar_group_count_differs";
        public const string CodeCalloutOutsideCards = "callout_outside_cards";
        public const string CodeLevelWithoutLine = "level_without_line";
        public const string CodeHorizontalSpacingMissing = "horizontal_bar_spacing_missing";
        public const string CodeHorizontalSpacingAmbiguous = "horizontal_bar_spacing_ambiguous";
        public const string CodeTableHeaderIncomplete = "table_header_incomplete";
        public const string CodeTableHeaderAmbiguous = "table_header_ambiguous";
        public const string CodeTableCellTwoTexts = "table_cell_has_two_texts";
        public const string CodeTableCellMissing = "table_cell_missing";
        public const string CodeTableCellNotNumber = "table_cell_not_a_number";
        public const string CodeTableBarUnrecognized = "table_bar_unrecognized";

        // ---- why a card is not in the proposed set ------------------------------------
        public const string NpBarTypeNotDeclared = "bar_type_not_declared";
        public const string NpHookTypeNotDeclared = "hook_type_not_declared";
        public const string NpHookShapeNotRead = "hook_shape_not_read";
        public const string NpFaceNotDeterminable = "bar_face_not_determinable";
        public const string NpExtentNotExpressible = "extent_not_expressible";
        public const string NpAxesMissing = "axes_missing";
        public const string NpAxisPositionsUncertain = "axis_positions_uncertain";
        public const string NpHookEndNotMappable = "hook_end_not_mappable";
        public const string NpZonesOrientationUnknown = "zones_orientation_unknown";
        public const string NpMixedContinuousBars = "face_has_mixed_continuous_bars";
        public const string NpStirrupBarsDiffer = "stirrup_bars_differ";
        public const string NpZonesAcrossSupports = "zones_across_supports";
        public const string NpFaceBarsOverlap = "face_bars_overlap";
        public const string NpBarEndParallel = "bar_end_continues_parallel";
        public const string NpZonesNotConsecutive = "zones_not_consecutive";
        public const string NpAxisNamesRepeated = "axis_names_repeated";
        public const string NpTitleRepeated = "card_title_repeated";
        public const string NpCardHasRefusals = "card_has_refusals";
        public const string NpCardChecksFailed = "card_checks_failed";
        public const string NpNoBeamCards = "no_beam_cards_read";

        public const string CheckBarLength = "bar_length_matches_callout";
        public const string CheckZoneCount = "zone_count_matches_its_length";
        public const string CheckStirrupTotal = "stirrups_sum_to_stated_total";
        public const string CheckZoneSpan = "zones_fill_axis_span";
        public const string CheckZoneGaps = "zones_fill_span_between_supports";
        public const string CheckCardCount = "cards_match_stated_count";
        public const string CheckLevels = "levels_agree_with_scale";
        public const string CheckTableRow = "table_row_total_matches";
        public const string CheckTableSum = "table_rows_sum_to_total";

        /// <summary>Refusals that mean a ROW (a bar, a zone, a table line) was not read.</summary>
        private static readonly HashSet<string> RowCodes = new HashSet<string>(StringComparer.Ordinal)
        {
            CodeCalloutWithoutLine, CodeTwoLinesEquallyClose, CodeLineClaimedTwice, CodeNotationRefused,
            CodeHookAmbiguous, CodeZoneSpacingMissing, CodeZoneSpacingAmbiguous, CodeZoneBarNotStated, CodeZoneGroupNotPlaced, CodeCalloutNotRecognised, CodeBarGroupCountDiffers, CodeCalloutOutsideCards,
            CodeHorizontalSpacingMissing, CodeHorizontalSpacingAmbiguous, CodeTableCellTwoTexts,
            CodeTableCellMissing, CodeTableCellNotNumber, CodeTableBarUnrecognized
        };

        // ---- the geometry rules, as fractions of the callout's own text height ------
        // Relative to the text, so the same rules read a sheet in points, a sheet in
        // millimetres and a DWG drawn full size, whatever its drawing scale.
        /// <summary>How far below its baseline a callout looks for its line.</summary>
        public const double LineWindowBelow = 3.0;
        /// <summary>Two candidate lines whose gaps differ by no more than this are EQUALLY close.</summary>
        public const double EquallyClose = 0.15;
        /// <summary>A title is at least this many times the median text height of its sheet.</summary>
        public const double TitleHeightRatio = 1.6;
        /// <summary>Axis labels are looked for this many title heights above the frame.</summary>
        public const double AxisBand = 5.0;
        /// <summary>How far below a wall's horizontal-bar label its spacing may be written, in text heights.</summary>
        public const double WallSpacingWindow = 4.0;
        /// <summary>Parallel / perpendicular, as the sine or cosine of the angle (about 1.1 degrees).</summary>
        private const double AngleSlack = 0.02;

        // ==================================================================== primitives

        public sealed class Seg
        {
            public int Id;
            public double X0, Y0, X1, Y1;
            public string Color, Layer, Linetype;
            public double Length => Math.Sqrt((X1 - X0) * (X1 - X0) + (Y1 - Y0) * (Y1 - Y0));
        }

        public sealed class Span
        {
            public int Id;
            public string Text;
            /// <summary>The anchor: the baseline START, or its MIDDLE when AnchorMiddle.</summary>
            public double X, Y;
            public double Dx = 1, Dy = 0;
            public double Height;
            /// <summary>The advance along the baseline; null when the source does not carry it (a DWG text).</summary>
            public double? Width;
            public bool AnchorMiddle;

            /// <summary>True when the middle of the baseline is known.</summary>
            public bool CentreKnown => AnchorMiddle || Width.HasValue;
            public double Cx => AnchorMiddle || !Width.HasValue ? X : X + Dx * Width.Value / 2;
            public double Cy => AnchorMiddle || !Width.HasValue ? Y : Y + Dy * Width.Value / 2;
            /// <summary>The middle of the text box: what decides whether a text is inside a frame.</summary>
            public double Mx => Cx - Dy * Height / 2;
            public double My => Cy + Dx * Height / 2;
            public bool Horizontal => Math.Abs(Dy) <= AngleSlack && Dx > 0;
            public bool Vertical => Math.Abs(Dx) <= AngleSlack;
            public double HalfWidth => Width.HasValue ? Width.Value / 2 : 0;
            /// <summary>Along the baseline from the middle (u) and up from it (v).</summary>
            public void Local(double px, double py, out double u, out double v)
            {
                double ax = px - Cx, ay = py - Cy;
                u = ax * Dx + ay * Dy;
                v = -ax * Dy + ay * Dx;
            }
        }

        public sealed class Sheet
        {
            public string Source;
            public int Page = 1;
            public string TextExtent = "box";
            public List<Seg> Segments = new List<Seg>();
            public List<Span> Texts = new List<Span>();
            /// <summary>Primitive-level refusals (a text not read), each a JSON row.</summary>
            public List<JObject> Refusals = new List<JObject>();
            public Dictionary<string, int> IgnoredByKind = new Dictionary<string, int>(StringComparer.Ordinal);
            public int DuplicateSegments;
        }

        // ======================================================================= options

        public sealed class TableSpec
        {
            public Dictionary<string, string> Columns = new Dictionary<string, string>(StringComparer.Ordinal);
            public string BarPrefix, TotalLabel;
        }

        public sealed class ProposalSpec
        {
            public string SetId;
            public Dictionary<string, string> BarTypeNames = new Dictionary<string, string>(StringComparer.Ordinal);
            public Dictionary<string, string> HookTypeNames = new Dictionary<string, string>(StringComparer.Ordinal);
            public JObject Cover;
            public string StirrupHookTypeName;
            public bool CardLeftIsHostStart;
            /// <summary>
            /// "axes" (the default): bars and stirrups are proposed as stations from the card's own named
            /// axes, which the plan finds in the model. "host": from the host's start face, as before.
            /// </summary>
            public string Extents = "axes";
            /// <summary>face_slots.laps: "inside" / "outside" - two overlapping groups of one bar are a lap, its pieces side by side that way. Null: not declared.</summary>
            public string LapsToward;
            /// <summary>face_slots.additional: "between" - a group within a continuous one, one bar fewer, lies in its gaps.</summary>
            public bool AdditionalBetween;
        }

        public sealed class Options
        {
            public double Scale;
            public double LengthToleranceMm, ZoneSpanToleranceMm;
            /// <summary>Declared: the widest gap a support leaves between two zones (or between an end axis and its zone); null for the axis-span check.</summary>
            public double? SupportGapMm;
            public bool BeamCards = true, WallCards = true, Tables = true;
            public List<Regex> Titles = new List<Regex> { new Regex(@"^[A-Z]{1,3}-?\d{1,4}[A-Z]?$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200)) };
            /// <summary>A card text that states its stirrup bar (declared; a named group "bar"), or null.</summary>
            public Regex StirrupBarLabel;
            public HashSet<string> BarColors, BarLayers, BarLinetypes;
            public TableSpec Table;
            public ProposalSpec Proposal;
        }

        private static readonly string[] TableRoles = { "mark", "bar", "count", "length", "total", "shape", "notation" };
        private static readonly string[] RequiredTableRoles = { "mark", "bar", "count", "length" };

        /// <summary>
        /// The reading arguments, validated. Null with a code when the scale or the
        /// tolerances are not declared, or an argument cannot be used - nothing is read.
        /// Keys this does not own (file_path, pages, primitives ...) are left to the caller.
        /// </summary>
        public static Options ReadOptions(JObject args, out string code, out string error)
        {
            code = null; error = null;
            var o = new Options();
            JToken scale = args?["scale"];
            if (!Number(scale, out double sc) || sc <= 0)
                return Refuse(CodeScaleNotDeclared,
                    "scale is required: the drawing scale's denominator (50 for 1:50), so that a drawn length becomes a real one. " +
                    "It is never inferred from the sheet: a scale read from a title block is one more thing read wrong.",
                    out code, out error);
            o.Scale = sc;

            var tol = args["tolerances"] as JObject;
            if (tol == null || !Number(tol["length_mm"], out double lt) || lt <= 0 || !Number(tol["zone_span_mm"], out double zt) || zt <= 0)
                return Refuse(CodeTolerancesNotDeclared,
                    "tolerances is required: { length_mm, zone_span_mm }, both greater than zero. length_mm judges a drawn bar against " +
                    "its callout and a table row against its total; zone_span_mm judges the stirrup zones against the span between the " +
                    "card's axes. Neither has a default.", out code, out error);
            foreach (JProperty p in tol.Properties())
                if (p.Name != "length_mm" && p.Name != "zone_span_mm" && p.Name != "support_gap_mm")
                    return Refuse(CodeOptionsNotUsable, "tolerances." + p.Name + " is not a tolerance this reads.", out code, out error);
            o.LengthToleranceMm = lt; o.ZoneSpanToleranceMm = zt;
            if (tol["support_gap_mm"] != null)
            {
                if (!Number(tol["support_gap_mm"], out double sg) || sg <= 0)
                    return Refuse(CodeOptionsNotUsable, "tolerances.support_gap_mm must be a number greater than zero: the widest gap a support leaves " +
                                  "between two stirrup zones, or between an end axis and the zone beside it.", out code, out error);
                o.SupportGapMm = sg;
            }

            if (args["kinds"] != null)
            {
                var kinds = args["kinds"] as JArray;
                if (kinds == null || kinds.Count == 0 || kinds.Any(k => k.Type != JTokenType.String ||
                        ((string)k != "beam_cards" && (string)k != "wall_cards" && (string)k != "tables")))
                    return Refuse(CodeOptionsNotUsable, "kinds must be a non-empty list of beam_cards, wall_cards, tables.", out code, out error);
                var set = new HashSet<string>(kinds.Select(k => (string)k));
                o.BeamCards = set.Contains("beam_cards"); o.WallCards = set.Contains("wall_cards"); o.Tables = set.Contains("tables");
            }

            if (args["title_pattern"] != null)
            {
                // One pattern, or a list: a drawing set may title its cards in more than one way
                // (measured: wall cards "W-3" and their detail boxes "W-3 <level>-<level>").
                JToken tp = args["title_pattern"];
                List<JToken> pats = tp is JArray ta ? ta.ToList() : new List<JToken> { tp };
                var titles = new List<Regex>();
                foreach (JToken one in pats)
                {
                    string pat = one.Type == JTokenType.String ? (string)one : null;
                    Regex rx = null;
                    try { if (!string.IsNullOrEmpty(pat)) rx = new Regex(pat, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200)); }
                    catch (ArgumentException ex) { error = ex.Message; }
                    if (rx == null)
                        return Refuse(CodeOptionsNotUsable, "title_pattern must be a regular expression, or a non-empty list of them, that matches a card's title" +
                                      (error != null ? " (" + error + ")" : "") + ".", out code, out error);
                    titles.Add(rx);
                }
                if (titles.Count == 0)
                    return Refuse(CodeOptionsNotUsable, "title_pattern must be a regular expression, or a non-empty list of them, that matches a card's title.", out code, out error);
                o.Titles = titles;
            }

            if (args["stirrup_bar_label"] != null)
            {
                string pat = args["stirrup_bar_label"].Type == JTokenType.String ? (string)args["stirrup_bar_label"] : null;
                Regex rx = null;
                try { if (!string.IsNullOrEmpty(pat)) rx = new Regex(pat, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200)); }
                catch (ArgumentException ex) { error = ex.Message; }
                if (rx == null || Array.IndexOf(rx.GetGroupNames(), "bar") < 0)
                    return Refuse(CodeOptionsNotUsable, "stirrup_bar_label must be a regular expression that matches the text a card states its stirrup bar in, " +
                                  "with a named group \"bar\" around the bar as written (#3, Ø10)" + (error != null ? " (" + error + ")" : "") + ".", out code, out error);
                o.StirrupBarLabel = rx;
            }

            if (!Strings(args["bar_colors"], "bar_colors", out o.BarColors, out error) ||
                !Strings(args["bar_layers"], "bar_layers", out o.BarLayers, out error) ||
                !Strings(args["bar_linetypes"], "bar_linetypes", out o.BarLinetypes, out error))
                return Refuse(CodeOptionsNotUsable, error, out code, out error);
            if (o.BarColors != null) o.BarColors = new HashSet<string>(o.BarColors.Select(c => c.ToLowerInvariant()), StringComparer.Ordinal);

            if (args["table"] != null)
            {
                var t = args["table"] as JObject;
                var cols = t?["columns"] as JObject;
                if (t == null || cols == null)
                    return Refuse(CodeOptionsNotUsable, "table must be { columns: { mark, bar, count, length, total?, shape?, notation? }, bar_prefix?, total_label? }.", out code, out error);
                foreach (JProperty p in t.Properties())
                    if (p.Name != "columns" && p.Name != "bar_prefix" && p.Name != "total_label")
                        return Refuse(CodeOptionsNotUsable, "table." + p.Name + " is not a table option.", out code, out error);
                var spec = new TableSpec();
                foreach (JProperty p in cols.Properties())
                {
                    if (Array.IndexOf(TableRoles, p.Name) < 0)
                        return Refuse(CodeOptionsNotUsable, "table.columns." + p.Name + " is not a column this reads (" + string.Join(", ", TableRoles) + ").", out code, out error);
                    if (p.Value.Type != JTokenType.String || string.IsNullOrWhiteSpace((string)p.Value))
                        return Refuse(CodeOptionsNotUsable, "table.columns." + p.Name + " must be the header text as written on the sheet.", out code, out error);
                    spec.Columns[p.Name] = Normalize((string)p.Value);
                }
                foreach (string role in RequiredTableRoles)
                    if (!spec.Columns.ContainsKey(role))
                        return Refuse(CodeOptionsNotUsable, "table.columns." + role + " is required: the header text of that column, as the sheet writes it. " +
                                      "No header dictionary is compiled into this bridge.", out code, out error);
                if (spec.Columns.Values.Distinct(StringComparer.OrdinalIgnoreCase).Count() != spec.Columns.Count)
                    return Refuse(CodeOptionsNotUsable, "table.columns names one header for two columns.", out code, out error);
                if (t["bar_prefix"] != null)
                {
                    string bp = t["bar_prefix"].Type == JTokenType.String ? Normalize((string)t["bar_prefix"]) : null;
                    if (bp != "#" && bp != "Ø")
                        return Refuse(CodeOptionsNotUsable, "table.bar_prefix must be \"#\" (bar numbers) or \"Ø\" (diameters in mm).", out code, out error);
                    spec.BarPrefix = bp;
                }
                if (t["total_label"] != null)
                {
                    if (t["total_label"].Type != JTokenType.String || string.IsNullOrWhiteSpace((string)t["total_label"]))
                        return Refuse(CodeOptionsNotUsable, "table.total_label must be the text of the totals row's first cell.", out code, out error);
                    spec.TotalLabel = Normalize((string)t["total_label"]);
                }
                o.Table = spec;
            }

            if (args["requirement_set"] != null)
            {
                var r = args["requirement_set"] as JObject;
                if (r == null) return Refuse(CodeOptionsNotUsable, "requirement_set must be an object.", out code, out error);
                var spec = new ProposalSpec();
                foreach (JProperty p in r.Properties())
                {
                    switch (p.Name)
                    {
                        case "set_id":
                            spec.SetId = p.Value.Type == JTokenType.String ? (string)p.Value : null;
                            if (string.IsNullOrWhiteSpace(spec.SetId)) return Refuse(CodeOptionsNotUsable, "requirement_set.set_id must be a non-empty string.", out code, out error);
                            break;
                        case "bar_type_names":
                        case "hook_type_names":
                            var names = p.Value as JObject;
                            if (names == null || names.Properties().Any(n => n.Value.Type != JTokenType.String || string.IsNullOrWhiteSpace((string)n.Value)))
                                return Refuse(CodeOptionsNotUsable, "requirement_set." + p.Name + " must map each key to the name of a type in the model.", out code, out error);
                            foreach (JProperty n in names.Properties())
                            {
                                string key = Normalize(n.Name).Replace(" ", "");
                                if (p.Name == "hook_type_names" && key != "90" && key != "135" && key != "180")
                                    return Refuse(CodeOptionsNotUsable, "requirement_set.hook_type_names: '" + n.Name + "' - a hook is read by its drawn angle, keyed \"90\", \"135\" or \"180\".", out code, out error);
                                if (p.Name == "bar_type_names" && !Regex.IsMatch(key, @"^[#Ø]\d+$"))
                                    return Refuse(CodeOptionsNotUsable, "requirement_set.bar_type_names: '" + n.Name + "' is not a bar as a card writes it (#5, Ø16).", out code, out error);
                                (p.Name == "bar_type_names" ? spec.BarTypeNames : spec.HookTypeNames)[key] = (string)n.Value;
                            }
                            break;
                        case "cover":
                            spec.Cover = p.Value as JObject;
                            if (spec.Cover == null) return Refuse(CodeOptionsNotUsable, "requirement_set.cover must be a cover object of the requirement-set schema.", out code, out error);
                            break;
                        case "stirrup_hook_type_name":
                            spec.StirrupHookTypeName = p.Value.Type == JTokenType.String ? (string)p.Value : null;
                            if (string.IsNullOrWhiteSpace(spec.StirrupHookTypeName)) return Refuse(CodeOptionsNotUsable, "requirement_set.stirrup_hook_type_name must be a hook type name.", out code, out error);
                            break;
                        case "face_slots":
                            var fs = p.Value as JObject;
                            if (fs == null || !fs.Properties().Any() || fs.Properties().Any(q => q.Name != "laps" && q.Name != "additional"))
                                return Refuse(CodeOptionsNotUsable, "requirement_set.face_slots is { laps?: \"inside\" | \"outside\", additional?: \"between\" }: how the drawing set lays " +
                                              "laps and bars added to continuous ones across a face. Nothing else is declared there.", out code, out error);
                            if (fs["laps"] != null)
                            {
                                spec.LapsToward = fs["laps"].Type == JTokenType.String ? (string)fs["laps"] : null;
                                if (spec.LapsToward != "inside" && spec.LapsToward != "outside")
                                    return Refuse(CodeOptionsNotUsable, "requirement_set.face_slots.laps is \"inside\" or \"outside\": the side a lap's second piece sits on.", out code, out error);
                            }
                            if (fs["additional"] != null)
                            {
                                if (fs["additional"].Type != JTokenType.String || (string)fs["additional"] != "between")
                                    return Refuse(CodeOptionsNotUsable, "requirement_set.face_slots.additional is \"between\": one bar in each gap of the continuous bars.", out code, out error);
                                spec.AdditionalBetween = true;
                            }
                            break;
                        case "extents":
                            spec.Extents = p.Value.Type == JTokenType.String ? (string)p.Value : null;
                            if (spec.Extents != "axes" && spec.Extents != "host")
                                return Refuse(CodeOptionsNotUsable, "requirement_set.extents is \"axes\" (stations from the card's named axes, placed on the model's grids) " +
                                              "or \"host\" (from the host's start face).", out code, out error);
                            break;
                        case "card_left_is_host_start":
                            if (p.Value.Type != JTokenType.Boolean) return Refuse(CodeOptionsNotUsable, "requirement_set.card_left_is_host_start must be true or false.", out code, out error);
                            spec.CardLeftIsHostStart = (bool)p.Value;
                            break;
                        default:
                            return Refuse(CodeOptionsNotUsable, "requirement_set." + p.Name + " is not a proposal option.", out code, out error);
                    }
                }
                if (spec.Extents == "host" && (spec.LapsToward != null || spec.AdditionalBetween))
                    return Refuse(CodeOptionsNotUsable, "requirement_set.face_slots lays a face's groups as layers from the card's axes; extents \"host\" " +
                                  "proposes one layer per face and has nothing to lay them in.", out code, out error);
                o.Proposal = spec;
            }
            return o;
        }

        // ============================================================ primitive sources

        /// <summary>
        /// Generic primitives: { segments: [{a:[x,y], b:[x,y], color?, layer?}], polylines:
        /// [{points:[[x,y]...], closed?, color?, layer?}], texts: [{text, at:[x,y], height,
        /// direction?:[dx,dy], width?}] } in drawing millimetres, y up. Malformed: null.
        /// </summary>
        public static Sheet SheetFromPrimitives(JObject p, out string code, out string error)
        {
            code = null; error = null;
            if (p == null) { Refuse(CodePrimitivesNotUsable, "primitives must be an object { segments, polylines, texts }.", out code, out error); return null; }
            foreach (JProperty prop in p.Properties())
                if (prop.Name != "segments" && prop.Name != "polylines" && prop.Name != "texts" && prop.Name != "units" && prop.Name != "source" && prop.Name != "page")
                { Refuse(CodePrimitivesNotUsable, "primitives." + prop.Name + " is not a primitive list.", out code, out error); return null; }
            if (p["units"] != null && (string)p["units"] != "drawing_mm")
            { Refuse(CodePrimitivesNotUsable, "primitives.units must be \"drawing_mm\" (millimetres on the sheet, y up) or absent.", out code, out error); return null; }
            var s = new Sheet { Source = p["source"]?.Type == JTokenType.String ? (string)p["source"] : "primitives" };
            if (p["page"] != null && p["page"].Type == JTokenType.Integer) s.Page = (int)p["page"];
            var raw = new List<Seg>();
            int i = 0;
            foreach (JToken t in p["segments"] as JArray ?? new JArray())
            {
                if (!Point(t?["a"], out double ax, out double ay) || !Point(t["b"], out double bx, out double by))
                { Refuse(CodePrimitivesNotUsable, "segments[" + i + "] needs a: [x, y] and b: [x, y], finite numbers.", out code, out error); return null; }
                raw.Add(new Seg { X0 = ax, Y0 = ay, X1 = bx, Y1 = by, Color = Color(t["color"]), Layer = Str(t["layer"]) });
                i++;
            }
            i = 0;
            foreach (JToken t in p["polylines"] as JArray ?? new JArray())
            {
                var pts = t?["points"] as JArray;
                var xy = new List<double[]>();
                if (pts == null || pts.Count < 2) { Refuse(CodePrimitivesNotUsable, "polylines[" + i + "].points needs at least two [x, y].", out code, out error); return null; }
                for (int k = 0; k < pts.Count; k++)
                {
                    if (!Point(pts[k], out double x, out double y))
                    { Refuse(CodePrimitivesNotUsable, "polylines[" + i + "].points[" + k + "] is not [x, y].", out code, out error); return null; }
                    xy.Add(new[] { x, y });
                }
                bool closed = t["closed"] != null && t["closed"].Type == JTokenType.Boolean && (bool)t["closed"];
                AddPolyline(raw, xy, closed, Color(t["color"]), Str(t["layer"]));
                i++;
            }
            i = 0;
            foreach (JToken t in p["texts"] as JArray ?? new JArray())
            {
                string text = t?["text"]?.Type == JTokenType.String ? (string)t["text"] : null;
                if (text == null || !Point(t["at"], out double x, out double y) || !Number(t["height"], out double h) || h <= 0)
                { Refuse(CodePrimitivesNotUsable, "texts[" + i + "] needs text, at: [x, y] and height > 0.", out code, out error); return null; }
                double dx = 1, dy = 0;
                if (t["direction"] != null && !Point(t["direction"], out dx, out dy))
                { Refuse(CodePrimitivesNotUsable, "texts[" + i + "].direction is not [dx, dy].", out code, out error); return null; }
                double n = Math.Sqrt(dx * dx + dy * dy);
                if (n < 1e-9) { Refuse(CodePrimitivesNotUsable, "texts[" + i + "].direction has no length.", out code, out error); return null; }
                double? w = null;
                if (t["width"] != null)
                {
                    if (!Number(t["width"], out double ww) || ww < 0) { Refuse(CodePrimitivesNotUsable, "texts[" + i + "].width must be a number >= 0.", out code, out error); return null; }
                    w = ww;
                }
                s.Texts.Add(new Span { Text = text, X = x, Y = y, Dx = dx / n, Dy = dy / n, Height = h, Width = w });
                i++;
            }
            if (s.Texts.Any(t => !t.Width.HasValue)) s.TextExtent = "anchor_start";
            Finish(s, raw);
            return s;
        }

        /// <summary>
        /// CAD IR entities (CadIrEntity.ToJson) that carry TEXT - the accoreconsole reading of a DWG;
        /// horizun_cad_extract over a Revit link has none, Revit's import drops text: line and polyline
        /// points in millimetres, arcs with their start, middle and end, text with one point,
        /// text_height_mm and rotation_radians.
        /// A DWG text carries no extent, so WHERE its point sits on it must be declared:
        /// "start" (the baseline start - AutoCAD's left justification) or "middle".
        /// </summary>
        public static Sheet SheetFromCadEntities(JArray entities, string textAnchor, out string code, out string error)
        {
            code = null; error = null;
            if (entities == null) { Refuse(CodePrimitivesNotUsable, "cad_entities must be a list of CAD IR entities (lines, polylines, arcs, texts in mm).", out code, out error); return null; }
            // A text that carries its own placement (text_type with its justification or attachment)
            // is placed by it; only texts that do not need the declared anchor - and then it is
            // required, never assumed.
            bool untyped = entities.OfType<JObject>().Any(e => Str(e["kind"]) == "text" && e["block_path"] == null && Str(e["text_type"]) == null);
            if (untyped && textAnchor != "start" && textAnchor != "middle")
            {
                Refuse(CodeCadTextAnchorNotDeclared,
                    "cad_text_anchor is required with cad_entities whose texts do not carry text_type: \"start\" when each text's point is " +
                    "the start of its baseline (left-justified text), \"middle\" when it is the middle (centred text). A DWG text has no " +
                    "extent in the reading, so where its point sits on it is declared, not assumed.", out code, out error);
                return null;
            }
            if (textAnchor != null && textAnchor != "start" && textAnchor != "middle")
            {
                Refuse(CodeCadTextAnchorNotDeclared, "cad_text_anchor is \"start\" or \"middle\".", out code, out error);
                return null;
            }
            var s = new Sheet { Source = "cad_entities", TextExtent = !untyped ? "per_entity" : textAnchor == "middle" ? "anchor_middle" : "anchor_start" };
            var raw = new List<Seg>();
            for (int i = 0; i < entities.Count; i++)
            {
                var e = entities[i] as JObject;
                string kind = e?["kind"]?.Type == JTokenType.String ? (string)e["kind"] : null;
                if (kind == null) { Refuse(CodePrimitivesNotUsable, "cad_entities[" + i + "] has no kind.", out code, out error); return null; }
                var pts = new List<double[]>();
                foreach (JToken q in e["points"] as JArray ?? new JArray())
                {
                    if (!Point(q, out double x, out double y)) { Refuse(CodePrimitivesNotUsable, "cad_entities[" + i + "].points holds something that is not [x, y, z].", out code, out error); return null; }
                    pts.Add(new[] { x, y });
                }
                // An entity inside a block definition carries the BLOCK's coordinates, not the sheet's:
                // where it stands depends on the insert(s) that place the block, which this reading does
                // not apply. Measured on real schedule DWGs: the title block, axis bubbles and detail
                // symbols are blocks. Counted and left out, never read at block-local positions.
                if (e["block_path"] is JArray bp && bp.Count > 0) { Count(s.IgnoredByKind, "in_block_definition"); continue; }
                // A true colour reads as a PDF's does (#rrggbb); otherwise the ACI index (aci:n).
                string color = Str(e["color_rgb"])?.ToLowerInvariant() ??
                               (e["color_index"] != null && e["color_index"].Type == JTokenType.Integer ? "aci:" + (int)e["color_index"] : null);
                string linetype = Str(e["linetype"]);
                string layer = Str(e["layer"]);
                if (kind == "line")
                {
                    if (pts.Count != 2) { Refuse(CodePrimitivesNotUsable, "cad_entities[" + i + "] is a line without two points.", out code, out error); return null; }
                    raw.Add(new Seg { X0 = pts[0][0], Y0 = pts[0][1], X1 = pts[1][0], Y1 = pts[1][1], Color = color, Layer = layer, Linetype = linetype });
                }
                else if (kind == "polyline")
                {
                    if (pts.Count < 2) { Refuse(CodePrimitivesNotUsable, "cad_entities[" + i + "] is a polyline with fewer than two points.", out code, out error); return null; }
                    AddPolyline(raw, pts, e["closed"] != null && e["closed"].Type == JTokenType.Boolean && (bool)e["closed"], color, layer, linetype);
                }
                else if (kind == "arc")
                {
                    // A bend drawn as an ARC (measured on real schedule DWGs: every hook's bend). Read as
                    // the two chords start-middle-end, as a PDF carries a plotted bend; an arc whose ends
                    // the entity does not carry is counted and left out.
                    var arc = e["arc"] as JObject;
                    if (arc == null || !Point(arc["start"], out double sx0, out double sy0) || !Point(arc["middle"], out double mx0, out double my0) ||
                        !Point(arc["end"], out double ex0, out double ey0))
                    { Count(s.IgnoredByKind, "arc_without_ends"); continue; }
                    AddPolyline(raw, new List<double[]> { new[] { sx0, sy0 }, new[] { mx0, my0 }, new[] { ex0, ey0 } }, false, color, layer, linetype);
                }
                else if (kind == "text")
                {
                    string text = e["text"]?.Type == JTokenType.String ? (string)e["text"] : null;
                    string id = Str(e["id"]) ?? ("#" + i);
                    if (text == null || pts.Count < 1) { Count(s.IgnoredByKind, "text_without_string"); continue; }
                    if (text.IndexOf('\\') >= 0 || text.IndexOf('{') >= 0)
                    {
                        s.Refusals.Add(Refusal(CodeTextFormattingNotRead, null, s.Page, text, pts[0][0], pts[0][1],
                            "entity " + id + " carries MTEXT formatting codes; they are not stripped on a guess about which part is the text."));
                        continue;
                    }
                    if (!Number(e["text_height_mm"], out double h) || h <= 0)
                    {
                        s.Refusals.Add(Refusal(CodeTextHeightUnknown, null, s.Page, text, pts[0][0], pts[0][1],
                            "entity " + id + " has no text height: titles and callouts are told apart by their height."));
                        continue;
                    }
                    double rot = Number(e["rotation_radians"], out double rr) ? rr : 0;
                    string type = Str(e["text_type"]);
                    if (type == null)
                    {
                        s.Texts.Add(new Span { Text = text, X = pts[0][0], Y = pts[0][1], Dx = Math.Cos(rot), Dy = Math.Sin(rot), Height = h / CapHeight, AnchorMiddle = textAnchor == "middle" });
                        continue;
                    }
                    Span placed = PlaceCadText(e, type, text, pts[0][0], pts[0][1], rot, h, out string why);
                    if (placed == null)
                    {
                        s.Refusals.Add(Refusal(CodeTextPlacementNotRead, null, s.Page, text, pts[0][0], pts[0][1], "entity " + id + ": " + why));
                        continue;
                    }
                    s.Texts.Add(placed);
                }
                else Count(s.IgnoredByKind, kind);
            }
            Finish(s, raw);
            return s;
        }

        // A DWG text's height is the height of its CAPITALS (AutoCAD's text height); a PDF's is the
        // em, and every window here is in em. Measured on the same sheet both ways, capitals against
        // em: 0.72 - so a DWG height is read as height / CapHeight.
        /// <summary>
        /// A DWG text put on its baseline by what the entity says places it (measured on real
        /// schedule DWGs: a third of the texts were MTEXT whose point sat 0.2-0.8 of their height
        /// above the baseline, at their attachment).
        ///   TEXT: the point is the baseline start whatever the justification. The extent is known
        ///   when the alignment point lies on the baseline (vertical justification 0): centred (1)
        ///   at its middle, right (2), aligned (3) and fit (5) at its end. Otherwise the start only.
        ///   MTEXT: the point is the attachment (1..9, top-left to bottom-right) of a box
        ///   text_box_width_mm wide and text_box_height_mm high; the baseline runs one text height
        ///   under the box's top. Without the attachment or the box it is not placed.
        /// </summary>
        private static Span PlaceCadText(JObject e, string type, string text, double x, double y, double rot, double h, out string why)
        {
            why = null;
            double dx = Math.Cos(rot), dy = Math.Sin(rot);
            if (type == "TEXT")
            {
                int jh = e["text_justify_h"]?.Type == JTokenType.Integer ? (int)e["text_justify_h"] : 0;
                int jv = e["text_justify_v"]?.Type == JTokenType.Integer ? (int)e["text_justify_v"] : 0;
                double? width = null;
                if (jv == 0 && (jh == 1 || jh == 2 || jh == 3 || jh == 5) && Point(e["text_alignment_point"], out double ax, out double ay))
                {
                    double along = (ax - x) * dx + (ay - y) * dy, across = -(ax - x) * dy + (ay - y) * dx;
                    if (along <= 0 || Math.Abs(across) > 0.05 * h) { why = "its alignment point is not on its baseline ahead of its start."; return null; }
                    width = jh == 1 ? 2 * along : along;
                }
                return new Span { Text = text, X = x, Y = y, Dx = dx, Dy = dy, Height = h / CapHeight, Width = width };
            }
            if (type == "MTEXT")
            {
                int a = e["text_attachment"]?.Type == JTokenType.Integer ? (int)e["text_attachment"] : 0;
                if (a < 1 || a > 9 || !Number(e["text_box_width_mm"], out double w) || w <= 0 || !Number(e["text_box_height_mm"], out double bh) || bh <= 0)
                { why = "an MTEXT whose attachment or box the entity does not carry: where its baseline is, is not read."; return null; }
                int col = (a - 1) % 3, row = (a - 1) / 3;
                double up = row == 0 ? 0 : row == 1 ? bh / 2 : bh;        // from the point up to the box's top
                double back = col == 0 ? 0 : col == 1 ? w / 2 : w;        // from the point back to the box's left
                double sx = x - dx * back + (-dy) * (up - h), sy = y - dy * back + dx * (up - h);
                return new Span { Text = text, X = sx, Y = sy, Dx = dx, Dy = dy, Height = h / CapHeight, Width = w };
            }
            why = "text_type '" + type + "' is neither TEXT nor MTEXT.";
            return null;
        }

        /// <summary>
        /// A sheet whose primitives another extractor filled in (the server's PDF reader):
        /// the same clean-up as every other source - zero-length and repeated segments
        /// dropped and counted, texts normalised.
        /// </summary>
        public static Sheet FromExtracted(Sheet s)
        {
            List<Seg> raw = s.Segments;
            s.Segments = new List<Seg>();
            Finish(s, raw);
            return s;
        }

        private static void AddPolyline(List<Seg> raw, List<double[]> pts, bool closed, string color, string layer, string linetype = null)
        {
            for (int k = 0; k + 1 < pts.Count; k++)
                raw.Add(new Seg { X0 = pts[k][0], Y0 = pts[k][1], X1 = pts[k + 1][0], Y1 = pts[k + 1][1], Color = color, Layer = layer, Linetype = linetype });
            if (closed && pts.Count > 2)
                raw.Add(new Seg { X0 = pts[pts.Count - 1][0], Y0 = pts[pts.Count - 1][1], X1 = pts[0][0], Y1 = pts[0][1], Color = color, Layer = layer, Linetype = linetype });
        }

        /// <summary>Zero-length segments dropped; a segment drawn twice (either way round) kept once and counted.</summary>
        private static void Finish(Sheet s, List<Seg> raw)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (Seg g in raw)
            {
                if (g.Length < 1e-6) continue;
                bool swap = g.X1 < g.X0 || (g.X1 == g.X0 && g.Y1 < g.Y0);
                double ax = swap ? g.X1 : g.X0, ay = swap ? g.Y1 : g.Y0, bx = swap ? g.X0 : g.X1, by = swap ? g.Y0 : g.Y1;
                string key = Q(ax) + "," + Q(ay) + "," + Q(bx) + "," + Q(by) + "," + g.Color + "," + g.Layer + "," + g.Linetype;
                if (!seen.Add(key)) { s.DuplicateSegments++; continue; }
                g.Id = s.Segments.Count;
                s.Segments.Add(g);
            }
            for (int i = 0; i < s.Texts.Count; i++)
            {
                s.Texts[i].Id = i;
                s.Texts[i].Text = Normalize(s.Texts[i].Text);
            }
            s.Texts.RemoveAll(t => t.Text.Length == 0);
        }

        private static string Q(double v) => Math.Round(v, 2).ToString("R", CultureInfo.InvariantCulture);

        // ===================================================================== patterns

        private static readonly Regex RxBar = new Regex(@"^(\d+)\s*\.?\s*([#Ø])\s*(\d+)\s*[Ll]\s*=\s*([0-9]*[.,]?[0-9]+)\s*(?:\((.*)\))?$", RegexOptions.CultureInvariant);
        // The stirrup type may follow the bar after a space ("12e#3 T-4") or against it ("12e#3T-4",
        // measured on real cards): it starts with a letter, so where the bar size ends is not in doubt.
        private static readonly Regex RxZoneE = new Regex(@"^(\d+)\s*[eE]\s*([#Ø])\s*(\d+)(?:\s+(\S.*)|([A-Za-z]\S*))?$", RegexOptions.CultureInvariant);
        private static readonly Regex RxZoneAt = new Regex(@"^(\d+)\s*\.?\s*@\s*([0-9]*[.,]?[0-9]+)$", RegexOptions.CultureInvariant);
        private static readonly Regex RxZoneNb = new Regex(@"^(\d+)\s*\.?\s*([#Ø])\s*(\d+)\s*@\s*([0-9]*[.,]?[0-9]+)$", RegexOptions.CultureInvariant);
        private static readonly Regex RxSpacing = new Regex(@"^[cC]\s*/\s*([0-9]*[.,]?[0-9]+)$", RegexOptions.CultureInvariant);
        private static readonly Regex RxCount = new Regex(@"^(?:ES|SON)\s+(\d+)\s+(\p{L}+)\.?$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        private static readonly Regex RxStirrupTotal = new Regex(@"^(?:SON|CANTIDAD)\s*[:=]?\s*(\d+)$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        private static readonly Regex RxSection = new Regex(@"^B\s*=\s*[0-9]+(?:[.,][0-9]+)?\s*H\s*=\s*[0-9]+(?:[.,][0-9]+)?$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        private static readonly Regex RxAxis = new Regex(@"^[A-Z0-9]{1,3}'?(?:/[A-Z0-9]{1,3}'?)?$", RegexOptions.CultureInvariant);
        private static readonly Regex RxLevel = new Regex(@"^([+-])\s*(\d+[.,]\d+)$", RegexOptions.CultureInvariant);
        private static readonly Regex RxWallHorizontal = new Regex(@"^(\d+)\s*([#Ø])\s*(\d+)$", RegexOptions.CultureInvariant);

        private static bool IsBar(Span s) => RxBar.IsMatch(s.Text);
        private static bool IsZone(Span s) => RxZoneE.IsMatch(s.Text) || RxZoneAt.IsMatch(s.Text) || RxZoneNb.IsMatch(s.Text);
        /// <summary>One text holding several zone callouts side by side ("6.#3@.06 2.#3@.25").</summary>
        private static bool IsZoneGroup(string text)
        {
            string[] parts = text.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            return parts.Length >= 2 && parts.All(p => RxZoneNb.IsMatch(p) || RxZoneAt.IsMatch(p));
        }
        private static readonly Regex RxLooksBar = new Regex(@"[#Ø]\s*\d", RegexOptions.CultureInvariant);
        private static readonly Regex RxLooksAmount = new Regex(@"[Ll]\s*=|@|[cC]\s*/", RegexOptions.CultureInvariant);
        private static readonly Regex RxLooksCountOfBars = new Regex(@"^\d+\s*[eE]\s*[#Ø]\s*\d", RegexOptions.CultureInvariant);
        /// <summary>A text that names a bar and a length or a spacing, or a count of stirrups, whatever its shape.</summary>
        private static bool LooksLikeCallout(string text) =>
            (RxLooksBar.IsMatch(text) && RxLooksAmount.IsMatch(text)) || RxLooksCountOfBars.IsMatch(text);
        /// <summary>A reinforcement callout of any kind this reads - what makes a frame a card's.</summary>
        private static bool IsCalloutText(Span t) => IsBar(t) || IsZone(t) || RxWallHorizontal.IsMatch(t.Text) || IsZoneGroup(t.Text);

        // ======================================================================= reading

        private sealed class Ctx
        {
            public Options O;
            public JArray Refusals = new JArray(), Checks = new JArray(), Cards = new JArray(), Tables = new JArray(), Sources = new JArray();
            public List<CardRead> BeamCards = new List<CardRead>();
            public JArray TitlesNotCards = new JArray();
            public int RowsRead;
        }

        private sealed class Frame
        {
            public double X0, Y0, X1, Y1;
            /// <summary>"closed" (four sides) or "top_and_bottom_lines" (rounded corners the reader cannot see).</summary>
            public string Kind = "closed";
            public bool Contains(double x, double y, double tol) => x >= X0 - tol && x <= X1 + tol && y >= Y0 - tol && y <= Y1 + tol;
        }

        private sealed class BarRead
        {
            public JObject Json;
            public double Y;
            public int Count, Size;
            public string System;
            public double? FromStation, ToStation;
            public double? HookStart, HookEnd;
            public double DevelopedMm, NotationMm;
            public double AngleStart, AngleEnd;
            public string ShapeStart, ShapeEnd;
            public string Face;
        }

        private sealed class ZoneRead
        {
            public JObject Json;
            public int Count;
            public int? Size;
            public string System;
            public double SpacingMm, LengthMm;
            public double Y;
            public double? FromStation, ToStation;
        }

        private sealed class CardRead
        {
            public string Id, Title;
            public int Page;
            public bool AxesUncertain;
            public List<double> AxisStations = new List<double>();
            public List<string> AxisNames = new List<string>();
            public List<BarRead> Bars = new List<BarRead>();
            public List<ZoneRead> Zones = new List<ZoneRead>();
            public bool HadRefusals, HadFailedChecks;
        }

        /// <summary>Read every sheet. Options come validated from ReadOptions.</summary>
        public static JObject Read(IList<Sheet> sheets, Options o)
        {
            var c = new Ctx { O = o };
            foreach (Sheet s in sheets) ReadSheet(c, s);
            JObject proposal = null;
            var notProposed = new JArray();
            if (o.Proposal != null) proposal = Propose(c, notProposed);

            var byCode = new SortedDictionary<string, int>(StringComparer.Ordinal);
            foreach (JObject r in c.Refusals) { string k = (string)r["code"]; byCode[k] = byCode.TryGetValue(k, out int n) ? n + 1 : 1; }
            int passed = c.Checks.Count(k => (string)k["result"] == "pass");
            int failed = c.Checks.Count(k => (string)k["result"] == "fail");
            int notRun = c.Checks.Count(k => (string)k["result"] == "not_run");
            int rowsRefused = c.Refusals.Count(r => RowCodes.Contains((string)r["code"]));
            string verdict = failed > 0 ? "checks_failed"
                           : c.Refusals.Count > 0 ? "read_with_refusals"
                           : c.Cards.Count + c.Tables.Count == 0 ? "nothing_read" : "read_clean";
            var refusalsJ = new JObject();
            foreach (KeyValuePair<string, int> kv in byCode) refusalsJ[kv.Key] = kv.Value;

            var reply = new JObject
            {
                ["schema"] = SchemaName,
                ["scale"] = new JObject { ["denominator"] = o.Scale, ["means"] = "one drawing millimetre is " + F(o.Scale) + " real millimetres" },
                ["tolerances"] = o.SupportGapMm.HasValue
                    ? new JObject { ["length_mm"] = o.LengthToleranceMm, ["zone_span_mm"] = o.ZoneSpanToleranceMm, ["support_gap_mm"] = o.SupportGapMm.Value }
                    : new JObject { ["length_mm"] = o.LengthToleranceMm, ["zone_span_mm"] = o.ZoneSpanToleranceMm },
                ["summary"] = new JObject
                {
                    ["sheets"] = sheets.Count,
                    ["beam_cards"] = c.Cards.Count(k => (string)k["kind"] == "beam"),
                    ["wall_cards"] = c.Cards.Count(k => (string)k["kind"] == "wall"),
                    ["tables"] = c.Tables.Count,
                    ["rows_read"] = c.RowsRead,
                    ["rows_refused"] = rowsRefused,
                    ["refusals_by_code"] = refusalsJ,
                    ["checks_passed"] = passed, ["checks_failed"] = failed, ["checks_not_run"] = notRun,
                    ["verdict"] = verdict
                },
                ["sources"] = c.Sources,
                ["cards"] = c.Cards,
                ["tables"] = c.Tables,
                ["refusals"] = c.Refusals,
                ["titles_not_cards"] = c.TitlesNotCards,
                ["checks"] = c.Checks,
                ["requirement_set"] = proposal ?? (JToken)JValue.CreateNull(),
                ["requirement_set_not_proposed"] = notProposed,
                ["writes_nothing"] = true
            };
            if (o.Table == null && o.Tables)
                reply["tables_not_read"] = "no table was read: give table.columns (the header text of mark, bar, count and length as the " +
                                           "sheet writes them). No header dictionary is compiled into this bridge.";
            if (o.Proposal == null)
                reply["requirement_set_not_requested"] = "pass requirement_set: { bar_type_names, hook_type_names?, cover?, set_id? } to have the beam cards proposed as section_rules.";
            else if (proposal != null)
                reply["next"] = "requirement_set is a PROPOSAL read from a drawing: run horizun_plan_reinforcement with it (a dry run that " +
                                "resolves each mark to its hosts and refuses what does not fit), compare the hosts each mark selects with " +
                                "cards[].members_stated, then apply.";
            return reply;
        }

        private static void ReadSheet(Ctx c, Sheet s)
        {
            Options o = c.O;
            var counted = new JObject();
            foreach (KeyValuePair<string, int> kv in s.IgnoredByKind.OrderBy(k => k.Key, StringComparer.Ordinal)) counted[kv.Key] = kv.Value;
            c.Sources.Add(new JObject
            {
                ["source"] = s.Source, ["page"] = s.Page, ["segments"] = s.Segments.Count, ["texts"] = s.Texts.Count,
                ["duplicate_segments_dropped"] = s.DuplicateSegments, ["ignored_by_kind"] = counted, ["text_extent"] = s.TextExtent
            });
            foreach (JObject r in s.Refusals) c.Refusals.Add(r);
            if (s.Texts.Count == 0)
            {
                c.Refusals.Add(Refusal(CodeSheetHasNoText, null, s.Page, null, 0, 0,
                    "page " + s.Page + " of " + s.Source + " carries no text: a scanned sheet, or text drawn as lines. Nothing is read from it " +
                    "and nothing is recognised by shape."));
                return;
            }
            double hMed = Median(s.Texts.Select(t => t.Height));
            var spanFrames = new List<Frame>();

            if (o.BeamCards || o.WallCards)
            {
                List<Span> titles = s.Texts.Where(t => t.Horizontal && t.Height >= TitleHeightRatio * hMed && o.Titles.Any(rx => SafeMatch(rx, t.Text))).ToList();
                var lines = new LineIndex(s.Segments, hMed);
                var found = new List<KeyValuePair<Span, Frame>>();
                var sharedTitles = new List<Tuple<Span, Frame, int>>();
                foreach (Span t in titles)
                {
                    Frame f = FindFrame(t, titles, s, lines, out int shared, out Frame sharedFrame);
                    if (f == null && shared > 1)
                    {
                        sharedTitles.Add(Tuple.Create(t, sharedFrame, shared));
                        continue;
                    }
                    if (f == null)
                    {
                        c.Refusals.Add(Refusal(CodeFrameNotFound, "p" + s.Page + ":" + t.Text, s.Page, t.Text, t.Cx, t.Cy,
                            "no closed frame around the title holds a reinforcement callout. A card is read inside its frame or not at all."));
                        continue;
                    }
                    found.Add(new KeyValuePair<Span, Frame>(t, f));
                }
                // A title whose smallest callout-holding frame is a frame AROUND other cards - the
                // sheet's own number in its border, a region heading - is no card when every callout
                // in that frame lies in the frame of another title found on its own. Nothing of
                // the sheet is left unread by setting it aside, so it is listed, not refused.
                // Otherwise the callouts it would claim are not placed: card_frame_shared.
                var orphaned = new HashSet<Span>();
                foreach (Tuple<Span, Frame, int> st in sharedTitles)
                {
                    Span t = st.Item1;
                    Frame big = st.Item2;
                    List<Frame> inner = found.Where(x => !ReferenceEquals(x.Key, t) && Encloses(big, x.Value) && !x.Value.Contains(t.Mx, t.My, 0)).Select(x => x.Value).ToList();
                    List<Span> orphans = s.Texts.Where(x => big.Contains(x.Mx, x.My, 0) && (IsCalloutText(x) || LooksLikeCallout(x.Text)) &&
                                                            !inner.Any(f => f.Contains(x.Mx, x.My, 0)) && !titles.Any(o => ReferenceEquals(o, x))).ToList();
                    // What that frame holds outside every card is steel no card carries: each one is named.
                    foreach (Span x in orphans.Where(x => !orphaned.Contains(x)))
                    {
                        orphaned.Add(x);
                        c.Refusals.Add(Refusal(CodeCalloutOutsideCards, "p" + s.Page, s.Page, x.Text, x.Cx, x.Cy,
                            "this callout lies in no card's frame (only in a frame around cards): which card it belongs to is not read from position."));
                    }
                    // With nothing left over, the title frames only other cards: a heading or the sheet's
                    // number. With callouts left over it may be THEIR card, whose own frame was not read
                    // (measured: a card with a frame line lost was set aside as a heading) - refused.
                    if (inner.Count > 0 && orphans.Count == 0)
                    {
                        c.TitlesNotCards.Add(new JObject
                        {
                            ["page"] = s.Page, ["title"] = t.Text, ["at_mm"] = new JArray(R(t.Cx), R(t.Cy)),
                            ["why"] = "the smallest frame around it holds " + st.Item3 + " titles and " + inner.Count +
                                      " card frame(s) and nothing outside them: a frame around cards - a heading or the sheet's number - is not a card."
                        });
                        continue;
                    }
                    c.Refusals.Add(Refusal(CodeFrameShared, "p" + s.Page + ":" + t.Text, s.Page, t.Text, t.Cx, t.Cy,
                        "the smallest frame around this title that holds a callout holds " + st.Item3 + " titles: which callout belongs to which card is not read from position."));
                }
                foreach (KeyValuePair<Span, Frame> kv in found)
                {
                    int sharing = found.Count(x => Same(x.Value, kv.Value));
                    if (sharing > 1)
                    {
                        c.Refusals.Add(Refusal(CodeFrameShared, "p" + s.Page + ":" + kv.Key.Text, s.Page, kv.Key.Text, kv.Key.Cx, kv.Key.Cy,
                            "the smallest frame holding this title holds " + sharing + " titles: which callout belongs to which card is not read from position."));
                        continue;
                    }
                    spanFrames.Add(kv.Value);
                    ReadCard(c, s, kv.Key, kv.Value, found.Select(x => x.Value).Where(x => !Same(x, kv.Value)).ToList(), hMed);
                }
            }

            // Sheet-level stated counts: "Son 3 Vigas" outside every card.
            foreach (Span t in s.Texts)
            {
                Match m = RxCount.Match(t.Text);
                if (!m.Success || spanFrames.Any(f => f.Contains(t.Mx, t.My, 0))) continue;
                int stated = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                string noun = m.Groups[2].Value;
                var cards = c.Cards.Cast<JObject>().Where(k => (int)k["page"] == s.Page && k["members_noun"] != null &&
                                                               string.Equals((string)k["members_noun"], noun, StringComparison.OrdinalIgnoreCase)).ToList();
                if (cards.Count == 0)
                    c.Checks.Add(Check(CheckCardCount, "p" + s.Page, "not_run", stated, null, null, null,
                        "'" + t.Text + "' is stated on the sheet, and no card on it states a count with the word '" + noun + "'."));
                else
                {
                    int sum = cards.Sum(k => (int)k["members_stated"]);
                    c.Checks.Add(Check(CheckCardCount, "p" + s.Page, sum == stated ? "pass" : "fail", stated, sum, null, null,
                        "'" + t.Text + "' against the counts the " + cards.Count + " card(s) state with the same word."));
                }
            }

            if (o.Tables && o.Table != null) ReadTables(c, s, hMed);
        }

        // ---------------------------------------------------------------------- frames

        /// <summary>Long horizontal and vertical lines, collinear pieces joined, for finding frames.</summary>
        private sealed class LineIndex
        {
            public readonly List<double[]> H = new List<double[]>(), V = new List<double[]>();   // {y, x0, x1} / {x, y0, y1}
            public LineIndex(List<Seg> segs, double hMed)
            {
                double gap = 0.5 * hMed, same = 0.05 * hMed;
                var h = new List<double[]>(); var v = new List<double[]>();
                foreach (Seg g in segs)
                {
                    double len = g.Length;
                    if (Math.Abs(g.Y1 - g.Y0) <= AngleSlack * len) h.Add(new[] { (g.Y0 + g.Y1) / 2, Math.Min(g.X0, g.X1), Math.Max(g.X0, g.X1) });
                    else if (Math.Abs(g.X1 - g.X0) <= AngleSlack * len) v.Add(new[] { (g.X0 + g.X1) / 2, Math.Min(g.Y0, g.Y1), Math.Max(g.Y0, g.Y1) });
                }
                Merge(h, H, gap, same); Merge(v, V, gap, same);
            }
            /// <summary>
            /// Collinear pieces joined: lines within `same` of one another across are one row, and
            /// along a row pieces closer than `gap` are one line. The row is formed first and then
            /// walked along - measured on a real sheet, ordering by the across coordinate alone let
            /// a piece a hundredth of a millimetre lower and further along swallow a card's whole
            /// bottom line, and the card lost its frame.
            /// </summary>
            private static void Merge(List<double[]> src, List<double[]> dst, double gap, double same)
            {
                List<double[]> sorted = src.OrderBy(a => a[0]).ToList();
                int i = 0;
                while (i < sorted.Count)
                {
                    int j = i;
                    while (j + 1 < sorted.Count && sorted[j + 1][0] - sorted[i][0] <= same) j++;
                    double[] cur = null;
                    foreach (double[] l in sorted.Skip(i).Take(j - i + 1).OrderBy(a => a[1]))
                    {
                        if (cur != null && l[1] <= cur[2] + gap) cur[2] = Math.Max(cur[2], l[2]);
                        else { cur = new[] { l[0], l[1], l[2] }; dst.Add(cur); }
                    }
                    i = j + 1;
                }
            }
        }

        private static Frame FindFrame(Span title, List<Span> titles, Sheet s, LineIndex li, out int shared, out Frame sharedFrame)
        {
            double th = title.Height, tol = 0.5 * th, px = title.Mx, py = title.My;
            double left = title.Cx - title.HalfWidth, right = title.Cx + title.HalfWidth;
            List<double[]> lefts = li.V.Where(v => v[0] < left && v[1] <= py && v[2] >= py && v[2] - v[1] >= 3 * th)
                                       .OrderByDescending(v => v[0]).Take(15).ToList();
            List<double[]> rights = li.V.Where(v => v[0] > right && v[1] <= py && v[2] >= py && v[2] - v[1] >= 3 * th)
                                        .OrderBy(v => v[0]).Take(15).ToList();
            var rects = new List<Frame>();
            foreach (double[] l in lefts)
                foreach (double[] r in rights)
                {
                    double[] top = li.H.Where(h => h[0] > py && h[1] <= l[0] + tol && h[2] >= r[0] - tol).OrderBy(h => h[0]).FirstOrDefault();
                    double[] bottom = li.H.Where(h => h[0] < py && h[1] <= l[0] + tol && h[2] >= r[0] - tol).OrderByDescending(h => h[0]).FirstOrDefault();
                    if (top == null || bottom == null) continue;
                    if (l[1] > bottom[0] + tol || l[2] < top[0] - tol || r[1] > bottom[0] + tol || r[2] < top[0] - tol) continue;
                    rects.Add(new Frame { X0 = l[0], X1 = r[0], Y0 = bottom[0], Y1 = top[0] });
                }
            // A frame with ROUNDED corners: its corners are arcs, and a PDF carries arcs as curves
            // this reader leaves out, so its sides never reach the corners - measured on real
            // sheets, the card's whole outline was its top and bottom lines. Such a frame is a
            // top line and a bottom line around the title with the same ends, at least
            // MinOpenFrameWidth title heights wide; the card says frame_kind so.
            double minWidth = MinOpenFrameWidth * th;
            List<double[]> tops = li.H.Where(h => h[0] > py && h[1] <= left + tol && h[2] >= right - tol && h[2] - h[1] >= minWidth)
                                      .OrderBy(h => h[0]).Take(15).ToList();
            List<double[]> bottoms = li.H.Where(h => h[0] < py && h[1] <= left + tol && h[2] >= right - tol && h[2] - h[1] >= minWidth)
                                         .OrderByDescending(h => h[0]).Take(15).ToList();
            var open = new List<Frame>();
            foreach (double[] t in tops)
                foreach (double[] b in bottoms)
                    if (Math.Abs(t[1] - b[1]) <= 2 * th && Math.Abs(t[2] - b[2]) <= 2 * th && t[0] - b[0] >= 3 * th)
                        open.Add(new Frame { X0 = Math.Min(t[1], b[1]), X1 = Math.Max(t[2], b[2]), Y0 = b[0], Y1 = t[0], Kind = "top_and_bottom_lines" });
            // A closed frame is drawn; an open one is inferred from two lines. The open reading is
            // the fallback for the card no closed frame holds: measured on real wall cards, the
            // title boxes of two cards stacked one above the other made a smaller "open frame"
            // than the card's own closed one, and cut its left column off.
            Frame closed = Smallest(rects, title, titles, s, out shared, out sharedFrame);
            if (closed != null) return closed;
            Frame byLines = Smallest(open, title, titles, s, out int sharedOpen, out Frame sharedOpenFrame);
            if (byLines != null || shared > 0) return byLines;
            shared = sharedOpen; sharedFrame = sharedOpenFrame;
            return null;
        }

        /// <summary>An open frame is at least this many title heights wide; shorter pairs of lines are a drawing's, not a card's.</summary>
        public const double MinOpenFrameWidth = 10.0;

        /// <summary>
        /// The smallest frame, closed or open, that holds a reinforcement callout. If it also holds
        /// another title it is no card's frame: null, with the number of titles it holds.
        /// </summary>
        private static Frame Smallest(List<Frame> rects, Span title, List<Span> titles, Sheet s, out int shared, out Frame sharedFrame)
        {
            shared = 0; sharedFrame = null;
            foreach (Frame f in rects.OrderBy(f => (f.X1 - f.X0) * (f.Y1 - f.Y0)))
            {
                bool holdsCallout = s.Texts.Any(t => f.Contains(t.Mx, t.My, 0) && IsCalloutText(t));
                if (!holdsCallout) continue;
                int inside = titles.Count(t => f.Contains(t.Mx, t.My, 0));
                if (inside > 1) { shared = inside; sharedFrame = f; return null; }
                return f;
            }
            return null;
        }

        /// <summary>A frame around another one (a sheet border around the cards) does not take the cards' axis labels from them.</summary>
        private static bool Encloses(Frame outer, Frame inner) =>
            outer.X0 <= inner.X0 + 1e-6 && outer.X1 >= inner.X1 - 1e-6 && outer.Y0 <= inner.Y0 + 1e-6 && outer.Y1 >= inner.Y1 - 1e-6;

        private static bool Same(Frame a, Frame b) =>
            Math.Abs(a.X0 - b.X0) < 1e-6 && Math.Abs(a.X1 - b.X1) < 1e-6 && Math.Abs(a.Y0 - b.Y0) < 1e-6 && Math.Abs(a.Y1 - b.Y1) < 1e-6;

        // ------------------------------------------------------------------------ cards

        private static void ReadCard(Ctx c, Sheet s, Span title, Frame f, List<Frame> others, double hMed)
        {
            Options o = c.O;
            string id = "p" + s.Page + ":" + title.Text;
            double tol = 0.5 * title.Height;
            List<Span> texts = s.Texts.Where(t => f.Contains(t.Mx, t.My, 0) && !ReferenceEquals(t, title)).ToList();
            List<Seg> segs = s.Segments.Where(g => f.Contains(g.X0, g.Y0, tol) && f.Contains(g.X1, g.Y1, tol)).ToList();

            List<Span> vBars = texts.Where(t => t.Vertical && IsBar(t)).ToList();
            List<Span> hBars = texts.Where(t => t.Horizontal && IsBar(t)).ToList();
            List<Span> zones = texts.Where(t => t.Horizontal && (IsZone(t) || IsZoneGroup(t.Text))).ToList();
            // A wall card writes its bars turned, beside them, and may also write some level; a beam
            // card writes them level and carries stirrup zones. Turned bars AND stirrup zones in one
            // frame is neither convention.
            // A card whose bar callouts are all level is still a wall card when it carries no stirrup
            // zone, at least two level values and a horizontal-bar label written turned (measured on
            // real wall cards: "4 #4 L=2.70" written across the storey's turned bars, "12#3" turned
            // beside the horizontal bars). A beam card writes none of the last two.
            bool levelWall = vBars.Count == 0 && zones.Count == 0 && hBars.Count > 0 &&
                             texts.Count(t => t.Horizontal && RxLevel.IsMatch(t.Text)) >= 2 &&
                             texts.Any(t => t.Vertical && RxWallHorizontal.IsMatch(t.Text));
            bool wall = vBars.Count > 0 || levelWall, beam = !wall && (hBars.Count > 0 || zones.Count > 0);
            if (wall && zones.Count > 0)
            {
                c.Refusals.Add(Refusal(CodeCardKindAmbiguous, id, s.Page, title.Text, title.Cx, title.Cy,
                    "the card holds turned bar callouts (a wall card) and stirrup zones (a beam card)."));
                return;
            }
            if (!wall && !beam)
            {
                wall = texts.Any(t => t.Horizontal && RxWallHorizontal.IsMatch(t.Text)) && texts.Any(t => t.Horizontal && RxLevel.IsMatch(t.Text));
                if (!wall)
                {
                    c.Refusals.Add(Refusal(CodeCardHasNoCallouts, id, s.Page, title.Text, title.Cx, title.Cy, "the frame holds no callout this reads."));
                    return;
                }
            }
            if (wall ? !o.WallCards : !o.BeamCards) return;

            var card = new JObject
            {
                ["id"] = id, ["page"] = s.Page, ["title"] = title.Text, ["kind"] = wall ? "wall" : "beam",
                ["frame_mm"] = new JArray(R(f.X0), R(f.Y0), R(f.X1), R(f.Y1)),
                ["frame_kind"] = f.Kind
            };
            int refusalsBefore = c.Refusals.Count, checksBefore = c.Checks.Count;

            // Members: "Son 2 Vigas". Two different statements in one card: neither is taken.
            var members = texts.Select(t => RxCount.Match(t.Text)).Where(m => m.Success).ToList();
            if (members.Select(m => m.Groups[1].Value).Distinct().Count() == 1)
            {
                card["members_stated"] = int.Parse(members[0].Groups[1].Value, CultureInfo.InvariantCulture);
                card["members_noun"] = members[0].Groups[2].Value;
            }
            else card["members_stated"] = JValue.CreateNull();

            if (wall) ReadWall(c, s, id, f, texts, segs, card);
            else ReadBeam(c, s, id, title, f, others, texts, segs, card, hMed);

            // A text that writes a bar AND a length or a spacing, in a shape none of the
            // conventions above reads, is steel this card states and the reading does not
            // carry: refused by name, never dropped in silence.
            foreach (Span t in texts.Where(t => LooksLikeCallout(t.Text) && !IsCalloutText(t) && !RxSpacing.IsMatch(t.Text)))
                c.Refusals.Add(Refusal(CodeCalloutNotRecognised, id, s.Page, t.Text, t.Cx, t.Cy,
                    "the text writes a bar with a length or a spacing in a form this reader does not read; the steel it states is not in the reading."));

            if (wall)
            {
                c.Cards.Add(card);
                return;
            }
            c.Cards.Add(card);
            CardRead read = c.BeamCards[c.BeamCards.Count - 1];
            read.HadRefusals = c.Refusals.Count > refusalsBefore;
            read.HadFailedChecks = c.Checks.Skip(checksBefore).Any(k => (string)k["result"] == "fail");
        }

        private static void ReadBeam(Ctx c, Sheet s, string id, Span title, Frame f, List<Frame> others, List<Span> texts, List<Seg> segs, JObject card, double hMed)
        {
            Options o = c.O;
            var read = new CardRead { Id = id, Title = title.Text, Page = s.Page };
            c.BeamCards.Add(read);

            // Axes: short labels in the band above the frame, in order along it.
            double band = AxisBand * title.Height;
            List<Span> axes = s.Texts.Where(t => t.Horizontal && RxAxis.IsMatch(t.Text) && t.Height >= 0.8 * hMed &&
                                                t.My > f.Y1 - 0.2 * band && t.My <= f.Y1 + band && t.Cx >= f.X0 && t.Cx <= f.X1 &&
                                                !others.Any(x => x.Contains(t.Mx, t.My, 0) && !Encloses(x, f)))
                                     .OrderBy(t => t.Cx).ToList();
            read.AxesUncertain = axes.Any(a => !a.CentreKnown);
            double? x0 = axes.Count > 0 && !read.AxesUncertain ? axes[0].Cx : (double?)null;
            Func<double, double?> station = x => x0.HasValue ? R((x - x0.Value) * o.Scale) : (double?)null;
            card["axes"] = new JArray(axes.Select(a => (JToken)new JObject
            {
                ["name"] = a.Text, ["x_mm"] = R(a.Cx), ["station_mm"] = station(a.Cx) ?? (JToken)JValue.CreateNull(),
                ["position"] = a.CentreKnown ? "centre" : "anchor_start_extent_unknown"
            }));
            if (x0.HasValue) foreach (Span a in axes) { read.AxisStations.Add(station(a.Cx).Value); read.AxisNames.Add(a.Text); }

            Span section = texts.FirstOrDefault(t => RxSection.IsMatch(t.Text));
            card["section_as_written"] = section != null ? (JToken)section.Text : JValue.CreateNull();

            List<Match> totals = texts.Where(t => !RxCount.IsMatch(t.Text)).Select(t => RxStirrupTotal.Match(t.Text)).Where(m => m.Success).ToList();
            int? stated = totals.Select(m => m.Groups[1].Value).Distinct().Count() == 1 ? int.Parse(totals[0].Groups[1].Value, CultureInfo.InvariantCulture) : (int?)null;
            card["stirrups_stated"] = stated.HasValue ? (JToken)stated.Value : JValue.CreateNull();

            // ---- bars -----------------------------------------------------------------
            List<Seg> barLines = segs.Where(g => (o.BarColors == null || (g.Color != null && o.BarColors.Contains(g.Color))) &&
                                                 (o.BarLayers == null || (g.Layer != null && o.BarLayers.Contains(g.Layer))) &&
                                                 (o.BarLinetypes == null || (g.Linetype != null && o.BarLinetypes.Contains(g.Linetype)))).ToList();
            var barMatches = new List<KeyValuePair<Span, Seg>>();
            foreach (Span t in texts.Where(t => t.Horizontal && IsBar(t)).OrderBy(t => t.Cy).ThenBy(t => t.Cx))
            {
                Seg line = MatchLine(c, s, id, t, barLines);
                if (line != null) barMatches.Add(new KeyValuePair<Span, Seg>(t, line));
            }
            barMatches = DropShared(c, s, id, barMatches);
            var claimed = new HashSet<int>(barMatches.Select(m => m.Value.Id));
            foreach (KeyValuePair<Span, Seg> m in barMatches)
            {
                BarRead b = ReadBar(c, s, id, m.Key, m.Value, segs, station, false);
                if (b != null) read.Bars.Add(b);
            }

            // ---- zones ----------------------------------------------------------------
            int zoneRefusalsBefore = c.Refusals.Count;
            List<Seg> zoneLines = segs.Where(g => !claimed.Contains(g.Id)).ToList();
            var used = new HashSet<Span>();
            var zoneMatches = new List<KeyValuePair<Span, Seg>>();
            var zoneOf = new Dictionary<Span, ZoneRead>();
            foreach (Span t in SplitZoneSpans(texts.Where(t => t.Horizontal && (IsZone(t) || IsZoneGroup(t.Text)))).OrderBy(t => t.Cx))
            {
                ZoneRead z = ParseZone(c, s, id, t, texts, used, out Span lower);
                if (z == null) continue;
                Seg line = null;
                if (!ReferenceEquals(lower, t))
                {
                    // Count over the line and spacing under it ("40e#3" / line / "c/.25"): a line
                    // between the two texts, parallel and under the callout's middle, is its line.
                    List<Seg> between = LinesBetween(t, lower, zoneLines);
                    if (between.Count > 1)
                    {
                        c.Refusals.Add(Refusal(CodeTwoLinesEquallyClose, id, s.Page, t.Text, t.Cx, t.Cy,
                            between.Count + " lines run between the zone's count and its spacing; which one it labels is not chosen."));
                        continue;
                    }
                    if (between.Count == 1) line = between[0];
                }
                if (line == null) line = MatchLine(c, s, id, lower, zoneLines, t.Text, true, out _, ZoneLineMinimum);
                if (line == null) continue;
                zoneMatches.Add(new KeyValuePair<Span, Seg>(t, line));
                zoneOf[t] = z;
            }
            zoneMatches = DropShared(c, s, id, zoneMatches);

            var stirrupSizes = zoneOf.Values.Where(z => z.Size.HasValue).Select(z => z.System + ":" + z.Size.Value).Distinct().ToList();
            // The card's own statement of its stirrup bar, when the caller declared how it is written.
            var labelSizes = new List<string>();
            if (o.StirrupBarLabel != null)
                foreach (Span t in texts)
                {
                    Match lm;
                    try { lm = o.StirrupBarLabel.Match(t.Text); } catch (RegexMatchTimeoutException) { continue; }
                    if (!lm.Success || !lm.Groups["bar"].Success) continue;
                    RebarNotation.Result ln = RebarNotation.Parse("1" + lm.Groups["bar"].Value.Replace(" ", "") + " L=1");
                    if (!ln.Ok)
                    {
                        JObject r = Refusal(CodeNotationRefused, id, s.Page, t.Text, t.Cx, t.Cy, "the card's stirrup bar label: " + ln.Why);
                        r["notation_code"] = ln.Code;
                        c.Refusals.Add(r);
                        continue;
                    }
                    labelSizes.Add(ln.Bars[0].System + ":" + ln.Bars[0].Size);
                }
            labelSizes = labelSizes.Distinct().ToList();
            card["stirrup_bar_stated"] = labelSizes.Count == 1
                ? (JToken)BarJson(labelSizes[0].Split(':')[0], int.Parse(labelSizes[0].Split(':')[1], CultureInfo.InvariantCulture)) : JValue.CreateNull();
            List<string> cardSizes = stirrupSizes.Union(labelSizes).ToList();
            foreach (KeyValuePair<Span, Seg> m in zoneMatches.OrderBy(m => Math.Min(m.Value.X0, m.Value.X1)))
            {
                ZoneRead z = zoneOf[m.Key];
                Seg g = m.Value;
                if (!z.Size.HasValue)
                {
                    if (cardSizes.Count != 1)
                    {
                        c.Refusals.Add(Refusal(CodeZoneBarNotStated, id, s.Page, m.Key.Text, m.Key.Cx, m.Key.Cy,
                            cardSizes.Count == 0 ? "the zone states no bar and no other zone on the card does" +
                                                   (o.StirrupBarLabel == null ? " (a card that states its stirrup bar in a label is read with stirrup_bar_label declared)." : ", nor a stirrup bar label.")
                                                 : "the zone states no bar and the card states " + cardSizes.Count + " different stirrup bars."));
                        continue;
                    }
                    string[] sz = cardSizes[0].Split(':');
                    z.System = sz[0]; z.Size = int.Parse(sz[1], CultureInfo.InvariantCulture);
                    z.Json["bar_from"] = stirrupSizes.Count == 0 ? "card_label" : "card";
                }
                z.Json["bar"] = BarJson(z.System, z.Size.Value);
                z.LengthMm = R(g.Length * o.Scale);
                z.Y = (g.Y0 + g.Y1) / 2;
                double a = Math.Min(g.X0, g.X1), b = Math.Max(g.X0, g.X1);
                z.Json["length_mm"] = z.LengthMm;
                z.Json["from_station_mm"] = station(a) ?? (JToken)JValue.CreateNull();
                z.Json["to_station_mm"] = station(b) ?? (JToken)JValue.CreateNull();
                z.FromStation = station(a); z.ToStation = station(b);
                read.Zones.Add(z);
                c.RowsRead++;
                double expected = R((z.Count - 1) * z.SpacingMm);
                double diff = R(Math.Abs(expected - z.LengthMm));
                c.Checks.Add(Check(CheckZoneCount, id, diff <= o.LengthToleranceMm ? "pass" : "fail", expected, z.LengthMm, diff, o.LengthToleranceMm,
                    "'" + m.Key.Text + "': " + z.Count + " stirrups at " + F(z.SpacingMm) + " mm measure (n-1) x spacing; the dimension line measures " + F(z.LengthMm) + " mm at 1:" + F(o.Scale) + "."));
            }

            // ---- faces: above the stirrup zone line is top, below is bottom ------------------
            if (read.Zones.Count > 0)
            {
                double zy = read.Zones.Average(z => z.Y);
                bool both = read.Bars.Any(b => b.Y > zy) && read.Bars.Any(b => b.Y < zy);
                foreach (BarRead b in read.Bars)
                {
                    b.Face = both && b.Y != zy ? (b.Y > zy ? "top" : "bottom") : null;
                    b.Json["face"] = b.Face ?? (JToken)JValue.CreateNull();
                }
            }
            else foreach (BarRead b in read.Bars) b.Json["face"] = JValue.CreateNull();

            card["bars"] = new JArray(read.Bars.Select(b => b.Json));
            card["zones"] = new JArray(read.Zones.Select(z => z.Json));
            bool zoneRefused = c.Refusals.Count > zoneRefusalsBefore;

            // ---- card checks ---------------------------------------------------------------
            if (!stated.HasValue)
                c.Checks.Add(Check(CheckStirrupTotal, id, "not_run", null, null, null, null,
                    totals.Count == 0 ? "the card states no stirrup total ('SON n' or 'Cantidad n')." : "the card states " + totals.Count + " different stirrup totals."));
            else if (zoneRefused)
                c.Checks.Add(Check(CheckStirrupTotal, id, "not_run", stated.Value, null, null, null, "a zone of this card was refused, and a refused zone cannot be summed."));
            else
            {
                int sum = read.Zones.Sum(z => z.Count);
                c.Checks.Add(Check(CheckStirrupTotal, id, sum == stated.Value ? "pass" : "fail", stated.Value, sum, null, null,
                    "the zones' counts (" + string.Join(" + ", read.Zones.Select(z => z.Count.ToString(CultureInfo.InvariantCulture))) + ") against the card's stated total."));
            }
            string spanCheck = o.SupportGapMm.HasValue ? CheckZoneGaps : CheckZoneSpan;
            if (read.AxisStations.Count < 2 || read.Zones.Count == 0 || zoneRefused)
                c.Checks.Add(Check(spanCheck, id, "not_run", null, null, null, o.SupportGapMm ?? o.ZoneSpanToleranceMm,
                    read.AxesUncertain ? "the axis labels' positions are anchors with no extent, so the span between them is not measured." :
                    read.AxisStations.Count < 2 ? "the card shows " + read.AxisStations.Count + " axis label(s); the span needs two." :
                    read.Zones.Count == 0 ? "no stirrup zone was read." : "a zone of this card was refused."));
            else if (o.SupportGapMm.HasValue) ZoneGapCheck(c, id, read);
            else
            {
                double span = R(read.AxisStations[read.AxisStations.Count - 1] - read.AxisStations[0]);
                double sum = R(read.Zones.Sum(z => z.LengthMm));
                double diff = R(Math.Abs(span - sum));
                c.Checks.Add(Check(CheckZoneSpan, id, diff <= o.ZoneSpanToleranceMm ? "pass" : "fail", span, sum, diff, o.ZoneSpanToleranceMm,
                    "the zones' lengths against the span between the first and the last axis, at the declared scale."));
            }
        }

        /// <summary>
        /// The declared support convention (tolerances.support_gap_mm): a zone's line runs from its
        /// first stirrup to its last, so consecutive zones leave a gap - one spacing where one zone
        /// hands over to the next, a support's width where the beam sits on a column. Measured on
        /// real cards: zones are drawn between support faces, and their lengths never add up to the
        /// axis-to-axis span. Every gap - between consecutive zones, and between the first and last
        /// axis and the zone beside them - must be either a hand-over (no wider than the larger
        /// spacing on either side, within length_mm) or a support (an axis inside it or at its end,
        /// no wider than support_gap_mm). No zone runs past the outer axes, no two overlap. A gap
        /// that is neither is where a zone is missing.
        /// </summary>
        private static void ZoneGapCheck(Ctx c, string id, CardRead read)
        {
            Options o = c.O;
            double tol = o.LengthToleranceMm, sup = o.SupportGapMm.Value;
            double first = read.AxisStations[0], last = read.AxisStations[read.AxisStations.Count - 1];
            List<ZoneRead> z = read.Zones.OrderBy(x => x.FromStation.Value).ToList();
            var problems = new List<string>();
            double worst = 0;
            if (z[0].FromStation.Value < first - tol || z[z.Count - 1].ToStation.Value > last + tol)
                problems.Add("a zone runs past the outer axes (" + F(z[0].FromStation.Value) + ".." + F(z[z.Count - 1].ToStation.Value) +
                             " against " + F(first) + ".." + F(last) + " mm)");
            for (int i = 0; i <= z.Count; i++)
            {
                double a = i == 0 ? first : z[i - 1].ToStation.Value, b = i == z.Count ? last : z[i].FromStation.Value;
                double gap = R(b - a);
                worst = Math.Max(worst, gap);
                if (gap < -tol) { problems.Add("two zones overlap by " + F(-gap) + " mm"); continue; }
                bool atEnd = i == 0 || i == z.Count;
                double handOver = Math.Max(i > 0 ? z[i - 1].SpacingMm : 0, i < z.Count ? z[i].SpacingMm : 0);
                bool support = atEnd || read.AxisStations.Any(x => x > a - tol && x < b + tol);
                if (!atEnd && gap <= handOver + tol) continue;
                if (support && gap <= sup) continue;
                problems.Add("a gap of " + F(gap) + " mm at " + F(R(a)) + ".." + F(R(b)) + " mm is " +
                             (support ? "wider than support_gap_mm" : "neither a hand-over (" + F(handOver) + " mm) nor at a support"));
            }
            c.Checks.Add(Check(CheckZoneGaps, id, problems.Count == 0 ? "pass" : "fail", sup, R(worst), null, sup,
                problems.Count == 0 ? "every gap between the zones, and between the outer axes and the zones beside them, is a hand-over or a support."
                                    : string.Join("; ", problems) + "."));
        }

        /// <summary>
        /// The line just below a callout's baseline: parallel to the text, under its middle,
        /// within LineWindowBelow heights. None, or two whose gaps differ by no more than
        /// EquallyClose heights, is a refusal - the line is not chosen.
        /// </summary>
        private static Seg MatchLine(Ctx c, Sheet s, string id, Span t, List<Seg> candidates, string calloutText = null)
            => MatchLine(c, s, id, t, candidates, calloutText, true, out _);

        /// <summary>
        /// refuseNone=false: no line at all is not refused here (none=true); the caller tries another
        /// convention. minLength: the shortest line taken, in text heights (1 for a bar).
        /// </summary>
        private static Seg MatchLine(Ctx c, Sheet s, string id, Span t, List<Seg> candidates, string calloutText, bool refuseNone, out bool none, double minLength = 1.0)
        {
            none = false;
            double h = t.Height;
            var hits = new List<KeyValuePair<Seg, double>>();
            foreach (Seg g in candidates)
            {
                double len = g.Length;
                if (len < minLength * h) continue;
                double ux = (g.X1 - g.X0) / len, uy = (g.Y1 - g.Y0) / len;
                if (Math.Abs(ux * t.Dy - uy * t.Dx) > AngleSlack) continue;
                t.Local(g.X0, g.Y0, out double u0, out double v0);
                t.Local(g.X1, g.Y1, out double u1, out double v1);
                double gap = -(v0 + v1) / 2;
                if (gap < -0.25 * h || gap > LineWindowBelow * h) continue;
                if (Math.Min(u0, u1) - 0.5 * h > 0 || Math.Max(u0, u1) + 0.5 * h < 0) continue;
                hits.Add(new KeyValuePair<Seg, double>(g, gap));
            }
            string text = calloutText ?? t.Text;
            if (hits.Count == 0)
            {
                none = true;
                if (!refuseNone) return null;
                c.Refusals.Add(Refusal(CodeCalloutWithoutLine, id, s.Page, text, t.Cx, t.Cy,
                    "no line runs under this callout within " + F(LineWindowBelow) + " text heights: it is not placed on the nearest line further away."));
                return null;
            }
            hits.Sort((a, b) => a.Value.CompareTo(b.Value));
            if (hits.Count > 1 && hits[1].Value - hits[0].Value <= EquallyClose * h)
            {
                c.Refusals.Add(Refusal(CodeTwoLinesEquallyClose, id, s.Page, text, t.Cx, t.Cy,
                    "two lines run under this callout " + F(R(hits[0].Value)) + " and " + F(R(hits[1].Value)) + " drawing mm below it - equally close within " +
                    F(EquallyClose) + " text heights. Which one it labels is not chosen."));
                return null;
            }
            return hits[0].Key;
        }

        /// <summary>
        /// A zone with few stirrups at a close spacing is drawn short: two at .08 m is 1.6 mm at
        /// 1:50, under the text's height. A zone's line is taken down to this many text heights;
        /// the zone count check then holds the line against (n-1) x spacing.
        /// </summary>
        public const double ZoneLineMinimum = 0.25;

        /// <summary>The height of capitals and digits, as a share of the text height (the em).</summary>
        public const double CapHeight = 0.7;

        /// <summary>Lines parallel to the upper text, between its baseline and the lower text's top, under the upper text's middle.</summary>
        private static List<Seg> LinesBetween(Span upper, Span lower, List<Seg> candidates)
        {
            double h = upper.Height;
            upper.Local(lower.Cx, lower.Cy, out _, out double vLower);
            // The top of the lower text's capitals, not of its em box: measured on real cards the
            // line runs 0.9 of a text height over the lower baseline, inside the em box and clear
            // of the letters (capitals and digits stand about 0.7 of the size).
            double topOfLower = vLower + CapHeight * lower.Height;
            var hits = new List<Seg>();
            if (topOfLower >= 0) return hits;
            foreach (Seg g in candidates)
            {
                double len = g.Length;
                if (len < ZoneLineMinimum * h) continue;
                double ux = (g.X1 - g.X0) / len, uy = (g.Y1 - g.Y0) / len;
                if (Math.Abs(ux * upper.Dy - uy * upper.Dx) > AngleSlack) continue;
                upper.Local(g.X0, g.Y0, out double u0, out double v0);
                upper.Local(g.X1, g.Y1, out double u1, out double v1);
                double v = (v0 + v1) / 2;
                if (v >= 0 || v <= topOfLower) continue;
                if (Math.Min(u0, u1) - 0.5 * h > 0 || Math.Max(u0, u1) + 0.5 * h < 0) continue;
                hits.Add(g);
            }
            return hits;
        }

        private static List<KeyValuePair<Span, Seg>> DropShared(Ctx c, Sheet s, string id, List<KeyValuePair<Span, Seg>> matches)
        {
            var keep = new List<KeyValuePair<Span, Seg>>();
            foreach (IGrouping<int, KeyValuePair<Span, Seg>> g in matches.GroupBy(m => m.Value.Id))
            {
                if (g.Count() == 1) { keep.Add(g.First()); continue; }
                foreach (KeyValuePair<Span, Seg> m in g)
                    c.Refusals.Add(Refusal(CodeLineClaimedTwice, id, s.Page, m.Key.Text, m.Key.Cx, m.Key.Cy,
                        g.Count() + " callouts (" + string.Join(", ", g.Select(x => "'" + x.Key.Text + "'")) + ") stand over the same line; none of them is given it."));
            }
            return keep;
        }

        /// <summary>A bar: its callout through RebarNotation, its drawn line, and the 90-degree ticks at its ends.</summary>
        private static BarRead ReadBar(Ctx c, Sheet s, string id, Span t, Seg line, List<Seg> segs, Func<double, double?> station, bool wall)
            => ReadBar(c, s, id, t, line, segs, station, wall, true, out _, out _);

        /// <summary>
        /// record=false reads the bar without adding its row, its check or its refusal: the
        /// caller (a callout naming a group of drawn bars) answers for the callout once.
        /// </summary>
        private static BarRead ReadBar(Ctx c, Sheet s, string id, Span t, Seg line, List<Seg> segs, Func<double, double?> station, bool wall,
                                       bool record, out string refusedCode, out string refusedWhy)
        {
            Options o = c.O;
            refusedCode = null; refusedWhy = null;
            Match m = RxBar.Match(t.Text);
            string notation = m.Groups[1].Value + m.Groups[2].Value + m.Groups[3].Value + " L=" + m.Groups[4].Value;
            RebarNotation.Result n = RebarNotation.Parse(notation);
            if (!n.Ok)
            {
                JObject r = Refusal(CodeNotationRefused, id, s.Page, t.Text, t.Cx, t.Cy, n.Why);
                r["notation_code"] = n.Code;
                refusedCode = CodeNotationRefused; refusedWhy = n.Why;
                if (record) c.Refusals.Add(r);
                return null;
            }
            // Order the line's ends along the text: start is where the callout begins (left; bottom for a wall bar).
            t.Local(line.X0, line.Y0, out double ua, out _);
            t.Local(line.X1, line.Y1, out double ub, out _);
            double sx = ua <= ub ? line.X0 : line.X1, sy = ua <= ub ? line.Y0 : line.Y1;
            double ex = ua <= ub ? line.X1 : line.X0, ey = ua <= ub ? line.Y1 : line.Y0;
            if (!BarEnd(t, line, sx, sy, sx - ex, sy - ey, segs, out EndRead es, out string why) ||
                !BarEnd(t, line, ex, ey, ex - sx, ey - sy, segs, out EndRead ee, out why))
            {
                refusedCode = CodeHookAmbiguous; refusedWhy = why;
                if (record) c.Refusals.Add(Refusal(CodeHookAmbiguous, id, s.Page, t.Text, t.Cx, t.Cy, why));
                return null;
            }
            Func<EndRead, JObject> hookJson = e => e.Developed <= 0 ? null : new JObject
            {
                ["direction"] = e.Direction, ["angle_degrees"] = e.AngleDegrees,
                ["shape"] = HookShape(e.AngleDegrees) != null ? (JToken)HookShape(e.AngleDegrees) : JValue.CreateNull(),
                ["leg_mm"] = R(e.Leg * o.Scale), ["developed_mm"] = R(e.Developed * o.Scale)
            };
            JObject hs = hookJson(es), he = hookJson(ee);
            double? legS = hs != null ? (double?)R(es.Developed * o.Scale) : null;
            double? legE = he != null ? (double?)R(ee.Developed * o.Scale) : null;
            double straight = R((line.Length + es.Straight + ee.Straight) * o.Scale);
            double developed = R(straight + (legS ?? 0) + (legE ?? 0));
            RebarNotation.Bar bar = n.Bars[0];
            var json = new JObject
            {
                ["callout"] = t.Text, ["count"] = bar.Count, ["bar"] = BarJson(bar.System, bar.Size),
                ["notation_length_mm"] = n.LengthMm.Value, ["drawn_straight_mm"] = straight
            };
            var b = new BarRead
            {
                Json = json, Y = (line.Y0 + line.Y1) / 2, Count = bar.Count, DevelopedMm = developed, NotationMm = n.LengthMm.Value, Size = bar.Size, System = bar.System, HookStart = legS, HookEnd = legE,
                ShapeStart = hs != null ? (string)hs["shape"] : null, ShapeEnd = he != null ? (string)he["shape"] : null,
                AngleStart = es.AngleDegrees, AngleEnd = ee.AngleDegrees
            };
            if (wall)
            {
                json["hook_bottom_mm"] = legS.HasValue ? (JToken)legS.Value : JValue.CreateNull();
                json["hook_top_mm"] = legE.HasValue ? (JToken)legE.Value : JValue.CreateNull();
            }
            else
            {
                json["hook_start"] = hs ?? (JToken)JValue.CreateNull();
                json["hook_end"] = he ?? (JToken)JValue.CreateNull();
                // The straight run, its collinear continuations included.
                double ux = (ex - sx) / line.Length, uy = (ey - sy) / line.Length;
                double ax = sx - ux * es.Straight, bx = ex + ux * ee.Straight;
                b.FromStation = station(Math.Min(ax, bx));
                b.ToStation = station(Math.Max(ax, bx));
                json["from_station_mm"] = b.FromStation ?? (JToken)JValue.CreateNull();
                json["to_station_mm"] = b.ToStation ?? (JToken)JValue.CreateNull();
            }
            json["drawn_developed_mm"] = developed;
            json["note"] = m.Groups[5].Success && m.Groups[5].Value.Trim().Length > 0 ? (JToken)m.Groups[5].Value.Trim() : JValue.CreateNull();
            if (!record) return b;
            c.RowsRead++;
            double diff = R(Math.Abs(developed - n.LengthMm.Value));
            c.Checks.Add(Check(CheckBarLength, id, diff <= o.LengthToleranceMm ? "pass" : "fail", n.LengthMm.Value, developed, diff, o.LengthToleranceMm,
                "'" + t.Text + "': the drawn line" + (legS.HasValue || legE.HasValue ? " and its hook legs" : "") + " at 1:" + F(o.Scale) + " against the callout's L."));
            return b;
        }

        private sealed class EndRead
        {
            /// <summary>Drawing millimetres the bar's own line continues, collinear, past the matched piece.</summary>
            public double Straight;
            /// <summary>The hook's whole drawn length (bend chords and leg), drawing millimetres; 0 for none.</summary>
            public double Developed;
            /// <summary>The last straight run of the hook.</summary>
            public double Leg;
            public double AngleDegrees;
            public string Direction;
        }

        /// <summary>
        /// What continues a bar past one end: the lines of its colour joined end to end - first
        /// any collinear piece (the same bar drawn in pieces), then the hook, a bend drawn as
        /// short chords and a leg, or a single square tick. Walked until it stops. A fork (two
        /// lines leaving one point) or a run longer than HookRunLimit of the bar is refused:
        /// which line is the hook is not chosen.
        /// </summary>
        private static bool BarEnd(Span t, Seg line, double x, double y, double ox, double oy, List<Seg> segs, out EndRead end, out string why)
        {
            end = new EndRead(); why = null;
            double h = t.Height, len = line.Length, eps = 0.1 * h;
            double lx = ox / Math.Sqrt(ox * ox + oy * oy), ly = oy / Math.Sqrt(ox * ox + oy * oy);
            var used = new HashSet<int> { line.Id };
            double cx = x, cy = y, pdx = lx, pdy = ly;
            bool inHook = false;
            int steps = 0;
            while (true)
            {
                List<Seg> near = segs.Where(g => !used.Contains(g.Id) && g.Color == line.Color && g.Layer == line.Layer &&
                                                 (Dist(g.X0, g.Y0, cx, cy) <= eps || Dist(g.X1, g.Y1, cx, cy) <= eps)).ToList();
                // A line whose END lies on the point leaves it; one that only comes near is a
                // fork only when nothing ends there. Measured on real sheets: a hook's bend is
                // drawn in chords a few tenths of a millimetre long, so the chord AFTER the next
                // one comes within EndSnap of the bar's end too - it continues the bend, it does
                // not leave the bar.
                double same = EndExact * h;
                List<Seg> exact = near.Where(g => Math.Min(Dist(g.X0, g.Y0, cx, cy), Dist(g.X1, g.Y1, cx, cy)) <= same).ToList();
                bool chained = exact.Count == 1 && ChainedFrom(exact[0], near, same);
                List<Seg> next = chained ? exact : near;
                if (next.Count == 0) break;
                if (next.Count > 1)
                {
                    why = next.Count + " lines of the bar's colour leave the point (" + F(R(cx)) + ", " + F(R(cy)) + ") at its end: which one is the hook is not chosen.";
                    return false;
                }
                Seg g = next[0];
                used.Add(g.Id);
                bool fromStart = Dist(g.X0, g.Y0, cx, cy) <= Dist(g.X1, g.Y1, cx, cy);
                double nx = fromStart ? g.X1 : g.X0, ny = fromStart ? g.Y1 : g.Y0, gl = g.Length;
                double dx = (nx - cx) / gl, dy = (ny - cy) / gl;
                bool collinear = Math.Abs(dx * pdy - dy * pdx) <= AngleSlack && dx * pdx + dy * pdy > 0;
                if (!inHook && collinear && Math.Abs(dx * ly - dy * lx) <= AngleSlack) end.Straight += gl;
                else
                {
                    inHook = true;
                    end.Developed += gl;
                    end.Leg = collinear && end.Leg > 0 ? end.Leg + gl : gl;
                }
                pdx = dx; pdy = dy; cx = nx; cy = ny;
                if (end.Developed > HookRunLimit * len || ++steps > 64)
                {
                    why = "the line continuing the bar's end runs " + F(R(end.Developed)) + " drawing mm, more than a hook on a bar this long: " +
                          "another bar or a drawing line, not chosen as its hook.";
                    return false;
                }
            }
            if (!inHook) return true;
            double cos = Math.Max(-1, Math.Min(1, pdx * lx + pdy * ly));
            end.AngleDegrees = Math.Round(Math.Acos(cos) * 180 / Math.PI, 1);
            t.Local(cx, cy, out _, out double vEnd);
            t.Local(x, y, out _, out double vBar);
            end.Direction = vEnd > vBar ? "up" : "down";
            return true;
        }

        /// <summary>True when every line of the set is reached from the first through ends drawn joined: one chain, no stranger.</summary>
        private static bool ChainedFrom(Seg first, List<Seg> set, double same)
        {
            Func<Seg, Seg, bool> joined = (a, b) =>
                Dist(a.X0, a.Y0, b.X0, b.Y0) <= same || Dist(a.X0, a.Y0, b.X1, b.Y1) <= same ||
                Dist(a.X1, a.Y1, b.X0, b.Y0) <= same || Dist(a.X1, a.Y1, b.X1, b.Y1) <= same;
            var reached = new HashSet<Seg> { first };
            var queue = new Queue<Seg>(new[] { first });
            while (queue.Count > 0)
            {
                Seg a = queue.Dequeue();
                foreach (Seg b in set)
                    if (!reached.Contains(b) && joined(a, b)) { reached.Add(b); queue.Enqueue(b); }
            }
            return reached.Count == set.Count;
        }

        /// <summary>Two line ends this close (in text heights) are the same point: drawn joined, not merely near.</summary>
        public const double EndExact = 0.005;

        /// <summary>A hook's drawn run longer than this share of its bar is not taken as a hook.</summary>
        public const double HookRunLimit = 0.6;

        /// <summary>The hook shape a measured angle between the bar and its leg reads as, within 10 degrees; null otherwise.</summary>
        private static string HookShape(double angle) =>
            Math.Abs(angle - 90) <= 10 ? "90" : Math.Abs(angle - 135) <= 10 ? "135" : Math.Abs(angle - 180) <= 10 ? "180" : null;

        /// <summary>One text holding several zone callouts ("6.#3@.06 2.#3@.25") is split, each part placed by its characters.</summary>
        private static IEnumerable<Span> SplitZoneSpans(IEnumerable<Span> spans)
        {
            foreach (Span t in spans)
            {
                string[] parts = t.Text.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (!IsZoneGroup(t.Text) || !t.Width.HasValue)
                {
                    yield return t;
                    continue;
                }
                int pos = 0;
                double w = t.Width.Value, n = t.Text.Length;
                double sx = t.Cx - t.Dx * w / 2, sy = t.Cy - t.Dy * w / 2;
                foreach (string p in parts)
                {
                    int i0 = t.Text.IndexOf(p, pos, StringComparison.Ordinal);
                    pos = i0 + p.Length;
                    double a = w * i0 / n, pw = w * p.Length / n;
                    yield return new Span { Id = t.Id, Text = p, X = sx + t.Dx * a, Y = sy + t.Dy * a, Dx = t.Dx, Dy = t.Dy, Height = t.Height, Width = pw };
                }
            }
        }

        private static ZoneRead ParseZone(Ctx c, Sheet s, string id, Span t, List<Span> texts, HashSet<Span> used, out Span lower)
        {
            lower = t;
            string notation;
            int count;
            string type = null;
            Match mE = RxZoneE.Match(t.Text), mNb = RxZoneNb.Match(t.Text), mAt = RxZoneAt.Match(t.Text);
            if (!mE.Success && !mNb.Success && !mAt.Success)
            {
                // Several zones in one text whose extent is not known (a DWG text's single point):
                // where each part stands - and so which line is whose - is not read.
                c.Refusals.Add(Refusal(CodeZoneGroupNotPlaced, id, s.Page, t.Text, t.Cx, t.Cy,
                    "one text holds several zone callouts and its extent is not known, so where each one stands is not read."));
                return null;
            }
            bool sized = true;
            if (mNb.Success)
            {
                count = int.Parse(mNb.Groups[1].Value, CultureInfo.InvariantCulture);
                notation = "E" + mNb.Groups[2].Value + mNb.Groups[3].Value + " " + mNb.Groups[1].Value + "@" + mNb.Groups[4].Value;
            }
            else if (mAt.Success)
            {
                count = int.Parse(mAt.Groups[1].Value, CultureInfo.InvariantCulture);
                // No bar in the callout: the notation is read with a placeholder size that is
                // never reported - the zone's bar comes from the card or the zone is refused.
                notation = "E#3 " + mAt.Groups[1].Value + "@" + mAt.Groups[2].Value;
                sized = false;
            }
            else
            {
                count = int.Parse(mE.Groups[1].Value, CultureInfo.InvariantCulture);
                type = mE.Groups[4].Success ? mE.Groups[4].Value : mE.Groups[5].Success ? mE.Groups[5].Value : null;
                // Its spacing is the "c/.30" written just under it.
                var below = new List<Span>();
                foreach (Span x in texts)
                {
                    if (!x.Horizontal || !RxSpacing.IsMatch(x.Text) || used.Contains(x)) continue;
                    t.Local(x.Cx, x.Cy, out double u, out double v);
                    if (v < -2.5 * t.Height || v > -0.3 * t.Height || Math.Abs(u) > 1.5 * t.Height + t.HalfWidth) continue;
                    below.Add(x);
                }
                if (below.Count != 1)
                {
                    c.Refusals.Add(Refusal(below.Count == 0 ? CodeZoneSpacingMissing : CodeZoneSpacingAmbiguous, id, s.Page, t.Text, t.Cx, t.Cy,
                        below.Count == 0 ? "the zone states a count and a bar and no spacing ('c/...') is written just under it."
                                         : below.Count + " spacings are written under the zone; which one is its own is not chosen."));
                    return null;
                }
                used.Add(below[0]);
                lower = below[0];
                notation = "E" + mE.Groups[2].Value + mE.Groups[3].Value + " " + "c/" + RxSpacing.Match(below[0].Text).Groups[1].Value;
            }
            RebarNotation.Result n = RebarNotation.Parse(notation);
            if (!n.Ok)
            {
                JObject r = Refusal(CodeNotationRefused, id, s.Page, t.Text, t.Cx, t.Cy, n.Why);
                r["notation_code"] = n.Code;
                c.Refusals.Add(r);
                return null;
            }
            if (count == 0)
            {
                JObject r = Refusal(CodeNotationRefused, id, s.Page, t.Text, t.Cx, t.Cy, "'" + t.Text + "' places zero stirrups.");
                r["notation_code"] = RebarNotation.CodeZeroCount;
                c.Refusals.Add(r);
                return null;
            }
            double spacing = n.SpacingMm ?? n.Zones[0].SpacingMm;
            var json = new JObject
            {
                ["callout"] = lower == t ? t.Text : t.Text + " " + lower.Text,
                ["count"] = count,
                ["bar"] = JValue.CreateNull(),
                ["bar_from"] = sized ? "callout" : "card",
                ["spacing_mm"] = spacing,
                ["stirrup_type"] = type != null ? (JToken)type : JValue.CreateNull()
            };
            return new ZoneRead
            {
                Json = json, Count = count, SpacingMm = spacing,
                Size = sized ? n.Bars[0].Size : (int?)null, System = sized ? n.Bars[0].System : null
            };
        }

        // ------------------------------------------------------------------------- walls

        private static void ReadWall(Ctx c, Sheet s, string id, Frame frame, List<Span> texts, List<Seg> segs, JObject card)
        {
            Options o = c.O;
            // Levels: "+2.50" with a horizontal line just under it.
            var levels = new List<double[]>();   // {z_mm, y}
            foreach (Span t in texts.Where(t => t.Horizontal && RxLevel.IsMatch(t.Text)))
            {
                Match m = RxLevel.Match(t.Text);
                if (!MeasuredNumber.TryParse(m.Groups[2].Value, out double z)) continue;
                z = R((m.Groups[1].Value == "-" ? -1 : 1) * z * 1000);
                // The level line runs just under the value, or just over it with the value written
                // beneath (measured on real sheets: the line crosses the top of the text). The line
                // nearest the middle of the text, within 1.5 heights of the baseline either way.
                double mid = t.Cy + t.Height / 2;
                var hits = segs.Where(g => Math.Abs(g.Y1 - g.Y0) <= AngleSlack * g.Length && g.Length >= t.Height)
                               .Select(g => new { g, y = (g.Y0 + g.Y1) / 2 })
                               .Where(x => x.y >= t.Cy - 1.5 * t.Height && x.y <= t.Cy + 1.5 * t.Height &&
                                           Math.Min(x.g.X0, x.g.X1) - 0.5 * t.Height <= t.Cx && Math.Max(x.g.X0, x.g.X1) + 0.5 * t.Height >= t.Cx)
                               .Select(x => new { x.g, gap = Math.Abs(x.y - mid) })
                               .OrderBy(x => x.gap).ToList();
                if (hits.Count == 0 || (hits.Count > 1 && hits[1].gap - hits[0].gap <= EquallyClose * t.Height))
                {
                    c.Refusals.Add(Refusal(CodeLevelWithoutLine, id, s.Page, t.Text, t.Cx, t.Cy,
                        hits.Count == 0 ? "no level line runs within 1.5 text heights of this level text."
                                        : "two lines run equally close to this level text; which one it marks is not chosen."));
                    continue;
                }
                levels.Add(new[] { z, (hits[0].g.Y0 + hits[0].g.Y1) / 2 });
            }
            levels = levels.OrderBy(l => l[0]).ToList();
            card["levels"] = new JArray(levels.Select(l => (JToken)new JObject { ["z_mm"] = l[0], ["y_mm"] = R(l[1]) }));
            bool levelsOk = false;
            if (levels.Count < 2)
                c.Checks.Add(Check(CheckLevels, id, "not_run", null, null, null, o.LengthToleranceMm, "the card shows " + levels.Count + " level line(s); the check needs two."));
            else
            {
                double worst = 0; double[] wl = null;
                foreach (double[] l in levels.Skip(1))
                {
                    double d = Math.Abs((l[0] - levels[0][0]) - (l[1] - levels[0][1]) * o.Scale);
                    if (d >= worst) { worst = d; wl = l; }
                }
                levelsOk = worst <= o.LengthToleranceMm;
                c.Checks.Add(Check(CheckLevels, id, levelsOk ? "pass" : "fail", R(wl[0] - levels[0][0]), R((wl[1] - levels[0][1]) * o.Scale), R(worst), o.LengthToleranceMm,
                    "the levels' stated difference against the distance between their lines at 1:" + F(o.Scale) + "; the bars' heights are given only when they agree."));
            }
            Func<double, double?> z0 = y => levelsOk ? R(levels[0][0] + (y - levels[0][1]) * o.Scale) : (double?)null;

            List<Seg> barLines = segs.Where(g => (o.BarColors == null || (g.Color != null && o.BarColors.Contains(g.Color))) &&
                                                 (o.BarLayers == null || (g.Layer != null && o.BarLayers.Contains(g.Layer))) &&
                                                 (o.BarLinetypes == null || (g.Linetype != null && o.BarLinetypes.Contains(g.Linetype)))).ToList();
            var matches = new List<KeyValuePair<Span, Seg>>();
            var across = new List<Span>();
            foreach (Span t in texts.Where(t => (t.Vertical || t.Horizontal) && IsBar(t)).OrderBy(t => t.Cx).ThenBy(t => t.Cy))
            {
                Seg line = MatchLine(c, s, id, t, barLines, null, !t.Horizontal, out bool none);
                if (line != null) matches.Add(new KeyValuePair<Span, Seg>(t, line));
                else if (none && t.Horizontal) across.Add(t);   // a level callout with no level line under it: perhaps written across turned bars
            }
            var vertical = new JArray();
            var level = new JArray();
            foreach (KeyValuePair<Span, Seg> m in DropShared(c, s, id, matches))
            {
                BarRead b = ReadBar(c, s, id, m.Key, m.Value, segs, x => null, true);
                if (b == null) continue;
                if (m.Key.Horizontal)
                {
                    // A bar written level on a wall card: read and checked like any bar; it has no height range.
                    b.Json.Remove("hook_bottom_mm"); b.Json.Remove("hook_top_mm");
                    level.Add(b.Json);
                    continue;
                }
                double ya = Math.Min(m.Value.Y0, m.Value.Y1), yb = Math.Max(m.Value.Y0, m.Value.Y1);
                b.Json["z_bottom_mm"] = z0(ya) ?? (JToken)JValue.CreateNull();
                b.Json["z_top_mm"] = z0(yb) ?? (JToken)JValue.CreateNull();
                vertical.Add(b.Json);
            }
            var claimedLines = new HashSet<int>(matches.Select(m => m.Value.Id));
            ReadAcross(c, s, id, frame, across, barLines, claimedLines, segs, z0, vertical);
            card["vertical_bars"] = vertical;
            card["level_callout_bars"] = level;

            var horizontal = new JArray();
            var used = new HashSet<Span>();
            // "2#3" over "c/.20" - or both turned, the spacing beside: the spacing is the text written
            // just below the label in the label's own direction, within WallSpacingWindow heights.
            foreach (Span t in texts.Where(t => (t.Horizontal || t.Vertical) && RxWallHorizontal.IsMatch(t.Text)).OrderByDescending(t => t.Cy).ThenBy(t => t.Cx))
            {
                var below = new List<Span>();
                foreach (Span x in texts)
                {
                    if (!RxSpacing.IsMatch(x.Text) || used.Contains(x) || Math.Abs(x.Dx * t.Dy - x.Dy * t.Dx) > AngleSlack || x.Dx * t.Dx + x.Dy * t.Dy <= 0) continue;
                    t.Local(x.Cx, x.Cy, out double u, out double v);
                    if (v < -WallSpacingWindow * t.Height || v > -0.3 * t.Height || Math.Abs(u) > 1.5 * t.Height + t.HalfWidth) continue;
                    below.Add(x);
                }
                if (below.Count != 1)
                {
                    c.Refusals.Add(Refusal(below.Count == 0 ? CodeHorizontalSpacingMissing : CodeHorizontalSpacingAmbiguous, id, s.Page, t.Text, t.Cx, t.Cy,
                        below.Count == 0 ? "a horizontal bar with no spacing ('c/...') written just under it." : below.Count + " spacings are written under it; none is chosen."));
                    continue;
                }
                used.Add(below[0]);
                Match m = RxWallHorizontal.Match(t.Text);
                RebarNotation.Result n = RebarNotation.Parse("E" + m.Groups[2].Value + m.Groups[3].Value + " c/" + RxSpacing.Match(below[0].Text).Groups[1].Value);
                int count = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                if (!n.Ok || count == 0)
                {
                    JObject r = Refusal(CodeNotationRefused, id, s.Page, t.Text + " " + below[0].Text, t.Cx, t.Cy, n.Ok ? "zero bars." : n.Why);
                    r["notation_code"] = n.Ok ? RebarNotation.CodeZeroCount : n.Code;
                    c.Refusals.Add(r);
                    continue;
                }
                horizontal.Add(new JObject
                {
                    ["callout"] = t.Text + " " + below[0].Text, ["count"] = count, ["bar"] = BarJson(n.Bars[0].System, n.Bars[0].Size),
                    ["spacing_mm"] = n.SpacingMm.Value
                });
                c.RowsRead++;
            }
            card["horizontal_bars"] = horizontal;
        }

        /// <summary>
        /// A level bar callout written ACROSS a row of turned bars (measured on real wall cards:
        /// "4 #4 L=2.70" at mid-height of a storey's three vertical bars, crossing one of them).
        /// The bars it names are the bar lines, upright and at least three text heights long, that
        /// run through the whole height of the text, in the card, and that no turned callout of
        /// the card took. It is read only when that count is the callout's own n - a different
        /// count means the group is not what the drawing says it is, and it is refused - and only
        /// when no other such callout reaches the same lines. Each line is walked for its hooks
        /// like any bar; the length check is held against the line furthest from the callout's L.
        /// </summary>
        /// <summary>An upright line lying along the card's left or right side: the frame, not a bar.</summary>
        private static bool OnFrame(Seg g, Frame f, double tol) =>
            (Math.Abs(g.X0 - f.X0) <= tol && Math.Abs(g.X1 - f.X0) <= tol) || (Math.Abs(g.X0 - f.X1) <= tol && Math.Abs(g.X1 - f.X1) <= tol);

        private static void ReadAcross(Ctx c, Sheet s, string id, Frame frame, List<Span> callouts, List<Seg> barLines, HashSet<int> claimed, List<Seg> segs,
                                       Func<double, double?> z0, JArray vertical)
        {
            Options o = c.O;
            var reach = new Dictionary<Span, List<Seg>>();
            foreach (Span t in callouts)
            {
                double bottom = t.Cy, top = t.Cy + t.Height;
                reach[t] = barLines.Where(g => !claimed.Contains(g.Id) && !OnFrame(g, frame, 0.25 * t.Height) && g.Length >= 3 * t.Height && Math.Abs(g.X1 - g.X0) <= AngleSlack * g.Length &&
                                               Math.Min(g.Y0, g.Y1) <= bottom && Math.Max(g.Y0, g.Y1) >= top)
                                   .OrderBy(g => g.X0).ToList();
            }
            foreach (Span t in callouts)
            {
                List<Seg> lines = reach[t];
                if (lines.Count == 0)
                {
                    c.Refusals.Add(Refusal(CodeCalloutWithoutLine, id, s.Page, t.Text, t.Cx, t.Cy,
                        "no line runs under this callout within " + F(LineWindowBelow) + " text heights, and no upright bar line runs through its height: " +
                        "it is not placed on the nearest line further away."));
                    continue;
                }
                List<Span> rivals = callouts.Where(x => !ReferenceEquals(x, t) && reach[x].Any(g => lines.Contains(g))).ToList();
                if (rivals.Count > 0)
                {
                    c.Refusals.Add(Refusal(CodeLineClaimedTwice, id, s.Page, t.Text, t.Cx, t.Cy,
                        (rivals.Count + 1) + " callouts (" + string.Join(", ", new[] { t }.Concat(rivals).Select(x => "'" + x.Text + "'")) +
                        ") are written across the same upright bar lines; none of them is given them."));
                    continue;
                }
                Match m = RxBar.Match(t.Text);
                int n = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                if (lines.Count != n)
                {
                    c.Refusals.Add(Refusal(CodeBarGroupCountDiffers, id, s.Page, t.Text, t.Cx, t.Cy,
                        "the callout states " + n + " bar(s) and " + lines.Count + " upright bar line(s) run through its height: which lines it names is not read."));
                    continue;
                }
                var read = new List<BarRead>();
                string code = null, why = null;
                foreach (Seg g in lines)
                {
                    var turned = new Span { Id = t.Id, Text = t.Text, X = (g.X0 + g.X1) / 2, Y = t.Cy, Dx = 0, Dy = 1, Height = t.Height, Width = 0 };
                    BarRead b = ReadBar(c, s, id, turned, g, segs, x => null, true, false, out code, out why);
                    if (b == null) break;
                    read.Add(b);
                }
                if (read.Count != lines.Count)
                {
                    c.Refusals.Add(Refusal(code, id, s.Page, t.Text, t.Cx, t.Cy, "a bar line the callout is written across: " + why));
                    continue;
                }
                BarRead worst = read.OrderByDescending(b => Math.Abs(b.DevelopedMm - b.NotationMm)).First();
                JObject json = (JObject)worst.Json.DeepClone();
                json["count"] = n;
                json["read_as"] = "level_callout_across_turned_bars";
                json["drawn_lines"] = lines.Count;
                json["drawn_developed_mm_each"] = new JArray(read.Select(b => (JToken)b.DevelopedMm));
                double ya = lines.Min(g => Math.Min(g.Y0, g.Y1)), yb = lines.Max(g => Math.Max(g.Y0, g.Y1));
                json["z_bottom_mm"] = z0(ya) ?? (JToken)JValue.CreateNull();
                json["z_top_mm"] = z0(yb) ?? (JToken)JValue.CreateNull();
                vertical.Add(json);
                c.RowsRead++;
                double diff = R(Math.Abs(worst.DevelopedMm - worst.NotationMm));
                c.Checks.Add(Check(CheckBarLength, id, diff <= o.LengthToleranceMm ? "pass" : "fail", worst.NotationMm, worst.DevelopedMm, diff, o.LengthToleranceMm,
                    "'" + t.Text + "': the " + lines.Count + " upright lines it is written across, each with its hooks, at 1:" + F(o.Scale) +
                    " against the callout's L; the line furthest from it is reported."));
            }
        }

        // ------------------------------------------------------------------------ tables

        private static void ReadTables(Ctx c, Sheet s, double hMed)
        {
            TableSpec spec = c.O.Table;
            List<string> roles = spec.Columns.Keys.ToList();
            var hits = s.Texts.Where(t => t.Horizontal).Select(t => new { t, role = roles.FirstOrDefault(r => string.Equals(spec.Columns[r], t.Text, StringComparison.OrdinalIgnoreCase)) })
                              .Where(x => x.role != null).ToList();
            // Header rows: hits that share a baseline.
            var rowsOfHits = new List<List<Span>>();
            foreach (var x in hits.OrderByDescending(x => x.t.Cy))
            {
                List<Span> row = rowsOfHits.FirstOrDefault(r => Math.Abs(r[0].Cy - x.t.Cy) <= 0.5 * x.t.Height);
                if (row == null) rowsOfHits.Add(new List<Span> { x.t }); else row.Add(x.t);
            }
            int n = 0;
            foreach (List<Span> header in rowsOfHits)
            {
                var byRole = header.GroupBy(t => roles.First(r => string.Equals(spec.Columns[r], t.Text, StringComparison.OrdinalIgnoreCase)))
                                   .ToDictionary(g => g.Key, g => g.ToList());
                if (byRole.Count < 2) continue;   // one header word alone ("TOTAL" on the totals row) is not a header row
                string tid = "p" + s.Page + ":table" + (++n);
                if (byRole.Count < roles.Count)
                {
                    c.Refusals.Add(Refusal(CodeTableHeaderIncomplete, tid, s.Page, string.Join(" | ", header.Select(t => t.Text)), header[0].Cx, header[0].Cy,
                        "a header row names " + byRole.Count + " of the " + roles.Count + " declared columns; missing: " +
                        string.Join(", ", roles.Where(r => !byRole.ContainsKey(r)).Select(r => r + " ('" + spec.Columns[r] + "')")) + "."));
                    continue;
                }
                if (byRole.Values.Any(l => l.Count > 1))
                {
                    c.Refusals.Add(Refusal(CodeTableHeaderAmbiguous, tid, s.Page, string.Join(" | ", header.Select(t => t.Text)), header[0].Cx, header[0].Cy,
                        "a declared header is written twice in one header row."));
                    continue;
                }
                ReadTable(c, s, tid, byRole.ToDictionary(kv => kv.Key, kv => kv.Value[0]));
            }
        }

        private static void ReadTable(Ctx c, Sheet s, string tid, Dictionary<string, Span> head)
        {
            Options o = c.O;
            TableSpec spec = o.Table;
            List<KeyValuePair<string, Span>> cols = head.OrderBy(kv => kv.Value.Cx).ToList();
            double h = Median(head.Values.Select(t => t.Height));
            double headerY = head.Values.Average(t => t.Cy);
            var bounds = new double[cols.Count + 1];
            for (int i = 1; i < cols.Count; i++) bounds[i] = (cols[i - 1].Value.Cx + cols[i].Value.Cx) / 2;
            bounds[0] = cols[0].Value.Cx - (cols.Count > 1 ? (cols[1].Value.Cx - cols[0].Value.Cx) / 2 : 3 * h);
            bounds[cols.Count] = cols[cols.Count - 1].Value.Cx + (cols.Count > 1 ? (cols[cols.Count - 1].Value.Cx - cols[cols.Count - 2].Value.Cx) / 2 : 3 * h);
            var headerSet = new HashSet<Span>(head.Values);
            List<Span> body = s.Texts.Where(t => t.Horizontal && !headerSet.Contains(t) && t.Cy < headerY - 0.5 * h && t.Cx >= bounds[0] && t.Cx <= bounds[cols.Count])
                                     .OrderByDescending(t => t.Cy).ToList();
            var lines = new List<List<Span>>();
            foreach (Span t in body)
            {
                List<Span> last = lines.Count > 0 ? lines[lines.Count - 1] : null;
                if (last != null && Math.Abs(last[0].Cy - t.Cy) <= 0.5 * h) last.Add(t);
                else lines.Add(new List<Span> { t });
            }
            var table = new JObject
            {
                ["id"] = tid, ["page"] = s.Page, ["header_at_mm"] = new JArray(R(cols[0].Value.Cx), R(headerY)),
                ["columns"] = new JObject(cols.Select(kv => new JProperty(kv.Key, spec.Columns[kv.Key])))
            };
            var rows = new JArray();
            double pitch = 0, prevY = headerY;
            bool refusedAny = false;
            double? stated = null;
            double sum = 0;
            int index = 0;
            foreach (List<Span> line in lines)
            {
                double y = line[0].Cy;
                double step = prevY - y;
                if (pitch == 0) pitch = step;
                else if (step > 3 * pitch) break;          // past the table: the next text is far below
                prevY = y;
                index++;
                var cells = new Dictionary<string, List<Span>>(StringComparer.Ordinal);
                foreach (Span t in line)
                    for (int i = 0; i < cols.Count; i++)
                        if (t.Cx >= bounds[i] && t.Cx < bounds[i + 1])
                        {
                            if (!cells.ContainsKey(cols[i].Key)) cells[cols[i].Key] = new List<Span>();
                            cells[cols[i].Key].Add(t);
                        }
                string mark = cells.ContainsKey("mark") && cells["mark"].Count == 1 ? cells["mark"][0].Text : null;
                if (spec.TotalLabel != null && mark != null && string.Equals(mark, spec.TotalLabel, StringComparison.OrdinalIgnoreCase))
                {
                    if (cells.ContainsKey("total") && cells["total"].Count == 1 && MeasuredNumber.TryParse(cells["total"][0].Text, out double tv))
                        stated = R(tv * 1000);
                    break;
                }
                string why;
                string code = TableRow(spec, cells, out JObject row, out why);
                if (code != null)
                {
                    refusedAny = true;
                    Span at = line[0];
                    c.Refusals.Add(Refusal(code, tid, s.Page, string.Join(" | ", line.OrderBy(t => t.Cx).Select(t => t.Text)), at.Cx, at.Cy, why));
                    continue;
                }
                row.AddFirst(new JProperty("row_index", index));
                rows.Add(row);
                c.RowsRead++;
                if (row["total_mm"] != null && row["total_mm"].Type != JTokenType.Null)
                {
                    double expected = R((int)row["count"] * (double)row["length_mm"]);
                    double found = (double)row["total_mm"];
                    double diff = R(Math.Abs(expected - found));
                    sum += found;
                    c.Checks.Add(Check(CheckTableRow, tid, diff <= o.LengthToleranceMm ? "pass" : "fail", expected, found, diff, o.LengthToleranceMm,
                        "row '" + (string)row["mark"] + "': count x length against its total column."));
                }
            }
            table["rows"] = rows;
            table["total_stated_mm"] = stated.HasValue ? (JToken)stated.Value : JValue.CreateNull();
            c.Tables.Add(table);
            if (!head.ContainsKey("total"))
                return;
            if (!stated.HasValue)
                c.Checks.Add(Check(CheckTableSum, tid, "not_run", null, R(sum), null, o.LengthToleranceMm,
                    spec.TotalLabel == null ? "table.total_label was not declared, so the totals row is not recognised." : "no totals row ('" + spec.TotalLabel + "') with a number was found under the rows."));
            else if (refusedAny)
                c.Checks.Add(Check(CheckTableSum, tid, "not_run", stated.Value, null, null, o.LengthToleranceMm, "a row of this table was refused, and a refused row cannot be summed."));
            else
            {
                double diff = R(Math.Abs(stated.Value - sum));
                c.Checks.Add(Check(CheckTableSum, tid, diff <= o.LengthToleranceMm ? "pass" : "fail", stated.Value, R(sum), diff, o.LengthToleranceMm,
                    "the rows' totals against the totals row."));
            }
        }

        private static string TableRow(TableSpec spec, Dictionary<string, List<Span>> cells, out JObject row, out string why)
        {
            row = null; why = null;
            foreach (KeyValuePair<string, List<Span>> kv in cells)
                if (kv.Value.Count > 1) { why = "the " + kv.Key + " cell holds " + kv.Value.Count + " texts; they are not joined."; return CodeTableCellTwoTexts; }
            foreach (string role in RequiredTableRoles.Concat(spec.Columns.ContainsKey("total") ? new[] { "total" } : new string[0]))
                if (!cells.ContainsKey(role)) { why = "the " + role + " cell is empty."; return CodeTableCellMissing; }
            string bar = cells["bar"][0].Text.Replace(" ", "");
            if (!Regex.IsMatch(bar, @"^[#Ø]\d+$"))
            {
                if (spec.BarPrefix != null && Regex.IsMatch(bar, @"^\d+$")) bar = spec.BarPrefix + bar;
                else { why = "'" + bar + "' is not a bar as #n or Øn" + (spec.BarPrefix == null ? "; a bare number is read only with table.bar_prefix declared." : "."); return CodeTableBarUnrecognized; }
            }
            string countText = cells["count"][0].Text;
            if (!int.TryParse(countText, NumberStyles.None, CultureInfo.InvariantCulture, out int count))
            { why = "count '" + countText + "' is not a whole number."; return CodeTableCellNotNumber; }
            string lengthText = cells["length"][0].Text;
            if (!MeasuredNumber.TryParse(lengthText, out double _))
            { why = "length '" + lengthText + "' is not a number in metres read without guessing."; return CodeTableCellNotNumber; }
            double? total = null;
            if (cells.ContainsKey("total"))
            {
                if (!MeasuredNumber.TryParse(cells["total"][0].Text, out double tv)) { why = "total '" + cells["total"][0].Text + "' is not a number."; return CodeTableCellNotNumber; }
                total = R(tv * 1000);
            }
            RebarNotation.Result n = RebarNotation.Parse(count + bar + " L=" + lengthText);
            if (!n.Ok) { why = n.Why; return n.Code == RebarNotation.CodeBarOutOfRange ? CodeTableBarUnrecognized : CodeTableCellNotNumber; }
            row = new JObject
            {
                ["mark"] = cells["mark"][0].Text, ["bar"] = BarJson(n.Bars[0].System, n.Bars[0].Size), ["count"] = count,
                ["length_mm"] = n.LengthMm.Value, ["total_mm"] = total.HasValue ? (JToken)total.Value : JValue.CreateNull()
            };
            if (cells.ContainsKey("shape")) row["shape"] = cells["shape"][0].Text;
            if (cells.ContainsKey("notation")) row["notation"] = cells["notation"][0].Text;
            return null;
        }

        // ---------------------------------------------------------------------- proposal

        private static JObject Propose(Ctx c, JArray notProposed)
        {
            Options o = c.O;
            ProposalSpec p = o.Proposal;
            Action<string, string, string> no = (subject, code, why) => notProposed.Add(new JObject { ["subject"] = subject, ["code"] = code, ["why"] = why });
            if (c.BeamCards.Count == 0) { no(null, NpNoBeamCards, "no beam card was read; walls and tables are read and checked, not proposed."); return null; }
            double tol = o.LengthToleranceMm;
            var barTypes = new SortedDictionary<string, string>(StringComparer.Ordinal);   // id -> type name
            var hooksUsed = new SortedSet<string>(StringComparer.Ordinal);
            var rules = new JArray();
            foreach (CardRead k in c.BeamCards)
            {
                int before = notProposed.Count;
                if (c.BeamCards.Count(x => x.Title == k.Title) > 1)
                    no(k.Id, NpTitleRepeated, "two cards carry the title '" + k.Title + "', and a section rule is keyed by mark.");
                if (k.HadRefusals) no(k.Id, NpCardHasRefusals, "something on this card was refused, so the steel read from it is not the card's whole steel.");
                if (k.HadFailedChecks) no(k.Id, NpCardChecksFailed, "a self-check of this card failed: what was read disagrees with what the card states.");
                if (k.AxesUncertain) { no(k.Id, NpAxisPositionsUncertain, "the axis labels are anchors with no extent: whether a bar runs axis to axis is not measured."); continue; }
                if (k.AxisStations.Count < 2) { no(k.Id, NpAxesMissing, "the card shows fewer than two axis labels: whether a bar runs the whole member is not measured."); continue; }
                double first = k.AxisStations[0], last = k.AxisStations[k.AxisStations.Count - 1];
                if (p.Extents == "axes")
                {
                    List<JObject> made = ProposeFromAxes(k, p, tol, no, barTypes, hooksUsed);
                    if (notProposed.Count == before) foreach (JObject one in made) rules.Add(one);
                    continue;
                }

                var layers = new JArray();
                foreach (IGrouping<string, BarRead> face in k.Bars.GroupBy(b => b.Face ?? ""))
                {
                    if (face.Key == "")
                    {
                        no(k.Id, NpFaceNotDeterminable, face.Count() + " bar(s) are not on either side of a stirrup zone line: top or bottom is not read.");
                        continue;
                    }
                    List<BarRead> continuous = face.Where(b => b.FromStation <= first + tol && b.ToStation >= last - tol).ToList();
                    foreach (BarRead b in face.Except(continuous))
                        no(k.Id, NpExtentNotExpressible, "'" + (string)b.Json["callout"] + "' runs " + F(b.FromStation.Value) + ".." + F(b.ToStation.Value) +
                           " mm from the first axis, not axis to axis; where the host's start face is relative to the axis is not on the card, so the run is not proposed.");
                    if (continuous.Count == 0) continue;
                    if (continuous.Select(b => b.System + b.Size + "|" + b.ShapeStart + (b.HookStart.HasValue) + "|" + b.ShapeEnd + (b.HookEnd.HasValue)).Distinct().Count() > 1)
                    {
                        no(k.Id, NpMixedContinuousBars, "the " + face.Key + " face carries continuous bars of different sizes or hooks; one layer per face is what a section rule lays.");
                        continue;
                    }
                    BarRead b0 = continuous[0];
                    string key = (b0.System == RebarNotation.SystemBarNumber ? "#" : "Ø") + b0.Size;
                    if (!p.BarTypeNames.TryGetValue(key, out string typeName)) { no(k.Id, NpBarTypeNotDeclared, "no bar_type_names entry for " + key + "."); continue; }
                    string barId = BarId(b0.System, b0.Size);
                    barTypes[barId] = typeName;
                    var layer = new JObject
                    {
                        ["name"] = face.Key, ["face"] = face.Key, ["count"] = continuous.Sum(b => b.Count), ["bar_type"] = barId, ["extend"] = "host_length"
                    };
                    if (b0.HookStart.HasValue || b0.HookEnd.HasValue)
                    {
                        if (b0.HookStart.HasValue != b0.HookEnd.HasValue && !p.CardLeftIsHostStart)
                        {
                            no(k.Id, NpHookEndNotMappable, "the " + face.Key + " bars are hooked at one end only; which end of the host the card's left is, is not on the card " +
                               "(declare requirement_set.card_left_is_host_start to map it).");
                            continue;
                        }
                        string[] shapes = { b0.HookStart.HasValue ? b0.ShapeStart : "", b0.HookEnd.HasValue ? b0.ShapeEnd : "" };
                        if (shapes.Any(x => x == null))
                        {
                            no(k.Id, NpHookShapeNotRead, "a hook of the " + face.Key + " bars is drawn at an angle that is not 90, 135 or 180 degrees within 10.");
                            continue;
                        }
                        string missing = shapes.Where(x => x.Length > 0 && !p.HookTypeNames.ContainsKey(x)).FirstOrDefault();
                        if (missing != null) { no(k.Id, NpHookTypeNotDeclared, "the " + face.Key + " bars carry " + missing + "-degree hooks and hook_type_names has no \"" + missing + "\"."); continue; }
                        foreach (string x in shapes.Where(x => x.Length > 0)) hooksUsed.Add(x);
                        if (b0.HookStart.HasValue) layer["start"] = new JObject { ["hook_type"] = "H" + shapes[0] };
                        if (b0.HookEnd.HasValue) layer["end"] = new JObject { ["hook_type"] = "H" + shapes[1] };
                    }
                    layers.Add(layer);
                }

                JObject stirrups = null;
                if (k.Zones.Count > 0)
                {
                    var sizes = k.Zones.Select(z => z.System + ":" + z.Size).Distinct().ToList();
                    List<double> openSupports = k.AxisStations.Skip(1).Take(Math.Max(0, k.AxisStations.Count - 2))
                        .Where(x => !k.Zones.Any(z => z.FromStation.HasValue && z.FromStation <= x && z.ToStation >= x)).ToList();
                    if (sizes.Count > 1) no(k.Id, NpStirrupBarsDiffer, "the zones state different stirrup bars; a section rule's stirrups are one bar.");
                    else if (openSupports.Count > 0)
                        no(k.Id, NpZonesAcrossSupports, "the stirrup zones stop at " + openSupports.Count + " interior support(s) and start again past them; " +
                           "a section rule's zones run on along one host, so the gap at the support is not expressible.");
                    else
                    {
                        ZoneRead z0 = k.Zones[0];
                        string key = (z0.System == RebarNotation.SystemBarNumber ? "#" : "Ø") + z0.Size;
                        bool symmetric = true;
                        for (int i = 0; i < k.Zones.Count; i++)
                        {
                            ZoneRead a = k.Zones[i], b = k.Zones[k.Zones.Count - 1 - i];
                            if (a.Count != b.Count || Math.Abs(a.SpacingMm - b.SpacingMm) > 1e-6 || Math.Abs(a.LengthMm - b.LengthMm) > tol) symmetric = false;
                        }
                        if (!symmetric && !p.CardLeftIsHostStart)
                            no(k.Id, NpZonesOrientationUnknown, "the stirrup zones are not symmetric, and which end of the host the card's left is, is not on the card " +
                               "(declare requirement_set.card_left_is_host_start to map it).");
                        else if (!p.BarTypeNames.TryGetValue(key, out string typeName)) no(k.Id, NpBarTypeNotDeclared, "no bar_type_names entry for " + key + ".");
                        else
                        {
                            string barId = BarId(z0.System, z0.Size.Value);
                            barTypes[barId] = typeName;
                            var zones = new JArray();
                            for (int i = 0; i < k.Zones.Count; i++)
                            {
                                var layout = new JObject { ["rule"] = "maximum_spacing", ["spacing_mm"] = k.Zones[i].SpacingMm };
                                if (i < k.Zones.Count - 1) layout["include_last_bar"] = false;
                                zones.Add(new JObject { ["name"] = "z" + (i + 1), ["length_mm"] = k.Zones[i].LengthMm, ["layout"] = layout });
                            }
                            stirrups = new JObject { ["bar_type"] = barId, ["zones"] = zones };
                            if (p.StirrupHookTypeName != null)
                            {
                                stirrups["start"] = new JObject { ["hook_type"] = "HS" };
                                stirrups["end"] = new JObject { ["hook_type"] = "HS" };
                            }
                        }
                    }
                }
                if (notProposed.Count > before) continue;
                var rule = new JObject
                {
                    ["id"] = k.Title,
                    ["host"] = new JObject { ["category"] = "OST_StructuralFraming", ["mark"] = k.Title },
                    ["cover"] = p.Cover != null ? p.Cover.DeepClone() : new JObject { ["source"] = "host" },
                    ["layers"] = layers
                };
                if (stirrups != null) rule["stirrups"] = stirrups;
                rules.Add(rule);
            }
            // Half a requirement set is worse than none: it builds some of the steel and leaves
            // the caller believing it asked for what arrived. Every reason is listed instead.
            if (notProposed.Count > 0) return null;
            var hookTypes = new JArray();
            foreach (string x in hooksUsed) hookTypes.Add(new JObject { ["id"] = "H" + x, ["type_name"] = p.HookTypeNames[x] });
            if (p.StirrupHookTypeName != null) hookTypes.Add(new JObject { ["id"] = "HS", ["type_name"] = p.StirrupHookTypeName });
            var set = new JObject
            {
                ["schema"] = StructuralRequirementSetSchema,
                ["units"] = "millimeter",
                ["requirement_set"] = new JObject { ["id"] = p.SetId ?? "schedule-reading", ["version"] = "1.0.0", ["title"] = "Proposed from " + c.BeamCards.Count + " beam card(s) read by horizun_read_schedule" },
                ["bar_types"] = new JArray(barTypes.Select(kv => (JToken)new JObject { ["id"] = kv.Key, ["type_name"] = kv.Value }))
            };
            if (hookTypes.Count > 0) set["hook_types"] = hookTypes;
            set["section_rules"] = rules;
            return set;
        }

        /// <summary>
        /// One beam card as section rules whose extents are stations from the card's OWN axes (stage 1c):
        /// the bars and the stirrups exactly where the card draws them, measured from its first axis
        /// toward its last; the plan finds those grids in the model by name and refuses a host they do
        /// not bound. Every bar group is its own layer, so a baston is expressed, and its hooks are at
        /// the ends the card draws them - no host orientation is assumed. Refused by name: two groups
        /// overlapping on one face (where each stands across the face is not on the card), zones the
        /// stated layouts do not reproduce stirrup for stirrup, axis names repeated on the card.
        /// The stirrup runs a support separates become rules of their own: one host's zones run on.
        /// </summary>
        private static List<JObject> ProposeFromAxes(CardRead k, ProposalSpec p, double tol, Action<string, string, string> no,
                                                     SortedDictionary<string, string> barTypes, SortedSet<string> hooksUsed)
        {
            var made = new List<JObject>();
            if (k.AxisNames.Distinct(StringComparer.Ordinal).Count() != k.AxisNames.Count)
            {
                no(k.Id, NpAxisNamesRepeated, "the card writes one axis name twice (" + string.Join(", ", k.AxisNames) + "): which grid a station is from is not read.");
                return made;
            }
            string axis = k.AxisNames[0], toward = k.AxisNames[k.AxisNames.Count - 1];
            Func<double, double, JObject> extent = (a, b) => new JObject { ["axis"] = axis, ["toward"] = toward, ["from_mm"] = R(a), ["to_mm"] = R(b) };
            Func<string, int, string> barKey = (sys, size) => (sys == RebarNotation.SystemBarNumber ? "#" : "Ø") + size;

            var layers = new JArray();
            foreach (IGrouping<string, BarRead> face in k.Bars.GroupBy(b => b.Face ?? ""))
            {
                if (face.Key == "")
                {
                    no(k.Id, NpFaceNotDeterminable, face.Count() + " bar(s) are not on either side of a stirrup zone line: top or bottom is not read.");
                    continue;
                }
                // Bars drawn alike - one size, one run, one hook at each end - are one layer.
                var groups = new List<List<BarRead>>();
                foreach (BarRead b in face.OrderBy(b => b.FromStation))
                {
                    List<BarRead> g = groups.FirstOrDefault(x => x[0].System == b.System && x[0].Size == b.Size &&
                        Math.Abs(x[0].FromStation.Value - b.FromStation.Value) <= tol && Math.Abs(x[0].ToStation.Value - b.ToStation.Value) <= tol &&
                        x[0].ShapeStart == b.ShapeStart && x[0].ShapeEnd == b.ShapeEnd && x[0].HookStart.HasValue == b.HookStart.HasValue && x[0].HookEnd.HasValue == b.HookEnd.HasValue);
                    if (g == null) groups.Add(new List<BarRead> { b }); else g.Add(b);
                }
                // THE FACE, AS THE CALLER DECLARED IT IS LAID (requirement_set.face_slots). Two groups
                // overlapping on one face need a position across it the card does not give: without
                // the declaration they are refused; with it, only what it resolves without a doubt.
                var units = new List<FaceUnit>();
                foreach (List<BarRead> g in groups) units.Add(FaceUnit.Of(g));
                if (p.LapsToward != null) units = FaceUnit.ChainLaps(units, tol);
                if (p.AdditionalBetween) FaceUnit.SetBetween(units, tol);
                for (int i = 0; i < units.Count; i++)
                    for (int j = i + 1; j < units.Count; j++)
                    {
                        if (units[i].Between == units[j] || units[j].Between == units[i]) continue;
                        double over = Math.Min(units[i].To, units[j].To) - Math.Max(units[i].From, units[j].From);
                        if (over > tol)
                            no(k.Id, NpFaceBarsOverlap, "on the " + face.Key + " face '" + units[i].Callout + "' and '" + units[j].Callout +
                               "' overlap over " + F(R(over)) + " mm: where each stands across the face is not on the card" +
                               (p.LapsToward == null && !p.AdditionalBetween ? " (requirement_set.face_slots declares how a schedule lays laps and bars between continuous ones)." :
                                ", and face_slots does not resolve it: a lap is two groups of one bar and one count overlapping at their unhooked ends; " +
                                "a bar between continuous bars is a group lying within one continuous group, one bar per gap (one fewer than it)."));
                    }
                // A layer laid between others is declared after the layer whose gaps it fills.
                units = units.OrderBy(u => u.Between != null ? 1 : 0).ToList();
                for (int i = 0; i < units.Count; i++) units[i].Name = face.Key + (units.Count > 1 ? "-" + (i + 1).ToString(CultureInfo.InvariantCulture) : "");
                foreach (FaceUnit u in units)
                {
                    string key = barKey(u.System, u.Size);
                    if (!p.BarTypeNames.TryGetValue(key, out string typeName)) { no(k.Id, NpBarTypeNotDeclared, "no bar_type_names entry for " + key + "."); continue; }
                    string barId = BarId(u.System, u.Size);
                    barTypes[barId] = typeName;
                    var layer = new JObject
                    {
                        ["name"] = u.Name, ["face"] = face.Key, ["count"] = u.Count, ["bar_type"] = barId,
                        ["extend"] = extent(u.From, u.To)
                    };
                    if (u.Between != null) layer["between"] = u.Between.Name;
                    if (u.Laps.Count > 0)
                        layer["laps"] = new JArray(u.Laps.Select(l => (JToken)new JObject { ["at_mm"] = R(l[0]), ["length_mm"] = R(l[1]), ["toward"] = p.LapsToward }));
                    BarRead first = u.First, last = u.Last;
                    string[] shapes = { first.HookStart.HasValue ? first.ShapeStart : "", last.HookEnd.HasValue ? last.ShapeEnd : "" };
                    if (shapes.Any(x => x == null))
                    {
                        bool parallel = (first.HookStart.HasValue && first.ShapeStart == null && first.AngleStart < 10) ||
                                        (last.HookEnd.HasValue && last.ShapeEnd == null && last.AngleEnd < 10);
                        if (parallel)
                            no(k.Id, NpBarEndParallel, "'" + u.Callout + "' bends and runs on parallel to itself: a bayoneta, or the offset a drawing gives one " +
                               "bar of a lap - the card does not say which, so it is read as neither a hook nor a crank.");
                        else no(k.Id, NpHookShapeNotRead, "a hook of '" + u.Callout + "' is drawn at an angle that is not 90, 135 or 180 degrees within 10.");
                        continue;
                    }
                    string missing = shapes.FirstOrDefault(x => x.Length > 0 && !p.HookTypeNames.ContainsKey(x));
                    if (missing != null) { no(k.Id, NpHookTypeNotDeclared, "'" + u.Callout + "' carries " + missing + "-degree hooks and hook_type_names has no \"" + missing + "\"."); continue; }
                    foreach (string x in shapes.Where(x => x.Length > 0)) hooksUsed.Add(x);
                    if (first.HookStart.HasValue) layer["start"] = new JObject { ["hook_type"] = "H" + shapes[0] };
                    if (last.HookEnd.HasValue) layer["end"] = new JObject { ["hook_type"] = "H" + shapes[1] };
                    layers.Add(layer);
                }
            }

            // Stirrup runs: a gap with an axis inside it is a support, and the run stops there.
            var runs = new List<List<ZoneRead>>();
            List<ZoneRead> zs = k.Zones.Where(z => z.FromStation.HasValue).OrderBy(z => z.FromStation.Value).ToList();
            List<double> interior = k.AxisStations.Skip(1).Take(Math.Max(0, k.AxisStations.Count - 2)).ToList();
            foreach (ZoneRead z in zs)
            {
                List<ZoneRead> run = runs.LastOrDefault();
                bool support = run != null && interior.Any(x => x > run[run.Count - 1].ToStation.Value - tol && x < z.FromStation.Value + tol);
                if (run == null || support) runs.Add(new List<ZoneRead> { z }); else run.Add(z);
            }
            var stirrupRules = new List<JObject>();
            if (zs.Count > 0)
            {
                var sizes = zs.Select(z => z.System + ":" + z.Size).Distinct().ToList();
                if (sizes.Count > 1) no(k.Id, NpStirrupBarsDiffer, "the zones state different stirrup bars; a section rule's stirrups are one bar.");
                else
                {
                    string key = barKey(zs[0].System, zs[0].Size.Value);
                    if (!p.BarTypeNames.TryGetValue(key, out string typeName)) no(k.Id, NpBarTypeNotDeclared, "no bar_type_names entry for " + key + ".");
                    else
                    {
                        string barId = BarId(zs[0].System, zs[0].Size.Value);
                        barTypes[barId] = typeName;
                        foreach (List<ZoneRead> run in runs)
                        {
                            // Each zone lays its n stirrups at its spacing from its OWN first stirrup
                            // (number_with_spacing), and runs on to where the next zone's first one is: the
                            // card's stirrups, station for station, whatever gap the drawing leaves between
                            // zones. A zone that reaches the next one's first stirrup (no gap) would lay one
                            // stirrup twice, and one that overruns it is drawn wrong: both refused.
                            var zones = new JArray();
                            bool exact = true;
                            for (int i = 0; i < run.Count; i++)
                            {
                                ZoneRead z = run[i];
                                bool lastZone = i == run.Count - 1;
                                double drawn = (z.Count - 1) * z.SpacingMm;
                                double len = lastZone ? drawn : run[i + 1].FromStation.Value - z.FromStation.Value;
                                if (!lastZone && len < drawn + tol) exact = false;
                                JObject layout = z.Count == 1 ? new JObject { ["rule"] = "single" }
                                               : new JObject { ["rule"] = "number_with_spacing", ["number"] = z.Count, ["spacing_mm"] = z.SpacingMm };
                                zones.Add(new JObject { ["name"] = "z" + (i + 1), ["length_mm"] = R(len), ["layout"] = layout });
                            }
                            double end = run[run.Count - 1].FromStation.Value + (run[run.Count - 1].Count - 1) * run[run.Count - 1].SpacingMm;
                            if (!exact)
                            {
                                no(k.Id, NpZonesNotConsecutive, "a zone from " + F(R(run[0].FromStation.Value)) + " mm reaches the next zone's first stirrup " +
                                   "with its stated count and spacing: laid as declared, one stirrup would be laid twice or the zones would overlap.");
                                continue;
                            }
                            var st = new JObject { ["bar_type"] = barId, ["extent"] = extent(run[0].FromStation.Value, end), ["zones"] = zones };
                            if (p.StirrupHookTypeName != null) { st["start"] = new JObject { ["hook_type"] = "HS" }; st["end"] = new JObject { ["hook_type"] = "HS" }; }
                            stirrupRules.Add(st);
                        }
                    }
                }
            }
            for (int i = 0; i < Math.Max(1, stirrupRules.Count); i++)
            {
                string id = i == 0 ? k.Title : k.Title + "-s" + (i + 1).ToString(CultureInfo.InvariantCulture);
                var rule = new JObject
                {
                    ["id"] = id,
                    ["host"] = new JObject { ["category"] = "OST_StructuralFraming", ["mark"] = k.Title },
                    ["cover"] = p.Cover != null ? p.Cover.DeepClone() : new JObject { ["source"] = "host" },
                    ["layers"] = i == 0 ? layers : new JArray()
                };
                if (i < stirrupRules.Count) rule["stirrups"] = stirrupRules[i];
                if (((JArray)rule["layers"]).Count > 0 || rule["stirrups"] != null) made.Add(rule);
            }
            return made;
        }

        /// <summary>
        /// One layer of a face as the proposal lays it: a group of bars drawn alike, or - with
        /// face_slots.laps declared - a chain of such groups lapped end to end.
        /// </summary>
        private sealed class FaceUnit
        {
            public List<List<BarRead>> Pieces = new List<List<BarRead>>();
            public double From, To;
            public int Count, Size;
            public string System, Name;
            public FaceUnit Between;
            /// <summary>[station from the first axis, length] of each lap.</summary>
            public List<double[]> Laps = new List<double[]>();
            public BarRead First => Pieces[0][0];
            public BarRead Last => Pieces[Pieces.Count - 1][0];
            public string Callout => string.Join(" + ", Pieces.Select(x => (string)x[0].Json["callout"]));

            public static FaceUnit Of(List<BarRead> g) => new FaceUnit
            {
                Pieces = { g }, From = g[0].FromStation.Value, To = g[0].ToStation.Value, Count = g.Sum(b => b.Count),
                Size = g[0].Size, System = g[0].System
            };

            /// <summary>
            /// A LAP, read only when it is one beyond doubt: two groups of one bar and one count, the
            /// second starting inside the first and ending past it, neither hooked at the end that laps.
            /// The lap is centred on the overlap and as long as it.
            /// </summary>
            public static List<FaceUnit> ChainLaps(List<FaceUnit> units, double tol)
            {
                var list = units.OrderBy(u => u.From).ToList();
                bool merged = true;
                while (merged)
                {
                    merged = false;
                    for (int i = 0; i < list.Count && !merged; i++)
                        for (int j = 0; j < list.Count && !merged; j++)
                        {
                            FaceUnit a = list[i], b = list[j];
                            if (a == b || a.System != b.System || a.Size != b.Size || a.Count != b.Count) continue;
                            double over = a.To - b.From;
                            if (!(b.From > a.From + tol && b.To > a.To + tol && over > tol)) continue;
                            if (a.Last.HookEnd.HasValue || b.First.HookStart.HasValue) continue;
                            var u = new FaceUnit { From = a.From, To = b.To, Count = a.Count, Size = a.Size, System = a.System };
                            u.Pieces.AddRange(a.Pieces); u.Pieces.AddRange(b.Pieces);
                            u.Laps.AddRange(a.Laps); u.Laps.Add(new[] { (b.From + a.To) / 2, over }); u.Laps.AddRange(b.Laps);
                            list.Remove(a); list.Remove(b); list.Add(u);
                            list = list.OrderBy(x => x.From).ToList();
                            merged = true;
                        }
                }
                return list;
            }

            /// <summary>
            /// A BAR BETWEEN CONTINUOUS BARS, read only when it is one beyond doubt: a group lying within
            /// exactly one other group's stretch, one bar fewer than it (one per gap); neither is lapped (a lapped piece moves across).
            /// </summary>
            public static void SetBetween(List<FaceUnit> units, double tol)
            {
                foreach (FaceUnit inner in units)
                {
                    if (inner.Laps.Count > 0) continue;
                    List<FaceUnit> holders = units.Where(o => o != inner && o.Between == null && o.Laps.Count == 0 &&
                        inner.From >= o.From - tol && inner.To <= o.To + tol && (o.To - o.From) > (inner.To - inner.From) + tol).ToList();
                    if (holders.Count == 1 && holders[0].Count == inner.Count + 1)
                    {
                        // Ends within the reader's own tolerance of the holder's ARE its ends (as two
                        // bars' ends within it are one run): the gaps end where the holder does.
                        inner.Between = holders[0];
                        inner.From = Math.Max(inner.From, holders[0].From);
                        inner.To = Math.Min(inner.To, holders[0].To);
                    }
                }
                // Two groups laid between the same bars over the same stretch would coincide again.
                foreach (FaceUnit a in units)
                    foreach (FaceUnit b in units)
                        if (a != b && a.Between != null && a.Between == b.Between && Math.Min(a.To, b.To) - Math.Max(a.From, b.From) > tol)
                        { a.Between = null; b.Between = null; }
            }
        }

        /// <summary>The schema name of the set this proposes; equal to StructuralRequirementSet's, pinned by a test.</summary>
        public const string StructuralRequirementSetSchema = "horizun.structural-requirements/1";

        private static string BarId(string system, int size) => (system == RebarNotation.SystemBarNumber ? "N" : "D") + size.ToString(CultureInfo.InvariantCulture);

        // ----------------------------------------------------------------------- helpers

        private static JObject BarJson(string system, int size) => new JObject { ["system"] = system, ["size"] = size };

        private static JObject Refusal(string code, string subject, int page, string text, double x, double y, string why)
        {
            var r = new JObject { ["code"] = code, ["subject"] = subject != null ? (JToken)subject : JValue.CreateNull(), ["page"] = page };
            r["text"] = text != null ? (JToken)text : JValue.CreateNull();
            r["at_mm"] = new JArray(R(x), R(y));
            r["why"] = why;
            return r;
        }

        private static JObject Check(string check, string subject, string result, object expected, object found, double? diff, double? tolerance, string why)
        {
            var o = new JObject { ["check"] = check, ["subject"] = subject, ["result"] = result };
            o["expected"] = expected != null ? JToken.FromObject(expected) : JValue.CreateNull();
            o["found"] = found != null ? JToken.FromObject(found) : JValue.CreateNull();
            if (diff.HasValue) o["difference_mm"] = diff.Value;
            if (tolerance.HasValue) o["tolerance_mm"] = tolerance.Value;
            o["why"] = why;
            return o;
        }

        private static Options Refuse(string c, string e, out string code, out string error)
        {
            code = c; error = e;
            return null;
        }

        private static bool Number(JToken t, out double v)
        {
            v = 0;
            if (t == null || (t.Type != JTokenType.Integer && t.Type != JTokenType.Float)) return false;
            v = (double)t;
            return !double.IsNaN(v) && !double.IsInfinity(v);
        }

        private static bool Point(JToken t, out double x, out double y)
        {
            x = y = 0;
            var a = t as JArray;
            return a != null && a.Count >= 2 && Number(a[0], out x) && Number(a[1], out y);
        }

        private static string Str(JToken t) => t != null && t.Type == JTokenType.String ? (string)t : null;
        private static string Color(JToken t) => Str(t)?.ToLowerInvariant();

        private static bool Strings(JToken t, string name, out HashSet<string> set, out string error)
        {
            set = null; error = null;
            if (t == null) return true;
            var a = t as JArray;
            if (a == null || a.Count == 0 || a.Any(x => x.Type != JTokenType.String || string.IsNullOrWhiteSpace((string)x)))
            {
                error = name + " must be a non-empty list of strings.";
                return false;
            }
            set = new HashSet<string>(a.Select(x => (string)x), StringComparer.Ordinal);
            return true;
        }

        private static void Count(Dictionary<string, int> d, string k) => d[k] = d.TryGetValue(k, out int n) ? n + 1 : 1;

        /// <summary>One spelling: the diameter signs as Ø, single spaces, trimmed.</summary>
        private static string Normalize(string s) =>
            Regex.Replace((s ?? "").Replace('ø', 'Ø').Replace('∅', 'Ø').Replace(' ', ' ').Trim(), @"\s+", " ", RegexOptions.CultureInvariant);

        private static bool SafeMatch(Regex r, string s)
        {
            try { return r.IsMatch(s); }
            catch (RegexMatchTimeoutException) { return false; }
        }

        private static double Median(IEnumerable<double> values)
        {
            List<double> v = values.OrderBy(x => x).ToList();
            if (v.Count == 0) return 0;
            return v.Count % 2 == 1 ? v[v.Count / 2] : (v[v.Count / 2 - 1] + v[v.Count / 2]) / 2;
        }

        private static double Dist(double ax, double ay, double bx, double by) => Math.Sqrt((ax - bx) * (ax - bx) + (ay - by) * (ay - by));
        private static double R(double v) => Math.Round(v, 3, MidpointRounding.AwayFromZero);
        private static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
    }
}
