// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// INT-7: the rest of the findings of the independent review of INT-6 that a Revit-free test can hold - the ones whose fix adds API (the
// ones that could be written against 0b41ea8c are in Int7RedTests.cs). Behaviour first: where a text of the product has to match a
// document, the test ties the CODE's text to the document's, it does not look for a word.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Horizun.Contracts;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    internal static class Int7Repo
    {
        internal static string Root()
        {
            string dir = AppContext.BaseDirectory;
            while (dir != null && !File.Exists(Path.Combine(dir, "AGENTS.md"))) dir = Path.GetDirectoryName(dir);
            return dir ?? throw new InvalidOperationException("repository root not found");
        }

        internal static string Read(params string[] parts) => File.ReadAllText(Path.Combine(new[] { Root() }.Concat(parts).ToArray()));

        /// <summary>AGENTS.md has an English half and a Spanish half; the Spanish one starts at its own heading.</summary>
        internal static void AgentsHalves(out string english, out string spanish)
        {
            string all = Read("AGENTS.md");
            int es = all.IndexOf("\n## Español", StringComparison.Ordinal);
            Assert.True(es > 0, "AGENTS.md has no Spanish half");
            english = all.Substring(0, es);
            spanish = all.Substring(es);
        }
    }

    // ---- m-4: what a script run did to the owner's sync grants is REPORTED (settings_changed), never reverted ----------------

    public class SettingsChangedTests
    {
        private static void WithRoot(Action action)
        {
            using (new EnvGuard())
            {
                string temp = Path.Combine(Path.GetTempPath(), "hz-int7-changed-" + Guid.NewGuid().ToString("N"));
                EnvGuard.Set(HorizunPaths.RootOverrideVariable, temp);
                try { Directory.CreateDirectory(temp); action(); }
                finally { try { Directory.Delete(temp, true); } catch { } }
            }
        }

        private static void Edit(Action<JObject> change)
        {
            JObject o = File.Exists(HorizunPaths.SettingsPath()) ? JObject.Parse(File.ReadAllText(HorizunPaths.SettingsPath())) : new JObject();
            change(o);
            File.WriteAllText(HorizunPaths.SettingsPath(), o.ToString());
        }

        [Fact]
        public void A_run_that_changed_nothing_says_nothing()
        {
            WithRoot(() =>
            {
                Assert.True(Settings.TrySetSyncOnlyGrant(true, out _));
                Settings.SyncGrantPhoto before = Settings.PhotoSyncGrants();
                Edit(o => o["permission_profile"] = "unsafe_code");           // not a grant: not this report
                Assert.Null(Settings.SyncGrantsChangedSince(before));
            });
        }

        [Fact]
        public void A_grant_a_script_turned_on_is_reported_and_left_exactly_as_the_script_wrote_it()
        {
            WithRoot(() =>
            {
                Assert.True(Settings.TrySetSyncOnlyGrant(false, out _));
                Settings.SyncGrantPhoto before = Settings.PhotoSyncGrants();
                Edit(o => o["sync_with_central_owner_granted"] = true);       // the script
                JArray changed = Settings.SyncGrantsChangedSince(before);
                Assert.NotNull(changed);
                JObject row = (JObject)Assert.Single(changed);
                Assert.Equal("sync_with_central_owner_granted", (string)row["key"]);
                Assert.False((bool)row["before"]);
                Assert.True((bool)row["after"]);
                Assert.False((bool)row["reverted"]);
                Assert.False((bool)row["owner_writer_stamp_changed"]);
                Assert.Equal("the_script_or_another_process_of_this_user", (string)row["possible_cause"]);
                Assert.Contains("may have written it", (string)row["means"]);
                Assert.True(Settings.SyncWithCentralOwnerEnabled);            // nothing was put back
            });
        }

        [Fact]
        public void A_grant_a_script_turned_off_is_reported_too()
        {
            WithRoot(() =>
            {
                Assert.True(Settings.TrySetSyncWithCentralOwnerGrant(true, out _));
                Settings.SyncGrantPhoto before = Settings.PhotoSyncGrants();
                Edit(o => o["sync_with_central_owner_granted"] = false);
                JObject row = (JObject)Assert.Single(Settings.SyncGrantsChangedSince(before));
                Assert.True((bool)row["before"]);
                Assert.False((bool)row["after"]);
                Assert.False(Settings.SyncWithCentralOwnerEnabled);
            });
        }

        [Fact]
        public void When_the_owners_writer_stamped_the_key_during_the_run_the_report_does_not_blame_the_script()
        {
            WithRoot(() =>
            {
                Assert.True(Settings.TrySetSyncOnlyGrant(false, out _));
                Settings.SyncGrantPhoto before = Settings.PhotoSyncGrants();
                System.Threading.Thread.Sleep(5);
                Assert.True(Settings.TrySetSyncOnlyGrant(true, out _));       // another Revit instance's ribbon, in the same interval
                JObject row = (JObject)Assert.Single(Settings.SyncGrantsChangedSince(before));
                Assert.True((bool)row["owner_writer_stamp_changed"]);
                Assert.Equal("owner_writer_of_another_revit_instance_or_the_owner", (string)row["possible_cause"]);
                Assert.Contains("does not say the script did", (string)row["means"]);
                Assert.False((bool)row["reverted"]);
            });
        }

        [Fact]
        public void A_settings_file_that_cannot_be_read_after_the_run_is_reported_not_ignored()
        {
            WithRoot(() =>
            {
                Assert.True(Settings.TrySetSyncOnlyGrant(true, out _));
                Settings.SyncGrantPhoto before = Settings.PhotoSyncGrants();
                File.WriteAllText(HorizunPaths.SettingsPath(), "{ not json");
                JObject row = (JObject)Assert.Single(Settings.SyncGrantsChangedSince(before));
                Assert.Equal("unreadable_after_the_run", (string)row["problem"]);
                Assert.False((bool)row["reverted"]);
            });
        }

        [Fact]
        public void When_the_file_could_not_be_read_before_nothing_is_claimed()
        {
            WithRoot(() =>
            {
                File.WriteAllText(HorizunPaths.SettingsPath(), "{ not json");
                Settings.SyncGrantPhoto before = Settings.PhotoSyncGrants();
                File.WriteAllText(HorizunPaths.SettingsPath(), "{\"allow_sync_only_grant\":true}");
                Assert.Null(Settings.SyncGrantsChangedSince(before));
            });
        }

        [Fact]
        public void The_absent_file_and_an_empty_one_are_the_same_off()
        {
            Assert.Null(Settings.CompareSyncGrants(new JObject(), new JObject { ["allow_sync_only_grant"] = false }));
            Assert.Null(Settings.CompareSyncGrants(new JObject { ["allow_sync_only_grant"] = "yes" }, new JObject()));   // not a boolean true: off both times
            Assert.NotNull(Settings.CompareSyncGrants(new JObject(), new JObject { ["allow_sync_only_grant"] = true }));
        }

        [Fact]
        public void Every_row_names_a_sensitive_key_and_the_documents_list_all_of_them_in_both_languages()
        {
            JArray rows = Settings.CompareSyncGrants(new JObject(), new JObject { ["allow_sync_only_grant"] = true, ["sync_with_central_owner_granted"] = true });
            foreach (JToken r in rows) Assert.Contains((string)r["key"], Settings.SensitiveKeys);
            Int7Repo.AgentsHalves(out string en, out string es);
            foreach (string key in Settings.SensitiveKeys)
            {
                Assert.Contains("`" + key + "`", en);
                Assert.Contains("`" + key + "`", es);
            }
            Assert.Contains("`" + Settings.SettingsChangedField + ": [...]`", en);
            Assert.Contains("`" + Settings.SettingsChangedField + ": [...]`", es);
            Assert.Contains("`" + Settings.SettingsChangedField + "`", Int7Repo.Read("docs", "TOOLS-EXTENDED.md"));
        }

        [Fact]
        public void The_documents_say_who_can_edit_the_file_and_what_really_protects_it()
        {
            Int7Repo.AgentsHalves(out string en, out string es);
            string enFlat = Regex.Replace(en, @"\s+", " "), esFlat = Regex.Replace(es, @"\s+", " ");
            Assert.Contains("Any process that runs with the user's rights can edit that file", enFlat);
            Assert.Contains("the permission profile and the owner's decision NOT to grant `unsafe_code`", enFlat);
            Assert.Contains("Cualquier proceso que corra con los permisos del usuario puede editar ese archivo", esFlat);
            Assert.Contains("el perfil de permisos y la decisión del dueño de NO conceder `unsafe_code`", esFlat);
            foreach (string gone in new[] { "owner-seal.key", "settings_guard", "owner_grants_rejected", "owner_seals" })
            {
                Assert.DoesNotContain(gone, Int7Repo.Read("AGENTS.md"));
                Assert.DoesNotContain(gone, Int7Repo.Read("docs", "TOOLS-EXTENDED.md"));
            }
        }
    }

    // ---- m-k: one photo of the settings per call ---------------------------------------------------------------------------

    public class OneSettingsPhotoTests
    {
        private static void WithRoot(Action action)
        {
            using (new EnvGuard())
            {
                string temp = Path.Combine(Path.GetTempPath(), "hz-int7-photo-" + Guid.NewGuid().ToString("N"));
                EnvGuard.Set(HorizunPaths.RootOverrideVariable, temp);
                try { Directory.CreateDirectory(temp); action(); }
                finally { try { Directory.Delete(temp, true); } catch { } }
            }
        }

        [Fact]
        public void The_photo_holds_everything_a_sync_decision_uses_and_does_not_move_when_the_file_does()
        {
            WithRoot(() =>
            {
                File.WriteAllText(HorizunPaths.SettingsPath(),
                    "{\"permission_profile\":\"full_write\",\"sync_with_central_owner_granted\":true,\"allow_sync_only_grant\":false,\"force_read_only_on_workshared\":true}");
                Settings.SyncSettingsSnapshot photo = Settings.ReadSyncSettings();
                File.WriteAllText(HorizunPaths.SettingsPath(),
                    "{\"permission_profile\":\"read_only\",\"sync_with_central_owner_granted\":false,\"allow_sync_only_grant\":true,\"force_read_only_on_workshared\":false}");
                Assert.Equal("full_write", photo.Profile);
                Assert.True(photo.OwnerSyncGrant);
                Assert.False(photo.SyncOnlyFlag);
                Assert.True(photo.ForceReadOnly);
                Settings.SyncSettingsSnapshot now = Settings.ReadSyncSettings();
                Assert.Equal("read_only", now.Profile);
                Assert.False(now.OwnerSyncGrant);
                Assert.True(now.SyncOnlyFlag);
                Assert.False(now.ForceReadOnly);
            });
        }

        [Fact]
        public void The_admission_of_a_tool_is_decided_from_the_photo_not_from_a_second_read()
        {
            WithRoot(() =>
            {
                CommandContract save = Contract.Find("horizun_save_document");
                CommandContract python = Contract.Find("horizun_execute_python");
                CommandContract verify = Contract.Find("horizun_verify_changes");
                File.WriteAllText(HorizunPaths.SettingsPath(), "{\"permission_profile\":\"unsafe_code\",\"enable_execute_python\":true}");
                Settings.SyncSettingsSnapshot photo = Settings.ReadSyncSettings();
                // The file changes after the photo: paused, read_only, Python off.
                File.WriteAllText(HorizunPaths.SettingsPath(), "{\"permission_profile\":\"read_only\",\"mcp_paused\":true}");
                Assert.True(Settings.IsToolAllowed(python, out string r1, photo), r1);
                Assert.True(Settings.IsToolAllowed(save, out string r2, photo), r2);
                Assert.True(Settings.IsToolAllowed(verify, out string r3, photo), r3);
                Assert.False(Settings.IsToolAllowed(python, out string fresh));    // a fresh read sees the paused machine
                Assert.Contains("paused", fresh);
            });
        }

        [Fact]
        public void A_photo_of_a_paused_machine_refuses_every_tool_but_health_and_says_why()
        {
            WithRoot(() =>
            {
                File.WriteAllText(HorizunPaths.SettingsPath(), "{\"mcp_paused\":true}");
                Settings.SyncSettingsSnapshot photo = Settings.ReadSyncSettings();
                File.WriteAllText(HorizunPaths.SettingsPath(), "{}");
                Assert.False(Settings.IsToolAllowed(Contract.Find("horizun_save_document"), out string reason, photo));
                Assert.Contains("paused", reason);
                Assert.True(Settings.IsToolAllowed(Contract.Find("horizun_health"), out _, photo));
            });
        }

        [Fact]
        public void A_photo_of_an_unreadable_file_falls_closed_everywhere()
        {
            WithRoot(() =>
            {
                File.WriteAllText(HorizunPaths.SettingsPath(), "{ not json");
                Settings.SyncSettingsSnapshot photo = Settings.ReadSyncSettings();
                Assert.Equal("read_only", photo.Profile);
                Assert.False(photo.OwnerSyncGrant);
                Assert.False(photo.SyncOnlyFlag);
                Assert.False(Settings.IsToolAllowed(Contract.Find("horizun_save_document"), out string reason, photo));
                Assert.Contains("read_only", reason);
            });
        }

        [Fact]
        public void A_hand_made_photo_without_the_file_behind_it_reads_the_file_now()
        {
            WithRoot(() =>
            {
                File.WriteAllText(HorizunPaths.SettingsPath(), "{\"permission_profile\":\"safe_write\"}");
                var handMade = new Settings.SyncSettingsSnapshot { Profile = "unsafe_code" };
                Assert.False(Settings.IsToolAllowed(Contract.Find("horizun_execute_python"), out _, handMade));
            });
        }

        [Fact]
        public void The_default_photo_is_the_closed_one()
        {
            var s = new Settings.SyncSettingsSnapshot();
            Assert.Equal("read_only", s.Profile);
            Assert.False(s.OwnerSyncGrant);
            Assert.False(s.SyncOnlyFlag);
            Assert.True(s.ForceReadOnly);
            Assert.False(SyncOnlyGrantRules.Decide(s).Authorised);
        }
    }

    // ---- m-d: no person and no team is named in the product ---------------------------------------------------------------

    /// <summary>
    /// The names that must not appear in the product, held WITHOUT writing them here. Two sources, so the guard cannot be the file that
    /// publishes the names: (1) SHA-256 of the normalised (trimmed, lower-case) term, compared against every word of the text - always
    /// runs, needs nothing outside the repository; (2) the owner's wordlist outside the repository
    /// (%USERPROFILE%\.horizun\sensitive-terms.txt, the file scan-sensitive.ps1 reads) - its test is SKIPPED with a stated reason when the
    /// file is absent, never passed as if it had looked.
    /// </summary>
    internal static class InternalNames
    {
        // SHA-256 of the lower-case term. Add a hash, never the term.
        internal static readonly HashSet<string> TermHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "26079e41910bcde04be636fbeecc9045379882b5ad3fe7f70b762436c6d98055",
            "7c82602500857aa6ed0cf38c4c3e4ec645bdcaa82c00b9155eb08be100c778a9",
        };

        private static readonly Regex Word = new Regex(@"[\p{L}\p{N}_]+", RegexOptions.Compiled);

        internal static string Hash(string term)
        {
            using (var sha = SHA256.Create())
                return string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(term.Trim().ToLowerInvariant())).Select(b => b.ToString("x2")));
        }

        /// <summary>The first word of the text whose hash is on the list, with a window of the text around it; null when none.</summary>
        internal static string FirstHashHit(string text, HashSet<string> hashes = null)
        {
            hashes = hashes ?? TermHashes;
            foreach (Match w in Word.Matches(text))
                if (hashes.Contains(Hash(w.Value)))
                    return "'" + w.Value + "' near: " + text.Substring(Math.Max(0, w.Index - 60), Math.Min(120, text.Length - Math.Max(0, w.Index - 60)));
            return null;
        }

        internal static string WordlistPath()
        {
            // Same default as scan-sensitive.ps1 (-TermsFile overrides it there; HORIZUN_SENSITIVE_TERMS does here, to prove the skip).
            string over = Environment.GetEnvironmentVariable("HORIZUN_SENSITIVE_TERMS");
            if (!string.IsNullOrWhiteSpace(over)) return over;
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrWhiteSpace(home)) home = Environment.GetEnvironmentVariable("USERPROFILE");
            if (string.IsNullOrWhiteSpace(home)) home = Environment.GetEnvironmentVariable("HOME");
            return string.IsNullOrWhiteSpace(home) ? null : Path.Combine(home, ".horizun", "sensitive-terms.txt");
        }

        internal static IReadOnlyList<string> WordlistTerms()
        {
            string path = WordlistPath();
            if (path == null || !File.Exists(path)) return new string[0];
            return File.ReadAllLines(path).Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith("#")).ToArray();
        }

        /// <summary>The first term of the wordlist found in the text (case-insensitive, on word boundaries); null when none. The term is not echoed.</summary>
        internal static string FirstWordlistHit(string text, IEnumerable<string> terms)
        {
            foreach (string t in terms)
            {
                Match m = Regex.Match(text, @"(?<![\p{L}\p{N}_])" + Regex.Escape(t) + @"(?![\p{L}\p{N}_])", RegexOptions.IgnoreCase);
                if (m.Success)
                    return "a wordlist term (#" + Hash(t).Substring(0, 8) + ") near: " + text.Substring(Math.Max(0, m.Index - 60), Math.Min(120, text.Length - Math.Max(0, m.Index - 60)));
            }
            return null;
        }
    }

    /// <summary>A Fact that is reported as SKIPPED, with the reason, when the owner's wordlist is not on this machine.</summary>
    public sealed class WordlistFactAttribute : FactAttribute
    {
        public WordlistFactAttribute()
        {
            if (InternalNames.WordlistTerms().Count == 0)
                Skip = "NOT RUN: the owner's wordlist is absent or empty (" + (InternalNames.WordlistPath() ?? "no user profile") + "); the hashed check still ran.";
        }
    }

    /// <summary>A Theory with the same rule.</summary>
    public sealed class WordlistTheoryAttribute : TheoryAttribute
    {
        public WordlistTheoryAttribute()
        {
            if (InternalNames.WordlistTerms().Count == 0)
                Skip = "NOT RUN: the owner's wordlist is absent or empty (" + (InternalNames.WordlistPath() ?? "no user profile") + "); the hashed check still ran.";
        }
    }

    public class NoInternalNamesTests
    {
        [Theory]
        [InlineData("AGENTS.md")]
        [InlineData("CHANGELOG.md")]
        [InlineData("README.md")]
        [InlineData("README.es.md")]
        [InlineData("docs/TOOLS-EXTENDED.md")]
        [InlineData("docs/INFORMATION-MANAGEMENT.md")]
        [InlineData("docs/CLIENTS.md")]
        public void The_public_documents_name_no_person_and_no_agent(string relative)
        {
            string hit = InternalNames.FirstHashHit(Int7Repo.Read(relative.Split('/')));
            Assert.True(hit == null, relative + " names " + hit);
        }

        [WordlistTheory]
        [InlineData("AGENTS.md")]
        [InlineData("CHANGELOG.md")]
        [InlineData("README.md")]
        [InlineData("README.es.md")]
        [InlineData("docs/TOOLS-EXTENDED.md")]
        [InlineData("docs/INFORMATION-MANAGEMENT.md")]
        [InlineData("docs/CLIENTS.md")]
        public void The_public_documents_name_nobody_on_the_owners_wordlist(string relative)
        {
            string hit = InternalNames.FirstWordlistHit(Int7Repo.Read(relative.Split('/')), InternalNames.WordlistTerms());
            Assert.True(hit == null, relative + " names " + hit);
        }

        [Fact]
        public void The_texts_of_the_sync_only_option_name_nobody_and_say_it_is_experimental_and_off()
        {
            foreach (bool es in new[] { true, false })
            {
                string note = SyncOnlyGrantRules.ExperimentalNote(es);
                string dialog = SyncOnlyGrantRules.EnableDialogText(es, es ? "Sincronizar con el central" : "Synchronize with central", es ? "Proteger los modelos compartidos" : "Protect shared models");
                Assert.Null(InternalNames.FirstHashHit(note + dialog));
                string listed = InternalNames.FirstWordlistHit(note + dialog, InternalNames.WordlistTerms());
                Assert.True(listed == null, listed);
                Assert.StartsWith(es ? "Opción experimental, apagada por defecto." : "Experimental option, off by default.", note);
                Assert.StartsWith(note, dialog);
                // The truth about the higher modes (INT-6 m-2): they lose accept_findings and two relinquish choices there.
                Assert.Contains("accept_findings", dialog);
                Assert.Contains("full_write", dialog);
                Assert.DoesNotContain("which lose nothing", dialog);
                Assert.DoesNotContain("que no pierden nada", dialog);
            }
        }

        [Fact]
        public void The_documents_carry_the_very_sentence_the_option_shows_in_its_dialog()
        {
            Int7Repo.AgentsHalves(out string en, out string es);
            Assert.Contains(SyncOnlyGrantRules.ExperimentalNote(false).Trim(), en);
            Assert.Contains(SyncOnlyGrantRules.ExperimentalNote(true).Trim(), es);
            Assert.Contains(SyncOnlyGrantRules.ExperimentalNote(false).Trim(), Int7Repo.Read("docs", "TOOLS-EXTENDED.md"));
            Assert.Contains(SyncOnlyGrantRules.ExperimentalNote(true).Trim().TrimEnd('.'), Int7Repo.Read("CHANGELOG.md"));
        }

        [Fact]
        public void The_enable_text_ties_to_the_documents_about_the_higher_modes()
        {
            // What the dialog promises about full_write/unsafe_code is what the documents say.
            string dialog = SyncOnlyGrantRules.EnableDialogText(false, "Synchronize with central", "Protect shared models");
            Assert.Contains("the relinquish choices none and keep_borrowed", dialog);
            Int7Repo.AgentsHalves(out string en, out _);
            string flat = Regex.Replace(en, @"\s+", " ");
            Assert.Contains("which never get less than `safe_write`", flat);
            Assert.Contains("(`accept_findings` and `accept_findings_ids` are refused)", flat);
            Assert.Contains("accept_findings, accept_findings_ids", dialog);
        }
    }

    // ---- m-e: cancelling an async execute_plan -----------------------------------------------------------------------------

    public class PlanCancelRulesTests
    {
        private static JObject Applied() { var d = new JObject { ["transaction_status"] = "Committed" }; ApplicationOutcome.StampApplied(d, "Committed", 2, 2, 2, 0, 0, 0); return d; }

        /// <summary>The per-action protocol of ExecutePlanCommand's apply loop, with the same ledger calls in the same order.</summary>
        private static (PlanLedger ledger, string error) RunLikeTheCommand(string[] keys, Func<int, bool> cancelAskedBefore, Func<bool> cancelAskedAtTheEnd)
        {
            var ledger = new PlanLedger();
            for (int i = 0; i < keys.Length; i++)
            {
                if (ledger.StopBeforeAction(cancelAskedBefore(i), i, keys[i], "horizun_write_params_verified"))
                    return (ledger, "action '" + keys[i] + "' was not started: cancellation of the job was requested. The atomic plan rolls back.");
                ledger.RecordExecuted(i, keys[i], "horizun_write_params_verified", true, Applied(), null, out ApplicationState _);
            }
            if (ledger.StopBeforeCommit(cancelAskedAtTheEnd()))
                return (ledger, "cancellation of the job was requested before the group was kept: the atomic plan rolls back instead of committing.");
            return (ledger, null);
        }

        private static JObject DiagnosticOf(PlanLedger ledger, string error) =>
            PlanFailure.Diagnostic(true, "RolledBack", true, "RolledBack", ledger.Executed, error, ledger.FailedAction, cancelled: ledger.Cancelled);

        [Fact]
        public void No_cancel_runs_every_action_and_keeps_the_group()
        {
            var (ledger, error) = RunLikeTheCommand(new[] { "a", "b", "c" }, i => false, () => false);
            Assert.Null(error);
            Assert.False(ledger.Cancelled);
            Assert.Equal(3, ledger.Executed.Count);
            Assert.Null(ledger.FailedAction);
        }

        [Fact]
        public void A_cancel_before_an_action_stops_the_plan_and_that_action_is_cancelled_not_executed()
        {
            var (ledger, error) = RunLikeTheCommand(new[] { "a", "b", "c" }, i => i == 1, () => false);
            Assert.NotNull(error);
            Assert.True(ledger.Cancelled);
            Assert.Single(ledger.Executed);                                       // only "a" ran
            Assert.Equal("a", (string)ledger.Executed[0]["key"]);
            JObject failed = ledger.FailedAction;
            Assert.Equal("b", (string)failed["key"]);
            Assert.Equal("cancelled", (string)failed["status"]);
            Assert.False((bool)failed["started"]);
            Assert.Equal(ErrorCodes.Cancelado, (string)failed["error_code"]);
            JObject diag = DiagnosticOf(ledger, error);
            Assert.DoesNotContain(diag["execution_trace"].Select(t => (string)t["key"]), k => k == "b" || k == "c");
        }

        [Fact]
        public void A_cancel_during_the_last_action_rolls_back_instead_of_committing()
        {
            var (ledger, error) = RunLikeTheCommand(new[] { "a", "b" }, i => false, () => true);
            Assert.NotNull(error);
            Assert.True(ledger.Cancelled);
            Assert.Equal(2, ledger.Executed.Count);                               // both ran; the COMMIT did not happen
            Assert.Equal("cancelled", (string)ledger.FailedAction["status"]);
            Assert.Equal("before_commit", (string)ledger.FailedAction["stage"]);
            Assert.False((bool)ledger.FailedAction["started"]);
        }

        [Fact]
        public void The_result_of_a_cancelled_plan_carries_the_stable_status_and_code()
        {
            var (ledger, error) = RunLikeTheCommand(new[] { "a", "b" }, i => i == 0, () => false);
            JObject diag = DiagnosticOf(ledger, error);
            Assert.Equal("cancelled", (string)diag["status"]);
            Assert.True((bool)diag["cancelled"]);
            Assert.Equal("cancelado", (string)diag[ErrorCodes.CodeField]);
            Assert.True(ErrorCodes.IsStable((string)diag[ErrorCodes.CodeField]));
            Assert.Contains("CANCELLED by request", PlanFailure.Message(diag));
            Assert.True((bool)diag["rollback_confirmed"]);                        // the honest rollback fields stay beside it
            // The envelope the caller reads classifies it as cancelled, not as an unclassified failure.
            Assert.Equal(ErrorCodes.Cancelado, ErrorCodes.Classify(PlanFailure.Message(diag), diag).Code);
        }

        [Fact]
        public void A_plan_that_failed_for_another_reason_is_not_stamped_cancelled()
        {
            var ledger = new PlanLedger();
            ledger.RecordExecuted(0, "a", "t", false, null, "boom", out ApplicationState _);
            JObject diag = PlanFailure.Diagnostic(true, "RolledBack", true, "RolledBack", ledger.Executed, "action 'a' failed: boom", ledger.FailedAction, cancelled: ledger.Cancelled);
            Assert.Null(diag["status"]);
            Assert.Null(diag["cancelled"]);
            Assert.DoesNotContain("CANCELLED", PlanFailure.Message(diag));
            Assert.Single(ledger.Executed);                                         // a failed action DID run, and stays in the trace
        }

        [Fact]
        public void The_rehearsal_stops_on_a_cancel_and_is_no_longer_clean()
        {
            var ledger = new PlanLedger();
            var clean = new JObject();
            ApplicationOutcome.StampRehearsal(clean, 2, 0, 0, 0);
            JObject ok = ledger.RecordRehearsal(0, "a", "t", true, clean, null);
            Assert.True(ledger.RehearsedCleanly);                                  // a clean rehearsal so far
            Assert.False(ledger.StopBeforeAction(false, 1, "b", "t"));
            Assert.True(ledger.StopBeforeAction(true, 1, "b", "t"));
            Assert.False(ledger.RehearsedCleanly);                                 // no token can come out of a rehearsal that was cut
            JObject detail = PlanFailure.BeforeGroup("dry_run", new JArray(ok), ledger.FailedAction, "Atomic plan rehearsal cancelled before action 'b'", cancelled: true);
            Assert.Equal("cancelled", (string)detail["status"]);
            Assert.Equal("cancelado", (string)detail[ErrorCodes.CodeField]);
            Assert.True((bool)detail["nothing_ran"]);
            Assert.Empty(detail["execution_trace"]);
        }

        [Fact]
        public void A_job_key_claimed_for_a_plan_outlives_its_scripts_and_any_other_does_not()
        {
            Assert.True(PythonRunRegistry.RunsChildren("horizun_execute_plan"));
            Assert.False(PythonRunRegistry.RunsChildren("horizun_execute_python"));
            Assert.False(PythonRunRegistry.RunsChildren("horizun_model_scan"));
        }
    }

    // ---- m-f: what `renamed_to` announces --------------------------------------------------------------------------------

    public class SheetReferenceRowsTests
    {
        private static Dictionary<string, string> Map(params string[] pairs)
        {
            var m = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < pairs.Length; i += 2) m[pairs[i]] = pairs[i + 1];
            return m;
        }

        [Fact]
        public void A_reference_the_rule_will_not_rename_does_not_announce_a_new_number()
        {
            var loose = new Regex(@"(A1)");
            List<NoteLeaderRules.SheetRefMatch> rows = NoteLeaderRules.ListSheetReferences("plano A10 y plano A1 fin", loose, Map("A1", "B1"));
            Assert.Equal(2, rows.Count);
            Assert.Equal("A1", rows[0].Number);
            Assert.Null(rows[0].RenamedTo);          // glued to the 0 of A10: the rule leaves it, so the reply does not announce it
            Assert.Equal("B1", rows[1].RenamedTo);
        }

        [Theory]
        [InlineData(@"S(\d+)", "Ver S12, S7 y AS12", "12", "13")]
        [InlineData(@"(?i)\b(?:HOJA|SHEET)\s+([A-Z]{1,4}\d{1,4})", "Hoja A1 y Hoja A10 y Hoja A2", "A1", "A9")]
        public void The_rows_announce_exactly_the_renames_the_edits_make(string pattern, string plain, string from, string to)
        {
            var rx = new Regex(pattern);
            var map = Map(from, to);
            List<NoteLeaderRules.SheetRefMatch> rows = NoteLeaderRules.ListSheetReferences(plain, rx, map);
            List<NoteLeaderRules.SheetRefEdit> edits = NoteLeaderRules.PlanSheetReferenceEdits(plain, rx, map);
            Assert.Equal(rows.Count(r => r.RenamedTo != null), edits.Count);
            foreach (NoteLeaderRules.SheetRefEdit e in edits)
                Assert.Contains(rows, r => r.RenamedTo == e.NewNumber && r.Start == e.Start && r.Length == e.Length);
            // Whatever the rows announce is what the note reads afterwards.
            string after = NoteLeaderRules.ApplySheetReferenceEdits(plain, edits);
            Assert.Equal(NoteLeaderRules.ExpectedAfterSheetReferences(plain, rx, map), after);
            foreach (var r in rows.Where(r => r.RenamedTo != null)) Assert.Contains(r.RenamedTo, after);
            // And a row that announces nothing is a reference the note still carries untouched.
            foreach (var r in rows.Where(r => r.RenamedTo == null && map.ContainsKey(r.Number)))
                Assert.Equal(plain.Substring(r.Start, r.Length), r.Number);
        }

        [Fact]
        public void Without_a_map_nothing_is_announced()
        {
            List<NoteLeaderRules.SheetRefMatch> rows = NoteLeaderRules.ListSheetReferences("Ver S12", new Regex(@"S(\d+)"), null);
            Assert.Single(rows);
            Assert.Null(rows[0].RenamedTo);
            Assert.Empty(NoteLeaderRules.PlanSheetReferenceEdits("Ver S12", new Regex(@"S(\d+)"), null));
        }
    }

    // ---- m-g: 2027 is measured like the other years ----------------------------------------------------------------------

    public class IfcSetupYearTests
    {

        

        

    }

    // ---- m-l: strict AFTER the export ------------------------------------------------------------------------------------

}
