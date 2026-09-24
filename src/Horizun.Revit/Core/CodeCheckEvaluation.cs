// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// CODE CHECKS: the evaluator. One loop over rules and elements, no branch on which
// norm a set encodes - the same property the reference sets prove for ISO 19650,
// IFC and COBie. Level-scoped arithmetic (occupant load -> required exits) lives
// here too, because it is arithmetic over facts the Revit side already read.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public static partial class CodeCheckRules
    {
        public const string UnverifiedReason =
            "unverified_value: this set marks the threshold as NOT verified against the norm's text, so no number " +
            "is applied. Confirm the value in the cited numeral and edit the set.";

        /// <summary>Evaluate every rule over the facts. Findings list non-passing rows (and passes when asked).</summary>
        public static JObject Evaluate(RequirementSet set, IList<CheckedElement> elements, int maxFindings, bool includePasses)
        {
            var rules = new JArray();
            var findings = new JArray();
            int total = 0, listed = 0;
            var totals = new Dictionary<string, int>(StringComparer.Ordinal)
            { { "passes", 0 }, { "fails", 0 }, { "not_decidable", 0 }, { "unreadable", 0 } };

            foreach (Requirement rule in set.Rules)
            {
                var counts = new Dictionary<string, int>(StringComparer.Ordinal)
                { { "passes", 0 }, { "fails", 0 }, { "not_decidable", 0 }, { "unreadable", 0 } };
                int examined = 0;
                foreach (CheckedElement e in elements)
                {
                    if (!Selects(rule, e)) continue;
                    string reason = null;
                    JObject measured = null;
                    string outcome;
                    if (rule.SelectorMeasure != null)
                    {
                        e.Measures.TryGetValue(rule.SelectorMeasure, out MeasuredValue sm);
                        if (sm == null || sm.Value == null || sm.Unavailable != null)
                        {
                            // Cannot tell whether the rule applies: that is not a pass either.
                            examined++;
                            outcome = "not_decidable";
                            reason = "selector measure '" + rule.SelectorMeasure + "' unavailable: " + (sm?.Unavailable ?? "not measured");
                            Record(rule, e, outcome, reason, null);
                            continue;
                        }
                        double x = sm.Value.Value;
                        if ((rule.SelectorMeasureMin != null && !(x > rule.SelectorMeasureMin.Value)) ||
                            (rule.SelectorMeasureMax != null && !(x <= rule.SelectorMeasureMax.Value)))
                            continue;
                    }
                    examined++;
                    outcome = Judge(set, rule, e, out reason, out measured);
                    Record(rule, e, outcome, reason, measured);
                }

                void Record(Requirement r, CheckedElement el, string oc, string why, JObject meas)
                {
                    counts[oc]++;
                    totals[oc]++;
                    if (oc == "passes" && !includePasses) return;
                    total++;
                    if (listed >= maxFindings) return;
                    listed++;
                    var f = new JObject
                    {
                        ["rule"] = r.Id,
                        ["outcome"] = oc,
                        ["element_id"] = el.Id,
                        ["category"] = el.CategoryName ?? el.CategoryToken,
                        ["name"] = el.Name,
                        ["level"] = el.Level,
                        ["severity"] = r.Blocking ? "blocking" : "advisory"
                    };
                    if (meas != null) f["measured"] = meas;
                    if (why != null) f["reason"] = why;
                    if (oc == "fails" && r.RemediationTool != null)
                        f["remediation"] = new JObject { ["tool"] = r.RemediationTool, ["arguments"] = r.RemediationArguments };
                    findings.Add(f);
                }

                string verdict = examined == 0 ? "not_decidable"
                    : counts["fails"] > 0 ? "fails"
                    : counts["not_decidable"] + counts["unreadable"] > 0 ? "not_decidable" : "passes";
                var row = new JObject
                {
                    ["id"] = rule.Id,
                    ["verdict"] = verdict,
                    ["examined"] = examined,
                    ["passes"] = counts["passes"], ["fails"] = counts["fails"],
                    ["not_decidable"] = counts["not_decidable"], ["unreadable"] = counts["unreadable"],
                    ["severity"] = rule.Blocking ? "blocking" : "advisory",
                    ["assertion"] = (rule.AssertionMeasure != null ? "measure " + rule.AssertionMeasure : "parameter " + rule.AssertionParameter) +
                                    " " + rule.Operator + (rule.Value == null ? "" : " " + rule.Value.ToString(Newtonsoft.Json.Formatting.None))
                };
                if (rule.Source != null) row["source"] = rule.Source.DeepClone();
                if (rule.UnverifiedValue) row["unverified_value"] = true;
                if (examined == 0) row["reason"] = "no element in the model matched the selector; examined nothing is not a pass.";
                rules.Add(row);
            }

            string overall = rules.Any(r => (string)r["verdict"] == "fails") ? "fails"
                : rules.Any(r => (string)r["verdict"] == "not_decidable") ? "not_decidable" : "passes";
            return new JObject
            {
                ["verdict"] = overall,
                ["totals"] = JObject.FromObject(totals),
                ["rules"] = rules,
                ["findings"] = findings,
                ["findings_total"] = total,
                ["findings_listed"] = listed,
                ["disclaimer"] = Disclaimer
            };
        }

        /// <summary>One rule against one selected element.</summary>
        public static string Judge(RequirementSet set, Requirement rule, CheckedElement e, out string reason, out JObject measured)
        {
            reason = null;
            measured = null;
            if (rule.AssertionMeasure != null)
            {
                MeasuredValue m = MeasureFor(rule, e);
                measured = new JObject { ["measure"] = rule.AssertionMeasure };
                if (m?.Value != null) measured["value"] = Math.Round(m.Value.Value, 2);
                if (m != null && m.Bound != "exact") measured["bound"] = m.Bound;
                if (m?.Basis != null) measured["basis"] = m.Basis;
                if (m?.Detail != null) measured["detail"] = m.Detail;
                if (rule.UnverifiedValue) { reason = UnverifiedReason; return "not_decidable"; }
                return CompareMeasured(rule, m, out reason);
            }

            string key = CheckedElement.ParamKey(rule.AssertionParameter, rule.AssertionUnit);
            e.Params.TryGetValue(key, out ParamFact p);
            measured = new JObject { ["parameter"] = rule.AssertionParameter };
            if (p != null && p.Exists) measured["value"] = rule.AssertionUnit != null && p.Number != null
                ? (JToken)Math.Round(p.Number.Value, 3) : p.Text;
            if (rule.AssertionUnit != null) measured["unit"] = rule.AssertionUnit;
            if (p != null && p.Unreadable) { reason = "the parameter could not be read."; return "unreadable"; }
            bool exists = p != null && p.Exists;
            bool blank = !exists || (rule.AssertionUnit != null ? p.Number == null : string.IsNullOrWhiteSpace(p.Text));
            if (rule.UnverifiedValue) { reason = UnverifiedReason; return "not_decidable"; }
            if (blank && rule.MissingIs == "not_decidable" && rule.Operator != "exists" && rule.Operator != "not_exists")
            {
                reason = exists ? "the parameter is blank" + (rule.AssertionUnit != null ? " or not convertible to " + rule.AssertionUnit : "") + ": the model does not carry the datum."
                                : "the parameter does not exist on this element: the model does not carry the datum.";
                return "not_decidable";
            }
            if (rule.AssertionUnit != null)
                return exists && p.Number != null && RequirementSet.CompareNumber(rule, p.Number.Value) ? "passes" : "fails";
            return set.Passes(rule, exists, p?.Text) ? "passes" : "fails";
        }

        private static MeasuredValue MeasureFor(Requirement rule, CheckedElement e)
        {
            if (rule.AssertionMeasure == "travel_distance_m")
                return MeasuredValue.None("this tool does not route a travel distance. horizun_audit_access with " +
                                          "route_view_id measures a real path per room; a straight line is not a travel distance.");
            if (rule.AssertionMeasure == "exit_count_minus_required") return ExitsVsRequired(rule.Config, e);
            e.Measures.TryGetValue(rule.AssertionMeasure, out MeasuredValue m);
            return m ?? MeasuredValue.None("this element yields no " + rule.AssertionMeasure + " (wrong category for the measure?).");
        }
    }
}
