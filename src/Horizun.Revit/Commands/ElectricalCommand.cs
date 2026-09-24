// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// horizun_electrical - panels, circuits and panel schedules, read and written
// the verified way.
//
//   list_panels / list_circuits  answer from the document; loads are converted
//                                from internal units (VA, W, V, m) and a value the
//                                API cannot give is null, never 0.
//   create_circuit               ElectricalSystem.Create over element ids (+ panel).
//   assign_panel                 SelectPanel; re-read BaseEquipment.
//   add_to_circuit /             AddToCircuit / RemoveFromCircuit; re-read the
//   remove_from_circuit          circuit's member ids.
//   panel_schedule               PanelScheduleView.CreateInstanceView (template
//                                optional); re-read the view's panel.
//
// Compatibility of voltage, poles and distribution system is REVIT's decision:
// the rehearsal runs the real call, so a refusal comes back as Revit's message
// with nothing committed.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public sealed class ElectricalCommand : ICommand
    {
        public const string Tool = "horizun_electrical";
        public string Name => "horizun_electrical";
        public string Description => "Electrical panels, circuits and panel schedules: list, create, assign and edit, re-read after commit.";

        private const string Known = "list_panels, list_circuits, create_circuit, assign_panel, add_to_circuit, remove_from_circuit, panel_schedule";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            JObject request = VerifiedModelEdit.Parse(paramsJson, out CommandResult parseError);
            if (request == null) return parseError;
            string op = VerifiedModelEdit.Operation(request);
            if (op == "list_panels" || op == "list_circuits")
            {
                Document doc = app.ActiveUIDocument?.Document;
                if (doc == null) return CommandResult.Fail("No document is open.");
                CommandResult guard = DocumentGate.ReadGuard(doc, request, Tool);
                if (guard != null) return guard;
                return VerifiedModelEdit.ReadReply(op == "list_panels" ? ListPanels(doc) : ListCircuits(doc, request));
            }
            if (op != "create_circuit" && op != "assign_panel" && op != "add_to_circuit" &&
                op != "remove_from_circuit" && op != "panel_schedule")
                return VerifiedModelEdit.UnknownOperation(Tool, op, Known);

            GateResult gate = DocumentGate.ForMutation(app, request, Tool);
            if (!gate.Ok) return gate.Refusal;
            string error;
            ModelEdit edit;
            switch (op)
            {
                case "create_circuit": edit = PlanCreate(gate.Document, request, out error); break;
                case "assign_panel": edit = PlanAssign(gate.Document, request, out error); break;
                case "panel_schedule": edit = PlanSchedule(gate.Document, request, out error); break;
                default: edit = PlanMembers(gate.Document, request, op == "add_to_circuit", out error); break;
            }
            if (edit == null) return CommandResult.Fail(error + " Nothing was written.");
            edit.Tool = Tool; edit.Operation = op;
            return VerifiedModelEdit.Run(app, gate, request, edit, "element_ids", "circuit_id", "panel_id", "system_type", "template_id");
        }

        // ================================================================== reads

        private static JObject ListPanels(Document doc)
        {
            var rows = new JArray();
            var circuits = new FilteredElementCollector(doc).OfClass(typeof(ElectricalSystem)).Cast<ElectricalSystem>().ToList();
            foreach (FamilyInstance fi in new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_ElectricalEquipment)
                         .WhereElementIsNotElementType().OfType<FamilyInstance>())
            {
                ElementId distId = ParamId(fi, "RBS_FAMILY_CONTENT_DISTRIBUTION_SYSTEM");
                Element dist = distId == null ? null : doc.GetElement(distId);
                var fed = circuits.Where(c => SafeId(() => c.BaseEquipment?.Id) == fi.Id).ToList();
                rows.Add(new JObject
                {
                    ["id"] = Rid.Value(fi.Id),
                    ["name"] = Param(fi, "RBS_ELEC_PANEL_NAME") ?? VerifiedModelEdit.Safe(() => fi.Name),
                    ["family"] = VerifiedModelEdit.Safe(() => fi.Symbol.FamilyName),
                    ["type"] = VerifiedModelEdit.Safe(() => fi.Symbol.Name),
                    ["is_panel"] = fi.MEPModel is ElectricalEquipment,
                    ["distribution_system"] = VerifiedModelEdit.Safe(() => dist?.Name),
                    ["distribution_system_id"] = distId == null ? null : (JToken)Rid.Value(distId),
                    ["voltage"] = dist == null ? null : Param(dist, "RBS_DISTRIBUTIONSYS_VLL_PARAM"),
                    ["circuit_count"] = fed.Count,
                    ["apparent_load_va"] = Sum(fed, c => c.ApparentLoad, UnitTypeId.VoltAmperes),
                    ["total_connected_load"] = Param(fi, "RBS_ELEC_PANEL_TOTALLOAD_PARAM"),
                    ["total_demand_current"] = Param(fi, "RBS_ELEC_PANEL_TOTAL_DEMAND_CURRENT_PARAM")
                });
            }
            return new JObject
            {
                ["document"] = doc.Title, ["count"] = rows.Count, ["panels"] = rows,
                ["note"] = rows.Count == 0 ? "No electrical equipment in this document." :
                    "Loads from parameters are Revit's formatted strings; apparent_load_va sums the circuits fed by the panel."
            };
        }

        private static JObject ListCircuits(Document doc, JObject r)
        {
            long? panelFilter = r.Value<long?>("panel_id");
            var rows = new JArray();
            foreach (ElectricalSystem c in new FilteredElementCollector(doc).OfClass(typeof(ElectricalSystem)).Cast<ElectricalSystem>())
            {
                ElementId panel = SafeId(() => c.BaseEquipment?.Id);
                if (panelFilter.HasValue && (panel == null || Rid.Value(panel) != panelFilter.Value)) continue;
                rows.Add(CircuitJson(c));
            }
            return new JObject { ["document"] = doc.Title, ["count"] = rows.Count, ["circuits"] = rows,
                ["note"] = rows.Count == 0 ? "No electrical circuits" + (panelFilter.HasValue ? " on that panel." : " in this document.") : null };
        }

        private static JObject CircuitJson(ElectricalSystem c)
        {
            ElementId panel = SafeId(() => c.BaseEquipment?.Id);
            return new JObject
            {
                ["id"] = Rid.Value(c.Id),
                ["system_type"] = VerifiedModelEdit.Safe(() => c.SystemType.ToString()),
                ["panel_id"] = panel == null ? null : (JToken)Rid.Value(panel),
                ["panel"] = VerifiedModelEdit.Safe(() => c.PanelName),
                ["circuit_number"] = VerifiedModelEdit.Safe(() => c.CircuitNumber),
                ["load_name"] = VerifiedModelEdit.Safe(() => c.LoadName),
                ["element_ids"] = new JArray(Members(c).OrderBy(x => x)),
                ["apparent_load_va"] = Conv(() => c.ApparentLoad, UnitTypeId.VoltAmperes),
                ["true_load_w"] = Conv(() => c.TrueLoad, UnitTypeId.Watts),
                ["voltage_v"] = Conv(() => c.Voltage, UnitTypeId.Volts),
                ["poles"] = N(() => c.PolesNumber),
                ["length_m"] = Conv(() => c.Length, UnitTypeId.Meters),
                // ElectricalSystem.VoltageDrop is deprecated in 2026 and gone in 2027; there the
                // value comes from the circuit's own parameter, formatted by Revit.
#if REVIT2026 || REVIT2027
                ["voltage_drop"] = Param(c, "RBS_ELEC_VOLTAGE_DROP_PARAM")
#else
                ["voltage_drop_v"] = Conv(() => c.VoltageDrop, UnitTypeId.Volts)
#endif
            };
        }

        // ================================================================== writes

        private static ModelEdit PlanCreate(Document doc, JObject r, out string error)
        {
            List<ElementId> ids = Ids(doc, r, out error);
            if (ids == null) return null;
            string typeName = r.Value<string>("system_type") ?? "PowerCircuit";
            if (!Enum.TryParse(typeName, true, out ElectricalSystemType type))
            { error = "system_type must be one of " + string.Join(", ", Enum.GetNames(typeof(ElectricalSystemType))) + "."; return null; }
            FamilyInstance panel = null;
            if (r["panel_id"] != null && (panel = Panel(doc, r, out error)) == null) return null;
            foreach (ElementId id in ids)
            {
                var fi = doc.GetElement(id) as FamilyInstance;
                if (fi?.MEPModel == null) { error = "element " + Rid.Value(id) + " is not an MEP family instance with connectors."; return null; }
            }
            ElementId created = null; ElementId panelId = panel?.Id;
            var edit = new ModelEdit { Subject = "circuit:" + string.Join(",", ids.Select(Rid.Value).OrderBy(x => x)), Category = "ElectricalSystem", Action = PlannedAction.Create };
            foreach (ElementId id in ids) edit.Before["element:" + Rid.Value(id)] = CircuitsOf(doc, id);
            if (panel != null) edit.Before["panel"] = Rid.Value(panel.Id).ToString();
            edit.Plan = new JObject { ["element_ids"] = new JArray(ids.Select(Rid.Value)), ["system_type"] = type.ToString(), ["panel_id"] = panelId == null ? null : (JToken)Rid.Value(panelId) };
            edit.Apply = d =>
            {
                ElectricalSystem sys = ElectricalSystem.Create(d, ids, type);
                if (sys == null) throw new InvalidOperationException("Revit created no circuit from those elements");
                created = sys.Id;
                if (panelId != null) sys.SelectPanel((FamilyInstance)d.GetElement(panelId));
            };
            edit.Verify = d =>
            {
                var req = new List<string> { "exists", "members", "system_type" };
                if (panelId != null) req.Add("panel");
                var check = new PostconditionCheck(req.ToArray());
                var sys = created == null ? null : d.GetElement(created) as ElectricalSystem;
                if (sys == null) { foreach (string f in req) check.Unreadable(f, null, "no circuit re-reads"); return check; }
                check.Compare("exists", true, true);
                CheckMembers(check, sys, ids.Select(Rid.Value));
                try { check.Compare("system_type", type.ToString(), sys.SystemType.ToString()); } catch (Exception ex) { check.Unreadable("system_type", type.ToString(), ex.Message); }
                if (panelId != null) CheckPanel(check, sys, panelId);
                return check;
            };
            edit.Result = d => created == null || !(d.GetElement(created) is ElectricalSystem s) ? new JObject() : CircuitJson(s);
            return edit;
        }

        private static ModelEdit PlanAssign(Document doc, JObject r, out string error)
        {
            ElectricalSystem sys = Circuit(doc, r, out error);
            if (sys == null) return null;
            FamilyInstance panel = Panel(doc, r, out error);
            if (panel == null) return null;
            if (SafeId(() => sys.BaseEquipment?.Id) == panel.Id) { error = "circuit " + Rid.Value(sys.Id) + " is already on panel " + Rid.Value(panel.Id) + "; a no-op is refused rather than reported as work."; return null; }
            ElementId sid = sys.Id, pid = panel.Id;
            var edit = new ModelEdit { Subject = VerifiedModelEdit.Safe(() => sys.UniqueId) ?? "circuit:" + Rid.Value(sid), Category = "ElectricalSystem" };
            edit.Before["panel_id"] = SafeId(() => sys.BaseEquipment?.Id) is ElementId b ? Rid.Value(b).ToString() : "<none>";
            edit.Before["circuit_number"] = VerifiedModelEdit.Safe(() => sys.CircuitNumber) ?? "";
            edit.Plan = new JObject { ["circuit_id"] = Rid.Value(sid), ["panel_id"] = Rid.Value(pid) };
            edit.Apply = d => ((ElectricalSystem)d.GetElement(sid)).SelectPanel((FamilyInstance)d.GetElement(pid));
            edit.Verify = d =>
            {
                var check = new PostconditionCheck("panel");
                var s = d.GetElement(sid) as ElectricalSystem;
                if (s == null) check.Unreadable("panel", Rid.Value(pid), "the circuit does not re-read");
                else CheckPanel(check, s, pid);
                return check;
            };
            edit.Result = d => d.GetElement(sid) is ElectricalSystem s ? CircuitJson(s) : new JObject();
            return edit;
        }

        private static ModelEdit PlanMembers(Document doc, JObject r, bool add, out string error)
        {
            ElectricalSystem sys = Circuit(doc, r, out error);
            if (sys == null) return null;
            List<ElementId> ids = Ids(doc, r, out error);
            if (ids == null) return null;
            HashSet<long> current = new HashSet<long>(Members(sys));
            foreach (ElementId id in ids)
            {
                bool inIt = current.Contains(Rid.Value(id));
                if (add && inIt) { error = "element " + Rid.Value(id) + " is already on circuit " + Rid.Value(sys.Id) + "."; return null; }
                if (!add && !inIt) { error = "element " + Rid.Value(id) + " is not on circuit " + Rid.Value(sys.Id) + "."; return null; }
            }
            if (!add && ids.Count >= current.Count) { error = "removing every element would leave an empty circuit; delete the circuit instead (horizun_delete_verified)."; return null; }
            var expected = new HashSet<long>(current);
            foreach (ElementId id in ids) { if (add) expected.Add(Rid.Value(id)); else expected.Remove(Rid.Value(id)); }
            ElementId sid = sys.Id;
            var edit = new ModelEdit { Subject = VerifiedModelEdit.Safe(() => sys.UniqueId) ?? "circuit:" + Rid.Value(sid), Category = "ElectricalSystem" };
            edit.Before["members"] = string.Join(",", current.OrderBy(x => x));
            edit.Plan = new JObject { ["circuit_id"] = Rid.Value(sid), [add ? "add" : "remove"] = new JArray(ids.Select(Rid.Value)), ["members_after"] = new JArray(expected.OrderBy(x => x)) };
            edit.Apply = d =>
            {
                var set = new ElementSet();
                foreach (ElementId id in ids) set.Insert(d.GetElement(id));
                var s = (ElectricalSystem)d.GetElement(sid);
                bool ok = true;
                if (add) ok = s.AddToCircuit(set); else s.RemoveFromCircuit(set);
                if (!ok) throw new InvalidOperationException("Revit refused to " + (add ? "add the elements to" : "remove the elements from") + " the circuit");
            };
            edit.Verify = d =>
            {
                var check = new PostconditionCheck("members");
                var s = d.GetElement(sid) as ElectricalSystem;
                if (s == null) check.Unreadable("members", null, "the circuit does not re-read");
                else CheckMembers(check, s, expected);
                return check;
            };
            edit.Result = d => d.GetElement(sid) is ElectricalSystem s ? CircuitJson(s) : new JObject();
            return edit;
        }

        private static ModelEdit PlanSchedule(Document doc, JObject r, out string error)
        {
            FamilyInstance panel = Panel(doc, r, out error);
            if (panel == null) return null;
            ElementId templateId = null;
            if (r["template_id"] != null)
            {
                long t = r.Value<long>("template_id");
                templateId = Rid.CanRepresent(t) && doc.GetElement(Rid.Make(t)) is PanelScheduleTemplate ? Rid.Make(t) : null;
                if (templateId == null) { error = "template_id does not identify a panel schedule template."; return null; }
            }
            var existing = new FilteredElementCollector(doc).OfClass(typeof(PanelScheduleView)).Cast<PanelScheduleView>()
                .FirstOrDefault(v => SafeId(() => v.GetPanel()) == panel.Id);
            if (existing != null) { error = "panel " + Rid.Value(panel.Id) + " already has panel schedule view " + Rid.Value(existing.Id) + " ('" + existing.Name + "')."; return null; }
            ElementId pid = panel.Id, created = null;
            var edit = new ModelEdit { Subject = "panel_schedule:" + Rid.Value(pid), Category = "PanelScheduleView", Action = PlannedAction.Create };
            edit.Before["panel"] = Rid.Value(pid).ToString();
            edit.Before["template"] = templateId == null ? "<default>" : Rid.Value(templateId).ToString();
            edit.Plan = new JObject { ["panel_id"] = Rid.Value(pid), ["template_id"] = templateId == null ? null : (JToken)Rid.Value(templateId) };
            edit.Apply = d =>
            {
                PanelScheduleView v = templateId == null ? PanelScheduleView.CreateInstanceView(d, pid)
                                                         : PanelScheduleView.CreateInstanceView(d, templateId, pid);
                created = v?.Id ?? throw new InvalidOperationException("Revit created no panel schedule view");
            };
            edit.Verify = d =>
            {
                var check = new PostconditionCheck("panel");
                var v = created == null ? null : d.GetElement(created) as PanelScheduleView;
                if (v == null) check.Unreadable("panel", Rid.Value(pid), "no panel schedule view re-reads");
                else { try { check.Compare("panel", Rid.Value(pid), Rid.Value(v.GetPanel())); } catch (Exception ex) { check.Unreadable("panel", Rid.Value(pid), ex.Message); } }
                return check;
            };
            edit.Result = d => new JObject { ["panel_schedule_view_id"] = created == null ? null : (JToken)Rid.Value(created),
                                             ["name"] = created == null ? null : VerifiedModelEdit.Safe(() => d.GetElement(created)?.Name) };
            return edit;
        }

        // ================================================================== helpers

        private static List<ElementId> Ids(Document doc, JObject r, out string error)
        {
            error = null;
            JArray raw = r["element_ids"] as JArray;
            if (raw == null || raw.Count < 1 || raw.Count > 500) { error = "element_ids must hold 1..500 ids."; return null; }
            var ids = new List<ElementId>();
            foreach (JToken t in raw)
            {
                long v = t.Value<long>();
                if (!Rid.CanRepresent(v) || doc.GetElement(Rid.Make(v)) == null) { error = "element " + v + " does not exist."; return null; }
                if (ids.Any(x => Rid.Value(x) == v)) { error = "element " + v + " is repeated."; return null; }
                ids.Add(Rid.Make(v));
            }
            return ids;
        }

        private static FamilyInstance Panel(Document doc, JObject r, out string error)
        {
            error = null;
            long v = r.Value<long?>("panel_id") ?? -1;
            var fi = Rid.CanRepresent(v) ? doc.GetElement(Rid.Make(v)) as FamilyInstance : null;
            if (fi == null || !(fi.MEPModel is ElectricalEquipment)) { error = "panel_id must identify electrical equipment (operation=list_panels)."; return null; }
            return fi;
        }

        private static ElectricalSystem Circuit(Document doc, JObject r, out string error)
        {
            error = null;
            long v = r.Value<long?>("circuit_id") ?? -1;
            var s = Rid.CanRepresent(v) ? doc.GetElement(Rid.Make(v)) as ElectricalSystem : null;
            if (s == null) error = "circuit_id must identify an electrical circuit (operation=list_circuits).";
            return s;
        }

        private static void CheckMembers(PostconditionCheck check, ElectricalSystem s, IEnumerable<long> wanted)
        {
            var w = new JArray(wanted.OrderBy(x => x));
            try
            {
                var found = new JArray(Members(s).OrderBy(x => x));
                check.Record("members", w, found, JToken.DeepEquals(w, found));
            }
            catch (Exception ex) { check.Unreadable("members", w, ex.Message); }
        }

        private static void CheckPanel(PostconditionCheck check, ElectricalSystem s, ElementId panel)
        {
            try
            {
                ElementId now = s.BaseEquipment?.Id;
                if (now == null) check.Record("panel", Rid.Value(panel), null, false);
                else check.Compare("panel", Rid.Value(panel), Rid.Value(now));
            }
            catch (Exception ex) { check.Unreadable("panel", Rid.Value(panel), ex.Message); }
        }

        private static IEnumerable<long> Members(ElectricalSystem s)
        {
            var ids = new List<long>();
            foreach (Element e in s.Elements) ids.Add(Rid.Value(e.Id));
            return ids;
        }

        private static string CircuitsOf(Document doc, ElementId id)
        {
            try
            {
                var fi = doc.GetElement(id) as FamilyInstance;
                var systems = fi?.MEPModel?.GetElectricalSystems();
                return systems == null ? "" : string.Join(",", systems.Select(x => Rid.Value(x.Id)).OrderBy(x => x));
            }
            catch { return "?"; }
        }

        private static string Param(Element e, string bipName)
        {
            if (!Enum.TryParse(bipName, out BuiltInParameter bip)) return null;
            try
            {
                Parameter p = e.get_Parameter(bip);
                if (p == null || !p.HasValue) return null;
                return p.StorageType == StorageType.String ? p.AsString() : p.AsValueString();
            }
            catch { return null; }
        }

        private static ElementId ParamId(Element e, string bipName)
        {
            if (!Enum.TryParse(bipName, out BuiltInParameter bip)) return null;
            try { Parameter p = e.get_Parameter(bip); return p != null && p.StorageType == StorageType.ElementId ? p.AsElementId() : null; }
            catch { return null; }
        }

        private static JToken Conv(Func<double> f, ForgeTypeId unit)
        {
            try { return Math.Round(UnitUtils.ConvertFromInternalUnits(f(), unit), 4); }
            catch { return null; }
        }

        private static JToken Sum(List<ElectricalSystem> cs, Func<ElectricalSystem, double> f, ForgeTypeId unit)
        {
            try { return Math.Round(UnitUtils.ConvertFromInternalUnits(cs.Sum(f), unit), 4); }
            catch { return null; }
        }

        private static JToken N(Func<int> f) { try { return f(); } catch { return null; } }
        private static ElementId SafeId(Func<ElementId> f) { try { return f(); } catch { return null; } }
    }
}
