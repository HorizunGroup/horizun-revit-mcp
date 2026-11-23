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
    public sealed partial class ManageGroupsCommand
    {
        // ------------------------------------------------------------------ list
        private static CommandResult FindDuplicates(Document doc, JObject request)
        {
            double tolerance = request.Value<double?>("position_tolerance_mm") ?? 1.0;
            double levelTolerance = request.Value<double?>("level_tolerance_mm") ?? 300.0;
            if (tolerance <= 0 || levelTolerance < 0 || double.IsNaN(tolerance) ||
                double.IsInfinity(tolerance) || double.IsNaN(levelTolerance) || double.IsInfinity(levelTolerance))
                return CommandResult.Fail("position_tolerance_mm must be positive and level_tolerance_mm nonnegative.");
            var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
                .Select(l => new GroupLevelFact { Id = Rid.Value(l.Id), ElevationMm = l.Elevation * 304.8 }).ToList();
            var facts = new List<GroupDuplicateFact>();
            var evidence = new Dictionary<long, JObject>();
            var skipped = new JArray();
            IEnumerable<Element> candidates = new FilteredElementCollector(doc).OfClass(typeof(Group)).Cast<Element>()
                .Concat(new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance)).Cast<Element>());
            foreach (Element element in candidates)
            {
                long id = Rid.Value(element.Id);
                LocationPoint at = element.Location as LocationPoint;
                BoundingBoxXYZ bounds = Safe(() => element.get_BoundingBox(null));
                if (at == null || at.Point == null || bounds == null)
                {
                    skipped.Add(new JObject { ["id"] = id, ["reason"] = "location_or_geometry_unreadable" });
                    continue;
                }
                long typeId = Rid.Value(element.GetTypeId());
                string kind = element is Group ? "group" : "family";
                var inferred = GroupDuplicateRules.InferLevel(bounds.Min.Z * 304.8, levels, levelTolerance);
                facts.Add(new GroupDuplicateFact { Id = id, TypeId = typeId, Kind = kind,
                    X = at.Point.X * 304.8, Y = at.Point.Y * 304.8, Z = at.Point.Z * 304.8 });
                evidence[id] = new JObject { ["id"] = id, ["kind"] = kind, ["type_id"] = typeId,
                    ["position_mm"] = new JArray(at.Point.X * 304.8, at.Point.Y * 304.8, at.Point.Z * 304.8),
                    ["geometry_bottom_mm"] = inferred.GeometryBottomMm,
                    ["inferred_level_id"] = inferred.LevelId.HasValue ? (JToken)inferred.LevelId.Value : JValue.CreateNull(),
                    ["inference"] = inferred.Evidence,
                    ["level_distance_mm"] = double.IsInfinity(inferred.DistanceMm) ? (JToken)JValue.CreateNull() : inferred.DistanceMm,
                    ["stored_level_id"] = element.LevelId == ElementId.InvalidElementId ? (JToken)JValue.CreateNull() : Rid.Value(element.LevelId),
                    ["group_id"] = element.GroupId == ElementId.InvalidElementId ? (JToken)JValue.CreateNull() : Rid.Value(element.GroupId) };
            }
            var pairs = GroupDuplicateRules.Find(facts, tolerance);
            int maxRows = Math.Max(1, Math.Min(2000, request.Value<int?>("max_rows") ?? 200));
            var rows = new JArray();
            foreach (var pair in pairs.Take(maxRows))
                rows.Add(new JObject { ["kind"] = pair.Kind, ["type_id"] = pair.TypeId,
                    ["distance_mm"] = pair.DistanceMm,
                    ["first"] = evidence[pair.FirstId], ["second"] = evidence[pair.SecondId] });
            var result = new JObject { ["document"] = doc.Title,
                ["position_tolerance_mm"] = tolerance, ["level_tolerance_mm"] = levelTolerance,
                ["candidate_count"] = facts.Count, ["duplicate_pair_count"] = pairs.Count,
                ["returned_pairs"] = rows.Count, ["pairs_truncated"] = rows.Count < pairs.Count,
                ["pairs"] = rows, ["skipped"] = skipped,
                ["level_meaning"] = "Nearest Level to geometry bottom within tolerance; unknown otherwise. Stored LevelId is shown separately." };
            ApplicationOutcome.StampApplied(result, ApplicationOutcome.NotStarted, 0, 0, 0, 0, 0, 0);
            return CommandResult.Ok(result);
        }

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
            if (op == "place_group")
            {
                if (p.Groups.Count > 0) { error = "place_group uses pattern_group_id, not group_ids."; return null; }
                long patternId = r.Value<long?>("pattern_group_id") ?? -1;
                Group pattern = patternId >= 0 && Rid.CanRepresent(patternId)
                    ? doc.GetElement(Rid.Make(patternId)) as Group : null;
                if (pattern == null) { error = "pattern_group_id must identify a group instance."; return null; }
                if (pattern.GroupId != ElementId.InvalidElementId)
                { error = "A nested group cannot be used as the placement pattern."; return null; }
                p.Groups.Add(pattern); p.GroupIds.Add(pattern.Id);
            }

            switch (op)
            {
                case "create":
                    if (p.Elements.Count == 0) { error = "create needs element_ids."; return null; }
                    if (!Loose(doc, p.Elements, out error)) return null;
                    if (!NameFree(doc, p.Name, null, out error)) return null;
                    // THE CLOSURE, SAID BEFORE THE RUN. NewGroup over MEP curves brings each curve's "Center Line"
                    // companion (a dependent) along by itself; the exact member comparison used to refuse that valid group.
                    // They are named here so the rehearsal shows the physical count apart from the representations.
                    foreach (ElementId dep in DependentMembers(doc, p.Elements, null))
                        if (!p.Elements.Contains(dep))
                        {
                            p.AllowedExtra.Add(dep);
                            p.ExtraKinds[dep] = GroupMemberClosureRules.KindCenterLine;
                            p.Representational.Add(dep);
                        }
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
                case "change_group_type":
                    if (p.Type == null || p.Groups.Count == 0) { error = "swap_type needs group_ids and the target type_id."; return null; }
                    if (op == "change_group_type" && p.Groups.Count != 1)
                    { error = "change_group_type needs exactly one group_ids entry so member and tag mapping is unambiguous."; return null; }
                    foreach (Group g in p.Groups)
                    {
                        if (g.GroupType.Id == p.Type.Id) { error = "group " + Rid.Value(g.Id) + " already has type " + typeId + "; a no-op is refused, not reported as work."; return null; }
                        if (Kind(g.GroupType) != Kind(p.Type)) { error = "group " + Rid.Value(g.Id) + " is a " + Kind(g.GroupType) + " group and type " + typeId + " is " + Kind(p.Type) + "."; return null; }
                    }
                    p.TypeInstancesBefore = GroupsOf(p.Type).Count;
                    if (op == "change_group_type")
                    {
                        Group old = p.Groups[0];
                        p.AnnotationsBefore = AnnotationDependencyAudit.Read(doc, p.GroupIds);
                        if ((bool?)p.AnnotationsBefore["coverage_complete"] != true)
                        { error = "Some annotation references are unreadable; change_group_type refuses to risk orphaning them."; return null; }
                        try
                        {
                            p.OldMemberFacts = MemberFacts(doc, old);
                            p.TagsBefore = CaptureMemberTags(doc, old);
                        }
                        catch (Exception ex) { error = "Cannot map members/tags safely: " + ex.Message; return null; }
                    }
                    return p;
                case "set_group_level":
                {
                    if (p.Groups.Count != 1) { error = "set_group_level needs exactly one group_ids entry."; return null; }
                    long wanted = r.Value<long?>("level_id") ?? -1;
                    Level level = wanted >= 0 && Rid.CanRepresent(wanted) ? doc.GetElement(Rid.Make(wanted)) as Level : null;
                    if (level == null) { error = "level_id must identify an existing Level."; return null; }
                    Group target = p.Groups[0];
                    // A group that carries connected MEP elements has closed Revit when its level was changed (field, 2026-11):
                    // refuse before anything is written, and name the route that works (ungroup, move_run, regroup).
                    int mepMembers = CountMepMembers(doc, target, out List<long> mepExamples);
                    string mepRefusal = GroupLevelRules.MepLevelChangeRefusal(mepMembers, mepExamples);
                    if (mepRefusal != null) { error = mepRefusal; return null; }
                    Parameter levelParam = target.get_Parameter(BuiltInParameter.GROUP_LEVEL);
                    Parameter offsetParam = target.get_Parameter(BuiltInParameter.GROUP_OFFSET_FROM_LEVEL);
                    if (levelParam == null || levelParam.IsReadOnly || offsetParam == null || offsetParam.IsReadOnly)
                    { error = "Group level or offset parameter is unavailable/read-only."; return null; }
                    LocationPoint point = target.Location as LocationPoint;
                    if (point == null || point.Point == null) { error = "Group position is unreadable."; return null; }
                    p.LevelId = level.Id; p.KeepPosition = r.Value<bool?>("keep_position") ?? true;
                    p.PositionBefore = point.Point;
                    p.MembersBefore[target.Id] = target.GetMemberIds().ToList();
                    p.Type = target.GroupType; p.TypeId = p.Type.Id;
                    return p;
                }
                case "place_group":
                {
                    Group pattern = p.Groups[0];
                    if (Kind(pattern.GroupType) != "model")
                    { error = "place_group currently requires a model group pattern."; return null; }
                    LocationPoint at = pattern.Location as LocationPoint;
                    if (at == null || at.Point == null) { error = "Pattern position/rotation is unreadable."; return null; }
                    JArray xy = r["point_mm"] as JArray;
                    if (xy == null || xy.Count != 2 || xy.Any(v => v.Type != JTokenType.Float && v.Type != JTokenType.Integer))
                    { error = "point_mm must be [x,y] in internal-origin millimetres; pattern Z is preserved."; return null; }
                    double x = xy[0].Value<double>(), y = xy[1].Value<double>();
                    if (double.IsNaN(x) || double.IsInfinity(x) || double.IsNaN(y) || double.IsInfinity(y))
                    { error = "point_mm must contain finite coordinates."; return null; }
                    p.PositionBefore = at.Point;
                    p.TargetPoint = new XYZ(x / 304.8, y / 304.8, at.Point.Z);
                    // Group's LocationPoint.Rotation throws in Revit 2024. Infer candidate
                    // rotations from the actual member geometry during rehearsal instead.
                    p.Type = pattern.GroupType; p.TypeId = p.Type.Id;
                    p.LevelId = pattern.get_Parameter(BuiltInParameter.GROUP_LEVEL)?.AsElementId();
                    p.PatternOffset = pattern.get_Parameter(BuiltInParameter.GROUP_OFFSET_FROM_LEVEL)?.AsDouble();
                    p.PatternWorkset = pattern.WorksetId;
                    p.PatternSignatures = Signatures(doc, pattern.GetMemberIds());
                    if (p.LevelId == null || p.PatternOffset == null || p.PatternSignatures.Count == 0)
                    { error = "Pattern level, offset or member geometry is unreadable."; return null; }
                    return p;
                }
                case "ungroup":
                    if (p.Groups.Count == 0) { error = "ungroup needs group_ids."; return null; }
                    foreach (Group g in p.Groups)
                    {
                        p.MembersBefore[g.Id] = g.GetMemberIds().ToList();
                        p.ParentBefore[g.Id] = Rid.Value(g.GroupId);
                        ElementId t = g.GroupType.Id;
                        if (!p.UngroupTypes.ContainsKey(t)) p.UngroupTypes[t] = new[] { GroupsOf(g.GroupType).Count, 0 };
                        p.UngroupTypes[t][1]++;
                    }
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
            if (p.Op == "add_members" && p.Type != null && p.Type.Id != reference.GroupType.Id)
            { error = "type_id does not match the reference instance's type."; return null; }
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
                // NewGroup brings an added pipe's "Center Line" companions along by itself; they are
                // allowed in the new definition (and counted), nothing else is.
                foreach (ElementId dep in DependentMembers(doc, p.Elements, null))
                    if (!p.Elements.Contains(dep) && !current.Contains(dep)) p.AllowedExtra.Add(dep);
                p.Expected = current.Concat(p.Elements).ToList();
            }
            else
            {
                var notMember = p.Elements.Where(id => !current.Contains(id)).ToList();
                if (notMember.Count > 0) { error = "element(s) " + string.Join(", ", notMember.Select(Rid.Value)) + " are not direct members of group " + Rid.Value(reference.Id) + "."; return null; }
                // A removed pipe's "Center Line" companion is a DEPENDENT of the pipe and a member in its
                // own right. MEASURED (Revit 2025): leaving it in the group makes the swap of every other
                // instance fail at commit ("Can't make change to Group ... with more than one instance").
                // It leaves with its owner, and the plan says so.
                foreach (ElementId dep in DependentMembers(doc, p.Elements, current))
                    if (!p.Elements.Contains(dep)) { p.Elements.Add(dep); p.DependentsIncluded.Add(dep); }
                p.Expected = current.Where(id => !p.Elements.Contains(id)).ToList();
                if (p.Expected.Count == 0) { error = "removing every member empties the group; use operation=ungroup."; return null; }
            }
            p.Others = GroupsOf(p.Type).Where(g => g.Id != reference.Id).ToList();
            if (p.Others.Any(g => g.GroupId != ElementId.InvalidElementId))
            { error = "another instance of this type is nested inside a group; redefining the type from here is refused."; return null; }
            p.Scope = GroupRedefinitionRules.ResolveScope(r.Value<string>("scope"), p.Others.Count, out error, p.Op);
            if (p.Scope == null) return null;
            p.TypeAddition = GroupRedefinitionRules.IsTypeAddition(p.Op, p.Scope);
            if (p.Op == "add_members")
            {
                bool allowRegeneration = p.AcceptRegeneration = r.Value<bool?>("accept_member_regeneration") == true;
                try
                {
                    if (p.TypeAddition || p.Others.Count == 0)
                        HrzOwnership.Require(doc, p.Type, "group type", allowRegeneration, "accept_member_regeneration");
                    var affected = GroupRedefinitionRules.AffectedAddInstances(p.Scope, reference, p.Others);
                    p.AnnotationTargets = AnnotationDependencyAudit.TargetIds(doc,
                        affected.Select(g => g.Id).Concat(p.Elements).Concat(p.AllowedExtra), true);
                    foreach (long id in p.AnnotationTargets.OrderBy(id => id))
                    {
                        Element member = doc.GetElement(Rid.Make(id));
                        HrzOwnership.Require(doc, member, member is Group ? "group instance" : "group member", allowRegeneration, "accept_member_regeneration");
                    }
                }
                catch (ArgumentException ex) { error = ex.Message; return null; }
            }
            p.OldTypeName = p.Type.Name;
            if (p.Scope == GroupRedefinitionRules.ScopeThis && p.Others.Count > 0)
            { if (!NameFree(doc, p.Name, null, out error)) return null; }
            else p.Name = p.OldTypeName;
            foreach (Group o in p.Others) p.OtherCounts[o.Id] = o.GetMemberIds().Count;
            p.OtherIds = p.Others.Select(o => o.Id).ToList();
            if (p.Scope == GroupRedefinitionRules.ScopeAll)
            {
                foreach (Group o in p.Others) p.SigsBefore[o.Id] = Signatures(doc, o.GetMemberIds());
                p.AcceptLost = r.Value<bool?>("accept_lost_connections") ?? false;
                foreach (Group o in p.Others)
                {
                    var ids = o.GetMemberIds().ToList();
                    p.OtherMembers[o.Id] = EditMembers(doc, ids);
                    p.OtherMemberIds[o.Id] = ids;
                    p.OtherConnections[o.Id] = CaptureExternal(doc, o.Id, ids);
                }
                // scope=all swaps EVERY other instance onto the redefined type (Apply: o.GroupType = ...).
                // Revit does not "edit" that instance's members in place - it deletes the old ones and
                // instantiates fresh ones from the type definition, so EVERY member gets a NEW element id,
                // not only the ones actually added/removed. An instance-level override on an old member
                // (Mark, Comments) has nowhere to land on its replacement, and any tag or dimension that
                // referenced the old member is orphaned. Refuse when that would discard a real value,
                // unless the caller explicitly accepts it.
                if (p.Others.Count > 0 && r.Value<bool?>("accept_member_regeneration") != true)
                {
                    var atRisk = new List<string>();
                    foreach (Group o in p.Others)
                    {
                        List<string> found = MembersWithValuesAtRisk(doc, o);
                        if (found.Count > 0) atRisk.Add("group " + Rid.Value(o.Id) + ": " + string.Join("; ", found));
                    }
                    if (atRisk.Count > 0)
                    {
                        error = p.Others.Count + " other instance(s) of this type will be swapped to the redefined type " +
                            "(scope=all): every member of each is REGENERATED with a new element id, which discards any " +
                            "instance-level Mark/Comments on the old member and orphans any tag or dimension that " +
                            "referenced it. " + atRisk.Count + " instance(s) currently carry a non-empty Mark or " +
                            "Comments value on a member that would be lost this way: " + string.Join(" | ", atRisk) +
                            ". Refused; pass accept_member_regeneration=true to proceed anyway. Nothing was written.";
                        return null;
                    }
                }
            }
            p.TypeInstancesBefore = p.Others.Count + 1;
            if (p.Op == "add_members")
            {
                p.RequiredDeletions = p.TypeAddition || p.Others.Count == 0
                    ? new[] { Rid.Value(reference.Id), Rid.Value(p.TypeId) }
                    : new[] { Rid.Value(reference.Id) };
                p.DefinitionTypeId = p.TypeId;
            }
            return p;
        }

        /// <summary>Members of "other" that currently carry a non-empty Mark or Comments instance value -
        /// both lost when scope=all regenerates every member as a fresh element (see PlanMembers). A
        /// member whose parameters cannot be read is skipped: this is a warning gate, not a full audit,
        /// and a read failure must never manufacture a false "nothing at risk".</summary>
        private static List<string> MembersWithValuesAtRisk(Document doc, Group other)
        {
            var found = new List<string>();
            foreach (ElementId mid in Safe(() => other.GetMemberIds()) ?? new List<ElementId>())
            {
                Element m = doc.GetElement(mid);
                if (m == null) continue;
                string mark = null, comments = null;
                try { mark = m.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString(); } catch { }
                try { comments = m.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString(); } catch { }
                if (string.IsNullOrEmpty(mark) && string.IsNullOrEmpty(comments)) continue;
                string what = (string.IsNullOrEmpty(mark) ? "" : "Mark='" + mark + "'") +
                              (string.IsNullOrEmpty(mark) || string.IsNullOrEmpty(comments) ? "" : " ") +
                              (string.IsNullOrEmpty(comments) ? "" : "Comments='" + comments + "'");
                found.Add(Rid.Value(mid) + " " + what);
            }
            return found;
        }

        // ----------------------------------------------------------------- apply
        private static void Apply(Document doc, Plan p)
        {
            p.Shifts.Clear();
            p.RecreatedCopies.Clear();
            switch (p.Op)
            {
                case "create":
                {
                    Group g = doc.Create.NewGroup(p.Elements);
                    g.GroupType.Name = p.Name;
                    p.NewGroupId = g.Id; p.NewTypeId = g.GroupType.Id;
                    // What NewGroup really made against what was asked: the elements asked for plus, at most, the
                    // network's own companions (center lines of the curves, fittings Revit added over their connectors).
                    // Anything else is a surprise and refuses; the exact-equality comparison refused the valid case.
                    ClassifyExtraMembers(doc, p, g.GetMemberIds());
                    p.Closure = GroupMemberClosureRules.Evaluate(p.Elements.Select(Rid.Value), g.GetMemberIds().Select(Rid.Value),
                        ExtraKindsById(p), RepresentationalById(p));
                    if (!p.Closure.Holds) throw new InvalidOperationException(GroupMemberClosureRules.Refusal(p.Closure));
                    break;
                }
                case "add_members":
                case "remove_members":
                {
                    ElementId oldType = p.TypeId;
                    if (p.Op == "add_members")
                        p.ReferenceMembersBeforeRegeneration = ReadMemberBounds(doc, p.Expected.Concat(p.AllowedExtra), true);
                    p.Groups[0].UngroupMembers();
                    Group g = doc.Create.NewGroup(p.Expected);
                    p.NewGroupId = g.Id; p.NewTypeId = g.GroupType.Id;
                    if (p.Op == "add_members")
                    {
                        // What NewGroup really made: the planned set plus, at most, the added elements' own companions.
                        var made = ReadReferenceMembers(doc, p, g)?.Select(m => Rid.Make(m.Id)).ToList();
                        if (made == null) throw new InvalidOperationException("A recreated reference member could not be identified.");
                        var extra = made.Where(id => !p.Expected.Contains(id)).ToList();
                        if (p.Expected.Any(id => !made.Contains(id)) || extra.Any(id => !p.AllowedExtra.Contains(id)))
                            throw new InvalidOperationException("NewGroup did not take exactly the planned members (extra: " + string.Join(",", extra.Select(Rid.Value)) + ").");
                        p.DependentsIncluded.Clear(); p.DependentsIncluded.AddRange(extra);
                        p.Expected = made;
                    }
                    if (p.Scope == GroupRedefinitionRules.ScopeThis && p.Others.Count > 0) { g.GroupType.Name = p.Name; break; }
                    p.ConnectionRows.Clear();
                    List<long> oldReferenceIds = p.TypeAddition ? p.MembersBefore[p.GroupIds[0]].Select(Rid.Value).ToList() : null;
                    List<long> newReferenceIds = p.TypeAddition ? ReadReferenceMembers(doc, p, g).Select(m => m.Id).ToList() : null;
                    foreach (ElementId originalId in p.OtherIds)
                    {
                        var o = (Group)doc.GetElement(originalId);
                        // ChangeTypeId tells us when Revit has returned a replacement instance.
                        if (p.TypeAddition)
                        {
                            ElementId replacementId = o.ChangeTypeId(p.NewTypeId);
                            if (replacementId != ElementId.InvalidElementId)
                            {
                                p.RecreatedCopies.Add(originalId, replacementId);
                                o = doc.GetElement(replacementId) as Group ??
                                    throw new InvalidOperationException("The replacement for group " + Rid.Value(originalId) + " could not be read.");
                            }
                        }
                        else o.GroupType = g.GroupType;
                        doc.Regenerate();
                        string why = "the retained members could not be aligned using the old and new group member orders.";
                        double[] shift = p.TypeAddition
                            ? GroupLevelRules.MeasureRetainedTranslation(oldReferenceIds, newReferenceIds,
                                p.CopyPlacementsBefore[originalId].Members, HeightMembers(doc, o), GroupRedefinitionRules.BoxTolerance)
                            : GroupRedefinitionRules.MeasureShift(p.SigsBefore[originalId], Signatures(doc, o.GetMemberIds()), out why);
                        if (shift == null) throw new InvalidOperationException("instance " + Rid.Value(o.Id) + ": " + why);
                        if (Math.Abs(shift[0]) + Math.Abs(shift[1]) + Math.Abs(shift[2]) > 1e-9)
                        {
                            ElementTransformUtils.MoveElement(doc, o.Id, new XYZ(-shift[0], -shift[1], -shift[2]));
                            doc.Regenerate();
                        }
                        p.Shifts[o.Id] = shift;
                        // The regenerated members come back with OPEN connectors (measured): put every
                        // connection to an element outside the group back, by connector identity.
                        ReconnectRetained(doc, p, o, originalId);
                    }
                    if (doc.GetElement(oldType) != null)
                    {
                        ICollection<ElementId> deleted = doc.Delete(oldType);
                        if (p.Op == "add_members") p.ExplicitDeletes.UnionWith(deleted.Select(Rid.Value));
                    }
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
                case "change_group_type":
                {
                    Group g = p.Groups[0];
                    g.GroupType = p.Type;
                    doc.Regenerate();
                    GroupMemberMap map = GroupMemberEquivalence.Match(p.OldMemberFacts,
                        MemberFacts(doc, g), 2.0 / 304.8);
                    if (!map.Complete) throw new InvalidOperationException("Equivalent member mapping refused: " + map.Reason);
                    p.MemberMap = map.ByOldId;
                    p.RestoredTags.Clear();
                    foreach (TagSnapshot tag in p.TagsBefore)
                    {
                        long newId = map.ByOldId[tag.MemberId];
                        if (doc.GetElement(tag.TagId) != null) doc.Delete(tag.TagId);
                        Element target = doc.GetElement(Rid.Make(newId));
                        if (target == null) throw new InvalidOperationException("Equivalent member disappeared before tag recreation.");
                        IndependentTag made = IndependentTag.Create(doc, tag.TypeId, tag.ViewId,
                            new Reference(target), tag.HasLeader, tag.Orientation, tag.Head);
                        if (made == null) throw new InvalidOperationException("Revit did not create the replacement tag.");
                        p.RestoredTags.Add(new RestoredTag { OldId = tag.TagId, NewId = made.Id,
                            MemberId = newId, ViewId = tag.ViewId });
                    }
                    p.LostDimensions = LostDimensions(doc, p.AnnotationsBefore, map.ByOldId);
                    break;
                }
                case "set_group_level":
                {
                    Group g = p.Groups[0];
                    Level newLevel = doc.GetElement(p.LevelId) as Level;
                    if (newLevel == null) throw new InvalidOperationException("Target Level disappeared.");
                    if (!g.get_Parameter(BuiltInParameter.GROUP_LEVEL).Set(p.LevelId))
                        throw new InvalidOperationException("Revit refused GROUP_LEVEL.");
                    if (p.KeepPosition)
                    {
                        double offsetFeet = GroupLevelRules.OffsetMmForWorldZ(
                            p.PositionBefore.Z * 304.8, newLevel.Elevation * 304.8) / 304.8;
                        if (!g.get_Parameter(BuiltInParameter.GROUP_OFFSET_FROM_LEVEL).Set(offsetFeet))
                            throw new InvalidOperationException("Revit refused GROUP_OFFSET_FROM_LEVEL.");
                    }
                    break;
                }
                case "place_group":
                {
                    // 5.0.6: COPY THE PATTERN INSTANCE FIRST. A copy keeps the pattern's rotation and
                    // mirror state in one operation and one regeneration. The orientation search below
                    // places the group up to 100 times, each with a rotation, up to three mirrors and
                    // two regenerations, and does it in the rehearsal AND again in the apply: on a
                    // large group that held a client the whole 600 s (field 2026-11, 5.0.2). The search
                    // stays as the fallback when the copy is refused or does not match the pattern.
                    Group placed = TryCopyPattern(doc, p);
                    if (placed != null) { p.PatternRotation = 0; p.MirrorApplied = false; p.PlacedBy = "copy_of_pattern"; }
                    List<double> angles = new List<double>();
                    if (placed == null)
                    {
                    p.PlacedBy = "orientation_search";
                    var expectedAtTarget = GroupPatternRules.Translated(p.PatternSignatures,
                        p.TargetPoint.X - p.PositionBefore.X,
                        p.TargetPoint.Y - p.PositionBefore.Y, 0);
                    using (var baseline = new SubTransaction(doc))
                    {
                        baseline.Start();
                        Group temporary = doc.Create.PlaceGroup(p.TargetPoint, p.Type);
                        doc.Regenerate();
                        angles = GroupPatternRules.CandidateAngles(expectedAtTarget,
                            Signatures(doc, temporary.GetMemberIds()), p.TargetPoint.X, p.TargetPoint.Y);
                        if (!Guard.RollBack(baseline).Confirmed)
                            throw new InvalidOperationException("Pattern baseline rollback was not confirmed.");
                    }
                    }
                    // Empty when the copy already placed the group.
                    foreach (double angle in angles)
                    {
                        using (var trial = new SubTransaction(doc))
                        {
                            trial.Start();
                            Group candidate = doc.Create.PlaceGroup(p.TargetPoint, p.Type);
                            Line axis = Line.CreateBound(p.TargetPoint, p.TargetPoint + XYZ.BasisZ);
                            if (Math.Abs(angle) > 1e-10)
                                ElementTransformUtils.RotateElement(doc, candidate.Id, axis, angle);
                            doc.Regenerate();
                            bool matches = PatternMatches(doc, p, candidate);
                            bool mirrored = false;
                            if (!matches)
                            {
                                XYZ n = new XYZ(Math.Cos(angle), Math.Sin(angle), 0);
                                Plane plane = Plane.CreateByNormalAndOrigin(n, p.TargetPoint);
                                ElementTransformUtils.MirrorElements(doc, new[] { candidate.Id }, plane, false);
                                doc.Regenerate();
                                matches = PatternMatches(doc, p, candidate);
                                if (!matches)
                                {
                                    ElementTransformUtils.MirrorElements(doc, new[] { candidate.Id }, plane, false);
                                    XYZ other = new XYZ(-n.Y, n.X, 0);
                                    ElementTransformUtils.MirrorElements(doc, new[] { candidate.Id },
                                        Plane.CreateByNormalAndOrigin(other, p.TargetPoint), false);
                                    doc.Regenerate();
                                    matches = PatternMatches(doc, p, candidate);
                                }
                                mirrored = matches;
                            }
                            if (matches)
                            {
                                if (trial.Commit() != TransactionStatus.Committed)
                                    throw new InvalidOperationException("Pattern orientation trial did not commit.");
                                placed = candidate; p.PatternRotation = angle; p.MirrorApplied = mirrored;
                                break;
                            }
                            if (!Guard.RollBack(trial).Confirmed)
                                throw new InvalidOperationException("Pattern orientation trial rollback was not confirmed.");
                        }
                    }
                    if (placed == null) throw new InvalidOperationException("The placed group does not match the pattern under tested rotations/reflections; nothing is claimed copied.");
                    p.NewGroupId = placed.Id; p.NewTypeId = p.TypeId;
                    Parameter level = placed.get_Parameter(BuiltInParameter.GROUP_LEVEL);
                    Parameter offset = placed.get_Parameter(BuiltInParameter.GROUP_OFFSET_FROM_LEVEL);
                    if (level == null || offset == null || !level.Set(p.LevelId) || !offset.Set(p.PatternOffset.Value))
                        throw new InvalidOperationException("Revit refused the pattern level/offset.");
                    if (placed.WorksetId != p.PatternWorkset)
                    {
                        Parameter workset = placed.get_Parameter(BuiltInParameter.ELEM_PARTITION_PARAM);
                        if (workset == null || workset.IsReadOnly || !workset.Set(p.PatternWorkset.IntegerValue))
                            throw new InvalidOperationException("Revit refused the pattern workset.");
                    }
                    break;
                }
                case "ungroup":
                    p.Released.Clear();
                    for (int i = 0; i < p.Groups.Count; i++)
                    {
                        // The Group object is invalid once ungrouped: keep the id taken before, and Revit's own member list.
                        ElementId id = p.GroupIds[i];
                        p.Released[id] = (p.Groups[i].UngroupMembers() ?? new List<ElementId>()).ToList();
                    }
                    break;
            }
        }

        // ---------------------------------------------------------------- verify
        private static PostconditionCheck Verify(Document doc, Plan p, bool observesChanges = false)
        {
            switch (p.Op)
            {
                case "create":
                {
                    var c = new PostconditionCheck("group_exists", "members", "member_closure", "type_name");
                    Group g = p.NewGroupId == null ? null : doc.GetElement(p.NewGroupId) as Group;
                    c.Compare("group_exists", true, g != null);
                    IList<ElementId> held = g?.GetMemberIds()?.ToList();
                    if (held == null)
                    {
                        c.Unreadable("members", new JArray(p.Elements.Select(Rid.Value)), "the group does not re-read");
                        c.Unreadable("member_closure", new JArray(p.Elements.Select(Rid.Value)), "the group does not re-read");
                    }
                    else
                    {
                        // The elements asked for must ALL be there and every extra must be one the network itself adds.
                        ClassifyExtraMembers(doc, p, held);
                        GroupClosure closure = GroupMemberClosureRules.Evaluate(p.Elements.Select(Rid.Value), held.Select(Rid.Value),
                            ExtraKindsById(p), RepresentationalById(p));
                        c.Record("members", new JArray(p.Elements.Select(Rid.Value).OrderBy(x => x)),
                            new JArray(held.Select(Rid.Value).OrderBy(x => x)), closure.Holds);
                        c.Record("member_closure", new JObject { ["requested_physical"] = p.Elements.Count },
                            closure.ToJson(ExtraKindsById(p)), closure.Holds);
                    }
                    c.Compare("type_name", p.Name, g?.GroupType?.Name);
                    return c;
                }
                case "add_members":
                case "remove_members":
                {
                    var required = new List<string> { "reference_members", "type_name", "old_type", "instance_count", "other_instances", "external_connections" };
                    if (p.Op == "add_members") required.Add("annotation_references");
                    if (observesChanges) required.AddRange(new[] { "also_created", "deleted_elements", "out_of_scope_additions", "addition_roles" });
                    var c = new PostconditionCheck(required.ToArray());
                    Group g = p.NewGroupId == null ? null : doc.GetElement(p.NewGroupId) as Group;
                    var referenceMembers = p.Op == "add_members" && g != null ? ReadReferenceMembers(doc, p, g) : null;
                    if (p.Op == "add_members")
                        c.Record("reference_members", new JArray(p.Expected.Select(Rid.Value)),
                            g == null ? null : new JArray(g.GetMemberIds().Select(Rid.Value)),
                            referenceMembers != null && p.Expected.Select(Rid.Value).ToHashSet().SetEquals(referenceMembers.Select(m => m.Id)));
                    else SameIds(c, "reference_members", p.Expected, g?.GetMemberIds());
                    c.Compare("type_name", p.Name, g?.GroupType?.Name);
                    bool keepOld = p.Scope == GroupRedefinitionRules.ScopeThis && p.Others.Count > 0;
                    GroupType old = doc.GetElement(p.TypeId) as GroupType;
                    c.Compare("old_type", keepOld ? "kept" : "deleted", old == null ? "deleted" : "kept");
                    GroupType now = p.NewTypeId == null ? null : doc.GetElement(p.NewTypeId) as GroupType;
                    c.Compare("instance_count", keepOld ? 1 : p.Others.Count + 1, now == null ? -1 : GroupsOf(now).Count);
                    var evidence = new JArray(); bool held = true;
                    foreach (ElementId oid in p.OtherIds)
                    {
                        ElementId currentId = CurrentCopyId(p, oid);
                        Group o = doc.GetElement(currentId) as Group;
                        if (keepOld)
                        {
                            int n;
                            bool same = o != null && old != null && o.GroupType.Id == old.Id && p.OtherCounts.TryGetValue(oid, out n) && o.GetMemberIds().Count == n;
                            held &= same; evidence.Add(new JObject { ["group_id"] = Rid.Value(oid), ["untouched"] = same });
                            continue;
                        }
                        RetainedMatch m = o == null ? null : GroupRedefinitionRules.Match(p.SigsBefore[oid], Signatures(doc, o.GetMemberIds()),
                            p.Op == "add_members", p.Elements.Count + (p.Op == "add_members" ? p.DependentsIncluded.Count : 0), GroupRedefinitionRules.BoxTolerance);
                        bool ok = m != null && m.Held && o.GroupType.Id == p.NewTypeId;
                        string why = m?.Why ?? (o == null ? "instance no longer exists" : null);
                        JArray added = null;
                        JObject placement = null;
                        if (ok && p.TypeAddition)
                        {
                            added = referenceMembers == null ? null : VerifyAddedMembersInCopy(doc, referenceMembers,
                                o, p.Elements.Select(Rid.Value), p.DependentsIncluded.Select(Rid.Value), p.ReferencePlacementBefore,
                                p.CopyPlacementsBefore[oid], p.LoosePlacementBefore, p.CopyRotations[oid], out placement);
                            if (added == null)
                            {
                                ok = false;
                                why = placement?.Value<string>("reason") ?? "The added members could not be verified in their reference slots and placement.";
                            }
                        }
                        held &= ok;
                        // The instance still holds exactly what it held: Revit did not regenerate it at all.
                        if (!ok && o != null && p.OtherMemberIds.TryGetValue(oid, out List<ElementId> heldIds) && heldIds.SequenceEqual(o.GetMemberIds()))
                            why = "Revit did not regenerate this instance onto the redefined type (it still holds its old member ids " +
                                  string.Join(",", heldIds.Take(8).Select(Rid.Value)) + (heldIds.Count > 8 ? ",..." : "") + "); revit_errors says why when the commit refused";
                        var row = new JObject { ["group_id"] = Rid.Value(currentId), ["held"] = ok, ["members"] = m?.After ?? -1,
                            ["expected"] = m?.Expected ?? -1, ["why"] = why, ["added_member_ids"] = added };
                        if (placement != null) row["placement"] = placement;
                        evidence.Add(row);
                    }
                    c.Record("other_instances", p.OtherIds.Count, evidence, held);
                    if (p.Op == "add_members")
                    {
                        JObject annotationsAfter = AnnotationDependencyAudit.ReadTargets(doc, p.AnnotationTargets, true);
                        c.Record("annotation_references", p.AnnotationsBefore, annotationsAfter,
                            AnnotationDependencyRules.ReferencesPreserved(p.AnnotationsBefore, annotationsAfter));
                    }
                    int lost = 0, captured = 0; var lostRows = new JArray();
                    foreach (var kv in p.OtherConnections)
                        foreach (CapturedConnection x in kv.Value)
                        {
                            if (keepOld || x.Fact.MemberRemoved) continue;
                            captured++;
                            Element member = x.MemberAfter == null ? null : doc.GetElement(x.MemberAfter);
                            Connector mc = ConnectorById(member, x.MemberConnectorId);
                            Connector xc = ConnectorById(doc.GetElement(Rid.Make(x.Fact.ExternalId)), x.ExternalConnectorId);
                            if (mc != null && xc != null && SafeConnected(mc, xc)) continue;
                            lost++;
                            var lr = p.ConnectionRows.OfType<JObject>().FirstOrDefault(rw => rw.Value<long>("group_id") == x.Fact.GroupId &&
                                rw.Value<long>("member_id_before") == x.Fact.MemberId && rw.Value<long>("external_id") == x.Fact.ExternalId);
                            lostRows.Add(new JObject { ["group_id"] = x.Fact.GroupId, ["member_id"] = x.Fact.MemberId, ["external_id"] = x.Fact.ExternalId,
                                ["why"] = lr?.Value<string>("why") ?? "the regenerated member is not connected to it" });
                        }
                    c.Record("external_connections", new JObject { ["captured"] = captured, ["lost_allowed"] = p.AcceptLost },
                        new JObject { ["connected"] = captured - lost, ["lost"] = lost, ["lost_rows"] = lostRows, ["rows"] = p.ConnectionRows }, lost == 0 || p.AcceptLost);
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
                case "change_group_type":
                {
                    var c = new PostconditionCheck("instance_type", "equivalent_members", "tags_restored", "dimensions_reported");
                    Group g = doc.GetElement(p.GroupIds[0]) as Group;
                    c.Compare("instance_type", Rid.Value(p.TypeId), g == null ? -1 : Rid.Value(g.GroupType.Id));
                    GroupMemberMap current = g == null ? null : GroupMemberEquivalence.Match(
                        p.OldMemberFacts, MemberFacts(doc, g), 2.0 / 304.8);
                    c.Record("equivalent_members", p.OldMemberFacts.Count,
                        current?.ByOldId.Count ?? 0, current != null && current.Complete);
                    bool tagsHeld = p.RestoredTags.Count == p.TagsBefore.Count;
                    var rows = new JArray();
                    foreach (RestoredTag t in p.RestoredTags)
                    {
                        IndependentTag tag = doc.GetElement(t.NewId) as IndependentTag;
                        bool held = tag != null && tag.OwnerViewId == t.ViewId &&
                            tag.GetTaggedLocalElementIds().Any(x => Rid.Value(x) == t.MemberId);
                        tagsHeld &= held;
                        rows.Add(new JObject { ["old_tag_id"] = Rid.Value(t.OldId),
                            ["new_tag_id"] = Rid.Value(t.NewId), ["member_id"] = t.MemberId,
                            ["view_id"] = Rid.Value(t.ViewId), ["verified"] = held });
                    }
                    c.Record("tags_restored", p.TagsBefore.Count, rows, tagsHeld);
                    JArray nowLost = LostDimensions(doc, p.AnnotationsBefore, p.MemberMap);
                    c.Record("dimensions_reported", p.LostDimensions, nowLost,
                        JToken.DeepEquals(p.LostDimensions, nowLost));
                    return c;
                }
                case "set_group_level":
                {
                    var c = new PostconditionCheck("group_level", "position", "members", "annotations");
                    Group g = doc.GetElement(p.GroupIds[0]) as Group;
                    ElementId actualLevel = g?.get_Parameter(BuiltInParameter.GROUP_LEVEL)?.AsElementId();
                    c.Compare("group_level", Rid.Value(p.LevelId), actualLevel == null ? -1 : Rid.Value(actualLevel));
                    XYZ actual = (g?.Location as LocationPoint)?.Point;
                    bool held = !p.KeepPosition || actual != null && GroupLevelRules.PositionHeld(
                        p.PositionBefore.X * 304.8, p.PositionBefore.Y * 304.8, p.PositionBefore.Z * 304.8,
                        actual.X * 304.8, actual.Y * 304.8, actual.Z * 304.8, 1.0);
                    c.Record("position", new JArray(p.PositionBefore.X, p.PositionBefore.Y, p.PositionBefore.Z),
                        actual == null ? (JToken)JValue.CreateNull() : new JArray(actual.X, actual.Y, actual.Z), held);
                    SameIds(c, "members", p.MembersBefore[p.GroupIds[0]], g?.GetMemberIds());
                    JObject afterAnnotations = AnnotationDependencyAudit.Read(doc, p.GroupIds);
                    bool annotationsHeld = p.AnnotationsBefore != null &&
                        JToken.DeepEquals(p.AnnotationsBefore["by_view"], afterAnnotations["by_view"]) &&
                        (bool?)afterAnnotations["coverage_complete"] == true;
                    c.Record("annotations", p.AnnotationsBefore?["by_view"], afterAnnotations["by_view"], annotationsHeld);
                    return c;
                }
                case "place_group":
                {
                    var c = new PostconditionCheck("group_exists", "type", "position", "level", "offset", "workset", "members");
                    Group g = p.NewGroupId == null ? null : doc.GetElement(p.NewGroupId) as Group;
                    c.Compare("group_exists", true, g != null);
                    c.Compare("type", Rid.Value(p.TypeId), g == null ? -1 : Rid.Value(g.GroupType.Id));
                    XYZ at = (g?.Location as LocationPoint)?.Point;
                    bool located = at != null && GroupLevelRules.PositionHeld(
                        p.TargetPoint.X * 304.8, p.TargetPoint.Y * 304.8, p.TargetPoint.Z * 304.8,
                        at.X * 304.8, at.Y * 304.8, at.Z * 304.8, 1.0);
                    c.Record("position", new JArray(p.TargetPoint.X,p.TargetPoint.Y,p.TargetPoint.Z),
                        at == null ? (JToken)JValue.CreateNull() : new JArray(at.X,at.Y,at.Z), located);
                    ElementId lev = g?.get_Parameter(BuiltInParameter.GROUP_LEVEL)?.AsElementId();
                    c.Compare("level", Rid.Value(p.LevelId), lev == null ? -1 : Rid.Value(lev));
                    double? off = g?.get_Parameter(BuiltInParameter.GROUP_OFFSET_FROM_LEVEL)?.AsDouble();
                    c.Record("offset", p.PatternOffset, off, off.HasValue && Math.Abs(off.Value - p.PatternOffset.Value) <= 0.001 / 304.8);
                    c.Compare("workset", p.PatternWorkset.IntegerValue, g == null ? -1 : g.WorksetId.IntegerValue);
                    var expected = GroupPatternRules.Translated(p.PatternSignatures,
                        p.TargetPoint.X - p.PositionBefore.X, p.TargetPoint.Y - p.PositionBefore.Y, 0);
                    RetainedMatch match = g == null ? null : GroupRedefinitionRules.Match(expected,
                        Signatures(doc, g.GetMemberIds()), true, 0, GroupRedefinitionRules.BoxTolerance);
                    c.Record("members", expected.Count, match?.After ?? -1, match != null && match.Held);
                    return c;
                }
                default: // ungroup
                {
                    // Ungroup deletes the instance BY DESIGN: nothing is read through it. Members come from
                    // UngroupMembers' own answer (read-before only as fallback) and are re-read one by one.
                    var c = new PostconditionCheck("groups_removed", "members_released", "type_kept");
                    // MEASURED 2026-09-24 (Revit 2026, inside the rehearsal transaction): after
                    // UngroupMembers the Group element is still there, EMPTY, until the
                    // transaction ends. An instance with no members holds nothing and is
                    // counted as dissolved; an instance that still HAS members is the failure.
                    var still = p.GroupIds.Where(id => { Element e = doc.GetElement(id); return e is Group g && e.IsValidObject && MemberCount(g) > 0; }).Select(Rid.Value).ToList();
                    var emptyShells = p.GroupIds.Where(id => { Element e = doc.GetElement(id); return e is Group g && e.IsValidObject && MemberCount(g) == 0; }).Select(Rid.Value).ToList();
                    c.Record("groups_removed", p.GroupIds.Count, new JObject { ["still_holding_members"] = new JArray(still), ["empty_until_commit"] = new JArray(emptyShells) }, still.Count == 0);
                    var evidence = new JArray(); bool released = true; int checkedMembers = 0;
                    foreach (ElementId gid in p.GroupIds)
                    {
                        List<ElementId> got; p.Released.TryGetValue(gid, out got);
                        List<ElementId> before; p.MembersBefore.TryGetValue(gid, out before);
                        var ids = UngroupRules.MembersToCheck((got ?? new List<ElementId>()).Select(Rid.Value), (before ?? new List<ElementId>()).Select(Rid.Value));
                        var states = ids.Select(v =>
                        {
                            Element e = Rid.CanRepresent(v) ? doc.GetElement(Rid.Make(v)) : null;
                            return new UngroupMemberState { Id = v, Exists = e != null, GroupIdAfter = e == null ? UngroupRules.NoGroup : Rid.Value(e.GroupId) };
                        }).ToList();
                        long parent; if (!p.ParentBefore.TryGetValue(gid, out parent)) parent = UngroupRules.NoGroup;
                        bool ok = UngroupRules.MembersReleased(states, parent);
                        released &= ok; checkedMembers += states.Count;
                        evidence.Add(new JObject { ["group_id"] = Rid.Value(gid), ["members"] = states.Count,
                            ["source"] = got != null && got.Count > 0 ? "UngroupMembers" : "read_before",
                            ["expected_group_id"] = parent, ["not_released"] = new JArray(UngroupRules.NotReleased(states, parent)) });
                    }
                    c.Record("members_released", checkedMembers, evidence, released);
                    var types = new JArray(); bool typesHeld = true;
                    foreach (var kv in p.UngroupTypes)
                    {
                        GroupType t = doc.GetElement(kv.Key) as GroupType;
                        int after = t == null ? 0 : GroupsOf(t).Count(g => MemberCount(g) > 0);
                        string expectation;
                        bool held = UngroupRules.TypeHeld(kv.Value[0], kv.Value[1], t != null, after, out expectation);
                        typesHeld &= held;
                        types.Add(new JObject { ["type_id"] = Rid.Value(kv.Key), ["expected"] = expectation, ["exists"] = t != null, ["instances_after"] = after, ["held"] = held });
                    }
                    c.Record("type_kept", p.UngroupTypes.Count, types, typesHeld);
                    return c;
                }
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

        /// <summary>
        /// place_group's first route: a copy of the pattern instance moved to the target point, which
        /// keeps its rotation and mirror state. Returns the new group when exactly one group of the
        /// pattern's type was made and its members match the pattern there; otherwise the copy is
        /// rolled back and null tells the caller to search orientations instead.
        /// </summary>
        private static Group TryCopyPattern(Document doc, Plan p)
        {
            using (var copy = new SubTransaction(doc))
            {
                copy.Start();
                Group candidate = null;
                try
                {
                    XYZ shift = new XYZ(p.TargetPoint.X - p.PositionBefore.X, p.TargetPoint.Y - p.PositionBefore.Y, 0);
                    ICollection<ElementId> made = ElementTransformUtils.CopyElement(doc, p.Groups[0].Id, shift);
                    doc.Regenerate();
                    if (made != null && made.Count == 1)
                    {
                        Group g = doc.GetElement(made.First()) as Group;
                        if (g != null && g.GroupType.Id == p.Type.Id && PatternMatches(doc, p, g)) candidate = g;
                    }
                }
                catch (Exception ex) when (!(ex is OutOfMemoryException)) { candidate = null; }
                if (candidate != null && copy.Commit() == TransactionStatus.Committed) return candidate;
                if (copy.GetStatus() == TransactionStatus.Started && !Guard.RollBack(copy).Confirmed)
                    throw new InvalidOperationException("Pattern copy rollback was not confirmed.");
                return null;
            }
        }

        private static bool PatternMatches(Document doc, Plan p, Group placed)
        {
            var expected = GroupPatternRules.Translated(p.PatternSignatures,
                p.TargetPoint.X - p.PositionBefore.X,
                p.TargetPoint.Y - p.PositionBefore.Y, 0);
            RetainedMatch match = GroupRedefinitionRules.Match(expected,
                Signatures(doc, placed.GetMemberIds()), true, 0, GroupRedefinitionRules.BoxTolerance);
            return match.Held;
        }

        private static List<GroupMemberFact> MemberFacts(Document doc, Group group)
        {
            XYZ origin = (group.Location as LocationPoint)?.Point;
            if (origin == null) throw new InvalidOperationException("Group origin is unavailable.");
            var result = new List<GroupMemberFact>();
            foreach (ElementId id in group.GetMemberIds())
            {
                Element element = doc.GetElement(id);
                BoundingBoxXYZ b = element?.get_BoundingBox(null);
                if (b == null) throw new InvalidOperationException("Member " + Rid.Value(id) + " has no geometry box.");
                result.Add(new GroupMemberFact(Rid.Value(id),
                    element.Category == null ? -1 : Rid.Value(element.Category.Id),
                    (b.Min.X+b.Max.X)/2-origin.X, (b.Min.Y+b.Max.Y)/2-origin.Y,
                    (b.Min.Z+b.Max.Z)/2-origin.Z,
                    b.Max.X-b.Min.X,b.Max.Y-b.Min.Y,b.Max.Z-b.Min.Z));
            }
            return result;
        }

        private static List<TagSnapshot> CaptureMemberTags(Document doc, Group group)
        {
            var memberIds = new HashSet<long>(group.GetMemberIds().Select(Rid.Value));
            var result = new List<TagSnapshot>();
            foreach (IndependentTag tag in new FilteredElementCollector(doc).OfClass(typeof(IndependentTag)))
            {
                var all = tag.GetTaggedLocalElementIds().Select(Rid.Value).ToList();
                var relevant = all.Where(memberIds.Contains).ToList();
                if (relevant.Count == 0) continue;
                if (all.Count != 1 || relevant.Count != 1)
                    throw new InvalidOperationException("Tag " + Rid.Value(tag.Id) + " has multiple references; automatic recreation is ambiguous.");
                result.Add(new TagSnapshot { TagId = tag.Id, MemberId = relevant[0],
                    TypeId = tag.GetTypeId(), ViewId = tag.OwnerViewId,
                    Head = tag.TagHeadPosition, HasLeader = tag.HasLeader,
                    Orientation = tag.TagOrientation });
            }
            return result;
        }

        private static JArray LostDimensions(Document doc, JObject before,
            IDictionary<long,long> memberMap)
        {
            var lost = new JArray();
            if (before?["by_view"] is not JArray views) return lost;
            foreach (JObject view in views.OfType<JObject>())
            {
                long viewId = view.Value<long>("view_id");
                foreach (JObject row in (view["dimensions"] as JArray ?? new JArray()).OfType<JObject>())
                {
                    long id = row.Value<long>("id");
                    Dimension current = Rid.CanRepresent(id) ? doc.GetElement(Rid.Make(id)) as Dimension : null;
                    var refs = new HashSet<long>();
                    try { if (current?.References != null) foreach (Reference reference in current.References)
                            if (reference != null) refs.Add(Rid.Value(reference.ElementId)); }
                    catch { }
                    var expected = ((JArray)row["referenced_target_ids"]).Select(x => x.Value<long>())
                        .Where(memberMap.ContainsKey).Select(x => memberMap[x]).ToList();
                    if (current == null || expected.Any(x => !refs.Contains(x)))
                        lost.Add(new JObject { ["view_id"] = viewId, ["dimension_id"] = id,
                            ["reason"] = current == null ? "deleted_by_revit" : "reference_not_rebound_to_equivalent_member" });
                }
            }
            return lost;
        }

        private static int MemberCount(Group g)
        {
            try { return g.GetMemberIds().Count; } catch { return 0; }
        }

        /// <summary>Of the elements a rehearsal deleted, the hidden definition Revit keeps for the replaced group
        /// type. Measured on 2025: creating a group also makes an instance-less group of the same type that is NOT
        /// one of GroupType.Groups, with its own member copies and its own copy of the level; neither the type's
        /// dependents nor a Group collector name it, so it is recognised among the deletions themselves: a deleted
        /// group of that type that is not a visible instance, its members at any depth, and the level they stand on
        /// when no visible instance or member stands on it. They go with the type, never with somebody else's work.</summary>
        private static HashSet<long> TypeDefinitionClosure(Document doc, ElementId typeId, ICollection<long> deleted)
        {
            var ids = new HashSet<long>();
            GroupType t = typeId == null ? null : Safe(() => doc.GetElement(typeId) as GroupType);
            if (t == null || deleted == null || deleted.Count == 0) return ids;
            var visible = new HashSet<long> { Rid.Value(t.Id) };
            var visibleLevels = new HashSet<long>();
            foreach (Group g in GroupsOf(t)) CollectMembers(doc, g, visible, visibleLevels);
            // Only what was measured: the type itself among the deletions and exactly ONE top-level group of it
            // outside GroupType.Groups. A nested copy of the type inside somebody else's definition, or several
            // candidates, are not recognised: the rehearsal then refuses (fail closed) instead of authorising.
            if (!deleted.Contains(Rid.Value(t.Id))) return ids;
            var candidates = new List<Group>();
            foreach (long id in deleted)
            {
                if (visible.Contains(id) || !Rid.CanRepresent(id)) continue;
                if (Safe(() => doc.GetElement(Rid.Make(id))) is Group g && Safe(() => g.GroupType?.Id) == t.Id)
                    candidates.Add(g);
            }
            if (candidates.Count != 1 || Safe(() => candidates[0].GroupId) != ElementId.InvalidElementId) return ids;
            var definition = new HashSet<long>();
            var levels = new HashSet<long>();
            CollectMembers(doc, candidates[0], definition, levels);
            var deletedSet = new HashSet<long>(deleted);
            ids.UnionWith(definition.Where(id => !visible.Contains(id) && deletedSet.Contains(id)));
            ids.UnionWith(levels.Where(l => !visibleLevels.Contains(l) && deletedSet.Contains(l)
                && Safe(() => doc.GetElement(Rid.Make(l))) is Level));
            return ids;
        }

        private static void CollectMembers(Document doc, Element e, HashSet<long> ids, HashSet<long> levels)
        {
            ElementId own = e == null ? null : Safe(() => e.Id);
            if (own == null || !ids.Add(Rid.Value(own))) return;
            ElementId level = Safe(() => e.LevelId);
            if (level != null && level != ElementId.InvalidElementId) levels.Add(Rid.Value(level));
            // A member names other levels through its parameters too - a wall's top constraint
            // (MEASURED 2026-11-21, Revit 2024-2026: add_members deleted the hidden definition's
            // copy of the wall's base level AND of its top level; only the LevelId one was in
            // scope). The caller still keeps only deleted, non-visible elements that are Levels.
            foreach (Parameter prm in Safe(() => e.Parameters.Cast<Parameter>().ToList()) ?? new List<Parameter>())
            {
                if (!Holds(() => prm.StorageType == StorageType.ElementId)) continue;
                ElementId target = Safe(() => prm.AsElementId());
                if (target != null && target != ElementId.InvalidElementId
                    && Safe(() => doc.GetElement(target)) is Level)
                    levels.Add(Rid.Value(target));
            }
            if (e is Group g)
                foreach (ElementId m in Safe(() => g.GetMemberIds()) ?? new List<ElementId>())
                    CollectMembers(doc, Safe(() => doc.GetElement(m)), ids, levels);
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
            ["level_id"] = p.LevelId == null ? (JToken)JValue.CreateNull() : Rid.Value(p.LevelId),
            ["keep_position"] = p.KeepPosition,
            ["point_mm"] = p.TargetPoint == null ? (JToken)JValue.CreateNull() : new JArray(p.TargetPoint.X*304.8,p.TargetPoint.Y*304.8),
            ["type_instances_before"] = p.TypeInstancesBefore,
            ["other_instances_affected"] = p.Scope == GroupRedefinitionRules.ScopeAll ? new JArray(p.OtherIds.Select(Rid.Value)) : new JArray(),
            ["tags_to_recreate"] = p.TagsBefore.Count,
            ["dependents_included"] = new JArray(p.DependentsIncluded.Select(Rid.Value)),
            ["expected_representational_members"] = new JArray(p.Representational.Select(Rid.Value)),
            ["physical_members_requested"] = p.Op == "create" ? (JToken)p.Elements.Count : JValue.CreateNull(),
            ["external_connections_captured"] = p.OtherConnections.Values.Sum(v => v.Count),
            ["dimensions_lost_in_rehearsal"] = p.LostDimensions,
            ["note"] = p.Op == "add_members" || p.Op == "remove_members"
                ? "Revit has no edit-group API: the reference instance is ungrouped and regrouped (it gets a NEW group id; its instance parameters are not carried). " +
                  (p.Scope == GroupRedefinitionRules.ScopeAll && p.Others.Count > 0
                      ? p.Others.Count + " other instance(s) of this type are swapped to the redefined type. Revit " +
                        "does not edit their members in place: it deletes the old ones and instantiates fresh ones " +
                        "from the type definition, so EVERY member of each of these " + p.Others.Count + " instance(s) " +
                        "gets a NEW element id - not only the members actually added or removed. Any instance-level " +
                        "Mark/Comments on an old member, and any tag or dimension that referenced it, is lost/orphaned. " +
                        "Their connections to MEP elements outside the group come back OPEN after the regeneration " +
                        "(measured); they are reconnected by connector identity and reported in external_connections."
                      : p.Others.Count > 0
                          ? "scope=this_instance: this instance moves to a NEW type named '" + p.Name + "'; the other " + p.Others.Count +
                            " instance(s) keep the old type, so a typical floor is SPLIT into two types from here on. " +
                            "To change every instance use scope=all_instances (or edit_type_members for geometry)."
                          : "No other instance is touched.")
                : null
        };

        private static JObject ResultJson(Document doc, Plan p)
        {
            ElementId typeId = p.NewTypeId ?? p.TypeId;
            var o = new JObject { ["type_id"] = typeId == null ? (JToken)JValue.CreateNull() : Rid.Value(typeId) };
            if (p.NewGroupId != null) o["group_id"] = Rid.Value(p.NewGroupId);
            if (p.Op == "add_members" && p.NewGroupId != null) o["group_unique_id"] = doc.GetElement(p.NewGroupId)?.UniqueId;
            if (p.Op == "add_members" && p.Scope == GroupRedefinitionRules.ScopeAll && p.NewGroupId != null)
                o["affected_group_ids"] = new JArray(new[] { p.NewGroupId }.Concat(p.OtherIds.Select(id => CurrentCopyId(p, id))).Select(Rid.Value));
            if (p.LevelId != null) o["level_id"] = Rid.Value(p.LevelId);
            if (p.Op == "place_group") { o["mirrored_to_match_pattern"] = p.MirrorApplied; o["placed_by"] = p.PlacedBy; }
            if (p.Op == "change_group_type")
            {
                o["tags_recreated"] = new JArray(p.RestoredTags.Select(t => (JToken)new JObject {
                    ["old_tag_id"] = Rid.Value(t.OldId), ["new_tag_id"] = Rid.Value(t.NewId),
                    ["member_id"] = t.MemberId, ["view_id"] = Rid.Value(t.ViewId) }));
                o["dimensions_lost"] = p.LostDimensions;
            }
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
            if (p.Op == "add_members")
            {
                before["affected_identities"] = string.Join(";", p.AnnotationTargets.OrderBy(id => id)
                    .Select(id => id + "=" + gate.Document.GetElement(Rid.Make(id)).UniqueId));
                before["annotations"] = p.AnnotationsBefore["by_view"].ToString(Newtonsoft.Json.Formatting.None);
            }
            if (p.LevelId != null) before["level"] = Rid.Value(p.LevelId) + "|" + p.KeepPosition + "|" + p.PositionBefore;
            if (p.Op == "place_group") before["pattern"] = p.Groups[0].UniqueId + "|" + p.PositionBefore + "|" + p.TargetPoint + "|" + p.PatternRotation + "|" + p.PatternWorkset.IntegerValue;
            if (p.Op == "change_group_type") before["annotations"] = p.AnnotationsBefore.ToString(Newtonsoft.Json.Formatting.None);
            rp.Elements.Add(new PlannedElement { UniqueId = "groups:" + p.Op, Category = "group", Action = p.Op == "create" || p.Op == "duplicate_type" ? PlannedAction.Create : PlannedAction.Modify, BeforeValues = before });
            return rp;
        }

        private sealed class Plan
        {
            public string Op, Name, Scope, OldTypeName;
            public bool TypeAddition;
            public MembershipChanges InnerCommit;
            public JArray ApprovedAdditionRoles;
            public List<GroupRebuildChecks.Deletion> ApprovedDeletions;
            public bool AcceptRegeneration;
            public long[] RequiredDeletions;
            /// <summary>The group type add_members replaces, read before any change (see TypeDefinitionClosure).</summary>
            public ElementId DefinitionTypeId;
            public readonly HashSet<long> ExplicitDeletes = new HashSet<long>();
            public GroupPlacementSnapshot ReferencePlacementBefore;
            public readonly Dictionary<ElementId, GroupPlacementSnapshot> CopyPlacementsBefore = new Dictionary<ElementId, GroupPlacementSnapshot>();
            public readonly Dictionary<ElementId, double> CopyRotations = new Dictionary<ElementId, double>();
            public List<GroupMemberBounds> LoosePlacementBefore;
            public GroupType Type;
            public ElementId TypeId;
            public List<ElementId> GroupIds = new List<ElementId>(), OtherIds = new List<ElementId>();
            public List<GroupMemberBounds> ReferenceMembersBeforeRegeneration;
            public readonly Dictionary<ElementId, ElementId> RecreatedCopies = new Dictionary<ElementId, ElementId>();
            public int TypeInstancesBefore;
            public ElementId NewGroupId, NewTypeId;
            public ElementId LevelId;
            public bool KeepPosition;
            public XYZ PositionBefore;
            public XYZ TargetPoint;
            public double PatternRotation;
            public double? PatternOffset;
            public WorksetId PatternWorkset;
            public List<MemberSignature> PatternSignatures;
            public bool MirrorApplied;
            public string PlacedBy;
            public List<GroupMemberFact> OldMemberFacts;
            public List<TagSnapshot> TagsBefore = new List<TagSnapshot>();
            public Dictionary<long,long> MemberMap = new Dictionary<long,long>();
            public List<RestoredTag> RestoredTags = new List<RestoredTag>();
            public JArray LostDimensions = new JArray();
            public JObject AnnotationsBefore;
            public HashSet<long> AnnotationTargets;
            public readonly List<Group> Groups = new List<Group>();
            public readonly List<ElementId> Elements = new List<ElementId>();
            public List<ElementId> Expected = new List<ElementId>();
            public List<Group> Others = new List<Group>();
            public readonly Dictionary<ElementId, List<ElementId>> MembersBefore = new Dictionary<ElementId, List<ElementId>>();
            public readonly Dictionary<ElementId, List<MemberSignature>> SigsBefore = new Dictionary<ElementId, List<MemberSignature>>();
            public readonly Dictionary<ElementId, double[]> Shifts = new Dictionary<ElementId, double[]>();
            public readonly Dictionary<ElementId, int> OtherCounts = new Dictionary<ElementId, int>();
            public readonly Dictionary<ElementId, List<ElementId>> Released = new Dictionary<ElementId, List<ElementId>>();
            public readonly Dictionary<ElementId, long> ParentBefore = new Dictionary<ElementId, long>();
            public readonly Dictionary<ElementId, int[]> UngroupTypes = new Dictionary<ElementId, int[]>(); // type -> {instances before, ungrouped}
            public readonly List<ElementId> DependentsIncluded = new List<ElementId>();
            public readonly HashSet<ElementId> AllowedExtra = new HashSet<ElementId>();
            /// <summary>Why each accepted extra member of a new group is there (GroupMemberClosureRules.Kind*).</summary>
            public readonly Dictionary<ElementId, string> ExtraKinds = new Dictionary<ElementId, string>();
            /// <summary>Members that are a representation (a center line), not a physical element.</summary>
            public readonly HashSet<ElementId> Representational = new HashSet<ElementId>();
            public GroupClosure Closure;
            public bool AcceptLost;
            public readonly Dictionary<ElementId, List<EditMember>> OtherMembers = new Dictionary<ElementId, List<EditMember>>();
            public readonly Dictionary<ElementId, List<ElementId>> OtherMemberIds = new Dictionary<ElementId, List<ElementId>>();
            public readonly Dictionary<ElementId, List<CapturedConnection>> OtherConnections = new Dictionary<ElementId, List<CapturedConnection>>();
            public JArray ConnectionRows = new JArray();
        }

        /// <summary>A member-to-outside connection, with the connector ids that find both ends again.</summary>
        private sealed class CapturedConnection
        {
            public ExternalConnection Fact;
            public int MemberConnectorId, ExternalConnectorId;
            public ElementId MemberAfter;
        }

        private static List<CapturedConnection> CaptureExternal(Document doc, ElementId groupId, List<ElementId> memberIds)
        {
            var list = new List<CapturedConnection>();
            var members = new HashSet<ElementId>(memberIds);
            for (int k = 0; k < memberIds.Count; k++)
            {
                Element m = doc.GetElement(memberIds[k]);
                ConnectorManager cm = m == null ? null : Connectors(m);
                if (cm == null) continue;
                foreach (Connector c in cm.Connectors)
                {
                    if (c.ConnectorType == ConnectorType.Logical) continue;
                    foreach (Connector r in c.AllRefs)
                    {
                        if (!IsExternalRef(r, members)) continue;
                        list.Add(new CapturedConnection
                        {
                            Fact = new ExternalConnection { GroupId = Rid.Value(groupId), MemberIndex = k, MemberId = Rid.Value(m.Id), ExternalId = Rid.Value(r.Owner.Id),
                                ExternalIsCurve = r.Owner is MEPCurve, ExternalEndOfCurve = r.Owner is MEPCurve && r.ConnectorType == ConnectorType.End, Point = A(c.Origin),
                                ExternalPoint = A(r.Origin), GapBeforeFeet = c.Origin.DistanceTo(r.Origin) },
                            MemberConnectorId = c.Id, ExternalConnectorId = r.Id
                        });
                    }
                }
            }
            return list;
        }

        /// <summary>
        /// How many members of a group (nested groups included, up to three levels) are connected MEP elements:
        /// MEP curves, and family instances with a connector manager. `examples` carries the first few ids.
        /// Public to the assembly: write_params uses the same count to refuse a GROUP_LEVEL write.
        /// </summary>
        internal static int CountMepMembers(Document doc, Group group, out List<long> examples)
        {
            examples = new List<long>();
            return CountMepMembers(doc, group, 0, examples);
        }

        private static int CountMepMembers(Document doc, Group group, int depth, List<long> examples)
        {
            int count = 0;
            IList<ElementId> ids = Safe(() => group.GetMemberIds());
            if (ids == null) return 0;
            foreach (ElementId id in ids)
            {
                Element e = doc.GetElement(id);
                if (e is Group inner) { if (depth < 3) count += CountMepMembers(doc, inner, depth + 1, examples); continue; }
                if (!IsNetworkMember(e)) continue;
                count++;
                if (examples.Count < 5) examples.Add(Rid.Value(id));
            }
            return count;
        }

        /// <summary>
        /// A pipe, duct, conduit or cable tray, or a family instance with a physical-network connector (a fitting, an
        /// accessory, a piece of equipment). Electrical-only devices (a luminaire, an outlet) are not network members here.
        /// </summary>
        private static bool IsNetworkMember(Element e)
        {
            if (e is MEPCurve) return true;
            if (!(e is FamilyInstance)) return false;
            ConnectorManager manager = Safe(() => MepFacts.ManagerOf(e));
            if (manager == null) return false;
            try
            {
                foreach (Connector c in MepFacts.Ordered(manager))
                {
                    string domain = MepFacts.DomainName(c);
                    if (domain == "piping" || domain == "hvac" || domain == "cable_tray_conduit") return true;
                }
            }
            catch { }
            return false;
        }

        private static IDictionary<long, string> ExtraKindsById(Plan p) =>
            p.ExtraKinds.ToDictionary(kv => Rid.Value(kv.Key), kv => kv.Value);

        private static ISet<long> RepresentationalById(Plan p) =>
            new HashSet<long>(p.Representational.Select(Rid.Value));

        /// <summary>
        /// Name what NewGroup added beyond the elements asked for. A dependent of an element asked for is the network's
        /// representation (the Center Line of a curve); an MEP fitting joined to an element asked for is the fitting Revit
        /// put there itself (a tee that reduces on its own). Everything else stays unclassified and refuses.
        /// </summary>
        private static void ClassifyExtraMembers(Document doc, Plan p, IEnumerable<ElementId> held)
        {
            var asked = new HashSet<ElementId>(p.Elements);
            var extras = held.Where(id => !asked.Contains(id) && !p.ExtraKinds.ContainsKey(id)).ToList();
            if (extras.Count == 0) return;
            var dependents = new HashSet<ElementId>();
            foreach (ElementId owner in p.Elements)
            {
                IList<ElementId> deps = Safe(() => doc.GetElement(owner)?.GetDependentElements(null));
                if (deps != null) foreach (ElementId d in deps) dependents.Add(d);
            }
            foreach (ElementId id in extras)
            {
                if (dependents.Contains(id))
                {
                    p.ExtraKinds[id] = GroupMemberClosureRules.KindCenterLine;
                    p.Representational.Add(id);
                    continue;
                }
                if (doc.GetElement(id) is FamilyInstance fitting && JoinedToAny(fitting, asked))
                    p.ExtraKinds[id] = GroupMemberClosureRules.KindAutoFitting;
            }
        }

        /// <summary>True when an MEP element has a connector joined to one of `others`.</summary>
        private static bool JoinedToAny(Element e, HashSet<ElementId> others)
        {
            ConnectorManager manager = Safe(() => MepFacts.ManagerOf(e));
            if (manager == null) return false;
            foreach (Connector c in MepFacts.Ordered(manager))
            {
                try
                {
                    if (!c.IsConnected) continue;
                    foreach (Connector r in c.AllRefs.OfType<Connector>())
                        if (r.Owner != null && others.Contains(r.Owner.Id)) return true;
                }
                catch { }
            }
            return false;
        }

        /// <summary>Members of `pool` (or any element when pool is null) that are dependents of `owners`.</summary>
        private static List<ElementId> DependentMembers(Document doc, IEnumerable<ElementId> owners, List<ElementId> pool)
        {
            var found = new List<ElementId>();
            var inPool = pool == null ? null : new HashSet<ElementId>(pool);
            foreach (ElementId id in owners.ToList())
            {
                Element e = doc.GetElement(id);
                IList<ElementId> deps = Safe(() => e?.GetDependentElements(null));
                if (deps == null) continue;
                foreach (ElementId d in deps)
                {
                    if (d == id || found.Contains(d)) continue;
                    Element de = doc.GetElement(d);
                    if (de == null || de is ElementType || de is View) continue;
                    if (inPool != null ? inPool.Contains(d) : de.GroupId == ElementId.InvalidElementId) found.Add(d);
                }
            }
            return found;
        }

        /// <summary>After a swap: pair each retained old member with its regenerated twin by geometry and reconnect what it had outside.</summary>
        private static void ReconnectRetained(Document doc, Plan p, Group o, ElementId originalId)
        {
            if (!p.OtherConnections.TryGetValue(originalId, out List<CapturedConnection> conns) || conns.Count == 0) return;
            List<ElementId> newIds = o.GetMemberIds().ToList();
            List<EditMember> now = EditMembers(doc, newIds);
            List<EditMember> before = p.OtherMembers[originalId];
            Dictionary<int, int> map = GroupEditMatching.ByGeometry(before, now, EditTolerance);
            int removedExpected = p.Op == "remove_members" ? p.Elements.Count : 0;
            int unmatched = before.Count - map.Count;
            foreach (CapturedConnection c in conns)
            {
                var row = new JObject { ["group_id"] = Rid.Value(o.Id), ["member_id_before"] = c.Fact.MemberId, ["external_id"] = c.Fact.ExternalId };
                bool removed = !map.ContainsKey(c.Fact.MemberIndex) && unmatched <= removedExpected;
                c.Fact.MemberRemoved = removed;
                Element member = map.TryGetValue(c.Fact.MemberIndex, out int a) ? doc.GetElement(newIds[a]) : null;
                c.MemberAfter = member?.Id;
                Connector mc = ConnectorById(member, c.MemberConnectorId);
                Connector xc = ConnectorById(doc.GetElement(Rid.Make(c.Fact.ExternalId)), c.ExternalConnectorId);
                row["member_id_after"] = member == null ? (JToken)JValue.CreateNull() : Rid.Value(member.Id);
                if (mc != null && xc != null && SafeConnected(mc, xc)) { row["action"] = "kept"; p.ConnectionRows.Add(row); continue; }
                // A connection Revit kept "connected" while apart comes back as it was: judged by how far
                // the external connector is from where it belongs (its old place moved by what the
                // member moved), not by the gap it always had (field: 1.72 / 8.3 mm offsets lost).
                double? residual = mc != null && xc != null && !mc.IsConnected && !xc.IsConnected && c.Fact.ExternalPoint != null
                    ? X(PreservingReconnect.Desired(c.Fact.ExternalPoint, c.Fact.Point, A(mc.Origin))).DistanceTo(xc.Origin) : (double?)null;
                if (c.Fact.GapBeforeFeet > EditTolerance / 50) row["prior_gap_mm"] = Math.Round(c.Fact.GapBeforeFeet * 304.8, 3);
                string decision = PreservingReconnect.Decide(c.Fact, residual, 0, EditTolerance, out string why);
                if (decision == ExternalReconnectRules.Connect)
                {
                    try { mc.ConnectTo(xc); doc.Regenerate(); }
                    catch (Exception ex) { decision = ExternalReconnectRules.Lost; why = "Revit refused the reconnection: " + ex.Message; }
                }
                row["action"] = decision;
                if (why != null) row["why"] = why;
                p.ConnectionRows.Add(row);
            }
        }

        private sealed class TagSnapshot
        {
            public ElementId TagId, TypeId, ViewId;
            public long MemberId;
            public XYZ Head;
            public bool HasLeader;
            public TagOrientation Orientation;
        }

        private sealed class RestoredTag
        {
            public ElementId OldId, NewId, ViewId;
            public long MemberId;
        }

        /// <summary>
        /// Records and deletes warnings; an error rolls the transaction back and is reported -
        /// as text (Errors, kept for every caller) and per FailureMessage in Digest: definition
        /// GUID, failing and additional element ids, and what the caller's Describe adds.
        /// A caller may name errors it can resolve (MayResolve): those are resolved with
        /// DetachElements and the commit proceeds, and the caller must then verify what it
        /// needed survived - the resolution is never the last word.
        /// </summary>
        private sealed class FailureLog : IFailuresPreprocessor
        {
            public readonly List<string> Warnings = new List<string>();
            public readonly List<string> Errors = new List<string>();
            public readonly FailureDigest Digest = new FailureDigest();
            public Func<Document, FailureMessageAccessor, JObject> Describe;
            public Func<FailureMessageAccessor, bool> MayResolve;
            public int Resolved;
            public int Passes;

            /// <summary>
            /// ProceedWithCommit makes Revit regenerate and call this again. A detach that
            /// does not make its failure go away would be resolved forever on the UI thread,
            /// so a failure whose DetachElements was already attempted, or a pass beyond this
            /// bound, is an error and the transaction rolls back.
            /// </summary>
            internal const int MaxPasses = 6;

            public FailureProcessingResult PreprocessFailures(FailuresAccessor a)
            {
                Passes++;
                bool error = false;
                Document doc = Safe(() => a.GetDocument());
                var messages = a.GetFailureMessages().ToList();
                var resolvable = new List<FailureMessageAccessor>();
                foreach (FailureMessageAccessor f in messages)
                    if (f.GetSeverity() != FailureSeverity.Warning && MayResolve != null && Holds(() => MayResolve(f)) &&
                        Holds(() => f.HasResolutionOfType(FailureResolutionType.DetachElements)) &&
                        Passes <= MaxPasses &&
                        !Holds(() => a.GetAttemptedResolutionTypes(f).Contains(FailureResolutionType.DetachElements)))
                        resolvable.Add(f);
                bool onlyResolvable = messages.All(f => f.GetSeverity() == FailureSeverity.Warning || resolvable.Contains(f));
                foreach (FailureMessageAccessor f in messages)
                {
                    string text = Safe(() => f.GetDescriptionText()) ?? "(no text)";
                    bool warning = f.GetSeverity() == FailureSeverity.Warning;
                    string action = warning ? "deleted_warning" : (onlyResolvable && resolvable.Contains(f)) ? "resolved_detach" : "rolled_back";
                    string def = Safe(() => f.GetFailureDefinitionId().Guid.ToString());
                    string sev = f.GetSeverity().ToString();
                    JObject sample = null;
                    if (Describe != null && doc != null && Digest.WantsSample(def, sev, text, action))
                        sample = Safe(() => Describe(doc, f));
                    Digest.Add(def, sev, text, action,
                        Safe(() => f.GetFailingElementIds().Select(Rid.Value).ToList()),
                        Safe(() => f.GetAdditionalElementIds().Select(Rid.Value).ToList()), sample);
                    if (warning) { Warnings.Add(text); a.DeleteWarning(f); }
                    else if (action == "resolved_detach")
                    {
                        f.SetCurrentResolutionType(FailureResolutionType.DetachElements);
                        a.ResolveFailure(f);
                        Resolved++;
                    }
                    else { Errors.Add(text); error = true; }
                }
                if (error) return FailureProcessingResult.ProceedWithRollBack;
                return resolvable.Count > 0 ? FailureProcessingResult.ProceedWithCommit : FailureProcessingResult.Continue;
            }
        }
    }
}
