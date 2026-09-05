// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// FAMILIES AND TYPES, counted without opening a single .rfa.
//
// The temptation in this area is to call a family "heavy". Nothing here can:
// a family loaded into a model has no file size the API will report, and this
// census deliberately does not open family documents - opening one changes the
// active document, and a diagnostic that changes what it is measuring is not a
// diagnostic. So MANY TYPES and MANY INSTANCES are published as INDICATORS with
// the reason attached, never as a measured weight and never as a defect.
//
// THREE KINDS THAT ARE GENUINELY DIFFERENT, and are constantly conflated:
//
//   loadable   a Family element, editable, came from an .rfa.
//   in_place   a Family element too, but it belongs to this project alone.
//              Reporting it as loadable hides the thing worth knowing.
//   system     NOT a Family element at all. A wall type has no Family behind
//              it, so a census built on OfClass(Family) cannot see one and
//              silently reports a model as having fewer families than it has.
//
// A fourth state, unreadable, is its own counter and never merges into the
// other three - a family we could not classify is not a loadable one.
//
// NOTHING ORGANISATIONAL IS COMPILED IN. With no profile this returns facts and
// ranked candidates; a candidate is a place to look, not a violation.
//
// Revit-free, so all of it is provable at a desk.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public static class FamilyKind
    {
        public const string Loadable = "loadable";
        public const string InPlace = "in_place";
        public const string System = "system";
        public const string Unreadable = "unreadable";

        public static readonly string[] All = { Loadable, InPlace, System, Unreadable };
    }

    public static class FamilyFindingCodes
    {
        public const string TooManyTypes = "too_many_types";
        public const string TooManyUnusedTypes = "too_many_unused_types";
        public const string TooManyInstances = "too_many_instances";
        public const string InPlaceForbidden = "in_place_forbidden";
        public const string CategoryNotAllowed = "category_not_allowed";
        public const string ExpectedSharedNotShared = "expected_shared_not_shared";
    }

    public static class FamilyProfileCodes
    {
        public const string NoVersion = "family_profile_no_version";
        public const string UnknownKey = "family_profile_unknown_key";
        public const string BadRule = "family_profile_bad_rule";
        public const string Absent = "family_profile_absent";
    }

    /// <summary>One family as the model reports it. Nothing here is judged.</summary>
    public sealed class FamilyFact
    {
        /// <summary>-1 for a system family: it has no Family element to have an id.</summary>
        public long ElementId = -1;
        public string UniqueId;
        public string Name;
        public bool NameReadable = true;
        public string Category;
        public string Kind = FamilyKind.Unreadable;

        // bool?, NOT bool. A family whose IsInPlace read threw has not told us it
        // is loadable, and false here would be exactly that claim.
        public bool? IsInPlace;
        /// <summary>Null when FAMILY_SHARED is absent or unreadable. NEVER defaulted to false.</summary>
        public bool? IsShared;

        public int TypeCount;
        public int UnusedTypeCount;
        public int UnreadableTypeCount;
        public long InstanceCount;
        public long UnreadableInstanceCount;

        /// <summary>Null when nothing was placed - NOT 0, which would claim a depth was observed.</summary>
        public int? NestedDepthObserved;

        public Dictionary<string, long> WorksetDistribution = new Dictionary<string, long>(StringComparer.Ordinal);
        public Dictionary<string, long> HostDistribution = new Dictionary<string, long>(StringComparer.Ordinal);

        public int ParameterCount;
        public bool ParametersReadable = true;

        /// <summary>True when every type and instance of this family could be read.</summary>
        public bool CoverageComplete
        {
            get { return UnreadableTypeCount == 0 && UnreadableInstanceCount == 0 && NameReadable; }
        }
    }

    public sealed class FamilyFinding
    {
        public string Code;
        public string FamilyName;
        public long ElementId;
        public string Detail;
    }

    public sealed class FamilyProfile
    {
        public bool Ok;
        public bool Absent;
        public string Code;
        public string Message;
        public string Version;

        public int? MaxTypes;
        public int? MaxUnusedTypes;
        public long? MaxInstances;

        /// <summary>Category to whether an in-place family is allowed there.</summary>
        public Dictionary<string, bool> InPlaceAllowedByCategory =
            new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        public List<string> ExpectedSharedFamilies = new List<string>();
        /// <summary>Null when the caller listed none: silence is not a ban on every category.</summary>
        public List<string> AllowedCategories;
        public HashSet<string> Exceptions = new HashSet<string>(StringComparer.Ordinal);
    }

    public static class FamilyCensusRules
    {
        public const string IndicatorMeans =
            "type_count and instance_count are INDICATORS, not weights. A family loaded into a model reports no " +
            "file size through the API, and this census does not open family documents - opening one would " +
            "change the document being measured. A family with many types may be a well-built catalogue and a " +
            "family with one type may be the heaviest thing in the file. Treat these as places to look.";

        public const string KindsMean =
            "a system family is NOT a Family element - a wall type has no Family behind it - so a census built " +
            "only on loadable families reports fewer families than the model has. in_place is separated from " +
            "loadable because it is the distinction worth acting on, and unreadable is its own count rather " +
            "than being folded into either.";

        // ------------------------------------------------------------- profile

        public static FamilyProfile Read(JToken token)
        {
            var p = new FamilyProfile();
            if (token == null || token.Type == JTokenType.Null)
            {
                p.Absent = true;
                p.Code = FamilyProfileCodes.Absent;
                p.Message = "no family profile was supplied, so no family was judged. The facts and the ranked " +
                            "candidates below stand on their own; NONE of them is a violation, because how many " +
                            "types a family may have is one organisation's decision and none is compiled in here.";
                return p;
            }

            var o = token as JObject;
            if (o == null)
            {
                p.Code = FamilyProfileCodes.BadRule;
                p.Message = "the family profile must be an object.";
                return p;
            }

            JToken v = o["version"];
            if (v == null || string.IsNullOrWhiteSpace(v.Value<string>()))
            {
                p.Code = FamilyProfileCodes.NoVersion;
                p.Message = "the family profile needs a 'version', so a report can say which rules produced it.";
                return p;
            }
            p.Version = v.Value<string>();

            foreach (JProperty prop in o.Properties())
            {
                switch (prop.Name)
                {
                    case "version":
                        break;

                    case "max_types":
                        if (!ReadNonNegativeInt(prop, out p.MaxTypes, p)) return p;
                        break;

                    case "max_unused_types":
                        if (!ReadNonNegativeInt(prop, out p.MaxUnusedTypes, p)) return p;
                        break;

                    case "max_instances":
                    {
                        if (prop.Value.Type != JTokenType.Integer || prop.Value.Value<long>() < 0)
                        {
                            p.Code = FamilyProfileCodes.BadRule;
                            p.Message = "'max_instances' must be a whole number of zero or more.";
                            return p;
                        }
                        p.MaxInstances = prop.Value.Value<long>();
                        break;
                    }

                    case "in_place_allowed_by_category":
                    {
                        var body = prop.Value as JObject;
                        if (body == null)
                        {
                            p.Code = FamilyProfileCodes.BadRule;
                            p.Message = "'in_place_allowed_by_category' must be an object of category to true/false.";
                            return p;
                        }
                        foreach (JProperty c in body.Properties())
                        {
                            if (c.Value.Type != JTokenType.Boolean)
                            {
                                p.Code = FamilyProfileCodes.BadRule;
                                p.Message = "the entry for category '" + c.Name + "' must be true or false.";
                                return p;
                            }
                            p.InPlaceAllowedByCategory[c.Name] = c.Value.Value<bool>();
                        }
                        break;
                    }

                    case "expected_shared_families":
                        if (!ReadStringList(prop, p.ExpectedSharedFamilies, p)) return p;
                        break;

                    case "allowed_categories":
                    {
                        p.AllowedCategories = new List<string>();
                        if (!ReadStringList(prop, p.AllowedCategories, p)) return p;
                        if (p.AllowedCategories.Count == 0)
                        {
                            // An empty allow-list would forbid EVERY category, which is
                            // almost certainly a mistake in the profile rather than a
                            // model where nothing is permitted.
                            p.Code = FamilyProfileCodes.BadRule;
                            p.Message = "'allowed_categories' is empty, which would forbid every category in " +
                                        "the model. Omit the key to allow all categories.";
                            return p;
                        }
                        break;
                    }

                    case "exceptions":
                    {
                        var list = new List<string>();
                        if (!ReadStringList(prop, list, p)) return p;
                        foreach (string e in list) p.Exceptions.Add(e);
                        break;
                    }

                    default:
                        p.Code = FamilyProfileCodes.UnknownKey;
                        p.Message = "'" + prop.Name + "' is not a key this profile defines. Known keys: " +
                                    "version, max_types, max_unused_types, max_instances, " +
                                    "in_place_allowed_by_category, expected_shared_families, allowed_categories, " +
                                    "exceptions. A rule filed under a name nothing reads never runs, and the " +
                                    "report looks like a clean pass of a rule nobody applied.";
                        return p;
                }
            }

            p.Ok = true;
            return p;
        }

        private static bool ReadNonNegativeInt(JProperty prop, out int? into, FamilyProfile p)
        {
            into = null;
            if (prop.Value.Type != JTokenType.Integer || prop.Value.Value<long>() < 0)
            {
                p.Code = FamilyProfileCodes.BadRule;
                p.Message = "'" + prop.Name + "' must be a whole number of zero or more.";
                return false;
            }
            into = prop.Value.Value<int>();
            return true;
        }

        private static bool ReadStringList(JProperty prop, List<string> into, FamilyProfile p)
        {
            var arr = prop.Value as JArray;
            if (arr == null)
            {
                p.Code = FamilyProfileCodes.BadRule;
                p.Message = "'" + prop.Name + "' must be an array of names.";
                return false;
            }
            foreach (JToken t in arr)
            {
                if (t.Type != JTokenType.String || string.IsNullOrWhiteSpace(t.Value<string>()))
                {
                    p.Code = FamilyProfileCodes.BadRule;
                    p.Message = "'" + prop.Name + "' contains an entry that is not a name.";
                    return false;
                }
                into.Add(t.Value<string>());
            }
            return true;
        }

        // ------------------------------------------------------------ judging

        /// <summary>
        /// Judges the families against a caller's profile. With no profile - or a
        /// refused one - NOTHING is judged, and the empty list means "not checked"
        /// rather than "all clean". A refused profile is never partly applied: Read
        /// fills the rules as it parses and only then meets the bad key, so
        /// enforcing what it collected would judge a model against rules the caller
        /// was told were rejected.
        /// </summary>
        public static List<FamilyFinding> Judge(IEnumerable<FamilyFact> families, FamilyProfile p)
        {
            var findings = new List<FamilyFinding>();
            if (families == null || p == null || !p.Ok) return findings;

            foreach (FamilyFact f in families)
            {
                if (f == null) continue;
                if (f.Name != null && p.Exceptions.Contains(f.Name)) continue;

                if (p.MaxTypes.HasValue && f.TypeCount > p.MaxTypes.Value)
                    findings.Add(Find(FamilyFindingCodes.TooManyTypes, f,
                        f.TypeCount + " types, more than the " + p.MaxTypes.Value + " you allow."));

                if (p.MaxUnusedTypes.HasValue && f.UnusedTypeCount > p.MaxUnusedTypes.Value)
                    findings.Add(Find(FamilyFindingCodes.TooManyUnusedTypes, f,
                        f.UnusedTypeCount + " types with no instance, more than the " +
                        p.MaxUnusedTypes.Value + " you allow."));

                if (p.MaxInstances.HasValue && f.InstanceCount > p.MaxInstances.Value)
                    findings.Add(Find(FamilyFindingCodes.TooManyInstances, f,
                        f.InstanceCount + " instances, more than the " + p.MaxInstances.Value + " you allow."));

                // TRUE, not "not false". A family whose IsInPlace could not be read
                // must not be reported as an in-place family somebody forbade.
                if (f.IsInPlace == true && f.Category != null)
                {
                    bool allowed;
                    if (p.InPlaceAllowedByCategory.TryGetValue(f.Category, out allowed) && !allowed)
                        findings.Add(Find(FamilyFindingCodes.InPlaceForbidden, f,
                            "an in-place family in '" + f.Category + "', where you do not allow them."));
                }

                if (p.AllowedCategories != null && f.Category != null &&
                    !p.AllowedCategories.Any(c => string.Equals(c, f.Category, StringComparison.OrdinalIgnoreCase)))
                    findings.Add(Find(FamilyFindingCodes.CategoryNotAllowed, f,
                        "category '" + f.Category + "' is not in your allowed list."));

                // IsShared false, not null. A family whose shared flag could not be
                // read has not told us it is unshared.
                if (f.Name != null && f.IsShared == false &&
                    p.ExpectedSharedFamilies.Any(n => string.Equals(n, f.Name, StringComparison.Ordinal)))
                    findings.Add(Find(FamilyFindingCodes.ExpectedSharedNotShared, f,
                        "you expect this family to be shared and the model reports it is not."));
            }
            return findings;
        }

        private static FamilyFinding Find(string code, FamilyFact f, string detail)
        {
            return new FamilyFinding
            {
                Code = code,
                FamilyName = f.Name,
                ElementId = f.ElementId,
                Detail = detail
            };
        }

        // --------------------------------------------------------- candidates

        public const string SelectionRule =
            "ranked by type_count + unused_type_count, then by instance_count, then by name; the top N are " +
            "returned and the rest are counted as not_selected. This is a place to look, NOT a measure of " +
            "size and NOT a finding.";

        /// <summary>
        /// The families worth a human's attention first, and an explicit statement
        /// of how many were passed over - a triage that does not say what it
        /// skipped reads as a complete list.
        /// </summary>
        public static JObject Candidates(IEnumerable<FamilyFact> families, int budget)
        {
            List<FamilyFact> ranked = (families ?? Enumerable.Empty<FamilyFact>())
                .Where(f => f != null)
                .OrderByDescending(f => f.TypeCount + f.UnusedTypeCount)
                .ThenByDescending(f => f.InstanceCount)
                .ThenBy(f => f.Name, StringComparer.Ordinal)
                .ToList();

            if (budget < 0) budget = 0;
            List<FamilyFact> selected = ranked.Take(budget).ToList();

            var rows = new JArray();
            foreach (FamilyFact f in selected)
                rows.Add(new JObject
                {
                    ["family_id"] = f.ElementId,
                    ["name"] = f.Name,
                    ["category"] = f.Category,
                    ["kind"] = f.Kind,
                    ["type_count"] = f.TypeCount,
                    ["unused_type_count"] = f.UnusedTypeCount,
                    ["instance_count"] = f.InstanceCount,
                    // Every row says what class of evidence it is, in the row.
                    ["evidence"] = EvidenceClass.Indicator,
                    ["why"] = "many types or many instances is a signal worth looking at; it is not a measure " +
                              "of how much file this family occupies, and nothing here opened it."
                });

            return new JObject
            {
                ["selection_rule"] = SelectionRule,
                ["budget"] = budget,
                ["ranked"] = ranked.Count,
                ["selected"] = rows,
                ["not_selected"] = ranked.Count - selected.Count,
                ["means"] = IndicatorMeans
            };
        }

        // ------------------------------------------------------------ rollups

        /// <summary>
        /// The scalars, with unreadable kept out of every other bucket. A family
        /// nobody could classify is not a loadable one, and adding it to that count
        /// is how a census reports more loadable families than the model holds.
        /// </summary>
        public static JObject Totals(IEnumerable<FamilyFact> families)
        {
            var all = (families ?? Enumerable.Empty<FamilyFact>()).Where(f => f != null).ToList();

            long loadable = all.Count(f => f.Kind == FamilyKind.Loadable);
            long inPlace = all.Count(f => f.Kind == FamilyKind.InPlace);
            long system = all.Count(f => f.Kind == FamilyKind.System);
            long unreadable = all.Count(f => f.Kind == FamilyKind.Unreadable);

            return new JObject
            {
                ["families_total"] = all.Count,
                ["families_loadable"] = loadable,
                ["families_in_place"] = inPlace,
                ["families_system"] = system,
                ["families_unreadable"] = unreadable,
                ["families_shared_unreadable"] = all.Count(f => f.IsShared == null),
                ["types_total"] = all.Sum(f => (long)f.TypeCount),
                ["types_unused"] = all.Sum(f => (long)f.UnusedTypeCount),
                ["types_unreadable"] = all.Sum(f => (long)f.UnreadableTypeCount),
                ["instances_total"] = all.Sum(f => f.InstanceCount),
                ["instances_unreadable"] = all.Sum(f => f.UnreadableInstanceCount),
                ["coverage_complete"] = all.All(f => f.CoverageComplete),
                ["kinds_mean"] = KindsMean
            };
        }

        public static JObject ToJson(FamilyFact f)
        {
            if (f == null) return null;
            var worksets = new JArray();
            foreach (KeyValuePair<string, long> kv in Ranked(f.WorksetDistribution))
                worksets.Add(new JObject { ["workset"] = kv.Key, ["instances"] = kv.Value });

            var hosts = new JArray();
            foreach (KeyValuePair<string, long> kv in Ranked(f.HostDistribution))
                hosts.Add(new JObject { ["host_category"] = kv.Key, ["instances"] = kv.Value });

            return new JObject
            {
                ["family_id"] = f.ElementId < 0 ? null : (JToken)f.ElementId,
                ["unique_id"] = f.UniqueId,
                ["name"] = f.Name,
                ["name_readable"] = f.NameReadable,
                ["category"] = f.Category,
                ["kind"] = f.Kind,
                ["is_in_place"] = f.IsInPlace,
                ["is_shared"] = f.IsShared,
                ["type_count"] = f.TypeCount,
                ["unused_type_count"] = f.UnusedTypeCount,
                ["unreadable_type_count"] = f.UnreadableTypeCount,
                ["instance_count"] = f.InstanceCount,
                ["unreadable_instance_count"] = f.UnreadableInstanceCount,
                ["nested_depth_observed"] = f.NestedDepthObserved,
                ["workset_distribution"] = worksets,
                ["host_distribution"] = hosts,
                ["parameter_count"] = f.ParametersReadable ? (JToken)f.ParameterCount : null,
                ["parameters_readable"] = f.ParametersReadable,
                ["coverage_complete"] = f.CoverageComplete
            };
        }

        /// <summary>Largest first, ties by name, so two runs of one model agree.</summary>
        public static List<KeyValuePair<string, long>> Ranked(Dictionary<string, long> d)
        {
            var rows = new List<KeyValuePair<string, long>>();
            if (d == null) return rows;
            rows.AddRange(d);
            rows.Sort((a, b) =>
            {
                int byCount = b.Value.CompareTo(a.Value);
                return byCount != 0 ? byCount : string.CompareOrdinal(a.Key, b.Key);
            });
            return rows;
        }
    }
}
