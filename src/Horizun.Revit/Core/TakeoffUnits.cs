// -----------------------------------------------------------------------------
// Horizun Core - original Horizun code. Revit-free.
//
// A TAKEOFF NUMBER IS IN THE UNIT IT DECLARES, OR IT IS NOT A NUMBER AT ALL.
//
// horizun_quantities converted a Double parameter only when its spec was exactly
// Length, Area or Volume. Every other measurable spec - a bar length, a bar
// diameter, a mass, a force - went out in Revit's internal units (feet, kg...)
// labelled with whatever unit the takeoff declared (audit 2026-10-08, 6.3). The
// declared unit is now resolved to a Revit unit and the value converted to it;
// a unit this table does not know, or one Revit says does not fit the
// parameter's spec, is refused rather than labelled.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;

namespace Horizun.Revit.Core
{
    public static class TakeoffUnits
    {
        // normalised spelling (lower case, no spaces, ^ dropped, ² ³ as digits) -> canonical unit name.
        private static readonly Dictionary<string, string> Canonical = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["m"] = "meters", ["mm"] = "millimeters", ["cm"] = "centimeters", ["ft"] = "feet", ["in"] = "inches",
            ["m2"] = "square_meters", ["ft2"] = "square_feet",
            ["m3"] = "cubic_meters", ["ft3"] = "cubic_feet", ["l"] = "liters",
            ["kg"] = "kilograms", ["t"] = "tonnes",
            ["n"] = "newtons", ["kn"] = "kilonewtons",
            ["kg/m"] = "kilograms_per_meter",
        };

        /// <summary>
        /// The canonical unit a normalised takeoff unit names, or null when the table
        /// does not know it. Null means "refuse": the bridge does not guess a unit.
        /// </summary>
        public static string CanonicalOf(string normalised)
        {
            string c;
            return normalised != null && Canonical.TryGetValue(normalised, out c) ? c : null;
        }

        /// <summary>The spellings a takeoff may declare for a converted quantity, for the refusal text.</summary>
        public static string Known() => string.Join(", ", Canonical.Keys);

        /// <summary>
        /// Why a measurable Double that is not Length/Area/Volume cannot be reported in the
        /// declared unit, or null when it can (<paramref name="canonical"/> known and
        /// <paramref name="validForSpec"/> true).
        /// </summary>
        public static string RefusalFor(string parameter, string declared, string canonical, bool validForSpec, string specId)
        {
            if (canonical == null)
                return "parameter '" + parameter + "' is a measured quantity (Revit spec " + (specId ?? "?") +
                       ") stored in Revit's internal units, and the declared unit '" + declared +
                       "' is not one this takeoff can convert to (" + Known() + "). Nothing is reported " +
                       "rather than an internal-unit number under the declared label.";
            if (!validForSpec)
                return "parameter '" + parameter + "' is a measured quantity (Revit spec " + (specId ?? "?") +
                       ") and Revit says the declared unit '" + declared + "' does not measure it. Declare a unit of that kind.";
            return null;
        }
    }
}
