// -----------------------------------------------------------------------------
// Horizun Revit MCP - the PLANIMETRY requirement set: a documentation standard,
// as data, with no Revit and no company in it.
//
// Core/RequirementSet.cs is the element/parameter half of the same idea and is
// left exactly as it is - old sets keep loading, and its tests keep passing.
// This is its sibling for the documentation surface, because the entities are
// different in kind: a sheet is not an element with a parameter, a viewport
// overlap is not an assertion about a value, and "these categories must be
// tagged in this view" has no expression in the element schema at all.
//
// Everything the loader refuses, it refuses because the silent alternative
// reports a clean model:
//
//   * A RULE THAT MATCHES EVERYTHING BY ACCIDENT. A selector with no fields is
//     refused unless the author wrote applies_to_all: true. The legitimate case
//     ("every sheet needs a titleblock of an allowed type") stays expressible;
//     the accident - an empty selector left behind by an edit - stops being
//     indistinguishable from it.
//   * AN OPERATOR NOBODY IMPLEMENTED, a field that does not exist on the entity,
//     a regex that does not compile, a duplicated rule id, a set with no id or
//     no version. Each of those would become a rule that quietly does not run,
//     and a rule that does not run reports no findings, which reads as a pass.
//   * A REGEX THAT NEVER RETURNS. Every pattern is compiled with an explicit
//     match timeout, and the timeout is a finding on that rule rather than an
//     exception that takes the audit down.
//
// The set's SHA-256 travels in the answer and in every finding, so a report can
// prove WHICH document produced it.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    /// <summary>A malformed planimetry requirement set. The message IS the refusal.</summary>
    public sealed class PlanimetryRequirementSetException : Exception
    {
        public PlanimetryRequirementSetException(string message) : base(message) { }
    }

    /// <summary>One selector predicate: a field of the entity, compared one way.</summary>
    public sealed class PlanimetrySelector
    {
        public string Field;
        public string Operator;          // matches | equals | in_list | applies_to
        public JToken Value;
        public Regex Pattern;            // compiled when Operator == matches
    }

    /// <summary>
    /// One category a requires_tag rule demands, with its EXCLUSIONS. Exclusions matter
    /// because "every door must be tagged" is never true of every door: the ones inside a
    /// linked model, the ones of a type the project does not schedule, the ones already
    /// marked as excluded by a parameter. Without them the rule produces noise and gets
    /// switched off, which is worse than not having it.
    /// </summary>
    public sealed class TagRequirement
    {
        public string Category;
        public List<string> ExcludeTypes = new List<string>();
        public List<string> ExcludeFamilies = new List<string>();
        public Regex ExcludeTypeMatches;
        public string ExcludeWhenParameterSet;
    }

    public sealed class PlanimetryRule
    {
        public string Id;
        public string Entity;
        public bool Blocking;
        public bool AppliesToAll;
        public List<PlanimetrySelector> Selectors = new List<PlanimetrySelector>();

        public string AssertionField;
        public string Operator;
        public JToken Value;
        public Regex Pattern;            // compiled when Operator is a regex one
        public string Message;

        /// <summary>Parsed requires_tag value. Empty for every other operator.</summary>
        public List<TagRequirement> TagRequirements = new List<TagRequirement>();
    }

    public sealed class PlanimetryRequirementSet
    {
        public string Id;
        public string Version;
        public string Title;
        public string Sha256;
        public List<PlanimetryRule> Rules = new List<PlanimetryRule>();

        /// <summary>
        /// The categories any requires_tag rule asked about, so the inventory knows - BEFORE
        /// it walks the model - whether it must do the expensive per-view visible-element
        /// pass at all. A tag-coverage rule that arrived after collection would have to
        /// answer over data nobody gathered, and "nothing gathered" must never look like
        /// "nothing untagged".
        /// </summary>
        public List<string> TagCoverageCategories = new List<string>();

        /// <summary>Parameters an exclusion asked about, so the inventory reads those and no
        /// others off the untagged elements it lists.</summary>
        public List<string> TagCoverageExcludeParameters = new List<string>();

        // ---- limits, published in the refusal text so an author can act on it ----
        public const int MaxRules = 200;
        public const int MaxDocumentChars = 262144;          // 256 KB of JSON
        public const int MaxSelectorsPerRule = 12;
        public const int MaxListValues = 500;
        public static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);

        private static readonly HashSet<string> TopLevelKeys = new HashSet<string>(StringComparer.Ordinal)
        { "requirement_set", "rules" };

        private static readonly HashSet<string> HeaderKeys = new HashSet<string>(StringComparer.Ordinal)
        { "id", "version", "title", "scope" };

        private static readonly HashSet<string> RuleKeys = new HashSet<string>(StringComparer.Ordinal)
        { "id", "entity", "severity", "selector", "assertion", "message" };

        private static readonly HashSet<string> AssertionKeys = new HashSet<string>(StringComparer.Ordinal)
        { "field", "operator", "value" };

        /// <summary>The entity kinds a rule may be written about. A rule naming anything else
        /// is refused, because it would silently select nothing.</summary>
        public static readonly string[] Entities =
        {
            "sheet", "view", "viewport", "schedule_placement", "dimension", "tag",
            "text_note", "detail_2d", "view_reference"
        };

        /// <summary>
        /// WHICH FIELDS EACH ENTITY HAS. This table is the reason a typo in a field name is
        /// a refusal instead of a rule that examines nothing. `parameter:<name>` is accepted
        /// on sheet and view as an open extension - a project parameter is not knowable here.
        /// </summary>
        public static readonly Dictionary<string, string[]> Fields =
            new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            { "sheet", new[] { "sheet_number", "name", "placeholder", "titleblock_type",
                               "titleblock_family", "titleblock_count", "revision_count",
                               "viewport_count", "schedule_placement_count" } },
            { "view", new[] { "name", "view_type", "discipline", "detail_level", "scale",
                              "template_name", "template_id", "level", "phase", "phase_filter",
                              "crop_box_active", "placed_on_sheet", "is_template", "sheet_count" } },
            { "viewport", new[] { "sheet_number", "view_name", "viewport_type", "detail_number",
                                  "title", "rotation", "pinned" } },
            { "schedule_placement", new[] { "sheet_number", "schedule_name", "pinned" } },
            { "dimension", new[] { "type", "family", "owner_view_name", "value_override",
                                   "has_value_override", "references_available", "segment_count",
                                   "has_view_overrides" } },
            { "tag", new[] { "type", "family", "owner_view_name", "target_categories",
                             "orphaned", "has_leader", "has_view_overrides" } },
            { "text_note", new[] { "type", "owner_view_name", "text", "alignment", "width",
                                   "has_view_overrides" } },
            { "detail_2d", new[] { "category", "type", "family", "owner_view_name",
                                   "has_view_overrides" } },
            { "view_reference", new[] { "kind", "owner_view_name", "target_view_name",
                                        "target_state", "target_placed" } }
        };

        /// <summary>Operators that compare a named FIELD, and whether each needs a value.</summary>
        private static readonly Dictionary<string, bool> FieldOperators =
            new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            { "matches", true }, { "not_matches", true },
            { "equals", true }, { "not_equals", true },
            { "in_list", true }, { "not_in_list", true },
            { "required", false }, { "not_empty", false },
            { "greater_than", true }, { "less_than", true }, { "between", true }
        };

        /// <summary>
        /// Operators that carry their OWN semantics and take no field: they are about the
        /// entity as a whole (its geometry, its type, the elements around it). Declaring a
        /// field alongside one is refused rather than ignored.
        /// </summary>
        private static readonly Dictionary<string, bool> WholeEntityOperators =
            new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            { "minimum_gap", true }, { "inside_extent", true },
            { "allowed_type", true }, { "allowed_template", true }, { "allowed_scale", true },
            { "required_parameter", true }, { "forbid_numeric_override", false },
            { "requires_tag", true }
        };

        /// <summary>Which entities each whole-entity operator is meaningful for. An operator
        /// aimed at an entity it cannot measure is a refusal, not a rule that never fires.</summary>
        private static readonly Dictionary<string, string[]> WholeEntityScope =
            new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            { "minimum_gap", new[] { "viewport", "schedule_placement" } },
            { "inside_extent", new[] { "viewport", "schedule_placement" } },
            { "allowed_type", new[] { "sheet", "viewport", "dimension", "tag", "text_note", "detail_2d" } },
            { "allowed_template", new[] { "view" } },
            { "allowed_scale", new[] { "view" } },
            { "required_parameter", new[] { "sheet", "view" } },
            { "forbid_numeric_override", new[] { "dimension" } },
            { "requires_tag", new[] { "view" } }
        };

        public static bool IsWholeEntityOperator(string op)
        {
            return op != null && WholeEntityOperators.ContainsKey(op);
        }

        /// <summary>
        /// Parse and validate an INLINE requirement set. There is no path argument anywhere
        /// on this surface on purpose: a read-only audit tool that opens arbitrary files on
        /// the machine is a file reader wearing an auditor's name.
        /// </summary>
        public static PlanimetryRequirementSet Load(JObject doc)
        {
            if (doc == null)
                throw new PlanimetryRequirementSetException("The requirement set is empty.");

            string raw = doc.ToString(Newtonsoft.Json.Formatting.None);
            if (raw.Length > MaxDocumentChars)
                throw new PlanimetryRequirementSetException(
                    "The requirement set is " + raw.Length + " characters; the limit is " + MaxDocumentChars +
                    ". Split it, or narrow its scope - a document this size is not a standard, it is a program.");

            foreach (JProperty prop in doc.Properties())
                if (!TopLevelKeys.Contains(prop.Name))
                    throw new PlanimetryRequirementSetException(
                        "Unknown top-level key '" + prop.Name + "'. Known: " +
                        string.Join(", ", TopLevelKeys.OrderBy(x => x, StringComparer.Ordinal)) +
                        ". A misspelt section would otherwise be a section that silently does not run.");

            var set = new PlanimetryRequirementSet();
            JObject header = doc["requirement_set"] as JObject;
            if (header == null)
                throw new PlanimetryRequirementSetException("requirement_set (id, version, title) is required.");
            foreach (JProperty prop in header.Properties())
                if (!HeaderKeys.Contains(prop.Name))
                    throw new PlanimetryRequirementSetException(
                        "Unknown requirement_set key '" + prop.Name + "'. Known: " +
                        string.Join(", ", HeaderKeys.OrderBy(x => x, StringComparer.Ordinal)) + ".");

            set.Id = header.Value<string>("id");
            set.Version = header.Value<string>("version");
            set.Title = header.Value<string>("title");
            if (string.IsNullOrWhiteSpace(set.Id))
                throw new PlanimetryRequirementSetException(
                    "requirement_set.id is required; every finding is keyed by it.");
            if (string.IsNullOrWhiteSpace(set.Version))
                throw new PlanimetryRequirementSetException(
                    "requirement_set.version is required; a finding always cites the version that produced it.");

            JArray rules = doc["rules"] as JArray;
            if (rules == null || rules.Count == 0)
                throw new PlanimetryRequirementSetException(
                    "rules is required and must be non-empty. A set with no rules examines nothing, and " +
                    "'examined nothing' must never look like 'passed'.");
            if (rules.Count > MaxRules)
                throw new PlanimetryRequirementSetException(
                    "The set carries " + rules.Count + " rules; the limit is " + MaxRules + ".");

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (JToken r in rules)
            {
                JObject rj = r as JObject;
                if (rj == null) throw new PlanimetryRequirementSetException("Every rules entry must be an object.");
                set.Rules.Add(ParseRule(rj, seen, set));
            }

            set.Sha256 = RequestFingerprint.Sha256Hex(RequestFingerprint.Canonical(doc));
            return set;
        }

        private static PlanimetryRule ParseRule(JObject rj, HashSet<string> seen, PlanimetryRequirementSet set)
        {
            foreach (JProperty prop in rj.Properties())
                if (!RuleKeys.Contains(prop.Name))
                    throw new PlanimetryRequirementSetException(
                        "Unknown rule key '" + prop.Name + "'. Known: " +
                        string.Join(", ", RuleKeys.OrderBy(x => x, StringComparer.Ordinal)) + ".");

            var rule = new PlanimetryRule { Id = rj.Value<string>("id") };
            if (string.IsNullOrWhiteSpace(rule.Id))
                throw new PlanimetryRequirementSetException(
                    "A rule without an id cannot be reported on; findings are keyed by it.");
            if (!seen.Add(rule.Id))
                throw new PlanimetryRequirementSetException(
                    "Rule id '" + rule.Id + "' is duplicated. Two rules under one id produce findings nobody " +
                    "can act on, because the id no longer identifies which rule failed.");

            rule.Entity = rj.Value<string>("entity");
            if (string.IsNullOrWhiteSpace(rule.Entity) || !Entities.Contains(rule.Entity, StringComparer.Ordinal))
                throw new PlanimetryRequirementSetException(
                    "Rule '" + rule.Id + "' entity '" + (rule.Entity ?? "(none)") + "' is unknown. Known: " +
                    string.Join(", ", Entities) + ".");

            string severity = rj.Value<string>("severity") ?? "advisory";
            if (severity != "blocking" && severity != "advisory")
                throw new PlanimetryRequirementSetException(
                    "Rule '" + rule.Id + "' severity must be blocking or advisory.");
            rule.Blocking = severity == "blocking";
            rule.Message = rj.Value<string>("message");

            ParseSelector(rj["selector"], rule);
            ParseAssertion(rj["assertion"] as JObject, rule, set);
            return rule;
        }

        private static void ParseSelector(JToken sel, PlanimetryRule rule)
        {
            JObject so = sel as JObject;
            if (sel != null && so == null)
                throw new PlanimetryRequirementSetException("Rule '" + rule.Id + "' selector must be an object.");

            if (so == null || !so.Properties().Any())
                throw new PlanimetryRequirementSetException(
                    "Rule '" + rule.Id + "' has no selector. A rule that matches EVERY " + rule.Entity +
                    " is legitimate but must say so: write \"selector\": { \"applies_to_all\": true }. " +
                    "Refused otherwise, because an empty selector left behind by an edit is indistinguishable " +
                    "from one that was meant.");

            string[] fields = Fields[rule.Entity];
            foreach (JProperty prop in so.Properties())
            {
                if (prop.Name == "applies_to_all")
                {
                    if (prop.Value.Type != JTokenType.Boolean)
                        throw new PlanimetryRequirementSetException(
                            "Rule '" + rule.Id + "' selector.applies_to_all must be a boolean.");
                    rule.AppliesToAll = (bool)prop.Value;
                    continue;
                }
                if (prop.Name == "applies_to")
                {
                    JArray ids = prop.Value as JArray;
                    if (ids == null || ids.Count == 0)
                        throw new PlanimetryRequirementSetException(
                            "Rule '" + rule.Id + "' selector.applies_to must be a non-empty list of element ids.");
                    if (ids.Count > MaxListValues)
                        throw new PlanimetryRequirementSetException(
                            "Rule '" + rule.Id + "' selector.applies_to carries " + ids.Count +
                            " ids; the limit is " + MaxListValues + ".");
                    foreach (JToken id in ids)
                        if (id.Type != JTokenType.Integer)
                            throw new PlanimetryRequirementSetException(
                                "Rule '" + rule.Id + "' selector.applies_to must contain integers only.");
                    rule.Selectors.Add(new PlanimetrySelector
                    { Field = "element_id", Operator = "applies_to", Value = ids });
                    continue;
                }

                // field_matches / field_equals / field_in
                string field, op;
                if (prop.Name.EndsWith("_matches", StringComparison.Ordinal))
                { field = prop.Name.Substring(0, prop.Name.Length - 8); op = "matches"; }
                else if (prop.Name.EndsWith("_in", StringComparison.Ordinal))
                { field = prop.Name.Substring(0, prop.Name.Length - 3); op = "in_list"; }
                else
                { field = prop.Name; op = "equals"; }

                if (!IsKnownField(rule.Entity, fields, field))
                    throw new PlanimetryRequirementSetException(
                        "Rule '" + rule.Id + "' selector '" + prop.Name + "' names field '" + field +
                        "', which a " + rule.Entity + " does not have. Known fields: " +
                        string.Join(", ", fields) +
                        (SupportsParameters(rule.Entity) ? ", parameter:<name>" : "") +
                        ". Refused rather than ignored: an unknown field selects nothing, and a rule that " +
                        "selects nothing reports a clean model.");

                var selector = new PlanimetrySelector { Field = field, Operator = op, Value = prop.Value };
                if (op == "matches")
                {
                    if (prop.Value.Type != JTokenType.String)
                        throw new PlanimetryRequirementSetException(
                            "Rule '" + rule.Id + "' selector '" + prop.Name + "' must be a regex string.");
                    selector.Pattern = Compile(rule.Id, prop.Name, (string)prop.Value);
                }
                else if (op == "in_list")
                {
                    JArray list = prop.Value as JArray;
                    if (list == null || list.Count == 0)
                        throw new PlanimetryRequirementSetException(
                            "Rule '" + rule.Id + "' selector '" + prop.Name + "' must be a non-empty list.");
                    if (list.Count > MaxListValues)
                        throw new PlanimetryRequirementSetException(
                            "Rule '" + rule.Id + "' selector '" + prop.Name + "' carries " + list.Count +
                            " values; the limit is " + MaxListValues + ".");
                }
                rule.Selectors.Add(selector);
                if (rule.Selectors.Count > MaxSelectorsPerRule)
                    throw new PlanimetryRequirementSetException(
                        "Rule '" + rule.Id + "' carries more than " + MaxSelectorsPerRule + " selector predicates.");
            }

            if (rule.Selectors.Count == 0 && !rule.AppliesToAll)
                throw new PlanimetryRequirementSetException(
                    "Rule '" + rule.Id + "' selector carries applies_to_all: false and nothing else, so it " +
                    "selects nothing at all. Remove the rule, or give it a predicate.");
        }

        private static void ParseAssertion(JObject a, PlanimetryRule rule, PlanimetryRequirementSet set)
        {
            if (a == null)
                throw new PlanimetryRequirementSetException("Rule '" + rule.Id + "' has no assertion.");
            foreach (JProperty prop in a.Properties())
                if (!AssertionKeys.Contains(prop.Name))
                    throw new PlanimetryRequirementSetException(
                        "Unknown assertion key '" + prop.Name + "' in rule '" + rule.Id + "'. Known: " +
                        string.Join(", ", AssertionKeys.OrderBy(x => x, StringComparer.Ordinal)) + ".");

            rule.Operator = a.Value<string>("operator");
            if (string.IsNullOrWhiteSpace(rule.Operator))
                throw new PlanimetryRequirementSetException("Rule '" + rule.Id + "' assertion has no operator.");

            bool isField = FieldOperators.ContainsKey(rule.Operator);
            bool isWhole = WholeEntityOperators.ContainsKey(rule.Operator);
            if (!isField && !isWhole)
                throw new PlanimetryRequirementSetException(
                    "Rule '" + rule.Id + "' operator '" + rule.Operator + "' is unknown. Known: " +
                    string.Join(", ", FieldOperators.Keys.Concat(WholeEntityOperators.Keys)
                                                    .OrderBy(x => x, StringComparer.Ordinal)) +
                    ". Refused rather than skipped: a skipped rule reports a clean model.");

            rule.Value = a["value"];
            rule.AssertionField = a.Value<string>("field");

            if (isWhole)
            {
                if (!string.IsNullOrWhiteSpace(rule.AssertionField))
                    throw new PlanimetryRequirementSetException(
                        "Rule '" + rule.Id + "' operator '" + rule.Operator + "' is about the whole " +
                        rule.Entity + " and takes no field; '" + rule.AssertionField +
                        "' would be silently ignored.");
                if (!WholeEntityScope[rule.Operator].Contains(rule.Entity, StringComparer.Ordinal))
                    throw new PlanimetryRequirementSetException(
                        "Rule '" + rule.Id + "' operator '" + rule.Operator + "' cannot be measured on a " +
                        rule.Entity + ". It applies to: " +
                        string.Join(", ", WholeEntityScope[rule.Operator]) + ".");
                if (WholeEntityOperators[rule.Operator] && rule.Value == null)
                    throw new PlanimetryRequirementSetException(
                        "Rule '" + rule.Id + "' operator '" + rule.Operator + "' requires value.");
                ValidateWholeEntityValue(rule, set);
                return;
            }

            if (string.IsNullOrWhiteSpace(rule.AssertionField))
                throw new PlanimetryRequirementSetException(
                    "Rule '" + rule.Id + "' assertion has no field. Operator '" + rule.Operator +
                    "' compares one named field of the " + rule.Entity + ".");
            if (!IsKnownField(rule.Entity, Fields[rule.Entity], rule.AssertionField))
                throw new PlanimetryRequirementSetException(
                    "Rule '" + rule.Id + "' assertion field '" + rule.AssertionField + "' is not a field of a " +
                    rule.Entity + ". Known: " + string.Join(", ", Fields[rule.Entity]) +
                    (SupportsParameters(rule.Entity) ? ", parameter:<name>" : "") + ".");
            if (FieldOperators[rule.Operator] && rule.Value == null)
                throw new PlanimetryRequirementSetException(
                    "Rule '" + rule.Id + "' operator '" + rule.Operator + "' requires value.");
            if (!FieldOperators[rule.Operator] && rule.Value != null)
                throw new PlanimetryRequirementSetException(
                    "Rule '" + rule.Id + "' operator '" + rule.Operator +
                    "' takes no value; the one given would be silently ignored.");

            switch (rule.Operator)
            {
                case "matches":
                case "not_matches":
                    if (rule.Value.Type != JTokenType.String)
                        throw new PlanimetryRequirementSetException(
                            "Rule '" + rule.Id + "' " + rule.Operator + " requires a regex string.");
                    rule.Pattern = Compile(rule.Id, "assertion", (string)rule.Value);
                    break;
                case "in_list":
                case "not_in_list":
                    RequireList(rule, rule.Value);
                    break;
                case "greater_than":
                case "less_than":
                    if (!IsNumber(rule.Value))
                        throw new PlanimetryRequirementSetException(
                            "Rule '" + rule.Id + "' " + rule.Operator + " requires a number.");
                    break;
                case "between":
                    JArray pair = rule.Value as JArray;
                    if (pair == null || pair.Count != 2 || !IsNumber(pair[0]) || !IsNumber(pair[1]))
                        throw new PlanimetryRequirementSetException(
                            "Rule '" + rule.Id + "' between requires exactly two numbers [min, max].");
                    if (pair[0].Value<double>() > pair[1].Value<double>())
                        throw new PlanimetryRequirementSetException(
                            "Rule '" + rule.Id + "' between has min greater than max, so it matches nothing.");
                    break;
            }
        }

        private static void ValidateWholeEntityValue(PlanimetryRule rule, PlanimetryRequirementSet set)
        {
            switch (rule.Operator)
            {
                case "minimum_gap":
                case "inside_extent":
                    if (!IsNumber(rule.Value) || rule.Value.Value<double>() < 0)
                        throw new PlanimetryRequirementSetException(
                            "Rule '" + rule.Id + "' " + rule.Operator +
                            " requires a non-negative number, in the units of the call.");
                    break;
                case "allowed_type":
                case "allowed_template":
                case "required_parameter":
                    RequireList(rule, rule.Value);
                    foreach (JToken v in (JArray)rule.Value)
                        if (v.Type != JTokenType.String)
                            throw new PlanimetryRequirementSetException(
                                "Rule '" + rule.Id + "' " + rule.Operator + " takes a list of names.");
                    break;
                case "allowed_scale":
                    RequireList(rule, rule.Value);
                    foreach (JToken v in (JArray)rule.Value)
                        if (!IsNumber(v))
                            throw new PlanimetryRequirementSetException(
                                "Rule '" + rule.Id + "' allowed_scale takes a list of numbers (a view's Scale " +
                                "is the denominator Revit reports, e.g. 50 for 1:50).");
                    break;
                case "requires_tag":
                    RequireList(rule, rule.Value);
                    foreach (JToken v in (JArray)rule.Value)
                        rule.TagRequirements.Add(ParseTagRequirement(rule, v, set));
                    break;
            }
        }

        private static readonly HashSet<string> TagRequirementKeys = new HashSet<string>(StringComparer.Ordinal)
        { "category", "exclude_types", "exclude_families", "exclude_type_matches", "exclude_when_parameter_set" };

        private static TagRequirement ParseTagRequirement(PlanimetryRule rule, JToken v, PlanimetryRequirementSet set)
        {
            var req = new TagRequirement();
            if (v.Type == JTokenType.String)
            {
                req.Category = (string)v;
            }
            else if (v is JObject o)
            {
                foreach (JProperty prop in o.Properties())
                    if (!TagRequirementKeys.Contains(prop.Name))
                        throw new PlanimetryRequirementSetException(
                            "Rule '" + rule.Id + "' requires_tag entry has unknown key '" + prop.Name +
                            "'. Known: " + string.Join(", ", TagRequirementKeys.OrderBy(x => x, StringComparer.Ordinal)) + ".");
                req.Category = o.Value<string>("category");
                foreach (JToken t in o["exclude_types"] as JArray ?? new JArray())
                {
                    if (t.Type != JTokenType.String)
                        throw new PlanimetryRequirementSetException(
                            "Rule '" + rule.Id + "' exclude_types takes type names.");
                    req.ExcludeTypes.Add((string)t);
                }
                foreach (JToken t in o["exclude_families"] as JArray ?? new JArray())
                {
                    if (t.Type != JTokenType.String)
                        throw new PlanimetryRequirementSetException(
                            "Rule '" + rule.Id + "' exclude_families takes family names.");
                    req.ExcludeFamilies.Add((string)t);
                }
                string pattern = o.Value<string>("exclude_type_matches");
                if (pattern != null) req.ExcludeTypeMatches = Compile(rule.Id, "exclude_type_matches", pattern);
                req.ExcludeWhenParameterSet = o.Value<string>("exclude_when_parameter_set");
                if (req.ExcludeWhenParameterSet != null && req.ExcludeWhenParameterSet.Trim().Length == 0)
                    throw new PlanimetryRequirementSetException(
                        "Rule '" + rule.Id + "' exclude_when_parameter_set must name a parameter.");
            }
            else
            {
                throw new PlanimetryRequirementSetException(
                    "Rule '" + rule.Id + "' requires_tag takes category names or objects " +
                    "{ category, exclude_types, exclude_families, exclude_type_matches, " +
                    "exclude_when_parameter_set } (OST_* tokens are the portable form of a category).");
            }

            if (string.IsNullOrWhiteSpace(req.Category))
                throw new PlanimetryRequirementSetException(
                    "Rule '" + rule.Id + "' requires_tag entry has no category.");
            if (!set.TagCoverageCategories.Contains(req.Category, StringComparer.OrdinalIgnoreCase))
                set.TagCoverageCategories.Add(req.Category);
            if (!string.IsNullOrWhiteSpace(req.ExcludeWhenParameterSet) &&
                !set.TagCoverageExcludeParameters.Contains(req.ExcludeWhenParameterSet, StringComparer.OrdinalIgnoreCase))
                set.TagCoverageExcludeParameters.Add(req.ExcludeWhenParameterSet);
            return req;
        }

        private static void RequireList(PlanimetryRule rule, JToken value)
        {
            JArray list = value as JArray;
            if (list == null || list.Count == 0)
                throw new PlanimetryRequirementSetException(
                    "Rule '" + rule.Id + "' " + rule.Operator + " requires a non-empty list.");
            if (list.Count > MaxListValues)
                throw new PlanimetryRequirementSetException(
                    "Rule '" + rule.Id + "' " + rule.Operator + " carries " + list.Count +
                    " values; the limit is " + MaxListValues + ".");
        }

        private static bool IsNumber(JToken t)
        {
            return t != null && (t.Type == JTokenType.Integer || t.Type == JTokenType.Float);
        }

        private static bool SupportsParameters(string entity)
        {
            return entity == "sheet" || entity == "view";
        }

        private static bool IsKnownField(string entity, string[] fields, string field)
        {
            if (string.IsNullOrWhiteSpace(field)) return false;
            if (fields.Contains(field, StringComparer.Ordinal)) return true;
            return SupportsParameters(entity) &&
                   field.StartsWith("parameter:", StringComparison.Ordinal) &&
                   field.Length > "parameter:".Length;
        }

        /// <summary>
        /// A regex with an explicit match timeout. Compiled AT LOAD so a pattern that does
        /// not parse is a refusal rather than a rule that throws on its first element and
        /// leaves the rest of the model unexamined.
        /// </summary>
        private static Regex Compile(string ruleId, string where, string pattern)
        {
            try
            {
                return new Regex(pattern, RegexOptions.CultureInvariant, RegexTimeout);
            }
            catch (Exception ex)
            {
                throw new PlanimetryRequirementSetException(
                    "Rule '" + ruleId + "' " + where + " is not a valid regex: " + ex.Message);
            }
        }

        /// <summary>
        /// Run one compiled pattern with its timeout. `timedOut` is the caller's signal to
        /// record `unknown` for that element - a pattern that ran out of time measured
        /// nothing, and measuring nothing is not passing.
        /// </summary>
        public static bool IsMatch(Regex pattern, string value, out bool timedOut)
        {
            timedOut = false;
            if (pattern == null || value == null) return false;
            try { return pattern.IsMatch(value); }
            catch (RegexMatchTimeoutException) { timedOut = true; return false; }
        }

        public static string DisplayValue(JToken t)
        {
            if (t == null || t.Type == JTokenType.Null) return null;
            if (t.Type == JTokenType.String) return (string)t;
            if (t.Type == JTokenType.Float) return ((double)t).ToString("0.######", CultureInfo.InvariantCulture);
            return t.ToString(Newtonsoft.Json.Formatting.None);
        }
    }
}
