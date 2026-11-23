// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// THE ONE PLACE A COMPOSITE TOOL TURNS ITS CHILDREN'S OWN VERDICTS INTO ITS OWN
// application BLOCK.
//
// THE DEFECT THIS EXISTS TO FIX, found in the 2026-09-24 inventory. A composite
// tool (horizun_apply_cad_plan (Pro edition), horizun_apply_cad_update (Pro edition), horizun_cad_connect (Pro edition),
// horizun_apply_ifc_plan (Pro edition), horizun_execute_plan) calls a typed child and then asks
// only "did the call answer Success?" - which is transport, not application (see
// ApplicationOutcome.cs). A child that answered Success over a rollback, a
// partial write, or an unmeasured result was counted as LANDED, exactly the
// failure ApplicationOutcome exists to end one level down, reproduced one level
// up because nothing forced a composite to look.
//
// THE RULE, reusing ApplicationOutcome.Applied's arithmetic rather than
// reinventing it (see that file's own note on why: "this cannot drift into a
// second opinion"). Each child is one unit:
//
//   * the child's transport failed (Success == false)              -> failed
//   * the child declared ApplicationState.NoOp                     -> excluded
//     (legitimately nothing was requested of it; it does not inflate the
//     denominator, and it does not count against the composite either)
//   * the child declared the state this call expects from a landed
//     child (VerifiedApplied on an apply, Rehearsed on a dry run)   -> counted
//     as both requested and fully landed
//   * the child declared ApplicationState.Uncertain, OR declared nothing
//     at all (ApplicationOutcome.Read is fail-closed on both)       -> unknown
//   * anything else the child declared (Partial, RolledBack, Failed,
//     or a Rehearsed state showing up where an apply was expected)  -> failed
//
// PURE AND REVIT-FREE ON PURPOSE, same reasoning as PlanLedger and
// CorrectionApplyLoop: every scenario that matters here - a child that rolled
// back, a child that said nothing, a mix of nine verified rows and one partial
// one - is exactly the shape a live Revit will not produce on demand, and the
// arithmetic has to be provably honest without one.
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    /// <summary>
    /// One child call, exactly as a composite verdict needs to see it: whether the
    /// transport answered, and whatever the child returned as its own reply data -
    /// declared or not. This is deliberately NOT a bool "landed" flag: that is the
    /// judgement CompositeVerdict makes, not something a caller is trusted to have
    /// made correctly already.
    /// </summary>
    public struct CompositeChild
    {
        public readonly bool Success;
        public readonly object Data;

        public CompositeChild(bool success, object data)
        {
            Success = success;
            Data = data;
        }

        public static CompositeChild Of(bool success, object data) => new CompositeChild(success, data);
    }

    public static class CompositeVerdict
    {
        private enum Bucket { Excluded, Landed, Unknown, Failed }

        /// <summary>
        /// Classify one child against what this call expects from a LANDED child:
        /// ApplicationState.VerifiedApplied when expectRehearsal is false (an apply),
        /// ApplicationState.Rehearsed when it is true (a dry run). NoOp is excluded
        /// under either expectation - a child with legitimately nothing to do is
        /// neither a landed contributor nor a failure.
        /// </summary>
        private static Bucket Classify(CompositeChild child, bool expectRehearsal)
        {
            if (!child.Success) return Bucket.Failed;

            ApplicationState state = ApplicationOutcome.Read(child.Data);
            if (state == ApplicationState.NoOp) return Bucket.Excluded;
            if (state == ApplicationState.Uncertain) return Bucket.Unknown;

            bool landed = expectRehearsal ? state == ApplicationState.Rehearsed
                                          : state == ApplicationState.VerifiedApplied;
            return landed ? Bucket.Landed : Bucket.Failed;
        }

        /// <summary>
        /// The composite's own application block for an APPLY: every child is expected
        /// to have declared ApplicationState.VerifiedApplied (or the legitimate NoOp).
        /// Anything else - a child that only succeeded, one that declared nothing, one
        /// that declared a rollback or a partial - keeps the composite's state below
        /// verified_applied. Reuses ApplicationOutcome.Applied's exact arithmetic, so a
        /// composite cannot compute a second, disagreeing opinion about what its own
        /// numbers mean.
        /// </summary>
        public static JObject Aggregate(string transactionStatus, IEnumerable<CompositeChild> children)
        {
            int requested = 0, applied = 0, verified = 0, failed = 0, unknown = 0;
            foreach (CompositeChild child in children ?? Enumerable.Empty<CompositeChild>())
            {
                switch (Classify(child, expectRehearsal: false))
                {
                    case Bucket.Excluded: continue;
                    case Bucket.Landed: requested++; applied++; verified++; break;
                    case Bucket.Unknown: requested++; unknown++; break;
                    default: requested++; failed++; break;
                }
            }
            string status = string.IsNullOrEmpty(transactionStatus) ? ApplicationOutcome.NotStarted : transactionStatus;
            return ApplicationOutcome.DeclareApplied(status, requested, applied, verified, 0, failed, unknown);
        }

        public static JObject Aggregate(string transactionStatus, params CompositeChild[] children)
            => Aggregate(transactionStatus, (IEnumerable<CompositeChild>)children);

        /// <summary>
        /// The composite's own application block for a DRY RUN: every child is expected
        /// to have declared a clean ApplicationState.Rehearsed (or the legitimate NoOp).
        /// A child that instead WROTE during what should have been a preview, one that
        /// could not resolve, or one that declared nothing, all keep the composite below
        /// a state a caller may turn into a confirmed apply.
        /// </summary>
        public static JObject AggregateRehearsal(IEnumerable<CompositeChild> children)
        {
            int requested = 0, failed = 0, unknown = 0;
            foreach (CompositeChild child in children ?? Enumerable.Empty<CompositeChild>())
            {
                switch (Classify(child, expectRehearsal: true))
                {
                    case Bucket.Excluded: continue;
                    case Bucket.Landed: requested++; break;
                    case Bucket.Unknown: requested++; unknown++; break;
                    default: requested++; failed++; break;
                }
            }
            return ApplicationOutcome.DeclareRehearsal(requested, 0, failed, unknown);
        }

        public static JObject AggregateRehearsal(params CompositeChild[] children)
            => AggregateRehearsal((IEnumerable<CompositeChild>)children);
    }
}
