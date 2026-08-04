// -----------------------------------------------------------------------------
// Horizun Revit MCP — a requirement set, loaded and validated. Original code.
//
// This is the pure half of story 4.1: the document that says what a model must
// satisfy, parsed and REFUSED ON when malformed, with no Revit anywhere in it.
// The measuring half (walking elements, reading parameters) plugs in above this
// when the tool lands; the loader must not wait for it, because every refusal
// rule here is testable tonight and none of them needs a model.
//
// The design line, from docs/requirement-set.md: the bridge MEASURES and never
// judges. This class does even less - it only decides whether the document is
// well-formed enough that measuring against it would mean something. Every rule
// it refuses on exists because the silent alternative reports a clean model:
// a rule that matches everything, an operator nobody implemented, a table that
// failed to load. Input from outside is validated like any other argument.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    /// <summary>One rule: WHICH elements (selector), WHAT must be true (assertion).</summary>
    public sealed class Requirement
    {
        public string Id;
        public string SelectorCategory;
        public string SelectorTypeNameMatches;     // regex text, already compile-checked
        public string SelectorParameterExists;
        public string AssertionParameter;
        public string Operator;
        public JToken Value;                       // null only for the non-comparing operators
        public string RemediationTool;
        public JObject RemediationArguments;
        public bool Blocking;                      // default advisory
    }

    public sealed class RequirementTable
    {
        public string Id;
        public string Title;
        /// <summary>code -> parent code (empty string for a root).</summary>
        public Dictionary<string, string> Parents = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        /// <summary>Codes that are somebody's parent - the NON-leaves.</summary>
        public HashSet<string> HasChildren = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public bool IsLeaf(string code) =>
            code != null && Parents.ContainsKey(code) && !HasChildren.Contains(code);
    }

    /// <summary>
    /// A parsed, validated requirement set. Load() either returns one whose every rule
    /// is usable, or throws with the refusal - there is no partially-loaded state,
    /// because a set that half-loaded and then "passed" is the lie this exists to stop.
    /// </summary>
    public sealed class RequirementSet
    {
        public string Id;
        public string Version;
        public string Title;
        public List<string> ScopeCategories = new List<string>();
        public string ScopeStage;
        public List<Requirement> Rules = new List<Requirement>();
        public Dictionary<string, RequirementTable> Tables =
            new Dictionary<string, RequirementTable>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Operators and whether each needs a value. THE list - the error message prints it.</summary>
        private static readonly Dictionary<string, bool> Operators = new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            { "exists", false }, { "not_exists", false }, { "not_empty", false },
            { "equals", true }, { "not_equals", true }, { "matches", true }, { "in_list", true },
            { "is_leaf_of", true }, { "gt", true }, { "gte", true }, { "lt", true }, { "lte", true }
        };

        private static readonly HashSet<string> TopLevelKeys = new HashSet<string>(StringComparer.Ordinal)
        { "requirement_set", "rules", "tables" };

        /// <summary>
        /// Parse and validate. `resolveTableSource` maps a `source:` path to CSV text -
        /// injected so the loader stays pure and a test can hand it a string; the caller
        /// decides what "relative to the set" means on its filesystem. JSON today; YAML
        /// arrives as its own decision because it means a parser dependency, and a
        /// dependency is a supply-chain call this loader must not make by itself.
        /// </summary>
        public static RequirementSet Load(JObject doc, Func<string, string> resolveTableSource)
        {
            if (doc == null) throw new RequirementSetException("The requirement set is empty.");

            // Unknown top-level key: a typo is a refusal, not a rule nobody notices is missing.
            foreach (JProperty prop in doc.Properties())
                if (!TopLevelKeys.Contains(prop.Name))
                    throw new RequirementSetException(
                        "Unknown top-level key '" + prop.Name + "'. Known: " + string.Join(", ", TopLevelKeys.OrderBy(x => x)) +
                        ". A misspelt section would otherwise be a section that silently does not run.");

            var set = new RequirementSet();
            JObject header = doc["requirement_set"] as JObject
                ?? throw new RequirementSetException("requirement_set (id, version, title) is required.");
            set.Id = header.Value<string>("id");
            set.Version = header.Value<string>("version");
            set.Title = header.Value<string>("title");
            if (string.IsNullOrWhiteSpace(set.Id)) throw new RequirementSetException("requirement_set.id is required; findings are keyed by it.");
            if (string.IsNullOrWhiteSpace(set.Version)) throw new RequirementSetException("requirement_set.version is required; a finding always cites the version it came from.");
            JObject scope = header["scope"] as JObject;
            if (scope != null)
            {
                foreach (JToken c in scope["categories"] as JArray ?? new JArray())
                    if (c.Type == JTokenType.String) set.ScopeCategories.Add((string)c);
                set.ScopeStage = scope["stage"]?.ToString();
            }

            // ---- Tables first: rules may reference them. ----
            foreach (JToken t in doc["tables"] as JArray ?? new JArray())
            {
                JObject tj = t as JObject ?? throw new RequirementSetException("Every tables entry must be an object.");
                var table = new RequirementTable { Id = tj.Value<string>("id"), Title = tj.Value<string>("title") };
                if (string.IsNullOrWhiteSpace(table.Id)) throw new RequirementSetException("A table without an id cannot be referenced.");
                if (set.Tables.ContainsKey(table.Id)) throw new RequirementSetException("Table id '" + table.Id + "' is duplicated.");

                string source = tj.Value<string>("source");
                JArray inline = tj["entries"] as JArray;
                if (source == null && inline == null)
                    throw new RequirementSetException("Table '" + table.Id + "' has neither entries nor source.");
                if (source != null)
                {
                    // Refused AT LOAD, not at first use: a classification check that
                    // quietly passes because its table is missing is worse than no check.
                    string csv;
                    try { csv = resolveTableSource == null ? null : resolveTableSource(source); }
                    catch (Exception ex) { throw new RequirementSetException("Table '" + table.Id + "' source '" + source + "' could not be read: " + ex.Message); }
                    if (csv == null)
                        throw new RequirementSetException("Table '" + table.Id + "' source '" + source + "' did not resolve. Refused at load: a missing table must not become a passing check.");
                    ParseCsv(table, csv);
                }
                else
                {
                    foreach (JToken e in inline)
                    {
                        JObject ej = e as JObject ?? throw new RequirementSetException("Table '" + table.Id + "': every entry must be an object.");
                        AddEntry(table, ej.Value<string>("code"), ej.Value<string>("parent") ?? "");
                    }
                }
                if (table.Parents.Count == 0)
                    throw new RequirementSetException("Table '" + table.Id + "' is empty. An empty table makes every is_leaf_of fail, which reads like a model problem and is a document problem.");
                set.Tables[table.Id] = table;
            }

            // ---- Rules. ----
            JArray rules = doc["rules"] as JArray;
            if (rules == null || rules.Count == 0)
                throw new RequirementSetException("rules is required and must be non-empty. A set with no rules examines nothing, and 'examined nothing' must never look like 'passed'.");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (JToken r in rules)
            {
                JObject rj = r as JObject ?? throw new RequirementSetException("Every rules entry must be an object.");
                var rule = new Requirement { Id = rj.Value<string>("id") };
                if (string.IsNullOrWhiteSpace(rule.Id)) throw new RequirementSetException("A rule without an id cannot be reported on; findings are keyed by it.");
                if (!seen.Add(rule.Id)) throw new RequirementSetException("Rule id '" + rule.Id + "' is duplicated.");

                // WHICH elements. A rule with no selector selects nothing and is refused,
                // because a rule that silently matches everything is how a requirement set
                // deletes a project.
                JObject sel = rj["selector"] as JObject;
                if (sel == null || !sel.Properties().Any())
                    throw new RequirementSetException("Rule '" + rule.Id + "' has no selector. Refused: a rule that matches everything can drive a remediation across an entire model.");
                rule.SelectorCategory = sel.Value<string>("category");
                rule.SelectorTypeNameMatches = sel.Value<string>("type_name_matches");
                rule.SelectorParameterExists = sel.Value<string>("parameter_exists");
                if (rule.SelectorTypeNameMatches != null)
                {
                    try { _ = new System.Text.RegularExpressions.Regex(rule.SelectorTypeNameMatches); }
                    catch (Exception ex) { throw new RequirementSetException("Rule '" + rule.Id + "' type_name_matches is not a valid regex: " + ex.Message); }
                }

                // WHAT must be true. Exactly one assertion - the schema makes two
                // impossible to express (assertion is an object, not an array), so the
                // refusal here is about the DEGENERATE shapes: missing, or empty.
                JObject a = rj["assertion"] as JObject;
                if (a == null) throw new RequirementSetException("Rule '" + rule.Id + "' has no assertion.");
                rule.AssertionParameter = a.Value<string>("parameter");
                if (string.IsNullOrWhiteSpace(rule.AssertionParameter))
                    throw new RequirementSetException("Rule '" + rule.Id + "' assertion has no parameter.");
                rule.Operator = a.Value<string>("operator");
                if (rule.Operator == null || !Operators.ContainsKey(rule.Operator))
                    throw new RequirementSetException(
                        "Rule '" + rule.Id + "' operator '" + (rule.Operator ?? "(none)") + "' is unknown. Known: " +
                        string.Join(", ", Operators.Keys.OrderBy(x => x)) +
                        ". Refused rather than skipped: a skipped rule reports a clean model.");
                rule.Value = a["value"];
                if (Operators[rule.Operator] && rule.Value == null)
                    throw new RequirementSetException("Rule '" + rule.Id + "' operator '" + rule.Operator + "' requires value.");
                if (rule.Operator == "is_leaf_of")
                {
                    string tableId = rule.Value.Type == JTokenType.String ? (string)rule.Value : null;
                    if (tableId == null || !set.Tables.ContainsKey(tableId))
                        throw new RequirementSetException("Rule '" + rule.Id + "' is_leaf_of names table '" + (tableId ?? "(not a string)") + "', which this set does not carry.");
                }
                if (rule.Operator == "in_list" && !(rule.Value is JArray))
                    throw new RequirementSetException("Rule '" + rule.Id + "' in_list requires value to be a list.");

                // WHAT TO DO about it. Optional - a set may be read-only by design - but
                // when present it must name a tool, because a remediation that is not a
                // typed command is a standard smuggling in behaviour.
                JObject rem = rj["remediation"] as JObject;
                if (rem != null)
                {
                    rule.RemediationTool = rem.Value<string>("tool");
                    if (string.IsNullOrWhiteSpace(rule.RemediationTool))
                        throw new RequirementSetException("Rule '" + rule.Id + "' remediation has no tool.");
                    rule.RemediationArguments = rem["arguments"] as JObject;
                }

                string severity = rj.Value<string>("severity") ?? "advisory";
                if (severity != "blocking" && severity != "advisory")
                    throw new RequirementSetException("Rule '" + rule.Id + "' severity must be blocking or advisory.");
                rule.Blocking = severity == "blocking";
                set.Rules.Add(rule);
            }
            return set;
        }

        /// <summary>
        /// One assertion against one measured value. Pure: the caller reads the model,
        /// this decides. `measured` null means THE PARAMETER READ AS NULL - the caller
        /// must keep "could not be read" out of here entirely, reporting it as
        /// `unreadable` instead; collapsing unreadable into a pass or a fail is the
        /// substitution this repository exists to refuse, and it would happen HERE if
        /// this method accepted it.
        /// </summary>
        public bool Passes(Requirement rule, bool parameterExists, string measured)
        {
            switch (rule.Operator)
            {
                case "exists": return parameterExists;
                case "not_exists": return !parameterExists;
                case "not_empty": return parameterExists && !string.IsNullOrWhiteSpace(measured);
                case "equals": return parameterExists && string.Equals(measured, rule.Value.ToString(), StringComparison.OrdinalIgnoreCase);
                case "not_equals": return parameterExists && !string.Equals(measured, rule.Value.ToString(), StringComparison.OrdinalIgnoreCase);
                case "matches":
                    return parameterExists && measured != null &&
                           System.Text.RegularExpressions.Regex.IsMatch(measured, (string)rule.Value);
                case "in_list":
                    return parameterExists && ((JArray)rule.Value).Any(v =>
                        string.Equals(measured, v.ToString(), StringComparison.OrdinalIgnoreCase));
                case "is_leaf_of":
                    return parameterExists && Tables[(string)rule.Value].IsLeaf(measured);
                case "gt": case "gte": case "lt": case "lte":
                    if (!parameterExists) return false;
                    if (!double.TryParse(measured, NumberStyles.Any, CultureInfo.InvariantCulture, out double have)) return false;
                    double want = rule.Value.Value<double>();
                    return rule.Operator == "gt" ? have > want
                         : rule.Operator == "gte" ? have >= want
                         : rule.Operator == "lt" ? have < want
                         : have <= want;
                default:
                    // Unreachable: Load() refused unknown operators. Throwing keeps it
                    // that way - returning false here would turn a future loader bug
                    // into findings that look measured.
                    throw new InvalidOperationException("operator '" + rule.Operator + "' escaped load validation");
            }
        }

        private static void ParseCsv(RequirementTable table, string csv)
        {
            string[] lines = csv.Replace("\r\n", "\n").Split('\n');
            int start = 0;
            // A header row is expected (code,title,parent) but not demanded by name -
            // demanded by shape: skip the first row only if it does not look like data.
            if (lines.Length > 0 && lines[0].StartsWith("code", StringComparison.OrdinalIgnoreCase)) start = 1;
            for (int i = start; i < lines.Length; i++)
            {
                if (string.IsNullOrWhiteSpace(lines[i])) continue;
                string[] cells = lines[i].Split(',');
                if (cells.Length < 1 || string.IsNullOrWhiteSpace(cells[0]))
                    throw new RequirementSetException("Table '" + table.Id + "' line " + (i + 1) + " has no code.");
                AddEntry(table, cells[0].Trim(), cells.Length >= 3 ? cells[2].Trim() : "");
            }
        }

        private static void AddEntry(RequirementTable table, string code, string parent)
        {
            if (string.IsNullOrWhiteSpace(code))
                throw new RequirementSetException("Table '" + table.Id + "' has an entry with no code.");
            if (table.Parents.ContainsKey(code))
                throw new RequirementSetException("Table '" + table.Id + "' code '" + code + "' is duplicated.");
            table.Parents[code] = parent ?? "";
            if (!string.IsNullOrWhiteSpace(parent)) table.HasChildren.Add(parent);
        }
    }

    /// <summary>A malformed requirement set. The message IS the refusal the caller reports.</summary>
    public sealed class RequirementSetException : Exception
    {
        public RequirementSetException(string message) : base(message) { }
    }
}
