// Horizun Revit MCP - review plans use the SAME document/token/typed-child gates.
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.UI;
using Horizun.Revit.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Commands
{
    public sealed partial class ApplyCorrectionsCommand
    {
        private CommandResult ExecuteReviewPlan(UIApplication app, JObject request)
        {
            string[] keys = { "target_document", "correction_plan", "dry_run", "confirmation_token", "idempotency_key" };
            var shape = ScanRequestRules.CheckUnknownKeys(request, keys, Name);
            if (!shape.Ok) return CommandResult.Fail(shape.Message);
            JObject plan = request["correction_plan"] as JObject;
            JObject check = ReviewCorrectionPlan.Check(plan, t => Horizun.Contracts.Contract.Find(t) != null);
            if ((bool)check["runnable"] != true)
                return CommandResult.FailWithDetail("Resolve the review plan before rehearsing. Nothing was changed.", check);
            GateResult gate = DocumentGate.ForMutation(app, request, Name);
            if (!gate.Ok) return gate.Refusal;
            bool dry = (bool?)request["dry_run"] ?? true;
            string hash = DocumentGate.PlanHash(request, "correction_plan");
            var resolved = new ResolvedPlan { Command = Name, DocumentKey = gate.Fingerprint,
                RevitVersion = SafeVersion(app), DocumentFingerprint = gate.Identity.FingerprintDigest() };
            var rehearsals = new JArray();
            bool clean = true;
            foreach (JObject step in (JArray)plan["pasos"])
            {
                string tool = (string)step["herramienta"];
                JObject source = (JObject)step["argumentos"];
                // The outer document cannot silently redirect a plan that names another model.
                string target = (string)source["target_document"];
                if (target != (string)request["target_document"])
                    return CommandResult.Fail("Every review step must name the outer target_document. Nothing was changed.");
                if (source["confirmation_token"] != null || source["idempotency_key"] != null)
                    return CommandResult.Fail("A review plan must not carry spent child tokens or keys. Nothing was changed.");
                string why;
                ICommand child = ResolvePermitted(tool, out why);
                if (child == null) return CommandResult.Fail(why);
                JObject args = ChildArguments(source, gate, true);
                CommandResult r = child.Execute(app, args.ToString(Formatting.None));
                JObject data = ToToken(r.Data ?? r.Detail) as JObject ?? new JObject();
                bool ok = r.Success && ApplicationOutcome.IsValidRehearsal(ApplicationOutcome.Read(data));
                string fingerprint = (string)data["plan_resolved"]?["fingerprint"];
                // Without a child's resolved model facts a token would bind only a request.
                if (string.IsNullOrEmpty(fingerprint)) ok = false;
                clean &= ok;
                rehearsals.Add(new JObject { ["step_id"] = step["id"], ["ok"] = ok, ["reply"] = data, ["error"] = r.Error });
                resolved.Elements.Add(new PlannedElement { UniqueId = "review:" + (string)step["id"], Category = tool,
                    Action = PlannedAction.Modify, BeforeValues = new Dictionary<string, string> { ["child"] = fingerprint ?? "unresolved" } });
            }
            if (dry)
            {
                var preview = new JObject { ["dry_run"] = true, ["rehearsals"] = rehearsals, ["rehearsed_cleanly"] = clean,
                    ["confirmation_withheld"] = !clean, ["rollback_scope"] = RollbackScope };
                ApplicationOutcome.StampRehearsal(preview, rehearsals.Count, clean ? 0 : rehearsals.Count, 0, 0);
                if (clean) DocumentGate.RecordResolvedPlan(resolved);
                DocumentGate.StampConfirmation(preview, gate, Name, hash, clean,
                    "The token binds the review plan and every child's resolved model facts; apply rehearses again and reads every target after all steps.");
                return CommandResult.Ok(preview);
            }
            if (!clean) return CommandResult.FailWithDetail("Not every review step rehearsed cleanly; nothing was applied.", new JObject { ["rehearsals"] = rehearsals });
            CommandResult refusal = DocumentGate.RequireConfirmation(app, gate, request, Name, hash, resolved, null);
            if (refusal != null) return refusal;
            JObject done;
            using (DocumentGate.EnterConfirmedAtomicPlan())
                done = ReviewCorrectionPlan.ApplyAndReRead(plan, step =>
                {
                    string why;
                    ICommand child = ResolvePermitted((string)step["herramienta"], out why);
                    if (child == null) return new JObject { ["error"] = why };
                    JObject args = ChildArguments((JObject)step["argumentos"], gate, false);
                    int index = ((JArray)plan["pasos"]).IndexOf(step);
                    args["__expected_plan_fingerprint"] = rehearsals[index]["reply"]["plan_resolved"]["fingerprint"];
                    CommandResult applied = child.Execute(app, args.ToString(Formatting.None));
                    return ToToken(applied.Data ?? applied.Detail) as JObject ?? new JObject { ["error"] = applied.Error };
                }, (step, reply) => (string)step["herramienta"] == "horizun_write_params_verified"
                    ? WriteParamsCommand.ReReadReview(gate.Document, (JObject)step["argumentos"], reply)
                    : ManageRevisionsCommand.ReReadReview(gate.Document, (JObject)step["argumentos"], reply));
            done["dry_run"] = false;
            done["rollback_scope"] = RollbackScope;
            done["rollback_means"] = RollbackMeans;
            int good = ((JArray)done["steps"]).Count(s => (string)s["state"] == ExchangeManifest.Verified);
            ApplicationOutcome.StampApplied(done, ApplicationOutcome.Committed, rehearsals.Count, good, good, 0, rehearsals.Count - good, 0);
            DocumentGate.StampConfirmation(done, gate, Name, hash, false);
            return CommandResult.Ok(done);
        }
    }
}
