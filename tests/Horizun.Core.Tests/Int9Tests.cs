using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Horizun.Contracts;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    // INT-9: the corrections of the independent review of INT-8 (M-1, M-2, M-3, m-1, m-2, m-3).
    [Collection("SaveAs")]
    public class Int9Tests
    {
        private static string RepoRoot()
        {
            DirectoryInfo d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, "AGENTS.md"))) d = d.Parent;
            Assert.NotNull(d);
            return d.FullName;
        }

        private static string Source(params string[] parts) => File.ReadAllText(Path.Combine(new[] { RepoRoot() }.Concat(parts).ToArray()));

        private static string Key(string what) => "path:c:\\int9\\" + what + "-" + Guid.NewGuid().ToString("N") + ".rvt";

        private static readonly List<SyncLedgerEntry> Blockers = new List<SyncLedgerEntry>
        {
            new SyncLedgerEntry { Tool = "horizun_execute_python", EvidenceStatus = "completed_unverified" }
        };

        // ------------------------------------------------------------------ M-1 / M-2

        [Fact]
        public void The_way_out_of_an_unverified_block_is_never_a_retry_that_the_target_refuses_for_every_profile_option_grant_and_caller()
        {
            string[] profiles = { "read_only", "documentation", "safe_write", "full_write", "unsafe_code" };
            int offered = 0, combinations = 0;
            foreach (string profile in profiles)
                foreach (bool flag in new[] { false, true })
                    foreach (bool grant in new[] { false, true })
                        foreach (bool callerSyncOnly in new[] { false, true })
                        {
                            combinations++;
                            var decision = SyncOnlyGrantRules.Decide(profile, grant, flag);
                            var wayOut = SyncOnlyGrantRules.WayOutOfBlocked(decision, callerSyncOnly);
                            string message = SyncLedgerRules.BlockedMessage("Model", Blockers, wayOut);
                            string where = profile + " flag=" + flag + " grant=" + grant + " caller=" + callerSyncOnly;

                            // What horizun_save_document does with sync_with_central + accept_findings=true, from the real functions.
                            bool targetAdmits = OperationPermission.SaveRefusal(profile, OperationPermission.SyncWithCentral, grant, flag) == null;
                            bool targetStrict = SyncOnlyGrantRules.Strict(flag, profile);
                            bool targetAuthorised = grant || targetStrict;   // the sync's own owner authorisation (an owner grant, or the strict option)
                            bool targetAccepts = targetAdmits && targetAuthorised && (!targetStrict || SyncOnlyGrantRules.RequestRefusal("c", SyncRelinquish.All, true) == null);

                            if (wayOut.AcceptFindingsOffered)
                            {
                                offered++;
                                Assert.True(targetAccepts, "a retry is suggested that the target refuses: " + where);
                                Assert.Contains("accept_findings=true", message);
                            }
                            else
                            {
                                Assert.DoesNotContain("repeat the sync with", message);
                                Assert.DoesNotContain("accept_findings=true", message);
                            }
                            // The converse for the target's own decision: when it would accept and the caller is not strict, it is offered.
                            if (targetAccepts && !callerSyncOnly) Assert.True(wayOut.AcceptFindingsOffered, "a working way out is hidden: " + where);
                            if (wayOut.Strict) Assert.Contains("turn that option off", message);
                        }
            Assert.Equal(40, combinations);
            Assert.True(offered > 0);
        }

        [Fact]
        public void With_full_write_and_the_option_on_the_message_does_not_send_the_caller_to_save_document()
        {
            // The finding: horizun_document_session runs non-strict (its caller flag is false) while the owner's option is on, and the old text named
            // horizun_save_document accept_findings, which is strict there and refuses it.
            var wayOut = SyncOnlyGrantRules.WayOutOfBlocked(SyncOnlyGrantRules.Decide("full_write", true, true), callerSyncOnly: false);
            Assert.True(wayOut.Strict);
            Assert.False(wayOut.AcceptFindingsOffered);
            string message = SyncLedgerRules.BlockedMessage("Model", Blockers, wayOut);
            Assert.DoesNotContain("repeat the sync with", message);
            Assert.Contains("accepts no findings", message);
            Assert.Contains("accept_findings_ids", message);
            Assert.DoesNotContain("full_write", message);
            Assert.Contains("horizun_verify_changes", message);
            Assert.Contains("Nothing was synchronized", message);
        }

        [Fact]
        public void The_sync_only_caller_and_an_unreadable_decision_offer_no_acceptance()
        {
            Assert.False(SyncOnlyGrantRules.WayOutOfBlocked(SyncOnlyGrantRules.Decide("full_write", true, false), callerSyncOnly: true).AcceptFindingsOffered);
            Assert.False(SyncOnlyGrantRules.WayOutOfBlocked(null, callerSyncOnly: false).AcceptFindingsOffered);
            Assert.False(SyncOnlyGrantRules.WayOutOfBlocked(SyncOnlyGrantRules.Decide("full_write", true, true), callerSyncOnly: true).AcceptFindingsOffered);
            Assert.True(SyncOnlyGrantRules.WayOutOfBlocked(SyncOnlyGrantRules.Decide("full_write", true, false), callerSyncOnly: false).AcceptFindingsOffered);
            string sync = Source("src", "Horizun.Revit", "Commands", "DocumentSessionSync.cs");
            int offerBlock = sync.IndexOf("if (wayOut.AcceptFindingsOffered)", StringComparison.Ordinal);
            int field = sync.IndexOf("[\"accept_findings_with\"]", StringComparison.Ordinal);
            Assert.True(offerBlock > 0 && field > offerBlock, "accept_findings_with is emitted only inside the offered branch");
            Assert.Contains("SyncLedgerRules.BlockedMessage(title, blockers, wayOut)", sync);
            Assert.Contains("syncDecision)", Source("src", "Horizun.Revit", "Commands", "SaveDocumentCommand.cs"));
        }

        // ------------------------------------------------------------------ M-3

        [Fact]
        public void MoveAll_moves_every_dictionary_and_leaves_the_destination_with_only_what_the_renamed_document_brings()
        {
            var a = new Dictionary<string, int>(); var b = new Dictionary<string, string>(); var c = new Dictionary<string, DateTime>();
            a["old"] = 1; a["new"] = 99; b["new"] = "closed document"; c["new"] = DateTime.UtcNow;
            Assert.True(LedgerRenameRules.MoveAll("old", "new", a, b, c));
            Assert.Equal(1, a["new"]); Assert.False(a.ContainsKey("old"));
            Assert.False(b.ContainsKey("new"));            // the old key held nothing here: the closed document's entry does not survive
            Assert.False(c.ContainsKey("new"));
            Assert.False(LedgerRenameRules.MoveAll("nothing", "new", a, b, c));
            Assert.Equal(1, a["new"]);                      // nothing moved, nothing touched
        }

        [Fact]
        public void The_sync_ledger_follows_a_save_as_in_all_four_dictionaries_and_the_destination_inherits_nothing()
        {
            string from = Key("a"), to = Key("b");
            // A synchronized by hand (event), then renamed: last_sync_by follows.
            SyncLedgerRules.ObserveSynchronization(from, true, DateTime.UtcNow);
            SyncLedgerRules.Rename(from, to);
            Assert.Equal("revit_event", SyncLedgerRules.LastSyncBy(to));
            Assert.Null(SyncLedgerRules.LastSyncBy(from));
            Assert.NotNull(SyncLedgerRules.LastSyncUtc(to));

            // A sync that began on the old key and ends on the new one is observed (SyncStarts follows).
            string from2 = Key("c"), to2 = Key("d");
            SyncLedgerRules.BeginSynchronization(from2, DateTime.UtcNow);
            SyncLedgerRules.Rename(from2, to2);
            SyncLedgerRules.EndSynchronization(to2, true, DateTime.UtcNow.AddSeconds(1));
            Assert.Equal("revit_event", SyncLedgerRules.LastSyncBy(to2));

            // B closed under a path, A saves to that path: A must not inherit B's "horizun" source or last sync.
            string closedB = Key("e"), aOld = Key("f");
            SyncLedgerRules.MarkSynced(closedB, DateTime.UtcNow);
            Assert.Equal("horizun", SyncLedgerRules.LastSyncBy(closedB));
            SyncLedgerRules.RecordWrite(aOld, new SyncLedgerEntry { Tool = "horizun_create_elements", Ids = new long[] { 1 } });
            SyncLedgerRules.Rename(aOld, closedB);
            Assert.Null(SyncLedgerRules.LastSyncBy(closedB));
            Assert.Null(SyncLedgerRules.LastSyncUtc(closedB));
            Assert.Single(SyncLedgerRules.Writes(closedB));
        }

        [Fact]
        public void The_save_suggestion_count_follows_a_save_as_and_a_save_to_a_path_left_by_another_document_does_not_inherit_its_count()
        {
            string from = Key("g"), to = Key("h");
            for (int i = 0; i < 4; i++) SaveSuggestionRules.RecordWrite(from);
            SaveSuggestionRules.Rename(from, to);
            Assert.Equal(4, SaveSuggestionRules.WritesSinceSave(to));
            Assert.Equal(0, SaveSuggestionRules.WritesSinceSave(from));

            string closedB = Key("i"), aOld = Key("j");
            for (int i = 0; i < 7; i++) SaveSuggestionRules.RecordWrite(closedB);
            SaveSuggestionRules.RecordWrite(aOld);
            SaveSuggestionRules.Rename(aOld, closedB);
            Assert.Equal(1, SaveSuggestionRules.WritesSinceSave(closedB));
        }

        [Fact]
        public void A_save_as_through_the_lifecycle_rule_renames_the_save_suggestion_too()
        {
            string from = Key("k"), to = Key("l");
            SaveSuggestionRules.RecordWrite(from); SaveSuggestionRules.RecordWrite(from);
            DocumentLifecycleRules.SavedAs(from, to, (x, y) => { });
            Assert.Equal(2, SaveSuggestionRules.WritesSinceSave(to));
            Assert.Equal(0, SaveSuggestionRules.WritesSinceSave(from));
        }

        // The sweep: every static dictionary by string that a runtime fills (empty at construction) is either renamed with the document through
        // LedgerRenameRules or listed here with the reason it is not per document. A new one makes this fail until it is classified.
        private static readonly Regex MutableDictionary = new Regex(
            @"static\s+(?:readonly\s+)?(?:Concurrent)?Dictionary<string,[^;{=]*?>\s+(\w+)\s*=\s*new\s*(?:(?:Concurrent)?Dictionary<[^;{]*?>)?\([^;{]*\)\s*;",
            RegexOptions.Compiled);

        private static readonly string[] Renamed =
        {
            "SyncLedgerRules.cs:Pending", "SyncLedgerRules.cs:LastSync", "SyncLedgerRules.cs:LastSyncSource", "SyncLedgerRules.cs:SyncStarts",
            "SaveSuggestionRules.cs:Counts", "IdFreshnessRules.cs:Documents", "SessionCreationLedger.cs:Writes", "SessionCreationLedger.cs:Duplicated",
            "ChangeWatch.cs:Last", "ChangeWatch.cs:History"
        };

        // Not a per-document trust ledger: keyed by token, tool, file, view or job; losing it after a Save As only repeats work or restarts a pass.
        private static readonly string[] NotPerDocumentLedger =
        {
            "DocumentSessionSync.cs:SyncEstimates",       // by confirmation token, bounded to 32
            "AsyncQueue.cs:_activeCooperative",           // by job
            "BBoxCache.cs:Store",                         // geometry cache by element key
            "CaptureFreshness.cs:Last",                   // last capture per view: a stale entry reads as first_capture
            "OwnershipPass.cs:Passes",                    // health cursor: a new key restarts the pass
            "SelectionContextRules.cs:ByDocument",        // named snapshots of the session (not trust)
            "ToolTimings.cs:_byTool",                     // by tool
            "SubscriptionStream.cs:Open",                  // server: open subscriptions
            "CompactAnnouncement.cs:Cache", "IdentitySchema.cs:Cache", "MrtrRequestState.cs:Consumed"   // server: by tool, schema or nonce
        };

        // ------------------------------------------------------------------ m-1

        [Fact]
        public void What_a_failed_call_stamped_is_entered_in_the_ledger_of_creations_so_the_same_session_believes_it()
        {
            Assert.True(HrzOwnershipRules.ShouldRecordCreationAfterFailure(true, "horizun_create_elements"));
            Assert.False(HrzOwnershipRules.ShouldRecordCreationAfterFailure(false, "horizun_create_elements"));   // a success is recorded by the change ledger
            Assert.False(HrzOwnershipRules.ShouldRecordCreationAfterFailure(true, "horizun_execute_python"));
            Assert.False(HrzOwnershipRules.ShouldRecordCreationAfterFailure(true, null));
            // The pair "stamped" and "believed" cannot drift: every tool that may be stamped after a failure enters the ledger after one.
            foreach (string tool in new[] { "horizun_create_elements", "horizun_framing", "horizun_manage_views", "horizun_execute_python", "horizun_undo", "horizun_document_session" })
                Assert.Equal(HrzOwnershipRules.ShouldStampAfterFailure(tool, true), HrzOwnershipRules.ShouldRecordCreationAfterFailure(true, tool));

            string key = Key("m");
            SessionCreationLedger.RecordFor("horizun_create_elements", key, new[] { "uid-failed-1" });
            Assert.True(SessionCreationLedger.Created(key, "uid-failed-1"));
            string own = Source("src", "Horizun.Revit", "Core", "HrzOwnership.cs");
            int hook = own.IndexOf("ShouldRecordCreationAfterFailure(failed, tool)", StringComparison.Ordinal);
            int readBack = own.IndexOf("stamped++;", StringComparison.Ordinal);
            Assert.True(readBack > 0 && hook > readBack, "the ledger is fed from the marks that were read back");
            Assert.Contains("SessionCreationLedger.RecordFor(tool, ChangeWatch.Key(doc), stampedUids)", own);
        }

        // ------------------------------------------------------------------ m-2

        private static ElementIdentityFacts Facts(long id) => id < 1 || id > 999 ? null : new ElementIdentityFacts
        {
            Id = id, UniqueId = "u" + id, Owned = true, Category = "Walls", CategoryBuiltIn = "OST_Walls", Type = "Generic - 200mm", Family = "Basic Wall"
        };

        private static ElementIdentityFacts ByUid(string uid) =>
            uid != null && uid.StartsWith("u", StringComparison.Ordinal) && long.TryParse(uid.Substring(1), out long id) ? Facts(id) : null;

        private static JObject Pre(string json) { IdentityBinding.Reset(); return ElementIdentityGuard.PreCheck(JObject.Parse(json), "horizun_manage_revisions", Facts, ByUid, () => false); }

        [Fact]
        public void The_loose_identity_form_cannot_leave_remove_sheet_ids_unchecked()
        {
            // unique_ids bind sheet_ids; remove_sheet_ids (a removal) would go unchecked.
            JObject error = Pre("{\"operation\":\"update_revision\",\"revision_id\":7,\"sheet_ids\":[9],\"remove_sheet_ids\":[8],\"unique_ids\":[\"u9\"]}");
            Assert.NotNull(error);
            Assert.Equal("entrada_invalida", (string)error["error_code"]);
            Assert.Contains("remove_sheet_ids", (string)error["detail"]);
            Assert.NotNull(Pre("{\"operation\":\"update_revision\",\"revision_id\":7,\"sheet_ids\":[9],\"remove_sheet_ids\":[8],\"expected_category\":\"Walls\"}"));
            // The keyed form names each key and still passes; the loose form bound to remove_sheet_ids itself passes.
            Assert.Null(Pre("{\"operation\":\"update_revision\",\"revision_id\":7,\"sheet_ids\":[9],\"remove_sheet_ids\":[8],\"identity\":{\"sheet_ids\":[\"u9\"],\"remove_sheet_ids\":[\"u8\"]}}"));
            Assert.Null(Pre("{\"operation\":\"update_revision\",\"revision_id\":7,\"remove_sheet_ids\":[8],\"unique_ids\":[\"u8\"]}"));
            // An empty removal list removes nothing.
            Assert.Null(Pre("{\"operation\":\"update_revision\",\"revision_id\":7,\"sheet_ids\":[9],\"remove_sheet_ids\":[],\"unique_ids\":[\"u9\"]}"));
        }

        // ------------------------------------------------------------------ m-3

        [Fact]
        public void What_a_write_only_duplicated_is_not_a_creation_of_ours_but_the_new_view_and_what_the_write_made_are()
        {
            string key = Key("n");
            SessionCreationLedger.NoteDuplicated(key, new[] { "copy-annotation-1", "copy-annotation-2" });
            SessionCreationLedger.RecordFor("horizun_planimetry_ops", key, new[] { "new-legend-view", "copy-annotation-1", "new-label", "copy-annotation-2" });
            Assert.True(SessionCreationLedger.Created(key, "new-legend-view"));
            Assert.True(SessionCreationLedger.Created(key, "new-label"));
            Assert.False(SessionCreationLedger.Created(key, "copy-annotation-1"));
            Assert.False(SessionCreationLedger.Created(key, "copy-annotation-2"));
            Assert.True(SessionCreationLedger.IsDuplicated(key, "copy-annotation-1"));
            Assert.False(SessionCreationLedger.IsDuplicated(key, "new-label"));
            Assert.DoesNotContain("copy-annotation-1", SessionCreationLedger.All(key));
            Assert.False(SessionCreationLedger.IsDuplicated(Key("other"), "copy-annotation-1"));
        }

        [Fact]
        public void The_duplicated_set_follows_a_save_as_and_a_destination_keeps_nothing_of_a_closed_document()
        {
            string from = Key("o"), to = Key("p");
            SessionCreationLedger.NoteDuplicated(from, new[] { "dup-a" });
            SessionCreationLedger.Rename(from, to);
            Assert.True(SessionCreationLedger.IsDuplicated(to, "dup-a"));
            Assert.False(SessionCreationLedger.IsDuplicated(from, "dup-a"));
            string closedB = Key("q"), aOld = Key("r");
            SessionCreationLedger.NoteDuplicated(closedB, new[] { "dup-of-b" });
            SessionCreationLedger.RecordFor("horizun_create_elements", aOld, new[] { "made-by-a" });
            SessionCreationLedger.Rename(aOld, closedB);
            Assert.False(SessionCreationLedger.IsDuplicated(closedB, "dup-of-b"));
            Assert.True(SessionCreationLedger.Created(closedB, "made-by-a"));
        }

        [Fact]
        public void The_duplicating_writes_say_what_they_duplicated_and_the_stamp_skips_it()
        {
            string plan = Source("src", "Horizun.Revit", "Commands", "PlanimetryOpsCommand.cs");
            int dup = plan.IndexOf("SessionCreationLedger.NoteDuplicated(ChangeWatch.Key(d), inLegend.Select(e => e.UniqueId));", StringComparison.Ordinal);
            int del = plan.IndexOf("d.Delete(inLegend.Where", StringComparison.Ordinal);
            Assert.True(dup > 0 && del > dup, "legend_from_families notes what the duplicate brought before it deletes anything");
            string legends = Source("src", "Horizun.Revit", "Commands", "ManageViewsLegends.cs");
            Assert.Contains("SessionCreationLedger.NoteDuplicated(ChangeWatch.Key(doc),", legends);
            string own = Source("src", "Horizun.Revit", "Core", "HrzOwnership.cs");
            Assert.Contains("SessionCreationLedger.IsDuplicated(docKey, doc.GetElement(id)?.UniqueId)", own);
        }

        // ------------------------------------------------------------------ cleanliness of the shipped source

        [Fact]
        public void Comments_in_src_name_no_member_of_the_team()
        {
            // The names themselves are not written here: the check is on the shapes the comments had ("E8 (<name>, date)", "<name> measured").
            var bad = new List<string>();
            foreach (string dir in new[] { "Horizun.Revit", "Horizun.Server", "Horizun.Contracts", "Horizun.Core" })
            {
                string root = Path.Combine(RepoRoot(), "src", dir);
                if (!Directory.Exists(root)) continue;
                foreach (string file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
                {
                    string rel = file.Replace('\\', '/');
                    if (rel.Contains("/obj/") || rel.Contains("/bin/")) continue;
                    foreach (string line in File.ReadLines(file))
                        if (Regex.IsMatch(line, @"\bE\d+ \([A-Z][a-z]+,\s*20\d\d-") || Regex.IsMatch(line, @"\([A-Z][a-z]+ E\d+:"))
                            bad.Add(Path.GetFileName(file) + ": " + line.Trim());
                }
            }
            Assert.Empty(bad);
        }

        [Fact]
        public void Every_runtime_dictionary_by_string_is_classified_and_the_renamed_ones_are_moved_by_the_one_rule()
        {
            var found = new List<string>();
            var texts = new Dictionary<string, string>();
            foreach (string dir in new[] { "Horizun.Revit", "Horizun.Server" })
                foreach (string file in Directory.EnumerateFiles(Path.Combine(RepoRoot(), "src", dir), "*.cs", SearchOption.AllDirectories))
                {
                    string rel = file.Replace('\\', '/');
                    if (rel.Contains("/obj/") || rel.Contains("/bin/")) continue;
                    string text = File.ReadAllText(file);
                    foreach (Match m in MutableDictionary.Matches(text))
                    {
                        string id = Path.GetFileName(file) + ":" + m.Groups[1].Value;
                        found.Add(id);
                        texts[id] = text;
                    }
                }
            var known = Renamed.Concat(NotPerDocumentLedger).ToList();
            Assert.Empty(found.Except(known));            // a new dictionary: classify it (renamed with the document, or not per document and why)
            Assert.Empty(known.Except(found));            // a listed one that no longer exists: keep the list honest
            foreach (string id in Renamed)
            {
                string field = id.Substring(id.IndexOf(':') + 1);
                var calls = Regex.Matches(texts[id], @"LedgerRenameRules\.Move(?:All)?\(([^;]*)\)\s*;").Cast<Match>().Select(m => m.Groups[1].Value);
                Assert.True(calls.Any(args => Regex.IsMatch(args, @"\b" + field + @"\b")), id + " is not moved by LedgerRenameRules.Move/MoveAll");
            }
        }
    }
}
