// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// WHETHER A WHOLE REQUEST EARNS THE PYTHON FALLBACK. One decision, in one place,
// over the per-action planning results.
//
// THE DEFECT THIS EXISTS TO FIX, found in review. The first implementation set a
// request-level "you may write Python" as soon as ANY action in a batch named a
// capability this bridge lacks. A batch of ten walls where one entry says
// kind:"sprinkler_head" and another has a malformed profile came back with
// fallback.allowed=true - so a client was told to go write a script while the
// request still contained input it should have fixed first. The permission was
// true of one entry and published for the whole call, which is the same shape of
// error as reporting a guarantee that held for seven commands out of eight.
//
// THE RULE. A request-level grant requires ALL of:
//
//   1. nothing was written - no transaction opened, no document changed;
//   2. at least one action failed for a structural capability gap;
//   3. EVERY failing action failed for a capability gap.
//
// A mixed batch fails (3) and is refused the global grant. It still gets told
// WHICH actions were capability gaps, per index, so the caller can fix the
// invalid entries and then decide about the rest - that is strictly more
// information than the old blanket yes, and none of it is permission.
//
// Pure and Revit-free on purpose: this is the piece whose behaviour has to be
// provable exhaustively, and it must not be reimplemented per command. Four
// copies of a rule are four chances to get it subtly different, and the previous
// version proved that by shipping the same mistake four times.
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    /// <summary>One action's planning outcome, as far as this decision cares.</summary>
    public sealed class ActionOutcome
    {
        /// <summary>Position in the caller's array, so a gap can be named precisely.</summary>
        public int Index { get; set; }

        /// <summary>Null when the action planned cleanly.</summary>
        public string Error { get; set; }

        /// <summary>
        /// A FallbackSignal.Reason* value when this action failed because no typed
        /// capability covers it; null when it failed for anything a caller could fix.
        /// </summary>
        public string UnsupportedReason { get; set; }

        public bool Failed => Error != null;
        public bool IsCapabilityGap => Failed && UnsupportedReason != null;
    }

    /// <summary>What the request as a whole may claim.</summary>
    public sealed class FallbackVerdict
    {
        /// <summary>The signal to attach, or null when nothing should be attached.</summary>
        public FallbackSignal Signal { get; internal set; }

        /// <summary>Per-action capability gaps, or null when there are none.</summary>
        public JArray CapabilityGaps { get; internal set; }

        /// <summary>True when a request-level grant was issued.</summary>
        public bool GlobalGrant => Signal != null && Signal.IsAllowed;
    }

    public static class FallbackDecision
    {
        /// <summary>
        /// Decide from every action's outcome. writeStarted is the caller's honest answer
        /// to "could anything have been written by now?" - a transaction opened, a
        /// document changed, a commit attempted. When it is true nothing here can grant
        /// the fallback, whatever the errors say.
        /// </summary>
        public static FallbackVerdict Decide(IEnumerable<ActionOutcome> outcomes, bool writeStarted)
        {
            var verdict = new FallbackVerdict();

            int failed = 0, gaps = 0;
            var gapRows = new JArray();

            if (outcomes != null)
            {
                foreach (ActionOutcome o in outcomes)
                {
                    if (o == null || !o.Failed) continue;
                    failed++;
                    if (!o.IsCapabilityGap) continue;

                    gaps++;
                    gapRows.Add(new JObject
                    {
                        ["index"] = o.Index,
                        ["reason"] = o.UnsupportedReason,
                        ["recommended_tool"] = FallbackSignal.RecommendedTool,
                        ["error"] = o.Error
                    });
                }
            }

            if (gaps > 0) verdict.CapabilityGaps = gapRows;

            // (1) Anything that may have written closes the question. A grant here would
            // invite a Python "retry" of work that may be half done.
            if (writeStarted)
            {
                if (failed > 0)
                    verdict.Signal = FallbackSignal.NotAllowed("write_may_have_started", true);
                return verdict;
            }

            // (2) No capability gap at all: ordinary failure, no signal, and the absence
            // is the answer a client reads.
            if (gaps == 0) return verdict;

            // (3) THE MIXED BATCH. Some entries are gaps and some are fixable. The caller
            // is told exactly which are which and gets NO permission, because the request
            // still contains input that should be corrected before anything is generated.
            if (gaps < failed)
            {
                verdict.Signal = FallbackSignal.NotAllowed("mixed_capability_and_invalid_input", false);
                return verdict;
            }

            // Every failure is a capability gap, and nothing was written.
            verdict.Signal = FallbackSignal.Allowed(DominantReason(gapRows));
            return verdict;
        }

        /// <summary>
        /// One reason for the request. When every gap agrees, that reason; when they
        /// differ, the generic one rather than a specific reason that is true of only
        /// some of them.
        /// </summary>
        private static string DominantReason(JArray gaps)
        {
            string first = null;
            foreach (JToken g in gaps)
            {
                string reason = (string)g["reason"];
                if (first == null) first = reason;
                else if (first != reason) return FallbackSignal.ReasonUnsupportedCapability;
            }
            return first ?? FallbackSignal.ReasonUnsupportedCapability;
        }

        /// <summary>
        /// Attach the verdict to a failing result. Returns the result to hand back, so a
        /// command reads as one line and cannot forget the gaps.
        ///
        /// The human message is never rewritten: the signal travels beside it, which is
        /// the whole point of it being structured.
        /// </summary>
        public static CommandResult Refuse(string message, FallbackVerdict verdict)
        {
            if (verdict == null || verdict.Signal == null)
                return CommandResult.Fail(message);

            return CommandResult.FailWithFallback(message, verdict.Signal, verdict.CapabilityGaps);
        }

        /// <summary>
        /// Stamp the verdict onto a SUCCESSFUL planning result - the dry run.
        ///
        /// THE DEFECT THIS EXISTS TO FIX. dry_run defaults to true, so the first call
        /// an agent makes is a rehearsal. The rehearsal came back success=true,
        /// invalid=1, and no verdict anywhere: the decision was computed only on the
        /// apply path. To be told that Python was the way forward, a caller had to
        /// already suspect it and deliberately send dry_run=false. The live probe did
        /// exactly that and reported the guarantee as proven.
        ///
        /// So the same verdict, from the same function, rides the rehearsal too. It
        /// never changes the result's success or its payload - it is additional
        /// information about what the rehearsal FOUND, not a judgement on the
        /// rehearsal itself.
        /// </summary>
        public static CommandResult Attach(CommandResult planning, FallbackVerdict verdict)
        {
            if (planning == null || verdict == null || verdict.Signal == null) return planning;
            planning.CarryFallback(verdict.Signal, verdict.CapabilityGaps);
            return planning;
        }
    }
}
