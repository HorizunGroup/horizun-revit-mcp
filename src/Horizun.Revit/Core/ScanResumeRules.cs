using System.Collections.Generic;
using System;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public static class ScanResumeRules
    {
        // Continuations carry only sections, so remember the last emitted list for a document.
        // An unrelated request starts a new chain. Access is serialized by the Revit dispatcher.
        public sealed class ProgressTracker
        {
            private string document;
            private HashSet<string> expected;
            private HashSet<string> pending;
            public List<string> Resume(string documentKey, IEnumerable<string> requested,
                IEnumerable<string> notMeasured, IEnumerable<string> weightSources, IEnumerable<string> order)
            {
                var current = new HashSet<string>(notMeasured, StringComparer.OrdinalIgnoreCase);
                var requestSet = new HashSet<string>(requested, StringComparer.OrdinalIgnoreCase);
                var sources = new HashSet<string>(weightSources, StringComparer.OrdinalIgnoreCase);
                bool continuation = document == documentKey && expected != null && expected.SetEquals(requestSet);
                var stalled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (continuation && pending != null)
                {
                    // Re-measuring weight sources is required and is not forward progress.
                    bool advanced = pending.Any(s => !current.Contains(s) && !sources.Contains(s));
                    if (!advanced) stalled.UnionWith(current.Where(pending.Contains));
                }
                if (stalled.Overlaps(sources) && current.Contains("weight")) stalled.Add("weight");
                var remaining = current.Where(s => !stalled.Contains(s)).ToList();
                var resume = ResumeSections(remaining, sources, order);
                if (stalled.Count != 0) resume.RemoveAll(s => stalled.Contains(s));
                document = documentKey;
                expected = resume.Count == 0 ? null : new HashSet<string>(resume, StringComparer.OrdinalIgnoreCase);
                pending = current;
                return resume;
            }
        }
        public static JObject CostEstimate(int elements, int sectionsRequested, int budgetMs) => new JObject
        {
            ["elements"] = elements,
            ["sections_requested"] = sectionsRequested,
            ["budget_ms"] = budgetMs,
            ["note"] = "Elements is a count, not an estimate of time."
        };

        public static List<string> ResumeSections(IEnumerable<string> notMeasured,
            IEnumerable<string> weightMissing, IEnumerable<string> sectionOrder)
        {
            var pending = notMeasured.ToList();
            if (!pending.Contains("weight")) return pending;
            var needed = new HashSet<string>(pending.Concat(weightMissing));
            var ordered = sectionOrder.Where(needed.Remove).ToList();
            ordered.AddRange(pending.Concat(weightMissing).Where(needed.Remove));
            return ordered;
        }

        public static List<string> WeightMissingSources(ISet<string> measuredOk, IEnumerable<string> weightSources) =>
            weightSources.Where(source => !measuredOk.Contains(source)).ToList();
    }
}
