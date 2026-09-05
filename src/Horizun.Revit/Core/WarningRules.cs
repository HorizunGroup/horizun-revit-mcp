// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// WARNINGS, GROUPED BY SOMETHING THAT DOES NOT MOVE.
//
// Both surfaces used to group warnings by GetDescriptionText(). That text is
// LOCALIZED and it is rewritten between Revit versions, which breaks three
// things at once and breaks them silently:
//
//   - the same warning in a Spanish and an English session becomes two
//     different warnings, and one model looks twice as broken as it is;
//   - a run-over-run trend compares last month's wording with this month's and
//     reports the whole population as new;
//   - a caller's suppression list, keyed on the text they were given, stops
//     matching the day they upgrade.
//
// FailureDefinitionId.Guid is the same number in every language and in all five
// supported years - checked by reflection over each RevitAPI.dll rather than
// assumed - so it is the key here, and the description is demoted to a label.
//
// When the guid cannot be read the grouping FALLS BACK to the text and SAYS SO
// in identity_is_stable, because a fallback nobody is told about is the same
// defect wearing a different name.
//
// Revit-free: the facts are filled by the commands, the grouping is decided
// here, and all of it is provable at a desk.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    /// <summary>One warning as the model reports it, before anything is grouped.</summary>
    public sealed class WarningFact
    {
        /// <summary>The stable identity. Null when the model would not report it.</summary>
        public string DefinitionGuid;
        public string Description;
        /// <summary>Revit's OWN severity, not a caller's opinion of it.</summary>
        public string Severity;
        public List<long> FailingElementIds = new List<long>();
        /// <summary>False when the failing-element read threw: the ids are a lower bound.</summary>
        public bool IdsReadable = true;
        public string IdsError;
    }

    public sealed class WarningGroup
    {
        public string Key;
        /// <summary>True when the key is a guid. False means it is localized text.</summary>
        public bool IdentityIsStable;
        public string DefinitionGuid;
        public string Description;
        /// <summary>How many DIFFERENT description texts landed in this one group.</summary>
        public int DistinctDescriptions;
        public string Severity;
        /// <summary>Warnings, NOT failing elements. One warning can name many.</summary>
        public long Occurrences;
        public List<long> FailingElementIds = new List<long>();
        /// <summary>False when any member's ids could not be read.</summary>
        public bool IdsComplete = true;
        public string IdsError;
        /// <summary>The caller's triage for this warning, or null when no profile was supplied.</summary>
        public string CallerSeverity;
        public string CallerLabel;
    }

    public static class WarningCodes
    {
        public const string NoVersion = "warning_profile_no_version";
        public const string BadKey = "warning_profile_key_not_a_guid";
        public const string UnknownKey = "warning_profile_unknown_key";
        public const string BadRule = "warning_profile_bad_rule";
    }

    public sealed class WarningProfile
    {
        public bool Ok;
        public string Code;
        public string Message;
        public string Version;
        /// <summary>Guid (lower-case, no braces) to the caller's severity and label.</summary>
        public Dictionary<string, KeyValuePair<string, string>> ByGuid =
            new Dictionary<string, KeyValuePair<string, string>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>True when no profile was supplied at all - triage is not_requested.</summary>
        public bool Absent;
    }

    public static class WarningRules
    {
        /// <summary>Published beside the groups, so the identity is never assumed.</summary>
        public const string IdentityMeans =
            "grouped by FailureDefinitionId, which is the same value in every language and Revit version. " +
            "The description is a LABEL, not the key: grouping by it splits one warning across two languages " +
            "and merges two different warnings that happen to read alike. Where identity_is_stable is false " +
            "the guid could not be read and the text was used instead, so that group must not be compared " +
            "across languages or versions.";

        /// <summary>Published beside the counts, because the two numbers get confused.</summary>
        public const string OccurrencesMeans =
            "occurrences counts WARNINGS, not elements. One warning can name many failing elements, so the " +
            "sum of occurrences is smaller than the number of elements involved and neither is the other.";

        public static List<WarningGroup> Group(IEnumerable<WarningFact> facts)
        {
            var byKey = new Dictionary<string, WarningGroup>(StringComparer.Ordinal);
            var descriptions = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            var order = new List<string>();

            foreach (WarningFact f in facts ?? Enumerable.Empty<WarningFact>())
            {
                if (f == null) continue;

                bool stable = !string.IsNullOrWhiteSpace(f.DefinitionGuid);
                string key = stable
                    ? f.DefinitionGuid.Trim().ToLowerInvariant()
                    : ("text:" + (f.Description ?? "(description unreadable)"));

                WarningGroup g;
                if (!byKey.TryGetValue(key, out g))
                {
                    g = new WarningGroup
                    {
                        Key = key,
                        IdentityIsStable = stable,
                        DefinitionGuid = stable ? key : null,
                        Description = f.Description,
                        Severity = f.Severity
                    };
                    byKey[key] = g;
                    descriptions[key] = new HashSet<string>(StringComparer.Ordinal);
                    order.Add(key);
                }

                g.Occurrences++;
                if (f.Description != null) descriptions[key].Add(f.Description);

                // A member whose ids could not be read makes the WHOLE group's id
                // list a lower bound. The occurrence count stays exact - we counted
                // the warning, we just could not ask it which elements it names.
                if (!f.IdsReadable)
                {
                    g.IdsComplete = false;
                    if (g.IdsError == null) g.IdsError = f.IdsError;
                }
                foreach (long id in f.FailingElementIds ?? new List<long>())
                    if (!g.FailingElementIds.Contains(id)) g.FailingElementIds.Add(id);
            }

            foreach (string k in order)
            {
                byKey[k].DistinctDescriptions = descriptions[k].Count;
                byKey[k].FailingElementIds.Sort();
            }

            return order.Select(k => byKey[k])
                        .OrderByDescending(g => g.Occurrences)
                        // Ties break by key so two runs of one model agree. Without
                        // it a snapshot diff reports reordering as change.
                        .ThenBy(g => g.Key, StringComparer.Ordinal)
                        .ToList();
        }

        /// <summary>
        /// Reads the caller's triage. Keys MUST be guids: allowing a description
        /// key would rebuild the exact fragility this file exists to remove, and a
        /// profile that silently stops matching after an upgrade is worse than one
        /// that was refused on the day it was written.
        /// </summary>
        public static WarningProfile ReadProfile(JToken token)
        {
            var p = new WarningProfile();
            if (token == null || token.Type == JTokenType.Null)
            {
                p.Absent = true;
                p.Ok = false;
                p.Message = "no warning profile was supplied, so no warning was triaged. This is NOT a pass: " +
                            "every warning below is reported with Revit's own severity and none with yours.";
                return p;
            }

            var o = token as JObject;
            if (o == null)
            {
                p.Code = WarningCodes.BadRule;
                p.Message = "the warning profile must be an object.";
                return p;
            }

            JToken v = o["version"];
            if (v == null || string.IsNullOrWhiteSpace(v.Value<string>()))
            {
                p.Code = WarningCodes.NoVersion;
                p.Message = "the warning profile needs a 'version'. Without one, a report cannot say which " +
                            "triage produced it and two runs cannot be compared.";
                return p;
            }
            p.Version = v.Value<string>();

            foreach (JProperty prop in o.Properties())
            {
                if (prop.Name == "version") continue;

                Guid parsed;
                if (!Guid.TryParse(prop.Name, out parsed))
                {
                    p.Code = WarningCodes.BadKey;
                    p.Message = "'" + prop.Name + "' is not a FailureDefinitionId guid. Warning profiles are " +
                                "keyed by guid ONLY: a profile keyed on the description text stops matching " +
                                "the day Revit is upgraded or the session language changes, and it stops " +
                                "matching silently.";
                    return p;
                }

                var body = prop.Value as JObject;
                if (body == null)
                {
                    p.Code = WarningCodes.BadRule;
                    p.Message = "the entry for '" + prop.Name + "' must be an object with 'severity' and " +
                                "optionally 'label'.";
                    return p;
                }
                foreach (JProperty b in body.Properties())
                {
                    if (b.Name != "severity" && b.Name != "label")
                    {
                        p.Code = WarningCodes.UnknownKey;
                        p.Message = "'" + b.Name + "' is not a key this contract defines. Known keys: " +
                                    "severity, label.";
                        return p;
                    }
                }

                string sev = body.Value<string>("severity");
                if (string.IsNullOrWhiteSpace(sev))
                {
                    p.Code = WarningCodes.BadRule;
                    p.Message = "the entry for '" + prop.Name + "' has no 'severity'.";
                    return p;
                }
                p.ByGuid[parsed.ToString("D")] =
                    new KeyValuePair<string, string>(sev, body.Value<string>("label"));
            }

            p.Ok = true;
            return p;
        }

        /// <summary>
        /// Applies the caller's triage. A group the profile is silent about keeps a
        /// NULL caller severity - never a default - because inventing "normal" for
        /// a warning nobody classified is the same lie as reporting it clean.
        /// </summary>
        public static void Triage(IEnumerable<WarningGroup> groups, WarningProfile profile)
        {
            if (groups == null || profile == null || !profile.Ok) return;
            foreach (WarningGroup g in groups)
            {
                if (g == null || !g.IdentityIsStable || g.DefinitionGuid == null) continue;

                Guid parsed;
                if (!Guid.TryParse(g.DefinitionGuid, out parsed)) continue;

                KeyValuePair<string, string> hit;
                if (!profile.ByGuid.TryGetValue(parsed.ToString("D"), out hit)) continue;
                g.CallerSeverity = hit.Key;
                g.CallerLabel = hit.Value;
            }
        }

        public static JObject ToJson(WarningGroup g)
        {
            if (g == null) return null;
            return new JObject
            {
                ["failure_definition_guid"] = g.DefinitionGuid,
                ["identity_is_stable"] = g.IdentityIsStable,
                ["description"] = g.Description,
                ["distinct_descriptions"] = g.DistinctDescriptions,
                ["revit_severity"] = g.Severity,
                ["caller_severity"] = g.CallerSeverity,
                ["caller_label"] = g.CallerLabel,
                ["occurrences"] = g.Occurrences,
                ["failing_element_ids_complete"] = g.IdsComplete,
                ["failing_element_ids_error"] = g.IdsError
            };
        }
    }
}
