using System.Collections.Generic;
using System.Linq;

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
