// -----------------------------------------------------------------------------
// Horizun Revit MCP — original Horizun code.
//
// THE "CANNOT LIE" CONTRACT.
//
// Every Horizun write is built on one rule: never report an outcome you did not
// verify. Three real defects, measured on one model in one afternoon, are why:
//
//   1. A batch purge reported "758 types purged". Nothing was purged. Revit had
//      rolled the whole transaction back and Transaction.Commit() returned
//      RolledBack WITHOUT throwing. The script counted its own Delete() calls.
//   2. Keynote tags placed on 12 elements, reported "12 tagged". Every tag
//      rendered empty. The count was of tags created, not of tags with text.
//   3. A beam's Volume parameter said 0.4531 m3 while its geometry said 0.7913 —
//      a 75% gap on the number you bill. No handler said a word.
//
// The pattern: the code measured its own intent, not the model's state. So commit
// through Guard.Commit (a silent rollback becomes an error), count what the model
// says AFTER the write, and when two sources of one quantity disagree return BOTH.
//
// The Revit-FREE arithmetic lives in Reconcile.cs so it can be unit-tested without
// loading Autodesk assemblies; Guard delegates to it and re-exposes it so handlers
// call one facade (Guard.ToM, Guard.Verify, Guard.Reconcile).
// -----------------------------------------------------------------------------
using System;
using Autodesk.Revit.DB;
using RMath = Horizun.Revit.Core.Reconcile;

namespace Horizun.Revit.Core
{
    /// <summary>Raised when Revit rolled back without throwing — the silent-success failure mode.</summary>
    public sealed class SilentRollbackException : Exception
    {
        public TransactionStatus Status { get; }

        public SilentRollbackException(string what, TransactionStatus status)
            : base($"'{what}' did not commit: Revit returned {status}. Nothing was written. " +
                   "This is the failure that reports success and changes nothing — usually one " +
                   "bad element poisoning a batch. Retry in smaller batches to find it.")
        {
            Status = status;
        }
    }

    public static class Guard
    {
        /// <summary>Commit and prove it: throws on any status other than Committed.</summary>
        public static TransactionStatus Commit(Transaction t, string what)
        {
            TransactionStatus status = t.Commit();
            if (status != TransactionStatus.Committed)
                throw new SilentRollbackException(what, status);
            return status;
        }

        /// <summary>Commit and prove it for a nested SubTransaction too.</summary>
        public static TransactionStatus Commit(SubTransaction t, string what)
        {
            TransactionStatus status = t.Commit();
            if (status != TransactionStatus.Committed)
                throw new SilentRollbackException(what, status);
            return status;
        }

        /// <summary>Same, for a TransactionGroup.</summary>
        public static TransactionStatus Assimilate(TransactionGroup g, string what)
        {
            TransactionStatus status = g.Assimilate();
            if (status != TransactionStatus.Committed)
                throw new SilentRollbackException(what, status);
            return status;
        }

        /// <summary>
        /// What a rollback ACTUALLY did, never what we hoped. Revit's RollBack() returns a
        /// TransactionStatus; Confirmed is true only when that status is RolledBack. A
        /// Pending, an Error or anything else keeps its uncertainty here rather than being
        /// smoothed into "done" - the whole point is that the caller cannot claim a clean
        /// model it did not see.
        /// </summary>
        public readonly struct RollbackResult
        {
            public TransactionStatus Status { get; }
            public RollbackResult(TransactionStatus status) { Status = status; }
            public bool Confirmed => Status == TransactionStatus.RolledBack;
            public string StatusName => Status.ToString();
        }

        /// <summary>Roll a transaction back and REPORT the status Revit returned - do not assume it.</summary>
        public static RollbackResult RollBack(Transaction t) => new RollbackResult(t.RollBack());

        /// <summary>Roll a transaction group back and REPORT the status Revit returned - do not assume it.</summary>
        public static RollbackResult RollBack(TransactionGroup g) => new RollbackResult(g.RollBack());

        /// <summary>Same for a SubTransaction - the per-item rollback inside a batch.</summary>
        public static RollbackResult RollBack(SubTransaction t) => new RollbackResult(t.RollBack());

        /// <summary>
        /// Verify a write actually landed: compare what we asked for against what the
        /// model reports now. Returns a block the caller must include in its response —
        /// the point is that the discrepancy reaches the user, not a log.
        /// </summary>
        public static object Verify(string what, int intended, int actual)
        {
            bool verified = RMath.Verified(intended, actual);
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
        /// Two sources for one quantity disagree. Do not choose — report both and let
        /// the person who bills it decide. `tolerance` is relative (0.01 = 1%).
        /// </summary>
        public static object Reconcile(string quantity, string sourceA, double a,
                                       string sourceB, double b, string unit, double tolerance = 0.01)
        {
            RMath.Comparison cmp = RMath.Compare(a, b, tolerance);

            // A source that returned NaN or an infinity did not measure anything. Its value
            // goes out as null rather than as a number — both because it IS unknown, and
            // because NaN is not valid JSON and would poison the whole response.
            object valueA = RMath.IsMeasurement(a) ? (object)Math.Round(a, 4) : null;
            object valueB = RMath.IsMeasurement(b) ? (object)Math.Round(b, 4) : null;

            return new
            {
                quantity,
                unit,
                sources = new object[]
                {
                    new { source = sourceA, value = valueA },
                    new { source = sourceB, value = valueB }
                },
                agree = cmp.Agree,
                comparable = cmp.Comparable,
                difference = cmp.Comparable ? (object)cmp.Difference : null,
                difference_pct = cmp.Comparable ? (object)cmp.DifferencePct : null,
                note = !cmp.Comparable
                    ? "NOT COMPARABLE: at least one source did not return a finite number " +
                      $"({sourceA}, {sourceB}). No agreement is claimed and no difference is " +
                      "reported — an absent measurement is not a small one. Find out why that " +
                      "source produced nothing before using either figure."
                    : cmp.Agree
                        ? null
                        : $"These disagree by {cmp.DifferencePct}%. Both are reported on purpose: " +
                          "picking one silently is how the wrong number gets billed. Decide which " +
                          "matches your measurement criteria."
            };
        }

        // ---- Metric at the boundary, re-exposed so handlers call one facade. ----
        public const double Ft3ToM3 = RMath.Ft3ToM3;
        public const double Ft2ToM2 = RMath.Ft2ToM2;
        public const double FtToM = RMath.FtToM;

        public static double ToM3(double cubicFeet) => RMath.ToM3(cubicFeet);
        public static double ToM2(double squareFeet) => RMath.ToM2(squareFeet);
        public static double ToM(double feet) => RMath.ToM(feet);

        /// <summary>Compare two measured values of one quantity; relative tolerance.</summary>
        public static bool Agree(double a, double b, double tolerance, out double differencePct)
        {
            RMath.Comparison cmp = RMath.Compare(a, b, tolerance);
            differencePct = cmp.DifferencePct;
            return cmp.Agree;
        }
    }
}
