// -----------------------------------------------------------------------------
// Horizun MCP - original Horizun code.
//
// horizun_document_session operation=sync_with_central.
//
// The only write in this bridge that can be neither rehearsed nor rolled back:
// Document.SynchronizeWithCentral publishes the local's changes into the central
// and reloads everyone else's, and the relinquish hands ownership back. So:
//
//   * It is OFF unless the machine owner turned it on in Revit (the ribbon's
//     Advanced options), exactly like horizun_execute_python - and a Python script
//     that calls SynchronizeWithCentral is refused while it is off. The workshared
//     read-only policy wins over that grant. Decisions: SyncWithCentralRules.
//   * Its preview is an ESTIMATE and says so. Document.HasAllChangesFromCentral()
//     asks the central whether the local is up to date; ownership
//     (WorksharingUtils.GetCheckoutStatus over every collectable element) and a
//     SAMPLE of GetModelUpdatesStatus are the session's cached view.
//   * The confirmation token binds the request as its plan hash and the estimate as
//     its model fingerprint: a model that moved is refused naming what moved.
//   * Revit waits forever for a locked central by default; this call gives up at
//     once, because nobody is at the keyboard to read an answer that arrives later.
//   * After the sync, HasAllChangesFromCentral() is read at once (it describes the
//     central NOW: false is someone else's later sync - unmeasured, not a failure),
//     everything measured before is looked up again by UniqueId (an ElementId may
//     change across a sync), ownership is held against the relinquish choice, the
//     sample is re-read and a save must be witnessed. A sync that returned but did not
//     verify is reported as exactly that - it happened, and it did not verify. A
//     documented precondition throw is re-read too before it is called "nothing moved".
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

using Horizun.Revit.Core;
using BridgeSettings = Horizun.Revit.Core.Settings;

namespace Horizun.Revit.Commands
{
    public partial class DocumentSessionCommand
    {
        /// <summary>
        /// Ownership keyed so it survives the sync: ids order the sample and are shown to the
        /// caller, UniqueIds are what gets compared after SynchronizeWithCentral.
        /// </summary>
        internal sealed class SyncCensus
        {
            public int? OwnedWorksets;
            public readonly List<long> AllIds = new List<long>();
            public readonly Dictionary<long, string> UidById = new Dictionary<long, string>();
            public readonly Dictionary<string, long> IdByUid = new Dictionary<string, long>(StringComparer.Ordinal);
            public readonly List<long> OwnedIds = new List<long>();
            public readonly List<long> BorrowedIds = new List<long>();
            public int Unreadable;
            // M10 (strict sync-only): the user's owned worksets by id and name, and the elements held by someone else.
            public readonly List<int> OwnedWorksetIds = new List<int>();
            public readonly List<string> OwnedWorksetNames = new List<string>();
            public readonly List<long> OthersIds = new List<long>();

            public List<string> Uids(IEnumerable<long> ids) =>
                ids.Where(UidById.ContainsKey).Select(id => UidById[id]).ToList();
        }

        /// <summary>
        /// Revit's default when the central is locked is to wait and retry endlessly
        /// (TransactWithCentralOptions.SetLockCallback). Unattended, that would hold Revit's
        /// UI thread and this bridge's one-command queue with nobody left to read the
        /// outcome, so the bridge gives up at once and reports the central as locked.
        /// </summary>
        private sealed class GiveUpWhenCentralLocked : ICentralLockedCallback
        {
            public bool ShouldWaitForLockAvailability() => false;
        }

        // The preview's model fields by token, so a refused apply can name what moved.
        // Bounded: tokens are single-use and expire in minutes.
        private static readonly object SyncEstimatesLock = new object();
        private static readonly Dictionary<string, Dictionary<string, string>> SyncEstimates =
            new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        private static readonly Queue<string> SyncEstimateOrder = new Queue<string>();

        private static CommandResult SyncRefuse(string code, string message, bool writeStarted = false)
        {
            return CommandResult.FailWithDetail(message, new JObject
            {
                ["code"] = code, ["operation"] = "sync_with_central",
                ["write_started"] = writeStarted, ["changes_applied"] = false,
                ["transaction_status"] = "not_started"
            });
        }

        /// <summary>
        /// horizun_save_document operation=sync_with_central. The SAME implementation, not a
        /// copy: the caller (SaveDocumentCommand) has already refused an empty comment and a
        /// profile below full_write; everything else - owner grant, workshared policy,
        /// estimate, token, re-read - is decided here exactly as for document_session.
        /// </summary>
        internal static CommandResult SyncWithCentralFromSave(UIApplication app, JObject request, bool syncOnly = false, bool? authorised = null, bool? protectedShared = null, SyncOnlyGrantRules.SyncDecision decision = null)
            => SyncWithCentral(app, request, syncOnly, authorised, protectedShared, decision);

