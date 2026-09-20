// -----------------------------------------------------------------------------
// Horizun Revit MCP - accessibility and egress as MEASUREMENTS plus an EXTERNAL
// rule profile. Original Horizun code.
//
// G22 of the 2026-09-14 competitive inventory, and the one gap whose hardest
// requirement is a sentence rather than a feature:
//
//   "Separa hallazgos geométricos de afirmaciones de cumplimiento normativo."
//
// THIS FILE NEVER KNOWS A CODE. No ADA, no NFPA 101, no CTE DB-SUA, no NOM. The
// bridge is organisation-neutral by design and this is where that matters most,
// because the alternative is a tool that tells somebody their building complies
// with a standard whose edition, jurisdiction and exemptions it has never seen.
// A number compiled in here would be read as authority, and it would be wrong in
// some jurisdiction on some project, and the person who relied on it would be the
// one who paid for that.
//
// SO THE SPLIT IS STRUCTURAL, not a disclaimer:
//
//   A MEASUREMENT is what the model says: this opening is 810 mm of clear width,
//   this ramp runs at 8.3%, this room's nearest exit door is 34 m away in a
//   straight line. Measurements are produced with no profile at all and are true
//   of the model regardless of where it is being built.
//
//   A PROFILE is a threshold somebody supplied, WITH its own name and source, and
//   comparing a measurement to it produces a finding that says "below the
//   threshold this profile calls minimum_clear_width_mm", never "does not
//   comply". The profile's own text is echoed back so the reader can see whose
//   rule was applied.
//
//   THERE IS NO THIRD THING. Nothing here emits "compliant", "approved" or
//   "passes". The strongest word available is `within_profile`, and the reply
//   states in full that a profile is not a code and this is not an assessment.
//
// STRAIGHT-LINE IS NOT TRAVEL DISTANCE, and the distinction is reported on every
// row that carries one. Real travel distance follows a path around walls and
// furniture; a straight line through three partitions is an OPTIMISTIC number,
// and an optimistic egress number reported without that word is the most
// dangerous output this file could produce.
//
// Revit-free on purpose: thresholds, comparisons and severities are arithmetic,
// and arithmetic is provable without a building.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    /// <summary>One threshold a supplied profile declares.</summary>
    public sealed class AccessThreshold
    {
        public string Key;
        public double Value;
        public string Comparison;   // min | max
        public string Units;
        public string Note;
    }

    /// <summary>A rule profile somebody supplied. It is DATA, never compiled in.</summary>
    public sealed class AccessProfile
    {
        public string Name;
        public string Source;
        public string Jurisdiction;
        public readonly Dictionary<string, AccessThreshold> Thresholds =
            new Dictionary<string, AccessThreshold>(StringComparer.OrdinalIgnoreCase);
    }

    public static class AccessRules
    {
        /// <summary>
        /// Every threshold this evaluator knows how to APPLY. A profile may carry others;
        /// they are echoed back as `unapplied` rather than silently dropped, because a
        /// threshold somebody wrote and nothing checked is worse than one nobody wrote.
        /// </summary>
        public static readonly string[] KnownThresholds =
        {
            "minimum_clear_width_mm",
            "minimum_corridor_width_mm",
            "maximum_ramp_slope_percent",
            "maximum_stair_riser_mm",
            "minimum_stair_tread_mm",
            "minimum_stair_width_mm",
            "maximum_straight_line_to_exit_mm",
            "minimum_door_approach_mm"
        };

        /// <summary>
        /// Read a supplied profile. Every failure is a refusal with a reason: a profile
        /// this could not read must never be replaced by a default, because a default
        /// threshold is a number this file invented.
        /// </summary>
        public static AccessProfile ReadProfile(JObject json, out string error)
        {
            error = null;
            if (json == null)
            {
                error = "a rule profile is required. This bridge carries no accessibility or egress " +
                        "thresholds of its own: they depend on a jurisdiction, an edition and a building " +
                        "use that nothing here can know, and inventing one would produce a number somebody " +
                        "relies on.";
                return null;
            }

            var profile = new AccessProfile
            {
                Name = json.Value<string>("name"),
                Source = json.Value<string>("source"),
                Jurisdiction = json.Value<string>("jurisdiction")
            };
            if (string.IsNullOrWhiteSpace(profile.Name) || string.IsNullOrWhiteSpace(profile.Source))
            {
                error = "the profile needs a name AND a source. A finding that cannot say whose rule produced " +
                        "it is a finding nobody can check, argue with, or take to a reviewer.";
                return null;
            }

            JObject thresholds = json["thresholds"] as JObject;
            if (thresholds == null || !thresholds.Properties().Any())
            {
                error = "the profile declares no thresholds, so there is nothing to compare a measurement to.";
                return null;
            }

            foreach (JProperty property in thresholds.Properties())
            {
                JObject row = property.Value as JObject;
                if (row == null)
                {
                    error = "threshold '" + property.Name + "' is not an object.";
                    return null;
                }
                double value;
                JToken valueToken = row["value"];
                if (valueToken == null ||
                    !double.TryParse(valueToken.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                {
                    error = "threshold '" + property.Name + "' has no numeric value.";
                    return null;
                }
                string comparison = (row.Value<string>("comparison") ?? "").ToLowerInvariant();
                if (comparison != "min" && comparison != "max")
                {
                    error = "threshold '" + property.Name + "' must state comparison 'min' or 'max'. Without it " +
                            "nothing knows which side of the number is the problem.";
                    return null;
                }
                profile.Thresholds[property.Name] = new AccessThreshold
                {
                    Key = property.Name,
                    Value = value,
                    Comparison = comparison,
                    Units = row.Value<string>("units"),
                    Note = row.Value<string>("note")
                };
            }
            return profile;
        }

        /// <summary>Thresholds the profile declares that this evaluator cannot apply.</summary>
        public static List<string> Unapplied(AccessProfile profile)
            => profile.Thresholds.Keys
                .Where(k => Array.FindIndex(KnownThresholds,
                            known => string.Equals(known, k, StringComparison.OrdinalIgnoreCase)) < 0)
                .OrderBy(k => k, StringComparer.Ordinal)
                .ToList();

        /// <summary>
        /// Compare one measurement to one threshold.
        ///
        /// Returns null when the profile says nothing about it - which is NOT a pass. A
        /// measurement with no threshold is reported as a measurement, and the caller can
        /// see that their profile was silent about it.
        /// </summary>
        public static JObject Evaluate(AccessProfile profile, string thresholdKey, double measured,
                                       string measuredUnits)
        {
            AccessThreshold threshold;
            if (profile == null || !profile.Thresholds.TryGetValue(thresholdKey, out threshold)) return null;

            bool within = threshold.Comparison == "min"
                ? measured >= threshold.Value
                : measured <= threshold.Value;

            return new JObject
            {
                ["threshold"] = threshold.Key,
                ["threshold_value"] = threshold.Value,
                ["threshold_comparison"] = threshold.Comparison,
                ["threshold_units"] = threshold.Units,
                ["threshold_note"] = threshold.Note == null ? (JToken)JValue.CreateNull() : threshold.Note,
                ["measured"] = Math.Round(measured, 1),
                ["measured_units"] = measuredUnits,
                ["within_profile"] = within,
                ["shortfall"] = within
                    ? (JToken)JValue.CreateNull()
                    : Math.Round(Math.Abs(measured - threshold.Value), 1),
                // The strongest word this file will say, said the same way every time.
                ["means"] = within
                    ? "the measurement is on the permitted side of a threshold THIS PROFILE declares. That is " +
                      "not a statement of code compliance, and this is not an assessment."
                    : "the measurement is on the wrong side of a threshold THIS PROFILE declares (" +
                      (profile.Name ?? "unnamed") + ", from " + (profile.Source ?? "an unstated source") +
                      "). That is a geometric finding against a supplied rule, not a finding of " +
                      "non-compliance with any code."
            };
        }

        /// <summary>
        /// The paragraph that goes at the top of every reply. It is not a disclaimer
        /// bolted on: it is the accurate description of what the numbers below are.
        /// </summary>
        public static JObject Preamble(AccessProfile profile) => new JObject
        {
            ["profile_name"] = profile?.Name,
            ["profile_source"] = profile?.Source,
            ["profile_jurisdiction"] = profile?.Jurisdiction == null
                ? (JToken)JValue.CreateNull() : profile.Jurisdiction,
            ["what_this_is"] =
                "GEOMETRIC MEASUREMENTS of the model, compared against thresholds SOMEBODY SUPPLIED. Horizun " +
                "carries no accessibility or egress thresholds of its own.",
            ["what_this_is_not"] =
                "An assessment of compliance with any building code. No edition, jurisdiction, occupancy, " +
                "exemption or alternative-compliance path was considered, because none of them is knowable " +
                "from a model. The strongest claim any row makes is 'within_profile'.",
            ["straight_line_warning"] =
                "Any distance-to-exit here is a STRAIGHT LINE, not a travel distance. A straight line passes " +
                "through walls; the real path is longer, always. An optimistic egress number reported without " +
                "that word is the most dangerous thing this command could produce."
        };
    }
}
