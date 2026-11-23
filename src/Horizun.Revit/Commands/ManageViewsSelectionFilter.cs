// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// create_selection_filter: a SelectionFilterElement - a view filter that holds a FIXED SET OF
// ELEMENT IDS instead of parameter rules - and, optionally, the same action puts it on a view
// with graphic overrides (view_id / view_key + overrides + visible). create_filter and
// apply_filter cover the rule-based filters; a set picked by hand had no typed path.
//
//   * name and members are checked before any transaction (SelectionFilterRules); the name is
//     unique among ALL view filters of the document, rule-based or not;
//   * a view that takes the filter must accept graphic overrides (a template that governs V/G
//     refuses with the template named) and passes the same ownership gate as the other view
//     writers (allow_unmarked_view lifts it);
//   * the re-read after the commit compares the stored members with the requested ones
//     (exactly those, no more, no fewer) and, on a view, the filter's presence, visibility and
//     overrides.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Commands
{
    public sealed partial class ManageViewsCommand
    {
        internal static bool IsSelectionFilterOperation(string op) => string.Equals(op, "create_selection_filter", StringComparison.OrdinalIgnoreCase);

        private static List<long> SelectionMemberIds(JObject a)
        {
            var array = a["element_ids"] as JArray;
            if (array == null || array.Any(t => t.Type != JTokenType.Integer)) return null;
            return array.Select(t => t.Value<long>()).ToList();
        }

        private static FilterElement ExistingAnyFilter(Document doc, string name)
        {
            foreach (FilterElement f in new FilteredElementCollector(doc).OfClass(typeof(FilterElement)).Cast<FilterElement>())
            {
                string existing; try { existing = f.Name; } catch { continue; }
                if (string.Equals(existing, name, StringComparison.Ordinal)) return f;
            }
            return null;
        }

        internal static void ValidateSelectionFilter(Document doc, JObject a, Dictionary<string, Type> known)
        {
            string name = a.Value<string>("name");
            string nameError = SelectionFilterRules.NameError(name);
            if (nameError != null) throw new ArgumentException(nameError);
            FilterElement taken = ExistingAnyFilter(doc, name);
            if (taken != null)
                throw new ArgumentException("a view filter named '" + name + "' already exists (id " + Rid.Value(taken.Id) +
                    "). Filter names are unique in a document; pick another name or put the existing one on a view with apply_filter.");
            List<long> ids = SelectionMemberIds(a);
            if (a["element_ids"] != null && ids == null) throw new ArgumentException("element_ids must be an array of element ids.");
            string membersError = SelectionFilterRules.MembersError(ids);
            if (membersError != null) throw new ArgumentException(membersError);
            ReadElementIds(doc, a, "element_ids");   // every id must exist in THIS document
            bool hasView = a["view_id"] != null || !string.IsNullOrWhiteSpace(a.Value<string>("view_key"));
            string orphan = SelectionFilterRules.OrphanFieldError(hasView, a["overrides"] != null, a["visible"] != null);
            if (orphan != null) throw new ArgumentException(orphan);
            if (a["visible"] != null && a["visible"].Type != JTokenType.Boolean) throw new ArgumentException("visible must be a boolean when given");
            if (!hasView) return;
            View view = GraphicsView(doc, a, known);
            RequireOverridesAllowed(view, "filters");
            if (view != null)
            {
                RequireEditable(doc, view);
                HrzOwnership.Require(doc, view, "view", a.Value<bool?>("allow_unmarked_view") == true, "allow_unmarked_view");
            }
            if (a["overrides"] != null) ReadOverrides(doc, a["overrides"] as JObject);
        }

        internal static JObject SelectionFilterPreview(JObject a)
        {
            List<long> ids = SelectionMemberIds(a);
            return new JObject
            {
                ["name"] = a["name"], ["members"] = ids == null ? 0 : ids.Count,
                ["on_view"] = a["view_id"] != null || a["view_key"] != null,
                ["note"] = "A SelectionFilterElement holds exactly these ids; the apply re-reads the stored members."
            };
        }

        internal static Element ApplySelectionFilter(Document doc, JObject a, Dictionary<string, ElementId> aliases)
        {
            ElementId[] members = ReadElementIds(doc, a, "element_ids").ToArray();
            SelectionFilterElement filter = SelectionFilterElement.Create(doc, a.Value<string>("name"));
            filter.SetElementIds(members);
            bool hasView = a["view_id"] != null || !string.IsNullOrWhiteSpace(a.Value<string>("view_key"));
            if (hasView)
            {
                View view = GraphicsViewApply(doc, a, aliases);
                RequireOverridesAllowed(view, "filters");
                RequireEditable(doc, view);
                HrzOwnership.Require(doc, view, "view", a.Value<bool?>("allow_unmarked_view") == true, "allow_unmarked_view");
                try { view.AddFilter(filter.Id); }
                catch (Autodesk.Revit.Exceptions.ArgumentException ex)
                {
                    throw new InvalidOperationException("view '" + view.Name + "' cannot take this filter (" + ex.Message.Split('\n')[0] +
                        "): a member's category may not be filterable in that view. Nothing was kept.");
                }
                if (a["overrides"] is JObject overrides) view.SetFilterOverrides(filter.Id, ReadOverrides(doc, overrides));
                if (a["visible"] != null) view.SetFilterVisibility(filter.Id, a.Value<bool>("visible"));
                a["__selection_view_id"] = Rid.Value(view.Id);
            }
            a["__selection_filter_id"] = Rid.Value(filter.Id);
            return filter;
        }

        internal static bool VerifySelectionFilter(Document doc, JObject a, Element e)
        {
            var detail = new JObject();
            a["__selection_detail"] = detail;
            try
            {
                if (!(e is SelectionFilterElement filter) || !string.Equals(filter.Name, a.Value<string>("name"), StringComparison.Ordinal)) { detail["verified"] = false; return false; }
                var found = filter.GetElementIds().Select(Rid.Value).ToList();
                bool members = SelectionFilterRules.SameMembers(SelectionMemberIds(a), found);
                detail["members_requested"] = SelectionMemberIds(a).Count; detail["members_read"] = found.Count; detail["members_match"] = members;
                bool ok = members;
                if (a["__selection_view_id"] != null)
                {
                    View view = doc.GetElement(Rid.Make(a.Value<long>("__selection_view_id"))) as View;
                    bool onView = view != null && view.GetFilters().Contains(filter.Id);
                    detail["on_view"] = onView; ok &= onView;
                    if (onView && a["visible"] != null) { bool v = view.GetFilterVisibility(filter.Id) == a.Value<bool>("visible"); detail["visible_match"] = v; ok &= v; }
                    if (onView && a["overrides"] is JObject o) { bool v = OverridesMatch(view.GetFilterOverrides(filter.Id), o); detail["overrides_match"] = v; ok &= v; }
                }
                detail["verified"] = ok;
                return ok;
            }
            catch (Exception ex) { detail["verified"] = false; detail["reread_error"] = ex.Message; return false; }
        }

        internal static JObject SelectionFilterDetail(JObject a) => a["__selection_detail"] as JObject;
    }
}
