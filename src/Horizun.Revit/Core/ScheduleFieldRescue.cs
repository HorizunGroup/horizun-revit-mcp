// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// horizun_create_schedule: a requested field that Revit does not offer for the category used to
// roll the whole schedule back ("these requested fields are not schedulable for Doors:
// FAMILY_LEVEL_PARAM"). Two things were missing:
//   1. the commonest wrong tokens have a schedulable twin (FAMILY_LEVEL_PARAM is the level of a
//      family INSTANCE; the schedule column of that name is SCHEDULE_LEVEL_PARAM, shown as Level);
//   2. when there is no twin, the rest of the request is still worth building: the schedule is
//      created with the fields that exist and the dropped ones are listed as SUPUESTO with the
//      closest names Revit does offer, instead of nothing.
// When NO field resolves, nothing is created and the refusal lists what the category offers.
//
// No Revit types: the command hands plain strings.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public static class ScheduleFieldRescue
    {
        // requested token -> alternatives tried in order. Each alternative is a displayed name or a
        // BuiltInParameter token the command's own resolver understands.
        private static readonly Dictionary<string, string[]> Aliases = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["FAMILY_LEVEL_PARAM"] = new[] { "SCHEDULE_LEVEL_PARAM", "Level", "Nivel" },
            ["LEVEL_PARAM"] = new[] { "SCHEDULE_LEVEL_PARAM", "Level", "Nivel" },
            ["FAMILY_BASE_LEVEL_PARAM"] = new[] { "SCHEDULE_BASE_LEVEL_PARAM", "Base Level", "Nivel de base" },
            ["FAMILY_TOP_LEVEL_PARAM"] = new[] { "SCHEDULE_TOP_LEVEL_PARAM", "Top Level", "Nivel superior" },
            ["FAMILYANDTYPE"] = new[] { "ELEM_FAMILY_AND_TYPE_PARAM", "Family and Type", "Familia y tipo" },
            ["FAMILY_AND_TYPE"] = new[] { "ELEM_FAMILY_AND_TYPE_PARAM", "Family and Type", "Familia y tipo" },
            ["TYPE_NAME"] = new[] { "ELEM_TYPE_PARAM", "Type", "Tipo" },
            ["FAMILY_NAME"] = new[] { "ELEM_FAMILY_PARAM", "Family", "Familia" },
            ["ELEMENT_ID"] = new[] { "ID", "Element Id", "Id" },
        };

        /// <summary>Alternatives to try, in order, for a requested field that did not resolve. Never null.</summary>
        public static IReadOnlyList<string> AliasesFor(string requested)
        {
            if (string.IsNullOrWhiteSpace(requested)) return new string[0];
            string key = requested.Trim().Replace(" ", "_");
            if (Aliases.TryGetValue(key, out string[] found)) return found;
            string flat = requested.Trim().Replace(" ", "").Replace("_", "");
            if (Aliases.TryGetValue(flat, out found)) return found;
            return new string[0];
        }

        /// <summary>
        /// The available names closest to <paramref name="requested"/> (same name, substring either
        /// way, then a small edit distance), at most <paramref name="max"/>, never the same name twice.
        /// </summary>
        public static IReadOnlyList<string> Suggest(string requested, IEnumerable<string> available, int max = 5)
        {
            var result = new List<string>();
            if (string.IsNullOrWhiteSpace(requested) || available == null || max <= 0) return result;
            string want = Fold(requested);
            var names = available.Where(n => !string.IsNullOrWhiteSpace(n))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var scored = new List<KeyValuePair<int, string>>();
            foreach (string name in names)
            {
                string have = Fold(name);
                int score;
                if (have == want) score = 0;
                else if (want.Length >= 3 && have.Length >= 3 && (have.Contains(want) || want.Contains(have))) score = 1;
                else
                {
                    int longest = Math.Max(want.Length, have.Length);
                    int d = Distance(want, have);
                    // Close only: a third of the longer name at most, and never a coincidence of short names.
                    if (longest < 4 || d > Math.Max(1, longest / 3)) continue;
                    score = 2 + d;
                }
                scored.Add(new KeyValuePair<int, string>(score, name));
            }
            foreach (var kv in scored.OrderBy(k => k.Key).ThenBy(k => k.Value, StringComparer.OrdinalIgnoreCase).Take(max))
                result.Add(kv.Value);
            return result;
        }

        /// <summary>The refusal when nothing the caller named could be added.</summary>
        public static string NothingResolved(string categoryName, IList<string> requested, IEnumerable<string> available)
        {
            var names = (available ?? new string[0]).Where(n => !string.IsNullOrWhiteSpace(n))
                .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
            string offered = names.Count == 0 ? "(none could be read)"
                : string.Join(", ", names.Take(40)) + (names.Count > 40 ? ", ... (" + names.Count + " in all)" : "");
            return "The schedule was not created: none of the requested fields (" + string.Join(", ", requested ?? new string[0]) +
                   ") is schedulable for " + categoryName + ". Revit offers for it: " + offered +
                   ". Use a displayed field name, Count/Family/Type, or a BuiltInParameter token such as ELEM_TYPE_PARAM. Nothing was changed.";
        }

        /// <summary>The assumption row for the fields that were left out.</summary>
        public static JObject SkippedRow(string categoryName, IList<string> skipped, IDictionary<string, IReadOnlyList<string>> suggestions)
        {
            string hints = string.Join("; ", skipped.Select(s =>
            {
                IReadOnlyList<string> near = null;
                return suggestions != null && suggestions.TryGetValue(s, out near) && near != null && near.Count > 0
                    ? s + " (closest: " + string.Join(", ", near) + ")" : s;
            }));
            return AssumptionNotes.Row(
                "fields left out: " + string.Join(", ", skipped),
                "Revit does not offer " + (skipped.Count == 1 ? "this field" : "these fields") + " for " + categoryName +
                "; the schedule is built with the rest, and its state is partial (not verified in full) because of them. " + hints + ".",
                "remove the field from fields, or use one Revit offers for this category (the closest names are above); a column can also be added later with horizun_manage_schedules.");
        }

        public static JObject SubstitutedRow(IList<KeyValuePair<string, string>> pairs)
            => AssumptionNotes.Row(
                "fields substituted: " + string.Join(", ", pairs.Select(p => p.Key + " -> " + p.Value)),
                "the requested token is not schedulable for this category; its schedulable twin was used.",
                "name the exact displayed field to avoid the substitution.");

        private static string Fold(string s)
        {
            var sb = new System.Text.StringBuilder();
            foreach (char c in s.Normalize(System.Text.NormalizationForm.FormD))
                if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            return sb.ToString();
        }

        private static int Distance(string a, string b)
        {
            var prev = new int[b.Length + 1];
            var cur = new int[b.Length + 1];
            for (int j = 0; j <= b.Length; j++) prev[j] = j;
            for (int i = 1; i <= a.Length; i++)
            {
                cur[0] = i;
                for (int j = 1; j <= b.Length; j++)
                    cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
                var t = prev; prev = cur; cur = t;
            }
            return prev[b.Length];
        }
    }
}
