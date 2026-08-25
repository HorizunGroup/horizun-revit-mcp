// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// EVERY STRUCTURAL REFUSAL IN THE TYPED SURFACE, CLASSIFIED - and a scanner that
// fails when a new one appears unclassified.
//
// The Python fallback is only safe because "this bridge cannot do that" is
// distinguishable from "your arguments are wrong" and from "it broke halfway".
// That distinction lives at each refusal site, and a site added later without a
// decision would silently land in the third category by accident - either
// granting a fallback after a write, or withholding one that should exist.
//
// So the inventory is data, and the scanner is the gate. Three classifications:
//
//   StructuralGranted - a capability gap decided BEFORE any write. Carries
//                       UnsupportedCapability and reaches FallbackDecision.
//   Argument          - a value the caller can correct and retry typed. No
//                       fallback: suggesting Python would send a client to write
//                       a script around their own typo.
//   PostWrite         - reachable once a transaction may be open. No fallback at
//                       any price: a Python "retry" there is a second write.
//
// Adding a refusal without adding a row here fails this test with the choice
// spelled out. That is the intent: the classification is cheap while the code is
// being written and archaeology afterwards.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Horizun.Core.Tests
{
    public class CapabilityInventoryTests
    {
        private enum Kind { StructuralGranted, Argument, PostWrite }

        private sealed class Entry
        {
            public string File;
            public string Fragment;   // unique text at the refusal site
            public Kind Classification;
            public string Why;
        }

        /// <summary>
        /// THE AUDIT. Every structural refusal the scanner below can see, and what was
        /// decided about it. Reviewed 2026-08-04 over the whole of src/Horizun.Revit/Commands.
        /// </summary>
        private static readonly Entry[] Inventory =
        {
            new Entry {
                File = "AnnotateCommand.cs", Fragment = "horizun_annotate creates text, tags and dimensions",
                Classification = Kind.StructuralGranted,
                Why = "The operation enum is the command's whole contract; an unlisted one has no typed path. " +
                      "Raised while planning, before any transaction." },

            new Entry {
                File = "CreateElementsCommand.cs", Fragment = "horizun_create_elements implements a fixed set",
                Classification = Kind.StructuralGranted,
                Why = "An element kind this command does not implement. Raised in PlanItem, before the " +
                      "transaction opens." },

            new Entry {
                File = "CreateElementsCommand.cs", Fragment = "InvalidOperationException(\"unsupported kind '\" + p.Kind",
                Classification = Kind.PostWrite,
                Why = "The mirror switch inside Create(), which runs INSIDE the transaction. Defensive and " +
                      "unreachable in practice - PlanItem already refused unknown kinds - but if it ever fires, " +
                      "elements may already have been created, so it must never grant a fallback." },

            new Entry {
                File = "TransformElementsCommand.cs", Fragment = "horizun_transform_elements does move, copy",
                Classification = Kind.StructuralGranted,
                Why = "An operation outside move/copy/rotate/pin/unpin/change_type. Raised while planning." },

            new Entry {
                File = "ManageViewsCommand.cs", Fragment = "horizun_manage_views implements a fixed",
                Classification = Kind.StructuralGranted,
                Why = "An operation outside the documented view/sheet/viewport set. Raised in Validate(), " +
                      "before the transaction." },

            new Entry {
                File = "ManageViewsCommand.cs", Fragment = "InvalidOperationException(\"unsupported operation\")",
                Classification = Kind.PostWrite,
                Why = "The mirror switch inside Apply(), inside the transaction. Same reasoning as " +
                      "CreateElements: unreachable via Validate(), and post-write if it ever is not." },

            new Entry {
                File = "CreateFamilyCommand.cs", Fragment = "unsupported storage type for '",
                Classification = Kind.PostWrite,
                Why = "Reached while writing parameter values into the family document, with the family " +
                      "transaction open. The family may already be partly built." },

            new Entry {
                File = "CreateFamilyCommand.cs", Fragment = "data_type '",
                Classification = Kind.Argument,
                Why = "data_type is a closed enum in the input schema. A value outside it is a request the " +
                      "caller can correct and resend typed - not a capability this bridge lacks." },

            new Entry {
                File = "CreateFamilyCommand.cs", Fragment = "parameter group '",
                Classification = Kind.Argument,
                Why = "Same as data_type: a closed enum in the schema, so an unknown group is a typo, not a gap." },

            new Entry {
                File = "ManageSystemTypesCommand.cs", Fragment = "ArgumentException(\"unsupported storage type",
                Classification = Kind.PostWrite,
                Why = "Reached while writing a duplicated type's parameters inside the transaction. Revit " +
                      "storage types this command cannot write are real gaps, but by the time one is seen the " +
                      "duplicate may already exist, so the safe answer is the real state, never a retry." },

            new Entry {
                File = "ManageSystemTypesCommand.cs", Fragment = "InvalidOperationException(\"unsupported storage type",
                Classification = Kind.PostWrite,
                Why = "The read-back half of the same write, likewise inside the transaction." },

            new Entry {
                File = "ExportCommand.cs", Fragment = "has unsupported value '",
                Classification = Kind.Argument,
                Why = "An export option outside its documented set. The caller fixes the field; no script is " +
                      "needed and none is suggested." },

            new Entry {
                File = "AnnotateCommand.cs", Fragment = "DimensionPlanRules.NoApiAnyYear(op)",
                Classification = Kind.Argument,
                Why = "spot_slope: no creation API exists in ANY Revit 2023-2027, so a Python fallback would " +
                      "be a doomed script, not a workaround. Deliberately an ArgumentException rather than " +
                      "UnsupportedCapability so FallbackDecision never grants what nothing can honour; the " +
                      "message says the API is absent everywhere." },

            new Entry {
                File = "AnnotateCommand.cs", Fragment = "\"RadialDimension.Create\", 2025",
                Classification = Kind.Argument,
                Why = "radial/diameter dimensions on Revit 2023/2024: RadialDimension.Create arrived in 2025, " +
                      "and there is no other route - Python runs against the same API and cannot reach it " +
                      "either. The refusal names the API and the year that introduces it, and grants no " +
                      "fallback for the same reason as spot_slope." },

            new Entry {
                File = "AnnotateCommand.cs", Fragment = "\"ArcLengthDimension.Create\", 2025",
                Classification = Kind.Argument,
                Why = "arc-length dimensions on Revit 2023/2024: same shape as the radial refusal above - the " +
                      "API arrives in 2025 and Python cannot conjure it earlier." },

            new Entry {
                File = "AnnotateCommand.cs", Fragment = "resolves into a LINKED model",
                Classification = Kind.Argument,
                Why = "A stable reference that resolves into an RVT link. Consuming linked references in " +
                      "dimension creation is not proven live, so offering them typed - or via a Python " +
                      "fallback that would hit the same unproven path - would present a guess as a " +
                      "capability. Raised while planning, before any transaction." },

            new Entry {
                File = "Detail2DCommand.cs", Fragment = "unsupported operation '",
                Classification = Kind.StructuralGranted,
                Why = "The operation enum is the command's whole contract; an unlisted one has no typed path. " +
                      "Raised while planning, before any transaction, and handed to FallbackDecision over the " +
                      "whole batch - the AnnotateCommand shape exactly." },

            new Entry {
                File = "EditDimensionsCommand.cs", Fragment = "unsupported action field '",
                Classification = Kind.StructuralGranted,
                Why = "An action field outside the edit contract (say, text_position). Raised while planning, " +
                      "before any transaction; Python CAN reach dimension members this command does not type, " +
                      "so the gap is granted to FallbackDecision over the whole batch." },

            new Entry {
                File = "FixPlanimetryCommand.cs", Fragment = "unsupported operation '",
                Classification = Kind.StructuralGranted,
                Why = "The fix catalog is closed on purpose - packing, tagging and revision generation are " +
                      "later phases. An operation outside it has no typed path; raised while planning, before " +
                      "any transaction, and handed to FallbackDecision over the whole batch. The other " +
                      "structural refusals in this command (non-rectangular crop, an API-absent " +
                      "ScheduleSheetInstance.Point setter) do not use the scanner's keywords: the crop is an " +
                      "UnsupportedCapability that reaches the same decision, and the API absence deliberately " +
                      "stays an ArgumentException because Python faces the same absent setter." },

            new Entry {
                File = "EditDimensionsCommand.cs", Fragment = "not supported by the Revit API itself",
                Classification = Kind.Argument,
                Why = "replace_references and its aliases. Dimension.References has no setter in ANY Revit " +
                      "2023-2027, so a Python fallback would be a doomed script, not a workaround - the " +
                      "refusal deliberately stays an ArgumentException that names the honest route: delete " +
                      "and recreate through horizun_annotate. 'Argument' here means the CALLER can fix the " +
                      "approach, not that a value was mistyped." },
        };

        /// <summary>
        /// What counts as a structural refusal worth classifying. Deliberately broad -
        /// the cost of a false hit is one inventory row, and the cost of a miss is an
        /// unreviewed refusal.
        /// </summary>
        private static readonly Regex Rejection = new Regex(
            @"(?i)\b(unsupported|not\s+supported|NotSupportedException|not\s+implemented|out\s+of\s+contract)\b",
            RegexOptions.Compiled);

        /// <summary>
        /// The plumbing that merely CARRIES a classification is not itself a refusal:
        /// parameter names, the reason lookup, the assignment. Excluding them keeps the
        /// inventory about decision points.
        /// </summary>
        private static bool IsPlumbing(string line) =>
            line.Contains("out string unsupportedReason") ||
            line.Contains("unsupportedReason = ") ||
            line.Contains("UnsupportedCapability.ReasonOf(") ||
            line.Contains("string unsupportedReason") ||
            line.TrimStart().StartsWith("//", StringComparison.Ordinal) ||
            line.TrimStart().StartsWith("///", StringComparison.Ordinal) ||
            line.TrimStart().StartsWith("*", StringComparison.Ordinal);

        private static string CommandsDir()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !Directory.Exists(Path.Combine(d.FullName, "src", "Horizun.Revit", "Commands")))
                d = d.Parent;
            Assert.True(d != null, "Could not locate src/Horizun.Revit/Commands");
            return Path.Combine(d.FullName, "src", "Horizun.Revit", "Commands");
        }

        /// <summary>
        /// The one file excluded from the scan, and why. ExecutePythonCommand IS the
        /// fallback: it has no typed capability edge to fall off, and its own description
        /// legitimately discusses answering "not supported", which the scanner would
        /// otherwise read as a refusal site. The exclusion is guarded by
        /// The_excluded_file_cannot_hide_a_grant below, so it cannot become a hiding place.
        /// </summary>
        private const string ExcludedFile = "ExecutePythonCommand.cs";

        private static IEnumerable<(string File, string Line)> Rejections()
        {
            foreach (string path in Directory.EnumerateFiles(CommandsDir(), "*Command.cs").OrderBy(p => p))
            {
                string name = Path.GetFileName(path);
                if (string.Equals(name, ExcludedFile, StringComparison.Ordinal)) continue;
                foreach (string raw in File.ReadAllLines(path))
                {
                    string line = raw;
                    if (!Rejection.IsMatch(line)) continue;
                    if (IsPlumbing(line)) continue;
                    yield return (name, line);
                }
            }
        }

        /// <summary>
        /// THE GATE. Every structural refusal in the tree is accounted for. A new one
        /// fails here with the three choices spelled out.
        /// </summary>
        [Fact]
        public void Every_structural_refusal_is_classified()
        {
            var unclassified = new List<string>();

            foreach (var (file, line) in Rejections())
            {
                bool known = Inventory.Any(e =>
                    string.Equals(e.File, file, StringComparison.Ordinal) && line.Contains(e.Fragment));
                if (!known) unclassified.Add(file + ": " + line.Trim());
            }

            Assert.True(unclassified.Count == 0,
                "These structural refusals are not in the capability inventory:\n  " +
                string.Join("\n  ", unclassified) +
                "\n\nClassify each one and add a row to CapabilityInventoryTests.Inventory:\n" +
                "  StructuralGranted - no typed path exists AND the refusal happens before any write. It must " +
                "throw UnsupportedCapability and reach FallbackDecision.\n" +
                "  Argument          - the caller can fix the value and retry typed. No fallback.\n" +
                "  PostWrite         - reachable once a transaction may be open. No fallback, ever.");
        }

        /// <summary>
        /// The inventory must not rot in the other direction either: a row whose site was
        /// deleted or reworded would keep passing the gate above while describing nothing.
        /// </summary>
        [Fact]
        public void Every_inventory_row_still_matches_a_real_site()
        {
            var stale = new List<string>();
            var all = Rejections().ToList();

            foreach (Entry e in Inventory)
            {
                bool found = all.Any(r =>
                    string.Equals(r.File, e.File, StringComparison.Ordinal) && r.Line.Contains(e.Fragment));
                if (!found) stale.Add(e.File + " :: " + e.Fragment);
            }

            Assert.True(stale.Count == 0,
                "These inventory rows match no refusal in the tree any more: " + string.Join(", ", stale) +
                ". Remove the row, or fix the fragment - a stale row hides a real site from the gate.");
        }

        /// <summary>
        /// Only the StructuralGranted rows may live in a command that reaches the
        /// fallback decision. A file classified entirely as Argument/PostWrite that
        /// started emitting a fallback would be a grant nobody reviewed.
        /// </summary>
        [Fact]
        public void Only_commands_with_a_granted_row_emit_the_fallback()
        {
            var granting = new HashSet<string>(
                Inventory.Where(e => e.Classification == Kind.StructuralGranted).Select(e => e.File),
                StringComparer.Ordinal);

            foreach (string path in Directory.EnumerateFiles(CommandsDir(), "*Command.cs"))
            {
                string name = Path.GetFileName(path);
                bool emits = File.ReadAllText(path).Contains("FallbackDecision.");
                if (emits)
                    Assert.True(granting.Contains(name),
                        name + " emits the Python fallback but has no StructuralGranted row in the capability " +
                        "inventory. Classify the refusal it is granting for.");
            }

            // ...and the reverse: a granted classification with no wiring is a gap that
            // silently answers "not supported" instead of handing over the fallback.
            foreach (string file in granting)
                Assert.Contains("FallbackDecision.",
                    File.ReadAllText(Path.Combine(CommandsDir(), file)), StringComparison.Ordinal);
        }

        /// <summary>
        /// Every PostWrite and Argument row must be honest about the corollary: those
        /// sites must NOT be throwing UnsupportedCapability, which is the type that
        /// carries a grant to the decision.
        /// </summary>
        [Fact]
        public void Argument_and_post_write_sites_do_not_carry_the_granting_type()
        {
            foreach (Entry e in Inventory.Where(x => x.Classification != Kind.StructuralGranted))
            {
                string text = File.ReadAllText(Path.Combine(CommandsDir(), e.File));
                foreach (string line in text.Split('\n'))
                {
                    if (!line.Contains(e.Fragment)) continue;
                    Assert.False(line.Contains("new UnsupportedCapability("),
                        e.File + " throws UnsupportedCapability at a site classified " + e.Classification +
                        ": " + line.Trim() + ". That type is the grant; " +
                        (e.Classification == Kind.PostWrite
                            ? "a write may already have landed here."
                            : "the caller can fix this by sending different arguments."));
                }
            }
        }

        /// <summary>
        /// The exclusion above must stay a statement about prose, not a blind spot. The
        /// Python command may not grant a fallback to itself under any spelling.
        /// </summary>
        [Fact]
        public void The_excluded_file_cannot_hide_a_grant()
        {
            string text = File.ReadAllText(Path.Combine(CommandsDir(), ExcludedFile));

            Assert.DoesNotContain("new UnsupportedCapability(", text);
            // No granting primitive under any spelling: the removed FailUnsupported, the
            // internal factory, the raw signal grant, or the central decision itself.
            Assert.DoesNotContain("CommandResult.FailUnsupported", text);
            Assert.DoesNotContain("CommandResult.FailWithFallback", text);
            Assert.DoesNotContain("FallbackSignal.Allowed(", text);
            Assert.DoesNotContain("FallbackDecision.", text);
        }

        /// <summary>The audit is only useful if each decision says WHY.</summary>
        [Fact]
        public void Every_row_records_its_reasoning()
        {
            foreach (Entry e in Inventory)
            {
                Assert.False(string.IsNullOrWhiteSpace(e.Why), e.File + " :: " + e.Fragment + " has no reason");
                Assert.True(e.Why.Length > 40, e.File + " :: " + e.Fragment + " has a reason too short to review");
            }
        }
    }
}
