// -----------------------------------------------------------------------------
// Horizun Core - original Horizun code. Revit-free.
//
// WHEN A LINKED DATUM HAS THE SAME TYPE NAME AS A HOST ONE (§6, field report 2026-11-17).
//
// "Type Name not equal to <host types>" cannot hide a linked grid or level whose type has the same
// name as a host type. Another string parameter can separate them ONLY if the linked datum's value
// of it is not any value a host datum of the kind has: the filter would be "P not equal to each
// host value", and it hides exactly the datums whose P is outside the host's set. This type decides,
// from values the command read, which candidate parameter does that and
// how many of the unresolved datums each one would resolve. It proposes; it never writes, and it
// says what stays unmeasured (a candidate filter was not applied or seen in a live view).
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public static class LinkDatumSeparators
    {
        public const string Workset = "workset";
        public const string ScopeBox = "scope_box";
        public const string Name = "name";

        /// <summary>The candidates, in the order they are preferred on a tie.</summary>
        public static readonly string[] Candidates = { Name, Workset, ScopeBox };

        /// <summary>
        /// Can a filter rule compare this parameter by VALUE across documents? Only the datum Name is a string. Workset and
        /// scope box are stored as ids of the document that owns the element: a rule written with the host's id would be
        /// compared with the link's id, which may name another workset or scope box, so they are reported and never proposed.
        /// </summary>
        public static bool ComparesByValue(string candidate) => candidate == Name;

        /// <summary>One datum's value of a candidate parameter: null/blank when unreadable or empty.</summary>
        public sealed class Row
        {
            public string Id;
            public Dictionary<string, string> Values = new Dictionary<string, string>(StringComparer.Ordinal);
        }

        public sealed class Verdict
        {
            public string Candidate;
            /// <summary>Distinct, non-blank values the HOST datums of the kind carry; the rule would be "not equal to" each.</summary>
            public List<string> HostValues = new List<string>();
            public int HostBlank;
            public int Separated, SameValue, Unreadable;
            public string Caveat;
            public bool ByValue;
            /// <summary>True when the filter could be built without risking a host datum: every host datum has a readable value.</summary>
            public bool HostSafe => HostBlank == 0 && HostValues.Count > 0;
            /// <summary>A rule on it can be proposed: host datums all protected and the rule compares by value.</summary>
            public bool Usable => HostSafe && ByValue;
        }

        /// <summary>
        /// The value as Revit would compare it, or null. NOT trimmed: a name with leading or trailing spaces is treated as
        /// unreadable, because a "not equal" rule on the trimmed text would not match the real one and host_safe would over-claim.
        /// </summary>
        private static string Clean(string v) => string.IsNullOrWhiteSpace(v) || v != v.Trim() ? null : v;

        /// <summary>A scope box value as a name: Revit's "None" (any language it reports) is no scope box, so null.</summary>
        public static string ScopeBoxName(string v)
        {
            if (string.IsNullOrWhiteSpace(v)) return null;
            string t = v.Trim().Trim('<', '>');
            return t.Equals("None", StringComparison.OrdinalIgnoreCase) || t.Equals("Ninguno", StringComparison.OrdinalIgnoreCase)
                || t.Equals("Ninguna", StringComparison.OrdinalIgnoreCase) ? null : v;
        }

        /// <summary>
        /// Per candidate: how many of the <paramref name="unresolved"/> linked datums (same type name as a host one) a
        /// "P not equal to each host value" rule would hide, how many share a host value, how many have no readable value.
        /// <paramref name="hostAll"/>: every host datum of the kind, drawn today or not.
        /// </summary>
        public static List<Verdict> Analyze(IEnumerable<Row> hostAll, IEnumerable<Row> unresolved, IEnumerable<string> candidates = null, bool nameFilterable = true)
        {
            var host = (hostAll ?? Enumerable.Empty<Row>()).ToList();
            var linked = (unresolved ?? Enumerable.Empty<Row>()).ToList();
            var result = new List<Verdict>();
            foreach (string c in candidates ?? Candidates)
            {
                var v = new Verdict { Candidate = c, ByValue = ComparesByValue(c) && (c != Name || nameFilterable) };
                var hv = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (Row h in host)
                {
                    string x = h.Values.TryGetValue(c, out string raw) ? Clean(raw) : null;
                    if (x == null) v.HostBlank++; else hv.Add(x);
                }
                v.HostValues = hv.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();
                foreach (Row l in linked)
                {
                    string x = l.Values.TryGetValue(c, out string raw) ? Clean(raw) : null;
                    if (x == null) v.Unreadable++;
                    else if (hv.Contains(x)) v.SameValue++;
                    else v.Separated++;
                }
                v.Caveat = CaveatFor(c, v);
                result.Add(v);
            }
            return result;
        }

        private static string CaveatFor(string candidate, Verdict v)
        {
            if (candidate == Name && !v.ByValue) return "the datum Name is not among the filterable parameters of this category in this Revit, so no rule can be built on it";
            if (!v.ByValue) return "the rule would compare ids of the host's document (the link's " + candidate + " has its own ids), so it cannot be proven to separate them; shown for information only";
            if (v.HostValues.Count == 0) return "no host datum has a readable " + candidate + ", so no rule can keep them";
            if (v.HostBlank > 0) return v.HostBlank + " host datum(s) have no readable " + candidate + "; the rule would not protect them";
            return "a host datum created later with a new name would be hidden by this rule";
        }

        /// <summary>The best candidate: protects every host datum and separates the most; null when none separates any.</summary>
        public static Verdict Best(IEnumerable<Verdict> verdicts) =>
            (verdicts ?? Enumerable.Empty<Verdict>()).Where(v => v.Usable && v.Separated > 0)
                .OrderByDescending(v => v.Separated).ThenBy(v => Array.IndexOf(Candidates, v.Candidate)).FirstOrDefault();

        public static JObject ToJson(string kind, IList<Verdict> verdicts, int sameNameDatums, bool nameFilterable = true)
        {
            Verdict best = Best(verdicts);
            var o = new JObject
            {
                ["kind"] = kind, ["same_type_name_datums"] = sameNameDatums,
                ["candidates"] = new JArray(verdicts.Select(v =>
                {
                    var c = new JObject
                    {
                        ["parameter"] = v.Candidate, ["would_hide"] = v.Separated, ["same_value_as_host"] = v.SameValue, ["unreadable"] = v.Unreadable,
                        ["host_values"] = new JArray(v.HostValues.Take(30)), ["host_values_total"] = v.HostValues.Count,
                        ["compares_by_value"] = v.ByValue, ["caveat"] = v.Caveat
                    };
                    if (v.ByValue) c["host_safe"] = v.HostSafe;   // meaningless for an id-valued parameter: a rule cannot be written
                    return c;
                })),
                ["best"] = best?.Candidate == null ? JValue.CreateNull() : (JToken)best.Candidate,
                ["status"] = "proposed_not_applied",
                ["unmeasured"] = "whether Revit evaluates this parameter on a LINKED element through a host view filter was not measured live; apply by hand on one view and re-read with horizun_capture_view before trusting it"
                              + (nameFilterable ? "" : "; the datum Name is not in this category's filterable parameters here, so it was not proposed")
            };
            o["means"] = best == null
                ? "no parameter that a rule can compare by value (the datum Name) separates these datums from the host's; rename a host type, duplicate a host type for these datums, or switch the link's Grids/Levels off by hand and spread it with copy_link_overrides"
                : "a second filter on the datum " + best.Candidate + " would hide " + best.Separated + " of " + sameNameDatums + "; " + best.Caveat;
            return o;
        }
    }
}
