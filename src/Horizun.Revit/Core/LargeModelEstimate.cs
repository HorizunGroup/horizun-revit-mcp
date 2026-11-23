using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Horizun.Revit.Core
{
    // Arithmetic only: the host must supply scope counts and an explicitly sourced rate.
    // This is not a duration measurement and does not start or schedule a command.
    public sealed class LargeModelEstimate
    {
        public bool? LargeModel { get; private set; }
        public IReadOnlyDictionary<string, long> CategoryCounts { get; private set; }
        public long ScopedElements { get; private set; }
        public double? EstimatedSeconds { get; private set; }
        public bool RequiresConfirmation { get; private set; }

        public static LargeModelEstimate Create(int? modelElements, IDictionary<string, long> categoryCounts,
            long scopeThreshold, bool confirmed, double? elementsPerSecond, bool scopeOnly = false)
        {
            if (modelElements < 0) throw new ArgumentOutOfRangeException(nameof(modelElements));
            if (categoryCounts == null) throw new ArgumentNullException(nameof(categoryCounts));
            if (scopeThreshold < 0) throw new ArgumentOutOfRangeException(nameof(scopeThreshold));
            if (elementsPerSecond.HasValue && (elementsPerSecond.Value <= 0 || double.IsNaN(elementsPerSecond.Value) || double.IsInfinity(elementsPerSecond.Value)))
                throw new ArgumentOutOfRangeException(nameof(elementsPerSecond));
            long total = 0;
            var copy = new Dictionary<string, long>();
            foreach (var pair in categoryCounts)
            {
                if (pair.Value < 0) throw new ArgumentOutOfRangeException(nameof(categoryCounts));
                total = checked(total + pair.Value);
                copy.Add(pair.Key, pair.Value);
            }
            bool? large = LargeModelRules.IsLarge(modelElements);
            return new LargeModelEstimate
            {
                LargeModel = large,
                CategoryCounts = new ReadOnlyDictionary<string, long>(copy),
                ScopedElements = total,
                EstimatedSeconds = elementsPerSecond.HasValue ? (double?)(total / elementsPerSecond.Value) : null,
                // An unavailable document count is not proof of a small model.
                RequiresConfirmation = (scopeOnly || large != false) && total > scopeThreshold && !confirmed
            };
        }
    }
}
