// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// IS THIS ELEMENT ON THE RIGHT WORKSET, and the one thing that must never
// happen when the answer is unknowable.
//
// A CLOSED WORKSET'S ELEMENTS ARE NOT IN THE DOCUMENT. Nothing can enumerate
// them, so a placement check run over a model with a closed workset has not
// examined the model - it has examined the part somebody left open. Every count
// it produces is a LOWER BOUND.
//
// That has one consequence, and it is the whole reason this file exists:
//
//     a check with incomplete coverage may FAIL, and may never PASS.
//
// Failing is still sound - a violation found among the loaded elements is a
// real violation, and closing a workset cannot un-break it. Passing is not: "I
// found no elements on the wrong workset" is a statement about everything, and
// nobody looked at everything. A gate that passes here tells a team their model
// is clean because a colleague happened to have a workset closed.
//
// NO WORKSET NAME IS COMPILED IN. Revit's own default workset name is
// localized, so a built-in "Workset1" would silently stop matching in a Spanish
// session - the identical failure the warning identities were rewritten to
// avoid. The caller declares the names, versioned.
//
// Revit-free, so all of it is provable at a desk.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public static class WorksetGate
    {
        public const string Pass = "pass";
        public const string Fail = "fail";
        /// <summary>Coverage was incomplete and nothing was found. Not a pass.</summary>
        public const string NotAssessable = "not_assessable";
    }

    public static class WorksetRuleCodes
    {
        public const string NoVersion = "workset_rules_no_version";
        public const string UnknownKey = "workset_rules_unknown_key";
        public const string BadRule = "workset_rules_bad_rule";
    }

    public sealed class WorksetRules
    {
        public bool Ok;
        public bool Absent;
        public string Code;
        public string Message;
        public string Version;

        /// <summary>Category name to the workset name its elements belong on.</summary>
        public Dictionary<string, string> ExpectedByCategory =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Workset names the caller considers un-renamed defaults. Nothing is compiled in.</summary>
        public List<string> DefaultWorksetNames = new List<string>();

        /// <summary>Null when the caller set no ceiling: a count with no threshold cannot pass or fail.</summary>
        public long? MaxElementsInWrongWorkset;
    }

    /// <summary>One element sitting somewhere the caller did not expect.</summary>
    public sealed class WorksetMisplacement
    {
        public long ElementId;
        public string Category;
        public string ActualWorkset;
        public string ExpectedWorkset;
    }

    public static class WorksetPlacementRules
    {
        public const string CoverageMeans =
            "a CLOSED workset's elements are not in the document, so nothing can enumerate them. Where any " +
            "user workset is closed this check has examined the part of the model somebody left open, every " +
            "count below is a LOWER BOUND, and the check may FAIL but can never PASS - 'nothing found' would " +
            "be a claim about elements nobody loaded.";

        public static WorksetRules Read(JToken token)
        {
            var r = new WorksetRules();
            if (token == null || token.Type == JTokenType.Null)
            {
                r.Absent = true;
                r.Message = "no workset rules were supplied, so no element's placement was judged. This is NOT " +
                            "a pass: which workset a wall belongs on is one organisation's decision and none " +
                            "is compiled in here.";
                return r;
            }

            var o = token as JObject;
            if (o == null)
            {
                r.Code = WorksetRuleCodes.BadRule;
                r.Message = "workset_rules must be an object.";
                return r;
            }

            JToken v = o["version"];
            if (v == null || string.IsNullOrWhiteSpace(v.Value<string>()))
            {
                r.Code = WorksetRuleCodes.NoVersion;
                r.Message = "workset_rules needs a 'version', so a report can say which rules produced it.";
                return r;
            }
            r.Version = v.Value<string>();

            foreach (JProperty p in o.Properties())
            {
                switch (p.Name)
                {
                    case "version":
                        break;

                    case "by_category":
                    {
                        var body = p.Value as JObject;
                        if (body == null)
                        {
                            r.Code = WorksetRuleCodes.BadRule;
                            r.Message = "'by_category' must be an object of category name to workset name.";
                            return r;
                        }
                        foreach (JProperty c in body.Properties())
                        {
                            string want = c.Value.Type == JTokenType.String ? c.Value.Value<string>() : null;
                            if (string.IsNullOrWhiteSpace(want))
                            {
                                r.Code = WorksetRuleCodes.BadRule;
                                r.Message = "the entry for category '" + c.Name + "' must name a workset.";
                                return r;
                            }
                            r.ExpectedByCategory[c.Name] = want;
                        }
                        break;
                    }

                    case "default_workset_names":
                    {
                        var arr = p.Value as JArray;
                        if (arr == null)
                        {
                            r.Code = WorksetRuleCodes.BadRule;
                            r.Message = "'default_workset_names' must be an array of names. Revit's own default " +
                                        "name is LOCALIZED, so none is compiled in and this list is how a " +
                                        "session says what it calls one.";
                            return r;
                        }
                        foreach (JToken t in arr)
                            if (t.Type == JTokenType.String) r.DefaultWorksetNames.Add(t.Value<string>());
                        break;
                    }

                    case "max_elements_in_wrong_workset":
                    {
                        if (p.Value.Type != JTokenType.Integer)
                        {
                            r.Code = WorksetRuleCodes.BadRule;
                            r.Message = "'max_elements_in_wrong_workset' must be a whole number.";
                            return r;
                        }
                        long max = p.Value.Value<long>();
                        if (max < 0)
                        {
                            r.Code = WorksetRuleCodes.BadRule;
                            r.Message = "'max_elements_in_wrong_workset' cannot be negative.";
                            return r;
                        }
                        r.MaxElementsInWrongWorkset = max;
                        break;
                    }

                    default:
                        r.Code = WorksetRuleCodes.UnknownKey;
                        r.Message = "'" + p.Name + "' is not a key workset_rules defines. Known keys: " +
                                    "version, by_category, default_workset_names, max_elements_in_wrong_workset. " +
                                    "A rule filed under a name nothing reads would never run, and the report " +
                                    "would look like a clean pass of a rule nobody applied.";
                        return r;
                }
            }

            r.Ok = true;
            return r;
        }

        /// <summary>
        /// THE RULE THIS FILE EXISTS FOR. Incomplete coverage may fail and may
        /// never pass.
        ///
        /// A violation found among the loaded elements is real, and closing a
        /// workset cannot un-break it - so a fail stands. "Nothing found" is a
        /// claim about every element, and with a workset closed nobody looked at
        /// every element, so it is not_assessable rather than a pass.
        /// </summary>
        public static string Outcome(long found, long? max, bool coverageComplete)
        {
            if (!max.HasValue) return WorksetGate.NotAssessable;
            if (found > max.Value) return WorksetGate.Fail;
            return coverageComplete ? WorksetGate.Pass : WorksetGate.NotAssessable;
        }

        /// <summary>True when every user workset was open, so the walk saw the whole model.</summary>
        public static bool CoverageComplete(int worksetsClosed, long worksetUnreadable)
        {
            return worksetsClosed == 0 && worksetUnreadable == 0;
        }

        public static string CoverageNote(int worksetsClosed, long worksetUnreadable)
        {
            if (worksetsClosed == 0 && worksetUnreadable == 0)
                return "every user workset was open and every element reported its workset, so these counts " +
                       "are exact.";
            var parts = new List<string>();
            if (worksetsClosed > 0)
                parts.Add(worksetsClosed + " user workset(s) are CLOSED, so their elements are not in the " +
                          "document and were never examined");
            if (worksetUnreadable > 0)
                parts.Add(worksetUnreadable + " element(s) would not report a workset at all");
            return string.Join("; ", parts) + ". Every count here is a LOWER BOUND, and this check cannot PASS.";
        }

        /// <summary>
        /// Which elements sit somewhere the caller did not expect. A category the
        /// rules are silent about is NOT a violation - it is unjudged, and the two
        /// must not be added together.
        /// </summary>
        public static List<WorksetMisplacement> Misplaced(IEnumerable<WorksetMisplacement> observed,
                                                          WorksetRules rules)
        {
            var found = new List<WorksetMisplacement>();
            if (observed == null || rules == null || !rules.Ok) return found;

            foreach (WorksetMisplacement e in observed)
            {
                if (e == null || e.Category == null) continue;
                string want;
                if (!rules.ExpectedByCategory.TryGetValue(e.Category, out want)) continue;
                // An element whose workset could not be read is not evidence that it
                // is on the wrong one. It is counted as unreadable by the caller and
                // never appears here.
                if (e.ActualWorkset == null) continue;
                if (!string.Equals(e.ActualWorkset, want, StringComparison.Ordinal))
                {
                    e.ExpectedWorkset = want;
                    found.Add(e);
                }
            }
            return found;
        }

        /// <summary>Worksets still carrying a name the caller declared to be a default.</summary>
        public static List<string> DefaultNamed(IEnumerable<string> worksetNames, WorksetRules rules)
        {
            var hits = new List<string>();
            if (worksetNames == null || rules == null || !rules.Ok || rules.DefaultWorksetNames.Count == 0)
                return hits;
            foreach (string n in worksetNames)
                if (n != null && rules.DefaultWorksetNames.Any(d => string.Equals(d, n, StringComparison.Ordinal)))
                    hits.Add(n);
            return hits;
        }

        /// <summary>
        /// One workset's share of what was SCANNED - never of the model. The
        /// denominator is named in the reply because "78% of the model" and "78% of
        /// what this scan could see" are different claims about a file with a
        /// closed workset in it.
        /// </summary>
        public static double? ShareOfScanned(long elements, long scanned)
        {
            if (scanned <= 0) return null;
            return Math.Round(elements * 100.0 / scanned, 4);
        }
    }
}
