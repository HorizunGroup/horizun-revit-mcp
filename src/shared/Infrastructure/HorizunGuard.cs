// -----------------------------------------------------------------------------
// Horizun — NEW FILE. Apache-2.0 (see LICENSE); original Horizun contribution.
//
// THE "CANNOT LIE" CONTRACT
//
// Every Horizun handler is built on one rule: never report an outcome you did
// not verify. This file exists because we got burned by all three of these on a
// real model, in one afternoon:
//
//   1. A batch purge reported "758 types purged". Nothing was purged. Revit had
//      rolled the whole transaction back and `Transaction.Commit()` returned
//      TransactionStatus.RolledBack WITHOUT throwing. The script counted its own
//      Delete() calls and called that success.
//   2. Keynote tags were placed on 12 elements and reported as "12 tagged".
//      Every tag rendered empty; the API resolves nothing when the keynote lives
//      on the type. The count was of tags created, not of tags with text.
//   3. A beam's `Volume` parameter said 0.4531 m3 while its real geometry and the
//      material takeoff both said 0.7913 m3 — a 75% gap, on the number you bill.
//      No handler said a word.
//
// The pattern in all three: the code measured its own intent, not the model's
// state. So:
//
//   * Commit through Commit() below — it turns a silent rollback into an error.
//   * Count what the model says AFTER the write, never what you asked for.
//   * When two sources of the same quantity disagree, return BOTH and say so.
//     Do not pick one for the user; they are the ones who bill it.
// -----------------------------------------------------------------------------
using System;
using Autodesk.Revit.DB;

namespace RvtMcp.Plugin
{
    /// <summary>
    /// Raised when Revit rolls a transaction back without throwing — the failure
    /// mode that let a purge report 758 deletions and delete nothing.
    /// </summary>
    public class HorizunSilentRollbackException : Exception
    {
        public TransactionStatus Status { get; }
        public HorizunSilentRollbackException(string what, TransactionStatus status)
            : base($"'{what}' did not commit: Revit returned {status}. " +
                   "Nothing was written. This is the failure that reports success and " +
                   "changes nothing — usually one bad element poisoning a batch. " +
                   "Retry in smaller batches to find it.")
        {
            Status = status;
        }
    }

    public static class HorizunGuard
    {
        /// <summary>
        /// Commit and PROVE it. Revit can roll back on failure handling and return
        /// RolledBack with no exception; plain <c>t.Commit()</c> swallows that and
        /// the caller happily reports success. Always commit through here.
        /// </summary>
        public static void Commit(Transaction t, string what)
        {
            var status = t.Commit();
            if (status != TransactionStatus.Committed)
                throw new HorizunSilentRollbackException(what, status);
        }

        /// <summary>
        /// Same, for a TransactionGroup.
        /// </summary>
        public static void Assimilate(TransactionGroup g, string what)
        {
            var status = g.Assimilate();
            if (status != TransactionStatus.Committed)
                throw new HorizunSilentRollbackException(what, status);
        }

        /// <summary>
        /// Verify a write actually landed: compare what we asked for against what
        /// the model reports now. Returns a block the caller must include in its
        /// response — the point is that the discrepancy reaches the user, not a log.
        /// </summary>
        public static object Verify(string what, int intended, int actual)
        {
            var verified = HorizunReconcile.Verified(intended, actual);
            return new
            {
                what,
                intended,
                actual,
                verified,
                note = verified
                    ? null
                    : $"MISMATCH: asked for {intended}, the model reports {actual}. " +
                      "The difference was NOT applied. Do not treat this as done."
            };
        }

        /// <summary>
        /// Two sources for one quantity disagree. Do not choose — report both and
        /// let the person who bills it decide. `tolerance` is relative (0.01 = 1%).
        /// </summary>
        public static object Reconcile(string quantity, string sourceA, double a,
                                       string sourceB, double b, string unit, double tolerance = 0.01)
        {
            var cmp = HorizunReconcile.Compare(a, b, tolerance);
            return new
            {
                quantity,
                unit,
                sources = new object[]
                {
                    new { source = sourceA, value = Math.Round(a, 4) },
                    new { source = sourceB, value = Math.Round(b, 4) }
                },
                agree = cmp.Agree,
                difference = cmp.Difference,
                difference_pct = cmp.DifferencePct,
                note = cmp.Agree
                    ? null
                    : $"These disagree by {cmp.DifferencePct}%. Both are reported on purpose: " +
                      "picking one silently is how the wrong number gets billed. Decide which " +
                      "matches your measurement criteria."
            };
        }

        // ---- Metric at the boundary. Horizun bills in m3/m2, not cubic feet. ----
        // Single source in HorizunReconcile (Revit-free, unit-tested); re-exposed here
        // so existing handlers keep calling HorizunGuard.ToM3 unchanged.
        public const double FT3_TO_M3 = HorizunReconcile.FT3_TO_M3;
        public const double FT2_TO_M2 = HorizunReconcile.FT2_TO_M2;
        public const double FT_TO_M = HorizunReconcile.FT_TO_M;

        public static double ToM3(double cubicFeet) => HorizunReconcile.ToM3(cubicFeet);
        public static double ToM2(double squareFeet) => HorizunReconcile.ToM2(squareFeet);
        public static double ToM(double feet) => HorizunReconcile.ToM(feet);
    }
}
