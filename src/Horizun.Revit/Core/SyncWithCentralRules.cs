// -----------------------------------------------------------------------------
// Horizun MCP - original Horizun code.
//
// SYNCHRONIZE WITH CENTRAL - the decisions, without Revit.
//
// A sync is the one write this bridge makes that can be neither rehearsed nor
// rolled back: it publishes the local's changes into a file other people work
// from and hands ownership back to the server. So everything that CAN be decided
// before the call is decided here, in code a test can hold:
//
//   * who may ask: the machine owner's grant AND the workshared read-only policy,
//     which wins over the grant - for the typed operation and for a Python script
//     whose source or includes name SynchronizeWithCentral or a Synchronize
//     postable command;
//   * what the document must be: workshared, not a detached copy, and not in a
//     state SynchronizeWithCentral documents as a precondition failure;
//   * what the preview is: an ESTIMATE. Document.HasAllChangesFromCentral() asks
//     the central whether this local is up to date; ownership (GetCheckoutStatus)
//     and the SAMPLED per-element GetModelUpdatesStatus are the local session's
//     CACHED view, which cannot see what others pushed since the last reload;
//   * what "it worked" means for each relinquish choice, measured afterwards by
//     UniqueId - the API documents that an ElementId may change across a sync
//     (Element.UniqueId remarks), so ids are display values only.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Horizun.Revit.Core
{
    public enum SyncRelinquish { All, KeepBorrowed, None, BorrowedOnly }

    /// <summary>How a SynchronizeWithCentral that threw is reported.</summary>
    public enum SyncFailureKind
    {
        /// <summary>A documented precondition: thrown before anything was read or written.</summary>
        NotStarted,
        /// <summary>The central was locked and the give-up callback made Revit cancel.</summary>
        CentralLocked,
        /// <summary>Anything else: the local may already have been saved or reloaded.</summary>
        Unknown
    }

    public sealed class SyncRefusal
    {
        public SyncRefusal(string code, string message) { Code = code; Message = message; }
        public string Code { get; }
        public string Message { get; }
    }

    /// <summary>
    /// Tri-state: true = held, false = measured and did not hold, null = could not be measured.
    /// Element keys are UniqueIds.
    /// </summary>
    public sealed class SyncVerdict
    {
        public bool? Verified;
        public readonly List<string> UnexpectedlyOwned = new List<string>();
        public readonly List<string> UnexpectedlyReleased = new List<string>();
        /// <summary>relinquish=none: elements the sync's reload brought into a workset this user still owns.</summary>
        public readonly List<string> ArrivedOwned = new List<string>();
        /// <summary>Sampled elements reading UpdatedInCentral/DeletedInCentral after the sync: the central moved since.</summary>
        public readonly List<string> MovedInCentral = new List<string>();
        public readonly List<string> Problems = new List<string>();
    }

    public static class SyncWithCentralRules
    {
        public const string ConfirmationScope = "horizun_document_session:sync_with_central";
        public const string CurrentWithCentral = "CurrentWithCentral";
        public const string Unreadable = "unreadable";
        public const int SpreadSampleSize = 150;
        public const int OwnedSampleSize = 50;
        public const string NotYetInCentral = "NotYetInCentral";

        /// <summary>SynchronizeWithCentralOptions.Comment throws ArgumentException above this (RevitAPI.xml 2023, 2026).</summary>
        public const int MaxCommentChars = 30000;

        /// <summary>The English label of the Advanced-options row behind force_read_only_on_workshared (RibbonText.CentralTitle).</summary>
        public const string ProtectSharedModelsLabel = "Protect shared models";

        /// <summary>
        /// The REQUEST: hashed into the token's plan hash, so a different request is refused
        /// as a different request.
        /// </summary>
        /// <remarks>INT-11 D.5: accept_findings_ids (sorted, distinct) is part of the request, so an apply that names other ids than its
        /// dry run is refused as a different request.</remarks>
        public static readonly string[] RequestFields = { "document", "comment", "relinquish", "compact", "accept_findings_ids" };

        /// <summary>
        /// The MODEL as the preview measured it: bound as the token's element fingerprint, so
        /// a model that moved between preview and apply is refused as a stale plan, with the
        /// fields that moved named (DescribeEstimateDrift).
        /// </summary>
        public static readonly string[] ModelFields =
        {
            "owned_worksets", "owned_elements", "borrowed_elements", "is_modified",
            "has_all_changes_from_central", "sample_status_counts"
        };

        /// <summary>Everything the confirmation token binds: the request and the estimate.</summary>
        public static readonly string[] EstimateFields = RequestFields.Concat(ModelFields).ToArray();

        // Any MENTION in the masked source, not only a call: `s = doc.SynchronizeWithCentral`
        // then `s(t, o)` is the same sync. The trailing \b keeps SynchronizeWithCentralOptions
        // out. PostableCommand.SynchronizeNow / SynchronizeAndModifySettings reach a sync
        // through UIApplication.PostCommand without naming the method at all.
        private static readonly Regex PythonSyncMention =
            new Regex(@"\bSynchronizeWithCentral\b|\bSynchronize(?:Now|AndModifySettings)\b", RegexOptions.Compiled);

        /// <summary>Does this MASKED source (comments and strings blanked) mention a synchronize?</summary>
        public static bool MentionsSync(string maskedCode) =>
            !string.IsNullOrEmpty(maskedCode) && PythonSyncMention.IsMatch(maskedCode);

        /// <summary>
        /// CT-P1-2: the checklist a sync preview publishes so the person (and a safe_write
        /// agent that holds only the sync grant) can see, BEFORE the token is issued, what the
        /// apply will do. Pure: every input is a measured count the command already holds.
        /// ok_to_apply is false while the comment is empty or a session write is unverified
        /// without being accepted; it never replaces the token or the owner's grant.
        /// </summary>
        public static Newtonsoft.Json.Linq.JObject Precheck(string comment, int sessionWrites, int unverifiedBlockers,
            bool blockersAccepted, int ownedElements, int borrowedElements, SyncRelinquish choice)
        {
            bool commentPresent = !string.IsNullOrWhiteSpace(comment);
            bool writesClear = unverifiedBlockers == 0 || blockersAccepted;
            int ownedViaWorksets = Math.Max(0, ownedElements - borrowedElements);
            int released = choice == SyncRelinquish.All ? ownedElements
                : choice == SyncRelinquish.BorrowedOnly ? borrowedElements
                : choice == SyncRelinquish.KeepBorrowed ? ownedViaWorksets
                : 0;
            var problems = new Newtonsoft.Json.Linq.JArray();
            if (!commentPresent) problems.Add("comment is empty: the central keeps it as the record of this sync");
            if (!writesClear) problems.Add(unverifiedBlockers + " write(s) of this session are unverified: re-check them or accept them by name");
            return new Newtonsoft.Json.Linq.JObject
            {
                ["comment_present"] = commentPresent,
                ["session_writes"] = sessionWrites,
                ["session_writes_unverified"] = unverifiedBlockers,
                ["session_writes_clear"] = writesClear,
                ["borrowed_elements_listed"] = borrowedElements,
                ["relinquish"] = Name(choice),
                ["elements_that_go_back"] = released,
                ["elements_kept_owned"] = Math.Max(0, ownedElements - released),
                ["after_sync"] = "the ownership is read again and held against this relinquish choice; a mismatch is reported, not hidden",
                ["problems"] = problems,
                ["ok_to_apply"] = commentPresent && writesClear
            };
        }

        public static bool TryParseRelinquish(string value, out SyncRelinquish choice)
        {
            switch ((value ?? "all").Trim().ToLowerInvariant())
            {
                case "all": choice = SyncRelinquish.All; return true;
                case "keep_borrowed": choice = SyncRelinquish.KeepBorrowed; return true;
                case "none": choice = SyncRelinquish.None; return true;
                case "borrowed_only": choice = SyncRelinquish.BorrowedOnly; return true;
                default: choice = SyncRelinquish.All; return false;
            }
        }

        public static string Name(SyncRelinquish choice)
        {
            switch (choice)
            {
                case SyncRelinquish.All: return "all";
                case SyncRelinquish.KeepBorrowed: return "keep_borrowed";
                case SyncRelinquish.BorrowedOnly: return "borrowed_only";
                default: return "none";
            }
        }

        private const string OwnerSwitch =
            "Only the machine owner enables it, inside Revit: Horizun Hub tab > Advanced options > Synchronize with central.";

        /// <summary>
        /// Where the owner turns it on. Said in every refusal, because a refusal that does
        /// not say who can change it invites the caller to look for a way around it. The
        /// profile is named too: under the default safe_write every document-session call is
        /// refused, so the owner's grant alone does not make the typed operation reachable.
        /// </summary>
        public static string HowOwnerEnables(string settingsPath)
        {
            return OwnerSwitch + " The call itself goes through horizun_save_document operation=sync_with_central, " +
                   "which the owner's grant alone opens even under the default safe_write; " +
                   "horizun_document_session needs permission_profile=full_write. Both choices are stored in " +
                   (settingsPath ?? "settings.json") + ", which no MCP call writes; do not edit it on the owner's behalf.";
        }

        /// <summary>The document's shape. null = it has a central this call could synchronize with.</summary>
        public static SyncRefusal DocumentRefusal(bool? workshared, bool? detached)
        {
            if (workshared == false)
                return new SyncRefusal("not_workshared",
                    "is not workshared: there is no central model to synchronize with. Nothing ran.");
            if (workshared == null)
                return new SyncRefusal("workshared_state_unreadable",
                    "could not report whether it is workshared (Document.IsWorkshared threw). An unknown " +
                    "collaboration state is not a central to write to. Nothing ran.");
            if (detached == true)
                return new SyncRefusal("detached_copy",
                    "is a DETACHED copy: it was cut loose from its central and has nothing to synchronize with. " +
                    "Nothing ran. Save it with save/save_as if it must be kept.");
            if (detached == null)
                return new SyncRefusal("detached_state_unreadable",
                    "could not report whether it is detached (Document.IsDetached threw). Nothing ran.");
            return null;
        }

        /// <summary>
        /// SynchronizeWithCentral's documented InvalidOperationException preconditions that can
        /// be read before the call. transactionOpen is Document.IsModifiable: true only inside an
        /// open transaction, which the call refuses ("has an open editing transaction"). An
        /// unreadable value is left to the call itself, whose precondition throw is classified
        /// NotStarted. null = none applies.
        /// </summary>
        public static SyncRefusal PreconditionRefusal(bool? isReadOnly, bool? transactionOpen, bool? hasCentralPath,
                                                      bool? readOnlyFile = null)
        {
            if (isReadOnly == true)
                return new SyncRefusal("document_read_only",
                    "is read-only right now (Document.IsReadOnly; Revit may be processing failures), so " +
                    "SynchronizeWithCentral would refuse it. Nothing ran.");
            // Document.IsReadOnlyFile: SynchronizeWithCentral documents that a read-only local
            // "can not be saved before or after synchronizing", without saying at which step it
            // notices - after the central write is not excluded. So it is refused here, before.
            if (readOnlyFile == true)
                return new SyncRefusal("local_file_read_only",
                    "was opened from a read-only file (Document.IsReadOnlyFile). SynchronizeWithCentral cannot save a " +
                    "read-only local before or after synchronizing, and the API does not say whether that is noticed " +
                    "before or after the central is written. Nothing ran. Reopen the local from a writable file.");
            if (transactionOpen == true)
                return new SyncRefusal("transaction_open",
                    "has an open transaction (Document.IsModifiable is true), which SynchronizeWithCentral refuses. " +
                    "Nothing ran.");
            if (hasCentralPath == false)
                return new SyncRefusal("no_central_path",
                    "reports no central model path (GetWorksharingCentralModelPath): there is nothing to " +
                    "synchronize with. Nothing ran.");
            return null;
        }

        /// <summary>
        /// Who allowed it. The workshared read-only policy wins over the grant: an owner who
        /// protected shared models has said no to this as well, and the two switches must
        /// never be read as "the newer one wins".
        /// </summary>
        public static SyncRefusal AuthorisationRefusal(bool ownerEnabled, bool forceReadOnlyOnWorkshared,
                                                       string settingsPath)
        {
            if (forceReadOnlyOnWorkshared)
                return new SyncRefusal("force_read_only_on_workshared",
                    "This machine protects shared models (force_read_only_on_workshared=true): the assistant may " +
                    "only look at them, and a synchronize with central is a write to the central. Nothing ran. " +
                    "The owner removes that protection in Revit: Horizun Hub tab > Advanced options > " +
                    ProtectSharedModelsLabel + ".");
            if (!ownerEnabled)
                return new SyncRefusal("sync_not_authorised",
                    "Synchronize with central is OFF on this machine, which is the default: it publishes into a " +
                    "file other people work from and cannot be undone. Nothing ran. " + HowOwnerEnables(settingsPath));
            return null;
        }

        /// <summary>
        /// Both switches cover horizun_execute_python as well, with the typed operation's
        /// precedence: under force_read_only_on_workshared a script that synchronizes is refused
        /// whatever the grant says (every sync writes a workshared central, whichever document
        /// the script targets - app.Documents reaches them all), and with the grant OFF it is
        /// refused too. Every source that runs in the scope is passed: the main script AND its
        /// includes. The same honest ceiling as ReadOnlyPythonGuard: a scan of the MASKED
        /// sources (comments and strings blanked), not a sandbox. RelinquishOwnership is not a
        /// synchronize and has its own typed tool (horizun_relinquish_all), so it is not
        /// covered here. null = run it.
        /// </summary>
        public static SyncRefusal PythonSyncRefusal(IEnumerable<string> maskedSources, bool ownerEnabled,
                                                    bool forceReadOnlyOnWorkshared)
        {
            if (maskedSources == null || !maskedSources.Any(MentionsSync)) return null;
            const string ceiling = " This is a static scan of the masked source text (the script and each include), " +
                "not a sandbox: it catches any mention of SynchronizeWithCentral or of the SynchronizeNow / " +
                "SynchronizeAndModifySettings postable commands, not a name assembled at runtime (getattr() with a " +
                "built string).";
            if (forceReadOnlyOnWorkshared)
                return new SyncRefusal("force_read_only_on_workshared",
                    "horizun_execute_python REFUSES to run this script: it synchronizes with central, and this machine " +
                    "protects shared models (force_read_only_on_workshared=true), which wins over the owner's sync " +
                    "switch. Nothing ran. The owner removes that protection in Revit: Horizun Hub tab > Advanced " +
                    "options > " + ProtectSharedModelsLabel + "." + ceiling);
            if (ownerEnabled) return null;
            return new SyncRefusal("sync_not_authorised",
                "horizun_execute_python REFUSES to run this script: it synchronizes with central, and Synchronize " +
                "with central is OFF on this machine - the owner's switch covers scripts as well as " +
                "horizun_document_session. Nothing ran. " + OwnerSwitch + ceiling);
        }

        /// <summary>
        /// A deterministic sample of element ids: an even spread over the whole sorted id
        /// range, plus up to `owned` of this local's likely changes - the BORROWED elements
        /// first, then the NEWEST owned ids. Every element of an owned workset reads as owned
        /// (a non-borrowed element's owner is its workset's owner), so the lowest owned ids
        /// are usually old elements already in central, while new NotYetInCentral elements
        /// take the highest ids. Deterministic so the preview and the apply sample the same
        /// elements and the token can bind their statuses.
        /// </summary>
        public static List<long> Sample(IEnumerable<long> allIds, IEnumerable<long> borrowedIds,
                                        IEnumerable<long> ownedIds, int spread, int owned)
        {
            var sorted = (allIds ?? Enumerable.Empty<long>()).Distinct().OrderBy(x => x).ToList();
            var picked = new SortedSet<long>();
            if (spread > 0 && sorted.Count > 0)
            {
                if (sorted.Count <= spread) foreach (long id in sorted) picked.Add(id);
                else
                    for (int i = 0; i < spread; i++)
                        picked.Add(sorted[(int)((long)i * sorted.Count / spread)]);
            }
            int added = 0;
            if (owned > 0)
            {
                IEnumerable<long> candidates =
                    (borrowedIds ?? Enumerable.Empty<long>()).Distinct().OrderByDescending(x => x)
                    .Concat((ownedIds ?? Enumerable.Empty<long>()).Distinct().OrderByDescending(x => x));
                foreach (long id in candidates)
                {
                    if (added >= owned) break;
                    if (picked.Add(id)) added++;
                }
            }
            return picked.ToList();
        }

        /// <summary>Count of each status name, ordinal-sorted so it hashes the same every time.</summary>
        public static SortedDictionary<string, int> Counts(IEnumerable<string> statuses)
        {
            var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
            foreach (string s in statuses ?? Enumerable.Empty<string>())
            {
                string key = s ?? "gone";
                counts[key] = counts.TryGetValue(key, out int n) ? n + 1 : 1;
            }
            return counts;
        }

        /// <summary>
        /// Which estimate fields moved between the preview and now, from each side's canonical
        /// text per ModelFields. null when nothing is known to have moved.
        /// </summary>
        public static string DescribeEstimateDrift(IDictionary<string, string> approved, IDictionary<string, string> now,
                                                   IEnumerable<string> fields = null)
        {
            if (approved == null || now == null) return null;
            var moved = new List<string>();
            foreach (string field in fields ?? ModelFields)
            {
                approved.TryGetValue(field, out string a);
                now.TryGetValue(field, out string b);
                if (!string.Equals(a, b, StringComparison.Ordinal))
                    moved.Add(field + " " + (a ?? "null") + " -> " + (b ?? "null"));
            }
            return moved.Count == 0 ? null : string.Join("; ", moved) + ".";
        }

        /// <summary>
        /// Did the ownership left behind match the relinquish choice? Keyed by UniqueId.
        /// Measured, per choice:
        ///   all           no workset and no element left owned by this user;
        ///   keep_borrowed no workset owned, and exactly the elements borrowed before
        ///                 (still existing) still owned;
        ///   borrowed_only the worksets this user owns stay owned (same count as before) and ONLY the
        ///                 elements borrowed one by one go back: owned before minus borrowed before;
        ///   none          the same number of worksets owned, and exactly the elements
        ///                 owned before (still existing) still owned; an element the reload
        ///                 brought into a still-owned workset is listed as arrived, not a failure.
        /// An unreadable count or element is UNMEASURED (null), never a pass.
        /// </summary>
        public static SyncVerdict VerifyOwnership(SyncRelinquish choice,
            int? ownedWorksetsBefore, ICollection<string> ownedElementsBefore, ICollection<string> borrowedBefore,
            int? ownedWorksetsAfter, ICollection<string> ownedElementsAfter, int unreadableAfter,
            Func<string, bool> stillExists, ICollection<string> knownBefore = null, ICollection<string> borrowedAfter = null)
        {
            var v = new SyncVerdict();
            Func<string, bool> exists = stillExists ?? (_ => true);
            bool unmeasured = false;

            if (ownedWorksetsAfter == null) { unmeasured = true; v.Problems.Add("owned workset count after the sync could not be read"); }
            if (unreadableAfter > 0) { unmeasured = true; v.Problems.Add(unreadableAfter + " element(s) did not report a checkout status after the sync"); }

            bool keepsWorksets = choice == SyncRelinquish.None || choice == SyncRelinquish.BorrowedOnly;
            int? expectedWorksets = keepsWorksets ? ownedWorksetsBefore : 0;
            if (keepsWorksets && ownedWorksetsBefore == null)
            { unmeasured = true; v.Problems.Add("owned workset count before the sync was not read"); }

            IEnumerable<string> expectedSource =
                choice == SyncRelinquish.All ? Enumerable.Empty<string>()
                : choice == SyncRelinquish.KeepBorrowed ? (borrowedBefore ?? new string[0])
                : choice == SyncRelinquish.BorrowedOnly ? (ownedElementsBefore ?? new string[0]).Except(borrowedBefore ?? new string[0], StringComparer.Ordinal)
                : (ownedElementsBefore ?? new string[0]);
            var expected = new HashSet<string>(expectedSource.Where(exists), StringComparer.Ordinal);
            var actual = new HashSet<string>(ownedElementsAfter ?? new string[0], StringComparer.Ordinal);
            if ((choice == SyncRelinquish.None || choice == SyncRelinquish.BorrowedOnly) && knownBefore != null)
            {
                // relinquish=none keeps this user's worksets, and the sync's reload brings in what
                // others created since: an element that lands in a workset this user still owns
                // reads OwnedByCurrentUser (a non-borrowed element's owner is its workset's owner)
                // although this relinquish never touched it. Listed, not held against the choice.
                // An arrival that reads BORROWED is not explained by that and stays unexpected.
                var known = new HashSet<string>(knownBefore, StringComparer.Ordinal);
                var borrowedNow = new HashSet<string>(borrowedAfter ?? new string[0], StringComparer.Ordinal);
                foreach (string uid in actual.Where(u => !known.Contains(u) && !borrowedNow.Contains(u))
                                             .OrderBy(x => x, StringComparer.Ordinal).ToList())
                {
                    v.ArrivedOwned.Add(uid);
                    actual.Remove(uid);
                }
            }

            v.UnexpectedlyOwned.AddRange(actual.Where(id => !expected.Contains(id)).OrderBy(x => x, StringComparer.Ordinal));
            v.UnexpectedlyReleased.AddRange(expected.Where(id => !actual.Contains(id)).OrderBy(x => x, StringComparer.Ordinal));

            bool worksetsHeld = ownedWorksetsAfter != null && expectedWorksets != null && ownedWorksetsAfter == expectedWorksets;
            if (ownedWorksetsAfter != null && expectedWorksets != null && !worksetsHeld)
                v.Problems.Add("expected " + expectedWorksets + " owned workset(s) after relinquish=" + Name(choice) +
                               ", measured " + ownedWorksetsAfter);
            if (v.UnexpectedlyOwned.Count > 0)
                v.Problems.Add(v.UnexpectedlyOwned.Count + " element(s) still owned that relinquish=" + Name(choice) + " should have released");
            if (v.UnexpectedlyReleased.Count > 0)
                v.Problems.Add(v.UnexpectedlyReleased.Count + " element(s) released that relinquish=" + Name(choice) + " should have kept");

            bool measuredMismatch = (ownedWorksetsAfter != null && expectedWorksets != null && !worksetsHeld) ||
                                    v.UnexpectedlyOwned.Count > 0 || v.UnexpectedlyReleased.Count > 0;
            v.Verified = measuredMismatch ? false : (unmeasured ? (bool?)null : true);
            return v;
        }

        /// <summary>
        /// After a sync every sampled element that still exists should read CurrentWithCentral:
        /// its local changes went up and the central's came down. Only NotYetInCentral
        /// contradicts THIS sync (its own change is not in the central). UpdatedInCentral and
        /// DeletedInCentral describe the central as it is now - another user's sync after this
        /// one returned produces them - so they are listed in MovedInCentral and leave the
        /// verdict unmeasured, never false. An element that no longer exists is not a failure;
        /// an unreadable one is unmeasured. Keys are UniqueIds; a null value = the element is gone.
        /// </summary>
        public static SyncVerdict VerifyUpdates(IDictionary<string, string> after)
        {
            var v = new SyncVerdict();
            bool unmeasured = false;
            foreach (var kv in (after ?? new Dictionary<string, string>()).OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                if (kv.Value == null) continue;
                if (kv.Value == Unreadable) { unmeasured = true; continue; }
                if (string.Equals(kv.Value, CurrentWithCentral, StringComparison.Ordinal)) continue;
                if (string.Equals(kv.Value, NotYetInCentral, StringComparison.Ordinal))
                {
                    v.UnexpectedlyOwned.Add(kv.Key);
                    v.Problems.Add("sampled element " + kv.Key + " still reads NotYetInCentral after the sync");
                    continue;
                }
                v.MovedInCentral.Add(kv.Key);
            }
            if (v.MovedInCentral.Count > 0)
                v.Problems.Add(v.MovedInCentral.Count + " sampled element(s) read UpdatedInCentral/DeletedInCentral after the " +
                               "sync: the central moved since (another user may have synchronized after this one), which " +
                               "neither confirms nor contradicts this sync");
            if (unmeasured) v.Problems.Add("some sampled elements did not report an update status");
            v.Verified = v.UnexpectedlyOwned.Count > 0 ? false
                : (unmeasured || v.MovedInCentral.Count > 0 ? (bool?)null : true);
            return v;
        }

        /// <summary>
        /// sync_verified. true only when the call RETURNED, the ownership and the sample held,
        /// the document reads HasAllChangesFromCentral()==true, and at least one witness says a
        /// save really happened: IsModified false after SaveLocalAfter, or a file-based
        /// central's write time advanced (the API saves to central even with no changes).
        /// false when the call threw or anything measured contradicts THIS sync; null when
        /// unmeasured. HasAllChangesFromCentral()==false is never a contradiction: it describes
        /// the central now, and another user's later sync makes it false (it is read at once).
        /// </summary>
        public static bool? OverallVerdict(bool callThrew, bool? ownership, bool? updates, bool? hasAllChangesAfter,
                                           bool? modifiedAfter, bool? centralWriteAdvanced, List<string> problems)
        {
            if (callThrew)
            {
                problems?.Add("SynchronizeWithCentral threw, so nothing measured afterwards is read as success");
                return false;
            }
            if (hasAllChangesAfter == false)
                problems?.Add("HasAllChangesFromCentral() read false right after the sync returned: the central moved since " +
                              "(another user may have synchronized after this one), which neither confirms nor contradicts this sync");
            else if (hasAllChangesAfter == null)
                problems?.Add("HasAllChangesFromCentral() could not be read after the sync");
            bool witnessed = modifiedAfter == false || centralWriteAdvanced == true;
            bool contradicted = modifiedAfter == true && centralWriteAdvanced == false;
            if (contradicted)
                problems?.Add("no save witnessed: IsModified is still true and the central file's write time did not advance");
            else if (!witnessed)
                problems?.Add("no save witnessed: IsModified after = " + (modifiedAfter?.ToString() ?? "unreadable") +
                              ", central file write time " + (centralWriteAdvanced == null ? "not measurable (not a file, or unreadable)" : "did not advance"));
            if (ownership == false || updates == false || contradicted) return false;
            return ownership == true && updates == true && hasAllChangesAfter == true && witnessed ? true : (bool?)null;
        }

        /// <summary>Did the owned set stay exactly as it was? null when either side is unreadable.</summary>
        public static bool? OwnershipUnchanged(int? worksetsBefore, ICollection<string> ownedBefore,
                                               int? worksetsAfter, ICollection<string> ownedAfter, int unreadableAfter)
        {
            if (worksetsBefore == null || worksetsAfter == null || unreadableAfter > 0) return null;
            return worksetsBefore == worksetsAfter &&
                   new HashSet<string>(ownedBefore ?? new string[0], StringComparer.Ordinal).SetEquals(ownedAfter ?? new string[0]);
        }

        /// <summary>
        /// write_started after a throw ClassifyFailure called NotStarted. The documented
        /// precondition exceptions include one the API does not place before the central write
        /// ("The local file is read-only. It can not be saved before or after synchronizing"),
        /// so the classification alone is no proof. false only when the re-read proves nothing
        /// moved: ownership unchanged, IsModified unchanged, and a file-based central's write
        /// time measured and not advanced. true when anything measured moved; null otherwise.
        /// </summary>
        public static bool? WriteStartedAfterPreconditionThrow(bool? ownershipUnchanged, bool? modifiedBefore,
                                                               bool? modifiedAfter, bool? centralWriteAdvanced)
        {
            bool modifiedKnown = modifiedBefore != null && modifiedAfter != null;
            if (ownershipUnchanged == false || centralWriteAdvanced == true || (modifiedKnown && modifiedBefore != modifiedAfter))
                return true;
            if (ownershipUnchanged == true && centralWriteAdvanced == false && modifiedKnown) return false;
            return null;
        }

        /// <summary>
        /// From the thrown type's FULL name. The documented InvalidOperationException and
        /// ArgumentException(Null) cases are preconditions (read-only, open transaction, edit
        /// mode, no central, local not owned or read-only, ...), documented as preconditions -
        /// but not all placed before the central write, so the caller re-reads before it says
        /// "nothing moved" (WriteStartedAfterPreconditionThrow). A locked central under the give-up callback is Revit cancelling. Every
        /// other family - communication, access, CentralModelException, cancellation, server
        /// errors - can arrive after the local was saved or reloaded, so it stays unknown.
        /// </summary>
        public static SyncFailureKind ClassifyFailure(string exceptionFullName)
        {
            switch (exceptionFullName)
            {
                case "Autodesk.Revit.Exceptions.InvalidOperationException":
                case "Autodesk.Revit.Exceptions.ArgumentException":
                case "Autodesk.Revit.Exceptions.ArgumentNullException":
                    return SyncFailureKind.NotStarted;
                case "Autodesk.Revit.Exceptions.CentralModelContentionException":
                    return SyncFailureKind.CentralLocked;
                default:
                    return SyncFailureKind.Unknown;
            }
        }
    }
}
