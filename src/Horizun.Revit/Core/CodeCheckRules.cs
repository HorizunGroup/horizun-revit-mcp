// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// CODE CHECKS AS DATA - the Revit-free half of horizun_code_check.
//
// The requirement set (docs/requirement-set.md, Core/RequirementSet.cs) already
// says WHICH elements and WHAT must be true of a parameter. A building code asks
// about GEOMETRY as well - how steep a ramp runs, how wide a door opens, whether
// two risers and a tread add up - so an assertion may name a MEASURE instead of a
// parameter. The bridge computes measures; it still knows no code. Every number a
// norm contributes arrives in the set, with the norm and the numeral it came from.
//
// FOUR OUTCOMES, NEVER THREE. passes, fails, unreadable - and not_decidable, which
// is what the model cannot answer: a clear width when only the leaf width is known
// and the leaf is wide enough, an illuminance nobody computed, a travel distance
// this tool does not route, a threshold the set itself marks unverified_value. A
// not_decidable is never a pass, and a rule that examined nothing is not_decidable.
//
// BOUNDS. A measure is exact, or a bound: the nominal leaf width is an UPPER bound
// of the clear width (clear <= leaf). Below a minimum, an upper bound FAILS for
// certain; above it, nothing is decided. That asymmetry is the whole reason bounds
// exist here - a nominal number reported as the clear width would be a pass nobody
// measured.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    /// <summary>One measured number and how good it is.</summary>
    public sealed class MeasuredValue
    {
        public double? Value;
        /// <summary>exact | upper (true value &lt;= Value) | lower (true value &gt;= Value).</summary>
        public string Bound = "exact";
        /// <summary>Why there is no value. Non-null makes every assertion on it not_decidable.</summary>
        public string Unavailable;
        public string Basis;
        /// <summary>Supporting numbers (e.g. occupant load and required exits).</summary>
        public JObject Detail;

        public static MeasuredValue Exact(double v, string basis) => new MeasuredValue { Value = v, Basis = basis };
        public static MeasuredValue None(string why) => new MeasuredValue { Unavailable = why };
    }

    /// <summary>A parameter as read: absent, blank, unreadable, or a value (Number converted to the rule's unit).</summary>
    public sealed class ParamFact
    {
        public bool Exists;
        public bool Unreadable;
        public string Text;
        public double? Number;
    }

    /// <summary>What the Revit side read about one element - nothing here reads Revit.</summary>
    public sealed class CheckedElement
    {
        public long Id;
        /// <summary>BuiltInCategory name (OST_Doors) - language-independent.</summary>
        public string CategoryToken;
        /// <summary>The category's display name in the session language.</summary>
        public string CategoryName;
        public string TypeName;
        public string Name;
        public string Level;
        /// <summary>Keyed by parameter name, or "name|unit" when a unit conversion was requested.</summary>
        public Dictionary<string, ParamFact> Params = new Dictionary<string, ParamFact>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, MeasuredValue> Measures = new Dictionary<string, MeasuredValue>(StringComparer.Ordinal);
        /// <summary>Levels only: placed rooms and doors on this level, for exit_count_minus_required.</summary>
        public List<CheckedElement> Rooms;
        public List<CheckedElement> Doors;
        /// <summary>Rooms only: area in m2 (0 = unplaced).</summary>
        public double AreaM2;
        /// <summary>Doors only: the Mark.</summary>
        public string Mark;

        public static string ParamKey(string name, string unit) => unit == null ? name : name + "|" + unit;
    }

    public static partial class CodeCheckRules
    {
        /// <summary>
        /// THE measure list: name -> what it is. Only these may appear in an assertion
        /// or a measure_range; the loader refuses any other name.
        /// </summary>
        public static readonly IReadOnlyDictionary<string, string> Measures = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { "door_clear_width_mm", "UPPER BOUND: the nominal leaf width (Width parameter). Clear width is smaller by the stop and leaf." },
            { "ramp_slope_percent", "steepest upward sloped face of the ramp's own geometry, rise/run x 100." },
            { "ramp_run_length_mm", "longest horizontal run of one sloped face along its gradient (one flight)." },
            { "ramp_width_mm", "narrowest sloped face measured across its gradient." },
            { "ramp_landing_length_mm", "shortest flat top face measured along the adjacent flight's gradient; absent when no flat face exists." },
            { "stair_riser_mm", "Revit's actual riser height of the stair." },
            { "stair_tread_mm", "Revit's actual tread depth of the stair." },
            { "stair_2r_plus_t_mm", "2 x actual riser + actual tread." },
            { "stair_run_width_mm", "narrowest run's actual run width; handrails not deducted." },
            { "space_illuminance_lx", "the Space's Average Estimated Illumination as Revit computed it; 0 or empty = not computed." },
            { "exit_count_minus_required", "per level: doors matching config.exit_door minus the exits config.required_exits asks for the level's occupant load." },
            { "travel_distance_m", "NOT computed by this tool (always not_decidable); horizun_audit_access with route_view_id routes a real path." }
        };

        public static IEnumerable<string> MeasureNames => Measures.Keys;
        public static bool IsKnownMeasure(string name) => name != null && Measures.ContainsKey(name);

        public const string Disclaimer =
            "A requirement set is not a code, and this is not a compliance assessment: it compares measurements " +
            "of the model with thresholds the set declares, citing each rule's source. not_decidable and " +
            "unreadable are not passes. Verify the edition and applicability of every cited norm.";

        /// <summary>Does the rule's selector pick this element? Pure; measure_range is handled by the evaluator.</summary>
        public static bool Selects(Requirement rule, CheckedElement e)
        {
            if (rule.SelectorCategory != null &&
                !string.Equals(rule.SelectorCategory, e.CategoryToken, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(rule.SelectorCategory, e.CategoryName, StringComparison.OrdinalIgnoreCase))
                return false;
            if (rule.SelectorTypeNameMatches != null &&
                (e.TypeName == null || !Regex.IsMatch(e.TypeName, rule.SelectorTypeNameMatches)))
                return false;
            if (rule.SelectorNameMatches != null &&
                (e.Name == null || !Regex.IsMatch(e.Name, rule.SelectorNameMatches)))
                return false;
            if (rule.SelectorParameterExists != null &&
                !(e.Params.TryGetValue(rule.SelectorParameterExists, out ParamFact pe) && pe.Exists))
                return false;
            if (rule.SelectorParameterEqualsName != null)
            {
                if (!e.Params.TryGetValue(rule.SelectorParameterEqualsName, out ParamFact p) || !p.Exists ||
                    !string.Equals((p.Text ?? "").Trim(), rule.SelectorParameterEqualsValue.Trim(), StringComparison.OrdinalIgnoreCase))
                    return false;
            }
            return true;
        }

        /// <summary>
        /// Compare a measured value with the rule. Returns passes | fails | not_decidable and
        /// the reason for the last. Bounds decide only the side they bound.
        /// </summary>
        public static string CompareMeasured(Requirement rule, MeasuredValue m, out string reason)
        {
            reason = null;
            if (m == null || m.Unavailable != null || m.Value == null)
            {
                reason = m?.Unavailable ?? "the model did not yield this measurement.";
                return "not_decidable";
            }
            double v = m.Value.Value;
            if (m.Bound == "exact") return RequirementSet.CompareNumber(rule, v) ? "passes" : "fails";

            // The two ends of the operator's acceptable range.
            double lo = double.NegativeInfinity, hi = double.PositiveInfinity;
            bool loOpen = false, hiOpen = false;
            switch (rule.Operator)
            {
                case "gte": lo = rule.Value.Value<double>(); break;
                case "gt": lo = rule.Value.Value<double>(); loOpen = true; break;
                case "lte": hi = rule.Value.Value<double>(); break;
                case "lt": hi = rule.Value.Value<double>(); hiOpen = true; break;
                case "between": lo = ((JArray)rule.Value)[0].Value<double>(); hi = ((JArray)rule.Value)[1].Value<double>(); break;
            }
            bool belowLo(double x) => loOpen ? x <= lo : x < lo;
            bool aboveHi(double x) => hiOpen ? x >= hi : x > hi;

            if (m.Bound == "upper")
            {
                // true <= v. Certain fail when even v is below the minimum.
                if (belowLo(v)) return "fails";
                if (double.IsNegativeInfinity(lo) && !aboveHi(v)) return "passes";
                reason = "the measure is an UPPER BOUND (" + Fmt(v) + "); the true value may be smaller. " + (m.Basis ?? "");
                return "not_decidable";
            }
            // lower: true >= v. Certain fail when even v is above the maximum.
            if (aboveHi(v)) return "fails";
            if (double.IsPositiveInfinity(hi) && !belowLo(v)) return "passes";
            reason = "the measure is a LOWER BOUND (" + Fmt(v) + "); the true value may be larger. " + (m.Basis ?? "");
            return "not_decidable";
        }

        internal static string Fmt(double v) => Math.Round(v, 2).ToString(CultureInfo.InvariantCulture);
    }
}
