using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Horizun.Contracts;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    internal static class DocumentBatchRun
    {
        private const string Scope = "horizun_document_session:batch";

        internal static CommandResult Run(JObject request, string host, ConfirmationStore confirmations,
            Func<string, JObject> readSource, Func<bool> cancelled,
            Action<JObject, JObject, bool, Action, Func<Func<CommandResult>, CommandResult>> runModel)
        {
            string problem = BatchSessionRules.Validate(request);
            if (problem != null) return CommandResult.RefuseBeforeWrite(problem);

            bool dry = request.Value<bool?>("dry_run") ?? true;
            var models = (JArray)request["models"];
            string hash = ConfirmationStore.PlanHash(request, "models");
            JArray approvals;
            if (dry) approvals = new JArray();
            else
            {
                if (string.IsNullOrWhiteSpace(request.Value<string>("idempotency_key")))
                    return CommandResult.RefuseBeforeWrite("batch apply requires a new idempotency_key.");
                var check = confirmations.Validate(request.Value<string>("confirmation_token"), Scope, host, hash);
                if (!check.Ok) return CommandResult.RefuseBeforeWrite(check.Message);
                approvals = check.RehearsedRows;
                if (approvals?.Count != models.Count)
                    return CommandResult.RefuseBeforeWrite("The batch approval has no per-model rehearsal. Rehearse again.");
            }

            var rows = new JArray();
            for (int index = 0; index < models.Count; index++)
            {
                var model = (JObject)models[index];
                var row = ReliabilityDefaults.BatchRow(model, host, false);
                row["index"] = index;
                rows.Add(row);
                if (!dry && approvals[index]["source"] == null)
                {
                    rows[index] = approvals[index]["rejected"].DeepClone();
                    rows[index]["skipped"] = "not_approved_in_rehearsal";
                    continue;
                }
                JObject source = null;
                string actionPlan = null;
                try
                {
                    if (cancelled()) throw new InvalidOperationException("Batch cancelled before opening this model.");
                    string path = model.Value<string>("file_path");
                    bool saveInPlace = model.Value<string>("close") == "save" && model.Value<string>("save_as_path") == null;
                    JObject current = readSource(path);
                    source = dry ? current : (JObject)approvals[index]["source"];
                    CheckSource(row, source, current, saveInPlace, "before opening");
                    bool hasActions = model["actions"] is JArray actions && actions.Count > 0;
                    void CheckActions()
                    {
                        if (!hasActions) return;
                        actionPlan = (string)row["steps_rehearsal"]?["data"]?["plan_resolved"]?["fingerprint"];
                        if (string.IsNullOrWhiteSpace(actionPlan) ||
                            (!dry && !string.Equals(actionPlan, (string)approvals[index]["action_plan"], StringComparison.Ordinal)))
                        {
                            row["state"] = dry ? "refused" : "stale_plan";
                            throw new InvalidOperationException(dry ? "The model actions returned no resolved plan fingerprint." :
                                "The resolved model actions differ from the approved batch rehearsal. Rehearse this model again.");
                        }
                    }
                    runModel(model, row, dry, CheckActions, save =>
                    {
                        CheckActions();
                        CheckSource(row, source, readSource(path), saveInPlace, "before saving");
                        return save();
                    });
                    if (row.Value<bool>("success")) CheckActions();
                }
                catch (Exception ex)
                {
                    row["success"] = false;
                    row["error"] = (row.Value<bool?>("has_assumptions") == true ? "SUPUESTO used; " : "") + ex.Message;
                }

                if (dry)
                {
                    if (row.Value<bool>("success")) approvals.Add(new JObject { ["source"] = source, ["action_plan"] = actionPlan });
                    else approvals.Add(new JObject { ["rejected"] = new JObject(row.Properties()
                        .Where(p => p.Name != "reads" && p.Name != "steps_rehearsal" && p.Name != "close" &&
                            p.Name != "open" && p.Name != "relinquish").Select(p => p.DeepClone())) });
                }
            }

            int successful = rows.Count(r => r.Value<bool>("success"));
            var result = new JObject { ["operation"] = "batch", ["dry_run"] = dry, ["models"] = rows,
                ["failed_models"] = rows.Count - successful, ["successful_models"] = successful, ["batch_atomic"] = false,
                ["write_started"] = !dry && rows.Any(r => r["steps_apply"] != null ||
                    r["save"]?["data"]?.Value<bool?>("write_started") == true || r["save"]?["detail"]?.Value<bool?>("write_started") == true),
                ["source_files_saved"] = rows.Any(r => r["save"]?["data"]?.Value<bool?>("source_saved") == true)
                    ? (JToken)true : rows.Any(r => r["save"]?["detail"]?.Value<bool?>("source_save_requested") == true)
                    ? JValue.CreateNull() : (JToken)false,
                ["note"] = "Independent models; successful earlier saves remain. Rehearsal opens and closes but saves nothing. For 11 or more models use horizun_submit_job." };
            if (dry && successful > 0)
                result["confirmation_token"] = confirmations.Issue(Scope, host, hash, rehearsedRows: approvals).Token;
            var notes = rows.SelectMany(r => (r["assumptions"] as JArray ?? new JArray()).OfType<JObject>()).ToList();
            return successful == rows.Count
                ? AssumptionNotes.StampResult(CommandResult.Ok(result), notes)
                : AssumptionNotes.MarkFailure(CommandResult.FailWithDetail("Batch completed with failed models; inspect the per-model evidence.",
                    result), "SUPUESTO used; ", notes);
        }

        private static void CheckSource(JObject row, JObject approved, JObject current, bool saveInPlace, string stage)
        {
            bool approvedHash = HasHash(approved);
            bool currentHash = HasHash(current);
            bool same = JToken.DeepEquals(approved, current);
            row["source_verified"] = approvedHash && currentHash && same;
            if (!approvedHash || !currentHash)
                row["source_verification_error"] = new JObject
                {
                    ["stage"] = stage,
                    ["approved"] = approvedHash ? null : approved?.Value<string>("error") ?? "sha256_unavailable",
                    ["current"] = currentHash ? null : current?.Value<string>("error") ?? "sha256_unavailable"
                };
            if (saveInPlace && (!approvedHash || !currentHash))
                throw new InvalidOperationException("A readable SHA-256 is required " + stage + " for a save to the source file. Rehearse again.");
            if (!same)
                throw new InvalidOperationException("Source changed " + stage + "; this model must be rehearsed again.");
        }

        private static bool HasHash(JObject source)
        {
            string hash = source?.Value<string>("sha256");
            return hash?.Length == 64 && hash.All(Uri.IsHexDigit);
        }

        internal static JObject ReadSource(string path)
        {
            try
            {
                using (var stream = File.OpenRead(path))
                using (var sha = SHA256.Create())
                    return new JObject { ["path"] = Path.GetFullPath(path),
                        ["sha256"] = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "") };
            }
            catch (Exception ex) { return new JObject { ["path"] = path, ["error"] = ex.GetType().Name }; }
        }
    }
}
