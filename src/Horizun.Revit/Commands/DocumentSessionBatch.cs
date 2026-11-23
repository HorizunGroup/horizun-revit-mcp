using System;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Horizun.Revit.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Settings = Horizun.Revit.Core.Settings;

namespace Horizun.Revit.Commands
{
    public partial class DocumentSessionCommand
    {
        private CommandResult Batch(UIApplication app, JObject request)
        {
            if (_resolve == null) return CommandResult.RefuseBeforeWrite("Typed command resolver is unavailable.");
            return DocumentBatchRun.Run(request, HostVersion(app), DocumentGate.Confirmations, DocumentBatchRun.ReadSource,
                () => PythonRunRegistry.IsCancelRequested(Job.Ambient?.Id),
                (model, row, dry, checkActions, saveChecked) => RunBatchModel(app, (JArray)request["models"], model, row, dry, checkActions, saveChecked));
        }

        private void RunBatchModel(UIApplication app, JArray models, JObject model, JObject row, bool dry,
            Action checkActions, Func<Func<CommandResult>, CommandResult> saveChecked)
        {
            string host = HostVersion(app);
            Document owned = null;
            var before = app.Application.Documents.Cast<Document>().ToList();
            try
            {
                string path = model.Value<string>("file_path");
                if (before.Any(d => !d.IsLinked && DocIdentity.SamePath(SafePath(d), path)))
                    throw new InvalidOperationException("Source is already open; no existing document was used or closed.");
                string expected = model.Value<string>("expected_version") ?? host;
                bool workshared;
                using (var info = BasicFileInfo.Extract(path)) workshared = info.IsWorkshared;
                ReliabilityDefaults.StampBatchDetach(row, model, workshared);
                string detachError = Horizun.Contracts.BatchSessionRules.DetachDecision(workshared, model.Value<bool?>("detach"), out bool detach);
                if (detachError != null) throw new InvalidOperationException(detachError);
                row["detach"] = detach;
                string destination = model.Value<string>("save_as_path");
                if ((model.Value<string>("close") ?? "discard") == "save" && destination != null)
                {
                    string error = LocalDestinationProblem(destination);
                    if (error != null) throw new InvalidOperationException(error);
                    if (File.Exists(destination)) throw new InvalidOperationException("Batch destination must be new; overwriting is refused.");
                    if (models.OfType<JObject>().Any(m => DocIdentity.SamePath(m.Value<string>("file_path"), destination)))
                        throw new InvalidOperationException("A destination cannot be a source of this batch.");
                }
                var open = new JObject { ["operation"] = "open", ["file_path"] = path,
                    ["expected_version"] = expected, ["detach"] = detach,
                    ["audit"] = model.Value<bool?>("audit") ?? false };
                CommandResult opened = Open(app, open);
                row["open"] = BatchEvidence(opened);
                // Record ownership even on a post-open verification failure.
                var added = app.Application.Documents.Cast<Document>().Where(d => !d.IsLinked && !before.Contains(d)).ToList();
                if (added.Count == 1) owned = added[0];
                if (!opened.Success) throw new InvalidOperationException(opened.Error);
                if (owned == null || !OpenGuard.SameDocument(app.ActiveUIDocument?.Document, owned))
                    throw new InvalidOperationException("The newly opened active document could not be identified.");
                if (SafeWorkshared(owned) != false && !owned.IsDetached)
                {
                    row["warning"] = "Unexpected connected workshared model; no actions or save. Relinquishing current user's ownership without synchronization.";
                    throw new InvalidOperationException("Batch requires a detached workshared document.");
                }
                if (!dry && Settings.ForceReadOnlyOnWorkshared && SafeWorkshared(owned) != false)
                    throw new InvalidOperationException("force_read_only_on_workshared refuses writes to this model.");
                string target = SafePath(owned);
                if (string.IsNullOrWhiteSpace(target)) target = SafeTitle(owned);
                // Reads run on the explicitly named model in both rehearsal and apply.
                // Admit only read-only contracts or registered read operations,
                // and still enforce the tool's permission settings.
                if (model["reads"] is JArray reads)
                {
                    var evidence = new JArray();
                    row["reads"] = evidence;
                    foreach (JObject read in reads)
                    {
                        string tool = read.Value<string>("tool");
                        var contract = Horizun.Contracts.Contract.Find(tool);
                        if (!Horizun.Contracts.BatchSessionRules.AdmitsRead(contract, (JObject)read["arguments"]) ||
                            !Settings.IsToolAllowed(contract, out _))
                            throw new InvalidOperationException("Read operation is unknown, mutating, or not permitted: " + tool);
                        var command = _resolve(tool);
                        if (command == null) throw new InvalidOperationException("Read tool is not installed: " + tool);
                        var args = (JObject)read["arguments"].DeepClone();
                        args["target_document"] = target;
                        var readResult = IdentityPrepass.Execute(app, command, args.ToString(Formatting.None),
                            (a, c, json) => c.Execute(a, json));
                        evidence.Add(new JObject { ["tool"] = tool, ["result"] = BatchEvidence(readResult) });
                        if (!readResult.Success) throw new InvalidOperationException(readResult.Error);
                    }
                }
                var actions = model["actions"] as JArray;
                if (actions != null && actions.Count > 0)
                {
                    var plan = new JObject { ["target_document"] = target, ["actions"] = actions.DeepClone(), ["dry_run"] = true };
                    var executor = new ExecutePlanCommand(_resolve);
                    var preview = IdentityPrepass.Execute(app, executor, plan.ToString(Formatting.None),
                        (a, c, json) => c.Execute(a, json));
                    row["steps_rehearsal"] = BatchEvidence(preview);
                    var data = preview.Data as JObject;
                    if (!preview.Success || string.IsNullOrWhiteSpace(data?.Value<string>("confirmation_token")))
                        throw new InvalidOperationException(preview.Error ?? "Steps did not produce an executable rehearsal.");
                    checkActions();
                    if (!dry)
                    {
                        plan["dry_run"] = false;
                        plan["confirmation_token"] = data["confirmation_token"].DeepClone();
                        // The outer dispatcher reports only after this model has
                        // closed. Run its model-local reports NOW, while creations
                        // can still be marked and re-read before SaveAs.
                        using (var changes = new ChangeWatch(app.Application))
                        {
                            var applied = IdentityPrepass.Execute(app, executor, plan.ToString(Formatting.None),
                                (a, c, json) => c.Execute(a, json));
                            SpatialAfterWrite.Attach(executor.Name, changes, applied);
                            HrzOwnership.AfterCommand(executor.Name, Horizun.Contracts.Contract.Find(executor.Name), changes, applied);
                            SyncReminder.AfterCommand(app, executor.Name, plan.ToString(Formatting.None), changes, applied);
                            row["steps_apply"] = BatchEvidence(applied);
                            if (!applied.Success) throw new InvalidOperationException(applied.Error);
                            if (applied.Data is JObject appliedData && appliedData.TryGetValue("spatial_check", out JToken spatial) &&
                                !SyncLedgerRules.IsCleanSpatialCheck(spatial as JObject))
                            {
                                row["state"] = "spatial_check_failed";
                                throw new InvalidOperationException("The model-local spatial check found conflicts or was incomplete. Closing this copy without saving.");
                            }
                            if (!changes.ObservationComplete) throw new InvalidOperationException("Model-local change observation was incomplete; refusing to save this copy.");
                            foreach (var changed in changes.Documents)
                                foreach (long id in changed.Added)
                                {
                                    var element = changed.Document.GetElement(Rid.Make(id));
                                    if (element == null || changed.Document.IsFamilyDocument || element.Category == null ||
                                        SessionCreationLedger.IsDuplicated(ChangeWatch.Key(changed.Document), element.UniqueId)) continue;
                                    if (!HrzOwnership.IsTrustedOwned(changed.Document, element))
                                        throw new InvalidOperationException("Creation " + id + " has no verified Horizun ownership mark; refusing to save this copy.");
                                }
                        }
                    }
                }
                if (!dry && (model.Value<string>("close") ?? "discard") == "save")
                {
                    var saved = saveChecked(() => SaveBatchDocument(app, owned, destination ?? path, destination == null));
                    row["save"] = BatchEvidence(saved);
                    if (!saved.Success) throw new InvalidOperationException(saved.Error);
                }
                row["success"] = true;
            }
            finally
            {
                if (owned == null)
                {
                    var added = app.Application.Documents.Cast<Document>().Where(d => !d.IsLinked && !before.Contains(d)).ToList();
                    if (added.Count == 1) owned = added[0];
                    else if (added.Count > 1) { row["success"] = false; row["cleanup_error"] = "Multiple new documents; ownership could not be resolved. None closed."; }
                }
                if (owned != null && owned.IsValidObject)
                {
                    try
                    {
                        if (SafeWorkshared(owned) != false && !owned.IsDetached)
                        {
                            row["warning"] = "Unexpected connected workshared document; relinquishing without synchronization before close.";
                            var release = RelinquishAllCommand.Release(app, owned, "batch_cleanup");
                            row["relinquish"] = BatchEvidence(release);
                            if (!release.Success) { row["success"] = false; row["cleanup_error"] = release.Error; }
                        }
                        string target = SafePath(owned);
                        if (string.IsNullOrWhiteSpace(target)) target = SafeTitle(owned);
                        // Loss is limited to the copy opened by this batch, never a pre-existing document.
                        var close = new JObject { ["operation"] = "close", ["target_document"] = target,
                            ["discard_unsaved"] = true, ["activate_other"] = true, ["dry_run"] = true };
                        var preview = Close(app, close);
                        if (!preview.Success) throw new InvalidOperationException(preview.Error);
                        close["dry_run"] = false;
                        close["confirmation_token"] = (preview.Data as JObject)?["confirmation_token"]?.DeepClone();
                        var closed = Close(app, close);
                        row["close"] = BatchEvidence(closed);
                        if (!closed.Success) throw new InvalidOperationException(closed.Error);
                    }
                    catch (Exception ex) { row["success"] = false; row["cleanup_error"] = ex.Message; }
                }
            }
        }

