// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// THE "SYNC ONLY" PERMISSION (M10) - OFF BY DEFAULT, BEHIND THE OWNER'S FLAG.
//
// An option that lets horizun_save_document run operation=sync_with_central under
// safe_write (and relinquish_own of what is the user's own) without granting full_write;
// with the flag on, full_write/unsafe_code sync too, always strictly (a higher profile never
// gets less than safe_write). It exists only while the machine owner turns on
// allow_sync_only_grant from Revit (Advanced options); no MCP call writes that key. With
// the flag OFF nothing in this file is consulted and the behaviour is the one that
// shipped before it (safe_write plus the owner's "Synchronize with central" grant).
//
// With the flag ON (and a profile that can sync: safe_write, full_write, unsafe_code; also
// when the older grant is on too) the sync is STRICT and always passes this precheck, in the
// dry run AND again in the apply:
//
//   * the session's own diff is zero: no write of the session with a failed or unmeasured
//     verification, and accept_findings does not exist on this path;
//   * a non-empty comment;
//   * the relinquish leaves nothing of the user's borrowed (relinquish=all or borrowed_only);
//     that only the user's own is given back is guaranteed by RelinquishOptions, which the API
//     cannot point at another user's elements;
//   * the preview lists what the user holds (elements and worksets, counts, a sample of
//     ids and unique_ids) and the token binds the comment AND a digest of that list: if
//     either moves between preview and apply the apply is refused;
//   * after the sync the model is read again: nothing borrowed by the user is left, no
//     local change is pending and the update status against the central is measured; what
//     others held before and no longer holds is listed. A leftover is a failure, never "verified".
//
// Revit-free on purpose: every decision is a pure function a test can hold.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public static class SyncOnlyGrantRules
    {
        /// <summary>The settings key. Written only by the ribbon's owner dialog.</summary>
        public const string SettingsKey = "allow_sync_only_grant";

        /// <summary>The profile this option exists for.</summary>
        public const string Profile = "safe_write";

        /// <summary>The estimate fields that the token's model fingerprint adds in strict mode.</summary>
        public static readonly string[] StrictExtraFields = { "borrowed_digest", "owned_digest", "owned_workset_ids" };

        /// <summary>The model fields bound by the token: the usual ones, plus the lists when strict.</summary>
        public static string[] ModelFields(bool strict) =>
            strict ? SyncWithCentralRules.ModelFields.Concat(StrictExtraFields).ToArray() : SyncWithCentralRules.ModelFields;

        /// <summary>
        /// Strict mode: the owner's flag is on AND the profile can synchronize at all (safe_write, full_write,
        /// unsafe_code). A higher profile never gets LESS than safe_write: with the flag on it may sync too,
        /// without the older grant, and always strictly.
        /// </summary>
        public static bool Strict(bool flagOn, string profile) =>
            flagOn && (profile == Profile || profile == "full_write" || profile == "unsafe_code");

        /// <summary>The one decision the command and the pending_sync reminder both use.</summary>
        public sealed class SyncDecision
        {
            /// <summary>The sync-only precheck applies.</summary>
            public bool Strict;
            /// <summary>The owner's authorisation the sync itself checks (the older grant, or the flag).</summary>
            public bool Authorised;
            /// <summary>horizun_save_document may run sync_with_central: profile admits it and it is authorised.</summary>
            public bool Allowed;
        }

        /// <summary>
        /// The line every text of this option opens with (INT-7 m-d): it is experimental and off by default. Neutral on purpose: no
        /// person and no team is named, and nothing says who decided what.
        /// </summary>
        public static string ExperimentalNote(bool spanish) =>
            spanish ? "Opción experimental, apagada por defecto. " : "Experimental option, off by default. ";

        /// <summary>
        /// The ribbon dialog's text for TURNING THE OPTION ON, built here so a test can read it. It tells the truth about the higher
        /// modes: under full_write and unsafe_code the sync of horizun_save_document is strict too while the option is on.
        /// </summary>
        public static string EnableDialogText(bool spanish, string syncGrantTitle, string protectTitle)
        {
            return ExperimentalNote(spanish) + (spanish
                ? "En safe_write el asistente podrá sincronizar con el central, sin necesitar además «" + syncGrantTitle + "». En los modos superiores (full_write, unsafe_code) la sincronización por horizun_save_document pasa a ser también ESTRICTA mientras esta opción esté encendida: pierden, en esa herramienta, accept_findings, accept_findings_ids y los relinquish none y keep_borrowed. El asistente solo puede sincronizar si: el comentario no está vacío, no hay scripts propios sin verificar y las verificaciones fallidas están corregidas, devuelve todo lo tuyo (all o borrowed_only) y el modelo se relee después. Nunca cede lo de otras personas. No hay ensayo ni deshacer: verás una ESTIMACIÓN con lo prestado y debe confirmarse. No sincroniza copias desvinculadas ni modelos bajo «" + protectTitle + "». Si además tienes encendido «" + syncGrantTitle + "», safe_write sigue siendo ESTRICTO. Queda activo hasta que lo apagues aquí."
                : "On safe_write the assistant may synchronize with the central, without also needing \"" + syncGrantTitle + "\". On the higher modes (full_write, unsafe_code) synchronizing through horizun_save_document becomes STRICT too while this option is on: there they lose accept_findings, accept_findings_ids and the relinquish choices none and keep_borrowed. The assistant may synchronize only if: the comment is not empty, there are no unverified scripts and failed checks are corrected, it gives back all that is yours (all or borrowed_only) and the model is re-read afterwards. It never gives back what other people hold. There is no rehearsal and no undo: you get an ESTIMATE listing what is borrowed and it must be confirmed. It does not synchronize detached copies nor models under \"" + protectTitle + "\". If \"" + syncGrantTitle + "\" is also on, safe_write is still STRICT. It stays on until you turn it off here.");
        }

        /// <summary>
        /// INT-6 m-1: the decision from ONE read of the settings file (Settings.ReadSyncSettings). The save command, the
        /// strict precheck and the sync it calls take their authorisation from this value and do not read the file again.
        /// </summary>
        public static SyncDecision Decide(Settings.SyncSettingsSnapshot settings) =>
            Decide(settings.Profile, settings.OwnerSyncGrant, settings.SyncOnlyFlag);

        public static SyncDecision Decide(string profile, bool ownerSyncGrant, bool flagOn)
        {
            bool strict = Strict(flagOn, profile);
            bool authorised = ownerSyncGrant || strict;
            return new SyncDecision
            {
                Strict = strict,
                Authorised = authorised,
                Allowed = authorised &&
                    OperationPermission.SaveRefusal(profile, OperationPermission.SyncWithCentral, ownerSyncGrant, flagOn) == null
            };
        }

        /// <summary>
        /// INT-9 M-2: what a refusal for unverified writes may tell the caller to do next. Taken from the SAME decision the
        /// target tool applies (Decide), never from the flag the caller happened to use: horizun_document_session runs the sync
        /// non-strict even when the owner's option is on, but the tool the text names (horizun_save_document) is then strict
        /// and refuses accept_findings.
        /// </summary>
        public sealed class BlockedWayOut
        {
            /// <summary>The retry with horizun_save_document operation=sync_with_central accept_findings=true would be accepted.</summary>
            public bool AcceptFindingsOffered;
            /// <summary>The target tool is strict: accept_findings does not exist on that path.</summary>
            public bool Strict;
        }

        /// <summary>
        /// Pure rule: the retry is offered only when the target (horizun_save_document) admits the operation AND is not strict.
        /// An unknown decision (settings unreadable) offers nothing: a message never promises a way out it cannot prove.
        /// </summary>
        public static BlockedWayOut WayOutOfBlocked(SyncDecision decision, bool callerSyncOnly)
        {
            bool strict = callerSyncOnly || (decision != null && decision.Strict);
            return new BlockedWayOut
            {
                Strict = strict,
                AcceptFindingsOffered = decision != null && !strict && decision.Allowed
            };
        }

        /// <summary>The relinquish choices that leave nothing of the user's borrowed behind.</summary>
        public static bool RelinquishLeavesNothingBorrowed(SyncRelinquish choice) =>
            choice == SyncRelinquish.All || choice == SyncRelinquish.BorrowedOnly;

        /// <summary>
        /// The refusals decided before the census, identical in the dry run and the apply.
        /// null = the request may go on to the measured checks.
        /// </summary>
        public static SyncRefusal RequestRefusal(string comment, SyncRelinquish choice, bool acceptFindings, bool acceptFindingsIds = false)
        {
            if (string.IsNullOrWhiteSpace(comment))
                return new SyncRefusal("sync_only_comment_required",
                    "The sync-only permission requires a non-empty comment: the central keeps it as the record of this sync. Nothing ran.");
            if (acceptFindings || acceptFindingsIds)
                return new SyncRefusal("sync_only_no_accept_findings",
                    "The owner sync-only option is on; this path accepts no findings through accept_findings or accept_findings_ids. " +
                    "Fix the unverified writes and re-check them with horizun_verify_changes, or ask the owner to turn off the sync-only option. Nothing ran.");
            if (!RelinquishLeavesNothingBorrowed(choice))
                return new SyncRefusal("sync_only_relinquish",
                    "The sync-only permission gives back what the user holds: relinquish must be all or borrowed_only (got " +
                    SyncWithCentralRules.Name(choice) + "). Nothing ran.");
            return null;
        }

        /// <summary>A short, order-independent digest of a list of unique ids. null/empty list -> the digest of nothing.</summary>
        public static string Digest(IEnumerable<string> items)
        {
            var sorted = (items ?? Enumerable.Empty<string>()).Where(s => s != null).Distinct(StringComparer.Ordinal)
                .OrderBy(s => s, StringComparer.Ordinal).ToList();
            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(sorted.Count + ":" + string.Join("|", sorted)));
                return BitConverter.ToString(hash, 0, 8).Replace("-", "").ToLowerInvariant();
            }
        }

        /// <summary>
        /// What the strict preview publishes: the lists the token binds, with counts and a bounded
        /// sample. Reaching the preview already means the comment, the relinquish choice and the
        /// own diff passed (they are refused before it), so it states no verdict of its own.
        /// </summary>
        public static JObject PreviewBlock(SyncRelinquish choice, IList<long> borrowedIds, IList<string> borrowedUids,
            IList<long> ownedIds, IList<string> ownedWorksetNames, IList<int> ownedWorksetIds, int ownedByOthers, int sample = 20)
        {
            return new JObject
            {
                ["active"] = true,
                ["relinquish"] = SyncWithCentralRules.Name(choice),
                ["borrowed"] = new JObject
                {
                    ["count"] = borrowedIds.Count,
                    ["sample_ids"] = new JArray(borrowedIds.OrderByDescending(x => x).Take(sample)),
                    ["sample_unique_ids"] = new JArray(borrowedUids.Take(sample))
                },
                ["owned_elements"] = ownedIds.Count,
                ["owned_worksets"] = new JObject
                {
                    ["count"] = ownedWorksetIds.Count,
                    ["names"] = new JArray(ownedWorksetNames.Take(sample))
                },
                ["held_by_others"] = ownedByOthers,
                ["token_binds"] = "the comment and a digest of the borrowed list, the owned elements and the owned worksets"
            };
        }

        /// <summary>
        /// The re-read after the sync. Tri-state: true held, false a leftover or a pending
        /// change, null something could not be measured. Problems name each shortfall.
        /// </summary>
        public static SyncVerdict PostCheck(SyncRelinquish choice, int ownedElementsAfter, int borrowedElementsAfter,
            int? ownedWorksetsAfter, bool? modifiedAfter, bool? hasAllChangesAfter,
            IEnumerable<string> ownedBefore, IEnumerable<string> ownedAfter, IEnumerable<string> ownedByOthersBefore,
            IEnumerable<string> ownedByOthersAfter = null, Func<string, bool> stillExists = null)
        {
            var v = new SyncVerdict();
            bool? ok = true;
            void Fail(string problem) { v.Problems.Add(problem); ok = false; }
            void Unmeasured(string problem) { v.Problems.Add(problem); if (ok == true) ok = null; }

            if (borrowedElementsAfter > 0)
                Fail(borrowedElementsAfter + " element(s) are still borrowed by the user after the sync");
            if (choice == SyncRelinquish.All)
            {
                if (ownedElementsAfter > 0) Fail(ownedElementsAfter + " element(s) are still owned by the user after relinquish=all");
                if (ownedWorksetsAfter == null) Unmeasured("the user's owned worksets could not be read after the sync");
                else if (ownedWorksetsAfter.Value > 0) Fail(ownedWorksetsAfter.Value + " workset(s) are still owned by the user after relinquish=all");
            }
            if (modifiedAfter == true) Fail("the local still has pending changes (IsModified is true after the sync)");
            else if (modifiedAfter == null) Unmeasured("whether the local has pending changes could not be read");
            if (hasAllChangesAfter == null) Unmeasured("the update status against the central could not be read");
            else if (hasAllChangesAfter == false)
                Unmeasured("the local does not have all the central's changes right after the sync (the central moved since, or the reload is incomplete)");

            // CEDE ONLY WHAT IS OURS: what went back must have been the user's before; and nothing
            // that someone else held may now read as ours.
            var before = new HashSet<string>(ownedBefore ?? Enumerable.Empty<string>(), StringComparer.Ordinal);
            var after = new HashSet<string>(ownedAfter ?? Enumerable.Empty<string>(), StringComparer.Ordinal);
            var others = new HashSet<string>(ownedByOthersBefore ?? Enumerable.Empty<string>(), StringComparer.Ordinal);
            foreach (string uid in before.Where(u => !after.Contains(u) && others.Contains(u)).Take(20))
                Fail("an element that read as held by someone else was also counted as ceded by the user: " + uid);
            foreach (string uid in others.Where(u => after.Contains(u)).Take(20))
                Fail("an element that was held by someone else reads as the user's after the sync: " + uid);
            // What others held before and no longer hold after (and still exists) is LISTED, not failed: another
            // user may have released or lost it for their own reasons, and a re-read cannot tell whose act it was.
            // The guarantee that this user gives back only what this user owns comes from RelinquishOptions
            // (the API cannot release another user's elements); this list lets the person see anything odd.
            if (ownedByOthersAfter != null)
            {
                var othersAfter = new HashSet<string>(ownedByOthersAfter, StringComparer.Ordinal);
                v.UnexpectedlyReleased.AddRange(others.Where(u => !othersAfter.Contains(u) && (stillExists == null || stillExists(u)))
                    .OrderBy(u => u, StringComparer.Ordinal).Take(50));
            }
            v.Verified = ok;
            return v;
        }
    }
}