        /// <param name="syncOnly">
        /// M10: the strict sync-only path (owner's flag on, profile safe_write): SyncOnlyGrantRules. False
        /// is the behaviour before the option existed, byte for byte.
        /// </param>
        /// <param name="authorised">
        /// INT-6 m-1: the owner's authorisation as the CALLER already decided it (SyncOnlyGrantRules.Decide, from one read of the
        /// settings). Null = this call decides it (horizun_document_session, which has no sync-only path).
        /// </param>
        /// <param name="forceReadOnly">
        /// INT-7 m-k: force_read_only_on_workshared from the SAME read as <paramref name="authorised"/>. Null = read it here.
        /// </param>
        private static CommandResult SyncWithCentral(UIApplication app, JObject request, bool syncOnly = false, bool? authorised = null, bool? forceReadOnly = null, SyncOnlyGrantRules.SyncDecision decision = null)
        {
            // A sync is the least reversible call here, so an omitted dry_run is a preview.
            bool dryRun = request.Value<bool?>("dry_run") ?? true;
            if (!SyncWithCentralRules.TryParseRelinquish(request.Value<string>("relinquish"), out SyncRelinquish choice))
                return SyncRefuse("invalid_relinquish", "relinquish must be all, keep_borrowed, borrowed_only or none. Nothing ran.");
            string comment = request.Value<string>("comment") ?? "";
            // The options' Comment setter throws above this: refused here, so a preview never
            // issues a token for a request its apply could not run.
            if (comment.Length > SyncWithCentralRules.MaxCommentChars)
                return SyncRefuse("invalid_comment", "comment is " + comment.Length + " characters; Revit accepts at most " +
                    SyncWithCentralRules.MaxCommentChars + " (SynchronizeWithCentralOptions.Comment). Nothing ran.");
            bool compact = request.Value<bool?>("compact") ?? false;
            // INT-11 D.5: failed checks are accepted only by naming every element id of them (accept_findings_ids, sorted and distinct;
            // the token binds it). Read before anything is measured: a malformed list refuses with nothing done.
            if (!SyncLedgerRules.TryReadAcceptIds(request["accept_findings_ids"], out List<long> acceptIds, out string acceptIdsError))
                return SyncRefuse("invalid_accept_findings_ids", acceptIdsError);
            if (syncOnly)
            {
                SyncRefusal strictRefusal = SyncOnlyGrantRules.RequestRefusal(comment, choice,
                    request.Value<bool?>("accept_findings") ?? false, acceptFindingsIds: request.Property("accept_findings_ids") != null);
                if (strictRefusal != null) return SyncRefuse(strictRefusal.Code, strictRefusal.Message);
            }

            string pickError = PickDocument(app, request, out Document doc, requireExplicitTarget: true);
            if (pickError != null) return SyncRefuse("target_not_resolved", pickError);
            if (OpenRecoveryDocuments.IsMarked(doc)) return SyncRefuse("recovered_document", "Recovered documents cannot be synchronized.");
            string title = SafeTitle(doc) ?? SafePath(doc) ?? "the target document";

            bool? detached = null;
            try { detached = doc.IsDetached; } catch { }
            SyncRefusal shape = SyncWithCentralRules.DocumentRefusal(SafeWorkshared(doc), detached);
            if (shape != null) return SyncRefuse(shape.Code, "'" + title + "' " + shape.Message);

            // Unreadable settings fall CLOSED: protected, and not granted.
            bool protectedShared, ownerGranted;
            if (forceReadOnly.HasValue) protectedShared = forceReadOnly.Value;
            else { try { protectedShared = BridgeSettings.ForceReadOnlyOnWorkshared; } catch { protectedShared = true; } }
            if (authorised.HasValue) ownerGranted = authorised.Value;
            else { try { ownerGranted = BridgeSettings.SyncWithCentralOwnerEnabled || syncOnly; } catch { ownerGranted = syncOnly; } }
            string settingsPath;
            try { settingsPath = BridgeSettings.Path(); } catch { settingsPath = null; }
            SyncRefusal auth = SyncWithCentralRules.AuthorisationRefusal(ownerGranted, protectedShared, settingsPath);
            if (auth != null) return SyncRefuse(auth.Code, auth.Message);

            string centralPath = null;
            bool? hasCentral = null;
            try
            {
                ModelPath central = doc.GetWorksharingCentralModelPath();
                hasCentral = central != null;
                if (central != null) centralPath = ModelPathUtils.ConvertModelPathToUserVisiblePath(central);
            }
            catch { }
            bool? readOnlyNow = null, transactionOpen = null, readOnlyFile = null;
            try { readOnlyNow = doc.IsReadOnly; } catch { }
            try { transactionOpen = doc.IsModifiable; } catch { }
            try { readOnlyFile = doc.IsReadOnlyFile; } catch { }
            SyncRefusal pre = SyncWithCentralRules.PreconditionRefusal(readOnlyNow, transactionOpen, hasCentral, readOnlyFile);
            if (pre != null) return SyncRefuse(pre.Code, "'" + title + "' " + pre.Message);

            // WORK LOCALLY, SYNCHRONIZE AT THE END: a sync with writes of this session whose
            // verification failed or could not be measured is refused, dry run included, until
            // they are fixed and re-checked (horizun_verify_changes) or the person accepts them:
            // accept_findings=true accepts ONLY unverified scripts (kind unverified_python); a failed
            // check (kind failed_check) only by accept_findings_ids naming every element id of it
            // (INT-11 D.5). The strict path accepts nothing. See Core/SyncLedgerRules.cs.
            string ledgerKey = ChangeWatch.Key(doc);
            List<SyncLedgerEntry> blockers = SyncLedgerRules.Blockers(ledgerKey);
            if (blockers.Any(entry => entry.TransactionPending))
                return SyncRefuse(SyncLedgerRules.UnverifiedCode,
                    "Revit has not finished processing a transaction in '" + title +
                    "'. Wait for Revit to resolve it before retrying. Nothing was synchronized.");
            bool acceptFindings = request.Value<bool?>("accept_findings") ?? false;
            SyncLedgerRules.FindingsAcceptance acceptance = SyncLedgerRules.Accept(blockers, acceptFindings, acceptIds, syncOnly);
            if (acceptance.UnusedIds.Count > 0)
                return SyncRefuse("invalid_accept_findings_ids", "accept_findings_ids contains ids that do not belong to a pending failed check: " +
                    string.Join(", ", acceptance.UnusedIds) + ". Nothing was synchronized.");
            if (!acceptance.Proceed)
            {
                // INT-9 M-1/M-2: the text AND the structured block come from the decision of horizun_save_document (the tool they
                // name), whichever tool made this call. Unreadable settings: nothing is offered.
                SyncOnlyGrantRules.SyncDecision targetDecision = decision;
                if (targetDecision == null)
                {
                    try { targetDecision = SyncOnlyGrantRules.Decide(BridgeSettings.ReadSyncSettings()); } catch { targetDecision = null; }
                }
                SyncOnlyGrantRules.BlockedWayOut wayOut = SyncOnlyGrantRules.WayOutOfBlocked(targetDecision, syncOnly);
                var detail = new JObject
                {
                    ["code"] = acceptance.Code, ["operation"] = "sync_with_central",
                    ["write_started"] = false, ["changes_applied"] = false, ["transaction_status"] = "not_started",
                    ["session_changes"] = SyncLedgerRules.SessionChanges(ledgerKey),
                    ["blockers_unverified_python"] = acceptance.PythonRows,
                    ["blockers_failed_checks"] = acceptance.CheckRows,
                    ["blocker_kinds"] = new JArray(acceptance.Remaining.Select(e => e.BlockKind).Distinct())
                };
                if (acceptance.CheckRows > 0)
                {
                    detail["missing_accept_findings_ids"] = new JArray(acceptance.MissingIds.Take(SyncLedgerRules.IdsListed));
                    detail["missing_accept_findings_ids_count"] = acceptance.MissingIds.Count;
                }
                if (wayOut.AcceptFindingsOffered)
                    detail["accept_findings_with"] = SyncLedgerRules.AcceptFindingsWith(acceptance);
                string blockedText = acceptance.ChecksRemaining > 0 && (acceptance.AcceptFindings || acceptance.IdsGiven)
                    ? SyncLedgerRules.FailedChecksMessage(title, acceptance, wayOut)
                    : SyncLedgerRules.BlockedMessage(title, blockers, wayOut);
                return CommandResult.FailWithDetail(blockedText, detail);
            }

            var clock = Stopwatch.StartNew();
            SyncCensus before = TakeSyncCensus(doc);
            if (before.OwnedWorksets == null || before.Unreadable > 0)
                return SyncRefuse("ownership_census_incomplete",
                    "The ownership of '" + title + "' could not be read in full (worksets readable=" +
                    (before.OwnedWorksets != null) + ", elements without a checkout status or UniqueId=" + before.Unreadable +
                    "). A sync whose relinquish cannot be checked afterwards would be reported unverified, so it " +
                    "is not offered. Nothing ran.");

            List<long> sampleIds = SyncWithCentralRules.Sample(before.AllIds, before.BorrowedIds, before.OwnedIds,
                SyncWithCentralRules.SpreadSampleSize, SyncWithCentralRules.OwnedSampleSize);
            List<string> sample = before.Uids(sampleIds);
            Dictionary<string, string> statusBefore = ReadUpdateStatuses(doc, sample);
            bool? modified = SafeModified(doc);
            bool? hasAll = HasAllChanges(doc, out string hasAllError);
            string documentKey = DocumentGate.IdentityOf(doc, HostVersion(app))?.Fingerprint();
            var sampleCounts = JObject.FromObject(SyncWithCentralRules.Counts(statusBefore.Values));

            var estimate = new JObject
            {
                ["document"] = documentKey,
                ["comment"] = comment,
                ["relinquish"] = SyncWithCentralRules.Name(choice),
                ["compact"] = compact,
                ["owned_worksets"] = before.OwnedWorksets,
                ["owned_elements"] = before.OwnedIds.Count,
                ["borrowed_elements"] = before.BorrowedIds.Count,
                ["is_modified"] = modified,
                ["has_all_changes_from_central"] = hasAll,
                ["sample_status_counts"] = sampleCounts
            };
            // INT-11 D.5: the token is bound to the ids the person accepted (SyncWithCentralRules.RequestFields).
            estimate["accept_findings_ids"] = new JArray(acceptIds);
            if (syncOnly)
            {
                // The token also binds WHAT the user holds: a different borrowed list, owned set or
                // owned-workset set between the preview and the apply is refused as a moved model.
                estimate["borrowed_digest"] = SyncOnlyGrantRules.Digest(before.Uids(before.BorrowedIds));
                estimate["owned_digest"] = SyncOnlyGrantRules.Digest(before.Uids(before.OwnedIds));
                estimate["owned_workset_ids"] = SyncOnlyGrantRules.Digest(before.OwnedWorksetIds.Select(i => i.ToString()));
            }
            string[] modelFields = SyncOnlyGrantRules.ModelFields(syncOnly);
            string planHash = ConfirmationStore.PlanHash(estimate, SyncWithCentralRules.RequestFields);
            string modelFingerprint = ConfirmationStore.PlanHash(estimate, modelFields);
            Dictionary<string, string> modelView = ModelView(estimate, modelFields);

            if (dryRun)
            {
                Confirmation issued = DocumentGate.Confirmations.Issue(
                    SyncWithCentralRules.ConfirmationScope, documentKey, planHash, elementFingerprint: modelFingerprint);
                RememberEstimate(issued.Token, modelView);
                var preview = new JObject
                {
                    ["operation"] = "sync_with_central",
                    ["dry_run"] = true,
                    ["preview_kind"] = "estimate",
                    ["target_document"] = title,
                    ["central"] = centralPath,
                    ["relinquish"] = SyncWithCentralRules.Name(choice),
                    ["comment"] = comment,
                    ["compact"] = compact,
                    ["is_modified"] = modified,
                    ["has_all_changes_from_central"] = hasAll,
                    ["has_all_changes_error"] = hasAllError,
                    ["owned_worksets"] = before.OwnedWorksets,
                    ["owned_elements"] = before.OwnedIds.Count,
                    ["borrowed_elements"] = before.BorrowedIds.Count,
                    ["borrowed_sample_ids"] = new JArray(before.BorrowedIds.OrderByDescending(x => x).Take(20)),
                    ["session_changes"] = SyncLedgerRules.SessionChanges(ledgerKey),
                    ["precheck"] = SyncWithCentralRules.Precheck(comment, SyncLedgerRules.Writes(ledgerKey).Count,
                        blockers.Count, acceptance.Proceed, before.OwnedIds.Count, before.BorrowedIds.Count, choice),
                    ["accepted_findings"] = acceptance.AcceptedPython + acceptance.AcceptedChecks,
                    ["accepted_findings_by_kind"] = new JObject
                    {
                        ["unverified_python"] = acceptance.AcceptedPython,
                        ["failed_check"] = acceptance.AcceptedChecks
                    },
                    ["accept_findings_ids"] = new JArray(acceptIds),
                    ["accept_findings_ids_unused"] = new JArray(acceptance.UnusedIds.Take(SyncLedgerRules.IdsListed)),
                    ["elements_scanned"] = before.AllIds.Count,
                    ["update_status_sample"] = new JObject
                    {
                        ["sample_size"] = sample.Count,
                        ["counts"] = sampleCounts,
                        ["method"] = "WorksharingUtils.GetModelUpdatesStatus per element (a local cache that cannot see " +
                                     "what others pushed), on " + sample.Count + " of " + before.AllIds.Count +
                                     " elements: an even spread plus the borrowed, then the newest owned."
                    },
                    ["estimate_note"] =
                        "ESTIMATE, not a rehearsal. A sync cannot be rehearsed or rolled back. " +
                        "has_all_changes_from_central is Document.HasAllChangesFromCentral(), which asks the central " +
                        "whether this local is up to date (null + has_all_changes_error when it could not). Ownership " +
                        "counts and the sampled update statuses are this session's CACHED view (WorksharingUtils " +
                        "reads a local cache), not the central's.",
                    ["cannot_be_rolled_back"] = true,
                    ["confirmation_token"] = issued.Token,
                    ["confirmation_expires_utc"] = issued.ExpiresUtc.ToString("u"),
                    ["confirmation_note"] =
                        "Bound to THIS request (document, relinquish, comment, compact, accept_findings_ids) and THIS estimate (ownership " +
                        "counts, IsModified, has_all_changes_from_central, sampled statuses). If the estimate moves " +
                        "before the apply, it is refused naming what moved, and nothing is synchronized.",
                    ["elapsed_ms"] = clock.ElapsedMilliseconds
                };
                // Present only on the strict path: with the flag off the reply is the one that shipped.
                if (syncOnly)
                    preview["sync_only"] = SyncOnlyGrantRules.PreviewBlock(choice, before.BorrowedIds,
                        before.Uids(before.BorrowedIds), before.OwnedIds, before.OwnedWorksetNames, before.OwnedWorksetIds,
                        before.OthersIds.Count);
                return CommandResult.Ok(preview);
            }

            string token = request.Value<string>("confirmation_token");
            ConfirmationCheck check = DocumentGate.Confirmations.Validate(
                token, SyncWithCentralRules.ConfirmationScope, documentKey, planHash, modelFingerprint,
                SyncWithCentralRules.DescribeEstimateDrift(RecallEstimate(token), modelView, modelFields));
            if (!check.Ok)
                return SyncRefuse("confirmation_rejected",
                    "REFUSING TO SYNCHRONIZE '" + title + "': " + check.Message + " Run operation=sync_with_central " +
                    "with dry_run=true again and confirm the new estimate. Nothing was synchronized.");

            SynchronizeWithCentralOptions options;
            TransactWithCentralOptions transact;
            try
            {
                options = new SynchronizeWithCentralOptions
                {
                    Comment = comment,
                    Compact = compact,
                    SaveLocalBefore = true,
                    SaveLocalAfter = true
                };
                options.SetRelinquishOptions(BuildRelinquish(choice));
                transact = new TransactWithCentralOptions();
                transact.SetLockCallback(new GiveUpWhenCentralLocked());
            }
            catch (Exception ex)
            {
                // Building the options touches no document: a throw here is refused before the call.
                return SyncRefuse("sync_options_rejected",
                    "Revit rejected the synchronize options for '" + title + "' (" + ex.GetType().Name + ": " + ex.Message +
                    "). SynchronizeWithCentral was not called; nothing was synchronized. Preview again.");
            }

            DateTime? centralWriteBefore = CentralWriteTime(centralPath);
            string syncError = null;
            SyncFailureKind failure = SyncFailureKind.Unknown;
            DateTime syncStarted = DateTime.UtcNow;
            SyncLedgerRules.BeginCommandSync(ledgerKey);
            try { doc.SynchronizeWithCentral(transact, options); }
            catch (Exception ex)
            {
                syncError = ex.GetType().Name + ": " + ex.Message;
                failure = SyncWithCentralRules.ClassifyFailure(ex.GetType().FullName);
            }
            finally { SyncLedgerRules.EndCommandSync(ledgerKey); }
            // Read FIRST: it asks the central about itself NOW, and every second the census below
            // takes is a second in which another user's sync can make it false.
            bool? hasAllAfter = HasAllChanges(doc, out string hasAllAfterError);
            SyncCensus after = TakeSyncCensus(doc);
            bool? modifiedAfter = SafeModified(doc);
            DateTime? centralWriteAfter = CentralWriteTime(centralPath);
            bool? centralAdvanced = centralWriteBefore == null || centralWriteAfter == null
                ? (bool?)null : centralWriteAfter.Value > centralWriteBefore.Value;

            if (syncError != null && failure == SyncFailureKind.NotStarted)
            {
                // A documented precondition - but not every one is placed before the central
                // write, so "nothing moved" is said only when the re-read proves it.
                bool? started = SyncWithCentralRules.WriteStartedAfterPreconditionThrow(
                    SyncWithCentralRules.OwnershipUnchanged(before.OwnedWorksets, before.Uids(before.OwnedIds),
                        after.OwnedWorksets, after.Uids(after.OwnedIds), after.Unreadable),
                    modified, modifiedAfter, centralAdvanced);
                return CommandResult.FailWithDetail(started == false
                    ? "SynchronizeWithCentral refused '" + title + "' with one of its documented preconditions (" + syncError +
                      "), and the re-read confirms nothing moved: ownership, IsModified and the central file's write time " +
                      "are unchanged. Nothing was synchronized."
                    : "SynchronizeWithCentral threw one of its documented precondition exceptions for '" + title + "' (" +
                      syncError + "), but the API does not place every one before the central write, and the re-read " +
                      (started == true ? "shows something MOVED" : "cannot prove that nothing moved") + " (the detail " +
                      "lists what was measured). Do not retry blindly: preview again first.",
                    new JObject
                    {
                        ["code"] = "sync_precondition_failed", ["operation"] = "sync_with_central",
                        ["api_error"] = syncError,
                        ["write_started"] = started,
                        ["changes_applied"] = started == false ? (bool?)false : null,
                        ["transaction_status"] = started == false ? "not_started" : "unknown",
                        ["owned_worksets_before"] = before.OwnedWorksets, ["owned_worksets_after"] = after.OwnedWorksets,
                        ["owned_elements_before"] = before.OwnedIds.Count, ["owned_elements_after"] = after.OwnedIds.Count,
                        ["unreadable_after"] = after.Unreadable,
                        ["is_modified_before"] = modified, ["is_modified_after"] = modifiedAfter,
                        ["central_file_written"] = centralAdvanced,
                        ["has_all_changes_from_central_after"] = hasAllAfter
                    });
            }

            SyncVerdict ownership = SyncWithCentralRules.VerifyOwnership(choice,
                before.OwnedWorksets, before.Uids(before.OwnedIds), before.Uids(before.BorrowedIds),
                after.OwnedWorksets, after.Uids(after.OwnedIds), after.Unreadable, uid => ElementExists(doc, uid),
                knownBefore: before.UidById.Values, borrowedAfter: after.Uids(after.BorrowedIds));
            Dictionary<string, string> statusAfter = ReadUpdateStatuses(doc, sample);
            SyncVerdict updates = SyncWithCentralRules.VerifyUpdates(statusAfter);
            var overallProblems = new List<string>();
            bool? verified = SyncWithCentralRules.OverallVerdict(syncError != null, ownership.Verified, updates.Verified,
                hasAllAfter, modifiedAfter, centralAdvanced, overallProblems);
            SyncVerdict strictPost = null;
            if (syncOnly && syncError == null)
            {
                strictPost = SyncOnlyGrantRules.PostCheck(choice, after.OwnedIds.Count, after.BorrowedIds.Count, after.OwnedWorksets,
                    modifiedAfter, hasAllAfter, before.Uids(before.OwnedIds), after.Uids(after.OwnedIds), before.Uids(before.OthersIds),
                    after.Uids(after.OthersIds), uid => after.IdByUid.ContainsKey(uid));
                overallProblems.AddRange(strictPost.Problems);
                // A leftover or a pending change is a failure; an unmeasured read cannot stay "verified".
                if (strictPost.Verified == false) verified = false;
                else if (strictPost.Verified == null && verified == true) verified = null;
            }
            // A returned API call alone does not establish that the local work reached central.
            // Retain the ledger whenever the independent post-check is failed or unmeasured.
            if (verified == true) SyncLedgerRules.MarkSyncedThrough(ledgerKey, syncStarted, DateTime.UtcNow);

            var report = new JObject
            {
                ["operation"] = "sync_with_central",
                ["dry_run"] = false,
                ["target_document"] = title,
                ["central"] = centralPath,
                ["relinquish"] = SyncWithCentralRules.Name(choice),
                ["api_error"] = syncError,
                ["sync_verified"] = verified,
                ["ownership"] = new JObject
                {
                    ["verified"] = ownership.Verified,
                    ["owned_worksets_before"] = before.OwnedWorksets,
                    ["owned_worksets_after"] = after.OwnedWorksets,
                    ["owned_elements_before"] = before.OwnedIds.Count,
                    ["borrowed_elements_before"] = before.BorrowedIds.Count,
                    ["owned_elements_after"] = after.OwnedIds.Count,
                    ["unreadable_after"] = after.Unreadable,
                    ["unexpectedly_owned"] = Display(ownership.UnexpectedlyOwned, after, before),
                    ["unexpectedly_released"] = Display(ownership.UnexpectedlyReleased, after, before),
                    ["arrived_in_owned_worksets"] = Display(ownership.ArrivedOwned, after, before),
                    ["problems"] = new JArray(ownership.Problems)
                },
                ["update_status_sample"] = new JObject
                {
                    ["verified"] = updates.Verified,
                    ["sample_size"] = sample.Count,
                    ["counts_before"] = sampleCounts,
                    ["counts_after"] = JObject.FromObject(SyncWithCentralRules.Counts(statusAfter.Values)),
                    ["not_current"] = Display(updates.UnexpectedlyOwned, after, before),
                    ["moved_in_central_since"] = Display(updates.MovedInCentral, after, before),
                    ["problems"] = new JArray(updates.Problems)
                },
                ["has_all_changes_from_central_after"] = hasAllAfter,
                ["has_all_changes_error_after"] = hasAllAfterError,
                ["is_modified_after"] = modifiedAfter,
                ["central_file_written"] = centralAdvanced,
                ["problems"] = new JArray(overallProblems),
                ["measured_how"] =
                    "Elements tracked by UniqueId (an ElementId may change across a sync; element_id values are for " +
                    "display). Worksets counted by Owner; every collectable element checked with " +
                    "WorksharingUtils.GetCheckoutStatus before and after; the same sample re-read with " +
                    "GetModelUpdatesStatus; Document.HasAllChangesFromCentral() read first after the call; the save witnessed by " +
                    "IsModified=false after SaveLocalAfter or, for a file-based central, its write time advancing.",
                ["elapsed_ms"] = clock.ElapsedMilliseconds
            };

            if (strictPost != null)
                report["sync_only_postcheck"] = new JObject
                {
                    ["verified"] = strictPost.Verified,
                    ["borrowed_elements_left"] = after.BorrowedIds.Count,
                    ["owned_elements_left"] = after.OwnedIds.Count,
                    ["owned_worksets_left"] = after.OwnedWorksets,
                    ["pending_local_changes"] = modifiedAfter,
                    ["others_no_longer_hold"] = Display(strictPost.UnexpectedlyReleased, after, before),
                    ["problems"] = new JArray(strictPost.Problems)
                };

            if (syncError != null)
            {
                bool locked = failure == SyncFailureKind.CentralLocked;
                report["code"] = locked ? "central_locked" : "sync_failed_state_unknown";
                report["write_started"] = true;
                report["changes_applied"] = JValue.CreateNull();
                return CommandResult.FailWithDetail(locked
                    ? "The central of '" + title + "' is locked by another client (" + syncError + "). This call " +
                      "does not wait for a lock, so Revit cancelled the synchronize. The local may already have been " +
                      "saved or reloaded before the lock was needed; the re-read is in the detail. Preview again once " +
                      "the central is free."
                    : "SynchronizeWithCentral threw (" + syncError + "). Whether anything reached the central is not " +
                      "known from the exception, and the local may have been saved or reloaded; the ownership and " +
                      "status re-read after it is in the detail. Do not retry blindly: preview again first.", report);
            }
            if (verified != true)
            {
                report["write_started"] = true;
                report["changes_applied"] = true;
                return CommandResult.FailWithDetail(
                    "SynchronizeWithCentral RETURNED (Revit reports it synchronized), but the postcondition " +
                    (verified == false ? "did NOT hold" : "could not be measured") + ": " +
                    string.Join("; ", ownership.Problems.Concat(updates.Problems).Concat(overallProblems)) +
                    ". The sync cannot be undone; the detail names what differs.", report);
            }
            return CommandResult.Ok(report);
        }

