// -----------------------------------------------------------------------------
// Horizun Core - original Horizun code. Revit-free.
//
// THE PRE-CHECK COMPARES WHAT THE IFC WILL SAY, NOT WHAT REVIT STORES.
//
// IDS values are written against the IFC file: lengths in metres, areas in m²,
// angles in radians, booleans as TRUE/FALSE. The Revit pre-check compared the
// raw stored double (feet, square feet...) and the raw Yes/No integer (1/0)
// against them, so a 3 m wall failed "<= 3.5" at 9.84 and every boolean
// requirement failed (audit 2026-10-08, 6.4). The value is now put in the unit
// IFC uses before it is compared.
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using System.Globalization;

namespace Horizun.Revit.Core
{
    public static class IdsRevitValues
    {
        /// <summary>
        /// The SI units IFC states measures in, in the order the Revit half tries them
        /// against a spec (the first Revit accepts for that spec is used). Canonical names,
        /// mapped to UnitTypeId in the Revit half.
        /// </summary>
        public static readonly IReadOnlyList<string> SiCandidates = new[]
        {
            "meters", "square_meters", "cubic_meters", "radians", "kilograms", "newtons",
            "pascals", "kilograms_per_cubic_meter", "kelvin", "seconds", "watts",
            "meters_per_second", "cubic_meters_per_second", "amperes", "volts", "lux",
        };

        /// <summary>IDS spells a boolean TRUE / FALSE (IFC .T. / .F.).</summary>
        public static string YesNo(int stored) => stored != 0 ? "TRUE" : "FALSE";

        /// <summary>A number as the IFC file writes it: invariant, round-trippable.</summary>
        public static string Number(double value) => value.ToString("R", CultureInfo.InvariantCulture);
    }
}
