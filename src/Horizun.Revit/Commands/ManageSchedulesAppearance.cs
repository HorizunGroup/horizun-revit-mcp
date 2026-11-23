// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// The Revit half of horizun_manage_schedules set_appearance / split / unsplit.
// The decisions (what a style may say, which header rows are the title, how many
// segments a split makes) are in Core/ScheduleAppearanceRules.cs and proved
// without Revit; this file only talks to the API and RE-READS what it wrote.
//
// WHAT A STYLE IS HERE. TableCellStyle carries a value for every attribute and an
// override flag per attribute; a value written without its flag is accepted and
// ignored. BuildStyle sets the flag for exactly the attributes the caller named,
// and CheckStyle re-reads exactly those - so "bold did nothing" is a failed
// verification that rolls the batch back, not a reply that says it worked.
//
// MEASURED ON PAPER, NOT YET IN REVIT: the unit of TableCellStyle.TextSize (read
// and written here as points), whether a section-wide body style survives a
// refresh when a column carries its own, and how Revit snaps split heights to
// row boundaries. The re-read compares what Revit returns with what was asked;
// where Revit adjusts a value the batch refuses and says what it found.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    internal static class ScheduleAppearanceOps
    {
        private const double FeetPerMm = 1.0 / 304.8;

        // =====================================================================
        // Resolution (read-only - used by the rehearsal and by the apply)
        // =====================================================================

        /// <summary>none / an id / the name of a line style (a subcategory of Lines).</summary>
        internal static string ResolveLineStyle(Document doc, string text, out ElementId id)
        {
            id = ElementId.InvalidElementId;
            string t = (text ?? "").Trim();
            if (t.Equals("none", StringComparison.OrdinalIgnoreCase)) return null;
            long number;
            if (long.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out number))
            {
                if (Rid.CanRepresent(number) && doc.GetElement(Rid.Make(number)) is GraphicsStyle)
                { id = Rid.Make(number); return null; }
                return "border line style id " + t + " is not a line style (a GraphicsStyle). " +
                       "horizun_manage_styles operation=list_line_styles lists them.";
            }
            Category lines = Category.GetCategory(doc, BuiltInCategory.OST_Lines);
            if (lines != null)
                foreach (Category sub in lines.SubCategories.Cast<Category>())
                    if (string.Equals(sub.Name, t, StringComparison.OrdinalIgnoreCase))
                    {
                        GraphicsStyle style = sub.GetGraphicsStyle(GraphicsStyleType.Projection);
                        if (style != null) { id = style.Id; return null; }
                    }
            return "border line style '" + t + "' was not found (use none, an id, or a name from " +
                   "horizun_manage_styles operation=list_line_styles).";
        }

        /// <summary>A text type (TextNoteType) by name or id, valid for a schedule region.</summary>
        internal static string ResolveTextType(Document doc, ViewSchedule schedule, JToken token, out ElementId id)
        {
            id = ElementId.InvalidElementId;
            if (token.Type == JTokenType.Integer || long.TryParse(token.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
            {
                long number = token.Value<long>();
                if (!Rid.CanRepresent(number) || !(doc.GetElement(Rid.Make(number)) is TextNoteType))
                    return "text_type " + number + " is not a text type.";
                id = Rid.Make(number);
            }
            else
            {
                string name = token.ToString();
                var hits = new FilteredElementCollector(doc).OfClass(typeof(TextNoteType)).Cast<TextNoteType>()
                    .Where(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
                if (hits.Count != 1)
                    return "text_type '" + name + "' matches " + hits.Count + " text types; pass its id " +
                           "(horizun_manage_system_types (Pro edition) lists text note types).";
                id = hits[0].Id;
            }
            return schedule.IsValidTextTypeId(id) ? null : "text type " + Rid.Value(id) + " is not valid for a schedule.";
        }

        // =====================================================================
        // Validation against the live document (read-only)
        // =====================================================================

        /// <summary>
        /// The parts of set_appearance that need the document. A target whose layout an earlier
        /// action of this batch changes (show_title, show_headers) is validated again at apply.
        /// </summary>
        internal static string ValidateLive(Document doc, ViewSchedule schedule, JObject a, StyleSpec spec, bool deferLayout)
        {
            if (a["text_type"] != null)
            {
                ElementId ignored;
                string textError = ResolveTextType(doc, schedule, a["text_type"], out ignored);
                if (textError != null) return textError;
            }
            if (spec == null) return null;
            foreach (KeyValuePair<string, string> side in spec.Border.Sides())
            {
                ElementId ignored;
                string error = ResolveLineStyle(doc, side.Value, out ignored);
                if (error != null) return error;
            }
            if (deferLayout) return null;
            return CheckRegionExists(schedule, a);
        }

        /// <summary>The region the style targets must exist now (a title that is hidden has no cell to style).</summary>
        internal static string CheckRegionExists(ViewSchedule schedule, JObject a)
        {
            string scope = a.Value<string>("scope");
            if (scope == null || scope == ScheduleAppearanceRules.ScopeField) return null;
            ScheduleDefinition definition = schedule.Definition;
            // Before the write the request decides; after it, the definition already says the same.
            bool showTitle = a["show_title"] != null ? a.Value<bool>("show_title") : definition.ShowTitle;
            TableData table = schedule.GetTableData();
            if (scope == ScheduleAppearanceRules.ScopeBody)
                return table.GetSectionData(SectionType.Body).NumberOfRows < 1
                    ? "the schedule has no body rows to style; add elements or fields first." : null;
            if (scope == ScheduleAppearanceRules.ScopeTitle && !showTitle)
                return "the title is hidden; pass show_title=true in the same action to style it.";
            if (scope == ScheduleAppearanceRules.ScopeHeaders && !definition.ShowHeaders)
                return "the column headings are hidden (set_options headers=true shows them).";
            return null;
        }

        // =====================================================================
        // Apply
        // =====================================================================

        internal static void ApplyAppearance(Document doc, ViewSchedule schedule, JObject a, StyleSpec spec,
                                             ScheduleFieldId fieldId)
        {
            ScheduleDefinition definition = schedule.Definition;
            bool layoutChanged = false;
            if (a["show_grid"] != null) definition.ShowGridLines = a.Value<bool>("show_grid");
            if (a["show_title"] != null && definition.ShowTitle != a.Value<bool>("show_title"))
            { definition.ShowTitle = a.Value<bool>("show_title"); layoutChanged = true; }

            string scope = a.Value<string>("scope");
            if (a["text_type"] != null)
            {
                ElementId typeId;
                string error = ResolveTextType(doc, schedule, a["text_type"], out typeId);
                if (error != null) throw new InvalidOperationException(error);
                if (scope == ScheduleAppearanceRules.ScopeTitle) schedule.TitleTextTypeId = typeId;
                else if (scope == ScheduleAppearanceRules.ScopeHeaders) schedule.HeaderTextTypeId = typeId;
                else schedule.BodyTextTypeId = typeId;
            }
            if (spec == null) return;
            if (layoutChanged) doc.Regenerate();
            string regionError = CheckRegionExists(schedule, a);
            if (regionError != null) throw new InvalidOperationException(regionError);

            if (scope == ScheduleAppearanceRules.ScopeField)
            {
                ScheduleField field = definition.GetField(fieldId);
                field.SetStyle(BuildStyle(doc, spec, field.GetStyle()));
                return;
            }
            TableData table = schedule.GetTableData();
            if (scope == ScheduleAppearanceRules.ScopeBody)
            {
                // A section style: only the flagged attributes override, so the rest of the
                // body keeps whatever it had.
                table.GetSectionData(SectionType.Body).SetCellStyle(BuildStyle(doc, spec, null));
                return;
            }
            TableSectionData header = table.GetSectionData(SectionType.Header);
            foreach (int row in RegionRows(schedule, header, scope))
                for (int col = header.FirstColumnNumber; col <= header.LastColumnNumber; col++)
                    header.SetCellStyle(row, col, BuildStyle(doc, spec, header.GetTableCellStyle(row, col)));
        }

        private static int[] RegionRows(ViewSchedule schedule, TableSectionData header, string scope)
        {
            int[] title, headings;
            ScheduleAppearanceRules.HeaderLayout(header.NumberOfRows, schedule.Definition.ShowTitle,
                                                 schedule.Definition.ShowHeaders, out title, out headings);
            return scope == ScheduleAppearanceRules.ScopeTitle ? title : headings;
        }

        /// <summary>A style that overrides exactly the attributes the spec names.</summary>
        internal static TableCellStyle BuildStyle(Document doc, StyleSpec spec, TableCellStyle seed)
        {
            TableCellStyle style = seed == null ? new TableCellStyle() : new TableCellStyle(seed);
            TableCellStyleOverrideOptions overrides = style.GetCellStyleOverrideOptions();
            if (spec.Font != null) { style.FontName = spec.Font; overrides.Font = true; }
            if (spec.SizePt != null) { style.TextSize = spec.SizePt.Value; overrides.FontSize = true; }
            if (spec.Bold != null) { style.IsFontBold = spec.Bold.Value; overrides.Bold = true; }
            if (spec.Italic != null) { style.IsFontItalic = spec.Italic.Value; overrides.Italics = true; }
            if (spec.Underline != null) { style.IsFontUnderline = spec.Underline.Value; overrides.Underline = true; }
            if (spec.Color != null) { style.TextColor = ToColor(spec.Color); overrides.FontColor = true; }
            if (spec.Fill != null) { style.BackgroundColor = ToColor(spec.Fill); overrides.BackgroundColor = true; }
            if (spec.HAlign != null)
            {
                style.FontHorizontalAlignment = HAlignValue(spec.HAlign);
                overrides.HorizontalAlignment = true;
            }
            if (spec.VAlign != null)
            {
                style.FontVerticalAlignment = VAlignValue(spec.VAlign);
                overrides.VerticalAlignment = true;
            }
            foreach (KeyValuePair<string, string> side in spec.Border.Sides())
            {
                ElementId id;
                string error = ResolveLineStyle(doc, side.Value, out id);
                if (error != null) throw new InvalidOperationException(error);
                switch (side.Key)
                {
                    case "top": style.BorderTopLineStyle = id; overrides.BorderTopLineStyle = true; break;
                    case "bottom": style.BorderBottomLineStyle = id; overrides.BorderBottomLineStyle = true; break;
                    case "left": style.BorderLeftLineStyle = id; overrides.BorderLeftLineStyle = true; break;
                    default: style.BorderRightLineStyle = id; overrides.BorderRightLineStyle = true; break;
                }
            }
            style.SetCellStyleOverrideOptions(overrides);
            return style;
        }

        private static Color ToColor(string hex)
        {
            byte r, g, b;
            ScheduleAppearanceRules.TryParseColor(hex, out r, out g, out b);
            return new Color(r, g, b);
        }

        private static HorizontalAlignmentStyle HAlignValue(string name)
        {
            switch (name)
            {
                case "left": return HorizontalAlignmentStyle.Left;
                case "right": return HorizontalAlignmentStyle.Right;
                default: return HorizontalAlignmentStyle.Center;
            }
        }

        private static VerticalAlignmentStyle VAlignValue(string name)
        {
            switch (name)
            {
                case "top": return VerticalAlignmentStyle.Top;
                case "bottom": return VerticalAlignmentStyle.Bottom;
                default: return VerticalAlignmentStyle.Middle;
            }
        }

        // =====================================================================
        // Re-read
        // =====================================================================

        internal static bool VerifyAppearance(Document doc, ViewSchedule schedule, JObject a, StyleSpec spec,
                                              ScheduleFieldId fieldId, out string why)
        {
            why = null;
            ScheduleDefinition definition = schedule.Definition;
            if (a["show_grid"] != null && definition.ShowGridLines != a.Value<bool>("show_grid"))
            { why = "show_grid did not stick"; return false; }
            if (a["show_title"] != null && definition.ShowTitle != a.Value<bool>("show_title"))
            { why = "show_title did not stick"; return false; }

            string scope = a.Value<string>("scope");
            if (a["text_type"] != null)
            {
                ElementId wanted;
                string error = ResolveTextType(doc, schedule, a["text_type"], out wanted);
                if (error != null) { why = error; return false; }
                ElementId got = scope == ScheduleAppearanceRules.ScopeTitle ? schedule.TitleTextTypeId
                              : scope == ScheduleAppearanceRules.ScopeHeaders ? schedule.HeaderTextTypeId
                              : schedule.BodyTextTypeId;
                if (Rid.Value(got) != Rid.Value(wanted))
                { why = "the " + scope + " text type did not stick (read back " + Rid.Value(got) + ", asked " + Rid.Value(wanted) + ")"; return false; }
            }
            if (spec == null) return true;

            try
            {
                if (scope == ScheduleAppearanceRules.ScopeField)
                    return CheckStyle(doc, schedule.Definition.GetField(fieldId).GetStyle(), spec, "column", out why);
                TableData table = schedule.GetTableData();
                if (scope == ScheduleAppearanceRules.ScopeBody)
                {
                    TableSectionData body = table.GetSectionData(SectionType.Body);
                    if (body.NumberOfRows < 1) { why = "the body has no rows to re-read"; return false; }
                    return CheckStyle(doc, body.GetTableCellStyle(body.FirstRowNumber, body.FirstColumnNumber), spec, "body", out why);
                }
                TableSectionData header = table.GetSectionData(SectionType.Header);
                int[] rows = RegionRows(schedule, header, scope);
                if (rows.Length == 0) { why = "the " + scope + " region has no rows to re-read"; return false; }
                foreach (int row in rows)
                    for (int col = header.FirstColumnNumber; col <= header.LastColumnNumber; col++)
                        if (!CheckStyle(doc, header.GetTableCellStyle(row, col), spec, scope + " cell " + row + "," + col, out why))
                            return false;
                return true;
            }
            catch (Exception ex) { why = "the style could not be re-read: " + ex.Message; return false; }
        }

        /// <summary>Compare only the attributes the spec named.</summary>
        internal static bool CheckStyle(Document doc, TableCellStyle got, StyleSpec spec, string place, out string why)
        {
            why = null;
            if (spec.Font != null && !string.Equals(got.FontName, spec.Font, StringComparison.Ordinal))
            { why = place + ": the font did not stick (read back '" + got.FontName + "')"; return false; }
            if (spec.SizePt != null && Math.Abs(got.TextSize - spec.SizePt.Value) > 1e-6)
            { why = place + ": the text size did not stick (read back " + got.TextSize.ToString(CultureInfo.InvariantCulture) + ")"; return false; }
            if (spec.Bold != null && got.IsFontBold != spec.Bold.Value) { why = place + ": bold did not stick"; return false; }
            if (spec.Italic != null && got.IsFontItalic != spec.Italic.Value) { why = place + ": italic did not stick"; return false; }
            if (spec.Underline != null && got.IsFontUnderline != spec.Underline.Value) { why = place + ": underline did not stick"; return false; }
            if (spec.Color != null && !SameColor(got.TextColor, spec.Color)) { why = place + ": the text colour did not stick"; return false; }
            if (spec.Fill != null && !SameColor(got.BackgroundColor, spec.Fill)) { why = place + ": the fill did not stick"; return false; }
            if (spec.HAlign != null && got.FontHorizontalAlignment != HAlignValue(spec.HAlign)) { why = place + ": the horizontal alignment did not stick"; return false; }
            if (spec.VAlign != null && got.FontVerticalAlignment != VAlignValue(spec.VAlign)) { why = place + ": the vertical alignment did not stick"; return false; }
            foreach (KeyValuePair<string, string> side in spec.Border.Sides())
            {
                ElementId wanted;
                string error = ResolveLineStyle(doc, side.Value, out wanted);
                if (error != null) { why = error; return false; }
                ElementId read = side.Key == "top" ? got.BorderTopLineStyle
                               : side.Key == "bottom" ? got.BorderBottomLineStyle
                               : side.Key == "left" ? got.BorderLeftLineStyle : got.BorderRightLineStyle;
                if (Rid.Value(read) != Rid.Value(wanted))
                { why = place + ": the " + side.Key + " border did not stick (read back " + Rid.Value(read) + ")"; return false; }
            }
            return true;
        }

        private static bool SameColor(Color got, string hex)
        {
            if (got == null || !got.IsValid) return false;
            byte r, g, b;
            ScheduleAppearanceRules.TryParseColor(hex, out r, out g, out b);
            return got.Red == r && got.Green == g && got.Blue == b;
        }

        // =====================================================================
        // The canonical "appearance" section (the token binds it; the diff names it)
        // =====================================================================

        internal static List<string> CanonicalLines(ViewSchedule schedule)
        {
            ScheduleDefinition definition = schedule.Definition;
            int segments = 1;
            try { segments = schedule.GetSegmentCount(); } catch { }
            var styles = new List<string>();
            try
            {
                styles.Add("text_type.title=" + Rid.Value(schedule.TitleTextTypeId));
                styles.Add("text_type.headers=" + Rid.Value(schedule.HeaderTextTypeId));
                styles.Add("text_type.body=" + Rid.Value(schedule.BodyTextTypeId));
            }
            catch { styles.Add("text_type=(unreadable)"); }
            try
            {
                TableData table = schedule.GetTableData();
                TableSectionData header = table.GetSectionData(SectionType.Header);
                int[] title, headings;
                ScheduleAppearanceRules.HeaderLayout(header.NumberOfRows, definition.ShowTitle, definition.ShowHeaders,
                                                     out title, out headings);
                if (title.Length > 0)
                    styles.Add("style.title=" + Signature(header.GetTableCellStyle(title[0], header.FirstColumnNumber)));
                if (headings.Length > 0)
                    styles.Add("style.headers=" + Signature(header.GetTableCellStyle(headings[0], header.FirstColumnNumber)));
                TableSectionData body = table.GetSectionData(SectionType.Body);
                if (body.NumberOfRows > 0)
                    styles.Add("style.body=" + Signature(body.GetTableCellStyle(body.FirstRowNumber, body.FirstColumnNumber)));
            }
            catch { styles.Add("style=(unreadable)"); }
            try
            {
                IList<ScheduleFieldId> order = definition.GetFieldOrder();
                for (int i = 0; i < order.Count; i++)
                    styles.Add("style.field[" + i + "]=" + Signature(definition.GetField(order[i]).GetStyle()));
            }
            catch { styles.Add("style.fields=(unreadable)"); }
            return ScheduleAppearanceRules.CanonicalLines(definition.ShowGridLines, definition.ShowTitle, segments, styles);
        }

        private static string Signature(TableCellStyle s)
        {
            return string.Join("|", new[]
            {
                s.FontName, s.TextSize.ToString("0.###", CultureInfo.InvariantCulture),
                s.IsFontBold ? "b" : "-", s.IsFontItalic ? "i" : "-", s.IsFontUnderline ? "u" : "-",
                Hex(s.TextColor), Hex(s.BackgroundColor), s.FontHorizontalAlignment.ToString(), s.FontVerticalAlignment.ToString(),
                Rid.Value(s.BorderTopLineStyle).ToString(CultureInfo.InvariantCulture),
                Rid.Value(s.BorderBottomLineStyle).ToString(CultureInfo.InvariantCulture),
                Rid.Value(s.BorderLeftLineStyle).ToString(CultureInfo.InvariantCulture),
                Rid.Value(s.BorderRightLineStyle).ToString(CultureInfo.InvariantCulture)
            });
        }

        private static string Hex(Color c)
        {
            try { return c != null && c.IsValid ? ScheduleAppearanceRules.Hex(c.Red, c.Green, c.Blue) : "-"; }
            catch { return "-"; }
        }

        // =====================================================================
        // split / unsplit
        // =====================================================================

        internal static string ValidateSplitLive(ViewSchedule schedule, string op)
        {
            if (schedule.IsTitleblockRevisionSchedule) return "a title block revision schedule cannot be split.";
            bool split = schedule.IsSplit();
            if (op == ScheduleAppearanceRules.OpSplit && split)
                return "the schedule is already split into " + schedule.GetSegmentCount() + " segments; unsplit it first " +
                       "(Revit splits only a schedule that is not split yet).";
            if (op == ScheduleAppearanceRules.OpUnsplit && !split) return "the schedule is not split.";
            return null;
        }

        internal static void ApplySplit(ViewSchedule schedule, JObject a)
        {
            int count; List<double> heightsMm;
            string error = ScheduleAppearanceRules.ValidateSplit(a, out count, out heightsMm);
            if (error != null) throw new ArgumentException(error);
            if (heightsMm != null) schedule.Split(heightsMm.Select(h => h * FeetPerMm).ToList());
            else schedule.Split(count);
        }

        /// <summary>
        /// Merge from the last segment backwards until one is left. Merging deletes the merged
        /// segment's instances on sheets: the rehearsal states how many there are.
        /// </summary>
        internal static void ApplyUnsplit(ViewSchedule schedule)
        {
            while (schedule.GetSegmentCount() > 1)
            {
                int last = schedule.GetSegmentCount() - 1;
                schedule.MergeSegments(last, last - 1);
            }
        }

        /// <summary>How many placed instances the merge of every segment but the first would remove.</summary>
        internal static int InstancesThatUnsplitRemoves(Document doc, ViewSchedule schedule)
        {
            int removed = 0;
            foreach (ScheduleSheetInstance instance in new FilteredElementCollector(doc).OfClass(typeof(ScheduleSheetInstance))
                         .Cast<ScheduleSheetInstance>())
                try
                {
                    if (Rid.Value(instance.ScheduleId) == Rid.Value(schedule.Id) && instance.SegmentIndex > 0) removed++;
                }
                catch { }
            return removed;
        }

        internal static bool VerifySplit(Document doc, ViewSchedule schedule, JObject a, string op, out string why)
        {
            why = null;
            int count = schedule.GetSegmentCount();
            if (op == ScheduleAppearanceRules.OpUnsplit)
            {
                if (count != 1 || schedule.IsSplit()) { why = "the schedule still has " + count + " segments"; return false; }
                return true;
            }
            int wanted; List<double> heightsMm;
            string error = ScheduleAppearanceRules.ValidateSplit(a, out wanted, out heightsMm);
            if (error != null) { why = error; return false; }
            if (count != wanted || !schedule.IsSplit())
            { why = "the schedule has " + count + " segments, asked for " + wanted; return false; }
            if (heightsMm != null)
            {
                var got = new List<double>();
                for (int i = 0; i < heightsMm.Count; i++) got.Add(schedule.GetSegmentHeight(i) / FeetPerMm);
                // GetSegmentHeight = header + body; Split took the body. Revit may snap to a row boundary:
                // that is reported, not hidden.
                if (!ScheduleAppearanceRules.SegmentHeightsMatch(got, heightsMm, 0.5, out _, out why)) return false;
            }
            return true;
        }
    }
}
