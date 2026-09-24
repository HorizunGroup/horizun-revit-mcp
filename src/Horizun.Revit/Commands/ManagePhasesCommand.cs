// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// horizun_manage_phases: phases, phase filters and design options.
//
// WHAT THE API ALLOWS, MEASURED against RevitAPI.dll/RevitAPI.xml 2023-2027 (the
// same answer in all five years):
//   * Phases can be READ in order (Document.Phases) and RENAMED (Element.Name). There
//     is no method that creates, inserts, deletes or reorders a phase: create_phase is
//     a typed refusal that says so.
//   * An element's CreatedPhaseId / DemolishedPhaseId are writable where
//     ArePhasesModifiable(); ElementOnPhaseStatus comes from Element.GetPhaseStatus.
//   * PhaseFilter.Create and Set/GetPhaseStatusPresentation cover filter authoring.
//   * Design options are READ-ONLY: Element.DesignOption has no setter, there is no
//     "add to set" / "move to option" / "set active option" call. assign_design_option
//     is a typed refusal that says so, and why the move matters (it changes what each
//     view shows). Python cannot do it either, so no fallback is offered.
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
    public sealed class ManagePhasesCommand : ICommand
    {
        public string Name => "horizun_manage_phases";
        public string Description => "Read phases, phase filters and design options; set element phases and author phase filters with rehearsal and re-read.";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            JObject request;
            try { request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JObject.Parse(paramsJson); }
            catch (Exception ex) { return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message); }
            string op = (request.Value<string>("operation") ?? "").Trim().ToLowerInvariant();

            if (op == "create_phase")
                return CommandResult.FailWithDetail(
                    "no_phase_creation_api: Revit's API (2023-2027, measured) has no call that creates, inserts, deletes or " +
                    "reorders a phase - Document.Phases is read-only and Phase has no Create. Add or reorder phases in Manage > " +
                    "Phases; rename_phase is available here. Nothing was written.",
                    new JObject { ["state"] = "refused", ["code"] = "no_phase_creation_api", ["write_started"] = false });
            if (op == "assign_design_option")
                return CommandResult.FailWithDetail(
                    "no_design_option_assignment_api: Element.DesignOption is read-only in Revit's API (2023-2027, measured); there " +
                    "is no call to add elements to an option set, move them between options or set the active option, so Python " +
                    "cannot do it either. Mind what the move means before doing it by hand (Manage > Design Options > Add to Set / " +
                    "Pick to Edit): an element in a secondary option disappears from every view that shows the primary option or " +
                    "the main model, and views pinned to an option start or stop showing it. Nothing was written.",
                    new JObject { ["state"] = "refused", ["code"] = "no_design_option_assignment_api", ["write_started"] = false });

            if (op == "list" || op == "element_status")
            {
                Document rdoc = app.ActiveUIDocument?.Document;
                if (rdoc == null) return CommandResult.Fail("No document is open.");
                CommandResult guard = DocumentGate.ReadGuard(rdoc, request, Name);
                if (guard != null) return guard;
                return op == "list" ? List(rdoc) : ElementStatus(rdoc, request);
            }
            if (op != "set_element_phases" && op != "create_phase_filter" && op != "edit_phase_filter" && op != "rename_phase")
                return CommandResult.Fail("operation must be list, element_status, set_element_phases, create_phase_filter, " +
                                          "edit_phase_filter, rename_phase, create_phase or assign_design_option.");

            GateResult gate = DocumentGate.ForMutation(app, request, Name); if (!gate.Ok) return gate.Refusal;
            Document doc = gate.Document;
            string error; StagedWrite w;
            switch (op)
            {
                case "set_element_phases": w = PlanSetPhases(app, gate, request, out error); break;
                case "rename_phase": w = PlanRename(app, gate, request, out error); break;
                default: w = PlanFilter(app, gate, request, op == "create_phase_filter", out error); break;
            }
            if (w == null) return CommandResult.FailWithDetail(error + " Nothing was written.", new JObject { ["state"] = "refused", ["write_started"] = false });
            string hash = DocumentGate.PlanHash(request, "operation", "element_ids", "created_phase_id", "demolished_phase_id",
                                                "phase_id", "filter_id", "name", "presentation");
            return StagedGroupWrite.Run(app, gate, request, Name, w, hash);
        }

        // ---- reads -------------------------------------------------------------------

        private static List<Phase> Phases(Document doc)
        {
            var list = new List<Phase>(); foreach (Phase p in doc.Phases) list.Add(p); return list;
        }

        private static string Wire(PhaseStatusPresentation p)
            => p == PhaseStatusPresentation.ShowByCategory ? "by_category" : p == PhaseStatusPresentation.ShowOverriden ? "overridden" : "hidden";

        private static PhaseStatusPresentation FromWire(string s)
            => s == "by_category" ? PhaseStatusPresentation.ShowByCategory : s == "overridden" ? PhaseStatusPresentation.ShowOverriden : PhaseStatusPresentation.DontShow;

        private static ElementOnPhaseStatus Status(string s)
            => s == "new" ? ElementOnPhaseStatus.New : s == "existing" ? ElementOnPhaseStatus.Existing
             : s == "demolished" ? ElementOnPhaseStatus.Demolished : ElementOnPhaseStatus.Temporary;

        private static JObject Presentation(PhaseFilter f)
        {
            var o = new JObject();
            foreach (string s in PhaseRules.FilterStatuses)
            {
                try { o[s] = Wire(f.GetPhaseStatusPresentation(Status(s))); }
                catch (Exception ex) { o[s] = "unreadable: " + ex.Message; }
            }
            return o;
        }

        private static CommandResult List(Document doc)
        {
            var phases = new JArray(); int i = 0;
            foreach (Phase p in Phases(doc)) phases.Add(new JObject { ["index"] = i++, ["id"] = Rid.Value(p.Id), ["name"] = p.Name });
            var filters = new JArray();
            foreach (PhaseFilter f in new FilteredElementCollector(doc).OfClass(typeof(PhaseFilter)).Cast<PhaseFilter>().OrderBy(x => x.Name, StringComparer.Ordinal))
                filters.Add(new JObject { ["id"] = Rid.Value(f.Id), ["name"] = f.Name, ["presentation"] = Presentation(f) });

            var options = new JArray();
            foreach (DesignOption o in new FilteredElementCollector(doc).OfClass(typeof(DesignOption)).Cast<DesignOption>())
            {
                ElementId setId = ElementId.InvalidElementId;
                try { setId = o.get_Parameter(BuiltInParameter.OPTION_SET_ID)?.AsElementId() ?? ElementId.InvalidElementId; } catch { }
                List<ElementId> members;
                try
                {
                    members = new FilteredElementCollector(doc).WherePasses(new ElementDesignOptionFilter(o.Id))
                        .WhereElementIsNotElementType().ToElementIds().OrderBy(Rid.Value).ToList();
                }
                catch { members = null; }
                options.Add(new JObject
                {
                    ["id"] = Rid.Value(o.Id), ["name"] = o.Name, ["is_primary"] = o.IsPrimary,
                    ["option_set_id"] = setId == ElementId.InvalidElementId ? (JToken)JValue.CreateNull() : Rid.Value(setId),
                    ["option_set_name"] = setId == ElementId.InvalidElementId ? null : doc.GetElement(setId)?.Name,
                    ["element_count"] = members == null ? (JToken)"unreadable" : members.Count,
                    ["element_ids"] = members == null ? new JArray() : StagedGroupWrite.Json(members.Take(100))
                });
            }
            ElementId active = ElementId.InvalidElementId;
            try { active = DesignOption.GetActiveDesignOptionId(doc); } catch { }
            var result = new JObject
            {
                ["read_only"] = true, ["document"] = doc.Title,
                ["phases"] = phases, ["phase_filters"] = filters,
                ["design_options"] = options,
                ["active_design_option_id"] = active == ElementId.InvalidElementId ? (JToken)JValue.CreateNull() : Rid.Value(active),
                ["design_options_writable"] = false,
                ["notes"] = new JArray(
                    "Phases are listed in Revit's order; the API cannot create or reorder them.",
                    "element_ids per option are the first 100; element_count is the full count.")
            };
            if (options.Count == 0) result["design_options_note"] = "This document has no design options.";
            return CommandResult.Ok(result);
        }

        private static CommandResult ElementStatus(Document doc, JObject request)
        {
            string error; List<ElementId> ids = StagedGroupWrite.Ids(request, "element_ids", 500, out error);
            if (ids == null) return CommandResult.Fail(error);
            List<Phase> phases = Phases(doc);
            if (phases.Count == 0) return CommandResult.Fail("The document has no phases.");
            Phase phase = phases[phases.Count - 1];
            if (request["phase_id"] != null)
            {
                long pid = request.Value<long>("phase_id");
                phase = phases.FirstOrDefault(p => Rid.Value(p.Id) == pid);
                if (phase == null) return CommandResult.Fail("phase_id " + pid + " is not a phase of this document.");
            }
            var rows = new JArray();
            foreach (ElementId id in ids)
            {
                Element e = doc.GetElement(id);
                if (e == null) { rows.Add(new JObject { ["id"] = Rid.Value(id), ["error"] = "no element with this id" }); continue; }
                var row = new JObject { ["id"] = Rid.Value(id), ["category"] = e.Category?.Name };
                try
                {
                    bool has = e.HasPhases();
                    row["has_phases"] = has;
                    if (has)
                    {
                        row["created_phase_id"] = Rid.Value(e.CreatedPhaseId);
                        row["demolished_phase_id"] = Rid.Value(e.DemolishedPhaseId);
                        row["status"] = e.GetPhaseStatus(phase.Id).ToString().ToLowerInvariant();
                        row["phases_modifiable"] = e.ArePhasesModifiable();
                    }
                }
                catch (Exception ex) { row["phase_error"] = ex.Message; }
                DesignOption opt = null; try { opt = e.DesignOption; } catch { }
                row["design_option"] = opt == null ? (JToken)"main_model" : new JObject
                {
                    ["id"] = Rid.Value(opt.Id), ["name"] = opt.Name, ["is_primary"] = opt.IsPrimary,
                    ["option_set_id"] = Rid.Value(opt.get_Parameter(BuiltInParameter.OPTION_SET_ID)?.AsElementId() ?? ElementId.InvalidElementId)
                };
                rows.Add(row);
            }
            return CommandResult.Ok(new JObject
            {
                ["read_only"] = true, ["phase_id"] = Rid.Value(phase.Id), ["phase_name"] = phase.Name, ["rows"] = rows
            });
        }

        // ---- writes ------------------------------------------------------------------

        private static StagedWrite PlanSetPhases(UIApplication app, GateResult gate, JObject request, out string error)
        {
            Document doc = gate.Document;
            List<ElementId> ids = StagedGroupWrite.Ids(request, "element_ids", 500, out error);
            if (ids == null) return null;
            bool hasC = request["created_phase_id"] != null, hasD = request["demolished_phase_id"] != null;
            if (!hasC && !hasD) { error = "set_element_phases needs created_phase_id and/or demolished_phase_id (-1 clears demolition)."; return null; }
            List<Phase> phases = Phases(doc);
            Func<long, int> index = v => phases.FindIndex(p => Rid.Value(p.Id) == v);
            long reqC = hasC ? request.Value<long>("created_phase_id") : 0, reqD = hasD ? request.Value<long>("demolished_phase_id") : 0;
            if (hasC && index(reqC) < 0) { error = "created_phase_id " + reqC + " is not a phase of this document."; return null; }
            if (hasD && reqD != -1 && index(reqD) < 0) { error = "demolished_phase_id " + reqD + " is not a phase of this document (use -1 for none)."; return null; }

            var targets = new List<Element>();
            ResolvedPlan resolved = StagedGroupWrite.NewResolved("horizun_manage_phases", gate, app);
            var planRows = new JArray();
            foreach (ElementId id in ids)
            {
                Element e = doc.GetElement(id);
                if (e == null) { error = "element " + Rid.Value(id) + " does not exist."; return null; }
                bool ok; try { ok = e.HasPhases() && e.ArePhasesModifiable(); } catch { ok = false; }
                if (!ok) { error = "element " + Rid.Value(id) + " has no modifiable phases (HasPhases/ArePhasesModifiable is false)."; return null; }
                long curC = Rid.Value(e.CreatedPhaseId), curD = Rid.Value(e.DemolishedPhaseId);
                long finC = hasC ? reqC : curC, finD = hasD ? reqD : curD;
                string order = PhaseRules.OrderError(index(finC), finD == -1 ? -1 : index(finD));
                if (order != null) { error = "element " + Rid.Value(id) + ": " + order + "."; return null; }
                targets.Add(e);
                resolved.Elements.Add(new PlannedElement
                {
                    UniqueId = e.UniqueId, ElementId = Rid.Value(id), Category = e.Category?.Name, Action = PlannedAction.Modify,
                    BeforeValues = new Dictionary<string, string> { ["created"] = curC.ToString(), ["demolished"] = curD.ToString() },
                    ProposedValues = new Dictionary<string, string> { ["created"] = finC.ToString(), ["demolished"] = finD.ToString() }
                });
                planRows.Add(new JObject { ["id"] = Rid.Value(id), ["created_before"] = curC, ["demolished_before"] = curD, ["created_after"] = finC, ["demolished_after"] = finD });
            }
            var required = new List<string>();
            foreach (Element e in targets)
            {
                if (hasC) required.Add("e" + Rid.Value(e.Id) + ".created_phase_id");
                if (hasD) required.Add("e" + Rid.Value(e.Id) + ".demolished_phase_id");
            }
            return new StagedWrite
            {
                Operation = "set_element_phases", Resolved = resolved, Count = targets.Count,
                Plan = new JObject { ["elements"] = planRows },
                Apply = d => StagedGroupWrite.Tx(d, "Horizun: set element phases", () =>
                {
                    foreach (Element e in targets)
                    {
                        // Clear the demolition first, so a creation moved later than the
                        // current demolition is never momentarily out of order.
                        if (hasD) e.DemolishedPhaseId = ElementId.InvalidElementId;
                        if (hasC) e.CreatedPhaseId = Rid.Make(reqC);
                        if (hasD && reqD != -1) e.DemolishedPhaseId = Rid.Make(reqD);
                    }
                }),
                Verify = d =>
                {
                    var check = new PostconditionCheck(required.ToArray());
                    foreach (Element t in targets)
                    {
                        string k = "e" + Rid.Value(t.Id);
                        Element e = d.GetElement(t.Id);
                        if (e == null)
                        {
                            if (hasC) check.Unreadable(k + ".created_phase_id", reqC, "element no longer exists");
                            if (hasD) check.Unreadable(k + ".demolished_phase_id", reqD, "element no longer exists");
                            continue;
                        }
                        if (hasC) check.Compare(k + ".created_phase_id", reqC, Rid.Value(e.CreatedPhaseId));
                        if (hasD) check.Compare(k + ".demolished_phase_id", reqD, Rid.Value(e.DemolishedPhaseId));
                    }
                    return check;
                },
                Result = () => new JObject { ["element_ids"] = StagedGroupWrite.Json(targets.Select(t => t.Id)) },
                Warning = "Changing phases changes which views show these elements and how (each view's phase and phase filter decide)."
            };
        }

        private static StagedWrite PlanRename(UIApplication app, GateResult gate, JObject request, out string error)
        {
            error = null; Document doc = gate.Document;
            long pid = request.Value<long?>("phase_id") ?? -1;
            Phase phase = Phases(doc).FirstOrDefault(p => Rid.Value(p.Id) == pid);
            if (phase == null) { error = "phase_id " + pid + " is not a phase of this document."; return null; }
            string name = request.Value<string>("name");
            string nameError = PhaseRules.NameError(name, Phases(doc).Select(p => p.Name), phase.Name);
            if (nameError != null) { error = "name: " + nameError + "."; return null; }
            string before = phase.Name;
            ResolvedPlan resolved = StagedGroupWrite.NewResolved("horizun_manage_phases", gate, app);
            resolved.Elements.Add(new PlannedElement { UniqueId = phase.UniqueId, ElementId = pid, Category = "phase", Action = PlannedAction.Modify,
                BeforeValues = new Dictionary<string, string> { ["name"] = before }, ProposedValues = new Dictionary<string, string> { ["name"] = name } });
            ElementId id = phase.Id;
            return new StagedWrite
            {
                Operation = "rename_phase", Resolved = resolved,
                Plan = new JObject { ["phase_id"] = pid, ["name_before"] = before, ["name_after"] = name },
                Apply = d => StagedGroupWrite.Tx(d, "Horizun: rename phase", () => { d.GetElement(id).Name = name; }),
                Verify = d =>
                {
                    var check = new PostconditionCheck("phase.name");
                    Element p = d.GetElement(id);
                    if (p == null) check.Unreadable("phase.name", name, "phase no longer exists"); else check.Compare("phase.name", name, p.Name);
                    return check;
                },
                Result = () => new JObject { ["phase_id"] = pid, ["name"] = name }
            };
        }

        private static StagedWrite PlanFilter(UIApplication app, GateResult gate, JObject request, bool create, out string error)
        {
            error = null; Document doc = gate.Document;
            string presError = PhaseRules.PresentationError(request["presentation"]);
            if (presError != null) { error = presError + "."; return null; }
            var pres = new Dictionary<string, string>();
            JObject presObj = request["presentation"] as JObject;
            if (presObj != null) foreach (JProperty p in presObj.Properties()) pres[p.Name] = (string)p.Value;
            List<PhaseFilter> all = new FilteredElementCollector(doc).OfClass(typeof(PhaseFilter)).Cast<PhaseFilter>().ToList();
            string name = request.Value<string>("name");
            PhaseFilter existing = null;
            if (create)
            {
                if (request["filter_id"] != null) { error = "create_phase_filter takes no filter_id."; return null; }
                string nameError = PhaseRules.NameError(name, all.Select(f => f.Name), null);
                if (nameError != null) { error = "name: " + nameError + "."; return null; }
            }
            else
            {
                long fid = request.Value<long?>("filter_id") ?? -1;
                existing = all.FirstOrDefault(f => Rid.Value(f.Id) == fid);
                if (existing == null) { error = "filter_id " + fid + " is not a phase filter of this document."; return null; }
                if (name == null && pres.Count == 0) { error = "edit_phase_filter changes nothing: pass name and/or presentation."; return null; }
                if (name != null)
                {
                    string nameError = PhaseRules.NameError(name, all.Select(f => f.Name), existing.Name);
                    if (nameError != null) { error = "name: " + nameError + "."; return null; }
                }
            }
            ResolvedPlan resolved = StagedGroupWrite.NewResolved("horizun_manage_phases", gate, app);
            JObject before = existing == null ? null : Presentation(existing);
            resolved.Elements.Add(new PlannedElement
            {
                UniqueId = existing == null ? "new-phase-filter:" + name : existing.UniqueId, Category = "phase_filter",
                Action = existing == null ? PlannedAction.Create : PlannedAction.Modify,
                BeforeValues = existing == null ? null : new Dictionary<string, string> { ["name"] = existing.Name, ["presentation"] = before.ToString(Newtonsoft.Json.Formatting.None) }
            });
            ElementId filterId = existing?.Id;
            var required = new List<string>();
            if (create || name != null) required.Add("filter.name");
            foreach (string s in pres.Keys) required.Add("filter.presentation." + s);
            string op = create ? "create_phase_filter" : "edit_phase_filter";
            return new StagedWrite
            {
                Operation = op, Resolved = resolved,
                Plan = new JObject { ["filter_id"] = existing == null ? (JToken)JValue.CreateNull() : Rid.Value(existing.Id), ["name"] = name ?? existing?.Name,
                                     ["presentation_before"] = before, ["presentation_requested"] = presObj },
                Apply = d => StagedGroupWrite.Tx(d, "Horizun: " + op.Replace('_', ' '), () =>
                {
                    PhaseFilter f = create ? PhaseFilter.Create(d, name) : (PhaseFilter)d.GetElement(existing.Id);
                    filterId = f.Id;
                    if (!create && name != null) f.Name = name;
                    foreach (var kv in pres) f.SetPhaseStatusPresentation(Status(kv.Key), FromWire(kv.Value));
                }),
                Verify = d =>
                {
                    var check = new PostconditionCheck(required.ToArray());
                    PhaseFilter f = filterId == null ? null : d.GetElement(filterId) as PhaseFilter;
                    if (f == null) { foreach (string r in required) check.Unreadable(r, null, "the phase filter does not re-read"); return check; }
                    if (create || name != null) check.Compare("filter.name", name, f.Name);
                    foreach (var kv in pres)
                    {
                        try { check.Compare("filter.presentation." + kv.Key, kv.Value, Wire(f.GetPhaseStatusPresentation(Status(kv.Key)))); }
                        catch (Exception ex) { check.Unreadable("filter.presentation." + kv.Key, kv.Value, ex.Message); }
                    }
                    return check;
                },
                Result = () =>
                {
                    PhaseFilter f = filterId == null ? null : doc.GetElement(filterId) as PhaseFilter;
                    return new JObject { ["filter_id"] = filterId == null ? (JToken)JValue.CreateNull() : Rid.Value(filterId), ["name"] = f?.Name,
                                         ["presentation"] = f == null ? null : Presentation(f) };
                }
            };
        }
    }
}
