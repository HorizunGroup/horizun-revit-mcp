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
        public static HashSet<long> TargetIds(Document doc, IEnumerable<ElementId> sourceIds, bool strict)
        {
            var targets = new HashSet<long>();
            var stack = new Stack<ElementId>(sourceIds);
            while (stack.Count > 0)
            {
                ElementId id = stack.Pop();
                long key = Rid.Value(id);
                if (!targets.Add(key)) continue;
                Element element = doc.GetElement(id);
                if (element is Group group)
                {
                    try { foreach (ElementId member in group.GetMemberIds()) stack.Push(member); }
                    catch when (!strict) { }
                }
                else if (strict && element == null)
                    throw new ArgumentException("Group or member " + key + " could not be read.");
            }
            return targets;
        }

        public static JObject Read(Document doc, IEnumerable<ElementId> sourceIds) =>
            ReadTargets(doc, TargetIds(doc, sourceIds, false), false);

        public static JObject ReadTargets(Document doc, ISet<long> targets, bool verifyReferences)
        {
            var facts = new List<AnnotationDependencyFact>();
            var unreadable = new JArray();
            var identities = new Dictionary<long, JObject>();
            void Remember(Element element, IEnumerable<Reference> references)
            {
                identities.Add(Rid.Value(element.Id), new JObject { ["unique_id"] = element.UniqueId,
                    ["stable_references"] = new JArray(references.Select(r => r.ConvertToStableRepresentation(doc))
                        .OrderBy(s => s, StringComparer.Ordinal)) });
            }
            foreach (IndependentTag tag in new FilteredElementCollector(doc).OfClass(typeof(IndependentTag)))
            {
                try
                {
                    var refs = tag.GetTaggedLocalElementIds().Select(Rid.Value).ToList();
                    if (verifyReferences && refs.Any(targets.Contains))
                    {
                        if (tag.IsOrphaned) throw new InvalidOperationException("tag is orphaned");
                        Remember(tag, tag.GetTaggedReferences());
                    }
                    facts.Add(new AnnotationDependencyFact { Id = Rid.Value(tag.Id),
                        ViewId = Rid.Value(tag.OwnerViewId), Kind = "tag", References = refs });
                }
                catch (Exception ex)
                {
                    unreadable.Add(new JObject { ["id"] = Rid.Value(tag.Id), ["kind"] = "tag",
                        ["reason"] = verifyReferences ? ex.Message : ex.GetType().Name });
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
                    if (verifyReferences && refs.Any(targets.Contains))
                    {
                        if (!dimension.AreReferencesAvailable || refs.Where(targets.Contains).Any(id => doc.GetElement(Rid.Make(id)) == null))
                            throw new InvalidOperationException("dimension references are unavailable");
                        Remember(dimension, array.Cast<Reference>());
                    }
                    facts.Add(new AnnotationDependencyFact { Id = Rid.Value(dimension.Id),
                        ViewId = Rid.Value(dimension.OwnerViewId), Kind = "dimension", References = refs });
                }
                catch (Exception ex)
                {
                    unreadable.Add(new JObject { ["id"] = Rid.Value(dimension.Id), ["kind"] = "dimension",
                        ["reason"] = verifyReferences ? ex.Message : ex.GetType().Name });
                }
            }
            var views = AnnotationDependencyRules.ForTargets(facts, targets);
            var rows = new JArray();
            foreach (var view in views)
            {
                var element = Rid.CanRepresent(view.ViewId) ? doc.GetElement(Rid.Make(view.ViewId)) as View : null;
                rows.Add(new JObject { ["view_id"] = view.ViewId, ["view_name"] = element?.Name,
                    ["tags"] = new JArray(view.Items.Where(x => x.Kind == "tag").Select(Row)),
                    ["dimensions"] = new JArray(view.Items.Where(x => x.Kind == "dimension").Select(Row)) });
            }
            return new JObject { ["target_and_member_count"] = targets.Count,
                ["view_count"] = rows.Count,
                ["dependent_tag_count"] = views.Sum(v => v.Items.Count(x => x.Kind == "tag")),
                ["dependent_dimension_count"] = views.Sum(v => v.Items.Count(x => x.Kind == "dimension")),
                ["by_view"] = rows, ["unreadable_annotations"] = unreadable,
                ["coverage_complete"] = unreadable.Count == 0,
                ["means"] = "Annotations reference a target or current group member. Their fate after a change " +
                    "must be re-read; this list is a before snapshot, not a promise that Revit preserves them." };
            JObject Row(AnnotationDependencyFact fact)
            {
                var row = new JObject { ["id"] = fact.Id, ["referenced_target_ids"] = new JArray(fact.References) };
                if (identities.TryGetValue(fact.Id, out JObject identity)) row.Merge(identity);
                return row;
            }
        }
    }
}
