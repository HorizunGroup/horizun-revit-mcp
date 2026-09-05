// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// PARAMETER STANDARDS, declared rather than coded.
//
// A parameter rule is DATA. Nothing in a profile is executed: no expression, no
// script, no callback. The only caller-supplied thing that runs at all is a
// regular expression, and it runs with a timeout, because a pattern is data
// that can still cost a minute of somebody's CPU.
//
// THE DISTINCTION THIS FILE IS BUILT AROUND: a parameter is not its name.
// Two parameters called "Fire Rating" - one shared with a GUID, one a project
// parameter somebody typed - are DIFFERENT parameters, and a model full of the
// second satisfies no rule about the first. So identity is declared explicitly
// (guid, built-in, or name) and a rule keyed by GUID is NEVER satisfied by a
// name match. That single confusion is how a parameter audit reports a
// compliant model that will not schedule.
//
// THIRTEEN OUTCOMES, because "no value" has many causes and they are not
// interchangeable: missing, empty, placeholder, wrong scope, wrong binding,
// wrong guid, wrong storage, wrong specification, invalid value, unreadable,
// category not applicable, rule not requested - and present.
//
// A TYPE PARAMETER IS EVALUATED ONCE. Evaluating it per instance multiplies one
// wrong type into four hundred findings and buries everything else; the
// affected instances are kept as ids beside the single finding instead.
//
// Revit-free, so all of it is provable at a desk.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public static class ParameterOutcome
    {
        public const string Present = "parameter_present";
        public const string Missing = "parameter_missing";
        public const string Empty = "empty";
        public const string Placeholder = "placeholder";
        public const string WrongScope = "wrong_scope";
        public const string WrongBinding = "wrong_binding";
        public const string WrongGuid = "wrong_guid";
        public const string WrongStorageType = "wrong_storage_type";
        public const string WrongSpecification = "wrong_specification";
        public const string InvalidValue = "invalid_value";
        public const string Unreadable = "unreadable";
        public const string CategoryNotApplicable = "category_not_applicable";
        public const string RuleNotRequested = "rule_not_requested";

        public static readonly string[] All =
        {
            Present, Missing, Empty, Placeholder, WrongScope, WrongBinding, WrongGuid,
            WrongStorageType, WrongSpecification, InvalidValue, Unreadable,
            CategoryNotApplicable, RuleNotRequested
        };
    }

    public static class ParameterRuleCodes
    {
        public const string NoVersion = "parameter_profile_no_version";
        public const string UnknownKey = "parameter_profile_unknown_key";
        public const string BadGuid = "parameter_profile_bad_guid";
        public const string UnknownBuiltIn = "parameter_profile_unknown_builtin";
        public const string BadRegex = "parameter_profile_bad_regex";
        public const string BadRange = "parameter_profile_bad_range";
        public const string BadUnit = "parameter_profile_bad_unit";
        public const string BadStorageType = "parameter_profile_bad_storage_type";
        public const string NoIdentity = "parameter_profile_no_identity";
        public const string BadScope = "parameter_profile_bad_scope";
        public const string EmptyCategories = "parameter_profile_empty_categories";
        public const string DuplicateId = "parameter_profile_duplicate_id";
        public const string BadRule = "parameter_profile_bad_rule";
        public const string Absent = "parameter_profile_absent";
    }

    public static class ParameterScope
    {
        public const string Instance = "instance";
        public const string Type = "type";
    }

    public sealed class ParameterRule
    {
        public string Id;
        public string Name;
        public string Guid;
        public string BuiltIn;
        public string Scope = ParameterScope.Instance;
        public List<string> Categories = new List<string>();
        public List<string> ElementClasses = new List<string>();
        public bool Required = true;
        public bool AllowEmpty;
        public List<string> Placeholders = new List<string>();
        public string StorageType;
        public string Specification;
        public string ExpectedBinding;
        public Regex Pattern;
        public string PatternSource;
        public List<string> AllowedValues;
        public List<string> ForbiddenValues;
        public double? Min, Max;
        public string Unit;
        public HashSet<string> Exceptions = new HashSet<string>(StringComparer.Ordinal);
        public string Severity;
        public string Explanation;

        /// <summary>True when the rule pins a GUID, which a name can never satisfy.</summary>
        public bool KeyedByGuid { get { return !string.IsNullOrEmpty(Guid); } }
    }

    public sealed class ParameterProfile
    {
        public bool Ok;
        public bool Absent;
        public string Code;
        public string Message;
        public string Version;
        public List<ParameterRule> Rules = new List<ParameterRule>();
    }

    /// <summary>One parameter as the model reports it, on one element or type.</summary>
    public sealed class ParameterObservation
    {
        public long ElementId;
        public string Category;
        public string ElementClass;
        /// <summary>True when this observation is of a TYPE rather than an instance.</summary>
        public bool IsType;
        /// <summary>Instances affected when this is a type observation. Kept, not multiplied.</summary>
        public List<long> AffectedInstanceIds = new List<long>();

        public bool Present;
        public bool Readable = true;
        public string Guid;
        public bool IsShared;
        public string StorageType;
        public string Specification;
        public string Binding;
        public string ValueAsString;
        public double? ValueAsDouble;
        public bool HasValue;
    }

    public sealed class ParameterVerdict
    {
        public string RuleId;
        public string Outcome;
        public long ElementId;
        public bool IsType;
        public int AffectedInstances;
        public string Detail;
        public string Severity;
    }

    public static class ParameterStandardRules
    {
        /// <summary>
        /// A caller's regex runs against every observed value. Unbounded, a
        /// pathological pattern spends minutes inside one scan; this keeps a
        /// profile data rather than a way to occupy the machine.
        /// </summary>
        public static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);

        public const string IdentityMeans =
            "a parameter is NOT its name. Two parameters called the same thing - one shared with a GUID, one a " +
            "project parameter somebody typed - are different parameters, and a rule keyed by GUID is never " +
            "satisfied by a name match. A model full of the wrong one looks compliant and will not schedule.";

        public const string TypeEvaluationMeans =
            "a TYPE parameter is judged once per type, not once per instance. The instances are reported as a " +
            "count and a list of ids beside the single finding, so one wrong type does not become four hundred " +
            "findings that bury everything else.";

        public const string NothingIsExecuted =
            "profiles are data. No expression, script or code from a profile is executed. The only " +
            "caller-supplied thing that runs is a regular expression, and it runs with a timeout.";

        // ------------------------------------------------------------- profile

        public static ParameterProfile Read(JToken token, Func<string, bool> builtInExists)
        {
            var p = new ParameterProfile();
            if (token == null || token.Type == JTokenType.Null)
            {
                p.Absent = true;
                p.Code = ParameterRuleCodes.Absent;
                p.Message = "no parameter profile was supplied, so every rule is rule_not_requested. That is " +
                            "NOT a pass: which parameters a project requires is one organisation's decision " +
                            "and none is compiled in here.";
                return p;
            }

            var o = token as JObject;
            if (o == null) return Bad(p, ParameterRuleCodes.BadRule, "the parameter profile must be an object.");

            JToken v = o["version"];
            if (v == null || string.IsNullOrWhiteSpace(v.Value<string>()))
                return Bad(p, ParameterRuleCodes.NoVersion, "the parameter profile needs a 'version'.");
            p.Version = v.Value<string>();

            var rulesToken = o["rules"] as JArray;
            if (rulesToken == null)
                return Bad(p, ParameterRuleCodes.BadRule, "the parameter profile needs a 'rules' array.");

            foreach (JProperty prop in o.Properties())
                if (prop.Name != "version" && prop.Name != "rules")
                    return Bad(p, ParameterRuleCodes.UnknownKey,
                        "'" + prop.Name + "' is not a key this profile defines. Known keys: version, rules.");

            var seenIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (JToken t in rulesToken)
            {
                var body = t as JObject;
                if (body == null) return Bad(p, ParameterRuleCodes.BadRule, "every rule must be an object.");

                var r = new ParameterRule();
                foreach (JProperty f in body.Properties())
                {
                    switch (f.Name)
                    {
                        case "id": r.Id = f.Value.Value<string>(); break;
                        case "name": r.Name = f.Value.Value<string>(); break;
                        case "guid": r.Guid = f.Value.Value<string>(); break;
                        case "built_in_parameter": r.BuiltIn = f.Value.Value<string>(); break;
                        case "scope": r.Scope = f.Value.Value<string>(); break;
                        case "expected_binding": r.ExpectedBinding = f.Value.Value<string>(); break;
                        case "storage_type": r.StorageType = f.Value.Value<string>(); break;
                        case "specification": r.Specification = f.Value.Value<string>(); break;
                        case "unit": r.Unit = f.Value.Value<string>(); break;
                        case "severity": r.Severity = f.Value.Value<string>(); break;
                        case "explanation": r.Explanation = f.Value.Value<string>(); break;
                        case "required": r.Required = f.Value.Value<bool>(); break;
                        case "allow_empty": r.AllowEmpty = f.Value.Value<bool>(); break;
                        case "regex": r.PatternSource = f.Value.Value<string>(); break;
                        case "minimum": r.Min = f.Value.Value<double>(); break;
                        case "maximum": r.Max = f.Value.Value<double>(); break;
                        case "categories": r.Categories = Strings(f); break;
                        case "element_classes": r.ElementClasses = Strings(f); break;
                        case "placeholders": r.Placeholders = Strings(f); break;
                        case "allowed_values": r.AllowedValues = Strings(f); break;
                        case "forbidden_values": r.ForbiddenValues = Strings(f); break;
                        case "exceptions":
                            foreach (string e in Strings(f)) r.Exceptions.Add(e);
                            break;
                        default:
                            return Bad(p, ParameterRuleCodes.UnknownKey,
                                "'" + f.Name + "' is not a key a parameter rule defines.");
                    }
                }

                if (string.IsNullOrWhiteSpace(r.Id))
                    return Bad(p, ParameterRuleCodes.BadRule, "every rule needs an 'id'.");
                if (!seenIds.Add(r.Id))
                    return Bad(p, ParameterRuleCodes.DuplicateId,
                        "two rules share the id '" + r.Id + "'. A report keyed on a duplicated id cannot say " +
                        "which rule produced a finding.");

                // IDENTITY IS MANDATORY. A rule that names no parameter matches
                // everything or nothing depending on how it is read, and either way
                // the report is meaningless.
                if (string.IsNullOrWhiteSpace(r.Name) && string.IsNullOrWhiteSpace(r.Guid) &&
                    string.IsNullOrWhiteSpace(r.BuiltIn))
                    return Bad(p, ParameterRuleCodes.NoIdentity,
                        "rule '" + r.Id + "' names no parameter. Give it a name, a guid or a " +
                        "built_in_parameter.");

                if (r.Guid != null)
                {
                    Guid parsed;
                    if (!System.Guid.TryParse(r.Guid, out parsed))
                        return Bad(p, ParameterRuleCodes.BadGuid,
                            "rule '" + r.Id + "' has a guid that is not a guid.");
                    r.Guid = parsed.ToString("D");
                }

                if (r.BuiltIn != null && builtInExists != null && !builtInExists(r.BuiltIn))
                    return Bad(p, ParameterRuleCodes.UnknownBuiltIn,
                        "'" + r.BuiltIn + "' is not a BuiltInParameter this Revit has. A rule pinned to a " +
                        "name nothing matches never runs and reports every element as acceptable.");

                if (r.Scope != ParameterScope.Instance && r.Scope != ParameterScope.Type)
                    return Bad(p, ParameterRuleCodes.BadScope,
                        "rule '" + r.Id + "' has scope '" + r.Scope + "'; it must be instance or type.");

                if (r.ExpectedBinding != null && r.ExpectedBinding != ParameterScope.Instance &&
                    r.ExpectedBinding != ParameterScope.Type)
                    return Bad(p, ParameterRuleCodes.BadScope,
                        "rule '" + r.Id + "' expects binding '" + r.ExpectedBinding + "'.");

                // A rule that reads a TYPE parameter but expects an INSTANCE binding
                // contradicts itself and can never be satisfied by any model.
                if (r.ExpectedBinding != null && r.ExpectedBinding != r.Scope)
                    return Bad(p, ParameterRuleCodes.BadScope,
                        "rule '" + r.Id + "' reads the " + r.Scope + " but expects a " + r.ExpectedBinding +
                        " binding. Nothing can satisfy both.");

                if (r.StorageType != null &&
                    !new[] { "String", "Integer", "Double", "ElementId" }.Contains(r.StorageType))
                    return Bad(p, ParameterRuleCodes.BadStorageType,
                        "rule '" + r.Id + "' has storage_type '" + r.StorageType + "'.");

                // A numeric bound on a text parameter is a contradiction, not a
                // stricter rule.
                if ((r.Min.HasValue || r.Max.HasValue) && r.StorageType == "String")
                    return Bad(p, ParameterRuleCodes.BadRange,
                        "rule '" + r.Id + "' sets a numeric range on a String parameter.");

                if (r.Min.HasValue && r.Max.HasValue && r.Min > r.Max)
                    return Bad(p, ParameterRuleCodes.BadRange,
                        "rule '" + r.Id + "' has a minimum above its maximum, so nothing can satisfy it.");

                if (r.Unit != null && r.StorageType != null && r.StorageType != "Double")
                    return Bad(p, ParameterRuleCodes.BadUnit,
                        "rule '" + r.Id + "' declares a unit on a " + r.StorageType + " parameter; only a " +
                        "Double carries one.");

                if (r.PatternSource != null)
                {
                    try { r.Pattern = new Regex(r.PatternSource, RegexOptions.None, RegexTimeout); }
                    catch (Exception ex)
                    {
                        return Bad(p, ParameterRuleCodes.BadRegex,
                            "rule '" + r.Id + "' has an invalid regex (" + ex.Message + "). It is REFUSED " +
                            "rather than skipped: a rule that silently does not run reports every value as " +
                            "acceptable.");
                    }
                }

                if (r.Categories != null && r.Categories.Count == 0 && r.ElementClasses.Count == 0)
                    return Bad(p, ParameterRuleCodes.EmptyCategories,
                        "rule '" + r.Id + "' applies to no category and no element class, so it would never " +
                        "run. Name at least one.");

                p.Rules.Add(r);
            }

            p.Ok = true;
            return p;
        }

        private static ParameterProfile Bad(ParameterProfile p, string code, string message)
        {
            p.Ok = false;
            p.Code = code;
            p.Message = message;
            return p;
        }

        private static List<string> Strings(JProperty f)
        {
            var arr = f.Value as JArray;
            if (arr == null) return new List<string>();
            return arr.Where(t => t.Type == JTokenType.String).Select(t => t.Value<string>()).ToList();
        }

        // ---------------------------------------------------------- evaluation

        /// <summary>
        /// Judges one observation against one rule. A refused profile never gets
        /// here: Evaluate refuses the whole set, because Read fills its rules as it
        /// parses and enforcing what it collected would judge a model against a
        /// profile the caller was told had been rejected.
        /// </summary>
        public static ParameterVerdict Evaluate(ParameterRule r, ParameterObservation o)
        {
            if (r == null || o == null) return null;

            var v = new ParameterVerdict
            {
                RuleId = r == null ? null : r.Id,
                ElementId = o.ElementId,
                IsType = o.IsType,
                AffectedInstances = o.AffectedInstanceIds == null ? 0 : o.AffectedInstanceIds.Count,
                Severity = r.Severity
            };

            if (r.Exceptions.Contains(o.ElementId.ToString(CultureInfo.InvariantCulture)))
                return Set(v, ParameterOutcome.RuleNotRequested, "this element is an explicit exception.");

            // The rule applies to categories and classes the caller named. Anything
            // else is NOT a pass and NOT a failure.
            bool categoryHit = r.Categories.Count == 0 ||
                               (o.Category != null &&
                                r.Categories.Any(c => string.Equals(c, o.Category, StringComparison.OrdinalIgnoreCase)));
            bool classHit = r.ElementClasses.Count == 0 ||
                            (o.ElementClass != null &&
                             r.ElementClasses.Any(c => string.Equals(c, o.ElementClass, StringComparison.Ordinal)));
            if (!categoryHit || !classHit)
                return Set(v, ParameterOutcome.CategoryNotApplicable,
                    "this rule does not apply to '" + (o.Category ?? "(no category)") + "'.");

            // SCOPE BEFORE EVERYTHING. A type parameter read on an instance is not a
            // missing parameter; it is the wrong place to have looked.
            if ((r.Scope == ParameterScope.Type) != o.IsType)
                return Set(v, ParameterOutcome.WrongScope,
                    "the rule reads the " + r.Scope + " and this observation is of " +
                    (o.IsType ? "a type" : "an instance") + ".");

            if (!o.Readable)
                return Set(v, ParameterOutcome.Unreadable,
                    "the parameter read threw, so its state is unknown - not missing and not satisfied.");

            if (!o.Present)
                return Set(v, r.Required ? ParameterOutcome.Missing : ParameterOutcome.RuleNotRequested,
                    r.Required ? "the parameter does not exist on this element."
                               : "the parameter is absent and the rule does not require it.");

            // A GUID RULE IS NEVER SATISFIED BY A NAME. This is the check the whole
            // file exists for.
            if (r.KeyedByGuid && !string.Equals(r.Guid, o.Guid, StringComparison.OrdinalIgnoreCase))
                return Set(v, ParameterOutcome.WrongGuid,
                    "a parameter of this name exists, but its guid is '" + (o.Guid ?? "(none - not shared)") +
                    "' and the rule pins '" + r.Guid + "'. These are different parameters.");

            if (r.ExpectedBinding != null && o.Binding != null &&
                !string.Equals(r.ExpectedBinding, o.Binding, StringComparison.Ordinal))
                return Set(v, ParameterOutcome.WrongBinding,
                    "bound as " + o.Binding + " where the rule expects " + r.ExpectedBinding + ".");

            if (r.StorageType != null && o.StorageType != null &&
                !string.Equals(r.StorageType, o.StorageType, StringComparison.Ordinal))
                return Set(v, ParameterOutcome.WrongStorageType,
                    "stores " + o.StorageType + " where the rule expects " + r.StorageType + ".");

            if (r.Specification != null && o.Specification != null &&
                !string.Equals(r.Specification, o.Specification, StringComparison.Ordinal))
                return Set(v, ParameterOutcome.WrongSpecification,
                    "its specification is '" + o.Specification + "' where the rule expects '" +
                    r.Specification + "'.");

            string value = o.ValueAsString;
            bool empty = !o.HasValue || string.IsNullOrWhiteSpace(value);
            if (empty)
                return Set(v, r.AllowEmpty ? ParameterOutcome.Present : ParameterOutcome.Empty,
                    r.AllowEmpty ? "empty, which this rule allows."
                                 : "the parameter exists and is empty. Present is not the same as filled.");

            // A PLACEHOLDER IS NOT A VALUE, and it is not an empty either - somebody
            // typed it, which is a different problem from nobody typing anything.
            if (r.Placeholders.Any(ph => string.Equals(ph, value, StringComparison.OrdinalIgnoreCase)))
                return Set(v, ParameterOutcome.Placeholder,
                    "the value is '" + value + "', which you declared to be a placeholder.");

            if (r.ForbiddenValues != null &&
                r.ForbiddenValues.Any(x => string.Equals(x, value, StringComparison.Ordinal)))
                return Set(v, ParameterOutcome.InvalidValue, "'" + value + "' is a forbidden value.");

            if (r.AllowedValues != null && r.AllowedValues.Count > 0 &&
                !r.AllowedValues.Any(x => string.Equals(x, value, StringComparison.Ordinal)))
                return Set(v, ParameterOutcome.InvalidValue, "'" + value + "' is not an allowed value.");

            if (r.Pattern != null)
            {
                try
                {
                    if (!r.Pattern.IsMatch(value))
                        return Set(v, ParameterOutcome.InvalidValue,
                            "'" + value + "' does not match the required pattern.");
                }
                catch (RegexMatchTimeoutException)
                {
                    // The pattern outran its budget. That is unknown, not invalid.
                    return Set(v, ParameterOutcome.Unreadable,
                        "the caller's regex exceeded its time budget on this value, so the value was not judged.");
                }
            }

            if (r.Min.HasValue || r.Max.HasValue)
            {
                if (!o.ValueAsDouble.HasValue)
                    return Set(v, ParameterOutcome.Unreadable,
                        "a numeric range was declared and this value could not be read as a number.");
                if (r.Min.HasValue && o.ValueAsDouble.Value < r.Min.Value)
                    return Set(v, ParameterOutcome.InvalidValue,
                        o.ValueAsDouble.Value + " is below the minimum " + r.Min.Value + ".");
                if (r.Max.HasValue && o.ValueAsDouble.Value > r.Max.Value)
                    return Set(v, ParameterOutcome.InvalidValue,
                        o.ValueAsDouble.Value + " is above the maximum " + r.Max.Value + ".");
            }

            return Set(v, ParameterOutcome.Present, value);
        }

        private static ParameterVerdict Set(ParameterVerdict v, string outcome, string detail)
        {
            v.Outcome = outcome;
            v.Detail = detail;
            return v;
        }

        /// <summary>
        /// Judges a whole population. A refused or absent profile judges NOTHING -
        /// the empty result means "not checked", never "all clean".
        /// </summary>
        public static List<ParameterVerdict> Evaluate(IEnumerable<ParameterObservation> observations,
                                                      ParameterProfile p)
        {
            var verdicts = new List<ParameterVerdict>();
            if (observations == null || p == null || !p.Ok) return verdicts;

            foreach (ParameterObservation o in observations)
                foreach (ParameterRule r in p.Rules)
                {
                    ParameterVerdict v = Evaluate(r, o);
                    if (v != null) verdicts.Add(v);
                }
            return verdicts;
        }

        /// <summary>The thirteen outcomes, each counted, none folded into another.</summary>
        public static JObject Tally(IEnumerable<ParameterVerdict> verdicts)
        {
            var counts = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (string o in ParameterOutcome.All) counts[o] = 0;
            foreach (ParameterVerdict v in verdicts ?? Enumerable.Empty<ParameterVerdict>())
                if (v != null && v.Outcome != null && counts.ContainsKey(v.Outcome)) counts[v.Outcome]++;

            var o2 = new JObject();
            foreach (string k in ParameterOutcome.All) o2[k] = counts[k];
            o2["identity_means"] = IdentityMeans;
            o2["type_evaluation_means"] = TypeEvaluationMeans;
            o2["nothing_is_executed"] = NothingIsExecuted;
            return o2;
        }

        public static JObject ToJson(ParameterVerdict v)
        {
            if (v == null) return null;
            return new JObject
            {
                ["rule_id"] = v.RuleId,
                ["outcome"] = v.Outcome,
                ["element_id"] = v.ElementId,
                ["is_type"] = v.IsType,
                ["affected_instances"] = v.IsType ? (JToken)v.AffectedInstances : null,
                ["severity"] = v.Severity,
                ["detail"] = v.Detail
            };
        }
    }
}
