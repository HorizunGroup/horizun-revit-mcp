// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// WORKSHARED MODELS: WORK LOCALLY, SYNCHRONIZE ONCE, AT THE END (owner rule, 2026-10-27).
//
// While a task runs on a workshared model the bridge only ever saves the LOCAL file;
// synchronizing with central is a separate, explicit, rehearsed and confirmed action at
// the end, when everything is verified and the person says so. This file is the
// Revit-free memory that rule needs, per document, for this Revit session:
//
//  - every committed write since the last verified sync (tool, time, ids, its spatial
//    check and, for a script, its evidence status);
//  - which of them still BLOCK a sync: a spatial check with conflicts, a check that
//    could not be measured, or a script whose result is not self_reported_verified;
//  - what clears a blocker: a later horizun_verify_changes check that covers its ids and
//    finds no conflicts, or the person accepting the findings in the sync itself;
//  - the reminder every write and health reply carries while local work is unsynced.
//
// What it cannot know is stated, never guessed: Revit does not expose the list of
// element changes waiting to go to central, only per-element update status (sampled by
// the sync estimate) - so the session's own writes are what is listed.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public sealed class SyncLedgerEntry
    {
        public string Tool;
        public DateTime AtUtc;
        public long[] Ids = new long[0];
        public string SpatialStatus;   // clean | warnings | conflicts | partial | not_measured | ... | null (none attached)
        public int SpatialErrors;
        public bool SpatialPartial;
        public bool VerificationFailed;
        public bool TransactionPending;
        public string EvidenceStatus;  // execute_python only
        public bool Cleared;           // a later clean verify_changes covered it
        public string BlockReason => Cleared ? null
            : TransactionPending ? "transaction_pending"
            : SpatialErrors > 0 || SpatialStatus == "conflicts" ? "spatial_check_conflicts"
            : VerificationFailed ? "postcommit_verification_failed"
            : SpatialPartial || SpatialStatus == "partial" || SpatialStatus == "not_measured" ? "spatial_check_incomplete"
            : EvidenceStatus != null && EvidenceStatus != "self_reported_verified" ? "script_not_verified:" + EvidenceStatus
            : null;

        /// <summary>
        /// INT-10: what kind of blocker this is. unverified_python = a script whose result is only self-reported (the person can
        /// accept it with accept_findings); failed_check = a spatial or post-commit check that failed or could not be measured.
        /// null when the entry does not block.
        /// </summary>
        public string BlockKind => BlockReason == null ? null
            : BlockReason.StartsWith("script_not_verified", StringComparison.Ordinal) ? "unverified_python"
            : "failed_check";
    }

    public static class SyncLedgerRules
    {
        public const int HistoryCap = 500;
        private static readonly object Gate = new object();
        private static readonly Dictionary<string, List<SyncLedgerEntry>> Pending = new Dictionary<string, List<SyncLedgerEntry>>(StringComparer.Ordinal);
        private static readonly Dictionary<string, DateTime> LastSync = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        private static readonly Dictionary<string, string> LastSyncSource = new Dictionary<string, string>(StringComparer.Ordinal);
        private static readonly Dictionary<string, DateTime> SyncStarts = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        private static readonly HashSet<string> CommandSyncs = new HashSet<string>(StringComparer.Ordinal);

        public static void BeginCommandSync(string key) { lock (Gate) CommandSyncs.Add(key); }
        public static void EndCommandSync(string key) { lock (Gate) CommandSyncs.Remove(key); }

        public static void RecordWrite(string key, SyncLedgerEntry e)
        {
            if (string.IsNullOrEmpty(key) || e == null) return;
            lock (Gate)
            {
                if (!Pending.TryGetValue(key, out List<SyncLedgerEntry> list)) Pending[key] = list = new List<SyncLedgerEntry>();
                list.Add(e);
                while (list.Count(e => e.BlockReason == null) > HistoryCap)
                    list.RemoveAt(list.FindIndex(e => e.BlockReason == null));
            }
        }

        public static bool ShouldRecordResult(bool success, JObject data)
            => data != null && (success || data.Value<bool?>("changes_applied") == true);

        internal static void ResolvePendingTransaction(string key, SyncLedgerEntry entry, bool committed, long[] ids)
        {
            lock (Gate)
            {
                if (!Pending.TryGetValue(key, out List<SyncLedgerEntry> list) || !list.Contains(entry)) return;
                if (committed)
                {
                    if (entry.TransactionPending) entry.AtUtc = DateTime.UtcNow;
                    entry.Ids = ids;
                    entry.VerificationFailed = true;
                    entry.TransactionPending = false;
                }
                else
                {
                    list.Remove(entry);
                    if (list.Count == 0) Pending.Remove(key);
                }
            }
        }

        public static SyncLedgerEntry FromResult(string tool, bool success, JObject data, long[] ids)
        {
            JObject spatial = data?["spatial_check"] as JObject;
            return new SyncLedgerEntry {
                Tool = tool, AtUtc = DateTime.UtcNow, Ids = ids ?? new long[0],
                SpatialStatus = (string)spatial?["status"], SpatialErrors = (int?)spatial?["errors"] ?? 0,
                SpatialPartial = (bool?)spatial?["partial"] == true,
                VerificationFailed = !success,
                EvidenceStatus = tool == "horizun_execute_python" ? ((string)data?["evidence_status"] ?? "completed_unverified") : null
            };
        }

        public static bool IsCleanSpatialCheck(JObject sc)
        {
            if (sc == null || sc.Value<bool?>("partial") == true || (int?)sc["errors"] > 0) return false;
            string status = (string)sc["status"];
            return status == "clean" || status == "warnings" || status == "nothing_to_check";
        }

        /// <summary>
        /// A verify_changes check with no conflicts clears the blockers it covered: every entry
        /// whose ids are all inside checkedIds, or - with sessionScope - every entry up to now.
        /// Returns how many were cleared.
        /// </summary>
        public static int ClearVerified(string key, ICollection<long> checkedIds, bool sessionScope)
        {
            int n = 0;
            lock (Gate)
            {
                if (!Pending.TryGetValue(key, out List<SyncLedgerEntry> list)) return 0;
                var set = checkedIds == null ? new HashSet<long>() : new HashSet<long>(checkedIds);
                foreach (SyncLedgerEntry e in list)
                {
                    // A spatial check cannot prove a command-specific post-commit
                    // verification (for example the geometry of a new stair).
                    if (e.Cleared || e.BlockReason == null || e.VerificationFailed || e.TransactionPending) continue;
                    if (sessionScope || (e.Ids.Length > 0 && e.Ids.All(set.Contains))) { e.Cleared = true; n++; }
                }
            }
            return n;
        }

        public static void MarkSynced(string key, DateTime atUtc)
        {
            lock (Gate) { Pending.Remove(key); LastSync[key] = atUtc; LastSyncSource[key] = "horizun"; }
        }

        public static void MarkSyncedThrough(string key, DateTime cutoffUtc, DateTime completedUtc)
        {
            lock (Gate)
            {
                ObserveSynchronization(key, true, cutoffUtc);
                LastSync[key] = completedUtc;
                LastSyncSource[key] = "horizun";
            }
        }

        /// <summary>Reconcile only a successful Revit post-synchronization event.</summary>
        public static void ObserveSynchronization(string key, bool succeeded, DateTime atUtc)
        {
            if (!succeeded || string.IsNullOrEmpty(key)) return;
            lock (Gate)
            {
                if (Pending.TryGetValue(key, out List<SyncLedgerEntry> list))
                {
                    list.RemoveAll(entry => !entry.TransactionPending && entry.AtUtc <= atUtc);
                    if (list.Count == 0) Pending.Remove(key);
                }
                LastSync[key] = atUtc;
                LastSyncSource[key] = "revit_event";
            }
        }

        /// <summary>horizun (this bridge synchronized) | revit_event (observed from Revit, e.g. by hand) | null (none seen).</summary>
        public static string LastSyncBy(string key)
        {
            lock (Gate) return LastSyncSource.TryGetValue(key, out string s) ? s : null;
        }

        public static void BeginSynchronization(string key, DateTime atUtc)
        {
            if (string.IsNullOrEmpty(key)) return;
            lock (Gate) SyncStarts[key] = atUtc;
        }

        public static void EndSynchronization(string key, bool succeeded, DateTime completedUtc)
        {
            if (string.IsNullOrEmpty(key)) return;
            lock (Gate)
            {
                if (!SyncStarts.TryGetValue(key, out DateTime startedUtc)) return;
                SyncStarts.Remove(key);
                if (!succeeded) return;
                ObserveSynchronization(key, true, startedUtc);
                LastSync[key] = completedUtc;
                LastSyncSource[key] = "revit_event";
            }
        }

        public static void EndSynchronization(string key, bool succeeded, DateTime completedUtc, bool? hasAllChanges, bool? isModified)
        {
            lock (Gate) if (CommandSyncs.Contains(key)) { SyncStarts.Remove(key); return; }
            EndSynchronization(key, succeeded && hasAllChanges == true && isModified == false, completedUtc);
        }

        /// <summary>The document is now known under another key (the first save, Save As): its pending writes and last sync follow it.</summary>
        public static void Rename(string oldKey, string newKey)
        {
            if (string.IsNullOrEmpty(oldKey) || string.IsNullOrEmpty(newKey) || oldKey == newKey) return;
            lock (Gate)
            {
                LedgerRenameRules.MoveAll(oldKey, newKey, Pending, LastSync, LastSyncSource, SyncStarts);   // INT-9 M-3: every dictionary of this ledger follows the document   // R2Y m-3: the destination keeps only the renamed document's content
            }
        }

        public static List<SyncLedgerEntry> Writes(string key)
        {
            lock (Gate) return Pending.TryGetValue(key, out List<SyncLedgerEntry> list) ? list.ToList() : new List<SyncLedgerEntry>();
        }

        /// <summary>The tool and the argument that accept the findings of a sync that was refused for unverified writes (R2X: the old text named an argument document_session does not declare).</summary>
        public const string AcceptFindingsTool = "horizun_save_document";
        public const string AcceptFindingsArgument = "accept_findings";

        /// <summary>
        /// Why a sync is refused while writes of the session have failed or unmeasured verifications, and the way out. accept_findings is declared by
        /// horizun_save_document (operation=sync_with_central) and NOT by horizun_document_session, whose schema rejects it, so the text names the tool.
        /// </summary>
        public static string BlockedMessage(string title, IList<SyncLedgerEntry> blockers, SyncOnlyGrantRules.BlockedWayOut wayOut = null)
        {
            blockers = blockers ?? new List<SyncLedgerEntry>();
            string head = "REFUSING TO SYNCHRONIZE '" + title + "': " + blockers.Count + " write(s) of this session still have failed or " +
                          "unmeasured verifications (" + string.Join("; ", blockers.Take(5).Select(b => b.Tool + ": " + b.BlockReason));
            // INT-9 M-2: the way out comes from the decision of the tool the text names, not from the flag of the caller.
            if (wayOut != null && wayOut.Strict)
                return head + "). The owner's sync-only option is on; that path accepts no findings through " +
                       AcceptFindingsArgument + " or " + AcceptFindingsIdsArgument + ". Fix them and re-check with horizun_verify_changes, " +
                       "or the owner can turn that option off. Nothing was synchronized.";
            if (wayOut != null && !wayOut.AcceptFindingsOffered)
                return head + "). Fix them and re-check with horizun_verify_changes (a clean check clears them). This profile and grant " +
                       "do not admit " + AcceptFindingsTool + " operation=sync_with_central, so findings cannot be accepted here. " +
                       "Nothing was synchronized.";
            // INT-11 D.5: each acceptance accepts one kind. accept_findings=true accepts only unverified scripts; a failed check is accepted
            // only by accept_findings_ids naming every element id of it.
            bool python = blockers.Any(b => b.BlockKind == "unverified_python");
            bool checks = blockers.Any(b => b.BlockKind == "failed_check");
            string how = python && checks
                ? AcceptFindingsArgument + "=true (accepts only the unverified scripts) and " + AcceptFindingsIdsArgument + " naming EVERY element id of the failed checks"
                : checks
                    ? AcceptFindingsIdsArgument + " naming EVERY element id of the failed checks (" + AcceptFindingsArgument + "=true does not accept a failed check)"
                    : AcceptFindingsArgument + "=true";
            return head + "). Fix them and re-check with horizun_verify_changes (a clean check clears them), or - if the person " +
                   "accepts these findings - repeat the sync with " + AcceptFindingsTool + " operation=sync_with_central and " +
                   how + " (horizun_document_session does not take " + AcceptFindingsArgument + "). Nothing was synchronized.";
        }

        public static List<SyncLedgerEntry> Blockers(string key) => Writes(key).Where(e => e.BlockReason != null).ToList();

        // ------------------------------------------------------------------ INT-11 D.5: what each acceptance accepts

        /// <summary>The argument that accepts failed_check rows by naming every element id of them (horizun_save_document only).</summary>
        public const string AcceptFindingsIdsArgument = "accept_findings_ids";
        public const string FailedChecksCode = "sync_blocked_failed_checks";
        public const string UnverifiedCode = "sync_blocked_unverified";
        /// <summary>How many ids a refusal lists by value (the counts are always complete).</summary>
        public const int IdsListed = 500;

        /// <summary>
        /// INT-11 D.5: which blockers a sync request accepts. accept_findings=true accepts ONLY unverified_python rows (a script whose
        /// result is self-reported). A failed_check row (a spatial or post-commit check that failed or could not be measured) is accepted
        /// only when accept_findings_ids names EVERY element id of it; a row without ids cannot be named and is cleared only by a clean
        /// horizun_verify_changes. The strict path accepts neither kind of finding.
        /// </summary>
        public sealed class FindingsAcceptance
        {
            public List<SyncLedgerEntry> Remaining = new List<SyncLedgerEntry>();
            public int AcceptedPython, AcceptedChecks;
            public int PythonRows, CheckRows, PythonRemaining, ChecksRemaining, ChecksWithoutIds;
            /// <summary>Every element id of the failed_check rows (sorted): what accept_findings_ids must name.</summary>
            public List<long> RequiredIds = new List<long>();
            /// <summary>The required ids accept_findings_ids did not name (sorted, complete).</summary>
            public List<long> MissingIds = new List<long>();
            /// <summary>Named ids that belong to no pending failed_check row; the command refuses them.</summary>
            public List<long> UnusedIds = new List<long>();
            public bool AcceptFindings;
            public bool IdsGiven;
            public bool Proceed => Remaining.Count == 0;
            /// <summary>null when the sync may go on; otherwise sync_blocked_unverified for every kind of blocker.</summary>
            public string Code => Proceed ? null : UnverifiedCode;
        }

        /// <summary>Sorted, distinct: the form the token binds.</summary>
        public static List<long> NormalizeAcceptIds(IEnumerable<long> ids) =>
            (ids ?? Enumerable.Empty<long>()).Distinct().OrderBy(x => x).ToList();

        /// <summary>Reads accept_findings_ids: absent/null = none; otherwise an array of integers or an error.</summary>
        public static bool TryReadAcceptIds(JToken token, out List<long> ids, out string error)
        {
            ids = new List<long>(); error = null;
            if (token == null || token.Type == JTokenType.Null) return true;
            if (!(token is JArray arr))
            {
                error = AcceptFindingsIdsArgument + " must be an array of element ids (integers). Nothing was synchronized.";
                return false;
            }
            foreach (JToken t in arr)
            {
                if (t.Type != JTokenType.Integer)
                {
                    error = AcceptFindingsIdsArgument + " must contain only integer element ids (got '" + t.ToString(Newtonsoft.Json.Formatting.None) +
                            "'). Nothing was synchronized.";
                    return false;
                }
                ids.Add((long)t);
            }
            ids = NormalizeAcceptIds(ids);
            return true;
        }

        public static FindingsAcceptance Accept(IList<SyncLedgerEntry> blockers, bool acceptFindings, IEnumerable<long> acceptIds, bool strict)
        {
            var named = new HashSet<long>(acceptIds ?? Enumerable.Empty<long>());
            var a = new FindingsAcceptance { AcceptFindings = acceptFindings, IdsGiven = named.Count > 0 };
            var required = new HashSet<long>();
            foreach (SyncLedgerEntry e in blockers ?? new List<SyncLedgerEntry>())
            {
                if (e == null || e.BlockReason == null) continue;
                if (e.BlockKind == "unverified_python")
                {
                    a.PythonRows++;
                    if (acceptFindings && !strict) a.AcceptedPython++;
                    else { a.PythonRemaining++; a.Remaining.Add(e); }
                    continue;
                }
                a.CheckRows++;
                foreach (long id in e.Ids) required.Add(id);
                if (e.Ids.Length == 0) a.ChecksWithoutIds++;
                bool allNamed = !e.TransactionPending && e.Ids.Length > 0 && e.Ids.All(named.Contains);
                if (allNamed && !strict) a.AcceptedChecks++;
                else { a.ChecksRemaining++; a.Remaining.Add(e); }
            }
            a.RequiredIds = required.OrderBy(x => x).ToList();
            a.MissingIds = a.RequiredIds.Where(id => !named.Contains(id)).ToList();
            a.UnusedIds = named.Where(id => !required.Contains(id)).OrderBy(x => x).ToList();
            return a;
        }

        /// <summary>
        /// The structured way out of a refused sync, emitted only when the target admits the retry (BlockedWayOut.AcceptFindingsOffered):
        /// accept_findings for the unverified scripts, accept_findings_ids naming every id of the failed checks, or a clean recheck.
        /// </summary>
        public static JObject AcceptFindingsWith(FindingsAcceptance a)
        {
            a = a ?? new FindingsAcceptance();
            return new JObject
            {
                ["tool"] = AcceptFindingsTool,
                ["operation"] = "sync_with_central",
                ["argument"] = AcceptFindingsArgument,
                ["accepts"] = "unverified_python rows only (scripts whose result is self-reported); never a failed_check row",
                ["unverified_python_rows"] = a.PythonRows,
                ["failed_checks"] = new JObject
                {
                    ["rows"] = a.CheckRows,
                    ["argument"] = AcceptFindingsIdsArgument,
                    ["required_ids_count"] = a.RequiredIds.Count,
                    ["required_ids"] = new JArray(a.RequiredIds.Take(IdsListed)),
                    ["missing_ids_count"] = a.MissingIds.Count,
                    ["missing_ids"] = new JArray(a.MissingIds.Take(IdsListed)),
                    ["rows_without_ids"] = a.ChecksWithoutIds,
                    ["rule"] = AcceptFindingsIdsArgument + " must name EVERY element id of every failed_check row; if one is missing the whole " +
                               "sync is refused naming it and nothing is written. A row without ids cannot be named. The confirmation token " +
                               "of the dry run is bound to these ids: the apply must send the same list.",
                    ["or"] = "re-check with horizun_verify_changes until the check passes: a clean check clears the rows it covers"
                }
            };
        }

        /// <summary>The refusal when accept_findings (or accept_findings_ids) was sent but a failed check is still not accepted.</summary>
        public static string FailedChecksMessage(string title, FindingsAcceptance a, SyncOnlyGrantRules.BlockedWayOut wayOut = null)
        {
            a = a ?? new FindingsAcceptance();
            string ids = string.Join(", ", a.MissingIds.Take(50)) + (a.MissingIds.Count > 50 ? " ... (" + a.MissingIds.Count + " in all)" : "");
            string head = "REFUSING TO SYNCHRONIZE '" + title + "': " + a.ChecksRemaining + " write(s) of this session have a FAILED or unmeasured " +
                          "check (kind failed_check), and " + AcceptFindingsArgument + "=true accepts only unverified scripts (kind unverified_python), " +
                          "never a failed check. ";
            string named = a.MissingIds.Count > 0 ? "Element ids of those checks not named in " + AcceptFindingsIdsArgument + ": " + ids + ". " : "";
            string noIds = a.ChecksWithoutIds > 0 ? a.ChecksWithoutIds + " of those rows carry no element ids and can only be cleared by a clean recheck. " : "";
            string python = a.PythonRemaining > 0 ? a.PythonRemaining + " unverified script(s) are not accepted either (" + AcceptFindingsArgument + "=true was not sent). " : "";
            if (wayOut != null && !wayOut.AcceptFindingsOffered)
                return head + named + noIds + python + "Fix them and re-check with horizun_verify_changes (a clean check clears them); findings " +
                       "cannot be accepted on this path. Nothing was synchronized.";
            return head + named + noIds + python + "Two ways out: (a) fix them and re-check with horizun_verify_changes until the check passes; or " +
                   "(b) if the person accepts these failed checks, repeat " + AcceptFindingsTool + " operation=sync_with_central with " +
                   AcceptFindingsIdsArgument + " naming EVERY element id of them (dry run first: the token is bound to that list). Nothing was synchronized.";
        }

        public static DateTime? LastSyncUtc(string key)
        {
            lock (Gate) return LastSync.TryGetValue(key, out DateTime t) ? t : (DateTime?)null;
        }

        /// <summary>
        /// The reminder for a workshared document, or null when there is nothing to say: no
        /// write in this session since the last sync AND Revit reports no unsaved-to-central
        /// change it can see (isModified false).
        /// </summary>
        public static JObject PendingBlock(string key, bool workshared, bool? isModified)
        {
            if (!workshared) return null;
            List<SyncLedgerEntry> writes = Writes(key);
            if (writes.Count == 0 && isModified != true) return null;
            List<SyncLedgerEntry> blockers = writes.Where(e => e.BlockReason != null).ToList();
            int pending = writes.RemoveAll(e => e.TransactionPending);
            DateTime? last = LastSyncUtc(key);
            var block = new JObject
            {
                ["local_changes_unsynced"] = writes.Count != 0 || isModified == true ? (JToken)true : JValue.CreateNull(),
                ["writes_since_sync"] = writes.Count,
                ["tools"] = new JArray(writes.Select(w => w.Tool).Distinct().Take(20)),
                ["elements_touched"] = writes.SelectMany(w => w.Ids).Distinct().Count(),
                ["sync_blockers"] = blockers.Count,
                // INT-10: the same total, split. A script that only synchronized is still counted (the ledger cannot prove it touched
                // no element), but it is told apart from a failed check.
                ["blockers_unverified_python"] = blockers.Count(e => e.BlockKind == "unverified_python"),
                ["blockers_failed_checks"] = blockers.Count(e => e.BlockKind == "failed_check"),
                ["last_verified_sync_utc"] = last.HasValue ? last.Value.ToString("o") : null,
                ["last_sync_by"] = LastSyncBy(key),
                ["reminder"] = pending > 0
                    ? "Revit has not finished a transaction. Synchronization is blocked until the transaction is resolved."
                    : blockers.Count > 0
                    ? "Workshared model: these changes are saved LOCALLY only. " + blockers.Count + " write(s) still have failed or unmeasured " +
                      "verifications; inspect their reasons. A spatial recheck can clear covered spatial blockers, but cannot prove a failed " +
                      "command-specific post-commit check. When the task is done and the person confirms: " +
                      "horizun_save_document operation=sync_with_central (dry run, then token), relinquishing what you borrowed."
                    : "Workshared model: these changes are saved LOCALLY only. When the task is verified and the person confirms, synchronize " +
                      "once with horizun_save_document operation=sync_with_central (dry run, then token) and relinquish what you borrowed."
            };
            if (pending > 0) block["pending_transactions"] = pending;
            return block;
        }

        /// <summary>What a sync estimate lists of this session's work, and what it cannot know.</summary>
        public static JObject SessionChanges(string key)
        {
            List<SyncLedgerEntry> writes = Writes(key);
            List<long> ids = writes.SelectMany(w => w.Ids).Distinct().OrderBy(x => x).ToList();
            return new JObject
            {
                ["writes"] = writes.Count,
                ["by_tool"] = SelectionContextRules.ByCategory(writes.Select(w => w.Tool)),
                ["elements_touched"] = ids.Count,
                ["element_ids_sample"] = new JArray(ids.Take(100)),
                ["blockers"] = new JArray(writes.Where(e => e.BlockReason != null).Take(50).Select(e => (JToken)new JObject
                {
                    ["tool"] = e.Tool, ["at_utc"] = e.AtUtc.ToString("o"), ["reason"] = e.BlockReason, ["kind"] = e.BlockKind,
                    ["ids_sample"] = new JArray(e.Ids.Take(20))
                })),
                ["not_knowable"] = "Revit does not list the element changes waiting for central; this is the session's own record of " +
                                   "Horizun writes since the last verified sync (edits made by hand in Revit are not in it). " +
                                   "owned/borrowed counts and the update-status sample below come from Revit itself."
            };
        }

        internal static void ClearForTests() { lock (Gate) { Pending.Clear(); LastSync.Clear(); SyncStarts.Clear(); LastSyncSource.Clear(); } }
    }
}
