// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// A MODEL COMPARED AGAINST WHAT IT WAS MEANT TO STAND ON: a project template
// (.rte/.rvt) and/or a shared parameter file. Field session 2026-09-25: this
// single read answered more of "what is wrong with this model" than any other
// check that day - 495 parameters the template never declared, three
// "Material" parameters whose guid did not match the shared parameter file
// (three DIFFERENT parameters wearing one name), 465 view filters and 631
// line patterns nobody drew from the template. All of it had to be found by
// hand, one collector at a time, through horizun_execute_python.
//
// Revit-free: given the model's and the template's own facts - already read
// by the command that opened both - this decides what is EXTRA (in the model,
// not the template), MISSING (in the template, not the model) and IN CONFLICT
// (present in both under the same identity, but not the same thing). A
// project parameter and a shared parameter are identified differently on
// purpose: a shared parameter's guid is the same parameter wherever it is
// bound, so two guids that differ ARE two different parameters even if their
// name is identical - the exact shape of the three "Material" parameters this
// was written to catch. A parameter with no guid (project-only, unbound to a
// shared definition) has nothing else stable to key on, so it is compared by
// name, and that is reported alongside it rather than assumed silently.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;

namespace Horizun.Revit.Core
{
    /// <summary>One comparable thing, from either side. Category groups facts that are
    /// compared against each other and never across groups (a filter is never compared
    /// with a line pattern). Key is the stable identity within that category: a guid
    /// when one exists, the name otherwise - KeyIsGuid says which.</summary>
    public sealed class TemplateFact
    {
        public string Category;
        public string Key;
        public bool KeyIsGuid;
        public string Name;
        /// <summary>Attributes compared for a conflict when both sides share a Key: e.g.
        /// data_type, binding, category_set for a parameter; line_weight, color for a
        /// subcategory. Only attributes present on BOTH sides are compared - one side
        /// missing an attribute is not, by itself, a conflict.</summary>
        public Dictionary<string, string> Attributes = new Dictionary<string, string>(StringComparer.Ordinal);
    }

    public sealed class TemplateConflict
    {
        public string Category;
        public string Key;
        public string Name;
        public List<string> DifferingAttributes = new List<string>();
        public Dictionary<string, string> ModelValues = new Dictionary<string, string>(StringComparer.Ordinal);
        public Dictionary<string, string> TemplateValues = new Dictionary<string, string>(StringComparer.Ordinal);
    }

    public sealed class TemplateCategoryResult
    {
        public string Category;
        public List<TemplateFact> Extra = new List<TemplateFact>();      // in model, not template/spf
        public List<TemplateFact> Missing = new List<TemplateFact>();    // in template/spf, not model
        public List<TemplateConflict> Conflicts = new List<TemplateConflict>();
        public int ModelCount;
        public int TemplateCount;
    }

    public static class TemplateComparisonRules
    {
        public static readonly string IdentityMeans =
            "a shared parameter is identified by its guid - the same parameter wherever it is bound, so " +
            "two guids under one name ARE two different parameters. A parameter with no guid (project-only) " +
            "has no other stable identity and is compared by name; key_is_guid on each row says which " +
            "reading was used, because a name match on an unbound parameter is a weaker claim than a guid " +
            "match and a report should not blur the two.";

        /// <summary>
        /// Compares one category's facts from the model against the template/SPF. Facts from a
        /// different category are never mixed in by this function - the caller groups them first,
        /// one call per category, so a filter can never be reported extra against a line pattern.
        /// </summary>
        public static TemplateCategoryResult Compare(string category, IEnumerable<TemplateFact> modelFacts,
                                                      IEnumerable<TemplateFact> templateFacts)
        {
            var result = new TemplateCategoryResult { Category = category };
            var model = (modelFacts ?? Enumerable.Empty<TemplateFact>()).ToList();
            var template = (templateFacts ?? Enumerable.Empty<TemplateFact>()).ToList();
            result.ModelCount = model.Count;
            result.TemplateCount = template.Count;

            var templateByKey = new Dictionary<string, TemplateFact>(StringComparer.Ordinal);
            foreach (TemplateFact f in template)
                if (f.Key != null && !templateByKey.ContainsKey(f.Key)) templateByKey[f.Key] = f;

            var matchedKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (TemplateFact m in model)
            {
                TemplateFact t;
                if (m.Key == null || !templateByKey.TryGetValue(m.Key, out t))
                {
                    result.Extra.Add(m);
                    continue;
                }
                matchedKeys.Add(m.Key);
                List<string> differing = DifferingAttributes(m.Attributes, t.Attributes);
                if (differing.Count > 0)
                    result.Conflicts.Add(new TemplateConflict
                    {
                        Category = category,
                        Key = m.Key,
                        Name = m.Name,
                        DifferingAttributes = differing,
                        ModelValues = differing.ToDictionary(a => a, a => m.Attributes[a]),
                        TemplateValues = differing.ToDictionary(a => a, a => t.Attributes[a])
                    });
            }

            foreach (TemplateFact t in template)
                if (t.Key == null || !matchedKeys.Contains(t.Key)) result.Missing.Add(t);

            return result;
        }

        private static List<string> DifferingAttributes(Dictionary<string, string> a, Dictionary<string, string> b)
        {
            var differing = new List<string>();
            foreach (KeyValuePair<string, string> kv in a)
            {
                string other;
                if (b.TryGetValue(kv.Key, out other) && !string.Equals(kv.Value, other, StringComparison.Ordinal))
                    differing.Add(kv.Key);
            }
            differing.Sort(StringComparer.Ordinal);
            return differing;
        }
    }
}
