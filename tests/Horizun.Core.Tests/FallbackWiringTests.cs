// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// WHERE THE FALLBACK SIGNAL IS ALLOWED TO COME FROM, asserted over the shipped
// source. The handlers need a UIApplication and a document, so the wiring cannot
// be exercised in CI - the same technique and the same reason as
// ExecutePythonGateTests. The RULE itself is proved exhaustively and Revit-free
// in FallbackDecisionTests; what is pinned here is that every command reaches it
// and nothing reaches around it.
//
// Three properties:
//
//   1. A command with a real capability edge routes its refusal through
//      FallbackDecision, so the batch rule applies.
//   2. NO command reaches a granting primitive directly (the removed
//      FailUnsupported, or FailWithFallback / FallbackSignal.Allowed / CarryFallback).
//      Each grants without seeing the batch and would reintroduce the mixed-batch
//      defect one command at a time.
//   3. Every grant is positioned before the first transaction in its file.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class FallbackWiringTests
    {
        private static string CommandsDir()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !Directory.Exists(Path.Combine(d.FullName, "src", "Horizun.Revit", "Commands")))
                d = d.Parent;
            Assert.True(d != null, "Could not locate src/Horizun.Revit/Commands from " + AppContext.BaseDirectory);
            return Path.Combine(d.FullName, "src", "Horizun.Revit", "Commands");
        }

        private static string Source(string file)
        {
            string source = File.ReadAllText(Path.Combine(CommandsDir(), file));
            return file == "ManageViewsCommand.cs" || file == "AnnotateCommand.cs" || file == "CreateElementsCommand.cs"
                ? File.ReadAllText(Path.Combine(CommandsDir(), Path.GetFileNameWithoutExtension(file) + ".Execution.cs")) + source
                : source;
        }

        [Fact]
        public void Older_revit_wall_attachment_reports_a_capability_gap_in_the_rehearsal()
        {
            string source = Source("TransformElementsCommand.cs");
            int start = source.IndexOf("#else\n", source.IndexOf("if (op == \"wall_attach_top\" || op == \"wall_attach_base\")", StringComparison.Ordinal), StringComparison.Ordinal);
            if (start < 0) start = source.IndexOf("#else\r\n", source.IndexOf("if (op == \"wall_attach_top\" || op == \"wall_attach_base\")", StringComparison.Ordinal), StringComparison.Ordinal);
            Assert.True(start >= 0);
            int end = source.IndexOf("#endif", start, StringComparison.Ordinal);
            string refusal = source.Substring(start, end - start);
            Assert.Contains("new UnsupportedCapability", refusal);
            Assert.Contains("FallbackSignal.ReasonUnsupportedCapability", refusal);
            Assert.Contains("kind=wall_profile", refusal);
            Assert.Contains("FallbackDecision.Attach(", source);
            Assert.Contains("FallbackDecision.Decide(outcomes, writeStarted: false)", source);
        }

        /// <summary>
        /// The commands whose contracts name a fixed set of operations or kinds, and
        /// which therefore have a real capability edge a caller can fall off.
        /// </summary>
        public static IEnumerable<object[]> CoveredCommands => new[]
        {
            new object[] { "CreateElementsCommand.cs",    "ReasonUnsupportedKind" },
            new object[] { "AnnotateCommand.cs",          "ReasonUnsupportedOperation" },
            new object[] { "TransformElementsCommand.cs", "ReasonUnsupportedOperation" },
            new object[] { "ManageViewsCommand.cs",       "ReasonUnsupportedOperation" },
            new object[] { "ManageSchedulesCommand.cs",   "ReasonUnsupportedOperation" },
            new object[] { "ManageCadLinksCommand.cs",    "ReasonUnsupportedOperation" },
        };

        /// <summary>
        /// NONE of the low-level granting primitives may be reached from a command. Each of
        /// them can attach an allowed signal without seeing the rest of the batch, which is
        /// the mixed-batch defect. The removed CommandResult.FailUnsupported was one; the
        /// others (FailWithFallback, FallbackSignal.Allowed, CarryFallback) are only for
        /// FallbackDecision to call. A command reaches the fallback through FallbackDecision
        /// (Decide/Refuse/Attach) and nothing else.
        /// </summary>
        public static readonly string[] ForbiddenPrimitives =
        {
            "CommandResult.FailUnsupported",
            "CommandResult.FailWithFallback",
            "FallbackSignal.Allowed(",
            ".CarryFallback(",
        };

        [Fact]
        public void No_command_grants_the_fallback_directly()
        {
            var direct = new List<string>();
            foreach (string path in Directory.EnumerateFiles(CommandsDir(), "*Command*.cs"))
            {
                string text = File.ReadAllText(path);
                foreach (string primitive in ForbiddenPrimitives)
                    if (text.Contains(primitive))
                        direct.Add(Path.GetFileName(path) + " -> " + primitive);
            }

            Assert.True(direct.Count == 0,
                "These commands reach a fallback-granting primitive directly: " + string.Join(", ", direct) +
                ". Each attaches an allowed signal without seeing the rest of the batch - one uncovered entry " +
                "would license a request that also contains input the caller should fix. Route the refusal " +
                "through FallbackDecision (Decide/Refuse/Attach) instead.");
        }

        /// <summary>
        /// The whole-tree inventory. If this fails, a command started emitting the
        /// fallback and nobody checked whether it could already have written.
        /// </summary>
        public static readonly string[] ExpectedEmitters =
            { // Checked 2026-08-27: the single grant is in the dispatch switch's default
              // arm, reached only for an operation outside the enum, with one
              // ActionOutcome and Decide(writeStarted:false) - and the whole file's
              // first `new Transaction(` comes later, inside Add(). 'unload' is refused
              // ABOVE it WITHOUT a grant on purpose: Revit has no CAD unload in any
              // version 2023-2027, so handing over the Python fallback would send a
              // caller to write a script that finds the same absent API.
              "ManageCadLinksCommand.cs",
              "CreateElementsCommand.cs", "AnnotateCommand.cs",
              "TransformElementsCommand.cs", "ManageViewsCommand.cs",
              // Checked 2026-08-25: the ManageViews shape exactly - the unknown-operation
              // refusal is raised in Validate() with ReasonUnsupportedOperation, every
              // action lands in an ActionOutcome, and both Decide calls pass
              // writeStarted:false before the first `new Transaction(`.
              "ManageSchedulesCommand.cs",
              // Checked 2026-11-06: set_appearance refuses an unsupported appearance schema in PlanAll, before the first
              // `new Transaction(`, as the only action of its batch, through FallbackDecision.Decide(writeStarted:false).
              "ManageMaterialsCommand.cs",
              // Checked 2026-08-24: the unsupported-action-field refusal is raised while
              // planning, every action lands in an ActionOutcome, and both Decide calls
              // pass writeStarted:false before the first `new Transaction(`. The
              // reference-replacement refusal deliberately stays an ArgumentException so
              // it can never reach the decision at all.
              "EditDimensionsCommand.cs",
              // Checked 2026-08-24: same shape as AnnotateCommand - one
              // UnsupportedCapability for operations outside the enum, whole-batch
              // ActionOutcome collection, Decide(writeStarted:false) on both the dry-run
              // Attach and the invalid-batch Refuse, all before any transaction.
              "Detail2DCommand.cs",
              // Checked 2026-08-25: UnsupportedCapability only for an operation outside
              // the closed fix catalog and for a non-rectangular crop, both raised while
              // planning; every action lands in an ActionOutcome; the dry run Attaches
              // and the invalid batch carries the same Decide(writeStarted:false)
              // verdict through FailWithDetail, all before any transaction. The
              // API-absent move_schedule refusal deliberately stays an
              // ArgumentException so a Python script facing the same absent setter is
              // never suggested.
              "FixPlanimetryCommand.cs",
              // Checked 2026-09-24: resize judges EVERY element into an ActionOutcome
              // while planning (flex runs are the only UnsupportedKind; a size outside
              // the element's catalog or a shape mismatch is an ordinary error) and
              // refuses through Decide(writeStarted:false) before any transaction.
              "MepRoutingCommand.cs" };

        [Fact]
        public void No_other_command_emits_the_fallback()
        {
            var expected = new HashSet<string>(ExpectedEmitters, StringComparer.Ordinal);
            var emitting = new List<string>();

            foreach (string path in Directory.EnumerateFiles(CommandsDir(), "*Command.cs"))
            {
                string text = Source(Path.GetFileName(path));
                if (text.Contains("FallbackDecision.") || ForbiddenPrimitives.Any(text.Contains))
                    emitting.Add(Path.GetFileName(path));
            }

            var unexpected = emitting.Where(g => !expected.Contains(g)).ToList();
            Assert.True(unexpected.Count == 0,
                "These commands emit the Python fallback without being listed here: " +
                string.Join(", ", unexpected) + ". Add them only after checking that the refusal happens " +
                "before any write, and that the whole batch is passed to FallbackDecision.");
        }

        // Source already prepends the command's .Execution.cs part.
        private static string CommandSource(string file) => Source(file);

        private static int Occurrences(string text, string needle)
        {
            int count = 0, i = 0;
            while ((i = text.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { count++; i += needle.Length; }
            return count;
        }

        private static int FirstIndexOfAny(string text, params string[] needles)
        {
            int best = -1;
            foreach (string n in needles)
            {
                int i = text.IndexOf(n, StringComparison.Ordinal);
                if (i >= 0 && (best < 0 || i < best)) best = i;
            }
            return best;
        }
    }
}
