using System;
using System.Collections.Generic;
using System.Diagnostics;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    // Native Revit calls run on its UI thread and cannot be interrupted safely.
    // Check between calls and reserve time below the server's 600 s deadline for rollback.
    internal sealed class AnnotationBudget
    {
        internal const int DefaultMs = 120000;
        private readonly Func<long> elapsedMs;
        private readonly Func<bool> abandoned;
        private readonly int limitMs;
        private readonly HashSet<int> rowsAttempted = new HashSet<int>();

        internal AnnotationBudget(Func<long> elapsedMs = null, int limitMs = DefaultMs, Func<bool> abandoned = null)
        {
            if (elapsedMs == null)
            {
                var clock = Stopwatch.StartNew();
                elapsedMs = () => clock.ElapsedMilliseconds;
            }
            this.elapsedMs = elapsedMs;
            this.limitMs = limitMs;
            this.abandoned = abandoned;
        }

        internal void StartRow(int index)
        {
            Check();
            rowsAttempted.Add(index);
        }

        internal void Check()
        {
            long elapsed = elapsedMs();
            bool cancelled = abandoned?.Invoke() == true;
            if (elapsed >= limitMs || cancelled)
                throw new AnnotationBudgetExceeded(limitMs, elapsed, rowsAttempted.Count, cancelled);
        }
    }

    internal sealed class AnnotationBudgetExceeded : Exception
    {
        private readonly int limitMs;
        private readonly long elapsedMs;
        private readonly int rowsAttempted;
        private readonly bool cancelled;

        internal AnnotationBudgetExceeded(int limitMs, long elapsedMs, int rowsAttempted, bool cancelled)
            : base(cancelled
                ? "Annotation work stopped after the caller left. Attempted " + rowsAttempted + " row(s)."
                : "Annotation work exceeded its " + limitMs + " ms budget after attempting " +
                  rowsAttempted + " row(s). Use a smaller annotation batch.")
        {
            this.limitMs = limitMs;
            this.elapsedMs = elapsedMs;
            this.rowsAttempted = rowsAttempted;
            this.cancelled = cancelled;
        }

        internal void AddDetail(JObject detail)
        {
            detail[ErrorCodes.CodeField] = cancelled ? ErrorCodes.Cancelado : ErrorCodes.AnnotationBudgetExhausted;
            detail["budget_ms"] = limitMs;
            detail["elapsed_ms"] = elapsedMs;
            detail["rows_attempted"] = rowsAttempted;
        }
    }
}
