// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// ONE WRITE, REHEARSED AND RE-READ, for the single-operation writers of
// horizun_manage_phases and horizun_manage_assemblies_parts.
//
// Some of these writes need more than one transaction (an assembly is named only
// after the transaction that created it has committed), so the unit is a
// TransactionGroup, not a Transaction:
//
//   dry run  - group started, the write applied (every inner transaction
//              committed), the PostconditionCheck read back from the model, and
//              the WHOLE group rolled back. The reply carries the checklist and
//              Revit's rollback status; a token is issued only when the rehearsal
//              verified and the rollback was confirmed.
//   apply    - token spent, the same write inside a group, the checklist read
//              back while the group is still reversible; any property not
//              verified rolls the whole group back. After Assimilate the
//              checklist is read AGAIN from the committed model, and that second
//              reading is the evidence published.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    internal sealed class StagedWrite
    {
        public string Operation;
        /// <summary>Applies the write. Runs its own transactions through StagedGroupWrite.Tx.</summary>
        public Action<Document> Apply;
        /// <summary>Re-reads the model and returns the checklist; must use ids the last Apply recorded.</summary>
        public Func<Document, PostconditionCheck> Verify;
        /// <summary>What the write resolved to, before anything ran.</summary>
        public JObject Plan;
        /// <summary>What the committed write produced (created ids and the like), filled after Apply.</summary>
        public Func<JObject> Result;
        public ResolvedPlan Resolved;
        /// <summary>Said in the rehearsal and in the apply reply, e.g. the view-visibility consequence.</summary>
        public string Warning;
        public int Count = 1;
    }

    internal static class StagedGroupWrite
    {
        /// <summary>One inner transaction that must COMMIT, or the write throws.</summary>
        public static void Tx(Document doc, string name, Action body)
        {
            using (var tx = new Transaction(doc, name))
            {
                if (tx.Start() != TransactionStatus.Started) throw new InvalidOperationException("transaction '" + name + "' did not start");
                try { body(); doc.Regenerate(); }
                catch
                {
                    try { if (tx.GetStatus() == TransactionStatus.Started) tx.RollBack(); } catch { }
                    throw;
                }
                TransactionStatus s = tx.Commit();
                if (s != TransactionStatus.Committed) throw new InvalidOperationException("transaction '" + name + "' returned " + s);
            }
        }

        public static ResolvedPlan NewResolved(string command, GateResult gate, UIApplication app)
            => new ResolvedPlan
            {
                Command = command, DocumentKey = gate.Fingerprint, RevitVersion = app.Application.VersionNumber,
                DocumentFingerprint = gate.Identity.FingerprintDigest()
            };

        public static CommandResult Run(UIApplication app, GateResult gate, JObject request, string command, StagedWrite w, string hash)
        {
            Document doc = gate.Document;
            bool dry = request["dry_run"] == null || request.Value<bool>("dry_run");
            string txName = "Horizun: " + w.Operation.Replace('_', ' ');
            if (dry)
            {
                PostconditionCheck check = null; string error = null; Guard.RollbackResult? rb = null;
                using (var group = new TransactionGroup(doc, txName + " (rehearsal)"))
                {
                    if (group.Start() != TransactionStatus.Started) return CommandResult.Fail("Could not start the rehearsal TransactionGroup. Nothing was written.");
                    try { w.Apply(doc); check = w.Verify(doc); }
                    catch (Exception ex) { error = ex.Message; }
                    try { rb = Guard.RollBack(group); } catch (Exception ex) { error = (error == null ? "" : error + "; ") + "rollback: " + ex.Message; }
                }
                if (!rb.HasValue || !rb.Value.Confirmed)
                    return CommandResult.FailWithDetail("The rehearsal rollback was not confirmed; model state is uncertain.",
                        new JObject { ["state"] = "uncertain", ["write_started"] = true, ["rollback_status"] = rb.HasValue ? rb.Value.StatusName : "Error", ["error"] = error });
                if (check == null || !check.AllVerified)
                    return CommandResult.FailWithDetail("The rehearsal could not verify the " + w.Operation + " write" +
                        (error != null ? ": " + error : "") + ". It was rolled back; nothing was written.",
                        new JObject { ["state"] = "refused", ["plan"] = w.Plan, ["rollback_status"] = rb.Value.StatusName,
                                      ["postconditions"] = check == null ? (JToken)JValue.CreateNull() : check.ToJson(), ["error"] = error });
                DocumentGate.RecordResolvedPlan(w.Resolved);
                var result = new JObject
                {
                    ["dry_run"] = true, ["operation"] = w.Operation, ["plan"] = w.Plan,
                    ["rehearsal"] = new JObject { ["constructible_and_verified"] = true, ["rollback_status"] = rb.Value.StatusName, ["postconditions"] = check.ToJson() }
                };
                if (w.Warning != null) result["warning"] = w.Warning;
                ApplicationOutcome.StampRehearsal(result, w.Count, 0, 0, 0);
                DocumentGate.StampConfirmation(result, gate, command, hash, true,
                    "the token binds the resolved elements and their state before the write; apply re-runs the same write and re-reads it.");
                return CommandResult.Ok(result);
            }

            DocumentGate.RecordResolvedPlan(w.Resolved);
            CommandResult refusal = DocumentGate.RequireConfirmation(app, gate, request, command, hash, w.Resolved, null);
            if (refusal != null) return refusal;

            using (var group = new TransactionGroup(doc, txName))
            {
                if (group.Start() != TransactionStatus.Started) return CommandResult.Fail("Could not start the TransactionGroup. Nothing was written.");
                PostconditionCheck reversible = null;
                try
                {
                    w.Apply(doc);
                    reversible = w.Verify(doc);
                    if (!reversible.AllVerified) throw new InvalidOperationException("a postcondition failed while the write was still reversible");
                    TransactionStatus gs = group.Assimilate();
                    if (gs != TransactionStatus.Committed)
                        return CommandResult.FailWithDetail("The TransactionGroup did not assimilate; state is uncertain.",
                            new JObject { ["state"] = "uncertain", ["transaction_group_status"] = gs.ToString() });
                }
                catch (Exception ex)
                {
                    Guard.RollbackResult? rb = null;
                    try { if (group.GetStatus() == TransactionStatus.Started) rb = Guard.RollBack(group); } catch { }
                    bool confirmed = rb.HasValue && rb.Value.Confirmed;
                    return CommandResult.FailWithDetail("The " + w.Operation + " write failed and was rolled back: " + ex.Message,
                        new JObject { ["state"] = confirmed ? "rolled_back" : "uncertain", ["transaction_group_status"] = rb.HasValue ? rb.Value.StatusName : "Error",
                                      ["postconditions"] = reversible == null ? (JToken)JValue.CreateNull() : reversible.ToJson() });
                }
            }

            PostconditionCheck committed = w.Verify(doc);
            if (!committed.AllVerified)
                return CommandResult.FailWithDetail("A postcondition contradicted the reversible verification after the group assimilated; state is uncertain.",
                    new JObject { ["state"] = "uncertain", ["host_verified"] = false, ["postconditions"] = committed.ToJson() });
            var done = new JObject
            {
                ["state"] = "committed_verified", ["host_verified"] = true, ["operation"] = w.Operation,
                ["result"] = w.Result == null ? new JObject() : w.Result(), ["postconditions"] = committed.ToJson()
            };
            if (w.Warning != null) done["warning"] = w.Warning;
            ApplicationOutcome.StampApplied(done, ApplicationOutcome.Committed, w.Count, w.Count, w.Count, 0, 0, 0);
            return CommandResult.Ok(done);
        }

        public static List<ElementId> Ids(JObject request, string field, int max, out string error)
        {
            error = null; var ids = new List<ElementId>();
            JArray raw = request[field] as JArray;
            if (raw == null || raw.Count == 0) { error = field + " must list at least one element id."; return null; }
            if (raw.Count > max) { error = field + " takes at most " + max + " ids."; return null; }
            foreach (JToken t in raw)
            {
                long v;
                try { v = t.Value<long>(); } catch { error = field + " must contain integers."; return null; }
                if (!Rid.CanRepresent(v)) { error = Rid.RangeError(v); return null; }
                ElementId id = Rid.Make(v);
                if (ids.Contains(id)) { error = field + " repeats id " + v + "."; return null; }
                ids.Add(id);
            }
            return ids;
        }

        public static JArray Json(IEnumerable<ElementId> ids)
        {
            var a = new JArray(); foreach (ElementId id in ids) a.Add(Rid.Value(id)); return a;
        }
    }
}
