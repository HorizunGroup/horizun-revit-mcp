// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// EVERY WRITING TOOL DECLARES HOW IT VERIFIES - and the declaration is checked.
//
// WriteVerificationCatalog is the one table that says, per tool, which mechanism
// produces the verdict, which reply field carries it and where the code lives. These
// tests make it impossible to add a writer without a row, and hard to keep a row that
// lies:
//
//   * "writes" is taken from TWO independent places - the contract's ToolEffect AND
//     the source (a command file that opens DocumentGate.ForMutation). The second is
//     what found horizun_cad_connect classified ReadOnly while it writes through its
//     children; a list kept by hand agrees with itself, not with the code.
//   * a PostconditionChecklist row must actually construct a PostconditionCheck;
//   * every declared evidence field must appear in the row's sources.
//
// Like ApplicationDeclarationWiringTests they read SOURCE: none of these commands can
// be constructed without a Revit. Coarse on purpose.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Horizun.Contracts;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class WriteVerificationCatalogTests
    {
        private static readonly ToolEffect[] Writing =
        {
            ToolEffect.Mutating, ToolEffect.MutatingUnlessDryRun, ToolEffect.DocumentSession,
            ToolEffect.ExternalSideEffect, ToolEffect.ExternalSideEffectOnRequest
        };

        /// <summary>
        /// Command files that open a mutation gate without declaring their own tool name,
        /// and the tools they serve. Adding a file here is a deliberate act; a new file
        /// that fits no rule fails the test instead of being silently skipped.
        /// </summary>
        private static readonly Dictionary<string, string[]> FilesWithoutName = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            // partial class CaptureViewCommand
            { "CaptureViewOptions.cs", new[] { "horizun_capture_view" } },
            // partial class LinkScheduleCommand: operation=write and operation=status_view
            { "LinkScheduleWrite.cs", new[] { "horizun_link_schedule" } },
            { "LinkScheduleStatusView.cs", new[] { "horizun_link_schedule" } },
            // the abstract base of every recipe tool in RecipeTools.cs
            { "RecipeCommand.cs", new[] { "horizun_split_floor_loops", "horizun_split_multilayer_slabs", "horizun_ungroup_and_mark",
                                          "horizun_regroup_by_param", "horizun_copy_slab_elevations", "horizun_embed_floors_in_toposolid",
                                          "horizun_grade_toposolid_around_floors", "horizun_rectangularize_walls" } }
        };

        private static string RepoRoot()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null)
            {
                if (File.Exists(Path.Combine(d.FullName, "AGENTS.md")) &&
                    Directory.Exists(Path.Combine(d.FullName, "src", "Horizun.Revit", "Commands")))
                    return d.FullName;
                d = d.Parent;
            }
            throw new InvalidOperationException("repository root not found from " + AppContext.BaseDirectory);
        }

        private static string Src(string relative) => Path.Combine(RepoRoot(), "src", relative.Replace('/', Path.DirectorySeparatorChar));

        private static HashSet<string> WritingTools() => new HashSet<string>(
            Contract.All.Where(c => Writing.Contains(c.Effect)).Select(c => c.Name), StringComparer.Ordinal);

        [Fact]
        public void Every_writing_tool_declares_exactly_one_verification_row()
        {
            var rows = WriteVerificationCatalog.Rows;
            var duplicates = rows.GroupBy(r => r.Tool).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
            Assert.True(duplicates.Count == 0, "declared twice: " + string.Join(", ", duplicates));

            var declared = new HashSet<string>(rows.Select(r => r.Tool), StringComparer.Ordinal);
            var missing = WritingTools().Where(t => !declared.Contains(t)).OrderBy(t => t).ToList();
            Assert.True(missing.Count == 0,
                "These tools can write (their ToolEffect says so) and declare no verification mechanism. Add a row " +
                "to WriteVerificationCatalog.Rows saying HOW the reply's verdict is produced, which field carries " +
                "it and where it lives: " + string.Join(", ", missing));
        }

        [Fact]
        public void No_row_names_a_tool_that_does_not_write()
        {
            var writing = WritingTools();
            var stale = WriteVerificationCatalog.Rows.Select(r => r.Tool).Where(t => !writing.Contains(t)).ToList();
            Assert.True(stale.Count == 0,
                "These rows name tools the contract does not classify as writing (renamed? reclassified?): " +
                string.Join(", ", stale));
        }

        [Fact]
        public void A_command_that_opens_a_mutation_gate_is_classified_as_writing_and_declared()
        {
            string dir = Path.Combine(RepoRoot(), "src", "Horizun.Revit", "Commands");
            var byName = Contract.All.ToDictionary(c => c.Name, StringComparer.Ordinal);
            var declared = new HashSet<string>(WriteVerificationCatalog.Rows.Select(r => r.Tool), StringComparer.Ordinal);
            var nameDecl = new Regex(@"(?:Name\s*=>|ToolName\s*=)\s*""(horizun_[a-z0-9_]+)""");
            var failures = new List<string>();
            int gated = 0;
            foreach (string path in Directory.GetFiles(dir, "*.cs"))
            {
                string file = Path.GetFileName(path);
                string text = File.ReadAllText(path);
                if (!text.Contains("DocumentGate.ForMutation(")) continue;
                gated++;
                var tools = nameDecl.Matches(text).Cast<Match>().Select(m => m.Groups[1].Value).Distinct().ToList();
                if (tools.Count == 0 && FilesWithoutName.TryGetValue(file, out string[] mapped)) tools = mapped.ToList();
                if (tools.Count == 0)
                {
                    failures.Add(file + " opens DocumentGate.ForMutation but declares no tool name (Name => / ToolName =) " +
                                 "and is not mapped in FilesWithoutName");
                    continue;
                }
                foreach (string tool in tools)
                {
                    if (!byName.TryGetValue(tool, out CommandContract contract))
                    { failures.Add(file + ": " + tool + " has no contract"); continue; }
                    if (!Writing.Contains(contract.Effect))
                        failures.Add(file + ": " + tool + " opens a mutation gate but its contract says " + contract.Effect +
                                     " - read_only admits it and readOnlyHint tells every client it is safe");
                    if (!declared.Contains(tool))
                        failures.Add(file + ": " + tool + " writes and has no WriteVerificationCatalog row");
                }
            }
            Assert.True(gated >= 30, "found only " + gated + " gated command files; the scan is looking in the wrong place");
            Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
        }

        [Fact]
        public void Every_row_names_sources_that_exist_and_evidence_they_carry()
        {
            var failures = new List<string>();
            foreach (WriteVerification row in WriteVerificationCatalog.Rows)
            {
                if (row.Sources == null || row.Sources.Length == 0) { failures.Add(row.Tool + ": no sources"); continue; }
                if (row.Evidence == null || row.Evidence.Length == 0) { failures.Add(row.Tool + ": no evidence field"); continue; }
                var texts = new List<string>();
                foreach (string source in row.Sources)
                {
                    string path = Src(source);
                    if (!File.Exists(path)) { failures.Add(row.Tool + ": source does not exist: " + source); continue; }
                    texts.Add(File.ReadAllText(path));
                }
                string all = string.Join("\n", texts);
                foreach (string field in row.Evidence)
                {
                    bool carried = field == "application"
                        ? all.Contains("ApplicationOutcome.Stamp")
                        : all.Contains("\"" + field + "\"") || Regex.IsMatch(all, @"\b" + Regex.Escape(field) + @"\s*=");
                    if (!carried) failures.Add(row.Tool + ": evidence field '" + field + "' appears nowhere in its sources");
                }
                if (row.Mechanism == VerificationMechanism.PostconditionChecklist && !all.Contains("new PostconditionCheck("))
                    failures.Add(row.Tool + ": declared PostconditionChecklist but its sources never construct a PostconditionCheck");
                foreach (string gap in row.KnownGaps ?? new string[0])
                    if (string.IsNullOrWhiteSpace(gap)) failures.Add(row.Tool + ": an empty known-gap entry");
            }
            Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
        }

        // ---- the fixes this catalog was written with -------------------------------------

        private static string Command(string file) => File.ReadAllText(Src("Horizun.Revit/Commands/" + file));

        [Fact]
        public void Transform_tag_verification_is_a_checklist_and_never_invents_a_read()
        {
            string src = Command("TransformElementsCommand.cs");
            Assert.Contains("new PostconditionCheck(TagProperties(p)", src);
            // The old fallback reported an unreadable leader flag as the OPPOSITE of the request.
            Assert.DoesNotContain("catch { v = !p.HasLeader.Value; }", src);
            Assert.DoesNotContain("catch { v = !p.LeaderVisible.Value; }", src);
            Assert.Contains("return p.Ids.Count > 0 && good == p.Ids.Count;", src);
        }

        [Fact]
        public void Create_elements_production_properties_are_a_checklist_too()
        {
            string src = Command("CreateElementsProductionVerification.cs");
            Assert.Contains("PostconditionCheck production = ProductionChecklist(", src);
            Assert.Contains("production.AllVerified", src);
            Assert.Contains("[\"production_postconditions\"]", src);
            Assert.Contains("[\"unreadable_connectors\"]", src);
        }

        [Fact]
        public void An_unreadable_pin_state_is_not_an_unpin()
        {
            string src = Command("ManageLinksCommand.cs");
            Assert.DoesNotContain("bool pinnedAfter = Safe<bool>(() => (doc.GetElement(instance.Id) as RevitLinkInstance)?.Pinned) == true;", src);
            Assert.Contains("pinnedRead.HasValue && pinnedRead.Value == pin", src);
        }

        [Fact]
        public void Verdicts_that_were_literals_are_now_derived()
        {
            Assert.Contains("verifiedRows == plans.Count", Command("ManageMaterialsCommand.cs"));
            Assert.Contains("copyVerified ? \"committed_verified\"", Command("CopyBetweenDocumentsCommand.cs"));
            Assert.Contains("[\"host_verified\"] = committed && loaded && afterPrint != null", Command("ManageCadLinksCommand.cs"));
            Assert.Contains("allProvableHeld && provable > 0", Command("ExportCommand.cs"));
            Assert.Contains("VerifyReopenedCatalogue(reopened, plan)", Command("CreateFamilyCommand.cs"));
            Assert.Contains("coverage_complete\") != true", Command("ApplyIfcPlanCommand.cs"));
            Assert.Contains("changes nothing: pass description", Command("ManageRevisionsCommand.cs"));
            Assert.Contains("__removed_field_ids", Command("ManageSchedulesCommand.cs"));
            Assert.Contains("legend == null || legend.Count == 0", Command("ManageViewsGraphics.cs"));
            Assert.Contains("RecipeVerdict.Decide(counts, errorsReported)", Command("RecipeCommand.cs"));
            Assert.Contains("Guard.VerifyRequested(\"deletions\"", Command("DeleteCommand.cs"));
            Assert.Contains("Guard.VerifyRequested(\"keynote writes\"", Command("SetKeynoteCommand.cs"));
        }
    }

    public class VerificationArithmeticTests
    {
        private static List<KeyValuePair<int, int>> Counts(params int[] pairs)
        {
            var list = new List<KeyValuePair<int, int>>();
            for (int i = 0; i < pairs.Length; i += 2) list.Add(new KeyValuePair<int, int>(pairs[i], pairs[i + 1]));
            return list;
        }

        [Fact]
        public void Two_quantities_nobody_reported_do_not_agree()
        {
            Assert.False(Reconcile.Verified(-1, -1));
            Assert.False(Reconcile.Measured(-1, 3));
            Assert.True(Reconcile.Verified(0, 0));
            Assert.True(Reconcile.Verified(4, 4));
        }

        [Fact]
        public void Work_that_was_asked_for_and_compared_nothing_is_not_verified()
        {
            Assert.False(Reconcile.VerifiedWork(3, 0, 0));   // three asked, none reached a comparison
            Assert.True(Reconcile.VerifiedWork(0, 0, 0));    // nothing asked, nothing done
            Assert.True(Reconcile.VerifiedWork(3, 3, 3));
            Assert.False(Reconcile.VerifiedWork(3, 3, 2));
        }

        [Fact]
        public void A_recipe_whose_every_element_failed_is_not_verified_and_declares_failed()
        {
            // Ten floors asked, ten failures: both counts 0, errors 10.
            RecipeVerdict.Result r = RecipeVerdict.Decide(Counts(0, 0, 0, 0), errorsReported: 10);
            Assert.False(r.AllVerified);
            Assert.False(r.NothingChanged);
            Assert.Equal(ApplicationState.Failed, ApplicationOutcome.Applied(ApplicationOutcome.Committed,
                r.Requested, r.Applied, r.Verified, 0, r.Failed, r.Unknown));
        }

        [Fact]
        public void A_recipe_with_some_failed_elements_is_partial()
        {
            RecipeVerdict.Result r = RecipeVerdict.Decide(Counts(8, 8, 8, 8), errorsReported: 2);
            Assert.False(r.AllVerified);
            Assert.Equal(ApplicationState.Partial, ApplicationOutcome.Applied(ApplicationOutcome.Committed,
                r.Requested, r.Applied, r.Verified, 0, r.Failed, r.Unknown));
        }

        [Fact]
        public void A_recipe_with_nothing_to_do_is_no_op_not_verified_applied()
        {
            RecipeVerdict.Result r = RecipeVerdict.Decide(Counts(0, 0), errorsReported: 0);
            Assert.True(r.AllVerified);
            Assert.True(r.NothingChanged);
            Assert.Equal(ApplicationState.NoOp, ApplicationOutcome.Applied(ApplicationOutcome.Committed,
                r.Requested, r.Applied, r.Verified, 0, r.Failed, r.Unknown));
        }

        [Fact]
        public void An_unreported_count_is_unknown_and_a_clean_run_is_verified_applied()
        {
            RecipeVerdict.Result unknown = RecipeVerdict.Decide(Counts(5, 5, -1, -1), errorsReported: 0);
            Assert.False(unknown.AllVerified);
            Assert.Equal(ApplicationState.Uncertain, ApplicationOutcome.Applied(ApplicationOutcome.Committed,
                unknown.Requested, unknown.Applied, unknown.Verified, 0, unknown.Failed, unknown.Unknown));

            RecipeVerdict.Result clean = RecipeVerdict.Decide(Counts(5, 5, 5, 5), errorsReported: 0);
            Assert.True(clean.AllVerified);
            Assert.Equal(ApplicationState.VerifiedApplied, ApplicationOutcome.Applied(ApplicationOutcome.Committed,
                clean.Requested, clean.Applied, clean.Verified, 0, clean.Failed, clean.Unknown));

            RecipeVerdict.Result mismatch = RecipeVerdict.Decide(Counts(5, 4), errorsReported: 0);
            Assert.False(mismatch.AllVerified);
            RecipeVerdict.Result none = RecipeVerdict.Decide(Counts(), errorsReported: 0);
            Assert.False(none.AllVerified);
            Assert.Equal(ApplicationState.Uncertain, ApplicationOutcome.Applied(ApplicationOutcome.Committed,
                none.Requested, none.Applied, none.Verified, 0, none.Failed, none.Unknown));
        }
    }
}