        private static CommandResult SaveBatchDocument(UIApplication app, Document doc, string destination, bool sameSource)
        {
            string error = Horizun.Contracts.BatchSessionRules.SaveDecision(doc.IsWorkshared, doc.IsDetached);
            if (error != null) return CommandResult.RefuseBeforeWrite(error);
            error = LocalDestinationProblem(destination);
            if (error != null) return CommandResult.RefuseBeforeWrite(error);
            string oldKey = ChangeWatch.Key(doc);
            bool saveAs = doc.IsDetached || !sameSource;
            var result = new JObject { ["target_path"] = destination, ["source_save_requested"] = sameSource, ["source_saved"] = JValue.CreateNull(), ["save_as"] = saveAs, ["write_started"] = true };
            try
            {
                if (saveAs)
                {
                    var options = new SaveAsOptions { OverwriteExistingFile = sameSource };
                    if (doc.IsWorkshared) options.SetWorksharingOptions(new WorksharingSaveAsOptions { SaveAsCentral = true });
                    doc.SaveAs(destination, options);
                    DocumentLifecycleRules.SavedAs(oldKey, ChangeWatch.Key(doc), ChangeLedger.Rename);
                }
                else doc.Save();
                var file = ProbeFile(destination);
                result["file"] = file;
                bool verified = file.Value<bool?>("exists") == true && (file.Value<long?>("bytes") ?? 0) > 0 &&
                    SameVersion(VersionOf(file), HostVersion(app)) && DocIdentity.SamePath(SafePath(doc), destination) && SafeModified(doc) == false;
                result["file_verified"] = verified;
                if (verified) result["source_saved"] = sameSource;
                return verified ? CommandResult.Ok(result) : CommandResult.FailWithDetail("Batch save re-read disagreed; inspect the file.", result);
            }
            catch (Exception ex) { return CommandResult.FailWithDetail("Batch save failed; inspect the named destination: " + ex.Message, result); }
        }

        private static JToken BatchEvidence(CommandResult result) => new JObject { ["success"] = result.Success,
            ["data"] = result.Data == null ? null : JToken.FromObject(result.Data),
            ["error"] = result.Error, ["detail"] = result.Detail?.DeepClone() };
    }
}
