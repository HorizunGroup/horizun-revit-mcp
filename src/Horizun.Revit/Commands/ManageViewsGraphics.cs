// -----------------------------------------------------------------------------
// Horizun Revit MCP - graphic control: view filters, overrides, temporary
// hide/isolate, and colour by parameter value. Original Horizun code.
//
// G07 of the 2026-09-14 competitive inventory. The bridge could apply a view
// TEMPLATE and navigate, and it used OverrideGraphicSettings inside its own audit
// routines - but there was no typed way for a caller to say "colour every wall by
// its fire rating and show me", which is most of what graphic control is for.
//
// THREE THINGS THIS FILE REFUSES TO DO, and each of them is a measured trap:
//
//   A VIEW GOVERNED BY A TEMPLATE SILENTLY WINS. Revit does not throw when you set
//   filters or V/G on a view whose template controls them; it accepts the call and
//   keeps the template's value. Asking View.AreGraphicsOverridesAllowed() first
//   turns that into a refusal with the template named, instead of a command that
//   reports success over a view that did not change.
//
//   A DEPENDENT VIEW CANNOT HIDE A CATEGORY AT ALL. CanCategoryBeHidden is false
//   there and false again on a view whose template governs V/G - measured, both -
//   so the refusal says which of the two it is rather than "Revit said no".
//
//   TEMPORARY HIDE/ISOLATE IS A VIEW MODE, NOT A PROPERTY. It does not survive a
//   close, it is not what a printed sheet shows, and callers routinely expect it
//   to be permanent. Every reply says which it applied and how to undo it.
//
// THE PALETTE IS DETERMINISTIC ON PURPOSE. Colour by value is useless as evidence
// if the same model produces different colours on two runs: the distinct values
// are sorted and mapped to a fixed table, so the legend of one run is the legend
// of the next, and two people looking at two screenshots are looking at the same
// thing.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public sealed partial class ManageViewsCommand
    {
        /// <summary>The graphic-control operations this command answers.</summary>
        internal static readonly string[] GraphicsOperations =
        {
            "create_filter", "apply_filter", "color_by_value",
            "set_element_overrides", "hide_elements", "isolate_elements",
            "reset_temporary", "set_category_visibility"
        };

        internal static bool IsGraphicsOperation(string op)
            => Array.IndexOf(GraphicsOperations, op ?? "") >= 0;

        // ---------------------------------------------------------------------
        // A FIXED, ORDERED PALETTE.
        //
        // Twelve colours that stay apart from one another at a glance and survive
        // being printed in greyscale badly. A thirteenth distinct value wraps - and
        // the reply SAYS it wrapped, because two categories sharing a colour is a
        // legend that lies unless somebody is told.
        // ---------------------------------------------------------------------
        private static readonly byte[][] Palette =
        {
            new byte[] { 0xE6, 0x19, 0x4B }, new byte[] { 0x3C, 0xB4, 0x4B },
            new byte[] { 0x43, 0x63, 0xD8 }, new byte[] { 0xF5, 0x82, 0x31 },
            new byte[] { 0x91, 0x1E, 0xB4 }, new byte[] { 0x46, 0xF0, 0xF0 },
            new byte[] { 0xF0, 0x32, 0xE6 }, new byte[] { 0xBF, 0xEF, 0x45 },
            new byte[] { 0xFA, 0xBE, 0xD4 }, new byte[] { 0x46, 0x99, 0x90 },
            new byte[] { 0x9A, 0x63, 0x24 }, new byte[] { 0x80, 0x00, 0x00 }
        };

        // =====================================================================
        // VALIDATION - everything knowable before a transaction opens.
        // =====================================================================

        /// <summary>
        /// Validate one graphic-control action. Throws ArgumentException with the
        /// reason; the caller turns that into the batch's error entry.
        /// </summary>
        internal static void ValidateGraphics(Document doc, JObject a, string op,
                                              Dictionary<string, Type> known)
        {
            switch (op)
            {
                case "create_filter":
                {
                    string name = a.Value<string>("name");
                    if (string.IsNullOrWhiteSpace(name))
                        throw new ArgumentException(
                            "name is required: a filter without a name cannot be referred to again, and Revit " +
                            "would invent one nobody chose.");
                    if (ExistingFilter(doc, name) != null)
                        throw new ArgumentException(
                            "a view filter named '" + name + "' already exists (id " +
                            Rid.Value(ExistingFilter(doc, name).Id) + "). Filter names are unique in a document; " +
                            "use apply_filter to put the existing one on a view.");
                    ICollection<ElementId> categories = ReadCategories(doc, a);
                    ReadRules(doc, a, categories);   // throws with the exact bad rule
                    break;
                }

                case "apply_filter":
                {
                    View view = GraphicsView(doc, a, known);
                    RequireOverridesAllowed(view, "filters");
                    RequireFilterReference(doc, a, known);
                    if (a["overrides"] != null) ReadOverrides(doc, a["overrides"] as JObject);
                    if (a["visible"] != null && a["visible"].Type != JTokenType.Boolean)
                        throw new ArgumentException("visible must be a boolean when given");
                    if (a["enabled"] != null && a["enabled"].Type != JTokenType.Boolean)
                        throw new ArgumentException("enabled must be a boolean when given");
                    break;
                }

                case "color_by_value":
                {
                    View view = GraphicsView(doc, a, known);
                    RequireOverridesAllowed(view, "filters");
                    ICollection<ElementId> categories = ReadCategories(doc, a);
                    ElementId parameter = ResolveFilterableParameter(doc, a, categories);
                    if (parameter == null || parameter == ElementId.InvalidElementId)
                        throw new ArgumentException("parameter could not be resolved to a filterable parameter");
                    int max = a.Value<int?>("max_values") ?? 24;
                    if (max < 1 || max > 60)
                        throw new ArgumentException(
                            "max_values must be 1..60. Beyond that the legend stops being readable and the " +
                            "palette repeats so often that two colours mean nothing.");
                    break;
                }

                case "set_element_overrides":
                {
                    View view = GraphicsView(doc, a, known);
                    RequireOverridesAllowed(view, "element overrides");
                    ReadElementIds(doc, a, "element_ids");
                    if (a["overrides"] == null)
                        throw new ArgumentException(
                            "overrides is required: an override action with nothing in it would report success " +
                            "over a view that did not change.");
                    ReadOverrides(doc, a["overrides"] as JObject);
                    break;
                }

                case "hide_elements":
                case "isolate_elements":
                {
                    View view = GraphicsView(doc, a, known);
                    IList<ElementId> ids = ReadElementIds(doc, a, "element_ids");
                    if (ids.Count == 0)
                        throw new ArgumentException("element_ids must contain at least one element");
                    bool permanent = a.Value<bool?>("permanent") ?? false;
                    if (permanent && op == "isolate_elements")
                        throw new ArgumentException(
                            "isolate has no permanent form in Revit: permanently 'isolating' means hiding " +
                            "everything else, which is a different and far larger write. Use hide_elements with " +
                            "the complement, or leave permanent false for the temporary view mode.");
                    if (permanent)
                        foreach (ElementId id in ids)
                            if (!CanBeHiddenPermanently(doc, view, id))
                                throw new ArgumentException(
                                    "element " + Rid.Value(id) + " cannot be hidden in this view " +
                                    "(Element.CanBeHidden is false). Nothing was hidden.");
                    break;
                }

                case "reset_temporary":
                    GraphicsView(doc, a, known);
                    break;

                case "set_category_visibility":
                {
                    View view = GraphicsView(doc, a, known);
                    ElementId category = ResolveVisibilityCategory(doc, a);
                    bool hasHidden = a["hidden"] != null && a["hidden"].Type == JTokenType.Boolean;
                    if (!hasHidden && a["overrides"] == null)
                        throw new ArgumentException("set_category_visibility needs hidden (a boolean), overrides, or both - an action with neither changes nothing.");
                    if (a["hidden"] != null && !hasHidden) throw new ArgumentException("hidden must be a boolean");
                    RequireCategoryVgNotTemplated(doc, view, category);
                    if (hasHidden) RequireCategoryHideable(doc, view, category, a.Value<string>("category"));
                    if (a["overrides"] != null)
                    {
                        if (view != null && !view.IsCategoryOverridable(category))
                            throw new ArgumentException("category '" + a.Value<string>("category") + "' cannot take graphic overrides in view '" +
                                view.Name + "' (View.IsCategoryOverridable is false). Nothing was written.");
                        ReadOverrides(doc, a["overrides"] as JObject);
                    }
                    break;
                }
            }
        }

        // =====================================================================
        // APPLY - inside the transaction. Returns the element the batch reports.
        // =====================================================================

        internal static Element ApplyGraphics(Document doc, JObject a, string op,
                                              Dictionary<string, ElementId> aliases)
        {
            switch (op)
            {
                case "create_filter":
                {
                    ICollection<ElementId> categories = ReadCategories(doc, a);
                    ElementFilter rules = ReadRules(doc, a, categories);
                    ParameterFilterElement filter =
                        ParameterFilterElement.Create(doc, a.Value<string>("name"), categories, rules);
                    a["__filter_id"] = Rid.Value(filter.Id);
                    return filter;
                }

                case "apply_filter":
                {
                    View view = GraphicsViewApply(doc, a, aliases);
                    ElementId filterId = FilterIdApply(doc, a, aliases);
                    if (!view.GetFilters().Contains(filterId)) view.AddFilter(filterId);
                    JObject overrides = a["overrides"] as JObject;
                    if (overrides != null) view.SetFilterOverrides(filterId, ReadOverrides(doc, overrides));
                    if (a["visible"] != null) view.SetFilterVisibility(filterId, a.Value<bool>("visible"));
                    if (a["enabled"] != null) view.SetIsFilterEnabled(filterId, a.Value<bool>("enabled"));
                    a["__filter_id"] = Rid.Value(filterId);
                    return view;
                }

                case "color_by_value":
                    return ApplyColorByValue(doc, a, aliases);

                case "set_element_overrides":
                {
                    View view = GraphicsViewApply(doc, a, aliases);
                    OverrideGraphicSettings settings = ReadOverrides(doc, a["overrides"] as JObject);
                    foreach (ElementId id in ReadElementIds(doc, a, "element_ids"))
                        view.SetElementOverrides(id, settings);
                    return view;
                }

                case "hide_elements":
                {
                    View view = GraphicsViewApply(doc, a, aliases);
                    IList<ElementId> ids = ReadElementIds(doc, a, "element_ids");
                    if (a.Value<bool?>("permanent") ?? false) view.HideElements(ids);
                    else view.HideElementsTemporary(ids);
                    return view;
                }

                case "isolate_elements":
                {
                    View view = GraphicsViewApply(doc, a, aliases);
                    view.IsolateElementsTemporary(ReadElementIds(doc, a, "element_ids"));
                    return view;
                }

                case "reset_temporary":
                {
                    View view = GraphicsViewApply(doc, a, aliases);
                    view.DisableTemporaryViewMode(TemporaryViewMode.TemporaryHideIsolate);
                    return view;
                }

                case "set_category_visibility":
                {
                    View view = GraphicsViewApply(doc, a, aliases);
                    ElementId category = ResolveVisibilityCategory(doc, a);
                    if (a["hidden"] != null) view.SetCategoryHidden(category, a.Value<bool>("hidden"));
                    if (a["overrides"] != null) view.SetCategoryOverrides(category, ReadOverrides(doc, a["overrides"] as JObject));
                    a["__category_id"] = Rid.Value(category);
                    return view;
                }
            }
            throw new ArgumentException("unsupported graphics operation '" + op + "'");
        }

        private static Element ApplyColorByValue(Document doc, JObject a, Dictionary<string, ElementId> aliases)
        {
            View view = GraphicsViewApply(doc, a, aliases);
            ICollection<ElementId> categories = ReadCategories(doc, a);
            ElementId parameter = ResolveFilterableParameter(doc, a, categories);
            int max = a.Value<int?>("max_values") ?? 24;
            string prefix = a.Value<string>("filter_prefix") ?? ("HZ colour - " + a.Value<string>("parameter"));

            // THE VALUES COME FROM THE MODEL, NOT FROM THE CALLER. A palette built
            // from a list somebody typed colours the values they remembered and
            // leaves the rest grey, which is exactly the case a reviewer needs to see.
            List<string> values = DistinctValues(doc, view, categories, parameter);
            values.Sort(StringComparer.Ordinal);   // deterministic: same model, same colours

            var legend = new JArray();
            int index = 0;
            bool wrapped = false;
            foreach (string value in values)
            {
                if (index >= max) break;
                byte[] rgb = Palette[index % Palette.Length];
                if (index >= Palette.Length) wrapped = true;

                string name = prefix + " = " + (string.IsNullOrEmpty(value) ? "(empty)" : value);
                ParameterFilterElement filter = ExistingFilter(doc, name);
                if (filter == null)
                {
                    FilterRule rule = string.IsNullOrEmpty(value)
                        ? ParameterFilterRuleFactory.CreateHasNoValueParameterRule(parameter)
                        : ParameterFilterRuleFactory.CreateEqualsRule(parameter, value);
                    filter = ParameterFilterElement.Create(
                        doc, name, categories, new ElementParameterFilter(rule));
                }

                var colour = new Color(rgb[0], rgb[1], rgb[2]);
                var settings = new OverrideGraphicSettings();
                settings.SetProjectionLineColor(colour);
                settings.SetCutLineColor(colour);
                ElementId solid = SolidFillPattern(doc);
                if (solid != ElementId.InvalidElementId)
                {
                    settings.SetSurfaceForegroundPatternId(solid);
                    settings.SetSurfaceForegroundPatternColor(colour);
                    settings.SetCutForegroundPatternId(solid);
                    settings.SetCutForegroundPatternColor(colour);
                }

                if (!view.GetFilters().Contains(filter.Id)) view.AddFilter(filter.Id);
                view.SetFilterOverrides(filter.Id, settings);
                view.SetFilterVisibility(filter.Id, true);

                legend.Add(new JObject
                {
                    ["value"] = value,
                    ["filter_id"] = Rid.Value(filter.Id),
                    ["rgb"] = string.Format("#{0:X2}{1:X2}{2:X2}", rgb[0], rgb[1], rgb[2])
                });
                index++;
            }

            a["__legend"] = legend;
            a["__values_found"] = values.Count;
            a["__values_coloured"] = legend.Count;
            a["__palette_wrapped"] = wrapped;
            return view;
        }

        // =====================================================================
        // VERIFY - re-read from the model after the commit.
        // =====================================================================

        internal static bool VerifyGraphics(Document doc, JObject action, string op, Element e)
        {
            switch (op)
            {
                case "create_filter":
                    return e is ParameterFilterElement created &&
                           string.Equals(created.Name, action.Value<string>("name"), StringComparison.Ordinal);

                case "apply_filter":
                {
                    if (!(e is View view)) return false;
                    long raw = action.Value<long?>("__filter_id") ?? -1;
                    if (!Rid.CanRepresent(raw)) return false;
                    ElementId filterId = Rid.Make(raw);
                    if (!view.GetFilters().Contains(filterId)) return false;
                    if (action["visible"] != null &&
                        view.GetFilterVisibility(filterId) != action.Value<bool>("visible")) return false;
                    if (action["enabled"] != null &&
                        view.GetIsFilterEnabled(filterId) != action.Value<bool>("enabled")) return false;
                    // The overrides are re-READ rather than assumed: a template that
                    // governs filters accepts the call and keeps its own value, and the
                    // only way to know which happened is to ask the view afterwards.
                    return action["overrides"] == null ||
                           OverridesMatch(view.GetFilterOverrides(filterId), action["overrides"] as JObject);
                }

                case "color_by_value":
                {
                    if (!(e is View coloured)) return false;
                    JArray legend = action["__legend"] as JArray;
                    // An EMPTY legend coloured nothing: the loop below would pass it vacuously.
                    if (legend == null || legend.Count == 0) return false;
                    foreach (JToken row in legend)
                    {
                        long raw = row.Value<long?>("filter_id") ?? -1;
                        if (!Rid.CanRepresent(raw)) return false;
                        ElementId id = Rid.Make(raw);
                        if (!coloured.GetFilters().Contains(id)) return false;
                        if (!coloured.GetFilterVisibility(id)) return false;
                    }
                    return true;
                }

                case "set_element_overrides":
                {
                    if (!(e is View target)) return false;
                    JObject wanted = action["overrides"] as JObject;
                    var overridden = ReadElementIds(doc, action, "element_ids").ToList();
                    if (overridden.Count == 0) return false;   // nothing compared is not a pass
                    foreach (ElementId id in overridden)
                        if (!OverridesMatch(target.GetElementOverrides(id), wanted)) return false;
                    return true;
                }

                case "hide_elements":
                {
                    if (!(e is View hiding)) return false;
                    bool permanent = action.Value<bool?>("permanent") ?? false;
                    var hidden = ReadElementIds(doc, action, "element_ids").ToList();
                    if (hidden.Count == 0) return false;   // nothing compared is not a pass
                    foreach (ElementId id in hidden)
                    {
                        Element element = doc.GetElement(id);
                        if (element == null) return false;
                        if (permanent && !element.IsHidden(hiding)) return false;
                    }
                    // The temporary mode is a view state, not an element property, so
                    // the check is that the mode is ON - IsHidden does not report it.
                    return permanent || hiding.IsInTemporaryViewMode(TemporaryViewMode.TemporaryHideIsolate);
                }

                case "isolate_elements":
                    return e is View isolating &&
                           isolating.IsInTemporaryViewMode(TemporaryViewMode.TemporaryHideIsolate);

                case "reset_temporary":
                    return e is View reset &&
                           !reset.IsInTemporaryViewMode(TemporaryViewMode.TemporaryHideIsolate);

                case "set_category_visibility":
                {
                    if (!(e is View categoryView)) return false;
                    long raw = action.Value<long?>("__category_id") ?? -1;
                    if (!Rid.CanRepresent(raw)) return false;
                    ElementId categoryId = Rid.Make(raw);
                    if (action["hidden"] != null && categoryView.GetCategoryHidden(categoryId) != action.Value<bool>("hidden")) return false;
                    return action["overrides"] == null ||
                           OverridesMatch(categoryView.GetCategoryOverrides(categoryId), action["overrides"] as JObject);
                }
            }
            return false;
        }

        /// <summary>What a graphic-control action reports back beyond its id.</summary>
        internal static JObject GraphicsDetail(JObject action, string op)
        {
            if (op == "color_by_value")
                return new JObject
                {
                    ["legend"] = action["__legend"],
                    ["values_found"] = action["__values_found"],
                    ["values_coloured"] = action["__values_coloured"],
                    ["palette_wrapped"] = action["__palette_wrapped"],
                    ["means"] = "values_found counts the distinct values present in this view; " +
                                "values_coloured is how many got a colour before max_values. When " +
                                "palette_wrapped is true, two different values share a colour and the " +
                                "legend is the only way to tell them apart."
                };
            if (op == "hide_elements" || op == "isolate_elements")
                return new JObject
                {
                    ["temporary"] = !(action.Value<bool?>("permanent") ?? false),
                    ["means"] = "a temporary hide/isolate is a VIEW MODE. It does not survive closing the " +
                                "document, it is not what a printed sheet shows, and reset_temporary undoes it. " +
                                "A permanent hide is stored on the view and is what prints."
                };
            return null;
        }

        // =====================================================================
        // Resolution helpers, shared by validation and application.
        // =====================================================================

        private static View GraphicsView(Document doc, JObject a, Dictionary<string, Type> known)
        {
            string key = a.Value<string>("view_key");
            if (!string.IsNullOrWhiteSpace(key))
            {
                if (!known.TryGetValue(key, out Type actual))
                    throw new ArgumentException("view_key references unknown/prior key '" + key + "'");
                if (!typeof(View).IsAssignableFrom(actual))
                    throw new ArgumentException("view_key '" + key + "' produces " + actual.Name + ", not a view");
                return null;   // created later in this batch; nothing to inspect yet
            }
            return Need<View>(doc, a, "view_id");
        }

        private static View GraphicsViewApply(Document doc, JObject a, Dictionary<string, ElementId> aliases)
        {
            View view = Resolve<View>(doc, a, "view_id", "view_key", aliases);
            if (view == null) throw new ArgumentException("view_id/view_key did not resolve to a view");
            return view;
        }

        /// <summary>
        /// A view whose TEMPLATE governs the thing being written is a silent no-op in
        /// Revit: the call is accepted and the template's value is kept. Refuse instead,
        /// and name the template, because "it succeeded and nothing changed" is the worst
        /// answer this command could give.
        /// </summary>
        private static void RequireOverridesAllowed(View view, string what)
        {
            if (view == null) return;   // created later in this batch
            if (view.AreGraphicsOverridesAllowed()) return;
            string template = "none";
            try
            {
                Element t = view.Document.GetElement(view.ViewTemplateId);
                if (t != null) template = t.Name + " (id " + Rid.Value(t.Id) + ")";
            }
            catch { }
            throw new ArgumentException(
                "view '" + view.Name + "' does not accept " + what + ": View.AreGraphicsOverridesAllowed() is " +
                "false. Its view template is " + template + ", and a template that governs V/G takes the write " +
                "silently - Revit would accept this call and keep the template's value. Nothing was written. " +
                "Duplicate the view without the template, or change the template itself" +
                (view.ViewTemplateId != ElementId.InvalidElementId ? " by sending the same action with view_id=" + Rid.Value(view.ViewTemplateId) : "") + ".");
        }

        private static void RequireCategoryHideable(Document doc, View view, ElementId category, string name)
        {
            if (view == null) return;
            if (view.CanCategoryBeHidden(category)) return;

            // MEASURED: the two reasons this is false are a DEPENDENT view and a view
            // whose template governs V/G. Saying which one it is saves the caller the
            // experiment.
            bool dependent = false;
            try { dependent = view.GetPrimaryViewId() != ElementId.InvalidElementId; } catch { }
            string why = dependent
                ? "it is a DEPENDENT view - visibility follows its primary view, and Revit offers no way to " +
                  "diverge from it per category"
                : (view.ViewTemplateId != ElementId.InvalidElementId
                    ? "its view template governs V/G, so the category's visibility is the template's to decide"
                    : "Revit reports CanCategoryBeHidden false for it and gives no further reason");
            throw new ArgumentException(
                "category '" + name + "' cannot be hidden in view '" + view.Name + "': " + why +
                ". Nothing was written.");
        }

        private static bool CanBeHiddenPermanently(Document doc, View view, ElementId id)
        {
            if (view == null) return true;
            Element e = doc.GetElement(id);
            if (e == null) throw new ArgumentException("element " + Rid.Value(id) + " does not exist");
            try { return e.CanBeHidden(view); } catch { return false; }
        }

        private static IList<ElementId> ReadElementIds(Document doc, JObject a, string field)
        {
            JArray raw = a[field] as JArray;
            if (raw == null) throw new ArgumentException(field + " must be an array of element ids");
            if (raw.Count > 5000)
                throw new ArgumentException(field + " holds " + raw.Count + " ids; the bound is 5000 per action.");
            var ids = new List<ElementId>(raw.Count);
            foreach (JToken token in raw)
            {
                long value = token.Value<long?>() ?? -1;
                if (!Rid.CanRepresent(value)) throw new ArgumentException(field + " holds a value that is not an element id");
                ElementId id = Rid.Make(value);
                if (doc.GetElement(id) == null)
                    throw new ArgumentException("element " + value + " does not exist in this document");
                ids.Add(id);
            }
            return ids;
        }

        private static ICollection<ElementId> ReadCategories(Document doc, JObject a)
        {
            JArray raw = a["categories"] as JArray;
            if (raw == null || raw.Count == 0)
                throw new ArgumentException(
                    "categories is required: a filter with no categories matches nothing, and Revit accepts it.");
            // WHICH CATEGORIES CAN CARRY A FILTER AT ALL. There is no
            // IsCategoryFilterable in the API - checked against the installed
            // RevitAPI.xml after the first version of this file called one - so the
            // question is answered by membership in GetAllFilterableCategories, which is
            // the set Revit itself offers in the filter dialog. Fetched once per call:
            // it is a few hundred ids and re-fetching it per category would walk that
            // list for every entry.
            ICollection<ElementId> filterable = ParameterFilterUtilities.GetAllFilterableCategories();
            var filterableValues = new HashSet<long>(filterable.Select(Rid.Value));

            var ids = new List<ElementId>();
            foreach (JToken token in raw)
            {
                string requested = token.Value<string>();
                ElementId id = ResolveCategory(doc, requested);
                if (!filterableValues.Contains(Rid.Value(id)))
                    throw new ArgumentException(
                        "category '" + requested + "' cannot carry a view filter: it is not in " +
                        "ParameterFilterUtilities.GetAllFilterableCategories(), which is the set Revit itself " +
                        "offers in the filter dialog. A filter created over it would match nothing.");
                ids.Add(id);
            }
            return ids;
        }

        private static ElementId ResolveCategory(Document doc, string name)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("a category name is required");

            // A BuiltInCategory name first - stable across languages, which a display
            // name is not. A Spanish Revit calls walls "Muros", and a rule written
            // against the display name would work on one machine and not the next.
            BuiltInCategory builtIn;
            if (Enum.TryParse(name, true, out builtIn) && Enum.IsDefined(typeof(BuiltInCategory), builtIn))
            {
                Category c = Category.GetCategory(doc, builtIn);
                if (c != null) return c.Id;
            }
            foreach (Category c in doc.Settings.Categories)
                if (string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)) return c.Id;

            throw new ArgumentException(
                "category '" + name + "' was not found. Prefer the BuiltInCategory name (OST_Walls), which is " +
                "the same on every machine; a display name depends on the Revit language.");
        }

        private static ParameterFilterElement ExistingFilter(Document doc, string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            foreach (ParameterFilterElement f in new FilteredElementCollector(doc)
                         .OfClass(typeof(ParameterFilterElement)).Cast<ParameterFilterElement>())
            {
                string existing;
                try { existing = f.Name; } catch { continue; }
                if (string.Equals(existing, name, StringComparison.Ordinal)) return f;
            }
            return null;
        }

        private static void RequireFilterReference(Document doc, JObject a, Dictionary<string, Type> known)
        {
            string key = a.Value<string>("filter_key");
            if (!string.IsNullOrWhiteSpace(key))
            {
                if (!known.TryGetValue(key, out Type actual))
                    throw new ArgumentException("filter_key references unknown/prior key '" + key + "'");
                if (!typeof(ParameterFilterElement).IsAssignableFrom(actual))
                    throw new ArgumentException("filter_key '" + key + "' produces " + actual.Name + ", not a filter");
                return;
            }
            if (a["filter_id"] != null) { Need<ParameterFilterElement>(doc, a, "filter_id"); return; }
            string name = a.Value<string>("filter_name");
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("apply_filter needs filter_id, filter_key or filter_name");
            if (ExistingFilter(doc, name) == null)
                throw new ArgumentException("no view filter named '" + name + "' exists in this document");
        }

        private static ElementId FilterIdApply(Document doc, JObject a, Dictionary<string, ElementId> aliases)
        {
            string key = a.Value<string>("filter_key");
            if (!string.IsNullOrWhiteSpace(key) && aliases.TryGetValue(key, out ElementId fromKey)) return fromKey;
            if (a["filter_id"] != null) return Need<ParameterFilterElement>(doc, a, "filter_id").Id;
            ParameterFilterElement byName = ExistingFilter(doc, a.Value<string>("filter_name"));
            if (byName == null) throw new ArgumentException("the filter could not be resolved");
            return byName.Id;
        }

        // ---- rules ----------------------------------------------------------

        /// <summary>
        /// Build the filter's element filter from typed rules.
        ///
        /// The rule factory is typed by VALUE, and picking the wrong overload is a
        /// filter that silently matches nothing: a numeric parameter compared against
        /// the string "3" is a legal call and an empty view. So the declared type is
        /// required, not sniffed from the JSON.
        /// </summary>
        private static ElementFilter ReadRules(Document doc, JObject a, ICollection<ElementId> categories)
        {
            JArray raw = a["rules"] as JArray;
            if (raw == null || raw.Count == 0)
                throw new ArgumentException("rules is required and must hold at least one rule");
            if (raw.Count > 50) throw new ArgumentException("rules holds more than 50 entries");

            string join = (a.Value<string>("match") ?? "all").ToLowerInvariant();
            if (join != "all" && join != "any")
                throw new ArgumentException("match must be 'all' or 'any'");

            var filters = new List<ElementFilter>();
            foreach (JToken token in raw)
            {
                JObject rule = token as JObject;
                if (rule == null) throw new ArgumentException("each rule must be an object");
                filters.Add(new ElementParameterFilter(ReadRule(doc, rule, categories)));
            }
            if (filters.Count == 1) return filters[0];
            return join == "all" ? (ElementFilter)new LogicalAndFilter(filters) : new LogicalOrFilter(filters);
        }

        private static FilterRule ReadRule(Document doc, JObject rule, ICollection<ElementId> categories)
        {
            var carrier = new JObject { ["parameter"] = rule["parameter"] };
            ElementId parameter = ResolveFilterableParameter(doc, carrier, categories);
            string op = (rule.Value<string>("operator") ?? "equals").ToLowerInvariant();
            string type = (rule.Value<string>("value_type") ?? "string").ToLowerInvariant();

            if (op == "has_value") return ParameterFilterRuleFactory.CreateHasValueParameterRule(parameter);
            if (op == "has_no_value") return ParameterFilterRuleFactory.CreateHasNoValueParameterRule(parameter);

            JToken value = rule["value"];
            if (value == null)
                throw new ArgumentException("rule operator '" + op + "' requires a value");

            switch (type)
            {
                case "string":
                {
                    string text = value.Value<string>() ?? "";
                    switch (op)
                    {
                        case "equals": return ParameterFilterRuleFactory.CreateEqualsRule(parameter, text);
                        case "not_equals": return ParameterFilterRuleFactory.CreateNotEqualsRule(parameter, text);
                        case "contains": return ParameterFilterRuleFactory.CreateContainsRule(parameter, text);
                        case "not_contains": return ParameterFilterRuleFactory.CreateNotContainsRule(parameter, text);
                        case "begins_with": return ParameterFilterRuleFactory.CreateBeginsWithRule(parameter, text);
                        case "ends_with": return ParameterFilterRuleFactory.CreateEndsWithRule(parameter, text);
                        default: throw new ArgumentException("operator '" + op + "' is not valid for a string value");
                    }
                }
                case "number":
                {
                    double number = value.Value<double>();
                    // The tolerance is REQUIRED for a double comparison and defaulting it
                    // silently is how "equals 3.0" fails on a value Revit stores as
                    // 2.9999999. It defaults to a millimetre in internal feet.
                    double epsilon = rule.Value<double?>("tolerance") ?? (1.0 / 304.8);
                    switch (op)
                    {
                        case "equals": return ParameterFilterRuleFactory.CreateEqualsRule(parameter, number, epsilon);
                        case "not_equals": return ParameterFilterRuleFactory.CreateNotEqualsRule(parameter, number, epsilon);
                        case "greater": return ParameterFilterRuleFactory.CreateGreaterRule(parameter, number, epsilon);
                        case "greater_or_equal": return ParameterFilterRuleFactory.CreateGreaterOrEqualRule(parameter, number, epsilon);
                        case "less": return ParameterFilterRuleFactory.CreateLessRule(parameter, number, epsilon);
                        case "less_or_equal": return ParameterFilterRuleFactory.CreateLessOrEqualRule(parameter, number, epsilon);
                        default: throw new ArgumentException("operator '" + op + "' is not valid for a number value");
                    }
                }
                case "integer":
                {
                    int number = value.Value<int>();
                    switch (op)
                    {
                        case "equals": return ParameterFilterRuleFactory.CreateEqualsRule(parameter, number);
                        case "not_equals": return ParameterFilterRuleFactory.CreateNotEqualsRule(parameter, number);
                        case "greater": return ParameterFilterRuleFactory.CreateGreaterRule(parameter, number);
                        case "greater_or_equal": return ParameterFilterRuleFactory.CreateGreaterOrEqualRule(parameter, number);
                        case "less": return ParameterFilterRuleFactory.CreateLessRule(parameter, number);
                        case "less_or_equal": return ParameterFilterRuleFactory.CreateLessOrEqualRule(parameter, number);
                        default: throw new ArgumentException("operator '" + op + "' is not valid for an integer value");
                    }
                }
                default:
                    throw new ArgumentException("value_type must be string, number or integer");
            }
        }

        /// <summary>
        /// Resolve `parameter` against the parameters Revit will actually FILTER on for
        /// these categories - not against every parameter in the document.
        ///
        /// The difference matters: a parameter that exists on the element but is not
        /// filterable produces a rule Revit rejects at commit time, which is a rollback
        /// instead of an argument error.
        /// </summary>
        private static ElementId ResolveFilterableParameter(Document doc, JObject a, ICollection<ElementId> categories)
        {
            string name = a.Value<string>("parameter");
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("parameter is required");

            ICollection<ElementId> filterable =
                ParameterFilterUtilities.GetFilterableParametersInCommon(doc, categories);

            BuiltInParameter builtIn;
            if (Enum.TryParse(name, true, out builtIn) && Enum.IsDefined(typeof(BuiltInParameter), builtIn))
            {
                ElementId id = Rid.Make((long)builtIn);
                foreach (ElementId candidate in filterable)
                    if (candidate == id) return candidate;
            }

            var available = new List<string>();
            foreach (ElementId id in filterable)
            {
                string label = ParameterLabel(doc, id);
                if (label == null) continue;
                if (string.Equals(label, name, StringComparison.OrdinalIgnoreCase)) return id;
                if (available.Count < 40) available.Add(label);
            }

            available.Sort(StringComparer.OrdinalIgnoreCase);
            throw new ArgumentException(
                "parameter '" + name + "' is not filterable for these categories. Revit filters only on the " +
                "parameters they have in common; these are available: " + string.Join(", ", available) +
                (filterable.Count > available.Count ? " (and " + (filterable.Count - available.Count) + " more)" : ""));
        }

        private static string ParameterLabel(Document doc, ElementId id)
        {
            try
            {
                long raw = Rid.Value(id);
                if (raw < 0 && Enum.IsDefined(typeof(BuiltInParameter), (BuiltInParameter)raw))
                    return LabelUtils.GetLabelFor((BuiltInParameter)raw);
                return (doc.GetElement(id) as ParameterElement)?.Name;
            }
            catch { return null; }
        }

        private static List<string> DistinctValues(Document doc, View view, ICollection<ElementId> categories,
                                                   ElementId parameter)
        {
            var values = new HashSet<string>(StringComparer.Ordinal);
            var categorySet = new HashSet<long>(categories.Select(Rid.Value));

            foreach (Element e in new FilteredElementCollector(doc, view.Id).WhereElementIsNotElementType())
            {
                Category category;
                try { category = e.Category; } catch { continue; }
                if (category == null || !categorySet.Contains(Rid.Value(category.Id))) continue;

                Parameter p = ParameterOf(doc, e, parameter);
                if (p == null) { values.Add(""); continue; }
                string text = ValueText(p);
                values.Add(text ?? "");
            }
            return values.ToList();
        }

        private static Parameter ParameterOf(Document doc, Element e, ElementId parameter)
        {
            try
            {
                long raw = Rid.Value(parameter);
                if (raw < 0) return e.get_Parameter((BuiltInParameter)raw);
                var definition = (doc.GetElement(parameter) as ParameterElement)?.GetDefinition();
                return definition == null ? null : e.get_Parameter(definition);
            }
            catch { return null; }
        }

        private static string ValueText(Parameter p)
        {
            try
            {
                if (!p.HasValue) return "";
                switch (p.StorageType)
                {
                    case StorageType.String: return p.AsString() ?? "";
                    case StorageType.Integer: return p.AsInteger().ToString(System.Globalization.CultureInfo.InvariantCulture);
                    case StorageType.Double: return p.AsValueString() ?? p.AsDouble().ToString("R", System.Globalization.CultureInfo.InvariantCulture);
                    case StorageType.ElementId: return p.AsValueString() ?? Rid.Value(p.AsElementId()).ToString();
                    default: return "";
                }
            }
            catch { return ""; }
        }

        private static ElementId SolidFillPattern(Document doc)
        {
            try
            {
                foreach (FillPatternElement f in new FilteredElementCollector(doc)
                             .OfClass(typeof(FillPatternElement)).Cast<FillPatternElement>())
                {
                    FillPattern pattern = f.GetFillPattern();
                    if (pattern != null && pattern.IsSolidFill && pattern.Target == FillPatternTarget.Drafting)
                        return f.Id;
                }
            }
            catch { }
            return ElementId.InvalidElementId;
        }

        // ---- overrides ------------------------------------------------------

        private static OverrideGraphicSettings ReadOverrides(Document doc, JObject o)
        {
            if (o == null) throw new ArgumentException("overrides must be an object");
            var settings = new OverrideGraphicSettings();

            Color projection = ReadColour(o.Value<string>("line_color"), "line_color");
            if (projection != null) { settings.SetProjectionLineColor(projection); settings.SetCutLineColor(projection); }

            Color cut = ReadColour(o.Value<string>("cut_line_color"), "cut_line_color");
            if (cut != null) settings.SetCutLineColor(cut);

            Color surface = ReadColour(o.Value<string>("surface_color"), "surface_color");
            if (surface != null)
            {
                ElementId solid = SolidFillPattern(doc);
                if (solid != ElementId.InvalidElementId) settings.SetSurfaceForegroundPatternId(solid);
                settings.SetSurfaceForegroundPatternColor(surface);
            }

            Color cutFill = ReadColour(o.Value<string>("cut_color"), "cut_color");
            if (cutFill != null)
            {
                ElementId solid = SolidFillPattern(doc);
                if (solid != ElementId.InvalidElementId) settings.SetCutForegroundPatternId(solid);
                settings.SetCutForegroundPatternColor(cutFill);
            }

            int? transparency = o.Value<int?>("transparency");
            if (transparency != null)
            {
                if (transparency < 0 || transparency > 100)
                    throw new ArgumentException("transparency must be 0..100");
                settings.SetSurfaceTransparency(transparency.Value);
            }

            bool? halftone = o.Value<bool?>("halftone");
            if (halftone != null) settings.SetHalftone(halftone.Value);

            string pattern = o.Value<string>("line_pattern");
            if (!string.IsNullOrWhiteSpace(pattern))
            {
                ElementId patternId = string.Equals(pattern, "Solid", StringComparison.OrdinalIgnoreCase)
                    ? LinePatternElement.GetSolidPatternId()
                    : LinePatternElement.GetLinePatternElementByName(doc, pattern)?.Id;
                if (patternId == null)
                    throw new ArgumentException("line_pattern '" + pattern + "' is not a line pattern in this document (or 'Solid').");
                settings.SetProjectionLinePatternId(patternId);
                settings.SetCutLinePatternId(patternId);
                o["__line_pattern_id"] = Rid.Value(patternId);
            }

            int? weight = o.Value<int?>("line_weight");
            if (weight != null)
            {
                if (weight < 1 || weight > 16)
                    throw new ArgumentException("line_weight must be 1..16, which is Revit's whole range");
                settings.SetProjectionLineWeight(weight.Value);
                settings.SetCutLineWeight(weight.Value);
            }
            return settings;
        }

        private static Color ReadColour(string text, string field)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            string hex = text.Trim().TrimStart('#');
            if (hex.Length != 6)
                throw new ArgumentException(field + " must be a six-digit hex colour like #E6194B");
            try
            {
                return new Color(
                    Convert.ToByte(hex.Substring(0, 2), 16),
                    Convert.ToByte(hex.Substring(2, 2), 16),
                    Convert.ToByte(hex.Substring(4, 2), 16));
            }
            catch { throw new ArgumentException(field + " is not a valid hex colour: '" + text + "'"); }
        }

        /// <summary>
        /// Did the view keep what we asked for? Only the fields the caller SET are
        /// compared: an override object carries a value for everything, and comparing
        /// the untouched ones would fail on defaults nobody asked about.
        /// </summary>
        private static bool OverridesMatch(OverrideGraphicSettings actual, JObject wanted)
        {
            if (actual == null) return false;
            if (wanted == null) return true;
            try
            {
                Color line = ReadColour(wanted.Value<string>("line_color"), "line_color");
                if (line != null && !SameColour(actual.ProjectionLineColor, line)) return false;

                Color cutLine = ReadColour(wanted.Value<string>("cut_line_color"), "cut_line_color");
                if (cutLine != null && !SameColour(actual.CutLineColor, cutLine)) return false;

                Color surface = ReadColour(wanted.Value<string>("surface_color"), "surface_color");
                if (surface != null && !SameColour(actual.SurfaceForegroundPatternColor, surface)) return false;

                Color cutFill = ReadColour(wanted.Value<string>("cut_color"), "cut_color");
                if (cutFill != null && !SameColour(actual.CutForegroundPatternColor, cutFill)) return false;

                int? transparency = wanted.Value<int?>("transparency");
                if (transparency != null && actual.Transparency != transparency.Value) return false;

                bool? halftone = wanted.Value<bool?>("halftone");
                if (halftone != null && actual.Halftone != halftone.Value) return false;

                long? pattern = wanted.Value<long?>("__line_pattern_id");
                if (pattern != null && Rid.Value(actual.ProjectionLinePatternId) != pattern.Value) return false;

                int? weight = wanted.Value<int?>("line_weight");
                if (weight != null && actual.ProjectionLineWeight != weight.Value) return false;
            }
            catch { return false; }
            return true;
        }

        /// <summary>The category, or its named subcategory (V/G rows are both).</summary>
        private static ElementId ResolveVisibilityCategory(Document doc, JObject a)
        {
            ElementId main = ResolveCategory(doc, a.Value<string>("category"));
            string sub = a.Value<string>("subcategory");
            if (string.IsNullOrWhiteSpace(sub)) return main;
            Category parent = Category.GetCategory(doc, main);
            foreach (Category c in parent.SubCategories)
                if (string.Equals(c.Name, sub, StringComparison.OrdinalIgnoreCase)) return c.Id;
            throw new ArgumentException("subcategory '" + sub + "' is not under '" + parent.Name + "'. It has: " +
                string.Join(", ", parent.SubCategories.Cast<Category>().Select(c => c.Name).Take(40)));
        }

        /// <summary>
        /// A template that governs this category's V/G row takes any write to the view
        /// silently. Refused with the one alternative that works: edit the template.
        /// </summary>
        private static void RequireCategoryVgNotTemplated(Document doc, View view, ElementId categoryId)
        {
            if (view == null || view.ViewTemplateId == ElementId.InvalidElementId) return;
            View template = doc.GetElement(view.ViewTemplateId) as View;
            Category category = Category.GetCategory(doc, categoryId);
            bool annotation = category != null && category.CategoryType == CategoryType.Annotation;
            if (!TemplateGoverns(template, annotation ? BuiltInParameter.VIS_GRAPHICS_ANNOTATION : BuiltInParameter.VIS_GRAPHICS_MODEL))
                return;
            throw new ArgumentException(
                "view '" + view.Name + "' takes its " + (annotation ? "annotation" : "model") + " V/G from template '" +
                template.Name + "' (id " + Rid.Value(template.Id) + "): Revit would accept this write and keep the template's " +
                "value. Nothing was written. Edit the template instead - the same action with view_id=" + Rid.Value(template.Id) +
                " - or release that row with set_template_controls controlled=false.");
        }

        private static bool SameColour(Color actual, Color wanted)
        {
            if (actual == null || !actual.IsValid) return false;
            return actual.Red == wanted.Red && actual.Green == wanted.Green && actual.Blue == wanted.Blue;
        }
    }
}
