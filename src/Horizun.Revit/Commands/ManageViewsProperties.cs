// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// set_view_properties: scale, detail level, discipline, visual style and phase of one or
// several EXISTING views (or view templates), as a direct operation of horizun_manage_views.
//
//   * A view whose TEMPLATE controls a requested property is REFUSED with the template named
//     (ViewPropertyRules.TemplateRefusal): Revit would accept the call and keep the template's
//     value. The template is never detached for the caller.
//   * The views are the caller's to change: the HRZ ownership gate of view_crop applies
//     (allow_unmarked_view lifts it), and a view another person has checked out is refused.
//   * Every view is re-read after the commit and each requested property must read back as
//     asked; a view that does not match fails the whole batch.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Commands
{
    public sealed partial class ManageViewsCommand
    {
        internal static bool IsViewPropertiesOperation(string op) => string.Equals(op, "set_view_properties", StringComparison.OrdinalIgnoreCase);

        private static List<long> ViewPropertyIds(JObject a)
        {
            if (a["view_ids"] == null) return null;
            var array = a["view_ids"] as JArray;
            if (array == null || array.Any(t => t.Type != JTokenType.Integer))
                throw new ArgumentException("view_ids must be an array of view ids.");
            return array.Select(t => t.Value<long>()).ToList();
        }

        /// <summary>The BuiltInParameter names a view template CONTROLS (listed and not marked as uncontrolled).</summary>
        internal static HashSet<string> ControlledTemplateNames(View template)
        {
            var ids = new HashSet<ElementId>(template.GetTemplateParameterIds());
            foreach (ElementId id in template.GetNonControlledTemplateParameterIds()) ids.Remove(id);
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (ElementId id in ids)
            {
                long raw = Rid.Value(id);
                if (raw >= 0 || raw < int.MinValue) continue;
                BuiltInParameter bip = (BuiltInParameter)(int)raw;
                if (Enum.IsDefined(typeof(BuiltInParameter), bip)) names.Add(bip.ToString());
            }
            return names;
        }

        /// <summary>Another person's checkout makes the view theirs for now; Revit would refuse the edit with a worse sentence.</summary>
        internal static void RequireEditable(Document doc, Element e)
        {
            if (!doc.IsWorkshared) return;
            string owner = null; CheckoutStatus status;
            try { status = WorksharingUtils.GetCheckoutStatus(doc, e.Id, out owner); }
            catch { return; }
            if (status == CheckoutStatus.OwnedByOtherUser)
                throw new ArgumentException("'" + e.Name + "' (" + Rid.Value(e.Id) + ") is checked out by " + (owner ?? "another user") +
                    "; ask them to relinquish it or synchronize. Nothing was written.");
        }

        private static void ParseViewPropertyWords(Document doc, JObject a)
        {
            string error = null;
            if (a["detail_level"] != null && ViewPropertyRules.ParseDetailLevel(a.Value<string>("detail_level"), out error) == null) throw new ArgumentException(error);
            if (a["discipline"] != null && ViewPropertyRules.ParseDiscipline(a.Value<string>("discipline"), out error) == null) throw new ArgumentException(error);
            if (a["display_style"] != null)
            {
                View3DRequestRules.ParseStyle(a.Value<string>("display_style"), out error);
                if (error != null) throw new ArgumentException(error);
            }
            if (a["phase_id"] != null && !(Need<Element>(doc, a, "phase_id") is Phase)) throw new ArgumentException("phase_id must identify a Phase");
        }

        private static void CheckViewForProperties(Document doc, View view, JObject a, IList<string> requested)
        {
            string label = "view '" + view.Name + "' (" + Rid.Value(view.Id) + ")";
            string kind = ViewPropertyRules.KindRefusal(view.GetType().Name, view is ViewSheet || view is ViewSchedule,
                view is View3D three && three.IsPerspective, requested);
            if (kind != null) throw new ArgumentException(label + ": " + kind);
            ElementId templateId = view.ViewTemplateId;
            if (!view.IsTemplate && templateId != null && templateId != ElementId.InvalidElementId && doc.GetElement(templateId) is View template)
            {
                string refusal = ViewPropertyRules.TemplateRefusal(requested, ControlledTemplateNames(template), template.Name, Rid.Value(templateId));
                if (refusal != null) throw new ArgumentException(label + ": " + refusal);
            }
            RequireEditable(doc, view);
            HrzOwnership.Require(doc, view, "view", a.Value<bool?>("allow_unmarked_view") == true, "allow_unmarked_view");
        }

        internal static void ValidateViewProperties(Document doc, JObject a, Dictionary<string, Type> known)
        {
            List<string> requested = ViewPropertyRules.Requested(f => a[f] != null);
            if (requested.Count == 0) throw new ArgumentException(ViewPropertyRules.NothingRequested());
            List<long> list = ViewPropertyIds(a);
            string idsError = ViewPropertyRules.IdsError(a["view_id"] != null, !string.IsNullOrWhiteSpace(a.Value<string>("view_key")), list);
            if (idsError != null) throw new ArgumentException(idsError);
            ParseViewPropertyWords(doc, a);
            if (list != null)
            {
                foreach (long id in list)
                {
                    if (!Rid.CanRepresent(id) || !(doc.GetElement(Rid.Make(id)) is View v)) throw new ArgumentException("view_ids entry " + id + " is not a view");
                    CheckViewForProperties(doc, v, a, requested);
                }
            }
            else if (string.IsNullOrWhiteSpace(a.Value<string>("view_key"))) CheckViewForProperties(doc, Need<View>(doc, a, "view_id"), a, requested);
            else Reference<View>(doc, a, "view_id", "view_key", known);   // a view made earlier in this batch: checked again by Apply
        }

        private static List<View> ViewsForProperties(Document doc, JObject a, Dictionary<string, ElementId> aliases)
        {
            List<long> list = ViewPropertyIds(a);
            if (list == null) return new List<View> { Resolve<View>(doc, a, "view_id", "view_key", aliases) };
            return list.Select(id => Need<View>(doc, new JObject { ["view_id"] = id }, "view_id")).ToList();
        }

        private static JObject ViewPropertyState(View v, IEnumerable<string> fields)
        {
            var s = new JObject { ["view_id"] = Rid.Value(v.Id), ["name"] = v.Name };
            foreach (string f in fields)
                switch (f)
                {
                    case "view_scale": s[f] = v.Scale; break;
                    case "detail_level": s[f] = v.DetailLevel.ToString(); break;
                    case "discipline": s[f] = v.Discipline.ToString(); break;
                    case "display_style": s[f] = v.DisplayStyle.ToString(); break;
                    case "phase_id": s[f] = Rid.Value(v.get_Parameter(BuiltInParameter.VIEW_PHASE)?.AsElementId() ?? ElementId.InvalidElementId); break;
                }
            return s;
        }

        internal static JObject ViewPropertiesPreview(Document doc, JObject a)
        {
            var fields = ViewPropertyRules.Requested(f => a[f] != null);
            var rows = new JArray();
            try
            {
                List<long> list = ViewPropertyIds(a) ?? (a["view_id"] != null ? new List<long> { a.Value<long>("view_id") } : new List<long>());
                foreach (long id in list)
                    if (Rid.CanRepresent(id) && doc.GetElement(Rid.Make(id)) is View v) rows.Add(ViewPropertyState(v, fields));
            }
            catch (Exception) { }
            return new JObject { ["requested"] = new JObject(fields.Select(f => new JProperty(f, a[f].DeepClone()))), ["current"] = rows };
        }

        internal static Element ApplyViewProperties(Document doc, JObject a, Dictionary<string, ElementId> aliases)
        {
            List<string> requested = ViewPropertyRules.Requested(f => a[f] != null);
            List<View> views = ViewsForProperties(doc, a, aliases);
            var before = new JArray();
            foreach (View view in views)
            {
                CheckViewForProperties(doc, view, a, requested);
                before.Add(ViewPropertyState(view, requested));
                if (a["view_scale"] != null) ApplyViewScale(doc, view, a.Value<int>("view_scale"), "set_view_properties");
                if (a["detail_level"] != null)
                    view.DetailLevel = (ViewDetailLevel)Enum.Parse(typeof(ViewDetailLevel), ViewPropertyRules.ParseDetailLevel(a.Value<string>("detail_level"), out _));
                if (a["discipline"] != null)
                    view.Discipline = (ViewDiscipline)Enum.Parse(typeof(ViewDiscipline), ViewPropertyRules.ParseDiscipline(a.Value<string>("discipline"), out _));
                if (a["display_style"] != null)
                {
                    string style = View3DRequestRules.ParseStyle(a.Value<string>("display_style"), out _);
                    if (!Enum.TryParse(style, false, out DisplayStyle parsed) || !Enum.IsDefined(typeof(DisplayStyle), parsed))
                        throw new InvalidOperationException("display_style '" + style + "' does not exist in this Revit version.");
                    view.DisplayStyle = parsed;
                }
                if (a["phase_id"] != null)
                {
                    Parameter phase = view.get_Parameter(BuiltInParameter.VIEW_PHASE);
                    if (phase == null || phase.IsReadOnly) throw new InvalidOperationException("view '" + view.Name + "' has no editable phase.");
                    if (!phase.Set(Rid.Make(a.Value<long>("phase_id")))) throw new InvalidOperationException("Revit refused the phase for view '" + view.Name + "'.");
                }
            }
            a["__view_properties"] = new JObject { ["fields"] = new JArray(requested), ["before"] = before, ["view_ids"] = new JArray(views.Select(v => Rid.Value(v.Id))) };
            return views[0];
        }

        /// <summary>Every view re-read: each requested property must read back as asked.</summary>
        internal static bool VerifyViewProperties(Document doc, JObject a, Element first)
        {
            if (!(a["__view_properties"] is JObject detail)) return false;
            try
            {
                var fields = ((JArray)detail["fields"]).Select(t => (string)t).ToList();
                var rows = new JArray(); bool all = true;
                foreach (long id in ((JArray)detail["view_ids"]).Select(t => t.Value<long>()))
                {
                    View v = doc.GetElement(Rid.Make(id)) as View;
                    if (v == null) { all = false; continue; }
                    JObject now = ViewPropertyState(v, fields); bool ok = true;
                    var mismatch = new JArray();
                    foreach (string f in fields)
                    {
                        bool match;
                        switch (f)
                        {
                            case "view_scale": match = v.Scale == a.Value<int>("view_scale"); break;
                            case "detail_level": match = string.Equals(v.DetailLevel.ToString(), ViewPropertyRules.ParseDetailLevel(a.Value<string>(f), out _), StringComparison.Ordinal); break;
                            case "discipline": match = string.Equals(v.Discipline.ToString(), ViewPropertyRules.ParseDiscipline(a.Value<string>(f), out _), StringComparison.Ordinal); break;
                            case "display_style": match = string.Equals(v.DisplayStyle.ToString(), View3DRequestRules.ParseStyle(a.Value<string>(f), out _), StringComparison.Ordinal); break;
                            default: match = (long)now[f] == a.Value<long>(f); break;
                        }
                        if (!match) { ok = false; mismatch.Add(f); }
                    }
                    now["verified"] = ok; if (!ok) now["mismatch"] = mismatch;
                    rows.Add(now); all &= ok;
                }
                detail["reread"] = rows; detail["verified"] = all;
                return all;
            }
            catch (Exception ex) { detail["verified"] = false; detail["reread_error"] = ex.Message; return false; }
        }

        internal static JObject ViewPropertiesDetail(JObject a) => a["__view_properties"] as JObject;

        /// <summary>
        /// What a list of ids resolves to right now (unique id and name of each), as one digest: the confirmation
        /// token binds it, so a list whose members were renamed or swapped between rehearsal and apply is a stale plan.
        /// </summary>
        internal static string PlanListBinding(Document doc, JArray ids)
        {
            var sb = new System.Text.StringBuilder();
            foreach (JToken t in ids.Take(5000))
                sb.Append(t.Type == JTokenType.Integer ? SafePlanRef(doc, t.Value<long>()) : "<not an id>").Append('|');
            using (var sha = System.Security.Cryptography.SHA256.Create())
                return ids.Count.ToString(CultureInfo.InvariantCulture) + ":" +
                       BitConverter.ToString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(sb.ToString()))).Replace("-", "");
        }
    }
}
