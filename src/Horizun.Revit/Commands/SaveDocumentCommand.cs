// -----------------------------------------------------------------------------
// Horizun MCP — original Horizun code.
//
// Save the active document, and prove it landed.
//
// "Saved" is one of the easiest lies to tell: Document.Save() returns void, so a
// handler that calls it and reports success is reporting that it did not throw —
// not that the file changed. This one stats the file BEFORE and AFTER and reports
// both timestamps and both sizes, so the claim is backed by the filesystem.
//
// Two refusals, both deliberate:
//   * A document that has never been saved has no path. Saving it means INVENTING
//     one, and this command does not choose where your model lives. Refused.
//   * A workshared document: Save() writes the LOCAL file. It does not send a
//     thing to central. Reporting "saved" to someone who meant "synced" is how
//     a day of work is lost, so the answer says which one happened, loudly.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Commands
{
    public sealed class SaveDocumentCommand : ICommand
    {
        public string Name => "horizun_save_document";

        public string Description =>
            "Save the ACTIVE document and verify it against the filesystem: the file's timestamp and size are " +
            "read before and after, and reported. Refuses a document that was never saved (it will not invent a " +
            "path) and never calls SaveAs. On a workshared model this saves the LOCAL file only — it is NOT a " +
            "synchronize with central, and the response says so.";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            string expectedTitle = null;
            JObject req = null;
            try
            {
                req = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : Horizun.Revit.Core.JsonText.ParseObject(paramsJson);
                expectedTitle = req.Value<string>("expected_document");
            }
            catch (Exception ex)
            {
                return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message);
            }

            // WHICH OPERATION, and may this machine run it. Decided before anything is read
            // from the model: the tool is listed on safe_write and documentation for
            // save_local and relinquish_own, and a sync - which publishes into a file other
            // people work from and cannot be rolled back - is refused here by name unless the
            // profile is full_write or unsafe_code (OperationPermission.SaveRefusal).
            string operation = FieldCallRules.SaveOperation(req);
            bool operationDefaulted = req["operation"] == null;
            // INT-6 m-1: the profile, the owner's grant and the sync-only flag are read ONCE for this call, and every decision below
            // (the refusal, the strict precheck, the authorisation the sync itself checks) comes from SyncOnlyGrantRules.Decide.
            // Unreadable settings fall CLOSED.
            Horizun.Revit.Core.Settings.SyncSettingsSnapshot syncSettings;
            try { syncSettings = Horizun.Revit.Core.Settings.ReadSyncSettings(); }
            catch { syncSettings = new Horizun.Revit.Core.Settings.SyncSettingsSnapshot(); }
            SyncOnlyGrantRules.SyncDecision syncDecision = SyncOnlyGrantRules.Decide(syncSettings);
            string opRefusal = OperationPermission.SaveRefusal(syncSettings.Profile, operation,
                syncSettings.OwnerSyncGrant, syncSettings.SyncOnlyFlag);
            if (opRefusal != null) return CommandResult.Fail(opRefusal);
            if (operation == OperationPermission.SaveLocal)
            {
                foreach (string field in new[] { "comment", "relinquish", "confirmation_token", "accept_findings", "accept_findings_ids" })
                    if (req[field] != null && req[field].Type != JTokenType.Null)
                    {
                        var detail = new JObject { ["write_started"] = false };
                        return CommandResult.FailCoded(ErrorCodes.EsquemaInvalido, field + " is not available for save_local. " +
                            "For a central sync specify operation=sync_with_central and a non-empty comment. Nothing was saved.", extra: detail);
                    }
                // Field 2026-11-07..10: 14 refusals of save_local with dry_run=true, the default habit every other write
                // teaches. A save has nothing to rehearse, so the rehearsal answers what WOULD be saved instead of failing.
                if (req.Value<bool?>("dry_run") == true)
                {
                    Document target = app.ActiveUIDocument?.Document;
                    return CommandResult.Ok(new JObject
                    {
                        ["operation"] = "save_local", ["rehearsal"] = true, ["saved"] = false,
                        ["write_started"] = false, ["changes_applied"] = false,
                        ["document"] = target?.Title, ["path"] = target?.PathName,
                        ["is_modified"] = target?.IsModified,
                        ["next"] = "save_local has nothing to rehearse: send the same call without dry_run (or dry_run=false) to save the " +
                                   "local file; it is proved from disk afterwards. Nothing was saved by this call."
                    });
                }
            }
            else if (req["require_gate"] != null && req["require_gate"].Type != JTokenType.Null)
                return CommandResult.Fail("require_gate applies to operation=save_local. Nothing ran.");
            if (operation == OperationPermission.SyncWithCentral)
            {
                // THE SAME SYNC horizun_document_session runs - owner grant, workshared
                // policy, estimate, token bound to the estimate, re-read afterwards - with ONE
                // addition: the comment is mandatory here. A sync with no comment is a central
                // history entry nobody can attribute to a change.
                string comment = req.Value<string>("comment");
                if (string.IsNullOrWhiteSpace(comment))
                    return CommandResult.Fail("operation=sync_with_central requires a non-empty comment: it is stored " +
                                              "in the central model's history. Nothing was synchronized.");
                if (req["target_document"] == null && req["expected_document"] != null)
                    req["target_document"] = req["expected_document"];
                req.Remove("expected_document");
                // M10: with the owner's sync-only flag ON and profile safe_write the sync is STRICT
                // (Core/SyncOnlyGrantRules.cs). Flag off: exactly the earlier behaviour.
                return DocumentSessionCommand.SyncWithCentralFromSave(app, req, syncDecision.Strict, syncDecision.Authorised, syncSettings.ForceReadOnly, syncDecision);
            }
            if (operation == OperationPermission.RelinquishOwn)
            {
                foreach (string field in new[] { "comment", "relinquish", "compact", "accept_findings", "accept_findings_ids" })
                    if (req[field] != null && req[field].Type != JTokenType.Null)
                        return CommandResult.Fail(field + " applies to operation=sync_with_central. Nothing ran.");
                GateResult relinquishGate = DocumentGate.ForMutation(app, req, Name);
                if (!relinquishGate.Ok) return relinquishGate.Refusal;
                return RelinquishAllCommand.RelinquishOwn(app, relinquishGate, req);
            }

            // WHICH model. expected_document was an OPTIONAL guard here and is now required
            // through the shared gate, which also covers the two cases a title comparison
            // never could: a name matching more than one open document, and a name matching
            // one that is open but NOT active. Saving the wrong model is not recoverable by
            // saving again.
            GateResult gate = DocumentGate.ForMutation(app, req, Name);
            if (!gate.Ok) return gate.Refusal;
            Document doc = gate.Document;
            if (OpenRecoveryDocuments.IsMarked(doc)) return CommandResult.Fail("A document recovered in memory can only be saved with an explicitly confirmed save_as to a different new path.");

            string path = doc.PathName;
            if (string.IsNullOrEmpty(path))
            {
                return CommandResult.Fail(
                    "This document has never been saved, so it has no path. Saving it would mean choosing where " +
                    "your model lives, and this command does not do that. Save it once from Revit, then call this.");
            }

            // THE PREVENTION GATE, BEFORE THE FILE IS TOUCHED. Optional: without
            // require_gate this command behaves exactly as it always has. With it, the
            // audit's checks run on the document as it stands, the caller's profile is
            // evaluated with the audit's own evaluator, and a blocked or not-assessable
            // decision refuses HERE - above doc.Save() - so a refused save leaves the
            // file's bytes exactly as they were. The reply carries the decision either
            // way, and names the save paths this gate does not reach.
            OperationGateResult gateDecision = OperationGate.Evaluate(app, doc, req["require_gate"],
                                                                      GatedOperation.Save, Name);
            if (gateDecision.Refusal != null) return gateDecision.Refusal;

            // IsModified is measured, not assumed. Its BEFORE value is what makes the
            // difference between "nothing needed writing" and "something did and was not
            // written", which the old version could not tell apart because it reported
            // saved=true for both.
            bool? modifiedBefore = null;
            try { modifiedBefore = doc.IsModified; } catch { }

            // A CLOUD model's PathName is not a file. Revit writes its local copy into the
            // collaboration cache (Core/CloudCacheLocator.cs); that copy is what is measured.
            bool cloud = false;
            try { cloud = doc.IsModelInCloud; } catch { }
            string cacheRoot = null, cacheAccount = null, projectGuid = null, modelGuid = null, cloudIdError = null;
            List<CloudCacheCandidate> cacheBefore = null;
            if (cloud)
            {
                try { ModelPath mp = doc.GetCloudModelPath(); projectGuid = mp.GetProjectGUID().ToString(); modelGuid = mp.GetModelGUID().ToString(); }
                catch (Exception ex) { cloudIdError = ex.Message; }
                try { cacheAccount = app.Application.LoginUserId; } catch { }
                cacheRoot = CloudCacheLocator.CacheRoot(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), app.Application.VersionNumber);
                cacheBefore = CloudCacheLocator.Find(cacheRoot, cacheAccount, projectGuid, modelGuid);
            }

            DateTime? beforeStamp = null; long? beforeSize = null;
            ReadFileFacts(path, out beforeStamp, out beforeSize);
            string hashBefore = cloud ? null : HashOf(path);

            bool compact = req.Value<bool?>("compact") ?? false;
            try
            {
                if (compact) doc.Save(new SaveOptions { Compact = true });
                else doc.Save();
            }
            catch (Exception ex)
            {
                if (compact) return CommandResult.FailCoded(ErrorCodes.EsquemaInvalido,
                    "Revit refused the compact save: " + ex.Message,
                    extra: new JObject { ["write_started"] = false });
                return CommandResult.Fail("Revit refused to save: " + ex.Message);
            }

            DateTime? afterStamp = null; long? afterSize = null;
            ReadFileFacts(path, out afterStamp, out afterSize);
            string hashAfter = cloud ? null : HashOf(path);
            CloudSaveVerdict cacheVerdict = cloud ? CloudCacheLocator.Judge(cacheBefore, CloudCacheLocator.Find(cacheRoot, cacheAccount, projectGuid, modelGuid), cacheAccount) : null;

            bool? modifiedAfter = null;
            try { modifiedAfter = doc.IsModified; } catch { }

            // ---- Did the bytes move? Hash first; stat only when hashing could not run. ----
            //
            // A timestamp and a size can both stay put across a real write, and a
            // timestamp alone can move without the content changing. The hash settles it
            // when it is available, and when it is not the answer is UNKNOWN rather than
            // whatever the weaker signal happened to say.
            bool? bytesChanged = null;
            string evidence;
            if (hashBefore != null && hashAfter != null)
            {
                bytesChanged = hashBefore != hashAfter;
                evidence = "sha256 of the file, read before and after";
            }
            else if (beforeStamp.HasValue && afterStamp.HasValue)
            {
                bytesChanged = afterStamp.Value != beforeStamp.Value || afterSize != beforeSize;
                evidence = "timestamp and size only - the file could not be hashed, so a write that left both " +
                           "unchanged would not be visible here";
            }
            else if (cacheVerdict != null && cacheVerdict.Changed.HasValue)
            {
                bytesChanged = cacheVerdict.Changed.Value;
                evidence = "timestamp and size of the cloud model's local copy in the collaboration cache (" + cacheVerdict.Path + ")";
            }
            else
            {
                evidence = cloud
                    ? "none - this is a cloud model and its local copy was not found in the collaboration cache" + (cloudIdError != null ? " (model ids unreadable: " + cloudIdError + ")" : "")
                    : "none - the file could not be read before or after";
            }

            // ---- The outcome. saved=true means DEMONSTRATED, and nothing else. ----
            // Bytes first; when they cannot be measured (a cloud model whose local copy was
            // not found), Revit's own unsaved-changes flag clearing is the weaker evidence
            // left. Decided in Core/SaveLocalVerdict.cs, unit-tested there.
            SaveLocalOutcome verdict = SaveLocalVerdict.Decide(bytesChanged, modifiedBefore, modifiedAfter, cloud);
            string outcome = verdict.Outcome;
            string means = verdict.Means;
            if (verdict.EvidenceStrength == "revit_flag")
                evidence = "Document.IsModified true -> false across Save() (" + evidence + ")";

            bool demonstrated = verdict.Saved;

            var payload = new JObject
            {
                // Only ever true when the write was demonstrated. It used to be a constant.
                ["saved"] = demonstrated,
                ["operation"] = OperationPermission.SaveLocal,
                ["operation_defaulted"] = operationDefaulted,
                ["compact"] = compact,
                ["size_before_bytes"] = beforeSize,
                ["size_after_bytes"] = afterSize,
                ["size_delta_bytes"] = beforeSize.HasValue && afterSize.HasValue ? (JToken)(afterSize.Value - beforeSize.Value) : JValue.CreateNull(),
                ["outcome"] = outcome,
                ["outcome_means"] = means,
                ["document"] = doc.Title,
                ["path"] = path,
                ["is_workshared"] = SafeWorkshared(doc),
                ["what_this_did"] = SafeWorkshared(doc) == true
                    ? "Saved the LOCAL file. This is NOT a synchronize with central: nothing was sent to the " +
                      "central model and no other user can see these changes yet."
                    : "Saved the file in place.",
                ["was_modified_before"] = modifiedBefore.HasValue ? (JToken)modifiedBefore.Value : JValue.CreateNull(),
                ["is_modified_after"] = modifiedAfter.HasValue ? (JToken)modifiedAfter.Value : JValue.CreateNull(),
                ["modified_flags_note"] =
                    "null means Document.IsModified could not be read, which is UNKNOWN and never false.",
                ["bytes_changed_on_disk"] = bytesChanged.HasValue ? (JToken)bytesChanged.Value : JValue.CreateNull(),
                ["evidence_strength"] = verdict.EvidenceStrength,
                ["modified_flag_evidence"] = verdict.FlagEvidence,
                ["cloud_cache"] = cacheVerdict == null ? null : CloudCacheJson(cacheVerdict, cacheBefore, cacheRoot, cacheAccount, projectGuid, modelGuid),
                ["verified_by"] = evidence,
                ["sha256_before"] = hashBefore,
                ["sha256_after"] = hashAfter,
                ["file_before"] = new JObject { ["modified_utc"] = beforeStamp, ["size_bytes"] = beforeSize },
                ["file_after"] = new JObject { ["modified_utc"] = afterStamp, ["size_bytes"] = afterSize },
                // The two facts a person asks after a save, read from disk: when, in this
                // machine's local time, and how big.
                ["saved_at_local"] = afterStamp.HasValue
                    ? afterStamp.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture)
                    : null,
                ["size_bytes"] = afterSize
            };
            if (gateDecision.Requested) payload["prevention"] = gateDecision.Prevention;

            // A save that cannot be shown to have written is not reported as one. The
            // caller gets the whole measurement either way - a refusal that throws the
            // evidence away is its own kind of unhelpful.
            if (outcome == "verification_failed")
                return CommandResult.Fail(means + " Full measurement: " +
                                          payload.ToString(Newtonsoft.Json.Formatting.None));

            return CommandResult.Ok(payload);
        }

        /// <summary>
        /// SHA-256 of the file, or null when it cannot be read. Null is UNKNOWN.
        ///
        /// Capped: a central model can run to gigabytes, and hashing one twice per save
        /// would cost more than the save. Over the cap this returns null and the caller
        /// falls back to timestamp and size, saying which it used.
        /// </summary>
        private const long MaxHashBytes = 512L * 1024 * 1024;

        private static JObject CloudCacheJson(CloudSaveVerdict v, List<CloudCacheCandidate> before, string root, string account,
                                              string projectGuid, string modelGuid)
        {
            Func<CloudCacheCandidate, JObject> row = c => new JObject
            {
                ["path"] = c.Path, ["account_folder"] = c.AccountFolder, ["current_account"] = c.CurrentAccount,
                ["written_utc"] = c.WrittenUtc.HasValue ? (JToken)c.WrittenUtc.Value.ToString("o") : JValue.CreateNull(),
                ["bytes"] = c.Bytes.HasValue ? (JToken)c.Bytes.Value : JValue.CreateNull()
            };
            var o = new JObject
            {
                ["root"] = root, ["account"] = account, ["project_guid"] = projectGuid, ["model_guid"] = modelGuid,
                ["judged_path"] = v.Path, ["changed"] = v.Changed.HasValue ? (JToken)v.Changed.Value : JValue.CreateNull(),
                ["account_mismatch"] = v.AccountMismatch, ["several_accounts"] = v.SeveralAccounts,
                ["before"] = new JArray((before ?? new List<CloudCacheCandidate>()).Select(row)),
                ["after"] = new JArray(v.After.Select(row)),
                ["measures"] = "LastWriteTimeUtc and length of the cached .rvt before and after Save(); the cloud copy itself is not read or written here."
            };
            if (v.Warning != null) o["warning"] = v.Warning;
            return o;
        }
        private static string HashOf(string path)
        {
            try
            {
                var fi = new System.IO.FileInfo(path);
                if (!fi.Exists || fi.Length > MaxHashBytes) return null;

                // FileShare.ReadWrite, not OpenRead(). Measured: OpenRead() throws on the
                // document Revit currently has open - which is EVERY document this command
                // is ever pointed at - so the hash was always null and every save fell back
                // to timestamp and size. The strong evidence was unreachable in exactly the
                // case it exists for. Sharing write access lets the read succeed while
                // Revit holds the file.
                using (var sha = System.Security.Cryptography.SHA256.Create())
                using (var fs = new System.IO.FileStream(path, System.IO.FileMode.Open,
                                                         System.IO.FileAccess.Read,
                                                         System.IO.FileShare.ReadWrite | System.IO.FileShare.Delete))
                    return BitConverter.ToString(sha.ComputeHash(fs)).Replace("-", "").ToLowerInvariant();
            }
            catch { return null; }
        }

        private static bool? SafeWorkshared(Document d)
        {
            try { return d.IsWorkshared; } catch { return null; }
        }

        private static void ReadFileFacts(string path, out DateTime? stamp, out long? size)
        {
            stamp = null; size = null;
            try
            {
                var fi = new FileInfo(path);
                if (!fi.Exists) return;
                stamp = fi.LastWriteTimeUtc;
                size = fi.Length;
            }
            catch { /* unreadable: both stay null, which the caller reports as unknown */ }
        }
    }
}
