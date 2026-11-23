// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// WHICH SCHEDULE COLUMNS MUST NOT BE SUMMED. Revit sums any Length, Number or Area
// field set to Calculate Totals, and in a schedule that is NOT itemized the grouped
// row's cell then shows the sum - formatted like a value. The field cycle independent
// review (2026-10-24, R4/R5) found exactly that on issued sheets: a rebar schedule
// reading "R1.1 = 350 mm" (diameters added up) and a door schedule reading 2.60 x 8.40
// (four 0.65 x 2.10 doors). Revit raises no warning.
//
// Non-additive, said once:
//   * a diameter or a size (bar diameter, pipe/duct/conduit/cable tray size, wire) -
//     adding them means nothing whatever the grouping;
//   * an angle or a slope;
//   * a LENGTH read from the element's TYPE (door width, height, a profile depth): every
//     instance of the type carries the same value, so a group of N shows N times it.
// An instance length (a wall's length, a pipe run) IS additive and is not flagged.
// -----------------------------------------------------------------------------
using System;

namespace Horizun.Revit.Core
{
    public static class ScheduleTotalsRules
    {
        /// <summary>Why a totalled field is not additive, or null when summing it is meaningful (or cannot be judged).</summary>
        public static string NonAdditiveReason(string specTypeId, string fieldType)
        {
            string s = (specTypeId ?? "").ToLowerInvariant();
            if (s.Length == 0) return null;
            if (s.Contains("diameter") || s.Contains("size"))
                return "a diameter or size: adding diameters or sizes gives no quantity";
            if (s.Contains(":angle") || s.Contains("slope"))
                return "an angle or slope: adding them gives no quantity";
            if (s.Contains(":length") && fieldType == "element_type")
                return "a length read from the element's type (e.g. door width or height): a row grouping N elements shows N times the value";
            return null;
        }

        /// <summary>The ScheduleFieldType name in lower snake case ("ElementType" -> "element_type").</summary>
        public static string FieldTypeName(string enumName)
        {
            if (string.IsNullOrEmpty(enumName)) return null;
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < enumName.Length; i++)
            {
                char c = enumName[i];
                if (char.IsUpper(c) && i > 0) sb.Append('_');
                sb.Append(char.ToLowerInvariant(c));
            }
            return sb.ToString();
        }
    }
}
