// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// NAMING, AS A PROFILE THE CALLER BRINGS - never a convention compiled in here.
//
// One regular expression cannot serve a project. A level is named nothing like a
// sheet, a workset nothing like a view template, and an organisation that has a
// rule for one usually has a different rule for the other. So a profile carries
// a separate rule set PER CLASS OF OBJECT, and a class nobody wrote a rule for
// is reported `not_requested` - not as a class that passed.
//
// That distinction is the whole point. "Zero naming problems" over a model where
// nobody supplied a rule for sheets is a true sentence about the rules and a
// false impression about the sheets.
//
// WHAT THIS NEVER DOES: rename anything. It returns the name it found, the rule
// that was not met, and a suggestion. Renaming is somebody else's decision and a
// diagnosis that quietly performs it is not a diagnosis.
//
// Every refusal is by name. An unknown key in a profile is refused rather than
// ignored, because a rule silently dropped reads exactly like a rule that passed;
// and an invalid regular expression is refused rather than skipped, for the same
// reason.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public static class NamingCodes
    {
        public const string UnknownClass = "unknown_naming_class";
        public const string UnknownRuleKey = "unknown_naming_rule";
        public const string BadRegex = "invalid_regex";
        public const string BadRule = "invalid_naming_rule";
        public const string NoVersion = "naming_profile_has_no_version";
        /// <summary>
        /// No profile at all. Distinct from every other code here: those mean
        /// the caller wrote something wrong, this one means they wrote nothing,
        /// and a reply that cannot tell them apart tells the caller their
        /// profile was rejected when in fact none was sent.
        /// </summary>
        public const string NoProfile = "naming_profile_absent";

        // why one name failed
        public const string RegexFailed = "regex";
        public const string PrefixFailed = "prefix";
        public const string SuffixFailed = "suffix";
        public const string SegmentsFailed = "segments";
        public const string TooShort = "min_length";
        public const string TooLong = "max_length";
        public const string NotAllowed = "allowed";
        public const string Forbidden = "forbidden";
        public const string CaseFailed = "case";
        public const string DefaultWord = "default_word";
        public const string NotUnique = "unique";
    }

    /// <summary>Which classes of object a profile may carry rules for.</summary>
    public static class NamingClasses
    {
        public static readonly string[] All =
        {
            "levels", "grids", "views", "sheets", "families", "types", "worksets",
            "links", "groups", "rooms", "spaces", "systems", "filters", "view_templates",
        };
    }

    public sealed class NamingRule
    {
        public string Regex;
        public string Prefix;
        public string Suffix;
        public string Separator;
        public int? Segments;
        public int? MinLength;
        public int? MaxLength;
        public List<string> Allowed;
        public List<string> Forbidden;
        public string Case;                 // "upper" | "lower" | null
        public bool Unique;
        public List<string> DefaultWords;   // "Unnamed", "Copy of", ...
        public readonly HashSet<string> Exceptions = new HashSet<string>(StringComparer.Ordinal);

        private Regex _compiled;
        public Regex Compiled => _compiled ?? (_compiled = Regex == null ? null
            : new Regex(Regex, RegexOptions.CultureInvariant));
    }

    public sealed class NamingProfile
    {
        public bool Ok;
        public string Code;
        public string Message;
        public string Version;
        public readonly Dictionary<string, NamingRule> ByClass =
            new Dictionary<string, NamingRule>(StringComparer.Ordinal);

        public static NamingProfile Refused(string code, string message) =>
            new NamingProfile { Ok = false, Code = code, Message = message };
    }

    public sealed class NamingFinding
    {
        public string Class;
        public string Id;
        public string Name;
        public string Rule;
        public string Detail;
        public string Suggestion;
    }

    public sealed class NamingVerdict
    {
        public string Class;
        public string Status;          // ok | failed | not_requested | not_assessable
        public int Examined;
        public int Matched;
        public int Unreadable;
        public string Limitation;
        public List<NamingFinding> Findings = new List<NamingFinding>();

        public JObject ToJson() => new JObject
        {
            ["class"] = Class,
            ["status"] = Status,
            ["examined_count"] = Examined,
            ["matched_count"] = Matched,
            ["unreadable_count"] = Unreadable,
            ["limitations"] = Limitation,
            ["findings"] = new JArray(Findings.Select(f => (JToken)new JObject
            {
                ["id"] = f.Id,
                ["name"] = f.Name,
                ["rule"] = f.Rule,
                ["detail"] = f.Detail,
                ["suggestion"] = f.Suggestion,
            })),
        };
    }

    /// <summary>One thing with a name, as the extraction layer hands it over.</summary>
    public sealed class NamedThing
    {
        public string Id;
        public string Name;
        public bool Readable = true;
    }

    public static class NamingProfileRules
    {
        private static readonly HashSet<string> RuleKeys = new HashSet<string>(StringComparer.Ordinal)
        {
            "regex", "prefix", "suffix", "separator", "segments", "min_length", "max_length",
            "allowed", "forbidden", "case", "unique", "default_words", "exceptions",
        };

        public static NamingProfile Read(JToken profile)
        {
            if (profile == null || profile.Type == JTokenType.Null)
                return NamingProfile.Refused(NamingCodes.NoProfile,
                    "no naming profile was supplied. Nothing is compiled in here on purpose - a built-in " +
                    "convention would be one organisation's, applied to everybody's models - so with no profile " +
                    "there is nothing to check and every class is reported not_requested rather than clean.");

            if (profile.Type != JTokenType.Object)
                return NamingProfile.Refused(NamingCodes.BadRule,
                    "a naming profile must be an object with 'version' and a rule set per class.");

            var obj = (JObject)profile;
            var result = new NamingProfile { Ok = true, Version = obj.Value<string>("version") };

            if (string.IsNullOrWhiteSpace(result.Version))
                return NamingProfile.Refused(NamingCodes.NoVersion,
                    "this naming profile has no 'version'. A finding is only meaningful next to the rules that " +
                    "produced it, and a version is how a later reader knows two reports were judged the same way.");

            var known = new HashSet<string>(NamingClasses.All, StringComparer.Ordinal);
            foreach (JProperty p in obj.Properties())
            {
                if (p.Name == "version") continue;

                if (!known.Contains(p.Name))
                    return NamingProfile.Refused(NamingCodes.UnknownClass,
                        "'" + p.Name + "' is not a class this checks. The classes are: " +
                        string.Join(", ", NamingClasses.All.OrderBy(c => c, StringComparer.Ordinal)) +
                        ". A rule filed under a name nothing matches would never run, and the report would look " +
                        "like a clean pass of a rule nobody applied.");

                if (p.Value.Type != JTokenType.Object)
                    return NamingProfile.Refused(NamingCodes.BadRule,
                        "the rules for '" + p.Name + "' must be an object.");

                var rule = new NamingRule();
                foreach (JProperty r in ((JObject)p.Value).Properties())
                {
                    if (!RuleKeys.Contains(r.Name))
                        return NamingProfile.Refused(NamingCodes.UnknownRuleKey,
                            "'" + p.Name + "." + r.Name + "' is not a naming rule. The rules are: " +
                            string.Join(", ", RuleKeys.OrderBy(k => k, StringComparer.Ordinal)) + ".");

                    switch (r.Name)
                    {
                        case "regex":
                            rule.Regex = r.Value.ToString();
                            try { var _ = new Regex(rule.Regex, RegexOptions.CultureInvariant); }
                            catch (Exception ex)
                            {
                                return NamingProfile.Refused(NamingCodes.BadRegex,
                                    "the regular expression for '" + p.Name + "' does not compile (" + ex.Message +
                                    "). It is refused rather than skipped: a rule that silently does not run " +
                                    "reports every name as acceptable.");
                            }
                            break;
                        case "prefix": rule.Prefix = r.Value.ToString(); break;
                        case "suffix": rule.Suffix = r.Value.ToString(); break;
                        case "separator": rule.Separator = r.Value.ToString(); break;
                        case "case":
                            rule.Case = r.Value.ToString();
                            if (rule.Case != "upper" && rule.Case != "lower")
                                return NamingProfile.Refused(NamingCodes.BadRule,
                                    "'" + p.Name + ".case' must be 'upper' or 'lower'.");
                            break;
                        case "unique": rule.Unique = r.Value.Type == JTokenType.Boolean && r.Value.Value<bool>(); break;
                        case "segments":
                        case "min_length":
                        case "max_length":
                            if (r.Value.Type != JTokenType.Integer)
                                return NamingProfile.Refused(NamingCodes.BadRule,
                                    "'" + p.Name + "." + r.Name + "' must be a whole number.");
                            int n = r.Value.Value<int>();
                            if (n < 0)
                                return NamingProfile.Refused(NamingCodes.BadRule,
                                    "'" + p.Name + "." + r.Name + "' is " + n.ToString(CultureInfo.InvariantCulture) +
                                    " and cannot be negative.");
                            if (r.Name == "segments") rule.Segments = n;
                            else if (r.Name == "min_length") rule.MinLength = n;
                            else rule.MaxLength = n;
                            break;
                        case "allowed": rule.Allowed = Strings(r.Value); break;
                        case "forbidden": rule.Forbidden = Strings(r.Value); break;
                        case "default_words": rule.DefaultWords = Strings(r.Value); break;
                        case "exceptions":
                            foreach (string e in Strings(r.Value)) rule.Exceptions.Add(e);
                            break;
                    }
                }

                if (rule.Segments.HasValue && string.IsNullOrEmpty(rule.Separator))
                    return NamingProfile.Refused(NamingCodes.BadRule,
                        "'" + p.Name + "' asks for " + rule.Segments.Value + " segments but names no 'separator', " +
                        "so there is no way to count them.");

                if (rule.MinLength.HasValue && rule.MaxLength.HasValue && rule.MinLength > rule.MaxLength)
                    return NamingProfile.Refused(NamingCodes.BadRule,
                        "'" + p.Name + "' asks for a minimum length above its maximum, which nothing can satisfy.");

                result.ByClass[p.Name] = rule;
            }

            return result;
        }

        private static List<string> Strings(JToken t) =>
            t is JArray a ? a.Select(x => x.ToString()).ToList() : new List<string> { t.ToString() };

        /// <summary>
        /// Judge one class of names against its rule.
        ///
        /// A class the profile says nothing about is `not_requested`. It is never
        /// `ok`: a clean report about a rule nobody wrote is the misreading this
        /// whole surface exists to prevent.
        /// </summary>
        public static NamingVerdict Check(string cls, IEnumerable<NamedThing> things, NamingProfile profile)
        {
            var v = new NamingVerdict { Class = cls };
            List<NamedThing> all = (things ?? Enumerable.Empty<NamedThing>()).Where(t => t != null).ToList();

            v.Unreadable = all.Count(t => !t.Readable);
            List<NamedThing> readable = all.Where(t => t.Readable && t.Name != null).ToList();
            v.Examined = readable.Count;

            if (profile == null || !profile.Ok || !profile.ByClass.TryGetValue(cls, out NamingRule rule))
            {
                v.Status = "not_requested";
                v.Limitation = "no rule was supplied for '" + cls + "', so nothing was judged. This is NOT a pass.";
                return v;
            }

            var seen = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (NamedThing t in readable)
            {
                if (rule.Exceptions.Contains(t.Name)) { v.Matched++; continue; }

                NamingFinding f = Judge(cls, t, rule);
                if (f == null)
                {
                    if (rule.Unique)
                    {
                        seen.TryGetValue(t.Name, out int c);
                        seen[t.Name] = c + 1;
                    }
                    v.Matched++;
                }
                else v.Findings.Add(f);
            }

            if (rule.Unique)
                foreach (KeyValuePair<string, int> kv in seen.Where(k => k.Value > 1)
                                                            .OrderBy(k => k.Key, StringComparer.Ordinal))
                {
                    v.Matched -= kv.Value;
                    v.Findings.Add(new NamingFinding
                    {
                        Class = cls,
                        Name = kv.Key,
                        Rule = NamingCodes.NotUnique,
                        Detail = kv.Value.ToString(CultureInfo.InvariantCulture) + " share this name.",
                        Suggestion = "give each one a distinguishing suffix",
                    });
                }

            if (v.Unreadable > 0)
                v.Limitation = v.Unreadable.ToString(CultureInfo.InvariantCulture) +
                               " name(s) could not be read, so the count of matches is a lower bound.";

            v.Status = v.Findings.Count == 0 ? "ok" : "failed";
            return v;
        }

        private static NamingFinding Judge(string cls, NamedThing t, NamingRule r)
        {
            string name = t.Name;

            NamingFinding F(string ruleName, string detail, string suggestion) => new NamingFinding
            { Class = cls, Id = t.Id, Name = name, Rule = ruleName, Detail = detail, Suggestion = suggestion };

            if (r.Forbidden != null && r.Forbidden.Any(x => name.IndexOf(x, StringComparison.Ordinal) >= 0))
                return F(NamingCodes.Forbidden,
                    "contains a forbidden fragment", "remove it");

            if (r.DefaultWords != null && r.DefaultWords.Any(w => name.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0))
                return F(NamingCodes.DefaultWord,
                    "still carries a default word", "name it for what it is");

            if (r.Allowed != null && !r.Allowed.Contains(name, StringComparer.Ordinal))
                return F(NamingCodes.NotAllowed,
                    "is not one of the permitted names", "use one of: " + string.Join(", ", r.Allowed));

            if (r.MinLength.HasValue && name.Length < r.MinLength.Value)
                return F(NamingCodes.TooShort,
                    "is " + name.Length + " characters and the minimum is " + r.MinLength.Value, "lengthen it");

            if (r.MaxLength.HasValue && name.Length > r.MaxLength.Value)
                return F(NamingCodes.TooLong,
                    "is " + name.Length + " characters and the maximum is " + r.MaxLength.Value,
                    name.Substring(0, r.MaxLength.Value));

            if (!string.IsNullOrEmpty(r.Prefix) && !name.StartsWith(r.Prefix, StringComparison.Ordinal))
                return F(NamingCodes.PrefixFailed, "does not start with '" + r.Prefix + "'", r.Prefix + name);

            if (!string.IsNullOrEmpty(r.Suffix) && !name.EndsWith(r.Suffix, StringComparison.Ordinal))
                return F(NamingCodes.SuffixFailed, "does not end with '" + r.Suffix + "'", name + r.Suffix);

            if (r.Segments.HasValue)
            {
                int got = name.Split(new[] { r.Separator }, StringSplitOptions.None).Length;
                if (got != r.Segments.Value)
                    return F(NamingCodes.SegmentsFailed,
                        "has " + got + " segment(s) separated by '" + r.Separator + "' and the rule asks for " +
                        r.Segments.Value, null);
            }

            if (r.Case == "upper" && name != name.ToUpperInvariant())
                return F(NamingCodes.CaseFailed, "is not upper case", name.ToUpperInvariant());
            if (r.Case == "lower" && name != name.ToLowerInvariant())
                return F(NamingCodes.CaseFailed, "is not lower case", name.ToLowerInvariant());

            if (r.Compiled != null && !r.Compiled.IsMatch(name))
                return F(NamingCodes.RegexFailed, "does not match " + r.Regex, null);

            return null;
        }
    }
}
