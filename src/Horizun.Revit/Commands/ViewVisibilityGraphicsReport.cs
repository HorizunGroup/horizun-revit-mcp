using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Commands
{
    public sealed partial class ViewVisibilityCommand
    {
        private static ArchModelEdit GraphicsReport(Document doc, JObject request)
        {
            var rows = new JArray();
            JObject emptyGraphics = GraphicsOverrides(new OverrideGraphicSettings());
            foreach (long id in ((JArray)request["view_ids"]).Values<long>())
            {
                var row = new JObject { ["view_id"] = id };
                rows.Add(row);
                var errors = new JArray(); row["errors"] = errors;
                try
                {
                    var view = doc.GetElement(Rid.Make(id)) as View;
                    if (view == null || view.IsTemplate) throw new ArgumentException("Not a non-template view of this document.");
                    row["unique_id"] = view.UniqueId; row["name"] = view.Name;
                    var template = doc.GetElement(view.ViewTemplateId) as View;
                    if (template != null)
                    {
                        var excluded = new HashSet<long>(template.GetNonControlledTemplateParameterIds().Select(Rid.Value));
                        row["template"] = new JObject { ["id"] = Rid.Value(template.Id), ["unique_id"] = template.UniqueId,
                            ["name"] = template.Name, ["controlled_parameters"] = new JArray(template.GetTemplateParameterIds()
                                .Where(p => !excluded.Contains(Rid.Value(p))).Select(p => (JToken)new JObject
                                { ["id"] = Rid.Value(p), ["name"] = GraphicsParameterName(doc, p) })) };
                    }
                    else row["template"] = JValue.CreateNull();
                    bool filtersByTemplate = ManageViewsCommand.TemplateGoverns(template, BuiltInParameter.VIS_GRAPHICS_FILTERS);
                    View filterSource = filtersByTemplate ? template : view;
                    row["filters_source"] = filtersByTemplate ? "template" : "view";
                    HashSet<long> inView = null;
                    try
                    {
                        if (!FilteredElementCollector.IsViewValidForElementIteration(doc, view.Id))
                            throw new InvalidOperationException("View does not support element iteration.");
                        inView = new HashSet<long>(new FilteredElementCollector(doc, view.Id).WhereElementIsNotElementType().ToElementIds().Select(Rid.Value));
                    }
                    catch (Exception ex) { errors.Add("View element count unknown: " + ex.Message); }
                    var filters = new JArray(); row["filters"] = filters;
                    foreach (ElementId filterId in filterSource.GetOrderedFilters())
                    {
                        var filter = doc.GetElement(filterId) as FilterElement;
                        var f = new JObject { ["id"] = Rid.Value(filterId), ["unique_id"] = filter?.UniqueId,
                            ["name"] = filter?.Name, ["matching_model_elements"] = JValue.CreateNull(),
                            ["matching_view_collector_elements"] = JValue.CreateNull() };
                        filters.Add(f);
                        try
                        {
                            f["visible"] = filterSource.GetFilterVisibility(filterId);
                            f["enabled"] = filterSource.GetIsFilterEnabled(filterId);
                            f["overrides"] = GraphicsOverrides(filterSource.GetFilterOverrides(filterId));
                            ICollection<ElementId> matched;
                            if (filter is ParameterFilterElement parameter)
                            {
                                var collector = new FilteredElementCollector(doc).WhereElementIsNotElementType()
                                    .WherePasses(new ElementMulticategoryFilter(parameter.GetCategories()));
                                ElementFilter predicate = parameter.GetElementFilter();
                                if (predicate != null) collector.WherePasses(predicate);
                                matched = collector.ToElementIds();
                            }
                            else if (filter is SelectionFilterElement selection) matched = selection.GetElementIds();
                            else throw new InvalidOperationException("Filter kind cannot be evaluated.");
                            f["matching_model_elements"] = matched.Count;
                            if (inView != null) f["matching_view_collector_elements"] = matched.Count(e => inView.Contains(Rid.Value(e)));
                        }
                        catch (Exception ex) { f["error"] = ex.Message; errors.Add("Filter " + Rid.Value(filterId) + ": " + ex.Message); }
                    }
                    var categories = new JArray(); row["category_overrides"] = categories;
                    var categoryWarnings = new JArray(); row["category_warnings"] = categoryWarnings;
                    foreach (Category category in GraphicsViewCategories(doc, view))
                    {
                        try
                        {
                            View source = ManageViewsCommand.TemplateGoverns(template, GraphicsCategoryParameter(category)) ? template : view;
                            if (!source.IsCategoryOverridable(category.Id)) continue;
                            var settings = source.GetCategoryOverrides(category.Id);
                            bool? hidden = source.CanCategoryBeHidden(category.Id) ? source.GetCategoryHidden(category.Id) : (bool?)null;
                            if (!JToken.DeepEquals(GraphicsOverrides(settings), emptyGraphics) || hidden == true)
                                categories.Add(new JObject { ["id"] = Rid.Value(category.Id), ["name"] = category.Name,
                                    ["source"] = ReferenceEquals(source, view) ? "view" : "template",
                                    ["hidden"] = hidden.HasValue ? (JToken)hidden.Value : JValue.CreateNull(),
                                    ["overrides"] = GraphicsOverrides(settings) });
                        }
                        catch (Exception ex) { categoryWarnings.Add("Category " + Rid.Value(category.Id) + ": " + ex.Message); }
                    }
                }
                catch (Exception ex) { row["error"] = ex.Message; errors.Add(ex.Message); }
            }
            bool complete = ViewVisibilityRules.StampGraphicsCoverage(rows);
            return new ArchModelEdit { ReadResult = new JObject { ["operation"] = "graphics_report", ["read_only"] = true,
                ["views"] = rows, ["complete"] = complete, ["partial"] = !complete,
                ["count_basis"] = "Model matches evaluate the filter predicate/categories. View counts intersect the native view collector, which is affected by visibility, crop and phase; zero is not proof that a hidden filter has no potential members. Linked elements are not counted." } };
        }

        private static string GraphicsParameterName(Document doc, ElementId id)
        {
            try { return LabelUtils.GetLabelFor((BuiltInParameter)Rid.Value(id)); }
            catch { return doc.GetElement(id)?.Name ?? Rid.Value(id).ToString(); }
        }

    }
}
