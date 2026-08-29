// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// The Revit type catalog (.txt) as arithmetic: which parameters become
// columns, how a header cell spells its unit, how a value cell survives a
// comma. A catalog row Revit cannot parse fails silently at load time in
// somebody else's session - so everything here is exact, and everything that
// cannot be a column is EXCLUDED BY NAME rather than emitted wrong.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Horizun.Revit.Core
{
    public sealed class CatalogColumn
    {
        public string Name;
        public string DataType;   // length/area/volume/angle/number/integer/yesno/text
    }

    public static class TypeCatalogRules
    {
        /// <summary>data types a catalog column can carry, and their header spelling.</summary>
        private static readonly Dictionary<string, string> HeaderTypes = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { "length", "LENGTH##MILLIMETERS" },
            { "area", "AREA##SQUARE_METERS" },
            { "volume", "VOLUME##CUBIC_METERS" },
            { "angle", "ANGLE##DEGREES" },
            { "number", "OTHER##" },
            { "integer", "OTHER##" },
            { "yesno", "OTHER##" },
            { "text", "OTHER##" }
        };

        /// <summary>
        /// Build the catalog. Parameters with a FORMULA are excluded (their values
        /// are computed, and a catalog value would silently lose to the formula);
        /// MATERIAL parameters are excluded (a catalog cell cannot carry an
        /// ElementId); both exclusions are returned by name. A missing value leaves
        /// the cell empty, which Revit reads as "keep the type's current value" -
        /// stated in the caller's report, decided here.
        /// </summary>
        public static string Build(IList<CatalogColumn> parameters,
                                   IList<string> parameterNamesWithFormula, IList<string> materialParameterNames,
                                   IList<KeyValuePair<string, IDictionary<string, string>>> types,
                                   out string content, out List<string> excluded)
        {
            content = null;
            excluded = new List<string>();
            if (types == null || types.Count == 0)
                return "a type catalog of zero types is not a deliverable; give the family at least one type.";

            var columns = new List<CatalogColumn>();
            var withFormula = new HashSet<string>(parameterNamesWithFormula ?? new List<string>(), StringComparer.Ordinal);
            var materials = new HashSet<string>(materialParameterNames ?? new List<string>(), StringComparer.Ordinal);
            foreach (CatalogColumn parameter in parameters ?? new List<CatalogColumn>())
            {
                if (withFormula.Contains(parameter.Name))
                { excluded.Add(parameter.Name + " (formula-driven: a catalog value would silently lose to the formula)"); continue; }
                if (materials.Contains(parameter.Name) || parameter.DataType == "material")
                { excluded.Add(parameter.Name + " (material: a catalog cell cannot carry an ElementId)"); continue; }
                if (!HeaderTypes.ContainsKey(parameter.DataType ?? ""))
                { excluded.Add(parameter.Name + " (data type '" + parameter.DataType + "' has no catalog spelling)"); continue; }
                columns.Add(parameter);
            }
            if (columns.Count == 0)
                return "no parameter survived as a catalog column (" + excluded.Count +
                       " excluded by name); a catalog with no columns says nothing.";

            var sb = new StringBuilder();
            foreach (CatalogColumn column in columns)
                sb.Append(',').Append(Escape(column.Name + "##" + HeaderTypes[column.DataType]));
            sb.Append("\r\n");
            foreach (KeyValuePair<string, IDictionary<string, string>> type in types)
            {
                sb.Append(Escape(type.Key));
                foreach (CatalogColumn column in columns)
                {
                    sb.Append(',');
                    string value;
                    if (type.Value != null && type.Value.TryGetValue(column.Name, out value) && value != null)
                        sb.Append(Escape(value));
                }
                sb.Append("\r\n");
            }
            content = sb.ToString();
            return null;
        }

        /// <summary>The catalog value for a typed input, exact and culture-fixed.</summary>
        public static string ValueCell(string dataType, object value)
        {
            if (value == null) return "";
            switch (dataType)
            {
                case "yesno":
                    if (value is bool flag) return flag ? "1" : "0";
                    return Convert.ToString(value, CultureInfo.InvariantCulture);
                case "integer":
                    return Convert.ToInt64(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
                case "length": case "area": case "volume": case "angle": case "number":
                    return Convert.ToDouble(value, CultureInfo.InvariantCulture).ToString("0.########", CultureInfo.InvariantCulture);
                default:
                    return Convert.ToString(value, CultureInfo.InvariantCulture);
            }
        }

        public static string Escape(string cell)
        {
            if (string.IsNullOrEmpty(cell)) return "";
            bool needsQuotes = cell.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0;
            if (!needsQuotes) return cell;
            return "\"" + cell.Replace("\"", "\"\"") + "\"";
        }
    }
}
