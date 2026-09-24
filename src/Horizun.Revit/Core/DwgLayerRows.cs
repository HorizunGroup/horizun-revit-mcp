// -----------------------------------------------------------------------------
// Horizun Revit MCP - which row of a DWG export layer table a request names.
// Original Horizun code. Pure: no Revit types, so the rule is tested without Revit.
//
// MEASURED (Revit 2026, live matrix 2026-09-24): a request for category 'Walls'
// failed with "the layer table has no row for category 'Walls'" - the table was
// empty, and when it is not, a category has SEVERAL rows: Walls exists once per
// SpecialType (Default, ExteriorWall, InteriorWall, FoundationWall,
// RetainingWall). The old match took the first row whose names agreed, i.e. an
// arbitrary one of five. So:
//
//   * a row is identified by (category, subcategory, special) - all three;
//   * a category is named by its BuiltInCategory token (OST_Walls) or by the name
//     Revit shows, which follows Revit's LANGUAGE (a Spanish 2023 says 'Muros').
//     The caller resolves the token to this document's name and passes both
//     candidates; either may match;
//   * with no special given, the Default row is chosen when there is one; any
//     other plurality is refused by naming the specials that exist;
//   * an empty table is its own refusal: it is not "no such category".
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Horizun.Revit.Core
{
    /// <summary>One row of an export layer table, as plain text.</summary>
    public sealed class DwgLayerRow
    {
        public string Category = "";
        public string SubCategory = "";
        public string Special = "Default";
        public string Layer = "";
        public int Color;
        public string CutLayer = "";
        public int CutColor;

        /// <summary>How replies name the row: "Walls", "Walls / Common Edges", "Walls [ExteriorWall]".</summary>
        public string Label =>
            Category + (string.IsNullOrEmpty(SubCategory) ? "" : " / " + SubCategory) +
            (DwgLayerRows.IsDefaultSpecial(Special) ? "" : " [" + Special + "]");

        public string ValueText => DwgLayerRows.RowText(Layer, Color, CutLayer, CutColor);
    }

    public static class DwgLayerRows
    {
        public static string RowText(string layer, int color, string cutLayer, int cutColor) =>
            "layer=" + layer + ";color=" + color.ToString(CultureInfo.InvariantCulture) +
            ";cut_layer=" + cutLayer + ";cut_color=" + cutColor.ToString(CultureInfo.InvariantCulture);

        public static bool IsDefaultSpecial(string special) =>
            string.IsNullOrEmpty(special) || string.Equals(special, "Default", StringComparison.OrdinalIgnoreCase);

        /// <summary>True when the row has exactly this key (names ignore case; special defaults to Default).</summary>
        public static bool SameKey(DwgLayerRow row, string category, string subCategory, string special)
        {
            if (row == null) return false;
            return string.Equals(row.Category ?? "", category ?? "", StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(row.SubCategory ?? "", subCategory ?? "", StringComparison.OrdinalIgnoreCase) &&
                   (IsDefaultSpecial(row.Special) ? IsDefaultSpecial(special)
                                                  : string.Equals(row.Special, special, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// The index of the one row the request names, or -1 with <paramref name="error"/> saying why.
        /// <paramref name="categoryNames"/> are the acceptable spellings (the document's name for a
        /// BuiltInCategory token, and the text as given); <paramref name="special"/> null means
        /// "the Default row if there is one".
        /// </summary>
        public static int Find(IList<DwgLayerRow> rows, IList<string> categoryNames, string subCategory, string special, out string error)
        {
            error = null;
            string shown = categoryNames == null || categoryNames.Count == 0 ? "" : categoryNames[0];
            if (rows == null || rows.Count == 0)
            {
                error = "the layer table of this setup is EMPTY, so no row for category '" + shown + "' can be edited. " +
                        "Seed a new setup with dwg_setup.source (an existing setup) or dwg_setup.layer_standard.";
                return -1;
            }
            var names = new HashSet<string>((categoryNames ?? new string[0]).Where(n => !string.IsNullOrWhiteSpace(n)),
                                            StringComparer.OrdinalIgnoreCase);
            string sub = subCategory ?? "";
            var hits = new List<int>();
            for (int i = 0; i < rows.Count; i++)
                if (names.Contains(rows[i].Category ?? "") &&
                    string.Equals(rows[i].SubCategory ?? "", sub, StringComparison.OrdinalIgnoreCase))
                    hits.Add(i);

            string what = "category '" + string.Join("' / '", names) + "'" + (sub.Length > 0 ? " / subcategory '" + sub + "'" : "");
            if (hits.Count == 0)
            {
                error = "the layer table (" + rows.Count + " rows) has no row for " + what + ". Rows are matched by the " +
                        "name Revit gives them, which follows Revit's language; name the category by its BuiltInCategory " +
                        "token (for example OST_Walls) to be language-independent, or read the table first with no layers.";
                return -1;
            }
            if (!string.IsNullOrEmpty(special))
            {
                List<int> exact = hits.Where(i => IsDefaultSpecial(special) ? IsDefaultSpecial(rows[i].Special)
                                                  : string.Equals(rows[i].Special, special, StringComparison.OrdinalIgnoreCase)).ToList();
                if (exact.Count == 1) return exact[0];
                error = exact.Count == 0
                    ? "the layer table has " + what + " only with special " + Specials(rows, hits) + ", not '" + special + "'."
                    : "the layer table holds " + exact.Count + " identical rows for " + what + " [" + special + "]; refusing to pick one.";
                return -1;
            }
            if (hits.Count == 1) return hits[0];
            List<int> defaults = hits.Where(i => IsDefaultSpecial(rows[i].Special)).ToList();
            if (defaults.Count == 1) return defaults[0];
            error = "the layer table holds " + hits.Count + " rows for " + what + " (special " + Specials(rows, hits) +
                    ") and none is the single Default row; name one with special.";
            return -1;
        }

        private static string Specials(IList<DwgLayerRow> rows, List<int> hits) =>
            string.Join(", ", hits.Select(i => string.IsNullOrEmpty(rows[i].Special) ? "Default" : rows[i].Special).Distinct());
    }
}
