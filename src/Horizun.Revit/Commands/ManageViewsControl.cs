// -----------------------------------------------------------------------------
// Horizun Revit MCP - editing filters that already exist, their order on a view,
// the precedence report, and view templates. Original Horizun code.
//
// Field evidence: 33 scripts on view filters and overrides, most of them editing a
// filter's RULES after the fact or asking why an override did not show. The first
// graphic-control release (ManageViewsGraphics.cs) could create a filter and put it
// on a view; it could not change one, reorder one, switch one off, or say which of
// four layers decided what an element looks like. Those are here.
//
// MEASURED/DECLARED LIMITS, all 2023-2027 (checked against each RevitAPI.xml):
//
//   THERE IS NO SetFilterOrder. View.GetOrderedFilters (2021+) reads the order;
//   nothing writes it. AddFilter APPENDS, so the only way to reorder is to remove
//   every filter and add them back in the new order, restoring each one's
//   overrides, visibility and enabled flag. order_filters does exactly that and
//   re-reads every restored state AT ONCE, before a later action in the batch can
//   change it (MEASURED 2023: checking it at the end of the batch failed a correct
//   reorder followed by apply_filter enabled=false); after the batch it re-reads the
//   relative order. A filter whose state did not survive the round trip fails the
//   batch before commit. move_filter_ids rearranges only the named filters: a
//   duplicated view carries its source's filters (MEASURED 2026), and the caller
//   usually knows only its own.
//
//   A TEMPLATE IS A VIEW. CreateViewTemplate makes one from a view; which
//   parameters it governs is the complement of GetNonControlledTemplateParameterIds,
//   and a parameter not in GetTemplateParameterIds is silently ignored by the
//   setter - so it is refused here by name instead.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public sealed partial class ManageViewsCommand
    {
        internal static readonly string[] ControlOperations =
        {
            "edit_filter", "order_filters", "explain_graphics", "create_template", "set_template_controls"
        };

        internal static bool IsControlOperation(string op) => Array.IndexOf(ControlOperations, op ?? "") >= 0;

        // =====================================================================
        // VALIDATION
        // =====================================================================

        internal static void ValidateControl(Document doc, JObject a, string op, Dictionary<string, Type> known)
        {
            switch (op)
            {
                case "edit_filter":
                {
                    RequireFilterReference(doc, a, known);
                    bool byKey = !string.IsNullOrWhiteSpace(a.Value<string>("filter_key"));
                    ICollection<ElementId> categories;
                    if (a["categories"] != null) categories = ReadCategories(doc, a);
                    else if (byKey)
                        throw new ArgumentException("edit_filter on a filter_key needs categories: the filter does not exist yet to read them from.");
                    else
                    {
                        ParameterFilterElement existing = doc.GetElement(FilterIdApply(doc, a, new Dictionary<string, ElementId>())) as ParameterFilterElement;
                        if (existing == null)
                            throw new ArgumentException("edit_filter edits a rule-based filter; a selection filter has no rules.");
                        categories = existing.GetCategories();
                    }
                    ElementFilter rules = ReadRules(doc, a, categories);
                    if (!ParameterFilterElement.ElementFilterIsAcceptableForParameterFilterElement(
                            doc, new HashSet<ElementId>(categories), rules))
                        throw new ArgumentException(
                            "Revit reports these rules are not acceptable for a view filter over these categories " +
                            "(ParameterFilterElement.ElementFilterIsAcceptableForParameterFilterElement). Nothing was written.");
                    break;
                }

                case "order_filters":
                {
                    View view = GraphicsView(doc, a, known);
                    RequireOverridesAllowed(view, "filters");
                    if (a["move_filter_ids"] != null && a["filter_ids"] != null)
                        throw new ArgumentException("order_filters takes filter_ids (the whole list) OR move_filter_ids (only the filters to rearrange), not both.");
                    if (a["move_filter_ids"] != null)
                    {
                        ReadIdList(a, "move_filter_ids");
                        if (view != null) ResolveFilterOrder(view, a);
                        break;
                    }
                    List<ElementId> wanted = ReadFilterOrder(a);
                    if (view == null) break;
                    var current = view.GetOrderedFilters().ToList();
                    var currentSet = new HashSet<long>(current.Select(Rid.Value));
                    if (wanted.Count != current.Count || wanted.Any(id => !currentSet.Contains(Rid.Value(id))))
                        throw new ArgumentException(
                            "filter_ids must list EXACTLY the filters on view '" + view.Name + "' (" +
                            string.Join(", ", current.Select(Rid.Value)) + ") in the new order: a partial list would " +
                            "have to guess where the rest go.");
                    break;
                }

                case "explain_graphics":
                {
                    View view = GraphicsView(doc, a, known);
                    IList<ElementId> ids = ReadElementIds(doc, a, "element_ids");
                    if (ids.Count == 0 || ids.Count > 20)
                        throw new ArgumentException("explain_graphics reads 1..20 element_ids.");
                    if (view != null && view.IsTemplate)
                        throw new ArgumentException("explain_graphics reads a view that draws elements; '" + view.Name + "' is a template.");
                    break;
                }

                case "create_template":
                {
                    View source = Need<View>(doc, a, "view_id");
                    if (source.IsTemplate) throw new ArgumentException("view_id is already a template; duplicate_view copies one.");
                    if (source is ViewSheet || source is ViewSchedule)
                        throw new ArgumentException("a template is made from a graphical view, not a " + source.GetType().Name + ".");
                    if (string.IsNullOrWhiteSpace(a.Value<string>("name")))
                        throw new ArgumentException("name is required: Revit would call it after the source view, and nobody chose that.");
                    RequireUnusedViewName(doc, a.Value<string>("name"));
                    break;
                }

                case "set_template_controls":
                {
                    View template = Need<View>(doc, a, "view_id");
                    if (!template.IsTemplate) throw new ArgumentException("view_id must be a view template.");
                    if (a["controlled"] == null || a["controlled"].Type != JTokenType.Boolean)
                        throw new ArgumentException("controlled is required: true makes the template govern the parameters, false releases them.");
                    TemplateParameters(template, a);
                    break;
                }
            }
        }

        // =====================================================================
        // APPLY
        // =====================================================================

        internal static Element ApplyControl(Document doc, JObject a, string op, Dictionary<string, ElementId> aliases)
        {
            switch (op)
            {
                case "edit_filter":
                {
                    ParameterFilterElement filter = doc.GetElement(FilterIdApply(doc, a, aliases)) as ParameterFilterElement;
                    if (filter == null) throw new ArgumentException("the filter did not resolve to a rule-based filter");
                    ICollection<ElementId> categories = a["categories"] != null ? ReadCategories(doc, a) : filter.GetCategories();
                    ElementFilter rules = ReadRules(doc, a, categories);
                    if (a["categories"] != null)
                    {
                        // Rules first cleared: the OLD rules may not apply to the NEW
                        // categories, and SetCategories refuses a combination that is
                        // invalid at the moment it is called.
                        filter.ClearRules();
                        filter.SetCategories(categories);
                    }
                    filter.SetElementFilter(rules);
                    a["__rules"] = FilterSignature(rules);
                    a["__categories"] = new JArray(categories.Select(Rid.Value).OrderBy(x => x));
                    a["__filter_id"] = Rid.Value(filter.Id);
                    return filter;
                }

                case "order_filters":
                {
                    View view = GraphicsViewApply(doc, a, aliases);
                    List<ElementId> wanted = ResolveFilterOrder(view, a);
                    a["__wanted"] = new JArray(wanted.Select(Rid.Value));
                    var state = new JObject();
                    var saved = new Dictionary<long, OverrideGraphicSettings>();
                    foreach (ElementId id in view.GetOrderedFilters())
                    {
                        saved[Rid.Value(id)] = view.GetFilterOverrides(id);
                        state[Rid.Value(id).ToString(CultureInfo.InvariantCulture)] = FilterState(view, id);
                    }
                    foreach (ElementId id in view.GetOrderedFilters().ToList()) view.RemoveFilter(id);
                    foreach (ElementId id in wanted)
                    {
                        JObject s = (JObject)state[Rid.Value(id).ToString(CultureInfo.InvariantCulture)];
                        view.AddFilter(id);
                        view.SetFilterOverrides(id, saved[Rid.Value(id)]);
                        view.SetFilterVisibility(id, s.Value<bool>("visible"));
                        view.SetIsFilterEnabled(id, s.Value<bool>("enabled"));
                    }
                    // The restoration is re-read HERE, at the moment of the reorder, and not
                    // at the end of the batch. MEASURED (Revit 2023, 2026-09-24): a batch of
                    // order_filters followed by apply_filter(enabled=false) on one of the
                    // same filters failed order_filters' verification - the end-of-batch
                    // re-read compared the LATER action's legitimate change against the
                    // state saved before the reorder. What order_filters owns is the order
                    // and the restoration, so the restoration is proved before anything
                    // else in the batch can touch it.
                    var notRestored = new List<string>();
                    foreach (ElementId id in wanted)
                    {
                        string k = Rid.Value(id).ToString(CultureInfo.InvariantCulture);
                        JObject now = FilterState(view, id);
                        if (!JToken.DeepEquals(now, state[k]))
                            notRestored.Add(k + ": saved " + state[k].ToString(Formatting.None) + ", re-read " + now.ToString(Formatting.None));
                    }
                    if (notRestored.Count > 0)
                        throw new InvalidOperationException("order_filters removed and re-added the filters, but the re-read state " +
                            "differs from the saved one for " + string.Join("; ", notRestored));
                    a["__state"] = state;
                    a["__restored"] = true;
                    return view;
                }

                case "explain_graphics":
                    return GraphicsViewApply(doc, a, aliases);   // reads only; the report is taken after commit

                case "create_template":
                {
                    View template = Need<View>(doc, a, "view_id").CreateViewTemplate();
                    template.Name = a.Value<string>("name");
                    return template;
                }

                case "set_template_controls":
                {
                    View template = Need<View>(doc, a, "view_id");
                    List<ElementId> ids = TemplateParameters(template, a);
                    bool controlled = a.Value<bool>("controlled");
                    var free = new HashSet<ElementId>(template.GetNonControlledTemplateParameterIds());
                    foreach (ElementId id in ids) { if (controlled) free.Remove(id); else free.Add(id); }
                    template.SetNonControlledTemplateParameterIds(free);
                    a["__parameter_ids"] = new JArray(ids.Select(Rid.Value));
                    return template;
                }
            }
            throw new ArgumentException("unsupported control operation '" + op + "'");
        }

        // =====================================================================
        // VERIFY - re-read from the model.
        // =====================================================================

        internal static bool VerifyControl(Document doc, JObject a, string op, Element e)
        {
            switch (op)
            {
                case "edit_filter":
                {
                    if (!(e is ParameterFilterElement filter)) return false;
                    string reread = FilterSignature(filter.GetElementFilter());
                    var categories = new JArray(filter.GetCategories().Select(Rid.Value).OrderBy(x => x));
                    a["__rules_reread"] = reread;
                    return reread == a.Value<string>("__rules") && JToken.DeepEquals(categories, a["__categories"]);
                }

                case "order_filters":
                {
                    if (!(e is View view)) return false;
                    // The restoration was re-read at apply time (see ApplyControl). Here, after
                    // the whole batch, only what later actions cannot legitimately change is
                    // checked: the reordered filters still stand in the requested relative
                    // order (a later apply_filter may append a new filter or change a state).
                    List<long> wanted = (a["__wanted"] as JArray)?.Select(t => t.Value<long>()).ToList() ?? new List<long>();
                    List<long> order = view.GetOrderedFilters().Select(Rid.Value).ToList();
                    return a.Value<bool?>("__restored") == true && FilterOrderRules.KeepsRelativeOrder(order, wanted);
                }

                case "explain_graphics":
                {
                    if (!(e is View view)) return false;
                    a["__report"] = ExplainGraphics(doc, view, ReadElementIds(doc, a, "element_ids"));
                    return true;
                }

                case "create_template":
                    return e is View created && created.IsTemplate &&
                           string.Equals(created.Name, a.Value<string>("name"), StringComparison.Ordinal);

                case "set_template_controls":
                {
                    if (!(e is View template) || !(a["__parameter_ids"] is JArray ids) || ids.Count == 0) return false;
                    var free = new HashSet<long>(template.GetNonControlledTemplateParameterIds().Select(Rid.Value));
                    bool controlled = a.Value<bool>("controlled");
                    return ids.All(t => free.Contains(t.Value<long>()) != controlled);
                }
            }
            return false;
        }

        internal static JObject ControlDetail(JObject a, string op)
        {
            switch (op)
            {
                case "edit_filter": return new JObject { ["rules_reread"] = a["__rules_reread"], ["categories"] = a["__categories"] };
                case "order_filters": return new JObject { ["order"] = a["__wanted"], ["moved"] = a["move_filter_ids"], ["restored_state"] = a["__state"] };
                case "explain_graphics": return new JObject { ["report"] = a["__report"] };
                case "set_template_controls": return new JObject { ["parameter_ids"] = a["__parameter_ids"], ["controlled"] = a["controlled"] };
            }
            return null;
        }

        // =====================================================================
        // THE PRECEDENCE REPORT
        // =====================================================================

        /// <summary>What decides how each element looks in the view, layer by layer.</summary>
        internal static JArray ExplainGraphics(Document doc, View view, IList<ElementId> ids)
        {
            View template = view.ViewTemplateId == ElementId.InvalidElementId ? null : doc.GetElement(view.ViewTemplateId) as View;
            bool templateFilters = TemplateGoverns(template, BuiltInParameter.VIS_GRAPHICS_FILTERS);
            bool templateModel = TemplateGoverns(template, BuiltInParameter.VIS_GRAPHICS_MODEL);
            bool templateAnnotation = TemplateGoverns(template, BuiltInParameter.VIS_GRAPHICS_ANNOTATION);
            View filterSource = templateFilters ? template : view;

            var reports = new JArray();
            foreach (ElementId id in ids)
            {
                Element element = doc.GetElement(id);
                if (element == null) continue;
                var layers = new List<GraphicsLayer>();
                var rows = new JArray();

                bool hidden = false;
                try { hidden = element.IsHidden(view); } catch { }
                var elementLayer = new GraphicsLayer
                {
                    Source = "element", Label = "element",
                    Visible = hidden ? false : (bool?)null,
                    Fields = OverrideFields(doc, view.GetElementOverrides(id))
                };
                layers.Add(elementLayer);
                rows.Add(new JObject { ["source"] = "element", ["hidden"] = hidden, ["overrides"] = JObject.FromObject(elementLayer.Fields) });

                int order = 0;
                foreach (ElementId filterId in filterSource.GetOrderedFilters())
                {
                    order++;
                    FilterElement filter = doc.GetElement(filterId) as FilterElement;
                    bool matches = Passes(doc, filter, element);
                    bool enabled = true;
                    try { enabled = filterSource.GetIsFilterEnabled(filterId); } catch { }
                    bool visible = filterSource.GetFilterVisibility(filterId);
                    var layer = new GraphicsLayer
                    {
                        Source = "filter", Label = "filter:" + Rid.Value(filterId),
                        Applies = enabled && matches, Visible = visible ? (bool?)null : false,
                        Fields = OverrideFields(doc, filterSource.GetFilterOverrides(filterId))
                    };
                    layers.Add(layer);
                    rows.Add(new JObject
                    {
                        ["source"] = "filter", ["filter_id"] = Rid.Value(filterId), ["name"] = filter?.Name, ["order"] = order,
                        ["enabled"] = enabled, ["matches"] = matches, ["visible"] = visible,
                        ["from_template"] = templateFilters, ["overrides"] = JObject.FromObject(layer.Fields)
                    });
                }

                Category category = null;
                try { category = element.Category; } catch { }
                if (category != null)
                {
                    bool annotation = category.CategoryType == CategoryType.Annotation;
                    bool fromTemplate = annotation ? templateAnnotation : templateModel;
                    View categorySource = fromTemplate ? template : view;
                    bool categoryHidden = false;
                    try { categoryHidden = categorySource.GetCategoryHidden(category.Id); } catch { }
                    var layer = new GraphicsLayer
                    {
                        Source = "category", Label = "category:" + Rid.Value(category.Id),
                        Visible = categoryHidden ? false : (bool?)null,
                        Fields = OverrideFields(doc, categorySource.GetCategoryOverrides(category.Id))
                    };
                    layers.Add(layer);
                    rows.Add(new JObject
                    {
                        ["source"] = "category", ["category_id"] = Rid.Value(category.Id), ["name"] = category.Name,
                        ["hidden"] = categoryHidden, ["from_template"] = fromTemplate, ["overrides"] = JObject.FromObject(layer.Fields)
                    });
                }

                reports.Add(new JObject
                {
                    ["element_id"] = Rid.Value(id),
                    ["view_id"] = Rid.Value(view.Id),
                    ["template"] = template == null ? (JToken)JValue.CreateNull() : new JObject
                    {
                        ["id"] = Rid.Value(template.Id), ["name"] = template.Name,
                        ["governs_filters"] = templateFilters, ["governs_model_vg"] = templateModel,
                        ["governs_annotation_vg"] = templateAnnotation
                    },
                    ["layers"] = rows,
                    ["winners"] = GraphicsPrecedence.Resolve(layers),
                    ["means"] = "element beats filters, a filter higher in the list beats a lower one, filters beat the " +
                                "category, and object styles fill the rest. Any layer that hides wins. A template that " +
                                "governs V/G supplies the filter or category rows (from_template)."
                });
            }
            return reports;
        }

        private static bool Passes(Document doc, FilterElement filter, Element element)
        {
            try
            {
                if (filter is SelectionFilterElement selection) return selection.Contains(element.Id);
                if (!(filter is ParameterFilterElement parameterFilter)) return false;
                Category category = element.Category;
                if (category == null || !parameterFilter.GetCategories().Contains(category.Id)) return false;
                ElementFilter rules = parameterFilter.GetElementFilter();
                return rules == null || rules.PassesFilter(element);
            }
            catch { return false; }
        }

        /// <summary>Only the fields an override object SETS, as text.</summary>
        private static IDictionary<string, string> OverrideFields(Document doc, OverrideGraphicSettings o)
        {
            var fields = new Dictionary<string, string>();
            if (o == null) return fields;
            Action<string, Color> colour = (name, c) =>
            {
                try { if (c != null && c.IsValid) fields[name] = string.Format("#{0:X2}{1:X2}{2:X2}", c.Red, c.Green, c.Blue); } catch { }
            };
            colour("line_color", o.ProjectionLineColor);
            colour("cut_line_color", o.CutLineColor);
            colour("surface_color", o.SurfaceForegroundPatternColor);
            colour("cut_color", o.CutForegroundPatternColor);
            if (o.ProjectionLineWeight != OverrideGraphicSettings.InvalidPenNumber)
                fields["line_weight"] = o.ProjectionLineWeight.ToString(CultureInfo.InvariantCulture);
            if (o.ProjectionLinePatternId != ElementId.InvalidElementId)
                fields["line_pattern"] = SafeName(doc, o.ProjectionLinePatternId);
            if (o.SurfaceForegroundPatternId != ElementId.InvalidElementId)
                fields["surface_pattern"] = SafeName(doc, o.SurfaceForegroundPatternId);
            if (o.Transparency != 0) fields["transparency"] = o.Transparency.ToString(CultureInfo.InvariantCulture);
            if (o.Halftone) fields["halftone"] = "true";
            return fields;
        }

        private static string SafeName(Document doc, ElementId id)
        {
            try { return doc.GetElement(id)?.Name ?? Rid.Value(id).ToString(CultureInfo.InvariantCulture); }
            catch { return Rid.Value(id).ToString(CultureInfo.InvariantCulture); }
        }

        /// <summary>Does this template govern the named V/G parameter? False without a template.</summary>
        internal static bool TemplateGoverns(View template, BuiltInParameter parameter)
        {
            if (template == null) return false;
            try
            {
                var id = new ElementId(parameter);
                return template.GetTemplateParameterIds().Contains(id) &&
                       !template.GetNonControlledTemplateParameterIds().Contains(id);
            }
            catch { return false; }
        }

        // =====================================================================
        // helpers
        // =====================================================================

        private static JObject FilterState(View view, ElementId id)
        {
            bool enabled = true;
            try { enabled = view.GetIsFilterEnabled(id); } catch { }
            return new JObject
            {
                ["visible"] = view.GetFilterVisibility(id),
                ["enabled"] = enabled,
                ["overrides"] = JObject.FromObject(OverrideFields(view.Document, view.GetFilterOverrides(id)))
            };
        }

        /// <summary>
        /// The whole new order: filter_ids as given, or - with move_filter_ids - the view's current
        /// order with only those filters rearranged among the slots they already occupy. A duplicated
        /// view carries its source's filters (MEASURED 2026), so the caller often knows only its own.
        /// </summary>
        private static List<ElementId> ResolveFilterOrder(View view, JObject a)
        {
            if (a["move_filter_ids"] == null) return ReadFilterOrder(a);
            List<long> moved = ReadIdList(a, "move_filter_ids");
            List<long> current = view.GetOrderedFilters().Select(Rid.Value).ToList();
            List<long> order = FilterOrderRules.Reorder(current, moved);
            if (order == null)
                throw new ArgumentException("move_filter_ids must name filters that are on view '" + view.Name + "' (" +
                                            string.Join(", ", current) + "), each once.");
            return order.Select(Rid.Make).ToList();
        }

        private static List<long> ReadIdList(JObject a, string field)
        {
            JArray raw = a[field] as JArray;
            if (raw == null || raw.Count == 0) throw new ArgumentException(field + " must be a non-empty array of filter ids.");
            var ids = new List<long>();
            foreach (JToken t in raw)
            {
                long v = t.Value<long?>() ?? -1;
                if (!Rid.CanRepresent(v) || ids.Contains(v)) throw new ArgumentException(field + " holds an invalid or repeated id: " + t);
                ids.Add(v);
            }
            return ids;
        }

        private static List<ElementId> ReadFilterOrder(JObject a)
        {
            JArray raw = a["filter_ids"] as JArray;
            if (raw == null || raw.Count == 0) throw new ArgumentException("filter_ids (the view's filters in the new order) or move_filter_ids is required.");
            var ids = new List<ElementId>();
            var seen = new HashSet<long>();
            foreach (JToken t in raw)
            {
                long v = t.Value<long?>() ?? -1;
                if (!Rid.CanRepresent(v) || !seen.Add(v)) throw new ArgumentException("filter_ids holds an invalid or repeated id: " + t);
                ids.Add(Rid.Make(v));
            }
            return ids;
        }

        /// <summary>Resolve `parameters` against what this template CAN govern, or throw naming what it can.</summary>
        private static List<ElementId> TemplateParameters(View template, JObject a)
        {
            JArray raw = a["parameters"] as JArray;
            if (raw == null || raw.Count == 0) throw new ArgumentException("parameters is required: the template parameters to govern or release.");
            var available = template.GetTemplateParameterIds().ToList();
            var ids = new List<ElementId>();
            foreach (JToken t in raw)
            {
                string name = t.Value<string>() ?? "";
                ElementId match = null;
                if (Enum.TryParse(name, true, out BuiltInParameter bip) && Enum.IsDefined(typeof(BuiltInParameter), bip))
                    match = available.FirstOrDefault(id => Rid.Value(id) == (long)bip);
                if (match == null)
                    match = available.FirstOrDefault(id => string.Equals(ParameterLabel(template.Document, id), name, StringComparison.OrdinalIgnoreCase));
                if (match == null)
                    throw new ArgumentException(
                        "'" + name + "' is not a parameter template '" + template.Name + "' can govern. It can govern: " +
                        string.Join(", ", available.Select(id => ParameterLabel(template.Document, id)).Where(s => s != null).Take(60)));
                if (!ids.Contains(match)) ids.Add(match);
            }
            return ids;
        }

        /// <summary>
        /// A stable text form of a filter's rules, read the same way from the object we
        /// built and from the one Revit hands back after the commit - the comparison is
        /// the verification.
        /// </summary>
        internal static string FilterSignature(ElementFilter f)
        {
            if (f == null) return "none";
            string not = f.Inverted ? "NOT " : "";
            if (f is LogicalAndFilter and) return not + "AND(" + string.Join(",", and.GetFilters().Select(FilterSignature)) + ")";
            if (f is LogicalOrFilter or) return not + "OR(" + string.Join(",", or.GetFilters().Select(FilterSignature)) + ")";
            if (f is ElementParameterFilter p) return not + "P(" + string.Join(",", p.GetRules().Select(RuleSignature)) + ")";
            return not + f.GetType().Name;
        }

        private static string RuleSignature(FilterRule r)
        {
            if (r is FilterInverseRule inverse) return "!" + RuleSignature(inverse.GetInnerRule());
            string head = r.GetType().Name;
            try { head += "[" + Rid.Value(r.GetRuleParameter()) + "]"; } catch { }
            CultureInfo inv = CultureInfo.InvariantCulture;
            switch (r)
            {
                case FilterStringRule s: return head + s.GetEvaluator().GetType().Name + "'" + s.RuleString + "'";
                case FilterDoubleRule d: return head + d.GetEvaluator().GetType().Name + d.RuleValue.ToString("R", inv) + "~" + d.Epsilon.ToString("R", inv);
                case FilterIntegerRule i: return head + i.GetEvaluator().GetType().Name + i.RuleValue.ToString(inv);
                case FilterElementIdRule e: return head + e.GetEvaluator().GetType().Name + Rid.Value(e.RuleValue).ToString(inv);
            }
            return head;
        }
    }
}
