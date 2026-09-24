// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// horizun_manage_groups - model groups, typed. Until this command a group was
// created or redefined through execute_python (96 real scripts did it), which
// is the self-reported tier for work whose result the API answers directly.
//
// WHAT THE API DOES NOT HAVE, and how each gap is held:
//
//   * NO "EDIT GROUP". Membership changes go ungroup -> change the set ->
//     NewGroup -> (scope=all_instances) swap every other instance onto the new
//     type, delete the old one and give the new one its name. The scope is the
//     caller's decision whenever other instances exist (GroupRedefinitionRules);
//     a swapped instance's content is re-placed by a MEASURED displacement and
//     checked member by member, and a type with attached detail groups is refused
//     because the regroup would orphan them.
//   * NO "CONVERT TO LINK". GroupType has LoadFrom and nothing that saves a group
//     as a model, in every year 2023-2027 (checked by reflection on each
//     RevitAPI.dll). The refusal is typed and says Python cannot do it either.
//
// Every write rehearses in a rolled-back transaction by default, spends a
// single-use token, and is verified with a PostconditionCheck while the
// TransactionGroup can still roll back, and again after it assimilated.
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
    public sealed class ManageGroupsCommand : ICommand
    {
        public string Name => "horizun_manage_groups";
        public string Description => "List, create, redefine, rename, duplicate, swap and ungroup Revit groups with rehearsal and re-read verification.";

        private static readonly string[] Writes =
            { "create", "add_members", "remove_members", "rename_type", "duplicate_type", "swap_type", "ungroup" };

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            JObject request;
            try { request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JObject.Parse(paramsJson); }
            catch (Exception ex) { return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message); }
            string op = (request.Value<string>("operation") ?? "").Trim().ToLowerInvariant();

            if (op == "list")
            {
                Document readDoc = app.ActiveUIDocument?.Document;
                if (readDoc == null) return CommandResult.Fail("No document is open.");
                CommandResult wrong = DocumentGate.ReadGuard(readDoc, request, Name);
                if (wrong != null) return wrong;
                return List(readDoc, request);
            }
            if (op == "convert_to_link")
                return CommandResult.FailWithDetail(
                    "convert_to_link is not available: the Revit API (2023-2027) has no call that turns a group into a " +
                    "linked model - GroupType offers LoadFrom and nothing that saves a group as a file. Do it in Revit " +
                    "(select the group, Link). Nothing was written.",
                    new JObject { ["state"] = "refused", ["code"] = "api_absent", ["operation"] = op, ["write_started"] = false },
                    FallbackSignal.NotAllowed("api_absent", false), null);
            if (!Writes.Contains(op))
                return CommandResult.Fail("operation must be list, " + string.Join(", ", Writes) + " or convert_to_link.");

            GateResult gate = DocumentGate.ForMutation(app, request, Name); if (!gate.Ok) return gate.Refusal;
            Document doc = gate.Document;
            string error; Plan plan = MakePlan(doc, request, op, out error);
            if (plan == null)
                return CommandResult.FailWithDetail(error + " Nothing was written.",
                    new JObject { ["state"] = "refused", ["operation"] = op, ["write_started"] = false });

            string hash = DocumentGate.PlanHash(request, "operation", "group_ids", "type_id", "element_ids", "name", "scope");
            ResolvedPlan resolved = Resolved(gate, app, plan);
            bool dry = request["dry_run"] == null || request.Value<bool>("dry_run");
            string txName = "Horizun: groups " + op;

            if (dry)
            {
                JObject rehearsal = Rehearse(doc, plan, txName);
                if (rehearsal.Value<bool>("rollback_confirmed") != true)
                    return CommandResult.FailWithDetail("Group rehearsal rollback was not confirmed; model state is uncertain.",
                        new JObject { ["state"] = "uncertain", ["rehearsal"] = rehearsal, ["write_started"] = true });
                if (rehearsal.Value<bool>("verified") != true)
                    return CommandResult.FailWithDetail("The group rehearsal could not verify the change. Nothing was committed.",
                        new JObject { ["state"] = "refused", ["rehearsal"] = rehearsal });
                DocumentGate.RecordResolvedPlan(resolved);
                var result = new JObject { ["dry_run"] = true, ["plan"] = PlanJson(plan), ["rehearsal"] = rehearsal };
                ApplicationOutcome.StampRehearsal(result, 1, 0, 0, 0);
                DocumentGate.StampConfirmation(result, gate, Name, hash, true,
                    "the token binds the group instances, types, member ids and instance counts named in the plan.");
                return CommandResult.Ok(result);
            }

            DocumentGate.RecordResolvedPlan(resolved);
            CommandResult refusal = DocumentGate.RequireConfirmation(app, gate, request, Name, hash, resolved, null);
            if (refusal != null) return refusal;

            var failures = new FailureLog();
            PostconditionCheck inside;
            using (var group = new TransactionGroup(doc, txName))
            {
                if (group.Start() != TransactionStatus.Started) return CommandResult.Fail("Could not start the group TransactionGroup.");
                var tx = new Transaction(doc, txName); TransactionStatus txStatus = TransactionStatus.Uninitialized;
                try
                {
                    if (tx.Start() != TransactionStatus.Started) throw new InvalidOperationException("Could not start the group transaction.");
                    Quiet(tx, failures);
                    Apply(doc, plan);
                    doc.Regenerate();
                    inside = Verify(doc, plan);
                    if (!inside.AllVerified) throw new InvalidOperationException("a postcondition failed while the change was still reversible.");
                    txStatus = tx.Commit();
                    if (txStatus != TransactionStatus.Committed) throw new InvalidOperationException("the transaction returned " + txStatus + (failures.Errors.Count > 0 ? ": " + string.Join(" | ", failures.Errors) : "") + ".");
                    inside = Verify(doc, plan);
                    if (!inside.AllVerified) throw new InvalidOperationException("a postcondition failed after the transaction committed.");
                    TransactionStatus gs = group.Assimilate();
                    if (gs != TransactionStatus.Committed) return CommandResult.FailWithDetail("The group TransactionGroup did not assimilate; state is uncertain.",
                        new JObject { ["state"] = "uncertain", ["transaction_group_status"] = gs.ToString(), ["write_started"] = true });
                }
                catch (Exception ex)
                {
                    try { if (tx.GetStatus() == TransactionStatus.Started) Guard.RollBack(tx); } catch { }
                    Guard.RollbackResult? gr; try { gr = Guard.RollBack(group); } catch { gr = null; }
                    bool rolled = gr.HasValue && gr.Value.Confirmed;
                    return CommandResult.FailWithDetail("Group " + op + " failed and was " + (rolled ? "rolled back" : "NOT confirmed rolled back") + ": " + ex.Message,
                        new JObject { ["state"] = rolled ? "rolled_back" : "uncertain", ["write_started"] = true,
                                      ["transaction_group_status"] = gr.HasValue ? gr.Value.StatusName : "Error",
                                      ["postconditions"] = SafeJson(doc, plan), ["revit_warnings"] = new JArray(failures.Warnings) });
                }
                finally { tx.Dispose(); }
            }
            PostconditionCheck after = Verify(doc, plan);
            var done = new JObject
            {
                ["state"] = after.AllVerified ? "committed_verified" : "uncertain",
                ["host_verified"] = after.AllVerified,
                ["operation"] = op,
                ["result"] = ResultJson(doc, plan),
                ["postconditions"] = after.ToJson(),
                ["revit_warnings"] = new JArray(failures.Warnings)
            };
            ApplicationOutcome.StampApplied(done, ApplicationOutcome.Committed, 1, 1, after.AllVerified ? 1 : 0, 0, 0, after.AllVerified ? 0 : 1);
            if (!after.AllVerified)
                return CommandResult.FailWithDetail("The change committed but a postcondition no longer re-reads as planned; state is uncertain.", done);
            return CommandResult.Ok(done);
        }

        // ------------------------------------------------------------------ list
        private static CommandResult List(Document doc, JObject request)
        {
            long filter = request.Value<long?>("type_id") ?? -1;
            int max = Math.Max(1, Math.Min(2000, request.Value<int?>("max_rows") ?? 200));
            var all = new FilteredElementCollector(doc).OfClass(typeof(GroupType)).Cast<GroupType>()
                .Where(t => filter < 0 || Rid.Value(t.Id) == filter).OrderBy(t => Rid.Value(t.Id)).ToList();
            var rows = new JArray();
            int instances = 0;
            foreach (GroupType t in all.Take(max))
            {
                var inst = new JArray();
                foreach (Group g in GroupsOf(t))
                {
                    instances++;
                    IList<ElementId> members = Safe(() => g.GetMemberIds()) ?? new List<ElementId>();
                    var nested = members.Where(id => doc.GetElement(id) is Group).Select(Rid.Value).ToList();
                    var row = new JObject
                    {
                        ["group_id"] = Rid.Value(g.Id),
                        ["member_count"] = members.Count,
                        ["member_ids"] = new JArray(members.Take(500).Select(Rid.Value)),
                        ["nested_group_ids"] = new JArray(nested),
                        ["parent_group_id"] = g.GroupId == ElementId.InvalidElementId ? (JToken)JValue.CreateNull() : Rid.Value(g.GroupId)
                    };
                    if (members.Count > 500) row["member_ids_truncated"] = true;
                    ElementId attachedParent = Safe(() => g.AttachedParentId);
                    if (attachedParent != null && attachedParent != ElementId.InvalidElementId) row["attached_parent_id"] = Rid.Value(attachedParent);
                    inst.Add(row);
                }
                rows.Add(new JObject
                {
                    ["type_id"] = Rid.Value(t.Id),
                    ["name"] = Safe(() => t.Name),
                    ["kind"] = Kind(t),
                    ["instance_count"] = inst.Count,
                    ["attached_detail_type_ids"] = new JArray((Safe(() => t.GetAvailableAttachedDetailGroupTypeIds()) ?? new HashSet<ElementId>()).Select(Rid.Value)),
                    ["instances"] = inst
                });
            }
            var listing = new JObject
            {
                ["document"] = doc.Title,
                ["type_count"] = all.Count,
                ["returned_types"] = rows.Count,
                ["truncated"] = all.Count > rows.Count,
                ["instance_count_returned"] = instances,
                ["types"] = rows
            };
            ApplicationOutcome.StampApplied(listing, ApplicationOutcome.NotStarted, 0, 0, 0, 0, 0, 0);
            return CommandResult.Ok(listing);
        }

        // ------------------------------------------------------------------ plan
        private static Plan MakePlan(Document doc, JObject r, string op, out string error)
        {
            error = null;
            var p = new Plan { Op = op, Name = r.Value<string>("name") };
            foreach (JToken t in (r["group_ids"] as JArray) ?? new JArray())
            {
                long id = t.Value<long>();
                Group g = Rid.CanRepresent(id) ? doc.GetElement(Rid.Make(id)) as Group : null;
                if (g == null) { error = "group_ids names " + id + ", which is not a group instance."; return null; }
                if (p.Groups.Any(x => x.Id == g.Id)) { error = "group_ids repeats " + id + "."; return null; }
                if (g.GroupId != ElementId.InvalidElementId) { error = "group " + id + " is nested inside group " + Rid.Value(g.GroupId) + "; act on the outer group."; return null; }
                p.Groups.Add(g);
            }
            foreach (JToken t in (r["element_ids"] as JArray) ?? new JArray())
            {
                long id = t.Value<long>();
                Element e = Rid.CanRepresent(id) ? doc.GetElement(Rid.Make(id)) : null;
                if (e == null) { error = "element_ids names " + id + ", which is not an element."; return null; }
                if (p.Elements.Contains(e.Id)) { error = "element_ids repeats " + id + "."; return null; }
                p.Elements.Add(e.Id);
            }
            long typeId = r.Value<long?>("type_id") ?? -1;
            if (typeId >= 0) p.Type = Rid.CanRepresent(typeId) ? doc.GetElement(Rid.Make(typeId)) as GroupType : null;
            if (typeId >= 0 && p.Type == null) { error = "type_id " + typeId + " is not a group type."; return null; }
            if (p.Type != null) p.TypeId = p.Type.Id;
            p.GroupIds = p.Groups.Select(g => g.Id).ToList();

            switch (op)
            {
                case "create":
                    if (p.Elements.Count == 0) { error = "create needs element_ids."; return null; }
                    if (!Loose(doc, p.Elements, out error)) return null;
                    if (!NameFree(doc, p.Name, null, out error)) return null;
                    return p;
                case "add_members":
                case "remove_members":
                    return PlanMembers(doc, r, p, out error);
                case "rename_type":
                case "duplicate_type":
                    if (p.Type == null) { error = op + " needs type_id."; return null; }
                    if (!NameFree(doc, p.Name, p.Type, out error)) return null;
                    p.TypeInstancesBefore = GroupsOf(p.Type).Count;
                    return p;
                case "swap_type":
                    if (p.Type == null || p.Groups.Count == 0) { error = "swap_type needs group_ids and the target type_id."; return null; }
                    foreach (Group g in p.Groups)
                    {
                        if (g.GroupType.Id == p.Type.Id) { error = "group " + Rid.Value(g.Id) + " already has type " + typeId + "; a no-op is refused, not reported as work."; return null; }
                        if (Kind(g.GroupType) != Kind(p.Type)) { error = "group " + Rid.Value(g.Id) + " is a " + Kind(g.GroupType) + " group and type " + typeId + " is " + Kind(p.Type) + "."; return null; }
                    }
                    p.TypeInstancesBefore = GroupsOf(p.Type).Count;
                    return p;
                case "ungroup":
                    if (p.Groups.Count == 0) { error = "ungroup needs group_ids."; return null; }
                    foreach (Group g in p.Groups) p.MembersBefore[g.Id] = g.GetMemberIds().ToList();
                    return p;
            }
            error = "unknown operation."; return null;
        }

        private static Plan PlanMembers(Document doc, JObject r, Plan p, out string error)
        {
            error = null;
            if (p.Groups.Count != 1) { error = p.Op + " needs exactly one group_ids entry: the reference instance."; return null; }
            if (p.Elements.Count == 0) { error = p.Op + " needs element_ids."; return null; }
            Group reference = p.Groups[0];
            p.Type = reference.GroupType; p.TypeId = p.Type.Id;
            if (Kind(p.Type) == "attached_detail") { error = "attached detail groups are redefined through their model group, not directly."; return null; }
            var attached = Safe(() => p.Type.GetAvailableAttachedDetailGroupTypeIds());
            if (attached != null && attached.Count > 0)
            { error = "type '" + p.Type.Name + "' has " + attached.Count + " attached detail group type(s); the ungroup/regroup this redefinition needs would orphan them, so it is not done typed."; return null; }
            List<ElementId> current = reference.GetMemberIds().ToList();
            p.MembersBefore[reference.Id] = current;
            if (p.Op == "add_members")
            {
                if (!Loose(doc, p.Elements, out error)) return null;
                p.Expected = current.Concat(p.Elements).ToList();
            }
            else
            {
                var notMember = p.Elements.Where(id => !current.Contains(id)).ToList();
                if (notMember.Count > 0) { error = "element(s) " + string.Join(", ", notMember.Select(Rid.Value)) + " are not direct members of group " + Rid.Value(reference.Id) + "."; return null; }
                p.Expected = current.Where(id => !p.Elements.Contains(id)).ToList();
                if (p.Expected.Count == 0) { error = "removing every member empties the group; use operation=ungroup."; return null; }
            }
            p.Others = GroupsOf(p.Type).Where(g => g.Id != reference.Id).ToList();
            if (p.Others.Any(g => g.GroupId != ElementId.InvalidElementId))
            { error = "another instance of this type is nested inside a group; redefining the type from here is refused."; return null; }
            p.Scope = GroupRedefinitionRules.ResolveScope(r.Value<string>("scope"), p.Others.Count, out error);
            if (p.Scope == null) return null;
            p.OldTypeName = p.Type.Name;
            if (p.Scope == GroupRedefinitionRules.ScopeThis && p.Others.Count > 0)
            { if (!NameFree(doc, p.Name, null, out error)) return null; }
            else p.Name = p.OldTypeName;
            foreach (Group o in p.Others) p.OtherCounts[o.Id] = o.GetMemberIds().Count;
            p.OtherIds = p.Others.Select(o => o.Id).ToList();
            if (p.Scope == GroupRedefinitionRules.ScopeAll)
                foreach (Group o in p.Others) p.SigsBefore[o.Id] = Signatures(doc, o.GetMemberIds());
            p.TypeInstancesBefore = p.Others.Count + 1;
            return p;
        }

        // ----------------------------------------------------------------- apply
        private static void Apply(Document doc, Plan p)
        {
            p.Shifts.Clear();
            switch (p.Op)
            {
                case "create":
                {
                    Group g = doc.Create.NewGroup(p.Elements);
                    g.GroupType.Name = p.Name;
                    p.NewGroupId = g.Id; p.NewTypeId = g.GroupType.Id;
                    break;
                }
                case "add_members":
                case "remove_members":
                {
                    ElementId oldType = p.TypeId;
                    p.Groups[0].UngroupMembers();
                    Group g = doc.Create.NewGroup(p.Expected);
                    p.NewGroupId = g.Id; p.NewTypeId = g.GroupType.Id;
                    if (p.Scope == GroupRedefinitionRules.ScopeThis && p.Others.Count > 0) { g.GroupType.Name = p.Name; break; }
                    foreach (Group o in p.Others)
                    {
                        o.GroupType = g.GroupType;
                        doc.Regenerate();
                        string why;
                        double[] shift = GroupRedefinitionRules.MeasureShift(p.SigsBefore[o.Id], Signatures(doc, o.GetMemberIds()), out why);
                        if (shift == null) throw new InvalidOperationException("instance " + Rid.Value(o.Id) + ": " + why);
                        if (Math.Abs(shift[0]) + Math.Abs(shift[1]) + Math.Abs(shift[2]) > 1e-9)
                            ElementTransformUtils.MoveElement(doc, o.Id, new XYZ(-shift[0], -shift[1], -shift[2]));
                        p.Shifts[o.Id] = shift;
                    }
                    if (doc.GetElement(oldType) != null) doc.Delete(oldType);
                    g.GroupType.Name = p.OldTypeName;
                    break;
                }
                case "rename_type":
                    p.Type.Name = p.Name; p.NewTypeId = p.Type.Id; break;
                case "duplicate_type":
                    p.NewTypeId = p.Type.Duplicate(p.Name).Id; break;
                case "swap_type":
                    foreach (Group g in p.Groups) g.GroupType = p.Type;
                    break;
                case "ungroup":
                    foreach (Group g in p.Groups) g.UngroupMembers();
                    break;
            }
        }

        // ---------------------------------------------------------------- verify
        private static PostconditionCheck Verify(Document doc, Plan p)
        {
            switch (p.Op)
            {
                case "create":
                {
                    var c = new PostconditionCheck("group_exists", "members", "type_name");
                    Group g = p.NewGroupId == null ? null : doc.GetElement(p.NewGroupId) as Group;
                    c.Compare("group_exists", true, g != null);
                    SameIds(c, "members", p.Elements, g?.GetMemberIds());
                    c.Compare("type_name", p.Name, g?.GroupType?.Name);
                    return c;
                }
                case "add_members":
                case "remove_members":
                {
                    var c = new PostconditionCheck("reference_members", "type_name", "old_type", "instance_count", "other_instances");
                    Group g = p.NewGroupId == null ? null : doc.GetElement(p.NewGroupId) as Group;
                    SameIds(c, "reference_members", p.Expected, g?.GetMemberIds());
                    c.Compare("type_name", p.Name, g?.GroupType?.Name);
                    bool keepOld = p.Scope == GroupRedefinitionRules.ScopeThis && p.Others.Count > 0;
                    GroupType old = doc.GetElement(p.TypeId) as GroupType;
                    c.Compare("old_type", keepOld ? "kept" : "deleted", old == null ? "deleted" : "kept");
                    GroupType now = p.NewTypeId == null ? null : doc.GetElement(p.NewTypeId) as GroupType;
                    c.Compare("instance_count", keepOld ? 1 : p.Others.Count + 1, now == null ? -1 : GroupsOf(now).Count);
                    var evidence = new JArray(); bool held = true;
                    foreach (ElementId oid in p.OtherIds)
                    {
                        Group o = doc.GetElement(oid) as Group;
                        if (keepOld)
                        {
                            int n;
                            bool same = o != null && old != null && o.GroupType.Id == old.Id && p.OtherCounts.TryGetValue(oid, out n) && o.GetMemberIds().Count == n;
                            held &= same; evidence.Add(new JObject { ["group_id"] = Rid.Value(oid), ["untouched"] = same });
                            continue;
                        }
                        RetainedMatch m = o == null ? null : GroupRedefinitionRules.Match(p.SigsBefore[oid], Signatures(doc, o.GetMemberIds()),
                            p.Op == "add_members", p.Elements.Count, GroupRedefinitionRules.BoxTolerance);
                        bool ok = m != null && m.Held && o.GroupType.Id == p.NewTypeId;
                        held &= ok;
                        evidence.Add(new JObject { ["group_id"] = Rid.Value(oid), ["held"] = ok, ["members"] = m?.After ?? -1,
                            ["expected"] = m?.Expected ?? -1, ["why"] = m?.Why ?? (o == null ? "instance no longer exists" : null) });
                    }
                    c.Record("other_instances", p.OtherIds.Count, evidence, held);
                    return c;
                }
                case "rename_type":
                {
                    var c = new PostconditionCheck("type_name", "instance_count");
                    GroupType t = doc.GetElement(p.TypeId) as GroupType;
                    c.Compare("type_name", p.Name, t?.Name);
                    c.Compare("instance_count", p.TypeInstancesBefore, t == null ? -1 : GroupsOf(t).Count);
                    return c;
                }
                case "duplicate_type":
                {
                    var c = new PostconditionCheck("new_type_name", "new_type_instances", "source_instances");
                    GroupType n = p.NewTypeId == null ? null : doc.GetElement(p.NewTypeId) as GroupType;
                    GroupType s = doc.GetElement(p.TypeId) as GroupType;
                    c.Compare("new_type_name", p.Name, n?.Name);
                    c.Compare("new_type_instances", 0, n == null ? -1 : GroupsOf(n).Count);
                    c.Compare("source_instances", p.TypeInstancesBefore, s == null ? -1 : GroupsOf(s).Count);
                    return c;
                }
                case "swap_type":
                {
                    var c = new PostconditionCheck("instance_types", "target_instances");
                    var wrong = p.GroupIds.Where(id => (doc.GetElement(id) as Group)?.GroupType?.Id != p.TypeId).Select(Rid.Value).ToList();
                    c.Record("instance_types", Rid.Value(p.TypeId), new JArray(wrong), wrong.Count == 0);
                    GroupType t = doc.GetElement(p.TypeId) as GroupType;
                    c.Compare("target_instances", p.TypeInstancesBefore + p.Groups.Count, t == null ? -1 : GroupsOf(t).Count);
                    return c;
                }
                default: // ungroup
                {
                    var c = new PostconditionCheck("groups_removed", "members_released");
                    var still = p.GroupIds.Where(id => doc.GetElement(id) != null).Select(Rid.Value).ToList();
                    c.Record("groups_removed", p.GroupIds.Count, new JArray(still), still.Count == 0);
                    var members = p.MembersBefore.Values.SelectMany(x => x).ToList();
                    var bad = members.Where(id => { Element e = doc.GetElement(id); return e == null || e.GroupId != ElementId.InvalidElementId; }).Select(Rid.Value).ToList();
                    c.Record("members_released", members.Count, new JArray(bad), members.Count > 0 && bad.Count == 0);
                    return c;
                }
            }
        }

        private static JObject Rehearse(Document doc, Plan p, string name)
        {
            var failures = new FailureLog();
            PostconditionCheck check = null; string error = null; Guard.RollbackResult? rb = null; string rbError = null;
            using (var tx = new Transaction(doc, name))
            {
                try
                {
                    if (tx.Start() != TransactionStatus.Started) throw new InvalidOperationException("transaction did not start");
                    Quiet(tx, failures);
                    Apply(doc, p); doc.Regenerate(); check = Verify(doc, p);
                }
                catch (Exception ex) { error = ex.Message; }
                var shifts = new JArray(p.Shifts.Select(kv => new JObject { ["group_id"] = Rid.Value(kv.Key), ["measured_shift_ft"] = new JArray(kv.Value) }));
                try { rb = Guard.RollBack(tx); } catch (Exception ex) { rbError = ex.Message; }
                return new JObject
                {
                    ["verified"] = error == null && check != null && check.AllVerified,
                    ["postconditions"] = check?.ToJson(),
                    ["error"] = error,
                    ["swapped_instance_shifts"] = shifts,
                    ["rollback_status"] = rb.HasValue ? rb.Value.StatusName : "exception: " + rbError,
                    ["rollback_confirmed"] = rb.HasValue && rb.Value.Confirmed
                };
            }
        }

        // --------------------------------------------------------------- helpers
        private static bool Loose(Document doc, List<ElementId> ids, out string error)
        {
            error = null;
            foreach (ElementId id in ids)
            {
                Element e = doc.GetElement(id);
                if (e is ElementType || e is View) { error = "element " + Rid.Value(id) + " is a type or a view and cannot be grouped."; return false; }
                if (e.GroupId != ElementId.InvalidElementId) { error = "element " + Rid.Value(id) + " already belongs to group " + Rid.Value(e.GroupId) + "."; return false; }
            }
            return true;
        }

        private static bool NameFree(Document doc, string name, GroupType sameKindAs, out string error)
        {
            var names = new FilteredElementCollector(doc).OfClass(typeof(GroupType)).Cast<GroupType>()
                .Where(t => sameKindAs == null || Kind(t) == Kind(sameKindAs)).Select(t => Safe(() => t.Name)).Where(n => n != null);
            string problem = WorksetEditRules.NameProblem(name, names);
            error = problem == null ? null : "name: " + problem;
            return problem == null;
        }

        private static void SameIds(PostconditionCheck c, string what, IEnumerable<ElementId> wanted, IEnumerable<ElementId> found)
        {
            if (found == null) { c.Unreadable(what, new JArray(wanted.Select(Rid.Value)), "the group does not re-read"); return; }
            var w = wanted.Select(Rid.Value).OrderBy(x => x).ToList();
            var f = found.Select(Rid.Value).OrderBy(x => x).ToList();
            c.Record(what, new JArray(w), new JArray(f), w.Count > 0 && w.SequenceEqual(f));
        }

        private static List<MemberSignature> Signatures(Document doc, IEnumerable<ElementId> ids)
        {
            var list = new List<MemberSignature>();
            foreach (ElementId id in ids)
            {
                Element e = doc.GetElement(id); if (e == null) continue;
                BoundingBoxXYZ b = Safe(() => e.get_BoundingBox(null));
                var s = new MemberSignature(e.Category == null ? -1 : Rid.Value(e.Category.Id), Rid.Value(e.GetTypeId()),
                    b == null ? null : new[] { b.Min.X, b.Min.Y, b.Min.Z }, b == null ? null : new[] { b.Max.X, b.Max.Y, b.Max.Z });
                s.HasBox = b != null;
                list.Add(s);
            }
            return list;
        }

        private static List<Group> GroupsOf(GroupType t)
        {
            var list = new List<Group>();
            GroupSet set = Safe(() => t.Groups);
            if (set != null) foreach (Group g in set) list.Add(g);
            return list;
        }

        private static string Kind(GroupType t)
        {
            long c = t?.Category == null ? -1 : Rid.Value(t.Category.Id);
            if (c == (long)BuiltInCategory.OST_IOSModelGroups) return "model";
            if (c == (long)BuiltInCategory.OST_IOSDetailGroups) return "detail";
            if (c == (long)BuiltInCategory.OST_IOSAttachedDetailGroups) return "attached_detail";
            return "other";
        }

        private static JObject PlanJson(Plan p) => new JObject
        {
            ["operation"] = p.Op,
            ["group_ids"] = new JArray(p.GroupIds.Select(Rid.Value)),
            ["type_id"] = p.TypeId == null ? (JToken)JValue.CreateNull() : Rid.Value(p.TypeId),
            ["element_ids"] = new JArray(p.Elements.Select(Rid.Value)),
            ["name"] = p.Name,
            ["scope"] = p.Scope,
            ["type_instances_before"] = p.TypeInstancesBefore,
            ["other_instances_affected"] = p.Scope == GroupRedefinitionRules.ScopeAll ? new JArray(p.OtherIds.Select(Rid.Value)) : new JArray(),
            ["note"] = p.Op == "add_members" || p.Op == "remove_members"
                ? "Revit has no edit-group API: the reference instance is ungrouped and regrouped (it gets a NEW group id; its instance parameters are not carried). " +
                  (p.Scope == GroupRedefinitionRules.ScopeAll && p.Others.Count > 0
                      ? "Every other instance listed is swapped to the new type: its member ids change, and members removed from it are deleted."
                      : "No other instance is touched.")
                : null
        };

        private static JObject ResultJson(Document doc, Plan p)
        {
            ElementId typeId = p.NewTypeId ?? p.TypeId;
            var o = new JObject { ["type_id"] = typeId == null ? (JToken)JValue.CreateNull() : Rid.Value(typeId) };
            if (p.NewGroupId != null) o["group_id"] = Rid.Value(p.NewGroupId);
            GroupType t = typeId == null ? null : doc.GetElement(typeId) as GroupType;
            if (t != null) { o["type_name"] = t.Name; o["type_instances_after"] = GroupsOf(t).Count; }
            o["type_instances_before"] = p.TypeInstancesBefore;
            if (p.Shifts.Count > 0) o["swapped_instance_shifts_ft"] = new JArray(p.Shifts.Select(kv => new JObject { ["group_id"] = Rid.Value(kv.Key), ["shift"] = new JArray(kv.Value) }));
            return o;
        }

        private static void Quiet(Transaction tx, FailureLog log)
        {
            FailureHandlingOptions opts = tx.GetFailureHandlingOptions();
            opts.SetFailuresPreprocessor(log); opts.SetClearAfterRollback(true);
            tx.SetFailureHandlingOptions(opts);
        }

        private static JObject SafeJson(Document doc, Plan p) { try { return Verify(doc, p).ToJson(); } catch { return null; } }

        private static ResolvedPlan Resolved(GateResult gate, UIApplication app, Plan p)
        {
            var rp = new ResolvedPlan { Command = "horizun_manage_groups", DocumentKey = gate.Fingerprint, RevitVersion = app.Application.VersionNumber, DocumentFingerprint = gate.Identity.FingerprintDigest() };
            var before = new Dictionary<string, string>
            {
                ["operation"] = p.Op,
                ["type"] = p.Type == null ? "" : p.Type.UniqueId + "|" + Safe(() => p.Type.Name) + "|" + p.TypeInstancesBefore,
                ["groups"] = string.Join(",", p.Groups.Select(g => g.UniqueId + ":" + g.GroupType.Id)),
                ["members"] = string.Join(";", p.MembersBefore.Select(kv => Rid.Value(kv.Key) + "=" + string.Join(",", kv.Value.Select(Rid.Value)))),
                ["elements"] = string.Join(",", p.Elements.Select(Rid.Value)),
                ["others"] = string.Join(",", p.OtherIds.Select(Rid.Value))
            };
            rp.Elements.Add(new PlannedElement { UniqueId = "groups:" + p.Op, Category = "group", Action = p.Op == "create" || p.Op == "duplicate_type" ? PlannedAction.Create : PlannedAction.Modify, BeforeValues = before });
            return rp;
        }

        private static T Safe<T>(Func<T> f) where T : class { try { return f(); } catch { return null; } }

        private sealed class Plan
        {
            public string Op, Name, Scope, OldTypeName;
            public GroupType Type;
            public ElementId TypeId;
            public List<ElementId> GroupIds = new List<ElementId>(), OtherIds = new List<ElementId>();
            public int TypeInstancesBefore;
            public ElementId NewGroupId, NewTypeId;
            public readonly List<Group> Groups = new List<Group>();
            public readonly List<ElementId> Elements = new List<ElementId>();
            public List<ElementId> Expected = new List<ElementId>();
            public List<Group> Others = new List<Group>();
            public readonly Dictionary<ElementId, List<ElementId>> MembersBefore = new Dictionary<ElementId, List<ElementId>>();
            public readonly Dictionary<ElementId, List<MemberSignature>> SigsBefore = new Dictionary<ElementId, List<MemberSignature>>();
            public readonly Dictionary<ElementId, double[]> Shifts = new Dictionary<ElementId, double[]>();
            public readonly Dictionary<ElementId, int> OtherCounts = new Dictionary<ElementId, int>();        }

        /// <summary>Records and deletes warnings; an error rolls the transaction back and is reported.</summary>
        private sealed class FailureLog : IFailuresPreprocessor
        {
            public readonly List<string> Warnings = new List<string>();
            public readonly List<string> Errors = new List<string>();
            public FailureProcessingResult PreprocessFailures(FailuresAccessor a)
            {
                bool error = false;
                foreach (FailureMessageAccessor f in a.GetFailureMessages())
                {
                    string text = Safe(() => f.GetDescriptionText()) ?? "(no text)";
                    if (f.GetSeverity() == FailureSeverity.Warning) { Warnings.Add(text); a.DeleteWarning(f); }
                    else { Errors.Add(text); error = true; }
                }
                return error ? FailureProcessingResult.ProceedWithRollBack : FailureProcessingResult.Continue;
            }
        }
    }
}
