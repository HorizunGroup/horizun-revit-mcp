// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// CT-P0-4b: A REMINDER MAY ONLY NAME TOOLS THIS SESSION CAN CALL.
//
// pending_sync's reminder asked for horizun_verify_changes where tools/list did not carry
// it (permission_profile=safe_write hides it: it writes an image) and suggested a sync that
// the profile refuses. An agent that follows a reminder into a refusal has been misled by
// the bridge. This decides the sentence from what the session can actually do, and is
// applied AFTER the ledger builds its block, so the ledger keeps one definition of "pending".
//
// Revit-free: the capabilities arrive as plain values.
// -----------------------------------------------------------------------------
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public sealed class ReminderCapabilities
    {
        public static readonly ReminderCapabilities All = new ReminderCapabilities { VerifyAvailable = true, SyncAvailable = true };

        public bool VerifyAvailable { get; set; }
        public bool SyncAvailable { get; set; }
        /// <summary>M10: the owner's sync-only flag is on and the profile is safe_write: the strict precheck applies.</summary>
        public bool SyncOnly { get; set; }
        public string Profile { get; set; }

        /// <summary>
        /// What this session can do, from plain values. The sync half comes from the SAME function the
        /// sync command uses (SyncOnlyGrantRules.Decide), so the reminder never promises what the command refuses.
        /// </summary>
        public static ReminderCapabilities For(string profile, bool ownerSyncGrant, bool syncOnlyFlag,
                                               bool verifyAvailable, bool saveToolAllowed)
        {
            SyncOnlyGrantRules.SyncDecision d = SyncOnlyGrantRules.Decide(profile, ownerSyncGrant, syncOnlyFlag);
            return new ReminderCapabilities
            {
                Profile = profile,
                VerifyAvailable = verifyAvailable,
                SyncOnly = d.Strict,
                SyncAvailable = d.Allowed && saveToolAllowed
            };
        }

        /// <summary>The reminder sentence for <paramref name="blockers"/> unverified writes.</summary>
        public static string Reminder(int blockers, ReminderCapabilities caps)
        {
            caps = caps ?? All;
            string where = string.IsNullOrEmpty(caps.Profile) ? "this session" : "permission_profile=" + caps.Profile;
            // A reminder names only tools this session lists (CT-P0-4b). Under the sync-only permission accept_findings does not exist,
            // so when the re-check tool is not listed the only exits are the owner's: say that, not a tool the profile hides.
            string verify = caps.SyncOnly && caps.VerifyAvailable
                ? "re-check them with horizun_verify_changes (a clean check clears them); the sync-only permission does not accept findings " +
                  "(accept_findings is refused)"
                : caps.SyncOnly
                ? "leave it to the owner: the tool that re-checks writes is not listed in " + where + " (it writes an image) and the sync-only " +
                  "permission does not accept findings (accept_findings is refused), so only the owner can clear them in Revit, by widening the " +
                  "profile to full_write so the person can accept them by name; until then they stay pending"
                : caps.VerifyAvailable
                ? "re-check them with horizun_verify_changes"
                : "horizun_verify_changes is not available in " + where + " (it writes an image), so they cannot be cleared " +
                  "here: the person reviews them and accepts them by name (accept_findings=true) or the owner widens the profile in Revit";
            string sync = caps.SyncAvailable && caps.SyncOnly
                ? "horizun_save_document operation=sync_with_central under the owner's sync-only permission (dry run, then token): a non-empty " +
                  "comment, relinquish=all or borrowed_only, the session's own diff at zero; the dry run lists what you hold and the apply " +
                  "re-reads it."
                : caps.SyncAvailable
                ? "horizun_save_document operation=sync_with_central (dry run, then token), relinquishing what you borrowed."
                : "synchronizing is not allowed in " + where + " (the owner grants it in Revit: Advanced options > Synchronize with " +
                  "central; under safe_write the owner can instead turn on the sync-only option, off by default); until then keep saving locally with horizun_save_document operation=save_local and tell the person.";
            return blockers > 0
                ? "Workshared model: these changes are saved LOCALLY only. " + blockers + " write(s) still have failed or unmeasured " +
                  "verifications; inspect their reasons. A spatial recheck can clear covered spatial blockers, but cannot prove a failed " +
                  "command-specific post-commit check. To clear them, " + verify + ". When the task is done and the person confirms: " + sync
                : "Workshared model: these changes are saved LOCALLY only. When the task is verified and the person confirms, synchronize " +
                  "once with " + sync;
        }

        /// <summary>Rewrites the reminder of a pending_sync block in place, and says which tools it can name.</summary>
        public static void Apply(JObject block, ReminderCapabilities caps)
        {
            if (block == null) return;
            caps = caps ?? All;
            int blockers = block.Value<int?>("sync_blockers") ?? 0;
            block["reminder"] = Reminder(blockers, caps);
            block["verify_changes_available"] = caps.VerifyAvailable;
            block["sync_available"] = caps.SyncAvailable;
            if (caps.SyncOnly) block["sync_only"] = true;
        }
    }
}
