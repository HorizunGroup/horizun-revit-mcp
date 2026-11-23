using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public sealed class AnnotationDependencyFact
    {
        public long Id, ViewId;
        public string Kind;
        public List<long> References = new List<long>();
    }

    public sealed class AnnotationDependencyView
    {
        public long ViewId;
        public List<AnnotationDependencyFact> Items = new List<AnnotationDependencyFact>();
    }

    public static class AnnotationDependencyRules
    {
        public static bool TablesCovered(bool scopeKnown, IEnumerable<bool> reads)
            => scopeKnown && reads != null && reads.All(x => x);
        public static bool ScheduleBodyAllowed(int rows, int columns, int characters, bool coverageRead)
            => coverageRead || (rows <= 1000 && columns <= 100 && characters <= 30000);
        public static string ScheduleVerdict(string storedHash, bool? sourceExists, string currentHash)
        {
            if (string.IsNullOrEmpty(storedHash)) return "unknown";
            if (sourceExists == false) return "source_deleted";
            if (sourceExists != true || string.IsNullOrEmpty(currentHash)) return "unknown";
            return System.String.Equals(storedHash, currentHash, System.StringComparison.Ordinal) ? "current" : "stale";
        }
        public static bool IsClean(int stale, int unknown, bool raysCovered, bool tablesCovered)
            => stale == 0 && unknown == 0 && raysCovered && tablesCovered;

        public static bool ReferencesPreserved(JObject before, JObject after) =>
            before?.Value<bool>("coverage_complete") == true && after?.Value<bool>("coverage_complete") == true &&
            before["by_view"] is JArray expected && after["by_view"] is JArray found && JToken.DeepEquals(expected, found);

        public static List<AnnotationDependencyView> ForTargets(
            IEnumerable<AnnotationDependencyFact> annotations, ISet<long> targetIds)
        {
            return annotations.Select(a => new AnnotationDependencyFact {
                    Id = a.Id, ViewId = a.ViewId, Kind = a.Kind,
                    References = a.References.Where(targetIds.Contains).Distinct().OrderBy(x => x).ToList()
                })
                .Where(a => a.References.Count > 0)
                .GroupBy(a => a.ViewId)
                .OrderBy(g => g.Key)
                .Select(g => new AnnotationDependencyView { ViewId = g.Key,
                    Items = g.OrderBy(a => a.Kind).ThenBy(a => a.Id).ToList() })
                .ToList();
        }
    }
}
