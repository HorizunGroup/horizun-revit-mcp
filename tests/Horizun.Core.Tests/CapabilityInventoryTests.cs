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
                File = "RoomFinishesCommand.cs", Fragment = "boundary gap is within tolerance but repair of",
                Classification = Kind.Argument,
                Why = "A per-room boundary assessment caught during planning, before any write. Only Line/Arc endpoint " +
                      "repairs are admitted; the room is named in rejected_rooms and other valid rooms continue. " +
                      "The caller can correct the source boundary. A mixed valid/invalid room batch must not " +
                      "grant a second write path through Python." },

            new Entry {
                File = "ManageCadLinksCommand.cs", Fragment = "horizun_manage_cad_links does list, add",
                Classification = Kind.StructuralGranted,
                Why = "The operation enum is this command's whole contract, and an unlisted one has no typed " +
                      "path. Raised in the dispatch switch, before the transaction opens - and BEFORE it, " +
                      "'unload' is refused by name with the API fact behind it, because there is no CAD unload " +
                      "in any Revit 2023-2027 and a fallback would only find the same absence." },

            new Entry {
                File = "ManageMaterialsCommand.cs", Fragment = "Appearance edit unsupported here",
                Classification = Kind.StructuralGranted,
                Why = "set_appearance on an appearance schema without a supported flat colour or transparency, or with no Generic " +
                      "library asset for replace_with_generic. Raised while planning, before the transaction opens; set_appearance " +
                      "is the only action of its batch, so the single ActionOutcome is the whole batch." },

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
                File = "PlanFromIfcCommand.cs", Fragment = "public static ClassVerdict Unsupported(string reason)",
                Classification = Kind.Argument,
                Why = "The FACTORY that makes a class-table verdict. It builds the statement the table below " +
                      "is written in; it refuses nothing by itself, and it is recorded here because the " +
                      "scanner reads lines and a method named after an outcome looks like one." },

            new Entry {
                File = "PlanFromIfcCommand.cs", Fragment = "ClassVerdict.Unsupported(",
                Classification = Kind.Argument,
                Why = "The IFC class table: classes this route does not convert, DECLARED up front rather than " +
                      "discovered at run time. The call succeeds and the plan reports the verdict per class, so " +
                      "nothing is refused and nothing is written - the caller's recourse is to ask for what this " +
                      "route does convert, or to bring that geometry in another way. Deliberately NOT " +
                      "StructuralGranted: this command grants no Python fallback and should not, because the " +
                      "answer is a statement about coverage rather than a failure to act." },

            new Entry {
                File = "PlanFromIfcCommand.cs", Fragment = "The apply half reports every field as updated",
                Classification = Kind.Argument,
                Why = "PROSE, not a decision: the tool's own description listing the four words its reply uses. " +
                      "It matches the scanner because it contains one of them. Recorded rather than excluded by " +
                      "shape, because from one line's distance a refusal message looks exactly like this and a " +
                      "shape rule would hide real ones." },

            new Entry {
                File = "PlanFromIfcCommand.cs", Fragment = "unchanged, unsupported or refused.",
                Classification = Kind.Argument,
                Why = "The second half of the same sentence of prose, matched for the same reason and " +
                      "recorded rather than hidden by a shape rule." },

            new Entry {
                File = "ApplyIfcPlanCommand.cs", Fragment = "int updated = 0, unchanged = 0, refused = 0",
                Classification = Kind.Argument,
                Why = "A TALLY, not a decision: the counters this command reports its per-field outcome with. " +
                      "Counting how many fields could not be updated refuses nothing; the reply says what " +
                      "happened and the caller decides. It is recorded here because the scanner reads lines and " +
                      "a variable named after an outcome looks like one." },

            new Entry {
                File = "ApplyIfcPlanCommand.cs", Fragment = "outcomes.Count(o => o.State == IfcUpdate.Unsupported)",
                Classification = Kind.Argument,
                Why = "The same tally, read back so the reply can say how many fields this apply could " +
                      "not update. Reading a counter is not a decision about anything." },

            new Entry {
                File = "ApplyIfcPlanCommand.cs", Fragment = "else unsupported++",
                Classification = Kind.Argument,
                Why = "The same tally, incremented when one field could not be updated. The decision " +
                      "was made earlier, by whatever set that field's state." },

            new Entry {
                File = "ApplyIfcPlanCommand.cs", Fragment = "[\"partially_updated\"] = unsupported",
                Classification = Kind.Argument,
                Why = "The same tally, written into the reply under the name a reader will look for." },

            new Entry {
                File = "ApplyIfcPlanCommand.cs", Fragment = "refused == 0 && unsupported == 0",
                Classification = Kind.Argument,
                Why = "The same tally, asked whether it is empty - which is how this command decides " +
                      "whether to call its own coverage complete." },

            new Entry {
                File = "ApplyIfcPlanCommand.cs", Fragment = "refused + unsupported + skipped, 0);",
                Classification = Kind.Argument,
                Why = "The same tally again, folded into the composite application block's 'failed' count on a " +
                      "real apply (updated/unchanged are 'applied'; refused/unsupported/skipped are not). A " +
                      "count, not a decision - nothing here refuses anything." },

            new Entry {
                File = "CreateElementsCommand.cs", Fragment = "route == CadPlacementRoute.Unsupported",
                Classification = Kind.PostWrite,
                Why = "A family whose FamilyPlacementType this command has no route for - measured from the " +
                      "family itself, inside Create(), because only Revit can say what a family needs. It runs " +
                      "INSIDE the transaction, so earlier rows in the batch may already exist and it must " +
                      "never grant a fallback: the batch is rolled back atomically instead. The three routes " +
                      "it does have are a level, a host element and a face." },

            new Entry {
                File = "CreateElementsCommand.cs", Fragment = "InvalidOperationException(\"unsupported kind '\" + p.Kind",
                Classification = Kind.PostWrite,
                Why = "The mirror switch inside Create(), which runs INSIDE the transaction. Defensive and " +
                      "unreachable in practice - PlanItem already refused unknown kinds - but if it ever fires, " +
                      "elements may already have been created, so it must never grant a fallback." },

            new Entry {
                File = "CreateElementsCommand.cs", Fragment = "InvalidOperationException(\"unsupported fitting '\" + p.FittingSubtype",
                Classification = Kind.PostWrite,
                Why = "The fitting subtype switch inside Create(), the same mirror as the kind switch above: " +
                      "PlanItem already refused unknown subtypes before any transaction, so this is defensive, " +
                      "runs inside the transaction, and must never grant a fallback." },

            new Entry {
                File = "TransformElementsCommand.cs", Fragment = "horizun_transform_elements does move, move_preserve_pin, copy",
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
                File = "ManageSchedulesCommand.cs", Fragment = "return \"unsupported operation '\" + op",
                Classification = Kind.StructuralGranted,
                Why = "An operation outside the documented schedule-definition set. Raised in Validate(), " +
                      "before the transaction, with ReasonUnsupportedOperation - the ManageViews shape exactly." },

            new Entry {
                File = "ManageSchedulesCommand.cs", Fragment = "InvalidOperationException(\"unsupported operation '\" + op",
                Classification = Kind.PostWrite,
                Why = "The mirror switch inside Apply(), inside the transaction. Unreachable via Validate(), " +
                      "and post-write if it ever is not." },

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
                File = "AnnotateCommand.Execution.cs", Fragment = "DimensionPlanRules.SpotSlopeRefusal(",
                Classification = Kind.Argument,
                Why = "spot_slope: no creation API exists in Revit 2023-2027. The planner carries an " +
                      "explicit revit_api_unavailable denial to FallbackDecision before any write." },

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

            // The row that used to sit here refused every reference resolving into an RVT
            // link, on the ground that consuming one was not proven live. It IS proven
            // live now (see docs/LINKED-DIMENSIONS.md and the linked cases in the live
            // matrix), so the refusal is gone rather than reclassified. What replaced it
            // are ordinary argument errors - an unloaded link, a missing linked element,
            // a nested link - none of which is phrased as a capability limit, which is
            // why none of them appears in this inventory.

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
                File = "MepRoutingCommand.cs", Fragment = "which is unsupported by resize: it covers pipes, ducts, conduits, cable trays and flex runs",
                Classification = Kind.StructuralGranted,
                Why = "since 2026-09-26 resize also types flex pipes and flex ducts; any OTHER MEPCurve (a wire, " +
                      "a fabrication part's run) is still outside it. Judged while planning, per element, before " +
                      "any transaction, and handed to FallbackDecision over the whole batch." },

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
            @"(?i)\b(unsupported|not\s+supported|NotSupportedException|not\s+implemented|out\s+of\s+contract|SpotSlopeRefusal)\b",
            RegexOptions.Compiled);

        /// <summary>
        /// The plumbing that merely CARRIES a classification is not itself a refusal:
        /// parameter names, the reason lookup, the assignment. Excluding them keeps the
        /// inventory about decision points.
        /// </summary>
        private static bool IsPlumbing(string line) =>
            line.Contains("out string unsupportedReason") ||
            line.Contains("unsupportedReason = ", StringComparison.OrdinalIgnoreCase) ||
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
            string directory = CommandsDir();
            var paths = Directory.EnumerateFiles(directory, "*Command.cs")
                .Append(Path.Combine(directory, "AnnotateCommand.Execution.cs"));
            foreach (string path in paths.OrderBy(p => p))
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
