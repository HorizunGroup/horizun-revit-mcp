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

        private static string Source(string file) => File.ReadAllText(Path.Combine(CommandsDir(), file));

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

        [Theory]
        [MemberData(nameof(CoveredCommands))]
        public void A_capability_gap_is_routed_through_the_central_decision(string file, string expectedReason)
        {
            string text = Source(file);

            Assert.Contains("FallbackSignal." + expectedReason, text);
            // The batch rule, not a per-command opinion about it.
            Assert.Contains("FallbackDecision.Decide(outcomes", text);
            Assert.Contains("FallbackDecision.Refuse", text);
            // Every failing action is collected, so the decision sees the WHOLE batch.
            Assert.Contains("new ActionOutcome", text);
        }

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
            foreach (string path in Directory.EnumerateFiles(CommandsDir(), "*Command.cs"))
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
              "FixPlanimetryCommand.cs" };

        [Fact]
        public void No_other_command_emits_the_fallback()
        {
            var expected = new HashSet<string>(ExpectedEmitters, StringComparer.Ordinal);
            var emitting = new List<string>();

            foreach (string path in Directory.EnumerateFiles(CommandsDir(), "*Command.cs"))
            {
                string text = File.ReadAllText(path);
                if (text.Contains("FallbackDecision.") || ForbiddenPrimitives.Any(text.Contains))
                    emitting.Add(Path.GetFileName(path));
            }

            var unexpected = emitting.Where(g => !expected.Contains(g)).ToList();
            Assert.True(unexpected.Count == 0,
                "These commands emit the Python fallback without being listed here: " +
                string.Join(", ", unexpected) + ". Add them only after checking that the refusal happens " +
                "before any write, and that the whole batch is passed to FallbackDecision.");
        }

        /// <summary>
        /// The refusal that can grant must be the one that runs BEFORE the transaction.
        /// Asserted by position: every FallbackDecision.Refuse comes before the first
        /// `new Transaction(` / `new TransactionGroup(`.
        /// </summary>
        [Theory]
        [MemberData(nameof(CoveredCommands))]
        public void The_decision_happens_before_any_transaction_is_opened(string file, string expectedReason)
        {
            string text = Source(file);
            _ = expectedReason;

            int firstTransaction = FirstIndexOfAny(text, "new Transaction(", "new TransactionGroup(");
            if (firstTransaction < 0) return; // nothing to be after

            int scan = 0;
            while (true)
            {
                int refuse = text.IndexOf("FallbackDecision.Refuse", scan, StringComparison.Ordinal);
                if (refuse < 0) break;
                Assert.True(refuse < firstTransaction,
                    file + " decides the Python fallback at offset " + refuse + ", after a transaction is " +
                    "opened at " + firstTransaction + ". A fallback decided once a write may have started " +
                    "must pass writeStarted:true, and this call site cannot prove it does.");
                scan = refuse + 1;
            }
        }

        /// <summary>
        /// Every call site must state whether a write may have started. `writeStarted:`
        /// is named rather than positional so the claim is visible at the call, which is
        /// the only place it can be judged.
        /// </summary>
        [Theory]
        [MemberData(nameof(CoveredCommands))]
        public void Every_decision_states_whether_a_write_may_have_started(string file, string expectedReason)
        {
            string text = Source(file);
            _ = expectedReason;

            int decisions = Occurrences(text, "FallbackDecision.Decide(");
            int declared = Occurrences(text, "writeStarted:");
            Assert.True(decisions > 0, file + " must reach the central decision at least once.");
            Assert.Equal(decisions, declared);
        }

        /// <summary>
        /// A capability gap has to survive the per-item catch that flattens every other
        /// failure to a message - that is what the exception type is for.
        /// </summary>
        [Theory]
        [MemberData(nameof(CoveredCommands))]
        public void The_gap_travels_as_a_type_not_as_a_message(string file, string expectedReason)
        {
            string text = Source(file);
            _ = expectedReason;

            bool typed = text.Contains("new UnsupportedCapability(") ||
                         text.Contains("UnsupportedCapability.ReasonOf(");
            Assert.True(typed,
                file + " must carry a capability gap as UnsupportedCapability (or its reason) rather than " +
                "leaving it to be recognised from the error text later.");
        }

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
