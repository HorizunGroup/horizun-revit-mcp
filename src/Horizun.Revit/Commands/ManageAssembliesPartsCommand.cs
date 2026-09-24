// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// horizun_manage_assemblies_parts: Parts (PartUtils) and Assemblies
// (AssemblyInstance, AssemblyViewUtils). Every write is one StagedWrite: rehearsed
// inside a TransactionGroup that is rolled back, applied with a token, and re-read
// from the committed model - part associations, exclusion flags, assembly members,
// the naming category, the views' owning assembly.
//
// API facts this relies on (RevitAPI.xml, identical 2023-2027):
//   * PartUtils.CreateParts creates the parts at regeneration; the transaction helper
//     regenerates before committing.
//   * PartUtils.DivideParts requires a valid SketchPlane even with no curves, and
//     accepts only levels, grids and reference planes as intersecting references.
//   * Dissolving parts is deleting their PartMaker; nothing else in the API does it.
//   * An assembly's type name can be set only after the transaction that created the
//     instance has committed, so create_assembly uses two inner transactions.
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
    public sealed class ManageAssembliesPartsCommand : ICommand
    {
        public string Name => "horizun_manage_assemblies_parts";
        public string Description => "Create, divide, exclude and dissolve parts; create assemblies, their views and disassemble them, rehearsed and re-read.";

        private static readonly string[] Writes =
            { "create_parts", "divide_parts", "exclude_parts", "restore_parts", "dissolve_parts", "create_assembly", "assembly_views", "disassemble" };

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            JObject request;
            try { request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JObject.Parse(paramsJson); }
            catch (Exception ex) { return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message); }
            string op = (request.Value<string>("operation") ?? "").Trim().ToLowerInvariant();
            if (op == "list")
            {
                Document rdoc = app.ActiveUIDocument?.Document;
                if (rdoc == null) return CommandResult.Fail("No document is open.");
                CommandResult guard = DocumentGate.ReadGuard(rdoc, request, Name);
                return guard ?? List(rdoc, request);
            }
            if (!Writes.Contains(op)) return CommandResult.Fail("operation must be list or one of " + string.Join(", ", Writes) + ".");

            GateResult gate = DocumentGate.ForMutation(app, request, Name); if (!gate.Ok) return gate.Refusal;
            string error; StagedWrite w;
            switch (op)
            {
                case "create_parts": w = PlanCreateParts(app, gate, request, out error); break;
                case "divide_parts": w = PlanDivide(app, gate, request, out error); break;
                case "exclude_parts": w = PlanExclude(app, gate, request, true, out error); break;
                case "restore_parts": w = PlanExclude(app, gate, request, false, out error); break;
                case "dissolve_parts": w = PlanDissolve(app, gate, request, out error); break;
                case "create_assembly": w = PlanAssembly(app, gate, request, out error); break;
                case "assembly_views": w = PlanViews(app, gate, request, out error); break;
                default: w = PlanDisassemble(app, gate, request, out error); break;
            }
            if (w == null) return CommandResult.FailWithDetail(error + " Nothing was written.", new JObject { ["state"] = "refused", ["write_started"] = false });
            string hash = DocumentGate.PlanHash(request, "operation", "element_ids", "reference_ids", "assembly_id", "naming_category_id", "name", "views");
            return StagedGroupWrite.Run(app, gate, request, Name, w, hash);
        }

        // ---- reads -------------------------------------------------------------------

        private static List<ElementId> PartsOf(Document doc, ElementId source)
        {
            try { return PartUtils.GetAssociatedParts(doc, source, false, true).OrderBy(Rid.Value).ToList(); }
            catch { return new List<ElementId>(); }
        }

        private static JObject PartJson(Part p)
        {
            var src = new JArray();
            try { foreach (LinkElementId l in p.GetSourceElementIds()) src.Add(Rid.Value(l.HostElementId)); } catch { }
            return new JObject { ["id"] = Rid.Value(p.Id), ["excluded"] = p.Excluded, ["source_ids"] = src,
                                 ["original_category_id"] = Rid.Value(p.OriginalCategoryId) };
        }

        private static JObject AssemblyJson(Document doc, AssemblyInstance a)
            => new JObject
            {
                ["id"] = Rid.Value(a.Id), ["name"] = a.AssemblyTypeName,
                ["naming_category"] = Category.GetCategory(doc, a.NamingCategoryId)?.Name,
                ["member_ids"] = StagedGroupWrite.Json(a.GetMemberIds().OrderBy(Rid.Value))
            };

        private static CommandResult List(Document doc, JObject request)
        {
            var result = new JObject { ["read_only"] = true, ["document"] = doc.Title };
            if (request["element_ids"] != null)
            {
                string error; List<ElementId> ids = StagedGroupWrite.Ids(request, "element_ids", 500, out error);
                if (ids == null) return CommandResult.Fail(error);
                var rows = new JArray();
                foreach (ElementId id in ids)
                {
                    Element e = doc.GetElement(id);
                    if (e == null) { rows.Add(new JObject { ["id"] = Rid.Value(id), ["error"] = "no element with this id" }); continue; }
                    bool valid = false; try { valid = PartUtils.AreElementsValidForCreateParts(doc, new List<ElementId> { id }); } catch { }
                    rows.Add(new JObject
                    {
                        ["id"] = Rid.Value(id), ["category"] = e.Category?.Name, ["is_part"] = e is Part,
                        ["valid_for_create_parts"] = valid, ["part_ids"] = StagedGroupWrite.Json(PartsOf(doc, id)),
                        ["assembly_id"] = e.AssemblyInstanceId == ElementId.InvalidElementId ? (JToken)JValue.CreateNull() : Rid.Value(e.AssemblyInstanceId)
                    });
                }
                result["elements"] = rows;
            }
            List<Part> parts = new FilteredElementCollector(doc).OfClass(typeof(Part)).Cast<Part>().OrderBy(p => Rid.Value(p.Id)).ToList();
            result["part_count"] = parts.Count;
            result["parts"] = new JArray(parts.Take(500).Select(PartJson));
            List<AssemblyInstance> asm = new FilteredElementCollector(doc).OfClass(typeof(AssemblyInstance)).Cast<AssemblyInstance>().OrderBy(a => Rid.Value(a.Id)).ToList();
            result["assembly_count"] = asm.Count;
            result["assemblies"] = new JArray(asm.Take(200).Select(a => AssemblyJson(doc, a)));
            return CommandResult.Ok(result);
        }

        // ---- parts -------------------------------------------------------------------

        private static StagedWrite PlanCreateParts(UIApplication app, GateResult gate, JObject request, out string error)
        {
            Document doc = gate.Document;
            List<ElementId> ids = StagedGroupWrite.Ids(request, "element_ids", 200, out error);
            if (ids == null) return null;
            ResolvedPlan resolved = StagedGroupWrite.NewResolved("horizun_manage_assemblies_parts", gate, app);
            foreach (ElementId id in ids)
            {
                Element e = doc.GetElement(id);
                if (e == null) { error = "element " + Rid.Value(id) + " does not exist."; return null; }
                if (PartUtils.HasAssociatedParts(doc, id)) { error = "element " + Rid.Value(id) + " already has parts; dissolve_parts first or divide the existing parts."; return null; }
                if (!PartUtils.AreElementsValidForCreateParts(doc, new List<ElementId> { id }))
                { error = "element " + Rid.Value(id) + " (" + e.Category?.Name + ") is not valid for parts (PartUtils.AreElementsValidForCreateParts is false)."; return null; }
                resolved.Elements.Add(new PlannedElement { UniqueId = e.UniqueId, ElementId = Rid.Value(id), Category = e.Category?.Name, Action = PlannedAction.Create });
            }
            if (!PartUtils.AreElementsValidForCreateParts(doc, ids)) { error = "the elements are valid one by one but not together (PartUtils.AreElementsValidForCreateParts)."; return null; }
            string[] required = ids.Select(i => "e" + Rid.Value(i) + ".has_parts").ToArray();
            return new StagedWrite
            {
                Operation = "create_parts", Resolved = resolved, Count = ids.Count,
                Plan = new JObject { ["source_ids"] = StagedGroupWrite.Json(ids) },
                Apply = d => StagedGroupWrite.Tx(d, "Horizun: create parts", () => PartUtils.CreateParts(d, ids)),
                Verify = d =>
                {
                    var check = new PostconditionCheck(required);
                    foreach (ElementId id in ids)
                        check.Record("e" + Rid.Value(id) + ".has_parts", true, PartsOf(d, id).Count, PartsOf(d, id).Count > 0);
                    return check;
                },
                Result = () => new JObject { ["parts"] = new JArray(ids.Select(i => new JObject { ["source_id"] = Rid.Value(i), ["part_ids"] = StagedGroupWrite.Json(PartsOf(doc, i)) })) }
            };
        }

        private static StagedWrite PlanDivide(UIApplication app, GateResult gate, JObject request, out string error)
        {
            Document doc = gate.Document;
            List<ElementId> ids = StagedGroupWrite.Ids(request, "element_ids", 200, out error);
            if (ids == null) return null;
            List<ElementId> refs = StagedGroupWrite.Ids(request, "reference_ids", 50, out error);
            if (refs == null) return null;
            ResolvedPlan resolved = StagedGroupWrite.NewResolved("horizun_manage_assemblies_parts", gate, app);
            foreach (ElementId id in ids)
            {
                Part p = doc.GetElement(id) as Part;
                if (p == null) { error = "element " + Rid.Value(id) + " is not a part; divide_parts divides parts (create_parts first)."; return null; }
                resolved.Elements.Add(new PlannedElement { UniqueId = p.UniqueId, ElementId = Rid.Value(id), Category = "part", Action = PlannedAction.Modify });
            }
            foreach (ElementId r in refs)
            {
                Element e = doc.GetElement(r);
                if (!(e is Level) && !(e is Grid) && !(e is ReferencePlane))
                { error = "reference " + Rid.Value(r) + " is not a level, grid or reference plane - the only references DivideParts accepts."; return null; }
                resolved.Elements.Add(new PlannedElement { UniqueId = e.UniqueId, ElementId = Rid.Value(r), Category = "divider", Action = PlannedAction.Read });
            }
            if (!PartUtils.ArePartsValidForDivide(doc, ids)) { error = "PartUtils.ArePartsValidForDivide is false: a part is already divided or too far from its original."; return null; }
            string[] required = ids.Select(i => "p" + Rid.Value(i) + ".divided").ToArray();
            ElementId makerId = null;
            return new StagedWrite
            {
                Operation = "divide_parts", Resolved = resolved, Count = ids.Count,
                Plan = new JObject { ["part_ids"] = StagedGroupWrite.Json(ids), ["reference_ids"] = StagedGroupWrite.Json(refs) },
                Apply = d => StagedGroupWrite.Tx(d, "Horizun: divide parts", () =>
                {
                    // DivideParts demands a SketchPlane even when no curves are drawn; a
                    // horizontal plane at the origin carries none of the division.
                    SketchPlane sp = SketchPlane.Create(d, Plane.CreateByNormalAndOrigin(XYZ.BasisZ, XYZ.Zero));
                    PartMaker maker = PartUtils.DivideParts(d, ids, refs, new List<Curve>(), sp.Id);
                    makerId = maker?.Id;
                }),
                Verify = d =>
                {
                    var check = new PostconditionCheck(required);
                    foreach (ElementId id in ids)
                    {
                        int n = PartsOf(d, id).Count;
                        check.Record("p" + Rid.Value(id) + ".divided", ">= 2 parts", n, makerId != null && d.GetElement(makerId) != null && n >= 2);
                    }
                    return check;
                },
                Result = () => new JObject { ["part_maker_id"] = makerId == null ? (JToken)JValue.CreateNull() : Rid.Value(makerId),
                    ["divided"] = new JArray(ids.Select(i => new JObject { ["part_id"] = Rid.Value(i), ["sub_part_ids"] = StagedGroupWrite.Json(PartsOf(doc, i)) })) }
            };
        }

        private static StagedWrite PlanExclude(UIApplication app, GateResult gate, JObject request, bool exclude, out string error)
        {
            Document doc = gate.Document;
            List<ElementId> ids = StagedGroupWrite.Ids(request, "element_ids", 500, out error);
            if (ids == null) return null;
            ResolvedPlan resolved = StagedGroupWrite.NewResolved("horizun_manage_assemblies_parts", gate, app);
            foreach (ElementId id in ids)
            {
                Part p = doc.GetElement(id) as Part;
                if (p == null) { error = "element " + Rid.Value(id) + " is not a part."; return null; }
                if (p.Excluded == exclude) { error = "part " + Rid.Value(id) + " is already " + (exclude ? "excluded" : "included") + "; nothing to change."; return null; }
                resolved.Elements.Add(new PlannedElement { UniqueId = p.UniqueId, ElementId = Rid.Value(id), Category = "part", Action = PlannedAction.Modify,
                    BeforeValues = new Dictionary<string, string> { ["excluded"] = p.Excluded.ToString() } });
            }
            string op = exclude ? "exclude_parts" : "restore_parts";
            string[] required = ids.Select(i => "p" + Rid.Value(i) + ".excluded").ToArray();
            return new StagedWrite
            {
                Operation = op, Resolved = resolved, Count = ids.Count,
                Plan = new JObject { ["part_ids"] = StagedGroupWrite.Json(ids), ["excluded"] = exclude },
                Apply = d => StagedGroupWrite.Tx(d, "Horizun: " + op.Replace('_', ' '), () =>
                {
                    foreach (ElementId id in ids) ((Part)d.GetElement(id)).Excluded = exclude;
                }),
                Verify = d =>
                {
                    var check = new PostconditionCheck(required);
                    foreach (ElementId id in ids)
                    {
                        Part p = d.GetElement(id) as Part;
                        if (p == null) check.Unreadable("p" + Rid.Value(id) + ".excluded", exclude, "part no longer exists");
                        else check.Compare("p" + Rid.Value(id) + ".excluded", exclude, p.Excluded);
                    }
                    return check;
                },
                Result = () => new JObject { ["part_ids"] = StagedGroupWrite.Json(ids), ["excluded"] = exclude }
            };
        }

        private static StagedWrite PlanDissolve(UIApplication app, GateResult gate, JObject request, out string error)
        {
            Document doc = gate.Document;
            List<ElementId> ids = StagedGroupWrite.Ids(request, "element_ids", 200, out error);
            if (ids == null) return null;
            ResolvedPlan resolved = StagedGroupWrite.NewResolved("horizun_manage_assemblies_parts", gate, app);
            var planRows = new JArray();
            foreach (ElementId id in ids)
            {
                Element e = doc.GetElement(id);
                if (e == null) { error = "element " + Rid.Value(id) + " does not exist."; return null; }
                if (e is Part) { error = "element " + Rid.Value(id) + " is a part; name the ORIGINAL element whose parts should be dissolved."; return null; }
                PartMaker maker = PartUtils.HasAssociatedParts(doc, id) ? PartUtils.GetAssociatedPartMaker(doc, id) : null;
                if (maker == null) { error = "element " + Rid.Value(id) + " has no parts to dissolve."; return null; }
                List<ElementId> parts = PartsOf(doc, id);
                resolved.Elements.Add(new PlannedElement { UniqueId = e.UniqueId, ElementId = Rid.Value(id), Category = e.Category?.Name, Action = PlannedAction.Delete,
                    BeforeValues = new Dictionary<string, string> { ["parts"] = string.Join(",", parts.Select(Rid.Value)) } });
                planRows.Add(new JObject { ["source_id"] = Rid.Value(id), ["part_maker_id"] = Rid.Value(maker.Id), ["part_ids"] = StagedGroupWrite.Json(parts) });
            }
            var required = new List<string>();
            foreach (ElementId id in ids) { required.Add("e" + Rid.Value(id) + ".has_parts"); required.Add("e" + Rid.Value(id) + ".exists"); }
            return new StagedWrite
            {
                Operation = "dissolve_parts", Resolved = resolved, Count = ids.Count,
                Plan = new JObject { ["dissolve"] = planRows },
                Warning = "Dissolving removes the parts and every edit made to them (divisions, exclusions, part parameters); the original elements stay.",
                Apply = d => StagedGroupWrite.Tx(d, "Horizun: dissolve parts", () =>
                {
                    var makers = new List<ElementId>();
                    foreach (ElementId id in ids)
                    {
                        PartMaker m = PartUtils.GetAssociatedPartMaker(d, id);
                        if (m != null && !makers.Contains(m.Id)) makers.Add(m.Id);
                    }
                    d.Delete(makers);
                }),
                Verify = d =>
                {
                    var check = new PostconditionCheck(required.ToArray());
                    foreach (ElementId id in ids)
                    {
                        check.Compare("e" + Rid.Value(id) + ".has_parts", false, PartUtils.HasAssociatedParts(d, id));
                        check.Compare("e" + Rid.Value(id) + ".exists", true, d.GetElement(id) != null);
                    }
                    return check;
                },
                Result = () => new JObject { ["source_ids"] = StagedGroupWrite.Json(ids), ["dissolved"] = planRows }
            };
        }

        // ---- assemblies --------------------------------------------------------------

        private static StagedWrite PlanAssembly(UIApplication app, GateResult gate, JObject request, out string error)
        {
            Document doc = gate.Document;
            List<ElementId> ids = StagedGroupWrite.Ids(request, "element_ids", 500, out error);
            if (ids == null) return null;
            ResolvedPlan resolved = StagedGroupWrite.NewResolved("horizun_manage_assemblies_parts", gate, app);
            foreach (ElementId id in ids)
            {
                Element e = doc.GetElement(id);
                if (e == null) { error = "element " + Rid.Value(id) + " does not exist."; return null; }
                if (e.AssemblyInstanceId != ElementId.InvalidElementId) { error = "element " + Rid.Value(id) + " already belongs to assembly " + Rid.Value(e.AssemblyInstanceId) + "."; return null; }
                resolved.Elements.Add(new PlannedElement { UniqueId = e.UniqueId, ElementId = Rid.Value(id), Category = e.Category?.Name, Action = PlannedAction.Modify });
            }
            ElementId cat;
            if (request["naming_category_id"] != null)
            {
                long c = request.Value<long>("naming_category_id");
                if (!Rid.CanRepresent(c)) { error = Rid.RangeError(c); return null; }
                cat = Rid.Make(c);
            }
            else cat = doc.GetElement(ids[0]).Category?.Id ?? ElementId.InvalidElementId;
            bool validCat = false; try { validCat = AssemblyInstance.IsValidNamingCategory(doc, cat, ids); } catch { }
            if (!validCat) { error = "naming category " + Rid.Value(cat) + " is not valid for these members (AssemblyInstance.IsValidNamingCategory); pass naming_category_id of one member's category."; return null; }
            string name = request.Value<string>("name");
            if (name != null)
            {
                IEnumerable<string> taken = new FilteredElementCollector(doc).OfClass(typeof(AssemblyType)).Select(t => t.Name);
                string nameError = PhaseRules.NameError(name, taken, null);
                if (nameError != null) { error = "name: " + nameError + "."; return null; }
            }
            var sorted = ids.Select(Rid.Value).OrderBy(v => v).ToList();
            var required = new List<string> { "assembly.members", "assembly.naming_category" };
            if (name != null) required.Add("assembly.name");
            ElementId created = null;
            return new StagedWrite
            {
                Operation = "create_assembly", Resolved = resolved, Count = 1,
                Plan = new JObject { ["member_ids"] = new JArray(sorted), ["naming_category_id"] = Rid.Value(cat), ["name"] = name },
                Apply = d =>
                {
                    StagedGroupWrite.Tx(d, "Horizun: create assembly", () => { created = AssemblyInstance.Create(d, ids, cat).Id; });
                    // The type name can be set only once the creating transaction has committed.
                    if (name != null) StagedGroupWrite.Tx(d, "Horizun: name assembly", () => { ((AssemblyInstance)d.GetElement(created)).AssemblyTypeName = name; });
                },
                Verify = d =>
                {
                    var check = new PostconditionCheck(required.ToArray());
                    AssemblyInstance a = created == null ? null : d.GetElement(created) as AssemblyInstance;
                    if (a == null) { foreach (string r in required) check.Unreadable(r, null, "the assembly does not re-read"); return check; }
                    var members = a.GetMemberIds().Select(Rid.Value).OrderBy(v => v).ToList();
                    check.Record("assembly.members", new JArray(sorted), new JArray(members), members.SequenceEqual(sorted));
                    check.Compare("assembly.naming_category", Rid.Value(cat), Rid.Value(a.NamingCategoryId));
                    if (name != null) check.Compare("assembly.name", name, a.AssemblyTypeName);
                    return check;
                },
                Result = () =>
                {
                    AssemblyInstance a = created == null ? null : doc.GetElement(created) as AssemblyInstance;
                    return a == null ? new JObject() : AssemblyJson(doc, a);
                }
            };
        }

        private static AssemblyInstance ResolveAssembly(Document doc, JObject request, out string error)
        {
            error = null;
            long id = request.Value<long?>("assembly_id") ?? -1;
            AssemblyInstance a = Rid.CanRepresent(id) ? doc.GetElement(Rid.Make(id)) as AssemblyInstance : null;
            if (a == null) error = "assembly_id " + id + " is not an assembly instance.";
            return a;
        }

        private static StagedWrite PlanViews(UIApplication app, GateResult gate, JObject request, out string error)
        {
            Document doc = gate.Document;
            AssemblyInstance a = ResolveAssembly(doc, request, out error);
            if (a == null) return null;
            List<string> kinds = (request["views"] as JArray)?.Select(t => (string)t).ToList();
            string kindError = PhaseRules.ViewKindsError(kinds);
            if (kindError != null) { error = kindError + "."; return null; }
            ElementId aid = a.Id;
            ResolvedPlan resolved = StagedGroupWrite.NewResolved("horizun_manage_assemblies_parts", gate, app);
            resolved.Elements.Add(new PlannedElement { UniqueId = a.UniqueId, ElementId = Rid.Value(aid), Category = "assembly", Action = PlannedAction.Modify,
                BeforeValues = new Dictionary<string, string> { ["members"] = string.Join(",", a.GetMemberIds().Select(Rid.Value).OrderBy(v => v)) } });
            var createdViews = new Dictionary<string, ElementId>();
            string[] required = kinds.Select(k => "view." + k).ToArray();
            return new StagedWrite
            {
                Operation = "assembly_views", Resolved = resolved, Count = kinds.Count,
                Plan = new JObject { ["assembly_id"] = Rid.Value(aid), ["views"] = new JArray(kinds) },
                Apply = d => StagedGroupWrite.Tx(d, "Horizun: assembly views", () =>
                {
                    createdViews.Clear();
                    foreach (string k in kinds)
                    {
                        View v;
                        switch (k)
                        {
                            case "3d": v = AssemblyViewUtils.Create3DOrthographic(d, aid); break;
                            case "plan": v = AssemblyViewUtils.CreateDetailSection(d, aid, AssemblyDetailViewOrientation.HorizontalDetail); break;
                            case "section_a": v = AssemblyViewUtils.CreateDetailSection(d, aid, AssemblyDetailViewOrientation.DetailSectionA); break;
                            case "section_b": v = AssemblyViewUtils.CreateDetailSection(d, aid, AssemblyDetailViewOrientation.DetailSectionB); break;
                            case "elevation_front": v = AssemblyViewUtils.CreateDetailSection(d, aid, AssemblyDetailViewOrientation.ElevationFront); break;
                            default: v = AssemblyViewUtils.CreatePartList(d, aid); break;
                        }
                        createdViews[k] = v.Id;
                    }
                }),
                Verify = d =>
                {
                    var check = new PostconditionCheck(required);
                    foreach (string k in kinds)
                    {
                        ElementId vid; View v = createdViews.TryGetValue(k, out vid) ? d.GetElement(vid) as View : null;
                        if (v == null) check.Unreadable("view." + k, Rid.Value(aid), "the view does not re-read");
                        else check.Compare("view." + k, Rid.Value(aid), Rid.Value(v.AssociatedAssemblyInstanceId));
                    }
                    return check;
                },
                Result = () => new JObject { ["assembly_id"] = Rid.Value(aid),
                    ["views"] = new JArray(kinds.Select(k => new JObject { ["kind"] = k, ["view_id"] = Rid.Value(createdViews[k]), ["name"] = doc.GetElement(createdViews[k])?.Name })) }
            };
        }

        private static StagedWrite PlanDisassemble(UIApplication app, GateResult gate, JObject request, out string error)
        {
            Document doc = gate.Document;
            AssemblyInstance a = ResolveAssembly(doc, request, out error);
            if (a == null) return null;
            ElementId aid = a.Id;
            List<ElementId> members = a.GetMemberIds().OrderBy(Rid.Value).ToList();
            ResolvedPlan resolved = StagedGroupWrite.NewResolved("horizun_manage_assemblies_parts", gate, app);
            resolved.Elements.Add(new PlannedElement { UniqueId = a.UniqueId, ElementId = Rid.Value(aid), Category = "assembly", Action = PlannedAction.Delete,
                BeforeValues = new Dictionary<string, string> { ["members"] = string.Join(",", members.Select(Rid.Value)), ["name"] = a.AssemblyTypeName } });
            var required = new List<string> { "assembly.gone" };
            foreach (ElementId m in members) required.Add("m" + Rid.Value(m) + ".free");
            return new StagedWrite
            {
                Operation = "disassemble", Resolved = resolved, Count = 1,
                Plan = new JObject { ["assembly_id"] = Rid.Value(aid), ["name"] = a.AssemblyTypeName, ["member_ids"] = StagedGroupWrite.Json(members) },
                Warning = "Disassembling removes the assembly instance; its members stay in the model. Assembly views of a type left with no instance may be removed by Revit.",
                Apply = d => StagedGroupWrite.Tx(d, "Horizun: disassemble", () => ((AssemblyInstance)d.GetElement(aid)).Disassemble()),
                Verify = d =>
                {
                    var check = new PostconditionCheck(required.ToArray());
                    check.Compare("assembly.gone", true, d.GetElement(aid) == null);
                    foreach (ElementId m in members)
                    {
                        Element e = d.GetElement(m);
                        if (e == null) check.Unreadable("m" + Rid.Value(m) + ".free", true, "member no longer exists");
                        else check.Compare("m" + Rid.Value(m) + ".free", true, e.AssemblyInstanceId == ElementId.InvalidElementId);
                    }
                    return check;
                },
                Result = () => new JObject { ["assembly_id"] = Rid.Value(aid), ["member_ids"] = StagedGroupWrite.Json(members) }
            };
        }
    }
}
