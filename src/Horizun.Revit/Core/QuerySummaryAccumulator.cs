using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    // Counts have the same case folding and blank-name handling as JsonObjectKey.
    // Canonical traversal order matches the full query so casing is stable across modes.
    public sealed class QuerySummaryAccumulator
    {
        sealed class Bucket
        {
            public string Label, Kind, Model;
            public long Link, Id;
            public int Count;
        }
        readonly Dictionary<string, Bucket> categories = new Dictionary<string, Bucket>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, Bucket> levels = new Dictionary<string, Bucket>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, Bucket> sources = new Dictionary<string, Bucket>(StringComparer.OrdinalIgnoreCase);
        public int Count { get; private set; }
        public void Add(string category, string level, string sourceKind, string sourceModel, long? linkId = null, long id = 0)
        {
            Increment(categories, category ?? "(no category)", sourceKind, sourceModel, linkId ?? -1, id);
            Increment(levels, level ?? "(no level)", sourceKind, sourceModel, linkId ?? -1, id);
            Increment(sources, sourceKind + ":" + (sourceModel ?? "(unknown)"), sourceKind, sourceModel, linkId ?? -1, id);
            Count++;
        }
        static void Increment(Dictionary<string, Bucket> counts, string value, string kind, string model, long link, long id)
        {
            string key = JsonObjectKey.Summary(value);
            if (!counts.TryGetValue(key, out Bucket bucket))
                counts[key] = bucket = new Bucket { Label = key, Kind = kind, Model = model, Link = link, Id = id };
            int order = StringComparer.Ordinal.Compare(kind, bucket.Kind);
            if (order == 0) order = StringComparer.OrdinalIgnoreCase.Compare(model, bucket.Model);
            if (order == 0) order = link.CompareTo(bucket.Link);
            if (order == 0) order = id.CompareTo(bucket.Id);
            if (order < 0) { bucket.Label = key; bucket.Kind = kind; bucket.Model = model; bucket.Link = link; bucket.Id = id; }
            bucket.Count++;
        }
        static JObject Json(Dictionary<string, Bucket> counts)
        {
            var result = new JObject();
            foreach (var bucket in counts.Values.OrderBy(p => p.Label, StringComparer.OrdinalIgnoreCase)) result[bucket.Label] = bucket.Count;
            return result;
        }
        public JObject ToJson() => new JObject { ["by_category"] = Json(categories), ["by_level"] = Json(levels), ["by_source"] = Json(sources) };
    }
}
