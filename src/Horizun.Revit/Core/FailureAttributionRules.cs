// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// A BATCH OF 60 DOORS ROLLED BACK FOR ONE, AND THE ANSWER POINTED AT THE LAST (field cycle, 2026-10-21).
//
// MEASURED: horizun_create_elements with 60 doors answered "Atomic creation failed at item 59 ...
// Can't cut instance out of Wall" - the item index was simply the last one processed, because
// the error surfaced when the transaction COMMITTED, after every item had been created. The
// door that cuts another wall was somewhere in the 60 and the caller had to bisect from outside.
//
// Revit does know: each failure message names the elements that fail. This file matches those
// element ids against the elements the batch created and says WHICH items the failures point
// at, in the batch's own numbering. It changes nothing about what is rolled back.
//
// Revit-free: the Revit half only records (description, severity, ids); the matching is here.
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public sealed class FailureFact
    {
        public string Description;
        public bool IsError;
        /// <summary>Elements Revit names as failing (and the additional ones it lists).</summary>
        public long[] ElementIds = new long[0];
    }

    public static class FailureAttributionRules
    {
        /// <summary>
        /// Every ERROR fact that names at least one created element, as
        /// {items:[plan indices], element_ids:[...], description}. Warnings are not culprits.
        /// A fact that names no created element is kept with items=[] so it is not lost.
        /// </summary>
        public static JArray Attribute(IEnumerable<FailureFact> facts, IDictionary<long, int> createdIdToIndex)
        {
            var result = new JArray();
            if (facts == null) return result;
            foreach (FailureFact fact in facts.Where(f => f != null && f.IsError))
            {
                var items = new SortedSet<int>();
                if (createdIdToIndex != null)
                    foreach (long id in fact.ElementIds ?? new long[0])
                        if (createdIdToIndex.TryGetValue(id, out int index)) items.Add(index);
                result.Add(new JObject
                {
                    ["items"] = new JArray(items.Cast<object>().ToArray()),
                    ["element_ids"] = new JArray((fact.ElementIds ?? new long[0]).Cast<object>().ToArray()),
                    ["description"] = fact.Description
                });
            }
            return result;
        }

        /// <summary>The distinct batch items the errors point at, ascending.</summary>
        public static List<int> Culprits(JArray attributed)
        {
            var list = new SortedSet<int>();
            if (attributed != null)
                foreach (JObject a in attributed.OfType<JObject>())
                    if (a["items"] is JArray items)
                        foreach (JToken t in items) list.Add((int)t);
            return list.ToList();
        }

        /// <summary>One sentence for the error message; empty when Revit named no created element.</summary>
        public static string Sentence(JArray attributed)
        {
            List<int> culprits = Culprits(attributed);
            if (culprits.Count == 0)
            {
                // MEASURED (Revit 2023, 2026-10-23): 97 create_elements rollbacks in one day read
                // only "did not commit" because Revit's error named no element of the batch (or
                // none at all), and the sentence was dropped with the attribution. What Revit said
                // is still the cause; it is quoted even when it cannot be pinned to an item.
                var said = (attributed ?? new JArray()).OfType<JObject>()
                    .Select(a => a["description"]?.ToString()).Where(d => !string.IsNullOrWhiteSpace(d))
                    .Distinct().Take(3).ToList();
                if (said.Count == 0) return "";
                var named = (attributed ?? new JArray()).OfType<JObject>()
                    .SelectMany(a => (a["element_ids"] as JArray ?? new JArray()).Select(t => (long)t)).Distinct().OrderBy(x => x).Take(10).ToList();
                return " Revit said: " + string.Join("; ", said) +
                       (named.Count == 0 ? "" : " [elements " + string.Join(", ", named) + ", none of them created by this batch]") +
                       ". No item of the batch is named, so the item index above is only the last one processed.";
            }
            string first = ((attributed.OfType<JObject>().FirstOrDefault(a => a["items"] is JArray i && i.Count > 0))?["description"])?.ToString();
            return " Revit names item" + (culprits.Count == 1 ? " " : "s ") + string.Join(", ", culprits) +
                   " as the cause" + (string.IsNullOrWhiteSpace(first) ? "" : " (" + first + ")") +
                   "; the failure surfaced at commit, so the item index above is only the last one processed.";
        }
    }
}
