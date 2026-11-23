using System;
using System.Collections.Generic;

namespace Horizun.Revit.Core
{
    // Revit-independent boundary: the supplied operation is never invoked on refusal.
    public static class LargeModelExecutionGate
    {
        public sealed class Result<T>
        {
            public bool Executed { get; internal set; }
            public T Value { get; internal set; }
            public LargeModelEstimate Estimate { get; internal set; }
            public string ScopeProposal { get; internal set; }
        }

        public static Result<T> Run<T>(int? modelElements,
            IDictionary<string, long> categoryCounts, long scopeThreshold, bool confirmed, Func<T> operation, bool scopeOnly = false)
        {
            if (operation == null) throw new ArgumentNullException(nameof(operation));
            var estimate = LargeModelEstimate.Create(modelElements, categoryCounts, scopeThreshold, confirmed, null, scopeOnly);
            var result = new Result<T> { Estimate = estimate };
            if (estimate.RequiresConfirmation)
            {
                result.ScopeProposal = "Narrow the requested categories, models, or element selection before retrying; " +
                    "alternatively explicitly confirm the large operation.";
                return result;
            }
            result.Value = operation();
            result.Executed = true;
            return result;
        }
    }
}
