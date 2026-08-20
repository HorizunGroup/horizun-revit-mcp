// -----------------------------------------------------------------------------
// Horizun Server tests - original Horizun code.
//
// THE LIVE SUITE MAY NOT NAME TOOLS THIS VERSION DOES NOT HAVE.
//
// Four probes rotted exactly this way: connect_mep, terminate_riser,
// place_sprinklers and family_mirror_void kept running against a build that no
// longer published them, answered "not published by this build" every time, and
// counted as GAPS - so the version's coverage carried a permanent floor of
// missing guarantees that were not missing, they were retired.
//
// Deleting them would have been worse in the other direction: in a diff, a
// guarantee that used to be checked and now is not looks exactly like one that
// never existed. So they live in a registry with what they covered and what
// replaced them, and these tests hold both ends: no active probe may name an
// absent tool, and no retired entry may exist without its history.
//
// The live script cannot run in CI - it needs Revit - but its TEXT is data, and
// the tool names in it are checkable against the contract right here.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Horizun.Contracts;
using Xunit;

namespace Horizun.Server.Tests
{
    public class LiveProbeRegistryTests
    {
        private static string ScriptPath()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, "scripts", "verify-live.ps1")))
                d = d.Parent;
            Assert.True(d != null, "Could not locate scripts/verify-live.ps1");
            return Path.Combine(d.FullName, "scripts", "verify-live.ps1");
        }

        private static string Script() => File.ReadAllText(ScriptPath());

        private static HashSet<string> PublishedTools() =>
            new HashSet<string>(Contract.All.Select(c => c.Name), StringComparer.Ordinal);

        /// <summary>
        /// The retired block, as text, so the two halves can be told apart: everything
        /// after `$RetiredProbes = @(` up to the line that closes it.
        /// </summary>
        private static string RetiredBlock()
        {
            string text = Script();
            const string marker = "$RetiredProbes = @(";
            int start = text.IndexOf(marker, StringComparison.Ordinal);
            Assert.True(start >= 0, "the retired-probe registry is gone; retired probes must not simply vanish");
            // Past the declaration itself: "$RetiredProbes =" would otherwise be counted
            // as one more entry's Probes field.
            start += marker.Length;
            int end = text.IndexOf("$retiredRows = @()", start, StringComparison.Ordinal);
            Assert.True(end > start, "the retired registry no longer ends where expected");
            return text.Substring(start, end - start);
        }

        /// <summary>
        /// THE GATE. Every horizun_* tool named by an ACTIVE probe must exist in the
        /// contract. Names inside the retired registry are exempt - that is the whole
        /// point of the registry.
        /// </summary>
        [Fact]
        public void No_active_probe_names_a_tool_this_version_does_not_publish()
        {
            string text = Script();
            string retired = RetiredBlock();
            HashSet<string> published = PublishedTools();

            var dangling = new SortedSet<string>(StringComparer.Ordinal);
            foreach (Match m in Regex.Matches(text, @"'(horizun_[a-z_]+)'"))
            {
                string tool = m.Groups[1].Value;
                if (published.Contains(tool)) continue;
                if (retired.Contains(tool, StringComparison.Ordinal)) continue;
                dangling.Add(tool);
            }

            Assert.True(dangling.Count == 0,
                "verify-live.ps1 names tools that are not in the contract: " + string.Join(", ", dangling) +
                ". Either the tool is published and the name is a typo, or the probe is about a retired tool " +
                "and belongs in $RetiredProbes with its reason, what it covered and what replaced it. A probe " +
                "that answers 'not published by this build' is not coverage - it is a gap that is not a gap.");
        }

        /// <summary>
        /// The four that were retired must STAY retired, with their history intact. A
        /// probe silently revived would start reporting a permanent gap again.
        /// </summary>
        [Theory]
        [InlineData("horizun_connect_mep")]
        [InlineData("horizun_terminate_riser")]
        [InlineData("horizun_place_sprinklers")]
        [InlineData("horizun_family_mirror_void")]
        public void Each_retired_tool_is_registered_and_still_absent_from_the_contract(string tool)
        {
            // If one of these ever returns to the surface, this test is the reminder to
            // bring its probe back rather than leave the registry claiming it is gone.
            Assert.False(PublishedTools().Contains(tool),
                tool + " is published again. Restore its live probe and remove it from $RetiredProbes - the " +
                "registry must describe the surface, not lag behind it.");

            Assert.Contains(tool, RetiredBlock(), StringComparison.Ordinal);
        }

        /// <summary>
        /// A registry row with no history is a deletion wearing a label. Every entry has
        /// to say what it covered, what replaced it, and when it went.
        /// </summary>
        [Fact]
        public void Every_retired_entry_carries_its_justification()
        {
            string retired = RetiredBlock();

            int tools = Regex.Matches(retired, @"Tool\s*=").Count;
            Assert.True(tools >= 4, "expected at least the four known retirements, found " + tools);

            foreach (string field in new[] { "Probes", "Retired", "Covered", "Replacement" })
                Assert.Equal(tools, Regex.Matches(retired, field + @"\s*=").Count);

            // ...and the history must be a sentence, not a placeholder.
            foreach (Match m in Regex.Matches(retired, @"Covered\s*=\s*'([^']*)'"))
                Assert.True(m.Groups[1].Value.Length > 60,
                    "a retired probe records too little about what it covered: '" + m.Groups[1].Value + "'");
        }

        /// <summary>
        /// Retired probes must not be counted as gaps. The script has to exclude them
        /// from the coverage numbers explicitly, and say so where it prints them.
        /// </summary>
        [Fact]
        public void Retired_probes_are_excluded_from_the_coverage_counts()
        {
            string text = Script();

            Assert.Contains("retired probe(s), excluded from the counts above", text, StringComparison.Ordinal);
            Assert.Contains("not a gap", text, StringComparison.Ordinal);
            // And the guard that stops a new one from rotting the same way.
            Assert.Contains("ACTIVE PROBES NAME TOOLS THIS BUILD DOES NOT PUBLISH", text, StringComparison.Ordinal);
        }

        /// <summary>
        /// The rollback guarantee must be PROVOKED on a current tool, not inferred from
        /// whichever command happened to fail. The inferred version went UNVERIFIED the
        /// moment the commands it watched were retired.
        /// </summary>
        [Fact]
        public void The_rollback_probe_provokes_a_failure_on_a_published_tool()
        {
            string text = Script();

            Assert.Contains("execute_plan rolls the WHOLE graph back", text, StringComparison.Ordinal);
            Assert.Contains("horizun_execute_plan", PublishedTools());
            // It asserts against the MODEL, before and after - a command's own claim that
            // it rolled back is the claim under test.
            // Anchored on PIPES: the write fixture is an HVAC model with no wall type,
            // and a probe anchored on a category its fixture lacks reports UNVERIFIED
            // forever - measured, then fixed.
            Assert.Contains("pipeCountBefore", text, StringComparison.Ordinal);
            Assert.Contains("pipeCountAfter", text, StringComparison.Ordinal);
            Assert.Contains("RESIDUE", text, StringComparison.Ordinal);
            // A plan refused during rehearsal proves validation, not rollback.
            Assert.Contains("nothing was rolled ", text, StringComparison.Ordinal);

            // The count-only check was a false pass: a stale-token or confirmation refusal
            // also leaves the count unchanged. The probe now demands the STRUCTURED diagnostic
            // prove the group started, both actions were reached with the right outcomes, and
            // the rollback landed as RolledBack - asserted here so it cannot silently regress.
            Assert.Contains("transaction_group_started", text, StringComparison.Ordinal);
            Assert.Contains("execution_trace", text, StringComparison.Ordinal);
            Assert.Contains("rollback_status", text, StringComparison.Ordinal);
            Assert.Contains("RolledBack", text, StringComparison.Ordinal);
            // A pre-group refusal must be explicitly rejected as "not rollback tested".
            Assert.Contains("proves validation, not", text, StringComparison.Ordinal);

            // Both actions must be valid during rehearsal. The failure is created only
            // by their execution order: delete the real pipe, then try to pin that same
            // id. An intrinsically invalid change_type merely retests rehearsal refusal.
            Assert.Contains("key='delete'; tool='horizun_delete_verified'", text, StringComparison.Ordinal);
            Assert.Contains("operation='pin'; element_ids=@($pipeA)", text, StringComparison.Ordinal);
            Assert.DoesNotContain("type_id=$levelId", text, StringComparison.Ordinal);
        }

        [Fact]
        public void Dialog_probe_reads_structured_content_before_trying_to_parse_human_text()
        {
            string text = Script();
            int probe = text.IndexOf("a dialog raised mid-script is named", StringComparison.Ordinal);
            int useStructured = text.IndexOf("UseStructured = $true", probe, StringComparison.Ordinal);
            int branch = text.IndexOf("A probe about structuredContent gets structuredContent BEFORE", useStructured,
                                      StringComparison.Ordinal);
            int textParse = text.IndexOf("try { $data = $text | ConvertFrom-Json }", branch,
                                         StringComparison.Ordinal);

            Assert.True(probe >= 0 && useStructured > probe,
                "the mid-script dialog probe must opt into structuredContent");
            Assert.True(branch > useStructured && textParse > branch,
                "UseStructured must branch before content.text parsing; dialog narration makes that text non-JSON");
        }

        [Fact]
        public void Closed_workset_probes_are_bracketed_by_verified_document_activation()
        {
            string text = Script();
            int activate = text.IndexOf("$probes += @{ Name = $activateClosedName", StringComparison.Ordinal);
            int scan = text.IndexOf("a CLOSED workset makes model_scan", StringComparison.Ordinal);
            int restore = text.IndexOf("$probes += @{ Name = $restoreDocumentName", StringComparison.Ordinal);
            int cleanup = text.IndexOf("close the closed-workset fixture opened by this harness without saving",
                                       restore, StringComparison.Ordinal);

            Assert.True(activate >= 0 && scan > activate && restore > scan,
                "closed-workset probes must run only between activation and restoration");
            Assert.True(cleanup > restore,
                "cleanup must run after restoration was attempted, including when that attempt failed");
            Assert.Contains("$ClosedWorksetActivationReady", text, StringComparison.Ordinal);
            Assert.Contains("close_workset_names = @($ClosedWorksetName)", text, StringComparison.Ordinal);
            Assert.Contains("Copy-Item -LiteralPath $closedMatches[0].path", text, StringComparison.Ordinal);
            Assert.Contains("HZ_CLOSED_{0}.rvt", text, StringComparison.Ordinal);
            Assert.Contains("$closedInspect.file.is_central -eq $true", text, StringComparison.Ordinal);
            Assert.Contains("$activateClosed.Args.detach = $true", text, StringComparison.Ordinal);
            Assert.Contains("$activateClosed.Args.file_path = $closedPath", text,
                            StringComparison.Ordinal);
            Assert.Contains("$restoreOriginal.Args.file_path = $returnMatches[0].path", text,
                            StringComparison.Ordinal);
            Assert.Contains("$d.active_document_verified -eq $true", text, StringComparison.Ordinal);
            Assert.Contains("$d.workset_configuration_applied -eq $true", text, StringComparison.Ordinal);
            Assert.Contains("$d.workset_configuration_evidence.applied -eq $true", text,
                            StringComparison.Ordinal);
            Assert.Contains("$d.opened_now -eq $true", text, StringComparison.Ordinal);
            Assert.Contains("close the closed-workset fixture opened by this harness without saving", text,
                            StringComparison.Ordinal);
            Assert.Contains("activate_other = $true", text, StringComparison.Ordinal);
            Assert.Contains("$d.saved_on_close -eq $false", text, StringComparison.Ordinal);
            string cleanupBlock = text.Substring(cleanup, Math.Min(1100, text.Length - cleanup));
            Assert.Contains("Needs = 'ClosedWorksetCleanupTarget'", cleanupBlock, StringComparison.Ordinal);
            Assert.DoesNotContain("ErrorIsAlsoPass", cleanupBlock, StringComparison.Ordinal);
            Assert.Contains("structuredContent", text, StringComparison.Ordinal);
            Assert.Contains("opened -eq $true", text, StringComparison.Ordinal);
            Assert.Contains("horizun_health", text, StringComparison.Ordinal);
            Assert.Contains("$cleanupHealth.note -notmatch 'INCOMPLETE'", text,
                            StringComparison.Ordinal);
            Assert.Contains("-not $activationReportedOpened", text, StringComparison.Ordinal);
            Assert.Contains("$cleanupDryArgs.dry_run = $true", text, StringComparison.Ordinal);
            Assert.Contains("$cleanupDry.would_discard_unsaved", text, StringComparison.Ordinal);
            Assert.Contains("$p.Args.discard_unsaved = $true", text, StringComparison.Ordinal);
            Assert.Contains("$p.Args.confirmation_token = [string]$cleanupDry.confirmation_token", text,
                            StringComparison.Ordinal);
            Assert.Contains("$p.Args.save_on_close = $false", text, StringComparison.Ordinal);
            Assert.Contains("$d.discarded_unsaved_changes -eq [bool]$closedCleanupExpectedDiscard", text,
                            StringComparison.Ordinal);
            Assert.DoesNotContain("close_workset_names = @('HZ_WS_CLOSED')", text, StringComparison.Ordinal);
        }

        [Fact]
        public void Disabled_python_probes_are_not_reported_as_passed()
        {
            string text = Script();

            Assert.DoesNotContain("ErrorIsAlsoPass = 'DISABLED|requires BOTH'", text,
                                  StringComparison.Ordinal);
            Assert.DoesNotContain("$p.ErrorIsAlsoPass", text, StringComparison.Ordinal);
            Assert.Contains("ErrorIsNotCovered = 'DISABLED|requires BOTH'", text,
                            StringComparison.Ordinal);
            Assert.Contains("Note-NotCovered $p.Name 'the capability was switched off", text,
                            StringComparison.Ordinal);
        }

        [Fact]
        public void Delete_preview_tests_unresolved_and_real_target_semantics_separately()
        {
            string text = Script();

            Assert.Contains("delete dry run with no resolvable target withholds confirmation", text,
                            StringComparison.Ordinal);
            Assert.Contains("-not $d.confirmation_token", text, StringComparison.Ordinal);
            Assert.Contains("delete dry run issues a confirmation token for a real target", text,
                            StringComparison.Ordinal);
            Assert.Contains("ids = @($pipeA)", text, StringComparison.Ordinal);
            Assert.Contains("$deleteDry.data.application.state -eq 'rehearsed'", text,
                            StringComparison.Ordinal);
        }
    }
}
