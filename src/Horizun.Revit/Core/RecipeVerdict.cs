// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// WHAT A RECIPE'S RE-READ COUNTS ADD UP TO.
//
// THE DEFECT THIS EXISTS TO FIX, found in the 2026-09-24 verification inventory. The
// recipe-backed tools (split_floor_loops, ungroup_and_mark, ...) compare each intended
// count with the count re-read from the model after the commit, and the old verdict was
// "every block agrees". Three ways that said verified over a model that was not:
//
//   1. EVERY ELEMENT FAILED. A recipe skips an element it could not process and lists
//      it in applied["errors"]; the element then counts in neither the intended nor the
//      re-read number. Ten floors asked, ten failures: 0 == 0, all_verified=true,
//      application verified_applied.
//   2. A QUANTITY NOBODY REPORTED. An absent key reads as -1 on both sides, and -1 == -1
//      agreed. (RMath.Verified now refuses the sentinel; this file counts it as unknown.)
//   3. NOTHING TO DO. Every block 0 == 0 with no errors is a legitimate outcome - but it
//      is no_op, not verified_applied, and the declaration now says which.
//
// Revit-free: the counts arrive as integers, and the three cases above are exactly the
// ones a live model will not produce on demand.
// -----------------------------------------------------------------------------
using System.Collections.Generic;

namespace Horizun.Revit.Core
{
    public static class RecipeVerdict
    {
        public sealed class Result
        {
            /// <summary>Every block measured and agreeing, at least one block, and no element reported as failed.</summary>
            public bool AllVerified;
            /// <summary>Every block measured 0 == 0 and nothing failed: the run changed nothing, deliberately.</summary>
            public bool NothingChanged;
            public int Blocks, Agreed, Mismatched, Unmeasured, ErrorsReported;

            // The ApplicationOutcome counts this verdict declares.
            public int Requested, Applied, Verified, Failed, Unknown;
        }

        /// <param name="counts">(intended, re-read) per verification block; -1 means "not reported".</param>
        /// <param name="errorsReported">How many elements the recipe itself listed under applied["errors"].</param>
        public static Result Decide(IList<KeyValuePair<int, int>> counts, int errorsReported)
        {
            var r = new Result { Blocks = counts?.Count ?? 0, ErrorsReported = errorsReported < 0 ? 0 : errorsReported };
            bool anyWork = false;
            if (counts != null)
                foreach (KeyValuePair<int, int> c in counts)
                {
                    if (!Reconcile.Measured(c.Key, c.Value)) { r.Unmeasured++; continue; }
                    if (c.Key != 0 || c.Value != 0) anyWork = true;
                    if (c.Key == c.Value) r.Agreed++; else r.Mismatched++;
                }

            r.AllVerified = r.Blocks > 0 && r.Unmeasured == 0 && r.Mismatched == 0 && r.ErrorsReported == 0;
            r.NothingChanged = r.Blocks > 0 && r.Unmeasured == 0 && r.Mismatched == 0 && r.ErrorsReported == 0 && !anyWork;

            if (r.NothingChanged)
            {
                // requested = 0 over a committed transaction is ApplicationState.NoOp.
                r.Requested = r.Applied = r.Verified = r.Failed = r.Unknown = 0;
                return r;
            }
            r.Requested = r.Blocks + r.ErrorsReported;
            // A run whose only agreement is 0 == 0 landed nothing: it must read as Failed,
            // never as a partial success carried by counts of nothing.
            r.Applied = anyWork ? r.Agreed : 0;
            r.Verified = r.Applied;
            r.Failed = r.Mismatched + r.ErrorsReported;
            r.Unknown = r.Unmeasured;
            if (r.Blocks == 0) r.Unknown++;   // no block at all: nothing was measured
            return r;
        }
    }
}
