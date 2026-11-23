// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// horizun_manage_groups operation=edit_type_members - the typed "Edit Group".
//
// The API has no edit-group mode (GroupType has no member editor and a member of
// a group with several instances cannot be moved: MoveElement returns quietly and
// the commit fails - Core/GroupEditRules.cs has the measurements). So the edit is
// done the way Revit's own history shows it, inside ONE TransactionGroup:
//
//   1. capture: every instance's members in GetMemberIds order (the order Revit
//      keeps for a type), each instance's placement relative to the reference
//      (fitted from those members, residual checked), and every connection from
//      a member to an element OUTSIDE its group (member index + connector id).
//   2. ungroup the reference instance (its member ids survive), apply the edits
//      to those loose elements, regroup them as the new definition.
//   3. swap every other instance onto it. Revit regenerates their members with NEW
//      ids and OPEN connectors; each instance is re-read, its displacement
//      measured and corrected, and every member compared with the edited
//      reference carried into that instance's frame.
//   4. delete the old type, give the new one its name, and put every external
//      connection back by connector identity: coincident -> connect; a pipe/duct
//      end that has to follow the member -> stretch that end, then connect.
//   5. verify every instance (positions, ids mapped, connections) while the
//      TransactionGroup can still roll back; anything short of that rolls ALL of
//      it back. Re-verified after it assimilated.
//
// The rehearsal (dry_run, the default) runs the same five steps, commits inside
// the TransactionGroup so the regeneration Revit only performs at commit is
// rehearsed too, reports the effect on every instance, and rolls the group back.
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
        private const double EditTolerance = 0.5 / 304.8;
        private const string EditFaultKey = "edit_type_members_verify";

        private sealed class MemberEdit
        {
            public string Op;
            public List<ElementId> Ids = new List<ElementId>();
            public XYZ Vector, Start, End;
            public string Parameter;
            public JToken Value;
            public JObject Spec;
        }

        private sealed class InstanceState
        {
            public ElementId GroupId;
            public bool IsReference;
            public List<ElementId> MembersBefore = new List<ElementId>();
            public List<EditMember> Before = new List<EditMember>();
            public PlanarPlacement Placement;            // reference -> this instance
            public List<ElementId> MembersAfter = new List<ElementId>();
            public double[] CorrectedShift;
            public double WorstFeet = -1;
            public string Why;
        }

        private sealed class EditPlan
        {
            public Group Reference;            // valid only while planning
            public GroupType Type;
            public string TypeName;
            public ElementId TypeId;
            public List<MemberEdit> Edits = new List<MemberEdit>();
            public List<InstanceState> Instances = new List<InstanceState>();
            public List<ExternalConnection> Connections = new List<ExternalConnection>();
            public Dictionary<ExternalConnection, int[]> ConnectorIds = new Dictionary<ExternalConnection, int[]>(); // member connector id, external connector id
            public double StretchLimitFeet;
            public bool AcceptLost;
            public ElementId NewGroupId, NewTypeId;
            public List<EditMember> ReferenceAfter;
            public JArray ConnectionRows = new JArray();
            public JArray EditRows = new JArray();
            public JObject ParameterExpected = new JObject();
            public bool Rehearsing;
            public Dictionary<ElementId, Tuple<InstanceState, int>> FailureIndex;
            public ConnectionLedger Ledger = new ConnectionLedger();
            public Dictionary<long, EditMember> ExternalBefore = new Dictionary<long, EditMember>();
            public JArray DragRows = new JArray();
            public JArray SlopeRows = new JArray();
            public JArray SideEffectRows = new JArray();
            public int HorizontalSlopeChanges;
        }

        private static bool IsDisconnectFailure(FailureMessageAccessor f)
        {
            FailureDefinitionId id = f.GetFailureDefinitionId();
            return id == BuiltInFailures.ConnectorFailures.ElementsNeedToBeDisconnected ||
                   id == BuiltInFailures.ConnectorFailures.ElementsAreDisconnected;
        }

        private CommandResult ExecuteEditType(UIApplication app, JObject request)
        {
            GateResult gate = DocumentGate.ForMutation(app, request, Name); if (!gate.Ok) return gate.Refusal;
            Document doc = gate.Document;
            string error;
            EditPlan plan = PlanEditType(doc, request, out error);
            if (plan == null)
                return CommandResult.FailWithDetail(error + " Nothing was written.",
                    new JObject { ["state"] = "refused", ["operation"] = "edit_type_members", ["write_started"] = false });

            // Size first, before minutes of work: a synchronous call that cannot finish inside the
            // client's limit is refused and named the way through (field: 29 x 1,022 members ran
            // 21 minutes and the sync caller lost the result).
            int perInstance = plan.Instances[0].MembersBefore.Count;
            int[] seconds = GroupEditBudget.EstimatedSeconds(plan.Instances.Count, perInstance);
            if (GroupEditBudget.NeedsAsync(plan.Instances.Count, perInstance, Job.Ambient != null))
                return CommandResult.FailWithDetail(
                    "edit_type_members on " + plan.Instances.Count + " instances x " + perInstance + " members (" +
                    GroupEditBudget.MemberInstances(plan.Instances.Count, perInstance) + " member-instances, estimated " + seconds[0] + "-" + seconds[1] +
                    " s) is too long for a synchronous call. Send the SAME arguments through horizun_submit_job (tool=horizun_manage_groups) " +
                    "and follow it with horizun_job_status; the rehearsal and the apply both go that way. Nothing was written.",
                    new JObject
                    {
                        ["state"] = "refused", ["operation"] = "edit_type_members", ["write_started"] = false,
                        ["error_code"] = ErrorCodes.PrecondicionNoCumplida, ["route"] = "horizun_submit_job",
                        ["size"] = SizeJson(plan)
                    });
            CallProgress.Checkpoint("planned: " + plan.Instances.Count + " instances x " + perInstance + " members, " + plan.Ledger.Count + " connections captured", 1, 6);

            string hash = DocumentGate.PlanHash(request, "operation", "group_ids", "edits", "scope", "units", "reconnect_stretch_mm", "accept_lost_connections");
            ResolvedPlan resolved = ResolvedEdit(gate, app, plan);
            bool dry = request["dry_run"] == null || request.Value<bool>("dry_run");
            const string txName = "Horizun: edit group type members";

            if (dry)
            {
                JObject rehearsal = RunEdit(doc, plan, txName, true, out bool verified, out string why);
                if (rehearsal.Value<bool?>("rollback_confirmed") != true)
                    return CommandResult.FailWithDetail("The edit rehearsal rollback was not confirmed; model state is uncertain.",
                        new JObject { ["state"] = "uncertain", ["rehearsal"] = rehearsal, ["write_started"] = true });
                if (!verified)
                    return CommandResult.FailWithDetail("The edit rehearsal could not verify the change (" + why + "). Nothing was committed.",
                        new JObject { ["state"] = "refused", ["plan"] = EditPlanJson(plan), ["rehearsal"] = rehearsal, ["write_started"] = false });
                DocumentGate.RecordResolvedPlan(resolved);
                var result = new JObject { ["dry_run"] = true, ["plan"] = EditPlanJson(plan), ["rehearsal"] = rehearsal };
                ApplicationOutcome.StampRehearsal(result, 1, 0, 0, 0);
                DocumentGate.StampConfirmation(result, gate, Name, hash, true,
                    "the token binds the group type, every instance, its members and the edits.");
                return CommandResult.Ok(result);
            }

            DocumentGate.RecordResolvedPlan(resolved);
            CommandResult refusal = DocumentGate.RequireConfirmation(app, gate, request, Name, hash, resolved, null);
            if (refusal != null) return refusal;
            JObject applied = RunEdit(doc, plan, txName, false, out bool ok, out string failure);
            if (!ok)
            {
                bool rolled = applied.Value<bool?>("rollback_confirmed") == true;
                return CommandResult.FailWithDetail("edit_type_members failed and was " + (rolled ? "rolled back" : "NOT confirmed rolled back") +
                    ": " + failure, new JObject { ["state"] = rolled ? "rolled_back" : "uncertain", ["write_started"] = true, ["detail"] = applied });
            }
            PostconditionCheck after = VerifyEdit(doc, plan, false);
            applied["state"] = after.AllVerified ? "committed_verified" : "uncertain";
            applied["host_verified"] = after.AllVerified;
            applied["postconditions_after_assimilate"] = after.ToJson();
            ApplicationOutcome.StampApplied(applied, ApplicationOutcome.Committed, 1, 1, after.AllVerified ? 1 : 0, 0, 0, after.AllVerified ? 0 : 1);
            if (!after.AllVerified)
                return CommandResult.FailWithDetail("The edit committed but no longer re-reads as planned; state is uncertain.", applied);
            return CommandResult.Ok(applied);
        }

        // ------------------------------------------------------------------ plan
        private static EditPlan PlanEditType(Document doc, JObject r, out string error)
        {
            error = null;
            string scope = (r.Value<string>("scope") ?? "").Trim().ToLowerInvariant();
            if (scope != "type")
            { error = "edit_type_members changes the TYPE - every instance, as Edit Group does - and needs scope=type said explicitly."; return null; }
            JArray gids = r["group_ids"] as JArray;
            if (gids == null || gids.Count != 1) { error = "edit_type_members needs exactly one group_ids entry: any instance of the type."; return null; }
            long gid = gids[0].Value<long>();
            Group reference = Rid.CanRepresent(gid) ? doc.GetElement(Rid.Make(gid)) as Group : null;
            if (reference == null) { error = "group_ids names " + gid + ", which is not a group instance."; return null; }
            if (reference.GroupId != ElementId.InvalidElementId) { error = "group " + gid + " is nested inside group " + Rid.Value(reference.GroupId) + "; edit the outer group."; return null; }
            var p = new EditPlan { Reference = reference, Type = reference.GroupType, TypeId = reference.GroupType.Id, TypeName = reference.GroupType.Name };
            if (Kind(p.Type) != "model") { error = "edit_type_members edits model groups; '" + p.TypeName + "' is a " + Kind(p.Type) + " group."; return null; }
            var attached = Safe(() => p.Type.GetAvailableAttachedDetailGroupTypeIds());
            if (attached != null && attached.Count > 0)
            { error = "type '" + p.TypeName + "' has " + attached.Count + " attached detail group type(s); regrouping would orphan them."; return null; }
            double scale;
            string units = (r.Value<string>("units") ?? "mm").ToLowerInvariant();
            if (units == "mm") scale = 1 / 304.8; else if (units == "m") scale = 1000 / 304.8; else if (units == "feet") scale = 1; else { error = "units must be mm, m or feet."; return null; }
            double stretch = r.Value<double?>("reconnect_stretch_mm") ?? 50;
            if (double.IsNaN(stretch) || stretch < 0 || stretch > 1000) { error = "reconnect_stretch_mm must be 0..1000."; return null; }
            p.StretchLimitFeet = stretch / 304.8;
            p.AcceptLost = r.Value<bool?>("accept_lost_connections") ?? false;

            List<ElementId> refMembers = reference.GetMemberIds().ToList();
            var refSet = new HashSet<ElementId>(refMembers);
            if (refMembers.Any(id => doc.GetElement(id) is Group))
            { error = "the type contains nested groups; edit_type_members does not rewrite nested definitions."; return null; }

            JArray edits = r["edits"] as JArray;
            if (edits == null || edits.Count == 0 || edits.Count > 200) { error = "edits must hold 1..200 entries."; return null; }
            var claimed = new HashSet<ElementId>();
            for (int i = 0; i < edits.Count; i++)
            {
                JObject o = edits[i] as JObject;
                if (o == null) { error = "edits[" + i + "] is not an object."; return null; }
                var e = new MemberEdit { Op = (o.Value<string>("operation") ?? "").Trim().ToLowerInvariant(), Spec = o };
                var allowed = new HashSet<string> { "operation", "element_ids" };
                if (e.Op == "move") allowed.Add("vector");
                else if (e.Op == "set_curve") { allowed.Add("start"); allowed.Add("end"); }
                else if (e.Op == "set_parameter") { allowed.Add("parameter"); allowed.Add("value"); }
                else { error = "edits[" + i + "].operation must be move, set_curve or set_parameter."; return null; }
                foreach (var f in o.Properties()) if (!allowed.Contains(f.Name)) { error = "edits[" + i + "]." + f.Name + " is not applicable to " + e.Op + "."; return null; }
                foreach (JToken t in (o["element_ids"] as JArray) ?? new JArray())
                {
                    long v = t.Value<long>();
                    ElementId id = Rid.CanRepresent(v) ? Rid.Make(v) : ElementId.InvalidElementId;
                    if (!refSet.Contains(id)) { error = "edits[" + i + "] names " + v + ", which is not a member of group " + gid + " (use the member ids of THAT instance)."; return null; }
                    if (e.Op != "set_parameter" && !claimed.Add(id)) { error = "element " + v + " is moved by more than one edit; combine them first."; return null; }
                    e.Ids.Add(id);
                }
                if (e.Ids.Count == 0) { error = "edits[" + i + "] needs element_ids."; return null; }
                try
                {
                    if (e.Op == "move") e.Vector = EditPoint(o["vector"], scale, "vector");
                    if (e.Op == "set_curve")
                    {
                        if (e.Ids.Count != 1) { error = "edits[" + i + "]: set_curve takes exactly one element_id."; return null; }
                        if (!(doc.GetElement(e.Ids[0]).Location is LocationCurve lc) || lc.Curve == null)
                        { error = "edits[" + i + "]: element " + Rid.Value(e.Ids[0]) + " has no location line."; return null; }
                        e.Start = EditPoint(o["start"], scale, "start"); e.End = EditPoint(o["end"], scale, "end");
                        if (e.Start.DistanceTo(e.End) < 1e-9) { error = "edits[" + i + "]: start and end must differ."; return null; }
                    }
                    if (e.Op == "set_parameter")
                    {
                        e.Parameter = o.Value<string>("parameter");
                        e.Value = o["value"];
                        if (string.IsNullOrWhiteSpace(e.Parameter) || e.Value == null) { error = "edits[" + i + "]: set_parameter needs parameter and value."; return null; }
                        foreach (ElementId id in e.Ids)
                            if (doc.GetElement(id).LookupParameter(e.Parameter) == null)
                            { error = "edits[" + i + "]: element " + Rid.Value(id) + " has no parameter '" + e.Parameter + "'."; return null; }
                    }
                }
                catch (ArgumentException ex) { error = "edits[" + i + "]: " + ex.Message; return null; }
                p.Edits.Add(e);
            }

            // Every instance, the reference first; each paired by member order and placed by a fit.
            var refState = new InstanceState { GroupId = reference.Id, IsReference = true, MembersBefore = refMembers, Before = EditMembers(doc, refMembers),
                Placement = new PlanarPlacement() };
            p.Instances.Add(refState);
            foreach (Group g in GroupsOf(p.Type).Where(g => g.Id != reference.Id).OrderBy(g => Rid.Value(g.Id)))
            {
                if (g.GroupId != ElementId.InvalidElementId) { error = "instance " + Rid.Value(g.Id) + " is nested inside group " + Rid.Value(g.GroupId) + "; the type cannot be edited from here."; return null; }
                var s = new InstanceState { GroupId = g.Id, MembersBefore = g.GetMemberIds().ToList() };
                s.Before = EditMembers(doc, s.MembersBefore);
                string why;
                if (GroupEditMatching.ByOrder(refState.Before, s.Before, out why) == null)
                { error = "instance " + Rid.Value(g.Id) + " cannot be paired with the reference: " + why + "."; return null; }
                s.Placement = PlanarFit.Fit(GroupEditMatching.Flatten(refState.Before), GroupEditMatching.Flatten(s.Before));
                if (s.Placement == null) { error = "instance " + Rid.Value(g.Id) + " has no member with a readable location to place it by."; return null; }
                if (s.Placement.ResidualFeet > EditTolerance)
                { error = "instance " + Rid.Value(g.Id) + " is not a rigid copy of the reference (worst member " + Mm(s.Placement.ResidualFeet) +
                          " mm off): one of its members was changed outside group edit. Fix or exclude it first."; return null; }
                p.Instances.Add(s);
            }
            foreach (InstanceState s in p.Instances) CaptureConnections(doc, p, s);
            CaptureLedger(doc, p);
            foreach (long x in p.Connections.Select(c => c.ExternalId).Distinct())
                p.ExternalBefore[x] = EditMembers(doc, new[] { Rid.Make(x) })[0];
            return p;
        }

        /// <summary>A connector end as the ledger keys it: a member by instance/index, anything else by id.</summary>
        private static ConnectionLedger.End EndOf(Dictionary<ElementId, Tuple<int, int>> index, Connector c)
        {
            Tuple<int, int> at;
            if (index.TryGetValue(c.Owner.Id, out at)) return new ConnectionLedger.End { Instance = at.Item1, Member = at.Item2, Connector = c.Id };
            return new ConnectionLedger.End { ElementId = Rid.Value(c.Owner.Id), Connector = c.Id };
        }

        private static bool Physical(Connector r)
            => r != null && r.Owner != null && r.ConnectorType != ConnectorType.Logical && !(r.Owner is MEPSystem);

        /// <summary>
        /// Every physical connection of every member, and every connection of the elements one hop
        /// outside (a riser segment's other end): what Revit's regeneration or a failure resolution
        /// might break without the member positions showing it.
        /// </summary>
        private static void CaptureLedger(Document doc, EditPlan p)
        {
            var index = new Dictionary<ElementId, Tuple<int, int>>();
            for (int si = 0; si < p.Instances.Count; si++)
                for (int k = 0; k < p.Instances[si].MembersBefore.Count; k++) index[p.Instances[si].MembersBefore[k]] = Tuple.Create(si, k);
            var outside = new HashSet<ElementId>();
            foreach (InstanceState s in p.Instances)
                foreach (ElementId id in s.MembersBefore)
                {
                    ConnectorManager cm = Connectors(doc.GetElement(id));
                    if (cm == null) continue;
                    foreach (Connector c in cm.Connectors)
                    {
                        if (!Physical(c)) continue;
                        foreach (Connector r in c.AllRefs)
                        {
                            if (!Physical(r) || r.Owner.Id == id) continue;
                            p.Ledger.Add(EndOf(index, c), EndOf(index, r), c.Origin.DistanceTo(r.Origin));
                            if (!index.ContainsKey(r.Owner.Id)) outside.Add(r.Owner.Id);
                        }
                    }
                }
            foreach (ElementId x in outside)
            {
                ConnectorManager cm = Connectors(doc.GetElement(x));
                if (cm == null) continue;
                foreach (Connector c in cm.Connectors)
                {
                    if (!Physical(c)) continue;
                    foreach (Connector r in c.AllRefs)
                        if (Physical(r) && r.Owner.Id != x) p.Ledger.Add(EndOf(index, c), EndOf(index, r), c.Origin.DistanceTo(r.Origin));
                }
            }
        }

        /// <summary>The connector a ledger end names now: members through the instance's current member list.</summary>
        private static Connector ResolveEnd(Document doc, EditPlan p, ConnectionLedger.End e)
        {
            if (!e.IsMember) return ConnectorById(doc.GetElement(Rid.Make(e.ElementId)), e.Connector);
            InstanceState s = p.Instances[e.Instance];
            List<ElementId> list = s.MembersAfter.Count > 0 ? s.MembersAfter : s.MembersBefore;
            return e.Member < list.Count ? ConnectorById(doc.GetElement(list[e.Member]), e.Connector) : null;
        }

        private static JObject EndJson(EditPlan p, ConnectionLedger.End e)
        {
            if (!e.IsMember) return new JObject { ["element_id"] = e.ElementId, ["connector"] = e.Connector };
            InstanceState s = p.Instances[e.Instance];
            return new JObject { ["instance_group_id"] = Rid.Value(s.GroupId), ["member_index"] = e.Member,
                ["reference_member_id"] = Rid.Value(p.Instances[0].MembersBefore[e.Member]), ["connector"] = e.Connector };
        }

        private static XYZ EditPoint(JToken t, double scale, string name)
        {
            JArray a = t as JArray;
            if (a == null || a.Count != 3) throw new ArgumentException(name + " must be [x,y,z].");
            double x = a[0].Value<double>(), y = a[1].Value<double>(), z = a[2].Value<double>();
            if (new[] { x, y, z }.Any(v => double.IsNaN(v) || double.IsInfinity(v))) throw new ArgumentException(name + " must be finite.");
            return new XYZ(x * scale, y * scale, z * scale);
        }

        private static List<EditMember> EditMembers(Document doc, IEnumerable<ElementId> ids)
        {
            var list = new List<EditMember>();
            foreach (ElementId id in ids)
            {
                Element e = doc.GetElement(id);
                var m = new EditMember { Id = Rid.Value(id), Key = e == null ? "missing" : (e.Category == null ? -1 : Rid.Value(e.Category.Id)) + ":" + Rid.Value(e.GetTypeId()) };
                if (e?.Location is LocationPoint lp && lp.Point != null) m.Points.Add(A(lp.Point));
                else if (e?.Location is LocationCurve lc && lc.Curve != null) { m.Points.Add(A(lc.Curve.GetEndPoint(0))); m.Points.Add(A(lc.Curve.GetEndPoint(1))); }
                list.Add(m);
            }
            return list;
        }

        private static double[] A(XYZ p) => new[] { p.X, p.Y, p.Z };
        private static string Mm(double feet) => Math.Round(feet * 304.8, 2).ToString(System.Globalization.CultureInfo.InvariantCulture);
        private static XYZ X(double[] a) => new XYZ(a[0], a[1], a[2]);

        private static ConnectorManager Connectors(Element e)
        {
            if (e is MEPCurve c) return c.ConnectorManager;
            if (e is FamilyInstance f) return Safe(() => f.MEPModel?.ConnectorManager);
            return null;
        }

        private static Connector ConnectorById(Element e, int id)
        {
            ConnectorManager cm = e == null ? null : Connectors(e);
            if (cm == null) return null;
            foreach (Connector c in cm.Connectors) if (c.Id == id) return c;
            return null;
        }

        private static bool IsExternalRef(Connector r, HashSet<ElementId> members)
        {
            if (r == null || r.Owner == null || r.ConnectorType == ConnectorType.Logical || r.Owner is MEPSystem) return false;
            return !members.Contains(r.Owner.Id);
        }

        private static void CaptureConnections(Document doc, EditPlan p, InstanceState s)
        {
            var members = new HashSet<ElementId>(s.MembersBefore);
            for (int k = 0; k < s.MembersBefore.Count; k++)
            {
                Element m = doc.GetElement(s.MembersBefore[k]);
                ConnectorManager cm = m == null ? null : Connectors(m);
                if (cm == null) continue;
                foreach (Connector c in cm.Connectors)
                {
                    if (c.ConnectorType == ConnectorType.Logical) continue;
                    foreach (Connector r in c.AllRefs)
                    {
                        if (!IsExternalRef(r, members)) continue;
                        bool curve = r.Owner is MEPCurve;
                        bool end = curve && (r.ConnectorType == ConnectorType.End);
                        var ec = new ExternalConnection { GroupId = Rid.Value(s.GroupId), MemberIndex = k, MemberId = Rid.Value(m.Id),
                            ExternalId = Rid.Value(r.Owner.Id), ExternalIsCurve = curve, ExternalEndOfCurve = end, Point = A(c.Origin),
                            ExternalPoint = A(r.Origin), GapBeforeFeet = c.Origin.DistanceTo(r.Origin) };
                        p.Connections.Add(ec);
                        p.ConnectorIds[ec] = new[] { c.Id, r.Id };
                    }
                }
            }
        }

        // ----------------------------------------------------------------- apply
        private static JObject RunEdit(Document doc, EditPlan p, string txName, bool rehearse, out bool verified, out string why)
        {
            verified = false; why = null;
            var failures = new FailureLog();
            p.Rehearsing = rehearse;
            var report = new JObject { ["operation"] = "edit_type_members", ["rehearsal"] = rehearse };
            using (var group = new TransactionGroup(doc, txName))
            {
                if (group.Start() != TransactionStatus.Started) { why = "the TransactionGroup did not start"; report["rollback_confirmed"] = true; return report; }
                string failure = null;
                var tx = new Transaction(doc, txName);
                try
                {
                    if (tx.Start() != TransactionStatus.Started) throw new InvalidOperationException("the transaction did not start");
                    failures.Describe = (d, f) => DescribeFailure(d, p, f);
                    // "Elements need to be disconnected" is resolved (DetachElements) only so the
                    // commit can go on to be CHECKED: every captured connection - internal,
                    // external, one hop outside - is re-read right after, and one Revit took
                    // apart that the model had rolls the whole TransactionGroup back.
                    failures.MayResolve = IsDisconnectFailure;
                    Quiet(tx, failures);
                    ApplyEdit(doc, p);
                    doc.Regenerate();
                    CallProgress.Checkpoint("verifying before commit", 4, 6);
                    PostconditionCheck inside = VerifyEdit(doc, p, true);
                    report["postconditions_before_commit"] = inside.ToJson();
                    if (!inside.AllVerified) throw new InvalidOperationException("a postcondition failed before commit: " + FailedNames(inside));
                    CallProgress.Checkpoint("committing (Revit regenerates and checks every instance)", 5, 6);
                    TransactionStatus st = tx.Commit();
                    if (st != TransactionStatus.Committed)
                        throw new InvalidOperationException("Revit did not commit (" + st + ")" + (failures.Errors.Count > 0
                            ? ": " + string.Join(" | ", failures.Errors.Distinct().Take(5).Select(t => t + " (x" + failures.Errors.Count(x => x == t) + ")")) +
                              " - see revit_failures for the element ids and the connection Revit objects to"
                            : ""));
                    CallProgress.Checkpoint("committed; verifying after commit" + (failures.Resolved > 0 ? " (" + failures.Resolved + " disconnect failure(s) resolved by Revit)" : ""), 6, 6);
                    PostconditionCheck committed = VerifyEdit(doc, p, true);
                    report["postconditions"] = committed.ToJson();
                    if (!committed.AllVerified) throw new InvalidOperationException("a postcondition failed after commit: " + FailedNames(committed));
                }
                catch (Exception ex) { failure = ex.Message; }
                finally { try { if (tx.GetStatus() == TransactionStatus.Started) Guard.RollBack(tx); } catch { } tx.Dispose(); }

                report["instances"] = InstanceRows(p);
                report["external_connections"] = p.ConnectionRows;
                report["edits_on_reference"] = p.EditRows;
                report["dragged_members"] = new JObject { ["count"] = p.DragRows.Count, ["note"] = "members of the reference that moved although not named: Revit drags what is connected to a moved fitting, exactly as Edit Group in the UI does. Every instance gets the same.", ["rows"] = Cap(p.DragRows, 60) };
                report["slope_changes"] = new JObject { ["horizontal_runs_changed"] = p.HorizontalSlopeChanges, ["rows"] = Cap(p.SlopeRows, 60) };
                report["external_side_effects"] = new JObject { ["count"] = p.SideEffectRows.Count, ["note"] = "elements OUTSIDE the group that this edit moves or stretches (all instances).", ["rows"] = Cap(p.SideEffectRows, 120) };
                if (failures.Resolved > 0) report["revit_disconnects_resolved"] = failures.Resolved;
                report["revit_warnings"] = new JArray(failures.Warnings);
                if (failures.Errors.Count > 0) report["revit_errors"] = new JArray(failures.Errors.Distinct().Select(t => t + " (x" + failures.Errors.Count(x => x == t) + ")"));
                if (failures.Digest.Total > 0) report["revit_failures"] = failures.Digest.ToJson();
                report["result"] = new JObject
                {
                    ["type_name"] = p.TypeName,
                    ["new_type_id"] = p.NewTypeId == null ? (JToken)JValue.CreateNull() : Rid.Value(p.NewTypeId),
                    ["old_type_id"] = Rid.Value(p.TypeId),
                    ["reference_group_id_before"] = Rid.Value(p.Instances[0].GroupId),
                    ["reference_group_id_after"] = p.NewGroupId == null ? (JToken)JValue.CreateNull() : Rid.Value(p.NewGroupId),
                    ["ids_note"] = rehearse
                        ? "Rehearsal ids are provisional: the rolled-back model never kept them; the apply reports its own."
                        : "The reference instance keeps its member ids and gets a new group id; every other instance keeps its group id and gets NEW member ids (see member_id_map)."
                };
                if (failure == null && !rehearse)
                {
                    TransactionStatus gs = group.Assimilate();
                    if (gs != TransactionStatus.Committed) { why = "the TransactionGroup did not assimilate (" + gs + ")"; report["rollback_confirmed"] = false; return report; }
                    verified = true; report["rollback_confirmed"] = false; return report;
                }
                Guard.RollbackResult rb;
                try { rb = Guard.RollBack(group); } catch (Exception ex) { report["rollback_confirmed"] = false; report["rollback_error"] = ex.Message; why = failure ?? "rollback threw"; return report; }
                report["rollback_status"] = rb.StatusName;
                report["rollback_confirmed"] = rb.Confirmed;
                why = failure;
                verified = failure == null;
                return report;
            }
        }

        private static JArray Cap(JArray rows, int max)
        {
            if (rows.Count <= max) return rows;
            var a = new JArray(rows.Take(max));
            a.Add(new JObject { ["truncated"] = rows.Count - max });
            return a;
        }

        // ------------------------------------------------------------ failures
        /// <summary>Which instance/member an id is, by the member lists as they stand now.</summary>
        private static Dictionary<ElementId, Tuple<InstanceState, int>> MemberIndex(EditPlan p)
        {
            var map = new Dictionary<ElementId, Tuple<InstanceState, int>>();
            foreach (InstanceState s in p.Instances)
            {
                List<ElementId> list = s.MembersAfter.Count > 0 ? s.MembersAfter : s.MembersBefore;
                for (int k = 0; k < list.Count; k++) map[list[k]] = Tuple.Create(s, k);
            }
            return map;
        }

        private static JObject DescribeElement(Document doc, EditPlan p, Dictionary<ElementId, Tuple<InstanceState, int>> index, ElementId id)
        {
            Element e = doc.GetElement(id);
            var o = new JObject { ["id"] = Rid.Value(id), ["category"] = e?.Category?.Name, ["built_in_category"] = CategoryKey.Of(e) };
            Tuple<InstanceState, int> at;
            if (index.TryGetValue(id, out at))
            {
                o["role"] = "member";
                o["instance_group_id"] = Rid.Value(at.Item1.GroupId);
                o["reference_instance"] = at.Item1.IsReference;
                o["member_index"] = at.Item2;
                o["reference_member_id"] = Rid.Value(p.Instances[0].MembersBefore[at.Item2]);
            }
            else o["role"] = p.Connections.Any(c => c.ExternalId == Rid.Value(id)) ? "external_connected_to_a_member" : "other";
            if (e?.Location is LocationPoint lp && lp.Point != null) o["point_mm"] = new JArray(A(lp.Point).Select(v => Math.Round(v * 304.8, 2)));
            else if (e?.Location is LocationCurve lc && lc.Curve != null)
                o["ends_mm"] = new JArray(new JArray(A(lc.Curve.GetEndPoint(0)).Select(v => Math.Round(v * 304.8, 2))),
                                          new JArray(A(lc.Curve.GetEndPoint(1)).Select(v => Math.Round(v * 304.8, 2))));
            return o;
        }

        /// <summary>The connector pairs two failing elements share - the connection Revit objects to - with gap and angle.</summary>
        private static JArray ConnectionsBetween(Document doc, IList<ElementId> ids)
        {
            var rows = new JArray();
            for (int i = 0; i < ids.Count; i++)
            {
                ConnectorManager cm = Connectors(doc.GetElement(ids[i]));
                if (cm == null) continue;
                foreach (Connector c in cm.Connectors)
                {
                    if (c.ConnectorType == ConnectorType.Logical) continue;
                    foreach (Connector r in c.AllRefs)
                    {
                        if (r?.Owner == null || r.ConnectorType == ConnectorType.Logical || r.Owner is MEPSystem) continue;
                        int j = ids.IndexOf(r.Owner.Id);
                        if (j <= i) continue;
                        double angle = double.NaN;
                        try { angle = c.CoordinateSystem.BasisZ.AngleTo(r.CoordinateSystem.BasisZ.Negate()) * 180 / Math.PI; } catch { }
                        rows.Add(new JObject { ["a"] = Rid.Value(ids[i]), ["a_connector"] = c.Id, ["b"] = Rid.Value(ids[j]), ["b_connector"] = r.Id,
                            ["gap_mm"] = Math.Round(c.Origin.DistanceTo(r.Origin) * 304.8, 3),
                            ["direction_mismatch_deg"] = double.IsNaN(angle) ? (JToken)JValue.CreateNull() : Math.Round(angle, 3) });
                    }
                }
            }
            return rows;
        }

        private static JObject DescribeFailure(Document doc, EditPlan p, FailureMessageAccessor f)
        {
            var ids = f.GetFailingElementIds().Concat(f.GetAdditionalElementIds()).Distinct().Take(8).ToList();
            if (p.FailureIndex == null) p.FailureIndex = MemberIndex(p);
            var o = new JObject { ["elements"] = new JArray(ids.Select(id => DescribeElement(doc, p, p.FailureIndex, id))) };
            if (ids.Count >= 2) o["connections_between"] = ConnectionsBetween(doc, ids);
            return o;
        }

        private static string FailedNames(PostconditionCheck c)
            => RehearsalWhy(new JObject { ["postconditions"] = c.ToJson() }).Replace("failed: ", "");

        private static void ApplyEdit(Document doc, EditPlan p)
        {
            InstanceState refState = p.Instances[0];
            // The Group object is invalid once ungrouped (and after a rollback): only ids are kept past this line.
            ICollection<ElementId> loose = ((Group)doc.GetElement(refState.GroupId)).UngroupMembers();
            var looseSet = new HashSet<ElementId>(loose ?? new List<ElementId>());
            if (!refState.MembersBefore.All(looseSet.Contains))
                throw new InvalidOperationException("ungrouping the reference did not return its member ids; nothing is edited on a guess.");
            p.EditRows.Clear();
            foreach (MemberEdit e in p.Edits)
            {
                if (e.Op == "move") ElementTransformUtils.MoveElements(doc, e.Ids, e.Vector);
                else if (e.Op == "set_curve") ((LocationCurve)doc.GetElement(e.Ids[0]).Location).Curve = Line.CreateBound(e.Start, e.End);
                else foreach (ElementId id in e.Ids) SetParameter(doc.GetElement(id), e.Parameter, e.Value);
            }
            doc.Regenerate();
            // The edit on the reference, re-read and judged before anything else depends on it.
            var refBefore = refState.Before.ToDictionary(m => m.Id);
            foreach (MemberEdit e in p.Edits)
                foreach (ElementId id in e.Ids)
                {
                    Element el = doc.GetElement(id);
                    var now = EditMembers(doc, new[] { id })[0];
                    bool ok; JToken expected;
                    if (e.Op == "move")
                    {
                        var want = new EditMember { Key = now.Key, Points = refBefore[Rid.Value(id)].Points.Select(q => new[] { q[0] + e.Vector.X, q[1] + e.Vector.Y, q[2] + e.Vector.Z }).ToList() };
                        ok = GroupEditMatching.Distance(want, now) <= EditTolerance;
                        expected = new JArray(want.Points.Select(q => new JArray(q.Select(v => v * 304.8))));
                    }
                    else if (e.Op == "set_curve")
                    {
                        var lc = el.Location as LocationCurve;
                        string verdict = lc == null ? CurveChangeRules.OffLine : CurveChangeRules.Judge(refBefore[Rid.Value(id)].Points[0], refBefore[Rid.Value(id)].Points[1],
                            A(e.Start), A(e.End), A(lc.Curve.GetEndPoint(0)), A(lc.Curve.GetEndPoint(1)), EditTolerance);
                        ok = verdict == CurveChangeRules.Verified;
                        expected = new JArray(new JArray(A(e.Start).Select(v => v * 304.8)), new JArray(A(e.End).Select(v => v * 304.8)));
                    }
                    else
                    {
                        Parameter prm = el.LookupParameter(e.Parameter);
                        string got = ParamText(prm);
                        p.ParameterExpected[Rid.Value(id) + "|" + e.Parameter] = got;
                        ok = prm != null && ValueMatches(prm, e.Value);
                        expected = e.Value;
                    }
                    p.EditRows.Add(new JObject { ["element_id"] = Rid.Value(id), ["operation"] = e.Op, ["expected"] = expected,
                        ["found"] = new JArray(now.Points.Select(q => new JArray(q.Select(v => v * 304.8)))), ["verified"] = ok });
                    if (!ok) throw new InvalidOperationException("the edit on reference member " + Rid.Value(id) + " (" + e.Op + ") did not land as asked.");
                }
            p.ReferenceAfter = EditMembers(doc, refState.MembersBefore);
            ReportDrag(doc, p);
            CallProgress.Checkpoint("reference edited: " + p.DragRows.Count + " other member(s) dragged, " + p.HorizontalSlopeChanges + " horizontal slope change(s)", 2, 6);

            Group made = doc.Create.NewGroup(refState.MembersBefore);
            p.NewGroupId = made.Id; p.NewTypeId = made.GroupType.Id;
            var madeSet = new HashSet<ElementId>(made.GetMemberIds());
            if (madeSet.Count != refState.MembersBefore.Count || !refState.MembersBefore.All(madeSet.Contains))
                throw new InvalidOperationException("regrouping the edited reference did not keep its member ids.");
            refState.MembersAfter = refState.MembersBefore.ToList();
            int placed = 0, others = p.Instances.Count - 1;
            foreach (InstanceState s in p.Instances.Skip(1))
            {
                Group g = doc.GetElement(s.GroupId) as Group;
                g.GroupType = made.GroupType;
                doc.Regenerate();
                PlaceInstance(doc, p, s, g);
                CallProgress.Checkpoint("instance " + Rid.Value(s.GroupId) + " regenerated and placed", ++placed, others);
            }
            p.FailureIndex = null;
            if (doc.GetElement(p.TypeId) != null) doc.Delete(p.TypeId);
            made.GroupType.Name = p.TypeName;
            doc.Regenerate();
            CallProgress.Checkpoint("reconnecting " + p.Connections.Count + " external connection(s)", 3, 6);
            Reconnect(doc, p);
            doc.Regenerate();
            ReportExternalEffects(doc, p);
        }

        /// <summary>What the edit did on the reference beyond the named members: dragged members (with vectors) and slopes.</summary>
        private static void ReportDrag(Document doc, EditPlan p)
        {
            InstanceState refState = p.Instances[0];
            var named = new HashSet<long>(p.Edits.SelectMany(e => e.Ids).Select(Rid.Value));
            p.DragRows.Clear(); p.SlopeRows.Clear(); p.HorizontalSlopeChanges = 0;
            for (int k = 0; k < refState.Before.Count; k++)
            {
                EditMember b = refState.Before[k], a = p.ReferenceAfter[k];
                List<double[]> d = DragRules.Displacement(b, a);
                double big = DragRules.Largest(d);
                if (!named.Contains(b.Id) && big > EditTolerance / 50)
                {
                    Element e = doc.GetElement(Rid.Make(b.Id));
                    p.DragRows.Add(new JObject { ["member_id"] = b.Id, ["member_index"] = k, ["category"] = e?.Category?.Name, ["built_in_category"] = CategoryKey.Of(e),
                        ["rigid"] = DragRules.Rigid(d, EditTolerance / 50),
                        ["displacement_mm"] = d == null ? (JToken)JValue.CreateNull() : new JArray(d.Select(v => new JArray(v.Select(x => Math.Round(x * 304.8, 3))))) });
                }
                if (b.Points.Count == 2 && a.Points.Count == 2 && doc.GetElement(Rid.Make(b.Id)) is MEPCurve)
                {
                    double? s0 = RunSlopeRules.Percent(b.Points[0], b.Points[1]), s1 = RunSlopeRules.Percent(a.Points[0], a.Points[1]);
                    if (RunSlopeRules.Changed(s0, s1))
                    {
                        bool horizontal = RunSlopeRules.IsHorizontalRun(s0) || RunSlopeRules.IsHorizontalRun(s1);
                        if (horizontal) p.HorizontalSlopeChanges++;
                        p.SlopeRows.Add(new JObject { ["member_id"] = b.Id, ["horizontal_run"] = horizontal,
                            ["slope_percent_before"] = s0.HasValue ? (JToken)Math.Round(s0.Value, 4) : JValue.CreateNull(),
                            ["slope_percent_after"] = s1.HasValue ? (JToken)Math.Round(s1.Value, 4) : JValue.CreateNull() });
                    }
                }
            }
        }

        /// <summary>Every element outside the groups that the edit moved or stretched, with how far.</summary>
        private static void ReportExternalEffects(Document doc, EditPlan p)
        {
            p.SideEffectRows.Clear();
            foreach (var kv in p.ExternalBefore)
            {
                EditMember now = EditMembers(doc, new[] { Rid.Make(kv.Key) })[0];
                List<double[]> d = DragRules.Displacement(kv.Value, now);
                double big = DragRules.Largest(d);
                if (big <= EditTolerance / 50) continue;
                var row = new JObject { ["element_id"] = kv.Key, ["category"] = doc.GetElement(Rid.Make(kv.Key))?.Category?.Name,
                    ["moved_mm"] = double.IsInfinity(big) ? (JToken)JValue.CreateNull() : Math.Round(big * 304.8, 3),
                    ["rigid"] = DragRules.Rigid(d, EditTolerance / 50) };
                if (kv.Value.Points.Count == 2 && now.Points.Count == 2)
                {
                    double? s0 = RunSlopeRules.Percent(kv.Value.Points[0], kv.Value.Points[1]), s1 = RunSlopeRules.Percent(now.Points[0], now.Points[1]);
                    if (RunSlopeRules.Changed(s0, s1) && (RunSlopeRules.IsHorizontalRun(s0) || RunSlopeRules.IsHorizontalRun(s1)))
                    {
                        p.HorizontalSlopeChanges++;
                        row["horizontal_slope_percent"] = new JArray(Math.Round(s0 ?? double.NaN, 4), Math.Round(s1 ?? double.NaN, 4));
                    }
                }
                p.SideEffectRows.Add(row);
            }
        }

        /// <summary>Pair the regenerated members with the edited reference carried into this frame; correct the swap's displacement.</summary>
        private static void PlaceInstance(Document doc, EditPlan p, InstanceState s, Group g)
        {
            List<EditMember> expected = Expected(p, s);
            for (int attempt = 0; attempt < 2; attempt++)
            {
                s.MembersAfter = g.GetMemberIds().ToList();
                List<EditMember> actual = EditMembers(doc, s.MembersAfter);
                string why;
                if (GroupEditMatching.ByOrder(expected, actual, out why) == null)
                    throw new InvalidOperationException("instance " + Rid.Value(s.GroupId) + " after the swap: " + why);
                var map = Enumerable.Range(0, expected.Count).ToDictionary(i => i, i => i);
                double[] off = GroupEditMatching.MeanOffset(expected, actual, map);
                double size = Math.Sqrt(off[0] * off[0] + off[1] * off[1] + off[2] * off[2]);
                if (attempt == 0 && size > 1e-7)
                {
                    ElementTransformUtils.MoveElement(doc, g.Id, new XYZ(-off[0], -off[1], -off[2]));
                    doc.Regenerate();
                    s.CorrectedShift = off;
                    continue;
                }
                s.WorstFeet = Enumerable.Range(0, expected.Count).Select(i => GroupEditMatching.Distance(expected[i], actual[i])).DefaultIfEmpty(0).Max();
                if (s.WorstFeet > EditTolerance)
                    throw new InvalidOperationException("instance " + Rid.Value(s.GroupId) + " did not regenerate as the edited reference: worst member " +
                        Mm(s.WorstFeet) + " mm off.");
                return;
            }
        }

        private static List<EditMember> Expected(EditPlan p, InstanceState s)
            => p.ReferenceAfter.Select(m => new EditMember { Id = m.Id, Key = m.Key, Points = m.Points.Select(s.Placement.Apply).ToList() }).ToList();

        private static void Reconnect(Document doc, EditPlan p)
        {
            p.ConnectionRows.Clear();
            foreach (ExternalConnection c in p.Connections)
            {
                InstanceState s = p.Instances.First(x => Rid.Value(x.GroupId) == c.GroupId);
                int[] cid = p.ConnectorIds[c];
                Element member = c.MemberIndex < s.MembersAfter.Count ? doc.GetElement(s.MembersAfter[c.MemberIndex]) : null;
                Element ext = doc.GetElement(Rid.Make(c.ExternalId));
                Connector mc = ConnectorById(member, cid[0]);
                Connector xc = ConnectorById(ext, cid[1]);
                var row = new JObject { ["group_id"] = s.IsReference && p.NewGroupId != null ? Rid.Value(p.NewGroupId) : c.GroupId,
                    ["member_id_before"] = c.MemberId, ["member_id_after"] = member == null ? (JToken)JValue.CreateNull() : Rid.Value(member.Id),
                    ["external_id"] = c.ExternalId };
                if (c.GapBeforeFeet > EditTolerance / 50) row["prior_gap_mm"] = Math.Round(c.GapBeforeFeet * 304.8, 3);
                if (mc != null && xc != null && SafeConnected(mc, xc)) { row["action"] = "kept"; p.ConnectionRows.Add(row); continue; }
                // Where the external connector belongs: where it was, moved by what the member moved.
                // A prior offset (Revit keeps some connections connected while apart) is kept, not absorbed.
                double[] desired = mc == null || xc == null || c.ExternalPoint == null ? null : PreservingReconnect.Desired(c.ExternalPoint, c.Point, A(mc.Origin));
                double? residual = desired == null ? (double?)null : X(desired).DistanceTo(xc.Origin);
                string decision = PreservingReconnect.Decide(c, residual, p.StretchLimitFeet, EditTolerance, out string why);
                if (mc != null) row["member_moved_mm"] = Math.Round(mc.Origin.DistanceTo(X(c.Point)) * 304.8, 3);
                row["external_must_move_mm"] = residual.HasValue ? (JToken)Math.Round(residual.Value * 304.8, 3) : JValue.CreateNull();
                try
                {
                    if (decision == ExternalReconnectRules.StretchAndConnect)
                    {
                        var lc = (LocationCurve)ext.Location;
                        XYZ e0 = lc.Curve.GetEndPoint(0), e1 = lc.Curve.GetEndPoint(1);
                        bool first = e0.DistanceTo(xc.Origin) <= e1.DistanceTo(xc.Origin);
                        XYZ delta = X(desired) - xc.Origin;
                        lc.Curve = first ? Line.CreateBound(e0 + delta, e1) : Line.CreateBound(e0, e1 + delta);
                        doc.Regenerate();
                        xc = ConnectorById(ext, cid[1]);
                    }
                    if (decision == ExternalReconnectRules.Connect || decision == ExternalReconnectRules.StretchAndConnect)
                    {
                        mc.ConnectTo(xc);
                        doc.Regenerate();
                        if (!SafeConnected(mc, xc)) { decision = ExternalReconnectRules.Lost; why = "ConnectTo returned but the connectors do not read as connected"; }
                    }
                }
                catch (Exception ex) { decision = ExternalReconnectRules.Lost; why = "Revit refused the reconnection: " + ex.Message; }
                row["action"] = decision;
                if (why != null) row["why"] = why;
                p.ConnectionRows.Add(row);
            }
        }

        private static bool SafeConnected(Connector a, Connector b)
        {
            try { return a.IsConnectedTo(b); } catch { return false; }
        }

        private static void SetParameter(Element e, string name, JToken value)
        {
            Parameter prm = e.LookupParameter(name);
            if (prm == null || prm.IsReadOnly) throw new InvalidOperationException("parameter '" + name + "' on " + Rid.Value(e.Id) + " is missing or read-only.");
            bool ok;
            switch (prm.StorageType)
            {
                case StorageType.String: ok = prm.Set(value.Type == JTokenType.String ? (string)value : value.ToString()); break;
                case StorageType.Integer: ok = value.Type == JTokenType.Integer ? prm.Set(value.Value<int>()) : prm.SetValueString(value.ToString()); break;
                case StorageType.Double: ok = value.Type == JTokenType.String ? prm.SetValueString((string)value) : prm.Set(value.Value<double>()); break;
                case StorageType.ElementId: ok = prm.Set(Rid.Make(value.Value<long>())); break;
                default: ok = false; break;
            }
            if (!ok) throw new InvalidOperationException("Revit refused '" + name + "' on " + Rid.Value(e.Id) + ".");
        }

        private static string ParamText(Parameter prm)
        {
            if (prm == null) return null;
            switch (prm.StorageType)
            {
                case StorageType.String: return prm.AsString() ?? "";
                case StorageType.Integer: return prm.AsInteger().ToString(System.Globalization.CultureInfo.InvariantCulture);
                case StorageType.Double: return prm.AsDouble().ToString("R", System.Globalization.CultureInfo.InvariantCulture);
                case StorageType.ElementId: return Rid.Value(prm.AsElementId()).ToString(System.Globalization.CultureInfo.InvariantCulture);
                default: return null;
            }
        }

        private static bool ValueMatches(Parameter prm, JToken value)
        {
            switch (prm.StorageType)
            {
                case StorageType.String: return (prm.AsString() ?? "") == (value.Type == JTokenType.String ? (string)value : value.ToString());
                case StorageType.Integer: return value.Type != JTokenType.Integer || prm.AsInteger() == value.Value<int>();
                case StorageType.Double: return value.Type == JTokenType.String || Math.Abs(prm.AsDouble() - value.Value<double>()) < 1e-9;
                case StorageType.ElementId: return Rid.Value(prm.AsElementId()) == value.Value<long>();
                default: return false;
            }
        }

        // ---------------------------------------------------------------- verify
        private static PostconditionCheck VerifyEdit(Document doc, EditPlan p, bool insideTransaction)
        {
            var c = new PostconditionCheck("type", "instances", "external_connections", "connections", "parameters");
            GroupType now = p.NewTypeId == null ? null : doc.GetElement(p.NewTypeId) as GroupType;
            c.Compare("type", p.TypeName + " (old type deleted)", now == null ? "missing" : now.Name + (doc.GetElement(p.TypeId) == null ? " (old type deleted)" : " (old type still present)"));
            var rows = new JArray(); bool held = true;
            foreach (InstanceState s in p.Instances)
            {
                ElementId gid = s.IsReference ? p.NewGroupId : s.GroupId;
                Group g = gid == null ? null : doc.GetElement(gid) as Group;
                bool ok = g != null && now != null && g.GroupType.Id == now.Id;
                double worst = double.PositiveInfinity;
                if (ok)
                {
                    var actual = EditMembers(doc, s.IsReference ? s.MembersAfter : (IList<ElementId>)g.GetMemberIds());
                    var expected = s.IsReference ? p.ReferenceAfter : Expected(p, s);
                    string why;
                    if (GroupEditMatching.ByOrder(expected, actual, out why) == null) { ok = false; s.Why = why; }
                    else
                    {
                        worst = Enumerable.Range(0, expected.Count).Select(i => GroupEditMatching.Distance(expected[i], actual[i])).DefaultIfEmpty(0).Max();
                        ok = worst <= EditTolerance;
                        if (!ok) s.Why = "worst member " + Mm(worst) + " mm from the edited reference";
                    }
                }
                else s.Why = g == null ? "the instance no longer exists" : "the instance is not on the edited type";
                held &= ok;
                rows.Add(new JObject { ["group_id"] = gid == null ? (JToken)JValue.CreateNull() : Rid.Value(gid), ["verified"] = ok,
                    ["worst_mm"] = double.IsInfinity(worst) ? (JToken)JValue.CreateNull() : Math.Round(worst * 304.8, 3), ["why"] = ok ? null : s.Why });
            }
            // Fault injection for the live rollback test: the APPLY fails its check, the rehearsal does not.
            if (!p.Rehearsing && Environment.GetEnvironmentVariable("HORIZUN_TEST_FAIL_ACTION")?.Trim() == EditFaultKey)
            { held = false; rows.Add(new JObject { ["fault_injected"] = EditFaultKey }); }
            c.Record("instances", p.Instances.Count, rows, held);

            int lost = 0, total = 0; var lostRows = new JArray();
            foreach (ExternalConnection x in p.Connections)
            {
                if (x.MemberRemoved) continue;
                total++;
                InstanceState s = p.Instances.First(i => Rid.Value(i.GroupId) == x.GroupId);
                Element member = x.MemberIndex < s.MembersAfter.Count ? doc.GetElement(s.MembersAfter[x.MemberIndex]) : null;
                Connector mc = ConnectorById(member, p.ConnectorIds[x][0]);
                Connector xc = ConnectorById(doc.GetElement(Rid.Make(x.ExternalId)), p.ConnectorIds[x][1]);
                if (mc == null || xc == null || !SafeConnected(mc, xc))
                { lost++; lostRows.Add(new JObject { ["group_id"] = x.GroupId, ["member_index"] = x.MemberIndex, ["external_id"] = x.ExternalId }); }
            }
            c.Record("external_connections", new JObject { ["captured"] = total, ["lost_allowed"] = p.AcceptLost },
                new JObject { ["connected"] = total - lost, ["lost"] = lost, ["lost_rows"] = lostRows }, lost == 0 || p.AcceptLost);

            var prmRows = new JArray(); bool prmHeld = true;
            foreach (MemberEdit e in p.Edits.Where(e => e.Op == "set_parameter"))
                foreach (ElementId id in e.Ids)
                {
                    int k = p.Instances[0].MembersBefore.IndexOf(id);
                    string want = p.ParameterExpected.Value<string>(Rid.Value(id) + "|" + e.Parameter);
                    foreach (InstanceState s in p.Instances)
                    {
                        Element m = k >= 0 && k < s.MembersAfter.Count ? doc.GetElement(s.MembersAfter[k]) : null;
                        string got = ParamText(m?.LookupParameter(e.Parameter));
                        bool ok = got != null && got == want;
                        prmHeld &= ok;
                        if (!ok) prmRows.Add(new JObject { ["group_id"] = Rid.Value(s.GroupId), ["member_index"] = k, ["expected"] = want, ["found"] = got });
                    }
                }
            c.Record("parameters", p.Edits.Count(e => e.Op == "set_parameter"), prmRows, prmHeld);
            RecordConnections(doc, p, c);
            return c;
        }

        /// <summary>
        /// Every connection captured before the edit - internal (member-member, per instance),
        /// external and one hop outside - must still be connected, and no farther apart than it
        /// was. The member positions alone do not show a connection Revit took apart.
        /// </summary>
        private static void RecordConnections(Document doc, EditPlan p, PostconditionCheck c)
        {
            var perInstance = new Dictionary<int, int>();
            ConnectionLedger.JObjectLike v = p.Ledger.Verify(
                l =>
                {
                    Connector a = ResolveEnd(doc, p, l.A), b = ResolveEnd(doc, p, l.B);
                    bool ok = a != null && b != null && SafeConnected(a, b);
                    if (!ok)
                        foreach (ConnectionLedger.End e in new[] { l.A, l.B })
                            if (e.IsMember) { perInstance.TryGetValue(e.Instance, out int n); perInstance[e.Instance] = n + 1; break; }
                    return ok;
                },
                l =>
                {
                    Connector a = ResolveEnd(doc, p, l.A), b = ResolveEnd(doc, p, l.B);
                    return a == null || b == null ? (double?)null : a.Origin.DistanceTo(b.Origin);
                },
                EditTolerance);
            var kinds = new JObject();
            foreach (var kv in v.ByKind.OrderBy(k => k.Key))
            {
                ConnectionLedger.Tally t = kv.Value;
                kinds[kv.Key] = new JObject
                {
                    ["captured"] = t.Captured, ["connected"] = t.Captured - t.Lost, ["lost"] = t.Lost, ["pulled_apart"] = t.PulledApart,
                    ["lost_rows"] = new JArray(t.LostRows.Select(l => new JObject { ["a"] = EndJson(p, l.A), ["b"] = EndJson(p, l.B), ["gap_before_mm"] = Math.Round(l.GapBeforeFeet * 304.8, 3) })),
                    ["pulled_apart_rows"] = new JArray(t.PulledRows.Select(x => new JObject { ["a"] = EndJson(p, x.Item1.A), ["b"] = EndJson(p, x.Item1.B),
                        ["gap_before_mm"] = Math.Round(x.Item1.GapBeforeFeet * 304.8, 3), ["gap_now_mm"] = Math.Round(x.Item2 * 304.8, 3) }))
                };
            }
            var byInstance = new JArray(perInstance.OrderBy(k => k.Key).Select(k => new JObject { ["instance_group_id"] = Rid.Value(p.Instances[k.Key].GroupId), ["lost"] = k.Value }));
            // external links are judged by external_connections (which honours accept_lost_connections);
            // internal and neighbour links have no such waiver.
            c.Record("connections", new JObject { ["captured"] = p.Ledger.Count, ["kinds"] = new JArray(v.ByKind.Keys.OrderBy(k => k)) },
                new JObject { ["by_kind"] = kinds, ["instances_with_losses"] = byInstance },
                v.Holds("internal", "neighbour", "between_instances") && (p.AcceptLost || v.Holds("external")));
        }

        // ---------------------------------------------------------------- report
        private static JArray InstanceRows(EditPlan p)
        {
            var rows = new JArray();
            foreach (InstanceState s in p.Instances)
            {
                var map = new JObject();
                for (int k = 0; k < s.MembersBefore.Count && k < s.MembersAfter.Count; k++)
                    map[Rid.Value(s.MembersBefore[k]).ToString(System.Globalization.CultureInfo.InvariantCulture)] = Rid.Value(s.MembersAfter[k]);
                rows.Add(new JObject
                {
                    ["group_id_before"] = Rid.Value(s.GroupId),
                    ["group_id_after"] = s.IsReference ? (p.NewGroupId == null ? (JToken)JValue.CreateNull() : Rid.Value(p.NewGroupId)) : Rid.Value(s.GroupId),
                    ["reference"] = s.IsReference,
                    ["placement_degrees"] = Math.Round(s.Placement.AngleDegrees, 6),
                    ["mirrored"] = s.Placement.Mirrored,
                    ["member_id_map"] = map,
                    ["swap_shift_corrected_mm"] = s.CorrectedShift == null ? (JToken)JValue.CreateNull() : new JArray(s.CorrectedShift.Select(v => Math.Round(v * 304.8, 4))),
                    ["worst_mm"] = s.WorstFeet < 0 ? (JToken)JValue.CreateNull() : Math.Round(s.WorstFeet * 304.8, 4)
                });
            }
            return rows;
        }

        private static JObject EditPlanJson(EditPlan p)
        {
            var effects = new JArray();
            foreach (InstanceState s in p.Instances)
            {
                var changes = new JArray();
                foreach (MemberEdit e in p.Edits)
                    foreach (ElementId id in e.Ids)
                    {
                        int k = p.Instances[0].MembersBefore.IndexOf(id);
                        if (k < 0) continue;
                        var row = new JObject { ["member_id"] = Rid.Value(s.MembersBefore[k]), ["operation"] = e.Op };
                        if (e.Op == "move") row["vector_mm"] = new JArray(s.Placement.ApplyVector(A(e.Vector)).Select(v => Math.Round(v * 304.8, 4)));
                        if (e.Op == "set_curve") row["line_mm"] = new JArray(new JArray(s.Placement.Apply(A(e.Start)).Select(v => Math.Round(v * 304.8, 3))),
                                                                            new JArray(s.Placement.Apply(A(e.End)).Select(v => Math.Round(v * 304.8, 3))));
                        if (e.Op == "set_parameter") { row["parameter"] = e.Parameter; row["value"] = e.Value; }
                        changes.Add(row);
                    }
                effects.Add(new JObject { ["group_id"] = Rid.Value(s.GroupId), ["reference"] = s.IsReference,
                    ["placement_degrees"] = Math.Round(s.Placement.AngleDegrees, 6), ["mirrored"] = s.Placement.Mirrored, ["changes"] = changes,
                    ["external_connections"] = p.Connections.Count(c => c.GroupId == Rid.Value(s.GroupId)) });
            }
            return new JObject
            {
                ["operation"] = "edit_type_members", ["scope"] = "type", ["type_id"] = Rid.Value(p.TypeId), ["type_name"] = p.TypeName,
                ["instances"] = p.Instances.Count, ["members_changed_per_instance"] = p.Edits.Sum(e => e.Ids.Count),
                ["members_changed_total"] = p.Edits.Sum(e => e.Ids.Count) * p.Instances.Count,
                ["external_connections_captured"] = p.Connections.Count,
                ["connections_captured"] = new JObject(p.Ledger.Links.GroupBy(l => l.Kind).OrderBy(g => g.Key).Select(g => new JProperty(g.Key, g.Count()))),
                ["size"] = SizeJson(p),
                ["risk_non_coincident_before"] = RiskJson(p),
                ["side_effects"] = new JArray(
                    (p.Instances.Count - 1) + " non-reference instance(s) are regenerated: EVERY member of each gets a NEW element id (member_id_map); anything that referenced the old ids (tags, schedules keyed by id, external notes) must be re-pointed.",
                    "the old group type (id " + Rid.Value(p.TypeId) + ") is DELETED and the new type takes its name '" + p.TypeName + "'; the reference instance gets a new group id and keeps its member ids.",
                    "elements outside the group that are connected to a moved member are stretched or moved with it; the rehearsal lists each in external_side_effects, and dragged members in dragged_members."),
                ["reconnect_stretch_mm"] = Math.Round(p.StretchLimitFeet * 304.8, 3),
                ["effect_by_instance"] = effects,
                ["api_note"] = "The Revit API has no edit-group mode (checked 2023-2027: GroupType offers no member editor and a member of a " +
                    "multi-instance group cannot be moved). This ungroups the reference, edits it, regroups it as the type and swaps every other " +
                    "instance onto it; those instances get NEW member ids (member_id_map) and their external connections are put back."
            };
        }

        private static JObject SizeJson(EditPlan p)
        {
            int per = p.Instances[0].MembersBefore.Count;
            return new JObject
            {
                ["instances"] = p.Instances.Count, ["members_per_instance"] = per,
                ["member_instances"] = GroupEditBudget.MemberInstances(p.Instances.Count, per),
                ["estimated_seconds"] = new JArray(GroupEditBudget.EstimatedSeconds(p.Instances.Count, per)),
                ["sync_limit_member_instances"] = GroupEditBudget.SyncMemberInstanceLimit,
                ["route"] = GroupEditBudget.MemberInstances(p.Instances.Count, per) > GroupEditBudget.SyncMemberInstanceLimit ? "horizun_submit_job" : "sync_or_submit_job"
            };
        }

        /// <summary>Connections Revit already keeps "connected" while their connectors are apart: reported up front as risk.</summary>
        private static JObject RiskJson(EditPlan p)
        {
            var rows = p.Ledger.Links.Where(l => l.GapBeforeFeet > EditTolerance / 50).OrderByDescending(l => l.GapBeforeFeet).ToList();
            return new JObject
            {
                ["count"] = rows.Count,
                ["by_kind"] = new JObject(rows.GroupBy(l => l.Kind).Select(g => new JProperty(g.Key, g.Count()))),
                ["note"] = "connected but not coincident before the edit. The edit keeps each offset as it was (the external end follows only what the member moved); a stretch never closes it.",
                ["rows"] = new JArray(rows.Take(40).Select(l => new JObject { ["kind"] = l.Kind, ["a"] = EndJson(p, l.A), ["b"] = EndJson(p, l.B), ["gap_mm"] = Math.Round(l.GapBeforeFeet * 304.8, 3) }))
            };
        }

        private static ResolvedPlan ResolvedEdit(GateResult gate, UIApplication app, EditPlan p)
        {
            var rp = new ResolvedPlan { Command = "horizun_manage_groups", DocumentKey = gate.Fingerprint, RevitVersion = app.Application.VersionNumber, DocumentFingerprint = gate.Identity.FingerprintDigest() };
            var before = new Dictionary<string, string>
            {
                ["operation"] = "edit_type_members",
                ["type"] = p.Type.UniqueId + "|" + p.TypeName + "|" + p.Instances.Count,
                ["instances"] = string.Join(";", p.Instances.Select(s => Rid.Value(s.GroupId) + "=" + string.Join(",", s.MembersBefore.Select(Rid.Value)))),
                ["geometry"] = string.Join(";", p.Instances[0].Before.Select(m => m.Id + ":" + string.Join("/", m.Points.Select(q => string.Join(",", q.Select(v => Math.Round(v, 6))))))),
                ["connections"] = p.Connections.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)
            };
            rp.Elements.Add(new PlannedElement { UniqueId = "groups:edit_type_members", Category = "group", Action = PlannedAction.Modify, BeforeValues = before });
            return rp;
        }
    }
}
