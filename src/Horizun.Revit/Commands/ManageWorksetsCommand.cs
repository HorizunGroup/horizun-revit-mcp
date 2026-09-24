// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// horizun_manage_worksets - user worksets, typed. Before this command the bridge
// could open a model with its worksets and relinquish them, and nothing else.
//
// RULES THIS COMMAND HOLDS:
//
//   * ONLY ON A WORKSHARED MODEL. Every operation, the list included, is refused
//     with code=not_workshared on a model that is not: an empty workset table is
//     not a finding about the model, it is the absence of worksharing.
//   * A BORROWED ELEMENT IS NEVER FORCED. move_elements reports elements owned by
//     another user (with the owner) and elements whose workset parameter is
//     read-only, moves the rest, and re-reads WorksetId for every moved element.
//   * set_default (the active workset new elements go to) is a session setting
//     the rehearsal cannot provisionally change and roll back with certainty, so
//     its dry run is a MEASURED preview and says so; the apply re-reads it.
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
    public sealed class ManageWorksetsCommand : ICommand
    {
        public string Name => "horizun_manage_worksets";
        public string Description => "List, create and rename user worksets, move elements between them, set the active workset and per-view workset visibility, verified by re-reading.";

        private static readonly string[] Writes = { "create", "rename", "move_elements", "set_default", "visibility" };

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            JObject request;
            try { request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JObject.Parse(paramsJson); }
            catch (Exception ex) { return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message); }
            string op = (request.Value<string>("operation") ?? "").Trim().ToLowerInvariant();
            if (op != "list" && !Writes.Contains(op))
                return CommandResult.Fail("operation must be list, " + string.Join(", ", Writes) + ".");

            Document doc; GateResult gate = null;
            if (op == "list")
            {
                doc = app.ActiveUIDocument?.Document;
                if (doc == null) return CommandResult.Fail("No document is open.");
                CommandResult wrong = DocumentGate.ReadGuard(doc, request, Name);
                if (wrong != null) return wrong;
            }
            else
            {
                gate = DocumentGate.ForMutation(app, request, Name); if (!gate.Ok) return gate.Refusal;
                doc = gate.Document;
            }
            if (!doc.IsWorkshared)
                return CommandResult.FailWithDetail(
                    "'" + doc.Title + "' is not workshared, so it has no user worksets to " + (op == "list" ? "list" : op) +
                    ". Enabling worksharing is a project decision this command does not take. Nothing was written.",
                    new JObject { ["state"] = "refused", ["code"] = "not_workshared", ["operation"] = op, ["write_started"] = false },
                    FallbackSignal.NotAllowed("not_workshared", false), null);
            if (op == "list") return List(doc);

            string error; Plan plan = MakePlan(doc, request, op, out error);
            if (plan == null)
                return CommandResult.FailWithDetail(error + " Nothing was written.",
                    new JObject { ["state"] = "refused", ["operation"] = op, ["write_started"] = false });

            string hash = DocumentGate.PlanHash(request, "operation", "workset_id", "name", "element_ids", "category", "view_ids", "visibility");
            ResolvedPlan resolved = Resolved(gate, app, plan);
            bool dry = request["dry_run"] == null || request.Value<bool>("dry_run");
            string txName = "Horizun: worksets " + op;

            if (dry)
            {
                JObject rehearsal;
                if (op == "set_default")
                    rehearsal = new JObject { ["rehearsal_kind"] = "measured_preview", ["verified"] = true,
                        ["active_workset_id"] = doc.GetWorksetTable().GetActiveWorksetId().IntegerValue,
                        ["note"] = "The active workset is a session setting; it is not changed provisionally. Apply sets it and re-reads it." };
                else
                {
                    rehearsal = Rehearse(doc, plan, txName);
                    if (rehearsal.Value<bool>("rollback_confirmed") != true)
                        return CommandResult.FailWithDetail("Workset rehearsal rollback was not confirmed; model state is uncertain.",
                            new JObject { ["state"] = "uncertain", ["rehearsal"] = rehearsal, ["write_started"] = true });
                    if (rehearsal.Value<bool>("verified") != true)
                        return CommandResult.FailWithDetail("The workset rehearsal could not verify the change. Nothing was committed.",
                            new JObject { ["state"] = "refused", ["rehearsal"] = rehearsal });
                }
                DocumentGate.RecordResolvedPlan(resolved);
                var result = new JObject { ["dry_run"] = true, ["plan"] = PlanJson(plan), ["rehearsal"] = rehearsal };
                ApplicationOutcome.StampRehearsal(result, plan.Requested, 0, 0, 0);
                DocumentGate.StampConfirmation(result, gate, Name, hash, true,
                    "the token binds the workset, the element ids with their current worksets and owners, and the views named in the plan.");
                return CommandResult.Ok(result);
            }

            DocumentGate.RecordResolvedPlan(resolved);
            CommandResult refusal = DocumentGate.RequireConfirmation(app, gate, request, Name, hash, resolved, null);
            if (refusal != null) return refusal;

            string txStatus = ApplicationOutcome.Committed;
            if (op == "set_default")
            {
                try { doc.GetWorksetTable().SetActiveWorksetId(plan.Workset); }
                catch (Autodesk.Revit.Exceptions.ModificationOutsideTransactionException)
                {
                    using (var tx = new Transaction(doc, txName)) { tx.Start(); doc.GetWorksetTable().SetActiveWorksetId(plan.Workset); Guard.Commit(tx, txName); }
                }
                catch (Exception ex)
                {
                    return CommandResult.FailWithDetail("Setting the active workset failed: " + ex.Message,
                        new JObject { ["state"] = "failed", ["write_started"] = true, ["postconditions"] = Verify(doc, plan).ToJson() });
                }
            }
            else
            {
                using (var tx = new Transaction(doc, txName))
                {
                    try
                    {
                        if (tx.Start() != TransactionStatus.Started) throw new InvalidOperationException("Could not start the workset transaction.");
                        Apply(doc, plan);
                        doc.Regenerate();
                        if (!Verify(doc, plan).AllVerified) throw new InvalidOperationException("a postcondition failed while the change was still reversible.");
                        TransactionStatus s = tx.Commit();
                        if (s != TransactionStatus.Committed) throw new InvalidOperationException("the transaction returned " + s + ".");
                    }
                    catch (Exception ex)
                    {
                        Guard.RollbackResult? rb = null;
                        try { if (tx.GetStatus() == TransactionStatus.Started) rb = Guard.RollBack(tx); } catch { }
                        bool rolled = tx.GetStatus() != TransactionStatus.Committed;
                        return CommandResult.FailWithDetail("Workset " + op + " failed" + (rolled ? " and was rolled back" : "") + ": " + ex.Message,
                            new JObject { ["state"] = rolled ? "rolled_back" : "uncertain", ["write_started"] = true,
                                          ["transaction_status"] = rb.HasValue ? rb.Value.StatusName : tx.GetStatus().ToString() });
                    }
                }
            }

            PostconditionCheck after = Verify(doc, plan);
            int moved = after.AllVerified ? plan.Movable.Count : 0;
            var done = new JObject
            {
                ["state"] = after.AllVerified ? "committed_verified" : "uncertain",
                ["host_verified"] = after.AllVerified,
                ["operation"] = op,
                ["workset_id"] = plan.Workset?.IntegerValue,
                ["postconditions"] = after.ToJson(),
                ["skipped"] = SkippedJson(plan),
                ["worksets"] = Table(doc)
            };
            int requested = plan.Requested;
            int applied = op == "move_elements" ? plan.Movable.Count : 1;
            ApplicationOutcome.StampApplied(done, txStatus, requested, applied, after.AllVerified ? applied : 0,
                                            plan.Skipped.Count, 0, after.AllVerified ? 0 : 1);
            if (!after.AllVerified)
                return CommandResult.FailWithDetail("The change committed but a postcondition does not re-read as planned; state is uncertain.", done);
            return CommandResult.Ok(done);
        }

        // ------------------------------------------------------------------ list
        private static CommandResult List(Document doc)
        {
            var listing = new JObject
            {
                ["document"] = doc.Title,
                ["current_user"] = doc.Application.Username,
                ["active_workset_id"] = doc.GetWorksetTable().GetActiveWorksetId().IntegerValue,
                ["worksets"] = Table(doc, true)
            };
            ApplicationOutcome.StampApplied(listing, ApplicationOutcome.NotStarted, 0, 0, 0, 0, 0, 0);
            return CommandResult.Ok(listing);
        }

        private static JArray Table(Document doc, bool counts = false)
        {
            var rows = new JArray();
            foreach (Workset w in new FilteredWorksetCollector(doc).OfKind(WorksetKind.UserWorkset).ToWorksets().OrderBy(w => w.Id.IntegerValue))
            {
                var row = new JObject
                {
                    ["workset_id"] = w.Id.IntegerValue, ["name"] = w.Name, ["open"] = w.IsOpen, ["editable"] = w.IsEditable,
                    ["owner"] = string.IsNullOrEmpty(w.Owner) ? null : w.Owner, ["visible_by_default"] = w.IsVisibleByDefault
                };
                if (counts)
                {
                    try { row["element_count"] = new FilteredElementCollector(doc).WhereElementIsNotElementType().WherePasses(new ElementWorksetFilter(w.Id)).GetElementCount(); }
                    catch (Exception ex) { row["element_count"] = null; row["element_count_error"] = ex.Message; }
                }
                rows.Add(row);
            }
            return rows;
        }

        // ------------------------------------------------------------------ plan
        private static Plan MakePlan(Document doc, JObject r, string op, out string error)
        {
            error = null;
            var p = new Plan { Op = op, Name = r.Value<string>("name") };
            WorksetTable table = doc.GetWorksetTable();
            var users = new FilteredWorksetCollector(doc).OfKind(WorksetKind.UserWorkset).ToWorksets().ToList();
            int? wid = r.Value<int?>("workset_id");
            Workset target = wid.HasValue ? users.FirstOrDefault(w => w.Id.IntegerValue == wid.Value) : null;
            if (wid.HasValue && target == null) { error = "workset_id " + wid + " is not a user workset; operation=list shows them."; return null; }
            if (target != null) { p.Workset = target.Id; p.WorksetName = target.Name; }
            string me = doc.Application.Username;
            p.Requested = 1;

            switch (op)
            {
                case "create":
                {
                    string problem = WorksetEditRules.NameProblem(p.Name, users.Select(w => w.Name));
                    if (problem == null && !WorksetTable.IsWorksetNameUnique(doc, p.Name)) problem = "the name '" + p.Name + "' is already used.";
                    if (problem != null) { error = "name: " + problem; return null; }
                    return p;
                }
                case "rename":
                {
                    if (target == null) { error = "rename needs workset_id."; return null; }
                    string problem = WorksetEditRules.NameProblem(p.Name, users.Where(w => w.Id != target.Id).Select(w => w.Name));
                    if (problem != null) { error = "name: " + problem; return null; }
                    if (OwnedByOther(target.Owner, me)) { error = "workset '" + target.Name + "' is owned by " + target.Owner + "; it is not taken from them."; return null; }
                    return p;
                }
                case "set_default":
                    if (target == null) { error = "set_default needs workset_id."; return null; }
                    if (!target.IsOpen) { error = "workset '" + target.Name + "' is closed; the active workset must be open."; return null; }
                    if (table.GetActiveWorksetId() == target.Id) { error = "workset '" + target.Name + "' is already the active workset; a no-op is refused."; return null; }
                    return p;
                case "visibility":
                {
                    if (target == null) { error = "visibility needs workset_id."; return null; }
                    string v = (r.Value<string>("visibility") ?? "").ToLowerInvariant();
                    if (!WorksetEditRules.Visibilities.Contains(v)) { error = "visibility must be visible, hidden or use_global."; return null; }
                    p.Visibility = v == "visible" ? WorksetVisibility.Visible : v == "hidden" ? WorksetVisibility.Hidden : WorksetVisibility.UseGlobalSetting;
                    foreach (JToken t in (r["view_ids"] as JArray) ?? new JArray())
                    {
                        long id = t.Value<long>();
                        View view = Rid.CanRepresent(id) ? doc.GetElement(Rid.Make(id)) as View : null;
                        if (view == null || view is ViewSchedule || view is ViewSheet) { error = "view_ids names " + id + ", which is not a graphical view."; return null; }
                        if (p.Views.Any(x => x.Id == view.Id)) { error = "view_ids repeats " + id + "."; return null; }
                        string owner; if (WorksharingUtils.GetCheckoutStatus(doc, view.Id, out owner) == CheckoutStatus.OwnedByOtherUser)
                        { error = "view " + id + " is borrowed by " + owner + "; it is not taken from them."; return null; }
                        p.Views.Add(view);
                    }
                    if (p.Views.Count == 0) { error = "visibility needs view_ids."; return null; }
                    if (p.Views.All(vw => vw.GetWorksetVisibility(target.Id) == p.Visibility)) { error = "every view already has that visibility; a no-op is refused."; return null; }
                    p.Requested = p.Views.Count;
                    return p;
                }
                case "move_elements":
                {
                    if (target == null) { error = "move_elements needs workset_id (the destination)."; return null; }
                    JArray ids = r["element_ids"] as JArray; string category = r.Value<string>("category");
                    if ((ids == null || ids.Count == 0) == string.IsNullOrWhiteSpace(category)) { error = "move_elements needs exactly one of element_ids or category."; return null; }
                    var elements = new List<Element>();
                    if (ids != null && ids.Count > 0)
                        foreach (JToken t in ids)
                        {
                            long id = t.Value<long>();
                            Element e = Rid.CanRepresent(id) ? doc.GetElement(Rid.Make(id)) : null;
                            if (e == null) { error = "element_ids names " + id + ", which is not an element."; return null; }
                            if (elements.Any(x => x.Id == e.Id)) { error = "element_ids repeats " + id + "."; return null; }
                            elements.Add(e);
                        }
                    else
                    {
                        BuiltInCategory bic;
                        if (!Enum.TryParse(category.Trim(), true, out bic)) { error = "category must be a BuiltInCategory token such as OST_Walls."; return null; }
                        elements = new FilteredElementCollector(doc).OfCategory(bic).WhereElementIsNotElementType().ToElements().ToList();
                        if (elements.Count == 0) { error = "no element of " + category + " in the host model."; return null; }
                        if (elements.Count > 10000) { error = category + " has " + elements.Count + " elements; move at most 10000 per call."; return null; }
                    }
                    foreach (Element e in elements)
                    {
                        Parameter prm = e.get_Parameter(BuiltInParameter.ELEM_PARTITION_PARAM);
                        string owner; CheckoutStatus cs = WorksharingUtils.GetCheckoutStatus(doc, e.Id, out owner);
                        string verdict = WorksetEditRules.Classify(true, cs == CheckoutStatus.OwnedByOtherUser, prm != null && !prm.IsReadOnly, e.WorksetId == target.Id);
                        if (verdict == WorksetEditRules.Movable) { p.Movable.Add(e.Id); p.From[e.Id] = e.WorksetId.IntegerValue; }
                        else if (verdict == WorksetEditRules.AlreadyThere) p.Already.Add(e.Id);
                        else p.Skipped.Add(new JObject { ["element_id"] = Rid.Value(e.Id), ["reason"] = verdict, ["owner"] = cs == CheckoutStatus.OwnedByOtherUser ? owner : null });
                    }
                    if (p.Movable.Count == 0) { error = "nothing can move: " + p.Already.Count + " already in the target, " + p.Skipped.Count + " borrowed or read-only (" + SkippedJson(p).ToString(Newtonsoft.Json.Formatting.None) + ")."; return null; }
                    p.Requested = p.Movable.Count + p.Skipped.Count;
                    return p;
                }
            }
            error = "unknown operation."; return null;
        }

        private static bool OwnedByOther(string owner, string me) => !string.IsNullOrEmpty(owner) && !string.Equals(owner, me, StringComparison.OrdinalIgnoreCase);

        // ----------------------------------------------------------------- apply
        private static void Apply(Document doc, Plan p)
        {
            switch (p.Op)
            {
                case "create": p.Workset = Workset.Create(doc, p.Name).Id; break;
                case "rename": WorksetTable.RenameWorkset(doc, p.Workset, p.Name); break;
                case "visibility": foreach (View v in p.Views) v.SetWorksetVisibility(p.Workset, p.Visibility); break;
                case "move_elements":
                    foreach (ElementId id in p.Movable)
                        if (!doc.GetElement(id).get_Parameter(BuiltInParameter.ELEM_PARTITION_PARAM).Set(p.Workset.IntegerValue))
                            throw new InvalidOperationException("element " + Rid.Value(id) + " refused the workset parameter.");
                    break;
            }
        }

        // ---------------------------------------------------------------- verify
        private static PostconditionCheck Verify(Document doc, Plan p)
        {
            Workset w = p.Workset == null ? null : doc.GetWorksetTable().GetWorkset(p.Workset);
            switch (p.Op)
            {
                case "create":
                {
                    var c = new PostconditionCheck("workset_exists", "name");
                    c.Compare("workset_exists", true, w != null && w.Kind == WorksetKind.UserWorkset);
                    c.Compare("name", p.Name, w?.Name);
                    return c;
                }
                case "rename":
                    return new PostconditionCheck("name").Compare("name", p.Name, w?.Name);
                case "set_default":
                    return new PostconditionCheck("active_workset_id").Compare("active_workset_id", p.Workset.IntegerValue, doc.GetWorksetTable().GetActiveWorksetId().IntegerValue);
                case "visibility":
                {
                    var c = new PostconditionCheck("views");
                    var wrong = p.Views.Where(v => (doc.GetElement(v.Id) as View)?.GetWorksetVisibility(p.Workset) != p.Visibility).Select(v => Rid.Value(v.Id)).ToList();
                    c.Record("views", p.Visibility.ToString(), new JArray(wrong), wrong.Count == 0);
                    return c;
                }
                default:
                {
                    var c = new PostconditionCheck("moved_elements", "skipped_untouched");
                    var wrong = p.Movable.Where(id => doc.GetElement(id)?.WorksetId != p.Workset).Select(Rid.Value).ToList();
                    c.Record("moved_elements", p.Movable.Count, new JArray(wrong), p.Movable.Count > 0 && wrong.Count == 0);
                    var skippedMoved = p.Skipped.Select(s => s.Value<long>("element_id"))
                        .Where(id => doc.GetElement(Rid.Make(id))?.WorksetId == p.Workset).ToList();
                    c.Record("skipped_untouched", p.Skipped.Count, new JArray(skippedMoved), skippedMoved.Count == 0);
                    return c;
                }
            }
        }

        private static JObject Rehearse(Document doc, Plan p, string name)
        {
            PostconditionCheck check = null; string error = null; Guard.RollbackResult? rb = null; string rbError = null;
            WorksetId before = p.Workset;
            using (var tx = new Transaction(doc, name))
            {
                try
                {
                    if (tx.Start() != TransactionStatus.Started) throw new InvalidOperationException("transaction did not start");
                    Apply(doc, p); doc.Regenerate(); check = Verify(doc, p);
                }
                catch (Exception ex) { error = ex.Message; }
                try { rb = Guard.RollBack(tx); } catch (Exception ex) { rbError = ex.Message; }
            }
            p.Workset = before;
            return new JObject
            {
                ["verified"] = error == null && check != null && check.AllVerified,
                ["postconditions"] = check?.ToJson(),
                ["error"] = error,
                ["rollback_status"] = rb.HasValue ? rb.Value.StatusName : "exception: " + rbError,
                ["rollback_confirmed"] = rb.HasValue && rb.Value.Confirmed
            };
        }

        private static JArray SkippedJson(Plan p) => new JArray(p.Skipped.Select(s => s.DeepClone()));

        private static JObject PlanJson(Plan p) => new JObject
        {
            ["operation"] = p.Op,
            ["workset_id"] = p.Workset?.IntegerValue,
            ["workset_name"] = p.WorksetName,
            ["name"] = p.Name,
            ["move"] = new JArray(p.Movable.Select(id => new JObject { ["element_id"] = Rid.Value(id), ["from_workset_id"] = p.From[id] })),
            ["already_in_target"] = p.Already.Count,
            ["skipped"] = SkippedJson(p),
            ["view_ids"] = new JArray(p.Views.Select(v => Rid.Value(v.Id))),
            ["visibility"] = p.Views.Count > 0 ? p.Visibility.ToString() : null
        };

        private static ResolvedPlan Resolved(GateResult gate, UIApplication app, Plan p)
        {
            var rp = new ResolvedPlan { Command = "horizun_manage_worksets", DocumentKey = gate.Fingerprint, RevitVersion = app.Application.VersionNumber, DocumentFingerprint = gate.Identity.FingerprintDigest() };
            rp.Elements.Add(new PlannedElement
            {
                UniqueId = "worksets:" + p.Op, Category = "workset",
                Action = p.Op == "create" ? PlannedAction.Create : PlannedAction.Modify,
                BeforeValues = new Dictionary<string, string>
                {
                    ["workset"] = p.Workset == null ? "" : p.Workset.IntegerValue + "|" + p.WorksetName,
                    ["move"] = string.Join(",", p.Movable.Select(id => Rid.Value(id) + ":" + p.From[id])),
                    ["skipped"] = string.Join(",", p.Skipped.Select(s => s.Value<long>("element_id") + ":" + s.Value<string>("reason"))),
                    ["views"] = string.Join(",", p.Views.Select(v => Rid.Value(v.Id) + ":" + v.GetWorksetVisibility(p.Workset)))
                }
            });
            return rp;
        }

        private sealed class Plan
        {
            public string Op, Name, WorksetName;
            public WorksetId Workset;
            public int Requested;
            public WorksetVisibility Visibility;
            public readonly List<View> Views = new List<View>();
            public readonly List<ElementId> Movable = new List<ElementId>(), Already = new List<ElementId>();
            public readonly Dictionary<ElementId, int> From = new Dictionary<ElementId, int>();
            public readonly List<JObject> Skipped = new List<JObject>();
        }
    }
}
