// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// THE REVIT-FREE HALF OF THE HRZ OWNERSHIP MARK (FIELD FEEDBACK 4.1).
//
// In a collaborative model the bridge must be able to tell "something this MCP
// made" from "somebody's deliverable". Every element a typed write ADDS is stamped
// with the HorizunOwnership extensible-storage entity (HrzOwnership.cs); editing
// tools that are restricted to Horizun-owned elements (horizun_view_crop,
// horizun_sheet_layout) read it back.
//
// This file decides WHEN the dispatcher stamps and how the reply reports it. The
// rule that matters: a failed stamp never turns a committed command into an error.
// The command's own work is already in the model and verified; the stamp is
// bookkeeping and reports its own outcome beside the result.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public static class HrzOwnershipRules
    {
        /// <summary>The name of the transaction the stamp runs in (one per document, after the command).</summary>
        public const string TransactionName = "Horizun: marca HRZ";

        /// <summary>At most this many elements are stamped per call; the rest are reported skipped.</summary>
        public const int MaxStampedPerCall = 5000;

        /// <summary>
        /// Tools whose "added" elements are NOT the bridge's creations: a sync or reload
        /// brings other people's elements, an open or a new project brings a whole model,
        /// an undo restores what existed before. Stamping those would claim foreign work.
        /// </summary>
        private static readonly HashSet<string> NeverStamp = new HashSet<string>(StringComparer.Ordinal)
        {
            "horizun_document_session", "horizun_open_document", "horizun_save_document",
            "horizun_relinquish_all", "horizun_undo", "horizun_verify_changes", "horizun_capture_view",
            "horizun_target", "horizun_navigate",
            // R2V M-1: what a SCRIPT creates is not something the guard may call ours (the bridge does not know what the code did or whose
            // elements it copied), and a copy between documents may duplicate somebody else's elements. Neither is stamped, and neither
            // enters the ledger of creations (SessionCreationLedger.RecordFor). They stay in the change ledger and the verification.
            "horizun_execute_python"
        };

        /// <summary>
        /// Whether the elements a committed call of this tool ADDED are "ours": stamped with the mark and remembered in the ledger of creations.
        /// False for the tools that bring in or copy other people's work and for Python (R2V M-1).
        /// </summary>
        public static bool CreationsAreOurs(string tool) => !string.IsNullOrEmpty(tool) && !NeverStamp.Contains(tool);

        /// <summary>
        /// True when the dispatcher should look for added elements after this call.
        /// mutating: the contract classifies the tool as a model write (always or unless
        /// dry_run). A reply that says dry_run=true is a rehearsal and is never stamped.
        /// </summary>
        public static bool ShouldStamp(string tool, bool mutating, bool success, bool replyIsDryRun)
        {
            if (string.IsNullOrEmpty(tool) || !mutating || !success || replyIsDryRun) return false;
            return !NeverStamp.Contains(tool);
        }

        /// <summary>
        /// True when a FAILED call may still have left elements of its own in the model - a
        /// typed write that committed a transaction and then raised, a plan that stopped halfway.
        /// What it created and kept is ours, so it is marked like a success (a SCRIPT never is: R2V M-1); what a rollback
        /// took away no longer resolves and is dropped by ChangeWatch.Settle before stamping.
        /// </summary>
        public static bool ShouldStampAfterFailure(string tool, bool mutating)
            => !string.IsNullOrEmpty(tool) && mutating && !NeverStamp.Contains(tool);

        /// <summary>
        /// INT-9 m-1. What a FAILED call stamped must also enter the ledger of creations (by UniqueId), or the same session would read its own mark as a copy:
        /// "marked = believed". A success is recorded by the change ledger already; a script's or a copy's creations are not ours and never enter.
        /// </summary>
        public static bool ShouldRecordCreationAfterFailure(bool failed, string tool) => failed && CreationsAreOurs(tool);

        /// <summary>A copy of a failure's detail carrying the ownership_stamp block (the original is not touched).</summary>
        public static JObject MergeIntoDetail(JObject detail, JObject stampReport)
        {
            JObject d = detail != null ? (JObject)detail.DeepClone() : new JObject();
            d["ownership_stamp"] = stampReport;
            return d;
        }

        /// <summary>
        /// Whether a mark that was READ can be believed (R2T M-2, R2U M-B). Extensible storage travels with a copy, so a person's Ctrl+C of a marked
        /// element, a copy_between_documents and a new instance of a group with marked members all carry the mark. Told apart cheaply:
        ///  - the mark must name a USER and it must be this user (SameUser: without domain, without the "os:" prefix, without case, by SID when both have it); another user's mark, a mark without the field (made
        ///    before it existed) and a user that cannot be established are NOT ours (fail closed);
        ///  - a mark that says it was made in THIS Revit session must correspond to an element a Horizun write of this session ADDED (the
        ///    ledger of creations, by UniqueId), otherwise it is a copy;
        ///  - a mark of another session of the same user (an earlier session, or a copy made then) cannot be told apart from a genuine one without
        ///    a registry of every session, so it is believed: that limit is documented (docs/IDENTITY-GUARD.md).
        /// A mark whose session field cannot be read is believed when the user is ours.
        /// </summary>
        public static bool TrustMark(bool marked, string markSession, string currentSession, bool createdThisSession, string markUser, string currentUser) =>
            IsTrusted(new MarkFacts { Present = marked, Session = markSession, User = new UserRef { User = markUser } }, currentSession,
                      new UserRef { User = currentUser }, createdThisSession);

        /// <summary>
        /// The user a mark names and is compared with: Revit's own user name (Application.Username) or, when Revit has none, the Windows user
        /// prefixed "os:". Null when neither can be established. Stamping and reading use this one function, so they always agree.
        /// </summary>
        /// <summary>An owner id is 32 hexadecimal digits (a GUID in "N" form) and nothing else - no name can hide in it.</summary>
        public static bool IsOwnerId(string s) =>
            s != null && s.Trim().Length == 32 && s.Trim().All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'));

        /// <summary>
        /// The anonymous owner id kept at <paramref name="path"/>: read when the file holds a valid one, otherwise a new random one is written
        /// there and returned. Null when it can be neither read nor written (the mark then names no owner and nothing counts as ours).
        /// </summary>
        public static string OwnerIdFrom(string path)
        {
            try
            {
                if (System.IO.File.Exists(path))
                {
                    string text = System.IO.File.ReadAllText(path).Trim();
                    if (IsOwnerId(text)) return text.ToLowerInvariant();
                }
                string fresh = Guid.NewGuid().ToString("N");
                string dir = System.IO.Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) System.IO.Directory.CreateDirectory(dir);
                string tmp = path + ".tmp-" + Guid.NewGuid().ToString("N");
                System.IO.File.WriteAllText(tmp, fresh);
                if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
                System.IO.File.Move(tmp, path);
                string back = System.IO.File.ReadAllText(path).Trim();
                return IsOwnerId(back) ? back.ToLowerInvariant() : null;
            }
            catch { return null; }
        }

        public static string UserIdentity(string revitUsername, string osUser)
        {
            if (!string.IsNullOrWhiteSpace(revitUsername)) return revitUsername.Trim();
            if (!string.IsNullOrWhiteSpace(osUser)) return "os:" + osUser.Trim();
            return null;
        }

        /// <summary>
        /// The mark composed with the ledger of creations: the element (named by its UniqueId, never by its ElementId, which a synchronize can
        /// renumber and Revit can hand to another element) is ours. What the identity guard calls "Owned".
        /// </summary>
        public static bool Owned(string ledgerKey, string uniqueId, bool marked, string markSession, string markUser, string currentSession, string currentUser) =>
            TrustMark(marked, markSession, currentSession, SessionCreationLedger.Created(ledgerKey, uniqueId), markUser, currentUser);

        /// <summary>The block added to the reply: {stamped, skipped, error}. error is null when nothing failed.</summary>
        public static JObject Report(int stamped, int skipped, string error, IEnumerable<string> skipReasons = null)
        {
            var o = new JObject { ["stamped"] = stamped, ["skipped"] = skipped, ["error"] = error == null ? JValue.CreateNull() : (JToken)error };
            // This covers only the candidates observed by ChangeWatch, not all possible
            // creations in a command or other documents.
            o["coverage_complete"] = skipped == 0 && error == null;
            o["coverage_scope"] = "observed_candidates_only";
            if (skipReasons != null)
            {
                var reasons = new JArray();
                foreach (string r in skipReasons) if (!string.IsNullOrEmpty(r)) reasons.Add(r);
                if (reasons.Count > 0) o["skip_reasons"] = reasons;
            }
            return o;
        }

        /// <summary>
        /// The ownership gate of the restricted editing tools: an element may be edited when
        /// it carries the mark, when this Revit session created it, or when the caller sent
        /// the explicit override. Null when allowed; otherwise the refusal.
        /// </summary>
        public static string Gate(string what, long id, bool marked, bool createdThisSession, bool overrideFlag, string overrideName, string reason = null)
        {
            if (marked || createdThisSession || overrideFlag) return null;
            // R2W M-b: the message names the REAL reason (reason), not always "no mark".
            return what + " " + id + " " + (reason ?? "carries no HRZ ownership mark") + " and was not created in this session: it may be " +
                   "somebody else's work in a shared model, so it is not edited. Send " + overrideName +
                   "=true only if its owner agreed. Nothing was written.";
        }

        // ---------------------------------------------------------------------------------------------------------------
        // R2V. WHO A USER IS, WHAT A MARK MEANS FOR EVERY OWNERSHIP GATE, AND WHAT COUNTS AS A STAMP.
        // ---------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// A user as the mark records it: the name Revit (or the Windows fallback "os:x") gave, the Windows account and, when known, its SID.
        /// All three are optional; an absent one decides nothing.
        /// </summary>
        public sealed class UserRef
        {
            /// <summary>The identity of <see cref="UserIdentity"/>: Application.Username, or "os:" + the Windows user.</summary>
            public string User;
            /// <summary>The Windows account, "DOMAIN\user" or "user".</summary>
            public string Windows;
            /// <summary>The Windows SID of the account.</summary>
            public string Sid;
            /// <summary>
            /// 5.0.4: the anonymous owner id of this Windows user on this machine (a random GUID kept in %USERPROFILE%\.horizun\owner-id).
            /// The version 3 mark records ONLY this - no Revit user, Windows account, machine or SID - and is compared by it alone.
            /// </summary>
            public string Owner;
        }

        /// <summary>What was READ from an element about its Horizun mark. User is null for a mark of version 1, which has none.</summary>
        public sealed class MarkFacts
        {
            public bool Present;
            public string Session;
            public UserRef User;
        }

        /// <summary>The name stripped of the "os:" fallback prefix, of the domain ("DOM\x" -> "x") and of case. Null when nothing is left.</summary>
        public static string NormalizeUser(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            string n = name.Trim();
            if (n.StartsWith("os:", StringComparison.OrdinalIgnoreCase)) n = n.Substring(3).Trim();
            int bs = n.LastIndexOf('\\');
            if (bs >= 0) n = n.Substring(bs + 1).Trim();
            return n.Length == 0 ? null : n.ToLowerInvariant();
        }

        /// <summary>The Revit user name normalized, or null when there is none (blank, or the "os:" Windows fallback).</summary>
        private static string RevitName(string user)
        {
            if (string.IsNullOrWhiteSpace(user) || user.Trim().StartsWith("os:", StringComparison.OrdinalIgnoreCase)) return null;
            return NormalizeUser(user);
        }

        /// <summary>
        /// Whether the user a mark names is the user of this Revit (R2V M-4). The Windows SID decides when BOTH sides have it (two people on one
        /// generic Revit account are told apart by it; one person on two Revit accounts is recognised). Without SIDs the names decide, compared
        /// without domain, without the "os:" prefix and without case; the Revit name against the Revit name, the Windows account against the
        /// Windows account, and a Revit name against a Windows account (a session in which Revit had no name stamped "os:x", another in which it
        /// had "x"). LIMIT, documented in docs/IDENTITY-GUARD.md: two different people who share one account and have no SID in the mark look
        /// like the same user; a local (non-domain) SID differs per machine, so one person on two machines looks like two users (fail closed).
        /// </summary>
        public static bool SameUser(UserRef mark, UserRef current)
        {
            if (mark == null || current == null) return false;
            // A version 3 mark carries only the anonymous owner id: it is ours exactly when it is this owner id (fail closed when either is missing).
            if (!string.IsNullOrWhiteSpace(mark.Owner))
                return IsOwnerId(mark.Owner) && IsOwnerId(current.Owner) &&
                       string.Equals(mark.Owner.Trim(), current.Owner.Trim(), StringComparison.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(mark.Sid) && !string.IsNullOrWhiteSpace(current.Sid))
            {
                // Another SID: another person.
                if (!string.Equals(mark.Sid.Trim(), current.Sid.Trim(), StringComparison.OrdinalIgnoreCase)) return false;
                // R2W M-a. The same SID is the same Windows account, and two people can share one (a generic "cad" account). If BOTH sides have a Revit
                // user name (not the "os:" fallback) and the names differ, they are two people signed in to Revit with their own accounts.
                string a = RevitName(mark.User), b = RevitName(current.User);
                if (a != null && b != null && !string.Equals(a, b, StringComparison.Ordinal)) return false;
                return true;
            }
            string[] mine = new[] { NormalizeUser(mark.User), NormalizeUser(mark.Windows) }.Where(x => x != null).ToArray();
            string[] yours = new[] { NormalizeUser(current.User), NormalizeUser(current.Windows) }.Where(x => x != null).ToArray();
            return mine.Any(m => yours.Contains(m));
        }

        /// <summary>
        /// THE one rule for "this mark is ours" (R2V M-2): the guard, the delete cascade (OwnershipProbe), view_visibility and the restricted
        /// tools (view_crop, sheet_layout) all ask it. A mark of version 1 (no user), another user's mark and a user that cannot be established are
        /// somebody else's; a mark of this session needs a creation in the ledger; see <see cref="TrustMark"/>.
        /// </summary>
        public static bool IsTrusted(MarkFacts mark, string currentSession, UserRef current, bool createdThisSession)
        {
            if (mark == null || !mark.Present) return false;
            if (!SameUser(mark.User, current)) return false;
            if (string.IsNullOrEmpty(mark.Session) || string.IsNullOrEmpty(currentSession)) return true;   // unreadable session: the user is ours
            return mark.Session != currentSession || createdThisSession;
        }

        /// <summary>The mark composed with the ledger of creations by UniqueId (the guard's "Owned").</summary>
        public static bool Owned(string ledgerKey, string uniqueId, MarkFacts mark, string currentSession, UserRef current) =>
            IsTrusted(mark, currentSession, current, SessionCreationLedger.Created(ledgerKey, uniqueId));

        /// <summary>
        /// The ownership verdict of an element for the delete cascade and view_visibility: <see cref="OwnershipRules.Decide"/> fed with the
        /// BELIEVED mark, never with the mere presence of one (R2V M-2: another user's mark used to count as ours here).
        /// </summary>
        public static OwnershipVerdict Verdict(bool workshared, MarkFacts mark, string currentSession, UserRef current, bool createdThisSession, bool? creatorIsMe, string creator) =>
            OwnershipRules.Decide(workshared, IsTrusted(mark, currentSession, current, createdThisSession), createdThisSession, creatorIsMe, creator);

        /// <summary>The gate of the restricted editing tools (view_crop, sheet_layout): null when allowed, else the refusal.</summary>
        public static string RequireRefusal(string what, long id, MarkFacts mark, string currentSession, UserRef current, bool createdThisSession, bool overrideFlag, string overrideName,
                                            bool workshared = true) =>
            Gate(what, id, GateTrusts(mark, currentSession, current, createdThisSession, workshared), createdThisSession, overrideFlag, overrideName,
                 RefusalReason(mark, currentSession, current, createdThisSession, workshared));

        /// <summary>
        /// R2W M-b. The belief of the view / sheet gates: <see cref="IsTrusted"/>, except that in a document that is NOT collaborative a mark of version 1
        /// (rc2: no user) counts as ours - nobody else works in the file, and what an earlier build marked there was made by this person. In a
        /// collaborative document it stays strict (a version 1 mark cannot say whose it is). The guard, the cascade and view_visibility keep asking IsTrusted.
        /// </summary>
        public static bool GateTrusts(MarkFacts mark, string currentSession, UserRef current, bool createdThisSession, bool workshared)
        {
            if (IsTrusted(mark, currentSession, current, createdThisSession)) return true;
            return !workshared && mark != null && mark.Present && mark.User == null;
        }

        /// <summary>R2W M-b. Why an element is not trusted, in words (null when it is trusted): the real reason, not always "no mark".</summary>
        public static string RefusalReason(MarkFacts mark, string currentSession, UserRef current, bool createdThisSession, bool workshared)
        {
            if (GateTrusts(mark, currentSession, current, createdThisSession, workshared)) return null;
            if (mark == null || !mark.Present) return "carries no HRZ ownership mark";
            if (mark.User == null) return "carries a version 1 HRZ ownership mark (made by an earlier build, with no user) in a collaborative model, where it cannot say whose it is";
            if (!SameUser(mark.User, current)) return "carries the HRZ ownership mark of another user (or of a user that cannot be established)";
            return "carries an HRZ ownership mark that says it was made in this session, but no write of this session created it (it is a copy)";
        }

        /// <summary>The result of trying to stamp one element with both versions of the mark.</summary>
        public enum StampOutcome
        {
            /// <summary>Both versions were written: the element is stamped.</summary>
            Stamped,
            /// <summary>Version 2 failed and version 1 was removed again: the element carries no mark and is reported skipped.</summary>
            SkippedV1RolledBack,
            /// <summary>Version 2 failed and version 1 could not be removed: reported skipped; version 1 has no user, so it counts as somebody else's.</summary>
            SkippedV1Left,
            /// <summary>Version 1 failed: nothing was written.</summary>
            Skipped
        }

        /// <summary>R2V m-g: an element is stamped only when BOTH versions were written; otherwise it is never counted as sealed.</summary>
        public static StampOutcome StampResult(bool v1Written, bool v2Written, bool v1RolledBack)
        {
            if (!v1Written) return StampOutcome.Skipped;
            if (v2Written) return StampOutcome.Stamped;
            return v1RolledBack ? StampOutcome.SkippedV1RolledBack : StampOutcome.SkippedV1Left;
        }
    }
}
