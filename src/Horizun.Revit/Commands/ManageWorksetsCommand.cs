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
//   * OWNERSHIP IS A SIDE EFFECT, ANNOUNCED. MEASURED field session, 2026-09-25:
//     renaming a workset silently took ownership of 14,697 elements. create,
//     rename and move_elements now report ownership_effect - elements and the
//     target workset newly owned by the caller, measured with
//     WorksharingUtils.GetCheckoutStatus before/after (the dry run measures it
//     inside its own rolled-back rehearsal). relinquish_after=true gives
//     everything the caller owns back afterward (the same call
//     horizun_relinquish_all makes - Revit has no per-element relinquish scope)
//     and re-measures this operation's own targets to report what remains.
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

            string hash = DocumentGate.PlanHash(request, "operation", "workset_id", "name", "element_ids", "category", "view_ids", "visibility", "relinquish_after");
            ResolvedPlan resolved = Resolved(gate, app, plan);
            bool dry = request["dry_run"] == null || request.Value<bool>("dry_run");
            // create/rename/move_elements can silently take ownership of a great many
            // elements (MEASURED field session, 2026-09-25: renaming a workset took
            // ownership of 14,697 elements with no warning). relinquish_after is only
            // meaningful for those three; set_default/visibility touch no element ownership.
            bool relinquishAfter = (op == "create" || op == "rename" || op == "move_elements") &&
                                    (request.Value<bool?>("relinquish_after") ?? false);
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
            // Measured BEFORE the real transaction, over exactly what this operation's
            // workset touches - the same scope the dry-run rehearsal uses, so the two
            // numbers are comparable.
            List<ElementId> ownershipTargetsBefore = OwnershipTargets(doc, plan);
            int unreadableOwnedBefore;
            Dictionary<long, bool> ownedBeforeApply = OwnedByMe(doc, ownershipTargetsBefore, out unreadableOwnedBefore);
            string worksetOwnerBeforeApply = plan.Workset == null ? null : SafeOwner(doc, plan.Workset);
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
            // Measured AFTER the real commit. For create, plan.Workset now holds the
            // newly persisted id (OwnershipTargets recomputes it fresh, since it did not
            // exist before Apply ran).
            List<ElementId> ownershipTargetsAfter = OwnershipTargets(doc, plan);
            int unreadableOwnedAfter;
            Dictionary<long, bool> ownedAfterApply = OwnedByMe(doc, ownershipTargetsAfter, out unreadableOwnedAfter);
            string worksetOwnerAfterApply = plan.Workset == null ? null : SafeOwner(doc, plan.Workset);
            JObject ownershipEffect = op == "set_default" || op == "visibility"
                ? null
                : OwnershipEffectJson(Math.Max(ownershipTargetsBefore.Count, ownershipTargetsAfter.Count),
                    ownedBeforeApply, ownedAfterApply, unreadableOwnedBefore, unreadableOwnedAfter,
                    worksetOwnerBeforeApply, worksetOwnerAfterApply);
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
            if (ownershipEffect != null) done["ownership_effect"] = ownershipEffect;
            if (relinquishAfter && after.AllVerified)
            {
                JObject relinquishResult = RelinquishAfter(doc);
                int unreadableFinal;
                Dictionary<long, bool> ownedFinal = OwnedByMe(doc, ownershipTargetsAfter, out unreadableFinal);
                relinquishResult["elements_examined"] = ownershipTargetsAfter.Count;
                relinquishResult["elements_still_owned_by_me"] = ownedFinal.Values.Count(v => v);
                relinquishResult["elements_unreadable"] = unreadableFinal;
                relinquishResult["workset_owner_after_relinquish"] = plan.Workset == null ? null : SafeOwner(doc, plan.Workset);
                done["relinquish_after"] = relinquishResult;
            }
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
            List<ElementId> ownershipTargets = OwnershipTargets(doc, p);
            int unreadableBefore;
            Dictionary<long, bool> ownedBefore = OwnedByMe(doc, ownershipTargets, out unreadableBefore);
            string worksetOwnerBefore = p.Workset == null ? null : SafeOwner(doc, p.Workset);
            Dictionary<long, bool> ownedAfter = null; int unreadableAfter = 0; string worksetOwnerAfter = null;
            using (var tx = new Transaction(doc, name))
            {
                try
                {
                    if (tx.Start() != TransactionStatus.Started) throw new InvalidOperationException("transaction did not start");
                    Apply(doc, p); doc.Regenerate(); check = Verify(doc, p);
                    // MEASURED HERE, before the rollback: this is the only place the real
                    // ownership impact can be seen. Reading it after rollback would read
                    // Revit's undo, not its consequence.
                    ownedAfter = OwnedByMe(doc, ownershipTargets, out unreadableAfter);
                    worksetOwnerAfter = p.Workset == null ? null : SafeOwner(doc, p.Workset);
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
                ["rollback_confirmed"] = rb.HasValue && rb.Value.Confirmed,
                ["ownership_effect"] = OwnershipEffectJson(ownershipTargets.Count, ownedBefore, ownedAfter,
                    unreadableBefore, unreadableAfter, worksetOwnerBefore, worksetOwnerAfter)
            };
        }

        /// <summary>
        /// The elements THIS operation's workset touches, so the ownership effect is
        /// measured over exactly what could change - not a whole-document scan.
        /// create has none (the workset does not exist until Apply runs); move_elements
        /// is the elements about to move; rename is every element the renamed workset
        /// already holds (unbounded - MEASURED at 14,697 in the field, and that scale is
        /// exactly why this exists).
        /// </summary>
        private static List<ElementId> OwnershipTargets(Document doc, Plan p)
        {
            switch (p.Op)
            {
                case "rename":
                    if (p.Workset == null) return new List<ElementId>();
                    try
                    {
                        return new FilteredElementCollector(doc).WhereElementIsNotElementType()
                            .WherePasses(new ElementWorksetFilter(p.Workset)).ToElementIds().ToList();
                    }
                    catch { return new List<ElementId>(); }
                case "move_elements":
                    return p.Movable ?? new List<ElementId>();
                default:
                    return new List<ElementId>();
            }
        }

        private static Dictionary<long, bool> OwnedByMe(Document doc, List<ElementId> ids, out int unreadable)
        {
            var result = new Dictionary<long, bool>();
            unreadable = 0;
            foreach (ElementId id in ids)
            {
                try { result[Rid.Value(id)] = WorksharingUtils.GetCheckoutStatus(doc, id) == CheckoutStatus.OwnedByCurrentUser; }
                catch { unreadable++; }
            }
            return result;
        }

        private static string SafeOwner(Document doc, WorksetId id)
        { try { return doc.GetWorksetTable().GetWorkset(id).Owner; } catch { return null; } }

        private static JObject OwnershipEffectJson(int targetCount, Dictionary<long, bool> before, Dictionary<long, bool> after,
            int unreadableBefore, int unreadableAfter, string worksetOwnerBefore, string worksetOwnerAfter)
        {
            if (after == null)
                return new JObject { ["measured"] = false, ["reason"] = "the operation did not reach the point where ownership could be re-measured." };
            int newlyOwned = 0; var sample = new JArray();
            foreach (KeyValuePair<long, bool> kv in after)
            {
                bool wasOwned = before.TryGetValue(kv.Key, out bool b) && b;
                if (kv.Value && !wasOwned)
                {
                    newlyOwned++;
                    if (sample.Count < 50) sample.Add(kv.Key);
                }
            }
            return new JObject
            {
                ["measured"] = true,
                ["elements_examined"] = targetCount,
                ["elements_unreadable_before"] = unreadableBefore,
                ["elements_unreadable_after"] = unreadableAfter,
                ["elements_newly_owned_by_me"] = newlyOwned,
                ["elements_newly_owned_sample"] = sample,
                ["workset_owner_before"] = worksetOwnerBefore,
                ["workset_owner_after"] = worksetOwnerAfter,
                ["means"] = "measured with WorksharingUtils.GetCheckoutStatus before the operation and again, " +
                            "over the elements this operation's workset touches, right after Apply+Regenerate " +
                            "(for a dry run, still inside the rolled-back rehearsal transaction; for an apply, " +
                            "after the real commit). A count above zero is ownership this call took, not a " +
                            "side effect somebody merely suspected."
            };
        }

        /// <summary>
        /// Give back EVERYTHING this user owns (the same call horizun_relinquish_all
        /// makes - WorksharingUtils.RelinquishOwnership has no per-element scope), then
        /// report what Revit itself said it released. The caller re-measures the
        /// operation's own targets afterward, which is the honest "did THIS get let go".
        /// </summary>
        private static JObject RelinquishAfter(Document doc)
        {
            var apiElements = new List<long>();
            var apiWorksets = new List<long>();
            try
            {
                var options = new RelinquishOptions(true)
                {
                    CheckedOutElements = true, FamilyWorksets = true, StandardWorksets = true,
                    UserWorksets = true, ViewWorksets = true
                };
                using (RelinquishedItems apiResult = WorksharingUtils.RelinquishOwnership(doc, options, new TransactWithCentralOptions()))
                {
                    if (apiResult != null)
                    {
                        ICollection<ElementId> ids = apiResult.GetRelinquishedElements();
                        if (ids != null) foreach (ElementId id in ids) apiElements.Add(Rid.Value(id));
                        ICollection<WorksetId> worksets = apiResult.GetRelinquishedWorksets();
                        if (worksets != null) foreach (WorksetId w in worksets) apiWorksets.Add(w.IntegerValue);
                    }
                }
            }
            catch (Exception ex)
            {
                return new JObject { ["attempted"] = true, ["error"] = ex.Message };
            }
            return new JObject
            {
                ["attempted"] = true,
                ["api_reported_relinquished_elements"] = apiElements.Count,
                ["api_reported_relinquished_worksets"] = apiWorksets.Count,
                ["means"] = "this releases EVERY workset and element the current user owns in the document - the " +
                            "same call horizun_relinquish_all makes - not only what this operation took. Anything " +
                            "else you had checked out before this call is released too."
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