        private static RelinquishOptions BuildRelinquish(SyncRelinquish choice)
        {
            if (choice == SyncRelinquish.All) return new RelinquishOptions(true);
            // keep_borrowed gives the worksets back and keeps the borrowed elements;
            // borrowed_only is the mirror: the borrowed elements go back and the worksets stay.
            bool worksets = choice == SyncRelinquish.KeepBorrowed;
            return new RelinquishOptions(false)
            {
                StandardWorksets = worksets,
                ViewWorksets = worksets,
                FamilyWorksets = worksets,
                UserWorksets = worksets,
                CheckedOutElements = choice == SyncRelinquish.BorrowedOnly
            };
        }

        /// <summary>
        /// Worksets owned by this user, every collectable element's checkout status, and
        /// which owned elements are BORROWED (owned while their workset is not). Elements
        /// inside an owned workset read as owned too (a non-borrowed element's owner is its
        /// workset's owner): those land in owned, never in borrowed. An element whose
        /// UniqueId cannot be read cannot be followed across the sync, so it is unreadable.
        /// </summary>
        internal static SyncCensus TakeSyncCensus(Document doc)
        {
            var c = new SyncCensus();
            string user = null;
            try { user = doc.Application.Username; } catch { }
            var ownedWorksets = new HashSet<int>();
            try
            {
                foreach (Workset w in new FilteredWorksetCollector(doc))
                    if (w != null && user != null && string.Equals(w.Owner, user, StringComparison.OrdinalIgnoreCase))
                    {
                        ownedWorksets.Add(w.Id.IntegerValue);
                        c.OwnedWorksetIds.Add(w.Id.IntegerValue);
                        string wsName = null;
                        try { wsName = w.Name; } catch { }
                        c.OwnedWorksetNames.Add(wsName ?? "(unnamed)");
                    }
                c.OwnedWorksets = user == null ? (int?)null : ownedWorksets.Count;
            }
            catch { c.OwnedWorksets = null; }

            foreach (bool types in new[] { false, true })
            {
                FilteredElementCollector collector;
                try
                {
                    collector = new FilteredElementCollector(doc);
                    collector = types ? collector.WhereElementIsElementType() : collector.WhereElementIsNotElementType();
                }
                catch { c.Unreadable++; continue; }
                foreach (Element e in collector)
                {
                    if (e == null) continue;
                    long id = Rid.Value(e.Id);
                    if (c.UidById.ContainsKey(id)) continue;
                    string uid = null;
                    try { uid = e.UniqueId; } catch { }
                    if (string.IsNullOrEmpty(uid)) { c.Unreadable++; continue; }
                    c.UidById[id] = uid;
                    c.IdByUid[uid] = id;
                    c.AllIds.Add(id);
                    try
                    {
                        CheckoutStatus checkout = WorksharingUtils.GetCheckoutStatus(doc, e.Id);
                        if (checkout == CheckoutStatus.OwnedByOtherUser) c.OthersIds.Add(id);
                        if (checkout != CheckoutStatus.OwnedByCurrentUser) continue;
                        c.OwnedIds.Add(id);
                        int ws = -1;
                        try { ws = e.WorksetId.IntegerValue; } catch { }
                        if (!ownedWorksets.Contains(ws)) c.BorrowedIds.Add(id);
                    }
                    catch { c.Unreadable++; }
                }
            }
            c.AllIds.Sort();
            return c;
        }

