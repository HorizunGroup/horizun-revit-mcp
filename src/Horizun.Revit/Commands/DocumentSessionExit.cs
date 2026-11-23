using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Horizun.Contracts;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Commands
{
    public partial class DocumentSessionCommand
    {
        // A task sets this unpredictable marker in the environment WHEN IT STARTS
        // its own Revit. An existing shared process cannot be claimed by a call.
        private static readonly string ExitOwnerSession = Environment.GetEnvironmentVariable("HORIZUN_OWNER_SESSION");
        private const string ExitScope = "horizun_document_session:exit_application";

        private static CommandResult ExitApplication(UIApplication app, JObject request)
        {
            using (var process = Process.GetCurrentProcess())
            {
                string started = process.StartTime.ToUniversalTime().ToString("O");
                string error = SessionExitRules.OwnershipError(request, process.Id, started, ExitOwnerSession);
                if (error != null) return CommandResult.FailWithDetail(error, new JObject { ["pid"] = process.Id, ["started_utc"] = started, ["write_started"] = false, ["exit_queued"] = false });
                return RunOwnedExit(app, request, process.Id, started);
            }
        }

        internal static CommandResult RunOwnedExit(UIApplication app, JObject request, int pid, string started)
        {
            // Edits, opens and closes invalidate the initial preview. Once it is
            // accepted, our own closes advance ModelEpoch. Keep observing edits
            // to each approved document while activating and closing them.
            if (!QueryCacheLifecycle.Ready) return CommandResult.RefuseBeforeWrite("Document lifecycle observation is unavailable; process exit is refused.");
            long revision = QueryCacheLifecycle.ModelEpoch;
            string hostVersion = HostVersion(app);
            var docs = app.Application.Documents.Cast<Document>().Where(d => !d.IsLinked).ToList();
            var discards = ((JArray)request["discard_documents"] ?? new JArray()).Values<string>().ToList();
            var states = new JArray(docs.Select(doc =>
            {
                JObject state = ReadExitDocument(doc, hostVersion, discards);
                state["lifecycle_revision"] = revision;
                if (SafeWorkshared(doc) != false) state["ownership"] = RelinquishAllCommand.ExitOwnership(doc);
                return state;
            }));
            string foreign = SessionExitRules.ForeignDiscard(states, discards);
            if (foreign != null) return CommandResult.RefuseBeforeWrite(foreign);
            var bound = new JObject { ["documents"] = states, ["discard_documents"] = new JArray(discards) };
            string hash = ConfirmationStore.PlanHash(bound, "documents", "discard_documents");
            string key = pid + ":" + started;
            bool dry = request.Value<bool?>("dry_run") ?? true;
            bool ready = SessionExitRules.Ready(states);
            var result = new JObject { ["operation"] = "exit_application", ["dry_run"] = dry,
                ["pid"] = pid, ["started_utc"] = started, ["documents"] = states,
                ["ownership_basis"] = "launch_environment_marker_and_exact_process_identity",
                ["exit_queued"] = false, ["termination_verified"] = false, ["ready"] = ready, ["write_started"] = false };
            if (dry)
            {
                if (ready) result["confirmation_token"] = DocumentGate.Confirmations.Issue(ExitScope, key, hash).Token;
                else result["next"] = "Save the documents, or explicitly list their fingerprints in discard_documents and rehearse again. Nothing was saved, discarded or closed.";
                return CommandResult.Ok(result);
            }
            if (!ready) return CommandResult.FailWithDetail("Documents have unsaved or pending work without explicit discard authorization.", result);
            if (string.IsNullOrWhiteSpace(request.Value<string>("idempotency_key")))
                return CommandResult.RefuseBeforeWrite("exit_application apply requires a new idempotency_key.");
            var check = DocumentGate.Confirmations.Validate(request.Value<string>("confirmation_token"), ExitScope, key, hash);
            if (!check.Ok) return CommandResult.RefuseBeforeWrite(check.Message);
            var exit = RevitCommandId.LookupPostableCommandId(PostableCommand.ExitRevit);
            if (!app.CanPostCommand(exit)) return CommandResult.RefuseBeforeWrite("Revit cannot accept ExitRevit now.");

            var edited = new HashSet<Document>();
            void OnDocumentEdited(Document document) => edited.Add(document);
            QueryCacheLifecycle.DocumentEditObserved += OnDocumentEdited;
            try { return CloseForExit(app, exit, result, docs, states, hostVersion, discards, edited); }
            finally { QueryCacheLifecycle.DocumentEditObserved -= OnDocumentEdited; }
        }

        private static CommandResult CloseForExit(UIApplication app, RevitCommandId exit, JObject result,
            List<Document> docs, JArray states, string hostVersion, List<string> discards, HashSet<Document> edited)
        {
            var closed = new JArray();
            result["close_results"] = closed;
            for (int index = 0; index < docs.Count; index++)
            {
                Document doc = docs[index];
                var approved = (JObject)states[index];
                CommandResult CheckClose(Document target)
                {
                    JObject current = ReadExitDocument(target, hostVersion, discards);
                    bool wasDirty = approved.Value<bool>("discard_required");
                    bool discardChanged = wasDirty && edited.Contains(doc);
                    bool editDocumentUnknown = wasDirty && edited.Contains(null);
                    if (doc.IsValidObject && OpenGuard.SameDocument(doc, target) &&
                        current.Properties().All(p => JToken.DeepEquals(approved[p.Name], p.Value)) &&
                        !discardChanged && !editDocumentUnknown)
                        return null;
                    current["lifecycle_revision"] = QueryCacheLifecycle.ModelEpoch;
                    string cause = editDocumentUnknown
                        ? "An edit during exit could not be attributed to a document."
                        : "The document or its discard state changed during exit.";
                    return CommandResult.FailWithDetail(cause + " Leave the document open and rehearse exit again.",
                        new JObject { ["state"] = "stale_plan", ["write_started"] = false,
                            ["closed"] = false, ["approved"] = approved, ["current"] = current,
                            ["edits_since_apply_started"] = discardChanged,
                            ["edit_document_unknown"] = editDocumentUnknown });
                }

                string targetName = approved.Value<string>("path");
                if (string.IsNullOrWhiteSpace(targetName)) targetName = approved.Value<string>("title");
                var close = new JObject { ["target_document"] = targetName, ["activate_other"] = true,
                    ["discard_unsaved"] = approved.Value<bool>("discard_authorized"), ["dry_run"] = true };
                var preview = Close(app, close, CheckClose);
                if (!preview.Success)
                {
                    closed.Add(BatchEvidence(preview));
                    return CommandResult.FailWithDetail("Exit not queued: " + preview.Error, result);
                }
                close["dry_run"] = false;
                close["confirmation_token"] = (preview.Data as JObject)?["confirmation_token"]?.DeepClone();
                var applied = Close(app, close, CheckClose);
                closed.Add(BatchEvidence(applied));
                if (applied.Success || applied.Detail?.Value<bool?>("write_started") != false)
                    result["write_started"] = true;
                if (!applied.Success) return CommandResult.FailWithDetail("Exit not queued; inspect close_results: " + applied.Error, result);
            }
            // Only Close's own empty anchor may remain. Never implicitly let the
            // native exit command save/discard any newly modified document.
            if (app.Application.Documents.Cast<Document>().Any(d => !d.IsLinked &&
                SessionExitRules.DiscardRequired(SafeModified(d), SyncReminder.ForHealth(d))))
                return CommandResult.FailWithDetail("Exit not queued: a remaining document has unsaved, pending or unreadable state.", result);
            try { app.PostCommand(exit); }
            catch (Exception ex) { return CommandResult.FailWithDetail("Exit could not be queued: " + ex.Message, result); }
            result["exit_queued"] = true;
            Dispatcher.MarkExiting();
            result["status"] = "queued";
            result["verification_required"] = "Externally check termination of this exact pid AND started_utc within a bounded timeout. A queued command is not proof of termination.";
            return CommandResult.Ok(result);
        }

        private static CommandResult CheckExitClose(Document doc, Func<Document, CommandResult> guard, JObject activation = null)
        {
            CommandResult refusal = guard?.Invoke(doc);
            if (refusal == null) return null;
            refusal.Detail["close_started"] = false;
            if (activation != null)
            {
                refusal.Detail["activation"] = activation;
                refusal.Detail["write_started"] = true;
            }
            return refusal;
        }

        private static JObject ReadExitDocument(Document doc, string hostVersion, List<string> discards)
        {
            string fingerprint = DocumentGate.IdentityOf(doc, hostVersion).FingerprintDigest();
            bool? modified = SafeModified(doc);
            JObject pending = SyncReminder.ForHealth(doc);
            return new JObject { ["fingerprint"] = fingerprint, ["title"] = SafeTitle(doc),
                ["path"] = SafePath(doc), ["modified"] = modified.HasValue ? (JToken)modified.Value : JValue.CreateNull(),
                ["pending_sync"] = pending, ["discard_required"] = SessionExitRules.DiscardRequired(modified, pending),
                ["discard_authorized"] = discards.Contains(fingerprint) };
        }
    }
}
