// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// A LENGTH WRITTEN IN THE WRONG UNIT, ANNOUNCED BY NOTHING (field cycle, 2026-10-21).
//
// MEASURED: horizun_manage_system_types (Pro edition) values {b: 152.4, h: ...} with units=mm made
// beams 152.4 of something LARGE - "vigas de 397 m de profundidad" - and the rehearsal
// said nothing. A bare number in `values` is Revit's raw storage (decimal feet), not
// the request's units (those apply to compound layer widths); the schema said so in a
// sentence few read. The dry run now shows, for every number written to a length
// parameter, what it means in feet and millimetres, and warns when that is not the size
// of a building part. A string such as "152.4 mm" is parsed by Revit in the document's
// units and needs no such warning.
//
// Revit-free: the arithmetic and the wording.
// -----------------------------------------------------------------------------
using System.Globalization;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public static class LengthWriteRules
    {
        public const double FeetToMm = 304.8;
        /// <summary>Above this a type dimension is almost certainly a unit mistake (30 m).</summary>
        public const double SuspectMm = 30000.0;
        /// <summary>Below this a nonzero type dimension is almost certainly a unit mistake too (0.1 mm).</summary>
        public const double TinyMm = 0.1;

        /// <summary>A request parsed before Set; unlike a read of the stored parameter this is an independent expectation.</summary>
        public static JObject ConvertedExpectation(string requestedText, double convertedInternalUnits) => new JObject
        {
            ["readable"] = true,
            ["storage"] = "Double",
            ["value"] = convertedInternalUnits,
            ["requested_text"] = requestedText,
            ["expectation_source"] = "UnitFormatUtils.TryParse of the request before Set(double), in Revit internal units."
        };

        /// <summary>What a bare number written to a length parameter means: it is feet.</summary>
        public static JObject Interpret(string parameter, double rawFeet)
        {
            double mm = rawFeet * FeetToMm;
            var o = new JObject
            {
                ["parameter"] = parameter,
                ["number_is_raw_feet"] = rawFeet,
                ["equals_mm"] = System.Math.Round(mm, 3),
                ["equals_m"] = System.Math.Round(mm / 1000.0, 4)
            };
            string warning = MagnitudeWarning(parameter, rawFeet);
            if (warning != null) o["warning"] = warning;
            return o;
        }

        /// <summary>Null when the size is plausible for a building part; otherwise the sentence that names the likely mistake.</summary>
        public static string MagnitudeWarning(string parameter, double rawFeet)
        {
            double mm = System.Math.Abs(rawFeet) * FeetToMm;
            if (mm > SuspectMm)
                return "the number " + rawFeet.ToString("0.###", CultureInfo.InvariantCulture) + " written to '" + parameter +
                       "' is read as FEET = " + (mm / 1000.0).ToString("0.##", CultureInfo.InvariantCulture) +
                       " m. If you meant " + rawFeet.ToString("0.###", CultureInfo.InvariantCulture) + " mm, send the string \"" +
                       rawFeet.ToString("0.###", CultureInfo.InvariantCulture) + " mm\" or the number " +
                       (rawFeet / FeetToMm).ToString("0.########", CultureInfo.InvariantCulture) + " (feet).";
            if (mm > 0 && mm < TinyMm)
                return "the number " + rawFeet.ToString("0.#########", CultureInfo.InvariantCulture) + " written to '" + parameter +
                       "' is read as FEET = " + mm.ToString("0.####", CultureInfo.InvariantCulture) +
                       " mm. If you meant metres or millimetres, send a string such as \"0.5 m\" or \"152.4 mm\".";
            return null;
        }
    }
}
