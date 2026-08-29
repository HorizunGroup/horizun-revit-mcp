// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// Tabular import arithmetic without Revit: parsing the CSV, validating the
// mapping, and deciding per ROW what happens - written, skipped as unchanged,
// or refused with the row number and the reason. A spreadsheet is somebody's
// source of truth; a row that silently vanished between the file and the model
// is the failure this file exists to prevent.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Horizun.Revit.Core
{
    public sealed class TabularMapping
    {
        public int KeyIndex;
        /// <summary>column index -> parameter name, in declared column order.</summary>
        public List<KeyValuePair<int, string>> ValueColumns = new List<KeyValuePair<int, string>>();
    }

    public static class TabularRules
    {
        public const string CodeMissingColumn = "column_not_in_header";
        public const string CodeDuplicateKeyInFile = "duplicate_key_in_file";
        public const string CodeEmptyKey = "empty_key";
        public const string CodeUnmatchedElement = "no_element_carries_this_key";
        public const string CodeAmbiguousKey = "key_matches_more_than_one_element";
        public const string CodeSkippedUnchanged = "skipped_unchanged";

        /// <summary>
        /// RFC-4180-shaped CSV: quoted cells may hold commas, quotes (doubled) and
        /// newlines; rows end at CR, LF or CRLF outside quotes. Returns every row
        /// including empties the file really contains; the caller decides what a
        /// blank row means.
        /// </summary>
        public static List<string[]> ParseCsv(string text)
        {
            var rows = new List<string[]>();
            if (text == null) return rows;
            var row = new List<string>();
            var cell = new StringBuilder();
            bool quoted = false, cellStarted = false, rowStarted = false;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (quoted)
                {
                    if (c == '"')
                    {
                        if (i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                        else quoted = false;
                    }
                    else cell.Append(c);
                    continue;
                }
                switch (c)
                {
                    case '"':
                        quoted = true; cellStarted = true; rowStarted = true;
                        break;
                    case ',':
                        row.Add(cell.ToString()); cell.Clear(); cellStarted = false; rowStarted = true;
                        break;
                    case '\r':
                        if (i + 1 < text.Length && text[i + 1] == '\n') i++;
                        goto case '\n';
                    case '\n':
                        if (rowStarted || cellStarted || cell.Length > 0 || row.Count > 0)
                        {
                            row.Add(cell.ToString()); cell.Clear();
                            rows.Add(row.ToArray()); row.Clear();
                        }
                        cellStarted = false; rowStarted = false;
                        break;
                    default:
                        cell.Append(c); cellStarted = true; rowStarted = true;
                        break;
                }
            }
            if (rowStarted || cell.Length > 0 || row.Count > 0)
            {
                row.Add(cell.ToString());
                rows.Add(row.ToArray());
            }
            return rows;
        }

        /// <summary>
        /// Resolve the declared columns against the header row. Every missing column
        /// is named at once - a mapping fixed one refusal at a time is a bad afternoon.
        /// Header matching is exact and case-sensitive: a column is a NAME, not a hint.
        /// </summary>
        public static string MapColumns(string[] header, string keyColumn,
                                        IEnumerable<KeyValuePair<string, string>> valueColumns,
                                        out TabularMapping mapping)
        {
            mapping = new TabularMapping();
            var missing = new List<string>();
            int keyIndex = Array.IndexOf(header ?? new string[0], keyColumn);
            if (keyIndex < 0) missing.Add(keyColumn);
            mapping.KeyIndex = keyIndex;
            foreach (KeyValuePair<string, string> pair in valueColumns ?? new List<KeyValuePair<string, string>>())
            {
                int index = Array.IndexOf(header ?? new string[0], pair.Key);
                if (index < 0) { missing.Add(pair.Key); continue; }
                mapping.ValueColumns.Add(new KeyValuePair<int, string>(index, pair.Value));
            }
            if (missing.Count > 0)
                return CodeMissingColumn + ": the header does not contain " +
                       string.Join(", ", missing.ConvertAll(m => "'" + m + "'")) +
                       ". Header as read: [" + string.Join(", ", header ?? new string[0]) + "]. " +
                       "Column names match exactly, including case.";
            if (mapping.ValueColumns.Count == 0)
                return "value_columns must map at least one column to a parameter.";
            return null;
        }

        /// <summary>
        /// Duplicate keys in the FILE, found up front: two rows steering one element
        /// is a contradiction the file's author must resolve, and last-row-wins is a
        /// silent coin toss. Row numbers are 1-based over the whole file (header = 1).
        /// </summary>
        public static Dictionary<string, List<int>> DuplicateKeys(List<string[]> dataRows, int keyIndex, int firstRowNumber)
        {
            var byKey = new Dictionary<string, List<int>>(StringComparer.Ordinal);
            for (int i = 0; i < dataRows.Count; i++)
            {
                string key = keyIndex < dataRows[i].Length ? dataRows[i][keyIndex] : "";
                List<int> list;
                if (!byKey.TryGetValue(key, out list)) byKey[key] = list = new List<int>();
                list.Add(firstRowNumber + i);
            }
            var duplicates = new Dictionary<string, List<int>>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, List<int>> pair in byKey)
                if (pair.Value.Count > 1) duplicates[pair.Key] = pair.Value;
            return duplicates;
        }

        /// <summary>
        /// One cell against the model's current reading: write, or skip as unchanged.
        /// The comparison is exact string equality against what the model REPORTS -
        /// which makes the skip conservative: a formatting difference writes again,
        /// and the write path's own verification keeps that harmless.
        /// </summary>
        public static bool ShouldWrite(string fileValue, string currentReading) =>
            !string.Equals(fileValue ?? "", currentReading ?? "", StringComparison.Ordinal);

        /// <summary>
        /// Declared-locale numeric parse of one CSV cell. The separator is DECLARED,
        /// never guessed from the file: '.' parses invariant, ',' parses es-ES, and
        /// NumberStyles.Float means the OTHER mark is not silently a thousands
        /// separator - a cell carrying it simply does not parse as a number here and
        /// falls back to the exact string compare, which writes (harmlessly).
        /// </summary>
        public static bool TryParseCell(string cell, string decimalSeparator, out double value)
        {
            value = 0;
            if (string.IsNullOrWhiteSpace(cell)) return false;
            var culture = decimalSeparator == ","
                ? System.Globalization.CultureInfo.GetCultureInfo("es-ES")
                : System.Globalization.CultureInfo.InvariantCulture;
            return double.TryParse(cell, System.Globalization.NumberStyles.Float, culture, out value);
        }

        /// <summary>
        /// The unchanged rule for the numeric compare: equal within 1e-6 relative
        /// (1e-9 absolute near zero). Wide enough to absorb display rounding, far
        /// tighter than any value a model write is asked to change by.
        /// </summary>
        public static bool NumbersEqual(double a, double b) =>
            Math.Abs(a - b) <= Math.Max(1e-9, 1e-6 * Math.Max(Math.Abs(a), Math.Abs(b)));
    }
}
