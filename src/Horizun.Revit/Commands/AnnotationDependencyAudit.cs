using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    // Read-only evidence for operations that delete or recreate members. The
    // view is the owner of each annotation, not the active view of the caller.
    internal static class AnnotationDependencyAudit
    {
        public static JObject Read(Document doc, IEnumerable<ElementId> sourceIds)
        {
            var targets = new HashSet<long>();
            var stack = new Stack<ElementId>(sourceIds);
            while (stack.Count > 0)
            {
                ElementId id = stack.Pop();
                long key = Rid.Value(id);
                if (!targets.Add(key)) continue;
                if (doc.GetElement(id) is Group group)
                {
                    try { foreach (ElementId member in group.GetMemberIds()) stack.Push(member); }
                    catch { /* named as unreadable group below */ }
                }
            }
            var facts = new List<AnnotationDependencyFact>();
            var unreadable = new JArray();
            foreach (IndependentTag tag in new FilteredElementCollector(doc).OfClass(typeof(IndependentTag)))
            {
                try
                {
                    facts.Add(new AnnotationDependencyFact { Id = Rid.Value(tag.Id),
                        ViewId = Rid.Value(tag.OwnerViewId), Kind = "tag",
                        References = tag.GetTaggedLocalElementIds().Select(Rid.Value).ToList() });
                }
                catch (Exception ex)
                {
                    unreadable.Add(new JObject { ["id"] = Rid.Value(tag.Id), ["kind"] = "tag",
                        ["reason"] = ex.GetType().Name });
                }
            }
            foreach (Dimension dimension in new FilteredElementCollector(doc).OfClass(typeof(Dimension)))
            {
                try
                {
                    var refs = new List<long>();
                    ReferenceArray array = dimension.References;
                    if (array == null) throw new InvalidOperationException("references unavailable");
                    foreach (Reference reference in array)
                        if (reference != null && reference.ElementId != ElementId.InvalidElementId)
                            refs.Add(Rid.Value(reference.ElementId));
                    facts.Add(new AnnotationDependencyFact { Id = Rid.Value(dimension.Id),
                        ViewId = Rid.Value(dimension.OwnerViewId), Kind = "dimension", References = refs });
                }
                catch (Exception ex)
                {
                    unreadable.Add(new JObject { ["id"] = Rid.Value(dimension.Id), ["kind"] = "dimension",
                        ["reason"] = ex.GetType().Name });
                }
            }
            var views = AnnotationDependencyRules.ForTargets(facts, targets);
            var rows = new JArray();
            foreach (var view in views)
            {
                var element = Rid.CanRepresent(view.ViewId) ? doc.GetElement(Rid.Make(view.ViewId)) as View : null;
                rows.Add(new JObject { ["view_id"] = view.ViewId, ["view_name"] = element?.Name,
                    ["tags"] = new JArray(view.Items.Where(x => x.Kind == "tag").Select(x => (JToken)Row(x))),
                    ["dimensions"] = new JArray(view.Items.Where(x => x.Kind == "dimension").Select(x => (JToken)Row(x))) });
            }
            return new JObject { ["target_and_member_count"] = targets.Count,
                ["view_count"] = rows.Count,
                ["dependent_tag_count"] = views.Sum(v => v.Items.Count(x => x.Kind == "tag")),
                ["dependent_dimension_count"] = views.Sum(v => v.Items.Count(x => x.Kind == "dimension")),
                ["by_view"] = rows, ["unreadable_annotations"] = unreadable,
                ["coverage_complete"] = unreadable.Count == 0,
                ["means"] = "Annotations reference a target or current group member. Their fate after a change " +
                    "must be re-read; this list is a before snapshot, not a promise that Revit preserves them." };
        }

        private static JObject Row(AnnotationDependencyFact x) => new JObject {
            ["id"] = x.Id, ["referenced_target_ids"] = new JArray(x.References) };
    }
}
