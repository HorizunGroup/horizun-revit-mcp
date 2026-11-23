// -----------------------------------------------------------------------------
// Horizun MCP - original Horizun code.
//
// The few things that must be switched on deliberately.
//
// Read by BOTH halves: the server decides whether a tool is even advertised, the
// add-in decides whether it will run. Two checks, not one - the server and the
// plugin ship separately, so a stale server must not be able to enable something
// the machine's owner turned off, and a client that calls a tool it never saw in
// tools/list must still be refused at the far end.
//
// Deliberately dull: a JSON file the owner can read and edit in Notepad, at
//
//     %USERPROFILE%\.horizun\settings.json
//
// The location comes from HorizunPaths, which BOTH halves share. It used to be
// computed here from LocalApplicationData and again in six other places; see
// HorizunPaths.cs for why a per-process environment variable is the wrong root
// for state two processes must agree on.
//
// It is re-read on every use rather than cached, so switching something off takes
// effect on the next call instead of the next Revit restart. That matters most
// for the one setting that exists today: arbitrary code execution.
//
// THE DEFAULTS ARE SAFE FOR AN UNATTENDED AGENT. A fresh install permits typed
// writes inside the active document, but it cannot open/close documents, write
// external files or execute arbitrary code: an absent file, or a file without
// these keys, reads as permission_profile=safe_write and
// enable_execute_python=false. Elevation is always an explicit owner decision.
//
// One asymmetry is deliberate: a file that EXISTS but cannot be parsed falls
// CLOSED (read_only, Python off), not open. The owner may have written an
// explicit restriction into that file, and a corrupted byte must never convert
// "I turned this off" into "everything is enabled".
//
// WHAT EACH PROFILE MEANS. The ladder is cumulative, and each rung is decided by
// ToolEffect rather than by a list of tool names - a list is what let an
// externally-effecting tool be admitted by a profile that forbids external
// effects, because the tool was added to the enum and not to the list:
//
//   read_only    reads, and steers the host (which Revit answers, what is
//                selected). It does not change the model, does not open or close
//                a document session, and writes NOTHING outside the model.
//   safe_write   the above, plus typed writes INSIDE the document.
//   full_write   the above, plus document sessions and typed external writes.
//   unsafe_code  the above, plus eligibility for horizun_execute_python. It is
//                never the implicit default.
//
// documentation is a SIDE rung, not a step on the ladder: reads and host steering
// like read_only, plus the "documentation" toolset (views, annotation, sheets,
// revisions), PDF and one-view image export and the local save - and NOTHING that changes a modelled
// element. Admission is by toolset MEMBERSHIP (DocumentationAdmits), so a tool another
// change adds to that toolset is admitted without editing a list here; the per-call
// halves (export format, save operation, the element kinds delete/write_params may
// touch) live in OperationPermission and are asked by the commands themselves.
//
// ToolEffect.HostState is why read_only still admits something that is not a
// pure read: horizun_target chooses WHICH Revit every later call talks to, and a
// read-only machine that cannot choose its Revit cannot read. It used to share a
// classification with the workbook writer, and refusing the whole bucket would
// have broken the profile this fix exists to protect.
//
// ToolEffect.ExternalSideEffectOnRequest is the same shape of correction one
// level finer. horizun_budget_compare CAN create a workbook and CAN push a Power
// BI table, and both really do need full_write - but a call with no `outputs`
// does neither, and hiding the whole tool refused a read_only machine the
// arithmetic because the same surface can also write. Admission is by effect, as
// everything here is; THE DESTINATION is decided per call by
// AllowsExternalSideEffect below, which the handler must consult before it
// touches a destination. Two halves, and the tool is only honest with both.
//
// The matrix is asserted for every profile against every ToolEffect value in
// SettingsEffectMatrixTests, so a new effect nobody classified fails a test
// instead of quietly inheriting whichever branch happens to miss it.
// -----------------------------------------------------------------------------
using System;
using System.IO;
using System.Text;
using System.Collections.Generic;
using System.Threading;
using System.Linq;
using Horizun.Contracts;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public static class Settings
    {
        private static readonly object WriteLock = new object();
        private const int BusyRetryDefaultMaxAttempts = 4;
        private const int BusyRetryDefaultBaseDelayMs = 250;
        private const int BusyRetryDefaultMaxDelayMs = 2000;
        private const int BusyRetryDefaultBudgetMs = 5000;

        public static bool AutoCloseDialogs => !string.Equals(
            Environment.GetEnvironmentVariable("HORIZUN_AUTO_CLOSE_DIALOGS"), "false", StringComparison.OrdinalIgnoreCase) &&
            Environment.GetEnvironmentVariable("HORIZUN_AUTO_CLOSE_DIALOGS") != "0";

        // Re-signalling a queued ExternalEvent cannot execute a command twice: the
        // same RequestGate entry is claimed once. Keep this separate from any model
        // write retry, which requires its own idempotency key. These process knobs
        // are bounded so a malformed value cannot extend a call indefinitely.
        public static int BusyRetryMaxAttempts => BoundedEnvironmentInt("HORIZUN_BUSY_RETRY_MAX_ATTEMPTS",
            BusyRetryDefaultMaxAttempts, 0, 20);
        public static int BusyRetryBaseDelayMs => BoundedEnvironmentInt("HORIZUN_BUSY_RETRY_BASE_MS",
            BusyRetryDefaultBaseDelayMs, 25, 5000);
        public static int BusyRetryMaxDelayMs => BoundedEnvironmentInt("HORIZUN_BUSY_RETRY_MAX_DELAY_MS",
            BusyRetryDefaultMaxDelayMs, 25, 10000);
        public static int BusyRetryBudgetMs => BoundedEnvironmentInt("HORIZUN_BUSY_RETRY_BUDGET_MS",
            BusyRetryDefaultBudgetMs, 0, 30000);

        private static int BoundedEnvironmentInt(string name, int fallback, int min, int max)
        {
            string raw = Environment.GetEnvironmentVariable(name);
            return int.TryParse(raw, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out int value)
                ? Math.Max(min, Math.Min(max, value)) : fallback;
        }

        /// <summary>Owner policy, with a Revit-process override for one session. Default remains true.</summary>
        public static bool AutoCloseExternalToolFailure
        {
            get
            {
                JObject settings = Read(out FileState state);
                return AutoCloseDialogs && ExternalToolFailureAutoCloseFrom(settings, state == FileState.Malformed,
                    Environment.GetEnvironmentVariable("HORIZUN_AUTO_CLOSE_EXTERNAL_TOOL_FAILURE"));
            }
        }

        /// <summary>Independent owner/session policy for the reminder; never saves the model.</summary>
        public static bool AutoCloseSaveReminder
        {
            get
            {
                JObject settings = Read(out FileState state);
                return AutoCloseDialogs && SaveReminderAutoCloseFrom(settings, state == FileState.Malformed,
                    Environment.GetEnvironmentVariable("HORIZUN_AUTO_CLOSE_SAVE_REMINDER"));
            }
        }

        public static bool SaveReminderAutoCloseFrom(JObject settings, bool malformed, string sessionOverride)
        {
            if (malformed || settings?["auto_close_save_reminder"]?.Type == JTokenType.Boolean &&
                !(bool)settings["auto_close_save_reminder"]) return false;
            return !string.Equals(sessionOverride, "false", StringComparison.OrdinalIgnoreCase) && sessionOverride != "0";
        }

        public static bool ExternalToolFailureAutoCloseFrom(JObject settings, bool malformed, string sessionOverride)
        {
            // A session may disable the policy, but never override the owner's false.
            if (malformed || settings?["auto_close_external_tool_failure"]?.Type == JTokenType.Boolean &&
                !(bool)settings["auto_close_external_tool_failure"]) return false;
            return !string.Equals(sessionOverride, "false", StringComparison.OrdinalIgnoreCase) && sessionOverride != "0";
        }

        public static JObject ModalDialogPolicy() => new JObject
        {
            ["auto_close_external_tool_failure"] = AutoCloseExternalToolFailure,
            ["default_auto_close_external_tool_failure"] = true,
            ["owner_setting"] = "auto_close_external_tool_failure",
            ["session_environment"] = "HORIZUN_AUTO_CLOSE_EXTERNAL_TOOL_FAILURE",
            ["save_reminder_auto_close"] = AutoCloseSaveReminder,
            ["auto_close_save_reminder"] = AutoCloseSaveReminder,
            ["default_auto_close_save_reminder"] = true,
            ["save_reminder_owner_setting"] = "auto_close_save_reminder",
            ["save_reminder_session_environment"] = "HORIZUN_AUTO_CLOSE_SAVE_REMINDER",
            ["all_dialogs_session_environment"] = "HORIZUN_AUTO_CLOSE_DIALOGS",
            ["queued_busy_retry"] = new JObject
            {
                ["max_attempts"] = BusyRetryMaxAttempts,
                ["base_delay_ms"] = BusyRetryBaseDelayMs,
                ["max_delay_ms"] = BusyRetryMaxDelayMs,
                ["budget_ms"] = BusyRetryBudgetMs,
                ["configuration"] = "HORIZUN_BUSY_RETRY_MAX_ATTEMPTS, HORIZUN_BUSY_RETRY_BASE_MS, " +
                    "HORIZUN_BUSY_RETRY_MAX_DELAY_MS, HORIZUN_BUSY_RETRY_BUDGET_MS",
                ["scope"] = "re-signals one queued request; it never executes the command twice"
            }
        };

        public static string Path()
        {
            return HorizunPaths.SettingsPath();
        }

        /// <summary>
        /// Is horizun_execute_python allowed to run? Default FALSE.
        ///
        /// It runs arbitrary code inside Revit, on the UI thread, with full API access
        /// and the rights of the signed-in user. Enabling it requires an explicit true
        /// AND permission_profile=unsafe_code. An absent or non-boolean key means OFF.
        /// The Revit ribbon writes a separate persistent UI grant which authorizes only
        /// this tool and does not silently elevate the rest of permission_profile.
        /// A legacy execute_python_ui_grant_until_utc is still honoured until its old
        /// expiry so an in-place upgrade never revokes an already-approved running batch.
        /// </summary>
        public static bool ExecutePythonEnabled
        {
            get
            {
                FileState state;
                JObject o = Read(out state);
                return ExecutePythonEnabledFrom(o, state);
            }
        }

        private static bool ExecutePythonEnabledFrom(JObject o, FileState state)
        {
            if (state == FileState.Malformed) return false;
            if (TemporaryExecutePythonGrant(o, DateTimeOffset.UtcNow, out _)) return true;
            JToken ui = o?["execute_python_ui_granted"];
            if (ui != null && ui.Type == JTokenType.Boolean && (bool)ui) return true;
            JToken t = o?["enable_execute_python"];
            return t != null && t.Type == JTokenType.Boolean && (bool)t;
        }

        /// <summary>
        /// Legacy bounded grant written by versions before the ribbon became a persistent
        /// owner switch. New code never creates this key; it is read only for a safe
        /// in-place upgrade and removed by either ON or OFF.
        /// </summary>
        public static DateTimeOffset? ExecutePythonTemporaryGrantUntilUtc
        {
            get
            {
                FileState state;
                JObject o = Read(out state);
                if (state == FileState.Malformed) return null;
                return TemporaryExecutePythonGrant(o, DateTimeOffset.UtcNow, out DateTimeOffset until)
                    ? (DateTimeOffset?)until : null;
            }
        }

        /// <summary>read_only | documentation | safe_write (default) | full_write | unsafe_code.</summary>
        public static string PermissionProfile
        {
            get
            {
                FileState state;
                JObject o = Read(out state);
                return ProfileFrom(o, state);
            }
        }

        /// <summary>
        /// Local emergency pause for MCP work. This is separate from read_only:
        /// read_only still permits audits, whereas a pause hides and refuses every
        /// tool except health so an operator can establish that the bridge is paused.
        /// Only the local Revit ribbon writes it.
        /// </summary>
        public static bool McpPaused
        {
            get
            {
                FileState state;
                JObject o = Read(out state);
                return state != FileState.Malformed && o?["mcp_paused"]?.Type == JTokenType.Boolean &&
                       (bool)o["mcp_paused"];
            }
        }

        public static bool TrySetMcpPaused(bool paused, out string error)
        {
            return TryUpdate(o =>
            {
                o["mcp_paused"] = paused;
                o["mcp_paused_changed_from_revit_at_utc"] = DateTimeOffset.UtcNow.ToString("O");
                return true;
            }, out error);
        }

        /// <summary>
        /// Optional local policy for shared models. When enabled, a workshared or
        /// unreadable workshared state is treated as audit-only for every operation
        /// that could write. A dry run remains allowed because it commits nothing.
        /// </summary>
        public static bool ForceReadOnlyOnWorkshared
        {
            get
            {
                FileState state;
                JObject o = Read(out state);
                return state != FileState.Malformed && o?["force_read_only_on_workshared"]?.Type == JTokenType.Boolean &&
                       (bool)o["force_read_only_on_workshared"];
            }
        }

        public static bool TrySetForceReadOnlyOnWorkshared(bool enabled, out string error)
        {
            return TryUpdate(o =>
            {
                o["force_read_only_on_workshared"] = enabled;
                o["force_read_only_on_workshared_changed_from_revit_at_utc"] = DateTimeOffset.UtcNow.ToString("O");
                return true;
            }, out error);
        }

        /// <summary>
        /// May horizun_document_session synchronize a workshared model with its central?
        /// OFF unless the machine owner said yes from Revit's Advanced options, gated like
        /// execute_python: a sync publishes into a file other people work from and has no
        /// rollback. Only an explicit boolean true counts; a malformed file falls closed.
        /// No MCP call writes this key.
        /// </summary>
        public static bool SyncWithCentralOwnerEnabled
        {
            get
            {
                FileState state;
                JObject o = Read(out state);
                return state != FileState.Malformed && o?["sync_with_central_owner_granted"]?.Type == JTokenType.Boolean &&
                       (bool)o["sync_with_central_owner_granted"];
            }
        }

        /// <summary>
        /// M10: the owner's flag for the "sync only" permission (SyncOnlyGrantRules). OFF by default;
        /// only an explicit boolean true counts, a malformed file falls closed. No MCP call writes it.
        /// </summary>
        public static bool SyncOnlyGrantEnabled
        {
            get
            {
                FileState state;
                JObject o = Read(out state);
                return state != FileState.Malformed && o?["allow_sync_only_grant"]?.Type == JTokenType.Boolean &&
                       (bool)o["allow_sync_only_grant"];
            }
        }

        /// <summary>
        /// INT-6 m-1 / INT-7 m-k: the settings a sync decision depends on, from ONE read of the file (profile, the owner's grant,
        /// the sync-only flag, the workshared protection and - for IsToolAllowed(contract, out reason, photo) - the file itself),
        /// so everything a call decides with comes from one moment and not from several reads that a second process could split.
        /// </summary>
        public sealed class SyncSettingsSnapshot
        {
            public string Profile = "read_only";
            public bool OwnerSyncGrant;
            public bool SyncOnlyFlag;
            /// <summary>force_read_only_on_workshared as the file said at that read. The default is the closed one: protected.</summary>
            public bool ForceReadOnly = true;
            internal JObject Raw;
            internal FileState State;
            internal string Failure;
        }

        public static SyncSettingsSnapshot ReadSyncSettings()
        {
            JObject o = Read(out FileState state, out string failure);
            return new SyncSettingsSnapshot
            {
                Profile = ProfileFrom(o, state),
                OwnerSyncGrant = state != FileState.Malformed && o?["sync_with_central_owner_granted"]?.Type == JTokenType.Boolean &&
                                 (bool)o["sync_with_central_owner_granted"],
                SyncOnlyFlag = state != FileState.Malformed && o?["allow_sync_only_grant"]?.Type == JTokenType.Boolean &&
                               (bool)o["allow_sync_only_grant"],
                ForceReadOnly = state != FileState.Malformed && o?["force_read_only_on_workshared"]?.Type == JTokenType.Boolean &&
                                (bool)o["force_read_only_on_workshared"],
                Raw = o,
                State = state,
                Failure = failure
            };
        }

        // ---------------------------------------------------------------------------------------------
        // INT-7 m-4: WHAT A SCRIPT RUN DID TO THE OWNER'S SYNC GRANTS - REPORTED, NEVER REVERTED.
        //
        // The two grants (allow_sync_only_grant, sync_with_central_owner_granted) are plain booleans in settings.json, written by
        // the ribbon's owner dialog. horizun_execute_python (unsafe_code) runs as the user, so it - like any other process of
        // the user, or the agent's own shell - can write that file, and nothing inside Revit can stop it: the real protection is
        // the permission profile and the owner's decision not to grant unsafe_code (AGENTS.md). What the bridge CAN do is not
        // stay silent: the value of each grant before and after a run is compared, and a difference is told to the human in the
        // script's reply as `settings_changed`. Nothing is put back. When the owner's writer stamped the key during the run,
        // another Revit instance (or the owner) is the more likely hand, and the report says so without blaming the script.
        // ---------------------------------------------------------------------------------------------
        /// <summary>The field of a script's reply that carries the changes (a stable name: AGENTS.md says it).</summary>
        public const string SettingsChangedField = "settings_changed";

        /// <summary>
        /// The keys of settings.json that decide what the bridge may do, all of them writable by any process that runs with the user's
        /// rights. AGENTS.md lists them; a test keeps the list and the document in step.
        /// </summary>
        public static readonly string[] SensitiveKeys =
            { "allow_sync_only_grant", "sync_with_central_owner_granted", "force_read_only_on_workshared", "mcp_paused", "permission_profile" };

        private static readonly string[] SyncGrantKeys = { "allow_sync_only_grant", "sync_with_central_owner_granted" };
        private static readonly string[] SyncGrantStampKeys =
            { "allow_sync_only_grant_changed_from_revit_at_utc", "sync_with_central_owner_changed_from_revit_at_utc" };

        /// <summary>The settings file as it stood at one moment (null raw = it could not be read), taken before a script runs.</summary>
        public sealed class SyncGrantPhoto
        {
            internal JObject Raw;
        }

        public static SyncGrantPhoto PhotoSyncGrants()
        {
            var photo = new SyncGrantPhoto();
            try
            {
                string p = Path();
                photo.Raw = File.Exists(p) ? JObject.Parse(File.ReadAllText(p)) : new JObject();
            }
            catch { photo.Raw = null; }
            return photo;
        }

        /// <summary>
        /// The `settings_changed` array for a run that started at <paramref name="before"/>: one row per grant whose value is
        /// different now, or null when nothing changed (or the "before" could not be read, so nothing can be claimed).
        /// </summary>
        public static JArray SyncGrantsChangedSince(SyncGrantPhoto before)
        {
            if (before == null || before.Raw == null) return null;
            return CompareSyncGrants(before.Raw, PhotoSyncGrants().Raw);
        }

        /// <summary>Pure: the comparison itself. <paramref name="after"/> null means the file could not be read after the run.</summary>
        public static JArray CompareSyncGrants(JObject before, JObject after)
        {
            if (before == null) return null;
            var rows = new JArray();
            if (after == null)
            {
                rows.Add(new JObject
                {
                    ["key"] = "settings.json",
                    ["problem"] = "unreadable_after_the_run",
                    ["reverted"] = false,
                    ["means"] = "settings.json could not be read after the script ran (missing, locked or no longer valid JSON), so whether the owner's " +
                                "sync grants changed cannot be said. Nothing was put back. Check the file before relying on a sync permission."
                });
                return rows;
            }
            for (int i = 0; i < SyncGrantKeys.Length; i++)
            {
                string key = SyncGrantKeys[i];
                bool was = IsOnToken(before[key]), now = IsOnToken(after[key]);
                if (was == now) continue;
                bool stamped = !string.Equals(StampOf(before, SyncGrantStampKeys[i]), StampOf(after, SyncGrantStampKeys[i]), StringComparison.Ordinal);
                rows.Add(new JObject
                {
                    ["key"] = key,
                    ["before"] = was,
                    ["after"] = now,
                    ["owner_writer_stamp_changed"] = stamped,
                    ["reverted"] = false,
                    ["possible_cause"] = stamped ? "owner_writer_of_another_revit_instance_or_the_owner" : "the_script_or_another_process_of_this_user",
                    ["means"] = "The value of '" + key + "' in settings.json was " + was.ToString().ToLowerInvariant() + " when the script started and is " +
                                now.ToString().ToLowerInvariant() + " now. Nothing was put back: this is a notice for the owner. " +
                                (stamped
                                    ? "The owner's writer (the Revit ribbon) stamped the key during the run, so another Revit instance of this user, or the owner, may have changed it; this does not say the script did."
                                    : "No stamp of the owner's writer changed, so the script may have written it - or any other process of this user, or another Revit instance, in the same interval. The bridge cannot tell which.") +
                                " Check the grant in Revit's Advanced options."
                });
            }
            return rows.Count == 0 ? null : rows;
        }

        private static bool IsOnToken(JToken t) => t != null && t.Type == JTokenType.Boolean && (bool)t;
        // The stamp is an ISO date the JSON reader turns into a date token: it is compared by what it writes back, whatever its type.
        private static string StampOf(JObject o, string key) { JToken t = o?[key]; return t == null ? null : t.ToString(Newtonsoft.Json.Formatting.None); }

        /// <summary>Written only by the ribbon's owner dialog (SyncOnlyGrantCommand).</summary>
        public static bool TrySetSyncOnlyGrant(bool enabled, out string error)
        {
            return TryUpdate(o =>
            {
                o["allow_sync_only_grant"] = enabled;
                o["allow_sync_only_grant_changed_from_revit_at_utc"] = DateTimeOffset.UtcNow.ToString("O");
                return true;
            }, out error);
        }

        /// <summary>Written only by the ribbon's owner dialog (SyncCentralPermissionCommand).</summary>
        public static bool TrySetSyncWithCentralOwnerGrant(bool enabled, out string error)
        {
            return TryUpdate(o =>
            {
                o["sync_with_central_owner_granted"] = enabled;
                o["sync_with_central_owner_changed_from_revit_at_utc"] = DateTimeOffset.UtcNow.ToString("O");
                return true;
            }, out error);
        }

        /// <summary>
        /// Persist an owner-selected typed-tool permission rung. This is deliberately
        /// exposed only to the local Revit ribbon: an MCP request must never be able
        /// to make itself more capable. Python remains separately owner-gated.
        /// </summary>
        public static bool TrySetPermissionProfile(string profile, out string error)
        {
            error = null;
            if (profile != "read_only" && profile != "documentation" && profile != "safe_write" && profile != "full_write")
            {
                error = "Only read_only, documentation, safe_write and full_write may be selected from Revit. " +
                        "unsafe_code is an administrator-only profile.";
                return false;
            }

            return TryUpdate(o =>
            {
                o["permission_profile"] = profile;
                o["permission_profile_selected_from_revit_at_utc"] = DateTimeOffset.UtcNow.ToString("O");
                return true;
            }, out error);
        }

        /// <summary>
        /// MAY THIS CALL REACH OUTSIDE THE MODEL? The rung, asked directly.
        ///
        /// IsToolAllowed answers about a TOOL and is consulted once, for advertisement
        /// and for dispatch. This answers about the OPERATION a call has asked for, and
        /// is the second half of ToolEffect.ExternalSideEffectOnRequest: a handler
        /// carrying that effect calls this before it opens a destination, and refuses
        /// with the sentence returned here. Same two rungs full_write authorizes, decided
        /// in one place rather than restated in each handler.
        ///
        /// It is deliberately NOT about a tool name: the question is what this machine
        /// permits, and the answer must be the same for the next handler that asks.
        /// </summary>
        public static bool AllowsExternalSideEffect(out string reason)
        {
            string profile = PermissionProfile;
            if (profile == "full_write" || profile == "unsafe_code") { reason = null; return true; }
            reason = "permission_profile=" + profile + " in " + Path() + " does not authorize writing outside the " +
                     "model. Only full_write and unsafe_code do. Nothing was written, and this machine's owner is " +
                     "the only person who changes that file.";
            return false;
        }

        public static bool IsToolAllowed(CommandContract contract, out string reason)
            => IsToolAllowed(contract, out reason, ignoreToolPacks: false);

        /// <summary>
        /// The same decision from the file as ONE earlier read left it (INT-7 m-k): the profile, the pause, the lists, the packs and
        /// the Python grant all come from that photo, so a decision that also uses the photo's grants cannot straddle two moments.
        /// A photo without the file behind it (a hand-made one) reads the file now.
        /// </summary>
        public static bool IsToolAllowed(CommandContract contract, out string reason, SyncSettingsSnapshot photo)
            => IsToolAllowed(contract, out reason, ignoreToolPacks: false, photo: photo);

        /// <summary>
        /// The same admission decision with the tool-pack restriction lifted, and ONLY
        /// that one. Everything else - denied_tools, the allowlist, the pause, the
        /// permission profile, the Python grant - still applies.
        ///
        /// It exists for one purpose: measuring what the pack selection costs and saves
        /// (Protocol/DiscoveryCost.cs). A baseline that also lifted the permission
        /// profile would credit the packs with a saving the owner's read_only setting
        /// made, which is how a measured percentage becomes a flattering one.
        ///
        /// NOTHING DISPATCHES THROUGH THIS. It answers a question about the
        /// configuration; it never decides whether a call may run.
        /// </summary>
        public static bool IsToolAllowedIgnoringPacks(CommandContract contract, out string reason)
            => IsToolAllowed(contract, out reason, ignoreToolPacks: true);

        private static bool IsToolAllowed(CommandContract contract, out string reason, bool ignoreToolPacks, SyncSettingsSnapshot photo = null)
        {
            reason = null;
            if (contract == null) { reason = "Unknown tool contract."; return false; }
            // ONE read decides the whole admission (the pause, the lists, the packs, the profile and the Python grant), so the
            // profile and the reason given for it come from the same bytes (a second read could disagree with the first).
            JObject settings; FileState settingsState; string settingsFailure;
            if (photo != null && photo.Raw != null) { settings = photo.Raw; settingsState = photo.State; settingsFailure = photo.Failure; }
            else settings = Read(out settingsState, out settingsFailure);
            if (settingsState != FileState.Malformed && settings?["mcp_paused"]?.Type == JTokenType.Boolean &&
                (bool)settings["mcp_paused"] && contract.Name != "horizun_health")
            {
                reason = contract.Name + " is hidden/refused because MCP is paused by the local Revit owner. " +
                         "Only horizun_health remains available to report that state; resume from the Revit ribbon.";
                return false;
            }
            string fellClosed = settingsState == FileState.Malformed
                ? " (" + Path() + " did not decide this: " + settingsFailure + ". An unreadable or malformed " +
                  "choice never elevates, so read_only applies whatever the file says; retry once the file reads.)"
                : "";
            HashSet<string> denied = Strings(settings?["denied_tools"] as JArray);
            HashSet<string> allowed = Strings(settings?["allowed_tools"] as JArray);
            if (denied.Contains(contract.Name))
            { reason = contract.Name + " is disabled by denied_tools in " + Path() + "."; return false; }
            if (allowed.Count > 0 && !allowed.Contains(contract.Name))
            { reason = contract.Name + " is outside the allowed_tools allowlist in " + Path() + "."; return false; }

            // ---- tool packs: which tools this SESSION is about. -------------------
            // Enforced here, in the one place both advertisement and dispatch already
            // consult, so hidden means unreachable on every path - sync, async, submit
            // and execute_plan's children alike. Core tools never fall to this check;
            // ActivePackResolution welds them on even over a malformed selection.
            if (!ignoreToolPacks)
            {
                ToolPacks.Resolution packs = ActivePackResolution(settings);
                if (packs.Restricting && !packs.Tools().Contains(contract.Name))
                { reason = ToolPacks.HiddenReason(contract.Name, packs); return false; }
            }

            string profile = ProfileFrom(settings, settingsState);
            JToken persistentUiToken = settings?["execute_python_ui_granted"];
            bool persistentUiGrant = persistentUiToken != null &&
                                     persistentUiToken.Type == JTokenType.Boolean &&
                                     (bool)persistentUiToken;
            bool temporaryUiGrant = settingsState != FileState.Malformed &&
                                    TemporaryExecutePythonGrant(settings, DateTimeOffset.UtcNow, out DateTimeOffset _);
            bool humanPythonGrant = contract.Name == "horizun_execute_python" &&
                                    profile != "read_only" && profile != "documentation" &&
                                    (persistentUiGrant || temporaryUiGrant);

            // ExternalSideEffect is consulted by BOTH restrictive profiles, and that is
            // the fix rather than a detail. The classification exists precisely to mean
            // "this reaches outside the model", and neither profile used to ask about it
            // - so every tool carrying it was admitted by both. horizun_excel_write_rows
            // is one: a machine set to read_only refused to move a wall and then rewrote
            // a workbook on disk. Deciding on the ENUM rather than on a list of names is
            // what keeps the next externally-effecting tool from repeating it.
            // horizun_code_check is classified MutatingUnlessDryRun for its ONE write
            // (operation=travel_distance with travel.create_paths), but its check and its
            // measurement write nothing. Hiding it from read_only took a pure read away, so it
            // stays listed and the command itself refuses create_paths under read_only.
            bool writesOnlyOnRequest = contract.Name == "horizun_code_check";
            if (!humanPythonGrant && profile == "read_only" && !writesOnlyOnRequest &&
                (contract.Effect == ToolEffect.Mutating || contract.Effect == ToolEffect.MutatingUnlessDryRun ||
                 contract.Effect == ToolEffect.DocumentSession || contract.Effect == ToolEffect.ExternalSideEffect))
            {
                reason = contract.Name + " is hidden/refused by permission_profile=read_only in " + Path() +
                         ": read_only changes nothing - not the model, not the document session, and nothing " +
                         "written outside it." + fellClosed;
                return false;
            }
            if (profile == "documentation" && !DocumentationAdmits(contract))
            {
                reason = contract.Name + " is hidden/refused by permission_profile=documentation in " + Path() +
                         ": documentation permits reads, views, annotation, sheets, revisions, PDF and one-view image export and the " +
                         "local save, and changes no modelled element." + fellClosed;
                return false;
            }
            if (!humanPythonGrant && profile == "safe_write" &&
                (contract.Effect == ToolEffect.DocumentSession || contract.Effect == ToolEffect.ExternalSideEffect ||
                 // Named as well as classified: these write outside the model while being
                 // classified MutatingUnlessDryRun, so the effect alone does not catch them.
                 // horizun_save_document and horizun_export are NOT here (2026-10-18): they
                 // are admitted, and refuse per call what safe_write does not authorize -
                 // save_document anything but save_local/relinquish_own, export anything
                 // but format=pdf or a one-view format=image (OperationPermission, asked by each command).
                 contract.Name == "horizun_open_document" ||
                 contract.Name == "horizun_relinquish_all" ||
                 contract.Name == "horizun_power_bi_push"))
            {
                reason = contract.Name + " changes the Revit document session or writes external files and is " +
                         "hidden/refused by permission_profile=safe_write in " + Path() +
                         ". safe_write permits typed writes INSIDE the document only. Use full_write only on " +
                         "machines authorized for those side effects.";
                return false;
            }
            if (contract.Name == "horizun_execute_python" &&
                (!humanPythonGrant &&
                 (profile != "unsafe_code" || !ExecutePythonEnabledFrom(settings, settingsState))))
            {
                reason = "horizun_execute_python requires explicit permission_profile=unsafe_code and " +
                         "enable_execute_python=true in " + Path() + ", OR a persistent owner grant made from " +
                         "Revit's Python ON/OFF button. It is OFF on a fresh install. Only the machine's owner " +
                         "may grant that privilege, and it remains OFF until that owner does so." + fellClosed;
                return false;
            }
            return true;
        }

        /// <summary>
        /// Tools a permission_profile=documentation machine admits. By MEMBERSHIP of the
        /// "documentation" toolset rather than by a list of names, so a documentation tool
        /// added later is admitted without touching this file - minus two exclusions that
        /// change model-wide definitions (styles, phases), minus anything that opens a
        /// document session or writes external artefacts by its effect. The named extras
        /// are the ones whose writes are narrowed PER CALL by OperationPermission:
        /// capture_view (a PNG of a view), undo, and delete/write_params (only views,
        /// sheets, view-owned and annotation elements).
        /// </summary>
        public static bool DocumentationAdmits(CommandContract contract)
        {
            if (contract == null || contract.Name == "horizun_execute_python") return false;
            if (contract.Effect == ToolEffect.ReadOnly || contract.Effect == ToolEffect.HostState ||
                contract.Effect == ToolEffect.ExternalSideEffectOnRequest) return true;
            if (DocumentationNamed.Contains(contract.Name)) return true;
            if (DocumentationExcluded.Contains(contract.Name)) return false;
            if (contract.Effect == ToolEffect.DocumentSession || contract.Effect == ToolEffect.ExternalSideEffect) return false;
            return ToolsetCatalog.Of(contract.Name).Contains("documentation", StringComparer.Ordinal);
        }

        private static readonly HashSet<string> DocumentationNamed = new HashSet<string>(StringComparer.Ordinal)
        {
            "horizun_capture_view", "horizun_save_document", "horizun_export", "horizun_undo",
            "horizun_delete_verified", "horizun_write_params_verified"
        };

        private static readonly HashSet<string> DocumentationExcluded = new HashSet<string>(StringComparer.Ordinal)
        {
            "horizun_manage_styles", "horizun_manage_phases"
        };

        /// <summary>
        /// The resolved tool-pack selection, from the environment override or the
        /// settings file. Public so health can publish the profile a session is
        /// actually running and the UI can show it; the OPTIONAL settings argument
        /// spares a second file read on the IsToolAllowed hot path.
        /// </summary>
        /// <remarks>
        /// TWO SPELLINGS, ONE SELECTION. HORIZUN_TOOLSETS and the "toolsets" key are the
        /// names MCP clients use for the same thing; each is read only when its pack
        /// spelling is absent, so a machine that already configured tool_packs keeps
        /// exactly the behaviour it had, and the resolution says which spelling decided.
        /// </remarks>
        public static ToolPacks.Resolution ActivePackResolution(JObject settings = null)
        {
            string envName = ToolPacks.EnvironmentOverride;
            string env = null;
            try
            {
                env = Environment.GetEnvironmentVariable(ToolPacks.EnvironmentOverride);
                if (string.IsNullOrWhiteSpace(env))
                {
                    env = Environment.GetEnvironmentVariable(ToolPacks.ToolsetsEnvironmentVariable);
                    envName = ToolPacks.ToolsetsEnvironmentVariable;
                }
            }
            catch { env = null; }

            FileState state;
            JObject o = settings ?? Read(out state);
            string key = ToolPacks.SettingsKey;
            JToken raw = o?[ToolPacks.SettingsKey];
            if (raw == null)
            {
                raw = o?[ToolPacks.ToolsetsSettingsKey];
                key = ToolPacks.ToolsetsSettingsKey;
            }

            ToolPacks.Resolution r;
            if (raw == null) r = ToolPacks.Resolve(env, null, settingsValueMalformed: false);
            else
            {
                var array = raw as JArray;
                r = array == null || array.Any(t => t.Type != JTokenType.String)
                    ? ToolPacks.Resolve(env, null, settingsValueMalformed: true)
                    : ToolPacks.Resolve(env, array.Select(t => (string)t), settingsValueMalformed: false);
            }
            if (!string.IsNullOrWhiteSpace(env)) r.SourceName = envName;
            else if (raw != null) r.SourceName = key;
            return r;
        }

        /// <summary>
        /// Persist the user's pack selection. Null or empty restores the default
        /// (every pack). The same guarded read-modify-write as every settings change;
        /// the ToolListMonitor sees the file move and compatible clients get
        /// tools/list_changed without a restart.
        /// </summary>
        public static bool TrySetToolPacks(IEnumerable<string> packs, out string error)
        {
            List<string> list = packs?.Select(p => (p ?? "").Trim().ToLowerInvariant())
                                      .Where(p => p.Length > 0).Distinct().ToList();
            if (list != null && list.Count > 0)
            {
                // Refuse garbage BEFORE writing it: a settings file with an unknown pack
                // falls closed to core-only, which is safe and also nothing the user
                // asked to happen.
                var unknown = list.Where(p => p != ToolPacks.AllToken &&
                                              !ToolPacks.KnownPacks.Contains(p)).ToList();
                if (unknown.Count > 0)
                {
                    error = "unknown pack name(s): " + string.Join(", ", unknown) + ". Known: " +
                            string.Join(", ", ToolPacks.KnownPacks.OrderBy(k => k, StringComparer.Ordinal)) +
                            ". Nothing was changed.";
                    return false;
                }
            }
            return TryUpdate(o =>
            {
                if (list == null || list.Count == 0 ||
                    (list.Count == 1 && list[0] == ToolPacks.AllToken)) o.Remove(ToolPacks.SettingsKey);
                else o[ToolPacks.SettingsKey] = new JArray(list);
                return true;
            }, out error);
        }

        /// <summary>
        /// The sentence to show a caller who asked for a capability this machine has
        /// switched off: what is off, why it is off, and where the owner turns it back on.
        /// </summary>
        public static string ExecutePythonRefusal()
        {
            return "horizun_execute_python is DISABLED ON THIS MACHINE. This is the safe default: arbitrary " +
                   "code requires explicit owner consent in " + Path() + ". Respect the choice: do not edit the " +
                   "file yourself. If the MACHINE'S OWNER needs the developer escape hatch, they can grant a " +
                   "persistent opt-in from Revit's Python ON/OFF button or with " +
                   "scripts/enable-execute-python.ps1; -Disable revokes it. " +
                   "The add-in re-reads settings on every call, and the server announces a standard " +
                   "tools/list_changed notification. If a client does not implement that notification, restart " +
                   "it once for the tool to appear. Meanwhile, use typed commands: they cover most operations " +
                   "and verify their work.";
        }

        /// <summary>
        /// Grant arbitrary Python from an explicit human action inside Revit. This is a
        /// durable owner switch and remains enabled until that same Windows user revokes
        /// it. It authorizes only execute_python; it deliberately does not elevate the
        /// underlying profile or enable other external/session tools. The old expiring UI
        /// key is removed so there is one unambiguous source of truth after the decision.
        /// </summary>
        public static bool TryGrantExecutePythonPersistently(out string error)
        {
            return TryUpdate(o =>
            {
                o["execute_python_ui_granted"] = true;
                o["execute_python_ui_granted_at_utc"] = DateTimeOffset.UtcNow.ToString("O");
                o.Remove("execute_python_ui_grant_until_utc");
                return true;
            }, out error);
        }

        /// <summary>
        /// The Revit OFF button is an emergency stop, not merely expiry cleanup: it
        /// revokes both the temporary UI grant and any durable enable flag. It leaves
        /// the permission profile unchanged because an administrator may still need
        /// full_write for typed external operations.
        /// </summary>
        public static bool TryRevokeExecutePython(out string error)
        {
            return TryUpdate(o =>
            {
                o["enable_execute_python"] = false;
                o.Remove("execute_python_ui_granted");
                o.Remove("execute_python_ui_grant_until_utc");
                o.Remove("execute_python_ui_granted_at_utc");
                return true;
            }, out error);
        }

        /// <summary>
        /// One raw string setting, for callers that take an injected reader (the receipt
        /// ledger's retention). Guarded like every read of a file the user owns.
        /// </summary>
        public static string RawValue(string key)
        {
            try { return Read()?.Value<string>(key); } catch { return null; }
        }

        /// <summary>
        /// Raw retention setting with malformed-file provenance preserved. Returning
        /// null means the owner never selected this key and bounded defaults may apply;
        /// an unreadable settings file returns a deliberately invalid value so retention
        /// fails closed instead of mistaking corruption for absence and deleting data.
        /// </summary>
        public static string RetentionValue(string key)
        {
            FileState state;
            JObject o = Read(out state);
            return state == FileState.Malformed ? "invalid-settings-file" : o?.Value<string>(key);
        }

        /// <summary>
        /// Three states, because two of them look identical and must not act identical:
        /// an ABSENT file means "the owner never chose" and the defaults apply, while a
        /// MALFORMED file may be a corrupted explicit choice and everything falls closed.
        /// </summary>
        internal enum FileState { Absent, Readable, Malformed }

        private static JObject Read(out FileState state) => Read(out state, out string ignored);

        // A SHARING VIOLATION IS NOT A CHOICE. MEASURED 2026-09-26: a call refused as
        // "permission_profile=read_only in settings.json" while the file said unsafe_code
        // before and after - the read had failed for an instant, fell closed (right), and
        // the refusal then claimed the file SAID read_only (wrong). An I/O failure is
        // retried briefly; one that persists still falls closed, and 'failure' names it so
        // no refusal attributes to the owner a setting the owner never wrote.
        private static JObject Read(out FileState state, out string failure)
        {
            failure = null;
            string p = Path();
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    if (!File.Exists(p)) { state = FileState.Absent; return new JObject(); }
                    string text = File.ReadAllText(p);
                    try { JObject o = JObject.Parse(text); state = FileState.Readable; return o; }
                    catch (Exception parse)
                    {
                        state = FileState.Malformed;
                        failure = "it is not valid JSON (" + parse.Message + ")";
                        return new JObject();
                    }
                }
                catch (Exception io) when (io is IOException || io is UnauthorizedAccessException)
                {
                    if (attempt < 5) { System.Threading.Thread.Sleep(40); continue; }
                    state = FileState.Malformed;
                    failure = "it could not be read after " + attempt + " attempts (" + io.GetType().Name + ": " + io.Message + ")";
                    return new JObject();
                }
                catch (Exception other)
                {
                    state = FileState.Malformed;
                    failure = "it could not be read (" + other.GetType().Name + ": " + other.Message + ")";
                    return new JObject();
                }
            }
        }

        /// <summary>The profile one read of the file decides, and why when it fell closed.</summary>
        private static string ProfileFrom(JObject o, FileState state)
        {
            if (state == FileState.Malformed) return "read_only"; // an unreadable choice never elevates
            string p = o?.Value<string>("permission_profile");
            if (string.IsNullOrWhiteSpace(p)) return "safe_write";
            p = p.ToLowerInvariant();
            return p == "read_only" || p == "documentation" || p == "safe_write" || p == "full_write" || p == "unsafe_code"
                ? p : "read_only"; // malformed privilege never elevates
        }

        private static JObject Read()
        {
            FileState ignored;
            return Read(out ignored);
        }

        private static bool TemporaryExecutePythonGrant(
            JObject settings, DateTimeOffset nowUtc, out DateTimeOffset untilUtc)
        {
            untilUtc = default(DateTimeOffset);
            JToken t = settings?["execute_python_ui_grant_until_utc"];
            if (t == null) return false;
            if (t.Type == JTokenType.Date)
            {
                object value = ((JValue)t).Value;
                if (value is DateTimeOffset dto) untilUtc = dto;
                else if (value is DateTime dt)
                {
                    if (dt.Kind == DateTimeKind.Unspecified)
                        dt = DateTime.SpecifyKind(dt, DateTimeKind.Utc);
                    untilUtc = new DateTimeOffset(dt.ToUniversalTime());
                }
                else return false;
                return untilUtc.ToUniversalTime() > nowUtc.ToUniversalTime();
            }
            if (t.Type != JTokenType.String) return false;
            if (!DateTimeOffset.TryParse(
                    (string)t,
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal |
                    System.Globalization.DateTimeStyles.AdjustToUniversal,
                    out untilUtc)) return false;
            return untilUtc > nowUtc;
        }

        private static bool TryUpdate(Func<JObject, bool> update, out string error)
        {
            error = null;
            lock (WriteLock)
            {
                using (var mutex = new Mutex(false, "Local\\Horizun.Revit.Settings.V1"))
                {
                    bool held = false;
                    try
                    {
                        try { held = mutex.WaitOne(TimeSpan.FromSeconds(15)); }
                        catch (AbandonedMutexException) { held = true; }
                        if (!held)
                        {
                            error = "Timed out waiting for another Revit process to finish updating settings.json. Nothing changed.";
                            return false;
                        }
                        return TryUpdateLocked(update, out error);
                    }
                    catch (Exception ex)
                    {
                        error = "Could not acquire the cross-process settings lock: " + ex.Message;
                        return false;
                    }
                    finally { if (held) try { mutex.ReleaseMutex(); } catch { } }
                }
            }
        }

        private static bool TryUpdateLocked(Func<JObject, bool> update, out string error)
        {
            error = null;
            string path = Path();
            string temp = null;
            try
            {
                JObject settings;
                string originalRaw = null;
                bool originallyExisted = File.Exists(path);
                if (originallyExisted)
                {
                    originalRaw = File.ReadAllText(path);
                    try { settings = JObject.Parse(originalRaw); }
                    catch
                    {
                        error = "settings.json is malformed. It remains fail-closed and was not overwritten: " + path;
                        return false;
                    }
                }
                else settings = new JObject();

                if (!update(settings))
                {
                    error = "The requested settings update was refused.";
                    return false;
                }

                string directory = System.IO.Path.GetDirectoryName(path);
                if (string.IsNullOrWhiteSpace(directory))
                {
                    error = "The settings path has no parent directory: " + path;
                    return false;
                }
                Directory.CreateDirectory(directory);
                temp = System.IO.Path.Combine(directory, ".settings-" + Guid.NewGuid().ToString("N") + ".tmp");
                File.WriteAllText(temp, settings.ToString(Newtonsoft.Json.Formatting.Indented), new UTF8Encoding(false));

                // Non-cooperating editors do not take our named mutex. Refuse their
                // concurrent change instead of restoring an older privilege snapshot.
                bool existsNow = File.Exists(path);
                if (existsNow != originallyExisted ||
                    (existsNow && !string.Equals(File.ReadAllText(path), originalRaw, StringComparison.Ordinal)))
                {
                    error = "settings.json changed while the Revit permission dialog was open. Nothing was overwritten; try again.";
                    return false;
                }

                if (existsNow)
                {
                    string backup = path + ".horizun-ui-bak-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff") +
                                    "-" + Guid.NewGuid().ToString("N");
                    File.Replace(temp, path, backup);
                    PruneUiBackups(directory, System.IO.Path.GetFileName(path));
                }
                else File.Move(temp, path);
                temp = null;
                return true;
            }
            catch (Exception ex)
            {
                error = "Could not update " + path + ": " + ex.Message;
                return false;
            }
            finally
            {
                if (temp != null) try { File.Delete(temp); } catch { }
            }
        }

        private static void PruneUiBackups(string directory, string settingsFileName)
        {
            try
            {
                var backups = new DirectoryInfo(directory)
                    .GetFiles(settingsFileName + ".horizun-ui-bak-*");
                Array.Sort(backups, (a, b) => b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc));
                for (int i = 3; i < backups.Length; i++)
                    try { backups[i].Delete(); } catch { }
            }
            catch { /* backup retention must never turn a successful revoke into failure */ }
        }

        private static HashSet<string> Strings(JArray a)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (a != null) foreach (JToken t in a)
                if (t.Type == JTokenType.String && !string.IsNullOrWhiteSpace((string)t)) result.Add((string)t);
            return result;
        }
    }
}
