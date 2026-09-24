// -----------------------------------------------------------------------------
// Horizun Revit MCP - horizun_link_schedule, operation=write. Original Horizun code.
//
// Stamps each matched element's activity id (and optionally start/finish, ISO
// yyyy-MM-dd) into TEXT INSTANCE parameters. A row whose parameter is missing,
// read-only, a type parameter or not text is listed as unresolved and not
// written - the batch writes only what it can then re-read. Any Set that Revit
// refuses inside the transaction rolls the whole batch back.
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
    public sealed partial class LinkScheduleCommand
    {
        private sealed class WriteRow
        {
            public long Id;
            public string UniqueId;
            public string Activity;
            public Dictionary<string, string> Values = new Dictionary<string, string>(StringComparer.Ordinal);
            public Dictionary<string, string> Before = new Dictionary<string, string>(StringComparer.Ordinal);
            public string Unresolved;
        }

        private CommandResult Write(UIApplication app, JObject request, ScheduleImportResult schedule, JObject spec,
                                    string fileSha, JObject head, int maxRows)
        {
            JObject w = request["write"] as JObject;
            string actParam = w?.Value<string>("activity_parameter");
            if (string.IsNullOrWhiteSpace(actParam))
                return CommandResult.Fail("write.activity_parameter is required (a text instance parameter).");
            string startParam = w.Value<string>("start_parameter");
            string finishParam = w.Value<string>("finish_parameter");

            GateResult gate = DocumentGate.ForMutation(app, request, Name);
            if (!gate.Ok) return gate.Refusal;
            Document doc = gate.Document;
            bool dryRun = request["dry_run"] == null || request.Value<bool>("dry_run");

            ScheduleMatch m = ScheduleLinkRules.Match(spec, schedule.Activities, Facts(doc, spec, null));
            var rows = new List<WriteRow>();
            foreach (var kv in m.Links.OrderBy(k => k.Key))
            {
                Element e = doc.GetElement(Rid.Make(kv.Key));
                var row = new WriteRow { Id = kv.Key, UniqueId = e?.UniqueId, Activity = kv.Value.Id };
                row.Values[actParam] = kv.Value.Id;
                if (startParam != null) row.Values[startParam] = ScheduleImport.Day(kv.Value.Start) ?? "";
                if (finishParam != null) row.Values[finishParam] = ScheduleImport.Day(kv.Value.Finish) ?? "";
                foreach (string p in row.Values.Keys.ToList())
                {
                    string why = Writable(e, p, out string before);
                    if (why != null) { row.Unresolved = why; break; }
                    row.Before[p] = before ?? "";
                }
                rows.Add(row);
            }
            List<WriteRow> ready = rows.Where(r => r.Unresolved == null).ToList();
            int unresolved = rows.Count - ready.Count;

            var plan = new ResolvedPlan
            {
                Command = Name, DocumentKey = gate.Fingerprint,
                RevitVersion = app?.Application?.VersionNumber, DocumentFingerprint = gate.Identity?.FingerprintDigest()
            };
            foreach (WriteRow r in ready)
                plan.Elements.Add(new PlannedElement
                {
                    UniqueId = r.UniqueId, ElementId = r.Id, Action = PlannedAction.Modify,
                    BeforeValues = new Dictionary<string, string>(r.Before), ProposedValues = new Dictionary<string, string>(r.Values)
                });
            string planHash = PlanHash(request, fileSha, "write");

            head["document"] = doc.Title;
            MatchJson(head, m, maxRows);
            head["rows_ready"] = ready.Count;
            head["rows_unresolved"] = unresolved;
            head["unresolved"] = new JArray(rows.Where(r => r.Unresolved != null).Take(maxRows)
                .Select(r => new JObject { ["element_id"] = r.Id, ["reason"] = r.Unresolved }));

            if (dryRun)
            {
                head["dry_run"] = true;
                head["preview"] = new JArray(ready.Take(maxRows).Select(r => new JObject
                    { ["element_id"] = r.Id, ["activity"] = r.Activity, ["values"] = JObject.FromObject(r.Values) }));
                DocumentGate.RecordResolvedPlan(plan);
                DocumentGate.StampConfirmation(head, gate, Name, planHash, true,
                    "the token binds the schedule file's SHA-256, the match spec, the parameters and the resolved rows.");
                ApplicationOutcome.StampRehearsal(head, rows.Count, unresolved, 0, 0);
                return CommandResult.Ok(head);
            }
            if (ready.Count == 0)
                return CommandResult.Fail("Nothing to write: no matched element has writable text instance parameters. " +
                                          "See unresolved in a dry run. Nothing was changed.");

            CommandResult refusal = DocumentGate.RequireConfirmation(app, gate, request, Name, planHash, plan, null);
            if (refusal != null) return refusal;

            TransactionStatus status = TransactionStatus.Uninitialized;
            using (var tx = new Transaction(doc, "Horizun: link schedule (4D) parameters"))
            {
                tx.Start();
                try
                {
                    foreach (WriteRow r in ready)
                    {
                        Element e = doc.GetElement(Rid.Make(r.Id));
                        foreach (var kv in r.Values)
                            if (e == null || CodeCheckCommand.Lookup(e, kv.Key)?.Set(kv.Value) != true)
                            {
                                Guard.RollBack(tx);
                                return CommandResult.FailWithDetail("Revit refused to set '" + kv.Key + "' on element " + r.Id +
                                    ". The whole batch was rolled back; nothing was changed.",
                                    new JObject { ["state"] = "rolled_back", ["write_started"] = true, ["changes_applied"] = false });
                            }
                    }
                    try { status = Guard.Commit(tx, "link schedule parameters"); }
                    catch (SilentRollbackException ex) { status = ex.Status; }
                }
                catch
                {
                    if (tx.GetStatus() == TransactionStatus.Started) Guard.RollBack(tx);
                    throw;
                }
            }

            // Re-read from the committed model, row by row.
            int verified = 0, unverified = 0;
            var verification = new JArray();
            foreach (WriteRow r in ready)
            {
                Element e = doc.GetElement(Rid.Make(r.Id));
                bool ok = e != null && r.Values.All(kv => string.Equals(CodeCheckCommand.Lookup(e, kv.Key)?.AsString() ?? "", kv.Value, StringComparison.Ordinal));
                if (ok) verified++; else unverified++;
                if (!ok || verification.Count < maxRows)
                    verification.Add(new JObject { ["element_id"] = r.Id, ["verified"] = ok });
            }
            head["dry_run"] = false;
            head["verification"] = verification;
            head["rows_verified"] = verified;
            DocumentGate.StampConfirmation(head, gate, Name, planHash, false);
            ApplicationOutcome.Stamp(head, WriteTally.PerTarget(status.ToString(), ready.Count, unresolved, verified, unverified));
            return status == TransactionStatus.Committed && unverified == 0
                ? CommandResult.Ok(head)
                : CommandResult.FailWithDetail("The write did not verify: commit " + status + ", " + unverified +
                                               " row(s) re-read with a different value.", head);
        }

        /// <summary>Null when the instance parameter can take text; else why not. `before` is its current value.</summary>
        private static string Writable(Element e, string name, out string before)
        {
            before = null;
            if (e == null) return "the element no longer exists.";
            try { if (e.GroupId != ElementId.InvalidElementId) return "the element is a group member; its instance parameters may be locked by the group."; } catch { }
            Parameter p;
            p = CodeCheckCommand.Lookup(e, name);
            if (p == null) return "no INSTANCE parameter '" + name + "' (a type parameter would stamp every instance of the type).";
            if (p.StorageType != StorageType.String) return "'" + name + "' is not a text parameter.";
            if (p.IsReadOnly) return "'" + name + "' is read-only.";
            try { before = p.AsString(); } catch { }
            return null;
        }
    }
}
