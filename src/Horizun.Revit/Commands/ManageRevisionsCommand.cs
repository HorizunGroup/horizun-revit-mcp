// -----------------------------------------------------------------------------
// Horizun Revit MCP - rehearsed revision production.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public sealed class ManageRevisionsCommand : ICommand
    {
        public string Name => "horizun_manage_revisions";
        public string Description => "Create or edit revisions, assign them to sheets and create revision clouds atomically with rehearsal and verification.";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            JObject request;
            try { request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JObject.Parse(paramsJson); }
            catch (Exception ex) { return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message); }
            GateResult gate = DocumentGate.ForMutation(app, request, Name); if (!gate.Ok) return gate.Refusal;
            Document doc = gate.Document;
            double scale; string units = (request.Value<string>("units") ?? "mm").ToLowerInvariant();
            if (!DimensionPlanRules.UnitScale(units, out scale)) return CommandResult.Fail("units must be mm, m or feet.");
            JArray raw = request["actions"] as JArray;
            if (raw == null || raw.Count < 1 || raw.Count > 100) return CommandResult.Fail("actions must contain 1..100 entries.");
            string error; List<Plan> plans = PlanAll(doc, raw, scale, out error);
            if (plans == null) return CommandResult.Fail(error + " Nothing was written.");

            string hash = DocumentGate.PlanHash(request, "units", "actions");
            bool dry = request["dry_run"] == null || request.Value<bool>("dry_run");
            var resolved = Resolved(gate, app, plans);
            if (dry)
            {
                Rehearsal rehearsal = Rehearse(doc, plans, request.Value<string>("transaction_name") ?? "Horizun: rehearse revisions");
                if (!rehearsal.RollbackConfirmed)
                    return CommandResult.FailWithDetail("Revision rehearsal rollback was not confirmed; model state is uncertain.",
                        new JObject { ["state"] = "uncertain", ["rollback_status"] = rehearsal.RollbackStatus, ["write_started"] = true });
                if (!rehearsal.Verified)
                    return CommandResult.FailWithDetail("Revision rehearsal could not verify the whole batch. Nothing was committed.",
                        new JObject { ["state"] = "refused", ["rehearsal"] = rehearsal.Json });
                DocumentGate.RecordResolvedPlan(resolved);
                var result = new JObject { ["dry_run"] = true, ["valid"] = plans.Count, ["rehearsal"] = rehearsal.Json,
                    ["plan"] = new JArray(plans.Select(PlanJson)) };
                ApplicationOutcome.StampRehearsal(result, plans.Count, 0, 0, 0);
                DocumentGate.StampConfirmation(result, gate, Name, hash, true,
                    "the token binds existing revision state, sheet/view identities and every cloud loop; apply re-runs the same provisional creation before writing.");
                return CommandResult.Ok(result);
            }

            DocumentGate.RecordResolvedPlan(resolved);
            CommandResult refusal = DocumentGate.RequireConfirmation(app, gate, request, Name, hash, resolved, null);
            if (refusal != null) return refusal;

            string txName = request.Value<string>("transaction_name") ?? "Horizun: manage revisions";
            using (var group = new TransactionGroup(doc, txName))
            {
                if (group.Start() != TransactionStatus.Started) return CommandResult.Fail("Could not start the revision TransactionGroup.");
                var tx = new Transaction(doc, txName); TransactionStatus txStatus = TransactionStatus.Uninitialized;
                try
                {
                    if (tx.Start() != TransactionStatus.Started) throw new InvalidOperationException("Could not start the revision transaction.");
                    foreach (Plan p in plans) Apply(doc, p);
                    doc.Regenerate();
                    if (!plans.All(p => Verify(doc, p))) throw new InvalidOperationException("A revision postcondition failed while the batch was still reversible.");
                    txStatus = tx.Commit();
                    if (txStatus != TransactionStatus.Committed) throw new InvalidOperationException("Revision transaction returned " + txStatus + ".");
                    if (!plans.All(p => Verify(doc, p))) throw new InvalidOperationException("A revision postcondition failed after transaction commit.");
                    TransactionStatus gs = group.Assimilate();
                    if (gs != TransactionStatus.Committed) return CommandResult.FailWithDetail("Revision group did not assimilate; state is uncertain.",
                        new JObject { ["state"] = "uncertain", ["transaction_group_status"] = gs.ToString() });
                }
                catch (Exception ex)
                {
                    Guard.RollbackResult? txRollback = null;
                    try { if (tx.GetStatus() == TransactionStatus.Started) txRollback = Guard.RollBack(tx); } catch { }
                    Guard.RollbackResult? groupRollback;
                    try { groupRollback = Guard.RollBack(group); }
                    catch { groupRollback = null; }
                    string rb = groupRollback.HasValue ? groupRollback.Value.StatusName : "Error";
                    return CommandResult.FailWithDetail("Revision batch failed and was rolled back: " + ex.Message,
                        new JObject { ["state"] = groupRollback.HasValue && groupRollback.Value.Confirmed ? "rolled_back" : "uncertain", ["transaction_status"] = txRollback.HasValue ? txRollback.Value.StatusName : txStatus.ToString(), ["transaction_group_status"] = rb });
                }
            }
            if (!plans.All(p => Verify(doc, p)))
                return CommandResult.FailWithDetail(
                    "A revision postcondition contradicted the reversible verification after the TransactionGroup assimilated; state is uncertain.",
                    new JObject { ["state"] = "uncertain", ["transaction_group_status"] = "Committed", ["host_verified"] = false });
            var rows = new JArray(plans.Select(ResultJson));
            var done = new JObject { ["state"] = "committed_verified", ["host_verified"] = true, ["rows"] = rows };
            ApplicationOutcome.StampApplied(done, ApplicationOutcome.Committed, plans.Count, plans.Count, plans.Count, 0, 0, 0);
            return CommandResult.Ok(done);
        }

        private static List<Plan> PlanAll(Document doc, JArray raw, double scale, out string error)
        {
            error = null; var plans = new List<Plan>(); var keys = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < raw.Count; i++)
            {
                JObject a = raw[i] as JObject;
                if (a == null) { error = "actions[" + i + "] is not an object."; return null; }
                string key = a.Value<string>("key"); if (string.IsNullOrWhiteSpace(key) || !keys.Add(key)) { error = "actions[" + i + "].key is empty or duplicated."; return null; }
                string op = (a.Value<string>("operation") ?? "").ToLowerInvariant();
                if (op != "create_revision" && op != "update_revision") { error = "actions[" + i + "].operation must be create_revision or update_revision."; return null; }
                var p = new Plan { Index=i, Key=key, Operation=op, Input=a };
                if (op == "update_revision")
                {
                    long id = a.Value<long?>("revision_id") ?? -1; p.Revision = Rid.CanRepresent(id) ? doc.GetElement(Rid.Make(id)) as Revision : null;
                    if (p.Revision == null) { error = "actions[" + i + "].revision_id does not identify a revision."; return null; }
                    p.Before = RevisionState(p.Revision);
                }
                if (op == "create_revision" && string.IsNullOrWhiteSpace(a.Value<string>("description")))
                { error = "actions[" + i + "].description is required when creating a revision."; return null; }
                JArray sheets = a["sheet_ids"] as JArray;
                if (sheets != null)
                    foreach (JToken t in sheets)
                    {
                        long id = t.Value<long>(); ViewSheet sheet = Rid.CanRepresent(id) ? doc.GetElement(Rid.Make(id)) as ViewSheet : null;
                        if (sheet == null || sheet.IsPlaceholder) { error = "actions[" + i + "] names an invalid or placeholder sheet_id " + id + "."; return null; }
                        if (p.Sheets.Any(s => s.Id == sheet.Id)) { error = "actions[" + i + "] repeats sheet_id " + id + "."; return null; }
                        p.Sheets.Add(sheet);
                    }
                JArray clouds = a["clouds"] as JArray;
                if (clouds != null)
                    for (int ci=0; ci<clouds.Count; ci++)
                    {
                        JObject c = clouds[ci] as JObject; long vid = c?.Value<long?>("view_id") ?? -1;
                        View view = Rid.CanRepresent(vid) ? doc.GetElement(Rid.Make(vid)) as View : null;
                        if (view == null || view.IsTemplate || view is ViewSchedule) { error = "actions["+i+"].clouds["+ci+"] has an invalid view_id."; return null; }
                        JArray loops = c["loops"] as JArray;
                        if (loops == null || loops.Count < 1) { error = "actions["+i+"].clouds["+ci+"] needs one or more loops."; return null; }
                        var cp = new CloudPlan { View=view };
                        foreach (JArray loop in loops.OfType<JArray>())
                        {
                            if (loop.Count < 3 || loop.Count > 200) { error = "Every cloud loop needs 3..200 view-plane points."; return null; }
                            var canonical = new List<string>();
                            for (int pi=0; pi<loop.Count; pi++)
                            {
                                JArray one = loop[pi] as JArray, two = loop[(pi+1)%loop.Count] as JArray;
                                if (one == null || two == null || one.Count != 2 || two.Count != 2) { error = "Cloud points must be [x,y] in view-plane coordinates."; return null; }
                                XYZ a1 = view.Origin.Add(view.RightDirection.Multiply(one[0].Value<double>()*scale)).Add(view.UpDirection.Multiply(one[1].Value<double>()*scale));
                                XYZ a2 = view.Origin.Add(view.RightDirection.Multiply(two[0].Value<double>()*scale)).Add(view.UpDirection.Multiply(two[1].Value<double>()*scale));
                                if (a1.DistanceTo(a2) < 1e-7) { error = "Cloud loops cannot contain duplicate consecutive points."; return null; }
                                cp.Curves.Add(Line.CreateBound(a1,a2)); canonical.Add(Canon(a1)+">"+Canon(a2));
                            }
                            cp.LoopCount++; cp.Signatures.Add(string.Join(";", canonical));
                        }
                        if (cp.LoopCount != loops.Count) { error = "Every clouds.loops entry must be an array."; return null; }
                        p.Clouds.Add(cp);
                    }
                plans.Add(p);
            }
            return plans;
        }

        private static void Apply(Document doc, Plan p)
        {
            if (p.Operation == "create_revision") p.Revision = Revision.Create(doc);
            JObject a = p.Input;
            if (a["description"] != null) p.Revision.Description = a.Value<string>("description") ?? "";
            if (a["revision_date"] != null) p.Revision.RevisionDate = a.Value<string>("revision_date") ?? "";
            if (a["issued_by"] != null) p.Revision.IssuedBy = a.Value<string>("issued_by") ?? "";
            if (a["issued_to"] != null) p.Revision.IssuedTo = a.Value<string>("issued_to") ?? "";
            if (a["issued"] != null) p.Revision.Issued = a.Value<bool>("issued");
            foreach (ViewSheet sheet in p.Sheets)
            {
                List<ElementId> ids = sheet.GetAdditionalRevisionIds().ToList();
                if (!ids.Contains(p.Revision.Id)) { ids.Add(p.Revision.Id); sheet.SetAdditionalRevisionIds(ids); }
            }
            foreach (CloudPlan cloud in p.Clouds)
            {
                RevisionCloud created = RevisionCloud.Create(doc, cloud.View, p.Revision.Id, cloud.Curves);
                cloud.CreatedId = created.Id;
            }
        }

        private static bool Verify(Document doc, Plan p)
        {
            Revision revision = p.Revision == null ? null : doc.GetElement(p.Revision.Id) as Revision;
            if (revision == null) return false; JObject a=p.Input;
            if (a["description"] != null && revision.Description != (a.Value<string>("description") ?? "")) return false;
            if (a["revision_date"] != null && revision.RevisionDate != (a.Value<string>("revision_date") ?? "")) return false;
            if (a["issued_by"] != null && revision.IssuedBy != (a.Value<string>("issued_by") ?? "")) return false;
            if (a["issued_to"] != null && revision.IssuedTo != (a.Value<string>("issued_to") ?? "")) return false;
            if (a["issued"] != null && revision.Issued != a.Value<bool>("issued")) return false;
            if (p.Sheets.Any(s => !(doc.GetElement(s.Id) as ViewSheet).GetAdditionalRevisionIds().Contains(revision.Id))) return false;
            foreach (CloudPlan c in p.Clouds)
            {
                RevisionCloud cloud = c.CreatedId == null ? null : doc.GetElement(c.CreatedId) as RevisionCloud;
                if (cloud == null || cloud.RevisionId != revision.Id || cloud.OwnerViewId != c.View.Id) return false;
            }
            return true;
        }

        private static Rehearsal Rehearse(Document doc, List<Plan> plans, string name)
        {
            var r = new Rehearsal(); using (var tx=new Transaction(doc,name))
            {
                try { if (tx.Start()!=TransactionStatus.Started) throw new InvalidOperationException("transaction did not start"); foreach(Plan p in plans) Apply(doc,p); doc.Regenerate(); r.Verified=plans.All(p=>Verify(doc,p)); }
                catch(Exception ex) { r.Error=ex.Message; r.Verified=false; }
                try { Guard.RollbackResult rollback=Guard.RollBack(tx); r.RollbackStatus=rollback.StatusName; r.RollbackConfirmed=rollback.Confirmed; } catch(Exception ex) { r.RollbackStatus="exception: "+ex.Message; r.RollbackConfirmed=false; }
            }
            r.Json=new JObject { ["constructible_and_verified"]=r.Verified, ["rollback_status"]=r.RollbackStatus, ["error"]=r.Error==null?(JToken)JValue.CreateNull():r.Error };
            return r;
        }

        private static ResolvedPlan Resolved(GateResult gate, UIApplication app, List<Plan> plans)
        {
            var rp=new ResolvedPlan { Command="horizun_manage_revisions", DocumentKey=gate.Fingerprint, RevitVersion=app.Application.VersionNumber, DocumentFingerprint=gate.Identity.FingerprintDigest() };
            foreach(Plan p in plans)
            {
                var before=new Dictionary<string,string> { ["operation"]=p.Operation, ["revision_before"]=p.Before??"<new>", ["sheets"]=string.Join(",",p.Sheets.Select(s=>Rid.Value(s.Id))), ["views"]=string.Join(",",p.Clouds.Select(c=>Rid.Value(c.View.Id))), ["clouds"]=string.Join("|",p.Clouds.SelectMany(c=>c.Signatures)) };
                rp.Elements.Add(new PlannedElement { UniqueId="action:"+p.Key, Category="revision", Action=PlannedAction.Create, BeforeValues=before });
            }
            return rp;
        }

        private static string RevisionState(Revision r) => string.Join("|", new[]{r.UniqueId,r.Description,r.RevisionDate,r.IssuedBy,r.IssuedTo,r.Issued.ToString()});
        private static JObject PlanJson(Plan p) => new JObject { ["index"]=p.Index, ["key"]=p.Key, ["operation"]=p.Operation, ["revision_id"]=p.Operation=="update_revision"?(JToken)Rid.Value(p.Revision.Id):JValue.CreateNull(), ["sheet_ids"]=new JArray(p.Sheets.Select(s=>Rid.Value(s.Id))), ["cloud_views"]=new JArray(p.Clouds.Select(c=>Rid.Value(c.View.Id))) };
        private static JObject ResultJson(Plan p) => new JObject { ["key"]=p.Key, ["revision_id"]=Rid.Value(p.Revision.Id), ["verified"]=true, ["sheet_ids"]=new JArray(p.Sheets.Select(s=>Rid.Value(s.Id))), ["revision_cloud_ids"]=new JArray(p.Clouds.Select(c=>Rid.Value(c.CreatedId))) };
        private static string Canon(XYZ p)=>Math.Round(p.X,9).ToString("R",CultureInfo.InvariantCulture)+","+Math.Round(p.Y,9).ToString("R",CultureInfo.InvariantCulture)+","+Math.Round(p.Z,9).ToString("R",CultureInfo.InvariantCulture);

        private sealed class Plan { public int Index; public string Key,Operation,Before; public JObject Input; public Revision Revision; public readonly List<ViewSheet> Sheets=new List<ViewSheet>(); public readonly List<CloudPlan> Clouds=new List<CloudPlan>(); }
        private sealed class CloudPlan { public View View; public readonly List<Curve> Curves=new List<Curve>(); public readonly List<string> Signatures=new List<string>(); public int LoopCount; public ElementId CreatedId; }
        private sealed class Rehearsal { public bool Verified,RollbackConfirmed; public string Error,RollbackStatus; public JObject Json; }
    }
}
