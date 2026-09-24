// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// horizun_undo - "undo the last Horizun batch", because Revit exposes no Undo
// through its API. list shows the document's recorded batches; undo_last applies
// the newest one's inverse (Core/UndoJournal.cs) and refuses when:
//   * the document was saved or synchronized since the batch (the stamp moved);
//   * ANY element the batch touched changed since (state drift) - it never
//     overwrites work it did not do;
//   * the last batch was not recordable - it never skips to an older one.
// dry_run (default) shows exactly that verdict and issues a token; the apply runs
// every inverse in ONE transaction, re-reads each element against the state the
// batch found, rolls back whole on any mismatch, and marks the batch undone only
// after the post-commit re-read passes.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public sealed class UndoCommand : ICommand
    {
        public string Name => "horizun_undo";
        public string Description => "List and undo recorded Horizun write batches, verified against the state each batch left.";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            JObject request;
            try { request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JObject.Parse(paramsJson); }
            catch (JsonException ex) { return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message); }
            string op = request.Value<string>("operation") ?? "list";

            if (op == "list")
            {
                Document active = app.ActiveUIDocument?.Document;
                if (active == null) return CommandResult.Fail("No document is open.");
                string listPath = UndoJournalStore.PathFor(active.Title, UndoCapture.SafePath(active));
                List<UndoBatch> all = UndoJournalStore.Load(listPath);
                string stamp = UndoCapture.SaveStamp(active);
                var rows = new JArray(all.AsEnumerable().Reverse().Select(b =>
                {
                    JObject s = b.Summary();
                    s["saved_since"] = UndoRules.SavedSince(b.SaveStamp, stamp, out string _);
                    return (JToken)s;
                }));
                UndoRules.Last(all, out string why);
                return CommandResult.Ok(new JObject
                {
                    ["document"] = active.Title, ["journal_path"] = listPath, ["batches"] = rows,
                    ["undo_last_available"] = why == null, ["undo_last_blocked_by"] = why,
                    ["note"] = "Only the newest batch is undoable; saves, syncs and later edits to its elements block it."
                });
            }
            if (op != "undo_last") return CommandResult.Fail("operation must be list or undo_last.");

            GateResult gate = DocumentGate.ForMutation(app, request, Name);
            if (!gate.Ok) return gate.Refusal;
            Document doc = gate.Document;
            string path = UndoJournalStore.PathFor(doc.Title, UndoCapture.SafePath(doc));
            List<UndoBatch> batches = UndoJournalStore.Load(path);
            UndoBatch batch = UndoRules.Last(batches, out string refusal);
            if (batch == null) return CommandResult.FailWithDetail("Nothing was undone: " + refusal, new JObject { ["state"] = "refused" });

            if (UndoRules.SavedSince(batch.SaveStamp, UndoCapture.SaveStamp(doc), out string savedWhy))
                return CommandResult.FailWithDetail("Nothing was undone: " + savedWhy, new JObject { ["state"] = "refused", ["batch_id"] = batch.Id, ["reason"] = "saved_since" });
            List<string> drift = UndoRules.Drifted(batch, UndoCapture.Current(doc, batch));
            if (drift.Count > 0)
                return CommandResult.FailWithDetail("Nothing was undone: " + drift.Count + " element state(s) changed since batch " + batch.Id +
                    " (" + string.Join(", ", drift.Take(20)) + "). Undo refuses to overwrite work it did not do.",
                    new JObject { ["state"] = "refused", ["batch_id"] = batch.Id, ["reason"] = "model_changed", ["drifted"] = new JArray(drift) });

            bool dryRun = request["dry_run"] == null || request.Value<bool>("dry_run");
            var scope = new JObject { ["batch_id"] = batch.Id };
            string planHash = DocumentGate.PlanHash(scope, "batch_id");
            if (dryRun)
            {
                var result = new JObject
                {
                    ["dry_run"] = true, ["transaction_status"] = "not_started", ["batch"] = batch.Summary(),
                    ["verdict"] = "undoable", ["note"] = "Nothing was changed. Every element still carries the state this batch left."
                };
                ApplicationOutcome.StampRehearsal(result, batch.Entries.Count, 0, 0, 0);
                DocumentGate.StampConfirmation(result, gate, Name, planHash, true, "the token binds this batch id; drift is re-checked at apply");
                return CommandResult.Ok(result);
            }
            CommandResult gateRefusal = DocumentGate.RequireConfirmation(app, gate, MergeToken(scope, request), Name, planHash);
            if (gateRefusal != null) return gateRefusal;

            var deleted = new List<long>();
            var createdIds = new HashSet<long>(batch.Entries.Where(e => e.Op == "created").SelectMany(e => e.ElementIds));
            string txName = "Horizun: undo " + batch.Tool;
            using (var tx = new Transaction(doc, txName))
            {
                RevitErrorRecorder said = RevitErrorRecorder.On(tx);
                tx.Start();
                try
                {
                    foreach (UndoEntry e in Enumerable.Reverse(batch.Entries)) UndoCapture.ApplyInverse(doc, e, deleted);
                    var beyond = deleted.Where(id => !createdIds.Contains(id)).ToList();
                    if (beyond.Count > 0)
                        throw new InvalidOperationException("deleting the batch's elements would also delete " + beyond.Count +
                            " element(s) it did not create (" + string.Join(", ", beyond.Take(10)) + ")");
                    doc.Regenerate();
                    JObject pre = Check(doc, batch).ToJson();
                    if (pre.Value<bool>("all_verified") != true)
                        throw new InvalidOperationException("the inverse did not restore the recorded state: " + pre.ToString(Formatting.None));
                    Guard.Commit(tx, txName);
                }
                catch (Exception ex)
                {
                    string rb = PlanFailure.NotAttempted; bool attempted = false;
                    if (tx.GetStatus() == TransactionStatus.Started) { attempted = true; rb = Guard.RollBack(tx).StatusName; }
                    return CommandResult.Fail("Undo failed: " + ex.Message + said.Said() + " " +
                        PlanFailure.SingleTransactionOutcome(attempted, rb, "nothing was undone"));
                }
            }

            PostconditionCheck check = Check(doc, batch);
            JObject evidence = check.ToJson();
            if (!check.AllVerified)
                return CommandResult.FailWithDetail("The undo committed but did not re-read as the recorded state; inspect the model.",
                    new JObject { ["state"] = "uncertain", ["postconditions"] = evidence });
            batch.UndoneUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            string journalNote = null;
            try { UndoJournalStore.Save(path, batches); } catch (Exception ex) { journalNote = "the journal could not mark the batch undone: " + ex.Message; }
            var applied = new JObject
            {
                ["dry_run"] = false, ["transaction_status"] = "Committed", ["transaction_name"] = txName,
                ["batch"] = batch.Summary(), ["elements_deleted"] = deleted.Count, ["postconditions"] = evidence,
                ["journal_note"] = journalNote
            };
            ApplicationOutcome.StampApplied(applied, ApplicationOutcome.Committed, batch.Entries.Count,
                                            batch.Entries.Count, batch.Entries.Count, 0, 0, 0);
            return CommandResult.Ok(applied);
        }

        private static JObject MergeToken(JObject scope, JObject request)
        {
            var o = (JObject)scope.DeepClone();
            if (request["confirmation_token"] != null) o["confirmation_token"] = request["confirmation_token"];
            return o;
        }

        /// <summary>Every entry, every element: created is absent; the rest carry the state the batch found.</summary>
        private static PostconditionCheck Check(Document doc, UndoBatch batch)
        {
            var required = new List<string>();
            foreach (UndoEntry e in batch.Entries)
                foreach (long id in e.ElementIds) required.Add(Key(e, id));
            var check = new PostconditionCheck(required.ToArray());
            foreach (UndoEntry e in batch.Entries)
                foreach (long id in e.ElementIds)
                {
                    string k = Key(e, id), sid = id.ToString(CultureInfo.InvariantCulture);
                    try
                    {
                        if (e.Op == "created")
                        {
                            bool gone = UndoCapture.State(doc, id) == null;
                            check.Record(k, "absent", gone ? "absent" : "present", gone);
                        }
                        else if (e.Op == "parameter")
                        {
                            Element el = doc.GetElement(Rid.Make(id));
                            JObject now = UndoCapture.ParameterState(UndoCapture.ResolveParameter(el, e.Inverse.Value<string>("parameter")));
                            if (now == null) check.Unreadable(k, e.Before[sid], "parameter unreadable");
                            else check.Record(k, e.Before[sid], now, UndoRules.StatesMatch(e.Before[sid], now));
                        }
                        else
                        {
                            JObject now = UndoCapture.State(doc, id);
                            if (now == null) check.Unreadable(k, e.Before[sid], "element no longer exists");
                            else check.Record(k, e.Before[sid], now, UndoRules.StatesMatch(e.Before[sid], now));
                        }
                    }
                    catch (Exception ex) { check.Unreadable(k, e.Before[sid], ex.Message); }
                }
            return check;
        }

        private static string Key(UndoEntry e, long id) =>
            e.Op + ":" + id.ToString(CultureInfo.InvariantCulture) + (e.Op == "parameter" ? ":" + e.Inverse.Value<string>("parameter") : "");
    }
}
