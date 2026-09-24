// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
// horizun_manage_parameters: Global Parameters - list, create, set (value, formula,
// association to element parameters), delete. Values travel in the project's DISPLAY
// units and are converted to internal units here; the reply re-reads the value Revit
// computed after Regenerate, not the one that was sent.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public sealed partial class ManageParametersCommand
    {
        private static bool Is(ForgeTypeId spec, ForgeTypeId other) => spec != null && other != null && spec.TypeId == other.TypeId;

        private static ParameterValue ToValue(Document doc, ForgeTypeId spec, JToken v)
        {
            if (Is(spec, SpecTypeId.String.Text) || Is(spec, SpecTypeId.String.MultilineText) || Is(spec, SpecTypeId.String.Url))
                return new StringParameterValue((string)v);
            if (Is(spec, SpecTypeId.Boolean.YesNo))
                return new IntegerParameterValue(v.Type == JTokenType.Boolean ? ((bool)v ? 1 : 0) : (int)v);
            if (Is(spec, SpecTypeId.Int.Integer)) return new IntegerParameterValue((int)v);
            if (spec.TypeId.StartsWith("autodesk.spec:spec.reference", StringComparison.Ordinal) || Is(spec, SpecTypeId.Reference.Material))
                return new ElementIdParameterValue(Rid.Make((long)v));
            double number = (double)v;
            if (UnitUtils.IsMeasurableSpec(spec))
                number = UnitUtils.ConvertToInternalUnits(number, doc.GetUnits().GetFormatOptions(spec).GetUnitTypeId());
            return new DoubleParameterValue(number);
        }

        private static JToken ValueJson(Document doc, ForgeTypeId spec, ParameterValue pv)
        {
            switch (pv)
            {
                case DoubleParameterValue d:
                    if (spec != null && UnitUtils.IsMeasurableSpec(spec))
                        return new JObject
                        {
                            ["internal"] = d.Value,
                            ["display"] = UnitUtils.ConvertFromInternalUnits(d.Value, doc.GetUnits().GetFormatOptions(spec).GetUnitTypeId()),
                            ["formatted"] = UnitFormatUtils.Format(doc.GetUnits(), spec, d.Value, false)
                        };
                    return d.Value;
                case IntegerParameterValue i: return i.Value;
                case StringParameterValue s: return s.Value;
                case ElementIdParameterValue e: return Rid.Value(e.Value);
                default: return JValue.CreateNull();
            }
        }

        /// <summary>Same value in internal units: doubles within a relative 1e-9, the rest exactly.</summary>
        private static bool SameValue(ParameterValue a, ParameterValue b)
        {
            if (a is DoubleParameterValue x && b is DoubleParameterValue y)
                return Math.Abs(x.Value - y.Value) <= 1e-9 * Math.Max(1.0, Math.Abs(x.Value));
            if (a is IntegerParameterValue i && b is IntegerParameterValue j) return i.Value == j.Value;
            if (a is StringParameterValue s && b is StringParameterValue t) return (s.Value ?? "") == (t.Value ?? "");
            if (a is ElementIdParameterValue e && b is ElementIdParameterValue f) return e.Value == f.Value;
            return false;
        }

        private static JObject GlobalJson(Document doc, GlobalParameter g)
        {
            ForgeTypeId spec = null;
            try { spec = g.GetDefinition().GetDataType(); } catch { }
            return new JObject
            {
                ["id"] = Rid.Value(g.Id), ["name"] = g.Name, ["data_type"] = spec?.TypeId,
                ["value"] = ValueJson(doc, spec, g.GetValue()), ["formula"] = g.GetFormula(),
                ["driven_by_formula"] = g.IsDrivenByFormula, ["reporting"] = g.IsReporting,
                ["affected_elements"] = g.GetAffectedElements().Count
            };
        }

        private static JObject GlobalList(Document doc)
        {
            if (!GlobalParametersManager.AreGlobalParametersAllowed(doc))
                return new JObject { ["operation"] = "global_list", ["allowed"] = false, ["count"] = 0, ["globals"] = new JArray() };
            var rows = GlobalParametersManager.GetGlobalParametersOrdered(doc)
                .Select(id => doc.GetElement(id) as GlobalParameter).Where(g => g != null).Select(g => GlobalJson(doc, g)).ToList();
            return new JObject { ["operation"] = "global_list", ["document"] = doc.Title, ["allowed"] = true, ["count"] = rows.Count, ["globals"] = new JArray(rows) };
        }

        private static GlobalParameter FindGlobal(Document doc, JObject r, out string error)
        {
            error = null;
            string name = (r.Value<string>("name") ?? "").Trim();
            if (name.Length == 0) { error = "name is required."; return null; }
            if (!GlobalParametersManager.AreGlobalParametersAllowed(doc)) { error = "This document does not allow Global Parameters."; return null; }
            GlobalParameter g = doc.GetElement(GlobalParametersManager.FindByName(doc, name)) as GlobalParameter;
            if (g == null) error = "No Global Parameter is named '" + name + "'.";
            return g;
        }

        private static Plan BuildGlobalCreate(Document doc, JObject r, out string error)
        {
            error = null;
            string name = (r.Value<string>("name") ?? "").Trim();
            if (name.Length == 0) { error = "name is required."; return null; }
            if (!GlobalParametersManager.AreGlobalParametersAllowed(doc)) { error = "This document does not allow Global Parameters."; return null; }
            if (!GlobalParametersManager.IsUniqueName(doc, name)) { error = "A Global Parameter named '" + name + "' already exists; use global_set."; return null; }
            ForgeTypeId spec = ResolveSpec(r.Value<string>("data_type"), out error);
            if (spec == null) return null;
            JToken value = r["value"]; string formula = r.Value<string>("formula");
            if (value != null && formula != null) { error = "Pass value or formula, not both."; return null; }
            ParameterValue want;
            try { want = value == null ? null : ToValue(doc, spec, value); }
            catch (Exception ex) { error = "value does not fit data type " + spec.TypeId + ": " + ex.Message; return null; }
            var req = new List<string> { "exists", "data_type" };
            if (want != null) req.Add("value");
            if (formula != null) req.Add("formula");
            var p = new Plan
            {
                Op = "global_create", Subject = name, Category = "GlobalParameter", Action = PlannedAction.Create,
                Apply = (app, d, def, s) =>
                {
                    GlobalParameter g = GlobalParameter.Create(d, name, spec);
                    s.CreatedId = g.Id;
                    if (want != null) g.SetValue(want);
                    if (formula != null) g.SetFormula(formula);
                },
                Verify = (d, def, s) => VerifyGlobal(d, s.CreatedId, req, spec, want, formula, null),
                Report = (d, def, s) => d.GetElement(s.CreatedId) is GlobalParameter g ? GlobalJson(d, g) : new JObject()
            };
            p.Before["request"] = name + "|" + spec.TypeId;
            p.Preview = new JObject { ["name"] = name, ["data_type"] = spec.TypeId, ["value"] = value, ["formula"] = formula };
            return p;
        }

        private static PostconditionCheck VerifyGlobal(Document d, ElementId id, List<string> req, ForgeTypeId spec,
                                                       ParameterValue want, string formula, List<KeyValuePair<ElementId, string>> assoc)
        {
            var c = new PostconditionCheck(req.ToArray());
            GlobalParameter g = id == null ? null : d.GetElement(id) as GlobalParameter;
            if (req.Contains("exists")) c.Compare("exists", true, g != null);
            if (g == null) { foreach (string w in req.Where(x => x != "exists")) c.Unreadable(w, null, "the global parameter is absent"); return c; }
            if (req.Contains("data_type")) c.Compare("data_type", spec.TypeId, SafeId(() => g.GetDefinition().GetDataType()));
            if (want != null) c.Record("value", ValueJson(d, spec, want), ValueJson(d, spec, g.GetValue()), SameValue(want, g.GetValue()));
            if (formula != null) c.Compare("formula", formula, g.GetFormula() ?? "");
            if (assoc != null)
                for (int i = 0; i < assoc.Count; i++)
                {
                    Parameter prm = d.GetElement(assoc[i].Key)?.LookupParameter(assoc[i].Value);
                    if (prm == null) c.Unreadable("associate[" + i + "]", Rid.Value(id), "the element or its parameter no longer reads");
                    else c.Compare("associate[" + i + "]", Rid.Value(id), Rid.Value(prm.GetAssociatedGlobalParameter()));
                }
            return c;
        }

        private static Plan BuildGlobalSet(Document doc, JObject r, out string error)
        {
            GlobalParameter g = FindGlobal(doc, r, out error);
            if (g == null) return null;
            ForgeTypeId spec = g.GetDefinition().GetDataType();
            JToken value = r["value"]; string formula = r.Value<string>("formula");
            if (value != null && formula != null) { error = "Pass value or formula, not both."; return null; }
            if (value != null && g.IsDrivenByFormula) { error = "'" + g.Name + "' is driven by a formula; clear it with formula \"\" first."; return null; }
            ParameterValue want;
            try { want = value == null ? null : ToValue(doc, spec, value); }
            catch (Exception ex) { error = "value does not fit data type " + spec.TypeId + ": " + ex.Message; return null; }
            if (formula != null && formula.Length > 0 && !g.IsValidFormula(formula)) { error = "Revit rejects the formula '" + formula + "'."; return null; }
            var assoc = new List<KeyValuePair<ElementId, string>>();
            if (r["associate"] is JArray aj)
                foreach (JObject a in aj.OfType<JObject>())
                {
                    long eid = a.Value<long?>("element_id") ?? -1; string pn = a.Value<string>("parameter");
                    Element e = Rid.CanRepresent(eid) ? doc.GetElement(Rid.Make(eid)) : null;
                    Parameter prm = e?.LookupParameter(pn ?? "");
                    if (prm == null) { error = "associate: element " + eid + " has no parameter '" + pn + "'."; return null; }
                    if (!prm.CanBeAssociatedWithGlobalParameter(g.Id)) { error = "associate: '" + pn + "' of element " + eid + " cannot take '" + g.Name + "' (type or storage mismatch)."; return null; }
                    assoc.Add(new KeyValuePair<ElementId, string>(e.Id, pn));
                }
            if (want == null && formula == null && assoc.Count == 0) { error = "global_set changes nothing: pass value, formula and/or associate."; return null; }
            var req = new List<string>();
            if (want != null) req.Add("value");
            if (formula != null) req.Add("formula");
            for (int i = 0; i < assoc.Count; i++) req.Add("associate[" + i + "]");
            ElementId id = g.Id;
            var p = new Plan
            {
                Op = "global_set", Subject = Rid.Value(id).ToString(), Category = "GlobalParameter", Action = PlannedAction.Modify,
                Apply = (app, d, def, s) =>
                {
                    var gp = (GlobalParameter)d.GetElement(id);
                    if (formula != null) gp.SetFormula(formula);
                    if (want != null) gp.SetValue(want);
                    foreach (var a in assoc) d.GetElement(a.Key).LookupParameter(a.Value).AssociateWithGlobalParameter(id);
                },
                Verify = (d, def, s) => VerifyGlobal(d, id, req, spec, want, formula, assoc),
                Report = (d, def, s) =>
                {
                    JObject o = d.GetElement(id) is GlobalParameter gp ? GlobalJson(d, gp) : new JObject();
                    o["associated_values"] = new JArray(assoc.Select(a => new JObject
                    { ["element_id"] = Rid.Value(a.Key), ["parameter"] = a.Value, ["value"] = d.GetElement(a.Key)?.LookupParameter(a.Value)?.AsValueString() }));
                    return o;
                }
            };
            p.Before["global"] = g.Name + "|" + g.GetFormula() + "|" + ValueJson(doc, spec, g.GetValue()).ToString(Newtonsoft.Json.Formatting.None);
            p.Preview = new JObject { ["before"] = GlobalJson(doc, g), ["value"] = value, ["formula"] = formula, ["associate"] = assoc.Count };
            return p;
        }

        private static Plan BuildGlobalDelete(Document doc, JObject r, out string error)
        {
            GlobalParameter g = FindGlobal(doc, r, out error);
            if (g == null) return null;
            ElementId id = g.Id;
            int affected = g.GetAffectedElements().Count;
            var p = new Plan
            {
                Op = "global_delete", Subject = Rid.Value(id).ToString(), Category = "GlobalParameter", Action = PlannedAction.Delete, ValuesLost = affected,
                Apply = (app, d, def, s) => d.Delete(id),
                Verify = (d, def, s) => new PostconditionCheck("absent").Compare("absent", true, d.GetElement(id) == null),
                Report = (d, def, s) => new JObject { ["deleted_id"] = Rid.Value(id) }
            };
            p.Before["global"] = g.Name;
            p.Preview = new JObject { ["before"] = GlobalJson(doc, g), ["associations_released"] = affected };
            return p;
        }
    }
}
