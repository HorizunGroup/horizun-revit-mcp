using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Horizun.Revit.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Commands
{
    public sealed partial class ViewVisibilityCommand
    {
        private static ArchModelEdit CopyViewGraphics(Document doc, JObject r, View source, HashSet<string> session)
        {
            var include = ViewVisibilityRules.GraphicsIncludes(r);
            var parameters = new HashSet<long>();
            if (include.Contains("filters")) parameters.Add((long)BuiltInParameter.VIS_GRAPHICS_FILTERS);
            if (include.Contains("categories"))
            {
                parameters.Add((long)BuiltInParameter.VIS_GRAPHICS_MODEL);
                parameters.Add((long)BuiltInParameter.VIS_GRAPHICS_ANNOTATION);
                parameters.Add((long)BuiltInParameter.VIS_GRAPHICS_ANALYTICAL_MODEL);
            }
            var elementIds = new List<ElementId>();
            if (include.Contains("elements"))
            {
                if (!(r["element_ids"] is JArray ids) || ids.Count == 0)
                    throw new ArgumentException("include=elements requires explicit element_ids.");
                elementIds = ids.Values<long>().Select(Rid.Make).ToList();
                if (elementIds.Count > ViewVisibilityRules.MaxIds) throw new ArgumentException("Element override copy exceeds 2000 common model elements; select element_ids explicitly.");
                foreach (ElementId id in elementIds)
                    if (doc.GetElement(id) == null || doc.GetElement(id).OwnerViewId != ElementId.InvalidElementId || doc.GetElement(id).Category?.CategoryType != CategoryType.Model)
                        throw new ArgumentException("Element overrides can be copied only for common model elements; view-owned annotations do not belong to the destination view.");
            }
            var targets = ((JArray)r["view_ids"]).Values<long>().Distinct().Select(id => doc.GetElement(Rid.Make(id)) as View).ToList();
            foreach (View target in targets)
            {
                if (target == null || target.IsTemplate || target.ViewType != source.ViewType) throw new ArgumentException("Every destination must be a non-template view of the source view type.");
                RequireOwn(doc, target, session);
                if (target.GetPrimaryViewId() != ElementId.InvalidElementId || target.GetDependentViewIds().Count > 0)
                    throw new ArgumentException("Dependent or primary views with dependents share graphics; duplicate an independent destination to avoid changing unlisted views.");
                var assigned = doc.GetElement(target.ViewTemplateId) as View;
                if (assigned != null && parameters.Any(p => ManageViewsCommand.TemplateGoverns(assigned, (BuiltInParameter)p)))
                    throw new ArgumentException("Destination template controls the requested graphics; it would override the copy.");
            }
            var supported = new HashSet<long>(source.GetTemplateParameterIds().Select(Rid.Value));
            parameters.IntersectWith(supported);
            if (include.Any(i => i != "elements") && parameters.Count == 0) throw new ArgumentException("This view type has no transferable category/filter graphics groups.");
            JObject wanted = CopyGraphicsState(doc, source, include, elementIds);
            var templatesBefore = targets.ToDictionary(v => Rid.Value(v.Id), v => Rid.Value(v.ViewTemplateId));
            var edit = new ArchModelEdit();
            edit.Summary["operation"] = "copy_view_graphics";
            edit.Summary["include"] = new JArray(include);
            edit.Summary["element_count"] = elementIds.Count;
            edit.Summary["source_graphics"] = wanted;
            edit.Planned.Add(new PlannedElement { Action = PlannedAction.Read, ElementId = Rid.Value(source.Id), UniqueId = source.UniqueId });
            foreach (View target in targets)
            {
                var planned = ModelEditRunner.Planned(target, PlannedAction.Modify, r);
                planned.BeforeValues["source_graphics"] = wanted.ToString(Formatting.None);
                planned.BeforeValues["destination_graphics"] = CopyGraphicsState(doc, target, include, elementIds).ToString(Formatting.None);
                edit.Planned.Add(planned);
            }
            ElementId temporary = ElementId.InvalidElementId;
            edit.Apply = d =>
            {
                View template = null;
                if (parameters.Count > 0)
                {
                    template = ((View)d.GetElement(source.Id)).CreateViewTemplate();
                    temporary = template.Id;
                    template.SetNonControlledTemplateParameterIds(template.GetTemplateParameterIds().Where(id => !parameters.Contains(Rid.Value(id))).ToList());
                }
                foreach (View target in targets)
                {
                    var view = (View)d.GetElement(target.Id);
                    if (template != null) view.ApplyViewTemplateParameters(template);
                    foreach (ElementId id in elementIds) view.SetElementOverrides(id, ((View)d.GetElement(source.Id)).GetElementOverrides(id));
                }
                if (template != null) d.Delete(template.Id);
            };
            edit.Verify = d =>
            {
                var checks = new PostconditionCheck(targets.SelectMany(v => new[] { "graphics:" + Rid.Value(v.Id), "template:" + Rid.Value(v.Id) }).Concat(new[] { "temporary_template_deleted" }).ToArray());
                checks.Compare("temporary_template_deleted", true, temporary == ElementId.InvalidElementId || d.GetElement(temporary) == null);
                foreach (View target in targets)
                {
                    View now = (View)d.GetElement(target.Id);
                    checks.Compare("graphics:" + Rid.Value(target.Id), true, JToken.DeepEquals(wanted, CopyGraphicsState(d, now, include, elementIds)));
                    checks.Compare("template:" + Rid.Value(target.Id), templatesBefore[Rid.Value(target.Id)], Rid.Value(now.ViewTemplateId));
                }
                return checks;
            };
            return edit;
        }

        private static JObject CopyGraphicsState(Document doc, View view, string[] include, IList<ElementId> elements)
        {
            var result = new JObject();
            var template = doc.GetElement(view.ViewTemplateId) as View;
            if (include.Contains("filters"))
            {
                View filterSource = ManageViewsCommand.TemplateGoverns(template, BuiltInParameter.VIS_GRAPHICS_FILTERS) ? template : view;
                result["filters"] = new JArray(filterSource.GetOrderedFilters().Select(id => (JToken)new JObject
                { ["id"] = Rid.Value(id), ["visible"] = filterSource.GetFilterVisibility(id), ["enabled"] = filterSource.GetIsFilterEnabled(id),
                    ["overrides"] = GraphicsOverrides(filterSource.GetFilterOverrides(id)) }));
            }
            if (include.Contains("categories"))
                result["categories"] = new JArray(GraphicsViewCategories(doc, view).OrderBy(c => Rid.Value(c.Id)).Select(c =>
                {
                    var parameter = GraphicsCategoryParameter(c);
                    View categorySource = ManageViewsCommand.TemplateGoverns(template, parameter) ? template : view;
                    return (JToken)new JObject { ["id"] = Rid.Value(c.Id), ["hidden"] = categorySource.CanCategoryBeHidden(c.Id) ? (JToken)categorySource.GetCategoryHidden(c.Id) : JValue.CreateNull(),
                        ["overrides"] = categorySource.IsCategoryOverridable(c.Id) ? GraphicsOverrides(categorySource.GetCategoryOverrides(c.Id)) : null };
                }));
            if (include.Contains("elements"))
                result["elements"] = new JArray(elements.Select(id => (JToken)new JObject { ["id"] = Rid.Value(id),
                    ["unique_id"] = doc.GetElement(id)?.UniqueId,
                    ["overrides"] = GraphicsOverrides(view.GetElementOverrides(id)) }));
            return result;
        }

        private static JObject GraphicsOverrides(OverrideGraphicSettings settings)
        {
            var result = new JObject();
            foreach (string name in new[] { "ProjectionLineColor", "CutLineColor", "ProjectionLineWeight", "CutLineWeight",
                "ProjectionLinePatternId", "CutLinePatternId", "SurfaceForegroundPatternColor", "SurfaceForegroundPatternId",
                "SurfaceBackgroundPatternColor", "SurfaceBackgroundPatternId", "CutForegroundPatternColor", "CutForegroundPatternId",
                "CutBackgroundPatternColor", "CutBackgroundPatternId", "IsSurfaceForegroundPatternVisible", "IsSurfaceBackgroundPatternVisible",
                "IsCutForegroundPatternVisible", "IsCutBackgroundPatternVisible", "Transparency", "Halftone", "DetailLevel" })
            {
                var property = typeof(OverrideGraphicSettings).GetProperty(name);
                if (property == null) throw new InvalidOperationException("Graphics property is not readable in this Revit: " + name);
                object value = property.GetValue(settings, null);
                if (value is ElementId id) result[name] = Rid.Value(id);
                else if (value is Color color) result[name] = color.IsValid ? (JToken)new JArray(color.Red, color.Green, color.Blue) : JValue.CreateNull();
                else result[name] = value == null ? JValue.CreateNull() : JToken.FromObject(value);
            }
            return result;
        }

        private static IEnumerable<Category> GraphicsCategories(Categories categories)
        {
            foreach (Category category in categories)
                foreach (Category item in GraphicsCategoryTree(category)) yield return item;
        }
        private static IEnumerable<Category> GraphicsCategoryTree(Category category)
        {
            if (category.CategoryType == CategoryType.Internal || category.CategoryType == CategoryType.Invalid) yield break;
            yield return category;
            foreach (Category child in category.SubCategories)
                foreach (Category descendant in GraphicsCategoryTree(child)) yield return descendant;
        }

        private static BuiltInParameter GraphicsCategoryParameter(Category category) => category.CategoryType == CategoryType.Annotation
            ? BuiltInParameter.VIS_GRAPHICS_ANNOTATION : category.CategoryType == CategoryType.AnalyticalModel
            ? BuiltInParameter.VIS_GRAPHICS_ANALYTICAL_MODEL : BuiltInParameter.VIS_GRAPHICS_MODEL;

        private static IEnumerable<Category> GraphicsViewCategories(Document doc, View view)
        {
            var supported = new HashSet<long>(view.GetTemplateParameterIds().Select(Rid.Value));
            return GraphicsCategories(doc.Settings.Categories).Where(c => supported.Contains((long)GraphicsCategoryParameter(c)));
        }
    }
}