        /// <summary>By UniqueId. null = the element no longer exists; "unreadable" = the read threw.</summary>
        private static Dictionary<string, string> ReadUpdateStatuses(Document doc, List<string> sample)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string uid in sample)
            {
                try
                {
                    Element e = doc.GetElement(uid);
                    result[uid] = e == null ? null : WorksharingUtils.GetModelUpdatesStatus(doc, e.Id).ToString();
                }
                catch { result[uid] = SyncWithCentralRules.Unreadable; }
            }
            return result;
        }

        /// <summary>A lookup that throws counts as present: an unreadable element is never dropped from "expected".</summary>
        private static bool ElementExists(Document doc, string uid)
        {
            try { return doc.GetElement(uid) != null; }
            catch { return true; }
        }

        private static bool? HasAllChanges(Document doc, out string error)
        {
            error = null;
            try { return doc.HasAllChangesFromCentral(); }
            catch (Exception ex) { error = ex.GetType().Name + ": " + ex.Message; return null; }
        }

        /// <summary>A file-based central's last write time; null for a server or cloud central, or when unreadable.</summary>
        private static DateTime? CentralWriteTime(string centralPath)
        {
            try
            {
                return !string.IsNullOrEmpty(centralPath) && System.IO.File.Exists(centralPath)
                    ? System.IO.File.GetLastWriteTimeUtc(centralPath) : (DateTime?)null;
            }
            catch { return null; }
        }

        private static JArray Display(IEnumerable<string> uids, SyncCensus after, SyncCensus before)
        {
            var list = new JArray();
            foreach (string uid in uids.Take(50))
            {
                long id;
                JToken shown = after.IdByUid.TryGetValue(uid, out id) || before.IdByUid.TryGetValue(uid, out id)
                    ? (JToken)id : JValue.CreateNull();
                list.Add(new JObject { ["unique_id"] = uid, ["element_id"] = shown });
            }
            return list;
        }

        private static Dictionary<string, string> ModelView(JObject estimate, IEnumerable<string> fields = null)
        {
            var view = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string field in fields ?? SyncWithCentralRules.ModelFields)
                view[field] = estimate[field]?.ToString(Newtonsoft.Json.Formatting.None);
            return view;
        }

        private static void RememberEstimate(string token, Dictionary<string, string> view)
        {
            if (string.IsNullOrEmpty(token)) return;
            lock (SyncEstimatesLock)
            {
                SyncEstimates[token] = view;
                SyncEstimateOrder.Enqueue(token);
                while (SyncEstimateOrder.Count > 32) SyncEstimates.Remove(SyncEstimateOrder.Dequeue());
            }
        }

        private static Dictionary<string, string> RecallEstimate(string token)
        {
            if (string.IsNullOrEmpty(token)) return null;
            lock (SyncEstimatesLock)
                return SyncEstimates.TryGetValue(token, out Dictionary<string, string> view) ? view : null;
        }
    }
}

