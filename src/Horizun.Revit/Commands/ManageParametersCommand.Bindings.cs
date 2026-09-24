// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
// horizun_manage_parameters: the BindingMap - list, rebind, remove, and helpers.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public sealed partial class ManageParametersCommand
    {
        private sealed class Bound
        {
            public InternalDefinition Def;
            public ElementBinding Binding;
            public Guid? Guid;
            public bool IsType => Binding is TypeBinding;
            public List<Category> Cats => Binding?.Categories?.Cast<Category>().ToList() ?? new List<Category>();
        }

        private static List<Bound> AllBindings(Document doc)
        {
            var list = new List<Bound>();
            DefinitionBindingMapIterator it = doc.ParameterBindings.ForwardIterator();
            it.Reset();
            while (it.MoveNext())
            {
                var def = it.Key as InternalDefinition;
                if (def == null) continue;
                var b = new Bound { Def = def, Binding = it.Current as ElementBinding };
                if (doc.GetElement(def.Id) is SharedParameterElement sp) b.Guid = sp.GuidValue;
                list.Add(b);
            }
            return list;
        }

        /// <summary>By GUID when given (a GUID identifies a shared parameter), else by a UNIQUE name.</summary>
        private static Bound FindBound(Document doc, JObject r, out string error)
        {
            error = null;
            string gtext = r.Value<string>("guid"), name = r.Value<string>("name");
            List<Bound> all = AllBindings(doc);
            List<Bound> hits;
            if (!string.IsNullOrWhiteSpace(gtext))
            {
                if (!Guid.TryParse(gtext.Trim(), out Guid g)) { error = "guid '" + gtext + "' is not a GUID."; return null; }
                hits = all.Where(b => b.Guid == g).ToList();
            }
            else if (!string.IsNullOrWhiteSpace(name)) hits = all.Where(b => b.Def.Name == name.Trim()).ToList();
            else { error = "name or guid is required."; return null; }
            if (hits.Count == 1) return hits[0];
            error = hits.Count == 0 ? "No bound parameter matches '" + (gtext ?? name) + "'."
                : "'" + name + "' names " + hits.Count + " bound parameters; pass guid to say which.";
            return null;
        }

        private static string CatKey(IEnumerable<Category> cats)
            => string.Join(",", cats.Select(c => Rid.Value(c.Id)).OrderBy(v => v));

        private static bool HasValue(Parameter p)
            => p != null && p.HasValue && !(p.StorageType == StorageType.String && string.IsNullOrEmpty(p.AsString()));

        /// <summary>Elements (instances or types) in these categories that carry a value of the parameter.</summary>
        private static int CountValues(Document doc, Definition def, IEnumerable<Category> cats, bool typeSide)
        {
            int n = 0;
            foreach (Category c in cats)
            {
                var col = new FilteredElementCollector(doc).OfCategoryId(c.Id);
                col = typeSide ? col.WhereElementIsElementType() : col.WhereElementIsNotElementType();
                foreach (Element e in col) if (HasValue(e.get_Parameter(def))) n++;
            }
            return n;
        }

        private static JObject BindingJson(Bound b) => new JObject
        {
            ["name"] = b.Def.Name,
            ["parameter_element_id"] = Rid.Value(b.Def.Id),
            ["data_type"] = SafeId(() => b.Def.GetDataType()),
            ["binding_kind"] = b.IsType ? "Type" : "Instance",
            ["categories"] = new JArray(b.Cats.Select(c => c.Name).OrderBy(x => x, StringComparer.Ordinal)),
            ["group"] = SafeId(() => b.Def.GetGroupTypeId()),
            ["shared"] = b.Guid.HasValue,
            ["guid"] = b.Guid?.ToString("d"),
            ["varies_across_groups"] = b.Def.VariesAcrossGroups
        };

        private static string SafeId(Func<ForgeTypeId> f)
        {
            try { return f()?.TypeId; } catch { return null; }
        }

        private static JObject ListBindings(Document doc)
        {
            List<Bound> all = AllBindings(doc);
            return new JObject
            {
                ["operation"] = "list_bindings", ["document"] = doc.Title, ["count"] = all.Count,
                ["shared"] = all.Count(b => b.Guid.HasValue),
                ["bindings"] = new JArray(all.OrderBy(b => b.Def.Name, StringComparer.Ordinal).Select(BindingJson))
            };
        }

        private static List<Category> ResolveCats(Document doc, JArray tokens, out string error)
        {
            error = null; var cats = new List<Category>();
            foreach (JToken t in tokens)
            {
                Category c = BindSharedParamCommand.ResolveCategory(doc, (string)t);
                if (c == null) { error = "Category '" + t + "' does not resolve in this document."; return null; }
                if (!c.AllowsBoundParameters) { error = "Category '" + c.Name + "' does not accept bound parameters."; return null; }
                if (!cats.Any(x => x.Id == c.Id)) cats.Add(c);
            }
            if (cats.Count == 0) error = "categories must name at least one category.";
            return cats.Count == 0 ? null : cats;
        }

        private static ElementBinding NewBinding(UIApplication app, IEnumerable<Category> cats, bool type)
        {
            CategorySet set = app.Application.Create.NewCategorySet();
            foreach (Category c in cats) set.Insert(c);
            return type ? (ElementBinding)app.Application.Create.NewTypeBinding(set) : app.Application.Create.NewInstanceBinding(set);
        }

        /// <summary>The binding of one definition id, re-read from the map.</summary>
        private static Bound Reread(Document doc, ElementId defId)
            => AllBindings(doc).FirstOrDefault(b => b.Def.Id == defId);

        private static void CheckBinding(PostconditionCheck c, Bound now, bool type, IEnumerable<Category> cats, ForgeTypeId group)
        {
            c.Compare("bound", true, now != null);
            if (now == null)
            {
                foreach (string w in new[] { "binding_kind", "categories", "group" }) c.Unreadable(w, null, "the binding is absent");
                return;
            }
            c.Compare("binding_kind", type ? "Type" : "Instance", now.IsType ? "Type" : "Instance");
            c.Compare("categories", CatKey(cats), CatKey(now.Cats));
            c.Compare("group", group?.TypeId, SafeId(() => now.Def.GetGroupTypeId()));
        }

        private static Plan BuildRebind(UIApplication uiapp, Document doc, JObject r, out string error)
        {
            Bound b = FindBound(doc, r, out error);
            if (b == null) return null;
            bool type = r["binding_kind"] == null ? b.IsType : string.Equals(r.Value<string>("binding_kind"), "Type", StringComparison.OrdinalIgnoreCase);
            List<Category> cats = b.Cats;
            if (r["categories"] is JArray ct) { cats = ResolveCats(doc, ct, out error); if (cats == null) return null; }
            ForgeTypeId group = b.Def.GetGroupTypeId();
            if (!string.IsNullOrWhiteSpace(r.Value<string>("group")))
            {
                group = BindSharedParamCommand.ResolveGroup(r.Value<string>("group"), out string why);
                if (group == null) { error = why; return null; }
            }
            bool kindChanges = type != b.IsType, groupChanges = group.TypeId != b.Def.GetGroupTypeId().TypeId;
            if (!kindChanges && !groupChanges && CatKey(cats) == CatKey(b.Cats))
            { error = "rebind changes nothing: pass categories, binding_kind and/or group that differ from the current binding."; return null; }
            var keep = new HashSet<long>(cats.Select(c => Rid.Value(c.Id)));
            List<Category> dropped = kindChanges ? b.Cats : b.Cats.Where(c => !keep.Contains(Rid.Value(c.Id))).ToList();
            int lost = CountValues(doc, b.Def, dropped, b.IsType);
            ElementId defId = b.Def.Id;
            var p = new Plan
            {
                Op = "rebind", Subject = Rid.Value(defId).ToString(), Category = "ParameterBinding", Action = PlannedAction.Modify, ValuesLost = lost,
                Apply = (app, d, def, s) =>
                {
                    Bound now = Reread(d, defId) ?? throw new InvalidOperationException("the binding disappeared before the rebind");
                    if (!d.ParameterBindings.ReInsert(now.Def, NewBinding(app, cats, type), group))
                        throw new InvalidOperationException("Revit refused the ReInsert");
                    Bound after = Reread(d, defId);
                    if (after != null && after.Def.GetGroupTypeId().TypeId != group.TypeId) after.Def.SetGroupTypeId(group);
                },
                Verify = (d, def, s) => { var c = new PostconditionCheck("bound", "binding_kind", "categories", "group"); CheckBinding(c, Reread(d, defId), type, cats, group); return c; },
                Report = (d, def, s) => { Bound now = Reread(d, defId); return now == null ? new JObject() : BindingJson(now); }
            };
            p.Before["binding"] = (b.IsType ? "Type" : "Instance") + "|" + CatKey(b.Cats) + "|" + b.Def.GetGroupTypeId().TypeId;
            p.Preview = new JObject
            {
                ["before"] = BindingJson(b), ["binding_kind"] = type ? "Type" : "Instance",
                ["categories"] = new JArray(cats.Select(c => c.Name)), ["group"] = group.TypeId,
                ["categories_dropped"] = new JArray(dropped.Select(c => c.Name)), ["values_lost"] = lost,
                ["note"] = kindChanges ? "Changing Instance/Type discards every stored value of this parameter." : null
            };
            return p;
        }

        private static Plan BuildRemove(Document doc, JObject r, out string error)
        {
            Bound b = FindBound(doc, r, out error);
            if (b == null) return null;
            int lost = CountValues(doc, b.Def, b.Cats, b.IsType);
            ElementId defId = b.Def.Id;
            var p = new Plan
            {
                Op = "remove_binding", Subject = Rid.Value(defId).ToString(), Category = "ParameterBinding", Action = PlannedAction.Delete, ValuesLost = lost,
                Apply = (app, d, def, s) =>
                {
                    Bound now = Reread(d, defId) ?? throw new InvalidOperationException("the binding disappeared before the removal");
                    if (!d.ParameterBindings.Remove(now.Def)) throw new InvalidOperationException("Revit refused to remove the binding");
                },
                Verify = (d, def, s) => new PostconditionCheck("binding_absent").Compare("binding_absent", true, Reread(d, defId) == null),
                Report = (d, def, s) => new JObject { ["parameter_element_kept"] = d.GetElement(defId) != null }
            };
            p.Before["binding"] = (b.IsType ? "Type" : "Instance") + "|" + CatKey(b.Cats);
            p.Preview = new JObject { ["before"] = BindingJson(b), ["values_lost"] = lost };
            return p;
        }
    }
}
